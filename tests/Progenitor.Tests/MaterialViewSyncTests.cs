using System;
using System.Collections.Generic;
using System.Linq;
using Progenitor.Castles;
using Progenitor.Materials;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// The per-player totals end to end without a game: the server's refresh policy and publisher, a
/// transport that can drop packets, and the client's store and selection rules.
/// </summary>
public class MaterialViewSyncTests
{
    private const int Cloth = 100, Iron = 200, Wood = 300;
    private const ulong Heart = 0x0000000A00000001UL, OtherHeart = 0x0000000B00000001UL;
    private const ulong Bench = 0x0000001400000003UL, OtherBench = 0x0000001500000003UL;

    /// <summary>One server, one client, and a link between them that can lose packets.</summary>
    private sealed class Rig
    {
        public readonly MaterialRefreshPolicy Policy = new();
        public readonly MaterialViewPublisher Publisher = new();
        public MaterialOverlayStore Client = new();
        public readonly CastleId User = new(9, 1);
        public int Epoch = 3;
        public long Now;
        /// <summary>Content per operation; null = not governed there.</summary>
        public readonly Dictionary<MaterialOperation, MaterialViewContent?> Content = new()
        {
            [MaterialOperation.Build] = null,
            [MaterialOperation.Craft] = null,
            [MaterialOperation.Upgrade] = null,
            [MaterialOperation.CharacterCraft] = null,
            [MaterialOperation.RefineView] = null,
        };
        /// <summary>What the transport does with the next packets: true delivers, false fails the send, null accepts it and loses it.</summary>
        public readonly Queue<bool?> Link = new();
        public readonly List<MaterialView> Sent = new();
        public bool Dirty;

        /// <summary>One frame: run the refresh like the runtime does. Returns packets put on the wire.</summary>
        public int Frame(long advanceMs = 0, bool invalidateAll = false)
        {
            Now += advanceMs;
            if (!Policy.Tick(Now, invalidateAll, Dirty, out bool everyone, out bool force)) return 0;
            Dirty = false;
            int count = 0;
            foreach (var op in MaterialClientPolicy.ViewOperations)
            {
                var view = Publisher.Next(User, op, Epoch, Content[op], force);
                if (view == null) continue;
                count++;
                Sent.Add(view);
                bool? outcome = Link.Count > 0 ? Link.Dequeue() : true;
                // Through the real wire format, as the client would receive it.
                if (outcome == true)
                {
                    Assert.True(MaterialViewWire.TryDecode(MaterialViewWire.Encode(view), out var decoded, out _));
                    Client.Apply(decoded!);
                }
                Publisher.Sent(User, op, outcome != false);
            }
            return count;
        }

        public int ClientShows(MaterialOperation op, int item) =>
            Client.TryGet(op, out IReadOnlyDictionary<int, int> totals) && totals.TryGetValue(item, out int n) ? n : -1;
    }

    private static MaterialViewContent Build(params (int Item, int Amount)[] rows) =>
        new(5, 1, Heart, 0, rows.Select(r => new MaterialLine(r.Item, r.Amount)).ToArray());

    [Fact]
    public void An_unchanged_view_is_sent_once_and_a_change_is_sent_at_the_next_refresh()
    {
        var rig = new Rig();
        rig.Content[MaterialOperation.Build] = Build((Cloth, 12));
        Assert.Equal(1, rig.Frame());
        Assert.Equal(12, rig.ClientShows(MaterialOperation.Build, Cloth));
        Assert.Equal(0, rig.Frame(MaterialRefreshPolicy.IntervalMs));
        rig.Content[MaterialOperation.Build] = Build((Cloth, 7));
        Assert.Equal(0, rig.Frame(10)); // not due, nothing dirty
        Assert.Equal(1, rig.Frame(MaterialRefreshPolicy.IntervalMs));
        Assert.Equal(7, rig.ClientShows(MaterialOperation.Build, Cloth));
    }

    [Fact]
    public void A_payment_or_invalidation_refreshes_at_once()
    {
        var rig = new Rig();
        rig.Content[MaterialOperation.Build] = Build((Cloth, 12));
        rig.Frame();
        rig.Content[MaterialOperation.Build] = Build((Cloth, 4));
        rig.Dirty = true;
        Assert.Equal(1, rig.Frame(5));
        Assert.Equal(4, rig.ClientShows(MaterialOperation.Build, Cloth));
    }

    [Fact]
    public void A_failed_send_is_retried_on_the_next_refresh_even_when_unchanged()
    {
        var rig = new Rig();
        rig.Content[MaterialOperation.Build] = Build((Cloth, 12));
        rig.Link.Enqueue(false);
        Assert.Equal(1, rig.Frame());
        Assert.Equal(-1, rig.ClientShows(MaterialOperation.Build, Cloth));
        Assert.Equal(1, rig.Frame(MaterialRefreshPolicy.IntervalMs));
        Assert.Equal(12, rig.ClientShows(MaterialOperation.Build, Cloth));
        Assert.True(rig.Sent[1].Sequence > rig.Sent[0].Sequence);
    }

    [Fact]
    public void A_packet_lost_after_the_transport_accepted_it_is_repaired_by_the_periodic_resend()
    {
        var rig = new Rig();
        rig.Content[MaterialOperation.Build] = Build((Cloth, 12));
        rig.Link.Enqueue(null);
        rig.Frame();
        Assert.Equal(-1, rig.ClientShows(MaterialOperation.Build, Cloth));
        // Unchanged views are not resent every second ...
        for (int i = 0; i < 8; i++) Assert.Equal(0, rig.Frame(MaterialRefreshPolicy.IntervalMs));
        // ... but every player gets theirs again within the resync interval.
        rig.Frame(MaterialRefreshPolicy.IntervalMs);
        rig.Frame(MaterialRefreshPolicy.IntervalMs);
        Assert.Equal(12, rig.ClientShows(MaterialOperation.Build, Cloth));
    }

    [Fact]
    public void A_reconnect_gets_its_view_at_the_next_refresh_with_a_newer_sequence()
    {
        var rig = new Rig();
        rig.Content[MaterialOperation.Build] = Build((Cloth, 12));
        rig.Frame();
        uint before = rig.Sent.Last().Sequence;
        // Disconnect: the client drops its views; the server forgets what it sent.
        rig.Client.Clear();
        rig.Publisher.Disconnected(rig.User);
        Assert.Equal(-1, rig.ClientShows(MaterialOperation.Build, Cloth));
        Assert.Equal(1, rig.Frame(MaterialRefreshPolicy.IntervalMs));
        Assert.Equal(12, rig.ClientShows(MaterialOperation.Build, Cloth));
        Assert.True(rig.Sent.Last().Sequence > before);
    }

    [Fact]
    public void A_reconnect_the_server_did_not_notice_is_repaired_by_the_periodic_resend()
    {
        var rig = new Rig();
        rig.Content[MaterialOperation.Build] = Build((Cloth, 12));
        rig.Frame();
        rig.Client = new MaterialOverlayStore(); // client Core reload or a reconnect the server never saw
        long waited = 0;
        while (rig.ClientShows(MaterialOperation.Build, Cloth) != 12 && waited <= MaterialRefreshPolicy.ResyncMs)
        {
            rig.Frame(MaterialRefreshPolicy.IntervalMs);
            waited += MaterialRefreshPolicy.IntervalMs;
        }
        Assert.Equal(12, rig.ClientShows(MaterialOperation.Build, Cloth));
    }

    [Fact]
    public void A_late_packet_never_replaces_a_newer_view()
    {
        var store = new MaterialOverlayStore();
        var newer = new MaterialView(MaterialOperation.Build, 3, 1, 5, 5, false, false, new[] { new MaterialLine(Cloth, 2) }, Heart);
        var older = new MaterialView(MaterialOperation.Build, 3, 1, 4, 5, false, false, new[] { new MaterialLine(Cloth, 99) }, Heart);
        Assert.True(store.Apply(newer));
        Assert.False(store.Apply(older));
        Assert.True(store.TryGet(MaterialOperation.Build, out IReadOnlyDictionary<int, int> t));
        Assert.Equal(2, t[Cloth]);
    }

    [Fact]
    public void Removing_the_provider_clears_every_client_that_had_a_view_and_retries_a_lost_clear()
    {
        var rig = new Rig();
        rig.Content[MaterialOperation.Build] = Build((Cloth, 12));
        rig.Frame();
        rig.Content[MaterialOperation.Build] = null;
        rig.Link.Enqueue(false); // the clear fails
        Assert.Equal(1, rig.Frame(0, invalidateAll: true));
        Assert.Equal(12, rig.ClientShows(MaterialOperation.Build, Cloth));
        Assert.Equal(1, rig.Frame(MaterialRefreshPolicy.IntervalMs));
        Assert.Equal(-1, rig.ClientShows(MaterialOperation.Build, Cloth));
        Assert.True(rig.Sent.Last().Cleared);
        // Cleared once: no further packets for a player who has nothing to clear.
        Assert.Equal(0, rig.Frame(MaterialRefreshPolicy.IntervalMs));
        Assert.Equal(0, rig.Frame(MaterialRefreshPolicy.ResyncMs, invalidateAll: true));
    }

    [Fact]
    public void A_player_who_never_had_a_view_is_never_sent_a_clear()
    {
        var rig = new Rig();
        Assert.Equal(0, rig.Frame());
        Assert.Equal(0, rig.Frame(MaterialRefreshPolicy.ResyncMs, invalidateAll: true));
        Assert.Empty(rig.Sent);
    }

    [Fact]
    public void A_new_world_epoch_starts_the_client_over()
    {
        var rig = new Rig();
        rig.Content[MaterialOperation.Build] = Build((Cloth, 12));
        rig.Frame();
        // The client has seen a far higher sequence from this publisher in the old world.
        Assert.True(rig.Client.Apply(new MaterialView(MaterialOperation.Build, 3, 1, 1000, 5, false, false,
            new[] { new MaterialLine(Cloth, 12) }, Heart, 0, rig.Publisher.Generation)));
        rig.Epoch = 4;
        rig.Content[MaterialOperation.Build] = Build((Cloth, 3));
        rig.Frame(MaterialRefreshPolicy.IntervalMs);
        Assert.True(rig.Sent.Last().Sequence < 1000); // accepted only because the epoch changed
        Assert.Equal(3, rig.ClientShows(MaterialOperation.Build, Cloth));
    }

    [Fact]
    public void Refresh_policy_timeline()
    {
        var p = new MaterialRefreshPolicy();
        Assert.True(p.Tick(0, false, false, out bool everyone, out bool force));
        Assert.True(everyone && force);
        Assert.False(p.Tick(500, false, false, out _, out _));
        Assert.True(p.Tick(600, false, true, out everyone, out force));
        Assert.False(everyone || force);
        Assert.True(p.Tick(1000, false, false, out everyone, out force));
        Assert.True(everyone);
        Assert.False(force);
        Assert.True(p.Tick(10_000, false, false, out everyone, out force));
        Assert.True(everyone && force);
        p.ForceAll();
        Assert.True(p.Tick(10_001, false, false, out everyone, out force));
        Assert.True(everyone && force);
    }

    // ---- client selection ------------------------------------------------------------------------

    private static MaterialOverlayStore StoreWith(ulong buildHeart = Heart, ulong craftHeart = Heart, ulong craftStation = Bench)
    {
        var store = new MaterialOverlayStore();
        store.Apply(new MaterialView(MaterialOperation.Build, 3, 1, 1, 5, false, false, new[] { new MaterialLine(Cloth, 30) }, buildHeart));
        store.Apply(new MaterialView(MaterialOperation.Craft, 3, 1, 2, 5, false, false, new[] { new MaterialLine(Iron, 8) }, craftHeart, craftStation));
        return store;
    }

    [Fact]
    public void Craft_totals_apply_only_at_the_station_and_plot_they_were_computed_for()
    {
        var store = StoreWith();
        var at = new MaterialClientCall { LocalCharacter = true, HeartNet = Heart, TargetNet = Bench, TargetIsWorkstation = true };
        Assert.True(MaterialClientPolicy.TrySelect(store, at, out var op, out var totals));
        Assert.Equal(MaterialOperation.Craft, op);
        Assert.Equal(8, totals[Iron]);
        Assert.False(MaterialClientPolicy.TrySelect(store, at with { TargetNet = OtherBench }, out _, out _));
        Assert.False(MaterialClientPolicy.TrySelect(store, at with { HeartNet = OtherHeart }, out _, out _));
        Assert.False(MaterialClientPolicy.TrySelect(store, at with { LocalCharacter = false }, out _, out _));
    }

    [Fact]
    public void Build_totals_apply_only_while_building_on_their_plot()
    {
        var store = StoreWith();
        var building = new MaterialClientCall { LocalCharacter = true, HeartNet = Heart, BuildContext = true };
        Assert.True(MaterialClientPolicy.TrySelect(store, building, out var op, out var totals));
        Assert.Equal(MaterialOperation.Build, op);
        Assert.Equal(30, totals[Cloth]);
        // Another screen (repair, research, a chest) reading the same count keeps the game's own result.
        var other = building with { BuildContext = false };
        Assert.False(MaterialClientPolicy.TrySelect(store, other, out _, out _));
        Assert.False(MaterialClientPolicy.TrySelect(store, other with { TargetNet = 77 }, out _, out _));
        Assert.False(MaterialClientPolicy.TrySelect(store, building with { HeartNet = OtherHeart }, out _, out _));
        Assert.False(MaterialClientPolicy.TrySelect(store, building with { HeartNet = 0 }, out _, out _));
    }

    [Fact]
    public void While_building_a_lingering_workstation_target_never_shows_craft_totals()
    {
        // Built the way the client builds it: the raw build-menu flag and the workstation it still targets.
        var store = StoreWith();
        var call = MaterialClientPolicy.Call(localCharacter: true, heartNet: Heart, buildMenuOrPlacement: true, workstationNet: Bench);
        Assert.True(MaterialClientPolicy.TrySelect(store, call, out var op, out var totals));
        Assert.Equal(MaterialOperation.Build, op);
        Assert.False(totals.ContainsKey(Iron));
        // Building with no Build view shows the game's own counts, never the Craft view.
        var craftOnly = StoreWith();
        craftOnly.Apply(new MaterialView(MaterialOperation.Build, 3, 1, 9, 5, true, false, Array.Empty<MaterialLine>()));
        Assert.False(MaterialClientPolicy.TrySelect(craftOnly, call, out _, out _));
    }

    [Fact]
    public void The_client_call_is_built_from_raw_reads()
    {
        var atBench = MaterialClientPolicy.Call(true, Heart, false, Bench);
        Assert.True(atBench.LocalCharacter);
        Assert.Equal(Heart, atBench.HeartNet);
        Assert.False(atBench.BuildContext);
        Assert.True(atBench.TargetIsWorkstation);
        Assert.Equal(Bench, atBench.TargetNet);
        var nothing = MaterialClientPolicy.Call(true, Heart, false, 0);
        Assert.False(nothing.TargetIsWorkstation);
        Assert.False(MaterialClientPolicy.TrySelect(StoreWith(), nothing, out _, out _));
        Assert.True(MaterialClientPolicy.TrySelect(StoreWith(), atBench, out var op, out _));
        Assert.Equal(MaterialOperation.Craft, op);
        Assert.False(MaterialClientPolicy.TrySelect(StoreWith(), MaterialClientPolicy.Call(false, Heart, true, 0), out _, out _));
    }

    private static MaterialOverlayStore StoreWithUpgrade(ulong upgradeHeart = Heart)
    {
        var store = StoreWith();
        store.Apply(new MaterialView(MaterialOperation.Upgrade, 3, 1, 3, 5, false, false, new[] { new MaterialLine(Wood, 40) }, Heart, upgradeHeart));
        return store;
    }

    [Fact]
    public void Upgrade_totals_apply_only_with_their_hearts_window_open_and_counted_at_that_heart()
    {
        var store = StoreWithUpgrade();
        var atHeart = MaterialClientPolicy.Call(true, Heart, false, 0, heartTargetNet: Heart);
        Assert.Equal(Heart, atHeart.HeartTargetNet);
        Assert.True(MaterialClientPolicy.TrySelect(store, atHeart, out var op, out var totals));
        Assert.Equal(MaterialOperation.Upgrade, op);
        Assert.Equal(40, totals[Wood]);
        Assert.False(totals.ContainsKey(Iron)); // never the Craft view
        // Another heart's window, a count at another heart, another entity: the game's own counts.
        Assert.False(MaterialClientPolicy.TrySelect(store, atHeart with { HeartTargetNet = OtherHeart }, out _, out _));
        Assert.False(MaterialClientPolicy.TrySelect(store, atHeart with { HeartNet = OtherHeart }, out _, out _));
        Assert.False(MaterialClientPolicy.TrySelect(store, atHeart with { LocalCharacter = false }, out _, out _));
        // No heart window: the Upgrade view never applies.
        Assert.False(MaterialClientPolicy.TrySelect(store, MaterialClientPolicy.Call(true, Heart, false, 0), out _, out _));
        // Without an Upgrade view the heart window keeps the game's own counts.
        Assert.False(MaterialClientPolicy.TrySelect(StoreWith(), atHeart, out _, out _));
        // An Upgrade view without a heart never applies.
        Assert.False(MaterialClientPolicy.TrySelect(StoreWithUpgrade(0), atHeart with { HeartNet = 0, HeartTargetNet = 0 }, out _, out _));
    }

    [Fact]
    public void Upgrade_views_are_computed_only_for_hearts_the_player_or_the_clan_owns()
    {
        Assert.True(MaterialClientPolicy.MayViewUpgrade(Progenitor.Castles.CastleRelation.Owner));
        Assert.True(MaterialClientPolicy.MayViewUpgrade(Progenitor.Castles.CastleRelation.ClanMember));
        // An enemy's or a decaying heart's window (claim, raid, destroy), even an allied one, never shows pool totals.
        Assert.False(MaterialClientPolicy.MayViewUpgrade(Progenitor.Castles.CastleRelation.Allied));
        Assert.False(MaterialClientPolicy.MayViewUpgrade(Progenitor.Castles.CastleRelation.None));
    }

    [Fact]
    public void Build_and_craft_take_precedence_over_a_held_upgrade_view()
    {
        var store = StoreWithUpgrade();
        var building = MaterialClientPolicy.Call(true, Heart, true, 0, heartTargetNet: Heart);
        Assert.True(MaterialClientPolicy.TrySelect(store, building, out var op, out _));
        Assert.Equal(MaterialOperation.Build, op);
        var atBench = MaterialClientPolicy.Call(true, Heart, false, Bench, heartTargetNet: Heart);
        Assert.True(MaterialClientPolicy.TrySelect(store, atBench, out op, out _));
        Assert.Equal(MaterialOperation.Craft, op);
    }

    [Fact]
    public void No_repair_view_is_sent_or_selected()
    {
        Assert.DoesNotContain(MaterialOperation.Repair, MaterialClientPolicy.ViewOperations);
        Assert.True(MaterialClientPolicy.NeedsStation(MaterialOperation.Craft));
        Assert.True(MaterialClientPolicy.NeedsStation(MaterialOperation.Upgrade));
        Assert.False(MaterialClientPolicy.NeedsStation(MaterialOperation.Build));
        // Even a Repair view that somehow arrived is never selected: the repair tooltip keeps the game's count.
        var store = new MaterialOverlayStore();
        store.Apply(new MaterialView(MaterialOperation.Repair, 3, 1, 1, 5, false, false, new[] { new MaterialLine(Iron, 8) }, Heart, Bench));
        Assert.False(MaterialClientPolicy.TrySelect(store, MaterialClientPolicy.Call(true, Heart, false, 0), out _, out _));
        Assert.False(MaterialClientPolicy.TrySelect(store, MaterialClientPolicy.Call(true, Heart, false, Bench), out _, out _));
        Assert.False(MaterialClientPolicy.TrySelect(store, MaterialClientPolicy.Call(true, Heart, false, 0, Heart), out _, out _));
    }

    [Fact]
    public void Upgrade_views_round_trip_and_a_disconnect_forgets_them_too()
    {
        var rig = new Rig();
        rig.Content[MaterialOperation.Upgrade] = new MaterialViewContent(5, 1, Heart, Heart, new[] { new MaterialLine(Wood, 40) });
        Assert.Equal(1, rig.Frame());
        Assert.Equal(40, rig.ClientShows(MaterialOperation.Upgrade, Wood));
        Assert.True(rig.Client.TryGet(MaterialOperation.Upgrade, out MaterialView held, out _));
        Assert.Equal(Heart, held.StationNet);
        // The heart window closes: the server stops governing there and clears the client's view.
        rig.Content[MaterialOperation.Upgrade] = null;
        Assert.Equal(1, rig.Frame(MaterialRefreshPolicy.IntervalMs));
        Assert.Equal(-1, rig.ClientShows(MaterialOperation.Upgrade, Wood));
        rig.Publisher.Disconnected(rig.User);
        Assert.False(rig.Publisher.Book.HasView(rig.User, MaterialOperation.Upgrade));
    }

    [Fact]
    public void A_craft_view_without_a_station_never_applies()
    {
        var store = StoreWith(craftStation: 0);
        var call = new MaterialClientCall { LocalCharacter = true, HeartNet = Heart, TargetNet = 0, TargetIsWorkstation = true };
        Assert.False(MaterialClientPolicy.TrySelect(store, call, out _, out _));
    }

    [Fact]
    public void A_fresh_server_install_clears_every_online_client_once()
    {
        // A server Core swap: the new publisher does not know what the old one sent, and its sequences
        // restart (the old generation had already reached 40).
        var rig = new Rig();
        rig.Publisher.ClearEveryoneOnce();
        rig.Client.Apply(new MaterialView(MaterialOperation.Build, 3, 1, 40, 5, false, false, new[] { new MaterialLine(Cloth, 12) }, Heart, 0, 7));
        Assert.Equal(MaterialClientPolicy.ViewOperations.Count, rig.Frame()); // one cleared view per operation
        Assert.All(rig.Sent, v => Assert.True(v.Cleared));
        Assert.Equal(-1, rig.ClientShows(MaterialOperation.Build, Cloth));
        rig.Publisher.FirstRefreshDone();
        Assert.Equal(0, rig.Frame(MaterialRefreshPolicy.ResyncMs, invalidateAll: true));
    }

    [Fact]
    public void Without_a_view_the_game_counts_stand()
    {
        var store = new MaterialOverlayStore();
        var call = new MaterialClientCall { LocalCharacter = true, HeartNet = Heart, BuildContext = true };
        Assert.False(MaterialClientPolicy.TrySelect(store, call, out _, out _));
    }

    [Fact]
    public void Client_rows_replace_governed_items_and_keep_the_games_amount_for_the_rest()
    {
        var full = new Dictionary<int, long> { [Cloth] = 51, [Wood] = 9, [Iron] = 6 };
        var bag = new Dictionary<int, long> { [Cloth] = 1, [Wood] = 2 };
        var governed = new Dictionary<int, int> { [Cloth] = 10, [Iron] = 0, [999] = 4 };
        var extra = new List<MaterialLine>();
        MaterialClientPolicy.ComposeExtra(full, bag, governed, extra);
        Assert.Equal(new[] { new MaterialLine(Cloth, 10), new MaterialLine(Wood, 7), new MaterialLine(999, 4) }, extra);
    }

    // ---- CharacterCraft: the Character window's Crafting tab ----------------------------------------

    private static MaterialOverlayStore StoreWithCharacterCraft(ulong heart = Heart)
    {
        var store = StoreWithUpgrade();
        store.Apply(new MaterialView(MaterialOperation.CharacterCraft, 3, 1, 4, 5, false, false, new[] { new MaterialLine(Wood, 25) }, heart));
        return store;
    }

    [Fact]
    public void Character_craft_totals_apply_only_in_the_character_crafting_menu_at_the_standing_plots_heart()
    {
        var store = StoreWithCharacterCraft();
        var menu = MaterialClientPolicy.Call(true, Heart, false, 0, 0, characterCraft: true);
        Assert.True(menu.CharacterCraftContext);
        Assert.True(MaterialClientPolicy.TrySelect(store, menu, out var op, out var totals));
        Assert.Equal(MaterialOperation.CharacterCraft, op);
        Assert.Equal(25, totals[Wood]);
        // Another plot's heart, off a plot (the game resolved no heart), another entity: the game's own counts.
        Assert.False(MaterialClientPolicy.TrySelect(store, menu with { HeartNet = OtherHeart }, out _, out _));
        Assert.False(MaterialClientPolicy.TrySelect(store, menu with { HeartNet = 0 }, out _, out _));
        Assert.False(MaterialClientPolicy.TrySelect(store, menu with { LocalCharacter = false }, out _, out _));
        // Without a CharacterCraft view the menu keeps the game's own counts, even with other views held.
        Assert.False(MaterialClientPolicy.TrySelect(StoreWithUpgrade(), menu, out _, out _));
        // A CharacterCraft view computed off a plot (no heart) never applies.
        Assert.False(MaterialClientPolicy.TrySelect(StoreWithCharacterCraft(0), menu with { HeartNet = 0 }, out _, out _));
    }

    [Fact]
    public void Inside_the_character_crafting_menu_no_other_view_applies_and_outside_it_the_character_craft_view_never_does()
    {
        var store = StoreWithCharacterCraft();
        // The menu's count is a CharacterCraft count even when the build menu is shown or a workstation or
        // a heart window is the interaction target.
        var menu = MaterialClientPolicy.Call(true, Heart, true, Bench, Heart, characterCraft: true);
        Assert.True(MaterialClientPolicy.TrySelect(store, menu, out var op, out var totals));
        Assert.Equal(MaterialOperation.CharacterCraft, op);
        Assert.False(totals.ContainsKey(Cloth));
        Assert.False(totals.ContainsKey(Iron));
        // Outside the menu every count keeps its own operation; none of them is CharacterCraft.
        foreach (var call in new[]
        {
            MaterialClientPolicy.Call(true, Heart, true, 0),
            MaterialClientPolicy.Call(true, Heart, false, Bench),
            MaterialClientPolicy.Call(true, Heart, false, 0, Heart),
            MaterialClientPolicy.Call(true, Heart, false, 0),
        })
        {
            if (MaterialClientPolicy.TrySelect(store, call, out op, out _)) Assert.NotEqual(MaterialOperation.CharacterCraft, op);
        }
        Assert.False(MaterialClientPolicy.TrySelect(store, MaterialClientPolicy.Call(true, Heart, false, 0), out _, out _));
    }

    [Fact]
    public void Character_craft_views_are_sent_without_a_station_and_round_trip()
    {
        Assert.Contains(MaterialOperation.CharacterCraft, MaterialClientPolicy.ViewOperations);
        Assert.False(MaterialClientPolicy.NeedsStation(MaterialOperation.CharacterCraft));
        var rig = new Rig();
        rig.Content[MaterialOperation.CharacterCraft] = new MaterialViewContent(5, 1, Heart, 0, new[] { new MaterialLine(Wood, 25) });
        Assert.Equal(1, rig.Frame());
        Assert.Equal(25, rig.ClientShows(MaterialOperation.CharacterCraft, Wood));
        Assert.True(rig.Client.TryGet(MaterialOperation.CharacterCraft, out MaterialView held, out _));
        Assert.Equal(Heart, held.HeartNet);
        Assert.Equal(0UL, held.StationNet);
        // The character walks off the plot: the server stops governing there and clears the client's view.
        rig.Content[MaterialOperation.CharacterCraft] = null;
        Assert.Equal(1, rig.Frame(MaterialRefreshPolicy.IntervalMs));
        Assert.Equal(-1, rig.ClientShows(MaterialOperation.CharacterCraft, Wood));
    }

    [Fact]
    public void The_wire_accepts_character_craft_and_refine_view_and_refuses_unknown_operations()
    {
        var view = new MaterialView(MaterialOperation.CharacterCraft, 3, 1, 4, 5, false, false, new[] { new MaterialLine(Wood, 25) }, Heart);
        var bytes = MaterialViewWire.Encode(view);
        Assert.True(MaterialViewWire.TryDecode(bytes, out var decoded, out _));
        Assert.Equal(MaterialOperation.CharacterCraft, decoded!.Operation);
        bytes[1] = (byte)MaterialOperation.RefineView;
        Assert.True(MaterialViewWire.TryDecode(bytes, out _, out _));
        bytes[1] = (byte)MaterialOperation.RefineView + 1;
        Assert.False(MaterialViewWire.TryDecode(bytes, out _, out string reason));
        Assert.StartsWith("operation", reason);
    }
}
