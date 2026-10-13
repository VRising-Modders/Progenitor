#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using System.IO;
using BepInEx.Configuration;
using Progenitor.Ui;

namespace Progenitor.Networking;

/// <summary>
/// SERVERDESC1 (docs/server-list-description.md): the server-list description rows on Progenitor's page
/// (Server settings, next to the server message) and the server's apply loop.
///
/// <para><b>Rows.</b> Their own file, <c>BepInEx/config/progenitor.serverlist.cfg</c>, section
/// <c>[ServerList]</c>: <c>Description</c> (the admin's raw text, tokens unrendered, no signature) and
/// <c>Signature</c> (default on), ConfigSync rows of Core opted into administrator edits exactly like the
/// server message's (authorization, rate limit and audit: settings-scopes.md); and <c>Published</c>, a status
/// line the server writes after each publish, which the page shows read-only.</para>
///
/// <para><b>Applying.</b> The server ticks <see cref="ServerListApplier"/> on the main thread (every 250 ms):
/// at Core start once the game's settings exist, after an edit, and on the minute check for the tokens.</para>
/// </summary>
internal static class ServerListDescriptionSettings
{
    public const string FileName = "progenitor.serverlist.cfg";
    public const string Section = "ServerList";
    public const string DescriptionKey = "Description";
    public const string SignatureKey = "Signature";
    public const string StatusKey = "Published";
    public const string DescriptionInChatKey = "DescriptionInChat";

    /// <summary>BROWSERFILTER1: publish the <see cref="ServerBrowserMarker.Tag"/> server-list tag (cfg-only, default on).</summary>
    public const string BrowserTagKey = "BrowserTag";

    /// <summary>The page section's title.</summary>
    // LISTING1 (user review 2026-10-06): "Server listing" (the slug server-tab:server-list stays for the bridge).
    public const string PageSection = Progenitor.Ui.ProgenitorPage.ServerListTitle;

    /// <summary>LISTING1: when a change shows (docs/server-list-description.md: Publishing).</summary>
    public const string UpdateTiming =
        "A saved change is published at once: to Steam right away, to EOS within about a second. Tokens are checked every "
        + "minute and the text is published again only when it reads differently (about hourly at most). The browser shows it "
        + "the next time a player selects the server.";

    /// <summary>LISTING1: the section heading's help (the description column while it is hovered).</summary>
    public const string SectionHelp =
        "What players see about this server in the in-game server browser: the description under the server name, then "
        + "the Progenitor signature line. " + UpdateTiming;

    private static readonly object Owner = new();
    private static readonly List<IDisposable> Registrations = new();
    private static readonly ServerListGame Game = new();
    private static ServerListApplier _applier = new();
    private static bool _installed;
    private static ConfigFile? _file;
    private static ConfigEntry<string>? _description;
    private static ConfigEntry<bool>? _signature;
    private static ConfigEntry<string>? _status;
    private static ConfigEntry<bool>? _descriptionInChat;
    private static ConfigEntry<bool>? _browserTag;
    private static ServerBrowserTagPublisher _tagPublisher = new();
    private static readonly ServerTagGame TagGame = new();
    private static double _nextTick;
    private static bool _seedPending;
    private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();

    /// <summary>The row id the bridge and ServerSettings use: <c>progenitor/ServerList/&lt;key&gt;</c>.</summary>
    public static string RowId(string key) => ConfigSync.FrameworkSettingsId + "/" + Section + "/" + key;

    /// <summary>The applier (bridge readback).</summary>
    internal static ServerListApplier Applier => _applier;

    /// <summary>
    /// Bind the rows and register them with ConfigSync and the settings catalog; on the server, seed the admin
    /// text from the game's current description the first time and start the apply loop. Before
    /// <see cref="JoinServerSettings.Install"/>, whose page places these rows. Idempotent.
    /// </summary>
    internal static void Install()
    {
        if (_installed) return;
        _installed = true;
        try
        {
            string dir;
            try { dir = BepInEx.Paths.ConfigPath; }
            catch { dir = ModManifest.ConfigDirectory; }
            string path = Path.Combine(dir, FileName);
            bool firstRun = !File.Exists(path);
            _file = new ConfigFile(path, saveOnInit: true);
            _description = Config.ConfigHelper.Bind(_file, Section, DescriptionKey, "",
                "The server's description in the game's server list (Join Game), before the signature: plain text, line breaks "
                + "kept. The whole description is at most 128 characters; with the signature on, this text gets 82. Tokens: "
                + "{reset:relative} (in 3 days / in 5 hours / today) and {reset:date} (in the server's time zone), from the server "
                + "message's reset settings.");
            _signature = Config.ConfigHelper.Bind(_file, Section, SignatureKey, true,
                "Adds \"" + ServerListDescription.Signature + "\" on its own line after the description, so a player knows to "
                + "install Progenitor before joining.");
            _status = Config.ConfigHelper.Bind(_file, Section, StatusKey, "",
                "Written by the server after each publish (what happened, and any warning); edits here are replaced.");
            // NODESCCHAT: cfg-only (SettingPlaces homes it "cfg"; not a ConfigSync row, never sent to clients).
            _descriptionInChat = Config.ConfigHelper.Bind(_file, Section, DescriptionInChatKey, false,
                "Also post the server list description to each joining player's chat as a System line, as the game "
                + "does without Progenitor. Off (default): the description is only published to the server list. Read on "
                + "every join.");

            // BROWSERFILTER1: cfg-only like DescriptionInChat (SettingPlaces homes it "cfg"; never sent to clients).
            _browserTag = Config.ConfigHelper.Bind(_file, Section, BrowserTagKey, true,
                "Add the \"" + ServerBrowserMarker.Tag + "\" tag to this server's game tags in the server list, so a player's "
                + "Join Game browser can show only servers running Progenitor (Show Only: Progenitor Servers). Vanilla clients "
                + "ignore it. Off: the tag is removed at once.");

            // The seed reads the game's live value once its settings exist (first apply step), never game
            // statics this early: Core can load before the game has set up its paths.
            _seedPending = Plugin.IsServerProcess && firstRun;

            SettingsCatalog.Register(ConfigSync.FrameworkSettingsId, _file, Owner);
            foreach (var entry in new ConfigEntryBase[] { _description, _signature, _status })
            {
                try { Registrations.Add(ConfigSync.Register(ConfigSync.FrameworkSettingsId, entry)); }
                catch (Exception e) { Plugin.Logger?.LogWarning("ServerList: ConfigSync could not register " + entry.Definition + ": " + e.Message); }
            }

            if (Plugin.IsServerProcess)
            {
                // Through the tracked helper (SettingChangedPinTests): it is unsubscribed when Core unloads.
                Config.ConfigHelper.AddSettingChanged(_file, OnEdited);
                _applier = new ServerListApplier();
                _tagPublisher = new ServerBrowserTagPublisher();
                JoinDescriptionChatHook.Install(() => _descriptionInChat?.Value ?? false);
                _applier.MarkDirty("start");
                Plugin.Frame -= OnFrame;
                Plugin.Frame += OnFrame;
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("ServerList: the server list description settings could not be bound: " + e.Message);
        }
    }

    internal static void Uninstall()
    {
        try { Plugin.Frame -= OnFrame; }
        catch { /* teardown */ }
        try { JoinDescriptionChatHook.Uninstall(); }
        catch { /* teardown */ }
        for (int i = Registrations.Count - 1; i >= 0; i--)
        {
            try { Registrations[i].Dispose(); }
            catch { /* teardown */ }
        }

        Registrations.Clear();
        _description = null;
        _signature = null;
        _status = null;
        _descriptionInChat = null;
        _browserTag = null;
        _file = null;
        _installed = false;
        _nextTick = 0;
    }

    /// <summary>
    /// First run on a server: the admin text starts as the game's live description (what it loaded from its
    /// settings files, environment and command line), without a signature line an earlier run composed.
    /// </summary>
    private static void Seed()
    {
        try
        {
            string current = Game.LiveDescription ?? "";
            string seed = ServerListDescription.SeedFrom(current);
            if (seed.Length > 0 && _description != null)
            {
                _description.Value = seed;
                Plugin.Logger?.LogInfo("ServerList: the description row starts from the server's current description (" + seed.Length + " characters).");
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("ServerList: the current description could not be read for the first run: " + e.Message);
        }
    }

    private static void OnEdited(object? sender, SettingChangedEventArgs e)
    {
        // The status row's own write is not an edit; neither is anything after Uninstall.
        if (!_installed || e?.ChangedSetting == null) return;
        if (ReferenceEquals(e.ChangedSetting, _description) || ReferenceEquals(e.ChangedSetting, _signature))
            _applier.MarkDirty("edit");
    }

    private static void OnFrame()
    {
        JoinDescriptionChatHook.OnFrame();
        double now = Clock.Elapsed.TotalSeconds;
        if (now < _nextTick) return;
        _nextTick = now + 0.25;
        TickNow();
    }

    /// <summary>The tag publisher (bridge readback).</summary>
    internal static ServerBrowserTagPublisher TagPublisher => _tagPublisher;

    /// <summary>Whether the cfg switch publishes the tag (true when unbound).</summary>
    internal static bool BrowserTagOn() => _browserTag?.Value ?? true;

    /// <summary>One apply-loop step now (main thread). Returns the report when something was published.</summary>
    internal static ServerListReport? TickNow()
    {
        try
        {
            string? tagLine = _tagPublisher.Tick(DateTime.UtcNow, BrowserTagOn(), TagGame);
            if (tagLine != null) Plugin.Logger?.LogInfo(tagLine);
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("ServerList: the tag step threw: " + e.Message); }

        try
        {
            if (_seedPending && Game.SettingsReady)
            {
                _seedPending = false;
                Seed();
            }

            var report = _applier.Tick(DateTime.UtcNow, CurrentValues, Game);
            if (report != null) Published(report);
            return report;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("ServerList: the apply step threw: " + e.Message);
            return null;
        }
    }

    /// <summary>The status line as this side holds it (the server's own; a client's synced copy).</summary>
    internal static string StatusText() => _status?.Value ?? "";

    /// <summary>
    /// Bridge (server): write the rows as an accepted administrator edit does (null leaves one alone; the
    /// signature takes true/false/on/off). The values pass the same text checks an edit does.
    /// </summary>
    internal static bool TryWrite(string? text, string? signature, out string error)
    {
        error = "";
        if (_description == null || _signature == null)
        {
            error = "the server list rows are not bound";
            return false;
        }

        bool? sig = null;
        if (signature != null)
        {
            switch (signature.Trim().ToLowerInvariant())
            {
                case "true": case "on": case "1": sig = true; break;
                case "false": case "off": case "0": sig = false; break;
                default:
                    error = "signature must be true or false";
                    return false;
            }
        }

        if (text != null)
        {
            string value = UiCommitText.Trim(text.Replace("\r\n", "\n"));
            string? check = ServerSettings.CheckText(RowId(DescriptionKey), value);
            if (check != null)
            {
                error = check;
                return false;
            }

            _description.Value = value;
        }

        if (sig is bool on) _signature.Value = on;
        return true;
    }

    /// <summary>The values to compose from, as this side holds them.</summary>
    internal static ServerListValues CurrentValues() =>
        new(_description?.Value ?? "", _signature?.Value ?? true, JoinServerSettings.CurrentReset(), JoinServerSettings.ServerZone());

    private static void Published(ServerListReport report)
    {
        string status = report.Status(TimeZoneInfo.Local);
        Plugin.Logger?.LogInfo("ServerList: published (" + report.Reason + ") " + report.Composed.Length + " characters"
            + (report.Cut ? ", the admin text cut to fit" : "") + "; Steam " + report.Steam
            + (report.SteamCleared.Count > 0 ? " (cleared " + string.Join(", ", report.SteamCleared) + ")" : "")
            + "; file " + report.Persist + (report.PersistPath.Length > 0 ? " " + report.PersistPath : "")
            + (report.OverrideSource.Length > 0 ? "; overridden at start by " + report.OverrideSource : "") + ".");
        try
        {
            if (_status != null && !string.Equals(_status.Value, status, StringComparison.Ordinal)) _status.Value = status;
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("ServerList: the status row could not be written: " + e.Message); }
    }

    // ---- the page ---------------------------------------------------------------------------------

    /// <summary>Add the rows to Progenitor's page (called from <see cref="JoinServerSettings"/>'s page builder).</summary>
    internal static void AddRows(SettingsPageBuilder page)
    {
        if (_description == null || _signature == null || _status == null) return;
        page.Section(PageSection).SectionHelp(SectionHelp);
        page.Text(_description, multiline: true, lines: 3, label: "Server list description", scope: SettingScope.Server,
                help: TextTokens.Help(TextTokens.ServerListRowId,
                    "What players read about this server in Join Game, before they join. Plain text, line breaks kept. "
                    + "Saved and shown at once, no restart. Links are not clickable in the server list."))
            .MaxLength(ServerListDescription.MaxChars)
            .EditorCounterLimit(() => ServerListDescription.Room(_signature?.Value ?? true));
        // LISTING1: Room left is the counter line right under the field (TextTokens.Counter), not a row of its own.
        page.Toggle(_signature, label: "Progenitor signature", scope: SettingScope.Server,
            help: "Adds \"" + ServerListDescription.Signature + "\" on its own line after your text, so a new player "
                + "knows to install it before joining. Off: your text exactly as written.");
        // TOKENINSERT1: "Listed as" became the Players see line under the field (the description with its signature).
        // Placed (so it is not listed again under General) but never drawn; its text is the Status line.
        page.Setting(_status, showIf: () => false);
        page.Info("Status", () => string.IsNullOrEmpty(_status?.Value) ? "Not published yet" : _status!.Value, refreshSeconds: 1,
                help: "What the server did with the description last: Steam, EOS and the settings file. " + UpdateTiming)
            .InScope(SettingScope.ServerWide);
    }

    /// <summary>The admin's text as typed now (the field or the open editor), else the stored value.</summary>
    private static string TypedOrStored() =>
        ModSettingsView.TypedText(RowId(DescriptionKey)) ?? _description?.Value ?? "";

    /// <summary>TOKENINSERT1: whether the signature is on, as this side holds it (the Players see line adds it).</summary>
    internal static bool SignatureOn()
    {
        try { return _signature?.Value ?? true; }
        catch { return true; }
    }
}

/// <summary>
/// SERVERDESC1: the live game behind <see cref="IServerListGame"/>. Every member was checked with the IL viewer
/// against both the server's and the client's interop assemblies (2026-10-05): <c>SettingsManager._ServerHostSettings</c>
/// (static field), <c>ServerHostSettings.Description</c> / <c>ListOnSteam</c> / <c>ListOnEOS</c> (string and bool
/// fields), <c>ServerHostSettings.LocalPath</c> (static string), <c>ServerRuntimeSettings.GetCombinedFullSavePath(string, ulong)</c>,
/// <c>PlatformSystemBase.GetSystem()</c>, <c>PlatformSystemBase._ServerInitSuccess</c>,
/// <c>PlatformSystemBase.SetServerDescription(string)</c> and <c>Steamworks.SteamGameServer.SetKeyValue(string, string)</c>.
/// Plain strings, bools and one ulong: no by-ref structs, no generic instantiation. Main thread only.
///
/// <para>The live value is written to the field directly rather than through
/// <c>ServerBootstrapSystem.SetServerDescription</c>, whose only extra work is ignoring an empty text (which would
/// leave a cleared description in place) and the 128 cut the composition already makes.</para>
/// </summary>
internal sealed class ServerListGame : IServerListGame
{
    private static ProjectM.ServerHostSettings? Settings()
    {
        try { return ProjectM.SettingsManager._ServerHostSettings; }
        catch { return null; }
    }

    public bool SettingsReady => Settings() != null && Worlds.Server != null;

    public string? LiveDescription => Settings()?.Description;

    public void SetLiveDescription(string text)
    {
        var settings = Settings() ?? throw new InvalidOperationException("the game's ServerHostSettings is not loaded");
        settings.Description = text ?? "";
    }

    public (bool Steam, bool Eos)? Listing
    {
        get
        {
            var s = Settings();
            return s == null ? null : (s.ListOnSteam, s.ListOnEOS);
        }
    }

    public bool SteamReady
    {
        get
        {
            try
            {
                var system = ProjectM.Auth.PlatformSystemBase.GetSystem();
                return system != null && system._ServerInitSuccess;
            }
            catch { return false; }
        }
    }

    public void SteamPublish(string text)
    {
        var system = ProjectM.Auth.PlatformSystemBase.GetSystem()
            ?? throw new InvalidOperationException("PlatformSystemBase.GetSystem() returned nothing");
        system.SetServerDescription(text ?? "");
    }

    public void SteamSetKey(string key, string value) => Steamworks.SteamGameServer.SetKeyValue(key, value ?? "");

    public ServerHostSettingsResolution Resolve()
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
            if (raw.Length > 0 && Path.IsPathRooted(raw))
                local = raw.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? raw : raw + ".json";
        }
        catch { local = null; }

        ulong owner = 0;
        if (ServerHostSettingsFiles.HasArg(args, "userSave") && ServerHostSettingsFiles.TryArg(args, "owner", out string ownerText))
            ulong.TryParse(ownerText, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out owner);

        return ServerHostSettingsFiles.Resolve(new ServerHostSettingsInputs
        {
            Args = args,
            InstallRoot = install,
            LocalSettingsFile = local,
            SaveFolderFor = name =>
            {
                try { return ProjectM.ServerRuntimeSettings.GetCombinedFullSavePath(name, owner); }
                catch { return null; }
            },
        });
    }
}

/// <summary>
/// BROWSERFILTER1: the live game behind <see cref="IServerTagGame"/>. Checked with the IL viewer against the server's
/// interop (2026-10-08): <c>ServerBootstrapSystem._GameTags</c> (<c>List&lt;string&gt;</c> field),
/// <c>SetServerGameTag(string)</c> and <c>RemoveServerGameTag(string)</c> (private in the game, exposed by the interop).
/// The native bodies: <c>SetServerGameTag</c> ignores a blank or duplicate tag, adds it and sets <c>_GameTagsModified</c>;
/// <c>UpdatePlatformData</c> then rebuilds the joined string and sends it to Steam (<c>SetGameTags</c>, every second) and
/// EOS (the <c>TAGS</c> attribute). Plain strings and a <c>List&lt;string&gt;</c> the game already uses: no by-ref
/// struct, no new generic instantiation. Main thread only.
/// </summary>
internal sealed class ServerTagGame : IServerTagGame
{
    private static ProjectM.ServerBootstrapSystem? System()
    {
        try
        {
            var world = Worlds.Server;
            return world != null && world.IsCreated ? world.GetExistingSystemManaged<ProjectM.ServerBootstrapSystem>() : null;
        }
        catch { return null; }
    }

    public bool Ready
    {
        get
        {
            var system = System();
            return system != null && system._GameTags != null;
        }
    }

    public IReadOnlyList<string>? Tags
    {
        get
        {
            var list = System()?._GameTags;
            if (list == null) return null;
            var copy = new List<string>(list.Count);
            for (int i = 0; i < list.Count; i++) copy.Add(list[i] ?? "");
            return copy;
        }
    }

    public void Add(string tag) =>
        (System() ?? throw new InvalidOperationException("ServerBootstrapSystem is not running")).SetServerGameTag(tag);

    public void Remove(string tag) =>
        (System() ?? throw new InvalidOperationException("ServerBootstrapSystem is not running")).RemoveServerGameTag(tag);
}
#endif
