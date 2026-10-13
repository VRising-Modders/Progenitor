using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;

namespace Progenitor.Networking;

/// <summary>
/// Full blocking .NET collection immediately before the game starts a world load
/// (<c>GameHelper.LaunchGame</c>). Mitigation for the 2026-10-04 22:58 join hang: a CoreCLR GC
/// entered from Il2CppInterop's finalizer hook on IL2CPP's "GC Finalizer" thread was frozen when
/// Unity's asset unload stopped the IL2CPP world, and every liveness thread then waited forever
/// for it to finish. Collecting just before the load resets the allocation budget so a CoreCLR GC
/// is unlikely to be in flight when that unload stops the world. It does not remove the race.
///
/// <para>Seam. Core fills Bootstrap's process-lifetime <c>GameHelper.LaunchGame</c> plugin slot
/// (Bootstrap patches that original eagerly on every client), so the collection runs for every
/// launch: the game's server list, Direct Connect, Private Game / Host New, the bridge's
/// <c>connect</c> / <c>host</c> and JoinFlow's rejoin. That slot holds one dispatcher, so while
/// Core holds it a plugin bind of the same site (Dev's <c>host_capture</c>) is chained behind
/// Core's dispatcher by <see cref="HarmonySlotHandoff.TryBindPlugin(string, Delegate, string)"/>
/// instead of being refused. When the slot cannot be armed (an older Bootstrap without it, or
/// another owner bound first), Progenitor's own callers collect through
/// <see cref="BeforeOwnLaunch"/>; launches from the game's UI are then not covered.</para>
///
/// <para>Gated by <c>[Diagnostics] CollectBeforeWorldLoad</c> (default on) in
/// <c>progenitor.cfg</c>, read at every launch. Game-free; the slot binding and the
/// config bind are in <c>GcBeforeLaunchLive.cs</c>.</para>
/// </summary>
public static class GcBeforeLaunch
{
    public const string ConfigSection = "Diagnostics";
    public const string ConfigKey = "CollectBeforeWorldLoad";
    public const bool DefaultEnabled = true;
    public const string ConfigDescription =
        "Run a full blocking .NET garbage collection (and let pending .NET finalizers run, bounded) "
        + "immediately before the client starts a world load (GameHelper.LaunchGame). Mitigates a join "
        + "hang where a .NET GC was in flight when Unity's asset unload stopped the IL2CPP world. "
        + "Turn off if the collection causes a noticeable stutter when joining.";

    /// <summary>Upper bound on the main-thread wait for pending .NET finalizers.</summary>
    public const int FinalizerWaitMs = 2000;

    internal const string LogPrefix = "GcBeforeLaunch: ";

    /// <summary>Process-side probe of the .NET GC. Replaced by a fake in tests.</summary>
    internal interface IGcRuntime
    {
        int Gen2Count { get; }
        long HeapBytes { get; }
        void CollectFull();
        /// <summary>Wait for pending finalizers; false when the bound expired first.</summary>
        bool WaitForPendingFinalizers(int timeoutMs);
        long Timestamp { get; }
        double ElapsedMs(long start);
    }

    private sealed class ClrGcRuntime : IGcRuntime
    {
        public int Gen2Count => GC.CollectionCount(2);
        public long HeapBytes => GC.GetTotalMemory(false);
        public void CollectFull() => GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);

        /// <summary>
        /// Waits on a pool thread so a finalizer that blocks (for example one that waits on the
        /// main thread) costs at most <paramref name="timeoutMs"/>, never a hang. Progenitor
        /// declares no finalizers; Il2CppInterop's only free a GC handle.
        /// </summary>
        public bool WaitForPendingFinalizers(int timeoutMs) =>
            Task.Run(GC.WaitForPendingFinalizers).Wait(timeoutMs);

        public long Timestamp => Stopwatch.GetTimestamp();
        public double ElapsedMs(long start) =>
            (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
    }

    private static readonly IGcRuntime DefaultRuntime = new ClrGcRuntime();
    private static Func<bool>? _enabledReader;
    private static volatile bool _slotArmed;

#if PROGENITOR_TESTS
    private static IGcRuntime? _runtimeOverride;
    internal static IGcRuntime Runtime => _runtimeOverride ?? DefaultRuntime;
#else
    internal static IGcRuntime Runtime => DefaultRuntime;
#endif

    /// <summary>Log sink; null falls back to Core's logger.</summary>
    internal static Action<string>? LogSink { get; set; }

    /// <summary>True while Core's dispatcher is in Bootstrap's LaunchGame slot.</summary>
    public static bool SlotArmed => _slotArmed;

    internal static void SetSlotArmed(bool armed) => _slotArmed = armed;

    /// <summary>Live reader of the config entry. Null (not bound yet) is the default, on.</summary>
    internal static void SetEnabledReader(Func<bool>? reader) => _enabledReader = reader;

    internal static bool Enabled
    {
        get
        {
            var reader = _enabledReader;
            if (reader == null) return DefaultEnabled;
            try { return reader(); }
            catch { return DefaultEnabled; }
        }
    }

    /// <summary>The slot dispatcher collects whenever the gate is on.</summary>
    internal static bool ShouldCollectInSlot(bool enabled) => enabled;

    /// <summary>
    /// Progenitor's own callers collect only when the slot is not armed; with the slot armed
    /// the dispatcher already runs inside that same <c>LaunchGame</c> call.
    /// </summary>
    internal static bool ShouldCollectInOwnCaller(bool enabled, bool slotArmed) => enabled && !slotArmed;

    /// <summary>
    /// Call immediately before Progenitor's own <c>GameHelper.LaunchGame</c>. Collects only when
    /// the gate is on and the LaunchGame slot is not armed. Never throws.
    /// </summary>
    public static bool BeforeOwnLaunch(string caller)
    {
        try
        {
            bool enabled = Enabled;
            bool armed = SlotArmed;
            if (!ShouldCollectInOwnCaller(enabled, armed))
            {
                // With the slot armed the dispatcher logs the skip itself; one line per launch.
                if (!enabled && !armed) Log(SkippedLine(caller));
                return false;
            }
            return Collect(caller) != null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The slot dispatcher's half. Never throws.</summary>
    internal static void OnSlotLaunch()
    {
        try
        {
            if (!ShouldCollectInSlot(Enabled))
            {
                Log(SkippedLine("slot"));
                return;
            }
            Collect("slot");
        }
        catch
        {
            // Must not throw into LaunchGame.
        }
    }

    /// <summary>
    /// Collect, wait (bounded) for the finalizers that collection queued, collect what they
    /// released, then log one line. Returns the line, or null when the runtime threw.
    /// </summary>
    internal static string? Collect(string site)
    {
        var rt = Runtime;
        try
        {
            int gen2Before = rt.Gen2Count;
            long heapBefore = rt.HeapBytes;
            long start = rt.Timestamp;
            rt.CollectFull();
            bool finalizersDone = rt.WaitForPendingFinalizers(FinalizerWaitMs);
            rt.CollectFull();
            double ms = rt.ElapsedMs(start);
            int gen2After = rt.Gen2Count;
            long heapAfter = rt.HeapBytes;
            string line = FormatCollected(ms, gen2Before, gen2After, heapBefore, heapAfter);
            if (!finalizersDone)
                line += " [finalizer wait exceeded " + FinalizerWaitMs.ToString(CultureInfo.InvariantCulture) + " ms]";
            Log(line);
            return line;
        }
        catch (Exception e)
        {
            Log(LogPrefix + "collection before LaunchGame threw (" + site + "): " + e.Message);
            return null;
        }
    }

    internal static string FormatCollected(double ms, int gen2Before, int gen2After, long heapBefore, long heapAfter) =>
        LogPrefix + "collected before LaunchGame in "
        + Math.Round(ms).ToString("0", CultureInfo.InvariantCulture) + " ms (gen2 count "
        + gen2Before.ToString(CultureInfo.InvariantCulture) + "->" + gen2After.ToString(CultureInfo.InvariantCulture)
        + ", heap " + Mb(heapBefore) + "->" + Mb(heapAfter) + " MB)";

    internal static string SkippedLine(string site) =>
        LogPrefix + "skipped before LaunchGame (" + site + "): [" + ConfigSection + "] " + ConfigKey + "=false";

    internal static string ArmedLine =>
        LogPrefix + "armed on Bootstrap's GameHelper.LaunchGame slot; every world load collects first.";

    internal static string FallbackLine(string reason) =>
        LogPrefix + "LaunchGame slot not armed (" + reason + "); only Progenitor's own connect/host/rejoin collect.";

    private static string Mb(long bytes) =>
        (bytes / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture);

    private static void Log(string line)
    {
        var sink = LogSink;
        if (sink != null)
        {
            try { sink(line); } catch { /* a log sink must not break a launch */ }
            return;
        }
#if !PROGENITOR_TESTS
        try { Plugin.Logger?.LogInfo(line); } catch { /* same */ }
#endif
    }

    // ---- plugin chain on the LaunchGame slot ---------------------------------------------------

    private static readonly object ChainGate = new();
    private static Delegate? _chained;
    private static string? _chainedOwner;

    /// <summary>The plugin dispatcher chained behind Core's, or null.</summary>
    internal static Delegate? Chained
    {
        get { lock (ChainGate) return _chained; }
    }

    internal static string? ChainedOwner
    {
        get { lock (ChainGate) return _chainedOwner; }
    }

    /// <summary>
    /// Bind a plugin dispatcher behind Core's. Same ownership rule as Bootstrap's plugin slots:
    /// empty owner refused, a different owner refused while one is bound, the same owner rebinds.
    /// </summary>
    internal static bool TryChain(Delegate dispatcher, string? owner)
    {
        if (dispatcher == null) return false;
        if (string.IsNullOrEmpty(owner)) return false;
        lock (ChainGate)
        {
            if (!string.IsNullOrEmpty(_chainedOwner)
                && !string.Equals(_chainedOwner, owner, StringComparison.Ordinal))
            {
                Log(LogPrefix + "LaunchGame chain is bound by " + _chainedOwner + "; refused bind by " + owner + ".");
                return false;
            }
            _chained = dispatcher;
            _chainedOwner = owner;
            return true;
        }
    }

    /// <summary>Clear the chain. Empty owner force-clears; a different owner is refused.</summary>
    internal static bool ClearChain(string? owner)
    {
        lock (ChainGate)
        {
            if (_chained == null) return true;
            if (!string.IsNullOrEmpty(owner) && !string.IsNullOrEmpty(_chainedOwner)
                && !string.Equals(_chainedOwner, owner, StringComparison.Ordinal))
                return false;
            _chained = null;
            _chainedOwner = null;
            return true;
        }
    }

    /// <summary>Clear the chain when its owner is one of <paramref name="names"/> (ALC or assembly names).</summary>
    internal static bool ClearChainOwnedBy(IEnumerable<string?> names)
    {
        if (names == null) return false;
        lock (ChainGate)
        {
            if (_chained == null || string.IsNullOrEmpty(_chainedOwner)) return false;
            foreach (var name in names)
            {
                if (string.IsNullOrEmpty(name)) continue;
                if (!string.Equals(_chainedOwner, name, StringComparison.Ordinal)) continue;
                _chained = null;
                _chainedOwner = null;
                return true;
            }
            return false;
        }
    }

    /// <summary>Unload sweep: drop a chained dispatcher declared in (or targeting) one of these assemblies.</summary>
    internal static bool ClearChainForAssemblies(IEnumerable<System.Reflection.Assembly>? assemblies)
    {
        if (assemblies == null) return false;
        var names = new List<string?>();
        lock (ChainGate)
        {
            var live = _chained;
            if (live == null) return false;
            foreach (var asm in assemblies)
            {
                if (asm == null) continue;
                if (DelegateIn(live, asm))
                {
                    _chained = null;
                    _chainedOwner = null;
                    return true;
                }
                try { names.Add(asm.GetName().Name); } catch { /* unloading */ }
                try { names.Add(System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(asm)?.Name); } catch { /* unloading */ }
            }
        }
        return ClearChainOwnedBy(names);
    }

    private static bool DelegateIn(Delegate d, System.Reflection.Assembly asm)
    {
        try
        {
            if (ReferenceEquals(d.Method?.DeclaringType?.Assembly, asm)) return true;
            var target = d.Target;
            return target != null && ReferenceEquals(target.GetType().Assembly, asm);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Uninstall: drop everything this generation holds.</summary>
    internal static void Reset()
    {
        _slotArmed = false;
        _enabledReader = null;
        ClearChain(owner: null);
    }

#if PROGENITOR_TESTS
    internal static void UseRuntime(IGcRuntime? runtime) => _runtimeOverride = runtime;
#endif
}
