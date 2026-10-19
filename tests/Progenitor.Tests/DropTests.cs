using System;
using System.Collections.Generic;
using System.Linq;
using Progenitor.Drops;
using Progenitor.Inventory;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// ctx.Drops and ctx.Inventory.OnGroundDrop / OnDropAll: pile handles, the take rules, the
/// nearest-first selection, the bag-drop batch, the drop-all container matcher and the delivery hub.
/// The hooks, raw reads and the pile write (DropRuntime, GroundPiles) are not compiled here.
/// </summary>
public class DropTests
{
    const int Ore = 100, Wood = 200, Sword = 300;
    const long Alice = (7L << 32) | 1, AliceChar = (8L << 32) | 1, Bob = (9L << 32) | 2, BobChar = (10L << 32) | 2;
    static readonly DateTime T0 = new(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);

    static MoveSlotRow R(int item, int amount, long entity = 0) => new(item, amount, entity);
    static MoveSlotRow Empty => default;

    // ---- GroundPileId ---------------------------------------------------------------------------

    [Fact]
    public void Pile_id_is_the_entity_plus_the_epoch_and_goes_stale_on_a_reload()
    {
        var id = new GroundPileId(12, 3, 5);
        Assert.False(id.IsNone);
        Assert.False(id.IsStaleIn(5));
        Assert.True(id.IsStaleIn(6));
        Assert.Equal((12L << 32) | 3, id.Key);
        Assert.Equal("12:3@5", id.ToString());
        Assert.NotEqual(id, new GroundPileId(12, 3, 6));
        Assert.Equal(id, new GroundPileId(12, 3, 5));
        Assert.True(GroundPileId.None.IsNone);
        Assert.False(GroundPileId.None.IsStaleIn(99));
        Assert.Equal("none", GroundPileId.None.ToString());
    }

    // ---- GroundPile -----------------------------------------------------------------------------

    [Theory]
    [InlineData(4000, 0, 0, true)]   // stackable, no item entity: takeable
    [InlineData(1, 0, 0, false)]     // stack size 1: unique
    [InlineData(0, 0, 0, false)]     // not in the item table: unique
    [InlineData(4000, 55, 1, false)] // names its own item entity: unique
    public void A_pile_is_takeable_only_when_stackable_and_without_an_item_entity(int maxStack, int entityIndex, int entityVersion, bool canTake)
    {
        var p = new GroundPile(new GroundPileId(1, 1, 1), Ore, 10, 0, 0, 0, 0, false, maxStack, entityIndex, entityVersion);
        Assert.Equal(canTake, p.CanTake);
        Assert.Equal(!canTake, p.IsUnique);
    }

    [Fact]
    public void Pile_distance_is_the_root_of_the_squared_distance()
    {
        var p = new GroundPile(new GroundPileId(1, 1, 1), Ore, 10, 3, 0, 4, 25f, false, 100);
        Assert.Equal(5f, p.Distance, 3);
        Assert.Contains("item=100 x10", p.ToString());
    }

    // ---- take rules -----------------------------------------------------------------------------

    [Fact]
    public void Take_refusals_come_in_a_fixed_order()
    {
        string? Ref(bool none = false, bool stale = false, bool target = false, int req = 5, bool exists = true, bool removing = false,
            int amount = 10, bool flying = false, bool entity = false, bool known = true, int max = 100) =>
            GroundTakeRules.Refusal(none, stale, target, req, exists, removing, amount, flying, entity, known, max);

        Assert.Null(Ref());
        Assert.Equal(GroundTakeRules.NoPile, Ref(none: true, stale: true, req: 0));
        Assert.Equal(GroundTakeRules.NoTarget, Ref(target: true, req: 0));
        Assert.Equal(GroundTakeRules.BadAmount, Ref(req: 0, stale: true));
        Assert.Equal(GroundTakeRules.StalePile, Ref(stale: true, exists: false));
        Assert.Equal(GroundTakeRules.Gone, Ref(exists: false, removing: true));
        Assert.Equal(GroundTakeRules.Removing, Ref(removing: true, amount: 0));
        Assert.Equal(GroundTakeRules.Empty, Ref(amount: 0, flying: true));
        Assert.Equal(GroundTakeRules.InFlight, Ref(flying: true, entity: true));
        Assert.Equal(GroundTakeRules.ItemEntity, Ref(entity: true, known: false));
        Assert.Equal(GroundTakeRules.UnknownItem, Ref(known: false, max: 1));
        Assert.Equal(GroundTakeRules.NotStackable, Ref(max: 1));
    }

    [Theory]
    [InlineData(5, 10, 5)]
    [InlineData(50, 10, 10)]
    [InlineData(1, 1, 1)]
    public void Ask_never_exceeds_the_pile(int requested, int pile, int ask) => Assert.Equal(ask, GroundTakeRules.Ask(requested, pile));

    [Fact]
    public void Pile_shrinks_by_what_the_inventory_took_and_is_removed_when_empty()
    {
        Assert.Equal((7, false, 3), GroundTakeRules.After(10, 5, 3)); // a partial add
        Assert.Equal((0, true, 10), GroundTakeRules.After(10, 10, 10)); // the whole pile
        Assert.Equal((10, false, 0), GroundTakeRules.After(10, 5, 0)); // nothing fit: untouched, not removed
        Assert.Equal((5, false, 5), GroundTakeRules.After(10, 5, 9)); // a read-back above the ask is clamped
        Assert.Equal((0, false, 0), GroundTakeRules.After(0, 0, 0));
    }

    [Theory]
    [InlineData(10, 10, InventoryOutcome.Complete)]
    [InlineData(10, 4, InventoryOutcome.Partial)]
    [InlineData(10, 0, InventoryOutcome.Nothing)]
    public void Take_outcome_follows_the_done_count(int requested, int done, InventoryOutcome outcome) =>
        Assert.Equal(outcome, GroundTakeRules.Outcome(requested, done));

    // ---- selection ------------------------------------------------------------------------------

    [Fact]
    public void Radius_and_cap_are_clamped()
    {
        Assert.Equal(0f, GroundPileSelect.ClampRadius(float.NaN));
        Assert.Equal(0f, GroundPileSelect.ClampRadius(-3));
        Assert.Equal(GroundPileSelect.MaxRadius, GroundPileSelect.ClampRadius(1e9f));
        Assert.Equal(12f, GroundPileSelect.ClampRadius(12));
        Assert.Equal(1, GroundPileSelect.ClampMax(0));
        Assert.Equal(GroundPileSelect.MaxResults, GroundPileSelect.ClampMax(int.MaxValue));
    }

    [Fact]
    public void Within_uses_the_three_dimensional_distance()
    {
        Assert.True(GroundPileSelect.Within(0, 0, 0, 25, 3, 0, 4, out float d2));
        Assert.Equal(25f, d2);
        Assert.False(GroundPileSelect.Within(0, 0, 0, 25, 3, 1, 4, out _)); // a pile on the floor above is further
    }

    [Fact]
    public void Nearest_first_breaks_ties_by_entity_and_keeps_the_cap()
    {
        GroundPile P(int index, float d2) => new(new GroundPileId(index, 1, 1), Ore, 1, 0, 0, 0, d2, false, 100);
        var list = new List<GroundPile> { P(5, 9), P(2, 1), P(9, 4), P(1, 4) };
        GroundPileSelect.NearestFirst(list, 3);
        Assert.Equal(new[] { 2, 1, 9 }, list.Select(p => p.Id.Index));
    }

    // ---- prefabs --------------------------------------------------------------------------------

    [Fact]
    public void Drop_prefabs_name_the_death_container_access()
    {
        var p = new DropPrefabs(1, 2, 10, 11, 12, 20);
        Assert.True(p.IsComplete);
        Assert.Equal(DeathContainerAccess.Anyone, p.AccessOf(10));
        Assert.Equal(DeathContainerAccess.ClanMembers, p.AccessOf(11));
        Assert.Equal(DeathContainerAccess.OnlySelf, p.AccessOf(12));
        Assert.Equal(DeathContainerAccess.None, p.AccessOf(2)); // the generic death container is not a player's
        Assert.Equal(DeathContainerAccess.None, p.AccessOf(0));
        Assert.True(p.IsPlayerDeathContainer(11));
        Assert.True(p.IsPlayerInventoryDrop(20));
        Assert.False(p.IsPlayerInventoryDrop(0));
        Assert.False(new DropPrefabs(1, 2, 10, 11, 12, 0).IsComplete);
        Assert.False(default(DropPrefabs).IsPlayerInventoryDrop(0));
    }

    [Fact]
    public void Prefab_offsets_agree_between_server_and_client_and_match_the_metadata_order()
    {
        Assert.Equal(Layout.RootPrefabDropItemOffset, Layout.ClientRootPrefabDropItemOffset);
        Assert.Equal(Layout.RootPrefabDeathContainerOffset, Layout.ClientRootPrefabDeathContainerOffset);
        Assert.Equal(Layout.RootPrefabPlayerDeathContainerAnyoneOffset, Layout.ClientRootPrefabPlayerDeathContainerAnyoneOffset);
        Assert.Equal(Layout.RootPrefabPlayerDeathContainerClanMembersOffset, Layout.ClientRootPrefabPlayerDeathContainerClanMembersOffset);
        Assert.Equal(Layout.RootPrefabPlayerDeathContainerOnlySelfOffset, Layout.ClientRootPrefabPlayerDeathContainerOnlySelfOffset);
        Assert.Equal(Layout.RootPrefabPlayerInventoryDropContainerOffset, Layout.ClientRootPrefabPlayerInventoryDropContainerOffset);
        // The offsets the Autoloot mod read raw before this API (88, 92, 96, 104).
        Assert.Equal(88, Layout.RootPrefabPlayerDeathContainerAnyoneOffset);
        Assert.Equal(92, Layout.RootPrefabPlayerDeathContainerClanMembersOffset);
        Assert.Equal(96, Layout.RootPrefabPlayerDeathContainerOnlySelfOffset);
        Assert.Equal(104, Layout.RootPrefabPlayerInventoryDropContainerOffset);
    }

    [Fact]
    public void Drop_all_setting_values_are_the_games()
    {
        Assert.Equal(Layout.DropEntireInventorySettingAll, (int)DropAllSetting.All);
        Assert.Equal(Layout.DropEntireInventorySettingExceptAnyEquipped, (int)DropAllSetting.ExceptAnyEquipped);
        Assert.Equal(Layout.DropEntireInventorySettingExceptEquippedInActionBar, (int)DropAllSetting.ExceptEquippedInActionBar);
        Assert.Equal(Layout.DropEntireInventorySettingExceptAnyWeaponInActionBar, (int)DropAllSetting.ExceptAnyWeaponInActionBar);
        Assert.Equal(Layout.DropEntireInventorySettingExceptAnyWeaponInActionBarOrAnyEquipped, (int)DropAllSetting.ExceptAnyWeaponInActionBarOrAnyEquipped);
        Assert.Equal(Layout.DropEntireInventorySettingExceptAnythingInActionBar, (int)DropAllSetting.ExceptAnythingInActionBar);
        Assert.Equal(Layout.DropEntireInventorySettingExceptAnythingInActionBarOrEquipped, (int)DropAllSetting.ExceptAnythingInActionBarOrEquipped);
        Assert.Equal(DropAllSetting.ExceptAnythingInActionBar, PendingDropAll.Setting(5));
        Assert.Equal(DropAllSetting.Unknown, PendingDropAll.Setting(7));
        Assert.Equal(DropAllSetting.Unknown, PendingDropAll.Setting(-1));
    }

    // ---- the bag-drop batch ---------------------------------------------------------------------

    static (List<PendingGroundDrop> Ground, List<PendingDropAll> All) Run(BagDropBatch b)
    {
        var g = new List<PendingGroundDrop>();
        var a = new List<PendingDropAll>();
        b.Emit(T0, g, a);
        return (g, a);
    }

    [Fact]
    public void Drop_key_on_a_stack_is_one_ground_drop_with_the_position()
    {
        var b = new BagDropBatch();
        b.Begin(this);
        int at = b.AddRequest(Alice, AliceChar, BagDropRequestKind.DropKey);
        b.SetRows(at, false, new[] { R(Ore, 40), R(Wood, 5) });
        b.SetRows(at, true, new[] { Empty, R(Wood, 5) });
        b.SetPosition(at, 1, 2, 3);
        var (g, a) = Run(b);
        Assert.Empty(a);
        var d = g.Single();
        Assert.Equal(Ore, d.Item);
        Assert.Equal(40, d.Amount);
        Assert.Equal(40, d.Before);
        Assert.Equal(0, d.After);
        Assert.Equal(GroundDropSource.DropKey, d.Source);
        Assert.True(d.HasPosition);
        Assert.Equal((1f, 2f, 3f), (d.X, d.Y, d.Z));
        Assert.False(d.WithItemEntity);
        var drop = d.ToDrop(17);
        Assert.Equal(17, drop.Sequence);
        Assert.Equal(7, drop.UserIndex);
        Assert.Equal(1, drop.UserVersion);
        Assert.Equal(8, drop.CharacterIndex);
        Assert.False(b.Open);
    }

    [Fact]
    public void Drag_out_of_gear_is_flagged_with_its_item_entity()
    {
        var b = new BagDropBatch();
        b.Begin(this);
        int at = b.AddRequest(Alice, AliceChar, BagDropRequestKind.DragOut);
        b.SetRows(at, false, new[] { R(Sword, 1, 999), R(Sword, 1, 998) });
        b.SetRows(at, true, new[] { Empty, R(Sword, 1, 998) });
        var d = Run(b).Ground.Single();
        Assert.Equal(GroundDropSource.DragOut, d.Source);
        Assert.Equal(1, d.Amount);
        Assert.True(d.WithItemEntity);
        Assert.False(d.HasPosition);
    }

    [Fact]
    public void A_refused_drop_and_a_slot_move_raise_nothing()
    {
        var b = new BagDropBatch();
        b.Begin(this);
        int at = b.AddRequest(Alice, AliceChar, BagDropRequestKind.DropKey);
        b.SetRows(at, false, new[] { R(Ore, 40), Empty });
        b.SetRows(at, true, new[] { Empty, R(Ore, 40) });
        var (g, a) = Run(b);
        Assert.Empty(g);
        Assert.Empty(a);
        Assert.Equal(1, b.Unchanged);
    }

    [Fact]
    public void Unreadable_rows_are_counted_not_guessed()
    {
        var b = new BagDropBatch();
        b.Begin(this);
        int at = b.AddRequest(Alice, AliceChar, BagDropRequestKind.DropKey);
        b.SetRows(at, false, null);
        b.SetRows(at, true, new[] { Empty });
        Assert.Empty(Run(b).Ground);
        Assert.Equal(1, b.Unread);
    }

    [Fact]
    public void Two_drops_of_one_player_in_one_update_are_one_record_per_item_with_both_requests()
    {
        var b = new BagDropBatch();
        b.Begin(this);
        int first = b.AddRequest(Alice, AliceChar, BagDropRequestKind.DropKey);
        int second = b.AddRequest(Alice, AliceChar, BagDropRequestKind.DropKey);
        Assert.Equal(first, second);
        int bob = b.AddRequest(Bob, BobChar, BagDropRequestKind.DropKey);
        Assert.NotEqual(first, bob);
        b.SetRows(first, false, new[] { R(Ore, 40), R(Ore, 10) });
        b.SetRows(first, true, new[] { Empty, Empty });
        b.SetRows(bob, false, new[] { R(Wood, 3) });
        b.SetRows(bob, true, new[] { R(Wood, 1) });
        var g = Run(b).Ground;
        Assert.Equal(2, g.Count);
        var alice = g.Single(x => x.Character == AliceChar);
        Assert.Equal(50, alice.Amount);
        Assert.Equal(2, alice.Requests);
        var bobs = g.Single(x => x.Character == BobChar);
        Assert.Equal(2, bobs.Amount);
        Assert.Equal(Bob, bobs.User);
    }

    [Fact]
    public void Drop_all_collects_every_lost_stack_with_the_setting()
    {
        var b = new BagDropBatch();
        b.Begin(this);
        int at = b.AddRequest(Alice, AliceChar, BagDropRequestKind.DropAll, (int)DropAllSetting.ExceptAnythingInActionBar);
        Assert.True(b.HasDropAll);
        b.SetRows(at, false, new[] { R(Sword, 1, 50), R(Ore, 40), R(Wood, 7) });
        b.SetRows(at, true, new[] { R(Sword, 1, 50), Empty, Empty });
        b.SetPosition(at, 4, 0, 4);
        var (g, a) = Run(b);
        Assert.Empty(g);
        var p = a.Single();
        Assert.Equal(DropAllSetting.ExceptAnythingInActionBar, PendingDropAll.Setting(p.SettingRaw));
        Assert.False(p.Ambiguous);
        Assert.Equal(47, p.Units);
        Assert.Equal(new[] { Ore, Wood }, p.Items.Select(s => s.Item).OrderBy(i => i));
        var done = p.ToDrop(3, 44, 2, 9, true, false, "");
        Assert.True(done.HasContainer);
        Assert.True(done.ContentsMatch);
        Assert.Equal(2, done.Items.Count);
        Assert.Equal(9, done.Epoch);
    }

    [Fact]
    public void Drop_all_with_a_drop_key_in_the_same_update_is_ambiguous_and_raises_no_ground_drop()
    {
        var b = new BagDropBatch();
        b.Begin(this);
        int at = b.AddRequest(Alice, AliceChar, BagDropRequestKind.DropKey);
        b.AddRequest(Alice, AliceChar, BagDropRequestKind.DropAll, 0);
        b.SetRows(at, false, new[] { R(Ore, 40) });
        b.SetRows(at, true, new[] { Empty });
        var (g, a) = Run(b);
        Assert.Empty(g);
        Assert.True(a.Single().Ambiguous);
    }

    [Fact]
    public void Requests_past_the_caps_are_counted()
    {
        var b = new BagDropBatch();
        b.Begin(this);
        for (int i = 0; i < BagDropBatch.MaxBags; i++)
            Assert.True(b.AddRequest(i, 1000 + i, BagDropRequestKind.DropKey) >= 0);
        Assert.Equal(-1, b.AddRequest(5000, 5000, BagDropRequestKind.DropKey));
        Assert.Equal(1, b.Truncated);
        for (int i = b.RequestCount; i < BagDropBatch.MaxRequests; i++)
            Assert.True(b.AddRequest(0, 1000, BagDropRequestKind.DropKey) >= 0);
        Assert.Equal(-1, b.AddRequest(0, 1000, BagDropRequestKind.DropKey));
        Assert.Equal(2, b.Truncated);
    }

    [Fact]
    public void Differ_reports_units_that_left_with_their_own_entity()
    {
        var into = new List<ItemMoveDelta>();
        new PlayerMoveDiffer().Diff(new[] { R(Sword, 1, 9), R(Ore, 5) }, new[] { Empty, R(Ore, 2) }, into);
        Assert.Equal(1, into.Single(d => d.Item == Sword).OutWithEntity);
        Assert.Equal(0, into.Single(d => d.Item == Ore).OutWithEntity);
    }

    // ---- the drop-all container matcher ---------------------------------------------------------

    static PendingDropAll Req(float x, float z, bool position = true) =>
        new() { HasPosition = position, X = x, Y = 0, Z = z };

    static DropContainerCandidate C(long key, float x, float z) => new(key, x, 0, z);

    static readonly Func<int, int, bool> Any = (_, _) => true;

    [Fact]
    public void A_new_container_near_the_player_is_theirs()
    {
        var m = DropContainerMatcher.Match(new[] { Req(0, 0) }, new[] { C(1, 1, 0), C(2, 20, 0) }, Any, out var amb);
        Assert.Equal(new[] { 0 }, m);
        Assert.False(amb[0]);
    }

    [Fact]
    public void Containers_too_far_or_a_request_without_a_position_never_match()
    {
        Assert.Equal(new[] { -1 }, DropContainerMatcher.Match(new[] { Req(0, 0) },
            new[] { C(2, DropContainerMatcher.MaxDistance + 1, 0) }, Any, out _));
        Assert.Equal(new[] { -1 }, DropContainerMatcher.Match(new[] { Req(0, 0, position: false) }, new[] { C(3, 0, 0) }, Any, out _));
    }

    [Fact]
    public void Two_players_get_the_container_nearest_each_and_a_close_second_is_ambiguous()
    {
        var m = DropContainerMatcher.Match(new[] { Req(0, 0), Req(6, 0) }, new[] { C(1, 5.5f, 0), C(2, 0.5f, 0) }, Any, out var amb);
        Assert.Equal(new[] { 1, 0 }, m);
        Assert.False(amb[0]);

        var close = DropContainerMatcher.Match(new[] { Req(0, 0) }, new[] { C(1, 1f, 0), C(2, -1.2f, 0) }, Any, out var amb2);
        Assert.Equal(new[] { 0 }, close);
        Assert.True(amb2[0]);
    }

    [Fact]
    public void A_container_that_does_not_hold_the_bag_loss_is_never_handed_out()
    {
        // Alice and Bob both drop all within 8 m; only Alice's request made the batch (Bob's was
        // past the cap). Bob's container lies nearer to Alice than her own does.
        var alice = Req(0, 0);
        alice.Items.Add(new DroppedStack(Ore, 40, false));
        InventorySlot S(int item, int amount) => new(0, item, amount, 100, 0, false);
        var held = new Dictionary<long, InventorySlot[]>
        {
            [1] = new[] { S(Wood, 9) },   // Bob's, 1 m away
            [2] = new[] { S(Ore, 40) },   // Alice's, 3 m away
        };
        var candidates = new[] { C(1, 1, 0), C(2, 3, 0) };
        var asked = new List<(int, int)>();
        bool Covers(int r, int c)
        {
            asked.Add((r, c));
            return DropContainerMatcher.ContentsCover(alice.Items, held[candidates[c].Key]);
        }

        var m = DropContainerMatcher.Match(new[] { alice }, candidates, Covers, out var amb);
        Assert.Equal(new[] { 1 }, m);
        Assert.False(amb[0]);
        Assert.Equal(new[] { (0, 0), (0, 1) }, asked); // nearest first, each pair once

        // Only Bob's container ever appears: Alice's drop stays unmatched and keeps waiting.
        var none = DropContainerMatcher.Match(new[] { alice }, new[] { C(1, 1, 0) }, Covers, out _);
        Assert.Equal(new[] { -1 }, none);
    }

    [Fact]
    public void A_throwing_contents_check_counts_as_not_covered()
    {
        var m = DropContainerMatcher.Match(new[] { Req(0, 0) }, new[] { C(1, 1, 0) },
            (_, _) => throw new InvalidOperationException("unreadable"), out _);
        Assert.Equal(new[] { -1 }, m);
    }

    // ---- the container book ---------------------------------------------------------------------

    [Fact]
    public void Book_offers_only_containers_it_has_not_accounted_for()
    {
        var book = new DropContainerBook();
        Assert.False(book.HasBaselineFor(3));
        book.Baseline(3, new long[] { 10, 11 });
        Assert.True(book.HasBaselineFor(3));
        Assert.False(book.HasBaselineFor(4));

        var fresh = new List<DropContainerCandidate>();
        book.New(new[] { C(10, 0, 0), C(12, 0, 0), C(13, 0, 0) }, fresh);
        Assert.Equal(new long[] { 12, 13 }, fresh.Select(c => c.Key));

        book.Matched(12);
        book.New(new[] { C(12, 0, 0), C(13, 0, 0) }, fresh);
        Assert.Equal(new long[] { 13 }, fresh.Select(c => c.Key)); // 13 is still offered while drops wait

        book.Settle(); // nothing waits: 13 belongs to nobody we know of, and is never offered again
        book.New(new[] { C(13, 0, 0), C(14, 0, 0) }, fresh);
        Assert.Equal(new long[] { 14 }, fresh.Select(c => c.Key));

        book.Clear();
        Assert.False(book.HasBaselineFor(3));
        Assert.Equal(0, book.Count);
    }

    // ---- in flight ------------------------------------------------------------------------------

    [Fact]
    public void A_pile_in_flight_is_not_takeable_and_take_refuses_it()
    {
        var flying = new GroundPile(new GroundPileId(1, 1, 1), Ore, 10, 0, 0, 0, 0, false, 4000, 0, 0, inFlight: true);
        Assert.True(flying.InFlight);
        Assert.False(flying.IsUnique);
        Assert.False(flying.CanTake);
        Assert.Contains("in-flight", flying.ToString());
        Assert.Equal(GroundTakeRules.InFlight,
            GroundTakeRules.Refusal(false, false, false, 5, true, false, 10, true, false, true, 4000));
        // Empty and removing piles are refused before the flight check; the item checks come after it.
        Assert.Equal(GroundTakeRules.Empty, GroundTakeRules.Refusal(false, false, false, 5, true, false, 0, true, false, true, 4000));
        Assert.Equal(GroundTakeRules.InFlight, GroundTakeRules.Refusal(false, false, false, 5, true, false, 10, true, true, false, 1));
        Assert.Contains("in flight", GroundTakeRules.InFlight);
    }

    [Fact]
    public void Contents_cover_what_left_the_bag()
    {
        var lost = new[] { new DroppedStack(Ore, 40, false), new DroppedStack(Wood, 5, false) };
        InventorySlot S(int item, int amount) => new(0, item, amount, 100, 0, false);
        Assert.True(DropContainerMatcher.ContentsCover(lost, new[] { S(Ore, 30), S(Ore, 10), S(Wood, 5), S(Sword, 1) }));
        Assert.False(DropContainerMatcher.ContentsCover(lost, new[] { S(Ore, 40), S(Wood, 4) }));
        Assert.False(DropContainerMatcher.ContentsCover(Array.Empty<DroppedStack>(), new[] { S(Ore, 40) }));
    }

    // ---- the delivery hub -----------------------------------------------------------------------

    sealed class Rec
    {
        internal long Seq;
    }

    [Fact]
    public void Hub_turns_hooks_on_with_the_first_subscriber_and_off_with_the_last()
    {
        var states = new List<bool>();
        var hub = new DropEventHub<Rec>("test") { ActiveChanged = states.Add };
        var got = new List<long>();
        var a = hub.Subscribe(r => got.Add(r.Seq));
        var b = hub.Subscribe(_ => { });
        hub.Enqueue(s => new Rec { Seq = s });
        hub.Enqueue(s => new Rec { Seq = s });
        Assert.Equal(2, hub.Drain());
        Assert.Equal(new long[] { 1, 2 }, got);
        a.Dispose();
        a.Dispose();
        Assert.True(hub.Active);
        b.Dispose();
        Assert.False(hub.Active);
        Assert.Equal(new[] { true, false }, states);
        hub.Enqueue(s => new Rec { Seq = s }); // nobody listens: not queued
        Assert.Equal(0, hub.PendingCount);
    }

    [Fact]
    public void Hub_isolates_a_throwing_handler_and_caps_the_queue()
    {
        var warnings = new List<string>();
        var hub = new DropEventHub<Rec>("test", warnings.Add);
        int seen = 0;
        hub.Subscribe(_ => throw new InvalidOperationException("boom"));
        hub.Subscribe(_ => seen++);
        for (int i = 0; i < DropEventHub<Rec>.MaxPending + 5; i++) hub.Enqueue(s => new Rec { Seq = s });
        Assert.Equal(5, hub.Dropped);
        Assert.Equal(DropEventHub<Rec>.MaxPending, hub.Drain());
        Assert.Equal(DropEventHub<Rec>.MaxPending, seen);
        Assert.Equal(DropEventHub<Rec>.MaxPending, hub.HandlerErrors);
        Assert.Equal(8, warnings.Count);
        hub.Reset();
        Assert.False(hub.Active);
        Assert.Equal(0, hub.Subscribers);
    }

    [Fact]
    public void Records_print_one_log_line()
    {
        var g = new PlayerGroundDrop(4, T0, 7, 1, 8, 1, GroundDropSource.DropKey, Ore, 40, 40, 0, false, true, 1, 2, 3, 2);
        Assert.Equal("#4 user=7:1 DropKey item=100 x40 bag=40->0 at 1.0,2.0,3.0 requests=2", g.ToString());
        var a = new PlayerDropAll(5, T0, 7, 1, 8, 1, DropAllSetting.All, new[] { new DroppedStack(Ore, 40, false) }, true, 0, 0, 0,
            0, 0, 1, false, false, "no new container");
        Assert.False(a.HasContainer);
        Assert.Equal("#5 user=7:1 dropall All items=1 no-container (no new container)", a.ToString());
    }
}
