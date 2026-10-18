using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using Progenitor.Dev.Ops;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// The game-free half of the staged stress ops (stress_owner_groups, stress_clan, stress_refine,
/// stress_stations): argument parse, the owner grouping and its spread score, islands and their
/// raw-supply castles, the per-station input need, chest selection and the station top-up plan.
/// </summary>
public class DevStressStagesTests
{
    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement.Clone();

    /// <summary>
    /// 140 castles on a 14 x 10 grid 400 m apart, four regions by quadrant, assigned round-robin in
    /// territory order to 40 owners (20 with four castles, 20 with three), as the copied save was.
    /// </summary>
    private static List<StressOwnerSpots> World140()
    {
        var owners = Enumerable.Range(1, 40).Select(n => new StressOwnerSpots
        {
            Ordinal = n,
            Name = StressIdentity.OwnerName(n),
            PlatformId = StressIdentity.PlatformId(18, n),
            UserIndex = n + 2,
        }).ToList();
        for (int t = 0; t < 140; t++)
        {
            int gx = t % 14, gz = t / 14;
            var o = owners[t % 40];
            o.Castles.Add(new StressCastleSpot
            {
                Territory = t,
                X = gx * 400,
                Z = gz * 400,
                Region = (gx < 7 ? "West" : "East") + (gz < 5 ? "South" : "North"),
                Owner = o.Ordinal,
            });
        }
        return owners;
    }

    /// <summary>Same castles, but each owner's castles sit side by side (owner = territory / 3.5).</summary>
    private static List<StressOwnerSpots> Clustered140()
    {
        var owners = World140();
        foreach (var o in owners) o.Castles.Clear();
        int t = 0;
        foreach (var o in owners)
        {
            int count = o.Ordinal <= 20 ? 4 : 3;
            for (int i = 0; i < count; i++, t++)
            {
                int gx = t % 14, gz = t / 14;
                o.Castles.Add(new StressCastleSpot { Territory = t, X = gx * 400, Z = gz * 400, Region = gx < 7 ? "West" : "East", Owner = o.Ordinal });
            }
        }
        return owners;
    }

    private static readonly int[] Groups = { 8, 6, 4, 2 };

    [Fact]
    public void Args_DefaultsAndRefusals()
    {
        var g = StressStageArgs.ParseGroups(Json("{\"seed\":18}"));
        Assert.Equal(new[] { 8, 6, 4, 2 }, g.Groups);
        Assert.Null(g.Share);
        Assert.Throws<ArgumentException>(() => StressStageArgs.ParseGroups(Json("{}")));
        Assert.Throws<ArgumentException>(() => StressStageArgs.ParseGroups(Json("{\"seed\":18,\"groups\":[8,1]}")));
        Assert.Throws<ArgumentException>(() => StressStageArgs.ParseGroups(Json("{\"seed\":18,\"groups\":[]}")));

        var c = StressStageArgs.ParseClan(Json("{\"seed\":18,\"clan\":1,\"experiment\":true,\"confirm\":\"stress\"}"));
        Assert.Equal(1, c.OnlyClan);
        Assert.True(c.Experiment);
        Assert.Throws<ArgumentException>(() => StressStageArgs.ParseClan(Json("{\"seed\":18,\"clan\":5}")));

        var r = StressStageArgs.ParseRefine(Json("{\"seed\":18,\"hours\":2,\"supply\":\"local\",\"plots\":[4,1,4]}"));
        Assert.Equal(2, r.Hours);
        Assert.Equal(StressSupply.Local, r.Supply);
        Assert.False(r.AllPlots);
        Assert.Equal(new[] { 1, 4 }, r.Plots);
        Assert.Equal(3, r.EmptyChests);
        Assert.Equal(StressSupply.Auto, StressStageArgs.ParseRefine(Json("{\"seed\":18,\"plots\":\"stress\"}")).Supply);
        Assert.Throws<ArgumentException>(() => StressStageArgs.ParseRefine(Json("{\"seed\":18,\"supply\":\"far\"}")));
        Assert.Throws<ArgumentException>(() => StressStageArgs.ParseRefine(Json("{\"seed\":18,\"hours\":48}")));

        var s = StressStageArgs.ParseStations(Json("{\"seed\":18,\"perCastle\":20}"));
        Assert.Equal(20, s.PerCastle);
        Assert.Equal(15, StressStageArgs.ParseStations(Json("{\"seed\":18}")).PerCastle);
    }

    [Fact]
    public void Grouping_EveryOwnerOnceWithRequestedSizes()
    {
        var g = StressGroupPlanner.Plan(World140(), Groups, null, 18, out var refusal);
        Assert.Null(refusal);
        Assert.Equal(Groups, g.Clans.Select(c => c.Members.Count).ToArray());
        Assert.Equal(10, g.Share.Members.Count); // half of the 20 outside clans
        Assert.Equal(10, g.Solo.Members.Count);
        var all = g.All().SelectMany(x => x.Members).Select(m => m.Ordinal).ToList();
        Assert.Equal(40, all.Count);
        Assert.Equal(40, all.Distinct().Count());
        Assert.All(g.Share.Members, m => Assert.True(m.Castles.Count >= StressGroupPlanner.MinShareCastles));
        Assert.Equal(StressIdentity.ClanName(1), g.Clans[0].Name);
    }

    [Fact]
    public void Grouping_IsDeterministicForASeed()
    {
        string Shape(StressGrouping g) => string.Join("|", g.All().Select(x => x.Name + ":" + string.Join(",", x.Members.Select(m => m.Ordinal))));
        var a = StressGroupPlanner.Plan(World140(), Groups, null, 18, out _);
        var b = StressGroupPlanner.Plan(World140(), Groups, null, 18, out _);
        Assert.Equal(Shape(a), Shape(b));
        Assert.Equal(a.Score, b.Score);
    }

    [Fact]
    public void Grouping_SoloTakesTheMostCompactOwners()
    {
        var owners = Clustered140();
        // Spread four owners' castles across the map; they must not be solo.
        foreach (int n in new[] { 1, 2, 3, 4 })
        {
            var o = owners[n - 1];
            for (int i = 0; i < o.Castles.Count; i++) { o.Castles[i].X = i * 2000; o.Castles[i].Z = (i % 2) * 3600; }
        }
        var g = StressGroupPlanner.Plan(owners, Groups, null, 3, out _);
        var solo = g.Solo.Members.Select(m => m.Ordinal).ToHashSet();
        Assert.DoesNotContain(1, solo);
        Assert.DoesNotContain(4, solo);
        double maxSolo = g.Solo.Members.Max(StressGroupPlanner.OwnSpread);
        int below = owners.Count(o => StressGroupPlanner.OwnSpread(o) < maxSolo - 1e-9 && !solo.Contains(o.Ordinal) && o.Castles.Count < 3);
        Assert.Equal(0, below);
    }

    [Fact]
    public void Grouping_SpreadsClansAndMixesNeighbors()
    {
        var owners = Clustered140();
        var g = StressGroupPlanner.Plan(owners, Groups, null, 18, out _);

        // Baseline: clans of consecutive owners (neighbors on the map), the naive grouping.
        var byOrdinal = owners.ToDictionary(o => o.Ordinal);
        int at = 1;
        var baseline = new List<StressSpreadMetrics>();
        foreach (int size in Groups)
        {
            var members = Enumerable.Range(at, size).Select(n => byOrdinal[n]).ToList();
            at += size;
            baseline.Add(StressGroupPlanner.Spread(members, g.Diagonal, g.RegionsTotal));
        }
        double planned = g.Clans.Average(c => c.Spread.Score);
        Assert.True(planned > baseline.Average(b => b.Score) * 1.5, $"planned {planned} vs baseline {baseline.Average(b => b.Score)}");
        Assert.All(g.Clans, c => Assert.True(c.Spread.Regions >= 2, c.Name + " covers one region only"));
        Assert.True(g.Mixing > 0.5, "mixing " + g.Mixing);
        Assert.True(g.Swaps > 0);
    }

    [Fact]
    public void Grouping_RefusesImpossibleShapes()
    {
        StressGroupPlanner.Plan(World140(), new[] { 30, 20 }, null, 1, out var tooMany);
        Assert.Contains("need 50 owners", tooMany);
        var owners = World140().Take(10).ToList();
        foreach (var o in owners) o.Castles.RemoveRange(2, o.Castles.Count - 2);
        StressGroupPlanner.Plan(owners, new[] { 2 }, 5, 1, out var noShare);
        Assert.Contains("3+ castles", noShare);
        StressGroupPlanner.Plan(new List<StressOwnerSpots>(), Groups, null, 1, out var none);
        Assert.NotNull(none);
    }

    [Fact]
    public void Grouping_RunsInBoundedTime()
    {
        var sw = Stopwatch.StartNew();
        StressGroupPlanner.Plan(World140(), Groups, null, 18, out _);
        Assert.True(sw.ElapsedMilliseconds < 5000, "grouping took " + sw.ElapsedMilliseconds + " ms");
    }

    [Fact]
    public void Islands_AutoSupplyAndFarthestSuppliers()
    {
        var g = StressGroupPlanner.Plan(World140(), Groups, null, 18, out _);
        var islands = StressIslands.Build(g, StressSupply.Auto, 1.0 / 3.0);
        Assert.Equal(4 + 10 + 10, islands.Count);
        foreach (var i in islands)
        {
            if (i.Kind == StressGroupKind.Solo)
            {
                Assert.Equal(StressSupply.Local, i.Supply);
                Assert.Equal(i.Castles.Select(c => c.Territory), i.Suppliers);
            }
            else
            {
                Assert.Equal(StressSupply.Spread, i.Supply);
                Assert.Equal((int)Math.Ceiling(i.Castles.Count / 3.0 - 1e-9), i.Suppliers.Count);
                Assert.All(i.Suppliers, t => Assert.Contains(i.Castles, c => c.Territory == t));
            }
        }
        Assert.All(StressIslands.Build(g, StressSupply.Local, 0.3), i => Assert.Equal(i.Castles.Count, i.Suppliers.Count));
        Assert.All(StressIslands.Build(g, StressSupply.Spread, 0.3).Where(i => i.Kind == StressGroupKind.Solo), i => Assert.True(i.Suppliers.Count < i.Castles.Count));

        // A first run on a few plots keeps the islands but only those castles.
        var clan = islands.First(i => i.Kind == StressGroupKind.Clan);
        var two = clan.Castles.Take(2).Select(c => c.Territory).ToList();
        var restricted = StressIslands.Restrict(islands, two, 1.0 / 3.0);
        var only = Assert.Single(restricted);
        Assert.Equal(clan.Id, only.Id);
        Assert.Equal(two, only.Castles.Select(c => c.Territory));
        Assert.Single(only.Suppliers);
        Assert.Empty(StressIslands.Restrict(islands, new[] { 9999 }, 0.3));

        // On a line, two suppliers out of five are the two ends.
        var line = Enumerable.Range(0, 5).Select(k => new StressCastleSpot { Territory = 10 + k, X = k * 100, Z = 0 }).ToList();
        Assert.Equal(new[] { 10, 14 }, StressIslands.PickSuppliers(line, 0.4));
        Assert.Single(StressIslands.PickSuppliers(line, 0.05));
    }

    private static StressRecipe Recipe(int guid, double seconds, bool unlocked, params (int Item, int Amount)[] inputs)
    {
        var r = new StressRecipe { Guid = guid, DurationSeconds = seconds, Unlocked = unlocked };
        r.Inputs.AddRange(inputs);
        return r;
    }

    [Fact]
    public void RefineMath_StationNeedForHours()
    {
        const int Wood = -1593377811, Ore = -1805325497, Sulfur = 1501868129;
        // Sawmill planks: 20 wood per 20 s batch for one hour = 180 batches = 3600 wood.
        var saw = StressRefineMath.StationNeed(new[] { Recipe(1, 20, true, (Wood, 20)) }, 1, 1, 1);
        Assert.Equal(3600, saw[Wood]);
        // Two runnable recipes share the hour; the locked one is ignored.
        var furnace = StressRefineMath.StationNeed(new[]
        {
            Recipe(2, 30, true, (Ore, 20)),
            Recipe(3, 60, true, (Sulfur, 20)),
            Recipe(4, 30, false, (Ore, 99)),
        }, 1, 1, 1);
        Assert.Equal(60 * 20, furnace[Ore]);      // 1800 s / 30 s
        Assert.Equal(30 * 20, furnace[Sulfur]);   // 1800 s / 60 s
        // A faster server and a cost modifier.
        var fast = StressRefineMath.StationNeed(new[] { Recipe(1, 20, true, (Wood, 20)) }, 1, 2, 0.5);
        Assert.Equal(360 * 10, fast[Wood]);
        Assert.Empty(StressRefineMath.StationNeed(new[] { Recipe(4, 30, false, (Ore, 20)) }, 1, 1, 1));
        Assert.Empty(StressRefineMath.StationNeed(new[] { Recipe(5, 0, true, (Ore, 20)) }, 1, 1, 1));
    }

    [Fact]
    public void RefineMath_SplitKeepsTheTotal()
    {
        var total = new Dictionary<int, long> { [1] = 10, [2] = 3 };
        var parts = StressRefineMath.Split(total, 3);
        Assert.Equal(3, parts.Count);
        Assert.Equal(10, parts.Sum(p => p.TryGetValue(1, out long v) ? v : 0));
        Assert.Equal(new long[] { 4, 3, 3 }, parts.Select(p => p[1]).ToArray());
        Assert.Equal(3, parts.Sum(p => p.TryGetValue(2, out long v) ? v : 0));
        var sum = new Dictionary<int, long>();
        StressRefineMath.Add(sum, total);
        StressRefineMath.Add(sum, total);
        Assert.Equal(20, sum[1]);
    }

    [Fact]
    public void Chests_KeepEmptyOnesForProducts()
    {
        var chests = new[]
        {
            new StressChest { Entity = "a", Order = 0, Slots = 36, UsedSlots = 5 },
            new StressChest { Entity = "b", Order = 1, Slots = 36, UsedSlots = 0 },
            new StressChest { Entity = "c", Order = 2, Slots = 36, UsedSlots = 0 },
            new StressChest { Entity = "d", Order = 3, Slots = 36, UsedSlots = 12 },
            new StressChest { Entity = "e", Order = 4, Slots = 36, UsedSlots = 0 },
        };
        var (reserved, stock) = StressChests.Split(chests, 2);
        Assert.Equal(new[] { "e", "c" }, reserved.Select(c => c.Entity));
        Assert.Equal(new[] { "b", "a", "d" }, stock.Select(c => c.Entity));
        var (allReserved, none) = StressChests.Split(chests.Take(2).ToList(), 3);
        Assert.Equal(2, allReserved.Count);
        Assert.Empty(none);
    }

    [Fact]
    public void Clan_MembersByPlatformId()
    {
        var a = StressStageArgs.ParseClan(Json("{\"seed\":18,\"members\":[\"76561198000000001\",\"76561198000000002\"],\"name\":\"Headless\"}"));
        Assert.Equal(new ulong[] { 76561198000000001, 76561198000000002 }, a.Members);
        Assert.Equal("Headless", a.ClanName);
        Assert.Equal("StressClanH1", StressStageArgs.ParseClan(Json("{\"seed\":18,\"members\":[\"5\",\"6\"]}")).ClanName);
        Assert.Null(StressStageArgs.ParseClan(Json("{\"seed\":18}")).Members);
        Assert.Throws<ArgumentException>(() => StressStageArgs.ParseClan(Json("{\"seed\":18,\"members\":[5,6]}")));
        Assert.Throws<ArgumentException>(() => StressStageArgs.ParseClan(Json("{\"seed\":18,\"members\":[\"5\",\"5\"]}")));
        Assert.Throws<ArgumentException>(() => StressStageArgs.ParseClan(Json("{\"seed\":18,\"members\":[\"5\"],\"clan\":1}")));
        Assert.Throws<ArgumentException>(() => StressStageArgs.ParseClan(Json("{\"seed\":18,\"members\":[\"5\"],\"name\":\"" + new string('x', 62) + "\"}")));
    }

    [Fact]
    public void Stations_ExperimentNeedsAFewNamedPlots()
    {
        var a = StressStageArgs.ParseStations(Json("{\"seed\":18,\"plots\":[12],\"experiment\":true,\"limit\":2,\"confirm\":\"stress\"}"));
        Assert.True(a.Experiment);
        Assert.Equal(2, a.Limit);
        Assert.Throws<ArgumentException>(() => StressStageArgs.ParseStations(Json("{\"seed\":18,\"experiment\":true}")));
        Assert.Throws<ArgumentException>(() => StressStageArgs.ParseStations(Json("{\"seed\":18,\"plots\":[1,2,3,4],\"experiment\":true}")));
        // A dry run may plan the experiment anywhere.
        Assert.True(StressStageArgs.ParseStations(Json("{\"seed\":18,\"experiment\":true,\"dryRun\":true}")).Experiment);
    }

    [Fact]
    public void SpotFinder_FindsAFreeFloorBlock()
    {
        // Two 10 x 10 floors side by side; a chest occupies the low corner of the first.
        var floors = new[] { new StressRect(0, 0, 9, 9), new StressRect(10, 0, 19, 9) };
        var occupied = new List<StressRect> { new(0, 0, 2, 2) };
        var spot = StressSpotFinder.Find(floors, occupied, 3, 3, margin: 1);
        Assert.NotNull(spot);
        var r = spot!.Value;
        Assert.Equal((4, 0, 6, 2), (r.MinX, r.MinY, r.MaxX, r.MaxY)); // first column clear of the chest plus one tile
        Assert.All(occupied, o => Assert.False(r.Overlaps(o, 1)));
        // Off the floor edge is refused; a block wider than the floors is never found.
        Assert.Null(StressSpotFinder.Find(floors, occupied, 21, 1, 0));
        // A block may span two floors.
        var wide = StressSpotFinder.Find(floors, new List<StressRect>(), 15, 2, 0);
        Assert.Equal(0, wide!.Value.MinX);
        Assert.Equal(14, wide.Value.MaxX);
        // Fully occupied: nothing.
        Assert.Null(StressSpotFinder.Find(floors, new List<StressRect> { new(0, 0, 19, 9) }, 1, 1, 0));
        Assert.Null(StressSpotFinder.Find(Array.Empty<StressRect>(), occupied, 1, 1, 0));
    }

    [Fact]
    public void StationPlanner_TopsUpRoundRobin()
    {
        int[] catalog = { 100, 200, 300 };
        var add = StressStationPlanner.Plan(new[] { 100, 100, 200 }, 8, catalog);
        Assert.Equal(5, add.Count);
        var counts = new[] { 100, 100, 200 }.Concat(add).GroupBy(x => x).ToDictionary(x => x.Key, x => x.Count());
        Assert.True(counts.Values.Max() - counts.Values.Min() <= 1);
        Assert.Empty(StressStationPlanner.Plan(new[] { 1, 2, 3 }, 3, catalog));
        Assert.Empty(StressStationPlanner.Plan(Array.Empty<int>(), 5, Array.Empty<int>()));
    }
}
