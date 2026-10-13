using System;

namespace Progenitor.Buffs;

/// <summary>
/// The live <see cref="IBuffApplication"/>. Owns its state machine; every world reading and every
/// world write goes through <see cref="IBuffRuntime"/>, so this file is game-free and the suite
/// drives the real transitions with a fake runtime.
/// </summary>
internal sealed class BuffApplication : IBuffApplication
{
    private readonly object _gate = new();
    private readonly IBuffRuntime? _runtime;

    private BuffApplicationState _state;
    private BuffEntityId _instance;
    private int _framesWaited;
    private bool _cancelRequested;
    private float _duration;

    internal BuffApplication(
        string buffId,
        string pluginId,
        object? owner,
        BuffEntityId target,
        int prefabGuid,
        float durationSeconds,
        int epoch,
        IBuffRuntime? runtime,
        BuffApplicationState state,
        BuffRefusal refusal = BuffRefusal.None,
        string? refusalDetail = null)
    {
        BuffId = buffId ?? "";
        PluginId = pluginId ?? "";
        Owner = owner;
        Target = target;
        PrefabGuid = prefabGuid;
        _duration = durationSeconds;
        Epoch = epoch;
        _runtime = runtime;
        _state = state;
        Refusal = refusal;
        RefusalReason = refusal == BuffRefusal.None ? "" : BuffRefusalText.For(refusal, refusalDetail);
    }

    public string BuffId { get; }

    public string PluginId { get; }

    /// <summary>The owner object passed at apply time. The unload sweep matches on it by reference.</summary>
    internal object? Owner { get; }

    public BuffEntityId Target { get; }

    /// <summary>PrefabGUID hash of the buff clone this application applies.</summary>
    internal int PrefabGuid { get; }

    /// <summary>World-load epoch stamped when the apply was queued.</summary>
    internal int Epoch { get; }

    public BuffEntityId Instance
    {
        get { lock (_gate) return _instance; }
    }

    public BuffApplicationState State
    {
        get { lock (_gate) return _state; }
    }

    public bool IsActive => State == BuffApplicationState.Active;

    public BuffRefusal Refusal { get; }

    public string RefusalReason { get; }

    private BuffRefusal _lastRefreshRefusal;

    /// <summary>
    /// Why the last refresh did not take. Guarded by <c>_gate</c> like every other mutable piece of
    /// this handle, because a refresh runs on the game main thread while a reader may not.
    /// </summary>
    public BuffRefusal LastRefreshRefusal
    {
        get { lock (_gate) return _lastRefreshRefusal; }
    }

    public string LastRefreshRefusalReason
    {
        get
        {
            BuffRefusal refusal;
            lock (_gate) refusal = _lastRefreshRefusal;
            return refusal == BuffRefusal.None ? "" : BuffRefusalText.For(refusal);
        }
    }

    public float RequestedDurationSeconds
    {
        get { lock (_gate) return _duration; }
    }

    public event Action<IBuffApplication>? OnStateChanged;

    /// <summary>True while the module must keep this in its poll list.</summary>
    internal bool IsLive => BuffApplicationPolicy.IsLive(State);

    /// <summary>True while this application holds the (target, buff) slot against other applications.</summary>
    internal bool HoldsSlot => BuffApplicationPolicy.HoldsSlot(State);

    /// <summary>
    /// True after a <see cref="Remove"/> that arrived before confirmation and has not resolved yet.
    /// It still holds the slot — the queued apply is in flight and this application is the one that
    /// will destroy a late instance — but it is no longer anybody's to query or refresh, which is
    /// why <see cref="Buffs.Find"/> and <see cref="Buffs.Active"/> skip it and a second apply is
    /// refused with <see cref="BuffRefusal.CancelInFlight"/> rather than handed this handle back.
    /// </summary>
    internal bool IsCancelling
    {
        get { lock (_gate) return _cancelRequested && _state == BuffApplicationState.Requested; }
    }

    /// <summary>
    /// Replace the duration a not-yet-confirmed application will write when it confirms. Returns
    /// false unless the application is still <see cref="BuffApplicationState.Requested"/> and
    /// uncanceled. This is the refresh path for the window between the queued apply and the
    /// instance appearing: without it the second call's duration is silently dropped and
    /// <see cref="Confirm"/> writes the first one.
    /// </summary>
    internal bool UpdatePendingDuration(float seconds)
    {
        if (float.IsNaN(seconds) || float.IsInfinity(seconds)) return false;
        if (seconds <= 0f || seconds > BuffApplicationPolicy.MaxDurationSeconds) return false;
        lock (_gate)
        {
            if (_state != BuffApplicationState.Requested || _cancelRequested) return false;
            _duration = seconds;
            return true;
        }
    }

    public bool Refresh(float seconds)
    {
        var runtime = _runtime;
        BuffEntityId instance;
        bool active;
        lock (_gate)
        {
            active = _state == BuffApplicationState.Active;
            instance = _instance;
        }

        // Recorded like every other refusing path. This one cannot be reached through ApplyOrRefresh
        // - Buffs.RefreshExisting only calls Refresh on an Active application - but a caller holding
        // the handle can reach it directly, and leaving a stale value behind a `false` would make the
        // documented "read LastRefreshRefusal" snippet lie on an ended buff.
        if (!active) return RefreshRefused(BuffRefusal.RefreshNotActive, instance, null);

        // Each of these RECORDS why. Before, they answered false and left _lastRefreshRefusal
        // holding whatever an earlier call had put there - and on the ApplyOrRefresh route, where the
        // bool is discarded, a stale None is indistinguishable from "the refresh took".
        if (runtime == null || instance.IsNone)
            return RefreshRefused(BuffRefusal.ModuleUnavailable, instance, null);
        if (float.IsNaN(seconds) || float.IsInfinity(seconds) || seconds <= 0f
            || seconds > BuffApplicationPolicy.MaxDurationSeconds)
            return RefreshRefused(BuffRefusal.BadDuration, instance, "requested=" + seconds + "s");
        // Before any world touch: after an epoch move the recorded index may name a different
        // entity, so READING through it is as wrong as writing through it.
        if (EpochChanged(runtime))
            return RefreshRefused(BuffRefusal.NoWorld, instance, null);

        // `seconds` is the time that should REMAIN from now - which is what "refresh" means to a
        // caller, and what this API promises. LifeTime.Duration is the TOTAL lifetime, measured
        // against a separate Age component the game increments (LifeTimeSystem holds RefRW<Age> with
        // RefRO<LifeTime> in the server metadata). So the total to write is elapsed +
        // remaining, and the elapsed half has to be read from the instance.
        //
        // Before this, Refresh wrote `seconds` straight into Duration, so a refresh shorter than the
        // time already elapsed ENDED the buff on the spot. Observed live 2026-09-13: `.rested 90`,
        // then `.rested 30` at about +31 s, gone immediately.
        if (!runtime.TryReadTiming(instance, out BuffTiming timing))
            return RefreshRefused(BuffRefusal.RefreshTimingUnavailable, instance, null);

        // What Duration MEANS depends on EndAction, so an end action this API cannot reason about is
        // refused rather than guessed at.
        //
        //   RemoveBuffStack - Duration is the length of one STACK PERIOD, not a lifetime. The game
        //     removes a stack and restarts the counter (LifeTimeSystem.ReduceStacks takes `ref Age`).
        //     Writing elapsed + remaining into it would lengthen every later stack:
        //     repeated refreshes ratchet upward with no ceiling below the range guard. The old
        //     total-writing behavior was at least stable under repetition; this one would not be.
        //   Unknown - a value this build does not recognize gives Duration no known meaning.
        //
        // None and Kill and Destroy are all "Duration is this instance's lifetime", which is the case
        // the arithmetic below is correct for.
        if (timing.End == BuffLifetimeEnd.RemoveBuffStack || timing.End == BuffLifetimeEnd.Unknown)
            return RefreshRefused(BuffRefusal.RefreshEndActionUnsupported, instance, "endAction=" + timing.End);

        float total = timing.Elapsed + seconds;

        // ABOVE the seam, deliberately. `float.NaN > 10000f` is FALSE, so a non-finite elapsed would
        // sail through the range check below and write a NaN Duration - and BuffApplicationPolicy
        // already names that outcome: a NaN Duration never compares true against elapsed time, so
        // the buff would simply never end. UnityBuffRuntime refuses a non-finite age as well, but
        // that file is excluded from the test assembly, and an invariant the suite cannot see is not
        // an invariant.
        if (float.IsNaN(total) || float.IsInfinity(total) || total <= 0f)
            return RefreshRefused(BuffRefusal.BadDuration, instance, "elapsed=" + timing.Elapsed);

        // LifeTime.Duration is [SyncRangedFloat(0, 10000, ...)] (server metadata), so the TOTAL
        // has its own ceiling even when the requested remaining time is legal on its own. Refuse
        // rather than clamp: a silently shortened buff is the class of surprise this change removes.
        if (total > BuffApplicationPolicy.MaxDurationSeconds)
            return RefreshRefused(
                BuffRefusal.RefreshTotalOutOfRange, instance,
                "elapsed=" + timing.Elapsed + "s + requested=" + seconds + "s = " + total + "s");

        // A write that did not take is a REFUSAL, not a success. This used to record
        // `timed ? None : NotTimed`, and None is the value that means "the refresh took" - so a
        // throwing write on a perfectly timed instance told every ApplyOrRefresh caller, and the
        // worked example's player, that a duration had been written when none had. That is the exact
        // failure LastRefreshRefusal exists to prevent, reintroduced on the one leg that can reach it:
        // TryReadTiming above already refuses an instance with no LifeTime, so by here `timed` is
        // true and the only way out is UnityBuffRuntime's catch around the component write.
        //
        // The `timed == false` leg is kept for a runtime implementation that reports it anyway.
        if (!runtime.SetDuration(instance, total, out bool timed))
            return RefreshRefused(
                timed ? BuffRefusal.RefreshWriteFailed : BuffRefusal.NotTimed, instance,
                "total=" + total + "s");

        lock (_gate)
        {
            // What the caller asked for, which is what RequestedDurationSeconds has always meant.
            _duration = seconds;
            _lastRefreshRefusal = BuffRefusal.None;
        }

        Plugin.Logger?.LogInfo(
            "buff refreshed id='" + BuffId + "' target=" + Target + " instance=" + instance
            + " remaining=" + seconds + "s (elapsed=" + timing.Elapsed
            + "s, LifeTime.Duration=" + total + "s)");
        return true;
    }

    /// <summary>
    /// Record why a refresh did not take, say it once, and answer false.
    ///
    /// <para>The record matters as much as the log line: on the <c>ApplyOrRefresh</c> route the bool
    /// this returns is discarded by <c>Buffs.RefreshExisting</c> and the caller is handed the live
    /// application, so <see cref="LastRefreshRefusal"/> is the only way a caller can tell a refresh
    /// that was refused from one that took.</para>
    /// </summary>
    private bool RefreshRefused(BuffRefusal refusal, BuffEntityId instance, string? detail)
    {
        lock (_gate) _lastRefreshRefusal = refusal;
        Plugin.Logger?.LogWarning(
            "buff refresh refused id='" + BuffId + "' target=" + Target + " instance=" + instance
            + " reason=" + refusal + ": " + BuffRefusalText.For(refusal, detail)
            + " Nothing was written; the buff keeps the duration it already had.");
        return false;
    }

    public void Remove() => Dispose();

    public void Dispose()
    {
        var runtime = _runtime;
        BuffApplicationState next;
        BuffEntityId instance;
        bool destroy;

        lock (_gate)
        {
            if (BuffApplicationPolicy.IsTerminal(_state)) return;

            instance = _instance;
            bool epochChanged = runtime != null && EpochChangedUnlocked(runtime);
            bool instanceExists = !epochChanged
                && !instance.IsNone
                && runtime != null
                && runtime.Exists(instance);

            destroy = BuffApplicationPolicy.MayDestroy(_state, epochChanged, !instance.IsNone, instanceExists);

            if (_state == BuffApplicationState.Requested && !destroy)
            {
                // Cancel during initialization. The apply is already queued: the module keeps
                // polling until the confirm budget runs out precisely so a late instance is
                // destroyed rather than left on the player (PollStep.CancelLateInstance).
                _cancelRequested = true;
                Plugin.Logger?.LogInfo(
                    "buff cancel id='" + BuffId + "' target=" + Target
                    + " (apply was queued and not confirmed; watching "
                    + (BuffApplicationPolicy.ConfirmFrames - _framesWaited)
                    + " more frame(s) for a late instance)");
                return;
            }

            next = BuffApplicationState.Removed;
            _instance = BuffEntityId.None;
            _state = next;
        }

        if (destroy && runtime != null)
        {
            // `outcome`, not `gone`: Issued means the destroy was asked for and the entity is still
            // there for now. Calling that variable `gone` is what made the old "(already gone)" log
            // line print for a destroy that had actually thrown.
            BuffDestroy outcome;
            try { outcome = runtime.DestroyInstance(instance); }
            catch (Exception e)
            {
                outcome = BuffDestroy.Failed;
                Plugin.Logger?.LogWarning(
                    "buff remove id='" + BuffId + "' instance=" + instance + " threw: " + e.Message);
            }

            Plugin.Logger?.LogInfo(
                "buff removed id='" + BuffId + "' target=" + Target + " instance=" + instance
                + outcome switch
                {
                    BuffDestroy.NotPresent => " (already gone)",
                    BuffDestroy.Failed => " (destroy FAILED; the buff may still be on the wearer - watching)",
                    _ => "",
                });

            // BUFFP2: the destroy is an event, not an immediate removal, and this handle is about to
            // go terminal and leave the poll list. Hand the entity to the module's retirement watch
            // so somebody still confirms it actually went away.
            //
            // Failed is watched too, deliberately: the entity is present and nothing is scheduled to
            // remove it, which is precisely the case that needs a retry.
            if (outcome != BuffDestroy.NotPresent) Buffs.NoteRetiring(instance, Target, BuffId, Epoch);
        }

        Buffs.Forget(this);
        Publish(next);
    }

    /// <summary>
    /// One main-thread poll. <see cref="BuffApplicationPolicy.Poll"/> decides; this reads the world
    /// for it and performs the one write each step calls for.
    /// </summary>
    internal void Poll()
    {
        var runtime = _runtime;
        if (runtime == null) return;

        BuffApplicationState state;
        bool cancel;
        int waited;
        BuffEntityId instance;
        lock (_gate)
        {
            state = _state;
            cancel = _cancelRequested;
            waited = _framesWaited;
            instance = _instance;
        }

        if (!BuffApplicationPolicy.IsLive(state)) return;

        bool epochChanged = EpochChanged(runtime);
        bool targetExists = !epochChanged && Safe(() => runtime.Exists(Target));
        BuffEntityId found = BuffEntityId.None;
        bool instanceFound;

        if (epochChanged)
        {
            instanceFound = false;
        }
        else if (state == BuffApplicationState.Active)
        {
            // A confirmed application watches the instance it owns, not "any instance of this
            // buff": a second source's instance of the same prefab must not keep this one alive.
            instanceFound = !instance.IsNone && Safe(() => runtime.Exists(instance));
        }
        else
        {
            instanceFound = targetExists && TryFind(runtime, out found);
        }

        var step = BuffApplicationPolicy.Poll(state, cancel, epochChanged, targetExists, instanceFound, waited);
        switch (step)
        {
            case BuffApplicationPolicy.PollStep.WaitForInstance:
                lock (_gate) _framesWaited++;
                return;

            case BuffApplicationPolicy.PollStep.Confirm:
                Confirm(runtime, found);
                return;

            case BuffApplicationPolicy.PollStep.StillActive:
            case BuffApplicationPolicy.PollStep.Done:
                return;

            case BuffApplicationPolicy.PollStep.GiveUp:
                Finish(BuffApplicationState.NotConfirmed,
                    "buff not confirmed id='" + BuffId + "' target=" + Target
                    + " after " + BuffApplicationPolicy.ConfirmFrames
                    + " frame(s); the apply did not take and nothing was created");
                return;

            case BuffApplicationPolicy.PollStep.Ended:
                Finish(BuffApplicationState.Ended,
                    "buff ended id='" + BuffId + "' target=" + Target + " instance=" + instance
                    + " (" + BuffApplicationPolicy.EndedMeaning + ")");
                return;

            case BuffApplicationPolicy.PollStep.TargetLost:
                Finish(BuffApplicationState.TargetLost,
                    "buff target lost id='" + BuffId + "' target=" + Target
                    + " (the entity is gone; its buffs went with it and nothing is owed)");
                return;

            case BuffApplicationPolicy.PollStep.WorldChanged:
                Finish(BuffApplicationState.TargetLost,
                    "buff target lost id='" + BuffId + "' target=" + Target
                    + " (the world was replaced: entity ids from epoch " + Epoch
                    + " name different entities now, so nothing is read or written)");
                return;

            case BuffApplicationPolicy.PollStep.CancelLateInstance:
                CancelLate(runtime, found);
                return;

            case BuffApplicationPolicy.PollStep.CancelGiveUp:
                Finish(BuffApplicationState.Removed,
                    "buff canceled id='" + BuffId + "' target=" + Target
                    + " (the queued apply never produced an instance; nothing to remove)");
                return;
        }
    }

    private void Confirm(IBuffRuntime runtime, BuffEntityId instance)
    {
        float duration;
        lock (_gate)
        {
            if (_state != BuffApplicationState.Requested) return;
            _instance = instance;
            _state = BuffApplicationState.Active;
            duration = _duration;
        }

        if (duration > 0f)
        {
            bool wrote;
            bool timed = true;
            try { wrote = runtime.SetDuration(instance, duration, out timed); }
            catch (Exception e)
            {
                wrote = false;
                Plugin.Logger?.LogWarning(
                    "buff duration id='" + BuffId + "' instance=" + instance + " threw: " + e.Message);
            }

            if (!wrote)
            {
                // The buff is on; only its timer is the clone's rather than the caller's. Said out
                // loud, because "applied for 30s" would otherwise be a claim nothing established.
                Plugin.Logger?.LogWarning(
                    "buff duration not written id='" + BuffId + "' instance=" + instance
                    + " requested=" + duration + "s; the clone's own duration stands"
                    + (timed ? "" : " - " + BuffRefusalText.For(BuffRefusal.NotTimed)));
                lock (_gate) _duration = BuffRequest.KeepPrefabDuration;
            }
        }

        Plugin.Logger?.LogInfo(BuffApplicationPolicy.FormatTransition(
            BuffId, Target, instance, BuffApplicationState.Active));
        Publish(BuffApplicationState.Active);
    }

    private void CancelLate(IBuffRuntime runtime, BuffEntityId instance)
    {
        lock (_gate)
        {
            if (_state != BuffApplicationState.Requested) return;
            _instance = BuffEntityId.None;
            _state = BuffApplicationState.Removed;
        }

        BuffDestroy outcome;
        try { outcome = runtime.DestroyInstance(instance); }
        catch (Exception e)
        {
            outcome = BuffDestroy.Failed;
            Plugin.Logger?.LogWarning(
                "buff cancel destroy id='" + BuffId + "' instance=" + instance + " threw: " + e.Message);
        }

        Plugin.Logger?.LogInfo(
            "buff canceled id='" + BuffId + "' target=" + Target + " instance=" + instance
            + " (arrived after the cancel and was destroyed"
            + outcome switch
            {
                BuffDestroy.NotPresent => "; already gone",
                BuffDestroy.Failed => "; destroy FAILED, watching",
                _ => "",
            }
            + ")");

        // BUFFP2, same reason as Dispose: a late instance destroyed by a cancel still has to be
        // confirmed gone, and this handle stops polling here. Failed is watched too.
        if (outcome != BuffDestroy.NotPresent) Buffs.NoteRetiring(instance, Target, BuffId, Epoch);

        Buffs.Forget(this);
        Publish(BuffApplicationState.Removed);
    }

    private void Finish(BuffApplicationState state, string line)
    {
        lock (_gate)
        {
            if (BuffApplicationPolicy.IsTerminal(_state)) return;
            _state = state;
            _instance = BuffEntityId.None;
        }

        Plugin.Logger?.LogInfo(line);
        Buffs.Forget(this);
        Publish(state);
    }

    /// <summary>
    /// Raise <see cref="OnStateChanged"/> one subscriber at a time. Walking the invocation list is
    /// the point, not a detail: invoking the multicast delegate whole means the first handler that
    /// throws swallows every handler registered after it, so one plugin's bug silently stops
    /// another plugin from ever learning the buff went on. Same shape as the rest of Core's
    /// publishers.
    /// </summary>
    private void Publish(BuffApplicationState state)
    {
        var handler = OnStateChanged;
        if (handler != null)
        {
            var subscribers = handler.GetInvocationList();
            for (int i = 0; i < subscribers.Length; i++)
            {
                try { ((Action<IBuffApplication>)subscribers[i])(this); }
                catch (Exception e)
                {
                    Plugin.Logger?.LogWarning(
                        "buff OnStateChanged id='" + BuffId + "' state=" + state + " threw: " + e.Message);
                }
            }
        }

        if (BuffApplicationPolicy.IsTerminal(state)) OnStateChanged = null;
    }

    private bool EpochChanged(IBuffRuntime runtime)
    {
        lock (_gate) return EpochChangedUnlocked(runtime);
    }

    private bool EpochChangedUnlocked(IBuffRuntime runtime)
    {
        try { return runtime.Epoch != Epoch; }
        catch { return true; }
    }

    private static bool Safe(Func<bool> read)
    {
        try { return read(); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("buff poll read threw: " + e.Message);
            return false;
        }
    }

    private bool TryFind(IBuffRuntime runtime, out BuffEntityId instance)
    {
        instance = BuffEntityId.None;
        try { return runtime.TryFindInstance(Target, PrefabGuid, out instance); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("buff poll BuffBuffer read threw: " + e.Message);
            instance = BuffEntityId.None;
            return false;
        }
    }

    /// <summary>
    /// Drop every handler. Called by the unload sweep on an application the unloading plugin
    /// <i>owns</i>, before it disposes: a delegate whose method lives in a collectible ALC keeps
    /// that generation alive.
    /// </summary>
    internal void DropCallbacks() => OnStateChanged = null;

    /// <summary>
    /// Remove this application on plugin unload <b>without</b> publishing
    /// <see cref="OnStateChanged"/>.
    ///
    /// <para><c>PluginReloader.Unload</c> calls <c>plugin.Unload()</c> → <c>ctx.UndoAll()</c>
    /// <i>before</i> <c>SweepRegistrations</c>, so at teardown time the unloading plugin's own
    /// handlers are all still attached. Publishing <see cref="BuffApplicationState.Removed"/> from
    /// <c>UndoAll</c> therefore ran plugin code out of the collectible ALC that was in the middle of
    /// going away — and a handler doing the plausible thing (re-apply to keep the buff topped up)
    /// re-entered <c>ctx.Buffs.Apply</c> on an already-undone bag, leaving a <c>Requested</c>
    /// application in <c>Buffs.Live</c> holding the plugin's owner reference, with an
    /// <c>ApplyBuff</c> event already queued into the world, for up to
    /// <see cref="BuffApplicationPolicy.ConfirmFrames"/> more frames.</para>
    ///
    /// <para>The buff itself is still removed — this drops the notification, not the cleanup. An
    /// explicit <see cref="Remove"/> from a loaded plugin still publishes: that is what the event
    /// is for.</para>
    /// </summary>
    internal void TerminateForUnload()
    {
        OnStateChanged = null;
        Dispose();
    }

    /// <summary>
    /// The token <c>ctx.Buffs.Apply</c> tracks instead of the application itself, so
    /// <c>PluginContext.UndoAll</c> takes the silent route above. The interlocked exchange makes a
    /// double-dispose of the bag terminate once.
    /// </summary>
    internal static IDisposable UnloadReleaseFor(IBuffApplication application) =>
        new UnloadRelease(application as BuffApplication);

    private sealed class UnloadRelease : IDisposable
    {
        private BuffApplication? _application;

        internal UnloadRelease(BuffApplication? application) => _application = application;

        public void Dispose()
        {
            var application = System.Threading.Interlocked.Exchange(ref _application, null);
            if (application == null) return;
            try { application.TerminateForUnload(); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Buffs: unload release threw: " + e.Message); }
        }
    }

    /// <summary>
    /// Drop only the handlers whose method was written in <paramref name="assembly"/>, and report
    /// how many went. This is the third-party half of the sweep, and it is the reason
    /// <see cref="DropCallbacks"/> alone is not enough.
    ///
    /// <para><see cref="Buffs.Find"/> is public and documented as finding an application
    /// <i>whoever owns it</i>, so one plugin subscribing to another plugin's application is an
    /// invited use. Such a handler is not reachable from the subscriber: it hangs off a
    /// <see cref="BuffApplication"/> in Core's own static <c>Live</c> list, owned by a plugin that
    /// is not unloading, so neither <c>EventBus.RemoveAll</c> nor
    /// <c>PluginOwnedAssemblies.RemoveEventHandlers</c> can see it — they only know about the
    /// event bus, and this is a plain C# event. Left attached it pins the subscriber's collectible
    /// ALC for as long as the stranger's buff is live, and Core later invokes a method in a
    /// torn-down generation.</para>
    ///
    /// <para>Exactly the hazard, and exactly the remedy, that <c>UnitSequencer.DropCallbacksFrom</c>
    /// already implements for a <c>Then</c> step on a third party's handle (R55 finding 9, R58
    /// finding 6). The owner's own application keeps its handlers here; the sweep drops those
    /// wholesale on the owned path.</para>
    /// </summary>
    internal int DropCallbacksFrom(System.Reflection.Assembly assembly)
    {
        if (assembly == null) return 0;

        var handler = OnStateChanged;
        if (handler == null) return 0;

        int dropped = 0;
        var subscribers = handler.GetInvocationList();
        for (int i = 0; i < subscribers.Length; i++)
        {
            var subscriber = (Action<IBuffApplication>)subscribers[i];
            System.Reflection.Assembly? declaring;
            try { declaring = subscriber.Method?.DeclaringType?.Assembly; }
            catch { continue; }

            if (!ReferenceEquals(declaring, assembly)) continue;
            OnStateChanged -= subscriber;
            dropped++;
        }

        return dropped;
    }

    /// <summary>
    /// Core is going away and there will be no more polls. Take one last look for the instance this
    /// application asked for, destroy it if it is there, and finish. Called by
    /// <see cref="Buffs.Uninstall"/> only — an ordinary <see cref="Dispose"/> of an unconfirmed
    /// apply keeps watching instead, which is what stops a cancel from orphaning a late buff.
    /// </summary>
    internal void FinishForShutdown()
    {
        var runtime = _runtime;
        BuffApplicationState state;
        BuffEntityId instance;
        lock (_gate)
        {
            if (BuffApplicationPolicy.IsTerminal(_state)) return;
            state = _state;
            instance = _instance;
        }

        if (runtime != null && !EpochChanged(runtime))
        {
            if (instance.IsNone && state == BuffApplicationState.Requested)
            {
                if (!TryFind(runtime, out instance)) instance = BuffEntityId.None;
            }

            if (!instance.IsNone)
            {
                BuffDestroy outcome;
                try { outcome = runtime.DestroyInstance(instance); }
                catch (Exception e)
                {
                    outcome = BuffDestroy.Failed;
                    Plugin.Logger?.LogWarning(
                        "buff shutdown destroy id='" + BuffId + "' instance=" + instance
                        + " threw: " + e.Message);
                }

                // BUFFP2/P3: note it even though Poll is about to be unhooked. Buffs.Uninstall
                // reports whatever is still pending *after* this loop, so a shutdown destroy that
                // was issued and never confirmed is named out loud rather than vanishing.
                if (outcome != BuffDestroy.NotPresent)
                    Buffs.NoteRetiring(instance, Target, BuffId, Epoch, atShutdown: true);
            }
            else if (state == BuffApplicationState.Requested)
            {
                // Honest residual: the queued ApplyBuffDebugEvent may still be consumed after this
                // generation is gone, and nothing will be left to notice the instance.
                Plugin.Logger?.LogWarning(
                    "buff shutdown id='" + BuffId + "' target=" + Target
                    + " was queued and never confirmed; if the game builds the instance after this "
                    + "unload, no Progenitor generation will be watching for it");
            }
        }

        OnStateChanged = null;
        Finish(BuffApplicationState.Removed,
            "buff removed id='" + BuffId + "' target=" + Target + " (Core uninstall)");
    }
}
