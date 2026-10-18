using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Progenitor.Dev.Ops;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// The game-free half of <c>stress_populate</c> / <c>stress_clear</c>: argument parse, the
/// test-server gate, owner identities, the deterministic planner (owners, clans, plots, layouts)
/// and the frame-budgeted job against a fake world, including clear run twice.
/// </summary>
public class DevStressPlanTests
{
    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement.Clone();

    private static StressArgs Args(string json = "{\"seed\":7}") => StressArgs.ParsePopulate(Json(json));

    /// <summary>147 territories like the Moo World copy: index 0 region None, 7 claimed, the rest free.</summary>
    private static List<StressTerritory> World147()
    {
        var list = new List<StressTerritory>();
        for (int i = 0; i < 147; i++)
            list.Add(Territory(i, claimed: i is 3 or 13 or 17 or 31 or 60 or 90 or 120, region: i == 0 ? "None" : "FarbaneWoods"));
        return list;
    }

    private static StressTerritory Territory(int index, bool claimed = false, string region = "FarbaneWoods", bool pivot = true, bool[]? mask = null)
    {
        int baseX = 1000 + (index * 200), baseY = 3000;
        return new StressTerritory
        {
            Index = index,
            Claimed = claimed,
            ClaimedBy = claimed ? "Someone" : null,
            Region = region,
            MinX = baseX,
            MinY = baseY,
            MaxX = baseX + 100,
            MaxY = baseY + 100,
            Mask = mask,
            HasPivot = pivot && !claimed,
            PivotX = baseX + 45,
            PivotY = baseY + 45,
        };
    }

    // ---- args -----------------------------------------------------------------------------------

    [Fact]
    public void Parse_defaults()
    {
        var a = Args();
        Assert.Equal(7, a.Seed);
        Assert.Equal(40, a.Owners);
        Assert.Equal(12, a.Clans);
        Assert.True(a.AllFree);
        Assert.Equal((10, 25), (a.ChestsMin, a.ChestsMax));
        Assert.Equal((6, 10), (a.StationsMin, a.StationsMax));
        Assert.Equal(0.5, a.Fill);
        Assert.False(a.Confirmed);
        Assert.False(a.DryRun);
        Assert.False(a.Partial);
    }

    [Fact]
    public void Parse_reads_every_field()
    {
        var a = Args("{\"seed\":42,\"owners\":10,\"clans\":3,\"plots\":[9,4,4,12],\"chests\":[2,4],\"stations\":[1,3],\"fill\":0.9,\"confirm\":\"stress\",\"budgetMs\":2,\"dryRun\":true,\"partial\":true}");
        Assert.Equal(42, a.Seed);
        Assert.Equal(10, a.Owners);
        Assert.Equal(3, a.Clans);
        Assert.False(a.AllFree);
        Assert.Equal(new[] { 4, 9, 12 }, a.Plots);
        Assert.Equal((2, 4), (a.ChestsMin, a.ChestsMax));
        Assert.Equal((1, 3), (a.StationsMin, a.StationsMax));
        Assert.Equal(0.9, a.Fill);
        Assert.True(a.Confirmed);
        Assert.Equal(2, a.BudgetMs);
        Assert.True(a.DryRun);
        Assert.True(a.Partial);
        Assert.True(Args("{\"seed\":1,\"plots\":\"free\"}").AllFree);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"seed\":-1}")]
    [InlineData("{\"seed\":10000}")]
    [InlineData("{\"seed\":1.5}")]
    [InlineData("{\"seed\":1,\"owners\":0}")]
    [InlineData("{\"seed\":1,\"owners\":100}")]
    [InlineData("{\"seed\":1,\"plots\":\"all\"}")]
    [InlineData("{\"seed\":1,\"plots\":[]}")]
    [InlineData("{\"seed\":1,\"plots\":[-2]}")]
    [InlineData("{\"seed\":1,\"chests\":[25,10]}")]
    [InlineData("{\"seed\":1,\"chests\":[10]}")]
    [InlineData("{\"seed\":1,\"fill\":1.5}")]
    [InlineData("{\"seed\":1,\"budgetMs\":100}")]
    [InlineData("{\"seed\":1,\"partial\":\"yes\"}")]
    [InlineData("{\"seed\":1,\"stations\":[13,13]}")]
    [InlineData("{\"seed\":1,\"chests\":[31,31]}")]
    public void Parse_refuses_bad_values(string json)
    {
        Assert.Throws<ArgumentException>(() => StressArgs.ParsePopulate(Json(json)));
    }

    [Fact]
    public void Parse_largest_counts_still_fit_the_block()
    {
        var a = Args("{\"seed\":1,\"stations\":[12,12],\"chests\":[30,30]}");
        Assert.Equal(StressLayout.FloorCellCount, a.StationsMax + (a.ChestsMax / StressLayout.ChestsPerCell));
        var plot = StressPlanner.Plan(a, World147()).Plots[0];
        Assert.Equal(12, plot.Stations.Count);
        Assert.Equal(30, plot.Chests.Count);
    }

    [Fact]
    public void Parse_clear_needs_a_seed()
    {
        Assert.Throws<ArgumentException>(() => StressArgs.ParseClear(Json("{\"confirm\":\"stress\"}")));
        var a = StressArgs.ParseClear(Json("{\"seed\":5,\"confirm\":\"stress\"}"));
        Assert.Equal(5, a.Seed);
        Assert.True(a.Confirmed);
    }

    // ---- identity -------------------------------------------------------------------------------

    [Fact]
    public void Identity_round_trips_and_stays_out_of_the_ghost_and_extra_client_bands()
    {
        ulong id = StressIdentity.PlatformId(123, 7);
        Assert.True(StressIdentity.TrySeedOf(id, out int seed, out int ordinal));
        Assert.Equal((123, 7), (seed, ordinal));
        Assert.Equal("Stress07", StressIdentity.OwnerName(7));
        Assert.Equal("StressClan12", StressIdentity.ClanName(12));

        ulong lowest = StressIdentity.PlatformId(0, 1), highest = StressIdentity.PlatformId(StressArgs.MaxSeed, StressArgs.MaxOwners);
        Assert.True(lowest > 90000000000000001UL + 10_000); // ghost band
        Assert.True(lowest > 90000000000020000UL + 99); // extra-client band
        Assert.True(highest > lowest);
        Assert.False(StressIdentity.IsStressPlatformId(90000000000000001UL));
        Assert.False(StressIdentity.IsStressPlatformId(90000000000020000UL));
        Assert.False(StressIdentity.IsStressPlatformId(76561198000000000UL));
        Assert.False(StressIdentity.IsStressPlatformId(StressIdentity.PlatformIdBase)); // ordinal 0
        Assert.NotEqual(StressIdentity.PlatformId(1, 1), StressIdentity.PlatformId(2, 1));
    }

    // ---- gate -----------------------------------------------------------------------------------

    private static StressGateInputs Gate(string? save = @"C:\Workspace\_testserver\moo-world-copy-20260909", string? confirm = "stress",
        string process = @"C:\Workspace\_testserver\server\VRisingServer.exe") => new()
        {
            ProcessPath = process,
            GameRoot = @"C:\Workspace\_testserver\server",
            ConfigPath = @"C:\Workspace\_testserver\server\BepInEx\config",
            PersistentDataPath = save,
            Confirm = confirm,
        };

    [Fact]
    public void Gate_allows_the_moo_world_copy_on_the_test_server()
    {
        Assert.True(StressGate.Check(Gate(), out var code, out _));
        Assert.Equal("", code);
    }

    [Theory]
    [InlineData(@"C:\VRisingServer\VRisingServer.exe", @"C:\Workspace\_testserver\moo-world-copy-20260909", "stress", "live_server")]
    [InlineData(@"C:\Workspace\_testserver\server\VRisingServer.exe", @"C:\VRisingServer\save", "stress", "live_server")]
    [InlineData(@"D:\Games\VRisingServer\VRisingServer.exe", @"C:\Workspace\_testserver\moo-world-copy-20260909", "stress", "not_test_server")]
    [InlineData(@"C:\Workspace\_testserver\server\VRisingServer.exe", null, "stress", "no_save_path")]
    [InlineData(@"C:\Workspace\_testserver\server\VRisingServer.exe", @"C:\Workspace\_testserver\save-data", "stress", "not_approved_save")]
    [InlineData(@"C:\Workspace\_testserver\server\VRisingServer.exe", @"C:\Workspace\_testserver\moo-world-copy-20260909.bak-prestress-20261017-1504", "stress", "not_approved_save")]
    [InlineData(@"C:\Workspace\_testserver\server\VRisingServer.exe", @"C:\Saves\moo-world-copy-20260909", "stress", "not_approved_save")]
    [InlineData(@"C:\Workspace\_testserver\server\VRisingServer.exe", @"C:\Workspace\_testserver\moo-world-copy-20260909", null, "not_confirmed")]
    [InlineData(@"C:\Workspace\_testserver\server\VRisingServer.exe", @"C:\Workspace\_testserver\moo-world-copy-20260909", "Stress", "not_confirmed")]
    public void Gate_refuses(string process, string? save, string? confirm, string expected)
    {
        var inputs = Gate(save, confirm, process);
        if (!process.Contains("_testserver")) inputs.GameRoot = System.IO.Path.GetDirectoryName(process);
        Assert.False(StressGate.Check(inputs, out var code, out var reason));
        Assert.Equal(expected, code);
        Assert.StartsWith("refused", reason);
    }

    [Fact]
    public void Gate_reads_the_save_from_the_command_line()
    {
        var argv = new[] { "VRisingServer.exe", "-persistentDataPath", @"C:\Workspace\_testserver\moo-world-copy-20260909", "-saveName", "world1" };
        Assert.Equal(@"C:\Workspace\_testserver\moo-world-copy-20260909", StressGate.PersistentDataPathFrom(argv));
        Assert.Null(StressGate.PersistentDataPathFrom(new[] { "VRisingServer.exe", "-saveName", "world1" }));
        Assert.Null(StressGate.PersistentDataPathFrom(null));
    }

    // ---- planner --------------------------------------------------------------------------------

    private static string Fingerprint(StressPlan p) => string.Join("|",
        p.Owners.Select(o => o.Name + ":" + o.PlatformId + ":" + o.Clan + ":" + string.Join(",", o.Territories))
        .Concat(p.Clans.Select(c => c.Name + ":" + string.Join(",", c.Members)))
        .Concat(p.Plots.Select(x => x.Territory + ":" + x.Owner + ":" + x.Quadrant + ":" + x.Fuel + ":"
            + string.Join(",", x.Stations.Select(s => s.Kind + "@" + s.Cell.Dx + "/" + s.Cell.Dz))
            + ":" + string.Join(",", x.Chests.Select(c => c.Kind + "@" + c.X + "/" + c.Z + "#" + c.SlotFraction + "=" + string.Join("+", c.Items)))))
        .Concat(p.Refusals.Select(r => r.Territory + ":" + r.Reason)));

    [Fact]
    public void Plan_is_deterministic_for_a_seed()
    {
        var a = Fingerprint(StressPlanner.Plan(Args("{\"seed\":7}"), World147()));
        var b = Fingerprint(StressPlanner.Plan(Args("{\"seed\":7}"), World147()));
        var c = Fingerprint(StressPlanner.Plan(Args("{\"seed\":8}"), World147()));
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void Plan_layout_of_a_plot_does_not_depend_on_the_owner_count()
    {
        var few = StressPlanner.Plan(Args("{\"seed\":7,\"owners\":10,\"clans\":3}"), World147());
        var many = StressPlanner.Plan(Args("{\"seed\":7}"), World147());
        int shared = few.Plots.Select(p => p.Territory).First();
        var p1 = few.Plots.Single(p => p.Territory == shared);
        var p2 = many.Plots.Single(p => p.Territory == shared);
        Assert.Equal(p1.Quadrant, p2.Quadrant);
        Assert.Equal(p1.Chests.Select(c => c.Kind), p2.Chests.Select(c => c.Kind));
        Assert.Equal(p1.Stations.Select(s => s.Kind), p2.Stations.Select(s => s.Kind));
    }

    [Fact]
    public void Plan_counts_stay_in_range_on_the_147_plot_world()
    {
        var plan = StressPlanner.Plan(Args(), World147());
        Assert.Equal(139, plan.Plots.Count); // 147 - index 0 - 7 claimed
        Assert.Equal(8, plan.Refusals.Count);
        Assert.Equal(7, plan.Refusals.Count(r => r.Reason == "plot_taken"));
        Assert.Equal("not_a_plot", plan.Refusals.Single(r => r.Territory == 0).Reason);

        Assert.Equal(40, plan.Owners.Count);
        Assert.All(plan.Owners, o => Assert.InRange(o.Territories.Count, 2, 5));
        Assert.Equal(139, plan.Owners.Sum(o => o.Territories.Count));
        Assert.Equal(plan.Plots.Select(p => p.Territory).OrderBy(x => x), plan.Owners.SelectMany(o => o.Territories).OrderBy(x => x));
        Assert.Equal(Enumerable.Range(1, 40).Select(StressIdentity.OwnerName), plan.Owners.Select(o => o.Name));

        Assert.Equal(12, plan.Clans.Count);
        Assert.All(plan.Clans, c => Assert.InRange(c.Members.Count, 2, 5));
        Assert.Equal(Enumerable.Range(1, 40), plan.Clans.SelectMany(c => c.Members).OrderBy(x => x));
        Assert.All(plan.Owners, o => Assert.Contains(o.Ordinal, plan.Clans[o.Clan - 1].Members));

        foreach (var p in plan.Plots)
        {
            Assert.Equal(27, p.Floors.Count);
            Assert.InRange(p.Chests.Count, 10, 25);
            Assert.InRange(p.Stations.Count, 6, 10);
            Assert.InRange(p.Fuel, 100, 400);
            Assert.Equal(2, p.Stations.Count(s => s.Kind == "Workbench"));
            Assert.Equal(p.Stations.Count, p.Stations.Select(s => (s.Cell.Dx, s.Cell.Dz)).Distinct().Count());
            Assert.Equal(p.Chests.Count, p.Chests.Select(c => (c.X, c.Z)).Distinct().Count());
            var stationCells = p.Stations.Select(s => (s.Cell.Dx, s.Cell.Dz)).ToHashSet();
            Assert.All(p.Chests, c => Assert.DoesNotContain((c.Cell.Dx, c.Cell.Dz), stationCells));
            Assert.All(p.Chests, c => Assert.InRange(c.SlotFraction, 0, 1));
            Assert.All(p.Chests, c => Assert.NotEmpty(c.Prefabs));
            Assert.All(p.Floors, f => Assert.False(StressLayout.IsHeartCell(f.Dx, f.Dz)));
        }
        var chests = plan.Plots.SelectMany(p => p.Chests).ToList();
        Assert.Contains(chests, c => c.NearlyFull);
        Assert.Contains(chests, c => c.Kind != "General");
        Assert.All(chests.Where(c => c.NearlyFull), c => Assert.InRange(c.SlotFraction, 0.9, 1));
    }

    [Fact]
    public void Plan_floors_are_ordered_so_each_touches_the_heart_or_an_earlier_floor()
    {
        foreach (var p in StressPlanner.Plan(Args(), World147()).Plots.Take(20))
        {
            var placed = new HashSet<(int, int)>();
            for (int dx = -1; dx <= 1; dx++) for (int dz = -1; dz <= 1; dz++) placed.Add((dx, dz));
            foreach (var f in p.Floors)
            {
                bool touches = placed.Contains((f.Dx - 1, f.Dz)) || placed.Contains((f.Dx + 1, f.Dz))
                    || placed.Contains((f.Dx, f.Dz - 1)) || placed.Contains((f.Dx, f.Dz + 1));
                Assert.True(touches, $"plot {p.Territory} floor {f.Dx},{f.Dz}");
                placed.Add((f.Dx, f.Dz));
            }
        }
    }

    [Fact]
    public void Plan_with_no_fill_leaves_chests_empty()
    {
        var plan = StressPlanner.Plan(Args("{\"seed\":3,\"fill\":0}"), World147());
        Assert.All(plan.Plots.SelectMany(p => p.Chests), c =>
        {
            Assert.Equal(0, c.SlotFraction);
            Assert.False(c.NearlyFull);
            Assert.Empty(c.Items);
        });
    }

    [Fact]
    public void Plan_refuses_plots_with_reasons()
    {
        int b = 1000 + (5 * 200);
        var heartOnly = new bool[100 * 100];
        for (int y = 30; y < 60; y++) for (int x = 30; x < 60; x++) heartOnly[(y * 100) + x] = true;
        var world = new List<StressTerritory>
        {
            Territory(1),
            Territory(2, claimed: true),
            Territory(3, pivot: false),
            Territory(4),
            Territory(5, mask: heartOnly),
        };
        Assert.True(world[4].Buildable(b + 45, 3000 + 45));
        var plan = StressPlanner.Plan(Args("{\"seed\":1,\"plots\":[1,2,3,4,5,99]}"), world);
        var reasons = plan.Refusals.ToDictionary(r => r.Territory, r => r.Reason);
        Assert.Equal("plot_taken", reasons[2]);
        Assert.Equal("no_valid_pivot", reasons[3]);
        Assert.Equal("no_floor_fit", reasons[5]);
        Assert.Equal("no_such_plot", reasons[99]);
        Assert.Equal(new[] { 1, 4 }, plan.Plots.Select(p => p.Territory));
        Assert.Single(plan.Owners); // two plots: one owner
        Assert.Empty(plan.Clans); // one owner cannot make a clan of 2
        Assert.Contains(plan.Notes, n => n.Contains("owners reduced"));
    }

    [Fact]
    public void Plan_adjusts_clans_to_fit_two_to_five_members()
    {
        var plan = StressPlanner.Plan(Args("{\"seed\":2,\"owners\":12,\"clans\":1}"), World147());
        Assert.Equal(3, plan.Clans.Count); // 12 owners need at least 3 clans of 5 or fewer
        Assert.Contains(plan.Notes, n => n.Contains("clans adjusted"));
        var none = StressPlanner.Plan(Args("{\"seed\":2,\"owners\":12,\"clans\":0}"), World147());
        Assert.Empty(none.Clans);
        Assert.All(none.Owners, o => Assert.Equal(0, o.Clan));
    }

    [Fact]
    public void Plan_leaves_plots_beyond_five_per_owner_unassigned()
    {
        var plan = StressPlanner.Plan(Args("{\"seed\":2,\"owners\":4,\"clans\":2}"), World147());
        Assert.Equal(20, plan.Plots.Count);
        Assert.All(plan.Owners, o => Assert.Equal(5, o.Territories.Count));
        Assert.Equal(119, plan.Refusals.Count(r => r.Reason == "no_owner_capacity"));
    }

    // ---- job ------------------------------------------------------------------------------------

    private sealed class FakeWorld : IStressBackend
    {
        public readonly List<StressTerritory> Territories;
        public readonly Dictionary<ulong, string> Users = new();
        public readonly HashSet<ulong> HeartOwners = new();
        public readonly HashSet<StressStepKind> Unproven = new();
        public readonly List<string> Calls = new();
        public int Writes;
        private int _at;

        public FakeWorld(List<StressTerritory> territories) => Territories = territories;

        public bool Proven(StressStepKind kind, out string missing)
        {
            missing = Unproven.Contains(kind) ? "no path for " + kind : "";
            return !Unproven.Contains(kind);
        }

        public int BeginDiscovery() { _at = 0; return Territories.Count; }

        public bool DiscoverNext(out StressTerritory? territory)
        {
            territory = null;
            if (_at >= Territories.Count) return false;
            territory = Territories[_at++];
            return true;
        }

        public void CountExisting(int seed, out int sameSeed, out int otherSeeds)
        {
            sameSeed = otherSeeds = 0;
            foreach (var id in Users.Keys)
            {
                if (!StressIdentity.TrySeedOf(id, out int s, out _)) continue;
                if (s == seed) sameSeed++; else otherSeeds++;
            }
        }

        public List<int> UsedIndexes { get; } = new();

        public StressOutcome ReserveOwnerIndexes(IReadOnlyList<StressOwnerPlan> owners)
        {
            if (!StressIndexPlan.TryAllocate(UsedIndexes, owners.Count, out var picked, out var why))
                return StressOutcome.Refuse("owner_index", why);
            for (int i = 0; i < owners.Count; i++) owners[i].UserIndex = picked[i];
            return StressOutcome.Done();
        }

        public StressOutcome CreateOwner(StressOwnerPlan owner)
        {
            Assert.True(owner.UserIndex > 0, "owner index reserved before the write");
            Writes++;
            Users[owner.PlatformId] = owner.Name;
            return StressOutcome.Done();
        }

        private StressOutcome Write(string what) { Writes++; Calls.Add(what); return StressOutcome.Done(); }
        public StressOutcome CreateClan(StressClanPlan clan, StressPlan plan) => Write("clan");
        public StressOutcome PlaceHeart(StressPlotPlan plot, StressOwnerPlan owner) => Write("heart");
        public StressOutcome FuelHeart(StressPlotPlan plot) => Write("fuel");
        public StressOutcome PlaceFloor(StressPlotPlan plot, StressCell cell) => Write("floor");
        public StressOutcome PlaceStation(StressPlotPlan plot, StressStationPlan station) =>
            station.Number == 1 && plot.Territory == 5 ? StressOutcome.Refuse("spawn_failed", "test") : Write("station");
        public StressOutcome PlaceChest(StressPlotPlan plot, StressChestPlan chest) => Write("chest");
        public StressOutcome FillChest(StressPlotPlan plot, StressChestPlan chest) => Write("fill");

        public List<StressClearTarget> FindSeedUsers(int seed)
        {
            var list = new List<StressClearTarget>();
            foreach (var (id, name) in Users)
            {
                if (!StressIdentity.TrySeedOf(id, out int s, out int ordinal) || s != seed) continue;
                list.Add(new StressClearTarget { Entity = id.ToString(), Name = name, PlatformId = id, Ordinal = ordinal, HeartsOwned = HeartOwners.Contains(id) ? 1 : 0 });
            }
            return list;
        }

        public StressOutcome RemoveUser(StressClearTarget target)
        {
            Users.Remove(target.PlatformId);
            return StressOutcome.Done();
        }
    }

    /// <summary>A clock that moves 1 ms each time it is read.</summary>
    private static Func<double> Clock()
    {
        double t = 0;
        return () => t += 1;
    }

    private static int RunToEnd(StressJob job, int maxTicks = 100_000)
    {
        var clock = Clock();
        int ticks = 0;
        while (job.Tick(clock) && ticks < maxTicks) ticks++;
        return ticks;
    }

    private static List<StressTerritory> Small() => Enumerable.Range(1, 12).Select(i => Territory(i)).ToList();

    [Fact]
    public void Job_runs_every_step_a_few_per_tick_and_reports_per_plot()
    {
        var world = new FakeWorld(Small());
        var job = StressJob.Populate(Args("{\"seed\":4,\"owners\":4,\"clans\":2,\"confirm\":\"stress\",\"budgetMs\":4}"), world);
        Assert.False(job.Planned);
        int ticks = RunToEnd(job);
        Assert.Equal("done", job.Phase);
        Assert.True(ticks > 50, "the work is spread over many ticks");
        Assert.True(job.MaxTickMs <= 6, "a tick stops once the budget is spent");
        Assert.Equal(4, world.Users.Count);
        Assert.Equal(12, world.Calls.Count(c => c == "heart"));
        Assert.Equal(2, world.Calls.Count(c => c == "clan"));
        Assert.Equal(12 * 27, world.Calls.Count(c => c == "floor"));
        Assert.Equal(world.Calls.Count(c => c == "chest"), world.Calls.Count(c => c == "fill"));

        var rows = job.PlotResults.ToDictionary(r => r.Territory);
        Assert.Equal("partial", rows[5].Status);
        Assert.Contains(rows[5].Refusals, r => r.StartsWith("station: spawn_failed"));
        Assert.All(rows.Values.Where(r => r.Territory != 5), r => Assert.Equal("done", r.Status));
        Assert.All(rows.Values, r => Assert.True(r.Heart && r.Fuelled && r.Floors == 27));

        var status = job.Describe(plot: 5);
        Assert.Equal("done", status["phase"]);
        Assert.NotNull(status["plot"]);
        Assert.Empty((System.Collections.IDictionary)status["missing"]!);
    }

    // ---- owner User.Index -----------------------------------------------------------------------

    [Fact]
    public void Owner_indexes_fill_holes_below_the_highest_user_first_then_follow_it()
    {
        // Real users 0..9 and 12, 15: holes 10, 11, 13, 14 are used before 16, 17, ...
        var used = Enumerable.Range(0, 10).Concat(new[] { 12, 15 }).ToList();
        Assert.True(StressIndexPlan.TryAllocate(used, 6, out var picked, out var why), why);
        Assert.Equal(new[] { 10, 11, 13, 14, 16, 17 }, picked);
        Assert.Empty(picked.Intersect(used));
        Assert.Equal(picked.Length, picked.Distinct().Count());
    }

    [Fact]
    public void Forty_owners_fit_beside_a_typical_save_and_leave_headroom_for_real_players()
    {
        var used = Enumerable.Range(0, 30).ToList();
        Assert.True(StressIndexPlan.TryAllocate(used, 40, out var picked, out var why), why);
        Assert.Equal(Enumerable.Range(30, 40), picked);
        Assert.True(picked.Max() + StressIndexPlan.RealPlayerHeadroom < StressIndexPlan.MaskWidth);
    }

    [Fact]
    public void Owner_indexes_never_use_zero_and_start_at_one_on_an_empty_world()
    {
        Assert.True(StressIndexPlan.TryAllocate(Array.Empty<int>(), 3, out var picked, out _));
        Assert.Equal(new[] { 1, 2, 3 }, picked);
        Assert.True(StressIndexPlan.TryAllocate(new[] { 5 }, 2, out picked, out _));
        Assert.Equal(new[] { 1, 2 }, picked);
    }

    [Fact]
    public void Owner_indexes_fail_closed_when_the_mask_range_would_be_exceeded()
    {
        Assert.Equal(111, StressIndexPlan.MaxOwnerIndex);
        var used = Enumerable.Range(0, 100).ToList();
        Assert.False(StressIndexPlan.TryAllocate(used, 20, out var picked, out var why));
        Assert.Empty(picked);
        Assert.Contains("only 12 safe", why); // 100..111
        Assert.True(StressIndexPlan.TryAllocate(used, 12, out picked, out _));
        Assert.Equal(111, picked.Max());

        // A leftover synthetic user (ghost 10001, an older stress owner 20001) already sits far above the mask.
        Assert.False(StressIndexPlan.TryAllocate(new[] { 0, 1, 20001 }, 1, out _, out why));
        Assert.Contains("20001", why);
        Assert.False(StressIndexPlan.TryAllocate(new[] { -1 }, 1, out _, out _));
        Assert.True(StressIndexPlan.TryAllocate(new[] { 0, 1 }, 0, out picked, out _));
        Assert.Empty(picked);
    }

    [Fact]
    public void Job_refuses_before_any_write_when_owner_indexes_are_not_safe()
    {
        var world = new FakeWorld(Small());
        world.UsedIndexes.AddRange(Enumerable.Range(0, 110));
        var job = StressJob.Populate(Args("{\"seed\":4,\"owners\":4,\"clans\":0,\"confirm\":\"stress\"}"), world);
        RunToEnd(job);
        Assert.Equal("refused", job.Phase);
        Assert.StartsWith("owner_index:", job.Reason);
        Assert.Equal(0, world.Writes);
    }

    [Fact]
    public void Job_refuses_without_writing_when_a_step_has_no_proven_path()
    {
        var world = new FakeWorld(Small());
        world.Unproven.Add(StressStepKind.Heart);
        world.Unproven.Add(StressStepKind.Clan);
        var job = StressJob.Populate(Args("{\"seed\":4,\"owners\":4,\"clans\":2}"), world);
        RunToEnd(job);
        Assert.Equal("refused", job.Phase);
        Assert.StartsWith("unproven", job.Reason);
        Assert.Equal(0, world.Writes);
        Assert.Equal(new[] { "clan", "heart" }, job.Missing.Keys.OrderBy(k => k));
        Assert.NotNull(job.Plan);
        Assert.All(job.PlotResults, r => Assert.Equal("planned", r.Status));
    }

    [Fact]
    public void Job_partial_runs_only_proven_steps_and_refuses_the_plots()
    {
        var world = new FakeWorld(Small());
        world.Unproven.Add(StressStepKind.Heart);
        world.Unproven.Add(StressStepKind.Clan);
        var job = StressJob.Populate(Args("{\"seed\":4,\"owners\":4,\"clans\":2,\"partial\":true}"), world);
        RunToEnd(job);
        Assert.Equal("done", job.Phase);
        Assert.Equal(4, world.Users.Count);
        Assert.Empty(world.Calls);
        Assert.All(job.OwnerResults, o => Assert.Equal("created", o.Status));
        Assert.All(job.ClanResults, c => Assert.Equal("unproven", c.Reason));
        Assert.All(job.PlotResults, r =>
        {
            Assert.Equal("refused", r.Status);
            Assert.Equal("unproven", r.Reason);
            Assert.StartsWith("heart:", r.Detail);
        });
    }

    [Fact]
    public void Job_dry_run_plans_and_writes_nothing()
    {
        var world = new FakeWorld(Small());
        var job = StressJob.Populate(Args("{\"seed\":4,\"owners\":4,\"dryRun\":true}"), world);
        RunToEnd(job);
        Assert.Equal("done", job.Phase);
        Assert.Equal(0, world.Writes);
        Assert.Equal(12, job.Plan!.Plots.Count);
    }

    [Fact]
    public void Job_refuses_a_seed_that_is_already_populated_or_another_seed()
    {
        var world = new FakeWorld(Small());
        world.Users[StressIdentity.PlatformId(4, 1)] = "Stress01";
        var job = StressJob.Populate(Args("{\"seed\":4,\"owners\":4}"), world);
        RunToEnd(job);
        Assert.Equal("refused", job.Phase);
        Assert.Contains("stress_clear", job.Reason);

        var other = StressJob.Populate(Args("{\"seed\":5,\"owners\":4}"), world);
        RunToEnd(other);
        Assert.Equal("refused", other.Phase);
        Assert.Contains("another seed", other.Reason);
        Assert.Equal(0, world.Writes);
    }

    [Fact]
    public void Job_reports_a_world_with_no_usable_plot()
    {
        var world = new FakeWorld(new List<StressTerritory> { Territory(1, claimed: true) });
        var job = StressJob.Populate(Args("{\"seed\":1}"), world);
        RunToEnd(job);
        Assert.Equal("refused", job.Phase);
        Assert.Equal("plot_taken", job.PlotResults.Single().Reason);
    }

    [Fact]
    public void Clear_removes_the_seed_and_is_safe_to_run_twice()
    {
        var world = new FakeWorld(Small());
        RunToEnd(StressJob.Populate(Args("{\"seed\":4,\"owners\":4,\"clans\":2}"), world));
        world.Users[StressIdentity.PlatformId(9, 1)] = "Stress01"; // another seed: not touched
        world.Users[76561198000000001UL] = "RealPlayer";
        Assert.Equal(6, world.Users.Count);

        var first = StressJob.Clear(StressArgs.ParseClear(Json("{\"seed\":4,\"confirm\":\"stress\"}")), world);
        RunToEnd(first);
        Assert.Equal("done", first.Phase);
        Assert.Equal(4, first.Cleared.Count(c => c.Status == "removed"));
        Assert.Equal(2, world.Users.Count);
        Assert.Contains(76561198000000001UL, world.Users.Keys);

        var second = StressJob.Clear(StressArgs.ParseClear(Json("{\"seed\":4,\"confirm\":\"stress\"}")), world);
        RunToEnd(second);
        Assert.Equal("done", second.Phase);
        Assert.Empty(second.Cleared);
        Assert.Equal(0, second.Describe()["removed"]);
        Assert.Equal(2, world.Users.Count);
    }

    [Fact]
    public void Clear_keeps_an_owner_that_still_owns_a_heart_and_dry_run_removes_nothing()
    {
        var world = new FakeWorld(Small());
        ulong a = StressIdentity.PlatformId(4, 1), b = StressIdentity.PlatformId(4, 2);
        world.Users[a] = "Stress01";
        world.Users[b] = "Stress02";
        world.HeartOwners.Add(a);

        var dry = StressJob.Clear(StressArgs.ParseClear(Json("{\"seed\":4,\"dryRun\":true}")), world);
        RunToEnd(dry);
        Assert.Equal(2, dry.Cleared.Count(c => c.Status == "planned"));
        Assert.Equal(2, world.Users.Count);

        var job = StressJob.Clear(StressArgs.ParseClear(Json("{\"seed\":4,\"confirm\":\"stress\"}")), world);
        RunToEnd(job);
        Assert.Equal("heart_left", job.Cleared.Single(c => c.Name == "Stress01").Reason);
        Assert.Equal("removed", job.Cleared.Single(c => c.Name == "Stress02").Status);
        Assert.Equal(new[] { a }, world.Users.Keys);
    }

    [Fact]
    public void Job_cancel_stops_between_steps()
    {
        var world = new FakeWorld(Small());
        var job = StressJob.Populate(Args("{\"seed\":4,\"owners\":4}"), world);
        var clock = Clock();
        for (int i = 0; i < 10; i++) job.Tick(clock);
        job.Cancel("test");
        int writes = world.Writes;
        Assert.False(job.Tick(clock));
        Assert.Equal("cancelled", job.Phase);
        Assert.Equal(writes, world.Writes);
    }
}
