using System;

namespace Progenitor.Bootstrap;

/// <summary>
/// Frame-driven Core swap. <see cref="Request"/> only stores the reason and returns
/// <c>scheduled gen=N</c>; the next <see cref="Tick"/> from Bootstrap's FrameTicker
/// (after Drain, never nested in a plugin or Core frame) runs Freeze through
/// <c>ALC.Unload</c>; later ticks pulse GC and then restore or load N+1.
/// Live <c>CoreLoader.RequestReload</c> is this type plus a <see cref="ICoreReloadOps"/>
/// that talks to Core only through <c>ICoreRuntime</c> during uninstall/install.
/// </summary>
public sealed class CoreReloadFrameDriver
{
    private string? _reason;
    private CoreReloadPendingCollect? _pending;
    private int _frames;
    private int _holdFrames;
    private int _consecutiveHitches;
    private bool _loggedHitchBackoff;
    private bool _loggedCollectFrame;

    /// <summary>True while a swap is scheduled or mid-collect.</summary>
    public bool IsBusy => _reason != null || _pending != null;

    /// <summary>True after <see cref="Request"/> and before the first <see cref="Tick"/>.</summary>
    public bool IsScheduled => _reason != null;

    /// <summary>True after plugin unload or Core <c>ALC.Unload</c> while polling weaks.</summary>
    public bool IsCollecting => _pending != null;

    /// <summary>True while polling plugin ALCs, before Core Uninstall.</summary>
    public bool IsWaitingPluginAlcs =>
        _pending != null && _pending.Stage == CoreReloadPendingStage.PluginAlcs;

    /// <summary>
    /// True after Unity teardown and before <c>ALC.Unload</c>, while EventSystem of
    /// the teardown frame is allowed to run.
    /// </summary>
    public bool IsWaitingUnityFlush =>
        _pending != null && _pending.Stage == CoreReloadPendingStage.UnityFlush;

    /// <summary>GC pulses completed while collecting. 0 until the first collect tick.</summary>
    public int CollectFrames => _frames;

    /// <summary>Reason queued by <see cref="Request"/>, or null when not scheduled.</summary>
    public string? ScheduledReason => _reason;

    /// <summary>
    /// Queue a swap. Does not Freeze, Uninstall, or touch Core. Returns the grep line
    /// <c>scheduled gen=N</c> (with <c> dryRun</c> when the reason asks for it) or a
    /// refuse if a swap is already in flight / a previous generation is still alive.
    /// </summary>
    public string Request(int currentGeneration, string? alcName, bool previousGenerationAlive, string reason)
    {
        if (IsBusy)
            return CoreReloadPolicy.RefuseAlreadyReloading;
        if (previousGenerationAlive)
            return CoreReloadPolicy.RefusePreviousAlive(currentGeneration, alcName);
        _reason = reason ?? "";
        // One FrameTicker tick so the Dev bridge can write "scheduled gen=N"
        // before this driver Unload()s that plugin ALC.
        _holdFrames = 1;
        bool dryRun = CoreReloadPolicy.ParseDryRun(_reason);
        return CoreReloadPolicy.FormatScheduled(
            CoreReloadPolicy.ScheduledGeneration(currentGeneration), dryRun);
    }

    /// <summary>
    /// One FrameTicker step. First call runs the cascade through <c>ALC.Unload</c> and
    /// yields so that method's locals (strong ALC refs) leave the stack. Later calls
    /// count as collect frames; when the ALC is dead or the frame budget is exhausted,
    /// completes restore vs load. Returns null while still waiting.
    /// </summary>
    /// <param name="ops">Live or fake cascade host. Not stored across the collect poll.</param>
    /// <param name="alcAlive"><c>WeakReference.IsAlive</c> of the unloaded context.
    /// Prefer <see cref="Tick(ICoreReloadOps, Func{bool})"/> so the weak is sampled
    /// after the pulse (including poll frame 60).</param>
    public CoreReloadResult? Tick(ICoreReloadOps ops, bool alcAlive) =>
        Tick(ops, () => alcAlive);

    /// <summary>
    /// One FrameTicker step. Samples <paramref name="alcAlive"/> <em>after</em> any
    /// collect pulse on that frame so a generation that dies on the last allowed
    /// pulse is collected, not restored and latched.
    /// </summary>
    public CoreReloadResult? Tick(ICoreReloadOps ops, Func<bool> alcAlive)
    {
        if (ops == null) throw new ArgumentNullException(nameof(ops));
        if (alcAlive == null) throw new ArgumentNullException(nameof(alcAlive));

        if (_holdFrames > 0)
        {
            _holdFrames--;
            return null;
        }

        if (_pending == null)
        {
            if (_reason == null) return null;
            string reason = _reason;
            _reason = null;
            _frames = 0;
            _loggedCollectFrame = false;
            if (!CoreReloadOrchestrator.TryBegin(ops, reason, out _pending, out var terminal))
                return terminal;
            if (_pending!.Stage == CoreReloadPendingStage.PluginAlcs)
            {
                // Yield at least one frame: UnloadAll's locals still pinned plugin
                // ALCs on this stack until TryBegin returned.
                return null;
            }
            if (_pending.Stage == CoreReloadPendingStage.UnityFlush)
            {
                // Yield UnityFlushFrames ticks: EventSystem / UiInputContext of the
                // teardown frame must run before ALC.Unload (client-hot-swap-crash.md).
                return null;
            }
            if (_pending.ImmediateCollected)
                return Finish(collected: true);
            // Yield at least one frame: UnloadAlc's locals still pinned the ALC on this
            // stack until TryBegin returned; the next tick's GC can see them gone.
            // Do not store `ops` (or any Core runtime / ALC) on the driver across the
            // collect poll — pending holds only bytes plus the Bootstrap host.
            return null;
        }

        if (_pending.Stage == CoreReloadPendingStage.PluginAlcs)
        {
            if (CoreReloadPolicy.ShouldPulseCollect(_frames, CoreReloadPolicy.CollectPulseEveryFrames))
                ops.PulseCollect();
            _frames++;
            bool pluginCollected = CoreReloadPolicy.CountWaitedAlcNames(ops.AlivePluginAlcNames()) == 0;
            int wait = DetourCachePolicy.PluginAlcWaitFrames(ops.PluginAlcsNeverCollectable);
            if (!pluginCollected && _frames < wait)
                return null;
            if (!CoreReloadOrchestrator.ContinueAfterPlugins(
                    _pending, pluginCollected, _frames, out var nextPending, out var pluginTerminal))
            {
                _pending = null;
                _frames = 0;
                return pluginTerminal;
            }
            _pending = nextPending;
            _frames = 0;
            _consecutiveHitches = 0;
            _loggedHitchBackoff = false;
            if (_pending!.Stage == CoreReloadPendingStage.UnityFlush)
                return null;
            if (_pending.ImmediateCollected)
                return Finish(collected: true);
            return null;
        }

        if (_pending.Stage == CoreReloadPendingStage.UnityFlush)
        {
            _frames++;
            if (_frames < CoreReloadPolicy.UnityFlushFrames)
                return null;
            if (!CoreReloadOrchestrator.ContinueAfterUnityFlush(
                    _pending, out var unloaded, out var unloadTerminal))
            {
                _pending = null;
                _frames = 0;
                return unloadTerminal;
            }
            _pending = unloaded;
            _frames = 0;
            _consecutiveHitches = 0;
            _loggedHitchBackoff = false;
            if (_pending!.ImmediateCollected)
                return Finish(collected: true);
            return null;
        }

        // Microsoft unloadability: Collect then WaitForPendingFinalizers then check
        // the resurrection-tracking weak. Pulse every frame for the first 10 so a
        // generation that dies still collects in 3–4 ticks; hitch backoff must not
        // skip that window. After frame 10, every 5th frame (or every 10th after two
        // slow pulses). Sample the weak AFTER the pulse, including the last allowed
        // frame (R18 finding 2 / R20 finding 5).
        if (!_loggedCollectFrame)
        {
            _loggedCollectFrame = true;
            try
            {
                ops.Log(CoreReloadPolicy.StepLog(
                    _pending.Next,
                    CoreReloadPolicy.FormatCollectFrame(ops.CurrentFrame)));
            }
            catch { /* collect-frame log must not abort the pulse */ }
        }
        if (CoreReloadPolicy.ShouldPulseCoreCollect(_frames, _consecutiveHitches))
        {
            int ms = ops.PulseCollect();
            if (ms > CoreReloadPolicy.HitchBackoffMs)
            {
                _consecutiveHitches++;
                if (_consecutiveHitches >= CoreReloadPolicy.HitchBackoffAfter
                    && _frames >= CoreReloadPolicy.CollectPulseEveryFrameUntil
                    && !_loggedHitchBackoff)
                {
                    _loggedHitchBackoff = true;
                    try { ops.Log(CoreReloadPolicy.FormatCollectHitchBackoff()); }
                    catch { /* hitch log must not abort collect */ }
                }
            }
            else
                _consecutiveHitches = 0;
        }
        _frames++;
        bool collected = !alcAlive();
        if (!collected && _frames < CoreReloadPolicy.CollectWaitFrames)
            return null;
        return Finish(collected);
    }



    /// <summary>Drop a half-finished swap (tick threw). Caller must Unfreeze.</summary>
    public void Reset()
    {
        _reason = null;
        _pending = null;
        _frames = 0;
        _holdFrames = 0;
        _consecutiveHitches = 0;
        _loggedHitchBackoff = false;
        _loggedCollectFrame = false;
    }

    private CoreReloadResult Finish(bool collected)
    {
        var pending = _pending ?? throw new InvalidOperationException("Core reload finish with no pending collect.");
        int frames = _frames;
        _pending = null;
        _frames = 0;
        _consecutiveHitches = 0;
        _loggedHitchBackoff = false;
        _loggedCollectFrame = false;
        return pending.Complete(collected, frames);
    }
}
