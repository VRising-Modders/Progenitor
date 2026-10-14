using System;
using System.Collections.Generic;
using System.Linq;
using Progenitor.Castles;
using Progenitor.Inventory;
using Progenitor.Materials;
using Xunit;
using World = Progenitor.Tests.MaterialViewRefreshBenchTests.BenchWorld;

namespace Progenitor.Tests;

/// <summary>
/// The per-player view refresh: a reused view is exactly the view a full recompute gives, every
/// change it depends on recomputes it, each inventory is read once per refresh, and payment never
/// reads a cached view.
/// </summary>
public class MaterialViewRefreshTests
{
    /// <summary>Per-item spend orders, reserves and items left to the game, from a seed.</summary>
    private sealed class MixedProvider : IMaterialSourceProvider
    {
        private readonly List<ContainerId> _all;
        private readonly Dictionary<int, ItemSources?> _sources = new();
        public long Rev = 1;
        public int SourcesCalls, CandidatesCalls;
        public bool ThrowOnSources;

        public MixedProvider(List<ContainerId> all, int seed)
        {
            _all = all;
            var rng = new Random(seed);
            for (int i = 0; i < MaterialViewRefreshBenchTests.ItemTypes; i++)
            {
                int item = World.Item(i);
                int kind = rng.Next(5);
                if (kind == 0) { _sources[item] = null; continue; } // left to the game
                var order = all.OrderBy(_ => rng.Next()).Take(1 + rng.Next(all.Count)).ToList();
                _sources[item] = new ItemSources(order, kind == 1 ? rng.Next(200) : 0);
            }
        }

        public IReadOnlyCollection<MaterialOperation> Operations { get; } = new[]
            { MaterialOperation.Build, MaterialOperation.Craft, MaterialOperation.Repair, MaterialOperation.Upgrade };

        public long Revision(in MaterialContext context) => Rev;

        public IReadOnlyList<ContainerId> Candidates(in MaterialContext context)
        {
            CandidatesCalls++;
            return _all;
        }

        public ItemSources? Sources(in MaterialContext context, int item)
        {
            SourcesCalls++;
            if (ThrowOnSources) throw new InvalidOperationException("provider bug");
            return _sources.TryGetValue(item, out var s) ? s : new ItemSources(_all);
        }
    }

    private sealed class Rig
    {
        public readonly World World;
        public readonly MixedProvider Provider;
        public readonly MaterialSourceRegistry Registry = new() { HookState = () => (true, "") };
        public readonly MaterialService Service;
        public readonly MaterialStockReads Reads;
        public readonly List<string> Log = new();

        public Rig(int seed = 3)
        {
            World = new World(seed);
            Provider = new MixedProvider(World.ChestIds, seed);
            Registry.Register(Provider, "test");
            Service = new MaterialService(Registry, World, Log.Add);
            Reads = new MaterialStockReads(World);
        }

        /// <summary>One refresh of one operation, as the server runs it.</summary>
        public MaterialViewOutcome Refresh(MaterialOperation op, out MaterialViewResult view, bool recompute = false)
        {
            World.Now += 1000;
            Service.BeginFullRefresh();
            var outcome = Service.RefreshView(World.Player, op, Reads, recompute, out view);
            Reads.Clear();
            return outcome;
        }

        /// <summary>What a full recompute gives now (the previous code's view).</summary>
        public IReadOnlyList<MaterialLine> Fresh(MaterialOperation op)
        {
            var totals = new Dictionary<int, int>();
            Assert.True(Service.TryView(World.Player, op, totals, out _, out _));
            return totals.OrderBy(kv => kv.Key).Select(kv => new MaterialLine(kv.Key, kv.Value)).ToList();
        }

        public void Change(int chest, int slot, int item, int amount) =>
            World.Slots[World.ChestRefs[chest]][slot] = (item, amount);
    }

    private static readonly MaterialOperation[] ViewOps = { MaterialOperation.Build, MaterialOperation.Craft, MaterialOperation.Upgrade };

    [Fact]
    public void Refreshed_views_equal_a_full_recompute_through_changes()
    {
        for (int seed = 1; seed <= 5; seed++)
        {
            var rig = new Rig(seed);
            var rng = new Random(seed * 31);
            for (int step = 0; step < 40; step++)
            {
                // Sometimes change nothing, sometimes a chest, the bag, or the provider's revision.
                switch (rng.Next(5))
                {
                    case 0:
                    case 1:
                        rig.Change(rng.Next(MaterialViewRefreshBenchTests.Chests), rng.Next(MaterialViewRefreshBenchTests.SlotsPerChest),
                            rng.Next(4) == 0 ? 0 : World.Item(rng.Next(MaterialViewRefreshBenchTests.ItemTypes)), rng.Next(150));
                        break;
                    case 2:
                        rig.World.Slots[rig.World.Bag][rng.Next(MaterialViewRefreshBenchTests.SlotsPerChest)] = (World.Item(rng.Next(40)), 1 + rng.Next(9));
                        break;
                }
                foreach (var op in ViewOps)
                {
                    var outcome = rig.Refresh(op, out var view);
                    Assert.NotEqual(MaterialViewOutcome.NotGoverned, outcome);
                    Assert.Equal(rig.Fresh(op), view.Rows);
                    Assert.Equal(rig.Provider.Rev, view.Revision);
                    Assert.Equal(rig.World.Territory, view.Context.StandingTerritory);
                }
            }
        }
    }

    [Fact]
    public void An_unchanged_view_is_reused_without_asking_the_provider_for_sources()
    {
        var rig = new Rig();
        Assert.Equal(MaterialViewOutcome.Computed, rig.Refresh(MaterialOperation.Build, out var first));
        int sources = rig.Provider.SourcesCalls, candidates = rig.Provider.CandidatesCalls;
        for (int i = 0; i < 5; i++)
        {
            Assert.Equal(MaterialViewOutcome.Reused, rig.Refresh(MaterialOperation.Build, out var again));
            Assert.Same(first.Rows, again.Rows);
        }
        Assert.Equal(sources, rig.Provider.SourcesCalls);
        Assert.Equal(candidates, rig.Provider.CandidatesCalls);
        var status = rig.Registry.Status();
        Assert.Equal(1, status.Recomputes);
        Assert.Equal(5, status.Skips);
        Assert.Contains(" recomputes=1 skips=5", status.ToString());
        Assert.Equal(0, status.Views); // views counts packets sent; the service sends none
    }

    [Fact]
    public void A_chest_stock_change_recomputes_with_the_new_amount()
    {
        var rig = new Rig();
        rig.Refresh(MaterialOperation.Build, out _);
        rig.Change(5, 0, World.Item(3), 77);
        Assert.Equal(MaterialViewOutcome.Computed, rig.Refresh(MaterialOperation.Build, out var view));
        Assert.Equal(rig.Fresh(MaterialOperation.Build), view.Rows);
        Assert.Equal(MaterialViewOutcome.Reused, rig.Refresh(MaterialOperation.Build, out _));
    }

    [Fact]
    public void A_bag_or_station_change_recomputes()
    {
        var rig = new Rig();
        rig.Refresh(MaterialOperation.Craft, out _);
        rig.World.Slots[rig.World.Bag][0] = (World.Item(1), 5);
        Assert.Equal(MaterialViewOutcome.Computed, rig.Refresh(MaterialOperation.Craft, out _));
        rig.World.Slots[rig.World.Station][0] = (World.Item(1), 5);
        Assert.Equal(MaterialViewOutcome.Computed, rig.Refresh(MaterialOperation.Craft, out _));
        Assert.Equal(MaterialViewOutcome.Reused, rig.Refresh(MaterialOperation.Craft, out _));
    }

    [Fact]
    public void A_revision_change_recomputes()
    {
        var rig = new Rig();
        rig.Refresh(MaterialOperation.Build, out _);
        rig.Provider.Rev = 2;
        Assert.Equal(MaterialViewOutcome.Computed, rig.Refresh(MaterialOperation.Build, out var view));
        Assert.Equal(2, view.Revision);
    }

    [Fact]
    public void A_context_change_recomputes()
    {
        var rig = new Rig();
        rig.Refresh(MaterialOperation.Build, out _);
        rig.World.Territory = 8;
        Assert.Equal(MaterialViewOutcome.Computed, rig.Refresh(MaterialOperation.Build, out var view));
        Assert.Equal(8, view.Context.StandingTerritory);
    }

    [Fact]
    public void A_container_that_stops_resolving_recomputes()
    {
        var rig = new Rig();
        rig.Refresh(MaterialOperation.Build, out _);
        rig.World.Ids.Remove(rig.World.ChestIds[0]);
        Assert.Equal(MaterialViewOutcome.Computed, rig.Refresh(MaterialOperation.Build, out var view));
        Assert.Equal(rig.Fresh(MaterialOperation.Build), view.Rows);
    }

    [Fact]
    public void Invalidation_payment_and_provider_changes_recompute_at_once()
    {
        var rig = new Rig();
        rig.Refresh(MaterialOperation.Build, out _);
        // A dirty player (an invalidation or a governed payment for them).
        Assert.Equal(MaterialViewOutcome.Computed, rig.Refresh(MaterialOperation.Build, out _, recompute: true));
        // InvalidateAll or a provider (un)registration.
        rig.Service.ForgetViews();
        Assert.Equal(MaterialViewOutcome.Computed, rig.Refresh(MaterialOperation.Build, out _));
        // Another provider instance, even with the same revision.
        rig.Registry.RemoveOwner("test");
        Assert.Equal(MaterialViewOutcome.NotGoverned, rig.Refresh(MaterialOperation.Build, out _));
        var other = new MixedProvider(rig.World.ChestIds, 3);
        rig.Registry.Register(other, "test");
        Assert.Equal(MaterialViewOutcome.Computed, rig.Refresh(MaterialOperation.Build, out _));
    }

    [Fact]
    public void A_provider_fault_is_never_reused()
    {
        var rig = new Rig();
        rig.Provider.ThrowOnSources = true;
        Assert.Equal(MaterialViewOutcome.Computed, rig.Refresh(MaterialOperation.Build, out var view));
        Assert.All(view.Rows, l => Assert.Equal(0, l.Amount)); // no remote sources, never vanilla
        Assert.Equal(MaterialViewOutcome.Computed, rig.Refresh(MaterialOperation.Build, out _));
        rig.Provider.ThrowOnSources = false;
        Assert.Equal(MaterialViewOutcome.Computed, rig.Refresh(MaterialOperation.Build, out view));
        Assert.Contains(view.Rows, l => l.Amount > 0);
        Assert.Equal(MaterialViewOutcome.Reused, rig.Refresh(MaterialOperation.Build, out _));
    }

    [Fact]
    public void Each_inventory_is_read_once_per_refresh_across_operations()
    {
        var rig = new Rig();
        rig.World.SlotReads = 0;
        rig.Service.BeginFullRefresh();
        foreach (var op in ViewOps) rig.Service.RefreshView(rig.World.Player, op, rig.Reads, recompute: true, out _);
        rig.Reads.Clear();
        // 136 chests, the bag, the station and the heart; never once per item.
        Assert.Equal(MaterialViewRefreshBenchTests.Chests + 3, rig.World.SlotReads);
    }

    [Fact]
    public void Available_matches_the_pool()
    {
        var rng = new Random(7);
        var world = new World(7);
        var seen = new HashSet<InventoryRef>();
        for (int i = 0; i < 200; i++)
        {
            var order = Enumerable.Range(0, rng.Next(20)).Select(_ => world.ChestRefs[rng.Next(10)]).ToList();
            var exclude = new HashSet<InventoryRef>(Enumerable.Range(0, rng.Next(3)).Select(_ => world.ChestRefs[rng.Next(10)]));
            var spec = new MaterialPoolSpec<InventoryRef>(order, rng.Next(3) == 0 ? rng.Next(300) : 0);
            int item = World.Item(rng.Next(40));
            Assert.Equal(MaterialPlanner.Pool(item, spec, world, exclude).Available, MaterialPlanner.Available(item, spec, world, exclude, seen));
        }
    }

    [Fact]
    public void The_read_cache_never_writes()
    {
        var rig = new Rig();
        Assert.Throws<InvalidOperationException>(() => rig.Reads.Remove(rig.World.ChestRefs[0], World.Item(0), 1));
        Assert.Throws<InvalidOperationException>(() => rig.Reads.Add(rig.World.ChestRefs[0], World.Item(0), 1));
    }

    [Fact]
    public void Payment_reads_live_stock_not_the_cached_view()
    {
        var rig = new Rig();
        int item = Enumerable.Range(0, 40).Select(World.Item).First(i => rig.Fresh(MaterialOperation.Build).Any(l => l.Item == i && l.Amount > 0));
        rig.Refresh(MaterialOperation.Build, out var view);
        int shown = view.Rows.Single(l => l.Item == item).Amount;
        // Empty every chest of that item after the view was computed, without a refresh.
        foreach (var inv in rig.World.ChestRefs)
        {
            var slots = rig.World.Slots[inv];
            for (int s = 0; s < slots.Length; s++) if (slots[s].Item == item) slots[s] = (0, 0);
        }
        var extra = new List<MaterialLine>();
        Assert.True(rig.Service.CountGate(rig.World.Player, MaterialOperation.Build));
        Assert.True(rig.Service.ComposeExtraRows(rig.World.Player, MaterialOperation.Build,
            new Dictionary<int, long>(), new Dictionary<int, long>(), extra));
        rig.Service.MarkChecked(rig.World.Player, MaterialOperation.Build);
        Assert.DoesNotContain(extra, l => l.Item == item); // the check saw 0, not the cached amount
        var paid = rig.Service.Pay(rig.World.Player, MaterialOperation.Build, item, Math.Min(shown, 5));
        Assert.Equal(0, paid.Paid);
        rig.Service.EndScope();
        // And the next refresh notices the change.
        Assert.Equal(MaterialViewOutcome.Computed, rig.Refresh(MaterialOperation.Build, out var after));
        Assert.DoesNotContain(after.Rows, l => l.Item == item && l.Amount > 0);
        Assert.Equal(rig.Fresh(MaterialOperation.Build), after.Rows);
    }

    [Fact]
    public void Views_nobody_asked_for_are_dropped_at_the_next_full_refresh()
    {
        var rig = new Rig();
        rig.Refresh(MaterialOperation.Craft, out _);
        Assert.Equal(1, rig.Service.CachedViews);
        rig.Refresh(MaterialOperation.Build, out _); // the craft view was not asked for in this refresh
        rig.Service.BeginFullRefresh();
        Assert.Equal(1, rig.Service.CachedViews);
    }
}
