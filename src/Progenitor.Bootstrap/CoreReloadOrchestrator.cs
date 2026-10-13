using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Progenitor.Bootstrap;

/// <summary>
/// Ordered steps of a collectible Core swap. Live <see cref="CoreLoader"/> and the unit tests
/// share this so an exception cannot skip Unfreeze, and so "leave the old generation running
/// if possible" is the same decision in both.
/// </summary>
public interface ICoreReloadOps
{
    bool PreviousGenerationAlive { get; }
    bool AlreadyReloading { get; }
    int CurrentGeneration { get; }
    string? CurrentAlcName { get; }

    bool TryReadCoreBytes(out byte[] bytes, out string error);
    void Freeze();
    void Unfreeze();
    int UnloadPlugins();
    /// <summary>
    /// Collectible plugin ALC names still in <c>AssemblyLoadContext.All</c> after
    /// <see cref="UnloadPlugins"/>. Empty means Core may Uninstall. Tests mutate
    /// this between FrameTicker ticks to simulate collection. An enumeration
    /// failure must return a non-empty list (fail closed).
    /// </summary>
    IReadOnlyList<string> AlivePluginAlcNames();
    /// <summary>
    /// Names captured at <see cref="UnloadPlugins"/> so the cascade can log
    /// <c>plugin-alc name collected=True|False frames=N</c> even after the weak dies.
    /// </summary>
    IReadOnlyList<string> WatchedPluginAlcNames();
    /// <summary>
    /// True when a previous plugin-ALC wait miss latched weaks that are still
    /// alive. <c>RequestReload</c> / <see cref="CoreReloadOrchestrator.TryBegin"/>
    /// refuse with the Core-style restart line until they die.
    /// </summary>
    bool LatchedPluginAlcsAlive { get; }
    /// <summary>Names captured by <see cref="LatchUncollectedPluginAlcs"/>.</summary>
    IReadOnlyList<string> LatchedPluginAlcNames();
    /// <summary>
    /// Snapshot weaks/names of plugin ALCs that did not collect. Must run on a
    /// wait miss before <c>PutPluginsBack</c> so a retry cannot leak another
    /// generation of plugin contexts.
    /// </summary>
    void LatchUncollectedPluginAlcs();
    /// <summary>
    /// Live cascade yields FrameTicker ticks to poll plugin ALCs. Tests leave this
    /// false so <see cref="Run"/> decides immediately.
    /// </summary>
    bool YieldPluginAlcWait { get; }
    /// <summary>
    /// Live cascade yields <see cref="CoreReloadPolicy.UnityFlushFrames"/> FrameTicker
    /// ticks between Unity teardown (Uninstall, DestroyByPrefix, drop-core-work,
    /// null-runtime) and <c>ALC.Unload</c> so EventSystem / leftover MonoBehaviours
    /// of that frame cannot jump through a native UnityEvent thunk into an unloading
    /// ALC. Tests leave this false so <see cref="Run"/> Unload's in the same call.
    /// </summary>
    bool YieldUnityFlush { get; }
    /// <summary>
    /// Unity <c>Time.frameCount</c> (or a test stand-in) used on
    /// <c>unity-teardown frame=F</c> / <c>unload-alc frame=F+1</c> /
    /// <c>collect frame=F+2</c> log lines.
    /// </summary>
    int CurrentFrame { get; }
    /// <summary>
    /// True when Unload found plugin methods in the IL2CPP detour cache. Those ALCs
    /// will never collect; skip the 60-frame wait and latch immediately.
    /// </summary>
    bool PluginAlcsNeverCollectable { get; }
    /// <summary>
    /// Job FINJ1: reload-folder plugins that registered their own IL2CPP types AND reference Core.
    /// They can never be unloaded, so unloading Core under them would leave the outgoing generation
    /// pinned: the swap is refused before it freezes anything. Empty by default (tests, older ops).
    /// </summary>
    IReadOnlyList<string> CoreBoundProcessLifetimePlugins() => Array.Empty<string>();
    /// <summary>
    /// Bytes of the last generation that <see cref="LoadAndInstall"/> succeeded for.
    /// Restore after a broken N+1 on disk reloads these, not the new file. Null/empty
    /// must refuse restore rather than replay the N+1 file that just failed.
    /// </summary>
    byte[]? LastGoodBytes { get; }
    void DiagnosePins();
    bool SnapshotContent(out string error);
    bool UninstallCore(out string error);
    void DropCoreWork();
    void NullRuntime();
    bool UnloadAlc(out bool collected, out string error);
    bool LoadAndInstall(byte[] bytes, int generation, out string error);
    /// <summary>
    /// Null Harmony, receive, lifetime, and MainThread/tick slots that a failed
    /// <see cref="LoadAndInstall"/> may have filled. Must run before that
    /// generation's <c>ALC.Unload</c> (R14 finding 2): a live slot into an
    /// unloading collectible ALC is a silent process death on the next
    /// <c>SystemBase.Update</c>. Idempotent.
    /// </summary>
    void ClearSlotsAfterFailedInstall();
    /// <summary>
    /// Drop MainThread work and native pins tagged with the failed generation
    /// name. Call after <see cref="ClearSlotsAfterFailedInstall"/> so a
    /// trampoline cannot jump into a pin that was just freed. Never
    /// <c>ReleaseAll</c>.
    /// </summary>
    void ReleaseFailedGeneration(string? alcName);
    /// <summary>
    /// One collect-poll pulse: <c>GC.Collect</c> then <c>GC.WaitForPendingFinalizers</c>.
    /// Returns elapsed milliseconds (0 when unmeasured). The frame driver calls this
    /// on Core-ALC poll ticks that <see cref="CoreReloadPolicy.ShouldPulseCoreCollect"/>
    /// selects, then samples the generation weak. Fake ops record the two steps
    /// without running GC.
    /// </summary>
    int PulseCollect();
    /// <summary>
    /// Rename the on-disk Core image that just failed Install / protocol to
    /// <c>Progenitor.dll.failed-timestamp</c> and write last-good bytes back so the
    /// Step E watcher cannot loop. <paramref name="generation"/> is the failed N+1
    /// (not the rolled-back live generation). Idempotent when the file is already gone.
    /// </summary>
    void QuarantineFailedCore(int generation, string error);
    /// <summary>
    /// True when generation N's assembly is still the published Core. Leftover Harmony
    /// skips <c>ALC.Unload</c> and leaves this true so restore can
    /// <see cref="ReinstallCurrent"/>. After Unload drops the strong refs it is false,
    /// and restore must <see cref="LoadAndInstall"/> N's bytes into a fresh ALC — never
    /// rehydrate the old runtime object (that pin is why a real reload waited 30 frames
    /// and never collected while the dry-run collected at 11).
    /// </summary>
    bool HasPublishedAssembly { get; }
    /// <summary>
    /// Drop every strong reference to generation N (runtime object, assembly, ALC,
    /// restore candidate). Must run after a successful <see cref="UnloadAlc"/> and
    /// again at the start of the collect poll so the frame driver / pending collect
    /// cannot keep the old generation alive. Idempotent. Does not touch the
    /// <c>WeakReference</c> used to poll collect.
    /// </summary>
    void DropOldGenerationRefs();
    /// <summary>
    /// Re-Install generation N on the ALC that is still published. Only valid when
    /// <see cref="HasPublishedAssembly"/> is true (Unload was skipped). Collect-fail
    /// restore must not call this: it would keep the old runtime alive.
    /// </summary>
    bool ReinstallCurrent(out string error);
    void UnretireCoreWork();
    void OpenLiveGate();
    int ReloadPlugins();
    /// <summary>
    /// After a new ALC is live and plugins have re-Require/Provide, ask every currently
    /// connected user for a handshake the same way a join does. Same-ALC restore must not
    /// call this: Required/Provided survived Uninstall and the peers already answered.
    /// </summary>
    void ReaskConnectedHandshake();
    void Log(string message);
    /// <summary>
    /// Sweep + diagnostics + collect test, then always put generation N back. Used so pin
    /// lines can be read from the log without stacking N+1 or leaving the process Core-less.
    /// </summary>
    bool DryRun { get; }
}

/// <summary>Outcome of <see cref="CoreReloadOrchestrator.Run"/>.</summary>
public readonly struct CoreReloadResult
{
    public CoreReloadResult(
        bool ok,
        bool refused,
        bool oldGenerationRunning,
        int generation,
        bool? collected,
        string step,
        string summary,
        string? outcome = null)
    {
        Ok = ok;
        Refused = refused;
        OldGenerationRunning = oldGenerationRunning;
        Generation = generation;
        Collected = collected;
        Step = step;
        Summary = summary ?? "";
        Outcome = !string.IsNullOrEmpty(outcome)
            ? outcome
            : ok ? CoreReloadStatus.OutcomeOk
            : refused ? CoreReloadStatus.OutcomeRefused
            : CoreReloadStatus.OutcomeFailed;
    }

    public bool Ok { get; }
    public bool Refused { get; }
    public bool OldGenerationRunning { get; }
    public int Generation { get; }
    public bool? Collected { get; }
    public string Step { get; }
    public string Summary { get; }
    /// <summary>
    /// Bridge lastReload outcome. Failed-install restore is
    /// <see cref="CoreReloadStatus.OutcomeRestored"/>, not refused.
    /// </summary>
    public string Outcome { get; }

    public override string ToString() => Summary;
}

/// <summary>
/// State between <c>ALC.Unload</c> and the restore-vs-load decision. The live driver
/// holds this across frames so UnloadAlc's stack (strong ALC locals) can die before
/// the collect poll. Tests that need a single-shot swap still call
/// <see cref="CoreReloadOrchestrator.Run"/>.
/// </summary>
/// <summary>
/// Which collect the frame driver is polling. Plugin ALCs must die before Core
/// Uninstall: a live plugin context holds the Core assembly it resolved against
/// (evidence 59). <see cref="UnityFlush"/> is the one-frame gap after Unity
/// teardown and before <c>ALC.Unload</c> (client-hot-swap-crash.md).
/// </summary>
public enum CoreReloadPendingStage
{
    PluginAlcs,
    UnityFlush,
    CoreAlc,
}

public sealed class CoreReloadPendingCollect
{
    internal CoreReloadPendingCollect(
        ICoreReloadOps ops,
        string reason,
        bool dryRun,
        int current,
        int next,
        byte[] bytes,
        byte[] lastGoodBytes,
        int pluginsUnloaded,
        bool immediateCollected,
        CoreReloadPendingStage stage = CoreReloadPendingStage.CoreAlc)
    {
        Ops = ops;
        Reason = reason ?? "";
        DryRun = dryRun;
        Current = current;
        Next = next;
        Bytes = bytes ?? Array.Empty<byte>();
        LastGoodBytes = lastGoodBytes ?? Array.Empty<byte>();
        PluginsUnloaded = pluginsUnloaded;
        ImmediateCollected = immediateCollected;
        Stage = stage;
    }

    /// <summary>
    /// Bootstrap cascade host. Must not capture the old Core runtime, assembly, or ALC
    /// — those refs are dropped before the collect poll. Complete uses this only to
    /// <c>LoadAndInstall</c> the already-copied <see cref="Bytes"/>.
    /// </summary>
    internal ICoreReloadOps Ops { get; }
    internal string Reason { get; }
    internal bool DryRun { get; }
    internal int Current { get; }
    internal int Next { get; }
    /// <summary>N+1 bytes read from disk at the start of this swap.</summary>
    internal byte[] Bytes { get; }
    /// <summary>
    /// Last generation that installed successfully. Restore after a broken N+1
    /// reloads these, never <see cref="Bytes"/>.
    /// </summary>
    internal byte[] LastGoodBytes { get; }
    internal int PluginsUnloaded { get; }
    internal CoreReloadPendingStage Stage { get; }

    /// <summary>
    /// True when <c>UnloadAlc</c> reported the context already gone. The live path
    /// always reports false so the driver yields a frame; tests that decide instantly
    /// report the Fake's <c>Collects</c> flag. Meaningless while
    /// <see cref="Stage"/> is <see cref="CoreReloadPendingStage.PluginAlcs"/>.
    /// </summary>
    public bool ImmediateCollected { get; }

    /// <summary>
    /// Restore generation N or load N+1 (dry-run reloads N). Logs the collect-wait
    /// frame count. Safe to call from a later FrameTicker tick than <c>TryBegin</c>.
    /// </summary>
    public CoreReloadResult Complete(bool collected, int collectFrames) =>
        CoreReloadOrchestrator.Complete(this, collected, collectFrames);
}

/// <summary>
/// Cascade: Freeze → unload plugins (reverse) → wait plugin ALCs to collect → snapshot →
/// Uninstall → drop Core work → null runtime → <em>Unity flush</em> (one FrameTicker tick)
/// → ALC unload + bounded collect wait → Load → Install → open live-world gate → reload
/// plugins → Unfreeze. Bytes are read before Freeze so a missing file does not disrupt
/// the running generation. Restore after a broken N+1 reloads the last good bytes, not
/// the new file.
/// </summary>
public static class CoreReloadOrchestrator
{
    public const string StepStart = "start";
    public const string StepRead = "read";
    public const string StepFreeze = "freeze";
    public const string StepUnloadPlugins = "unload-plugins";
    public const string StepWaitPluginAlcs = "wait-plugin-alcs";
    public const string StepDiagnose = "diagnose-pins";
    public const string StepSnapshot = "snapshot";
    public const string StepUninstall = "uninstall";
    public const string StepDropWork = "drop-core-work";
    public const string StepNullRuntime = "null-runtime";
    public const string StepUnityTeardown = "unity-teardown";
    public const string StepUnloadAlc = "unload-alc";
    public const string StepCollectWait = "collect-wait";
    public const string StepLoad = "load";
    public const string StepClearSlots = "clear-slots";
    public const string StepReleaseFailed = "release-failed-generation";
    public const string StepQuarantine = "quarantine";
    public const string StepRestore = "restore";
    public const string StepOpenGate = "open-gate";
    public const string StepReloadPlugins = "reload-plugins";
    public const string StepUnfreeze = "unfreeze";

    /// <summary>
    /// Single-shot cascade used by unit tests. Live Bootstrap uses <see cref="TryBegin"/>
    /// plus <see cref="CoreReloadPendingCollect.Complete"/> across frames instead, so the
    /// collect wait does not block the main thread and UnloadAlc locals are not on the
    /// stack during the GC poll.
    /// </summary>
    public static CoreReloadResult Run(ICoreReloadOps ops, string reason)
    {
        if (!TryBegin(ops, reason, out var pending, out var terminal))
            return terminal;
        if (pending.Stage == CoreReloadPendingStage.PluginAlcs)
        {
            bool pluginCollected = CoreReloadPolicy.CountWaitedAlcNames(ops.AlivePluginAlcNames()) == 0;
            if (!ContinueAfterPlugins(pending, pluginCollected, pluginFrames: 0, out pending, out terminal))
                return terminal;
        }
        if (pending.Stage == CoreReloadPendingStage.UnityFlush)
        {
            if (!ContinueAfterUnityFlush(pending, out pending, out terminal))
                return terminal;
        }
        return pending.Complete(pending.ImmediateCollected, collectFrames: 0);
    }

    /// <summary>
    /// Freeze through plugin unload (and, when no plugin ALCs remain, through
    /// <c>ALC.Unload</c>). Returns false when the swap already has a terminal result.
    /// Returns true with <see cref="CoreReloadPendingStage.PluginAlcs"/> when the live
    /// path must poll plugin weaks before Uninstall, or
    /// <see cref="CoreReloadPendingStage.CoreAlc"/> when Unload succeeded (even if
    /// <c>collected</c> is already true — the caller may Complete immediately).
    /// UnloadAlc returning <c>ok=false, collected=false</c> restores generation N now.
    /// </summary>
    public static bool TryBegin(
        ICoreReloadOps ops,
        string reason,
        [NotNullWhen(true)] out CoreReloadPendingCollect? pending,
        out CoreReloadResult terminal)
    {
        pending = null;
        terminal = default;
        if (ops == null) throw new ArgumentNullException(nameof(ops));
        reason ??= "";

        if (ops.AlreadyReloading)
        {
            terminal = Refused(ops.CurrentGeneration, CoreReloadPolicy.RefuseAlreadyReloading);
            return false;
        }

        if (ops.PreviousGenerationAlive)
        {
            terminal = Refused(
                ops.CurrentGeneration,
                CoreReloadPolicy.RefusePreviousAlive(ops.CurrentGeneration, ops.CurrentAlcName));
            return false;
        }

        if (ops.LatchedPluginAlcsAlive)
        {
            terminal = Refused(
                ops.CurrentGeneration,
                CoreReloadPolicy.RefusePluginAlcAlive(ops.LatchedPluginAlcNames()));
            return false;
        }

        var lifetimeBound = ops.CoreBoundProcessLifetimePlugins();
        if (lifetimeBound is { Count: > 0 })
        {
            terminal = Refused(ops.CurrentGeneration, Il2CppInjectionPolicy.RefuseCoreSwap(lifetimeBound));
            return false;
        }

        int current = ops.CurrentGeneration;
        bool dryRun = ops.DryRun || CoreReloadPolicy.ParseDryRun(reason);
        int next = dryRun
            ? (current < CoreReloadPolicy.FirstGeneration ? CoreReloadPolicy.FirstGeneration : current)
            : CoreReloadPolicy.NextGeneration(current);

        if (CoreReloadPolicy.ShouldInstallDirect(current, ops.HasPublishedAssembly))
        {
            ops.Log(CoreReloadPolicy.FormatCoreLessInstall(next, reason));
            return CompleteDirectInstall(ops, reason, next, out pending, out terminal);
        }

        string step = StepStart;
        ops.Log(CoreReloadPolicy.StepLog(next, "start reason=" + reason + " from gen=" + current + (dryRun ? " dryRun" : "")));

        int pluginsUnloaded = 0;
        int pluginsReloaded = 0;
        try
        {
            step = StepRead;
            if (!ops.TryReadCoreBytes(out var bytes, out var readError))
            {
                ops.Log(CoreReloadPolicy.StepLog(next, "read failed: " + readError));
                terminal = Failed(next, step, readError, oldRunning: true);
                return false;
            }
            ops.Log(CoreReloadPolicy.StepLog(next, "read " + bytes.Length + " bytes"));
            byte[] lastGood = ops.LastGoodBytes ?? Array.Empty<byte>();

            step = StepFreeze;
            ops.Freeze();
            ops.Log(CoreReloadPolicy.StepLog(next, StepFreeze));

            try
            {
                step = StepUnloadPlugins;
                pluginsUnloaded = ops.UnloadPlugins();
                ops.Log(CoreReloadPolicy.StepLog(next, "unloaded " + pluginsUnloaded + " plugin file(s)"));

                step = StepDiagnose;
                ops.DiagnosePins();
                ops.Log(CoreReloadPolicy.StepLog(next, StepDiagnose));

                int alivePlugins = CoreReloadPolicy.CountWaitedAlcNames(ops.AlivePluginAlcNames());
                if (alivePlugins > 0)
                {
                    step = StepWaitPluginAlcs;
                    ops.Log(CoreReloadPolicy.StepLog(
                        next,
                        StepWaitPluginAlcs + " watching " + alivePlugins.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                    if (ops.YieldPluginAlcWait && !ops.PluginAlcsNeverCollectable)
                    {
                        pending = new CoreReloadPendingCollect(
                            ops, reason, dryRun, current, next, bytes, lastGood, pluginsUnloaded,
                            immediateCollected: false,
                            CoreReloadPendingStage.PluginAlcs);
                        return true;
                    }
                    LogPluginAlcWait(ops, frames: 0);
                    ops.LatchUncollectedPluginAlcs();
                    PutPluginsBack(ops, current, step, ref pluginsReloaded);
                    terminal = Refused(current, CoreReloadPolicy.FormatPluginAlcRefused(
                        CoreReloadPolicy.FilterWaitedAlcNames(ops.AlivePluginAlcNames()), pluginsReloaded));
                    return false;
                }

                return ContinueFromSnapshot(
                    ops, reason, dryRun, current, next, bytes, lastGood, pluginsUnloaded,
                    ref step, out pending, out terminal);
            }
            catch (Exception e)
            {
                terminal = RecoverPlugins(ops, current, step, e.GetType().Name + ": " + e.Message, ref pluginsReloaded);
                ops.Log(CoreReloadPolicy.StepLog(next, "threw at " + step + " before Uninstall: " + e));
                return false;
            }
        }
        catch (Exception e)
        {
            ops.Log(CoreReloadPolicy.StepLog(next, "threw at " + step + ": " + e));
            terminal = Failed(next, step, e.GetType().Name + ": " + e.Message, oldRunning: true);
            return false;
        }
    }

    /// <summary>
    /// Core-less recovery: read the live image and Install gen 1. No freeze,
    /// snapshot, uninstall, or ALC wait — there is nothing to unload. A failed
    /// Install quarantines only when the on-disk file is still the loaded image.
    /// </summary>
    private static bool CompleteDirectInstall(
        ICoreReloadOps ops,
        string reason,
        int next,
        [NotNullWhen(true)] out CoreReloadPendingCollect? pending,
        out CoreReloadResult terminal)
    {
        pending = null;
        terminal = default;
        string step = StepRead;
        int pluginsReloaded = 0;
        try
        {
            if (!ops.TryReadCoreBytes(out var bytes, out var readError))
            {
                ops.Log(CoreReloadPolicy.StepLog(next, "read failed: " + readError));
                terminal = Failed(next, step, readError, oldRunning: false);
                return false;
            }
            ops.Log(CoreReloadPolicy.StepLog(next, "read " + bytes.Length + " bytes"));
            terminal = FinishWithNewAlc(
                ops,
                currentGeneration: 0,
                loadGen: next,
                dryRun: false,
                collected: true,
                bytes,
                lastGoodBytes: Array.Empty<byte>(),
                pluginsUnloaded: 0,
                reason,
                ref step,
                ref pluginsReloaded);
            return false;
        }
        catch (Exception e)
        {
            ops.Log(CoreReloadPolicy.StepLog(next, "threw at " + step + ": " + e));
            terminal = Failed(next, step, e.GetType().Name + ": " + e.Message, oldRunning: false);
            return false;
        }
    }

    /// <summary>
    /// Second half of plugin-ALC wait. Logs each <c>plugin-alc</c> line. A miss puts
    /// plugins back and refuses (Core is still installed). A hit continues snapshot
    /// through <c>ALC.Unload</c>.
    /// </summary>
    internal static bool ContinueAfterPlugins(
        CoreReloadPendingCollect pending,
        bool pluginAlcsCollected,
        int pluginFrames,
        [NotNullWhen(true)] out CoreReloadPendingCollect? nextPending,
        out CoreReloadResult terminal)
    {
        if (pending == null) throw new ArgumentNullException(nameof(pending));
        nextPending = null;
        terminal = default;
        var ops = pending.Ops;
        LogPluginAlcWait(ops, pluginFrames);
        if (!pluginAlcsCollected)
        {
            int pluginsReloaded = 0;
            ops.LatchUncollectedPluginAlcs();
            PutPluginsBack(ops, pending.Current, StepWaitPluginAlcs, ref pluginsReloaded);
            terminal = Refused(
                pending.Current,
                CoreReloadPolicy.FormatPluginAlcRefused(
                    CoreReloadPolicy.FilterWaitedAlcNames(StillAlivePluginNames(ops)), pluginsReloaded));
            return false;
        }

        string step = StepSnapshot;
        try
        {
            return ContinueFromSnapshot(
                ops, pending.Reason, pending.DryRun, pending.Current, pending.Next,
                pending.Bytes, pending.LastGoodBytes, pending.PluginsUnloaded,
                ref step, out nextPending, out terminal);
        }
        catch (Exception e)
        {
            int pluginsReloaded = 0;
            terminal = RecoverPlugins(
                ops, pending.Current, step, e.GetType().Name + ": " + e.Message, ref pluginsReloaded);
            ops.Log(CoreReloadPolicy.StepLog(pending.Next, "threw at " + step + " before Uninstall: " + e));
            return false;
        }
    }

    /// <summary>
    /// Snapshot through <c>ALC.Unload</c>. Shared by <see cref="TryBegin"/> (no plugin
    /// ALCs left) and <see cref="ContinueAfterPlugins"/> (plugin wait succeeded).
    /// Core is still installed on entry.
    /// </summary>
    private static bool ContinueFromSnapshot(
        ICoreReloadOps ops,
        string reason,
        bool dryRun,
        int current,
        int next,
        byte[] bytes,
        byte[] lastGoodBytes,
        int pluginsUnloaded,
        ref string step,
        [NotNullWhen(true)] out CoreReloadPendingCollect? pending,
        out CoreReloadResult terminal)
    {
        pending = null;
        terminal = default;
        int pluginsReloaded = 0;
        bool uninstallSucceeded = false;
        bool reachedUnloadAlc = false;
        try
        {
            step = StepSnapshot;
            if (!ops.SnapshotContent(out var snapError))
            {
                ops.Log(CoreReloadPolicy.StepLog(next, "snapshot failed: " + snapError));
                terminal = RecoverPlugins(ops, current, step, snapError, ref pluginsReloaded);
                return false;
            }
            ops.Log(CoreReloadPolicy.StepLog(next, StepSnapshot));

            step = StepUninstall;
            if (!ops.UninstallCore(out var uninstallError))
            {
                ops.Log(CoreReloadPolicy.StepLog(next, "uninstall failed: " + uninstallError));
                terminal = RecoverPlugins(ops, current, step, uninstallError, ref pluginsReloaded);
                return false;
            }
            uninstallSucceeded = true;
            ops.Log(CoreReloadPolicy.StepLog(next, StepUninstall));

            step = StepDropWork;
            ops.DropCoreWork();
            ops.Log(CoreReloadPolicy.StepLog(next, StepDropWork));

            step = StepNullRuntime;
            ops.NullRuntime();
            ops.Log(CoreReloadPolicy.StepLog(next, StepNullRuntime));
            ops.Log(CoreReloadPolicy.StepLog(next, CoreReloadPolicy.FormatUnityTeardown(ops.CurrentFrame)));

            if (ops.YieldUnityFlush && CoreReloadPolicy.UnityFlushFrames > 0)
            {
                pending = new CoreReloadPendingCollect(
                    ops, reason, dryRun, current, next, bytes, lastGoodBytes, pluginsUnloaded,
                    immediateCollected: false, CoreReloadPendingStage.UnityFlush);
                return true;
            }

            reachedUnloadAlc = true;
            return ContinueUnloadAlc(
                ops, reason, dryRun, current, next, bytes, lastGoodBytes, pluginsUnloaded,
                ref step, out pending, out terminal);
        }
        catch (Exception e)
        {
            try
            {
                if (!uninstallSucceeded)
                {
                    terminal = RecoverPlugins(ops, current, step, e.GetType().Name + ": " + e.Message, ref pluginsReloaded);
                    ops.Log(CoreReloadPolicy.StepLog(next, "threw at " + step + " before Uninstall: " + e));
                    return false;
                }
                if (reachedUnloadAlc && !ops.HasPublishedAssembly)
                    ops.DropOldGenerationRefs();
                terminal = Restore(
                    ops, current, dryRun, pluginsUnloaded, reachedUnloadAlc,
                    BytesForRestore(lastGoodBytes, bytes), ref step, ref pluginsReloaded);
                ops.Log(CoreReloadPolicy.StepLog(next, "threw at " + step + ": " + e));
                return false;
            }
            catch (Exception restoreEx)
            {
                ops.Log(CoreReloadPolicy.StepLog(next, "restore after throw failed: " + restoreEx.Message));
            }
            SafeUnfreeze(ops, next);
            ops.Log(CoreReloadPolicy.StepLog(next, "threw at " + step + ": " + e));
            terminal = Failed(next, step, e.GetType().Name + ": " + e.Message, oldRunning: true);
            return false;
        }
    }

    /// <summary>
    /// After the Unity-flush yield: EventSystem of the teardown frame has run.
    /// <c>ALC.Unload</c> is now safe to request.
    /// </summary>
    internal static bool ContinueAfterUnityFlush(
        CoreReloadPendingCollect pending,
        [NotNullWhen(true)] out CoreReloadPendingCollect? nextPending,
        out CoreReloadResult terminal)
    {
        if (pending == null) throw new ArgumentNullException(nameof(pending));
        nextPending = null;
        terminal = default;
        var ops = pending.Ops;
        string step = StepUnloadAlc;
        int pluginsReloaded = 0;
        try
        {
            return ContinueUnloadAlc(
                ops, pending.Reason, pending.DryRun, pending.Current, pending.Next,
                pending.Bytes, pending.LastGoodBytes, pending.PluginsUnloaded,
                ref step, out nextPending, out terminal);
        }
        catch (Exception e)
        {
            try
            {
                if (!ops.HasPublishedAssembly)
                    ops.DropOldGenerationRefs();
                terminal = Restore(
                    ops, pending.Current, pending.DryRun, pending.PluginsUnloaded, reachedUnloadAlc: true,
                    BytesForRestore(pending.LastGoodBytes, pending.Bytes), ref step, ref pluginsReloaded);
                ops.Log(CoreReloadPolicy.StepLog(pending.Next, "threw at " + step + ": " + e));
                return false;
            }
            catch (Exception restoreEx)
            {
                ops.Log(CoreReloadPolicy.StepLog(pending.Next, "restore after throw failed: " + restoreEx.Message));
            }
            SafeUnfreeze(ops, pending.Next);
            ops.Log(CoreReloadPolicy.StepLog(pending.Next, "threw at " + step + ": " + e));
            terminal = Failed(pending.Next, step, e.GetType().Name + ": " + e.Message, oldRunning: true);
            return false;
        }
    }

    /// <summary>
    /// <c>ALC.Unload</c> half. Shared by same-tick tests and the live path after the
    /// Unity-flush yield.
    /// </summary>
    private static bool ContinueUnloadAlc(
        ICoreReloadOps ops,
        string reason,
        bool dryRun,
        int current,
        int next,
        byte[] bytes,
        byte[] lastGoodBytes,
        int pluginsUnloaded,
        ref string step,
        [NotNullWhen(true)] out CoreReloadPendingCollect? pending,
        out CoreReloadResult terminal)
    {
        pending = null;
        terminal = default;
        int pluginsReloaded = 0;
        step = StepUnloadAlc;
        ops.Log(CoreReloadPolicy.StepLog(next, CoreReloadPolicy.FormatUnloadAlcFrame(ops.CurrentFrame)));
        if (!ops.UnloadAlc(out var didCollect, out var unloadError))
        {
            ops.Log(CoreReloadPolicy.StepLog(next, "unload-alc failed: " + unloadError + " collected=" + didCollect));
            if (!didCollect)
            {
                if (!ops.HasPublishedAssembly)
                    ops.DropOldGenerationRefs();
                terminal = Restore(
                    ops, current, dryRun, pluginsUnloaded, reachedUnloadAlc: true,
                    BytesForRestore(lastGoodBytes, bytes), ref step, ref pluginsReloaded);
                return false;
            }
            ops.Log(CoreReloadPolicy.StepLog(next, "unload-alc collected after throw; loading instead of Core-less fail"));
            ops.DropOldGenerationRefs();
            pending = new CoreReloadPendingCollect(
                ops, reason, dryRun, current, next, bytes, lastGoodBytes, pluginsUnloaded,
                immediateCollected: true, CoreReloadPendingStage.CoreAlc);
            return true;
        }

        ops.Log(CoreReloadPolicy.StepLog(next, "unload-alc collected=" + didCollect));
        ops.DropOldGenerationRefs();
        pending = new CoreReloadPendingCollect(
            ops, reason, dryRun, current, next, bytes, lastGoodBytes, pluginsUnloaded,
            didCollect, CoreReloadPendingStage.CoreAlc);
        return true;
    }

    /// <summary>
    /// Second half of the cascade, after the collect poll. <paramref name="collectFrames"/>
    /// is logged; summaries stay the existing grep lines so dryRun/restore tests do not
    /// change.
    /// </summary>
    internal static CoreReloadResult Complete(CoreReloadPendingCollect pending, bool collected, int collectFrames)
    {
        if (pending == null) throw new ArgumentNullException(nameof(pending));
        var ops = pending.Ops;
        int current = pending.Current;
        int next = pending.Next;
        bool dryRun = pending.DryRun;
        int pluginsUnloaded = pending.PluginsUnloaded;
        int pluginsReloaded = 0;
        string step = StepCollectWait;
        ops.Log(CoreReloadPolicy.StepLog(next, CoreReloadPolicy.FormatCollectWait(collectFrames, collected)));
        // Collect poll is over. Drop again so a driver local or pending.Ops field
        // cannot keep generation N alive across LoadAndInstall.
        ops.DropOldGenerationRefs();

        if (!collected)
        {
            ops.Log(CoreReloadPolicy.StepLog(next, CoreReloadPolicy.DidNotCollect(current, ops.CurrentAlcName)));
            return Restore(
                ops, current, dryRun, pluginsUnloaded, reachedUnloadAlc: true,
                BytesForRestore(pending.LastGoodBytes, pending.Bytes), ref step, ref pluginsReloaded);
        }

        int loadGen = dryRun ? current : next;
        if (loadGen < CoreReloadPolicy.FirstGeneration)
            loadGen = CoreReloadPolicy.FirstGeneration;

        return FinishWithNewAlc(
            ops, current, loadGen, dryRun, collected: true,
            pending.Bytes, pending.LastGoodBytes, pluginsUnloaded, pending.Reason,
            ref step, ref pluginsReloaded);
    }

    /// <summary>
    /// Abort before Uninstall: Core is still installed, plugins were already dropped.
    /// Unfreeze (CoreAttached) first so plugin Scan is not a no-op, then put the files
    /// back. Reload-while-frozen left the process plugin-less (R13 brief).
    /// </summary>
    private static CoreReloadResult RecoverPlugins(
        ICoreReloadOps ops,
        int generation,
        string failedStep,
        string error,
        ref int pluginsReloaded)
    {
        PutPluginsBack(ops, generation, failedStep, ref pluginsReloaded);
        return Failed(generation, failedStep, error, oldRunning: true);
    }

    private static void PutPluginsBack(
        ICoreReloadOps ops,
        int generation,
        string failedStep,
        ref int pluginsReloaded)
    {
        SafeUnfreeze(ops, generation);
        try
        {
            pluginsReloaded = ops.ReloadPlugins();
            ops.Log(CoreReloadPolicy.StepLog(generation, "reloaded " + pluginsReloaded + " plugin file(s) after " + failedStep));
        }
        catch (Exception e)
        {
            ops.Log(CoreReloadPolicy.StepLog(generation, "plugin reload after " + failedStep + " threw: " + e.Message));
        }
    }

    /// <summary>
    /// Reverse the post-Uninstall half of the cascade. When Unload already dropped the
    /// published assembly, this is a fresh <c>LoadAndInstall</c> of generation N's bytes
    /// (exactly the collecting dry-run). When Unload was skipped (leftover Harmony) or
    /// never reached, <see cref="ICoreReloadOps.ReinstallCurrent"/> re-Installs the
    /// still-published ALC. Dry-run reports <c>ok</c> only when the cascade reached
    /// <c>unload-alc</c> — a throw during plugin unload is a failure, not a successful dry-run.
    /// A fresh-ALC restore re-asks connected users; same-ALC restore does not.
    /// </summary>
    private static CoreReloadResult Restore(
        ICoreReloadOps ops,
        int generation,
        bool dryRun,
        int pluginsUnloaded,
        bool reachedUnloadAlc,
        byte[] bytes,
        ref string step,
        ref int pluginsReloaded,
        int failedInstallGeneration = 0,
        string? failedInstallError = null)
    {
        if (reachedUnloadAlc && !ops.HasPublishedAssembly)
        {
            if (bytes == null || bytes.Length == 0)
            {
                string missing = "last-good bytes empty; refusing N+1 fallback";
                string coreLess = CoreReloadPolicy.FormatCoreLess(generation, missing);
                ops.Log(CoreReloadPolicy.StepLog(generation, coreLess));
                SafeUnfreeze(ops, generation);
                return Failed(generation, StepRestore, coreLess, oldRunning: false, collected: false);
            }
            ops.Log(CoreReloadPolicy.StepLog(generation, "restore via fresh LoadAndInstall gen=" + generation));
            return FinishWithNewAlc(
                ops, generation, generation, dryRun, collected: false, bytes, lastGoodBytes: bytes,
                pluginsUnloaded, reason: "",
                ref step, ref pluginsReloaded,
                failedInstallGeneration, failedInstallError);
        }

        _ = pluginsUnloaded;
        step = StepRestore;
        try
        {
            ops.Log(CoreReloadPolicy.StepLog(generation, "restore via ReinstallCurrent gen=" + generation));
            ops.UnretireCoreWork();
            if (!ops.ReinstallCurrent(out var restoreError))
            {
                ops.Log(CoreReloadPolicy.StepLog(generation, "restore failed: " + restoreError));
                SafeUnfreeze(ops, generation);
                return Failed(generation, step, restoreError, oldRunning: true, collected: false);
            }
            ops.Log(CoreReloadPolicy.StepLog(generation, "re-installed gen=" + generation));

            ops.OpenLiveGate();
            ops.Log(CoreReloadPolicy.StepLog(generation, StepOpenGate));

            pluginsReloaded = ops.ReloadPlugins();
            ops.Log(CoreReloadPolicy.StepLog(generation, "reloaded " + pluginsReloaded + " plugin file(s)"));

            ops.Unfreeze();
            ops.Log(CoreReloadPolicy.StepLog(generation, StepUnfreeze));

            bool failedInstall = failedInstallGeneration >= CoreReloadPolicy.FirstGeneration;
            bool dryRunOk = dryRun && reachedUnloadAlc && !failedInstall;
            string summary = failedInstall
                ? CoreReloadPolicy.FormatFailedInstallRestored(failedInstallGeneration, failedInstallError, generation)
                : dryRun
                    ? CoreReloadPolicy.FormatDryRun(generation, collected: false, restored: true)
                    : CoreReloadPolicy.FormatRestored(generation);
            ops.Log(CoreReloadPolicy.StepLog(generation, summary));
            return new CoreReloadResult(
                ok: dryRunOk,
                refused: !dryRun && !failedInstall,
                oldGenerationRunning: true,
                generation: generation,
                collected: false,
                step: StepUnfreeze,
                summary: summary,
                outcome: failedInstall ? CoreReloadStatus.OutcomeRestored : null);
        }
        catch (Exception e)
        {
            SafeUnfreeze(ops, generation);
            ops.Log(CoreReloadPolicy.StepLog(generation, "restore threw: " + e));
            return Failed(generation, step, e.GetType().Name + ": " + e.Message, oldRunning: true, collected: false);
        }
    }

    /// <summary>
    /// Load <paramref name="bytes"/> into a new collectible ALC, Install, open the
    /// live-world gate, reload plugins, re-ask connected users, unfreeze. Shared by the
    /// collecting success path (N+1, or N on a dry-run) and collect-fail restore (always N).
    /// A failed <c>LoadAndInstall</c> of N+1 after N collected clears every slot that
    /// may still point at the unloading ALC, releases that generation's pins, then
    /// restores N via a fresh ALC (same as a collect miss). A failed load of N itself
    /// after the old generation was unpublished leaves the process Core-less.
    /// </summary>
    private static CoreReloadResult FinishWithNewAlc(
        ICoreReloadOps ops,
        int currentGeneration,
        int loadGen,
        bool dryRun,
        bool collected,
        byte[] bytes,
        byte[] lastGoodBytes,
        int pluginsUnloaded,
        string reason,
        ref string step,
        ref int pluginsReloaded,
        int failedInstallGeneration = 0,
        string? failedInstallError = null)
    {
        step = StepLoad;
        if (!ops.LoadAndInstall(bytes, loadGen, out var loadError))
        {
            ops.Log(CoreReloadPolicy.StepLog(loadGen, "load failed: " + loadError));
            AbandonFailedInstall(ops, loadGen, loadError);
            // N collected, N+1 (or any other generation) Install failed: slots of the
            // dying ALC are now empty. Restore N the same way a collect miss does,
            // using the last good bytes — not the N+1 file that just failed (CV).
            // Do not recurse when this call already *is* the restore (loadGen == N).
            bool mayRestore = collected
                && currentGeneration >= CoreReloadPolicy.FirstGeneration
                && loadGen != currentGeneration;
            if (mayRestore)
            {
                ops.Log(CoreReloadPolicy.StepLog(
                    currentGeneration,
                    CoreReloadPolicy.FormatRestoreAfterFailedNext(currentGeneration, loadGen)));
                return Restore(
                    ops, currentGeneration, dryRun, pluginsUnloaded, reachedUnloadAlc: true,
                    BytesForRestore(lastGoodBytes, bytes), ref step, ref pluginsReloaded,
                    failedInstallGeneration: loadGen,
                    failedInstallError: loadError);
            }
            if (!collected)
                ops.Log(CoreReloadPolicy.StepLog(loadGen, CoreReloadPolicy.FormatCoreLess(loadGen, loadError)));
            SafeUnfreeze(ops, loadGen);
            return Failed(loadGen, step, loadError, oldRunning: false, collected: collected);
        }
        ops.Log(CoreReloadPolicy.StepLog(loadGen, "load+install gen=" + loadGen));
        // Dry-run that collected reused progenitor-core:N; DropOwnedBy retired that name
        // before UnloadAlc. Un-retire so MainThread.Post from the new ALC is not discarded.
        ops.UnretireCoreWork();

        step = StepOpenGate;
        ops.OpenLiveGate();
        ops.Log(CoreReloadPolicy.StepLog(loadGen, StepOpenGate));

        step = StepReloadPlugins;
        pluginsReloaded = ops.ReloadPlugins();
        ops.Log(CoreReloadPolicy.StepLog(loadGen, "reloaded " + pluginsReloaded + " plugin file(s)"));

        // Plugins have re-Require/Provide into this new ALC. Already-connected users
        // never fire OnPlayerConnected again; ask them the same way a join does.
        ops.ReaskConnectedHandshake();

        step = StepUnfreeze;
        ops.Unfreeze();
        ops.Log(CoreReloadPolicy.StepLog(loadGen, StepUnfreeze));

        string summary;
        bool ok;
        bool refused;
        bool oldRunning;
        string? outcome = null;
        bool failedInstall = failedInstallGeneration >= CoreReloadPolicy.FirstGeneration;
        if (failedInstall)
        {
            summary = CoreReloadPolicy.FormatFailedInstallRestored(failedInstallGeneration, failedInstallError, loadGen);
            ok = false;
            refused = false;
            oldRunning = true;
            outcome = CoreReloadStatus.OutcomeRestored;
        }
        else if (!collected)
        {
            summary = dryRun
                ? CoreReloadPolicy.FormatDryRun(loadGen, collected: false, restored: true)
                : CoreReloadPolicy.FormatRestored(loadGen);
            ok = dryRun;
            refused = !dryRun;
            oldRunning = true;
        }
        else
        {
            summary = dryRun
                ? CoreReloadPolicy.FormatDryRun(loadGen, collected: true, restored: true)
                : CoreReloadPolicy.FormatOk(loadGen, collected: true, pluginsUnloaded, pluginsReloaded, reason);
            ok = true;
            refused = false;
            oldRunning = false;
        }
        ops.Log(CoreReloadPolicy.StepLog(loadGen, summary));
        return new CoreReloadResult(
            ok: ok,
            refused: refused,
            oldGenerationRunning: oldRunning,
            generation: loadGen,
            collected: collected,
            step: step,
            summary: summary,
            outcome: outcome);
    }

    /// <summary>
    /// Null every slot that may still hold a delegate from <paramref name="loadGen"/>,
    /// then release that generation's pins and MainThread work. Order is slots first
    /// so a trampoline cannot jump into a pin that was just freed. Never throws out
    /// to the cascade: a throw here would skip restore of N.
    /// </summary>
    private static void AbandonFailedInstall(ICoreReloadOps ops, int loadGen, string error)
    {
        string? failedName = loadGen >= CoreReloadPolicy.FirstGeneration
            ? CoreReloadPolicy.AlcName(loadGen)
            : null;
        try
        {
            ops.QuarantineFailedCore(loadGen, error ?? "");
            ops.Log(CoreReloadPolicy.StepLog(loadGen, StepQuarantine));
        }
        catch (Exception e)
        {
            ops.Log(CoreReloadPolicy.StepLog(loadGen, StepQuarantine + " threw: " + e.Message));
        }
        try
        {
            ops.Log(CoreReloadPolicy.StepLog(
                loadGen,
                CoreReloadPolicy.FormatAbandonFailedInstall(loadGen, failedName)));
            ops.ClearSlotsAfterFailedInstall();
        }
        catch (Exception e)
        {
            ops.Log(CoreReloadPolicy.StepLog(loadGen, StepClearSlots + " threw: " + e.Message));
        }
        try
        {
            ops.ReleaseFailedGeneration(failedName);
            ops.Log(CoreReloadPolicy.StepLog(loadGen, StepReleaseFailed + " " + (failedName ?? "")));
        }
        catch (Exception e)
        {
            ops.Log(CoreReloadPolicy.StepLog(loadGen, StepReleaseFailed + " threw: " + e.Message));
        }
    }

    private static void SafeUnfreeze(ICoreReloadOps ops, int generation)
    {
        try
        {
            ops.Unfreeze();
            ops.Log(CoreReloadPolicy.StepLog(generation, "unfreeze after failure"));
        }
        catch (Exception e)
        {
            ops.Log(CoreReloadPolicy.StepLog(generation, "unfreeze threw: " + e.Message));
        }
    }

    /// <summary>
    /// Prefer the last generation that actually installed. An empty last-good
    /// must not fall back to the N+1 file that just failed (R15 finding 6):
    /// restore then hits CORE-LESS instead of replaying broken bytes.
    /// </summary>
    private static byte[] BytesForRestore(byte[]? lastGood, byte[]? newBytes)
    {
        _ = newBytes;
        if (lastGood != null && lastGood.Length > 0) return lastGood;
        return Array.Empty<byte>();
    }

    private static IReadOnlyList<string> StillAlivePluginNames(ICoreReloadOps ops)
    {
        var alive = ops.AlivePluginAlcNames();
        if (alive != null && alive.Count > 0) return alive;
        var watched = ops.WatchedPluginAlcNames();
        return watched ?? Array.Empty<string>();
    }

    private static void LogPluginAlcWait(ICoreReloadOps ops, int frames)
    {
        var aliveSet = new HashSet<string>(StringComparer.Ordinal);
        var alive = CoreReloadPolicy.FilterWaitedAlcNames(ops.AlivePluginAlcNames());
        for (int i = 0; i < alive.Count; i++)
        {
            if (!string.IsNullOrEmpty(alive[i]))
                aliveSet.Add(alive[i]);
        }
        var watched = CoreReloadPolicy.FilterWaitedAlcNames(ops.WatchedPluginAlcNames());
        for (int i = 0; i < watched.Count; i++)
        {
            string name = watched[i] ?? "";
            ops.Log(CoreReloadPolicy.FormatPluginAlcCollect(name, collected: !aliveSet.Contains(name), frames));
        }
        foreach (var name in aliveSet)
        {
            if (ContainsName(watched, name)) continue;
            ops.Log(CoreReloadPolicy.FormatPluginAlcCollect(name, collected: false, frames));
        }
    }

    private static bool ContainsName(IReadOnlyList<string>? names, string name)
    {
        if (names == null || string.IsNullOrEmpty(name)) return false;
        for (int i = 0; i < names.Count; i++)
        {
            if (string.Equals(names[i], name, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static CoreReloadResult Refused(int generation, string reason) =>
        new(
            ok: false,
            refused: true,
            oldGenerationRunning: true,
            generation: generation,
            collected: null,
            step: StepStart,
            summary: CoreReloadPolicy.FormatRefused(reason));

    private static CoreReloadResult Failed(int generation, string step, string error, bool oldRunning, bool? collected = null) =>
        new(
            ok: false,
            refused: false,
            oldGenerationRunning: oldRunning,
            generation: generation,
            collected: collected,
            step: step,
            summary: CoreReloadPolicy.FormatFailed(generation, step, error, oldRunning));
}
