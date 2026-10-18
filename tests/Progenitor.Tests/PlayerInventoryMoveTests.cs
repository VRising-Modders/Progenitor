using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Progenitor.Dev.Ops;
using Progenitor.Inventory;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// ctx.Inventory.OnPlayerMove: the before/after diff of one inventory, the attribution of an
/// update's requests to containers, and the delivery hub. The hooks and the raw reads
/// (PlayerMoveRuntime) are not compiled here.
/// </summary>
public class PlayerInventoryMoveTests
{
    const int Ore = 100, Wood = 200, Sword = 300;
    static readonly DateTime T0 = new(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);

    static MoveSlotRow R(int item, int amount, long entity = 0) => new(item, amount, entity);
    static MoveSlotRow Empty => default;

    static List<ItemMoveDelta> Diff(MoveSlotRow[] before, MoveSlotRow[] after)
    {
        var into = new List<ItemMoveDelta>();
        new PlayerMoveDiffer().Diff(before, after, into);
        return into;
    }

    // ---- diff -----------------------------------------------------------------------------------

    [Fact]
    public void Stack_deposited_into_a_chest_is_in_by_the_amount_that_arrived()
    {
        var d = Diff(new[] { R(Ore, 40), Empty }, new[] { R(Ore, 99), R(Ore, 11) }).Single();
        Assert.Equal(Ore, d.Item);
        Assert.Equal(70, d.In);
        Assert.Equal(0, d.Out);
        Assert.Equal(0, d.Within);
        Assert.Equal(40, d.Before);
        Assert.Equal(110, d.After);
    }

    [Fact]
    public void Partial_take_reports_only_what_left()
    {
        // The player asked for the whole stack; the bag took 30.
        var d = Diff(new[] { R(Ore, 50) }, new[] { R(Ore, 20) }).Single();
        Assert.Equal(30, d.Out);
        Assert.Equal(0, d.In);
    }

    [Fact]
    public void Refused_request_leaves_no_delta()
    {
        Assert.Empty(Diff(new[] { R(Ore, 50), R(Wood, 3) }, new[] { R(Ore, 50), R(Wood, 3) }));
    }

    [Fact]
    public void Sort_inside_one_container_is_within_with_the_relocated_units()
    {
        var deltas = Diff(new[] { R(Wood, 3), Empty, R(Ore, 5) }, new[] { R(Ore, 5), R(Wood, 3), Empty });
        Assert.Equal(2, deltas.Count);
        Assert.All(deltas, d => { Assert.Equal(0, d.In); Assert.Equal(0, d.Out); });
        Assert.Equal(3, deltas.Single(d => d.Item == Wood).Within);
        Assert.Equal(5, deltas.Single(d => d.Item == Ore).Within);
    }

    [Fact]
    public void Split_inside_a_container_is_within_by_the_split_amount()
    {
        var d = Diff(new[] { R(Ore, 10), Empty }, new[] { R(Ore, 6), R(Ore, 4) }).Single();
        Assert.Equal(4, d.Within);
        Assert.Equal(10, d.Before);
        Assert.Equal(10, d.After);
    }

    [Fact]
    public void Merge_of_two_own_stacks_is_within_by_the_units_that_moved()
    {
        var d = Diff(new[] { R(Ore, 5), R(Ore, 3) }, new[] { R(Ore, 8), Empty }).Single();
        Assert.Equal(3, d.Within);
    }

    [Fact]
    public void Swapping_one_sword_for_another_of_the_same_prefab_is_one_out_and_one_in()
    {
        var d = Diff(new[] { R(Sword, 1, entity: 0x0000_0010_0000_0001) }, new[] { R(Sword, 1, entity: 0x0000_0020_0000_0001) }).Single();
        Assert.Equal(1, d.In);
        Assert.Equal(1, d.Out);
        Assert.Equal(0, d.Within);
    }

    [Fact]
    public void Gear_moved_between_slots_keeps_its_entity_and_is_within()
    {
        long e = 0x0000_0010_0000_0001;
        var d = Diff(new[] { R(Sword, 1, e), Empty }, new[] { Empty, R(Sword, 1, e) }).Single();
        Assert.Equal(0, d.In);
        Assert.Equal(0, d.Out);
        Assert.Equal(1, d.Within);
    }

    [Fact]
    public void Gear_leaving_and_stack_arriving_in_one_update_are_both_reported()
    {
        long e = 0x0000_0010_0000_0001;
        var deltas = Diff(new[] { R(Sword, 1, e), R(Ore, 4) }, new[] { R(Ore, 9), Empty });
        Assert.Equal(1, deltas.Single(d => d.Item == Sword).Out);
        Assert.Equal(5, deltas.Single(d => d.Item == Ore).In);
    }

    [Fact]
    public void A_buffer_that_grew_or_shrank_compares_missing_slots_as_empty()
    {
        var grew = Diff(new[] { R(Ore, 5) }, new[] { R(Ore, 5), R(Wood, 2) }).Single();
        Assert.Equal(Wood, grew.Item);
        Assert.Equal(2, grew.In);
        var shrank = Diff(new[] { R(Ore, 5), R(Wood, 2) }, new[] { R(Ore, 5) }).Single();
        Assert.Equal(2, shrank.Out);
    }

    [Fact]
    public void Differ_reuses_its_scratch_between_calls()
    {
        var differ = new PlayerMoveDiffer();
        var into = new List<ItemMoveDelta>();
        differ.Diff(new[] { R(Ore, 5) }, new[] { R(Ore, 9) }, into);
        Assert.Equal(4, into.Single().In);
        differ.Diff(new[] { R(Wood, 5) }, new[] { R(Wood, 1) }, into);
        var d = into.Single();
        Assert.Equal(Wood, d.Item);
        Assert.Equal(4, d.Out);
        Assert.Equal(5, d.Before);
    }

    // ---- attribution ----------------------------------------------------------------------------

    static readonly InventoryRef Chest = new(10, 1, 0, 7);
    static readonly InventoryRef Chest2 = new(11, 1, 0, 7);
    static readonly InventoryRef StationIn = new(12, 1, 0, 7);
    static readonly InventoryRef StationOut = new(12, 1, 1, 7);
    const long UserA = (5L << 32) | 1, CharA = (6L << 32) | 1, UserB = (8L << 32) | 1, CharB = (9L << 32) | 1;

    static List<PendingPlayerMove> Run(PlayerMoveBatch batch)
    {
        var into = new List<PendingPlayerMove>();
        batch.Emit(T0, into);
        return into;
    }

    [Fact]
    public void Drag_from_bag_into_chest_is_in_with_the_bag_as_counterpart()
    {
        var b = new PlayerMoveBatch();
        b.Begin(this, PlayerInventoryMoveKind.Move);
        int c = b.AddInventory(1000, Chest);
        Assert.True(b.AddRequest(UserA, CharA, MoveSideRole.OwnPlayer, null, MoveSideRole.Inventory, new[] { c }));
        b.SetRows(c, after: false, new[] { Empty, Empty });
        b.SetRows(c, after: true, new[] { R(Ore, 25), Empty });
        var m = Run(b).Single();
        Assert.Equal(UserA, m.User);
        Assert.Equal(CharA, m.Character);
        Assert.Equal(PlayerInventoryMoveKind.Move, m.Kind);
        Assert.Equal(Chest, m.Inventory);
        Assert.Equal(PlayerInventoryMoveDirection.In, m.Direction);
        Assert.Equal(Ore, m.Item);
        Assert.Equal(25, m.Amount);
        Assert.Equal(0, m.Before);
        Assert.Equal(25, m.After);
        Assert.Equal(PlayerInventoryCounterpart.OwnInventory, m.Counterpart);
        Assert.False(m.Ambiguous);
        Assert.Equal(1, m.Requests);
        Assert.False(b.Open);
    }

    [Fact]
    public void Take_all_reports_one_out_per_item()
    {
        var b = new PlayerMoveBatch();
        b.Begin(this, PlayerInventoryMoveKind.MoveAll);
        int c = b.AddInventory(1000, Chest);
        b.AddRequest(UserA, CharA, MoveSideRole.Inventory, new[] { c }, MoveSideRole.OwnPlayer, null);
        b.SetRows(c, false, new[] { R(Ore, 50), R(Wood, 10) });
        b.SetRows(c, true, new[] { R(Ore, 20), Empty });
        var moves = Run(b);
        Assert.Equal(2, moves.Count);
        Assert.All(moves, m => Assert.Equal(PlayerInventoryMoveDirection.Out, m.Direction));
        Assert.Equal(30, moves.Single(m => m.Item == Ore).Amount);
        Assert.Equal(10, moves.Single(m => m.Item == Wood).Amount);
        Assert.All(moves, m => Assert.Equal(PlayerInventoryCounterpart.OwnInventory, m.Counterpart));
    }

    [Fact]
    public void Refused_request_raises_nothing_and_counts_unchanged()
    {
        var b = new PlayerMoveBatch();
        b.Begin(this, PlayerInventoryMoveKind.Move);
        int c = b.AddInventory(1000, Chest);
        b.AddRequest(UserA, CharA, MoveSideRole.Inventory, new[] { c }, MoveSideRole.OwnPlayer, null);
        b.SetRows(c, false, new[] { R(Ore, 50) });
        b.SetRows(c, true, new[] { R(Ore, 50) });
        Assert.Empty(Run(b));
        Assert.Equal(1, b.Unchanged);
    }

    [Fact]
    public void Two_users_on_one_chest_in_one_update_are_ambiguous_with_the_first_user()
    {
        var b = new PlayerMoveBatch();
        b.Begin(this, PlayerInventoryMoveKind.Move);
        int c = b.AddInventory(1000, Chest);
        Assert.Equal(c, b.AddInventory(1000, Chest));
        b.AddRequest(UserA, CharA, MoveSideRole.OwnPlayer, null, MoveSideRole.Inventory, new[] { c });
        b.AddRequest(UserB, CharB, MoveSideRole.OwnPlayer, null, MoveSideRole.Inventory, new[] { c });
        b.SetRows(c, false, new[] { R(Ore, 10) });
        b.SetRows(c, true, new[] { R(Ore, 30) });
        var m = Run(b).Single();
        Assert.True(m.Ambiguous);
        Assert.Equal(UserA, m.User);
        Assert.Equal(2, m.Requests);
        Assert.Equal(20, m.Amount);
    }

    [Fact]
    public void Two_fast_clicks_of_one_user_are_one_move_with_two_requests_not_ambiguous()
    {
        var b = new PlayerMoveBatch();
        b.Begin(this, PlayerInventoryMoveKind.Move);
        int c = b.AddInventory(1000, Chest);
        b.AddRequest(UserA, CharA, MoveSideRole.Inventory, new[] { c }, MoveSideRole.OwnPlayer, null);
        b.AddRequest(UserA, CharA, MoveSideRole.Inventory, new[] { c }, MoveSideRole.OwnPlayer, null);
        b.SetRows(c, false, new[] { R(Ore, 10), R(Wood, 5) });
        b.SetRows(c, true, new[] { Empty, Empty });
        var moves = Run(b);
        Assert.Equal(2, moves.Count);
        Assert.All(moves, m => { Assert.False(m.Ambiguous); Assert.Equal(2, m.Requests); });
    }

    [Fact]
    public void Requests_with_different_other_sides_on_one_chest_give_an_unknown_counterpart()
    {
        // Chest -> bag (refused) and chest -> chest 2 in one update: which one moved is not known.
        var b = new PlayerMoveBatch();
        b.Begin(this, PlayerInventoryMoveKind.Move);
        int c = b.AddInventory(1000, Chest);
        int c2 = b.AddInventory(1001, Chest2);
        b.AddRequest(UserA, CharA, MoveSideRole.Inventory, new[] { c }, MoveSideRole.OwnPlayer, null);
        b.AddRequest(UserA, CharA, MoveSideRole.Inventory, new[] { c }, MoveSideRole.Inventory, new[] { c2 });
        b.SetRows(c, false, new[] { R(Ore, 4) });
        b.SetRows(c, true, new[] { Empty });
        b.SetRows(c2, false, new[] { Empty });
        b.SetRows(c2, true, new[] { R(Ore, 4) });
        var moves = Run(b);
        var outOf = moves.Single(m => m.Inventory == Chest);
        Assert.Equal(PlayerInventoryCounterpart.Unknown, outOf.Counterpart);
        Assert.True(outOf.CounterpartInventory.IsNone);
        Assert.False(outOf.Ambiguous);
        Assert.Equal(2, outOf.Requests);
        Assert.Equal(PlayerInventoryCounterpart.OtherInventory, moves.Single(m => m.Inventory == Chest2).Counterpart);
    }

    [Fact]
    public void Different_users_on_different_chests_are_attributed_exactly()
    {
        var b = new PlayerMoveBatch();
        b.Begin(this, PlayerInventoryMoveKind.Move);
        int c1 = b.AddInventory(1000, Chest);
        int c2 = b.AddInventory(1001, Chest2);
        b.AddRequest(UserA, CharA, MoveSideRole.OwnPlayer, null, MoveSideRole.Inventory, new[] { c1 });
        b.AddRequest(UserB, CharB, MoveSideRole.Inventory, new[] { c2 }, MoveSideRole.OwnPlayer, null);
        b.SetRows(c1, false, new[] { Empty });
        b.SetRows(c1, true, new[] { R(Ore, 1) });
        b.SetRows(c2, false, new[] { R(Wood, 4) });
        b.SetRows(c2, true, new[] { Empty });
        var moves = Run(b);
        var a = moves.Single(m => m.Inventory == Chest);
        Assert.Equal(UserA, a.User);
        Assert.Equal(PlayerInventoryMoveDirection.In, a.Direction);
        Assert.False(a.Ambiguous);
        var bb = moves.Single(m => m.Inventory == Chest2);
        Assert.Equal(UserB, bb.User);
        Assert.Equal(PlayerInventoryMoveDirection.Out, bb.Direction);
        Assert.False(bb.Ambiguous);
    }

    [Fact]
    public void Chest_to_chest_names_the_other_container_as_counterpart()
    {
        var b = new PlayerMoveBatch();
        b.Begin(this, PlayerInventoryMoveKind.Move);
        int c1 = b.AddInventory(1000, Chest);
        int c2 = b.AddInventory(1001, Chest2);
        b.AddRequest(UserA, CharA, MoveSideRole.Inventory, new[] { c1 }, MoveSideRole.Inventory, new[] { c2 });
        b.SetRows(c1, false, new[] { R(Ore, 7) });
        b.SetRows(c1, true, new[] { Empty });
        b.SetRows(c2, false, new[] { Empty });
        b.SetRows(c2, true, new[] { R(Ore, 7) });
        var moves = Run(b);
        var outOf = moves.Single(m => m.Direction == PlayerInventoryMoveDirection.Out);
        Assert.Equal(Chest, outOf.Inventory);
        Assert.Equal(PlayerInventoryCounterpart.OtherInventory, outOf.Counterpart);
        Assert.Equal(Chest2, outOf.CounterpartInventory);
        var into = moves.Single(m => m.Direction == PlayerInventoryMoveDirection.In);
        Assert.Equal(Chest2, into.Inventory);
        Assert.Equal(Chest, into.CounterpartInventory);
    }

    [Fact]
    public void A_station_side_names_the_instance_that_changed_as_counterpart()
    {
        var b = new PlayerMoveBatch();
        b.Begin(this, PlayerInventoryMoveKind.Move);
        int c = b.AddInventory(1000, Chest);
        int si = b.AddInventory(1002, StationIn);
        int so = b.AddInventory(1003, StationOut);
        b.AddRequest(UserA, CharA, MoveSideRole.Inventory, new[] { si, so }, MoveSideRole.Inventory, new[] { c });
        b.SetRows(si, false, new[] { R(Wood, 2) });
        b.SetRows(si, true, new[] { R(Wood, 2) });
        b.SetRows(so, false, new[] { R(Ore, 8) });
        b.SetRows(so, true, new[] { Empty });
        b.SetRows(c, false, new[] { Empty });
        b.SetRows(c, true, new[] { R(Ore, 8) });
        var moves = Run(b);
        Assert.Equal(2, moves.Count);
        var fromOutput = moves.Single(m => m.Inventory == StationOut);
        Assert.Equal(PlayerInventoryMoveDirection.Out, fromOutput.Direction);
        var chestIn = moves.Single(m => m.Inventory == Chest);
        Assert.Equal(StationOut, chestIn.CounterpartInventory);
    }

    [Fact]
    public void Sort_of_one_container_is_within_with_no_counterpart()
    {
        var b = new PlayerMoveBatch();
        b.Begin(this, PlayerInventoryMoveKind.Sort);
        int c = b.AddInventory(1000, Chest);
        b.AddRequest(UserA, CharA, MoveSideRole.Inventory, new[] { c }, MoveSideRole.None, null);
        b.SetRows(c, false, new[] { Empty, R(Ore, 3) });
        b.SetRows(c, true, new[] { R(Ore, 3), Empty });
        var m = Run(b).Single();
        Assert.Equal(PlayerInventoryMoveDirection.Within, m.Direction);
        Assert.Equal(3, m.Amount);
        Assert.Equal(PlayerInventoryCounterpart.None, m.Counterpart);
        Assert.Equal(PlayerInventoryMoveKind.Sort, m.Kind);
    }

    [Fact]
    public void Drag_between_two_slots_of_one_chest_names_it_on_both_sides()
    {
        var b = new PlayerMoveBatch();
        b.Begin(this, PlayerInventoryMoveKind.Move);
        int c = b.AddInventory(1000, Chest);
        b.AddRequest(UserA, CharA, MoveSideRole.Inventory, new[] { c }, MoveSideRole.Inventory, new[] { c });
        b.SetRows(c, false, new[] { R(Ore, 3), Empty });
        b.SetRows(c, true, new[] { Empty, R(Ore, 3) });
        var m = Run(b).Single();
        Assert.Equal(PlayerInventoryMoveDirection.Within, m.Direction);
        Assert.Equal(PlayerInventoryCounterpart.None, m.Counterpart);
    }

    [Fact]
    public void Drop_and_equip_name_the_ground_and_the_equipment()
    {
        var b = new PlayerMoveBatch();
        b.Begin(this, PlayerInventoryMoveKind.Drop);
        int c = b.AddInventory(1000, Chest);
        b.AddRequest(UserA, CharA, MoveSideRole.Inventory, new[] { c }, MoveSideRole.Ground, null);
        b.SetRows(c, false, new[] { R(Ore, 3) });
        b.SetRows(c, true, new[] { Empty });
        Assert.Equal(PlayerInventoryCounterpart.Ground, Run(b).Single().Counterpart);

        b.Begin(this, PlayerInventoryMoveKind.Equip);
        c = b.AddInventory(1000, Chest);
        b.AddRequest(UserA, CharA, MoveSideRole.Inventory, new[] { c }, MoveSideRole.Equipment, null);
        b.SetRows(c, false, new[] { R(Sword, 1, 77) });
        b.SetRows(c, true, new[] { Empty });
        var m = Run(b).Single();
        Assert.Equal(PlayerInventoryCounterpart.Equipment, m.Counterpart);
        Assert.Equal(PlayerInventoryMoveKind.Equip, m.Kind);
    }

    [Fact]
    public void An_unresolved_side_is_unknown_counterpart_and_an_unread_inventory_is_skipped()
    {
        var b = new PlayerMoveBatch();
        b.Begin(this, PlayerInventoryMoveKind.Move);
        int c = b.AddInventory(1000, Chest);
        int gone = b.AddInventory(1001, Chest2);
        b.AddRequest(UserA, CharA, MoveSideRole.Unknown, null, MoveSideRole.Inventory, new[] { c });
        b.AddRequest(UserA, CharA, MoveSideRole.OwnPlayer, null, MoveSideRole.Inventory, new[] { gone });
        b.SetRows(c, false, new[] { Empty });
        b.SetRows(c, true, new[] { R(Ore, 2) });
        b.SetRows(gone, false, new[] { R(Ore, 2) });
        b.SetRows(gone, true, null);
        var m = Run(b).Single();
        Assert.Equal(PlayerInventoryCounterpart.Unknown, m.Counterpart);
        Assert.Equal(1, b.Unread);
    }

    [Fact]
    public void Batch_caps_requests_and_inventories()
    {
        var b = new PlayerMoveBatch();
        b.Begin(this, PlayerInventoryMoveKind.Move);
        for (int i = 0; i < PlayerMoveBatch.MaxInventories; i++) Assert.True(b.AddInventory(1000 + i, Chest) >= 0);
        Assert.Equal(-1, b.AddInventory(5000, Chest));
        for (int i = 0; i < PlayerMoveBatch.MaxRequests; i++) Assert.True(b.AddRequest(UserA, CharA, MoveSideRole.OwnPlayer, null, MoveSideRole.None, null));
        Assert.False(b.AddRequest(UserA, CharA, MoveSideRole.OwnPlayer, null, MoveSideRole.None, null));
    }

    [Fact]
    public void Begin_drops_a_batch_whose_after_never_came()
    {
        var b = new PlayerMoveBatch();
        b.Begin("first", PlayerInventoryMoveKind.Move);
        int c = b.AddInventory(1000, Chest);
        b.AddRequest(UserA, CharA, MoveSideRole.OwnPlayer, null, MoveSideRole.Inventory, new[] { c });
        b.Begin("second", PlayerInventoryMoveKind.Sort);
        Assert.Equal(0, b.RequestCount);
        Assert.Equal(0, b.InventoryCount);
        Assert.Equal("second", b.Owner);
    }

    // ---- hub ------------------------------------------------------------------------------------

    static List<PendingPlayerMove> One(int amount = 5) => new()
    {
        new PendingPlayerMove(UserA, CharA, PlayerInventoryMoveKind.Move, Chest, Ore, amount, PlayerInventoryMoveDirection.In,
            0, amount, PlayerInventoryCounterpart.OwnInventory, InventoryRef.None, false, 1, T0),
    };

    static readonly ContainerId ChestId = new(Guid.Parse("0123456789abcdef0123456789abcdef"));

    [Fact]
    public void Hub_delivers_with_the_container_id_and_unsubscribes_on_dispose()
    {
        var hub = new PlayerMoveHub();
        var seen = new List<PlayerInventoryMove>();
        var sub = hub.Subscribe(seen.Add);
        hub.Enqueue(One());
        Assert.Equal(1, hub.Drain(r => r == Chest ? ChestId : ContainerId.None));
        var m = seen.Single();
        Assert.Equal(1, m.Sequence);
        Assert.Equal(ChestId, m.Container);
        Assert.True(m.IsCastleContainer);
        Assert.Equal(5, m.UserIndex);
        Assert.Equal(1, m.UserVersion);
        Assert.Equal(6, m.CharacterIndex);
        Assert.Contains("In", m.ToString());
        sub.Dispose();
        sub.Dispose();
        Assert.False(hub.Active);
        hub.Enqueue(One());
        Assert.Equal(0, hub.PendingCount);
    }

    [Fact]
    public void Hub_switches_the_hooks_on_first_listener_and_off_after_the_last()
    {
        var hub = new PlayerMoveHub();
        var states = new List<bool>();
        hub.ActiveChanged = states.Add;
        var a = hub.Subscribe(_ => { });
        var b = hub.Subscribe(_ => { });
        hub.SetRecording(true);
        a.Dispose();
        b.Dispose();
        Assert.True(hub.Active);
        hub.SetRecording(false);
        Assert.Equal(new[] { true, false }, states);
        hub.SetRecording(true);
        hub.Reset();
        Assert.Equal(new[] { true, false, true, false }, states);
    }

    [Fact]
    public void Turning_the_last_listener_off_drops_the_queue()
    {
        var hub = new PlayerMoveHub();
        hub.SetRecording(true);
        hub.Enqueue(One());
        hub.SetRecording(false);
        Assert.Equal(0, hub.PendingCount);
        hub.SetRecording(true);
        Assert.Equal(0, hub.Drain(_ => ContainerId.None));
        Assert.Empty(hub.Recent(10));
    }

    [Fact]
    public void A_throwing_handler_does_not_stop_the_others()
    {
        var warnings = new List<string>();
        var hub = new PlayerMoveHub(warnings.Add);
        int calls = 0;
        hub.Subscribe(_ => throw new InvalidOperationException("boom"));
        hub.Subscribe(_ => calls++);
        hub.Enqueue(One());
        hub.Drain(_ => ContainerId.None);
        Assert.Equal(1, calls);
        Assert.Equal(1, hub.HandlerErrors);
        Assert.Contains("boom", warnings.Single());
    }

    [Fact]
    public void A_full_queue_drops_the_oldest_and_counts_them()
    {
        var hub = new PlayerMoveHub();
        hub.SetRecording(true);
        for (int i = 0; i < PlayerMoveHub.MaxPending + 3; i++) hub.Enqueue(One(i + 1));
        Assert.Equal(3, hub.Dropped);
        Assert.Equal(PlayerMoveHub.MaxPending, hub.PendingCount);
        hub.Drain(_ => ContainerId.None);
        var recent = hub.Recent(PlayerMoveHub.RingSize);
        Assert.Equal(PlayerMoveHub.RingSize, recent.Count);
        Assert.Equal(PlayerMoveHub.MaxPending + 3, recent[^1].Amount);
        Assert.True(recent[0].Sequence < recent[^1].Sequence);
    }

    [Fact]
    public void Recent_returns_the_last_moves_oldest_first_and_clear_forgets_them()
    {
        var hub = new PlayerMoveHub();
        hub.SetRecording(true);
        for (int i = 1; i <= 5; i++) hub.Enqueue(One(i));
        hub.Drain(_ => ContainerId.None);
        Assert.Equal(new[] { 3, 4, 5 }, hub.Recent(3).Select(m => m.Amount));
        Assert.Equal(5, hub.Recent(50).Count);
        hub.ClearRecent();
        Assert.Empty(hub.Recent(10));
    }

    [Fact]
    public void A_failing_id_lookup_gives_no_id_and_the_move_still_arrives()
    {
        var hub = new PlayerMoveHub();
        PlayerInventoryMove? got = null;
        hub.Subscribe(m => got = m);
        hub.Enqueue(One());
        hub.Drain(_ => throw new InvalidOperationException("not loaded"));
        Assert.NotNull(got);
        Assert.True(got!.Container.IsNone);
        Assert.False(got.IsCastleContainer);
    }

    // ---- bridge arguments -----------------------------------------------------------------------

    static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    [Fact]
    public void Bridge_args_default_and_parse()
    {
        var none = InventoryMovesArgs.Parse(default);
        Assert.Equal(InventoryMovesArgs.DefaultLast, none.Last);
        Assert.Null(none.Record);
        Assert.False(none.Clear);
        var r = InventoryMovesArgs.Parse(Json("{\"record\":true,\"last\":5,\"clear\":true}"));
        Assert.Equal(5, r.Last);
        Assert.True(r.Record);
        Assert.True(r.Clear);
        Assert.False(InventoryMovesArgs.Parse(Json("{\"record\":false}")).Record);
    }

    [Theory]
    [InlineData("{\"last\":-1}")]
    [InlineData("{\"last\":257}")]
    [InlineData("{\"last\":\"5\"}")]
    [InlineData("{\"record\":1}")]
    [InlineData("{\"clear\":\"yes\"}")]
    [InlineData("[1]")]
    public void Bridge_args_refuse_bad_values(string text)
    {
        Assert.Throws<ArgumentException>(() => InventoryMovesArgs.Parse(Json(text)));
    }
}
