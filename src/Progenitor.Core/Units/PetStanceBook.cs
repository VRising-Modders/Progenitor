using System;
using System.Collections.Generic;

namespace Progenitor.Units;

/// <summary>
/// The game side of pet stances and hold (<see cref="PetStanceBook"/>). The server runtime is
/// <c>UnityUnitRuntime</c>; tests inject a fake. Every method swallows game exceptions.
/// </summary>
internal interface IUnitStanceRuntime
{
    /// <summary>Follow stance step (the existing pet step): "combat", "follow" or "gone".</summary>
    string PetTick(UnitId pet, UnitId owner);

    bool TryGetPosition(UnitId unit, out UnitVec3 position);

    /// <summary><c>AggroConsumer.Active</c> now; null when the unit is gone or has no <c>AggroConsumer</c>.</summary>
    bool? ReadAggroActive(UnitId unit);

    /// <summary>
    /// One hold step: <c>Active</c> 0, <c>AggroTarget</c> and <c>AlertTarget</c> cleared,
    /// <c>Follower.Followed</c> cleared, and Idle posted when the tree is in Alert, Combat, Return,
    /// Follow or a combat relocation. "hold" or "gone".
    /// </summary>
    string HoldTick(UnitId unit);

    /// <summary>
    /// True while a buff on the unit carries <c>DisableAggroBuff</c> with <c>TargetDontAttackOthers</c>:
    /// that buff, through the game's modification registry, owns <c>AggroConsumer.Active</c> then.
    /// </summary>
    bool HasAggroBlockingBuff(UnitId unit);

    /// <summary>
    /// Leave hold: write <paramref name="active"/> to <c>AggroConsumer.Active</c> (nothing when null;
    /// <see cref="PetStanceBook.RestoreRule"/> decides) and clear the hold's walk-back goal. No-op when
    /// the unit is gone.
    /// </summary>
    void ReleaseHold(UnitId unit, bool? active);

    /// <summary>
    /// Walk the unit back to its hold position: the runtime GoTo field set, with no handle ticket,
    /// state change or arrival events. False when the write was refused or the unit is gone.
    /// </summary>
    bool WalkTo(UnitId unit, UnitVec3 position);

    /// <summary>
    /// One attack step: aggro target set to <paramref name="target"/> with <c>Active</c> on, Follow
    /// cleared, Combat posted when not in Combat. "combat", "done" (target dead or gone) or "gone".
    /// </summary>
    string AttackTick(UnitId unit, UnitId target);
}

/// <summary>
/// Stance and hold bookkeeping for one plugin's units (<c>ctx.Units</c>). Game-free: every game step
/// goes through <see cref="IUnitStanceRuntime"/>.
/// Pets (with an owner) take any <see cref="PetStance"/>; a unit that is not a pet can only be held.
/// </summary>
internal sealed class PetStanceBook
{
    /// <summary>A held unit further than this (meters, horizontal) from its hold position is walked back.</summary>
    internal const float HoldLeash = 2f;

    /// <summary>Steps (0.5 s each) before a walk back is sent again while the unit is still away.</summary>
    internal const int GoToRetrySteps = 6;

    internal sealed class Entry
    {
        internal Entry(IUnitHandle unit) => Unit = unit;

        internal IUnitHandle Unit { get; }
        internal bool IsPet { get; set; }
        internal UnitId Owner { get; set; }
        internal PetStance Stance { get; set; }
        internal UnitVec3 HoldAt { get; set; }
        internal UnitId Target { get; set; }

        /// <summary>True while Hold owns the unit's aggro (Active written 0); <see cref="SavedActive"/> is what to write back.</summary>
        internal bool Pacified { get; set; }
        internal bool? SavedActive { get; set; }

        /// <summary>Steps left before another walk back may be sent.</summary>
        internal int GoToWait { get; set; }
    }

    private readonly Func<IUnitStanceRuntime> _runtime;
    private readonly Dictionary<UnitId, Entry> _entries = new();

    internal PetStanceBook(Func<IUnitStanceRuntime> runtime) =>
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));

    internal int Count => _entries.Count;

    internal bool TryGet(IUnitHandle handle, out Entry entry) => _entries.TryGetValue(handle.Entity, out entry!);

    internal bool IsPet(IUnitHandle handle) => TryGet(handle, out var e) && e.IsPet;

    internal bool IsHeld(IUnitHandle handle) => TryGet(handle, out var e) && e.Stance == PetStance.Hold;

    internal PetStance? StanceOf(IUnitHandle handle) => TryGet(handle, out var e) && e.IsPet ? e.Stance : null;

    /// <summary>Make <paramref name="handle"/> a pet of <paramref name="owner"/> in <paramref name="stance"/> (Follow or Hold).</summary>
    internal void SetPet(IUnitHandle handle, UnitId owner, PetStance stance)
    {
        RequireLive(handle, "SetPet");
        if (!Enum.IsDefined(typeof(PetStance), stance))
            throw new ArgumentOutOfRangeException(nameof(stance), "pet stance " + (int)stance + " is not a defined PetStance.");
        if (stance == PetStance.Attack)
            throw new ArgumentException("the Attack stance needs a target: call PetAttack(handle, target).", nameof(stance));
        var e = GetOrAdd(handle);
        e.IsPet = true;
        e.Owner = owner;
        Apply(e, stance, UnitId.None, null);
    }

    /// <summary>Stop treating the unit as a pet. A held pet gets its aggro back; otherwise it keeps what it is doing.</summary>
    internal void ClearPet(IUnitHandle handle)
    {
        if (!TryGet(handle, out var e) || !e.IsPet) return;
        Release(e);
        _entries.Remove(handle.Entity);
    }

    /// <summary>Change a pet's stance to Follow or Hold (Hold at the spot it stands on).</summary>
    internal void SetStance(IUnitHandle handle, PetStance stance)
    {
        var e = RequirePet(handle, "SetPetStance");
        if (!Enum.IsDefined(typeof(PetStance), stance))
            throw new ArgumentOutOfRangeException(nameof(stance), "pet stance " + (int)stance + " is not a defined PetStance.");
        if (stance == PetStance.Attack)
            throw new ArgumentException("the Attack stance needs a target: call PetAttack(handle, target).", nameof(stance));
        Apply(e, stance, UnitId.None, null);
    }

    /// <summary>Send a pet at <paramref name="target"/>; it returns to Follow when the target is dead or gone.</summary>
    internal void Attack(IUnitHandle handle, UnitId target)
    {
        var e = RequirePet(handle, "PetAttack");
        if (target.IsNone) throw new ArgumentException("PetAttack needs a target entity.", nameof(target));
        if (target == e.Owner) throw new ArgumentException("a pet cannot attack its owner.", nameof(target));
        if (target == handle.Entity) throw new ArgumentException("a pet cannot attack itself.", nameof(target));
        Apply(e, PetStance.Attack, target, null);
    }

    /// <summary>Hold the unit (pet or not) at <paramref name="position"/>.</summary>
    internal void HoldAt(IUnitHandle handle, UnitVec3 position)
    {
        RequireLive(handle, "HoldAt");
        Apply(GetOrAdd(handle), PetStance.Hold, UnitId.None, position);
    }

    /// <summary>
    /// Hold a unit where it stands (<paramref name="hold"/> true) or release it. Releasing a pet puts
    /// it back in Follow; releasing a unit that is not a pet forgets it.
    /// </summary>
    internal void Hold(IUnitHandle handle, bool hold)
    {
        if (hold)
        {
            RequireLive(handle, "Hold");
            var e = GetOrAdd(handle);
            // Already held: keep the position it was held at.
            if (e.Stance == PetStance.Hold && e.Pacified) return;
            Apply(e, PetStance.Hold, UnitId.None, null);
            return;
        }
        if (!TryGet(handle, out var entry)) return;
        if (entry.IsPet)
        {
            if (entry.Stance == PetStance.Hold) Apply(entry, PetStance.Follow, UnitId.None, null);
            return;
        }
        Release(entry);
        _entries.Remove(handle.Entity);
    }

    /// <summary>One 0.5 s step over every entry. Entries whose unit is disposed or gone are dropped.</summary>
    internal void Step(Action<UnitId, Exception>? onError = null)
    {
        if (_entries.Count == 0) return;
        var snapshot = new List<Entry>(_entries.Values);
        foreach (var e in snapshot)
        {
            // An earlier entry's step, or a callback it raised, may have replaced or removed this one.
            if (!IsCurrent(e)) continue;
            bool keep;
            try { keep = Tick(e); }
            catch (Exception ex)
            {
                onError?.Invoke(e.Unit.Entity, ex);
                keep = true;
            }
            if (!keep) RemoveIfCurrent(e);
        }
    }

    private bool IsCurrent(Entry e) =>
        _entries.TryGetValue(e.Unit.Entity, out var cur) && ReferenceEquals(cur, e);

    private void RemoveIfCurrent(Entry e)
    {
        if (IsCurrent(e)) _entries.Remove(e.Unit.Entity);
    }

    /// <summary>
    /// The <c>Active</c> value to save when Hold begins. A 0 read while a buff with
    /// <c>DisableAggroBuff</c> <c>TargetDontAttackOthers</c> is on the unit came from that buff (possibly
    /// an entrance buff, which may carry it), not from the unit, so 1 is saved instead: the unit must
    /// not stay passive for good once the buff ends.
    /// </summary>
    internal static bool? SaveRule(bool? read, bool blockingBuff) =>
        read == false && blockingBuff ? true : read;

    /// <summary>
    /// What release writes to <c>Active</c>, or null for nothing. Only a saved 1 is ever written, and
    /// only when <c>Active</c> still reads the 0 Hold wrote and no aggro-blocking buff owns it now. When
    /// something else has set <c>Active</c> since (Engage, a buff, the game's modification registry), or
    /// a blocking buff is on the unit, the game's current value is left.
    /// </summary>
    internal static bool? RestoreRule(bool? saved, bool? current, bool blockingBuff) =>
        saved == true && current == false && !blockingBuff ? true : null;

    /// <summary>Release every held unit (aggro written back) and forget everything. Unload calls this.</summary>
    internal void ReleaseAll()
    {
        foreach (var e in new List<Entry>(_entries.Values))
        {
            try { Release(e); }
            catch { /* the unit may already be gone */ }
        }
        _entries.Clear();
    }

    private Entry GetOrAdd(IUnitHandle handle)
    {
        if (!_entries.TryGetValue(handle.Entity, out var e) || !ReferenceEquals(e.Unit, handle))
        {
            if (e != null) Release(e);
            e = new Entry(handle) { Stance = PetStance.Follow };
            _entries[handle.Entity] = e;
        }
        return e;
    }

    private void Apply(Entry e, PetStance stance, UnitId target, UnitVec3? holdAt)
    {
        var rt = _runtime();
        if (stance != PetStance.Hold && e.Pacified) Release(e);
        if (stance == PetStance.Hold)
        {
            if (!e.Pacified)
            {
                e.SavedActive = SaveRule(rt.ReadAggroActive(e.Unit.Entity), rt.HasAggroBlockingBuff(e.Unit.Entity));
                e.Pacified = true;
            }
            e.HoldAt = holdAt ?? (rt.TryGetPosition(e.Unit.Entity, out var here) ? here : e.Unit.Position);
            e.GoToWait = 0;
        }
        e.Stance = stance;
        e.Target = stance == PetStance.Attack ? target : UnitId.None;
        if (!Tick(e)) RemoveIfCurrent(e);
    }

    private void Release(Entry e)
    {
        if (!e.Pacified) return;
        e.Pacified = false;
        var saved = e.SavedActive;
        e.SavedActive = null;
        if (e.Unit.IsDisposed || !e.Unit.Enabled) return;
        var rt = _runtime();
        var id = e.Unit.Entity;
        rt.ReleaseHold(id, RestoreRule(saved, rt.ReadAggroActive(id), rt.HasAggroBlockingBuff(id)));
    }

    /// <summary>One step for one entry; false when the entry should be dropped.</summary>
    private bool Tick(Entry e)
    {
        if (e.Unit.IsDisposed || !e.Unit.Enabled) return false;
        var rt = _runtime();
        var id = e.Unit.Entity;
        switch (e.Stance)
        {
            case PetStance.Hold:
                if (rt.HoldTick(id) == "gone") return false;
                if (e.GoToWait > 0) e.GoToWait--;
                if (e.GoToWait == 0 && rt.TryGetPosition(id, out var at) && HorizontalDistance(at, e.HoldAt) > HoldLeash)
                {
                    rt.WalkTo(id, e.HoldAt);
                    e.GoToWait = GoToRetrySteps;
                }
                return true;

            case PetStance.Attack:
                string attack = rt.AttackTick(id, e.Target);
                if (attack == "gone") return false;
                if (attack != "done") return true;
                e.Stance = PetStance.Follow;
                e.Target = UnitId.None;
                return rt.PetTick(id, e.Owner) != "gone";

            default:
                return rt.PetTick(id, e.Owner) != "gone";
        }
    }

    private Entry RequirePet(IUnitHandle handle, string op)
    {
        RequireLive(handle, op);
        if (!TryGet(handle, out var e) || !e.IsPet)
            throw new InvalidOperationException("Units: " + op + " needs a pet; call SetPet first.");
        return e;
    }

    private static void RequireLive(IUnitHandle handle, string op)
    {
        if (handle == null) throw new ArgumentNullException(nameof(handle));
        if (!handle.Enabled || handle.IsDisposed) throw new InvalidOperationException("Units: " + op + " needs a live handle.");
    }

    internal static float HorizontalDistance(UnitVec3 a, UnitVec3 b)
    {
        float dx = a.X - b.X, dz = a.Z - b.Z;
        return (float)Math.Sqrt(dx * dx + dz * dz);
    }
}
