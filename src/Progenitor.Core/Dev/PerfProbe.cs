using System;

namespace Progenitor.Dev;

/// <summary>
/// The one timing seam the Dev <c>perf</c> op needs from Core, and the only Core change job FYP
/// made. It is a single nullable slot: when nothing is installed (the normal case, always in a
/// shipped build) every dispatch point pays one static field read and a branch, and Core's frame
/// dispatch walks the cached invocation list so a hitch can name the slowest subscriber.
///
/// <para><b>Why Core has to own it.</b> Progenitor's per-frame work is dispatched from three
/// funnels that live in Core and that nothing outside Core can decorate without breaking
/// something else:</para>
/// <list type="number">
/// <item><see cref="Plugin.Frame"/> — an <c>event Action</c>. A Dev-side wrapper would have to
/// unsubscribe each handler and re-subscribe its own, which silently breaks every
/// <c>Plugin.Frame -= OnFrame</c> in Core and makes <c>Plugin.RemoveFrameHandlers</c> unable to
/// drop the handler on unload, i.e. a guaranteed collectible-ALC pin. Not acceptable in this
/// codebase (see the hot-reload rules in docs/dev-loop.md).</item>
/// <item><c>Hooks.SystemHooks.Invoke</c> — the single funnel every plugin system hook goes
/// through, and the only place that still knows which plugin id owns the callback.</item>
/// <item><c>Events.EventBus.Publish</c> — same, for event handlers.</item>
/// </list>
///
/// <para><b>Ownership.</b> The sink object lives in the Dev plugin's collectible context, so an
/// installed sink pins that context. <c>PerfOps.Shutdown</c> (Dev <c>Unload</c>, which the Core
/// reload cascade runs before Uninstall) and every <c>stop</c> call <see cref="Install"/> with
/// null. Core's <c>Plugin.Uninstall</c> clears it as well, and <see cref="Install"/> drops the
/// cached invocation list so no plugin delegate is held here after a capture ends.</para>
///
/// <para><b>Detecting a swap.</b> The sink object belongs to Dev and the slot belongs to Core, so a
/// Core swap detaches the two: this generation's <see cref="Install"/>(null) runs from
/// <c>Plugin.Uninstall</c> via <see cref="NoteCoreUninstall"/>, and the new generation starts with
/// an empty slot it will never fill on its own. Dev cannot see that from the frame dispatch —
/// the swap is exactly what stops the frame dispatch reaching it (R28 findings 14 and 17). So the
/// slot carries two facts Dev polls from its own timer instead: <see cref="Generation"/>, which
/// moves on every install, and <see cref="CoreUninstalled"/>, which latches when this generation is
/// torn down. A capture whose sink is no longer the installed one stops itself with
/// <c>stopReason=core-swap</c> and writes the partial report.</para>
///
/// <para>Nothing here throws into the game: every call site wraps the sink call in a
/// <c>try</c>/<c>catch</c>, because a profiler must never be able to break a frame.</para>
/// </summary>
public static class PerfProbe
{
    private static volatile IPerfSink? _sink;
    private static volatile int _generation;
    private static volatile bool _coreUninstalled;
    private static Delegate? _cachedKey;
    private static Delegate[] _cachedList = Array.Empty<Delegate>();

    /// <summary>The installed sink, or null. Read once per dispatch; never cached by callers.</summary>
    public static IPerfSink? Sink => _sink;

    /// <summary>True while a <c>perf</c> capture is running in this process.</summary>
    public static bool Active => _sink != null;

    /// <summary>
    /// Bumped by every <see cref="Install"/>. Dev records the value it installed at and treats any
    /// later change as "something else owns this slot now".
    /// </summary>
    public static int Generation => _generation;

    /// <summary>
    /// True once this Core generation has been uninstalled. It never goes back to false for this
    /// generation, and a fresh generation's static starts false, so it cannot be mistaken for a
    /// plain <c>stop</c>.
    /// </summary>
    public static bool CoreUninstalled => _coreUninstalled;

    /// <summary>True when <paramref name="sink"/> is still the installed one.</summary>
    public static bool IsInstalled(IPerfSink? sink) => sink != null && ReferenceEquals(_sink, sink);

    /// <summary>
    /// Install (or, with null, remove) the sink. Removing also drops the cached
    /// <c>Plugin.Frame</c> invocation list, which would otherwise keep plugin delegates reachable
    /// from a Core static after the capture finished.
    /// </summary>
    public static void Install(IPerfSink? sink)
    {
        _sink = sink;
        if (sink != null) _coreUninstalled = false;
        _generation++;
        ResetCache();
        PerfPatchTimer.Reset(sink != null);
        PerfSeams.Reset(sink != null);
    }

    /// <summary>
    /// Core's <c>Plugin.Uninstall</c>: this generation is going away. Clears the slot (a leftover
    /// Dev sink would pin the Dev plugin's collectible context) and latches the reason, so a Dev
    /// capture that outlives the swap can say <c>core-swap</c> rather than reporting a window that
    /// silently stopped receiving frames.
    /// </summary>
    public static void NoteCoreUninstall()
    {
        Install(null);
        _coreUninstalled = true;
    }

    internal static void ResetCache()
    {
        _cachedKey = null;
        _cachedList = Array.Empty<Delegate>();
    }

    /// <summary>
    /// The invocation list of a multicast delegate, cached against the delegate reference.
    /// Multicast delegates are immutable, so a different reference is the only way the subscriber
    /// set can have changed — which is what keeps a profiled frame allocation-free in steady
    /// state, since <c>GetInvocationList</c> allocates a fresh array (and fresh delegate objects)
    /// every call.
    /// </summary>
    internal static Delegate[] InvocationList(Delegate? multicast)
    {
        if (multicast == null)
        {
            ResetCache();
            return Array.Empty<Delegate>();
        }
        if (!ReferenceEquals(multicast, _cachedKey))
        {
            _cachedList = multicast.GetInvocationList();
            _cachedKey = multicast;
        }
        return _cachedList;
    }
}

/// <summary>
/// What Core reports to the <c>perf</c> op. Every method is called on the game main thread, once
/// per dispatched callback, with <see cref="System.Diagnostics.Stopwatch"/> ticks. Implementations
/// must not allocate and must not throw; the arguments are references Core already had in hand
/// (no boxing, no strings built per frame).
/// </summary>
public interface IPerfSink
{
    /// <summary>
    /// A timed callback is about to run. Each call pairs with the next <see cref="FrameHandler"/>,
    /// <see cref="SystemHook"/> or <see cref="EventHandler"/> on the same thread, and pairs nest
    /// (an event published from a frame handler begins and ends inside it). The Dev sink reads
    /// the IL2CPP heap's used size here and again at the end, which attributes IL2CPP bytes to a
    /// source. The default does nothing, so a sink built against an older Core still loads.
    /// </summary>
    void BeginCallback() { }

    /// <summary>One <see cref="Plugin.Frame"/> subscriber finished. The delegate is the identity
    /// the sink classifies into a source (its declaring type and assembly).</summary>
    void FrameHandler(Delegate handler, long ticks);

    /// <summary>Core's whole frame dispatch finished. <paramref name="startTimestamp"/> is the
    /// <c>Stopwatch.GetTimestamp()</c> taken before the first handler, so the sink can measure the
    /// wall gap between frames without a clock of its own.</summary>
    void CoreTick(long ticks, long startTimestamp);

    /// <summary>One plugin system hook (Before or After) finished.</summary>
    void SystemHook(string pluginId, string systemName, bool before, long ticks);

    /// <summary>One event-bus handler finished.</summary>
    void EventHandler(string pluginId, Type eventType, long ticks);
}

/// <summary>
/// Timing for the bodies of Progenitor's Harmony prefixes / postfixes and Bootstrap slot callbacks on
/// game methods. Those run inside the game's own update (every system update, every late frame,
/// every packet), outside <see cref="Plugin.Frame"/>, so their cost is invisible to the frame
/// dispatch timing and shows up as "outside" time in a <c>perf</c> report.
///
/// <para><b>Cost.</b> With no capture running, <see cref="Start"/> is one static bool read and returns
/// 0, and <see cref="End"/> returns on that 0. While a capture runs it is two
/// <c>Stopwatch.GetTimestamp()</c> calls, a thread-id compare and two array adds per body; nothing is
/// allocated. Only the main thread is counted (the arrays are not synchronized); the Dev sink copies
/// and clears them once per frame with <see cref="Drain"/>. The timed span is the managed body only:
/// the Il2CppInterop trampoline that marshals the call into managed code (and builds the
/// <c>__instance</c> wrapper) is outside it, so <see cref="Calls"/> is reported beside the time.</para>
/// </summary>
public static class PerfPatchTimer
{
    /// <summary><c>SystemBase.Update</c> prefix body (every managed system, every world, every frame).</summary>
    public const int SystemPrefixManaged = 0;
    /// <summary><c>SystemBase.Update</c> postfix body.</summary>
    public const int SystemPostfixManaged = 1;
    /// <summary><c>WorldUnmanagedImpl.UnmanagedUpdate</c> prefix body (every ISystem update).</summary>
    public const int SystemPrefixUnmanaged = 2;
    /// <summary><c>WorldUnmanagedImpl.UnmanagedUpdate</c> postfix body.</summary>
    public const int SystemPostfixUnmanaged = 3;
    /// <summary><c>ChatMessageSystem.OnUpdate</c> prefix (server).</summary>
    public const int ChatPrefix = 4;
    /// <summary>Bootstrap late-frame slot (camera pre-cull or FrameTicker.LateUpdate) callbacks.</summary>
    public const int LateFrame = 5;
    /// <summary>Network receive slot dispatchers (client and server).</summary>
    public const int Receive = 6;
    /// <summary>UI input mask detour (<c>BlockInputState</c> apply).</summary>
    public const int UiMask = 7;
    /// <summary>Every other patch body that is timed.</summary>
    public const int Other = 8;

    public const int Count = 9;

    /// <summary>Report names, indexed by the constants above.</summary>
    public static readonly string[] Names =
    {
        "SystemBase.Update.prefix",
        "SystemBase.Update.postfix",
        "UnmanagedUpdate.prefix",
        "UnmanagedUpdate.postfix",
        "ChatMessageSystem.OnUpdate.prefix",
        "LateFrame",
        "Receive",
        "UiMask",
        "other",
    };

    private static volatile bool _on;
    private static int _mainThreadId;
    private static readonly long[] _ticks = new long[Count];
    private static readonly int[] _calls = new int[Count];

    /// <summary>True while a capture is timing patch bodies.</summary>
    public static bool On => _on;

    /// <summary>Called by <see cref="PerfProbe.Install"/> on the main thread.</summary>
    internal static void Reset(bool on)
    {
        Array.Clear(_ticks, 0, Count);
        Array.Clear(_calls, 0, Count);
        _mainThreadId = Environment.CurrentManagedThreadId;
        _on = on;
    }

    /// <summary>A timestamp to pass to <see cref="End"/>, or 0 when no capture runs.</summary>
    public static long Start() => _on ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;

    /// <summary>Adds the body's elapsed ticks to <paramref name="id"/>. No-op for a 0 start.</summary>
    public static void End(int id, long start)
    {
        if (start == 0L) return;
        if (Environment.CurrentManagedThreadId != _mainThreadId) return;
        if ((uint)id >= Count) id = Other;
        _ticks[id] += System.Diagnostics.Stopwatch.GetTimestamp() - start;
        _calls[id]++;
    }

    /// <summary>
    /// Copies the counters accumulated since the last drain into the caller's arrays (length
    /// <see cref="Count"/>) and clears them. Main thread only. Returns the summed ticks.
    /// </summary>
    public static long Drain(long[] ticks, int[] calls)
    {
        long total = 0;
        for (int i = 0; i < Count; i++)
        {
            if (ticks != null && i < ticks.Length) ticks[i] = _ticks[i];
            if (calls != null && i < calls.Length) calls[i] = _calls[i];
            total += _ticks[i];
            _ticks[i] = 0;
            _calls[i] = 0;
        }
        return total;
    }

    /// <summary>Calls recorded since the last drain for <paramref name="id"/>.</summary>
    public static int Calls(int id) => (uint)id < Count ? _calls[id] : 0;
}

/// <summary>
/// Per-frame timestamps at Progenitor's seams in the Unity frame, so a <c>perf</c> report can say
/// which stretch of a slow frame held the time (inside the game's simulation group, inside
/// presentation, between late frame and the next frame, ...). Core stamps <see cref="LateFrame"/>
/// itself; the Dev <c>perf</c> op registers short-lived system hooks for the group seams while a
/// capture runs and stamps them through <see cref="Stamp"/>. The Dev sink reads the frame's stamps
/// once per frame with <see cref="Drain"/>.
///
/// <para>For each seam the first and the last stamp since the previous drain are kept, plus a
/// count (a group can update once per world). Cost while no capture runs: one static bool read per
/// <see cref="Stamp"/>. Main thread only; nothing allocates.</para>
/// </summary>
public static class PerfSeams
{
    /// <summary>Before <c>ClientBootstrapSystem</c> / <c>ServerBootstrapSystem</c> updates; Bootstrap's frame tick (queue drain, Core tick) usually runs in that system's postfix.</summary>
    public const int BootstrapBefore = 0;
    /// <summary>After the bootstrap system's update, including Bootstrap's postfix (drain + Core tick) when that is where the tick ran.</summary>
    public const int BootstrapAfter = 1;
    public const int SimulationBefore = 2;
    public const int SimulationAfter = 3;
    public const int PresentationBefore = 4;
    public const int PresentationAfter = 5;
    /// <summary>Bootstrap's late-frame slot (camera pre-cull or FrameTicker.LateUpdate), stamped by Core.</summary>
    public const int LateFrame = 6;
    /// <summary>
    /// Bootstrap's main-thread queue drain, the first step of its frame tick (then CoreWatch, the
    /// plugin reload scan, then Core's tick). Stamped by an action the Dev sink posts each frame.
    /// </summary>
    public const int BootstrapDrain = 7;

    public const int Count = 8;

    /// <summary>The action the Dev sink posts to the main-thread queue to stamp <see cref="BootstrapDrain"/>. Core-owned, so a queued copy pins no plugin.</summary>
    public static readonly Action DrainStamp = () => Stamp(BootstrapDrain);

    public static readonly string[] Names =
    {
        "bootstrapSystem.before",
        "bootstrapSystem.after",
        "simulation.before",
        "simulation.after",
        "presentation.before",
        "presentation.after",
        "lateFrame",
        "bootstrapTick.drain",
    };

    private static volatile bool _on;
    private static int _mainThreadId;
    private static readonly long[] _first = new long[Count];
    private static readonly long[] _last = new long[Count];
    private static readonly int[] _count = new int[Count];

    public static bool On => _on;

    internal static void Reset(bool on)
    {
        Array.Clear(_first, 0, Count);
        Array.Clear(_last, 0, Count);
        Array.Clear(_count, 0, Count);
        _mainThreadId = Environment.CurrentManagedThreadId;
        _on = on;
    }

    /// <summary>Records "the frame reached <paramref name="seam"/> now". No-op without a capture.</summary>
    public static void Stamp(int seam)
    {
        if (!_on) return;
        if ((uint)seam >= Count) return;
        if (Environment.CurrentManagedThreadId != _mainThreadId) return;
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_count[seam] == 0) _first[seam] = now;
        _last[seam] = now;
        _count[seam]++;
    }

    /// <summary>
    /// Copies the stamps since the last drain (0 where a seam did not fire) and clears them. Main
    /// thread only. Arrays are length <see cref="Count"/>.
    /// </summary>
    public static void Drain(long[] first, long[] last, int[] count)
    {
        for (int i = 0; i < Count; i++)
        {
            if (first != null && i < first.Length) first[i] = _first[i];
            if (last != null && i < last.Length) last[i] = _last[i];
            if (count != null && i < count.Length) count[i] = _count[i];
            _first[i] = 0;
            _last[i] = 0;
            _count[i] = 0;
        }
    }
}
