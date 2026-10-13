using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Progenitor.Networking;

/// <summary>
/// SERVERDESC1: where a dedicated server's <c>ServerHostSettings</c> come from, in the game's own order
/// (<c>SettingsManager.VerifyServerSettings</c>, research note server-list-description-2026-10-05 §1a). Each
/// later layer overwrites only the keys it contains:
///
/// <list type="number">
/// <item>the install's <c>VRisingServer_Data\StreamingAssets\Settings\ServerHostSettings.json</c> (the shipped default);</item>
/// <item><c>&lt;persistentDataPath&gt;\Settings\ServerHostSettings.json</c>;</item>
/// <item>the save folder's <c>ServerHostSettings</c> (bare name first, then <c>.json</c>):
/// <c>&lt;persistentDataPath&gt;\Saves\v4\&lt;SaveName&gt;</c>, where <c>SaveName</c> is the persistent-data
/// file's (or the default's) unless <c>-saveName</c> / <c>-serverSaveName</c> names one;</item>
/// <item>environment variables (<c>VR_DESCRIPTION</c>, <c>VR_NAME</c>, …; the process's own value wins);</item>
/// <item>the command line (<c>-description</c>, <c>-serverName</c>, …).</item>
/// </list>
///
/// <para>Game-free and read-only: it reads files and the inputs it is given, and never writes. A server
/// process passes the game's exact paths (<see cref="ServerHostSettingsInputs.LocalSettingsFile"/>,
/// <see cref="ServerHostSettingsInputs.SaveFolderFor"/>); code that runs before the game (the dev gate)
/// derives them from <c>-persistentDataPath</c>, else the user's <c>LocalLow\Stunlock Studios\VRisingServer</c>
/// folder. Keys are matched without regard to case.</para>
/// </summary>
internal static class ServerHostSettingsFiles
{
    /// <summary>The file name of every layer.</summary>
    public const string FileName = "ServerHostSettings.json";

    /// <summary>The bare name the save-folder layer is looked for first (the game tries it before <c>.json</c>).</summary>
    public const string BareName = "ServerHostSettings";

    /// <summary>The persistence version folder this build of V Rising saves under (<c>Saves\v4</c>).</summary>
    public const string SaveVersionFolder = "v4";

    /// <summary>The <c>Name</c> the shipped default file carries; it names no server.</summary>
    public const string StockServerName = "V Rising Server";

    /// <summary>
    /// The environment variables and command-line arguments that override a key, from the game's
    /// <c>SettingEnvironmentVariableOverride</c> / <c>SettingCommandLineOverride</c> attributes (server dump).
    /// A flag pair (<c>-enableListOnSteam</c> / <c>-disableListOnSteam</c>) sets a bool without a value.
    /// </summary>
    private static readonly Dictionary<string, OverrideNames> Overrides = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Name"] = new(new[] { "serverName" }, new[] { "VR_NAME", "VR_SERVERNAME", "VR_SERVER_NAME" }),
        ["Description"] = new(new[] { "description" }, new[] { "VR_DESCRIPTION" }),
        ["Port"] = new(new[] { "gamePort", "serverPort", "gp" }, new[] { "VR_GAMEPORT", "VR_GAME_PORT" }),
        ["QueryPort"] = new(new[] { "queryPort", "qp" }, new[] { "VR_QUERY_PORT", "VR_QUERYPORT" }),
        ["Address"] = new(new[] { "address", "bindAddress" }, new[] { "VR_BINDADDRESS", "VR_ADDRESS", "VR_BIND_ADDRESS" }),
        ["ListOnSteam"] = new(new[] { "listOnSteam", "listing" }, new[] { "VR_LISTONSTEAM", "VR_LIST_ON_STEAM" }, "enableListOnSteam", "disableListOnSteam"),
        ["ListOnEOS"] = new(new[] { "listOnEOS" }, new[] { "VR_LIST_ON_EOS", "VR_LISTONEOS" }),
        ["LanMode"] = new(new[] { "lanMode" }, new[] { "VR_LAN_MODE", "VR_LAN" }, "enableLanMode", "disableLanMode", "lan"),
        ["PrivateGame"] = new(new[] { "privateGame" }, new[] { "VR_PRIVATEGAME", "VR_PRIVATE_GAME" }),
        ["SaveName"] = new(new[] { "saveName", "serverSaveName" }, new[] { "VR_SAVENAME", "VR_SAVE_NAME" }),
        // Settings organization stage 6 (server dump 501980-502144). A name starting with '=' is matched with its case:
        // the game reads -P as the password and -p as the preset.
        ["MaxConnectedUsers"] = new(new[] { "maxUsers", "maxConnectedUsers" }, new[] { "VR_MAXUSERS", "VR_MAX_USERS" }),
        ["MaxConnectedAdmins"] = new(new[] { "maxAdmins", "maxConnectedAdmins" }, new[] { "VR_MAX_ADMINS", "VR_MAXADMINS" }),
        ["Password"] = new(new[] { "password", "=P" }, new[] { "VR_PASSWORD" }),
        ["GameSettingsPreset"] = new(new[] { "preset", "=p" }, new[] { "VR_PRESET" }),
        ["GameDifficultyPreset"] = new(new[] { "difficultyPreset", "dp" }, new[] { "VR_DIFFICULTY_PRESET" }),
        ["ResetDaysInterval"] = new(new[] { "resetDaysInterval" }, new[] { "VR_RESET_DAYS_INTERVAL" }),
        ["DayOfReset"] = new(new[] { "dayOfReset" }, new[] { "VR_DAY_OF_RESET" }),
        // BACKUPNOW1: the game's autosave rotation (server metadata, ServerHostSettings.AutoSaveCount / AutoSaveInterval).
        ["AutoSaveCount"] = new(new[] { "saveCount" }, new[] { "VR_SAVECOUNT", "VR_SAVE_COUNT" }),
        ["AutoSaveInterval"] = new(new[] { "saveInterval" }, new[] { "VR_SAVE_INTERVAL", "VR_SAVEINTERVAL" }),
    };

    /// <summary>Read one settings file as a layer (settings organization stage 6 reads the game files with it).</summary>
    internal static ServerHostSettingsLayer ReadLayer(ServerHostSettingsLayerKind kind, string path) => Read(kind, path);

    /// <summary>Like <see cref="TryArg"/>, with the argument's case matched exactly.</summary>
    public static bool TryArgExact(IReadOnlyList<string>? args, string name, out string value)
    {
        value = "";
        if (args == null || string.IsNullOrEmpty(name)) return false;
        string flag = "-" + name;
        for (int i = 0; i < args.Count; i++)
        {
            string a = args[i] ?? "";
            if (string.Equals(a, flag, StringComparison.Ordinal))
            {
                if (i + 1 >= args.Count) return false;
                value = args[i + 1] ?? "";
                return true;
            }

            if (a.Length > flag.Length + 1 && a.StartsWith(flag + "=", StringComparison.Ordinal))
            {
                value = a.Substring(flag.Length + 1);
                return true;
            }
        }

        return false;
    }

    private static readonly JsonDocumentOptions ParseOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>Resolve the layers for <paramref name="inputs"/>. Never throws; an unreadable file is a layer with an error.</summary>
    public static ServerHostSettingsResolution Resolve(ServerHostSettingsInputs inputs)
    {
        inputs ??= new ServerHostSettingsInputs();
        var args = inputs.Args ?? Array.Empty<string>();
        Func<string, string?> env = inputs.Env ?? ReadEnvironment;

        string persistent = (inputs.PersistentDataPath ?? "").Trim();
        if (persistent.Length == 0 && TryArg(args, "persistentDataPath", out string argPath)) persistent = argPath.Trim();
        string localFile = (inputs.LocalSettingsFile ?? "").Trim();
        if (localFile.Length > 0 && persistent.Length == 0)
        {
            // <persistentDataPath>\Settings\ServerHostSettings.json: the data folder is two up.
            try { persistent = Path.GetDirectoryName(Path.GetDirectoryName(localFile) ?? "") ?? ""; }
            catch { persistent = ""; }
        }
        if (persistent.Length == 0) persistent = (inputs.DefaultDataPath ?? DefaultServerDataPath()).Trim();

        var layers = new List<ServerHostSettingsLayer>(3);

        // 1. The shipped default, in the install.
        string install = (inputs.InstallRoot ?? "").Trim();
        layers.Add(install.Length == 0
            ? new ServerHostSettingsLayer(ServerHostSettingsLayerKind.Default, "", exists: false)
            : Read(ServerHostSettingsLayerKind.Default, SafeCombine(install, "VRisingServer_Data", "StreamingAssets", "Settings", FileName)));

        // 2. The persistent-data copy.
        if (localFile.Length == 0 && persistent.Length > 0) localFile = SafeCombine(persistent, "Settings", FileName);
        layers.Add(localFile.Length == 0
            ? new ServerHostSettingsLayer(ServerHostSettingsLayerKind.Local, "", exists: false)
            : Read(ServerHostSettingsLayerKind.Local, localFile));

        // 3. The save folder's copy. SaveName is read before the environment and the command line are
        // applied, except that -saveName / -serverSaveName are applied first (VerifyServerSettings).
        string saveName = "";
        if (TryArg(args, "saveName", out string s1)) saveName = s1;
        else if (TryArg(args, "serverSaveName", out string s2)) saveName = s2;
        else
        {
            for (int i = layers.Count - 1; i >= 0 && saveName.Length == 0; i--)
                if (layers[i].TryGetString("SaveName", out string fromFile)) saveName = fromFile;
        }
        saveName = saveName.Trim();
        string saveFolder = "";
        if (saveName.Length > 0)
        {
            try
            {
                saveFolder = inputs.SaveFolderFor != null
                    ? (inputs.SaveFolderFor(saveName) ?? "").Trim()
                    : persistent.Length == 0 ? "" : SafeCombine(persistent, "Saves", SaveVersionFolder, saveName);
            }
            catch { saveFolder = ""; }
        }

        if (saveFolder.Length == 0)
            layers.Add(new ServerHostSettingsLayer(ServerHostSettingsLayerKind.Save, "", exists: false));
        else
        {
            string bare = SafeCombine(saveFolder, BareName);
            string json = SafeCombine(saveFolder, FileName);
            layers.Add(SafeExists(bare) ? Read(ServerHostSettingsLayerKind.Save, bare) : Read(ServerHostSettingsLayerKind.Save, json));
        }

        return new ServerHostSettingsResolution(layers, persistent, saveName, saveFolder, args, env);
    }

    /// <summary>
    /// Split a Windows command line into arguments (quotes group, a backslash before a quote escapes it).
    /// For a caller that has <c>Environment.CommandLine</c> rather than the argument array.
    /// </summary>
    public static IReadOnlyList<string> SplitCommandLine(string? commandLine)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(commandLine)) return result;
        var current = new StringBuilder();
        bool quoted = false, any = false;
        string text = commandLine!;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\\' && i + 1 < text.Length && text[i + 1] == '"')
            {
                current.Append('"');
                any = true;
                i++;
                continue;
            }

            if (c == '"')
            {
                quoted = !quoted;
                any = true;
                continue;
            }

            if (!quoted && char.IsWhiteSpace(c))
            {
                if (any) result.Add(current.ToString());
                current.Clear();
                any = false;
                continue;
            }

            current.Append(c);
            any = true;
        }

        if (any) result.Add(current.ToString());
        return result;
    }

    /// <summary>
    /// The value of command-line argument <c>-<paramref name="name"/></c> (also <c>-name=value</c>), matched
    /// without regard to case. The first occurrence wins.
    /// </summary>
    public static bool TryArg(IReadOnlyList<string>? args, string name, out string value)
    {
        value = "";
        if (args == null || string.IsNullOrEmpty(name)) return false;
        string flag = "-" + name;
        for (int i = 0; i < args.Count; i++)
        {
            string a = args[i] ?? "";
            if (string.Equals(a, flag, StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Count) return false;
                value = args[i + 1] ?? "";
                return true;
            }

            if (a.Length > flag.Length + 1 && a.StartsWith(flag + "=", StringComparison.OrdinalIgnoreCase))
            {
                value = a.Substring(flag.Length + 1);
                return true;
            }
        }

        return false;
    }

    /// <summary>True when <c>-<paramref name="name"/></c> appears as an argument (a flag without a value).</summary>
    public static bool HasArg(IReadOnlyList<string>? args, string name)
    {
        if (args == null || string.IsNullOrEmpty(name)) return false;
        string flag = "-" + name;
        foreach (string a in args)
            if (string.Equals(a, flag, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// <c>%USERPROFILE%\AppData\LocalLow\Stunlock Studios\VRisingServer</c>: the server's data folder when it
    /// runs without <c>-persistentDataPath</c>. Empty when the profile folder is unknown.
    /// </summary>
    public static string DefaultServerDataPath()
    {
        try
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(local)) return "";
            string parent = Path.GetDirectoryName(local.TrimEnd('\\', '/')) ?? "";
            return parent.Length == 0 ? "" : Path.Combine(parent, "LocalLow", "Stunlock Studios", "VRisingServer");
        }
        catch { return ""; }
    }

    /// <summary>The process's value, else the user's, else the machine's (the game reads all three; the process's is applied last).</summary>
    private static string? ReadEnvironment(string name)
    {
        try
        {
            string? v = Environment.GetEnvironmentVariable(name);
            if (v != null) return v;
            if (!OperatingSystem.IsWindows()) return null;
            v = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User);
            if (v != null) return v;
            return Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine);
        }
        catch { return null; }
    }

    internal static bool TryOverride(
        string key, IReadOnlyList<string> args, Func<string, string?> env, out ServerHostSettingsOverride found)
    {
        found = default;
        if (!Overrides.TryGetValue(key, out var names)) return false;
        // The command line is applied after the environment, so it wins.
        foreach (string cli in names.Cli)
        {
            bool exact = cli.StartsWith("=", StringComparison.Ordinal);
            string name = exact ? cli.Substring(1) : cli;
            if (exact ? TryArgExact(args, name, out string ve) : TryArg(args, name, out ve))
            {
                found = new ServerHostSettingsOverride(key, "-" + name, ve);
                return true;
            }
        }

        if (names.EnableFlag != null && HasArg(args, names.EnableFlag))
        {
            found = new ServerHostSettingsOverride(key, "-" + names.EnableFlag, "true");
            return true;
        }

        if (names.ShortFlag != null && HasArg(args, names.ShortFlag))
        {
            found = new ServerHostSettingsOverride(key, "-" + names.ShortFlag, "true");
            return true;
        }

        if (names.DisableFlag != null && HasArg(args, names.DisableFlag))
        {
            found = new ServerHostSettingsOverride(key, "-" + names.DisableFlag, "false");
            return true;
        }

        foreach (string name in names.Env)
        {
            string? v = null;
            try { v = env(name); }
            catch { /* unreadable: not set */ }
            if (v != null)
            {
                found = new ServerHostSettingsOverride(key, name, v);
                return true;
            }
        }

        return false;
    }

    private static ServerHostSettingsLayer Read(ServerHostSettingsLayerKind kind, string path)
    {
        if (!SafeExists(path)) return new ServerHostSettingsLayer(kind, path, exists: false);
        try
        {
            string text = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(text, ParseOptions);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return new ServerHostSettingsLayer(kind, path, exists: true, error: "not a JSON object");
            var values = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in doc.RootElement.EnumerateObject()) values[p.Name] = p.Value.Clone();
            return new ServerHostSettingsLayer(kind, path, exists: true, values: values);
        }
        catch (Exception e)
        {
            return new ServerHostSettingsLayer(kind, path, exists: true, error: e.Message);
        }
    }

    private static bool SafeExists(string path)
    {
        try { return path.Length > 0 && File.Exists(path); }
        catch { return false; }
    }

    private static string SafeCombine(params string[] parts)
    {
        try { return Path.Combine(parts); }
        catch { return ""; }
    }

    private sealed class OverrideNames
    {
        internal OverrideNames(string[] cli, string[] env, string? enableFlag = null, string? disableFlag = null, string? shortFlag = null)
        {
            Cli = cli;
            Env = env;
            EnableFlag = enableFlag;
            DisableFlag = disableFlag;
            ShortFlag = shortFlag;
        }

        internal string[] Cli { get; }
        internal string[] Env { get; }
        internal string? EnableFlag { get; }
        internal string? DisableFlag { get; }
        internal string? ShortFlag { get; }
    }
}

/// <summary>What <see cref="ServerHostSettingsFiles.Resolve"/> reads. Every member is optional.</summary>
internal sealed class ServerHostSettingsInputs
{
    /// <summary>The process's arguments (<c>Environment.GetCommandLineArgs()</c>).</summary>
    public IReadOnlyList<string>? Args { get; set; }

    /// <summary>Reads an environment variable (null: unset). Default: the process's, then the user's, then the machine's.</summary>
    public Func<string, string?>? Env { get; set; }

    /// <summary>The install folder that holds <c>VRisingServer_Data</c> (for the shipped default).</summary>
    public string? InstallRoot { get; set; }

    /// <summary>The data folder; default <c>-persistentDataPath</c>, else <see cref="DefaultDataPath"/>.</summary>
    public string? PersistentDataPath { get; set; }

    /// <summary>The data folder without <c>-persistentDataPath</c>; default the user's LocalLow server folder. Tests set it.</summary>
    public string? DefaultDataPath { get; set; }

    /// <summary>The game's exact persistent-data settings file (<c>ServerHostSettings.LocalPath</c>), when known.</summary>
    public string? LocalSettingsFile { get; set; }

    /// <summary>The game's save folder for a save name (<c>ServerRuntimeSettings.GetCombinedFullSavePath</c>), when known.</summary>
    public Func<string, string?>? SaveFolderFor { get; set; }
}

/// <summary>Which file a layer is.</summary>
internal enum ServerHostSettingsLayerKind
{
    /// <summary>The install's <c>StreamingAssets</c> default. Never written: a game update replaces it.</summary>
    Default = 0,

    /// <summary><c>&lt;persistentDataPath&gt;\Settings\ServerHostSettings.json</c>.</summary>
    Local = 1,

    /// <summary>The save folder's <c>ServerHostSettings(.json)</c>.</summary>
    Save = 2,
}

/// <summary>One settings file: where it is, whether it exists, and its top-level keys.</summary>
internal sealed class ServerHostSettingsLayer
{
    private readonly Dictionary<string, JsonElement>? _values;

    internal ServerHostSettingsLayer(
        ServerHostSettingsLayerKind kind, string path, bool exists, Dictionary<string, JsonElement>? values = null, string? error = null)
    {
        Kind = kind;
        Path = path ?? "";
        Exists = exists;
        _values = values;
        Error = error ?? "";
    }

    public ServerHostSettingsLayerKind Kind { get; }

    /// <summary>The file's path (where it would be when it does not exist); empty when unknown.</summary>
    public string Path { get; }

    public bool Exists { get; }

    /// <summary>Why an existing file gave no values (it did not parse); empty otherwise.</summary>
    public string Error { get; }

    /// <summary>True when the file was read and is a JSON object.</summary>
    public bool Readable => _values != null;

    /// <summary>True when the file carries <paramref name="key"/> (any type).</summary>
    public bool Has(string key) => _values != null && _values.ContainsKey(key);

    public bool TryGetString(string key, out string value)
    {
        value = "";
        if (_values == null || !_values.TryGetValue(key, out var v) || v.ValueKind != JsonValueKind.String) return false;
        value = v.GetString() ?? "";
        return true;
    }

    public bool TryGetBool(string key, out bool value)
    {
        value = false;
        if (_values == null || !_values.TryGetValue(key, out var v)) return false;
        if (v.ValueKind == JsonValueKind.True) { value = true; return true; }
        if (v.ValueKind == JsonValueKind.False) { value = false; return true; }
        return false;
    }

    /// <summary>The raw value of <paramref name="key"/> (any type).</summary>
    public bool TryGet(string key, out JsonElement value)
    {
        value = default;
        return _values != null && _values.TryGetValue(key, out value);
    }

    public bool TryGetInt(string key, out int value)
    {
        value = 0;
        return _values != null && _values.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out value);
    }
}

/// <summary>An environment variable or command-line argument that overrides a key at every start.</summary>
internal readonly struct ServerHostSettingsOverride
{
    internal ServerHostSettingsOverride(string key, string source, string value)
    {
        Key = key;
        Source = source;
        Value = value ?? "";
    }

    /// <summary>The settings key (<c>Description</c>).</summary>
    public string Key { get; }

    /// <summary>What sets it: <c>-description</c> or <c>VR_DESCRIPTION</c>.</summary>
    public string Source { get; }

    /// <summary>The raw value.</summary>
    public string Value { get; }

    /// <summary>True when the source is a command-line argument.</summary>
    public bool FromCommandLine => Source.StartsWith("-", StringComparison.Ordinal);

    /// <summary>The value as a bool the game would read, or null when it is not one.</summary>
    public bool? AsBool()
    {
        string v = Value.Trim();
        if (string.Equals(v, "true", StringComparison.OrdinalIgnoreCase) || v == "1") return true;
        if (string.Equals(v, "false", StringComparison.OrdinalIgnoreCase) || v == "0") return false;
        return null;
    }
}

/// <summary>The layers of one server's settings, merged in the game's order.</summary>
internal sealed class ServerHostSettingsResolution
{
    private readonly IReadOnlyList<string> _args;
    private readonly Func<string, string?> _env;

    internal ServerHostSettingsResolution(
        IReadOnlyList<ServerHostSettingsLayer> layers, string persistentDataPath, string saveName, string saveFolder,
        IReadOnlyList<string> args, Func<string, string?> env)
    {
        Layers = layers;
        PersistentDataPath = persistentDataPath ?? "";
        SaveName = saveName ?? "";
        SaveFolder = saveFolder ?? "";
        _args = args;
        _env = env;
    }

    /// <summary>Default, Local, Save: lowest to highest.</summary>
    public IReadOnlyList<ServerHostSettingsLayer> Layers { get; }

    public string PersistentDataPath { get; }

    /// <summary>The save name the game uses to find the save-folder layer (empty: none).</summary>
    public string SaveName { get; }

    public string SaveFolder { get; }

    public ServerHostSettingsLayer Default => Layers[0];

    public ServerHostSettingsLayer Local => Layers[1];

    public ServerHostSettingsLayer Save => Layers[2];

    /// <summary>
    /// The file a change should be written to: the highest layer that exists among the save folder's and the
    /// persistent-data copy, else the persistent-data copy (to be created). Never the shipped default.
    /// Null when neither path is known.
    /// </summary>
    public ServerHostSettingsLayer? WriteTarget
    {
        get
        {
            if (Save.Exists && Save.Path.Length > 0) return Save;
            if (Local.Path.Length > 0) return Local;
            return null;
        }
    }

    /// <summary>The highest file layer that carries <paramref name="key"/>, or null.</summary>
    public ServerHostSettingsLayer? FileLayerOf(string key)
    {
        for (int i = Layers.Count - 1; i >= 0; i--)
            if (Layers[i].Has(key)) return Layers[i];
        return null;
    }

    /// <summary>The environment variable or argument that overrides <paramref name="key"/> at start, if any.</summary>
    public ServerHostSettingsOverride? OverrideOf(string key) =>
        ServerHostSettingsFiles.TryOverride(key, _args, _env, out var found) ? found : null;

    /// <summary>The files' value of a string key (no overrides), or null when no layer has it.</summary>
    public string? FileString(string key)
    {
        for (int i = Layers.Count - 1; i >= 0; i--)
            if (Layers[i].TryGetString(key, out string v)) return v;
        return null;
    }

    /// <summary>The files' value of a bool key (no overrides), or null.</summary>
    public bool? FileBool(string key)
    {
        for (int i = Layers.Count - 1; i >= 0; i--)
            if (Layers[i].TryGetBool(key, out bool v)) return v;
        return null;
    }

    /// <summary>The files' value of an int key (no overrides), or null.</summary>
    public int? FileInt(string key)
    {
        for (int i = Layers.Count - 1; i >= 0; i--)
            if (Layers[i].TryGetInt(key, out int v)) return v;
        return null;
    }

    /// <summary>The value the game starts with: an override, else the files'. Null when nothing sets it.</summary>
    public string? EffectiveString(string key) => OverrideOf(key) is { } o ? o.Value : FileString(key);

    /// <summary>The bool the game starts with: an override that reads as a bool, else the files'.</summary>
    public bool? EffectiveBool(string key) => OverrideOf(key)?.AsBool() ?? FileBool(key);

    /// <summary>The int the game starts with: an override that reads as a number, else the files'.</summary>
    public int? EffectiveInt(string key)
    {
        if (OverrideOf(key) is { } o
            && int.TryParse(o.Value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)) return n;
        return FileInt(key);
    }

    /// <summary>The layers as one line, for logs: <c>default …(exists), local …(missing), save …</c>.</summary>
    public string Describe()
    {
        var parts = new List<string>(Layers.Count);
        foreach (var l in Layers)
        {
            string state = l.Path.Length == 0 ? "unknown" : !l.Exists ? "missing" : l.Readable ? "read" : "unreadable: " + l.Error;
            parts.Add(l.Kind.ToString().ToLowerInvariant() + " " + (l.Path.Length == 0 ? "(no path)" : l.Path) + " (" + state + ")");
        }
        return string.Join("; ", parts);
    }
}
