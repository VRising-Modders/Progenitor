using System;
using System.Collections.Generic;
using System.Linq;
using Progenitor.Castles;
using Progenitor.Inventory;
using Progenitor.Materials;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Crafting from the Character window's Crafting tab (<see cref="MaterialOperation.CharacterCraft"/>):
/// the same check-then-pay transaction as a workstation craft, with no station, governed only while the
/// character stands on a plot.
/// </summary>
public partial class MaterialServiceTests
{
    private const MaterialOperation CharacterCraft = MaterialOperation.CharacterCraft;

    [Fact]
    public void Character_crafting_is_wired_and_needs_a_standing_plot()
    {
        Assert.Contains(CharacterCraft, MaterialOperations.Supported);
        Assert.True(MaterialOperations.IsSupported(CharacterCraft));
        Assert.True(MaterialOperations.RequiresStandingPlot(CharacterCraft));
        foreach (var op in new[] { MaterialOperation.Build, MaterialOperation.Craft, MaterialOperation.Repair, MaterialOperation.Upgrade })
            Assert.False(MaterialOperations.RequiresStandingPlot(op));
        // Its own value: the earlier operations keep theirs (they travel on the wire as bytes).
        Assert.Equal(6, (int)CharacterCraft);
        Assert.Equal(5, (int)MaterialOperation.Other);

        var registry = new MaterialSourceRegistry { HookState = () => (true, "") };
        registry.Register(new OrderedProvider { Operations = new[] { CharacterCraft } }, "mod.one");
        Assert.Equal(new[] { CharacterCraft }, registry.Status().Wired);
        Assert.True(registry.Governs(CharacterCraft));
        Assert.False(registry.Governs(MaterialOperation.Craft));
    }

    [Fact]
    public void A_workstation_crafting_provider_does_not_govern_character_crafting()
    {
        var (world, provider, registry, service, paid, _) = Setup(); // the provider governs Build and Craft
        provider.All.Add(world.Chest(1, (Iron, 10)));
        registry.Register(provider, "test");
        service.BeginScope(1);
        Assert.False(service.CountGate(world.Player, CharacterCraft));
        Assert.False(service.GovernsLine(world.Player, CharacterCraft, Iron));
        Assert.Equal(4, service.Pay(world.Player, CharacterCraft, Iron, 4).Remainder);
        service.EndScope();
        Assert.Empty(paid);
        Assert.Empty(provider.Seen);
    }

    [Fact]
    public void A_character_craft_pays_every_line_from_the_checks_answer_with_one_transaction()
    {
        var (world, provider, registry, service, paid, log) = Setup();
        provider.Operations = new[] { CharacterCraft };
        var a = world.Chest(1, (Iron, 10));
        var b = world.Chest(2, (Iron, 10), (Wood, 6));
        provider.All.AddRange(new[] { a, b });
        provider.Orders[Iron] = new List<ContainerId> { b, a };
        registry.Register(provider, "test");
        service.BeginScope(1);
        var rows = Check(service, world.Player, CharacterCraft);
        Assert.Equal(20, rows.Single(r => r.Item == Iron).Amount);
        Assert.Equal(6, rows.Single(r => r.Item == Wood).Amount);
        var ctx = provider.Seen.Last();
        Assert.Equal(CharacterCraft, ctx.Operation);
        Assert.True(ctx.Station.IsNone); // no workstation
        Assert.Equal(world.Territory, ctx.StandingTerritory);

        provider.Orders[Iron] = new List<ContainerId> { a, b }; // too late: the operation keeps its answer
        Assert.Equal(0, PayLine(service, world.Player, CharacterCraft, Iron, 12));
        Assert.Equal(0, PayLine(service, world.Player, CharacterCraft, Wood, 6));
        Assert.Equal(0, world.Get(b, Iron));
        Assert.Equal(8, world.Get(a, Iron));
        Assert.Empty(paid); // published only when the system's update ends
        service.EndScope();

        Assert.Equal(2, paid.Count);
        Assert.All(paid, p => Assert.Equal(CharacterCraft, p.Context.Operation));
        Assert.All(paid, p => Assert.True(p.Context.Station.IsNone));
        Assert.Single(paid.Select(p => p.Transaction).Distinct());
        Assert.Equal(new[] { new MaterialDebit(b, 10).ToString(), new MaterialDebit(a, 2).ToString() },
            paid.Single(p => p.Item == Iron).Debits.Select(d => d.ToString()));
        Assert.Single(log, l => l.StartsWith("materials: check CharacterCraft", StringComparison.Ordinal));
        Assert.Equal(2, registry.Status().Payments);
    }

    [Fact]
    public void Off_a_plot_a_character_craft_keeps_the_games_own_rules_and_the_provider_is_not_asked()
    {
        var (world, provider, registry, service, paid, _) = Setup();
        provider.Operations = new[] { CharacterCraft };
        provider.All.Add(world.Chest(1, (Iron, 10)));
        registry.Register(provider, "test");
        world.Territory = -1;
        service.BeginScope(1);
        Assert.False(service.CountGate(world.Player, CharacterCraft));
        var extra = new List<MaterialLine>();
        Assert.False(service.ComposeExtraRows(world.Player, CharacterCraft, new Dictionary<int, long>(), new Dictionary<int, long>(), extra));
        Assert.Equal(-1, PayLine(service, world.Player, CharacterCraft, Iron, 4));
        Assert.Equal(4, service.Pay(world.Player, CharacterCraft, Iron, 4).Remainder);
        service.EndScope();
        Assert.Empty(paid);
        Assert.Empty(provider.Seen);
        Assert.Empty(provider.SourcesCalls);
        Assert.Equal(10, world.Get(provider.All[0], Iron));
        Assert.False(service.TryView(world.Player, CharacterCraft, new Dictionary<int, int>(), out var ctx, out _));
        Assert.Equal(-1, ctx.StandingTerritory);

        // Back on the plot: governed again.
        world.Territory = 4;
        Assert.True(service.TryView(world.Player, CharacterCraft, new Dictionary<int, int>(), out ctx, out _));
        Assert.Equal(4, ctx.StandingTerritory);
    }

    [Fact]
    public void A_character_craft_leaves_items_the_provider_returns_null_for_to_the_game()
    {
        var (world, provider, registry, service, paid, _) = Setup();
        provider.Operations = new[] { CharacterCraft };
        var a = world.Chest(1, (Iron, 10), (Wood, 10));
        provider.All.Add(a);
        provider.LeaveToGame.Add(Wood);
        registry.Register(provider, "test");
        service.BeginScope(1);
        // The game's own shared storage holds 7 wood: that item keeps the game's amount.
        var rows = Check(service, world.Player, CharacterCraft, new Dictionary<int, long> { [Wood] = 7 });
        Assert.Equal(7, rows.Single(r => r.Item == Wood).Amount);
        Assert.Equal(10, rows.Single(r => r.Item == Iron).Amount);
        Assert.Equal(-1, PayLine(service, world.Player, CharacterCraft, Wood, 3)); // the game's own removal pays it
        Assert.Equal(0, PayLine(service, world.Player, CharacterCraft, Iron, 3));
        service.EndScope();
        Assert.Equal(10, world.Get(a, Wood));
        Assert.Equal(Iron, paid.Single().Item);
    }

    [Fact]
    public void A_character_craft_shortfall_keeps_earlier_lines_and_logs_once()
    {
        var (world, provider, registry, service, paid, log) = Setup();
        provider.Operations = new[] { CharacterCraft };
        var a = world.Chest(1, (Iron, 5));
        var b = world.Chest(2, (Wood, 4), (Cloth, 2));
        provider.All.AddRange(new[] { a, b });
        registry.Register(provider, "test");
        service.BeginScope(1);
        Check(service, world.Player, CharacterCraft);
        Assert.Equal(0, PayLine(service, world.Player, CharacterCraft, Iron, 5));
        world.Stock[(world.Ids[b], Wood)] = 1; // something took from b between the check and the line
        world.Stock[(world.Ids[b], Cloth)] = 0;
        Assert.Equal(3, PayLine(service, world.Player, CharacterCraft, Wood, 4));
        Assert.Equal(2, PayLine(service, world.Player, CharacterCraft, Cloth, 2));
        Assert.Equal(0, world.Get(a, Iron)); // the earlier line keeps its debit
        service.EndScope();
        Assert.Equal(new[] { Iron, Wood }, paid.Select(p => p.Item));
        Assert.Equal(1, paid.Single(p => p.Item == Wood).Paid);
        Assert.Equal(1, registry.Status().Shortfalls);
        Assert.Single(log, l => l.Contains("shortfall at payment") && l.Contains("CharacterCraft"));
    }

    [Fact]
    public void A_character_craft_short_at_the_check_shows_the_short_amount_and_nothing_is_taken()
    {
        var (world, provider, registry, service, paid, _) = Setup();
        provider.Operations = new[] { CharacterCraft };
        var a = world.Chest(1, (Iron, 5));
        provider.All.Add(a);
        provider.Reserves[Iron] = 2;
        registry.Register(provider, "test");
        service.BeginScope(1);
        var rows = Check(service, world.Player, CharacterCraft);
        Assert.Equal(3, rows.Single(r => r.Item == Iron).Amount); // the game's check refuses a cost of 4
        service.EndScope(); // the game refused: no payment followed
        Assert.Empty(paid);
        Assert.Equal(5, world.Get(a, Iron));
        Assert.Equal(0, registry.Status().Shortfalls);
    }

    [Fact]
    public void A_character_craft_with_a_window_open_leaves_that_inventory_out_of_the_pool()
    {
        var (world, provider, registry, service, paid, _) = Setup();
        provider.Operations = new[] { CharacterCraft };
        world.Window = new InventoryRef(70, 1, 0, Epoch);
        var windowId = new ContainerId(Guid.NewGuid());
        world.Ids[windowId] = world.Window;
        world.Stock[(world.Window, Iron)] = 4; // the game's own pass counts and pays it
        var chest = world.Chest(1, (Iron, 10));
        provider.All.AddRange(new[] { windowId, chest }); // a provider that lists the open chest
        registry.Register(provider, "test");
        service.BeginScope(1);
        var rows = Check(service, world.Player, CharacterCraft);
        Assert.Equal(10, rows.Single(r => r.Item == Iron).Amount);
        Assert.Equal(0, PayLine(service, world.Player, CharacterCraft, Iron, 6));
        service.EndScope();
        Assert.Equal(4, world.Get(world.Window, Iron));
        Assert.Equal(4, world.Get(chest, Iron));
        Assert.Equal(chest, paid.Single().Debits.Single().Container);
    }

    [Fact]
    public void A_character_craft_and_a_workstation_craft_are_separate_operations()
    {
        var (world, provider, registry, service, paid, _) = Setup();
        provider.Operations = new[] { MaterialOperation.Craft, CharacterCraft };
        var a = world.Chest(1, (Iron, 10));
        provider.All.Add(a);
        registry.Register(provider, "test");
        service.BeginScope(1);
        Check(service, world.Player, CharacterCraft);
        Assert.Equal(0, PayLine(service, world.Player, CharacterCraft, Iron, 3));
        service.EndScope();
        service.BeginScope(2);
        Check(service, world.Player, MaterialOperation.Craft);
        Assert.Equal(0, PayLine(service, world.Player, MaterialOperation.Craft, Iron, 2));
        service.EndScope();
        Assert.Equal(new[] { CharacterCraft, MaterialOperation.Craft }, paid.Select(p => p.Context.Operation));
        Assert.Equal(2, paid.Select(p => p.Transaction).Distinct().Count());
        Assert.Equal(5, world.Get(a, Iron));
    }

    [Fact]
    public void Off_a_plot_the_refresh_reuses_the_character_craft_entry_without_asking_the_provider()
    {
        var (world, provider, registry, service, _, _) = Setup();
        provider.Operations = new[] { CharacterCraft };
        provider.All.Add(world.Chest(1, (Iron, 9)));
        registry.Register(provider, "test");
        world.Territory = -1;

        Assert.Equal(MaterialViewOutcome.NotGoverned, service.RefreshView(world.Player, CharacterCraft, new MaterialStockReads(world), false, out _));
        Assert.Equal(MaterialViewOutcome.NotGoverned, service.RefreshView(world.Player, CharacterCraft, new MaterialStockReads(world), false, out _));
        Assert.Equal(1, registry.Status().Recomputes);
        Assert.Equal(1, registry.Status().Skips);
        Assert.Equal(0, provider.RevisionCalls); // not even its revision
        Assert.Empty(provider.Seen);

        // Back on the plot: the context changed, so the view is recomputed at once.
        world.Territory = 4;
        Assert.Equal(MaterialViewOutcome.Computed, service.RefreshView(world.Player, CharacterCraft, new MaterialStockReads(world), false, out var view));
        Assert.Equal(4, view.Context.StandingTerritory);
        Assert.Equal(9, view.Rows.Single(r => r.Item == Iron).Amount);
    }

    [Fact]
    public void A_context_the_provider_does_not_govern_is_reused_while_its_revision_holds()
    {
        var (world, provider, registry, service, _, _) = Setup();
        provider.Operations = new[] { CharacterCraft };
        registry.Register(provider, "test"); // no candidates: not governed on this plot

        Assert.Equal(MaterialViewOutcome.NotGoverned, service.RefreshView(world.Player, CharacterCraft, new MaterialStockReads(world), false, out _));
        Assert.Equal(MaterialViewOutcome.NotGoverned, service.RefreshView(world.Player, CharacterCraft, new MaterialStockReads(world), false, out _));
        Assert.Equal(1, registry.Status().Recomputes);
        Assert.Equal(1, registry.Status().Skips);
        Assert.Single(provider.Seen); // candidates asked once

        // The provider starts governing and says so through its revision: recomputed.
        provider.All.Add(world.Chest(1, (Iron, 5)));
        provider.Rev++;
        Assert.Equal(MaterialViewOutcome.Computed, service.RefreshView(world.Player, CharacterCraft, new MaterialStockReads(world), false, out var view));
        Assert.Equal(5, view.Rows.Single(r => r.Item == Iron).Amount);
    }

    [Fact]
    public void Character_craft_views_go_only_to_clients_that_read_them()
    {
        Assert.True(MaterialClientPolicy.ReadByPeer(CharacterCraft, true));
        Assert.False(MaterialClientPolicy.ReadByPeer(CharacterCraft, false));
        foreach (var op in MaterialClientPolicy.ViewOperations.Where(o => o != CharacterCraft && o != MaterialOperation.RefineView))
            Assert.True(MaterialClientPolicy.ReadByPeer(op, false));
        Assert.True((Progenitor.Networking.Handshake.SelfCapabilities & Progenitor.Networking.Handshake.Capability.MaterialCharacterCraft) != 0);
    }

    [Fact]
    public void Character_craft_views_list_only_governed_items()
    {
        var (world, provider, registry, service, _, _) = Setup();
        provider.Operations = new[] { CharacterCraft };
        provider.All.Add(world.Chest(1, (Iron, 9), (Wood, 4)));
        provider.LeaveToGame.Add(Wood);
        registry.Register(provider, "test");
        var totals = new Dictionary<int, int>();
        Assert.True(service.TryView(world.Player, CharacterCraft, totals, out var ctx, out _));
        Assert.Equal(CharacterCraft, ctx.Operation);
        Assert.True(ctx.Station.IsNone);
        Assert.Equal(9, totals[Iron]);
        Assert.False(totals.ContainsKey(Wood));
        Assert.False(service.TryView(world.Player, MaterialOperation.Craft, new Dictionary<int, int>(), out _, out _));
    }
}
