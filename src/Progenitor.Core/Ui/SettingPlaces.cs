using System;
using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>
/// Settings organization stage 2 (docs/settings-organization.md, "Overlap: one stored value, many places"): one
/// drawn setting of Progenitor's own, where it is at home and where else it is drawn. A mirror never holds a copy:
/// every place reads and writes the same store (<see cref="Store"/>).
/// </summary>
internal sealed class SettingPlace
{
    internal SettingPlace(string settingId, string home, IReadOnlyList<string>? alsoAt, string store, string source, string hidden = "")
    {
        Hidden = hidden ?? "";
        SettingId = settingId ?? "";
        Home = home ?? "";
        AlsoAt = alsoAt ?? Array.Empty<string>();
        Store = store ?? SettingPlaces.StoreCfg;
        Source = source ?? "";
    }

    /// <summary><c>&lt;pluginId&gt;/&lt;Section&gt;/&lt;Key&gt;</c> for a cfg row (as <see cref="SettingsRow.RowId"/>), <c>progenitor/JoinAdmin/&lt;op&gt;</c> for the admin channel.</summary>
    internal string SettingId { get; }

    /// <summary>Where the setting is at home (a place word; <see cref="SettingPlaces.CfgOnly"/> when it is not drawn).</summary>
    internal string Home { get; }

    /// <summary>The mirrors: other places the same stored value is drawn.</summary>
    internal IReadOnlyList<string> AlsoAt { get; }

    /// <summary><see cref="SettingPlaces.StoreCfg"/> (a <c>ConfigEntry</c>) or <see cref="SettingPlaces.StoreJoinAdmin"/> (the server's state over <c>'PrJa'</c>).</summary>
    internal string Store { get; }

    /// <summary>The file and key (or server state) the value lives in, for the readback and the docs.</summary>
    internal string Source { get; }

    /// <summary>
    /// A setting that used to be drawn and was moved to the cfg file only (its home is <see cref="SettingPlaces.CfgOnly"/>):
    /// the date and the reason, for the readback. Empty for every other setting. Its key, default and behavior are unchanged.
    /// </summary>
    internal string Hidden { get; }

    /// <summary>The home, then the mirrors.</summary>
    internal IReadOnlyList<string> Places
    {
        get
        {
            var list = new List<string>(1 + AlsoAt.Count) { Home };
            foreach (var p in AlsoAt) if (!list.Contains(p)) list.Add(p);
            return list;
        }
    }

    /// <summary>True when the setting is drawn at <paramref name="place"/> (the home or a mirror).</summary>
    internal bool IsAt(string place)
    {
        if (string.Equals(Home, place, StringComparison.Ordinal)) return true;
        foreach (var p in AlsoAt) if (string.Equals(p, place, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>True when the setting is drawn anywhere on Progenitor's page (the home or a mirror).</summary>
    internal bool OnProgenitorPage
    {
        get
        {
            if (Home.StartsWith(SettingPlaces.PagePrefix, StringComparison.Ordinal)) return true;
            foreach (var p in AlsoAt) if (p.StartsWith(SettingPlaces.PagePrefix, StringComparison.Ordinal)) return true;
            return false;
        }
    }
}

/// <summary>
/// Settings organization stage 2: the table of Progenitor's own settings — each one's home and mirrors — that the
/// Configuration tab, Progenitor's page and the readback (<c>settingId</c>, <c>home</c>, <c>alsoAt</c>, the
/// <c>mods_ui settings_places</c> op) all ask. Game-free. A mod author's setting is never in the table: its home is
/// its own page (<see cref="Resolve"/>).
/// </summary>
internal static class SettingPlaces
{
    internal const string PluginId = "progenitor";

    // ---- places -----------------------------------------------------------------------------------

    /// <summary>Three homes (2026-10-06): the Preferences tab (renamed from Configuration).</summary>
    internal const string Preferences = "preferences";
    internal const string InstallToolbar = "install-toolbar";
    internal const string KeybindsTab = "keybinds-tab";
    internal const string PlayersJoiningPanel = "players-joining-panel";

    /// <summary>Not drawn anywhere: the setting stays in its cfg file (a secret, a test hook, an operator value).</summary>
    internal const string CfgOnly = "cfg";

    internal const string PagePrefix = "progenitor-page/";
    internal const string ModPagePrefix = "mod-page/";

    /// <summary>Three homes (2026-10-06): the Server tab's groups.</summary>
    internal const string ServerTabPrefix = "server-tab/";

    // Progenitor's page, Your settings (three homes: Gameplay split into Photo mode and Build menu).
    internal const string Keys = PagePrefix + "keys";
    internal const string PhotoMode = PagePrefix + "photo-mode";
    internal const string BuildMenu = PagePrefix + "build-menu";
    internal const string Equipment = PagePrefix + "equipment";
    internal const string Chat = PagePrefix + "chat";
    internal const string PrivateGames = PagePrefix + "private-games";
    internal const string GetHelp = PagePrefix + "get-help";

    /// <summary>Three homes: Progenitor's own two server toggles, on its page.</summary>
    internal const string OnThisServer = PagePrefix + "on-this-server";

    // The Server tab's groups.
    internal const string WhenPlayersJoin = ServerTabPrefix + "when-players-join";
    internal const string ModsPlayersNeed = ServerTabPrefix + "mods-players-need";
    internal const string ServerList = ServerTabPrefix + "server-list";
    internal const string PlayersWithoutProgenitor = ServerTabPrefix + "players-without-progenitor";
    internal const string Operator = ServerTabPrefix + "operator";

    /// <summary>Settings organization stage 6: the vanilla server settings' group.</summary>
    internal const string ServerRulesPlace = ServerTabPrefix + "server-rules";

    /// <summary>CHAT4: the Server tab's Chat group.</summary>
    internal const string ChatServer = ServerTabPrefix + "chat";

    /// <summary>AUTOUPDATE3: the Server tab's Updates group (the server's own mod updates).</summary>
    internal const string UpdatesServer = ServerTabPrefix + "updates";

    /// <summary>A Progenitor setting that has no table row (a new ui.cfg key): drawn under General on Progenitor's page.</summary>
    internal const string ProgenitorGeneral = PagePrefix + "general";

    // ---- stores -----------------------------------------------------------------------------------

    internal const string StoreCfg = "cfg";
    internal const string StoreJoinAdmin = "join-admin";

    /// <summary>Stage 6: the vanilla server settings, read over <c>'PrSv'</c> from the server's own files.</summary>
    internal const string StoreServerRules = "server-rules";

    // ---- files (the Source column) ----------------------------------------------------------------

    private const string Shared = "progenitor.cfg";
    private const string UiFile = "progenitor.ui.cfg";
    private const string MessageFile = "progenitor.servermessage.cfg";
    private const string ListFile = "progenitor.serverlist.cfg";
    private const string JoinAdminFile = "progenitor-join-admin.json";
    private const string ServerChatFile = "progenitor.serverchat.cfg";

    /// <summary>A cfg setting id: <c>progenitor/&lt;Section&gt;/&lt;Key&gt;</c>.</summary>
    internal static string Id(string section, string key) => PluginId + "/" + section + "/" + key;

    /// <summary>An admin-channel setting id: <c>progenitor/JoinAdmin/&lt;op&gt;</c> (the audit row).</summary>
    internal static string JoinAdminId(string op) => PluginId + "/JoinAdmin/" + op;

    // Admin-channel operations drawn as rows (the wire words, Networking.JoinAdminOps / ServerOptions).
    internal const string OpMidSession = "mid-session";

    /// <summary>Mod compatibility C3: Version matching and its per-mod rules (one setting id; the lock list names it).</summary>
    internal const string OpVersionMatching = "version-matching";
    internal const string OpUnverified = "unverified";
    internal const string OpWindow = "window";
    internal const string OpPolicyMode = "policy-mode";
    internal const string OpAnswerTimeout = "answer-timeout";

    /// <summary>KEEPOUT1: Players without Progenitor (Let them play / Keep them out).</summary>
    internal const string OpRequireProgenitor = "require-progenitor";
    internal const string OpPhotoMode = "photo-mode";
    internal const string OpAllySafeHits = "ally-safe-hits";
    internal const string OpContainerPreview = "container-preview";
    internal const string OpRemoteInstall = "remote-install";
    internal const string OpAllowedSteamIds = "allowed-steam-ids";
    internal const string OpDisconnectGrace = "disconnect-grace";
    internal const string OpNoticeSeconds = "notice-seconds";
    internal const string OpNoticeRepeatSeconds = "notice-repeat-seconds";
    internal const string OpNoticeCreationLimitSeconds = "notice-creation-limit-seconds";

    // The Players joining popup's lists: one setting id per list (the lock list names them; ServerSettingsLock).
    internal const string OpRecommended = "recommended";
    internal const string OpRequiredClient = "required-client";
    internal const string OpApproved = "approved";

    private static readonly Dictionary<string, SettingPlace> Table = BuildTable();

    /// <summary>Every row of the table, in the inventory's order.</summary>
    internal static IReadOnlyList<SettingPlace> All { get; } = BuildList();

    private static List<SettingPlace> BuildList()
    {
        var list = new List<SettingPlace>();
        foreach (var e in Entries()) list.Add(e);
        return list;
    }

    private static Dictionary<string, SettingPlace> BuildTable()
    {
        var map = new Dictionary<string, SettingPlace>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in Entries()) map[e.SettingId] = e;
        return map;
    }

    private static SettingPlace Cfg(string section, string key, string home, string file, params string[] alsoAt) =>
        new(Id(section, key), home, alsoAt, StoreCfg, file + " [" + section + "] " + key);

    /// <summary>
    /// Decided 2026-10-06 ("No user will care to change those"): a setting no menu draws any more. It stays in its cfg
    /// file with the same key, default and behavior; <c>settings_places</c> lists it as cfg-only.
    /// </summary>
    private static SettingPlace Hidden(string section, string key, string file, string reason = HiddenReason) =>
        new(Id(section, key), CfgOnly, null, StoreCfg, file + " [" + section + "] " + key, reason);

    /// <summary>The readback's <c>hidden</c> words for every setting <see cref="Hidden"/> moved to the file.</summary>
    internal const string HiddenReason = "2026-10-06: moved to the cfg file only (\"No user will care to change those\")";

    /// <summary>
    /// RESETWIPE1: the readback's <c>hidden</c> words for the four countdown settings that left the menus. The countdown
    /// follows the game's wipe schedule, its precision is automatic and the passed text is <c>now</c>; a saved
    /// ResetRepeat and ResetEvery still roll the Next reset date forward while the game schedules no wipes.
    /// </summary>
    internal static string[] CountdownHiddenKeys => new[] { "ResetRepeat", "ResetEvery", "ResetPrecision", "ResetPassedText" };

    /// <inheritdoc cref="CountdownHiddenKeys"/>
    internal const string CountdownHiddenReason =
        "2026-10-06: moved to the cfg file only (the {reset} countdown follows the game's wipe schedule, picks its own precision and reads \"now\" once passed)";

    private static SettingPlace Admin(string op, string home, string source, params string[] alsoAt) =>
        new(JoinAdminId(op), home, alsoAt, StoreJoinAdmin, source);

    private static SettingPlace Rule(string key, string home, string file) =>
        new(PluginId + "/ServerRules/" + key, home, null, StoreServerRules, "the server's " + file + " " + key);

    private static IEnumerable<SettingPlace> Entries()
    {
        // Mod-manager preferences: the Preferences tab.
        yield return Cfg("Mods", "InstallView", Preferences, Shared, InstallToolbar);
        yield return Cfg("Settings", "EditOtherModsSettings", Preferences, UiFile);
        yield return Cfg("Mods", "RememberInstallFilters", Preferences, Shared);
        yield return Cfg("Mods", "UseThunderstore", Preferences, Shared);
        yield return Cfg("Mods", "ShowNsfwContent", Preferences, Shared);
        yield return Cfg("Install", "MaxDownloadMB", Preferences, Shared);
        yield return Cfg("Mods", "IndexPath", Preferences, Shared);
        yield return Cfg("Workspace", "CanonicalInGameFrame", Preferences, Shared);
        yield return Cfg("Mods", "DedicatedServerPath", Preferences, Shared);
        // AUTOUPDATE1 (2026-10-09): Preferences · Updates.
        yield return Cfg("AutoUpdate", "Mods", Preferences, Shared);
        yield return Cfg("AutoUpdate", "PromptBeforeJoin", Preferences, Shared);
        yield return Cfg("AutoUpdate", "MajorVersions", Preferences, Shared);
        // AUTOUPDATE5: Update Progenitor (this PC's Progenitor; a server's is its own cfg).
        yield return Cfg("AutoUpdate", "Progenitor", Preferences, Shared);
        // BROWSERFILTER1: also drawn as the Join Game browser's own Show Only checkbox (not a Progenitor page row).
        yield return Cfg("ServerBrowser", "ProgenitorOnly", Preferences, UiFile);

        // Hidden on 2026-10-06 (user: "No user will care to change those"): drawn no more, same keys, defaults and behavior.
        yield return Hidden("Workspace", "BackgroundIntensity", Shared);
        yield return Hidden("Workspace", "BackgroundContrast", Shared);
        yield return Hidden("Ui", "MenuHistoryInnerTabs", UiFile);
        yield return Hidden("Ui", "AdminAuthButton", Shared);
        yield return Hidden("Install", "CurrentUpdateTag", Shared);
        yield return Hidden("Ui", "CaptureMode", UiFile);
        yield return Hidden("Keybinds", "EnforceGameWins", UiFile);

        // Mod-manager values that stay in the file.
        yield return Cfg("Mods", "UseNexus", CfgOnly, Shared);
        yield return Cfg("Mods", "NexusApiKey", CfgOnly, Shared);
        yield return Cfg("Mods", "InstallFiltersRemembered", CfgOnly, Shared);
        yield return Cfg("Install", "LocalIndexPath", CfgOnly, Shared);
        yield return Cfg("Install", "DefaultTarget", CfgOnly, Shared);
        yield return Cfg("Install", "TreatLocalServerAsRemote", CfgOnly, Shared);
        // Experimental server switch for ctx.Inventory.Watch (change stamps); off until verified live.
        yield return Cfg("Inventory", "WatchUsesChangeStamps", CfgOnly, Shared);

        // Progenitor on this PC: Progenitor's page, Your settings (the Core keybinds follow the rule in Resolve).
        yield return Cfg("PhotoMode", "Enabled", PhotoMode, UiFile);
        yield return Cfg("PhotoMode", "HideHudOnEnter", PhotoMode, UiFile);
        yield return Cfg("PhotoMode", "HideNameplates", PhotoMode, UiFile);
        yield return Cfg("PhotoMode", "LookSensitivity", PhotoMode, UiFile);
        yield return Cfg("BuildSearch", "Enabled", BuildMenu, UiFile);
        yield return Cfg("BuildSearch", "ClearOnClose", BuildMenu, UiFile);
        yield return Cfg("BuildSearch", "Breadcrumbs", BuildMenu, UiFile);
        // DYEALL in Core (2026-10-08): the switch on the page. Covered pieces and the hold time are not settings.
        yield return Cfg("DyeAll", "Enabled", Equipment, UiFile);
        foreach (var key in new[] { "MatchPalettes", "LegacySettingsImported" })
            yield return Cfg("DyeAll", key, CfgOnly, UiFile);
        yield return Cfg("Chat", "VisibleSeconds", Chat, UiFile);
        yield return Cfg("Chat", "KeepVisible", Chat, UiFile);
        yield return Cfg("Chat", "StayWhileHovered", Chat, UiFile);
        yield return Cfg("Chat", "ClickSafe", Chat, UiFile);
        yield return Cfg("Chat", "ReplyShortcut", Chat, UiFile);
        yield return Cfg("Chat", "NameCompletion", Chat, UiFile);
        foreach (var key in new[] { "View", "Timestamps", "CollapseRepeats", "KeepHistory", "HistoryLines", "Mentions",
                     "MentionSound", "MentionWords", "FontSize", "BackgroundOpacity", "LockPosition", "Recall", "MutedPlayers", "LongMessages", "ChannelColor", "CommandHints", "ModSuggestions" })
            yield return Cfg("Chat", key, Chat, UiFile);
        // Set from the chat box itself (the tab strip, dragging, the quick menu), not a page row; TestNames (testing
        // only), ReplacedBy (another chat mod's compatibility list) and HistoryDefaultRaised (the 500 to 5000 upgrade
        // marker) are file-only.
        foreach (var key in new[] { "Tabs", "ActiveTab", "OffsetX", "OffsetY", "Width", "Height", "Colors", "TestNames", "ReplacedBy", "HistoryDefaultRaised" })
            yield return Cfg("Chat", key, CfgOnly, UiFile);
        yield return Cfg("Hosted", "Enabled", PrivateGames, Shared);
        yield return Cfg("Hosted", "DryRun", CfgOnly, Shared);
        yield return Cfg("Hosted", "ServerPluginsMode", CfgOnly, Shared);
        // SUPPORT1: where Get help > Collect logs saves its zip (empty: the Desktop).
        yield return Cfg("Support", "OutputFolder", GetHelp, UiFile);
        yield return Cfg("Support", "IncludeServerLogs", GetHelp, UiFile);
        // Three homes: which drawers are closed (the Mods window's own memory; never drawn).
        yield return Cfg("Ui", "ClosedDrawers", CfgOnly, UiFile);
        // P7b help: the term markers' and tip cards' memory (set by the markers, the cards and a mod's own setting; never drawn).
        foreach (var key in new[] { "HiddenTermMarkers", "TermMarkerHovers", "TermMarkerAutoHide", "SeenTips" })
            yield return Cfg("Ui", key, CfgOnly, UiFile);
        // Core debug tools (2026-10-06): the debug buff's map teleport. Developer values, never drawn.
        yield return Cfg("DebugMap", "HoldSeconds", CfgOnly, UiFile);
        yield return Cfg("DebugMap", "SearchRadius", CfgOnly, Shared);
        yield return Cfg("DebugMap", "SearchStep", CfgOnly, Shared);
        yield return Cfg("DebugMap", "WalkableCheck", CfgOnly, Shared);

        // Server-wide: the Server tab (three homes), except Progenitor's own two toggles (On this server, on its page).
        foreach (string key in new[] { "Message", "NextReset", "ShowMessage", "ShowModsPage" })
            yield return Cfg("ServerMessage", key, WhenPlayersJoin, MessageFile);
        // RESETWIPE1 (decided 2026-10-06): in no menu, same keys and file.
        foreach (string key in CountdownHiddenKeys)
            yield return Hidden("ServerMessage", key, MessageFile, CountdownHiddenReason);
        yield return Admin(OpRecommended, PlayersJoiningPanel, JoinAdminFile + " recommended");
        yield return Admin(OpRequiredClient, PlayersJoiningPanel, JoinAdminFile + " requiredClient");
        yield return Admin(OpPolicyMode, PlayersJoiningPanel, JoinAdminFile + " clientPolicy.mode");
        yield return Admin(OpApproved, PlayersJoiningPanel, JoinAdminFile + " clientPolicy.approved");
        yield return Admin(OpWindow, PlayersJoiningPanel, "the server's " + Shared + " [Networking] InstallGraceSeconds");
        yield return Admin(OpMidSession, PlayersJoiningPanel, JoinAdminFile + " clientPolicy.midSession", ModsPlayersNeed);
        // Mod compatibility C3: Version matching and the per-mod rules; the server's cfg can force the mode.
        yield return Admin(OpVersionMatching, PlayersJoiningPanel,
            JoinAdminFile + " versionMatching and serverModRules (the server's " + Shared + " [ClientPolicy] VersionMatching overrides the mode)",
            ModsPlayersNeed);
        // REQRULE5: Disconnect them's delay, on the Server tab only (Mods players need).
        yield return Admin(OpDisconnectGrace, ModsPlayersNeed, "the server's " + Shared + " [ClientPolicy] DisconnectGraceSeconds");
        yield return Cfg("ServerList", "Description", ServerList, ListFile);
        yield return Cfg("ServerList", "Signature", ServerList, ListFile);
        yield return Cfg("ServerList", "Published", ServerList, ListFile);
        // NODESCCHAT (2026-10-06): the operator's switch back to the game's description chat line. Never drawn.
        yield return Cfg("ServerList", "DescriptionInChat", CfgOnly, ListFile);
        yield return Cfg("ServerList", "BrowserTag", CfgOnly, ListFile);
        yield return Cfg("ServerMessage", "NoProgenitorMessage", PlayersWithoutProgenitor, MessageFile);
        yield return Admin(OpRequireProgenitor, PlayersWithoutProgenitor, "the server's " + Shared + " [Networking] RequireProgenitor");
        yield return Admin(OpAnswerTimeout, PlayersWithoutProgenitor, "the server's " + Shared + " [Networking] HandshakeAnswerTimeoutSeconds");
        yield return Admin(OpUnverified, PlayersJoiningPanel, JoinAdminFile + " clientPolicy.unverified", ModsPlayersNeed);
        yield return Admin(OpPhotoMode, OnThisServer, "the server's " + Shared + " [PhotoMode] AllowOnThisServer");
        yield return Admin(OpAllySafeHits, OnThisServer, "the server's " + Shared + " [Ai] AllySafeHits");
        yield return Admin(OpContainerPreview, OnThisServer, "the server's " + Shared + " [ContainerPreview] RemotePreview");
        yield return Admin(OpRemoteInstall, Operator, "the server's " + Shared + " [RemoteInstall] Enabled");
        yield return Admin(OpAllowedSteamIds, Operator, "the server's " + Shared + " [RemoteInstall] AllowedSteamIds");
        yield return Admin(OpNoticeSeconds, Operator, "the server's " + Shared + " [Networking] NoAnswerNoticeSeconds");
        yield return Admin(OpNoticeRepeatSeconds, Operator, "the server's " + Shared + " [Networking] NoAnswerNoticeRepeatSeconds");
        yield return Admin(OpNoticeCreationLimitSeconds, Operator, "the server's " + Shared + " [Networking] NoAnswerCreationLimitSeconds");

        // Stage 6: the vanilla server settings (Server name is a listing field, under Server list; decision 2).
        yield return Rule("Name", ServerList, "ServerHostSettings.json");
        foreach (string key in new[] { "GameModeType", "ClanSize", "GameDifficulty", "BloodBoundEquipment", "CastleHeartDamageMode" })
            yield return Rule(key, ServerRulesPlace, "ServerGameSettings.json");
        foreach (string key in new[] { "Password", "MaxConnectedUsers", "ResetDaysInterval", "DayOfReset" })
            yield return Rule(key, ServerRulesPlace, "ServerHostSettings.json");
        // WIPECONFIRM1: Next wipe sets ResetDaysInterval (and DayOfReset) from a date; it stores nothing of its own.
        yield return Rule("NextWipe", ServerRulesPlace, "ServerHostSettings.json ResetDaysInterval and DayOfReset");

        // CHAT4: the Server tab's Chat group. The allow switches and the length are synced to every player; the filter and
        // the relay are server-only values on the admin channel (the webhook address is never sent: it reads set or empty).
        foreach (string key in Networking.ServerChatOptions.SyncedKeys)
            yield return Cfg(Networking.ServerChatOptions.Section, key, ChatServer, ServerChatFile);
        foreach (string op in Networking.ServerChatOptions.AdminOps)
        {
            var spec = Networking.ServerOptions.Find(op);
            yield return Admin(op, ChatServer, "the server's " + Shared + " [" + spec!.Section + "] " + spec.Key);
        }

        // AUTOUPDATE3: the Server tab's Updates group: the server's [AutoUpdate] values on the admin channel, and its two
        // restart actions (lockable with the group). The rest of the server's [AutoUpdate] section is cfg-only below.
        foreach (string op in Networking.ServerOptionOps.UpdateOps)
        {
            var spec = Networking.ServerOptions.Find(op);
            yield return Admin(op, UpdatesServer, "the server's " + Shared + " [" + spec!.Section + "] " + spec.Key);
        }

        yield return Admin(Networking.JoinAdminOps.UpdateCheck, UpdatesServer, "Check now (an action: one conditional index request)");
        yield return Admin(Networking.JoinAdminOps.UpdateRestart, UpdatesServer, "Restart now (an action: the server's save and quit)");
        yield return Admin(Networking.JoinAdminOps.UpdateCancelRestart, UpdatesServer, "Cancel restart (an action)");
        foreach (string key in new[]
                 {
                     Install.ServerUpdateSettings.CheckHoursKey, Install.ServerUpdateSettings.RestartWhenEmptyKey, Install.ServerUpdateSettings.EmptyMinutesKey,
                     Install.ServerUpdateSettings.BackupKey, Install.ServerUpdateSettings.BackupKeepKey, Install.ServerUpdateSettings.BackupSkipHoursKey,
                     Install.ServerUpdateSettings.WarnMinutesKey, Install.ServerUpdateSettings.ExcludeKey, Install.ServerUpdateSettings.PinsKey,
                     Install.ServerUpdateSettings.HostRestartsKey,
                 })
            yield return Cfg(Install.ServerUpdateSettings.Section, key, CfgOnly, Shared);

        // Server values reached only through the admin channel or the file (never drawn as the client's own cfg row).
        yield return Cfg("PhotoMode", "AllowOnThisServer", CfgOnly, Shared);
        yield return Cfg("Ai", "AllySafeHits", CfgOnly, Shared);
        yield return Cfg("ContainerPreview", "RemotePreview", CfgOnly, Shared);
        yield return Cfg("RemoteInstall", "Enabled", CfgOnly, Shared);
        yield return Cfg("RemoteInstall", "AllowedSteamIds", CfgOnly, Shared);
        // Deprecated (replaced by [ServerSettings] LockedInGame): false locks the list above. Never drawn.
        yield return Cfg("RemoteInstall", "AllowedSteamIdsInGame", CfgOnly, Shared);
        // The operator's lock list (requested 2026-10-05): never drawn, never editable in the game.
        yield return Cfg("ServerSettings", "LockedInGame", CfgOnly, Shared);
        yield return Cfg("Networking", "HandshakeAnswerTimeoutSeconds", CfgOnly, Shared);
        yield return Cfg("Networking", "RequireProgenitor", CfgOnly, Shared);
        yield return Cfg("Networking", "InstallGraceSeconds", CfgOnly, Shared);
        yield return Cfg("Networking", "NoAnswerNoticeSeconds", CfgOnly, Shared);
        yield return Cfg("Networking", "NoAnswerNoticeRepeatSeconds", CfgOnly, Shared);
        yield return Cfg("Networking", "NoAnswerCreationLimitSeconds", CfgOnly, Shared);
        yield return Cfg("ClientPolicy", "DisconnectGraceSeconds", CfgOnly, Shared);
        yield return Cfg("ClientPolicy", "VersionMatching", CfgOnly, Shared);
        // CHAT4: the server-only chat values behind the admin channel's rows (never drawn as the cfg row; the address is secret).
        foreach (string op in Networking.ServerChatOptions.AdminOps)
        {
            var spec = Networking.ServerOptions.Find(op);
            yield return Cfg(spec!.Section, spec.Key, CfgOnly, Shared);
        }
    }

    /// <summary>The table row for <paramref name="settingId"/>, or null.</summary>
    internal static SettingPlace? Find(string? settingId) =>
        !string.IsNullOrEmpty(settingId) && Table.TryGetValue(settingId!, out var place) ? place : null;

    /// <summary>
    /// Where a setting is drawn: its table row; else a Core keybind (home Your settings · Keys, mirror the
    /// Keybinds tab); else a mod's keybind (its page, mirror the Keybinds tab); else its own page. A Progenitor
    /// setting the table does not know is at home under General on Progenitor's page.
    /// </summary>
    internal static SettingPlace Resolve(string? settingId, string? pluginId, bool keybind)
    {
        string id = settingId ?? "";
        if (Find(id) is { } known) return known;
        string plugin = (pluginId ?? "").Trim();
        bool progenitor = string.Equals(plugin, PluginId, StringComparison.OrdinalIgnoreCase);
        if (keybind)
            return new SettingPlace(id, progenitor ? Keys : ModPagePrefix + plugin, new[] { KeybindsTab }, StoreCfg, "");
        return new SettingPlace(id, progenitor ? ProgenitorGeneral : ModPagePrefix + plugin, null, StoreCfg, "");
    }

    /// <summary>True when the table homes <paramref name="settingId"/> on the Preferences tab.</summary>
    internal static bool HomedOnPreferences(string? settingId) =>
        Find(settingId) is { } place && string.Equals(place.Home, Preferences, StringComparison.Ordinal);

    /// <summary>Three homes: true when <paramref name="place"/> is one of the Server tab's groups.</summary>
    internal static bool IsServerTab(string? place) =>
        place != null && place.StartsWith(ServerTabPrefix, StringComparison.Ordinal);

    /// <summary>
    /// Three homes: the top tab a place is on — <c>preferences</c>, <c>server</c> (a Server-tab group, or the Players
    /// joining popup the Server tab opens), <c>mods</c> (Progenitor's page or a mod's page), <c>cfg</c> (not drawn), or
    /// empty for a mirror that is not a home (the Install toolbar, the Keybinds tab).
    /// </summary>
    internal static string HomeTab(string? place) =>
        place == null ? "" :
        string.Equals(place, Preferences, StringComparison.Ordinal) ? "preferences" :
        IsServerTab(place) || string.Equals(place, PlayersJoiningPanel, StringComparison.Ordinal) ? "server" :
        place.StartsWith(PagePrefix, StringComparison.Ordinal) || place.StartsWith(ModPagePrefix, StringComparison.Ordinal) ? "mods" :
        string.Equals(place, CfgOnly, StringComparison.Ordinal) ? "cfg" : "";

    /// <summary>
    /// Stage 3: whether Progenitor's page draws a catalog row of its own: a row the table places on the page, or one
    /// the table does not know that comes from Progenitor's own file (a new ui.cfg key under General). A row homed on
    /// the Configuration tab, a cfg-only row, and every row of Bootstrap's shared file the table does not place on
    /// the page are left off.
    /// </summary>
    internal static bool DrawnOnProgenitorPage(string? settingId, bool fromSharedFile)
    {
        if (Find(settingId) is { } place) return place.OnProgenitorPage;
        return !fromSharedFile;
    }

    /// <summary>The readback keys every settings row carries: <c>settingId</c>, <c>home</c>, <c>alsoAt</c>.</summary>
    internal static void AddReadback(Dictionary<string, object?> d, string? settingId, string? pluginId, bool keybind)
    {
        if (d == null) return;
        var place = Resolve(settingId, pluginId, keybind);
        d["settingId"] = place.SettingId;
        d["home"] = place.Home;
        d["alsoAt"] = new List<string>(place.AlsoAt);
    }

    /// <summary>
    /// The <c>mods_ui settings_places</c> answer: <c>{settingId: {home, places[], value, display, store, source}}</c>
    /// for every placed setting, each value read from its store once, now. Cfg-only rows are left out (secrets among
    /// them), except the ones hidden by the 2026-10-06 decision: those are listed with <c>home cfg</c>, <c>places []</c>
    /// and <c>hidden</c> (the date and the reason).
    /// <paramref name="read"/> answers a setting's <c>(value, display)</c>, or null when the store has no value here
    /// (an admin-channel value before the server sent its state, an entry not bound in this process).
    /// </summary>
    internal static Dictionary<string, object?> Readback(Func<SettingPlace, (string Value, string Display)?> read)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var place in All)
        {
            bool cfgOnly = string.Equals(place.Home, CfgOnly, StringComparison.Ordinal);
            if (cfgOnly && place.Hidden.Length == 0) continue;
            (string Value, string Display)? value = null;
            try { value = read?.Invoke(place); }
            catch { value = null; }
            var entry = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["home"] = place.Home,
                ["places"] = cfgOnly ? new List<string>() : new List<string>(place.Places),
                ["value"] = value?.Value,
                ["display"] = value?.Display,
                ["known"] = value != null,
                ["store"] = place.Store,
                ["source"] = place.Source,
            };
            if (cfgOnly)
            {
                entry["cfgOnly"] = true;
                entry["hidden"] = place.Hidden;
            }

            result[place.SettingId] = entry;
        }

        return result;
    }

    /// <summary>A bool or other stored text as a person reads it: <c>on</c> / <c>off</c> for a switch, else the text.</summary>
    internal static string Display(string? value)
    {
        string v = (value ?? "").Trim();
        if (string.Equals(v, "true", StringComparison.OrdinalIgnoreCase)) return "on";
        if (string.Equals(v, "false", StringComparison.OrdinalIgnoreCase)) return "off";
        return v;
    }
}
