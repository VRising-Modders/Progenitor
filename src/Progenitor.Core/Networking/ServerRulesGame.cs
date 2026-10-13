#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Progenitor.Networking;

/// <summary>
/// Settings organization stages 6 and 7, server: the running game and its files as the Server rules read them. Loaded
/// game values come from <c>ServerGameSettingsSystem.Settings</c> (what EOS reads and <c>std</c> hashes; research §5.3), never
/// <c>SettingsManager.ServerGameSettings</c>, which misses the save folder and both presets. Loaded host values come from
/// <c>SettingsManager.ServerHostSettings</c>. Read-only here; main thread.
/// </summary>
internal static class ServerRulesGame
{
    private static readonly string[] DayWords = { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday" };

    internal static ServerRulesFacts ReadFacts()
    {
        var host = ResolveHost(out var saveFolderFor);
        string saveName = (host.EffectiveString(ServerRules.SaveName) ?? host.SaveName).Trim();
        string saveFolder = host.SaveFolder;
        if (saveName.Length > 0 && !string.Equals(saveName, host.SaveName, StringComparison.Ordinal))
        {
            try { saveFolder = saveFolderFor(saveName) ?? saveFolder; }
            catch { /* keep the files' folder */ }
        }

        string? install = null;
        try { install = BepInEx.Paths.GameRootPath; }
        catch { /* unknown */ }
        var game = new GameSettingsResolution(new GameSettingsInputs
        {
            InstallRoot = install,
            PersistentDataPath = host.PersistentDataPath,
            SaveFolder = saveFolder,
            Preset = host.EffectiveString(ServerRules.GameSettingsPreset),
            DifficultyPreset = host.EffectiveString(ServerRules.GameDifficultyPreset),
        });

        string? start = null;
        try
        {
            string path = saveFolder.Length == 0 ? "" : Path.Combine(saveFolder, "StartDate.json");
            if (path.Length > 0 && File.Exists(path)) start = File.ReadAllText(path);
        }
        catch { start = null; }

        return new ServerRulesFacts
        {
            Host = host,
            Game = game,
            Loaded = Loaded,
            PasswordSetNow = PasswordSetNow(),
            Tags = Tags(),
            StartDateText = start,
            SaveName = saveName,
            NowLocal = DateTime.Now,
            LockedBy = ServerSettingsLock.LockedBy,
        };
    }

    /// <summary>The host settings files with the game's own paths (as SERVERDESC1 resolves them).</summary>
    internal static ServerHostSettingsResolution ResolveHost(out Func<string, string?> saveFolderFor)
    {
        string[] args;
        try { args = Environment.GetCommandLineArgs(); }
        catch { args = Array.Empty<string>(); }
        string? install = null;
        try { install = BepInEx.Paths.GameRootPath; }
        catch { /* unknown */ }
        string? local = null;
        try
        {
            string raw = ProjectM.ServerHostSettings.LocalPath ?? "";
            if (raw.Length > 0 && Path.IsPathRooted(raw)) local = raw.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? raw : raw + ".json";
        }
        catch { local = null; }

        ulong owner = 0;
        if (ServerHostSettingsFiles.HasArg(args, "userSave") && ServerHostSettingsFiles.TryArg(args, "owner", out string ownerText))
            ulong.TryParse(ownerText, NumberStyles.Integer, CultureInfo.InvariantCulture, out owner);
        Func<string, string?> folderFor = name =>
        {
            try { return ProjectM.ServerRuntimeSettings.GetCombinedFullSavePath(name, owner); }
            catch { return null; }
        };
        saveFolderFor = folderFor;
        return ServerHostSettingsFiles.Resolve(new ServerHostSettingsInputs
        {
            Args = args,
            InstallRoot = install,
            LocalSettingsFile = local,
            SaveFolderFor = folderFor,
        });
    }

    private static ProjectM.ServerHostSettings? HostSettings()
    {
        try { return ProjectM.SettingsManager._ServerHostSettings; }
        catch { return null; }
    }

    private static ProjectM.ServerGameSettings? GameSettings()
    {
        try
        {
            var world = Worlds.Server;
            if (world == null || !world.IsCreated) return null;
            return world.GetExistingSystemManaged<ProjectM.ServerGameSettingsSystem>()?.Settings;
        }
        catch { return null; }
    }

    private static bool? PasswordSetNow()
    {
        var host = HostSettings();
        if (host == null) return null;
        try { return !string.IsNullOrWhiteSpace(host.Password); }
        catch { return null; }
    }

    /// <summary>The running game's value of a key (canonical text), or null when unknown.</summary>
    internal static string? Loaded(string key)
    {
        try
        {
            switch (key)
            {
                case ServerRules.Name: return HostSettings()?.Name;
                case ServerRules.MaxConnectedUsers:
                {
                    var h = HostSettings();
                    return h == null ? null : ServerRules.EnforcedPlayers(h.MaxConnectedUsers, h.MaxConnectedAdmins).ToString(CultureInfo.InvariantCulture);
                }
                case ServerRules.ResetDaysInterval: return HostSettings()?.ResetDaysInterval.ToString(CultureInfo.InvariantCulture);
                case ServerRules.DayOfReset:
                {
                    var h = HostSettings();
                    if (h == null) return null;
                    int day = (int)h.DayOfReset;
                    return day >= 0 && day < DayWords.Length ? DayWords[day] : "Any";
                }
                case ServerRules.GameModeType:
                {
                    var g = GameSettings();
                    return g == null ? null : (int)g.GameModeType == 0 ? "PvE" : "PvP";
                }
                case ServerRules.ClanSize:
                {
                    var g = GameSettings();
                    return g == null ? null : Math.Max(1, Math.Min(50, g.ClanSize)).ToString(CultureInfo.InvariantCulture);
                }
                case ServerRules.GameDifficulty:
                {
                    var g = GameSettings();
                    if (g == null) return null;
                    int d = (int)g.GameDifficulty;
                    return d == 0 ? "Relaxed" : d == 2 ? "Hard" : "Normal";
                }
                case ServerRules.BloodBoundEquipment:
                {
                    var g = GameSettings();
                    return g == null ? null : g.BloodBoundEquipment ? "true" : "false";
                }
                case ServerRules.CastleHeartDamageMode:
                {
                    var g = GameSettings();
                    if (g == null) return null;
                    int m = (int)g.CastleHeartDamageMode;
                    return m == 0 ? ServerRules.Decaying : m == 2 ? "CanBeSeizedOrDestroyedByPlayers" : "CanBeDestroyedByPlayers";
                }
                default: return null;
            }
        }
        catch { return null; }
    }

    /// <summary>
    /// Stage 7, review fix 4: the running server's password, through the game's own
    /// <c>ServerBootstrapSystem.SetServerPassword</c> (research §5.4; the review verified its <c>VerifyServerSettings(false)</c>
    /// is a no-op after startup). It truncates to 32 characters (the decision already refused longer), sets
    /// <c>RequiresPassword</c> in the discovery data and writes <c>SettingsManager.ServerHostSettings.Password</c>, which
    /// the connection check reads on every join.
    /// </summary>
    internal static void SetLivePassword(string password)
    {
        var world = Worlds.Server;
        if (world == null || !world.IsCreated) throw new InvalidOperationException("the server world is not running");
        var bootstrap = world.GetExistingSystemManaged<ProjectM.ServerBootstrapSystem>()
            ?? throw new InvalidOperationException("ServerBootstrapSystem is not running");
        bootstrap.SetServerPassword(password ?? "");
    }

    /// <summary>The game's tags (<c>ServerBootstrapSystem._GameTags</c>), or null.</summary>
    private static IReadOnlyList<string>? Tags()
    {
        try
        {
            var world = Worlds.Server;
            if (world == null || !world.IsCreated) return null;
            var bootstrap = world.GetExistingSystemManaged<ProjectM.ServerBootstrapSystem>();
            var tags = bootstrap?._GameTags;
            if (tags == null) return null;
            var list = new List<string>(tags.Count);
            for (int i = 0; i < tags.Count; i++) list.Add(tags[i] ?? "");
            return list;
        }
        catch { return null; }
    }
}
#endif
