using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Progenitor.Castles;

namespace Progenitor.Dev.Ops;

// Game-free half of the stress_populate / stress_clear / stress_status Dev ops: argument parse,
// the safety gate, test-owner identities, the deterministic random stream and the planner that
// turns a seed plus the free castle territories into owners, clans and per-plot castle layouts.
// StressOps applies a plan through StressJob against the live server world. Compiled into the
// test project, so no game, Unity or BepInEx types here.

/// <summary>Parsed arguments of <c>stress_populate</c> and <c>stress_clear</c>.</summary>
internal sealed class StressArgs
{
    public const string ConfirmWord = "stress";
    public const int MaxSeed = 9999;
    public const int MaxOwners = 99;
    public const int DefaultOwners = 40;
    public const int DefaultClans = 12;
    public const double DefaultBudgetMs = 4;
    public const double MinBudgetMs = 0.5;
    public const double MaxBudgetMs = 20;
    public const int MaxChests = 30;
    public const int MaxStations = 12;

    public int Seed { get; private set; }
    public int Owners { get; private set; } = DefaultOwners;
    public int Clans { get; private set; } = DefaultClans;
    /// <summary>True for <c>plots:"free"</c> (the default): every free plot.</summary>
    public bool AllFree { get; private set; } = true;
    /// <summary>Territory indices for <c>plots:[...]</c>, sorted and distinct.</summary>
    public IReadOnlyList<int> Plots { get; private set; } = Array.Empty<int>();
    public int ChestsMin { get; private set; } = 10;
    public int ChestsMax { get; private set; } = 25;
    public int StationsMin { get; private set; } = 6;
    public int StationsMax { get; private set; } = 10;
    /// <summary>0..1: how full an ordinary chest is on average.</summary>
    public double Fill { get; private set; } = 0.5;
    public string? Confirm { get; private set; }
    public double BudgetMs { get; private set; } = DefaultBudgetMs;
    /// <summary>Plan and report only: no writes.</summary>
    public bool DryRun { get; private set; }
    /// <summary>Run the steps that have a proven path and refuse the rest per plot.</summary>
    public bool Partial { get; private set; }

    public bool Confirmed => string.Equals(Confirm, ConfirmWord, StringComparison.Ordinal);

    /// <summary>Parse <c>stress_populate</c> args. Throws <see cref="ArgumentException"/> on a bad value.</summary>
    public static StressArgs ParsePopulate(JsonElement args)
    {
        var a = new StressArgs();
        a.Seed = RequiredSeed(args);
        a.Owners = Int(args, "owners", DefaultOwners, 1, MaxOwners);
        a.Clans = Int(args, "clans", DefaultClans, 0, MaxOwners);
        ParsePlots(args, a);
        (a.ChestsMin, a.ChestsMax) = Range(args, "chests", 10, 25, 0, MaxChests);
        (a.StationsMin, a.StationsMax) = Range(args, "stations", 6, 10, 0, MaxStations);
        int itemCells = StressLayout.FloorCellCount;
        int needed = a.StationsMax + ((a.ChestsMax + StressLayout.ChestsPerCell - 1) / StressLayout.ChestsPerCell);
        if (needed > itemCells)
            throw new ArgumentException(
                $"stations max {a.StationsMax} plus chests max {a.ChestsMax} need {needed} floor cells; the {StressLayout.BlockCells}x{StressLayout.BlockCells} block has {itemCells} beside the heart");
        a.Fill = Double(args, "fill", 0.5, 0, 1);
        a.Confirm = Str(args, "confirm");
        a.BudgetMs = Double(args, "budgetMs", DefaultBudgetMs, MinBudgetMs, MaxBudgetMs);
        a.DryRun = Bool(args, "dryRun", false);
        a.Partial = Bool(args, "partial", false);
        return a;
    }

    /// <summary>Parse <c>stress_clear</c> args: <c>seed</c>, <c>confirm</c>, <c>budgetMs</c>, <c>dryRun</c>.</summary>
    public static StressArgs ParseClear(JsonElement args)
    {
        var a = new StressArgs();
        a.Seed = RequiredSeed(args);
        a.Confirm = Str(args, "confirm");
        a.BudgetMs = Double(args, "budgetMs", DefaultBudgetMs, MinBudgetMs, MaxBudgetMs);
        a.DryRun = Bool(args, "dryRun", false);
        return a;
    }

    private static int RequiredSeed(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("seed", out var el))
            throw new ArgumentException("args.seed is required (0.." + MaxSeed + ")");
        if (el.ValueKind != JsonValueKind.Number || !el.TryGetInt32(out int seed) || seed < 0 || seed > MaxSeed)
            throw new ArgumentException("args.seed must be a whole number 0.." + MaxSeed);
        return seed;
    }

    private static void ParsePlots(JsonElement args, StressArgs a)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("plots", out var el)
            || el.ValueKind == JsonValueKind.Null)
            return;
        if (el.ValueKind == JsonValueKind.String)
        {
            if (!string.Equals(el.GetString()?.Trim(), "free", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("args.plots must be \"free\" or an array of territory indices");
            return;
        }
        if (el.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("args.plots must be \"free\" or an array of territory indices");
        var set = new SortedSet<int>();
        foreach (var item in el.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt32(out int n) || n < 0)
                throw new ArgumentException("args.plots entries must be territory indices (whole numbers >= 0)");
            set.Add(n);
        }
        if (set.Count == 0)
            throw new ArgumentException("args.plots is an empty array");
        a.AllFree = false;
        a.Plots = new List<int>(set);
    }

    private static (int, int) Range(JsonElement args, string key, int dmin, int dmax, int lo, int hi)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(key, out var el) || el.ValueKind == JsonValueKind.Null)
            return (dmin, dmax);
        if (el.ValueKind != JsonValueKind.Array || el.GetArrayLength() != 2)
            throw new ArgumentException($"args.{key} must be [min, max]");
        int[] v = new int[2];
        for (int i = 0; i < 2; i++)
        {
            var item = el[i];
            if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt32(out v[i]) || v[i] < lo || v[i] > hi)
                throw new ArgumentException($"args.{key} values must be whole numbers {lo}..{hi}");
        }
        if (v[0] > v[1])
            throw new ArgumentException($"args.{key} min {v[0]} is above max {v[1]}");
        return (v[0], v[1]);
    }

    private static int Int(JsonElement args, string key, int fallback, int lo, int hi)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(key, out var el) || el.ValueKind == JsonValueKind.Null)
            return fallback;
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

/// <summary>
/// Who a stress test owner is. The PlatformId band encodes the seed so <c>stress_clear {seed}</c>
/// can rediscover every owner from the world alone (no file, nothing held across a Dev reload).
/// The band sits above the <c>ghost</c> band (90000000000000001..+9999, swept by Dev unload) and
/// the extra-instance band (90000000000020000..+99), so neither sweep touches a stress owner.
/// </summary>
internal static class StressIdentity
{
    public const ulong PlatformIdBase = 90000000000100000UL;
    public const int PerSeed = 100;
    /// <summary>First <c>User.Index</c> tried for a stress owner (ghosts start at 10001).</summary>
    public const int IndexFloor = 20001;
    public const string OwnerPrefix = "Stress";
    public const string ClanPrefix = "StressClan";

    public static ulong PlatformId(int seed, int ordinal)
    {
        if (seed < 0 || seed > StressArgs.MaxSeed) throw new ArgumentOutOfRangeException(nameof(seed));
        if (ordinal < 1 || ordinal > StressArgs.MaxOwners) throw new ArgumentOutOfRangeException(nameof(ordinal));
        return PlatformIdBase + ((ulong)seed * PerSeed) + (ulong)ordinal;
    }

    /// <summary>True for any id in the stress band (every seed).</summary>
    public static bool IsStressPlatformId(ulong platformId) => TrySeedOf(platformId, out _, out _);

    public static bool TrySeedOf(ulong platformId, out int seed, out int ordinal)
    {
        seed = ordinal = 0;
        if (platformId <= PlatformIdBase) return false;
        ulong rel = platformId - PlatformIdBase;
        ulong s = rel / PerSeed, o = rel % PerSeed;
        if (s > StressArgs.MaxSeed || o < 1 || o > StressArgs.MaxOwners) return false;
        seed = (int)s;
        ordinal = (int)o;
        return true;
    }

    public static string OwnerName(int ordinal) => OwnerPrefix + ordinal.ToString("D2", CultureInfo.InvariantCulture);

    public static string ClanName(int ordinal) => ClanPrefix + ordinal.ToString("D2", CultureInfo.InvariantCulture);
}

/// <summary>What the gate learns about this process. Every field may be null.</summary>
internal sealed class StressGateInputs
{
    public string? ProcessPath { get; set; }
    public string? GameRoot { get; set; }
    public string? ConfigPath { get; set; }
    /// <summary>The server's <c>-persistentDataPath</c> (where the world save lives).</summary>
    public string? PersistentDataPath { get; set; }
    public string? Confirm { get; set; }
}

/// <summary>
/// Refusal rules for the stress ops. The user approved them for the Moo World copy test save on
/// the isolated test server only: any path under <c>C:\VRisingServer</c> refuses; the process
/// must run from a <c>_testserver</c> folder; the save (<c>-persistentDataPath</c>) must be a
/// <c>moo-world-copy*</c> folder under <c>_testserver</c> and not a backup (<c>.bak</c>); and the
/// operator must pass <c>confirm:"stress"</c>.
/// </summary>
internal static class StressGate
{
    public const string TestServerSegment = "_testserver";
    public const string SavePrefix = "moo-world-copy";

    public static bool Check(StressGateInputs inputs, out string code, out string reason)
    {
        code = "";
        reason = "";
        if (inputs == null) { code = "no_inputs"; reason = "no gate inputs"; return false; }
        foreach (var p in new[] { inputs.ProcessPath, inputs.GameRoot, inputs.ConfigPath, inputs.PersistentDataPath })
        {
            if (p != null && DevGate.IsUnderLiveServerRoot(p))
            {
                code = "live_server";
                reason = $"refused: {p} is under the live dedicated server ({DevGate.LiveServerRoot})";
                return false;
            }
        }
        if (!HasSegment(inputs.ProcessPath, TestServerSegment) && !HasSegment(inputs.GameRoot, TestServerSegment))
        {
            code = "not_test_server";
            reason = $"refused: this process does not run from a {TestServerSegment} folder (process '{inputs.ProcessPath}', game root '{inputs.GameRoot}')";
            return false;
        }
        var save = inputs.PersistentDataPath;
        if (string.IsNullOrWhiteSpace(save))
        {
            code = "no_save_path";
            reason = "refused: the server has no -persistentDataPath, so the save cannot be confirmed as the Moo World copy";
            return false;
        }
        if (!HasSegment(save, TestServerSegment) || !HasSavePrefixSegment(save, out bool backup) || backup)
        {
            code = "not_approved_save";
            reason = $"refused: approved only for a {SavePrefix}* save under {TestServerSegment} (not a .bak folder); this save is '{save}'";
            return false;
        }
        if (!string.Equals(inputs.Confirm, StressArgs.ConfirmWord, StringComparison.Ordinal))
        {
            code = "not_confirmed";
            reason = "refused: pass confirm:\"stress\"";
            return false;
        }
        reason = "isolated test server, " + save;
        return true;
    }

    /// <summary>The value after <c>-persistentDataPath</c> in a split command line, or null.</summary>
    public static string? PersistentDataPathFrom(IReadOnlyList<string>? argv)
    {
        if (argv == null) return null;
        for (int i = 0; i + 1 < argv.Count; i++)
        {
            if (string.Equals(argv[i], "-persistentDataPath", StringComparison.OrdinalIgnoreCase))
            {
                var v = argv[i + 1]?.Trim().Trim('"');
                return string.IsNullOrEmpty(v) ? null : v;
            }
        }
        return null;
    }

    private static string[] Segments(string path) =>
        path.Replace('/', '\\').Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);

    internal static bool HasSegment(string? path, string segment)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        foreach (var part in Segments(path))
            if (string.Equals(part, segment, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static bool HasSavePrefixSegment(string path, out bool backup)
    {
        backup = false;
        bool found = false;
        foreach (var part in Segments(path))
        {
            if (!part.StartsWith(SavePrefix, StringComparison.OrdinalIgnoreCase)) continue;
            found = true;
            if (part.IndexOf(".bak", StringComparison.OrdinalIgnoreCase) >= 0) backup = true;
        }
        return found;
    }
}

/// <summary>
/// SplitMix64: a small, fully specified generator, so a seed gives the same plan on every
/// runtime (System.Random's algorithm is an implementation detail). Each purpose gets its own
/// stream so, for example, a plot's layout does not change when the owner count does.
/// </summary>
internal sealed class StressRng
{
    private ulong _state;

    public StressRng(ulong seed) => _state = seed;

    public static StressRng For(int seed, string stream, int sub = 0)
    {
        ulong h = 1469598103934665603UL; // FNV-1a over the stream name
        foreach (char c in stream)
        {
            h ^= c;
            h *= 1099511628211UL;
        }
        return new StressRng(((ulong)(uint)seed * 0x9E3779B97F4A7C15UL) ^ h ^ ((ulong)(uint)sub << 32));
    }

    public ulong Next()
    {
        ulong z = _state += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>[0, 1).</summary>
    public double NextDouble() => (Next() >> 11) * (1.0 / (1UL << 53));

    /// <summary>Inclusive range.</summary>
    public int Range(int min, int max)
    {
        if (max <= min) return min;
        ulong span = (ulong)((long)max - min + 1);
        return (int)((long)min + (long)(Next() % span));
    }

    public void Shuffle<T>(IList<T> list)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = Range(0, i);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }
}

/// <summary>One castle territory as discovery read it.</summary>
internal sealed class StressTerritory
{
    public int Index { get; set; }
    public bool Claimed { get; set; }
    public string? ClaimedBy { get; set; }
    public string Region { get; set; } = "";
    public int MinX { get; set; }
    public int MinY { get; set; }
    public int MaxX { get; set; }
    public int MaxY { get; set; }
    /// <summary>Buildable tile mask, row-major over the bounds; null = treat every tile as buildable.</summary>
    public bool[]? Mask { get; set; }
    public bool HasPivot { get; set; }
    /// <summary>Heart pivot tile (≡ 5 mod 10), the 30 x 30 footprint around it all buildable.</summary>
    public int PivotX { get; set; }
    public int PivotY { get; set; }

    public bool Buildable(int x, int y)
    {
        int at = CastleGeometry.LocalTileIndex(x, y, MinX, MinY, MaxX, MaxY);
        if (at < 0) return false;
        return Mask == null || (at < Mask.Length && Mask[at]);
    }
}

/// <summary>A planned test owner.</summary>
internal sealed class StressOwnerPlan
{
    public int Ordinal { get; set; }
    public string Name { get; set; } = "";
    public ulong PlatformId { get; set; }
    /// <summary>Clan ordinal, 0 = no clan.</summary>
    public int Clan { get; set; }
    public List<int> Territories { get; } = new();
}

/// <summary>A planned clan of 2..5 owners.</summary>
internal sealed class StressClanPlan
{
    public int Ordinal { get; set; }
    public string Name { get; set; } = "";
    public List<int> Members { get; } = new();
}

/// <summary>One 5 m build cell, as an offset from the heart's center cell.</summary>
internal readonly struct StressCell
{
    public StressCell(int dx, int dz, int pivotX, int pivotY)
    {
        Dx = dx;
        Dz = dz;
        TileX = pivotX + (dx * StressLayout.CellTiles);
        TileY = pivotY + (dz * StressLayout.CellTiles);
    }

    public int Dx { get; }
    public int Dz { get; }
    /// <summary>Center tile of the cell (the cell spans center - 5 .. center + 5).</summary>
    public int TileX { get; }
    public int TileY { get; }
    public float X => CastleGeometry.TileToWorld(TileX);
    public float Z => CastleGeometry.TileToWorld(TileY);
}

/// <summary>A planned chest and what goes in it.</summary>
internal sealed class StressChestPlan
{
    public int Number { get; set; }
    public string Kind { get; set; } = "";
    /// <summary>Prefab names to try in order; the first the server knows is used.</summary>
    public IReadOnlyList<string> Prefabs { get; set; } = Array.Empty<string>();
    public StressCell Cell { get; set; }
    /// <summary>World position: one of the two half-cell spots of <see cref="Cell"/>.</summary>
    public float X { get; set; }
    public float Z { get; set; }
    /// <summary>Share of the chest's slots to fill, 0..1.</summary>
    public double SlotFraction { get; set; }
    public bool NearlyFull { get; set; }
    public List<string> Items { get; } = new();
}

/// <summary>A planned crafting or refining station.</summary>
internal sealed class StressStationPlan
{
    public int Number { get; set; }
    public string Kind { get; set; } = "";
    public IReadOnlyList<string> Prefabs { get; set; } = Array.Empty<string>();
    public StressCell Cell { get; set; }
}

/// <summary>A planned castle on one territory.</summary>
internal sealed class StressPlotPlan
{
    public int Territory { get; set; }
    public int Owner { get; set; }
    public int PivotX { get; set; }
    public int PivotY { get; set; }
    public float HeartX => CastleGeometry.TileToWorld(PivotX);
    public float HeartZ => CastleGeometry.TileToWorld(PivotY);
    public int Fuel { get; set; }
    /// <summary>Which corner the 6 x 6 block grows towards from the heart (0..3).</summary>
    public int Quadrant { get; set; }
    /// <summary>The block's floor cells beside the heart, nearest the heart first (so each touches one placed before it).</summary>
    public List<StressCell> Floors { get; } = new();
    public List<StressChestPlan> Chests { get; } = new();
    public List<StressStationPlan> Stations { get; } = new();
}

/// <summary>A plot the planner will not use, and why.</summary>
internal sealed class StressPlotRefusal
{
    public StressPlotRefusal(int territory, string reason, string detail)
    {
        Territory = territory;
        Reason = reason;
        Detail = detail;
    }

    public int Territory { get; }
    /// <summary>plot_taken, no_such_plot, not_a_plot, no_valid_pivot, no_floor_fit, no_owner_capacity.</summary>
    public string Reason { get; }
    public string Detail { get; }
}

/// <summary>The whole deterministic plan.</summary>
internal sealed class StressPlan
{
    public int Seed { get; set; }
    public List<StressOwnerPlan> Owners { get; } = new();
    public List<StressClanPlan> Clans { get; } = new();
    public List<StressPlotPlan> Plots { get; } = new();
    public List<StressPlotRefusal> Refusals { get; } = new();
    public List<string> Notes { get; } = new();

    public StressOwnerPlan? Owner(int ordinal) =>
        ordinal >= 1 && ordinal <= Owners.Count ? Owners[ordinal - 1] : null;
}

/// <summary>Block geometry and prefab/item tables.</summary>
internal static class StressLayout
{
    public const int CellTiles = 10;
    public const int BlockCells = 6;
    /// <summary>The heart covers 3 x 3 cells; 36 - 9 floor cells remain.</summary>
    public const int FloorCellCount = (BlockCells * BlockCells) - 9;
    public const int ChestsPerCell = 2;
    /// <summary>Half-cell offset of a chest spot from the cell center, world meters.</summary>
    public const float ChestOffset = 1.25f;
    public const double NearlyFullChance = 0.2;

    public const string HeartPrefab = "TM_BloodFountain_CastleHeart";
    public const string FloorPrefab = "TM_Castle_Floor_Foundation_Stone01";
    public const string FuelItem = "Item_BloodEssence_T01";

    public static readonly IReadOnlyList<string> ChestKinds = new[] { "General", "General", "General", "Big", "Big", "Minerals", "Alchemy", "Fish", "Gems" };

    // Only TM_Stash_Chest_Wood_General and the station names below with _Small/Fabricator/SimpleCraftingbench
    // were read live; the others are candidates resolved by name at run time and skipped when unknown.
    public static IReadOnlyList<string> ChestPrefabs(string kind) => kind switch
    {
        "General" => new[] { "TM_Stash_Chest_Wood_General" },
        "Big" => new[] { "TM_Stash_Chest_Wood_General_Big", "TM_Stash_Chest_Wood_Big" },
        "Minerals" => new[] { "TM_Stash_Chest_Wood_Minerals", "TM_Stash_Chest_Wood_General" },
        "Alchemy" => new[] { "TM_Stash_Chest_Wood_Alchemy", "TM_Stash_Chest_Wood_General" },
        "Fish" => new[] { "TM_Stash_Chest_Wood_Fish", "TM_Stash_Chest_Wood_General" },
        "Gems" => new[] { "TM_Stash_Chest_Wood_Gems", "TM_Stash_Chest_Wood_General" },
        _ => new[] { "TM_Stash_Chest_Wood_General" },
    };

    public static IReadOnlyList<string> ChestItems(string kind) => kind switch
    {
        "Minerals" => new[] { "Item_Ingredient_Stone", "Item_Ingredient_StoneBrick", "Item_Ingredient_Mineral_CopperOre", "Item_Ingredient_Mineral_CopperIngot", "Item_Ingredient_Stonedust", "Item_Ingredient_Whetstone" },
        "Alchemy" => new[] { "Item_Ingredient_Pollen", "Item_Ingredient_Plant_BloodRose", "Item_Ingredient_Plant_GhostShroom", "Item_Ingredient_Fishoil", "Item_Ingredient_MutantGrease", "Item_Ingredient_CorruptedSap" },
        "Fish" => new[] { "Item_Ingredient_FishBone", "Item_Ingredient_Fishoil", "Item_Ingredient_Fish_Corrupted_T03" },
        "Gems" => new[] { "Item_Ingredient_Crystal", "Item_Ingredient_Gemdust", "Item_Ingredient_Scourgestone" },
        _ => new[] { "Item_Ingredient_Stone", "Item_Ingredient_Wood_Standard", "Item_Ingredient_Plank", "Item_Ingredient_Plant_PlantFiber", "Item_Ingredient_Bone", "Item_Ingredient_StoneBrick", "Item_Ingredient_Clay", "Item_Ingredient_RuggedHide", "Item_Ingredient_CoarseThread" },
    };

    /// <summary>The two benches always come first; refiners follow in seeded order.</summary>
    public static readonly IReadOnlyList<string> Benches = new[] { "Workbench", "Workbench" };

    public static readonly IReadOnlyList<string> Refiners = new[] { "FurnaceSmall", "FurnaceLarge", "Sawmill", "Grinder", "Tannery", "Loom", "BloodPress", "Fabricator" };

    public static IReadOnlyList<string> StationPrefabs(string kind) => kind switch
    {
        "FurnaceSmall" => new[] { "TM_RefinementStation_Furnace_Small" },
        "FurnaceLarge" => new[] { "TM_RefinementStation_Furnace_Large", "TM_RefinementStation_Furnace" },
        "Sawmill" => new[] { "TM_RefinementStation_Sawmill_Small" },
        "Grinder" => new[] { "TM_RefinementStation_Grinder_Small", "TM_RefinementStation_Grinder" },
        "Tannery" => new[] { "TM_RefinementStation_Tannery_Small" },
        "Loom" => new[] { "TM_RefinementStation_Loom_Small" },
        "BloodPress" => new[] { "TM_RefinementStation_BloodPress_Small" },
        "Fabricator" => new[] { "TM_RefinementStation_Fabricator" },
        _ => new[] { "TM_Workstation_SimpleCraftingbench" },
    };

    /// <summary>
    /// The 6 x 6 cell block for quadrant <paramref name="q"/>: the heart's 3 x 3 cells (offsets
    /// -1..1) sit in one corner and the block grows three cells further on both axes.
    /// </summary>
    public static void BlockRange(int q, out int dx0, out int dx1, out int dz0, out int dz1)
    {
        bool east = q == 0 || q == 3, north = q == 0 || q == 1;
        dx0 = east ? -1 : -4;
        dx1 = east ? 4 : 1;
        dz0 = north ? -1 : -4;
        dz1 = north ? 4 : 1;
    }

    public static bool IsHeartCell(int dx, int dz) => dx >= -1 && dx <= 1 && dz >= -1 && dz <= 1;
}

/// <summary>Turns a seed and the discovered territories into a <see cref="StressPlan"/>.</summary>
internal static class StressPlanner
{
    public const int MinPlotsPerOwner = 2;
    public const int MaxPlotsPerOwner = 5;
    public const int MinClanSize = 2;
    public const int MaxClanSize = 5;

    public static StressPlan Plan(StressArgs args, IReadOnlyList<StressTerritory> territories)
    {
        var plan = new StressPlan { Seed = args.Seed };
        var byIndex = new SortedDictionary<int, StressTerritory>();
        foreach (var t in territories)
            if (!byIndex.ContainsKey(t.Index)) byIndex[t.Index] = t;

        var candidates = new List<int>();
        if (args.AllFree) candidates.AddRange(byIndex.Keys);
        else candidates.AddRange(args.Plots);

        // Layout first, so a plot that cannot hold the block is refused before owners are dealt.
        var usable = new List<StressPlotPlan>();
        foreach (int index in candidates)
        {
            if (!byIndex.TryGetValue(index, out var t))
            {
                plan.Refusals.Add(new StressPlotRefusal(index, "no_such_plot", "no castle territory has this index"));
                continue;
            }
            if (t.Claimed)
            {
                plan.Refusals.Add(new StressPlotRefusal(index, "plot_taken", t.ClaimedBy is { Length: > 0 } by ? "claimed by " + by : "claimed"));
                continue;
            }
            if (string.Equals(t.Region, "None", StringComparison.OrdinalIgnoreCase)
                || string.Equals(t.Region, "Debug", StringComparison.OrdinalIgnoreCase))
            {
                plan.Refusals.Add(new StressPlotRefusal(index, "not_a_plot", "world region " + t.Region + " (not a buildable plot)"));
                continue;
            }
            if (!t.HasPivot)
            {
                plan.Refusals.Add(new StressPlotRefusal(index, "no_valid_pivot", "no heart pivot whose 30 x 30 tile footprint is all buildable"));
                continue;
            }
            var plot = Layout(args, t);
            if (plot == null)
            {
                plan.Refusals.Add(new StressPlotRefusal(index, "no_floor_fit", "no corner around the heart has a 6 x 6 cell block of buildable tiles"));
                continue;
            }
            usable.Add(plot);
        }

        int owners = Math.Min(args.Owners, usable.Count / MinPlotsPerOwner);
        if (owners < args.Owners)
            plan.Notes.Add($"owners reduced from {args.Owners} to {owners}: {usable.Count} usable plot(s), each owner needs {MinPlotsPerOwner}");
        for (int i = 1; i <= owners; i++)
            plan.Owners.Add(new StressOwnerPlan { Ordinal = i, Name = StressIdentity.OwnerName(i), PlatformId = StressIdentity.PlatformId(args.Seed, i) });

        DealPlots(args, plan, usable);
        DealClans(args, plan);
        plan.Plots.Sort((a, b) => a.Territory.CompareTo(b.Territory));
        plan.Refusals.Sort((a, b) => a.Territory.CompareTo(b.Territory));
        return plan;
    }

    private static void DealPlots(StressArgs args, StressPlan plan, List<StressPlotPlan> usable)
    {
        int n = plan.Owners.Count;
        if (n == 0)
        {
            foreach (var p in usable)
                plan.Refusals.Add(new StressPlotRefusal(p.Territory, "no_owner_capacity", "fewer than " + MinPlotsPerOwner + " usable plots, so no owner"));
            return;
        }

        var counts = new int[n];
        for (int i = 0; i < n; i++) counts[i] = MinPlotsPerOwner;
        int extra = usable.Count - (n * MinPlotsPerOwner);
        var rng = StressRng.For(args.Seed, "plots-per-owner");
        var open = new List<int>();
        for (int i = 0; i < n; i++) open.Add(i);
        while (extra > 0 && open.Count > 0)
        {
            int pick = rng.Range(0, open.Count - 1);
            int owner = open[pick];
            counts[owner]++;
            extra--;
            if (counts[owner] >= MaxPlotsPerOwner) open.RemoveAt(pick);
        }

        var order = new List<StressPlotPlan>(usable);
        StressRng.For(args.Seed, "plot-deal").Shuffle(order);
        int at = 0;
        for (int i = 0; i < n; i++)
        {
            for (int k = 0; k < counts[i]; k++)
            {
                var p = order[at++];
                p.Owner = i + 1;
                plan.Owners[i].Territories.Add(p.Territory);
                plan.Plots.Add(p);
            }
            plan.Owners[i].Territories.Sort();
        }
        for (; at < order.Count; at++)
            plan.Refusals.Add(new StressPlotRefusal(order[at].Territory, "no_owner_capacity", $"every owner already has {MaxPlotsPerOwner} plots"));
    }

    private static void DealClans(StressArgs args, StressPlan plan)
    {
        int n = plan.Owners.Count;
        if (n < MinClanSize || args.Clans == 0)
        {
            if (args.Clans > 0) plan.Notes.Add("no clans: fewer than " + MinClanSize + " owners");
            return;
        }
        int lo = (n + MaxClanSize - 1) / MaxClanSize, hi = n / MinClanSize;
        int clans = Math.Clamp(args.Clans, lo, hi);
        if (clans != args.Clans)
            plan.Notes.Add($"clans adjusted from {args.Clans} to {clans} so every clan has {MinClanSize}..{MaxClanSize} of {n} owners");

        var sizes = new int[clans];
        for (int i = 0; i < clans; i++) sizes[i] = MinClanSize;
        int extra = n - (clans * MinClanSize);
        var rng = StressRng.For(args.Seed, "clan-size");
        var open = new List<int>();
        for (int i = 0; i < clans; i++) open.Add(i);
        while (extra > 0 && open.Count > 0)
        {
            int pick = rng.Range(0, open.Count - 1);
            int c = open[pick];
            sizes[c]++;
            extra--;
            if (sizes[c] >= MaxClanSize) open.RemoveAt(pick);
        }

        var members = new List<int>();
        for (int i = 1; i <= n; i++) members.Add(i);
        StressRng.For(args.Seed, "clan-deal").Shuffle(members);
        int at = 0;
        for (int c = 0; c < clans; c++)
        {
            var clan = new StressClanPlan { Ordinal = c + 1, Name = StressIdentity.ClanName(c + 1) };
            for (int k = 0; k < sizes[c]; k++)
            {
                int owner = members[at++];
                clan.Members.Add(owner);
                plan.Owners[owner - 1].Clan = clan.Ordinal;
            }
            clan.Members.Sort();
            plan.Clans.Add(clan);
        }
    }

    /// <summary>
    /// The block, chests and stations for one territory, or null when no corner fits. Uses its
    /// own random stream keyed by seed and territory, so it is the same however owners are dealt.
    /// </summary>
    internal static StressPlotPlan? Layout(StressArgs args, StressTerritory t)
    {
        var rng = StressRng.For(args.Seed, "plot", t.Index);
        var quadrants = new List<int> { 0, 1, 2, 3 };
        rng.Shuffle(quadrants);
        int chosen = -1;
        foreach (int q in quadrants)
        {
            if (BlockFits(t, q)) { chosen = q; break; }
        }
        if (chosen < 0) return null;

        var plot = new StressPlotPlan { Territory = t.Index, PivotX = t.PivotX, PivotY = t.PivotY, Quadrant = chosen };
        plot.Fuel = rng.Range(100, 400);
        StressLayout.BlockRange(chosen, out int dx0, out int dx1, out int dz0, out int dz1);
        for (int dz = dz0; dz <= dz1; dz++)
            for (int dx = dx0; dx <= dx1; dx++)
                if (!StressLayout.IsHeartCell(dx, dz))
                    plot.Floors.Add(new StressCell(dx, dz, t.PivotX, t.PivotY));
        // Nearest the heart first: ring distance from the 3 x 3 heart area, so every floor
        // touches the heart or a floor placed before it.
        plot.Floors.Sort((a, b) =>
        {
            int c = Ring(a).CompareTo(Ring(b));
            if (c != 0) return c;
            c = a.Dz.CompareTo(b.Dz);
            return c != 0 ? c : a.Dx.CompareTo(b.Dx);
        });

        int stations = rng.Range(args.StationsMin, args.StationsMax);
        int chests = rng.Range(args.ChestsMin, args.ChestsMax);

        var cells = new List<StressCell>(plot.Floors);
        rng.Shuffle(cells);
        var kinds = new List<string>(StressLayout.Benches);
        var refiners = new List<string>(StressLayout.Refiners);
        rng.Shuffle(refiners);
        kinds.AddRange(refiners);
        int cellAt = 0;
        for (int i = 0; i < stations; i++)
        {
            string kind = kinds[i % kinds.Count];
            plot.Stations.Add(new StressStationPlan
            {
                Number = i + 1,
                Kind = kind,
                Prefabs = StressLayout.StationPrefabs(kind),
                Cell = cells[cellAt++],
            });
        }

        for (int i = 0; i < chests; i++)
        {
            var cell = cells[cellAt + (i / StressLayout.ChestsPerCell)];
            float side = (i % StressLayout.ChestsPerCell) == 0 ? -StressLayout.ChestOffset : StressLayout.ChestOffset;
            string kind = StressLayout.ChestKinds[rng.Range(0, StressLayout.ChestKinds.Count - 1)];
            var chest = new StressChestPlan
            {
                Number = i + 1,
                Kind = kind,
                Prefabs = StressLayout.ChestPrefabs(kind),
                Cell = cell,
                X = cell.X + side,
                Z = cell.Z,
            };
            bool nearlyFull = args.Fill > 0 && rng.NextDouble() < StressLayout.NearlyFullChance;
            chest.NearlyFull = nearlyFull;
            chest.SlotFraction = nearlyFull
                ? 0.9 + (rng.NextDouble() * 0.08)
                : Math.Round(Math.Clamp(args.Fill + ((rng.NextDouble() - 0.5) * 0.4), 0, 0.85), 3);
            if (args.Fill <= 0) chest.SlotFraction = 0;
            var pool = new List<string>(StressLayout.ChestItems(kind));
            rng.Shuffle(pool);
            int take = chest.SlotFraction <= 0 ? 0 : rng.Range(Math.Min(2, pool.Count), Math.Min(6, pool.Count));
            for (int k = 0; k < take; k++) chest.Items.Add(pool[k]);
            plot.Chests.Add(chest);
        }
        return plot;
    }

    private static int Ring(StressCell c) => Math.Max(Math.Max(Math.Abs(c.Dx), Math.Abs(c.Dz)) - 1, 0) * 100 + Math.Abs(c.Dx) + Math.Abs(c.Dz);

    private static bool BlockFits(StressTerritory t, int q)
    {
        StressLayout.BlockRange(q, out int dx0, out int dx1, out int dz0, out int dz1);
        int half = StressLayout.CellTiles / 2;
        for (int dz = dz0; dz <= dz1; dz++)
        {
            for (int dx = dx0; dx <= dx1; dx++)
            {
                if (StressLayout.IsHeartCell(dx, dz)) continue;
                int cx = t.PivotX + (dx * StressLayout.CellTiles), cy = t.PivotY + (dz * StressLayout.CellTiles);
                for (int y = cy - half; y < cy + half; y++)
                    for (int x = cx - half; x < cx + half; x++)
                        if (!t.Buildable(x, y)) return false;
            }
        }
        return true;
    }
}
