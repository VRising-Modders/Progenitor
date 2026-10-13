using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace Progenitor.Dev.Ops;

/// <summary>
/// Game-free half of the <c>perf</c> op: argument policy, the ring buffers, the source table that
/// turns a dispatched delegate's declaring type into a readable bucket, the percentile maths, and
/// the report / summary shapes the main session reads. Everything that touches the game — the
/// <c>Progenitor.Dev.IPerfSink</c> implementation, the GC counters, the main-thread queue-depth
/// read — lives in <c>PerfOps</c>, so this file compiles into <c>Progenitor.Tests</c> (net8, no
/// game assemblies) unchanged, the same split <c>CaptureReport.cs</c> uses.
///
/// <para>Why the split matters here: the numbers themselves can only come from a running game,
/// but everything that can silently rot — the percentile arithmetic, the ring's wrap accounting,
/// per-source accumulation, and the summary the session actually reads — is in this file and is
/// unit tested.</para>
/// </summary>
internal static class PerfArgs
{
    public const double DefaultSeconds = 30;
    public const double MinSeconds = 1;

    /// <summary>
    /// Hard ceiling on a window. <c>start</c> returns immediately and the window outlives the
    /// bridge's 30 s socket timeout, so this only has to be short enough that a forgotten capture
    /// cannot profile the rest of the session. Past <see cref="PerfCapacity.MaxFrames"/> frames
    /// the ring wraps and keeps the most recent ones (the report says so).
    /// </summary>
    public const double MaxSeconds = 300;

    /// <summary><c>args.action</c>: <c>start</c>, <c>stop</c>, or <c>status</c>. Anything else throws.</summary>
    public static string ParseAction(JsonElement args)
    {
        string? action = null;
        if (args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty("action", out var el)
            && el.ValueKind == JsonValueKind.String)
            action = el.GetString();

        if (string.IsNullOrWhiteSpace(action))
            throw new ArgumentException("args.action is required: start, stop, or status");

        return action!.Trim().ToLowerInvariant() switch
        {
            "start" => "start",
            "stop" => "stop",
            "status" => "status",
            _ => throw new ArgumentException($"unknown perf action '{action}'; use start, stop, or status"),
        };
    }

    public static double ClampSeconds(double seconds)
    {
        if (double.IsNaN(seconds)) return DefaultSeconds;
        return Math.Clamp(seconds, MinSeconds, MaxSeconds);
    }
}

/// <summary>
/// Ring size for a requested window. Sized for the requested seconds at a little over 120 fps so a
/// 30 s capture on a 144 Hz client still fits without wrapping, and hard-capped at 60 s of 120 fps
/// (<see cref="MaxFrames"/>) so the preallocation stays bounded: at
/// <see cref="PerfSourceTable.MaxSources"/> per-source floats plus eight scalar series, and the
/// <see cref="PerfFrameDetail"/> series (IL2CPP, collectors, patches, one float per seam), the worst
/// case is about 1.5 MB, allocated once at <c>start</c> and never grown.
/// </summary>
internal static class PerfCapacity
{
    /// <summary>Headroom per requested second, so a 30 s window on a 144 Hz client does not wrap.</summary>
    public const int FramesPerSecond = 150;
    public const int MaxFrames = 60 * 120;
    public const int MinFrames = 64;

    public static int For(double seconds)
    {
        if (double.IsNaN(seconds) || seconds <= 0) return MinFrames;
        double frames = Math.Ceiling(seconds * FramesPerSecond) + 64;
        if (frames >= MaxFrames) return MaxFrames;
        return (int)Math.Max(MinFrames, frames);
    }
}

/// <summary>
/// Names the buckets a report splits Progenitor's per-frame work into, and hands out the stable
/// column index each bucket occupies in the per-frame matrix.
///
/// <para>Index 0 is always <see cref="OtherName"/>: a table that filled up must still be able to
/// answer "where did the rest of the time go" rather than dropping samples, and the report says
/// <c>sourceOverflow</c> when that happened.</para>
/// </summary>
internal sealed class PerfSourceTable
{
    /// <summary>Columns in the per-frame matrix, including <see cref="OtherName"/>.</summary>
    public const int MaxSources = 24;

    public const string OtherName = "other";
    public const string UnknownName = "unknown";

    private readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);
    private readonly List<string> _names = new();

    public PerfSourceTable()
    {
        _names.Add(OtherName);
        _index[OtherName] = 0;
    }

    public int Count => _names.Count;
    public IReadOnlyList<string> Names => _names;
    public bool Overflowed { get; private set; }

    /// <summary>Column for <paramref name="name"/>, adding it while there is room; index 0 otherwise.</summary>
    public int GetOrAdd(string? name)
    {
        if (string.IsNullOrEmpty(name)) name = UnknownName;
        if (_index.TryGetValue(name!, out var found)) return found;
        if (_names.Count >= MaxSources)
        {
            Overflowed = true;
            return 0;
        }
        int index = _names.Count;
        _names.Add(name!);
        _index[name!] = index;
        return index;
    }

    public int IndexOf(string name) => _index.TryGetValue(name, out var i) ? i : -1;

    /// <summary>
    /// A detached copy, for <see cref="PerfRecording.Snapshot"/>. Taken under the record lock so the
    /// serializer never enumerates a list the main thread is appending to (R29 finding 5).
    /// </summary>
    public PerfSourceTable Snapshot()
    {
        var copy = new PerfSourceTable();
        for (int i = 1; i < _names.Count; i++)
        {
            copy._names.Add(_names[i]);
            copy._index[_names[i]] = i;
        }
        copy.Overflowed = Overflowed;
        return copy;
    }

    /// <summary>
    /// Bucket name for a <c>Plugin.Frame</c> subscriber, from the declaring type's full name and
    /// the declaring assembly's simple name. Anything outside Core is a consumer plugin and is
    /// named after its assembly; inside Core the first namespace segment is the module, which is
    /// exactly how the source tree is laid out (<c>Progenitor.Effects.ServerBeam</c> is Effects).
    /// A type in the root <c>Progenitor</c> namespace, or a compiler-generated closure of one,
    /// is <c>core</c> — the segment there is the type name, not a module.
    /// </summary>
    public static string ClassifyFrameHandler(string? declaringTypeFullName, string? assemblySimpleName) =>
        ClassifyFrameHandler(declaringTypeFullName, assemblySimpleName, null);

    /// <summary>
    /// As above, with the handler's method name. In the modules listed in <see cref="SplitModules"/> each handler is
    /// its own source, <c>core:networking/JoinFlowClient.FrameLastServer</c>: networking runs several independent
    /// per-frame subsystems (the join screens' steps, the deny panel, the mod report), and on 2026-10-06 one of them
    /// was 78% of Progenitor's frame time behind a single <c>core:networking</c> bucket.
    /// </summary>
    public static string ClassifyFrameHandler(string? declaringTypeFullName, string? assemblySimpleName, string? methodName)
    {
        if (string.IsNullOrWhiteSpace(assemblySimpleName)) return UnknownName;
        if (!string.Equals(assemblySimpleName, CoreAssemblyName, StringComparison.OrdinalIgnoreCase))
            return "plugin:" + assemblySimpleName;

        string module = CoreModule(declaringTypeFullName);
        string source = "core:" + module;
        if (string.IsNullOrWhiteSpace(methodName) || Array.IndexOf(SplitModules, module) < 0) return source;
        return source + "/" + TypeName(declaringTypeFullName) + "." + methodName;
    }

    /// <summary>Core modules whose frame handlers are reported one source per handler.</summary>
    public static readonly string[] SplitModules = { "networking" };

    /// <summary>The simple name of the owning type (before any <c>+</c> nested part).</summary>
    private static string TypeName(string? declaringTypeFullName)
    {
        var full = declaringTypeFullName ?? "";
        int plus = full.IndexOf('+');
        if (plus > 0) full = full.Substring(0, plus);
        int dot = full.LastIndexOf('.');
        return dot >= 0 ? full.Substring(dot + 1) : full;
    }

    public const string CoreAssemblyName = "Progenitor";

    /// <summary>Module segment of a Core type's full name; <c>core</c> when it has none.</summary>
    public static string CoreModule(string? declaringTypeFullName)
    {
        if (string.IsNullOrWhiteSpace(declaringTypeFullName)) return "core";

        // Nested and compiler-generated types (Progenitor.Effects.ServerBeam+<>c) carry the
        // owning type after '+'; the namespace is everything before it.
        var full = declaringTypeFullName!;
        int plus = full.IndexOf('+');
        if (plus > 0) full = full.Substring(0, plus);

        const string prefix = CoreAssemblyName + ".";
        if (!full.StartsWith(prefix, StringComparison.Ordinal)) return "core";
        var rest = full.Substring(prefix.Length);
        int dot = rest.IndexOf('.');
        if (dot <= 0) return "core";            // Progenitor.Plugin -> the type, not a module
        return rest.Substring(0, dot).ToLowerInvariant();
    }

    /// <summary>Bucket for a plugin system hook, and for an event-bus handler.</summary>
    public static string HookSource(string? pluginId) => "hook:" + IdOrCore(pluginId);

    public static string EventSource(string? pluginId) => "event:" + IdOrCore(pluginId);

    private static string IdOrCore(string? pluginId) =>
        string.IsNullOrWhiteSpace(pluginId) ? "core" : pluginId!;
}

/// <summary>Percentiles and means over the recorded slice. Nearest-rank, no interpolation.</summary>
internal static class PerfStats
{
    /// <summary>
    /// Nearest-rank percentile of <paramref name="sorted"/>[0..<paramref name="count"/>).
    /// <paramref name="percentile"/> is 0..100. An empty slice is 0.
    /// </summary>
    public static double Percentile(float[] sorted, int count, double percentile)
    {
        if (sorted == null || count <= 0) return 0;
        if (count == 1) return sorted[0];
        double p = Math.Clamp(percentile, 0, 100);
        int rank = (int)Math.Ceiling(p / 100.0 * count);
        int index = Math.Clamp(rank - 1, 0, count - 1);
        return sorted[index];
    }

    public static double Mean(float[] values, int count)
    {
        if (values == null || count <= 0) return 0;
        double sum = 0;
        for (int i = 0; i < count; i++) sum += values[i];
        return sum / count;
    }

    public static double Sum(float[] values, int count)
    {
        if (values == null || count <= 0) return 0;
        double sum = 0;
        for (int i = 0; i < count; i++) sum += values[i];
        return sum;
    }

    public static double Max(float[] values, int count)
    {
        if (values == null || count <= 0) return 0;
        double max = values[0];
        for (int i = 1; i < count; i++) if (values[i] > max) max = values[i];
        return max;
    }

    /// <summary>Milliseconds, rounded for a report a person reads. Three decimals is a microsecond.</summary>
    public static double Ms(double value) => Math.Round(value, 3, MidpointRounding.AwayFromZero);

    public static double Round(double value, int digits) => Math.Round(value, digits, MidpointRounding.AwayFromZero);
}

/// <summary>One dispatch bucket's running totals outside the per-frame matrix.</summary>
internal sealed class PerfCallStat
{
    public long Calls;
    public double TotalMs;
    public double MaxMs;

    public void Add(double ms)
    {
        Calls++;
        TotalMs += ms;
        if (ms > MaxMs) MaxMs = ms;
    }

    /// <summary>A detached copy, for <see cref="PerfRecording.Snapshot"/>.</summary>
    public PerfCallStat Copy() => new() { Calls = Calls, TotalMs = TotalMs, MaxMs = MaxMs };
}

/// <summary>
/// The recording itself: fixed-size ring buffers for the per-frame series plus a
/// frames-by-sources matrix, and the aggregate tables for system hooks and events. Nothing here
/// allocates after construction — <see cref="AddFrame"/> writes into the preallocated arrays and
/// the per-source scratch row is owned by the caller.
/// </summary>
internal sealed class PerfRecording
{
    /// <summary>Detail rows kept for hooks and events before the report stops adding new keys.</summary>
    public const int MaxDetailRows = 64;

    private readonly float[] _atSeconds;
    private readonly float[] _frameMs;
    private readonly float[] _coreTickMs;
    private readonly float[] _handlersMs;
    private readonly int[] _queueDepth;
    private readonly int[] _delayedDepth;
    private readonly long[] _allocBytes;
    private readonly long[] _heapBytes;
    private readonly float[] _sourceMs;

    public PerfRecording(int capacity)
    {
        Capacity = Math.Max(1, capacity);
        _atSeconds = new float[Capacity];
        _frameMs = new float[Capacity];
        _coreTickMs = new float[Capacity];
        _handlersMs = new float[Capacity];
        _queueDepth = new int[Capacity];
        _delayedDepth = new int[Capacity];
        _allocBytes = new long[Capacity];
        _heapBytes = new long[Capacity];
        _sourceMs = new float[Capacity * PerfSourceTable.MaxSources];
        Detail = new PerfFrameDetail(Capacity);
    }

    /// <summary>IL2CPP heap, collector counts, timed patch bodies and seam stamps (same slots as the ring).</summary>
    public PerfFrameDetail Detail { get; private set; }

    public int Capacity { get; }
    public PerfSourceTable Sources { get; private set; } = new();

    /// <summary>Frames offered to <see cref="AddFrame"/>, including any the ring has overwritten.</summary>
    public int Frames { get; private set; }

    /// <summary>Frames still in the ring.</summary>
    public int Recorded => Math.Min(Frames, Capacity);

    /// <summary>True once the ring started overwriting: the report covers only the most recent frames.</summary>
    public bool Wrapped => Frames > Capacity;

    public readonly Dictionary<(string PluginId, string System, bool Before), PerfCallStat> Hooks = new();
    public readonly Dictionary<(string PluginId, string Event), PerfCallStat> Events = new();
    public bool HookDetailTruncated { get; private set; }
    public bool EventDetailTruncated { get; private set; }

    /// <summary>
    /// Record one frame. <paramref name="perSource"/> is the caller's reused row of per-source
    /// milliseconds for this frame (length <see cref="PerfSourceTable.MaxSources"/>); it is copied
    /// out, never retained.
    /// </summary>
    public void AddFrame(
        double atSeconds,
        double frameMs,
        double coreTickMs,
        float[] perSource,
        int queueDepth,
        int delayedDepth,
        long allocBytes,
        long heapBytes)
        => AddFrame(atSeconds, frameMs, coreTickMs, perSource, queueDepth, delayedDepth, allocBytes, heapBytes,
            PerfFrameExtra.None);

    /// <summary>As above, with the frame's IL2CPP, collector, patch and seam measurements.</summary>
    public void AddFrame(
        double atSeconds,
        double frameMs,
        double coreTickMs,
        float[] perSource,
        int queueDepth,
        int delayedDepth,
        long allocBytes,
        long heapBytes,
        in PerfFrameExtra extra)
    {
        int slot = Frames % Capacity;
        Frames++;

        _atSeconds[slot] = (float)atSeconds;
        _frameMs[slot] = (float)frameMs;
        _coreTickMs[slot] = (float)coreTickMs;
        _queueDepth[slot] = queueDepth;
        _delayedDepth[slot] = delayedDepth;
        _allocBytes[slot] = allocBytes;
        _heapBytes[slot] = heapBytes;

        int baseIndex = slot * PerfSourceTable.MaxSources;
        double handlers = 0;
        for (int i = 0; i < PerfSourceTable.MaxSources; i++)
        {
            float value = perSource != null && i < perSource.Length ? perSource[i] : 0f;
            _sourceMs[baseIndex + i] = value;
            handlers += value;
        }
        _handlersMs[slot] = (float)handlers;
        Detail.Add(slot, atSeconds, frameMs, in extra);
    }

    public void AddHook(string pluginId, string systemName, bool before, double ms)
    {
        var key = (pluginId ?? "", systemName ?? "", before);
        if (!Hooks.TryGetValue(key, out var stat))
        {
            if (Hooks.Count >= MaxDetailRows) { HookDetailTruncated = true; return; }
            stat = new PerfCallStat();
            Hooks[key] = stat;
        }
        stat.Add(ms);
    }

    public void AddEvent(string pluginId, string eventName, double ms)
    {
        var key = (pluginId ?? "", eventName ?? "");
        if (!Events.TryGetValue(key, out var stat))
        {
            if (Events.Count >= MaxDetailRows) { EventDetailTruncated = true; return; }
            stat = new PerfCallStat();
            Events[key] = stat;
        }
        stat.Add(ms);
    }

    /// <summary>
    /// A detached copy of everything the report reads: the ring buffers, the source table and the
    /// two detail dictionaries.
    ///
    /// <para><b>Why this exists (R29 finding 5).</b> The perf watchdog used to hand the <b>live</b>
    /// recording to <c>Summary()</c> and <c>JsonSerializer.Serialize</c> from a
    /// <see cref="System.Threading.Timer"/> thread while the game main thread was still inside
    /// <see cref="AddFrame"/> / <see cref="AddHook"/> / <see cref="AddEvent"/>. Enumerating
    /// <see cref="Hooks"/> or <see cref="Events"/> while the main thread inserts throws
    /// <c>InvalidOperationException</c> — which the caller turned into <c>writeError</c>, losing the
    /// whole capture — and reading the preallocated arrays mid-write gives a torn report that
    /// nothing flags. <c>allocBytes</c>, the op's headline number, is written last and so was the
    /// most likely field to be corrupted.</para>
    ///
    /// <para>The copy is taken under <see cref="PerfCloseGate.RecordGate"/>, which every write also
    /// holds, so the close thread blocks for a handful of <c>Array.Copy</c> calls. Serialization
    /// and the file write run afterwards on the detached copy via <c>PerfFinalize</c>, not on
    /// this lock and not on the close thread.</para>
    /// </summary>
    public PerfRecording Snapshot()
    {
        var copy = new PerfRecording(Capacity);
        Array.Copy(_atSeconds, copy._atSeconds, Capacity);
        Array.Copy(_frameMs, copy._frameMs, Capacity);
        Array.Copy(_coreTickMs, copy._coreTickMs, Capacity);
        Array.Copy(_handlersMs, copy._handlersMs, Capacity);
        Array.Copy(_queueDepth, copy._queueDepth, Capacity);
        Array.Copy(_delayedDepth, copy._delayedDepth, Capacity);
        Array.Copy(_allocBytes, copy._allocBytes, Capacity);
        Array.Copy(_heapBytes, copy._heapBytes, Capacity);
        Array.Copy(_sourceMs, copy._sourceMs, _sourceMs.Length);

        copy.Frames = Frames;
        copy.HookDetailTruncated = HookDetailTruncated;
        copy.EventDetailTruncated = EventDetailTruncated;
        copy.Sources = Sources.Snapshot();
        copy.Detail = Detail.Snapshot();
        foreach (var pair in Hooks) copy.Hooks[pair.Key] = pair.Value.Copy();
        foreach (var pair in Events) copy.Events[pair.Key] = pair.Value.Copy();
        return copy;
    }

    /// <summary>Physical slot of the <paramref name="i"/>-th oldest frame still in the ring.</summary>
    public int Slot(int i)
    {
        int recorded = Recorded;
        if (recorded <= 0) return 0;
        int start = Wrapped ? Frames % Capacity : 0;
        return (start + i) % Capacity;
    }

    public double AtSeconds(int i) => _atSeconds[Slot(i)];
    public double FrameMs(int i) => _frameMs[Slot(i)];
    public double CoreTickMs(int i) => _coreTickMs[Slot(i)];
    public double HandlersMs(int i) => _handlersMs[Slot(i)];
    public int QueueDepth(int i) => _queueDepth[Slot(i)];
    public int DelayedDepth(int i) => _delayedDepth[Slot(i)];
    public long AllocBytes(int i) => _allocBytes[Slot(i)];
    public long HeapBytes(int i) => _heapBytes[Slot(i)];
    public double SourceMs(int i, int source) => _sourceMs[Slot(i) * PerfSourceTable.MaxSources + source];

    /// <summary>Copy the whole-frame series named by <paramref name="series"/> into <paramref name="buffer"/>.</summary>
    public int CopySeries(PerfSeries series, float[] buffer)
    {
        int n = Math.Min(Recorded, buffer.Length);
        for (int i = 0; i < n; i++)
        {
            buffer[i] = series switch
            {
                PerfSeries.FrameMs => (float)FrameMs(i),
                PerfSeries.CoreTickMs => (float)CoreTickMs(i),
                PerfSeries.HandlersMs => (float)HandlersMs(i),
                PerfSeries.QueueDepth => QueueDepth(i),
                PerfSeries.DelayedDepth => DelayedDepth(i),
                PerfSeries.AllocBytes => AllocBytes(i),
                _ => (float)HeapBytes(i),
            };
        }
        return n;
    }

    /// <summary>Copy one source's per-frame milliseconds into <paramref name="buffer"/>.</summary>
    public int CopySource(int source, float[] buffer)
    {
        int n = Math.Min(Recorded, buffer.Length);
        for (int i = 0; i < n; i++) buffer[i] = (float)SourceMs(i, source);
        return n;
    }

    /// <summary>Index (0-based, oldest first) of the frame with the most Progenitor handler time.</summary>
    public int WorstFrameIndex()
    {
        int recorded = Recorded;
        if (recorded <= 0) return -1;
        int worst = 0;
        double best = HandlersMs(0);
        for (int i = 1; i < recorded; i++)
        {
            double v = HandlersMs(i);
            if (v > best) { best = v; worst = i; }
        }
        return worst;
    }

    /// <summary>Indices of the <paramref name="count"/> frames with the longest frame interval, slowest first.</summary>
    public List<int> SlowestFrames(int count)
    {
        var result = new List<int>();
        int recorded = Recorded;
        if (recorded <= 0 || count <= 0) return result;
        var order = new int[recorded];
        var keys = new double[recorded];
        for (int i = 0; i < recorded; i++) { order[i] = i; keys[i] = -FrameMs(i); }
        Array.Sort(keys, order);
        for (int i = 0; i < Math.Min(count, recorded); i++) result.Add(order[i]);
        return result;
    }

    /// <summary>Indices of the <paramref name="count"/> frames with the most handler time, worst first.</summary>
    public List<int> WorstFrames(int count)
    {
        var result = new List<int>();
        int recorded = Recorded;
        if (recorded <= 0 || count <= 0) return result;

        var order = new int[recorded];
        for (int i = 0; i < recorded; i++) order[i] = i;
        var keys = new double[recorded];
        for (int i = 0; i < recorded; i++) keys[i] = -HandlersMs(i);
        Array.Sort(keys, order);
        for (int i = 0; i < Math.Min(count, recorded); i++) result.Add(order[i]);
        return result;
    }
}

internal enum PerfSeries
{
    FrameMs,
    CoreTickMs,
    HandlersMs,
    QueueDepth,
    DelayedDepth,
    AllocBytes,
    HeapBytes,
}

/// <summary>
/// The finished report: the recording plus the run's metadata, and the two shapes the session
/// reads — <see cref="ToJson"/> (written to <c>BepInEx/progenitor-perf/&lt;timestamp&gt;.json</c>)
/// and <see cref="Summary"/> (returned by the bridge on <c>stop</c>).
/// </summary>
/// <summary>The closed set of reasons a <c>perf</c> window ends. Written into every report.</summary>
internal static class PerfStopReason
{
    /// <summary>An explicit <c>perf {"action":"stop"}</c>.</summary>
    public const string Stop = "stop";

    /// <summary>The requested number of seconds elapsed.</summary>
    public const string Deadline = "deadline";

    /// <summary>The Dev plugin was unloaded (which includes the start of a Core reload cascade).</summary>
    public const string Unload = "unload";

    /// <summary>
    /// Core was swapped out from under the window. The sink lives in Dev and the slot lives in
    /// Core, so the swap detaches them and no further frame is ever dispatched to this sink; the
    /// window closes itself and writes what it has (R28 findings 14 and 17).
    /// </summary>
    public const string CoreSwap = "core-swap";
}

/// <summary>
/// When a running <c>perf</c> window has to close, decided from facts Dev can read <b>without</b>
/// Core's frame dispatch.
///
/// <para>R28 findings 14 and 17: the only thing that closed a window was <c>Sink.CoreTick</c>, which
/// runs from Core's frame dispatch. In the two cases the op's own documentation calls the
/// interesting ones — profiling the boot, the main menu, or a Core-less process — no frame ticks at
/// all, so the deadline never fired, the sink stayed installed indefinitely as a Core static holding
/// a Dev collectible object, and every later <c>perf start</c> threw "a perf window is already
/// running". A Core swap mid-capture produced the same wedge from the other direction. Both are now
/// decided here and polled from a Dev-side timer.</para>
/// </summary>
internal static class PerfWindowPolicy
{
    /// <summary>True when the requested window has elapsed.</summary>
    public static bool DeadlineReached(double elapsedSeconds, double requestedSeconds)
    {
        if (double.IsNaN(elapsedSeconds) || double.IsNaN(requestedSeconds)) return false;
        if (requestedSeconds <= 0) return false;
        return elapsedSeconds >= requestedSeconds;
    }

    /// <summary>
    /// True when the Core generation this window installed into is gone: the slot latched
    /// <c>CoreUninstalled</c>, the generation counter moved, or the installed sink is no longer
    /// ours. Any one of the three is decisive; a window that is not running is never a swap.
    /// </summary>
    public static bool CoreSwapped(
        bool running, bool coreUninstalled, int installedGeneration, int currentGeneration, bool sinkStillInstalled)
    {
        if (!running) return false;
        return coreUninstalled || currentGeneration != installedGeneration || !sinkStillInstalled;
    }

    /// <summary>
    /// The reason a running window must stop, or null to keep running. A Core swap outranks the
    /// deadline: the report has to say why its frames stopped early.
    /// </summary>
    public static string? StopReasonFor(bool coreSwapped, bool deadlineReached)
    {
        if (coreSwapped) return PerfStopReason.CoreSwap;
        if (deadlineReached) return PerfStopReason.Deadline;
        return null;
    }
}

/// <summary>
/// Who is allowed to close a <c>perf</c> window, and the lock that makes closing it safe.
///
/// <para><b>R29 finding 5.</b> The watchdog timer used to call <c>Stop</c> straight off its own
/// thread: clear <c>_running</c>, take the live <see cref="PerfReport"/>, then — outside every lock
/// — walk it to build the summary and serialize the JSON. The game main thread was meanwhile inside
/// <see cref="PerfRecording.AddFrame"/> and the hook / event dictionaries, taking no lock at all.
/// The two outcomes were a thrown <c>InvalidOperationException</c> (dictionary enumeration during an
/// insert) reported as <c>writeError</c> with the capture lost, or a torn report that nothing
/// flagged.</para>
///
/// <para><b>The rule.</b> Nothing but the <i>owner thread</i> — the thread the window was started on,
/// which is the game main thread, because <c>perf</c> is a main-thread bridge op — performs the
/// close. Anything else (the watchdog timer) only <see cref="Request"/>s one; the next
/// <c>Sink.CoreTick</c> or the next op entry picks it up. The one exception is a window whose
/// recording has been <i>quiescent</i> for <see cref="QuiescentSeconds"/>
/// (<see cref="MayCloseOffOwnerThread"/>): that is the process the watchdog exists for at all — a
/// boot, a main menu, a Core-less or Core-swapped process where no frame ticks — and where by
/// definition nothing is writing. Even there the close still goes through
/// <see cref="Freeze"/>. <c>PerfFinalize</c> then serializes the detached copy on a
/// background thread; the close thread does not wait for JSON or the file write.</para>
/// </summary>
internal sealed class PerfCloseGate
{
    /// <summary>
    /// How long the recording must have gone unwritten before a non-owner thread may close the
    /// window itself. Longer than two watchdog periods, so a process that ticks at any rate at all
    /// never reaches it.
    /// </summary>
    public const double QuiescentSeconds = 2.0;

    private readonly object _flag = new();
    private string? _requested;

    /// <summary>
    /// The lock every recording write and every <see cref="Freeze"/> takes. Uncontended on the main
    /// thread (it is the only writer), and held by the close path only for the array copies.
    /// </summary>
    public object RecordGate { get; } = new();

    /// <summary>Managed id of the thread the window was started on; 0 before <see cref="ClaimOwner"/>.</summary>
    public int OwnerThreadId { get; private set; }

    /// <summary>Records the calling thread as the one allowed to perform the close.</summary>
    public void ClaimOwner() => OwnerThreadId = Environment.CurrentManagedThreadId;

    /// <summary>True when the calling thread is the one that started the window.</summary>
    public bool OnOwnerThread => Environment.CurrentManagedThreadId == OwnerThreadId;

    /// <summary>The reason a close is pending, or null.</summary>
    public string? PendingReason { get { lock (_flag) return _requested; } }

    /// <summary>
    /// Asks for the window to be closed. The first reason wins: a Core swap the timer noticed must
    /// not be relabeled <c>deadline</c> by the next poll. Returns true when this call latched it.
    /// </summary>
    public bool Request(string reason)
    {
        if (string.IsNullOrEmpty(reason)) return false;
        lock (_flag)
        {
            if (_requested != null) return false;
            _requested = reason;
            return true;
        }
    }

    /// <summary>Takes the pending reason, clearing it. Null when none is pending.</summary>
    public string? Take()
    {
        lock (_flag)
        {
            var reason = _requested;
            _requested = null;
            return reason;
        }
    }

    /// <summary>Detaches <paramref name="live"/> under the record lock, so serialization is safe.</summary>
    public PerfRecording Freeze(PerfRecording live)
    {
        lock (RecordGate) return live.Snapshot();
    }

    /// <summary>
    /// True when a thread that is not the owner may perform the close itself: the recording has not
    /// been written for <paramref name="quiescentSeconds"/>, so there is no main-thread writer to
    /// race. A negative or NaN age (no write has ever happened, or an unusable clock) counts as
    /// quiescent only when the window itself has been open that long, which is what the caller
    /// passes.
    /// </summary>
    public static bool MayCloseOffOwnerThread(
        double secondsSinceLastWrite, double quiescentSeconds = QuiescentSeconds)
    {
        if (quiescentSeconds <= 0) return false;
        if (double.IsNaN(secondsSinceLastWrite)) return false;
        return secondsSinceLastWrite >= quiescentSeconds;
    }
}

internal sealed class PerfReport
{
    public string Side = "";
    public string World = "";
    public DateTime StartedUtc;
    public double RequestedSeconds;
    public double ElapsedSeconds;
    public string StopReason = "stop";

    /// <summary>Which GC counter the allocation series came from.</summary>
    public string AllocApi = PerfAllocApi.PerThread;

    /// <summary>Where the main-thread queue depth was read from, or why it is unavailable.</summary>
    public string QueueSource = "";

    /// <summary>How the IL2CPP numbers were read, or why they are unavailable.</summary>
    public string Il2CppApi = PerfIl2CppApi.Unavailable;

    /// <summary>Unity's GC configuration at start (<c>GarbageCollector</c>), or null when unread.</summary>
    public bool? Il2CppIncremental;
    public ulong? Il2CppTimeSliceNs;
    public string? Il2CppGcMode;

    /// <summary>Which seams the Dev op hooked for this window (names), and any hook it could not place.</summary>
    public string SeamNote = "";

    public PerfRecording Recording = new(PerfCapacity.MinFrames);

    /// <summary>Sources returned in the compact summary.</summary>
    public const int SummarySourceRows = 8;

    public const string DrainNote =
        "Progenitor's main-thread queue is drained by Bootstrap before Core's tick, outside every "
        + "timing seam this op has; queueDepth is the proxy for that work, and progenitorMs covers "
        + "Core's frame dispatch (module ticks, plugin system hooks, event handlers) only.";

    /// <summary>
    /// R28 finding 28.21: <c>sharePercent</c> is a ratio of two differently scoped quantities, so
    /// say so in the report rather than letting a reader take it for "the share of the frame Core's
    /// tick spent". System-hook and event-handler samples are dispatched from the game's own system
    /// update, outside <c>Plugin.Frame</c>, while <c>coreTickMs</c> measures the <c>Plugin.Frame</c>
    /// loop alone — so a per-source row can legitimately exceed <c>coreTickMs</c>.
    /// </summary>
    public const string ScopeNote =
        "progenitorMs sums every Progenitor callback dispatched during the frame (frame subscribers, "
        + "plugin system hooks, event handlers); coreTickMs measures only the Plugin.Frame loop. "
        + "Hooks and events run inside the game's own system update, so a per-source row can exceed "
        + "coreTickMs and sharePercent is 'Progenitor cost per frame', not 'share of Core's tick'.";

    /// <summary>
    /// Set when a report was written for a window that ended before its deadline for a reason the
    /// numbers alone do not show — today, a Core swap. Read by the summary.
    /// </summary>
    public bool Partial;

    /// <summary>Monotonic capture id this report belongs to. 0 before a close stamps it.</summary>
    public long CaptureId;

    /// <summary>
    /// Milliseconds the close thread spent on snapshot, uninstall and stamping. Does not include
    /// Summary, JSON or the file write.
    /// </summary>
    public double CloseMs;

    /// <summary>
    /// Milliseconds the background writer spent on Summary, serialization and the file write.
    /// 0 in the file because the write has not finished when the JSON is built; status carries
    /// the end-to-end value.
    /// </summary>
    public double FinalizeMs;

    /// <summary>
    /// Silence at the moment of close: time since the last completed sample, or since capture
    /// start when there was none. A capture that ends during a stall omits that interval.
    /// </summary>
    public double SampleSilenceMsAtClose;

    /// <summary>
    /// True when <see cref="SampleSilenceMsAtClose"/> exceeds
    /// <see cref="PerfCloseObservation.SilenceGapMs"/>: this is not a clean capture that simply
    /// had no long frames. Diagnostic capture shape, not an attribution of a freeze.
    /// </summary>
    public bool EndedDuringSilence;

    /// <summary>Last completed frame interval, or null when no sample was recorded.</summary>
    public double? LastFrameMs;

    // ---- derived series ------------------------------------------------------------------------

    private float[] _scratch = Array.Empty<float>();

    private float[] Scratch(int needed)
    {
        if (_scratch.Length < needed) _scratch = new float[Math.Max(needed, 64)];
        return _scratch;
    }

    /// <summary>p50 / p95 / p99 / max / mean of one whole-frame series.</summary>
    public PerfDistribution Distribution(PerfSeries series)
    {
        var buffer = Scratch(Recording.Recorded);
        int n = Recording.CopySeries(series, buffer);
        return Describe(buffer, n);
    }

    public PerfDistribution SourceDistribution(int source)
    {
        var buffer = Scratch(Recording.Recorded);
        int n = Recording.CopySource(source, buffer);
        return Describe(buffer, n);
    }

    private static PerfDistribution Describe(float[] buffer, int n)
    {
        if (n <= 0) return default;
        double mean = PerfStats.Mean(buffer, n);
        double sum = PerfStats.Sum(buffer, n);
        Array.Sort(buffer, 0, n);
        return new PerfDistribution
        {
            Count = n,
            P50 = PerfStats.Percentile(buffer, n, 50),
            P95 = PerfStats.Percentile(buffer, n, 95),
            P99 = PerfStats.Percentile(buffer, n, 99),
            Max = buffer[n - 1],
            Mean = mean,
            Sum = sum,
        };
    }

    /// <summary>Total bytes allocated on the main thread across the recorded frames.</summary>
    public long AllocatedBytes()
    {
        long total = 0;
        for (int i = 0; i < Recording.Recorded; i++) total += Recording.AllocBytes(i);
        return total;
    }

    public double AllocatedBytesPerSecond()
    {
        double seconds = RecordedSeconds();
        if (seconds <= 0) return 0;
        return AllocatedBytes() / seconds;
    }

    /// <summary>Wall seconds the frames still in the ring span (not the whole window when wrapped).</summary>
    public double RecordedSeconds()
    {
        int n = Recording.Recorded;
        if (n <= 1) return ElapsedSeconds > 0 ? ElapsedSeconds : 0;
        double span = Recording.AtSeconds(n - 1) - Recording.AtSeconds(0);
        return span > 0 ? span : ElapsedSeconds;
    }

    // ---- json ----------------------------------------------------------------------------------

    public Dictionary<string, object?> ToJson()
    {
        var frame = Distribution(PerfSeries.FrameMs);
        var coreTick = Distribution(PerfSeries.CoreTickMs);
        var handlers = Distribution(PerfSeries.HandlersMs);
        var queue = Distribution(PerfSeries.QueueDepth);
        var delayed = Distribution(PerfSeries.DelayedDepth);
        var alloc = Distribution(PerfSeries.AllocBytes);
        var heap = Distribution(PerfSeries.HeapBytes);

        return new Dictionary<string, object?>
        {
            ["perf"] = new Dictionary<string, object?>
            {
                ["side"] = Side,
                ["world"] = World,
                ["startedUtc"] = StartedUtc.ToString("o", CultureInfo.InvariantCulture),
                ["requestedSeconds"] = PerfStats.Round(RequestedSeconds, 3),
                ["elapsedSeconds"] = PerfStats.Round(ElapsedSeconds, 3),
                ["stopReason"] = StopReason,
                ["partial"] = Partial,
                ["frames"] = Recording.Frames,
                ["framesRecorded"] = Recording.Recorded,
                ["recordedSeconds"] = PerfStats.Round(RecordedSeconds(), 3),
                ["capacity"] = Recording.Capacity,
                ["wrapped"] = Recording.Wrapped,
                ["sources"] = new List<string>(Recording.Sources.Names),
                ["sourceOverflow"] = Recording.Sources.Overflowed,
                ["allocApi"] = AllocApi,
                ["queueSource"] = QueueSource,
                ["note"] = DrainNote,
                ["captureId"] = CaptureId,
                ["closeMs"] = PerfStats.Round(CloseMs, 3),
                ["sampleSilenceMsAtClose"] = PerfStats.Round(SampleSilenceMsAtClose, 3),
                ["endedDuringSilence"] = EndedDuringSilence,
                ["lastFrameMs"] = LastFrameMs == null ? null : PerfStats.Round(LastFrameMs.Value, 3),
            },
            ["frame"] = new Dictionary<string, object?>
            {
                ["deltaMs"] = frame.ToJson(3),
                ["meanFps"] = frame.Mean > 0 ? PerfStats.Round(1000.0 / frame.Mean, 2) : 0,
            },
            ["progenitor"] = new Dictionary<string, object?>
            {
                ["msPerFrame"] = handlers.ToJson(3),
                ["coreTickMsPerFrame"] = coreTick.ToJson(3),
                ["sharePercent"] = frame.Sum > 0 ? PerfStats.Round(handlers.Sum / frame.Sum * 100.0, 3) : 0,
                ["scopeNote"] = ScopeNote,
            },
            ["sources"] = SourceRows(handlers.Sum, frame.Sum, int.MaxValue),
            ["hooks"] = HookRows(),
            ["events"] = EventRows(),
            ["mainThread"] = new Dictionary<string, object?>
            {
                ["queueDepth"] = queue.ToJson(2),
                ["delayedDepth"] = delayed.ToJson(2),
                ["drainTimed"] = false,
                ["note"] = DrainNote,
            },
            ["gc"] = new Dictionary<string, object?>
            {
                ["api"] = AllocApi,
                ["allocatedBytes"] = AllocatedBytes(),
                ["allocatedBytesPerSecond"] = PerfStats.Round(AllocatedBytesPerSecond(), 1),
                ["bytesPerFrame"] = alloc.ToJson(0),
                ["heapBytes"] = heap.ToJson(0),
            },
            ["worstFrames"] = WorstFrameRows(5),
            ["il2cpp"] = Il2CppJson(gcFrameLimit: PerfFrameDetail.MaxGcEvents, sourceLimit: int.MaxValue),
            ["clrGc"] = ClrGcJson(),
            ["patches"] = PatchRows(),
            ["seams"] = SeamJson(),
            ["slowestFrames"] = SlowestFrameRows(10, withSources: true),
        };
    }

    /// <summary>Compact result the bridge returns beside the report path.</summary>
    public Dictionary<string, object?> Summary()
    {
        var frame = Distribution(PerfSeries.FrameMs);
        var handlers = Distribution(PerfSeries.HandlersMs);
        var queue = Distribution(PerfSeries.QueueDepth);
        var worst = WorstFrameRows(1);

        return new Dictionary<string, object?>
        {
            ["frames"] = Recording.Frames,
            ["framesRecorded"] = Recording.Recorded,
            ["elapsedSeconds"] = PerfStats.Round(ElapsedSeconds, 2),
            ["stopReason"] = StopReason,
            ["partial"] = Partial,
            ["wrapped"] = Recording.Wrapped,
            ["frameMs"] = frame.ToJson(3),
            ["progenitorMs"] = handlers.ToJson(3),
            ["progenitorSharePercent"] = frame.Sum > 0 ? PerfStats.Round(handlers.Sum / frame.Sum * 100.0, 3) : 0,
            ["sources"] = SourceRows(handlers.Sum, frame.Sum, SummarySourceRows),
            ["allocBytesPerSecond"] = PerfStats.Round(AllocatedBytesPerSecond(), 1),
            ["heapBytes"] = Recording.Recorded > 0 ? Recording.HeapBytes(Recording.Recorded - 1) : 0L,
            ["queueDepth"] = queue.ToJson(2),
            ["worstFrame"] = worst.Count > 0 ? worst[0] : null,
            ["il2cpp"] = Il2CppJson(gcFrameLimit: 12, sourceLimit: 8),
            ["clrGc"] = ClrGcJson(),
            ["patches"] = PatchRows(),
            ["slowestFrames"] = SlowestFrameRows(5, withSources: false),
            ["note"] = DrainNote,
            ["captureId"] = CaptureId,
            ["closeMs"] = PerfStats.Round(CloseMs, 3),
            ["finalizeMs"] = PerfStats.Round(FinalizeMs, 3),
            ["sampleSilenceMsAtClose"] = PerfStats.Round(SampleSilenceMsAtClose, 3),
            ["endedDuringSilence"] = EndedDuringSilence,
            ["lastFrameMs"] = LastFrameMs == null ? null : PerfStats.Round(LastFrameMs.Value, 3),
        };
    }

    /// <summary>Per-source rows, heaviest total first; <paramref name="limit"/> caps the list.</summary>
    public List<Dictionary<string, object?>> SourceRows(double handlersTotalMs, double frameTotalMs, int limit)
    {
        var rows = new List<Dictionary<string, object?>>();
        var names = Recording.Sources.Names;
        var stats = new List<(string Name, PerfDistribution Dist)>(names.Count);
        for (int i = 0; i < names.Count; i++)
        {
            var dist = SourceDistribution(i);
            if (dist.Count == 0 || dist.Sum <= 0) continue;
            stats.Add((names[i], dist));
        }
        stats.Sort((a, b) => b.Dist.Sum.CompareTo(a.Dist.Sum));

        for (int i = 0; i < stats.Count && rows.Count < limit; i++)
        {
            var (name, dist) = stats[i];
            rows.Add(new Dictionary<string, object?>
            {
                ["name"] = name,
                ["p50Ms"] = PerfStats.Round(dist.P50, 3),
                ["p95Ms"] = PerfStats.Round(dist.P95, 3),
                ["maxMs"] = PerfStats.Round(dist.Max, 3),
                ["meanMs"] = PerfStats.Round(dist.Mean, 4),
                ["totalMs"] = PerfStats.Round(dist.Sum, 2),
                ["shareOfProgenitorPercent"] = handlersTotalMs > 0 ? PerfStats.Round(dist.Sum / handlersTotalMs * 100.0, 2) : 0,
                ["shareOfFramePercent"] = frameTotalMs > 0 ? PerfStats.Round(dist.Sum / frameTotalMs * 100.0, 3) : 0,
            });
        }
        return rows;
    }

    public List<Dictionary<string, object?>> HookRows()
    {
        var rows = new List<Dictionary<string, object?>>();
        foreach (var pair in Recording.Hooks)
        {
            rows.Add(new Dictionary<string, object?>
            {
                ["pluginId"] = pair.Key.PluginId,
                ["system"] = pair.Key.System,
                ["when"] = pair.Key.Before ? "before" : "after",
                ["calls"] = pair.Value.Calls,
                ["totalMs"] = PerfStats.Round(pair.Value.TotalMs, 3),
                ["maxMs"] = PerfStats.Round(pair.Value.MaxMs, 3),
            });
        }
        rows.Sort((a, b) => Convert.ToDouble(b["totalMs"]).CompareTo(Convert.ToDouble(a["totalMs"])));
        return rows;
    }

    public List<Dictionary<string, object?>> EventRows()
    {
        var rows = new List<Dictionary<string, object?>>();
        foreach (var pair in Recording.Events)
        {
            rows.Add(new Dictionary<string, object?>
            {
                ["pluginId"] = pair.Key.PluginId,
                ["event"] = pair.Key.Event,
                ["calls"] = pair.Value.Calls,
                ["totalMs"] = PerfStats.Round(pair.Value.TotalMs, 3),
                ["maxMs"] = PerfStats.Round(pair.Value.MaxMs, 3),
            });
        }
        rows.Sort((a, b) => Convert.ToDouble(b["totalMs"]).CompareTo(Convert.ToDouble(a["totalMs"])));
        return rows;
    }

    public const string Il2CppNote =
        "usedBytes is il2cpp_gc_get_used_size (heap size minus free heap blocks), so it moves in heap-block "
        + "steps rather than per object: per-source bytes are estimates that become meaningful summed over many "
        + "frames. Growth is counted only on frames without an IL2CPP collection. 'outside' is growth no timed "
        + "Progenitor callback was running for: the game, Harmony trampolines, Bootstrap's queue drain, other "
        + "BepInEx plugins, and this probe's own GC count read (one boxed int per frame).";

    public const string SeamGapNote =
        "largestGap names the two consecutive seams (or window bounds) with the most time between them in that "
        + "frame. The window runs from the previous Core tick's end to this Core tick's start; a seam that did not "
        + "fire is left out, so a missing seam widens its neighbors' gap.";

    /// <summary>The <c>il2cpp</c> section: collector, growth, per-source rates and the frames each collector ran in.</summary>
    public Dictionary<string, object?> Il2CppJson(int gcFrameLimit, int sourceLimit)
    {
        var d = Recording.Detail;
        double seconds = d.TotalSeconds > 0 ? d.TotalSeconds : RecordedSeconds();

        var used = new List<float>();
        for (int i = 0; i < Recording.Recorded; i++)
        {
            long u = d.Il2CppUsed(Recording.Slot(i));
            if (u >= 0) used.Add(u);
        }
        var usedArr = used.ToArray();

        var bySource = new List<(string Name, double Rate)>();
        var names = Recording.Sources.Names;
        for (int i = 0; i < names.Count && i < d.SourceIl2CppBytes.Length; i++)
        {
            if (d.SourceIl2CppBytes[i] <= 0) continue;
            bySource.Add((names[i], seconds > 0 ? d.SourceIl2CppBytes[i] / seconds : 0));
        }
        bySource.Sort((a, b) => b.Rate.CompareTo(a.Rate));
        var sources = new Dictionary<string, object?>();
        for (int i = 0; i < bySource.Count && i < sourceLimit; i++)
            sources[bySource[i].Name] = PerfStats.Round(bySource[i].Rate, 1);
        long outside = Math.Max(0, d.Il2CppGrowthNonGc - d.Il2CppAttributedNonGc);
        sources["outside"] = seconds > 0 ? PerfStats.Round(outside / seconds, 1) : 0;

        var gcFrames = new List<Dictionary<string, object?>>();
        var gcFrameMs = new float[d.GcEventCount];
        for (int i = 0; i < d.GcEventCount; i++)
        {
            var e = d.GcEvents[i];
            gcFrameMs[i] = e.FrameMs;
            if (gcFrames.Count >= gcFrameLimit) continue;
            gcFrames.Add(new Dictionary<string, object?>
            {
                ["atSeconds"] = PerfStats.Round(e.AtSeconds, 3),
                ["frameMs"] = PerfStats.Round(e.FrameMs, 3),
                ["kinds"] = PerfGcKindNames.Of(e.Kinds),
                ["il2cppUsedBefore"] = e.Il2CppUsedBefore,
                ["il2cppUsedAfter"] = e.Il2CppUsedAfter,
                ["largestGap"] = GapJson(e.GapFrom, e.GapTo, e.GapMs),
            });
        }

        return new Dictionary<string, object?>
        {
            ["available"] = d.Il2CppAvailable,
            ["api"] = Il2CppApi,
            ["incremental"] = Il2CppIncremental,
            ["incrementalTimeSliceNs"] = Il2CppTimeSliceNs,
            ["gcMode"] = Il2CppGcMode,
            ["gcCount"] = d.Il2CppCollections,
            ["seconds"] = PerfStats.Round(seconds, 3),
            ["usedBytes"] = Describe(usedArr, usedArr.Length).ToJson(0),
            ["usedBytesFirst"] = d.Il2CppUsedFirst,
            ["usedBytesLast"] = d.Il2CppUsedLast,
            ["heapBytesFirst"] = d.Il2CppHeapFirst,
            ["heapBytesLast"] = d.Il2CppHeapLast,
            ["growthBytesPerSecond"] = seconds > 0 ? PerfStats.Round(d.Il2CppGrowthNonGc / seconds, 1) : 0,
            ["attributedBytesPerSecond"] = seconds > 0 ? PerfStats.Round(d.Il2CppAttributedNonGc / seconds, 1) : 0,
            ["bytesPerSecondBySource"] = sources,
            ["gcFrames"] = gcFrames,
            ["gcFramesTotal"] = d.GcEventCount + d.GcEventsDropped,
            ["gcFramesTruncated"] = d.GcEventCount > gcFrames.Count || d.GcEventsDropped > 0,
            ["gcFrameMs"] = Describe(gcFrameMs, gcFrameMs.Length).ToJson(3),
            ["note"] = Il2CppNote,
        };
    }

    /// <summary>CLR (managed plugin heap) collections during the window, per generation.</summary>
    public Dictionary<string, object?> ClrGcJson()
    {
        var d = Recording.Detail;
        return new Dictionary<string, object?>
        {
            ["gen0"] = d.Gen0Collections,
            ["gen1"] = d.Gen1Collections,
            ["gen2"] = d.Gen2Collections,
        };
    }

    /// <summary>Timed Harmony patch / Bootstrap slot bodies, heaviest first. Calls is the trampoline count.</summary>
    public List<Dictionary<string, object?>> PatchRows()
    {
        var d = Recording.Detail;
        var rows = new List<Dictionary<string, object?>>();
        int frames = Math.Max(1, Recording.Frames);
        var names = Progenitor.Dev.PerfPatchTimer.Names;
        for (int i = 0; i < d.PatchTotalMs.Length; i++)
        {
            if (d.PatchCalls[i] == 0 && d.PatchTotalMs[i] <= 0) continue;
            rows.Add(new Dictionary<string, object?>
            {
                ["name"] = i < names.Length ? names[i] : "?",
                ["calls"] = d.PatchCalls[i],
                ["callsPerFrame"] = PerfStats.Round((double)d.PatchCalls[i] / frames, 1),
                ["totalMs"] = PerfStats.Round(d.PatchTotalMs[i], 3),
                ["meanMsPerFrame"] = PerfStats.Round(d.PatchTotalMs[i] / frames, 4),
                ["maxFrameMs"] = PerfStats.Round(d.PatchMaxFrameMs[i], 3),
            });
        }
        rows.Sort((a, b) => Convert.ToDouble(b["totalMs"]).CompareTo(Convert.ToDouble(a["totalMs"])));
        return rows;
    }

    /// <summary>How often each seam fired, and the note on reading the gaps.</summary>
    public Dictionary<string, object?> SeamJson()
    {
        var d = Recording.Detail;
        var fired = new Dictionary<string, object?>();
        for (int s = 0; s < d.SeamFiredFrames.Length; s++)
            fired[Progenitor.Dev.PerfSeams.Names[s]] = d.SeamFiredFrames[s];
        return new Dictionary<string, object?>
        {
            ["firedFrames"] = fired,
            ["hooked"] = SeamNote,
            ["note"] = SeamGapNote,
        };
    }

    private static Dictionary<string, object?> GapJson(int from, int to, double ms) => new()
    {
        ["from"] = PerfSeamGaps.Label(from),
        ["to"] = PerfSeamGaps.Label(to),
        ["ms"] = MsOrNull(ms),
    };

    /// <summary>Rounded milliseconds, or null for a value that was not measured (NaN). JSON has no NaN.</summary>
    private static double? MsOrNull(double ms) =>
        double.IsNaN(ms) || double.IsInfinity(ms) ? null : PerfStats.Round(ms, 3);

    /// <summary>
    /// The frames with the longest frame interval (not the most Progenitor time): where the time
    /// went between seams, which collectors ran, and how much was Progenitor's.
    /// </summary>
    public List<Dictionary<string, object?>> SlowestFrameRows(int count, bool withSources)
    {
        var rows = new List<Dictionary<string, object?>>();
        var names = Recording.Sources.Names;
        var d = Recording.Detail;
        foreach (var i in Recording.SlowestFrames(count))
        {
            int slot = Recording.Slot(i);
            double frameMs = Recording.FrameMs(i);
            double progenitorMs = Recording.HandlersMs(i);
            double patchMs = d.PatchMs(slot);
            var seams = new Dictionary<string, object?>();
            for (int s = 0; s < PerfFrameDetail.SeamCount; s++)
            {
                double v = d.SeamMs(slot, s);
                if (double.IsNaN(v)) continue;
                seams[Progenitor.Dev.PerfSeams.Names[s]] = PerfStats.Round(v, 3);
            }
            var row = new Dictionary<string, object?>
            {
                ["frame"] = i,
                ["atSeconds"] = PerfStats.Round(Recording.AtSeconds(i), 3),
                ["frameMs"] = PerfStats.Round(frameMs, 3),
                ["progenitorMs"] = PerfStats.Round(progenitorMs, 3),
                ["patchMs"] = PerfStats.Round(patchMs, 3),
                ["outsideMs"] = PerfStats.Round(Math.Max(0, frameMs - progenitorMs - patchMs), 3),
                ["coreTickMs"] = PerfStats.Round(Recording.CoreTickMs(i), 3),
                ["gc"] = PerfGcKindNames.Of(d.GcKinds(slot)),
                ["il2cppGrowthBytes"] = d.Il2CppGrowth(slot),
                ["allocBytes"] = Recording.AllocBytes(i),
                ["queueDepth"] = Recording.QueueDepth(i),
                ["largestGap"] = GapJson(d.GapFrom(slot), d.GapTo(slot), d.GapMs(slot)),
                ["windowMs"] = MsOrNull(d.WindowMs(slot)),
                ["seamsMs"] = seams,
            };
            if (withSources)
            {
                var breakdown = new Dictionary<string, object?>();
                for (int s = 0; s < names.Count; s++)
                {
                    double ms = Recording.SourceMs(i, s);
                    if (ms <= 0) continue;
                    breakdown[names[s]] = PerfStats.Round(ms, 3);
                }
                row["sources"] = breakdown;
            }
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>The heaviest frames, each with the per-source breakdown that made it heavy.</summary>
    public List<Dictionary<string, object?>> WorstFrameRows(int count)
    {
        var rows = new List<Dictionary<string, object?>>();
        var names = Recording.Sources.Names;
        foreach (var i in Recording.WorstFrames(count))
        {
            var breakdown = new Dictionary<string, object?>();
            for (int s = 0; s < names.Count; s++)
            {
                double ms = Recording.SourceMs(i, s);
                if (ms <= 0) continue;
                breakdown[names[s]] = PerfStats.Round(ms, 3);
            }
            rows.Add(new Dictionary<string, object?>
            {
                ["frame"] = i,
                ["atSeconds"] = PerfStats.Round(Recording.AtSeconds(i), 3),
                ["frameMs"] = PerfStats.Round(Recording.FrameMs(i), 3),
                ["progenitorMs"] = PerfStats.Round(Recording.HandlersMs(i), 3),
                ["coreTickMs"] = PerfStats.Round(Recording.CoreTickMs(i), 3),
                ["queueDepth"] = Recording.QueueDepth(i),
                ["allocBytes"] = Recording.AllocBytes(i),
                ["sources"] = breakdown,
            });
        }
        return rows;
    }
}

/// <summary>p50 / p95 / p99 / max / mean / sum of one series.</summary>
internal struct PerfDistribution
{
    public int Count;
    public double P50;
    public double P95;
    public double P99;
    public double Max;
    public double Mean;
    public double Sum;

    public Dictionary<string, object?> ToJson(int digits) => new()
    {
        ["p50"] = PerfStats.Round(P50, digits),
        ["p95"] = PerfStats.Round(P95, digits),
        ["p99"] = PerfStats.Round(P99, digits),
        ["max"] = PerfStats.Round(Max, digits),
        ["mean"] = PerfStats.Round(Mean, Math.Max(digits, 2)),
        ["samples"] = Count,
    };
}

/// <summary>Names of the two allocation counters the op can use, in preference order.</summary>
internal static class PerfIl2CppApi
{
    public const string Unavailable = "unavailable";
    public const string UsedSizeAndCount = "IL2CPP.il2cpp_gc_get_used_size + Il2CppSystem.GC.CollectionCount(0)";
}

internal static class PerfAllocApi
{
    public const string PerThread = "GC.GetAllocatedBytesForCurrentThread";
    public const string TotalMemory = "GC.GetTotalMemory(false) delta";
}

/// <summary>
/// Where a finished report is written: <c>progenitor-perf</c> beside the reload folder, so the
/// plugin reloader never scans it — the same rule <c>progenitor-capture</c> follows.
/// </summary>
internal static class PerfReportPaths
{
    public const string FolderName = "progenitor-perf";

    public static string FileName(DateTime utc) =>
        utc.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + ".json";

    public static string Folder(string reloadFolder)
    {
        if (string.IsNullOrWhiteSpace(reloadFolder)) return FolderName;
        var parent = Path.GetDirectoryName(reloadFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.IsNullOrEmpty(parent)
            ? Path.Combine(reloadFolder, FolderName)
            : Path.Combine(parent, FolderName);
    }

    public static string For(string reloadFolder, DateTime utc) => Path.Combine(Folder(reloadFolder), FileName(utc));
}
