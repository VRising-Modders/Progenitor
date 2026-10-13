using System;
using System.Collections.Generic;
using System.Globalization;
#if !PROGENITOR_TESTS
using System.IO;
using BepInEx.Configuration;
using Progenitor.Install;
using Progenitor.Ui;
#endif

namespace Progenitor.Networking;

/// <summary>
/// JOINFLOW1: the server message and the reset notification — Progenitor's own server-wide, admin-editable
/// settings, on Progenitor's page in the Mods menu (Server settings, "When players join").
///
/// <para><b>Where they live.</b> Their own file, <c>BepInEx/config/progenitor.servermessage.cfg</c>,
/// not the shared <c>progenitor.cfg</c>: ConfigSync holds a file's saving off while it has a
/// server value locked in it, and these rows are locked on every client for the whole join. They are
/// ConfigSync rows under <see cref="ConfigSync.FrameworkSettingsId"/> (sent to every client that answered the
/// handshake) and opted into administrator edits (<see cref="ServerSettings"/>), so an admin edits them
/// exactly like a mod's server-wide setting: the text-area message with its expanded editor and the
/// server-side limits (TEXTLIMIT1), the date-time picker for the next reset, and choices for the rest.</para>
///
/// <para><b>What reaches players.</b> Not ConfigSync: a refused player never receives a config snapshot.
/// The server builds a <see cref="JoinSection"/> from these rows (plus the operator overlay's
/// <c>recommended</c> list and its time zone) and signs it into the catalog envelope, which reaches both a
/// refused and an accepted join.</para>
///
/// <para><b>A notification, not a reset.</b> Nothing resets anything when the countdown ends.</para>
/// </summary>
internal static class JoinServerSettings
{
    public const string FileName = "progenitor.servermessage.cfg";
    public const string Section = "ServerMessage";
    public const string MessageKey = "Message";
    public const string NextResetKey = "NextReset";
    public const string RepeatKey = "ResetRepeat";
    public const string EveryKey = "ResetEvery";
    public const string PrecisionKey = "ResetPrecision";
    public const string PassedKey = "ResetPassedText";
    public const string MessageFrequencyKey = "ShowMessage";
    public const string ModsFrequencyKey = "ShowModsPage";

    /// <summary>NOPROGNOTICE: the admins' chat notice to a player without Progenitor (empty: the default text).</summary>
    public const string NoProgenitorMessageKey = "NoProgenitorMessage";

    /// <summary>The notice's template as the server reads it now (live: the row; tests set it). Empty: the default.</summary>
    internal static Func<string>? NoProgenitorTemplateReader { get; set; }

    /// <summary>The admins' notice template, or empty for the default.</summary>
    internal static string NoProgenitorTemplate()
    {
        try { return NoProgenitorTemplateReader?.Invoke() ?? ""; }
        catch { return ""; }
    }

    /// <summary>The settings page section title (settings organization stage 5: the first Server settings subheading).</summary>
    public const string PageSection = Progenitor.Ui.ProgenitorPage.WhenPlayersJoinTitle;

    /// <summary>JOINADMIN1: the Server settings row that opens the admin's Players joining popup.</summary>
    public const string PlayersJoiningLabel = "Players joining this server";

    /// <inheritdoc cref="PlayersJoiningLabel"/>
    public const string PlayersJoiningButton = "Open";

    public static readonly IReadOnlyList<string> MessageFrequencyChoices = new[] { "WhenChanged", "Always", "FirstJoin" };
    public static readonly IReadOnlyList<string> MessageFrequencyLabels = new[] { "When it changes or after a reset", "Every join", "First join only" };
    public static readonly IReadOnlyList<string> ModsFrequencyChoices = new[] { "WhenNeeded", "Always" };
    public static readonly IReadOnlyList<string> ModsFrequencyLabels = new[] { "When needed", "Every join" };

    // OPTIONHOVER1: each option's explanation, drawn under its radio line and shown in the details pane while it is hovered.
    // Kept to one short line each (the radio list draws one line). RESETWIPE1 removed Repeat and Countdown precision from the menus.
    public static readonly string[] MessageFrequencyDescriptions =
    {
        "The first join, when the text changes, and the first join after each reset.",
        "Every join; players can't dismiss it.",
        "Once, then again only when the text changes.",
    };

    public static readonly string[] ModsFrequencyDescriptions =
    {
        "A required mod needs attention, the mod lists changed since the last visit, or a first join where mods are recommended.",
        "Every join.",
    };

    /// <summary>The row id ServerSettings and the bridge use: <c>progenitor/ServerMessage/&lt;key&gt;</c>.</summary>
    public static string RowId(string key) => ConfigSync.FrameworkSettingsId + "/" + Section + "/" + key;

    /// <summary>
    /// The section to sign, from the stored values (game-free). Unknown repeat or precision words fall back
    /// to None and Minute; the message is cut to <see cref="JoinSectionLimits.MaxMessageChars"/>; the
    /// recommended rows are cleaned to their caps.
    ///
    /// <para>RESETWIPE1 (decided 2026-10-06): when the game schedules wipes (<paramref name="wipe"/> on), the reset is
    /// the next wipe (00:00 of its date in the server's zone, one time) and the stored Next reset, Repeat and Repeat every
    /// are not used. Otherwise the stored Next reset is the one-off date, and a Repeat and Repeat every saved before the
    /// rows left the menus still roll it forward (the fallback). The passed text is always <c>now</c>
    /// (<see cref="ResetCountdown.DefaultPassedText"/>). The stored precision still travels, for clients older than
    /// RESETWIPE1; newer ones choose it automatically (<see cref="ResetCountdown.AutoPrecision"/>).</para>
    /// </summary>
    public static JoinSection BuildSection(
        string? message, string? nextUtc, string? repeat, int every, string? precision, string? passed,
        IReadOnlyList<RecommendedMod>? recommended, TimeZoneInfo? zone, DateTime nowUtc, List<string>? skipped = null,
        string? showMessage = null, string? showMods = null, GameWipeSchedule? wipe = null)
    {
        if (!Enum.TryParse((showMessage ?? "").Trim(), ignoreCase: true, out MessageFrequency messageFrequency)) messageFrequency = MessageFrequency.WhenChanged;
        if (!Enum.TryParse((showMods ?? "").Trim(), ignoreCase: true, out ModsPageFrequency modsFrequency)) modsFrequency = ModsPageFrequency.WhenNeeded;
        string text = (message ?? "").Replace("\r\n", "\n");
        if (text.Length > JoinSectionLimits.MaxMessageChars) text = text.Substring(0, JoinSectionLimits.MaxMessageChars);
        if (!Enum.TryParse((repeat ?? "").Trim(), ignoreCase: true, out ResetRepeat r)) r = ResetRepeat.None;
        if (!Enum.TryParse((precision ?? "").Trim(), ignoreCase: true, out ResetPrecision p)) p = ResetPrecision.Minute;
        // RESETWIPE1: After the reset is no longer a setting; a saved text is ignored.
        _ = passed;
        zone ??= TimeZoneInfo.Utc;
        string next = nextUtc ?? "";
        if (wipe is { On: true })
        {
            next = wipe.NextUtc(zone);
            r = ResetRepeat.None;
            every = 1;
        }

        var schedule = new ResetSchedule(next, r, every, p, ResetCountdown.DefaultPassedText);
        int offset;
        try { offset = (int)Math.Round(zone.GetUtcOffset(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc)).TotalMinutes); }
        catch { offset = (int)zone.BaseUtcOffset.TotalMinutes; }
        string id = zone.Id ?? "";
        if (id.Length > JoinSectionLimits.MaxZoneIdChars) id = "";
        return new JoinSection(text, schedule, JoinSectionJson.Clean(recommended, skipped), new JoinServerZone(id, offset),
            showMessage: messageFrequency, showMods: modsFrequency);
    }

    /// <summary>RESETWIPE1: the Next reset row's label (the date picker, or the read-only wipe date).</summary>
    public const string NextResetLabel = "Next reset";

    /// <summary>WIPECONFIRM1: the date picker's label, shown only while the game schedules no wipes.</summary>
    public const string NextResetByHandLabel = "Next reset (you wipe by hand)";

    /// <summary>RESETWIPE1: the date picker's help, while the game schedules no wipes.</summary>
    public const string NextResetHelp =
        "When {reset} counts down to: a one-off notification, nothing is reset when it passes. Shown in the server's time. "
        + "With scheduled wipes on (Server rules), the countdown follows them instead.";

    /// <summary>RESETWIPE1: the read-only row's help, while the game schedules wipes.</summary>
    public const string NextResetFromWipesHelp =
        "The next wipe of the server rules' Scheduled wipes every (days) and Wipe on: {reset} counts down to 00:00 of that day "
        + "in the server's time. The game wipes at the first server start on or after it. Change it in Server rules.";

    /// <summary>
    /// RESETWIPE1: the game's wipe schedule as this side knows it (the server: its own rules, read at most every
    /// <see cref="WipeCacheSeconds"/>; an admin's client: the joined server's Server rules state). Null when unknown:
    /// the stored Next reset applies. Tests set their own.
    /// </summary>
    internal static Func<GameWipeSchedule?>? GameWipeProbe { get; set; }

    internal const double WipeCacheSeconds = 30;

    /// <summary>RESETWIPE1: <see cref="GameWipeProbe"/>, never throwing.</summary>
    internal static GameWipeSchedule? GameWipe()
    {
        try { return GameWipeProbe?.Invoke(); }
        catch { return null; }
    }

    /// <summary>The rendered message for a preview, in the player's (this machine's) zone.</summary>
    public static string Preview(JoinSection? section, DateTime nowUtc, TimeZoneInfo? playerZone) =>
        section == null ? "" : ResetCountdown.Render(section.Message, section.Reset, nowUtc, section.Zone.Resolve(), playerZone ?? TimeZoneInfo.Local);

    // ---- JOINADMIN1: the "Players joining this server" row (shared with the tests) ----------------------

    /// <summary>
    /// The facts the row's rule reads: the hello said the server has the panel, the hello's admin flag, and the
    /// server's last remote-install status (null when none arrived). Set on a client by <c>Install</c> from
    /// <c>JoinAdminClient</c> and <c>RemoteInstallClient</c>; tests set their own. Null: hidden.
    /// </summary>
    internal static Func<(bool Supports, bool HelloAdmin, Progenitor.Install.RemoteInstallStatus? Status)>? PlayersJoiningInputsProbe { get; set; }

    /// <summary>Asks the server for a fresh remote-install status (rate-limited by the caller's client).</summary>
    internal static Func<bool>? PlayersJoiningStatusAsk { get; set; }

    private static string _lastConsidered = "";

    /// <summary>The help line (search matches a button by its caption and help, so it names the popup).</summary>
    public const string PlayersJoiningHelp =
        "Admins: who can join — recommended client mods, mods shared with refused players, the install window "
        + "and the refused-join alert. Opens the Players joining popup.";

    /// <summary>
    /// Add the row to Progenitor's page, in the Server settings scope, exactly as the runtime page does:
    /// <c>Open</c> runs <paramref name="open"/>; it shows while <see cref="PlayersJoiningShown"/> says so.
    /// </summary>
    internal static void AddPlayersJoiningRow(Progenitor.Ui.SettingsPageBuilder page, Action open)
    {
        if (page == null) throw new ArgumentNullException(nameof(page));
        page.Button(PlayersJoiningButton, open, label: PlayersJoiningLabel, help: PlayersJoiningHelp, showIf: PlayersJoiningShown)
            .InScope(Progenitor.Ui.SettingScope.ServerWide);
    }

    /// <summary>
    /// The row's show-if: the former toolbar button's rule (<see cref="JoinAdminOffer.Offered"/>, which
    /// <c>JoinAdminClient.IsAdmin</c> applies) over the same inputs. Logs the decision and its inputs the first
    /// time the row is considered and whenever they change. While the server has the panel but the rule says no,
    /// it asks for a fresh remote-install status (the Install tab used to keep it fresh while open).
    /// </summary>
    internal static bool PlayersJoiningShown()
    {
        var probe = PlayersJoiningInputsProbe;
        (bool Supports, bool HelloAdmin, Progenitor.Install.RemoteInstallStatus? Status) inputs = (false, false, null);
        bool probed = probe != null;
        if (probe != null)
        {
            try { inputs = probe(); }
            catch { probed = false; }
        }

        bool shown = JoinAdminOffer.Offered(inputs.Supports, inputs.HelloAdmin, inputs.Status);
        string considered = "shown " + shown + " (serverSupports " + inputs.Supports + ", helloAdmin " + inputs.HelloAdmin
            + ", remote-install status " + (inputs.Status == null ? "none" : inputs.Status.Authorized ? "authorized" : "not authorized")
            + (probed ? "" : ", no client inputs") + ")";
        if (!string.Equals(considered, _lastConsidered, StringComparison.Ordinal))
        {
            _lastConsidered = considered;
            Plugin.Logger?.LogInfo("JoinAdmin: Players joining row considered: " + considered + ".");
        }

        if (!shown && inputs.Supports)
        {
            try { PlayersJoiningStatusAsk?.Invoke(); }
            catch { /* the next evaluation asks again */ }
        }

        return shown;
    }

    /// <summary>Tests: forget the last logged decision.</summary>
    internal static void ResetPlayersJoiningForTests()
    {
        PlayersJoiningInputsProbe = null;
        PlayersJoiningStatusAsk = null;
        _lastConsidered = "";
    }

#if PROGENITOR_TESTS
    /// <summary>Tests: the section the server would build (null: none).</summary>
    internal static JoinSection? TestContent;

    internal static JoinSection? Content(ManifestOverrides? overrides)
    {
        var content = TestContent;
        // JOINADMIN1: the admin's recommended rows lead, the section's own (standing in for the overlay) follow.
        if (content == null || JoinAdminServer.Config.Recommended.Count == 0) return content;
        return new JoinSection(content.Message, content.Reset, JoinAdminServer.MergedRecommended(content.Recommended), content.Zone,
            content.CatalogIssued, content.CatalogFp, content.Schema, content.ShowMessage, content.ShowMods, content.Names, content.Versions);
    }

    internal static void Install() { }

    internal static void Uninstall() => TestContent = null;
#else
    private static readonly object Owner = new();
    private static bool _installed;
    private static ConfigFile? _file;
    private static ConfigEntry<string>? _message;
    private static ConfigEntry<string>? _next;
    private static ConfigEntry<string>? _repeat;
    private static ConfigEntry<int>? _every;
    private static ConfigEntry<string>? _precision;
    private static ConfigEntry<string>? _passed;
    private static ConfigEntry<string>? _showMessage;
    private static ConfigEntry<string>? _showMods;
    private static ConfigEntry<string>? _noProgenitor;
    private static readonly List<IDisposable> Registrations = new();
    private static readonly HashSet<string> LoggedSkips = new(StringComparer.Ordinal);

    /// <summary>
    /// Bind the rows, register them with ConfigSync and for administrator edits, and register Progenitor's
    /// settings page. Both sides (the server enforces the text limits from its own page declaration).
    /// Idempotent; <c>ModManifest.Install</c> calls it.
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
            _file = new ConfigFile(Path.Combine(dir, FileName), saveOnInit: true);
            _message = Config.ConfigHelper.Bind(_file, Section, MessageKey, "",
                "What a joining player sees first: plain text, line breaks kept, at most 2,000 characters. Tokens: "
                + "{reset} (the live countdown), {reset:date} (the reset in the player's time zone), {reset:relative} "
                + "(in 3 days / in 5 hours / today). Empty shows no message page.");
            _next = Config.ConfigHelper.Bind(_file, Section, NextResetKey, "",
                "The next reset as ISO 8601 UTC (2026-10-10T18:00:00Z), or empty for none. A notification only: "
                + "nothing is reset when it passes. Not used while the game schedules wipes (ServerHostSettings "
                + "ResetDaysInterval above 0): {reset} then counts down to the next scheduled wipe.");
            // RESETWIPE1 (2026-10-06): the next four are in no menu any more; they stay here so a saved file keeps working.
            _repeat = Config.ConfigHelper.Bind(_file, Section, RepeatKey, "None",
                "Not in any menu. None, Days or Weeks: while the game schedules no wipes, NextReset rolls forward by ResetEvery "
                + "days or weeks after it passes.");
            _every = Config.ConfigHelper.Bind(_file, Section, EveryKey, 1,
                "Not in any menu. How many days or weeks one ResetRepeat step is (1 to 365).");
            _precision = Config.ConfigHelper.Bind(_file, Section, PrecisionKey, "Minute",
                "Not in any menu and no longer used here: {reset} picks its precision itself (days, then hours, then minutes "
                + "and seconds in the last hour). Sent to clients older than that change.");
            _passed = Config.ConfigHelper.Bind(_file, Section, PassedKey, ResetCountdown.DefaultPassedText,
                "Not in any menu and no longer used: {reset} reads \"now\" once the reset has passed.");
            _showMessage = Config.ConfigHelper.Bind(_file, Section, MessageFrequencyKey, "WhenChanged",
                "When the message page shows on a join: WhenChanged (the first join, when the text changes, and the first "
                + "join after each reset passes), Always (every join; players cannot dismiss it) or FirstJoin (once, then "
                + "only when the text changes).");
            _showMods = Config.ConfigHelper.Bind(_file, Section, ModsFrequencyKey, "WhenNeeded",
                "When the mods page shows on a successful join: WhenNeeded (a required mod needs attention, or the "
                + "server's mod lists changed since the player last saw them) or Always.");
            _noProgenitor = Config.ConfigHelper.Bind(_file, Section, NoProgenitorMessageKey, "",
                "The chat message a player without Progenitor gets before this server disconnects them (plain text, at most 300 "
                + "characters). Tokens: {mods} (the mods this server needs, with versions) and {seconds} (seconds until the "
                + "disconnect). Empty sends the default: \"" + NoAnswerNoticeText.DefaultTemplate + "\"");
            var noProgenitor = _noProgenitor;
            NoProgenitorTemplateReader = () => noProgenitor?.Value ?? "";
            // RESETWIPE1: the game's wipe schedule the countdown follows (the server's own rules; a client's joined server's).
            GameWipeProbe = Plugin.IsServerProcess ? ServerWipeCached : () => GameWipeSchedule.FromRules(ServerRulesClient.State);
            // WIPECONFIRM1: the wipe confirmation reads the server's zone and its local now.
            ServerRulesRows.ZoneProbe = ServerZoneForPage;
            ServerRulesRows.Today = () => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, ServerZoneForPage());
            // TOKENINSERT1: the Insert buttons' examples and the Players see lines render with this side's settings.
            TextTokens.Context = new TextTokens.RenderContext
            {
                Reset = CurrentReset,
                ServerZone = ServerZoneForPage,
                PlayerZone = () => TimeZoneInfo.Local,
                ServerListSignature = ServerListDescriptionSettings.SignatureOn,
            };

            SettingsCatalog.Register(ConfigSync.FrameworkSettingsId, _file, Owner);
            foreach (var entry in Entries())
            {
                try { Registrations.Add(ConfigSync.Register(ConfigSync.FrameworkSettingsId, entry)); }
                catch (Exception e) { Plugin.Logger?.LogWarning("JoinFlow: ConfigSync could not register " + entry.Definition + ": " + e.Message); }
            }

            Registrations.Add(ModSettingsPages.Register(Owner, ConfigSync.FrameworkSettingsId, BuildPage));
            Plugin.Logger?.LogInfo("JoinFlow: Progenitor's mod page registered (Server settings: server message, Players joining).");
            // JOINADMIN1: the Players joining row follows the hello and the answers on an open page.
            if (!Plugin.IsServerProcess)
            {
                PlayersJoiningInputsProbe = () => (JoinAdminClient.ServerSupports, JoinAdminClient.HelloAdmin, RemoteInstallClient.Shared.Status);
                PlayersJoiningStatusAsk = () => RemoteInstallClient.Shared.RequestStatusIfDue(DateTime.UtcNow, authorizationBlocked: true);
                JoinAdminClient.Changed -= OnJoinAdminChanged;
                JoinAdminClient.Changed += OnJoinAdminChanged;
                // Settings organization stage 5: the admin-channel rows on Progenitor's page, and settings_places.
                JoinAdminRows.StateProbe = () => JoinAdminClient.State;
                JoinAdminRows.Requester = (request, answer) => JoinAdminClient.Request(request, out var refusal, answer) ? null : refusal;
                JoinAdminRows.SelfSteamId = AdminListFile.LocalSteamId;
                ModsMenu.JoinAdminValueReader = JoinAdminRows.ReadPlace;
                // The joined server's lock list ([ServerSettings] LockedInGame): its rows are drawn read-only.
                ServerSettingsLock.ClientProbe = JoinAdminRows.LockedBy;
                // Stage 6: the Server rules rows and their settings_places values.
                ServerRulesRows.StateProbe = () => ServerRulesClient.State;
                ServerRulesRows.Requester = (request, answer) => ServerRulesClient.Request(request, out var refusal, answer) ? null : refusal;
                ServerRulesRows.SupportedProbe = () => JoinAdminClient.ServerHasRules;
                ServerRulesRows.CanEditProbe = PlayersJoiningShown;
                ModsMenu.ServerRulesValueReader = key => ServerRulesRows.Row(key) is { Next: { } next } ? (next, next) : null;
                ServerRulesClient.Changed -= OnJoinAdminChanged;
                ServerRulesClient.Changed += OnJoinAdminChanged;
                ServerSettingsLock.ClientFingerprint = JoinAdminRows.LockFingerprint;
                // WORLDRESTORE1: the Backups rows' dialogs (the Mods window's dialog kit) and the wipe picker's zone.
                ServerBackupsRows.DialogFactory = title =>
                    (ModPagePane.DialogFactory ?? (t => Progenitor.Ui.Ui.Panel(t, Owner, SettingsPanel.PluginId)))(title);
                ServerBackupsRows.ServerZone = ServerZone;
                // BACKUPSDLG1: the Backups dialogs belong to the server they were opened for: leaving it closes them.
                Handshake.ServerContextCleared -= OnServerLeft;
                Handshake.ServerContextCleared += OnServerLeft;
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("JoinFlow: the server message settings could not be bound: " + e.Message);
        }
    }

    internal static void Uninstall()
    {
        try { JoinAdminClient.Changed -= OnJoinAdminChanged; }
        catch { /* teardown */ }
        PlayersJoiningInputsProbe = null;
        PlayersJoiningStatusAsk = null;
        _lastConsidered = "";
        JoinAdminRows.Reset();
        if (!Plugin.IsServerProcess)
        {
            ModsMenu.JoinAdminValueReader = null;
            ModsMenu.ServerRulesValueReader = null;
            try { Handshake.ServerContextCleared -= OnServerLeft; } catch { /* teardown */ }
            ServerRulesRows.Reset();
            ServerBackupsRows.Reset();
            try { ServerRulesClient.Changed -= OnJoinAdminChanged; } catch { /* teardown */ }
            ServerSettingsLock.ClientProbe = null;
            ServerSettingsLock.ClientFingerprint = null;
        }
        for (int i = Registrations.Count - 1; i >= 0; i--)
        {
            try { Registrations[i].Dispose(); }
            catch { /* teardown */ }
        }

        Registrations.Clear();
        _message = _next = _repeat = _precision = _passed = _showMessage = _showMods = _noProgenitor = null;
        NoProgenitorTemplateReader = null;
        TextTokens.Context = new TextTokens.RenderContext();
        GameWipeProbe = null;
        _wipeCache = null;
        _wipeReadAt = double.NegativeInfinity;
        _every = null;
        _file = null;
        _installed = false;
        LoggedSkips.Clear();
    }

    /// <summary>BACKUPSDLG1: the client left its server (or a new join began): server-bound Backups dialogs close.</summary>
    private static void OnServerLeft(string reason)
    {
        try { ServerBackupsRows.Leave(reason); }
        catch (Exception e) { Plugin.Logger?.LogWarning("WorldRestore: closing the Backups dialogs on leave threw: " + e.Message); }
    }

    private static GameWipeSchedule? _wipeCache;
    private static double _wipeReadAt = double.NegativeInfinity;

    /// <summary>
    /// RESETWIPE1: the server's own wipe schedule, from its Server rules state (it reads the settings files and
    /// <c>StartDate.json</c>), at most once every <see cref="WipeCacheSeconds"/>: the section is built on every join.
    /// </summary>
    private static GameWipeSchedule? ServerWipeCached()
    {
        double now = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        if (now - _wipeReadAt < WipeCacheSeconds) return _wipeCache;
        _wipeReadAt = now;
        try { _wipeCache = GameWipeSchedule.FromRules(ServerRulesServer.BuildState()); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("JoinFlow: reading the wipe schedule threw: " + e.Message);
            _wipeCache = null;
        }

        return _wipeCache;
    }

    private static IEnumerable<ConfigEntryBase> Entries()
    {
        if (_message != null) yield return _message;
        if (_next != null) yield return _next;
        if (_repeat != null) yield return _repeat;
        if (_every != null) yield return _every;
        if (_precision != null) yield return _precision;
        if (_passed != null) yield return _passed;
        if (_showMessage != null) yield return _showMessage;
        if (_showMods != null) yield return _showMods;
        if (_noProgenitor != null) yield return _noProgenitor;
    }

    private static void BuildPage(SettingsPageBuilder page)
    {
        // Three homes (2026-10-06): one registration, two homes. Progenitor's page draws Your settings and On this server;
        // the Server tab draws the groups ProgenitorPage.ServerTabTitles names (ModsMenu splits them by title). The server
        // registers the same page, so its text limits and server edits are declared as before.
        ProgenitorPage.BuildYourSettings(page);
        ProgenitorPage.BuildOnThisServer(page);
        ProgenitorPage.BuildServerTab(page, new ProgenitorPage.ServerParts
        {
            WhenPlayersJoin = AddWhenPlayersJoinRows,
            // JOINADMIN1 (moved from the Install tab's toolbar, decided 2026-10-04: "more like admin stuff"):
            // the admin popup, for an admin of a joined server that has the panel only — the rule the toolbar used.
            PlayersJoiningRow = p => AddPlayersJoiningRow(p, OpenPlayersJoining),
            // SERVERDESC1: the server-list description and the Progenitor signature (their own "Server list" section).
            ServerList = ServerListDescriptionSettings.AddRows,
            NoProgenitorMessage = AddNoProgenitorRow,
            // CHAT4: the Chat group's synced rows (what the server allows in chat).
            Chat = Progenitor.Chat.ServerChat.AddRows,
        });
    }

    /// <summary>When players join: Show and View mods for every player, then the server message's admin rows.</summary>
    private static void AddWhenPlayersJoinRows(SettingsPageBuilder page)
    {
        // Live 2026-10-04: Preview, the countdown and the Show button belong with the server message, under Server
        // settings. Show comes first, for every player; the admin rows follow.
        page.Button(JoinText.ShowMessageButton, JoinFlowPanel.ShowServerMessage, label: "Server message",
                help: "Shows this server's message again, then its mods page.",
                enableIf: () => JoinFlowClient.CanShowMessage)
            .InScope(SettingScope.ServerWide);
        // Stage 2 live run: with everything ready the mods page is skipped, so the recommended mods need a way in.
        page.Button(JoinText.ViewModsButton, JoinFlowPanel.ShowServerMods, label: "This server's mods",
                help: "Shows this server's mods page: the mods it requires and the ones it recommends.",
                enableIf: () => JoinFlowClient.CanShowMods)
            .InScope(SettingScope.ServerWide);
        // TOKENINSERT1: the Insert buttons and the Players see line are drawn under the field from the one table
        // (TextTokens). Preview stays: it opens the whole message page, with its clickable links, as a player sees it.
        page.Text(_message!, multiline: true, lines: 6, label: "Message", scope: SettingScope.Server,
                help: TextTokens.Help(TextTokens.MessageRowId, "The first thing a joining player sees. Plain text, line breaks kept."))
            .MaxLength(JoinSectionLimits.MaxMessageChars);
        page.Button("Preview", JoinFlowPanel.OpenPreview, label: "Preview",
                help: "Shows the message page as a player sees it now, with the tokens filled in.")
            .InScope(SettingScope.ServerWide);
        page.Info("Countdown now", () => PreviewCountdown(), refreshSeconds: 1,
                help: "{reset} and {reset:relative} as a player sees them now.")
            .InScope(SettingScope.ServerWide);
        // RESETWIPE1 (decided 2026-10-06): {reset} follows the game's own wipe schedule when one is set, and Next
        // reset is then the computed wipe, read-only. Without one, the date picker is the only reset setting (a one-off).
        // Repeat, Repeat every, Countdown precision and After the reset left the menus (cfg file only).
        page.DateTime(_next!, seconds: () => false,
            zone: ServerZoneForPage, label: NextResetByHandLabel, scope: SettingScope.Server,
            help: NextResetHelp, showIf: () => !(GameWipe()?.On ?? false));
        page.Info(NextResetLabel, () => GameWipe()?.RowText() ?? "", refreshSeconds: 5, help: NextResetFromWipesHelp,
                showIf: () => GameWipe()?.On ?? false)
            .InScope(SettingScope.ServerWide)
            .StatusTone(() => CellTone.Muted);
        page.Radio(_showMessage!, choices: MessageFrequencyChoices, labels: MessageFrequencyLabels, label: "Show the message",
                scope: SettingScope.Server,
                help: "When a joining player sees the message page. Point at an option for what it does.")
            .OptionDescriptions(MessageFrequencyDescriptions);
        page.Radio(_showMods!, choices: ModsFrequencyChoices, labels: ModsFrequencyLabels, label: "Show the mods page",
                scope: SettingScope.Server,
                help: "When a joining player sees this server's mods page. Point at an option for what it does.")
            .OptionDescriptions(ModsFrequencyDescriptions);
    }

    /// <summary>
    /// Players without Progenitor: NOPROGNOTICE's message, the only words such a player can read (the disconnect screen
    /// shows only the game's own reason, never this server's text). Moved here from the server message (stage 5).
    /// </summary>
    private static void AddNoProgenitorRow(SettingsPageBuilder page)
    {
        // NOPROGMSG1 (user review 2026-10-06: "Don't we have a default? … how to use [the variables]"): a text area whose
        // empty value shows the default as a muted hint, the placeholders always named under it, what players see, the
        // length, and Use the default when the admins have their own text.
        page.Text(_noProgenitor!, multiline: true, lines: NoProgenitorMessageRow.Lines, label: NoProgenitorMessageRow.Label,
                scope: SettingScope.Server, help: NoProgenitorMessageRow.Help)
            .MaxLength(NoAnswerNoticeText.MaxTemplateChars)
            .EmptyMeans(NoAnswerNoticeText.DefaultTemplate);
        // TOKENINSERT1: Players see is drawn under the field (TextTokens), with Insert; the readback stays on Length.
        page.Info(NoProgenitorMessageRow.LengthLabel, () => NoProgenitorMessageRow.LengthLine(NoProgenitorDraft()), refreshSeconds: 1,
                help: "At most " + NoAnswerNoticeText.MaxTemplateChars + " characters. Empty uses the default message.")
            .InScope(SettingScope.ServerWide)
            .StatusTone(() => NoProgenitorMessageRow.LengthTone(NoProgenitorDraft()))
            .Readback(NoProgenitorMessageRow.ReadbackKey, () => NoProgenitorMessageRow.Readback(NoProgenitorDraft()));
        page.Button(NoProgenitorMessageRow.UseDefaultCaption, UseDefaultNoProgenitorMessage, label: NoProgenitorMessageRow.DefaultRowLabel,
                help: "Clears your message, so players without Progenitor get the default one. Saved and audited like any change.",
                showIf: () => NoProgenitorStored().Trim().Length > 0 && JoinAdminRows.IsAdmin())
            .InScope(SettingScope.ServerWide);
    }

    /// <summary>NOPROGMSG1: the row id of Message without Progenitor (<c>&lt;plugin&gt;/&lt;Section&gt;/NoProgenitorMessage</c>).</summary>
    internal static string NoProgenitorRowId => ConfigSync.FrameworkSettingsId + "/" + Section + "/" + NoProgenitorMessageKey;

    /// <summary>The value as this side holds it (the server's own; a client's synced copy).</summary>
    private static string NoProgenitorStored()
    {
        try { return _noProgenitor?.Value ?? ""; }
        catch { return ""; }
    }

    /// <summary>The field's text as typed when a page drew it, else the stored value.</summary>
    private static string NoProgenitorDraft() => SettingDrafts.ForRow(NoProgenitorRowId) ?? NoProgenitorStored();

    /// <summary>NOPROGMSG1: Use the default: the empty value, sent like the row's own Apply (the server saves and audits it).</summary>
    private static void UseDefaultNoProgenitorMessage()
    {
        string current = NoProgenitorStored();
        SettingDrafts.Note(NoProgenitorRowId, "");
        try
        {
            ServerSettings.Request(NoProgenitorRowId, "", current, result =>
                Plugin.Logger?.LogInfo("JoinFlow: Message without Progenitor: Use the default: " + (result.Success ? "saved" : "refused: " + result.Error)));
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("JoinFlow: Use the default threw: " + e.Message); }
        try { ModsMenu.NoteSettingsScopeChanged("no-progenitor-default"); }
        catch { /* a refresh hint */ }
    }

    /// <summary>SERVERDESC1: the reset settings as this side holds them (the server's own; a client's synced copy), for the description's tokens.</summary>
    internal static ResetSchedule? CurrentReset()
    {
        try { return CurrentValues(null, TimeZoneInfo.Local)?.Reset; }
        catch { return null; }
    }

    /// <summary>SERVERDESC1: the server's time zone as this side knows it (the server: its own; a client: the joined server's).</summary>
    internal static TimeZoneInfo ServerZone() => ServerZoneForPage();

    /// <summary>The Players joining row's action: the existing popup, unchanged.</summary>
    private static void OpenPlayersJoining() =>
        Plugin.Logger?.LogInfo("JoinAdmin: Players joining: " + JoinAdminPanel.Open());

    /// <summary>The hello, an answer or the connection changed: re-evaluate an open settings page in place.</summary>
    private static void OnJoinAdminChanged()
    {
        try { ModsMenu.NoteSettingsScopeChanged("join-admin"); }
        catch { /* a refresh hint */ }
    }

    /// <summary>The zone the admin's date picker shows: the joined server's (from its join section), else this machine's.</summary>
    private static TimeZoneInfo ServerZoneForPage()
    {
        if (Plugin.IsServerProcess) return TimeZoneInfo.Local;
        var zone = JoinFlowClient.ServerZone;
        return zone ?? TimeZoneInfo.Local;
    }

    private static string PreviewCountdown()
    {
        var section = CurrentValues(null, TimeZoneInfo.Local);
        if (section?.Reset == null) return "No reset set";
        var now = DateTime.UtcNow;
        var zone = JoinFlowClient.ServerZone ?? TimeZoneInfo.Local;
        return ResetCountdown.Render(ResetCountdown.TokenCountdown + " · " + ResetCountdown.TokenRelative, section.Reset, now, zone, TimeZoneInfo.Local);
    }

    /// <summary>The section built from the rows as this side sees them (the server's values; on a client its synced copy).</summary>
    internal static JoinSection? CurrentValues(IReadOnlyList<RecommendedMod>? recommended, TimeZoneInfo zone)
    {
        if (_message == null) return null;
        var skipped = new List<string>();
        // RESETWIPE1: on a client the wipe's 00:00 is the joined server's midnight, not this machine's.
        if (!Plugin.IsServerProcess) zone = ServerZoneForPage();
        var section = BuildSection(_message.Value, _next?.Value, _repeat?.Value, _every?.Value ?? 1, _precision?.Value,
            _passed?.Value, recommended, zone, DateTime.UtcNow, skipped, _showMessage?.Value, _showMods?.Value, GameWipe());
        foreach (string skip in skipped)
            if (LoggedSkips.Add(skip))
                Plugin.Logger?.LogWarning("JoinFlow: " + ModManifest.ManifestFileName + " recommended row skipped: " + skip);
        return section;
    }

    /// <summary>The server's section for the catalog (null when there is nothing to show).</summary>
    internal static JoinSection? Content(ManifestOverrides? overrides)
    {
        if (!Plugin.IsServerProcess) return null;
        // Always sent, even with no message: the admin's date picker needs the server's zone before anything
        // is set, and the page frequencies travel in it. A section is about 200 bytes when empty.
        // JOINADMIN1: the recommended rows are the admin panel's (progenitor-join-admin.json) first, then the
        // operator overlay's rows for other mods (JoinAdminPolicy.Merge).
        return CurrentValues(JoinAdminServer.MergedRecommended(overrides?.Recommended), TimeZoneInfo.Local);
    }
#endif
}

/// <summary>
/// NOPROGMSG1: the words and rules of the Server tab's Message without Progenitor row and the lines under it (game-free).
/// </summary>
internal static class NoProgenitorMessageRow
{
    internal const string Label = "Message without Progenitor";
    internal const int Lines = 4;
    internal const string PlaceholdersLabel = "Placeholders";
    /// <summary>TOKENTIPS1: from the one table (<see cref="TextTokens"/>).</summary>
    internal static string PlaceholdersLine => TextTokens.Line(TextTokens.NoProgenitorRowId);
    internal const string PreviewLabel = "Players see";

    /// <summary>
    /// RADIODESC1: the Players see preview's fixed height in lines (it changes while the admin types, so it is not fitted).
    /// The default message (about 290 characters) takes three to four lines at the Server tab's width.
    /// </summary>
    internal const int PreviewLines = 4;
    internal const string LengthLabel = "Length";
    internal const string UsingDefaultLine = "Using the default message.";
    internal const string UseDefaultCaption = "Use the default";
    internal const string DefaultRowLabel = "Default message";
    internal const string ReadbackKey = "noProgenitorMessage";

    /// <summary>The preview's sample: <c>{seconds}</c> as 20 (and <c>{mods}</c> as Progenitor, the default's own word).</summary>
    internal const int SampleSeconds = 20;

    internal const string Help =
        "Sent in chat, again every few seconds, to a player whose game has no Progenitor, before the disconnect. Leave it "
        + "empty to send the default message (shown in the empty field). Placeholders: {mods} is the mods they're missing "
        + "(the first five, then \"and N more\"; Progenitor when none are named) and {seconds} the seconds until they're "
        + "disconnected. At most 300 characters, sent as one chat line of plain letters, digits and punctuation; < and > "
        + "become parentheses.";

    internal static bool UsingDefault(string? text) => (text ?? "").Trim().Length == 0;

    /// <summary>What the chat line reads, with the sample values (the default when <paramref name="text"/> is empty).</summary>
    internal static string Preview(string? text) => NoAnswerNoticeText.Build(text, null, SampleSeconds);

    internal static string PreviewLine(string? text) => Preview(text);

    /// <summary><c>Using the default message.</c>, or <c>212 / 300</c>.</summary>
    internal static string LengthLine(string? text)
    {
        string t = (text ?? "").Trim();
        if (t.Length == 0) return UsingDefaultLine;
        return t.Length.ToString(CultureInfo.InvariantCulture) + " / " + NoAnswerNoticeText.MaxTemplateChars.ToString(CultureInfo.InvariantCulture);
    }

    internal static bool OverLimit(string? text) => (text ?? "").Trim().Length > NoAnswerNoticeText.MaxTemplateChars;

    /// <summary>Muted, and the warning tone over the limit.</summary>
    internal static Progenitor.Ui.CellTone LengthTone(string? text) => OverLimit(text) ? Progenitor.Ui.CellTone.Warn : Progenitor.Ui.CellTone.Muted;

    /// <summary><c>noProgenitorMessage {usingDefault, chars, max, over, preview}</c>.</summary>
    internal static Dictionary<string, object?> Readback(string? text)
    {
        string t = (text ?? "").Trim();
        return new Dictionary<string, object?>
        {
            ["usingDefault"] = t.Length == 0,
            ["chars"] = t.Length,
            ["max"] = NoAnswerNoticeText.MaxTemplateChars,
            ["over"] = t.Length > NoAnswerNoticeText.MaxTemplateChars,
            ["preview"] = Preview(text),
        };
    }
}
