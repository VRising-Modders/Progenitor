using System;
using System.Collections.Generic;
using Progenitor.Inventory;
using Progenitor.Players;
using Progenitor.Sample;
using Progenitor.Stations;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Game-free halves of the brazier switch (ctx.Stations), the client sort event
/// (InventorySortUsed) and online player positions (ctx.Players.OnlinePositions), plus the samples'
/// parsing and replies. Item GUIDs, ids and names are placeholders.
/// </summary>
public class SortBrazierPositionTests
{
    private const int Fuel = 4242;

    // ---- braziers ------------------------------------------------------------------------------

    [Fact]
    public void FuelSecondsLeft_adds_the_rest_of_the_started_item_to_the_waiting_ones()
    {
        Assert.Equal(300f, BrazierLogic.FuelSecondsLeft(0f, 3, 100f));
        Assert.Equal(325f, BrazierLogic.FuelSecondsLeft(0.75f, 3, 100f), 3);
        Assert.Equal(50f, BrazierLogic.FuelSecondsLeft(0.5f, 0, 100f), 3);
        Assert.Equal(0f, BrazierLogic.FuelSecondsLeft(0f, 0, 100f));
    }

    [Fact]
    public void FuelSecondsLeft_is_zero_for_a_bad_burn_time_and_clamps_progress()
    {
        Assert.Equal(0f, BrazierLogic.FuelSecondsLeft(0.5f, 4, 0f));
        Assert.Equal(0f, BrazierLogic.FuelSecondsLeft(0.5f, 4, -1f));
        Assert.Equal(0f, BrazierLogic.FuelSecondsLeft(0.5f, 4, float.NaN));
        Assert.Equal(0f, BrazierLogic.FuelSecondsLeft(0.5f, 4, float.PositiveInfinity));
        Assert.Equal(400f, BrazierLogic.FuelSecondsLeft(2f, 4, 100f));   // progress 1: nothing left of it
        Assert.Equal(400f, BrazierLogic.FuelSecondsLeft(float.NaN, 4, 100f));
        Assert.Equal(0f, BrazierLogic.FuelSecondsLeft(0f, -3, 100f));
    }

    [Fact]
    public void PlanSet_writes_only_on_a_difference()
    {
        Assert.Null(BrazierLogic.PlanSet(true, true));
        Assert.Null(BrazierLogic.PlanSet(false, false));
        Assert.Equal((byte)1, BrazierLogic.PlanSet(false, true));
        Assert.Equal((byte)0, BrazierLogic.PlanSet(true, false));
    }

    [Fact]
    public void Brazier_state_describes_itself()
    {
        var burning = new BrazierState
        {
            Enabled = true, HasBonfire = true, Burning = true, Strength = 0.8f, Progress = 0.5f,
            FuelItemGuid = Fuel, FuelAmount = 12, BurnTimeSeconds = 120f,
        };
        Assert.Equal(1500f, burning.FuelSecondsLeft, 3);
        Assert.Equal("on, burning 80%, fuel 12x 4242 (1500 s of burning)", burning.ToString());

        var dark = new BrazierState { Enabled = false, HasBonfire = true, FuelItemGuid = Fuel, BurnTimeSeconds = 120f };
        Assert.Equal("off, dark 0%, fuel 0x 4242", dark.ToString());

        var bare = new BrazierState { Enabled = true };
        Assert.Equal("on, no burn state", bare.ToString());
        Assert.Throws<ArgumentNullException>(() => BrazierLogic.Describe(null!));
    }

    [Fact]
    public void Brazier_sample_parses()
    {
        Assert.True(SampleBraziers.TryParse("", "", out var r));
        Assert.Equal(SampleBraziers.Kind.List, r.Kind);
        Assert.Equal(SampleBraziers.NearestRadius, r.Radius);
        Assert.True(SampleBraziers.TryParse("list", "50", out r));
        Assert.Equal(50f, r.Radius);
        Assert.False(SampleBraziers.TryParse("list", "500", out _));
        Assert.False(SampleBraziers.TryParse("list", "-1", out _));
        Assert.True(SampleBraziers.TryParse("ON", "", out r));
        Assert.Equal(SampleBraziers.Kind.On, r.Kind);
        Assert.False(r.HasId);
        Assert.True(SampleBraziers.TryParse("off", "12:3@4", out r));
        Assert.Equal(SampleBraziers.Kind.Off, r.Kind);
        Assert.Equal((12, 3, 4), (r.Index, r.Version, r.Epoch));
        Assert.True(SampleBraziers.TryParse("status", "", out r));
        Assert.Equal(SampleBraziers.Kind.Status, r.Kind);
        Assert.False(SampleBraziers.TryParse("on", "12:3", out _));
        Assert.False(SampleBraziers.TryParse("toggle", "", out _));
    }

    [Fact]
    public void Brazier_sample_replies()
    {
        var state = new BrazierState { Enabled = true, HasBonfire = true, FuelItemGuid = Fuel, FuelAmount = 2, BurnTimeSeconds = 10f };
        Assert.Equal("No braziers within 20 m.", SampleBraziers.ListReply(Array.Empty<(string, string, float, BrazierState)>(), 20f));
        string list = SampleBraziers.ListReply(new[] { ("5:1@2", "TM_Example_Brazier", 3.2f, state) }, 20f);
        Assert.StartsWith("1 brazier(s) within 20 m: 5:1@2 Example_Brazier (3.2 m, on, dark 0%", list);
        Assert.Equal("Entity 5:1@2 is not a brazier.", SampleBraziers.StatusReply("5:1@2", null));
        Assert.StartsWith("Brazier 5:1@2: on,", SampleBraziers.StatusReply("5:1@2", state));
        Assert.Equal("Brazier 5:1@2 is now on. It burns during the day while fuel is inside.",
            SampleBraziers.SetReply("5:1@2", new BrazierToggleResult { Ok = true, Changed = true, Enabled = true }));
        Assert.Equal("Brazier 5:1@2 is now off (no change).",
            SampleBraziers.SetReply("5:1@2", new BrazierToggleResult { Ok = true, Changed = false, Enabled = false }));
        Assert.Equal("Brazier 5:1@2: refused, the entity has no BurnContainer (not a brazier).",
            SampleBraziers.SetReply("5:1@2", new BrazierToggleResult { Ok = false, Reason = "the entity has no BurnContainer (not a brazier)" }));
    }

    // ---- sort event ----------------------------------------------------------------------------

    [Fact]
    public void NetworkId_words_are_normalized_like_vanilla_panel_targets()
    {
        // Normal id: generation keeps its low byte, type keeps its low byte.
        Assert.Equal((40, 1, 1), InventorySortFilter.Normalize(40, 0x7A01, 0x3301));
        // Static map id keeps its own second word.
        Assert.Equal((9, 0x12345, 2), InventorySortFilter.Normalize(9, 0x12345, 0x0002));
        Assert.Equal((0, 0, 0), InventorySortFilter.Normalize(0, 0, 0));
    }

    [Fact]
    public void Filter_drops_the_same_entity_and_a_too_close_repeat()
    {
        var f = new InventorySortFilter();
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var first = f.Admit(1, InventorySortScope.Single, InventorySortTarget.Own, 40, 1, 1, 1000, t0);
        Assert.NotNull(first);
        Assert.Equal(1, first!.Sequence);
        Assert.Equal(-1, first.SincePreviousMs);
        Assert.Equal(1000, first.TimestampMs);

        // The same request entity seen on a later frame: dropped by identity.
        Assert.Null(f.Admit(1, InventorySortScope.Single, InventorySortTarget.Own, 40, 1, 1, 1100, t0));
        // A second entity for the same inventory within the duplicate window: dropped.
        Assert.Null(f.Admit(2, InventorySortScope.Single, InventorySortTarget.Own, 40, 1, 1, 1000 + InventorySortFilter.DuplicateWindowMs - 1, t0));
        Assert.Equal(2, f.Dropped);

        // A deliberate double press (250 ms later) is a second event with its interval.
        var second = f.Admit(3, InventorySortScope.Single, InventorySortTarget.Own, 40, 1, 1, 1250, t0);
        Assert.NotNull(second);
        Assert.Equal(2, second!.Sequence);
        Assert.Equal(250, second.SincePreviousMs);

        // Another inventory or the other scope has its own interval.
        var other = f.Admit(4, InventorySortScope.Single, InventorySortTarget.Other, 77, 2, 1, 1260, t0);
        Assert.Equal(-1, other!.SincePreviousMs);
        var all = f.Admit(5, InventorySortScope.All, InventorySortTarget.Own, 40, 1, 1, 1270, t0);
        Assert.Equal(-1, all!.SincePreviousMs);
    }

    [Fact]
    public void Filter_forgets_old_entities_and_clears()
    {
        var f = new InventorySortFilter();
        var t0 = DateTime.UtcNow;
        long now = 0;
        for (int i = 0; i < InventorySortFilter.RememberedEntities + 1; i++)
        {
            now += 1000;
            Assert.NotNull(f.Admit(100 + i, InventorySortScope.Single, InventorySortTarget.Own, 1, 1, 1, now, t0));
        }
        // The first identity fell out of the remembered set, so it is admitted again (as a fresh request).
        now += 1000;
        Assert.NotNull(f.Admit(100, InventorySortScope.Single, InventorySortTarget.Own, 1, 1, 1, now, t0));

        f.Clear();
        var after = f.Admit(100, InventorySortScope.Single, InventorySortTarget.Own, 1, 1, 1, now + 10, t0);
        Assert.NotNull(after);
        Assert.Equal(-1, after!.SincePreviousMs);
    }

    [Fact]
    public void Sort_event_matches_a_panel_target_and_formats()
    {
        var e = new InventorySortUsed(InventorySortScope.Single, InventorySortTarget.Other, 40, 1, 1, 5000, DateTime.UtcNow, 3, 250);
        Assert.True(e.HasNetworkId);
        Assert.True(e.IsFor(new VanillaPanelTarget(VanillaPanelKind.Container, 812, 3, 40, 1, 1)));
        Assert.False(e.IsFor(new VanillaPanelTarget(VanillaPanelKind.Container, 812, 3, 41, 1, 1)));
        Assert.False(e.IsFor(VanillaPanelTarget.None));
        Assert.Equal("sort Single Other net=40:1 seq=3 since=250ms", e.ToString());

        var none = new InventorySortUsed(InventorySortScope.All, InventorySortTarget.Unknown, 0, 0, 0, 0, DateTime.UtcNow, 1, -1);
        Assert.False(none.HasNetworkId);
        Assert.False(none.IsFor(VanillaPanelTarget.None));
        Assert.Equal("sort All Unknown net=none seq=1 since=first", none.ToString());
    }

    // ---- player positions ----------------------------------------------------------------------

    private static PlayerInfo Player(string name, ulong id) =>
        new() { Account = new PlayerAccountId(id), DisplayName = name, IsOnline = true };

    [Fact]
    public void Positions_measure_horizontally()
    {
        var a = new PlayerPosition(Player("Alpha", 1), 3f, 50f, 4f);
        Assert.Equal(5f, a.HorizontalDistanceTo(0f, 0f), 4);
        Assert.Equal(3f, a.Position.X);
        Assert.Equal(50f, a.Position.Y);
        Assert.Equal(4f, a.Position.Z);
        Assert.Equal("Alpha @ 3.0, 50.0, 4.0", a.ToString());
        Assert.Throws<ArgumentNullException>(() => new PlayerPosition(null!, 0, 0, 0));
    }

    [Fact]
    public void Within_and_AnyWithin_are_inclusive_and_sorted()
    {
        var list = new List<PlayerPosition>
        {
            new(Player("Far", 1), 30f, 0f, 0f),
            new(Player("Edge", 2), 0f, 0f, 20f),
            new(Player("Near", 3), 1f, 9f, 1f),
        };
        var hits = PlayerPositionMath.Within(list, 0f, 0f, 20f);
        Assert.Equal(new[] { "Near", "Edge" }, hits.ConvertAll(p => p.Player.DisplayName));
        Assert.True(PlayerPositionMath.AnyWithin(list, 0f, 0f, 20f));
        Assert.False(PlayerPositionMath.AnyWithin(list, 100f, 100f, 20f));
        Assert.Empty(PlayerPositionMath.Within(Array.Empty<PlayerPosition>(), 0f, 0f, 5f));
        Assert.Throws<ArgumentNullException>(() => PlayerPositionMath.AnyWithin(null!, 0f, 0f, 1f));
    }

    private static PlayerInfo Row(ulong id, bool online, int epoch, bool character = true) =>
        new()
        {
            Account = new PlayerAccountId(id), DisplayName = "P" + id, IsOnline = online, WorldEpoch = epoch,
            Session = new PlayerSessionRef(10, 1, character ? 20 : 0, character ? 3 : 0, 0),
        };

    [Fact]
    public void A_kept_online_row_does_not_answer_after_a_disconnect()
    {
        var kept = Row(7, online: true, epoch: 4);
        // The directory has no online row for the account any more: no position, whatever the kept row says.
        Assert.Null(PlayerPositionMath.CurrentRow(kept, null, 4));
        // The account's current row is used, not the kept one (a new character after a relog).
        var now = Row(7, online: true, epoch: 4);
        Assert.Same(now, PlayerPositionMath.CurrentRow(kept, now, 4));
        // A current row that is offline, another account, without a character, or from another epoch: refused.
        Assert.Null(PlayerPositionMath.CurrentRow(kept, Row(7, online: false, epoch: 4), 4));
        Assert.Null(PlayerPositionMath.CurrentRow(kept, Row(8, online: true, epoch: 4), 4));
        Assert.Null(PlayerPositionMath.CurrentRow(kept, Row(7, online: true, epoch: 4, character: false), 4));
        Assert.Null(PlayerPositionMath.CurrentRow(kept, Row(7, online: true, epoch: 3), 4));
        // A kept row from an earlier world load is refused even when the account is online again.
        Assert.Null(PlayerPositionMath.CurrentRow(Row(7, online: true, epoch: 3), now, 4));
        Assert.Null(PlayerPositionMath.CurrentRow(null, now, 4));
    }

    [Fact]
    public void Brazier_search_radius_must_be_finite_and_positive()
    {
        BrazierLogic.RequireRadius(20f);
        BrazierLogic.RequireRadius(0.001f);
        foreach (float bad in new[] { float.NaN, 0f, -1f, float.PositiveInfinity, float.NegativeInfinity })
            Assert.Throws<ArgumentOutOfRangeException>(() => BrazierLogic.RequireRadius(bad));
    }

    [Fact]
    public void Players_sample_where_parses_and_replies()
    {
        Assert.True(SamplePlayerDirectory.TryParse("where", "", out var kind, out _, out _));
        Assert.Equal(SamplePlayerDirectory.Kind.Where, kind);
        Assert.False(SamplePlayerDirectory.TryParse("where", "extra", out _, out _, out _));
        Assert.Equal("0 online with a position.", SamplePlayerDirectory.WhereReply(Array.Empty<(string, float, float, float)>()));
        Assert.Equal("2 online with a position. Alpha @ 1.0, 2.0, 3.0; (no name) @ -4.5, 0.0, 6.0",
            SamplePlayerDirectory.WhereReply(new[] { ("Alpha", 1f, 2f, 3f), ("", -4.5f, 0f, 6f) }));
    }
}
