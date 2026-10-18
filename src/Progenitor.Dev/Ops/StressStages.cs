using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace Progenitor.Dev.Ops;

// Game-free half of the staged stress ops that work on castles stress_populate's owners already
// hold: stress_owner_groups (read only), stress_clan, stress_refine and stress_stations. Argument
// parse, the owner grouping with its spread score, islands and raw-supply castles, the per-station
// input need for N hours, chest selection and the station top-up plan. StressStageOps applies them
// to the live server world. Compiled into the test project, so no game, Unity or BepInEx types.

/// <summary>Where an island's raw inputs go.</summary>
internal enum StressSupply
{
    /// <summary>Spread for clan and castle-share islands, local for solo owners.</summary>
    Auto,
    /// <summary>Raws only in a few castles of the island, the ones farthest apart.</summary>
    Spread,
    /// <summary>Every castle gets the raws its own stations use.</summary>
    Local,
}

/// <summary>Parsed arguments of the staged stress ops.</summary>
internal sealed class StressStageArgs
{
    public static readonly int[] DefaultGroups = { 8, 6, 4, 2 };
    public const int MaxGroups = 12;
    public const double MaxHours = 24;
    public const int MaxPerCastle = 30;
    public const int MaxEmptyChests = 10;

    public int Seed { get; private set; }
    /// <summary>Clan sizes, largest first as given.</summary>
    public IReadOnlyList<int> Groups { get; private set; } = DefaultGroups;
    /// <summary>Castle-share owners; null = half of the owners outside clans (rounded up).</summary>
    public int? Share { get; private set; }
    /// <summary>True when <c>plots</c> was omitted or <c>"stress"</c>: every castle this seed's owners hold.</summary>
    public bool AllPlots { get; private set; } = true;
    public IReadOnlyList<int> Plots { get; private set; } = Array.Empty<int>();
    public double Hours { get; private set; } = 1;
    public int PerCastle { get; private set; } = 15;
    public StressSupply Supply { get; private set; } = StressSupply.Auto;
    /// <summary>Chests per castle that get nothing, so products have somewhere to go.</summary>
    public int EmptyChests { get; private set; } = 3;
    /// <summary>Fraction of an island's castles that get its raws when the supply is spread (at least one).</summary>
    public double Suppliers { get; private set; } = 1.0 / 3.0;
    /// <summary>Extra refining speed factor on top of the server's refinement rate (an enclosed room is 1.25).</summary>
    public double Speed { get; private set; } = 1;
    /// <summary>stress_clan: only this clan ordinal (1-based), to prove one clan first.</summary>
    public int? OnlyClan { get; private set; }
    /// <summary>stress_stations experiment: stations placed per castle at most (default 1).</summary>
    public int Limit { get; private set; } = 1;
    public const int MaxExperimentPlots = 3;
    public bool DryRun { get; private set; }
    public string? Confirm { get; private set; }
    /// <summary>stress_clan: the explicit opt-in for the game's own clan routine (a native call).</summary>
    public bool Experiment { get; private set; }
    public double BudgetMs { get; private set; } = StressArgs.DefaultBudgetMs;

    public static StressStageArgs ParseGroups(JsonElement args)
    {
        var a = new StressStageArgs { Seed = ReadSeed(args) };
        ParseGroupShape(args, a);
        return a;
    }

    public static StressStageArgs ParseClan(JsonElement args)
    {
        var a = ParseGroups(args);
        a.OnlyClan = OptInt(args, "clan", 1, MaxGroups);
        a.DryRun = Bool(args, "dryRun", false);
        a.Confirm = Str(args, "confirm");
        a.Experiment = Bool(args, "experiment", false);
        a.BudgetMs = Double(args, "budgetMs", StressArgs.DefaultBudgetMs, StressArgs.MinBudgetMs, StressArgs.MaxBudgetMs);
        if (a.OnlyClan is int c && c > a.Groups.Count)
            throw new ArgumentException($"args.clan {c} is above the {a.Groups.Count} clan(s) in groups");
        return a;
    }

    public static StressStageArgs ParseRefine(JsonElement args)
    {
        var a = ParseGroups(args);
        ParsePlots(args, a);
        a.Hours = Double(args, "hours", 1, 0.05, MaxHours);
        a.EmptyChests = OptInt(args, "emptyChests", 0, MaxEmptyChests) ?? 3;
        a.Suppliers = Double(args, "suppliers", 1.0 / 3.0, 0.05, 1);
        a.Speed = Double(args, "speed", 1, 0.25, 10);
        var supply = Str(args, "supply");
        a.Supply = supply == null ? StressSupply.Auto : supply.Trim().ToLowerInvariant() switch
        {
            "auto" => StressSupply.Auto,
            "spread" => StressSupply.Spread,
            "local" => StressSupply.Local,
            _ => throw new ArgumentException("args.supply must be \"spread\", \"local\" or \"auto\""),
        };
        a.DryRun = Bool(args, "dryRun", false);
        a.Confirm = Str(args, "confirm");
        a.BudgetMs = Double(args, "budgetMs", StressArgs.DefaultBudgetMs, StressArgs.MinBudgetMs, StressArgs.MaxBudgetMs);
        return a;
    }

    public static StressStageArgs ParseStations(JsonElement args)
    {
        var a = new StressStageArgs { Seed = ReadSeed(args) };
        ParsePlots(args, a);
        a.PerCastle = OptInt(args, "perCastle", 1, MaxPerCastle) ?? 15;
        a.DryRun = Bool(args, "dryRun", false);
        a.Experiment = Bool(args, "experiment", false);
        a.Limit = OptInt(args, "limit", 1, MaxPerCastle) ?? 1;
        if (a.Experiment && !a.DryRun && (a.AllPlots || a.Plots.Count > MaxExperimentPlots))
            throw new ArgumentException($"experiment placement needs plots:[...] with 1..{MaxExperimentPlots} territories (prove it on one castle first)");
        a.Confirm = Str(args, "confirm");
        a.BudgetMs = Double(args, "budgetMs", StressArgs.DefaultBudgetMs, StressArgs.MinBudgetMs, StressArgs.MaxBudgetMs);
        return a;
    }

    private static void ParseGroupShape(JsonElement args, StressStageArgs a)
    {
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("groups", out var g) && g.ValueKind != JsonValueKind.Null)
        {
            if (g.ValueKind != JsonValueKind.Array || g.GetArrayLength() == 0 || g.GetArrayLength() > MaxGroups)
                throw new ArgumentException($"args.groups must be an array of 1..{MaxGroups} clan sizes");
            var sizes = new List<int>();
            foreach (var item in g.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt32(out int n) || n < 2 || n > StressArgs.MaxOwners)
                    throw new ArgumentException("args.groups entries must be clan sizes of at least 2");
                sizes.Add(n);
            }
            a.Groups = sizes;
        }
        a.Share = OptInt(args, "share", 0, StressArgs.MaxOwners);
    }

    private static void ParsePlots(JsonElement args, StressStageArgs a)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("plots", out var el) || el.ValueKind == JsonValueKind.Null)
            return;
        if (el.ValueKind == JsonValueKind.String)
        {
            if (!string.Equals(el.GetString()?.Trim(), "stress", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("args.plots must be \"stress\" or an array of territory indices");
            return;
        }
        if (el.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("args.plots must be \"stress\" or an array of territory indices");
        var set = new SortedSet<int>();
        foreach (var item in el.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt32(out int n) || n < 0)
                throw new ArgumentException("args.plots entries must be territory indices (whole numbers >= 0)");
            set.Add(n);
        }
        if (set.Count == 0) throw new ArgumentException("args.plots is an empty array");
        a.AllPlots = false;
        a.Plots = new List<int>(set);
    }

    private static int ReadSeed(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("seed", out var el))
            throw new ArgumentException("args.seed is required (0.." + StressArgs.MaxSeed + ")");
        if (el.ValueKind != JsonValueKind.Number || !el.TryGetInt32(out int seed) || seed < 0 || seed > StressArgs.MaxSeed)
            throw new ArgumentException("args.seed must be a whole number 0.." + StressArgs.MaxSeed);
        return seed;
    }

    private static int? OptInt(JsonElement args, string key, int lo, int hi)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(key, out var el) || el.ValueKind == JsonValueKind.Null)
            return null;
        if (el.ValueKind != JsonValueKind.Number || !el.TryGetInt32(out int n) || n < lo || n > hi)
            throw new ArgumentException($"args.{key} must be a whole number {lo}..{hi}");
        return n;
    }

    private static double Double(JsonElement args, string key, double fallback, double lo, double hi)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(key, out var el) || el.ValueKind == JsonValueKind.Null)
            return fallback;
        if (el.ValueKind != JsonValueKind.Number || !el.TryGetDouble(out double d) || double.IsNaN(d) || d < lo || d > hi)
            throw new ArgumentException($"args.{key} must be a number {lo.ToString(CultureInfo.InvariantCulture)}..{hi.ToString(CultureInfo.InvariantCulture)}");
        return d;
    }

    private static bool Bool(JsonElement args, string key, bool fallback)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(key, out var el)) return fallback;
        return el.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => fallback,
            _ => throw new ArgumentException($"args.{key} must be true or false"),
        };
    }

    private static string? Str(JsonElement args, string key) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;
}

/// <summary>One castle a stress owner holds, as the grouping sees it.</summary>
internal sealed class StressCastleSpot
{
    public int Territory { get; set; }
    /// <summary>World x / z of the territory's bounds center, meters.</summary>
    public double X { get; set; }
    public double Z { get; set; }
    /// <summary>World region (map zone) name; empty when unknown.</summary>
    public string Region { get; set; } = "";
    public int Owner { get; set; }
}

/// <summary>One stress owner and the castles it holds.</summary>
internal sealed class StressOwnerSpots
{
    public int Ordinal { get; set; }
    public string Name { get; set; } = "";
    public ulong PlatformId { get; set; }
    public int UserIndex { get; set; }
    /// <summary>The clan the owner is already in ("index:version"), empty when none.</summary>
    public string ExistingClan { get; set; } = "";
    public List<StressCastleSpot> Castles { get; } = new();
}

internal enum StressGroupKind { Clan, Share, Solo }

/// <summary>Spread of one group's castles.</summary>
internal sealed class StressSpreadMetrics
{
    public int Castles { get; set; }
    public int Regions { get; set; }
    /// <summary>Mean distance between castles of different members (meters); 0 with fewer than two members' castles.</summary>
    public double MeanPairDistance { get; set; }
    /// <summary>Closest two castles of different members (meters).</summary>
    public double MinPairDistance { get; set; }
    /// <summary>Mean over castles of the distance to the nearest castle of another member (meters).</summary>
    public double MeanNearestDistance { get; set; }
    /// <summary>0..1 combined spread (0.4 mean pair, 0.4 mean nearest, both over the map diagonal, 0.2 region share).</summary>
    public double Score { get; set; }
}

/// <summary>A clan, the castle-share set or the solo set.</summary>
internal sealed class StressOwnerGroup
{
    public StressGroupKind Kind { get; set; }
    /// <summary>Clan ordinal (1-based); 0 for share and solo.</summary>
    public int Ordinal { get; set; }
    public string Name { get; set; } = "";
    public List<StressOwnerSpots> Members { get; } = new();
    public StressSpreadMetrics Spread { get; set; } = new();

    public IEnumerable<StressCastleSpot> Castles => Members.SelectMany(m => m.Castles);
}

/// <summary>The owner grouping and its spread score.</summary>
internal sealed class StressGrouping
{
    public List<StressOwnerGroup> Clans { get; } = new();
    public StressOwnerGroup Share { get; } = new() { Kind = StressGroupKind.Share, Name = "castle-share" };
    public StressOwnerGroup Solo { get; } = new() { Kind = StressGroupKind.Solo, Name = "solo" };
    /// <summary>Share of nearest-neighbor plot pairs (other owners, 4 nearest each) that belong to different groups, 0..1.</summary>
    public double Mixing { get; set; }
    public double Score { get; set; }
    public double Diagonal { get; set; }
    public int RegionsTotal { get; set; }
    public int Swaps { get; set; }
    public List<string> Notes { get; } = new();

    public IEnumerable<StressOwnerGroup> All()
    {
        foreach (var c in Clans) yield return c;
        yield return Share;
        yield return Solo;
    }

    /// <summary>The group of the owner with this ordinal, or null.</summary>
    public StressOwnerGroup? GroupOf(int ordinal)
    {
        foreach (var g in All())
            foreach (var m in g.Members)
                if (m.Ordinal == ordinal) return g;
        return null;
    }
}

/// <summary>
/// Decides which stress owner goes into which group: clans of the requested sizes, castle-share owners
/// (3+ castles, each owner's castles one island) and solo owners. Owners keep their castles; only the
/// grouping changes. Deterministic for a seed.
///
/// <para>Solo first: the owners whose castles lie closest together (smallest mean distance between their
/// own castles). The rest are assigned by a hill climb over pairwise swaps that maximizes
/// <c>0.55 x mean clan spread + 0.35 x mixing + 0.10 x mean castle-share own spread</c>, where a clan's spread is
/// its castles' mean pairwise and mean nearest-other-member distance over the map diagonal plus the share of
/// map regions it covers, and mixing is the share of each plot's 4 nearest other-owner plots that sit in a
/// different group (each clan its own group, castle-share one, solo one).</para>
/// </summary>
internal static class StressGroupPlanner
{
    public const int Neighbors = 4;
    public const int MinShareCastles = 3;
    public const int MaxPasses = 40;

    public static StressGrouping Plan(IReadOnlyList<StressOwnerSpots> ownersIn, IReadOnlyList<int> clanSizes, int? shareCount, int seed, out string? refusal)
    {
        refusal = null;
        var result = new StressGrouping();
        var owners = ownersIn.OrderBy(o => o.Ordinal).ToList();
        int n = owners.Count;
        int clanTotal = clanSizes.Sum();
        if (n == 0) { refusal = "no stress owners with castles for this seed"; return result; }
        if (clanTotal > n) { refusal = $"groups need {clanTotal} owners; seed has {n}"; return result; }
        int rest = n - clanTotal;
        int eligibleShare = owners.Count(o => o.Castles.Count >= MinShareCastles);
        int share = shareCount ?? (rest + 1) / 2;
        if (share > rest) { refusal = $"share {share} is above the {rest} owner(s) outside clans"; return result; }
        if (share > eligibleShare) { refusal = $"share {share} needs owners with {MinShareCastles}+ castles; only {eligibleShare} have that many"; return result; }
        int solo = rest - share;

        var all = owners.SelectMany(o => o.Castles).ToList();
        result.Diagonal = Math.Max(1, Diagonal(all));
        result.RegionsTotal = all.Select(c => c.Region).Where(r => r.Length > 0).Distinct(StringComparer.Ordinal).Count();
        var neighbors = NeighborPairs(all);

        // Solo: the most compact owners, but never so many that the share set cannot be filled.
        var byCompact = owners.OrderBy(o => OwnSpread(o)).ThenBy(o => o.Ordinal).ToList();
        var soloSet = new List<StressOwnerSpots>();
        int eligibleLeft = eligibleShare;
        foreach (var o in byCompact)
        {
            if (soloSet.Count >= solo) break;
            bool eligible = o.Castles.Count >= MinShareCastles;
            if (eligible && eligibleLeft - 1 < share) continue;
            soloSet.Add(o);
            if (eligible) eligibleLeft--;
        }

        // Labels for the pool: 1..k clan, -1 share.
        var pool = owners.Where(o => !soloSet.Contains(o)).ToList();
        var rng = StressRng.For(seed, "owner-groups");
        var order = Enumerable.Range(0, pool.Count).ToList();
        rng.Shuffle(order);
        var label = new int[pool.Count];
        int at = 0;
        // Share first from eligible owners (in shuffled order), then clans.
        foreach (int i in order)
            if (at < share && pool[i].Castles.Count >= MinShareCastles) { label[i] = -1; at++; }
        int clanIdx = 0, filled = 0;
        foreach (int i in order)
        {
            if (label[i] == -1) continue;
            while (clanIdx < clanSizes.Count && filled >= clanSizes[clanIdx]) { clanIdx++; filled = 0; }
            label[i] = clanIdx + 1;
            filled++;
        }

        var ordinalIndex = new Dictionary<int, int>();
        for (int i = 0; i < pool.Count; i++) ordinalIndex[pool[i].Ordinal] = i;
        var soloOrdinals = new HashSet<int>(soloSet.Select(o => o.Ordinal));

        double Score(int[] l) => Evaluate(pool, l, clanSizes.Count, soloOrdinals, ordinalIndex, neighbors, result.Diagonal, result.RegionsTotal, out _);

        double best = Score(label);
        int swaps = 0;
        for (int pass = 0; pass < MaxPasses; pass++)
        {
            bool improved = false;
            for (int i = 0; i < pool.Count; i++)
            {
                for (int j = i + 1; j < pool.Count; j++)
                {
                    if (label[i] == label[j]) continue;
                    if ((label[i] == -1 && pool[j].Castles.Count < MinShareCastles) || (label[j] == -1 && pool[i].Castles.Count < MinShareCastles)) continue;
                    (label[i], label[j]) = (label[j], label[i]);
                    double s = Score(label);
                    if (s > best + 1e-12) { best = s; improved = true; swaps++; }
                    else (label[i], label[j]) = (label[j], label[i]);
                }
            }
            if (!improved) break;
        }

        for (int k = 0; k < clanSizes.Count; k++)
            result.Clans.Add(new StressOwnerGroup { Kind = StressGroupKind.Clan, Ordinal = k + 1, Name = StressIdentity.ClanName(k + 1) });
        for (int i = 0; i < pool.Count; i++)
        {
            if (label[i] == -1) result.Share.Members.Add(pool[i]);
            else result.Clans[label[i] - 1].Members.Add(pool[i]);
        }
        result.Solo.Members.AddRange(soloSet.OrderBy(o => o.Ordinal));
        foreach (var g in result.All())
        {
            g.Members.Sort((a, b) => a.Ordinal.CompareTo(b.Ordinal));
            g.Spread = Spread(g.Members, result.Diagonal, result.RegionsTotal);
        }
        Evaluate(pool, label, clanSizes.Count, soloOrdinals, ordinalIndex, neighbors, result.Diagonal, result.RegionsTotal, out double mixing);
        result.Mixing = mixing;
        result.Score = best;
        result.Swaps = swaps;
        if (owners.Any(o => o.ExistingClan.Length > 0))
            result.Notes.Add("some owners are already in a clan; stress_clan leaves them out");
        return result;
    }

    /// <summary>Mean distance between one owner's own castles (0 for one castle).</summary>
    public static double OwnSpread(StressOwnerSpots o)
    {
        int c = o.Castles.Count;
        if (c < 2) return 0;
        double sum = 0;
        int pairs = 0;
        for (int i = 0; i < c; i++)
            for (int j = i + 1; j < c; j++) { sum += Dist(o.Castles[i], o.Castles[j]); pairs++; }
        return sum / pairs;
    }

    public static StressSpreadMetrics Spread(IReadOnlyList<StressOwnerSpots> members, double diagonal, int regionsTotal)
    {
        var castles = members.SelectMany(m => m.Castles).ToList();
        var m = new StressSpreadMetrics
        {
            Castles = castles.Count,
            Regions = castles.Select(c => c.Region).Where(r => r.Length > 0).Distinct(StringComparer.Ordinal).Count(),
        };
        double sum = 0, min = double.MaxValue, nearestSum = 0;
        int pairs = 0, nearestCount = 0;
        for (int i = 0; i < castles.Count; i++)
        {
            double nearest = double.MaxValue;
            for (int j = 0; j < castles.Count; j++)
            {
                if (i == j || castles[i].Owner == castles[j].Owner) continue;
                double d = Dist(castles[i], castles[j]);
                if (d < nearest) nearest = d;
                if (j > i) { sum += d; pairs++; if (d < min) min = d; }
            }
            if (nearest < double.MaxValue) { nearestSum += nearest; nearestCount++; }
        }
        m.MeanPairDistance = pairs > 0 ? sum / pairs : 0;
        m.MinPairDistance = pairs > 0 ? min : 0;
        m.MeanNearestDistance = nearestCount > 0 ? nearestSum / nearestCount : 0;
        int regionCap = Math.Max(1, Math.Min(regionsTotal, castles.Count));
        double regionShare = regionsTotal > 0 ? Math.Min(1, (double)m.Regions / regionCap) : 0;
        m.Score = 0.4 * (m.MeanPairDistance / diagonal) + 0.4 * (m.MeanNearestDistance / diagonal) + 0.2 * regionShare;
        return m;
    }

    private static double Evaluate(IReadOnlyList<StressOwnerSpots> pool, int[] label, int clanCount, HashSet<int> solo,
        Dictionary<int, int> ordinalIndex, List<(StressCastleSpot A, StressCastleSpot B)> neighbors, double diagonal, int regionsTotal, out double mixing)
    {
        double clanScore = 0;
        var members = new List<StressOwnerSpots>();
        for (int k = 1; k <= clanCount; k++)
        {
            members.Clear();
            for (int i = 0; i < pool.Count; i++) if (label[i] == k) members.Add(pool[i]);
            clanScore += Spread(members, diagonal, regionsTotal).Score;
        }
        clanScore = clanCount > 0 ? clanScore / clanCount : 0;

        double shareScore = 0;
        int shareN = 0;
        for (int i = 0; i < pool.Count; i++)
            if (label[i] == -1) { shareScore += OwnSpread(pool[i]) / diagonal; shareN++; }
        shareScore = shareN > 0 ? shareScore / shareN : 0;

        int different = 0;
        foreach (var (a, b) in neighbors)
            if (Key(a.Owner, label, solo, ordinalIndex) != Key(b.Owner, label, solo, ordinalIndex)) different++;
        mixing = neighbors.Count > 0 ? (double)different / neighbors.Count : 1;
        return 0.55 * clanScore + 0.35 * mixing + 0.10 * shareScore;
    }

    private static int Key(int ordinal, int[] label, HashSet<int> solo, Dictionary<int, int> ordinalIndex)
    {
        if (solo.Contains(ordinal)) return -2;
        return ordinalIndex.TryGetValue(ordinal, out int i) ? label[i] : -3;
    }

    /// <summary>Each plot with its <see cref="Neighbors"/> nearest plots of other owners (each pair once per side).</summary>
    public static List<(StressCastleSpot A, StressCastleSpot B)> NeighborPairs(IReadOnlyList<StressCastleSpot> all)
    {
        var pairs = new List<(StressCastleSpot, StressCastleSpot)>();
        foreach (var a in all)
        {
            var near = all.Where(b => b.Owner != a.Owner)
                .OrderBy(b => Dist(a, b)).ThenBy(b => b.Territory)
                .Take(Neighbors);
            foreach (var b in near) pairs.Add((a, b));
        }
        return pairs;
    }

    public static double Dist(StressCastleSpot a, StressCastleSpot b)
    {
        double dx = a.X - b.X, dz = a.Z - b.Z;
        return Math.Sqrt(dx * dx + dz * dz);
    }

    private static double Diagonal(IReadOnlyList<StressCastleSpot> all)
    {
        if (all.Count == 0) return 0;
        double minX = all.Min(c => c.X), maxX = all.Max(c => c.X), minZ = all.Min(c => c.Z), maxZ = all.Max(c => c.Z);
        double dx = maxX - minX, dz = maxZ - minZ;
        return Math.Sqrt(dx * dx + dz * dz);
    }
}

/// <summary>A set of castles that exchange items: a clan, one castle-share owner's castles, or one solo owner's castles.</summary>
internal sealed class StressIsland
{
    public string Id { get; set; } = "";
    public StressGroupKind Kind { get; set; }
    public StressSupply Supply { get; set; }
    public List<StressCastleSpot> Castles { get; } = new();
    /// <summary>Territories that get the island's raws.</summary>
    public List<int> Suppliers { get; } = new();
}

internal static class StressIslands
{
    /// <summary>Clans are one island each; every castle-share and solo owner is one island of its own castles.</summary>
    public static List<StressIsland> Build(StressGrouping g, StressSupply supply, double supplierShare)
    {
        var list = new List<StressIsland>();
        foreach (var c in g.Clans)
            list.Add(Make(c.Name, StressGroupKind.Clan, c.Castles, supply, supplierShare));
        foreach (var m in g.Share.Members)
            list.Add(Make("share:" + m.Name, StressGroupKind.Share, m.Castles, supply, supplierShare));
        foreach (var m in g.Solo.Members)
            list.Add(Make("solo:" + m.Name, StressGroupKind.Solo, m.Castles, supply, supplierShare));
        return list;
    }

    /// <summary>
    /// Only the castles in <paramref name="plots"/> (a first run on a few castles): each island keeps its
    /// castles among them, and a spread island picks its raw castles again among those. Islands with none are dropped.
    /// </summary>
    public static List<StressIsland> Restrict(IReadOnlyList<StressIsland> islands, IReadOnlyCollection<int> plots, double supplierShare)
    {
        var keep = new HashSet<int>(plots);
        var list = new List<StressIsland>();
        foreach (var i in islands)
        {
            var castles = i.Castles.Where(c => keep.Contains(c.Territory)).ToList();
            if (castles.Count == 0) continue;
            var r = new StressIsland { Id = i.Id, Kind = i.Kind, Supply = i.Supply };
            r.Castles.AddRange(castles);
            r.Suppliers.AddRange(i.Supply == StressSupply.Local ? castles.Select(c => c.Territory) : PickSuppliers(castles, supplierShare));
            list.Add(r);
        }
        return list;
    }

    public static StressSupply Resolve(StressSupply arg, StressGroupKind kind) =>
        arg != StressSupply.Auto ? arg : kind == StressGroupKind.Solo ? StressSupply.Local : StressSupply.Spread;

    private static StressIsland Make(string id, StressGroupKind kind, IEnumerable<StressCastleSpot> castles, StressSupply supply, double share)
    {
        var island = new StressIsland { Id = id, Kind = kind, Supply = Resolve(supply, kind) };
        island.Castles.AddRange(castles.OrderBy(c => c.Territory));
        if (island.Supply == StressSupply.Local)
            island.Suppliers.AddRange(island.Castles.Select(c => c.Territory));
        else
            island.Suppliers.AddRange(PickSuppliers(island.Castles, share));
        return island;
    }

    /// <summary>
    /// ceil(share x castles) castles (at least one), farthest-point first: the castle farthest from the
    /// island's center, then repeatedly the one farthest from every castle already picked. Sorted by territory.
    /// </summary>
    public static List<int> PickSuppliers(IReadOnlyList<StressCastleSpot> castles, double share)
    {
        var picked = new List<StressCastleSpot>();
        if (castles.Count == 0) return new List<int>();
        int k = Math.Max(1, Math.Min(castles.Count, (int)Math.Ceiling(share * castles.Count - 1e-9)));
        double cx = castles.Average(c => c.X), cz = castles.Average(c => c.Z);
        var center = new StressCastleSpot { X = cx, Z = cz };
        var first = castles.OrderByDescending(c => StressGroupPlanner.Dist(c, center)).ThenBy(c => c.Territory).First();
        picked.Add(first);
        while (picked.Count < k)
        {
            var next = castles.Where(c => !picked.Contains(c))
                .OrderByDescending(c => picked.Min(p => StressGroupPlanner.Dist(c, p)))
                .ThenBy(c => c.Territory).First();
            picked.Add(next);
        }
        return picked.Select(p => p.Territory).OrderBy(t => t).ToList();
    }
}

/// <summary>A recipe as stress_refine needs it.</summary>
internal sealed class StressRecipe
{
    public int Guid { get; set; }
    public string Name { get; set; } = "";
    public double DurationSeconds { get; set; }
    public bool Unlocked { get; set; } = true;
    public List<(int Item, int Amount)> Inputs { get; } = new();
}

internal static class StressRefineMath
{
    /// <summary>
    /// Raw inputs one refinement station uses in <paramref name="hours"/>. A station runs one recipe at a
    /// time, so its time is split evenly over its runnable recipes (unlocked, positive duration, with
    /// inputs). Batches per recipe = ceil(share seconds x rate / duration); each input = ceil(amount x
    /// costModifier) per batch. Rate = the server's refinement rate x the caller's speed factor.
    /// </summary>
    public static Dictionary<int, long> StationNeed(IReadOnlyList<StressRecipe> recipes, double hours, double rate, double costModifier)
    {
        var need = new Dictionary<int, long>();
        var runnable = recipes.Where(r => r.Unlocked && r.DurationSeconds > 0 && r.Inputs.Count > 0).ToList();
        if (runnable.Count == 0 || hours <= 0 || rate <= 0) return need;
        double share = hours * 3600.0 / runnable.Count;
        foreach (var r in runnable)
        {
            long batches = (long)Math.Ceiling(share * rate / r.DurationSeconds - 1e-9);
            foreach (var (item, amount) in r.Inputs)
            {
                if (amount <= 0) continue;
                long per = (long)Math.Ceiling(amount * Math.Max(0, costModifier) - 1e-9);
                need[item] = (need.TryGetValue(item, out long n) ? n : 0) + per * batches;
            }
        }
        return need;
    }

    public static void Add(Dictionary<int, long> into, IReadOnlyDictionary<int, long> add)
    {
        foreach (var kv in add) into[kv.Key] = (into.TryGetValue(kv.Key, out long n) ? n : 0) + kv.Value;
    }

    /// <summary>Split <paramref name="total"/> over <paramref name="parts"/> castles; the first ones take the remainder.</summary>
    public static List<Dictionary<int, long>> Split(IReadOnlyDictionary<int, long> total, int parts)
    {
        var list = new List<Dictionary<int, long>>();
        for (int i = 0; i < parts; i++) list.Add(new Dictionary<int, long>());
        if (parts <= 0) return list;
        foreach (var kv in total.OrderBy(k => k.Key))
        {
            long each = kv.Value / parts, rem = kv.Value % parts;
            for (int i = 0; i < parts; i++)
            {
                long v = each + (i < rem ? 1 : 0);
                if (v > 0) list[i][kv.Key] = v;
            }
        }
        return list;
    }
}

/// <summary>One chest of a castle, for stress_refine.</summary>
internal sealed class StressChest
{
    public string Entity { get; set; } = "";
    public int Order { get; set; }
    public int Slots { get; set; }
    public int UsedSlots { get; set; }
    public bool Empty => UsedSlots == 0;
}

internal static class StressChests
{
    /// <summary>
    /// Keep <paramref name="emptyChests"/> chests free for products: the empty ones last in the castle's
    /// order first, then the least used. The rest take raws, emptiest first.
    /// </summary>
    public static (List<StressChest> Reserved, List<StressChest> Stock) Split(IReadOnlyList<StressChest> chests, int emptyChests)
    {
        var reserved = chests
            .OrderBy(c => c.UsedSlots).ThenByDescending(c => c.Order)
            .Take(Math.Min(emptyChests, chests.Count)).ToList();
        var stock = chests.Where(c => !reserved.Contains(c))
            .OrderBy(c => c.UsedSlots).ThenBy(c => c.Order).ToList();
        return (reserved, stock);
    }
}

internal static class StressStationPlanner
{
    /// <summary>
    /// Station prefabs to add so a castle with <paramref name="current"/> refinement stations has
    /// <paramref name="perCastle"/>: the kinds in <paramref name="catalog"/> round-robin, starting after the
    /// kind the castle has fewest of (ties: catalog order). Deterministic; empty when the castle already has enough.
    /// </summary>
    public static List<int> Plan(IReadOnlyList<int> current, int perCastle, IReadOnlyList<int> catalog)
    {
        var add = new List<int>();
        if (catalog.Count == 0 || current.Count >= perCastle) return add;
        var counts = catalog.ToDictionary(g => g, g => current.Count(c => c == g));
        while (current.Count + add.Count < perCastle)
        {
            int pick = catalog.OrderBy(g => counts[g]).ThenBy(g => IndexOf(catalog, g)).First();
            add.Add(pick);
            counts[pick]++;
        }
        return add;
    }

    private static int IndexOf(IReadOnlyList<int> list, int value)
    {
        for (int i = 0; i < list.Count; i++) if (list[i] == value) return i;
        return int.MaxValue;
    }
}

/// <summary>A tile rectangle, both ends inclusive.</summary>
internal readonly struct StressRect
{
    public StressRect(int minX, int minY, int maxX, int maxY)
    {
        MinX = Math.Min(minX, maxX);
        MinY = Math.Min(minY, maxY);
        MaxX = Math.Max(minX, maxX);
        MaxY = Math.Max(minY, maxY);
    }

    public int MinX { get; }
    public int MinY { get; }
    public int MaxX { get; }
    public int MaxY { get; }
    public int Width => MaxX - MinX + 1;
    public int Depth => MaxY - MinY + 1;

    public bool Overlaps(StressRect o, int margin = 0) =>
        MinX - margin <= o.MaxX && o.MinX <= MaxX + margin && MinY - margin <= o.MaxY && o.MinY <= MaxY + margin;
}

/// <summary>
/// Where a new station can stand in an existing castle (the stress_stations experiment): a
/// <paramref name="width"/> x <paramref name="depth"/> tile block fully on the castle's floors and clear of every
/// other building's footprint plus <paramref name="margin"/> tiles. Footprints are taken inclusive at both ends, so
/// the test errs on the side of free space. Scans rows (y, then x) from the floors' low corner; deterministic.
/// </summary>
internal static class StressSpotFinder
{
    public static StressRect? Find(IReadOnlyList<StressRect> floors, IReadOnlyList<StressRect> occupied, int width, int depth, int margin)
    {
        if (floors.Count == 0 || width < 1 || depth < 1) return null;
        var covered = new HashSet<(int, int)>();
        foreach (var f in floors)
            for (int x = f.MinX; x <= f.MaxX; x++)
                for (int y = f.MinY; y <= f.MaxY; y++)
                    covered.Add((x, y));
        int minX = floors.Min(f => f.MinX), minY = floors.Min(f => f.MinY), maxX = floors.Max(f => f.MaxX), maxY = floors.Max(f => f.MaxY);
        for (int y = minY; y + depth - 1 <= maxY; y++)
        {
            for (int x = minX; x + width - 1 <= maxX; x++)
            {
                var r = new StressRect(x, y, x + width - 1, y + depth - 1);
                bool onFloor = true;
                for (int tx = r.MinX; tx <= r.MaxX && onFloor; tx++)
                    for (int ty = r.MinY; ty <= r.MaxY && onFloor; ty++)
                        if (!covered.Contains((tx, ty))) onFloor = false;
                if (!onFloor) continue;
                bool clear = true;
                foreach (var o in occupied)
                    if (r.Overlaps(o, margin)) { clear = false; break; }
                if (clear) return r;
            }
        }
        return null;
    }
}
