using System;
using System.Collections.Generic;
using System.Threading;
using Progenitor.Events;

namespace Progenitor.Units;

/// <summary>
/// Core-owned handle. Follow / GoTo / Despawn go through <see cref="IUnitRuntime"/> so
/// tests can drive the state machine without Unity. Arrival / unreachable publish on
/// <see cref="EventBus"/> from the main-thread poll, not from the Follow/GoTo call.
/// </summary>
internal sealed class UnitHandle : IUnitHandle, IUnitStepDriver
{
    /// <summary>ApproachPosition.Tick stop band max used by the spike (evidence 46).</summary>
    internal const float ArrivalDistance = 1f;

    /// <summary>Close enough to a follow target to raise Arrived once (FollowerSystem offset is 3–7 m).</summary>
    internal const float FollowArriveDistance = 3f;

    private readonly IUnitRuntime? _runtime;
    private readonly IUnitBehaviourRuntime? _behaviourRuntime;
    private readonly UnitSequencer _sequencer;
    private int _disposed;
    private bool _arrivedRaised;
    private bool _unreachableRaised;
    private bool _disabledLogged;
    private UnitId _followTarget;
    private UnitVec3 _goal;
    private UnitVec3 _position;
    private bool _addedFollower;
    private readonly UnitStatState _statState = new();

    /// <summary>
    /// The sequencer step, if any, whose GoTo wait this handle's next arrival belongs to. A
    /// move a mod starts directly on the handle carries <see cref="UnitMoveTicket.None"/>, so
    /// its arrival cannot complete a step the sequencer armed for a different walk
    /// (R56 finding 1).
    /// </summary>
    private UnitMoveTicket _moveTicket = UnitMoveTicket.None;

    internal UnitHandle(
        string prefab,
        string pluginId,
        object? owner,
        UnitId entity,
        UnitVec3 position,
        IUnitRuntime? runtime,
        bool enabled)
        : this(prefab, pluginId, owner, entity, position, runtime, enabled, Units.MonotonicSeconds)
    {
    }

    internal UnitHandle(
        string prefab,
        string pluginId,
        object? owner,
        UnitId entity,
        UnitVec3 position,
        IUnitRuntime? runtime,
        bool enabled,
        Func<double> clock)
    {
        Prefab = prefab ?? "";
        PluginId = pluginId ?? "";
        Owner = owner;
        Entity = entity;
        _position = position;
        _runtime = runtime;
        _behaviourRuntime = runtime as IUnitBehaviourRuntime;
        Enabled = enabled;
        State = enabled ? UnitHandleState.Spawned : UnitHandleState.Disabled;
        _sequencer = new UnitSequencer(this, clock ?? Units.MonotonicSeconds);
    }

    public string Prefab { get; }

    public string PluginId { get; }

    internal object? Owner { get; }

    public UnitId Entity { get; private set; }

    public bool Enabled { get; }

    public bool IsDisposed => _disposed != 0;

    public UnitHandleState State { get; private set; }

    /// <summary>
    /// True while the runtime still owns a Follower it added because the prefab did not
    /// ship one. GoTo / Despawn / RemoveAll clear this after the component is removed.
    /// </summary>
    internal bool AddedFollower => _addedFollower;

    public IUnitBehaviour Behaviour => _sequencer;

    /// <summary>
    /// True when this handle's sequence (playing or queued) holds a <see cref="UnitStep.Then"/>
    /// callback written in <paramref name="assembly"/>. The plugin sweep uses it
    /// to reach the ownerless static-API spawns its owner match cannot see (R55 finding 9).
    /// </summary>
    internal bool HoldsCallbackFrom(System.Reflection.Assembly assembly) =>
        _sequencer.HoldsCallbackFrom(assembly);

    /// <summary>
    /// The plugin that owns this unit is unloading: end the sequence, report it, and leave the
    /// unit alone. Published unless the handle is already disposed, in which case there is
    /// nobody sensible to hand it to (R57 finding 1). Returns true when there was a sequence
    /// to end, so the sweep logs a stop it actually performed rather than one line per idle
    /// handle the plugin happens to hold (R58 finding 3 widened the sweep to every handle).
    /// </summary>
    internal bool StopSequenceForUnload(string reason)
    {
        try { return _sequencer.StopForUnload(reason, publish: _disposed == 0); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Units: unload stop threw: " + e.Message);
            return false;
        }
    }

    /// <summary>
    /// Somebody else's plugin is unloading and this sequence names one of its methods: take
    /// those callbacks out and keep playing. Returns how many were dropped.
    /// </summary>
    internal int DropCallbacksFrom(System.Reflection.Assembly assembly)
    {
        try { return _sequencer.DropCallbacksFrom(assembly); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Units: callback drop threw: " + e.Message);
            return 0;
        }
    }

    /// <summary>
    /// A sequence with a <see cref="UnitStepKind.Wait"/> in it has to be ticked even when the
    /// unit is standing still, so the poll's "is anybody steering" shortcut has to count it.
    /// </summary>
    internal bool IsSteering =>
        _disposed == 0 && Enabled
        && (State == UnitHandleState.Following || State == UnitHandleState.GoingTo || _sequencer.IsPlaying || _statState.AnyPending);

    public UnitVec3 Position
    {
        get
        {
            if (_disposed != 0) return _position;
            if (_runtime != null && _runtime.TryGetPosition(Entity, out var pos))
            {
                _position = pos;
                return pos;
            }
            return _position;
        }
    }

    public UnitEngageResult Engage(UnitId target)
    {
        Units.EnsureMainThread(nameof(Engage));
        if (_disposed != 0 || !Enabled || target.IsNone)
            return default;
        try
        {
            return Units.Engage(Entity, target);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Units: Engage threw: " + e);
            return default;
        }
    }

    public void Follow(UnitId target)
    {
        Units.EnsureMainThread(nameof(Follow));
        if (_disposed != 0) return;
        if (!Enabled)
        {
            LogDisabledOnce("Follow");
            return;
        }

        if (target.IsNone)
        {
            Units.LogDebug("Units: Follow ignored a none target on " + Entity + ".");
            return;
        }

        _arrivedRaised = false;
        _unreachableRaised = false;
        // A fresh move: until the sequencer stamps it, this walk belongs to nobody's step.
        _moveTicket = UnitMoveTicket.None;

        bool ok = false;
        try { ok = _runtime != null && _runtime.Follow(Entity, target); }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Units: Follow threw: " + e);
        }

        _addedFollower = _runtime != null && _runtime.AddedFollower(Entity);
        if (!ok)
        {
            RaiseUnreachable(Position, "follow write refused");
            return;
        }

        _followTarget = target;
        _goal = default;
        State = UnitHandleState.Following;
        Units.LogDebug("Units: follow entity=" + Entity + " -> " + target);
    }

    public void GoTo(UnitVec3 position)
    {
        Units.EnsureMainThread(nameof(GoTo));
        if (_disposed != 0) return;
        if (!Enabled)
        {
            LogDisabledOnce("GoTo");
            return;
        }

        _arrivedRaised = false;
        _unreachableRaised = false;
        _moveTicket = UnitMoveTicket.None;

        bool ok = false;
        try { ok = _runtime != null && _runtime.GoTo(Entity, position); }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Units: GoTo threw: " + e);
        }

        _addedFollower = _runtime != null && _runtime.AddedFollower(Entity);
        if (!ok)
        {
            RaiseUnreachable(position, "goto write refused");
            return;
        }

        _followTarget = UnitId.None;
        _goal = position;
        State = UnitHandleState.GoingTo;
        Units.LogDebug("Units: go entity=" + Entity + " -> " + position);
    }

    public void Despawn() => Dispose();

    public void Dispose()
    {
        Units.EnsureMainThread(nameof(Despawn));
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        // Teardown: clear the state machine without publishing, the handle is going away.
        try { _sequencer.StopSilently(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Units: sequence stop threw: " + e.Message); }
        State = UnitHandleState.Disposed;
        _addedFollower = false;
        try
        {
            if (Enabled && _runtime != null && !Entity.IsNone)
                _runtime.Despawn(Entity);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Units: despawn threw: " + e.Message);
        }

        if (Enabled)
            Plugin.Logger?.LogInfo("Units: despawn entity=" + Entity + " prefab='" + Prefab + "'");
        Units.Forget(this);
    }

    /// <summary>Main-thread poll. Raises at most one Arrived and one Unreachable per Follow/GoTo.</summary>
    internal void Poll()
    {
        if (_disposed != 0 || !Enabled) return;

        // Timers first: a Wait / Animate step must advance even when nothing is steering.
        try { _sequencer.Tick(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Units: sequence tick threw: " + e.Message); }

        if (_disposed != 0) return;
        if (_statState.AnyPending)
        {
            try { ApplyPendingStats(); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Units: pending stats threw: " + e.Message); }
        }

        if (_disposed != 0) return;
        if (State != UnitHandleState.Following && State != UnitHandleState.GoingTo) return;

        try
        {
            if (_runtime == null || !_runtime.Exists(Entity))
            {
                RaiseUnreachable(State == UnitHandleState.GoingTo ? _goal : Position, "entity gone");
                return;
            }

            if (!_runtime.TryGetPosition(Entity, out var pos))
                return;
            _position = pos;

            if (State == UnitHandleState.GoingTo)
            {
                if (UnitVec3.Distance(pos, _goal) <= ArrivalDistance)
                    RaiseArrived(pos);
                return;
            }

            if (!_runtime.Exists(_followTarget))
            {
                RaiseUnreachable(pos, "follow target gone");
                return;
            }

            if (_runtime.TryGetPosition(_followTarget, out var targetPos)
                && UnitVec3.Distance(pos, targetPos) <= FollowArriveDistance)
                RaiseArrived(pos);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Units: poll threw: " + e.Message);
        }
    }

    /// <summary>
    /// Arrival: the bus publication and the sequencer's notify happen inside <b>one</b>
    /// sequencer entry (R56 finding 1). A subscriber still sees the arrival before the next
    /// step's write, which is what the old publish-then-notify order was for — but its
    /// <c>Play</c> is now deferred to the end of that entry instead of being applied inline,
    /// where the stale notify landed on the new sequence's first <c>GoTo</c> and published a
    /// forty-nine-meter walk as <c>Completed</c> without taking a step.
    /// </summary>
    private void RaiseArrived(UnitVec3 position)
    {
        if (_arrivedRaised || _disposed != 0) return;
        _arrivedRaised = true;
        if (State == UnitHandleState.GoingTo)
            State = UnitHandleState.Spawned;
        Plugin.Logger?.LogInfo("Units: arrived entity=" + Entity + " at " + position);
        var ticket = _moveTicket;
        _moveTicket = UnitMoveTicket.None;
        try
        {
            _sequencer.RaiseArrived(ticket, () =>
            {
                try { EventBus.Publish(new UnitArrived(this, position)); }
                catch (Exception e) { Plugin.Logger?.LogError("Units: Arrived publish threw: " + e); }
            });
        }
        catch (Exception e) { Plugin.Logger?.LogError("Units: step advance threw: " + e); }
    }

    /// <summary>Same one-entry shape as <see cref="RaiseArrived"/>, for the same reason.</summary>
    private void RaiseUnreachable(UnitVec3 goal, string reason)
    {
        if (_unreachableRaised || _disposed != 0) return;
        _unreachableRaised = true;
        if (State == UnitHandleState.Following || State == UnitHandleState.GoingTo)
            State = UnitHandleState.Spawned;
        Plugin.Logger?.LogInfo("Units: unreachable entity=" + Entity + " reason=" + reason);
        var ticket = _moveTicket;
        _moveTicket = UnitMoveTicket.None;
        try
        {
            _sequencer.RaiseUnreachable(ticket, reason, () =>
            {
                try { EventBus.Publish(new UnitUnreachable(this, goal, reason)); }
                catch (Exception e) { Plugin.Logger?.LogError("Units: Unreachable publish threw: " + e); }
            });
        }
        catch (Exception e) { Plugin.Logger?.LogError("Units: step advance threw: " + e); }
    }

    public UnitStatsWriteResult TrySetStats(UnitStatOverrides stats)
    {
        Units.EnsureMainThread(nameof(TrySetStats));
        if (stats == null) throw new ArgumentNullException(nameof(stats));
        if (_disposed != 0 || !Enabled || _runtime == null || Entity.IsNone)
            return new UnitStatsWriteResult(false, false, "handle disabled or disposed");
        if (stats.Refusal() is { } validation)
            return new UnitStatsWriteResult(false, false, validation);
        if (stats.IsEmpty)
            return new UnitStatsWriteResult(false, false, null);

        _statState.Request(stats.Clone());
        return ApplyPendingStats();
    }

    public bool TrySetLifetime(float seconds)
    {
        Units.EnsureMainThread(nameof(TrySetLifetime));
        if (_disposed != 0 || !Enabled || _runtime == null || Entity.IsNone) return false;
        float? clamped = UnitInstanceMutation.ClampLifetime(seconds);
        if (clamped == null) return false;
        try { return _runtime.TrySetLifetime(Entity, clamped.Value); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Units: TrySetLifetime threw: " + e.Message);
            return false;
        }
    }

    public bool TrySetTeam(int? teamValue = null, int? factionIndex = null)
    {
        Units.EnsureMainThread(nameof(TrySetTeam));
        if (_disposed != 0 || !Enabled || _runtime == null || Entity.IsNone) return false;
        if (teamValue == null && factionIndex == null) return true;
        try { return _runtime.TrySetTeam(Entity, teamValue, factionIndex); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Units: TrySetTeam threw: " + e.Message);
            return false;
        }
    }

    public UnitStripVBloodResult TryStripVBlood()
    {
        Units.EnsureMainThread(nameof(TryStripVBlood));
        if (_disposed != 0 || !Enabled || _runtime == null || Entity.IsNone)
            return default;
        try { return _runtime.StripVBlood(Entity); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Units: TryStripVBlood threw: " + e.Message);
            return default;
        }
    }

    /// <summary>Apply held stats once spawn computation has filled health (or immediately for power/level-only).</summary>
    internal UnitStatsWriteResult ApplyPendingStats()
    {
        if (_disposed != 0 || !Enabled || _runtime == null || Entity.IsNone || !_statState.AnyPending)
            return new UnitStatsWriteResult(false, false, null);

        if (!_runtime.TryGetHealth(Entity, out float max, out float value, out bool isDead))
            return new UnitStatsWriteResult(false, false, "unit " + Entity + " has no Health.");

        bool hasDead = _runtime.HasDeadComponent(Entity);
        var healthSlot = _statState[UnitStatKind.MaxHealth];
        if (healthSlot.Requested != null
            && UnitStatMath.DeadOrDownedRefusal(Entity.ToString(), isDead, hasDead, value, max) is { } why)
        {
            healthSlot.Requested = null;
            return new UnitStatsWriteResult(false, false, why);
        }

        bool needsHealthReady = healthSlot.Requested != null;
        if (needsHealthReady && !UnitStatMath.SpawnComputed(max))
            return new UnitStatsWriteResult(false, true, null);

        bool written = false;

        if (healthSlot.Requested is float requestedMax)
        {
            float write = UnitStatMath.PlanWrite(UnitStatKind.MaxHealth, max, healthSlot.Applied, requestedMax);
            if (_runtime.TrySetHealthPools(Entity, write, write, write))
            {
                healthSlot.Displaced ??= max;
                healthSlot.Applied = requestedMax;
                healthSlot.Requested = null;
                written = true;
            }
        }

        var physical = _statState[UnitStatKind.PhysicalPower];
        var spell = _statState[UnitStatKind.SpellPower];
        if (physical.Requested != null || spell.Requested != null)
        {
            if (!_runtime.TryGetUnitPower(Entity, out float curPhys, out float curSpell))
                return new UnitStatsWriteResult(written, false, "unit " + Entity + " has no UnitStats.");
            float? nextPhys = physical.Requested is float rp
                ? UnitStatMath.PlanWrite(UnitStatKind.PhysicalPower, curPhys, physical.Applied, rp)
                : null;
            float? nextSpell = spell.Requested is float rs
                ? UnitStatMath.PlanWrite(UnitStatKind.SpellPower, curSpell, spell.Applied, rs)
                : null;
            if (_runtime.TrySetUnitPower(Entity, nextPhys, nextSpell))
            {
                if (physical.Requested != null)
                {
                    physical.Displaced ??= curPhys;
                    physical.Applied = physical.Requested;
                    physical.Requested = null;
                }
                if (spell.Requested != null)
                {
                    spell.Displaced ??= curSpell;
                    spell.Applied = spell.Requested;
                    spell.Requested = null;
                }
                written = true;
            }
        }

        var level = _statState[UnitStatKind.Level];
        if (level.Requested is float requestedLevel)
        {
            if (!_runtime.TryGetLevel(Entity, out int curLevel))
                return new UnitStatsWriteResult(written, false, "unit " + Entity + " has no UnitLevel.");
            float write = UnitStatMath.PlanWrite(UnitStatKind.Level, curLevel, level.Applied, requestedLevel);
            if (_runtime.TrySetLevel(Entity, (int)write))
            {
                level.Displaced ??= curLevel;
                level.Applied = requestedLevel;
                level.Requested = null;
                written = true;
            }
        }

        return new UnitStatsWriteResult(written, false, null);
    }


    // ---- IUnitStepDriver: the sequencer's view of this handle ---------------------------------
    // Every decision above this line is game-free; these six writes are the only place the
    // sequencer touches the runtime, and each one folds a refusal into a step result.

    bool IUnitStepDriver.Alive => _disposed == 0 && Enabled && !Entity.IsNone;

    UnitStepResult IUnitStepDriver.Idle() =>
        Fold(_behaviourRuntime?.Idle(Entity), "Idle");

    UnitStepResult IUnitStepDriver.GoTo(UnitVec3 position)
    {
        GoTo(position);
        // GoTo raises Unreachable synchronously when the write is refused; the sequencer's
        // re-entrancy park turns that into Failed. Otherwise the state says it is under way.
        return State == UnitHandleState.GoingTo ? UnitStepResult.Running : UnitStepResult.Failed;
    }

    UnitStepResult IUnitStepDriver.Follow(UnitId target)
    {
        Follow(target);
        return State == UnitHandleState.Following ? UnitStepResult.Done : UnitStepResult.Failed;
    }

    void IUnitStepDriver.MoveArmed(UnitMoveTicket ticket) => _moveTicket = ticket;

    UnitStepResult IUnitStepDriver.Face(UnitId target) =>
        Fold(_behaviourRuntime?.Face(Entity, target), "Face");

    UnitStepResult IUnitStepDriver.Face(UnitVec3 position) =>
        Fold(_behaviourRuntime?.Face(Entity, position), "Face");

    UnitStepResult IUnitStepDriver.Animate(string animationName, float seconds)
    {
        if (_behaviourRuntime == null)
        {
            LogUnsupportedOnce("Animate");
            return UnitStepResult.Skipped;
        }

        bool ok = _behaviourRuntime.Animate(Entity, animationName, seconds, out string? refusal);
        if (ok) return UnitStepResult.Done;
        if (!string.IsNullOrEmpty(refusal))
            LogRefusalOnce("Animate", refusal!);
        return UnitStepResult.Skipped;
    }

    void IUnitStepDriver.Log(string line) => Plugin.Logger?.LogInfo(line + " entity=" + Entity);

    /// <summary>
    /// The live position, read straight through the runtime — <b>not</b> through
    /// <see cref="Position"/>, which falls back to the last value it saw. A disposed or
    /// disabled handle answers false, and so does a handle whose entity the runtime cannot
    /// read: that is what a killed or despawned unit looks like from here (job FUNIT6, R59
    /// finding 5). Going through the cache made that case answer true with a frozen number,
    /// so the wait-forever reminder saw "did not move" and printed the no-connected-player
    /// diagnosis at every reminder for a unit that no longer existed — the one line R58
    /// finding 8 wanted kept clean for real stalls.
    /// <para>
    /// <see cref="IUnitStepDriver.Alive"/> does not catch it: it is
    /// <c>_disposed == 0 &amp;&amp; Enabled &amp;&amp; !Entity.IsNone</c> and consults no
    /// runtime, and a destroyed entity keeps its handle value.
    /// </para>
    /// </summary>
    bool IUnitStepDriver.TryGetPosition(out UnitVec3 position)
    {
        position = default;
        if (_disposed != 0 || !Enabled || _runtime == null) return false;
        if (!_runtime.TryGetPosition(Entity, out var read)) return false;
        position = read;
        return true;
    }

    void IUnitStepDriver.StepCompleted(UnitStep step, int index, int count, UnitStepOutcome outcome, string reason)
    {
        try { EventBus.Publish(new UnitStepCompleted(this, step, index, count, outcome, reason)); }
        catch (Exception e) { Plugin.Logger?.LogError("Units: StepCompleted publish threw: " + e); }
    }

    void IUnitStepDriver.SequenceCompleted(UnitSequenceOutcome outcome, int stepsCompleted, int count, string reason)
    {
        try { EventBus.Publish(new UnitSequenceCompleted(this, outcome, stepsCompleted, count, reason)); }
        catch (Exception e) { Plugin.Logger?.LogError("Units: SequenceCompleted publish threw: " + e); }
    }

    /// <summary>
    /// A runtime with no behavior support at all is a skip, not a failure: the null runtime
    /// (client, skipped module) already returns disabled handles and must not turn a shared
    /// plugin's sequence into an error.
    /// </summary>
    private UnitStepResult Fold(bool? wrote, string op)
    {
        if (wrote == null)
        {
            LogUnsupportedOnce(op);
            return UnitStepResult.Failed;
        }

        return wrote.Value ? UnitStepResult.Done : UnitStepResult.Failed;
    }

    private readonly HashSet<string> _loggedOnce = new(StringComparer.Ordinal);

    private void LogUnsupportedOnce(string op)
    {
        if (!_loggedOnce.Add("unsupported:" + op)) return;
        Units.LogDebug("Units: " + op + " has no runtime on this side; the step is refused.");
    }

    private void LogRefusalOnce(string op, string refusal)
    {
        if (!_loggedOnce.Add("refusal:" + op)) return;
        Plugin.Logger?.LogInfo(refusal);
    }

    private void LogDisabledOnce(string op)
    {
        if (_disabledLogged) return;
        _disabledLogged = true;
        Units.LogDebug("Units: " + op + " on a disabled handle is a no-op.");
    }
}
