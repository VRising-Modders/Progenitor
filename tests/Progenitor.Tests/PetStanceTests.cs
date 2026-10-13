using System;
using System.Collections.Generic;
using System.Linq;
using Progenitor.Units;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// ctx.Units pet stances and hold, game-free half: <see cref="PetStanceBook"/> transitions and
/// bookkeeping over a fake <see cref="IUnitStanceRuntime"/>, and the new spawn-option validation
/// (<see cref="UnitEntrance"/>, <see cref="UnitSpawnOptions.Permanent"/>, <see cref="UnitSpawnOptions.DropLoot"/>).
/// The live half (<c>UnityUnitRuntime.Stance.cs</c>) makes the aggro, lifetime and drop writes.
/// </summary>
public class PetStanceTests
{
    private static readonly UnitId Owner = new(900, 1);
    private static readonly UnitId Dracula = new(700, 1);

    // ---- fakes ------------------------------------------------------------------------------------

    private sealed class FakeRuntime : IUnitStanceRuntime
    {
        public readonly List<string> Calls = new();
        public readonly Dictionary<UnitId, UnitVec3> Positions = new();
        public readonly Dictionary<UnitId, bool> Active = new();
        public readonly HashSet<UnitId> Gone = new();
        public readonly HashSet<UnitId> DeadTargets = new();
        public readonly HashSet<UnitId> Blocking = new();
        public readonly List<(UnitId Unit, UnitVec3 At)> Walks = new();
        public Action<UnitId>? OnHoldTick;

        public string PetTick(UnitId pet, UnitId owner)
        {
            Calls.Add("follow " + pet + " " + owner);
            return Gone.Contains(pet) ? "gone" : "follow";
        }

        public bool TryGetPosition(UnitId unit, out UnitVec3 position) => Positions.TryGetValue(unit, out position);

        public bool? ReadAggroActive(UnitId unit) => Active.TryGetValue(unit, out bool a) ? a : null;

        public string HoldTick(UnitId unit)
        {
            Calls.Add("hold " + unit);
            OnHoldTick?.Invoke(unit);
            if (Gone.Contains(unit)) return "gone";
            if (Active.ContainsKey(unit)) Active[unit] = false;
            return "hold";
        }

        public void ReleaseHold(UnitId unit, bool? active)
        {
            Calls.Add("release " + unit + " " + (active?.ToString() ?? "null"));
            if (active is bool a) Active[unit] = a;
        }

        public bool HasAggroBlockingBuff(UnitId unit) => Blocking.Contains(unit);

        public bool WalkTo(UnitId unit, UnitVec3 position)
        {
            Walks.Add((unit, position));
            return true;
        }

        public string AttackTick(UnitId unit, UnitId target)
        {
            Calls.Add("attack " + unit + " " + target);
            if (Gone.Contains(unit)) return "gone";
            return DeadTargets.Contains(target) ? "done" : "combat";
        }

        public int Count(string prefix) => Calls.Count(c => c.StartsWith(prefix, StringComparison.Ordinal));
    }

    private sealed class FakeHandle : IUnitHandle
    {
        public FakeHandle(int index) => Entity = new UnitId(index, 1);
        public readonly List<UnitVec3> GoTos = new();
        public string Prefab => "CHAR_Test";
        public string PluginId => "test";
        public UnitId Entity { get; }
        public bool Enabled { get; set; } = true;
        public bool IsDisposed { get; set; }
        public UnitHandleState State => UnitHandleState.Spawned;
        public UnitVec3 Position => default;
        public IUnitBehaviour Behaviour => null!;
        public UnitEngageResult Engage(UnitId target) => default;

        public UnitStatsWriteResult TrySetStats(UnitStatOverrides stats) => default;
        public bool TrySetLifetime(float seconds) => false;
        public bool TrySetTeam(int? teamValue = null, int? factionIndex = null) => false;
        public UnitStripVBloodResult TryStripVBlood() => default;

        public void Follow(UnitId target) { }
        public void GoTo(UnitVec3 position) => GoTos.Add(position);
        public void Despawn() => IsDisposed = true;
        public void Dispose() => IsDisposed = true;
    }

    private static (PetStanceBook Book, FakeRuntime Rt, FakeHandle Pet) NewPet(bool active = true)
    {
        var rt = new FakeRuntime();
        var pet = new FakeHandle(10);
        rt.Positions[pet.Entity] = new UnitVec3(5, 0, 5);
        rt.Active[pet.Entity] = active;
        var book = new PetStanceBook(() => rt);
        return (book, rt, pet);
    }

    // ---- enums ------------------------------------------------------------------------------------

    [Fact]
    public void Stance_values_are_pinned()
    {
        Assert.Equal(0, (int)PetStance.Follow);
        Assert.Equal(1, (int)PetStance.Hold);
        Assert.Equal(2, (int)PetStance.Attack);
        Assert.Equal(default, PetStance.Follow);
    }

    // ---- Follow -----------------------------------------------------------------------------------

    [Fact]
    public void SetPet_defaults_to_Follow_and_steps_at_once()
    {
        var (book, rt, pet) = NewPet();
        book.SetPet(pet, Owner, PetStance.Follow);

        Assert.True(book.IsPet(pet));
        Assert.Equal(PetStance.Follow, book.StanceOf(pet));
        Assert.False(book.IsHeld(pet));
        Assert.Equal(new[] { "follow 10:1 900:1" }, rt.Calls);

        book.Step();
        Assert.Equal(2, rt.Count("follow "));
        Assert.Equal(0, rt.Count("hold "));
    }

    [Fact]
    public void SetPet_refuses_Attack_and_undefined_stances_and_dead_handles()
    {
        var (book, _, pet) = NewPet();
        Assert.Throws<ArgumentException>(() => book.SetPet(pet, Owner, PetStance.Attack));
        Assert.Throws<ArgumentOutOfRangeException>(() => book.SetPet(pet, Owner, (PetStance)9));
        Assert.Equal(0, book.Count);

        pet.IsDisposed = true;
        Assert.Throws<InvalidOperationException>(() => book.SetPet(pet, Owner, PetStance.Follow));
        var disabled = new FakeHandle(11) { Enabled = false };
        Assert.Throws<InvalidOperationException>(() => book.Hold(disabled, true));
    }

    // ---- Hold -------------------------------------------------------------------------------------

    [Fact]
    public void Hold_saves_Active_quiets_the_unit_and_restores_on_Follow()
    {
        var (book, rt, pet) = NewPet(active: true);
        book.SetPet(pet, Owner, PetStance.Hold);

        Assert.True(book.IsHeld(pet));
        Assert.Equal(PetStance.Hold, book.StanceOf(pet));
        Assert.False(rt.Active[pet.Entity]);
        Assert.Equal(0, rt.Count("follow "));
        Assert.True(book.TryGet(pet, out var entry));
        Assert.Equal(new UnitVec3(5, 0, 5), entry.HoldAt);
        Assert.True(entry.Pacified);
        Assert.Equal(true, entry.SavedActive);

        book.SetStance(pet, PetStance.Follow);
        Assert.Contains("release 10:1 True", rt.Calls);
        Assert.True(rt.Active[pet.Entity]);
        Assert.False(book.IsHeld(pet));
        Assert.False(entry.Pacified);
        Assert.Equal("follow 10:1 900:1", rt.Calls.Last());
    }

    [Fact]
    public void A_unit_that_was_already_off_is_left_off_without_a_write()
    {
        var (book, rt, pet) = NewPet(active: false);
        book.Hold(pet, true);
        book.Hold(pet, false);
        Assert.Contains("release 10:1 null", rt.Calls);
        Assert.False(rt.Active[pet.Entity]);
        Assert.Equal(0, book.Count);
    }

    // ---- save and restore rules -------------------------------------------------------------------

    [Theory]
    [InlineData(null, false, null)]
    [InlineData(null, true, null)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    [InlineData(false, false, false)]
    [InlineData(false, true, true)] // the 0 came from a blocking buff (an entrance buff may carry one)
    public void SaveRule(bool? read, bool blocking, bool? saved) =>
        Assert.Equal(saved, PetStanceBook.SaveRule(read, blocking));

    [Theory]
    [InlineData(true, false, false, true)]   // still the 0 Hold wrote: write 1 back
    [InlineData(true, true, false, null)]    // something set it since (Engage, a buff): leave it
    [InlineData(true, null, false, null)]    // no AggroConsumer now
    [InlineData(true, false, true, null)]    // a blocking buff owns Active now
    [InlineData(false, false, false, null)]  // saved 0: nothing to write
    [InlineData(null, false, false, null)]   // nothing saved
    public void RestoreRule(bool? saved, bool? current, bool blocking, bool? write) =>
        Assert.Equal(write, PetStanceBook.RestoreRule(saved, current, blocking));

    [Fact]
    public void Release_leaves_Active_alone_when_something_set_it_since()
    {
        var (book, rt, pet) = NewPet(active: true);
        book.Hold(pet, true);
        rt.Active[pet.Entity] = true; // Engage or a buff turned it back on
        book.Hold(pet, false);
        Assert.Contains("release 10:1 null", rt.Calls);
    }

    [Fact]
    public void Release_skips_the_write_while_a_blocking_buff_is_on_the_unit()
    {
        var (book, rt, pet) = NewPet(active: true);
        book.Hold(pet, true);
        rt.Blocking.Add(pet.Entity);
        book.Hold(pet, false);
        Assert.Contains("release 10:1 null", rt.Calls);
        Assert.False(rt.Active[pet.Entity]);
    }

    [Fact]
    public void A_hold_that_starts_under_a_blocking_buff_saves_1()
    {
        // Held right after an entrance buff that turned Active off: once the buff is gone, release
        // must turn aggro back on rather than leave the unit passive for good.
        var (book, rt, pet) = NewPet(active: false);
        rt.Blocking.Add(pet.Entity);
        book.Hold(pet, true);
        Assert.True(book.TryGet(pet, out var entry));
        Assert.Equal(true, entry.SavedActive);

        rt.Blocking.Clear();
        book.Hold(pet, false);
        Assert.Contains("release 10:1 True", rt.Calls);
        Assert.True(rt.Active[pet.Entity]);
    }

    // ---- Step re-check ----------------------------------------------------------------------------

    [Fact]
    public void Step_skips_an_entry_replaced_by_an_earlier_step()
    {
        var rt = new FakeRuntime();
        var book = new PetStanceBook(() => rt);
        var a = new FakeHandle(1);
        var b = new FakeHandle(2);
        var b2 = new FakeHandle(2); // a new handle for the same entity id
        foreach (var h in new[] { a, b }) { rt.Positions[h.Entity] = default; rt.Active[h.Entity] = true; }
        book.Hold(a, true);
        book.Hold(b, true);

        // A's step replaces B's entry with b2 (held, then gone at its first step inside that call).
        bool replaced = false;
        rt.OnHoldTick = id =>
        {
            if (id != a.Entity || replaced) return;
            replaced = true;
            book.Hold(b2, true);
        };
        rt.Calls.Clear();
        book.Step();

        // The old B entry was not ticked after it was replaced (only b2's own first step ran), and
        // b2's entry was not removed by the stale one.
        Assert.Equal(2, rt.Count("hold 2:1") + rt.Count("hold 1:1"));
        Assert.True(book.TryGet(b2, out var cur));
        Assert.Same(b2, cur.Unit);
    }

    [Fact]
    public void Step_does_not_remove_a_new_entry_when_the_stale_one_reports_gone()
    {
        var rt = new FakeRuntime();
        var book = new PetStanceBook(() => rt);
        var a = new FakeHandle(1);
        var b = new FakeHandle(2);
        foreach (var h in new[] { a, b }) { rt.Positions[h.Entity] = default; rt.Active[h.Entity] = true; }
        book.Hold(a, true);
        book.Hold(b, true);

        // A's own step replaces A's entry with a new handle and then reports A gone.
        var a2 = new FakeHandle(1);
        rt.OnHoldTick = id =>
        {
            if (id != a.Entity) return;
            rt.OnHoldTick = null;
            book.Hold(a2, true);
            rt.Gone.Add(a.Entity); // the outer (stale) step now reports "gone"
        };
        book.Step();

        // a2's first step (inside Hold) already ran before Gone was set; the stale entry's "gone"
        // must not remove it.
        Assert.True(book.TryGet(a2, out var cur));
        Assert.Same(a2, cur.Unit);
    }

    [Fact]
    public void Hold_on_a_unit_without_AggroConsumer_restores_nothing()
    {
        var (book, rt, pet) = NewPet();
        rt.Active.Remove(pet.Entity);
        book.Hold(pet, true);
        book.Hold(pet, false);
        Assert.Contains("release 10:1 null", rt.Calls);
        Assert.False(rt.Active.ContainsKey(pet.Entity));
    }

    [Fact]
    public void A_second_Hold_keeps_the_first_position_and_saved_value()
    {
        var (book, rt, pet) = NewPet(active: true);
        book.Hold(pet, true);
        rt.Positions[pet.Entity] = new UnitVec3(6, 0, 5);
        book.Hold(pet, true);

        Assert.True(book.TryGet(pet, out var entry));
        Assert.Equal(new UnitVec3(5, 0, 5), entry.HoldAt);
        Assert.Equal(true, entry.SavedActive);
    }

    [Fact]
    public void A_held_unit_pushed_away_is_walked_back_once_per_retry_window()
    {
        var (book, rt, pet) = NewPet();
        book.HoldAt(pet, new UnitVec3(0, 0, 0));

        // Spawned 7 m away: the first step (inside HoldAt) sends it back.
        Assert.Single(rt.Walks);
        Assert.Equal((pet.Entity, new UnitVec3(0, 0, 0)), rt.Walks[0]);
        Assert.Empty(pet.GoTos); // never the handle path: no ticket, state or events

        // Still away: no new GoTo until the retry window has passed.
        for (int i = 0; i < PetStanceBook.GoToRetrySteps - 1; i++) book.Step();
        Assert.Single(rt.Walks);
        book.Step();
        Assert.Equal(2, rt.Walks.Count);

        // Inside the leash: nothing is sent.
        rt.Positions[pet.Entity] = new UnitVec3(1.5f, 10f, 0);
        for (int i = 0; i < PetStanceBook.GoToRetrySteps * 2; i++) book.Step();
        Assert.Equal(2, rt.Walks.Count);
    }

    [Fact]
    public void HoldAt_on_a_pet_sets_the_Hold_stance()
    {
        var (book, _, pet) = NewPet();
        book.SetPet(pet, Owner, PetStance.Follow);
        book.HoldAt(pet, new UnitVec3(1, 2, 3));
        Assert.Equal(PetStance.Hold, book.StanceOf(pet));
        Assert.True(book.TryGet(pet, out var entry));
        Assert.Equal(new UnitVec3(1, 2, 3), entry.HoldAt);
    }

    [Fact]
    public void A_held_unit_that_is_not_a_pet_is_forgotten_on_release()
    {
        var (book, rt, boss) = NewPet();
        book.Hold(boss, true);
        Assert.True(book.IsHeld(boss));
        Assert.False(book.IsPet(boss));
        Assert.Null(book.StanceOf(boss));

        book.Step();
        Assert.Equal(0, rt.Count("follow "));
        Assert.Equal(2, rt.Count("hold "));

        book.Hold(boss, false);
        Assert.Equal(0, book.Count);
        Assert.Contains("release 10:1 True", rt.Calls);
        // Releasing again, or a unit never held, does nothing.
        book.Hold(boss, false);
        Assert.Equal(1, rt.Count("release "));
    }

    [Fact]
    public void Releasing_a_held_pet_puts_it_back_in_Follow()
    {
        var (book, rt, pet) = NewPet();
        book.SetPet(pet, Owner, PetStance.Hold);
        book.Hold(pet, false);
        Assert.True(book.IsPet(pet));
        Assert.Equal(PetStance.Follow, book.StanceOf(pet));
        Assert.Equal(1, rt.Count("release "));
    }

    [Fact]
    public void SetStance_and_PetAttack_need_a_pet()
    {
        var (book, _, boss) = NewPet();
        Assert.Throws<InvalidOperationException>(() => book.SetStance(boss, PetStance.Hold));
        Assert.Throws<InvalidOperationException>(() => book.Attack(boss, Dracula));
        book.Hold(boss, true);
        Assert.Throws<InvalidOperationException>(() => book.SetStance(boss, PetStance.Follow));
    }

    // ---- Attack -----------------------------------------------------------------------------------

    [Fact]
    public void Attack_fights_until_the_target_is_dead_then_follows()
    {
        var (book, rt, pet) = NewPet();
        book.SetPet(pet, Owner, PetStance.Hold);
        rt.Calls.Clear();

        book.Attack(pet, Dracula);
        // Leaving Hold writes Active back before the first attack step.
        Assert.Equal(new[] { "release 10:1 True", "attack 10:1 700:1" }, rt.Calls);
        Assert.Equal(PetStance.Attack, book.StanceOf(pet));
        Assert.False(book.IsHeld(pet));

        book.Step();
        Assert.Equal(2, rt.Count("attack "));

        rt.DeadTargets.Add(Dracula);
        book.Step();
        Assert.Equal(PetStance.Follow, book.StanceOf(pet));
        Assert.Equal("follow 10:1 900:1", rt.Calls.Last());
        Assert.True(book.TryGet(pet, out var entry));
        Assert.True(entry.Target.IsNone);

        book.Step();
        Assert.Equal(3, rt.Count("attack "));
        Assert.Equal(2, rt.Count("follow "));
    }

    [Fact]
    public void Attack_refuses_no_target_the_owner_and_itself()
    {
        var (book, _, pet) = NewPet();
        book.SetPet(pet, Owner, PetStance.Follow);
        Assert.Throws<ArgumentException>(() => book.Attack(pet, UnitId.None));
        Assert.Throws<ArgumentException>(() => book.Attack(pet, Owner));
        Assert.Throws<ArgumentException>(() => book.Attack(pet, pet.Entity));
        Assert.Equal(PetStance.Follow, book.StanceOf(pet));
    }

    [Fact]
    public void Hold_after_Attack_stops_the_attack()
    {
        var (book, rt, pet) = NewPet();
        book.SetPet(pet, Owner, PetStance.Follow);
        book.Attack(pet, Dracula);
        book.SetStance(pet, PetStance.Hold);
        rt.Calls.Clear();
        book.Step();
        Assert.Equal(new[] { "hold 10:1" }, rt.Calls);
        Assert.True(book.TryGet(pet, out var entry));
        Assert.True(entry.Target.IsNone);
    }

    // ---- bookkeeping ------------------------------------------------------------------------------

    [Fact]
    public void ClearPet_releases_a_held_pet_and_forgets_it()
    {
        var (book, rt, pet) = NewPet();
        book.SetPet(pet, Owner, PetStance.Hold);
        book.ClearPet(pet);
        Assert.Equal(0, book.Count);
        Assert.False(book.IsPet(pet));
        Assert.Contains("release 10:1 True", rt.Calls);
    }

    [Fact]
    public void ClearPet_of_an_attacking_pet_writes_nothing_back()
    {
        var (book, rt, pet) = NewPet();
        book.SetPet(pet, Owner, PetStance.Follow);
        book.Attack(pet, Dracula);
        book.ClearPet(pet);
        Assert.Equal(0, rt.Count("release "));
        Assert.Equal(0, book.Count);
    }

    [Fact]
    public void Disposed_and_gone_units_drop_out_without_a_release()
    {
        var rt = new FakeRuntime();
        var book = new PetStanceBook(() => rt);
        var a = new FakeHandle(1);
        var b = new FakeHandle(2);
        var c = new FakeHandle(3);
        foreach (var h in new[] { a, b, c }) { rt.Positions[h.Entity] = default; rt.Active[h.Entity] = true; }
        book.SetPet(a, Owner, PetStance.Follow);
        book.Hold(b, true);
        book.SetPet(c, Owner, PetStance.Hold);
        Assert.Equal(3, book.Count);

        a.IsDisposed = true;
        rt.Gone.Add(b.Entity);
        book.Step();
        Assert.Equal(1, book.Count);
        Assert.True(book.IsPet(c));
        Assert.Equal(0, rt.Count("release "));
    }

    [Fact]
    public void ReleaseAll_writes_back_every_held_unit_and_forgets_all()
    {
        var rt = new FakeRuntime();
        var book = new PetStanceBook(() => rt);
        var held = new FakeHandle(1);
        var follower = new FakeHandle(2);
        var disposed = new FakeHandle(3);
        foreach (var h in new[] { held, follower, disposed }) { rt.Positions[h.Entity] = default; rt.Active[h.Entity] = true; }
        book.Hold(held, true);
        book.SetPet(follower, Owner, PetStance.Follow);
        book.Hold(disposed, true);
        disposed.IsDisposed = true;

        book.ReleaseAll();
        Assert.Equal(0, book.Count);
        Assert.Equal(new[] { "release 1:1 True" }, rt.Calls.Where(c => c.StartsWith("release", StringComparison.Ordinal)));
    }

    [Fact]
    public void A_throwing_step_is_reported_and_the_entry_kept()
    {
        var (book, _, pet) = NewPet();
        var throwing = new ThrowingRuntime();
        var book2 = new PetStanceBook(() => throwing);
        throwing.Throw = false;
        book2.SetPet(pet, Owner, PetStance.Follow);
        throwing.Throw = true;
        var errors = new List<UnitId>();
        book2.Step((id, _) => errors.Add(id));
        Assert.Equal(new[] { pet.Entity }, errors);
        Assert.Equal(1, book2.Count);
        Assert.Equal(0, book.Count);
    }

    private sealed class ThrowingRuntime : IUnitStanceRuntime
    {
        public bool Throw;
        public string PetTick(UnitId pet, UnitId owner) => Throw ? throw new InvalidOperationException("boom") : "follow";
        public bool TryGetPosition(UnitId unit, out UnitVec3 position) { position = default; return true; }
        public bool? ReadAggroActive(UnitId unit) => true;
        public string HoldTick(UnitId unit) => "hold";
        public void ReleaseHold(UnitId unit, bool? active) { }
        public string AttackTick(UnitId unit, UnitId target) => "combat";
        public bool HasAggroBlockingBuff(UnitId unit) => false;
        public bool WalkTo(UnitId unit, UnitVec3 position) => true;
    }

    // ---- spawn options ----------------------------------------------------------------------------

    [Fact]
    public void Spawn_option_defaults_change_nothing()
    {
        var options = new UnitSpawnOptions();
        Assert.False(options.Permanent);
        Assert.True(options.DropLoot);
        Assert.Equal(UnitEntrance.None, options.Entrance);
        Assert.Null(options.ExtrasRefusal());
    }

    [Fact]
    public void Entrance_values_are_pinned_and_map_to_the_verified_buff()
    {
        Assert.Equal(0, (int)UnitEntrance.None);
        Assert.Equal(1, (int)UnitEntrance.RiseFromGround);
        Assert.False(UnitEntranceTable.TryGetBuff(UnitEntrance.None, out _));
        Assert.True(UnitEntranceTable.TryGetBuff(UnitEntrance.RiseFromGround, out int guid));
        Assert.Equal(1820474368, guid);
        Assert.Equal(Enum.GetValues(typeof(UnitEntrance)).Length - 1, UnitEntranceTable.Rows.Length);
        foreach (UnitEntrance e in Enum.GetValues(typeof(UnitEntrance)))
        {
            Assert.True(UnitEntranceTable.IsDefined(e));
            Assert.Null(new UnitSpawnOptions { Entrance = e }.ExtrasRefusal());
        }
    }

    [Fact]
    public void An_undefined_entrance_is_refused_with_its_value()
    {
        var options = new UnitSpawnOptions { Entrance = (UnitEntrance)7 };
        string? refusal = options.ExtrasRefusal();
        Assert.NotNull(refusal);
        Assert.Contains("7", refusal);
        Assert.Throws<ArgumentOutOfRangeException>(() => UnitEntranceTable.Validate((UnitEntrance)7, "entrance"));
    }

    [Fact]
    public void Layout_facts_for_hold_and_Permanent_are_the_dump_values()
    {
        Assert.Equal(0x30, Layout.AggroConsumerAggroTargetOffset);
        Assert.Equal(0x3C, Layout.AggroConsumerAlertTargetOffset);
        Assert.Equal(0x50, Layout.AggroConsumerActiveOffset);
        Assert.Equal(0x8, Layout.NetworkedEntityWaitingForSyncOffset);
        Assert.Equal(0x0, Layout.LifeTimeDurationOffset);
        Assert.Equal(0x4, Layout.LifeTimeEndActionOffset);
        Assert.Equal(0, Layout.LifeTimeEndActionNone);
        Assert.Equal(4, Layout.GenericEnemyStateAlert);
        Assert.Equal(64, Layout.GenericEnemyStateReturn);
    }
}
