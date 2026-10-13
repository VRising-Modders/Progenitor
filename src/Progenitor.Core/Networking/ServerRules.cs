using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Progenitor.Networking;

/// <summary>Which settings file a server rule lives in.</summary>
internal enum ServerRuleFile
{
    /// <summary><c>ServerHostSettings.json</c> (and its command-line and environment overrides).</summary>
    Host = 0,

    /// <summary><c>ServerGameSettings.json</c> (and the presets).</summary>
    Game = 1,
}

/// <summary>How a server rule is edited.</summary>
internal enum ServerRuleControl
{
    Text = 0,
    Choice = 1,
    Number = 2,
    Switch = 3,

    /// <summary>Write-only: never read back, not even to admins.</summary>
    Password = 4,
}

/// <summary>One vanilla server setting Progenitor's page can change (settings organization stages 6 and 7).</summary>
internal sealed class ServerRuleSpec
{
    internal ServerRuleSpec(string key, ServerRuleFile file, ServerRuleControl control, string label, string help, bool live = false)
    {
        Key = key;
        File = file;
        Control = control;
        Label = label;
        Help = help;
        Live = live;
    }

    internal string Key { get; }
    internal ServerRuleFile File { get; }
    internal ServerRuleControl Control { get; }
    internal string Label { get; }
    internal string Help { get; }

    /// <summary>The running server takes the change at once (Server name, Password); every other rule applies at the next start.</summary>
    internal bool Live { get; }

    internal IReadOnlyList<string> Choices { get; init; } = Array.Empty<string>();
    internal IReadOnlyList<string> ChoiceLabels { get; init; } = Array.Empty<string>();

    /// <summary>
    /// OPTIONHOVER1: each choice's explanation, index-aligned with <see cref="Choices"/>; the details pane shows it while the
    /// pointer is over the option (the dropdown's list does not draw it).
    /// </summary>
    internal IReadOnlyList<string> ChoiceDescriptions { get; init; } = Array.Empty<string>();

    internal int Min { get; init; }
    internal int Max { get; init; }
    internal int MaxLength { get; init; } = 64;

    internal string Id => ServerRules.Id(Key);
}

/// <summary>
/// Settings organization stages 6 and 7 (docs/settings-organization.md, "Vanilla server settings (Server rules)"; research
/// note server-browser-filter-settings.md §5): the vanilla settings behind the <b>Join Game</b> browser's filters and
/// columns, their next-start values resolved in the game's own order, and the rules for changing them. Game-free.
/// </summary>
internal static class ServerRules
{
    internal const string Title = "Server rules · applied at the next restart";

    // Keys (the game's own).
    internal const string Name = "Name";
    internal const string GameModeType = "GameModeType";
    internal const string ClanSize = "ClanSize";
    internal const string GameDifficulty = "GameDifficulty";
    internal const string BloodBoundEquipment = "BloodBoundEquipment";
    internal const string CastleHeartDamageMode = "CastleHeartDamageMode";
    internal const string Password = "Password";
    internal const string MaxConnectedUsers = "MaxConnectedUsers";
    internal const string MaxConnectedAdmins = "MaxConnectedAdmins";
    internal const string ResetDaysInterval = "ResetDaysInterval";
    internal const string DayOfReset = "DayOfReset";
    internal const string GameSettingsPreset = "GameSettingsPreset";
    internal const string GameDifficultyPreset = "GameDifficultyPreset";
    internal const string SaveName = "SaveName";

    // Read-only lines.
    internal const string Merciless = "Merciless";
    internal const string Territory = "Territory";
    internal const string DaysRunning = "DaysRunning";
    internal const string StandardSettings = "StandardSettings";
    internal const string NextReset = "NextReset";

    internal const string Decaying = "CanBeDestroyedOnlyWhenDecaying";

    internal const int MaxSlots = 128;
    internal const int PasswordMax = 32;

    /// <summary>A password the game quits at start over (<c>GameBootstrap.Start</c>).</summary>
    internal const int PasswordQuitOver = 64;

    internal const int NameMax = 64;
    internal const int ClanMin = 1;
    internal const int ClanMax = 10;
    internal const int ResetDaysMax = 365;

    /// <summary>The setting id of a rule: <c>progenitor/ServerRules/&lt;Key&gt;</c>.</summary>
    internal static string Id(string key) => ServerSettingsLock.ServerRulesPrefix + key;

    private static readonly string[] Days = { "Any", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday" };

    // ---- OPTIONHOVER1: what each option does, for the details pane ----------------------------------------------------

    /// <summary>
    /// Game mode (<c>GameModeType</c>; the Join Game list's PvP / PvE column and its <c>pvp</c> / <c>pve</c> tag, research
    /// server-browser-filter-settings.md §2).
    /// </summary>
    internal static readonly string[] GameModeDescriptions =
    {
        "Players can fight each other and raid castles, within the server's PvP and castle siege settings.",
        "Players can't damage each other or each other's castles.",
    };

    /// <summary>
    /// Difficulty (<c>GameDifficulty</c> Relaxed / Normal / Hard): the game's own difficulty presets, read from the server's
    /// <c>VRisingServer_Data\StreamingAssets\GameDifficultyPresets\Difficulty_Easy.json</c>, <c>Difficulty_Normal.json</c> and
    /// <c>Difficulty_Brutal.json</c> (<see cref="DifficultyPresets"/>). "Power" is the game's <c>PowerModifier</c>.
    /// </summary>
    internal static readonly string[] DifficultyDescriptions =
    {
        "The game's Easy preset: enemies at 70% power; V Bloods at 80% health and 60% power; sun damage and blood drain at 75%.",
        "The game's Normal preset: enemies, V Bloods, sun damage, blood drain, loot and wear all at 100%.",
        "The game's Brutal preset: enemies at 140% power; V Bloods at 125% health and 170% power, 3 levels higher; 125% loot; gear wears half as fast.",
    };

    /// <summary>
    /// The modifiers of the game's bundled difficulty presets (<c>GameDifficultyPresets\Difficulty_*.json</c> under the
    /// server's StreamingAssets, the same three files in the client's), keyed by the <c>GameDifficulty</c> word each file sets.
    /// Values not named in a file keep the game's 1 (0 for the level increase).
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> DifficultyPresets =
        new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Relaxed"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["File"] = "Difficulty_Easy", ["SunDamageModifier"] = "0.75", ["BloodDrainModifier"] = "0.75",
                ["UnitStatModifiers_Global.MaxHealthModifier"] = "1", ["UnitStatModifiers_Global.PowerModifier"] = "0.7",
                ["UnitStatModifiers_VBlood.MaxHealthModifier"] = "0.8", ["UnitStatModifiers_VBlood.PowerModifier"] = "0.6",
                ["UnitStatModifiers_VBlood.LevelIncrease"] = "0", ["DropTableModifier_General"] = "1", ["DurabilityDrainModifier"] = "1",
            },
            ["Normal"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["File"] = "Difficulty_Normal", ["SunDamageModifier"] = "1", ["BloodDrainModifier"] = "1",
                ["UnitStatModifiers_Global.MaxHealthModifier"] = "1", ["UnitStatModifiers_Global.PowerModifier"] = "1",
                ["UnitStatModifiers_VBlood.MaxHealthModifier"] = "1", ["UnitStatModifiers_VBlood.PowerModifier"] = "1",
                ["UnitStatModifiers_VBlood.LevelIncrease"] = "0", ["DropTableModifier_General"] = "1", ["DurabilityDrainModifier"] = "1",
            },
            ["Hard"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["File"] = "Difficulty_Brutal", ["SunDamageModifier"] = "1", ["BloodDrainModifier"] = "1",
                ["UnitStatModifiers_Global.MaxHealthModifier"] = "1", ["UnitStatModifiers_Global.PowerModifier"] = "1.4",
                ["UnitStatModifiers_VBlood.MaxHealthModifier"] = "1.25", ["UnitStatModifiers_VBlood.PowerModifier"] = "1.7",
                ["UnitStatModifiers_VBlood.LevelIncrease"] = "3", ["DropTableModifier_General"] = "1.25", ["DurabilityDrainModifier"] = "0.5",
            },
        };

    /// <summary>
    /// Castle heart damage (<c>CastleHeartDamageMode</c>, the game's enum <c>CanBeDestroyedOnlyWhenDecaying</c> 0,
    /// <c>CanBeDestroyedByPlayers</c> 1, <c>CanBeSeizedOrDestroyedByPlayers</c> 2). Anything but the first lists the server as
    /// Merciless (research §5.1).
    /// </summary>
    internal static readonly string[] CastleHeartDescriptions =
    {
        "Raiders can't destroy a castle heart; a castle falls only after its heart runs out of blood essence and decays. Not Merciless.",
        "Raiders who break in can destroy the castle heart. Lists the server as Merciless.",
        "Raiders who break in can destroy the castle heart or seize the castle as their own. Lists the server as Merciless.",
    };

    /// <summary>
    /// Wipe on (<c>DayOfReset</c>): the game's <c>GetResetDateTime</c> moves the reset date (start date plus the interval) forward
    /// to the next such weekday, 0 to 6 days; <c>CheckIfResetServer</c> wipes at a start on or after it (research §5.7).
    /// </summary>
    internal static readonly string[] DayDescriptions = DayDescriptionsOf(Days);

    private static string[] DayDescriptionsOf(string[] days)
    {
        var result = new string[days.Length];
        for (int i = 0; i < days.Length; i++)
            result[i] = i == 0
                ? "A due wipe happens at the first server start once the interval has passed, on any day."
                : "A due wipe waits for the first " + days[i] + " once the interval has passed, and happens when the server starts on or after it.";
        return result;
    }

    internal static readonly IReadOnlyList<ServerRuleSpec> Specs = new[]
    {
        // Review fix 4: the listing reads the name from the discovery data the server built at start, which Progenitor does not
        // rewrite (a nested native struct), so a rename applies at the next restart like the other rules.
        new ServerRuleSpec(Name, ServerRuleFile.Host, ServerRuleControl.Text, "Server name",
            "The name in the Join Game list. At most 64 characters, one line. Applies at the next restart.") { MaxLength = NameMax },
        new ServerRuleSpec(GameModeType, ServerRuleFile.Game, ServerRuleControl.Choice, "Game mode",
            "PvP or PvE. Applies at the next restart; the world is kept.")
        { Choices = new[] { "PvP", "PvE" }, ChoiceLabels = new[] { "PvP", "PvE" }, ChoiceDescriptions = GameModeDescriptions },
        new ServerRuleSpec(ClanSize, ServerRuleFile.Game, ServerRuleControl.Number, "Clan size",
            "1 to 10. Existing clans larger than this keep their members; only new invites are blocked. Applies at the next restart.")
        { Min = ClanMin, Max = ClanMax },
        new ServerRuleSpec(GameDifficulty, ServerRuleFile.Game, ServerRuleControl.Choice, "Difficulty",
            "Listed as Easy, Normal or Hard, and enemy behavior written for a difficulty follows it. Each option names the game's "
            + "preset of that difficulty; this row sets the difficulty alone, not the preset's numbers (a difficulty preset in "
            + "ServerHostSettings sets both). Applies at the next restart.")
        {
            Choices = new[] { "Relaxed", "Normal", "Hard" }, ChoiceLabels = new[] { "Easy (Relaxed)", "Normal", "Hard" },
            ChoiceDescriptions = DifficultyDescriptions,
        },
        new ServerRuleSpec(BloodBoundEquipment, ServerRuleFile.Game, ServerRuleControl.Switch, "Blood-bound equipment",
            "Equipment stays with a player who dies. Off lists the server as Merciless. Applies at the next restart."),
        new ServerRuleSpec(CastleHeartDamageMode, ServerRuleFile.Game, ServerRuleControl.Choice, "Castle heart damage",
            "Anything but Only when decaying lists the server as Merciless. Applies at the next restart.")
        {
            Choices = new[] { Decaying, "CanBeDestroyedByPlayers", "CanBeSeizedOrDestroyedByPlayers" },
            ChoiceLabels = new[] { "Only when decaying", "Players can destroy it", "Players can seize or destroy it" },
            ChoiceDescriptions = CastleHeartDescriptions,
        },
        new ServerRuleSpec(Password, ServerRuleFile.Host, ServerRuleControl.Password, "Password",
            "At most 32 characters. Applies at once, to the next player who joins. Never shown, not even to admins. Leave the field "
            + "empty (or only spaces) and Apply to remove it.", live: true) { MaxLength = PasswordMax },
        new ServerRuleSpec(MaxConnectedUsers, ServerRuleFile.Host, ServerRuleControl.Number, "Max players",
            "Player slots. The admin slots (MaxConnectedAdmins) come on top, and both together are at most 128. Applies at the next restart.")
        { Min = 1, Max = MaxSlots },
        new ServerRuleSpec(ResetDaysInterval, ServerRuleFile.Host, ServerRuleControl.Number, "Scheduled wipes every (days)",
            "The game's own scheduled wipe: at startup, once the world is this many days old (then the next Wipe on day), the "
            + "server archives this world (the save is moved to <save>_<ticks>) and starts a new one. 0 = never. Enabling it, or "
            + "lowering it, on a world already older wipes at the NEXT restart; Progenitor asks first. Applies at the next restart.")
        { Min = 0, Max = ResetDaysMax },
        new ServerRuleSpec(DayOfReset, ServerRuleFile.Host, ServerRuleControl.Choice, "Wipe on",
            "The game's own scheduled wipe waits for this weekday once the world is old enough (Any: no wait), then happens at the "
            + "first server start on or after it. The server browser shows the Scheduled Wipes tag and the days until the wipe. "
            + "Applies at the next restart.")
        { Choices = Days, ChoiceLabels = Days, ChoiceDescriptions = DayDescriptions },
    };

    internal static ServerRuleSpec? Find(string? key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        foreach (var s in Specs)
            if (string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase)) return s;
        return null;
    }

    /// <summary>The game's code defaults of the game keys (<c>ServerGameSettings</c> field initializers, research §5.3).</summary>
    internal static readonly IReadOnlyDictionary<string, string> GameDefaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [GameModeType] = "PvP",
        [ClanSize] = "4",
        [GameDifficulty] = "Normal",
        [BloodBoundEquipment] = "true",
        [CastleHeartDamageMode] = "CanBeDestroyedByPlayers",
    };

    /// <summary>The host keys' defaults (<c>ServerHostSettings..ctor</c>).</summary>
    internal static readonly IReadOnlyDictionary<string, string> HostDefaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [Name] = "",
        [Password] = "",
        [MaxConnectedUsers] = "60",
        [MaxConnectedAdmins] = "4",
        [ResetDaysInterval] = "0",
        [DayOfReset] = "Any",
        [GameSettingsPreset] = "",
        [GameDifficultyPreset] = "",
    };

    /// <summary>The enum words of each enum key, in the game's numeric order (a file may hold the number).</summary>
    private static readonly IReadOnlyDictionary<string, string[]> EnumWords = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        [GameModeType] = new[] { "PvE", "PvP" },
        [GameDifficulty] = new[] { "Relaxed", "Normal", "Hard" },
        [CastleHeartDamageMode] = new[] { Decaying, "CanBeDestroyedByPlayers", "CanBeSeizedOrDestroyedByPlayers" },
    };

    /// <summary>
    /// A file value as the canonical text this model compares: an enum's word (from its word or number, any case), a
    /// bool's <c>true</c>/<c>false</c>, an integer, a string. Null when it is not a value the game would take.
    /// </summary>
    internal static string? Canonical(string key, JsonElement value)
    {
        if (string.Equals(key, DayOfReset, StringComparison.OrdinalIgnoreCase))
        {
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int day))
                return day < 0 ? "Any" : day <= 6 ? Days[day + 1] : null;
            if (value.ValueKind == JsonValueKind.String) return CanonicalWord(Days, value.GetString());
            return null;
        }

        if (EnumWords.TryGetValue(key, out var words))
        {
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int n)) return n >= 0 && n < words.Length ? words[n] : null;
            if (value.ValueKind == JsonValueKind.String) return CanonicalWord(words, value.GetString());
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Number => value.TryGetInt32(out int i) ? i.ToString(CultureInfo.InvariantCulture) : null,
            JsonValueKind.String => value.GetString() ?? "",
            _ => null,
        };
    }

    private static string? CanonicalWord(IReadOnlyList<string> words, string? text)
    {
        string t = (text ?? "").Trim();
        foreach (string w in words)
            if (string.Equals(w, t, StringComparison.OrdinalIgnoreCase)) return w;
        return null;
    }

    /// <summary>"No preset": empty, <c>default</c> or <c>none</c>, case-insensitive (research §5.3).</summary>
    internal static bool IsNoPreset(string? name)
    {
        string n = (name ?? "").Trim();
        return n.Length == 0 || string.Equals(n, "default", StringComparison.OrdinalIgnoreCase) || string.Equals(n, "none", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The <c>hc</c> tag's formula (research §5.1): blood-bound equipment off, or a castle heart that players can attack.</summary>
    internal static bool IsMerciless(string? bloodBound, string? heartMode) =>
        !string.Equals(bloodBound, "true", StringComparison.OrdinalIgnoreCase)
        || !string.Equals(heartMode, Decaying, StringComparison.OrdinalIgnoreCase);

    /// <summary>The Territory column's word for an activity level (research §5.6: LO = Abundant, MI = Moderate, HI = Limited).</summary>
    internal static string TerritoryWord(int level) => level switch
    {
        2 => "Limited",
        1 => "Moderate",
        _ => "Abundant",
    };

    /// <summary>The activity level the game's tags say: <c>sal_hi</c> 2, <c>sal_mi</c> 1, else 0.</summary>
    internal static int ActivityLevelOf(IEnumerable<string>? tags)
    {
        int level = 0;
        if (tags == null) return level;
        foreach (string t in tags)
        {
            if (string.Equals(t, "sal_hi", StringComparison.Ordinal)) return 2;
            if (string.Equals(t, "sal_mi", StringComparison.Ordinal)) level = 1;
        }

        return level;
    }

    /// <summary>The admin slots the game reserves: <c>MaxConnectedAdmins</c> clamped to 0..128.</summary>
    internal static int AdminReserve(int admins) => admins < 0 ? 0 : admins > MaxSlots ? MaxSlots : admins;

    /// <summary>The most player slots the game enforces: 128 less the admin reserve (at least 1 for the row's range).</summary>
    internal static int MaxPlayersMax(int admins) => Math.Max(1, MaxSlots - AdminReserve(admins));

    /// <summary>The player slots the game enforces for a file value (<c>CreateSaveConfiguration</c>: clamp 0..128 − admins).</summary>
    internal static int EnforcedPlayers(int users, int admins)
    {
        int max = MaxSlots - AdminReserve(admins);
        return users < 0 ? 0 : users > max ? max : users;
    }

    /// <summary>The start date in <c>StartDate.json</c> (the game writes it with the invariant culture), or null.</summary>
    internal static DateTime? ParseStartDate(string? text)
    {
        string t = (text ?? "").Trim().Trim('"');
        if (t.Length == 0) return null;
        return DateTime.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var d) ? d.Date : null;
    }

    /// <summary>
    /// The scheduled reset date (<c>GetResetDateTime</c>, research §5.7): the start date plus the interval in days, moved
    /// forward to the next <paramref name="dayOfReset"/> (0 to 6 days; <c>Any</c>: no move). Null when wipes are off.
    /// </summary>
    internal static DateTime? ResetDate(DateTime start, int intervalDays, string? dayOfReset)
    {
        if (intervalDays <= 0) return null;
        var date = start.Date.AddDays(intervalDays);
        int index = Array.FindIndex(Days, d => string.Equals(d, dayOfReset, StringComparison.OrdinalIgnoreCase));
        if (index <= 0) return date;
        // ResetDayEnum: Monday 0 … Sunday 6; System.DayOfWeek: Sunday 0 … Saturday 6.
        var want = (DayOfWeek)(index % 7);
        int add = ((int)want - (int)date.DayOfWeek + 7) % 7;
        return date.AddDays(add);
    }

    internal static string FormatDate(DateTime d) => d.ToString("ddd, MMM d, yyyy", CultureInfo.GetCultureInfo("en-US"));

    /// <summary>
    /// Review fix (scheduled wipes): whether a change of the wipe interval or day needs the admin's confirmation, and the
    /// reset date it leads to. It does when wipes turn on, when the next reset date would be today or earlier (the next
    /// start resets the world: the game resets once the local time has reached it), or when the world's start date is not
    /// known (nothing rules that out). <paramref name="past"/>: the date is already reached.
    /// </summary>
    internal static bool WipeNeedsConfirm(DateTime? start, int intervalBefore, int intervalAfter, string? dayAfter, DateTime now,
        out DateTime? reset, out bool past)
    {
        reset = null;
        past = false;
        if (intervalAfter <= 0) return false;
        if (start is { } s) reset = ResetDate(s, intervalAfter, dayAfter);
        past = reset is { } r && r <= now;
        return intervalBefore <= 0 || past || start == null;
    }
}

/// <summary>What the game-settings resolver reads. Every member is optional.</summary>
internal sealed class GameSettingsInputs
{
    /// <summary>The install folder that holds <c>VRisingServer_Data</c>.</summary>
    public string? InstallRoot { get; set; }

    public string? PersistentDataPath { get; set; }

    /// <summary>The save folder the game uses (<c>ServerRuntimeSettings.SaveDirectoryPath</c>).</summary>
    public string? SaveFolder { get; set; }

    /// <summary>The effective <c>GameSettingsPreset</c> (files, environment and command line applied).</summary>
    public string? Preset { get; set; }

    /// <summary>The effective <c>GameDifficultyPreset</c>.</summary>
    public string? DifficultyPreset { get; set; }
}

/// <summary>
/// The next start's game settings in the game's order (research §5.3; <c>ServerGameSettingsSystem.OnCreate</c>): code
/// defaults &lt; <c>StreamingAssets</c> &lt; <c>persistentDataPath\Settings</c> &lt; the save folder; a
/// <c>GameSettingsPreset</c> replaces every file layer (code defaults plus the preset's file); a
/// <c>GameDifficultyPreset</c> is laid over the result; then the clamps. Partial files layer key by key. Read-only.
/// </summary>
internal sealed class GameSettingsResolution
{
    internal const string FileName = "ServerGameSettings.json";
    internal const string BareName = "ServerGameSettings";

    internal GameSettingsResolution(GameSettingsInputs inputs)
    {
        inputs ??= new GameSettingsInputs();
        string install = (inputs.InstallRoot ?? "").Trim();
        string assets = install.Length == 0 ? "" : Combine(install, "VRisingServer_Data", "StreamingAssets");
        string persistent = (inputs.PersistentDataPath ?? "").Trim();
        string save = (inputs.SaveFolder ?? "").Trim();
        Default = assets.Length == 0
            ? new ServerHostSettingsLayer(ServerHostSettingsLayerKind.Default, "", exists: false)
            : ServerHostSettingsFiles.ReadLayer(ServerHostSettingsLayerKind.Default, Combine(assets, "Settings", FileName));
        Local = persistent.Length == 0
            ? new ServerHostSettingsLayer(ServerHostSettingsLayerKind.Local, "", exists: false)
            : ServerHostSettingsFiles.ReadLayer(ServerHostSettingsLayerKind.Local, Combine(persistent, "Settings", FileName));
        if (save.Length == 0) Save = new ServerHostSettingsLayer(ServerHostSettingsLayerKind.Save, "", exists: false);
        else
        {
            string bare = Combine(save, BareName);
            Save = Exists(bare)
                ? ServerHostSettingsFiles.ReadLayer(ServerHostSettingsLayerKind.Save, bare)
                : ServerHostSettingsFiles.ReadLayer(ServerHostSettingsLayerKind.Save, Combine(save, FileName));
        }

        PresetName = (inputs.Preset ?? "").Trim();
        // Review fix 9 (research §5.3): a preset applies only when StreamingAssets\GameSettingPresets exists. With the install
        // unknown, it is taken to apply (the rows stay read-only) but is never called missing.
        _assetsKnown = assets.Length > 0;
        _presetDirExists = _assetsKnown && DirExists(Combine(assets, "GameSettingPresets"));
        if (!ServerRules.IsNoPreset(PresetName) && _presetDirExists)
        {
            string dir = Combine(assets, "GameSettingPresets");
            string plain = Combine(dir, PresetName);
            string path = Exists(plain) ? plain : Combine(dir, PresetName + ".json");
            Preset = ServerHostSettingsFiles.ReadLayer(ServerHostSettingsLayerKind.Default, path);
        }

        DifficultyName = (inputs.DifficultyPreset ?? "").Trim();
        if (!ServerRules.IsNoPreset(DifficultyName) && assets.Length > 0)
            Difficulty = ServerHostSettingsFiles.ReadLayer(ServerHostSettingsLayerKind.Default, Combine(assets, "GameDifficultyPresets", DifficultyName + ".json"));
    }

    public ServerHostSettingsLayer Default { get; }
    public ServerHostSettingsLayer Local { get; }
    public ServerHostSettingsLayer Save { get; }

    public string PresetName { get; }

    /// <summary>The preset's file, when a preset is named (it may not exist).</summary>
    public ServerHostSettingsLayer? Preset { get; }

    public string DifficultyName { get; }

    public ServerHostSettingsLayer? Difficulty { get; }

    private readonly bool _assetsKnown;
    private readonly bool _presetDirExists;

    /// <summary>
    /// A preset is named and applies: <c>GameSettingPresets\</c> exists (or the install is unknown, so it may): every game row
    /// is set by it at start (or by code defaults when its file is missing).
    /// </summary>
    public bool PresetActive => !ServerRules.IsNoPreset(PresetName) && (!_assetsKnown || _presetDirExists);

    /// <summary>A preset applies whose file does not exist (the install is known): the server silently runs code defaults.</summary>
    public bool PresetMissing => PresetActive && _assetsKnown && (Preset == null || !Preset.Exists);

    /// <summary>The difficulty preset's file sets <paramref name="key"/>: that row is the preset's.</summary>
    public bool DifficultySets(string key) => Difficulty is { Exists: true } d && d.Has(key);

    /// <summary>
    /// The file a change is written to: the save folder's when it exists, else the persistent-data copy (created when
    /// missing). Never <c>StreamingAssets</c>. Null when neither path is known.
    /// </summary>
    public ServerHostSettingsLayer? WriteTarget => Save.Exists && Save.Path.Length > 0 ? Save : Local.Path.Length > 0 ? Local : null;

    /// <summary>The next start's value of <paramref name="key"/> and where it comes from (<c>difficulty-preset</c>, <c>preset</c>, <c>save</c>, <c>local</c>, <c>default</c>, <c>code</c>).</summary>
    /// <summary>The next start's value of <paramref name="key"/> before the clamps (review fix 6).</summary>
    public string? NextRaw(string key) => NextUnclamped(key).Value;

    public (string? Value, string Source) Next(string key)
    {
        var (value, source) = NextUnclamped(key);
        return (value == null ? null : Clamp(key, value), source);
    }

    private (string? Value, string Source) NextUnclamped(string key)
    {
        if (Difficulty is { Exists: true } diff && diff.TryGet(key, out var dv) && ServerRules.Canonical(key, dv) is { } dvalue)
            return (dvalue, "difficulty-preset");
        if (PresetActive)
        {
            if (Preset is { Exists: true } preset && preset.TryGet(key, out var pv) && ServerRules.Canonical(key, pv) is { } pvalue)
                return (pvalue, "preset");
            return (Code(key), "code");
        }

        foreach (var layer in new[] { Save, Local, Default })
            if (layer.TryGet(key, out var v) && ServerRules.Canonical(key, v) is { } value)
                return (value, layer.Kind.ToString().ToLowerInvariant());
        return (Code(key), "code");
    }

    private static string? Code(string key) => ServerRules.GameDefaults.TryGetValue(key, out var v) ? v : null;

    /// <summary><c>ToStruct</c>'s clamps the rows see: <c>ClanSize</c> 1..50.</summary>
    private static string Clamp(string key, string value)
    {
        if (string.Equals(key, ServerRules.ClanSize, StringComparison.OrdinalIgnoreCase)
            && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
            return Math.Max(1, Math.Min(50, n)).ToString(CultureInfo.InvariantCulture);
        return value;
    }

    public string Describe()
    {
        var parts = new List<string>();
        foreach (var l in new[] { Default, Local, Save })
            parts.Add(l.Kind.ToString().ToLowerInvariant() + " " + (l.Path.Length == 0 ? "(no path)" : l.Path) + " (" + (l.Path.Length == 0 ? "unknown" : !l.Exists ? "missing" : l.Readable ? "read" : "unreadable: " + l.Error) + ")");
        if (PresetActive) parts.Add("preset " + PresetName + (PresetMissing ? " (missing)" : ""));
        if (!ServerRules.IsNoPreset(DifficultyName)) parts.Add("difficulty preset " + DifficultyName + (Difficulty is { Exists: true } ? "" : " (missing)"));
        return string.Join("; ", parts);
    }

    private static string Combine(params string[] parts)
    {
        try { return Path.Combine(parts); }
        catch { return ""; }
    }

    private static bool DirExists(string path)
    {
        try { return path.Length > 0 && Directory.Exists(path); }
        catch { return false; }
    }

    private static bool Exists(string path)
    {
        try { return path.Length > 0 && File.Exists(path); }
        catch { return false; }
    }
}

/// <summary>What the server knows to build the rules' state (live: the running game and its files; tests: their own).</summary>
internal sealed class ServerRulesFacts
{
    public ServerHostSettingsResolution? Host { get; set; }
    public GameSettingsResolution? Game { get; set; }

    /// <summary>The running game's value of a key (canonical text), or null when unknown.</summary>
    public Func<string, string?>? Loaded { get; set; }

    /// <summary>The running server has a password now.</summary>
    public bool? PasswordSetNow { get; set; }

    /// <summary>The game's current tags (<c>_GameTags</c>), or null.</summary>
    public IReadOnlyList<string>? Tags { get; set; }

    /// <summary>The text of <c>&lt;save&gt;/StartDate.json</c>, or null.</summary>
    public string? StartDateText { get; set; }

    /// <summary>WIPEPOLISH1: the save's name (the next start's SaveName), for the wipe confirmation's archive folder.</summary>
    public string? SaveName { get; set; }

    public DateTime NowLocal { get; set; } = DateTime.Now;

    /// <summary>The lock list entry that locks a setting id (<see cref="ServerSettingsLock.LockedBy"/>), or null.</summary>
    public Func<string, string?>? LockedBy { get; set; }
}

/// <summary>One rule as the page draws it.</summary>
internal sealed class ServerRuleRow
{
    public string Key { get; set; } = "";
    public string? Loaded { get; set; }
    public string? Next { get; set; }
    public bool Pending { get; set; }

    /// <summary>The command-line argument or environment variable that sets it at every start, or empty.</summary>
    public string Override { get; set; } = "";

    public bool Editable { get; set; }

    /// <summary>Why it is read-only, or a warning on an editable row (an override); empty otherwise.</summary>
    public string Reason { get; set; } = "";

    public string LockedBy { get; set; } = "";

    /// <summary>The layer the next value comes from (<c>save</c>, <c>local</c>, <c>preset</c>, …).</summary>
    public string Source { get; set; } = "";

    /// <summary>
    /// Review fix 6 (server only, never on the wire): the next start's value before the game's clamps, so a file value above a
    /// clamp (max players over 128 less the admins) can be written down to it.
    /// </summary>
    public string? Raw { get; set; }
}

/// <summary>The whole Server rules state the server answers with.</summary>
internal sealed class ServerRulesState
{
    public List<ServerRuleRow> Rows { get; } = new();
    public bool PasswordSet { get; set; }
    public bool PasswordNextSet { get; set; }
    public int RestartPending { get; set; }
    public bool MercilessNow { get; set; }
    public bool MercilessNext { get; set; }
    public string Territory { get; set; } = "";
    public int? DaysRunning { get; set; }
    public string StartDate { get; set; } = "";

    /// <summary>WIPEPOLISH1: the next start's SaveName (empty when unknown).</summary>
    public string SaveName { get; set; } = "";
    public bool? StandardNow { get; set; }
    public bool StandardMayChange { get; set; }
    public string NextReset { get; set; } = "";
    public string PresetWarning { get; set; } = "";
    public int AdminReserve { get; set; }
    public int MaxPlayersMax { get; set; } = ServerRules.MaxSlots;

    public ServerRuleRow? Row(string key) => Rows.Find(r => string.Equals(r.Key, key, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Builds the state from the facts (game-free).</summary>
internal static class ServerRulesStateBuilder
{
    internal const string NoFileReason = "This server's settings file is not known, so Progenitor cannot change it.";

    internal static ServerRulesState Build(ServerRulesFacts facts)
    {
        facts ??= new ServerRulesFacts();
        var state = new ServerRulesState();
        var host = facts.Host;
        var game = facts.Game;
        int admins = Int(HostNext(host, ServerRules.MaxConnectedAdmins)) ?? 4;
        state.AdminReserve = ServerRules.AdminReserve(admins);
        state.MaxPlayersMax = ServerRules.MaxPlayersMax(admins);
        if (game?.PresetMissing == true)
            state.PresetWarning = "The preset " + game.PresetName + " named in ServerHostSettings does not exist: this server runs the game's built-in "
                + "defaults, not its settings files.";

        foreach (var spec in ServerRules.Specs)
        {
            var row = new ServerRuleRow { Key = spec.Key, LockedBy = SafeLock(facts, spec.Id) ?? "" };
            string? loaded = SafeLoaded(facts, spec.Key);
            if (spec.File == ServerRuleFile.Host)
            {
                string? next = HostNext(host, spec.Key);
                // Live 2026-10-06: what the files say, before overrides and clamps: a write is compared with this (the value
                // it replaces), never with an override's value (-serverName) or the clamped one.
                row.Raw = HostFileValue(host, spec.Key);
                if (spec.Key == ServerRules.MaxConnectedUsers)
                {
                    if (Int(next) is int users) next = ServerRules.EnforcedPlayers(users, admins).ToString(CultureInfo.InvariantCulture);
                }

                row.Next = next;
                row.Loaded = loaded;
                row.Source = host?.FileLayerOf(spec.Key)?.Kind.ToString().ToLowerInvariant() ?? "code";
                if (host?.OverrideOf(spec.Key) is { } o)
                {
                    row.Override = o.Source;
                    row.Reason = (o.FromCommandLine ? "The start command sets " + o.Source : "The environment variable " + o.Source + " is set")
                        + ": it replaces this value at every start. Remove it to keep the file in charge.";
                }

                row.Editable = host?.WriteTarget != null;
                if (!row.Editable) row.Reason = NoFileReason;
            }
            else
            {
                var (next, source) = game?.Next(spec.Key) ?? (ServerRules.GameDefaults.TryGetValue(spec.Key, out var d) ? d : null, "code");
                row.Next = next;
                row.Raw = game?.NextRaw(spec.Key) ?? next;
                row.Loaded = loaded;
                row.Source = source;
                row.Editable = game?.WriteTarget != null;
                if (game == null || game.WriteTarget == null) row.Reason = NoFileReason;
                else if (game.PresetActive)
                {
                    row.Editable = false;
                    row.Reason = game.PresetMissing
                        ? state.PresetWarning
                        : "This server uses the " + game.PresetName + " preset, which replaces these settings at start.";
                }
                else if (game.DifficultySets(spec.Key))
                {
                    row.Editable = false;
                    row.Reason = "Set by the difficulty preset " + game.DifficultyName + " in ServerHostSettings.";
                }
            }

            if (spec.Control == ServerRuleControl.Password)
            {
                state.PasswordNextSet = !string.IsNullOrWhiteSpace(row.Next);
                state.PasswordSet = facts.PasswordSetNow ?? state.PasswordNextSet;
                row.Next = state.PasswordNextSet ? "set" : "not set";
                row.Loaded = state.PasswordSet ? "set" : "not set";
            }

            // A live rule never waits for a restart; a restart rule is pending while the next start reads another value.
            row.Pending = !spec.Live && row.Loaded != null && row.Next != null && !string.Equals(row.Loaded, row.Next, StringComparison.OrdinalIgnoreCase);
            if (row.LockedBy.Length > 0) row.Editable = false;
            if (row.Pending) state.RestartPending++;
            state.Rows.Add(row);
        }

        // Merciless, from the loaded and the next values.
        state.MercilessNow = ServerRules.IsMerciless(LoadedOr(state, ServerRules.BloodBoundEquipment), LoadedOr(state, ServerRules.CastleHeartDamageMode));
        state.MercilessNext = ServerRules.IsMerciless(state.Row(ServerRules.BloodBoundEquipment)?.Next, state.Row(ServerRules.CastleHeartDamageMode)?.Next);
        state.Territory = facts.Tags == null ? "" : ServerRules.TerritoryWord(ServerRules.ActivityLevelOf(facts.Tags));
        if (facts.Tags != null)
        {
            bool std = false;
            foreach (string t in facts.Tags) std |= string.Equals(t, "std", StringComparison.Ordinal);
            state.StandardNow = std;
        }

        // Changing any game row but the clan size may change Standard settings (research §5.8 item 7).
        foreach (var r in state.Rows)
            if (r.Pending && ServerRules.Find(r.Key)?.File == ServerRuleFile.Game && r.Key != ServerRules.ClanSize) state.StandardMayChange = true;

        state.SaveName = (facts.SaveName ?? "").Trim();
        if (ServerRules.ParseStartDate(facts.StartDateText) is { } start)
        {
            state.StartDate = start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            state.DaysRunning = Math.Max(0, (int)(facts.NowLocal.Date - start).TotalDays);
            int interval = Int(state.Row(ServerRules.ResetDaysInterval)?.Next) ?? 0;
            if (ServerRules.ResetDate(start, interval, state.Row(ServerRules.DayOfReset)?.Next) is { } reset)
                state.NextReset = reset.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        return state;
    }

    private static string? LoadedOr(ServerRulesState state, string key)
    {
        var row = state.Row(key);
        return row?.Loaded ?? row?.Next;
    }

    /// <summary>The next start's value of a host key: an override, else the files', else the default.</summary>
    /// <summary>A host key's value in the files alone (the highest layer that sets it, else the default): no overrides, no clamp.</summary>
    internal static string? HostFileValue(ServerHostSettingsResolution? host, string key)
    {
        if (host != null)
            for (int i = host.Layers.Count - 1; i >= 0; i--)
                if (host.Layers[i].TryGet(key, out var v) && ServerRules.Canonical(key, v) is { } value) return value;
        return ServerRules.HostDefaults.TryGetValue(key, out var d) ? d : null;
    }

    internal static string? HostNext(ServerHostSettingsResolution? host, string key)
    {
        if (host == null) return ServerRules.HostDefaults.TryGetValue(key, out var d0) ? d0 : null;
        if (host.OverrideOf(key) is { } o)
        {
            if (key == ServerRules.DayOfReset) return CanonicalText(key, o.Value);
            return o.Value;
        }

        for (int i = host.Layers.Count - 1; i >= 0; i--)
            if (host.Layers[i].TryGet(key, out var v) && ServerRules.Canonical(key, v) is { } value) return value;
        return ServerRules.HostDefaults.TryGetValue(key, out var d) ? d : null;
    }

    private static string? CanonicalText(string key, string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(int.TryParse(text, out _) ? text : ServerListDescription.JsonString(text));
            return ServerRules.Canonical(key, doc.RootElement);
        }
        catch { return null; }
    }

    internal static int? Int(string? text) =>
        int.TryParse((text ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : null;

    private static string? SafeLoaded(ServerRulesFacts facts, string key)
    {
        try { return facts.Loaded?.Invoke(key); }
        catch { return null; }
    }

    private static string? SafeLock(ServerRulesFacts facts, string id)
    {
        try { return facts.LockedBy?.Invoke(id); }
        catch { return null; }
    }
}

/// <summary>
/// A JSON settings file edited in place: one top-level key's value replaced (or added after the opening brace), every
/// other byte kept (keys, order, spacing, comments, a byte-order mark). The writer of SERVERDESC1, generalized.
/// </summary>
internal static class JsonFileEdit
{
    /// <summary><paramref name="json"/> with top-level <paramref name="key"/> set to the JSON literal <paramref name="literal"/>; null when not a JSON object.</summary>
    internal static byte[]? WithValue(byte[] json, string key, string literal)
    {
        if (json == null) return null;
        int start = json.Length >= 3 && json[0] == 0xEF && json[1] == 0xBB && json[2] == 0xBF ? 3 : 0;
        try
        {
            var reader = new Utf8JsonReader(new ReadOnlySpan<byte>(json, start, json.Length - start),
                new JsonReaderOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return null;
            int open = start + (int)reader.TokenStartIndex;
            long valueStart = -1, valueEnd = -1;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == 0) break;
                if (reader.TokenType == JsonTokenType.PropertyName && reader.CurrentDepth == 1)
                {
                    bool match = string.Equals(reader.GetString(), key, StringComparison.OrdinalIgnoreCase);
                    if (!reader.Read()) return null;
                    // Review fix 10: a duplicated key's last occurrence is the one the game reads (Populate): edit that one.
                    if (match)
                    {
                        valueStart = reader.TokenStartIndex;
                        if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray) reader.Skip();
                        valueEnd = reader.BytesConsumed;
                    }
                    else if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                    {
                        reader.Skip();
                    }
                }
            }

            byte[] insert;
            int cutFrom, cutTo;
            if (valueStart >= 0)
            {
                insert = Encoding.UTF8.GetBytes(literal);
                cutFrom = start + (int)valueStart;
                cutTo = start + (int)valueEnd;
            }
            else
            {
                bool empty = IsEmptyObject(json, open + 1);
                insert = Encoding.UTF8.GetBytes("\n  " + ServerListDescription.JsonString(key) + ": " + literal + (empty ? "\n" : ","));
                cutFrom = cutTo = open + 1;
                if (empty)
                {
                    int close = cutTo;
                    while (close < json.Length && json[close] != (byte)'}') close++;
                    cutTo = close;
                }
            }

            var result = new byte[cutFrom + insert.Length + (json.Length - cutTo)];
            Buffer.BlockCopy(json, 0, result, 0, cutFrom);
            Buffer.BlockCopy(insert, 0, result, cutFrom, insert.Length);
            Buffer.BlockCopy(json, cutTo, result, cutFrom + insert.Length, json.Length - cutTo);
            return result;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsEmptyObject(byte[] json, int from)
    {
        for (int i = from; i < json.Length; i++)
        {
            byte b = json[i];
            if (b == (byte)'}') return true;
            if (b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') continue;
            return false;
        }

        return false;
    }

    /// <summary>
    /// Write <paramref name="literal"/> as <paramref name="key"/> into <paramref name="path"/> when it differs (an
    /// unchanged file is not rewritten). A missing file is created with only that key. Temp file, then replace.
    /// </summary>
    internal static PersistOutcome Persist(string path, string key, string literal)
    {
        if (string.IsNullOrWhiteSpace(path)) return new PersistOutcome(PersistResult.NoTarget, "no settings file path is known");
        try
        {
            byte[]? before = File.Exists(path) ? File.ReadAllBytes(path) : null;
            byte[]? after;
            if (before == null) after = Encoding.UTF8.GetBytes("{\n  " + ServerListDescription.JsonString(key) + ": " + literal + "\n}\n");
            else
            {
                if (CurrentLiteral(before, key) is { } current && string.Equals(current, literal, StringComparison.Ordinal))
                    return new PersistOutcome(PersistResult.Unchanged, "");
                after = WithValue(before, key, literal);
                if (after == null) return new PersistOutcome(PersistResult.Failed, "the file is not a JSON object; left as it is");
            }

            string dir = Path.GetDirectoryName(path) ?? "";
            if (dir.Length > 0) Directory.CreateDirectory(dir);
            string temp = path + ".progenitor-tmp";
            File.WriteAllBytes(temp, after);
            File.Move(temp, path, overwrite: true);
            return new PersistOutcome(before == null ? PersistResult.Created : PersistResult.Written, "");
        }
        catch (Exception e)
        {
            return new PersistOutcome(PersistResult.Failed, e.Message);
        }
    }

    /// <summary>The raw JSON text of top-level <paramref name="key"/>, or null.</summary>
    internal static string? CurrentLiteral(byte[] json, string key)
    {
        int start = json.Length >= 3 && json[0] == 0xEF && json[1] == 0xBB && json[2] == 0xBF ? 3 : 0;
        try
        {
            using var doc = JsonDocument.Parse(new ReadOnlyMemory<byte>(json, start, json.Length - start),
                new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            string? last = null;
            foreach (var p in doc.RootElement.EnumerateObject())
                if (string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase)) last = p.Value.GetRawText();
            return last;
        }
        catch (JsonException) { return null; }
    }
}
