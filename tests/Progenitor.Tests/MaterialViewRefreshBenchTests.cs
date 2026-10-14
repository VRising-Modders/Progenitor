using System;
using System.Collections.Generic;
using System.Diagnostics;
using Progenitor.Castles;
using Progenitor.Inventory;
using Progenitor.Materials;
using Xunit;
using Xunit.Abstractions;

namespace Progenitor.Tests;

/// <summary>
/// Game-free cost benchmark of the per-player totals refresh: one player on a plot with 136
/// provider chests holding 40 item types, refreshed once per second like the server's refresh. The
/// world counts what the server pays for live (inventory slot-buffer reads, container id
/// resolutions, castle building scans, provider calls); wall time and allocation are printed for
/// comparison only (the live cost of a read is an interop slot-buffer walk, far above this fake's).
///
/// <para>Before this change (same world, measured on the previous code, which recomputed every view
/// every second and read every container once per item): 1 op = 5712 inventory reads, 5576 id
/// resolves, 2 castle scans, 42 provider calls, ~755 KB allocated per refresh; 3 ops = 17136 reads,
/// 16728 resolves, 6 scans, 126 provider calls, ~2.26 MB; 4 ops = 22848 reads, 22304 resolves, 8 scans,
/// 168 provider calls, ~3.0 MB.</para>
/// </summary>
public class MaterialViewRefreshBenchTests
{
    private readonly ITestOutputHelper _out;

    public MaterialViewRefreshBenchTests(ITestOutputHelper output) => _out = output;

    internal const int Chests = 136, ItemTypes = 40, SlotsPerChest = 36;

    /// <summary>A server with one player standing on a plot. Every read walks the container's slots, as the real ledger does.</summary>
    internal sealed class BenchWorld : IMaterialWorld, IMaterialLedger<InventoryRef>
    {
        public const int Epoch = 5;
        public readonly Dictionary<InventoryRef, (int Item, int Amount)[]> Slots = new();
        public readonly Dictionary<ContainerId, InventoryRef> Ids = new();
        public readonly List<ContainerId> ChestIds = new();
        public readonly List<InventoryRef> ChestRefs = new();
        public readonly List<InventoryRef> Shared = new();
        public InventoryRef Bag = new(1, 1, 0, Epoch);
        public InventoryRef Station = new(2, 1, 0, Epoch);
        public InventoryRef Heart = new(3, 1, 0, Epoch);
        public CastleId Player = new(10, 1), User = new(11, 1);
        public int Territory = 7;

        /// <summary>Milliseconds; the shared-storage list is kept 10 s like the production world's.</summary>
        public long Now;
        private long _sharedStamp = -10_000;

        public long SlotReads, Resolves, CastleScans;

        public BenchWorld(int seed = 1)
        {
            var rng = new Random(seed);
            Slots[Bag] = new (int, int)[SlotsPerChest];
            Slots[Station] = new (int, int)[SlotsPerChest];
            Slots[Heart] = new (int, int)[SlotsPerChest];
            for (int c = 0; c < Chests; c++)
            {
                var inv = new InventoryRef(1000 + c, 1, 0, Epoch);
                var slots = new (int, int)[SlotsPerChest];
                // A chest holds a few item types, several stacks each; some slots stay empty.
                for (int s = 0; s < SlotsPerChest; s++)
                    slots[s] = rng.Next(4) == 0 ? (0, 0) : (Item(rng.Next(ItemTypes)), 1 + rng.Next(100));
                Slots[inv] = slots;
                var id = new ContainerId(new Guid(c + 1, 0, 0, new byte[8]));
                Ids[id] = inv;
                ChestIds.Add(id);
                ChestRefs.Add(inv);
                Shared.Add(inv);
            }
        }

        public static int Item(int i) => 5000 + i;

        int IMaterialWorld.Epoch => Epoch;
        public IMaterialLedger<InventoryRef> Ledger => this;

        public bool TryContext(CastleId character, MaterialOperation operation, out MaterialContext context)
        {
            context = default;
            if (!character.Equals(Player)) return false;
            var station = operation switch
            {
                MaterialOperation.Craft or MaterialOperation.Repair => new ContainerId(new Guid(9001, 0, 0, new byte[8])),
                MaterialOperation.Upgrade => new ContainerId(new Guid(9002, 0, 0, new byte[8])),
                _ => ContainerId.None,
            };
            context = new MaterialContext(User, Player, Epoch, operation, Territory, station);
            return true;
        }

        public bool TryResolve(ContainerId id, out InventoryRef inventory)
        {
            Resolves++;
            return Ids.TryGetValue(id, out inventory);
        }

        public ContainerId IdOf(InventoryRef inventory) => ContainerId.None;

        public void VanillaCounted(in MaterialContext context, ICollection<InventoryRef> into)
        {
            into.Add(Bag);
            if (context.Operation is MaterialOperation.Craft or MaterialOperation.Repair) into.Add(Station);
            if (context.Operation == MaterialOperation.Upgrade) into.Add(Heart);
        }

        public InventoryRef PoolFront(in MaterialContext context) => InventoryRef.None;

        public InventoryRef StationInventory(in MaterialContext context) => context.Operation switch
        {
            MaterialOperation.Craft or MaterialOperation.Repair => Station,
            MaterialOperation.Upgrade => Heart,
            _ => InventoryRef.None,
        };

        public void VanillaSharedInventories(in MaterialContext context, ICollection<InventoryRef> into)
        {
            if (Now - _sharedStamp >= 10_000)
            {
                _sharedStamp = Now;
                CastleScans += 2; // the castle's containers and its stations, as the production world lists them
            }
            foreach (var inv in Shared) into.Add(inv);
        }

        public void Items(InventoryRef inventory, ICollection<int> into)
        {
            if (!Slots.TryGetValue(inventory, out var slots)) return;
            SlotReads++;
            foreach (var (item, amount) in slots)
                if (item != 0 && amount > 0) into.Add(item);
        }

        public bool ReadStock(InventoryRef inventory, Dictionary<int, long> into)
        {
            if (!Slots.TryGetValue(inventory, out var slots)) return false;
            SlotReads++;
            foreach (var (item, amount) in slots)
                if (item != 0 && amount > 0) into[item] = (into.TryGetValue(item, out long n) ? n : 0) + amount;
            return true;
        }

        public int Count(InventoryRef source, int item)
        {
            if (!Slots.TryGetValue(source, out var slots)) return 0;
            SlotReads++;
            long n = 0;
            foreach (var (i, amount) in slots)
                if (i == item && amount > 0) n += amount;
            return (int)Math.Min(n, int.MaxValue);
        }

        public int Remove(InventoryRef source, int item, int amount)
        {
            var slots = Slots[source];
            int left = amount;
            for (int s = 0; s < slots.Length && left > 0; s++)
            {
                if (slots[s].Item != item || slots[s].Amount <= 0) continue;
                int take = Math.Min(left, slots[s].Amount);
                slots[s] = (item, slots[s].Amount - take);
                left -= take;
            }
            return amount - left;
        }

        public int Add(InventoryRef source, int item, int amount)
        {
            var slots = Slots[source];
            for (int s = 0; s < slots.Length; s++)
                if (slots[s].Item == item || slots[s].Item == 0 || slots[s].Amount == 0)
                {
                    slots[s] = (item, (slots[s].Item == item ? slots[s].Amount : 0) + amount);
                    return amount;
                }
            return 0;
        }
    }

    /// <summary>Governs every operation with every chest, in candidate order; cached like a real provider.</summary>
    internal sealed class BenchProvider : IMaterialSourceProvider
    {
        private readonly List<ContainerId> _all;
        public long Rev = 1;
        public long Calls, SourcesCalls;

        public BenchProvider(List<ContainerId> all) => _all = all;

        public IReadOnlyCollection<MaterialOperation> Operations { get; } = new[]
            { MaterialOperation.Build, MaterialOperation.Craft, MaterialOperation.Repair, MaterialOperation.Upgrade };

        public long Revision(in MaterialContext context) { Calls++; return Rev; }

        public IReadOnlyList<ContainerId> Candidates(in MaterialContext context) { Calls++; return _all; }

        private ItemSources? _sources;

        public ItemSources? Sources(in MaterialContext context, int item)
        {
            Calls++;
            SourcesCalls++;
            return _sources ??= new ItemSources(_all);
        }
    }

    internal static readonly MaterialOperation[] Ops =
        { MaterialOperation.Build, MaterialOperation.Craft, MaterialOperation.Upgrade, MaterialOperation.Repair };

    internal enum Load
    {
        /// <summary>Nothing changes between refreshes (the live case: a player standing on a plot).</summary>
        Unchanged,

        /// <summary>One chest's stock changes before every refresh.</summary>
        OneChestChangesEverySecond,

        /// <summary>Every refresh recomputes (the player is marked dirty every second).</summary>
        RecomputeEverySecond,
    }

    internal sealed record Sample(int Ops, double Reads, double Resolves, double Scans, double ProviderCalls, double Micros, double Bytes,
        double Recomputes, double Skips);

    internal static Sample Run(int opCount, int refreshes, Load load)
    {
        var world = new BenchWorld();
        var provider = new BenchProvider(world.ChestIds);
        var registry = new MaterialSourceRegistry { HookState = () => (true, "") };
        registry.Register(provider, "bench");
        var service = new MaterialService(registry, world, _ => { });
        var reads = new MaterialStockReads(world);

        void Refresh()
        {
            service.BeginFullRefresh();
            for (int o = 0; o < opCount; o++)
                service.RefreshView(world.Player, Ops[o], reads, load == Load.RecomputeEverySecond, out _);
            reads.Clear();
        }

        // Warm up (JIT, first compute), then measure.
        Refresh();
        Refresh();
        world.SlotReads = world.Resolves = world.CastleScans = provider.Calls = 0;
        var before = registry.Status();
        long bytes0 = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        for (int r = 0; r < refreshes; r++)
        {
            world.Now += 1000;
            if (load == Load.OneChestChangesEverySecond)
            {
                var slots = world.Slots[world.ChestRefs[r % Chests]];
                slots[0] = (BenchWorld.Item(r % ItemTypes), 1 + r % 50);
            }
            Refresh();
        }
        sw.Stop();
        long bytes = GC.GetAllocatedBytesForCurrentThread() - bytes0;
        var after = registry.Status();
        return new Sample(opCount, (double)world.SlotReads / refreshes, (double)world.Resolves / refreshes,
            (double)world.CastleScans / refreshes, (double)provider.Calls / refreshes,
            sw.Elapsed.TotalMilliseconds * 1000 / refreshes, (double)bytes / refreshes,
            (double)(after.Recomputes - before.Recomputes) / refreshes, (double)(after.Skips - before.Skips) / refreshes);
    }

    [Fact]
    public void Refresh_cost_per_second_with_136_chests()
    {
        foreach (var load in new[] { Load.Unchanged, Load.OneChestChangesEverySecond, Load.RecomputeEverySecond })
        {
            _out.WriteLine(load + ": ops | inventory reads | id resolves | castle scans | provider calls | recomputes | skips | us | bytes   (per 1 s refresh)");
            for (int ops = 1; ops <= 4; ops++)
            {
                var s = Run(ops, 30, load);
                _out.WriteLine($"  {s.Ops} | {s.Reads:F0} | {s.Resolves:F0} | {s.Scans:F1} | {s.ProviderCalls:F0} | {s.Recomputes:F1} | {s.Skips:F1} | {s.Micros:F0} | {s.Bytes:F0}");

                // Each inventory is read at most once per refresh, whatever the number of items and operations:
                // 136 chests plus the bag, the station and the heart.
                Assert.True(s.Reads <= Chests + 3, load + " ops=" + ops + ": " + s.Reads + " reads per refresh");
                Assert.True(s.Resolves <= Chests, load + " ops=" + ops + ": " + s.Resolves + " id resolves per refresh");
                if (load == Load.Unchanged)
                {
                    Assert.Equal(0, s.Recomputes);
                    Assert.Equal(ops, s.Skips);
                    Assert.Equal(ops, s.ProviderCalls); // one Revision per view, no Candidates, no Sources
                }
            }
        }
    }
}
