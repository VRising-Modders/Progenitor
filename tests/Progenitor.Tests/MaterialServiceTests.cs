using System;
using System.Collections.Generic;
using System.Linq;
using Progenitor.Castles;
using Progenitor.Inventory;
using Progenitor.Materials;
using Xunit;

namespace Progenitor.Tests;

public partial class MaterialServiceTests
{
    private const int Cloth = 100, Iron = 200, Wood = 300;
    private const int Epoch = 7;

    /// <summary>
    /// An in-memory server: inventories keyed by <see cref="InventoryRef"/>, container ids mapped to
    /// them, one player standing on a plot, optionally working at a station.
    /// </summary>
    private sealed class FakeWorld : IMaterialWorld, IMaterialLedger<InventoryRef>
    {
        public readonly Dictionary<(InventoryRef, int), int> Stock = new();
        public readonly Dictionary<ContainerId, InventoryRef> Ids = new();
        public readonly Dictionary<int, long> HeartAggregate = new();
        public InventoryRef Bag = new(1, 1, 0, MaterialServiceTests.Epoch);
        public InventoryRef Station = InventoryRef.None;
        /// <summary>The castle heart whose window is open (the Upgrade station); its inventory, if any.</summary>
        public InventoryRef Heart = InventoryRef.None;
        /// <summary>An open window that is not shared storage (a chest): the game's own pass reaches it for Repair, Upgrade and CharacterCraft.</summary>
        public InventoryRef Window = InventoryRef.None;
        public bool StationIsSharedStorage;
        /// <summary>Simulates a world whose bag-and-station answer misses the station (another identification of it).</summary>
        public bool VanillaCountedMissesStation;
        public CastleId Player = new(1, 1);
        public CastleId User = new(2, 1);
        public int Territory = 4;
        public Func<InventoryRef, int, int, int>? RemoveOverride;
        public Func<InventoryRef, int, int, int>? AddOverride;

        public int Epoch => MaterialServiceTests.Epoch;
        public IMaterialLedger<InventoryRef> Ledger => this;

        public ContainerId Chest(int index, params (int item, int amount)[] contents)
        {
            var inv = new InventoryRef(100 + index, 1, 0, Epoch);
            var id = new ContainerId(new Guid(index, 0, 0, new byte[8]));
            Ids[id] = inv;
            foreach (var (item, amount) in contents) Stock[(inv, item)] = Get(inv, item) + amount;
            return id;
        }

        public int Get(InventoryRef inv, int item) => Stock.TryGetValue((inv, item), out int n) ? n : 0;
        public int Get(ContainerId id, int item) => Get(Ids[id], item);

        public bool TryContext(CastleId character, MaterialOperation operation, out MaterialContext context)
        {
            context = default;
            if (!character.Equals(Player)) return false;
            var at = StationInventory(new MaterialContext(User, Player, Epoch, operation, Territory, ContainerId.None));
            var station = at.IsNone ? ContainerId.None : IdOf(at);
            context = new MaterialContext(User, Player, Epoch, operation, Territory, station);
            return true;
        }

        public bool TryResolve(ContainerId id, out InventoryRef inventory) => Ids.TryGetValue(id, out inventory);

        public ContainerId IdOf(InventoryRef inventory)
        {
            foreach (var kv in Ids) if (kv.Value.Equals(inventory)) return kv.Key;
            return ContainerId.None;
        }

        public void VanillaCounted(in MaterialContext context, ICollection<InventoryRef> into)
        {
            into.Add(Bag);
            if (!Station.IsNone && !StationIsSharedStorage && !VanillaCountedMissesStation) into.Add(Station);
            if (context.Operation == MaterialOperation.Upgrade && !Heart.IsNone) into.Add(Heart); // the open window
            if (context.Operation is MaterialOperation.Repair or MaterialOperation.Upgrade or MaterialOperation.CharacterCraft && !Window.IsNone)
                into.Add(Window);
        }

        public InventoryRef StationInventory(in MaterialContext context) => context.Operation switch
        {
            MaterialOperation.Craft or MaterialOperation.Repair => Station,
            MaterialOperation.Upgrade => Heart,
            _ => InventoryRef.None,
        };

        public InventoryRef PoolFront(in MaterialContext context) =>
            context.Operation is MaterialOperation.Craft or MaterialOperation.Repair && StationIsSharedStorage ? Station : InventoryRef.None;

        /// <summary>The plot's own shared storage, holding <see cref="HeartAggregate"/>.</summary>
        public InventoryRef SharedStore = new(90, 1, 0, MaterialServiceTests.Epoch);

        public void VanillaSharedInventories(in MaterialContext context, ICollection<InventoryRef> into)
        {
            if (HeartAggregate.Count > 0) into.Add(SharedStore);
        }

        public bool ReadStock(InventoryRef inventory, Dictionary<int, long> into)
        {
            if (inventory.Equals(SharedStore))
            {
                foreach (var kv in HeartAggregate) into[kv.Key] = kv.Value;
                return true;
            }
            foreach (var kv in Stock)
                if (kv.Key.Item1.Equals(inventory) && kv.Value > 0) into[kv.Key.Item2] = kv.Value;
            return true;
        }

        public void Items(InventoryRef inventory, ICollection<int> into)
        {
            foreach (var kv in Stock) if (kv.Key.Item1.Equals(inventory) && kv.Value > 0) into.Add(kv.Key.Item2);
        }

        public int Count(InventoryRef source, int item) => Get(source, item);

        public int Remove(InventoryRef source, int item, int amount)
        {
            if (RemoveOverride != null) amount = RemoveOverride(source, item, amount);
            int take = Math.Min(amount, Get(source, item));
            Stock[(source, item)] = Get(source, item) - take;
            return take;
        }

        public int Add(InventoryRef source, int item, int amount)
        {
            if (AddOverride != null) amount = AddOverride(source, item, amount);
            Stock[(source, item)] = Get(source, item) + amount;
            return amount;
        }
    }

    /// <summary>A provider with fixed per-item spend orders and reserves (a test double, not a sample).</summary>
    private sealed class OrderedProvider : IMaterialSourceProvider
    {
        public IReadOnlyCollection<MaterialOperation> Operations { get; set; } = new[] { MaterialOperation.Build, MaterialOperation.Craft };
        public List<ContainerId> All = new();
        public Dictionary<int, List<ContainerId>> Orders = new();
        public Dictionary<int, int> Reserves = new();
        public HashSet<int> LeaveToGame = new();
        public bool ThrowOnSources, ThrowOnCandidates;
        public HashSet<int> ThrowFor = new();
        public long Rev = 1;
        public readonly List<MaterialContext> Seen = new();
        public readonly Dictionary<int, int> SourcesCalls = new();

        public int RevisionCalls;
        public long Revision(in MaterialContext context) { RevisionCalls++; return Rev; }
        public IReadOnlyList<ContainerId> Candidates(in MaterialContext context)
        {
            Seen.Add(context);
            if (ThrowOnCandidates) throw new InvalidOperationException("provider bug");
            return All;
        }

        public ItemSources? Sources(in MaterialContext context, int item)
        {
            SourcesCalls[item] = (SourcesCalls.TryGetValue(item, out int n) ? n : 0) + 1;
            if (ThrowOnSources || ThrowFor.Contains(item)) throw new InvalidOperationException("provider bug");
            if (LeaveToGame.Contains(item)) return null;
            var order = Orders.TryGetValue(item, out var o) ? o : All;
            return new ItemSources(order, Reserves.TryGetValue(item, out int r) ? r : 0);
        }
    }

    private static (FakeWorld world, OrderedProvider provider, MaterialSourceRegistry registry, MaterialService service, List<MaterialPaid> paid, List<string> log) Setup()
    {
        var world = new FakeWorld();
        var provider = new OrderedProvider();
        var registry = new MaterialSourceRegistry { HookState = () => (true, "") };
        var paid = new List<MaterialPaid>();
        var log = new List<string>();
        var service = new MaterialService(registry, world, log.Add, paid.Add);
        return (world, provider, registry, service, paid, log);
    }

    [Fact]
    public void Nothing_is_governed_without_a_provider()
    {
        var (world, _, _, service, _, _) = Setup();
        world.Chest(1, (Cloth, 10));
        Assert.False(service.GovernsLine(world.Player, MaterialOperation.Craft, Cloth));
        Assert.False(service.ComposeExtraRows(world.Player, MaterialOperation.Craft,
            new Dictionary<int, long>(), new Dictionary<int, long>(), new List<MaterialLine>()));
        Assert.Equal(5, service.Pay(world.Player, MaterialOperation.Craft, Cloth, 5).Remainder);
    }

    [Fact]
    public void An_operation_the_provider_did_not_ask_for_stays_vanilla()
    {
        var (world, provider, registry, service, _, _) = Setup();
        provider.All.Add(world.Chest(1, (Cloth, 10)));
        provider.Operations = new[] { MaterialOperation.Craft };
        registry.Register(provider, "test");
        Assert.False(service.CountGate(world.Player, MaterialOperation.Build));
        Assert.False(service.GovernsLine(world.Player, MaterialOperation.Build, Cloth));
        Check(service, world.Player, MaterialOperation.Craft);
        Assert.True(service.GovernsLine(world.Player, MaterialOperation.Craft, Cloth));
    }

    [Fact]
    public void Empty_candidates_mean_this_context_is_not_governed()
    {
        var (world, provider, registry, service, _, _) = Setup();
        registry.Register(provider, "test");
        Assert.False(service.GovernsLine(world.Player, MaterialOperation.Craft, Cloth));
    }

    [Fact]
    public void Remote_plain_chest_pays_for_a_craft_with_its_own_spend_order()
    {
        var (world, provider, registry, service, paid, _) = Setup();
        var worst = world.Chest(1, (Cloth, 3));
        var best = world.Chest(2, (Cloth, 20));
        provider.All.AddRange(new[] { best, worst });
        provider.Orders[Cloth] = new List<ContainerId> { worst, best }; // reverse deposit priority
        registry.Register(provider, "test");

        Check(service, world.Player, MaterialOperation.Craft);
        var r = service.Pay(world.Player, MaterialOperation.Craft, Cloth, 8);
        Assert.Equal(0, r.Remainder);
        Assert.Equal(0, world.Get(worst, Cloth));
        Assert.Equal(15, world.Get(best, Cloth));
        Assert.Empty(paid); // published when the game system finishes the operation
        service.EndScope();
        var e = paid.Single();
        Assert.Equal(new[] { new MaterialDebit(worst, 3).ToString(), new MaterialDebit(best, 5).ToString() }, e.Debits.Select(d => d.ToString()));
        Assert.Equal(MaterialOperation.Craft, e.Context.Operation);
        Assert.Equal(1, registry.Status().Payments);
    }

    [Fact]
    public void A_line_beyond_the_quote_takes_what_the_quote_allows_and_leaves_the_rest_owed()
    {
        var (world, provider, registry, service, paid, log) = Setup();
        var a = world.Chest(1, (Cloth, 3));
        var b = world.Chest(2, (Cloth, 3));
        provider.All.AddRange(new[] { a, b });
        registry.Register(provider, "test");
        Check(service, world.Player, MaterialOperation.Craft);
        var r = service.Pay(world.Player, MaterialOperation.Craft, Cloth, 7);
        Assert.Equal(1, r.Remainder);
        Assert.Equal(6, r.Paid);
        Assert.Equal(0, world.Get(a, Cloth));
        Assert.Equal(0, world.Get(b, Cloth));
        service.EndScope();
        Assert.Equal(6, paid.Single().Paid);
        Assert.Equal(7, paid.Single().Requested);
        Assert.Contains(log, l => l.Contains("shortfall at payment") && l.Contains("owed=1"));
        Assert.Equal(1, registry.Status().Shortfalls);
    }

    [Fact]
    public void The_bag_and_a_plain_station_are_never_counted_twice()
    {
        var (world, provider, registry, service, _, _) = Setup();
        world.Station = new InventoryRef(50, 1, 0, Epoch);
        world.Stock[(world.Bag, Cloth)] = 40;
        world.Stock[(world.Station, Cloth)] = 40;
        var chest = world.Chest(1, (Cloth, 5));
        // A careless provider lists the bag and station too.
        world.Ids[new ContainerId(Guid.NewGuid())] = world.Bag;
        world.Ids[new ContainerId(Guid.NewGuid())] = world.Station;
        provider.All.AddRange(world.Ids.Keys);
        registry.Register(provider, "test");

        var totals = new Dictionary<int, int>();
        Assert.True(service.TryView(world.Player, MaterialOperation.Craft, totals, out _, out _));
        Assert.Equal(5, totals[Cloth]);
        // The game already paid from the bag and the station; the pool only holds the chest.
        var rows = Check(service, world.Player, MaterialOperation.Craft);
        Assert.Equal(5, rows.Single(l => l.Item == Cloth).Amount);
        Assert.Equal(0, service.Pay(world.Player, MaterialOperation.Craft, Cloth, 5).Remainder);
        Assert.Equal(0, world.Get(chest, Cloth));
        Assert.Equal(40, world.Get(world.Bag, Cloth));
        Assert.Equal(40, world.Get(world.Station, Cloth));
    }

    [Fact]
    public void A_station_that_is_shared_storage_pays_first()
    {
        var (world, provider, registry, service, paid, _) = Setup();
        world.Station = new InventoryRef(50, 1, 0, Epoch);
        world.StationIsSharedStorage = true;
        world.Ids[new ContainerId(Guid.NewGuid())] = world.Station;
        world.Stock[(world.Station, Iron)] = 2;
        var chest = world.Chest(1, (Iron, 10));
        provider.All.Add(chest);
        registry.Register(provider, "test");

        Check(service, world.Player, MaterialOperation.Craft);
        Assert.Equal(0, service.Pay(world.Player, MaterialOperation.Craft, Iron, 5).Remainder);
        Assert.Equal(0, world.Get(world.Station, Iron));
        Assert.Equal(7, world.Get(chest, Iron));
        service.EndScope();
        Assert.Equal(2, paid.Single().Debits[0].Amount);
    }

    [Fact]
    public void Compose_replaces_the_shared_rows_for_governed_items_and_keeps_them_for_the_rest()
    {
        var (world, provider, registry, service, _, _) = Setup();
        provider.All.Add(world.Chest(1, (Cloth, 12), (Iron, 4)));
        provider.Reserves[Cloth] = 2;
        provider.LeaveToGame.Add(Wood);
        registry.Register(provider, "test");

        // The game's own answer: bag holds 1 cloth; shared storage adds 50 cloth and 9 wood.
        var full = new Dictionary<int, long> { [Cloth] = 51, [Wood] = 9 };
        var bag = new Dictionary<int, long> { [Cloth] = 1 };
        var extra = new List<MaterialLine>();
        Assert.True(service.ComposeExtraRows(world.Player, MaterialOperation.Build, full, bag, extra));
        Assert.Equal(new[] { new MaterialLine(Cloth, 10), new MaterialLine(Iron, 4), new MaterialLine(Wood, 9) }, extra);
    }

    [Fact]
    public void A_governed_item_with_nothing_in_the_pool_hides_the_games_shared_amount()
    {
        var (world, provider, registry, service, _, _) = Setup();
        provider.All.Add(world.Chest(1, (Iron, 1)));
        registry.Register(provider, "test");
        var full = new Dictionary<int, long> { [Cloth] = 30 };
        var extra = new List<MaterialLine>();
        Assert.True(service.ComposeExtraRows(world.Player, MaterialOperation.Build, full, new Dictionary<int, long>(), extra));
        Assert.DoesNotContain(extra, l => l.Item == Cloth && l.Amount > 0);
    }

    [Fact]
    public void A_throwing_provider_means_no_remote_sources_never_vanilla_storage()
    {
        var (world, provider, registry, service, _, log) = Setup();
        provider.All.Add(world.Chest(1, (Cloth, 12)));
        provider.ThrowOnSources = true;
        registry.Register(provider, "test");
        var full = new Dictionary<int, long> { [Cloth] = 30 };
        var extra = new List<MaterialLine>();
        Assert.True(service.ComposeExtraRows(world.Player, MaterialOperation.Build, full, new Dictionary<int, long>(), extra));
        service.MarkChecked(world.Player, MaterialOperation.Build);
        Assert.Equal(0, extra.Where(l => l.Item == Cloth).Sum(l => l.Amount));
        Assert.True(service.GovernsLine(world.Player, MaterialOperation.Build, Cloth));
        Assert.Equal(4, service.Pay(world.Player, MaterialOperation.Build, Cloth, 4).Remainder);
        Assert.Contains(log, l => l.Contains("provider.Sources threw"));
    }

    [Fact]
    public void The_view_lists_governed_items_only()
    {
        var (world, provider, registry, service, _, _) = Setup();
        provider.All.Add(world.Chest(1, (Cloth, 12)));
        provider.LeaveToGame.Add(Wood);
        world.HeartAggregate[Wood] = 9;
        world.HeartAggregate[Iron] = 6; // governed item the provider's containers do not hold
        registry.Register(provider, "test");
        var totals = new Dictionary<int, int>();
        Assert.True(service.TryView(world.Player, MaterialOperation.Build, totals, out var ctx, out long rev));
        Assert.Equal(12, totals[Cloth]);
        Assert.False(totals.ContainsKey(Wood)); // left to the game: the client keeps the game's own amount
        Assert.Equal(0, totals[Iron]);          // governed, nothing in the pool: hides the game's amount
        Assert.Equal(1, rev);
        Assert.Equal(4, ctx.StandingTerritory);
    }

    [Fact]
    public void A_display_only_operation_is_viewed_but_never_checked_or_paid()
    {
        var (world, provider, registry, service, paid, _) = Setup();
        var a = world.Chest(1, (Iron, 10));
        provider.All.Add(a);
        provider.Operations = new[] { MaterialOperation.RefineView };
        registry.Register(provider, "test");

        // The view is computed (that is the operation's whole purpose) ...
        var totals = new Dictionary<int, int>();
        Assert.True(service.TryView(world.Player, MaterialOperation.RefineView, totals, out var ctx, out _));
        Assert.Equal(MaterialOperation.RefineView, ctx.Operation);
        Assert.Equal(10, totals[Iron]);

        // ... but no seam session is ever governed and nothing is ever taken.
        Assert.False(service.CountGate(world.Player, MaterialOperation.RefineView));
        Assert.False(service.ComposeExtraRows(world.Player, MaterialOperation.RefineView,
            new Dictionary<int, long> { [Iron] = 0 }, new Dictionary<int, long>(), new List<MaterialLine>()));
        Assert.False(service.GovernsLine(world.Player, MaterialOperation.RefineView, Iron));
        var r = service.Pay(world.Player, MaterialOperation.RefineView, Iron, 4);
        Assert.Equal(4, r.Remainder);
        Assert.Equal(0, r.Paid);
        Assert.Null(service.PlanRequest(world.Player, MaterialOperation.RefineView, new[] { new MaterialLine(Iron, 4) }));
        Assert.Null(service.PayRequest(world.Player, MaterialOperation.RefineView, new[] { new MaterialLine(Iron, 4) }));
        service.EndScope();
        Assert.Equal(10, world.Get(a, Iron));
        Assert.Empty(paid);
    }

    [Fact]
    public void Whole_request_payment_is_all_or_nothing()
    {
        var (world, provider, registry, service, _, _) = Setup();
        var a = world.Chest(1, (Iron, 10), (Wood, 1));
        provider.All.Add(a);
        registry.Register(provider, "test");
        var lines = new[] { new MaterialLine(Iron, 5), new MaterialLine(Wood, 2) };
        var refused = service.PayRequest(world.Player, MaterialOperation.Craft, lines)!;
        Assert.Equal(MaterialCommitOutcome.Refused, refused.Outcome);
        Assert.Equal(10, world.Get(a, Iron));
        world.Stock[(world.Ids[a], Wood)] = 2;
        Assert.True(service.PayRequest(world.Player, MaterialOperation.Craft, lines)!.Ok);
        Assert.Equal(5, world.Get(a, Iron));
        Assert.Equal(0, world.Get(a, Wood));
    }

    [Fact]
    public void Unknown_and_gone_container_ids_are_skipped()
    {
        var (world, provider, registry, service, _, _) = Setup();
        var a = world.Chest(1, (Cloth, 4));
        provider.All.Add(new ContainerId(Guid.NewGuid()));
        provider.All.Add(ContainerId.None);
        provider.All.Add(a);
        registry.Register(provider, "test");
        Check(service, world.Player, MaterialOperation.Build);
        Assert.Equal(0, service.Pay(world.Player, MaterialOperation.Build, Cloth, 4).Remainder);
    }

    [Fact]
    public void After_unregistering_everything_is_vanilla_again()
    {
        var (world, provider, registry, service, _, _) = Setup();
        provider.All.Add(world.Chest(1, (Cloth, 4)));
        var handle = registry.Register(provider, "test");
        Check(service, world.Player, MaterialOperation.Build);
        Assert.True(service.GovernsLine(world.Player, MaterialOperation.Build, Cloth));
        service.EndScope();
        handle.Dispose();
        Assert.False(service.GovernsLine(world.Player, MaterialOperation.Build, Cloth));
    }

    [Fact]
    public void The_provider_sees_the_operation_plot_and_station()
    {
        var (world, provider, registry, service, _, _) = Setup();
        world.Station = new InventoryRef(50, 1, 0, Epoch);
        var stationId = new ContainerId(Guid.NewGuid());
        world.Ids[stationId] = world.Station;
        provider.All.Add(world.Chest(1, (Cloth, 4)));
        registry.Register(provider, "test");
        service.GovernsLine(world.Player, MaterialOperation.Craft, Cloth);
        var c = provider.Seen.Last();
        Assert.Equal(MaterialOperation.Craft, c.Operation);
        Assert.Equal(stationId, c.Station);
        Assert.Equal(world.User, c.User);
        Assert.Equal(4, c.StandingTerritory);
        Assert.Equal(Epoch, c.WorldEpoch);
    }
}
