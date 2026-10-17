using System;
using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>
/// Settings organization, three homes (docs/settings-organization.md, "Three homes (2026-10-06)"): the rows of
/// Progenitor's own page registration. One registration holds two homes, drawn apart:
/// <list type="bullet">
/// <item>Progenitor's mod page (Mods → Required → Progenitor): <b>Your settings</b> (Keys, Photo mode, Build menu, Equipment,
/// Chat, Private games) and <b>On this server</b> (Progenitor's own two server toggles and the link to the Server tab).</item>
/// <item>The <b>Server</b> tab: the joined server's setup (<see cref="ServerTabTitles"/>).</item>
/// </list>
/// The rows are placed by section and key over the entries the owning modules bind, so a module may bind later than
/// the page registers. Game-free.
/// </summary>
internal static class ProgenitorPage
{
    internal const string KeysTitle = "Keys";
    internal const string PhotoModeTitle = "Photo mode";
    internal const string BuildMenuTitle = "Build menu";
    internal const string EquipmentTitle = "Equipment";
    internal const string ChatTitle = "Chat";
    internal const string PrivateGamesTitle = "Private games";

    /// <summary>SUPPORT1: Collect logs, Open the logs folder and the newest bundle (<see cref="Support.SupportActions"/>).</summary>
    internal const string GetHelpTitle = Support.SupportActions.GroupTitle;

    /// <summary>Three homes: the heading of Progenitor's page's server part (its scope heading, in place of Server settings).</summary>
    internal const string OnThisServerTitle = "On this server";

    /// <summary>
    /// The section the On this server rows are built in. A title that only restates the scope (<see cref="SettingScopeSections.IsRedundant"/>)
    /// draws no subheading under the scope heading.
    /// </summary>
    internal const string OnThisServerSection = SettingScopeSections.ServerHeading;

    internal const string LinkServerText = "This server's setup is in the Server tab.";
    internal const string LinkServerHelp =
        "Opens the Server tab: how the server you're on is set up — the message, the mods players need, the server list, the "
        + "server rules, chat and the operator settings. Back returns here.";

    /// <summary>Progenitor's page's groups in the left list (under Progenitor in the Mods list), in page order.</summary>
    internal static readonly IReadOnlyList<string> NavGroups = new[]
    {
        KeysTitle, PhotoModeTitle, BuildMenuTitle, EquipmentTitle, ChatTitle, PrivateGamesTitle, GetHelpTitle, OnThisServerTitle,
    };

    internal const string UninstallCaption = "Uninstall";
    internal const string UninstallLabel = "Remove Progenitor from this PC";
    internal const string UninstallHelp =
        "Progenitor removes itself, every mod it installed, its ledger and caches the next time V Rising starts; nothing is "
        + "deleted while the game runs. Mods installed by other tools and BepInEx stay.";
    internal const string RequestedLabel = "Removal requested";
    internal const string KeepCaption = "Keep Progenitor";
    internal const string KeepLabel = "Cancel the removal";

    /// <summary>The uninstall rows' readback key (the Configuration tab's former key).</summary>
    internal const string RowUninstall = "progenitorUninstall";

    /// <summary>
    /// Your settings, in order: Keys, Photo mode, Build menu, Equipment (Dye all, 2026-10-08), Chat (CHAT1), Private games, Get help (SUPPORT1). Decided 2026-10-06 ("No user will care to
    /// change those"): <c>[Keybinds] EnforceGameWins</c> and <c>[Ui] CaptureMode</c> are cfg-only now, so the UI group is gone.
    /// </summary>
    internal static void BuildYourSettings(SettingsPageBuilder page)
    {
        if (page == null) throw new ArgumentNullException(nameof(page));

        // Keys: every Core keybind (the Keybinds tab draws the same values).
        page.Section(KeysTitle)
            .AllKeybinds();

        page.Section(PhotoModeTitle)
            .SettingRef("PhotoMode", "Enabled", label: "Photo mode", control: SettingControl.Toggle,
                help: "A free-flying camera with the HUD hidden (Ctrl+Shift+F9 by default; the keys are under Keys). Off by default.")
            .SettingRef("PhotoMode", "HideHudOnEnter", label: "Hide the HUD on entry", control: SettingControl.Toggle)
            .SettingRef("PhotoMode", "HideNameplates", label: "Hide nameplates with the HUD", control: SettingControl.Toggle)
            .SettingRef("PhotoMode", "LookSensitivity", label: "Look speed");

        page.Section(BuildMenuTitle)
            .SettingRef("BuildSearch", "Enabled", label: "Build menu search", control: SettingControl.Toggle)
            .SettingRef("BuildSearch", "ClearOnClose", label: "Clear the search on close", control: SettingControl.Toggle)
            .SettingRef("BuildSearch", "Breadcrumbs", label: "Breadcrumbs", control: SettingControl.Toggle);

        // DYEALL in Core (decided 2026-10-08). Covered pieces and the hold time are not settings: a hold is
        // always one second, and a piece under a transmog is always dyed. The other [DyeAll] keys stay in the file.
        page.Section(EquipmentTitle)
            .SettingRef("DyeAll", "Enabled", label: "Dye all on hold", control: SettingControl.Toggle,
                help: "In Character > Equipment, hold a color swatch until the ring fills: every worn piece and transmog takes "
                    + "that color. A click still colors only the selected piece.");

        // CHAT1: the game's own chat box (Progenitor.Chat.ChatWindow).
        page.Section(ChatTitle)
            .SettingRef("Chat", "VisibleSeconds", label: "Seconds chat stays on screen", control: SettingControl.Slider,
                help: "How long the chat box stays up after the last message before it fades. The game's own is 14 seconds.")
            .SettingRef("Chat", "KeepVisible", label: "Keep chat on screen", control: SettingControl.Toggle,
                help: "Never fade the chat box. Escape still closes it.")
            .SettingRef("Chat", "StayWhileHovered", label: "Keep chat up while the mouse is over it", control: SettingControl.Toggle)
            .SettingRef("Chat", "ClickSafe", label: "Clicking chat doesn't attack", control: SettingControl.Toggle,
                help: "A click on the chat box doesn't attack, turn the camera or use another mouse action.")
            .SettingRef("Chat", "View", label: "Progenitor's chat lines", control: SettingControl.Toggle,
                help: "Tabs, longer history, clickable links, mentions and repeat counts. Off: the game's own chat lines.")
            .SettingRef("Chat", "Timestamps", label: "Show the time on each line", control: SettingControl.Toggle)
            .SettingRef("Chat", "CollapseRepeats", label: "Fold repeated lines into a count", control: SettingControl.Toggle,
                help: "The same line again within a minute shows ×2, ×3 instead of a new line.")
            .SettingRef("Chat", "KeepHistory", label: "Keep chat between sessions", control: SettingControl.Toggle,
                help: "Each server's chat comes back when you rejoin, with a line where each session started and ended.")
            .SettingRef("Chat", "HistoryLines", label: "Lines kept per server", control: SettingControl.Number,
                help: "50 to 5000. Scroll up in the chat box to read back through them. A lower number drops the oldest lines at once.")
            .SettingRef("Chat", "Mentions", label: "Highlight mentions", control: SettingControl.Toggle,
                help: "A line with your name, a watched word, or @all from an admin is highlighted.")
            .SettingRef("Chat", "MentionSound", label: "Play a sound for mentions", control: SettingControl.Toggle)
            .SettingRef("Chat", "MentionWords", label: "Watched words", control: SettingControl.Text,
                help: "Extra words that count as a mention, separated by commas.")
            .SettingRef("Chat", "FontSize", label: "Text size", help: "0 keeps the game's size.")
            .SettingRef("Chat", "BackgroundOpacity", label: "Background opacity", help: "-1 keeps the game's.")
            .SettingRef("Chat", "LockPosition", label: "Lock the chat box's position", control: SettingControl.Toggle,
                help: "Stops dragging the tab line or the corner from moving or resizing the chat box.")
            .SettingRef("Chat", "Recall", label: "Up and Down bring back sent lines", control: SettingControl.Toggle)
            .SettingRef("Chat", "CommandHints", label: "Show command hints after \".\"", control: SettingControl.Toggle,
                help: "The server's matching commands appear above the line; Tab completes the first.")
            .SettingRef("Chat", "ModSuggestions", label: "Suggest mods after \"[mod:\"", control: SettingControl.Toggle,
                help: "Type [mod: and part of a mod's name; matching mods show above the line. Tab puts the first in as a mod link, press again for the next. Installed mods always; Thunderstore mods once the Mods menu has loaded its list.")
            .SettingRef("Chat", "ChannelColor", label: "Color the line you type by channel", control: SettingControl.Toggle)
            .SettingRef("Chat", "LongMessages", label: "Send long lines in parts", control: SettingControl.Toggle,
                help: "A line longer than the game takes goes out in parts, joined back into one line for players with Progenitor.")
            .SettingRef("Chat", "MutedPlayers", label: "Muted players", control: SettingControl.Text,
                help: "Their lines are left out of your chat. Right-click one of their lines to mute or unmute.")
            .SettingRef("Chat", "ReplyShortcut", label: "/r replies to the last whisper", control: SettingControl.Toggle,
                help: "Typing /r at the start of a chat line becomes the game's /reply.")
            .SettingRef("Chat", "NameCompletion", label: "Tab completes names after /w", control: SettingControl.Toggle,
                help: "After /w or /whisper, the completion key fills in an online player's name; press it again for the next "
                    + "match. The key is under Keys.");

        page.Section(PrivateGamesTitle)
            .SettingRef("Hosted", "Enabled", label: "Load Progenitor in the private games I host", control: SettingControl.Toggle, owned: true,
                help: "Copies Progenitor and your mods into the game's bundled server when you start a private or single-player "
                    + "game, so server-side mods load there too. Off: private games start without Progenitor. Applies at the next "
                    + "private-game start.")
            .Button(UninstallCaption, () => RunOrThrow(ConfigurationPane.WriteClientUninstall()),
                confirm: Progenitor.Install.ClientUninstall.PromptHeading, label: UninstallLabel, help: UninstallHelp,
                showIf: () => !ConfigurationPane.ClientUninstallRequested)
            .Destructive()
            .Readback(RowUninstall, () => ConfigurationPane.ClientUninstallState())
            .Info(RequestedLabel, () => ConfigurationPane.ClientUninstallRequestedLine, refreshSeconds: 0,
                showIf: () => ConfigurationPane.ClientUninstallRequested)
            .Readback(RowUninstall, () => ConfigurationPane.ClientUninstallState())
            .Button(KeepCaption, () => RunOrThrow(ConfigurationPane.CancelClientUninstallRequest()), label: KeepLabel,
                help: "Deletes the removal request; Progenitor stays installed.",
                showIf: () => ConfigurationPane.ClientUninstallRequested);

        // SUPPORT1: one click collects the logs a help post needs (title screen and in game). The button only starts the
        // collect; the Last bundle line shows "Collecting logs..." and then the outcome.
        page.Section(GetHelpTitle)
            .Button(Support.SupportActions.CollectCaption, Support.SupportActions.Collect, label: Support.SupportActions.CollectLabel,
                help: Support.SupportActions.CollectHelp)
            .Info(Support.SupportActions.LastLabel, LastBundleLine, refreshSeconds: 1,
                help: "The newest bundle on your Desktop (or the folder set below) or in BepInEx/" + Support.SupportBundle.FolderName
                    + ", or how the last Collect logs went.")
            .Button(Support.SupportActions.OpenCaption, Support.SupportActions.OpenFolder, label: Support.SupportActions.OpenLabel,
                help: Support.SupportActions.OpenHelp)
            .SettingRef(Support.SupportActions.SettingsSection, Support.SupportActions.OutputFolderKey, label: Support.SupportActions.FolderLabel,
                control: SettingControl.Text, help: Support.SupportActions.FolderHelp)
            .SettingRef(Support.SupportActions.SettingsSection, Support.SupportActions.AskToOpenDiscordKey,
                label: Support.SupportActions.AskToOpenDiscordLabel, control: SettingControl.Toggle,
                help: Support.SupportActions.AskToOpenDiscordHelp)
            .SettingRef(Support.SupportActions.SettingsSection, Support.SupportActions.IncludeServerLogsKey,
                label: Support.SupportActions.IncludeServerLogsLabel, control: SettingControl.Toggle,
                help: Support.SupportActions.IncludeServerLogsHelp, showIf: Support.SupportActions.ServerLogsRowShown);
    }

    /// <summary>SUPPORT1: while collecting and after, the collect's own line; otherwise the newest bundle on disk.</summary>
    internal static string LastBundleLine()
    {
        if (Support.SupportActions.Running) return Support.SupportActions.RunningLine;
        string status = Support.SupportActions.Status;
        return status.Length > 0 ? status : Support.SupportActions.NewestLine();
    }

    /// <summary>
    /// Three homes: <b>On this server</b> — only Progenitor's own two server toggles (the admin channel: an admin edits
    /// them, everyone else does not see them, decision 5), then the link to the Server tab, which everyone sees.
    /// </summary>
    internal static void BuildOnThisServer(SettingsPageBuilder page)
    {
        if (page == null) throw new ArgumentNullException(nameof(page));
        page.Section(OnThisServerSection);
        Admin(page, Networking.JoinAdminRows.PhotoMode());
        Admin(page, Networking.JoinAdminRows.AllySafeHits());
        page.Link(SettingsLinks.ServerTabKey, LinkServerText, () => ModsMenu.OpenServerTabFromLink(),
            SettingsLinks.TargetServerTab, LinkServerHelp, SettingScope.ServerWide);
    }

    // ---- The Server tab ------------------------------------------------------------------------------

    internal const string WhenPlayersJoinTitle = "When players join";
    internal const string ModsPlayersNeedTitle = "Mods players need";
    // LISTING1 (user review 2026-10-06): renamed from "Server list"; its slug (server-tab:server-list) is unchanged.
    internal const string ServerListTitle = "Server listing";
    internal const string PlayersWithoutProgenitorTitle = "Players without Progenitor";
    internal const string ServerRulesTitle = Networking.ServerRules.Title;
    internal const string OperatorTitle = "Operator";

    /// <summary>The Server tab's left-list name of the Server rules group (its heading carries the restart note).</summary>
    internal const string ServerRulesNavTitle = "Server rules";

    /// <summary>
    /// CHAT4 (decided 2026-10-07: a "Chat" group of the Server tab): the group's heading. Not <c>Chat</c>, because the
    /// two homes are split by section title (<see cref="SectionsFor"/>) and <see cref="ChatTitle"/> is Your settings' group;
    /// the left list names it <see cref="ChatServerNavTitle"/>, as Server rules' list name drops its restart note.
    /// </summary>
    internal const string ChatServerTitle = "Chat on this server";

    /// <summary>CHAT4: the Server tab's left-list name of the Chat group.</summary>
    internal const string ChatServerNavTitle = "Chat";

    /// <summary>
    /// AUTOUPDATE3 (decided 2026-10-09, approving the design's Updates group): the server's own mod updates: the mode,
    /// major versions, Restart when empty and its quiet period, the pre-update world backup, the status with Check now and
    /// Restart now. Admins only, lock group <c>updates</c>.
    /// </summary>
    internal const string UpdatesTitle = "Updates";

    /// <summary>
    /// The Server tab's groups, in order (decided 2026-10-06). CHAT4: Chat follows Server rules: both are what the server
    /// decides for every player, ahead of the admin-only groups about players without Progenitor, the server's updates
    /// (AUTOUPDATE3) and the operator.
    /// </summary>
    internal static readonly IReadOnlyList<string> ServerTabTitles = new[]
    {
        WhenPlayersJoinTitle, ModsPlayersNeedTitle, ServerListTitle, ServerRulesTitle, ChatServerTitle, PlayersWithoutProgenitorTitle, UpdatesTitle,
        OperatorTitle,
    };

    /// <summary>The left-list caption of a Server tab group.</summary>
    internal static string NavCaption(string title) =>
        string.Equals(title, ServerRulesTitle, StringComparison.Ordinal) ? ServerRulesNavTitle
        : string.Equals(title, ChatServerTitle, StringComparison.Ordinal) ? ChatServerNavTitle
        : title ?? "";

    /// <summary>True when <paramref name="title"/> is one of the Server tab's groups (its section is drawn there, not on the page).</summary>
    internal static bool IsServerTabSection(string? title)
    {
        foreach (string t in ServerTabTitles)
            if (string.Equals(t, title, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>
    /// Three homes: the sections of Progenitor's registration for one home — the Server tab's groups when
    /// <paramref name="serverTab"/>, else everything else (Progenitor's page). The two never share a section.
    /// </summary>
    internal static List<ModSettingsSection> SectionsFor(IReadOnlyList<ModSettingsSection>? sections, bool serverTab)
    {
        var result = new List<ModSettingsSection>();
        if (sections == null) return result;
        foreach (var section in sections)
            if (section != null && IsServerTabSection(section.Title) == serverTab) result.Add(section);
        return result;
    }

    /// <summary>The line a group made only of admin-channel rows shows to a player who is not an admin there.</summary>
    internal const string AdminOnlyGroupNote = "Only an admin of this server sees these settings.";

    internal const string AdminOnlyGroupLabel = "Admin only";

    /// <summary>
    /// Stage 5 (the parts bound by their own modules): the server message's entries, the Players joining row, the
    /// server-list rows, the no-Progenitor message. Each adds rows to the section it is given; <see cref="ServerList"/>
    /// adds its own <b>Server list</b> section.
    /// </summary>
    internal sealed class ServerParts
    {
        internal Action<SettingsPageBuilder>? WhenPlayersJoin { get; init; }
        internal Action<SettingsPageBuilder>? PlayersJoiningRow { get; init; }
        internal Action<SettingsPageBuilder>? ServerList { get; init; }
        internal Action<SettingsPageBuilder>? NoProgenitorMessage { get; init; }

        /// <summary>CHAT4: the Chat group's synced rows (<c>[ServerChat]</c>), drawn before its admin-channel rows.</summary>
        internal Action<SettingsPageBuilder>? Chat { get; init; }
    }

    /// <summary>
    /// Three homes: the Server tab's groups — When players join, Mods players need, Server list, Server rules, Players
    /// without Progenitor, Operator. The admin-channel rows (<see cref="Networking.JoinAdminRows"/>) are shown only to an
    /// admin of the joined server (decision 5); a group made only of them says so to anyone else.
    /// </summary>
    internal static void BuildServerTab(SettingsPageBuilder page, ServerParts parts)
    {
        if (page == null) throw new ArgumentNullException(nameof(page));
        parts ??= new ServerParts();

        page.Section(WhenPlayersJoinTitle);
        parts.WhenPlayersJoin?.Invoke(page);

        page.Section(ModsPlayersNeedTitle);
        parts.PlayersJoiningRow?.Invoke(page);
        // Mod compatibility C3: Version matching first (above the required client mods in the popup), then a read-only line
        // with the per-mod rules (set in the Players joining popup).
        Admin(page, Networking.JoinAdminRows.VersionMatching());
        page.Info(Networking.VersionMatchText.RulesLabel, Networking.JoinAdminRows.ServerModRulesLine, refreshSeconds: 1,
                help: Networking.VersionMatchText.RulesHelp,
                showIf: () => Networking.JoinAdminRows.Shown(Networking.JoinAdminOps.VersionMatching))
            .InScope(SettingScope.ServerWide)
            .Uncounted();
        Admin(page, Networking.JoinAdminRows.MidSession());
        Admin(page, Networking.JoinAdminRows.DisconnectGrace());
        Admin(page, Networking.JoinAdminRows.Unverified());
        AdminOnlyNote(page);

        if (parts.ServerList != null) parts.ServerList(page);
        else page.Section(ServerListTitle);
        // Stage 6: Server name is a listing field (decision 2), so it is here rather than under Server rules.
        Rule(page, Networking.ServerRules.Name);

        BuildServerRules(page);

        // CHAT4: what the server allows in chat (synced to every player), then its moderation and relay (admins only).
        page.Section(ChatServerTitle);
        parts.Chat?.Invoke(page);
        foreach (var spec in Networking.ServerChatRows.All()) Admin(page, spec);

        page.Section(PlayersWithoutProgenitorTitle);
        parts.NoProgenitorMessage?.Invoke(page);
        // KEEPOUT1: the explicit choice first, then the deadline it (or the server's mods) uses.
        Admin(page, Networking.JoinAdminRows.KeepOut());
        Admin(page, Networking.JoinAdminRows.AnswerTimeout());

        // AUTOUPDATE3: the server's own mod updates (admins only).
        page.Section(UpdatesTitle);
        Networking.ServerUpdatesRows.Build(page, spec => Admin(page, spec));
        AdminOnlyNote(page);

        page.Section(OperatorTitle);
        #if !PROGENITOR_TESTS
        page.Button("Manage", ItemManagementPane.Open, label: "Saved mod items",
            help: "Restore previous items after reinstalling a mod, keep them saved, or discard them permanently. The server decides every request.",
            showIf: () => Networking.JoinAdminClient.IsAdmin).InScope(SettingScope.ServerWide);
#endif
        Admin(page, Networking.JoinAdminRows.RemoteInstall());
        Admin(page, Networking.JoinAdminRows.AllowedSteamIds());
        Admin(page, Networking.JoinAdminRows.NoticeSeconds());
        Admin(page, Networking.JoinAdminRows.NoticeRepeatSeconds());
        Admin(page, Networking.JoinAdminRows.NoticeCreationLimitSeconds());
        AdminOnlyNote(page);
    }

    /// <summary>A group of admin-channel rows says, to a player who is not an admin, why it is empty. Not a setting.</summary>
    private static void AdminOnlyNote(SettingsPageBuilder page) =>
        page.Info(AdminOnlyGroupLabel, () => AdminOnlyGroupNote, refreshSeconds: 0,
                help: "These settings travel on the server's admin channel, which only an admin receives.",
                showIf: () => !Networking.JoinAdminRows.IsAdmin())
            .InScope(SettingScope.ServerWide)
            .Uncounted();

    /// <summary>
    /// Stage 6: <b>Server rules · applied at the next restart</b>, the vanilla settings behind the Join Game browser, for every
    /// player of a server that has the channel (read-only for a player who is not an admin there): the restart line, a preset
    /// warning, the rules, then the read-only lines.
    /// </summary>
    internal static void BuildServerRules(SettingsPageBuilder page)
    {
        page.Section(ServerRulesTitle);
        // PENDING1 (user review 2026-10-06): a status row, not a setting: what waits for the next restart.
        Info(page, Networking.ServerRulesRows.PendingLabel, () => Networking.ServerRulesRows.PendingLine(),
            "Rules marked Restart apply when the server restarts; restarting it is the operator's job.");
        // PENDING2: the summary and one muted amber line per change, wrapped (the row is as tall as its lines).
        page.StatusTone(() => Networking.ServerRulesRows.PendingAttention() ? CellTone.Amber : CellTone.Muted,
                () => Networking.ServerRulesRows.PendingHelp())
            .StatusWraps()
            .Readback("pendingRestart", () => Networking.ServerRulesRows.PendingReadback());
        page.Info(Networking.ServerRulesRows.PresetLabel, Networking.ServerRulesRows.PresetLine, refreshSeconds: 0,
                showIf: () => Networking.ServerRulesRows.Shown() && Networking.ServerRulesRows.PresetLine().Length > 0)
            .InScope(SettingScope.ServerWide);
        foreach (var rule in Networking.ServerRules.Specs)
        {
            if (rule.Key == Networking.ServerRules.Name) continue;
            Rule(page, rule.Key);
            // WIPECONFIRM1: Next wipe, by date, right after the two game rows it sets.
            if (rule.Key == Networking.ServerRules.DayOfReset)
                page.AdminSetting(Networking.ServerRulesRows.NextWipeDateSpec(), Networking.ServerRulesRows.Shown);
        }
        Info(page, Networking.ServerRulesRows.MercilessLabel, Networking.ServerRulesRows.MercilessLine,
            "Blood-bound equipment off, or a castle heart players can attack, lists the server as Merciless.");
        Info(page, Networking.ServerRulesRows.TerritoryLabel, Networking.ServerRulesRows.TerritoryLine,
            "Not a setting: the game works it out every minute from free territory and new players.");
        Info(page, Networking.ServerRulesRows.DaysRunningLabel, Networking.ServerRulesRows.DaysRunningLine,
            "Days since this world started (its StartDate.json).");
        Info(page, Networking.ServerRulesRows.StandardLabel, Networking.ServerRulesRows.StandardLine,
            "The game lists a server as Standard when its settings match one of its own presets. It can only be predicted, not set.");
        Info(page, Networking.ServerRulesRows.NextWipeLabel, Networking.ServerRulesRows.NextWipeLine,
            "The start date plus the interval, moved to the wipe day. The server checks once, when it starts.");
        // WORLDRESTORE1: the restore points of this world, after the next-wipe row (admins only).
        Networking.ServerBackupsRows.Build(page);
    }

    private static void Info(SettingsPageBuilder page, string label, Func<string> value, string help) =>
        page.Info(label, value, refreshSeconds: 1, help: help, showIf: Networking.ServerRulesRows.Shown).InScope(SettingScope.ServerWide);

    private static void Rule(SettingsPageBuilder page, string key)
    {
        var rule = Networking.ServerRules.Find(key);
        if (rule != null) page.AdminSetting(Networking.ServerRulesRows.Spec(rule), Networking.ServerRulesRows.Shown);
    }

    /// <summary>
    /// SERVERLINK1: whether this player sees any of Progenitor's own server settings under On this server (its admin rows
    /// show only to an admin of the joined server); the Server tab's link lands on that group only then.
    /// </summary>
    internal static bool OnThisServerSettingsShown()
    {
        try
        {
            return Networking.JoinAdminRows.Shown(Networking.JoinAdminRows.PhotoMode().Key)
                || Networking.JoinAdminRows.Shown(Networking.JoinAdminRows.AllySafeHits().Key);
        }
        catch { return false; }
    }

    private static void Admin(SettingsPageBuilder page, AdminSettingSpec spec)
    {
        string op = spec.Key;
        page.AdminSetting(spec, () => Networking.JoinAdminRows.Shown(op));
    }

    /// <summary>A page button reports a refusal as its failure (the row's help line says it).</summary>
    private static void RunOrThrow(SettingActionResult result)
    {
        if (!result.Completed) throw new InvalidOperationException(result.Message);
    }
}
