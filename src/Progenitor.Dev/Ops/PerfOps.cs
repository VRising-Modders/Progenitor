using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using Progenitor.Reload;

namespace Progenitor.Dev.Ops;

/// <summary>
/// Bridge op <c>perf</c>: what does Progenitor cost this process, per frame, without a profiler.
///
/// <para><b>Why it exists.</b> "Is the framework cheap?" was until now answered by reading code
/// and hoping. This op answers it with numbers a session can act on: the game's frame delta, how
/// much of that frame Progenitor's own main-thread dispatch spent, and — the part that makes it
/// actionable — which module spent it. Written for the server, but nothing in it is server-only,
/// so it runs on the client too.</para>
///
/// <para><b>How the split is measured.</b> Core exposes one timing seam,
/// <see cref="Progenitor.Dev.PerfProbe"/>, and this class is the only implementation of its sink.
/// While a window is open Core times each <c>Plugin.Frame</c> subscriber separately, each plugin
/// system hook (<c>SystemHooks.Invoke</c>), and each event-bus handler
/// (<c>EventBus.Publish</c>) — the three funnels every per-frame Progenitor cost goes through.
/// The subscriber's declaring type names the module (<c>core:effects</c> is the ServerBeam
/// keep-alive poll, <c>core:units</c> the unit arrival poll, <c>core:content</c> the prefab
/// refresh / edit flush / handshake watch, <c>core:networking</c> the manifest and kick watches),
/// and a subscriber from any other assembly is <c>plugin:&lt;assembly&gt;</c>. Hooks and events
/// are bucketed by plugin id (<c>hook:progenitor.sample</c>).</para>
///
/// <para><b>What it cannot see.</b> The main-thread queue is drained by Bootstrap
/// (<c>ProcessHost.Tick</c> → <c>WorkQueue.Drain</c>) before Core's tick, and Bootstrap is not
/// instrumented, so drain execution time is not attributed. The op reports the queue's depth and
/// the delayed-queue depth every frame instead, read straight off Bootstrap's queue objects, and
/// says so in the report (<see cref="PerfReport.DrainNote"/>).</para>
///
/// <para><b>Cost.</b> The seam is a static field read and a branch per dispatch when no window is
/// open. While one is: two <c>Stopwatch.GetTimestamp()</c> calls per dispatched callback, one
/// reference-identity dictionary lookup, and one float add. Per frame it is a fixed row of
/// <see cref="PerfSourceTable.MaxSources"/> floats copied into a preallocated matrix, two GC
/// counter reads and two interface <c>Count</c> reads. Nothing is allocated per frame and no
/// string is built per frame — classification happens once per distinct delegate. Ring buffers are
/// preallocated at <c>start</c> for the requested window, capped at
/// <see cref="PerfCapacity.MaxFrames"/> (60 s at 120 fps); a longer or faster run wraps and keeps
/// the most recent frames, which the report flags.</para>
///
/// <para><b>Ownership.</b> The sink lives in this collectible plugin context and is installed into
/// a Core static, so it is uninstalled on <c>stop</c>, on the deadline, and in
/// <see cref="Shutdown"/> (Dev <c>Unload</c>, which the Core reload cascade runs before Core's
/// Uninstall). Timer and sink cleanup is one <see cref="PerfCaptureLifetime"/>: Dispose is
/// idempotent and drops the release callback so no static keeps a delegate into the old Core.
/// Core's <c>Plugin.Uninstall</c> clears the slot as a backstop. The delegate-to-source
/// cache is dropped with it so no plugin delegate stays reachable from here.</para>
///
/// <para><b>The window closes from Dev, not from Core.</b> R28 findings 14 and 17: the only thing
/// that ever closed a window was <c>Sink.CoreTick</c>, which runs inside Core's frame dispatch —
/// and the two situations this op documents as the interesting ones (a boot, a main menu, a
/// Core-less process) are precisely the ones where no frame ticks. The deadline never fired, the
/// sink stayed installed as a Core static holding a Dev collectible object, and every later
/// <c>perf start</c> threw "a perf window is already running". A Core swap mid-capture wedged it the
/// same way from the other side. So a Dev-owned <see cref="System.Threading.Timer"/> polls
/// <see cref="PerfWindowPolicy"/> twice a second: it closes the window on the deadline whether or
/// not frames are arriving, and it closes it with <c>stopReason=core-swap</c> — writing the partial
/// report — the moment <see cref="Progenitor.Dev.PerfProbe"/> says the generation this sink was
/// installed into is gone. The timer is disposed with the sink, so it pins nothing.</para>
///
/// <para><b>But the watchdog never serializes.</b> R29 finding 5: the timer callback called
/// <c>Stop</c> directly, which walked the live <see cref="PerfRecording"/> — the ring buffers and
/// the hook / event dictionaries the game main thread writes on every frame, under no lock — to
/// build the summary and the JSON. A dictionary insert during that enumeration threw
/// <c>InvalidOperationException</c>, which the caller reported as <c>writeError</c> with the whole
/// capture lost; an array write during the copy gave a silently torn report. So the timer now only
/// <see cref="PerfCloseGate.Request"/>s a close and the <b>main thread</b> performs it, from the
/// next <c>Sink.CoreTick</c> or the next op entry (both of which already call
/// <see cref="CloseIfDue"/>). The timer performs the close itself only when the recording has been
/// quiescent for <see cref="PerfCloseGate.QuiescentSeconds"/> — the non-ticking process it exists
/// for, where nothing is writing. Either way the close snapshots the recording under
/// <see cref="PerfCloseGate.RecordGate"/> first. Snapshotting protects concurrent recording; it
/// does not move <c>Summary()</c>, JSON or the file write off that thread —
/// <see cref="PerfFinalize"/> does, on a dedicated background thread. The close thread waits for
/// the array copies and uninstall only. This is a diagnostic close-path fix, not an explanation
/// of any observed freeze.</para>
///
/// <para>The queue depth is read from Bootstrap's queue only. Resolving Core's
/// <c>Progenitor.Dev.MainThread</c> as a fallback meant holding a <b>Core</b> static collection for
/// the capture's life, which is the pin this file's own doc comment is about; a process without
/// Bootstrap now reports <c>queueSource: unavailable</c> and depth <c>-1</c> instead.</para>
/// </summary>
internal static class PerfOps
{
    private static readonly object Gate = new();

    private static readonly double MsPerTick = 1000.0 / Stopwatch.Frequency;

    /// <summary>How often the Dev-side watchdog checks the deadline and the Core generation.</summary>
    private const int WatchdogMs = 500;

    private static bool _running;
    private static long _nextCaptureId;
    private static long _captureId;
    private static PerfProgress? _progress;
    private static Stopwatch? _clock;
    private static double _seconds;
    private static PerfReport? _live;
    private static Sink? _sink;
    private static PerfCaptureLifetime? _lifetime;
    private static int _installedGeneration;
    private static System.Threading.Timer? _watchdog;

    /// <summary>
    /// Who may close the current window, and the lock the sink's writes and the close's snapshot
    /// share (R29 finding 5). Replaced per window; null when none is running.
    /// </summary>
    private static PerfCloseGate? _close;

    /// <summary>
    /// <c>Stopwatch</c> timestamp of the last main-thread write into the recording, so the watchdog
    /// can tell a ticking process (request the close, let the main thread do it) from a quiescent
    /// one (close it here, because nothing is writing). 0 when nothing has been written yet.
    /// </summary>
    private static long _lastWriteStamp;

    private static string? _finishedPath;

    // ---- op entry ---------------------------------------------------------------------------

    /// <summary>
    /// <c>{"action":"start"|"stop"|"status"}</c>. Main thread, both sides. <c>start</c> installs
    /// the sink and returns immediately; the window then runs on Core's frame dispatch until
    /// <c>seconds</c> elapse or <c>stop</c> arrives, so a window longer than the bridge's 30 s
    /// socket timeout is fine.
    /// </summary>
    internal static object Run(JsonElement args)
    {
        // Any entry into the op is also a chance to notice a window that should have closed, for a
        // process where neither Core's dispatch nor the watchdog got there first.
        CloseIfDue();
        return PerfArgs.ParseAction(args) switch
        {
            "start" => Start(args),
            "stop" => RequestAndClose(PerfStopReason.Stop),
            _ => Status(),
        };
    }

    /// <summary>
    /// Dev plugin unload: close the window, start background finalization, then detach so an
    /// in-flight writer cannot publish into a later generation. Does not join the writer.
    /// </summary>
    internal static void Shutdown()
    {
        try
        {
            lock (Gate)
            {
                if (!_running)
                {
                    Uninstall();
                    PublishProgress(null);
                    PerfFinalize.Detach();
                    return;
                }
            }
            RequestAndClose(PerfStopReason.Unload);
            PublishProgress(null);
            PerfFinalize.Detach();
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"perf Shutdown: {e.Message}");
            try { Uninstall(); } catch { }
            lock (Gate)
            {
                _running = false;
                _live = null;
                _close = null;
                _lastWriteStamp = 0;
                PublishProgress(null);
            }
            try { PerfFinalize.Detach(); } catch { }
        }
    }

    private static object Start(JsonElement args)
    {
        lock (Gate)
        {
            var refusal = PerfFinalize.StartRefusal(_running, _close != null, PerfFinalize.IsBusy);
            if (refusal != null)
                throw new InvalidOperationException(refusal);
        }

        double seconds = PerfArgs.ClampSeconds(ReadDouble(args, "seconds", PerfArgs.DefaultSeconds));
        int capacity = PerfCapacity.For(seconds);

        // No world is required: profiling the boot, the main menu, or a Core-less process is
        // exactly when the numbers are interesting. The world name is metadata only.
        string worldName = "";
        try
        {
            var world = Progenitor.Plugin.IsServerProcess ? Progenitor.Worlds.Server : Progenitor.Worlds.Client;
            if (world != null && world.IsCreated) worldName = world.Name;
        }
        catch { }

        var report = new PerfReport
        {
            Side = Progenitor.Plugin.IsServerProcess ? "server" : "client",
            World = worldName,
            StartedUtc = DateTime.UtcNow,
            RequestedSeconds = seconds,
            Recording = new PerfRecording(capacity),
        };

        // The op is a main-thread op (Handlers.MainThreadOps), so the thread starting the window is
        // the game main thread: the one thread that writes into the recording, and therefore the
        // only one allowed to close it while frames are arriving (R29 finding 5).
        var close = new PerfCloseGate();
        close.ClaimOwner();

        long captureId = Interlocked.Increment(ref _nextCaptureId);
        var progress = new PerfProgress(captureId, Stopwatch.GetTimestamp(), Stopwatch.Frequency);
        var sink = new Sink(report, close, progress, captureId);
        report.AllocApi = sink.AllocApi;
        report.QueueSource = sink.QueueSource;
        report.Il2CppApi = sink.Il2CppApi;
        ReadGcConfig(report);
        var lifetime = new PerfCaptureLifetime(() => ReleaseGeneration(sink));

        PerfCaptureLifetime? previousLifetime;
        lock (Gate)
        {
            previousLifetime = _lifetime;
            _progress?.Complete();
            _live = report;
            PublishProgress(progress);
            _sink = sink;
            _lifetime = lifetime;
            _close = close;
            _captureId = captureId;
            _lastWriteStamp = 0;
            _seconds = seconds;
            _running = true;
            _clock = Stopwatch.StartNew();
        }
        previousLifetime?.Dispose();

        Progenitor.Dev.PerfProbe.Install(sink);
        lock (Gate) { _installedGeneration = Progenitor.Dev.PerfProbe.Generation; }
        report.SeamNote = sink.HookSeams();
        StartWatchdog();

        Plugin.Logger?.LogInfo(
            $"perf: started on {report.Side} for {seconds:0.##} s (ring {capacity} frames, "
            + $"alloc api {sink.AllocApi}, queue {sink.QueueSource}).");

        return new Dictionary<string, object?>
        {
            ["running"] = true,
            ["side"] = report.Side,
            ["world"] = report.World,
            ["seconds"] = seconds,
            ["capacity"] = capacity,
            ["captureId"] = captureId,
            ["allocApi"] = sink.AllocApi,
            ["queueSource"] = sink.QueueSource,
            ["il2cppApi"] = sink.Il2CppApi,
            ["seams"] = report.SeamNote,
            ["note"] = "sampling every frame; call perf {\"action\":\"stop\"} for the report, "
                       + "or let the deadline close it. " + PerfReport.DrainNote,
        };
    }

    /// <summary>
    /// The <c>stop</c> action and the unload path. Both run on the game main thread, but they go
    /// through the same gate as the watchdog so there is exactly one close path: latch the reason
    /// (so a Core swap the timer already noticed keeps its name), then perform it here.
    /// </summary>
    private static object RequestAndClose(string reason)
    {
        PerfCloseGate? close;
        lock (Gate) { close = _close; }
        close?.Request(reason);
        return PerformClose(close?.Take() ?? reason);
    }

    /// <summary>
    /// Performs a pending close if this thread is allowed to: the owner (main) thread always,
    /// another thread only when the recording is quiescent
    /// (<see cref="PerfCloseGate.MayCloseOffOwnerThread"/>) or the pending reason is a Core swap
    /// (nothing is writing). A refused close stays pending for the next <c>CoreTick</c> or op
    /// entry. Never throws.
    /// </summary>
    private static void PumpClose()
    {
        PerfCloseGate? close;
        bool running;
        double sinceWrite;
        lock (Gate)
        {
            close = _close;
            running = _running;
            sinceWrite = SecondsSinceLastWriteLocked();
        }

        if (close == null || !running) return;
        var pending = close.PendingReason;
        if (pending == null) return;
        // Core is gone, so nothing is writing: do not wait for the quiescent grace that exists
        // to protect a ticking main thread. Unload from Shutdown is already on the main thread.
        bool coreGone = pending == PerfStopReason.CoreSwap;
        if (!close.OnOwnerThread && !coreGone && !PerfCloseGate.MayCloseOffOwnerThread(sinceWrite)) return;

        var reason = close.Take();
        if (reason == null) return;
        try { PerformClose(reason); }
        catch (Exception e) { Plugin.Logger?.LogWarning($"perf {reason} stop: {e.Message}"); }
    }

    /// <summary>Seconds since the last main-thread write, or since the window opened. Call under <see cref="Gate"/>.</summary>
    private static double SecondsSinceLastWriteLocked()
    {
        double elapsed = _clock?.Elapsed.TotalSeconds ?? 0;
        long stamp = System.Threading.Interlocked.Read(ref _lastWriteStamp);
        if (stamp == 0) return elapsed;
        return (Stopwatch.GetTimestamp() - stamp) * MsPerTick / 1000.0;
    }

    private static object PerformClose(string reason)
    {
        PerfReport? report;
        Stopwatch? clock;
        PerfCloseGate? close;
        PerfProgress? progress;
        long closingId;
        lock (Gate)
        {
            if (!_running)
            {
                var existing = PerfFinalize.Read();
                if (existing.State != PerfFinalize.StateNone)
                    return PerfFinalize.Result(existing);
                if (_finishedPath != null)
                    return new Dictionary<string, object?>
                    {
                        ["running"] = false,
                        ["path"] = _finishedPath,
                        ["reportState"] = PerfFinalize.StateCompleted,
                        ["note"] = "the window had already closed; this is the finished report",
                    };
                throw new InvalidOperationException("no perf window is running; start one first");
            }
            _running = false;
            closingId = _captureId;
            report = _live;
            clock = _clock;
            close = _close;
            progress = _progress;
        }

        // Reject late frame publications before Freeze/Uninstall: a CoreTick already in flight
        // must not move retained historical values or a capture that starts afterwards.
        progress?.Complete();

        long closeStarted = Stopwatch.GetTimestamp();
        try
        {
            if (report == null)
            {
                Uninstall();
                throw new InvalidOperationException("perf had no report to write");
            }

            // Bounded close-thread work: snapshot under the record lock, uninstall the sink,
            // stamp close metadata. Summary, JSON and File.WriteAllText run on PerfFinalize's
            // writer thread. Snapshotting does not itself move that work off this thread.
            if (close != null) report.Recording = close.Freeze(report.Recording);

            Uninstall();

            report.ElapsedSeconds = clock?.Elapsed.TotalSeconds ?? 0;
            report.StopReason = reason;
            report.Partial = reason == PerfStopReason.CoreSwap || reason == PerfStopReason.Unload;
            PerfCloseObservation.Stamp(report, progress?.Read(Stopwatch.GetTimestamp()));

            string path = PerfReportPaths.For(PluginReloader.Folder, report.StartedUtc);
            double closeMs = (Stopwatch.GetTimestamp() - closeStarted) * MsPerTick;
            report.CloseMs = closeMs;
            report.CaptureId = closingId;

            if (!PerfFinalize.TryStart(closingId, report, path, closeMs))
            {
                Plugin.Logger?.LogWarning(
                    $"perf: capture {closingId} snapshotted but not queued ({PerfFinalize.BusyRefusal})");
                return new Dictionary<string, object?>
                {
                    ["running"] = false,
                    ["reportState"] = PerfFinalize.StateFailed,
                    ["captureId"] = closingId,
                    ["path"] = null,
                    ["writeError"] = PerfFinalize.BusyRefusal,
                    ["closeMs"] = closeMs,
                    ["endedDuringSilence"] = report.EndedDuringSilence,
                    ["sampleSilenceMsAtClose"] = report.SampleSilenceMsAtClose,
                    ["note"] = PerfFinalize.BusyRefusal,
                };
            }

            lock (Gate)
            {
                _finishedPath = path;
                if (_captureId == closingId)
                {
                    _live = null;
                    _clock = null;
                    _lastWriteStamp = 0;
                }
            }

            Plugin.Logger?.LogInfo(
                $"perf: stopped ({reason}) after {report.Recording.Frames} frames / {report.ElapsedSeconds:0.##} s; "
                + $"close {PerfStats.Round(closeMs, 3)} ms; report writing on background thread ({path})"
                + (report.EndedDuringSilence
                    ? $"; ended during silence {PerfStats.Round(report.SampleSilenceMsAtClose, 1)} ms"
                    : ""));

            return PerfFinalize.Result(PerfFinalize.Read());
        }
        finally
        {
            lock (Gate)
            {
                if (_captureId == closingId)
                    _close = null;
            }
        }
    }

    // ---- the Dev-side watchdog ---------------------------------------------------------------

    private static void StartWatchdog()
    {
        StopWatchdog();
        try
        {
            var timer = new System.Threading.Timer(_ => CloseIfDue(), null, WatchdogMs, WatchdogMs);
            lock (Gate) { _watchdog = timer; }
        }
        catch (Exception e)
        {
            // Not fatal: Run() also calls CloseIfDue, and Core's dispatch still closes a window in
            // a process that ticks. Say so, because the deadline is then best-effort.
            Plugin.Logger?.LogWarning($"perf watchdog timer: {e.Message}; the deadline is best-effort");
        }
    }

    private static void StopWatchdog()
    {
        System.Threading.Timer? timer;
        lock (Gate)
        {
            timer = _watchdog;
            _watchdog = null;
        }

        try { timer?.Dispose(); } catch { }
    }

    /// <summary>
    /// Notices that a running window must close — the deadline elapsed, or the Core generation the
    /// sink was installed into is gone — and asks for it. Runs on the watchdog thread and on every
    /// op entry; never throws.
    ///
    /// <para>It does <b>not</b> serialize anything itself (R29 finding 5). It latches the reason on
    /// <see cref="PerfCloseGate"/> and calls <see cref="PumpClose"/>, which performs the close only
    /// on the main thread — or, in a process where the recording has gone quiescent, here. From the
    /// main thread (the op entry, or <c>Sink.CoreTick</c>) the two steps run back to back and the
    /// behavior is unchanged.</para>
    /// </summary>
    internal static void CloseIfDue()
    {
        try
        {
            bool running;
            double elapsed;
            double seconds;
            int installed;
            Sink? sink;
            PerfCloseGate? close;
            lock (Gate)
            {
                running = _running;
                elapsed = _clock?.Elapsed.TotalSeconds ?? 0;
                seconds = _seconds;
                installed = _installedGeneration;
                sink = _sink;
                close = _close;
            }

            if (!running) return;

            bool swapped = PerfWindowPolicy.CoreSwapped(
                running,
                Progenitor.Dev.PerfProbe.CoreUninstalled,
                installed,
                Progenitor.Dev.PerfProbe.Generation,
                Progenitor.Dev.PerfProbe.IsInstalled(sink));

            var reason = PerfWindowPolicy.StopReasonFor(
                swapped, PerfWindowPolicy.DeadlineReached(elapsed, seconds));

            if (reason != null) close?.Request(reason);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"perf watchdog: {e.Message}");
            return;
        }

        PumpClose();
    }

    // Socket-thread safe: no Gate, no recording enumeration, close request, or Unity APIs.
    // A main-thread start/stop/report holding Gate cannot delay this read; it only takes
    // PerfProgress's short private lock and PerfFinalize's write-slot lock. Bypassing the
    // main-thread queue is not a deadline.
    internal static object LiveStatus() =>
        PerfFinalize.LiveView(
            PerfProgress.LiveStatus(Volatile.Read(ref _progress), Stopwatch.GetTimestamp()),
            PerfFinalize.Read());

    private static void PublishProgress(PerfProgress? progress) =>
        Volatile.Write(ref _progress, progress);

    private static object Status()
    {
        lock (Gate)
        {
            if (!_running)
            {
                var finished = PerfFinalize.Result(PerfFinalize.Read());
                finished["lastPath"] = _finishedPath;
                return finished;
            }

            var report = _live;
            double elapsed = _clock?.Elapsed.TotalSeconds ?? 0;
            return new Dictionary<string, object?>
            {
                ["running"] = true,
                ["reportState"] = PerfFinalize.StateNone,
                ["side"] = report?.Side,
                ["world"] = report?.World,
                ["elapsedSeconds"] = PerfStats.Round(elapsed, 2),
                ["remainingSeconds"] = PerfStats.Round(Math.Max(0, _seconds - elapsed), 2),
                ["frames"] = report?.Recording.Frames ?? 0,
                ["framesRecorded"] = report?.Recording.Recorded ?? 0,
                ["wrapped"] = report?.Recording.Wrapped ?? false,
                ["sources"] = report == null ? null : new List<string>(report.Recording.Sources.Names),
                ["installedGeneration"] = _installedGeneration,
                ["coreGeneration"] = Progenitor.Dev.PerfProbe.Generation,
                ["watchdog"] = _watchdog != null,
                // A close the watchdog asked for and the main thread has not run yet (R29 finding 5).
                ["closePending"] = _close?.PendingReason,
                ["captureId"] = _captureId,
                ["lastPath"] = _finishedPath,
            };
        }
    }

    /// <summary>Remove the sink from Core, stop the watchdog, and drop everything they held.</summary>
    private static void Uninstall()
    {
        PerfCaptureLifetime? lifetime;
        lock (Gate)
        {
            lifetime = _lifetime;
            _lifetime = null;
            _sink = null;
            _installedGeneration = 0;
        }
        if (lifetime != null) lifetime.Dispose();
        else StopWatchdog();
    }

    /// <summary>
    /// Stop this generation's timer and sink. The <see cref="PerfCaptureLifetime"/> holds this
    /// as a callback and drops it on dispose so no static keeps a delegate into old Core.
    /// </summary>
    private static void ReleaseGeneration(Sink sink)
    {
        StopWatchdog();
        // Only clear a slot that is still ours: after a Core swap the slot belongs to the new
        // generation (or to nobody), and clearing it blind would cancel a window someone else
        // started in the meantime.
        try
        {
            if (Progenitor.Dev.PerfProbe.IsInstalled(sink))
                Progenitor.Dev.PerfProbe.Install(null);
        }
        catch (Exception e) { Plugin.Logger?.LogWarning($"perf uninstall: {e.Message}"); }
        try { sink.Release(); }
        catch { }
    }

    /// <summary>
    /// Unity's GC configuration, read once at start: whether the IL2CPP collector is incremental,
    /// its time slice, and the mode. Each read is an ICall delegate (no boxing). Never throws.
    /// </summary>
    private static void ReadGcConfig(PerfReport report)
    {
        try { report.Il2CppIncremental = UnityEngine.Scripting.GarbageCollector.isIncremental; } catch { }
        try { report.Il2CppTimeSliceNs = UnityEngine.Scripting.GarbageCollector.incrementalTimeSliceNanoseconds; } catch { }
        try { report.Il2CppGcMode = UnityEngine.Scripting.GarbageCollector.GCMode.ToString(); } catch { }
    }

    private static double ReadDouble(JsonElement args, string name, double fallback)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var el)) return fallback;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out var d)) return d;
        if (el.ValueKind == JsonValueKind.String
            && double.TryParse(el.GetString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out d)) return d;
        return fallback;
    }

    // ---- the sink ----------------------------------------------------------------------------

    /// <summary>
    /// Core's view of the running window. Every method runs on the game main thread inside Core's
    /// dispatch and must be allocation-free and non-throwing; the one dictionary that can grow is
    /// bounded and reset.
    ///
    /// <para><b>Every write takes <see cref="PerfCloseGate.RecordGate"/></b> (R29 finding 5). It is
    /// uncontended — the main thread is the only writer — so the cost is one <c>Monitor</c>
    /// acquire per dispatch and nothing is allocated. Its only job is to make the close path's
    /// <see cref="PerfCloseGate.Freeze"/> mutually exclusive with these writes. Serialization and
    /// the file write run on <see cref="PerfFinalize"/>'s writer thread, so this lock is never
    /// held for them.</para>
    /// </summary>
    private sealed class Sink : Progenitor.Dev.IPerfSink
    {
        /// <summary>Distinct delegate identities kept before the classification cache is reset.</summary>
        private const int MaxHandlerCache = 256;

        private readonly PerfCloseGate _close;
        private readonly PerfRecording _recording;
        private readonly float[] _row = new float[PerfSourceTable.MaxSources];

        /// <summary>
        /// Keyed by <see cref="MethodInfo"/>, not by delegate reference. R28 finding 28.19: every
        /// <c>Plugin.Frame +=</c> / <c>-=</c> produces a fresh multicast delegate and a fresh set of
        /// delegate objects out of <c>GetInvocationList()</c>, and Core subscribes and unsubscribes
        /// from a dozen transient sites (live refresh, transfers, beams, units, hosted). Keying on
        /// the delegate missed the cache on every one of those frames and forced N reflective
        /// re-classifications — so the profiler allocated on exactly the frames it was measuring,
        /// and those allocations landed in <c>allocBytes</c>, its headline number. A MethodInfo is
        /// stable across re-subscription.
        /// </summary>
        private readonly Dictionary<MethodInfo, int> _handlerSource = new();
        private readonly Dictionary<string, int> _hookSource = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _eventSource = new(StringComparer.Ordinal);
        private readonly Dictionary<Type, string> _eventNames = new();
        private readonly int _mainThreadId = Environment.CurrentManagedThreadId;

        private readonly ICollection? _queue;
        private readonly ICollection? _delayed;
        private readonly bool _perThreadAlloc;

        private long _lastStart;
        private long _lastAlloc;
        private bool _primed;

        // ---- IL2CPP heap attribution (BeginCallback pushes the used size, the end pops it) ----
        private const int MaxNesting = 32;
        private readonly long[] _beginUsed = new long[MaxNesting];
        private int _depth;
        private readonly long[] _il2cppRow = new long[PerfSourceTable.MaxSources];
        private readonly bool _il2cppOk;
        private readonly bool _il2cppGcOk;
        private int _lastIl2CppGc;
        private int _lastGen0;
        private int _lastGen1;
        private int _lastGen2;

        // ---- patch bodies and seams, drained from Core once per frame ----
        private readonly long[] _patchTicks = new long[Progenitor.Dev.PerfPatchTimer.Count];
        private readonly int[] _patchCalls = new int[Progenitor.Dev.PerfPatchTimer.Count];
        private readonly double[] _patchMs = new double[Progenitor.Dev.PerfPatchTimer.Count];
        private readonly long[] _seamFirst = new long[Progenitor.Dev.PerfSeams.Count];
        private readonly long[] _seamLast = new long[Progenitor.Dev.PerfSeams.Count];
        private readonly int[] _seamCount = new int[Progenitor.Dev.PerfSeams.Count];
        private readonly double[] _seamOffsetMs = new double[Progenitor.Dev.PerfSeams.Count];
        private long _prevCoreEnd;
        private readonly List<IDisposable> _seamHooks = new();

        /// <summary>Plugin id the seam hooks register under; the sink does not time them as a source.</summary>
        internal const string SeamPluginId = "progenitor.perf.seams";

        private readonly PerfProgress _progress;
        private readonly long _captureId;

        public Sink(PerfReport report, PerfCloseGate close, PerfProgress progress, long captureId)
        {
            _progress = progress;
            _captureId = captureId;
            _close = close;
            _recording = report.Recording;
            _perThreadAlloc = TryPerThreadAllocation(out _);
            ResolveQueues(out _queue, out _delayed, out var source);
            QueueSource = source;
            _il2cppOk = Il2CppUsed() >= 0;
            _lastIl2CppGc = Il2CppGcCount();
            _il2cppGcOk = _lastIl2CppGc >= 0;
            _lastGen0 = SafeClrCount(0);
            _lastGen1 = SafeClrCount(1);
            _lastGen2 = SafeClrCount(2);
        }

        public string AllocApi => _perThreadAlloc ? PerfAllocApi.PerThread : PerfAllocApi.TotalMemory;
        public string QueueSource { get; }
        public string Il2CppApi => _il2cppOk && _il2cppGcOk ? PerfIl2CppApi.UsedSizeAndCount
            : _il2cppOk ? "IL2CPP.il2cpp_gc_get_used_size (no collection count)"
            : PerfIl2CppApi.Unavailable;

        /// <summary>
        /// Registers the seam stamps on the game's group updates for this window: before/after the
        /// side's bootstrap system (where Bootstrap's frame tick usually runs), the simulation group
        /// and the presentation group. Each callback is one <c>PerfSeams.Stamp</c>. Returns a note
        /// naming what was hooked. Disposed in <see cref="Release"/>. Main thread (start).
        /// </summary>
        public string HookSeams()
        {
            var hooked = new List<string>();
            var options = new Progenitor.Hooks.HookOptions { OnlyWhenSystemRuns = false, PluginId = SeamPluginId };
            Type bootstrap = Progenitor.Plugin.IsServerProcess
                ? typeof(ProjectM.ServerBootstrapSystem)
                : typeof(ProjectM.ClientBootstrapSystem);
            TryHook(bootstrap, before: true, Progenitor.Dev.PerfSeams.BootstrapBefore, options, hooked);
            TryHook(bootstrap, before: false, Progenitor.Dev.PerfSeams.BootstrapAfter, options, hooked);
            TryHook(typeof(Unity.Entities.SimulationSystemGroup), before: true, Progenitor.Dev.PerfSeams.SimulationBefore, options, hooked);
            TryHook(typeof(Unity.Entities.SimulationSystemGroup), before: false, Progenitor.Dev.PerfSeams.SimulationAfter, options, hooked);
            TryHook(typeof(Unity.Entities.PresentationSystemGroup), before: true, Progenitor.Dev.PerfSeams.PresentationBefore, options, hooked);
            TryHook(typeof(Unity.Entities.PresentationSystemGroup), before: false, Progenitor.Dev.PerfSeams.PresentationAfter, options, hooked);
            hooked.Add("lateFrame (Core)");
            hooked.Add("bootstrapTick.drain (posted each frame)");
            return string.Join(", ", hooked);
        }

        private void TryHook(Type system, bool before, int seam, Progenitor.Hooks.HookOptions options, List<string> hooked)
        {
            try
            {
                Action<Progenitor.Hooks.SystemHookContext> stamp = _ => Progenitor.Dev.PerfSeams.Stamp(seam);
                var handle = before
                    ? Progenitor.Hooks.SystemHooks.Before(system, stamp, options)
                    : Progenitor.Hooks.SystemHooks.After(system, stamp, options);
                lock (_close.RecordGate) _seamHooks.Add(handle);
                hooked.Add(Progenitor.Dev.PerfSeams.Names[seam]);
            }
            catch (Exception e)
            {
                hooked.Add(Progenitor.Dev.PerfSeams.Names[seam] + " (failed: " + e.Message + ")");
            }
        }

        /// <summary>
        /// Begin of a timed callback: remember the IL2CPP used size. Pairs with <see cref="EndCallback"/>
        /// in FrameHandler / SystemHook / EventHandler; pairs nest.
        /// </summary>
        public void BeginCallback()
        {
            if (Environment.CurrentManagedThreadId != _mainThreadId) return;
            if (_depth < MaxNesting) _beginUsed[_depth] = _il2cppOk ? Il2CppUsed() : -1;
            _depth++;
        }

        /// <summary>IL2CPP bytes the callback that just ended grew the used size by (0 when unknown or shrunk).</summary>
        private long EndCallback()
        {
            if (_depth <= 0) return 0;
            _depth--;
            if (_depth >= MaxNesting || !_il2cppOk) return 0;
            long before = _beginUsed[_depth];
            if (before < 0) return 0;
            long delta = Il2CppUsed() - before;
            return delta > 0 ? delta : 0;
        }

        /// <summary>
        /// Drop everything this sink holds; called once the slot is cleared. Under the record lock:
        /// the classification caches are written by the dispatch methods below, and Release can run
        /// from a quiescent-window close on the watchdog thread.
        /// </summary>
        public void Release()
        {
            IDisposable[] hooks;
            lock (_close.RecordGate)
            {
                _handlerSource.Clear();
                _hookSource.Clear();
                _eventSource.Clear();
                _eventNames.Clear();
                hooks = _seamHooks.ToArray();
                _seamHooks.Clear();
            }
            foreach (var hook in hooks)
            {
                try { hook.Dispose(); }
                catch { /* the Core generation that owned it may be gone */ }
            }
        }

        public void FrameHandler(Delegate handler, long ticks)
        {
            if (Environment.CurrentManagedThreadId != _mainThreadId) return;
            long bytes = EndCallback();
            if (handler == null) return;
            lock (_close.RecordGate)
            {
                int source = SourceFor(handler);
                _row[source] += (float)(ticks * MsPerTick);
                _il2cppRow[source] += bytes;
                MarkWrite();
            }
        }

        public void SystemHook(string pluginId, string systemName, bool before, long ticks)
        {
            if (Environment.CurrentManagedThreadId != _mainThreadId) return;
            long bytes = EndCallback();
            if (string.Equals(pluginId, SeamPluginId, StringComparison.Ordinal)) return;
            double ms = ticks * MsPerTick;
            lock (_close.RecordGate)
            {
                int source = HookSourceFor(pluginId);
                _row[source] += (float)ms;
                _il2cppRow[source] += bytes;
                _recording.AddHook(pluginId ?? "", systemName ?? "", before, ms);
                MarkWrite();
            }
        }

        public void EventHandler(string pluginId, Type eventType, long ticks)
        {
            if (Environment.CurrentManagedThreadId != _mainThreadId) return;
            long bytes = EndCallback();
            double ms = ticks * MsPerTick;
            lock (_close.RecordGate)
            {
                int source = EventSourceFor(pluginId);
                _row[source] += (float)ms;
                _il2cppRow[source] += bytes;
                _recording.AddEvent(pluginId ?? "", EventName(eventType), ms);
                MarkWrite();
            }
        }

        /// <summary>
        /// Stamps "the main thread wrote just now", which is how the watchdog tells a ticking
        /// process (ask, do not close) from a quiescent one (close it there, nothing is writing).
        /// </summary>
        private static void MarkWrite() =>
            System.Threading.Interlocked.Exchange(ref _lastWriteStamp, Stopwatch.GetTimestamp());

        /// <summary>
        /// End of Core's frame dispatch: close the row. The frame delta is the gap between this
        /// tick's start and the previous one — Core ticks exactly once per Unity frame
        /// (<c>Plugin.FrameTick</c> de-dupes on <c>Time.frameCount</c>), so that gap is the frame.
        /// The very first tick only primes the baselines; it has no delta to report.
        /// </summary>
        public void CoreTick(long ticks, long startTimestamp)
        {
            if (Environment.CurrentManagedThreadId != _mainThreadId) { Array.Clear(_row, 0, _row.Length); return; }

            // Every Begin from this frame's dispatch has ended by now; a stray depth (a sink call
            // that threw) must not misalign the next frame's pairs.
            _depth = 0;
            PostDrainStamp();
            long allocNow = ReadAllocated();
            long used = _il2cppOk ? Il2CppUsed() : -1;
            long heap = _il2cppOk ? Il2CppHeap() : -1;
            int il2cppGc = _il2cppGcOk ? Il2CppGcCount() : -1;
            int gen0 = SafeClrCount(0), gen1 = SafeClrCount(1), gen2 = SafeClrCount(2);
            Progenitor.Dev.PerfPatchTimer.Drain(_patchTicks, _patchCalls);
            Progenitor.Dev.PerfSeams.Drain(_seamFirst, _seamLast, _seamCount);
            long coreEnd = startTimestamp + ticks;

            if (!_primed)
            {
                _primed = true;
                _lastStart = startTimestamp;
                _lastAlloc = allocNow;
                _prevCoreEnd = coreEnd;
                _lastIl2CppGc = il2cppGc;
                _lastGen0 = gen0; _lastGen1 = gen1; _lastGen2 = gen2;
                Array.Clear(_row, 0, _row.Length);
                Array.Clear(_il2cppRow, 0, _il2cppRow.Length);
                return;
            }

            for (int i = 0; i < _patchMs.Length; i++) _patchMs[i] = _patchTicks[i] * MsPerTick;
            double windowMs = (startTimestamp - _prevCoreEnd) * MsPerTick;
            for (int s = 0; s < _seamOffsetMs.Length; s++)
            {
                if (_seamCount[s] == 0) { _seamOffsetMs[s] = double.NaN; continue; }
                // Before seams: the first time the frame reached them; After seams: the last.
                long at = IsAfterSeam(s) ? _seamLast[s] : _seamFirst[s];
                _seamOffsetMs[s] = (at - _prevCoreEnd) * MsPerTick;
            }
            var extra = new PerfFrameExtra
            {
                Il2CppUsedBytes = used,
                Il2CppHeapBytes = heap,
                Il2CppCollections = il2cppGc >= 0 && _lastIl2CppGc >= 0 ? Math.Max(0, il2cppGc - _lastIl2CppGc) : 0,
                Gen0 = Math.Max(0, gen0 - _lastGen0),
                Gen1 = Math.Max(0, gen1 - _lastGen1),
                Gen2 = Math.Max(0, gen2 - _lastGen2),
                Il2CppSourceBytes = _il2cppRow,
                PatchMs = _patchMs,
                PatchCalls = _patchCalls,
                SeamOffsetMs = _seamOffsetMs,
                WindowMs = windowMs,
            };
            _lastIl2CppGc = il2cppGc;
            _lastGen0 = gen0; _lastGen1 = gen1; _lastGen2 = gen2;
            _prevCoreEnd = coreEnd;

            double frameMs = (startTimestamp - _lastStart) * MsPerTick;
            _lastStart = startTimestamp;
            long allocDelta = allocNow - _lastAlloc;
            _lastAlloc = allocNow;
            if (allocDelta < 0) allocDelta = 0;   // GetTotalMemory can fall after a collection

            lock (_close.RecordGate)
            {
                _recording.AddFrame(
                    atSeconds: Elapsed(),
                    frameMs: frameMs,
                    coreTickMs: ticks * MsPerTick,
                    perSource: _row,
                    queueDepth: Depth(_queue),
                    delayedDepth: Depth(_delayed),
                    allocBytes: allocDelta,
                    heapBytes: SafeTotalMemory(),
                    extra: in extra);

                Array.Clear(_row, 0, _row.Length);
                Array.Clear(_il2cppRow, 0, _il2cppRow.Length);
                MarkWrite();
                _progress.Record(_captureId, Stopwatch.GetTimestamp(), frameMs, ticks * MsPerTick);
            }

            // This is the main thread, so it is also where a close the watchdog asked for is
            // actually performed (R29 finding 5) — and where the deadline is noticed one frame
            // earlier than the timer would. The decision itself lives in PerfWindowPolicy so both
            // paths agree (R28 finding 17). Outside the record lock: CloseIfDue takes it again to
            // freeze the ring. Summary, JSON and the file write then run on PerfFinalize's writer.
            if (PerfWindowPolicy.DeadlineReached(Elapsed(), _seconds) || _close.PendingReason != null)
                CloseIfDue();
        }

        private static double Elapsed() => _clock?.Elapsed.TotalSeconds ?? 0;

        // ---- classification ------------------------------------------------------------------

        private int SourceFor(Delegate handler)
        {
            MethodInfo? method;
            try { method = handler.Method; }
            catch { method = null; }
            if (method == null) return _recording.Sources.GetOrAdd(PerfSourceTable.UnknownName);

            if (_handlerSource.TryGetValue(method, out var index)) return index;
            if (_handlerSource.Count >= MaxHandlerCache) _handlerSource.Clear();

            string name;
            try
            {
                var declaring = method.DeclaringType;
                name = PerfSourceTable.ClassifyFrameHandler(
                    declaring?.FullName,
                    declaring?.Assembly.GetName().Name,
                    method.Name);
            }
            catch
            {
                name = PerfSourceTable.UnknownName;
            }

            index = _recording.Sources.GetOrAdd(name);
            _handlerSource[method] = index;
            return index;
        }

        private int HookSourceFor(string? pluginId)
        {
            var key = pluginId ?? "";
            if (_hookSource.TryGetValue(key, out var index)) return index;
            index = _recording.Sources.GetOrAdd(PerfSourceTable.HookSource(key));
            _hookSource[key] = index;
            return index;
        }

        private int EventSourceFor(string? pluginId)
        {
            var key = pluginId ?? "";
            if (_eventSource.TryGetValue(key, out var index)) return index;
            index = _recording.Sources.GetOrAdd(PerfSourceTable.EventSource(key));
            _eventSource[key] = index;
            return index;
        }

        private string EventName(Type? type)
        {
            if (type == null) return "?";
            if (_eventNames.TryGetValue(type, out var name)) return name;
            name = type.Name;
            if (_eventNames.Count < PerfRecording.MaxDetailRows * 2) _eventNames[type] = name;
            return name;
        }

        // ---- counters ------------------------------------------------------------------------

        private long ReadAllocated() => _perThreadAlloc ? SafeAllocatedPerThread() : SafeTotalMemory();

        /// <summary>Queue the next frame's drain stamp (see <c>PerfSeams.BootstrapDrain</c>).</summary>
        private static void PostDrainStamp()
        {
            try { Progenitor.Dev.MainThread.Post(Progenitor.Dev.PerfSeams.DrainStamp); }
            catch { /* the seam is then simply absent */ }
        }

        private static bool IsAfterSeam(int seam) =>
            seam == Progenitor.Dev.PerfSeams.BootstrapAfter
            || seam == Progenitor.Dev.PerfSeams.SimulationAfter
            || seam == Progenitor.Dev.PerfSeams.PresentationAfter
            || seam == Progenitor.Dev.PerfSeams.LateFrame;

        /// <summary>
        /// <c>il2cpp_gc_get_used_size</c>: a direct P/Invoke into GameAssembly (no runtime_invoke, so
        /// no boxed return on the IL2CPP heap). -1 when the export is unavailable.
        /// </summary>
        private static long Il2CppUsed()
        {
            try { return Il2CppInterop.Runtime.IL2CPP.il2cpp_gc_get_used_size(); }
            catch { return -1; }
        }

        private static long Il2CppHeap()
        {
            try { return Il2CppInterop.Runtime.IL2CPP.il2cpp_gc_get_heap_size(); }
            catch { return -1; }
        }

        /// <summary>
        /// The IL2CPP collection count. The interop wrapper goes through <c>il2cpp_runtime_invoke</c>,
        /// which boxes the int return: one ~32-byte IL2CPP allocation per frame, the probe's own
        /// overhead (the report's il2cpp note says so). -1 when unavailable.
        /// </summary>
        private static int Il2CppGcCount()
        {
            try { return Il2CppSystem.GC.CollectionCount(0); }
            catch { return -1; }
        }

        private static int SafeClrCount(int generation)
        {
            try { return GC.CollectionCount(generation); }
            catch { return 0; }
        }

        private static long SafeAllocatedPerThread()
        {
            try { return GC.GetAllocatedBytesForCurrentThread(); }
            catch { return 0; }
        }

        private static long SafeTotalMemory()
        {
            try { return GC.GetTotalMemory(false); }
            catch { return 0; }
        }

        /// <summary>
        /// <c>GC.GetAllocatedBytesForCurrentThread</c> is the counter we want (main-thread managed
        /// allocation, exact, no collection triggered). Probe it once rather than assume it: this
        /// assembly also runs under whatever runtime the game ships, and a throwing counter must
        /// degrade to the <c>GetTotalMemory</c> delta rather than break the op.
        /// </summary>
        private static bool TryPerThreadAllocation(out long value)
        {
            try
            {
                value = GC.GetAllocatedBytesForCurrentThread();
                return true;
            }
            catch
            {
                value = 0;
                return false;
            }
        }

        private static int Depth(ICollection? collection)
        {
            if (collection == null) return -1;
            try { return collection.Count; }
            catch { return -1; }
        }

        /// <summary>
        /// The main-thread queue depth, read straight off Bootstrap's live queue objects
        /// (<c>Progenitor.Bootstrap.WorkQueue</c>). They are internal statics, so this is a one-time
        /// reflective bind to the collection objects themselves — after that each frame is one
        /// interface <c>Count</c> call, and nothing is written.
        ///
        /// <para><b>Bootstrap only.</b> The first version fell back to Core's
        /// <c>Progenitor.Dev.MainThread</c>, which means the sink held a <b>Core</b> static
        /// collection for the capture's life — the collectible-ALC pin this file's own doc comment
        /// is about, in the one file whose subject is avoiding them (R28 finding 14). Bootstrap is
        /// not collectible; Core is. A process without Bootstrap reports <c>queueSource:
        /// unavailable</c> and depth <c>-1</c>, which is the honest answer.</para>
        /// </summary>
        private static void ResolveQueues(out ICollection? queue, out ICollection? delayed, out string source)
        {
            queue = null;
            delayed = null;
            source = "unavailable";

            if (TryBind("Progenitor.Bootstrap", "Progenitor.Bootstrap.WorkQueue", out queue, out delayed))
                source = "Progenitor.Bootstrap.WorkQueue";
        }

        private static bool TryBind(string assemblyName, string typeName, out ICollection? queue, out ICollection? delayed)
        {
            queue = null;
            delayed = null;
            try
            {
                Type? type = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (!string.Equals(asm.GetName().Name, assemblyName, StringComparison.OrdinalIgnoreCase)) continue;
                    var candidate = asm.GetType(typeName, throwOnError: false);
                    if (candidate == null) continue;
                    // Several collectible generations can share a simple name, so "first match in
                    // load order" could bind a dead one (R28 finding 14). Take the LAST match: the
                    // most recently loaded assembly is the live generation.
                    type = candidate;
                }
                if (type == null) return false;

                const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;
                queue = type.GetField("Queue", flags)?.GetValue(null) as ICollection;
                delayed = type.GetField("Delayed", flags)?.GetValue(null) as ICollection;
                return queue != null;
            }
            catch
            {
                return false;
            }
        }
    }
}
