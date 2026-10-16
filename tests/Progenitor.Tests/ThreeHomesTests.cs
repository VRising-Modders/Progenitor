using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BepInEx.Configuration;
using Progenitor.Config;
using Progenitor.Dev.Ops;
using Progenitor.Install;
using Progenitor.Networking;
using Progenitor.Plugins;
using Progenitor.Ui;
using Progenitor.WorldState;
using Xunit;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.Tests;

/// <summary>
/// Three homes (docs/settings-organization.md, "Three homes (2026-10-06)"), the parts that need no Mods window: each
/// setting's one home, the drawer threshold and its stored state, and the left list's highlight rules. Game-free.
/// </summary>
public sealed class ThreeHomesModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "progenitor-three-homes-" + Guid.NewGuid().ToString("N"));

    public ThreeHomesModelTests()
    {
        Directory.CreateDirectory(_dir);
        CoreUiSettings.Reset();
        SettingsDrawers.ResetMemory();
    }

    public void Dispose()
    {
        CoreUiSettings.Reset();
        SettingsDrawers.ResetMemory();
        try { Directory.Delete(_dir, true); } catch { }
    }

    // ---- placement: one home each -----------------------------------------------------------------

    [Fact]
    public void Every_drawn_setting_has_exactly_one_home_among_the_three_tabs()
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var place in SettingPlaces.All)
        {
            Assert.True(ids.Add(place.SettingId), place.SettingId + " is listed twice");
            string tab = SettingPlaces.HomeTab(place.Home);
            Assert.Contains(tab, new[] { "preferences", "server", "mods", "cfg" });
            // A mirror is never the home again, and never a second home on another tab of the three.
            Assert.DoesNotContain(place.Home, place.AlsoAt);
            foreach (string mirror in place.AlsoAt)
                Assert.True(SettingPlaces.HomeTab(mirror) is "" || SettingPlaces.HomeTab(mirror) == tab,
                    place.SettingId + ": the mirror " + mirror + " is on another home than " + place.Home);
        }

        string[] HomedOn(string tab) => SettingPlaces.All.Where(p => SettingPlaces.HomeTab(p.Home) == tab).Select(p => p.SettingId).ToArray();

        // Preferences: how the Mods window behaves on this PC, and nothing about a server.
        Assert.All(HomedOn("preferences"), id => Assert.DoesNotContain("/JoinAdmin/", id));
        Assert.Contains("progenitor/Settings/EditOtherModsSettings", HomedOn("preferences"));
        Assert.Contains("progenitor/Mods/DedicatedServerPath", HomedOn("preferences"));
        // Progenitor's page: its own settings, and of the server's only its two toggles (On this server).
        var onThisServer = SettingPlaces.All.Where(p => p.Home == SettingPlaces.OnThisServer).Select(p => p.SettingId).ToArray();
        Assert.Equal(new[] { SettingPlaces.JoinAdminId(SettingPlaces.OpPhotoMode), SettingPlaces.JoinAdminId(SettingPlaces.OpAllySafeHits) }, onThisServer);
        Assert.All(HomedOn("mods"), id => Assert.True(!id.Contains("/ServerMessage/") && !id.Contains("/ServerRules/") && !id.Contains("/ServerList/"), id));
        // The Server tab: the server message, the server list, the rules, the operator rows, Players without Progenitor.
        foreach (string id in new[]
                 {
                     "progenitor/ServerMessage/Message", "progenitor/ServerList/Description", "progenitor/ServerRules/ClanSize",
                     "progenitor/ServerRules/Name", "progenitor/ServerMessage/NoProgenitorMessage",
                     SettingPlaces.JoinAdminId(SettingPlaces.OpAnswerTimeout), SettingPlaces.JoinAdminId(SettingPlaces.OpRemoteInstall),
                     SettingPlaces.JoinAdminId(SettingPlaces.OpAllowedSteamIds), SettingPlaces.JoinAdminId(SettingPlaces.OpNoticeSeconds),
                 })
            Assert.Contains(id, HomedOn("server"));
        Assert.Equal(SettingPlaces.ServerRulesPlace, SettingPlaces.Find("progenitor/ServerRules/ClanSize")!.Home);
        Assert.Equal(SettingPlaces.ServerList, SettingPlaces.Find("progenitor/ServerRules/Name")!.Home);
        // The mid-session and can't-be-checked choices: at home in the popup, drawn on the Server tab under Mods players need.
        foreach (string op in new[] { SettingPlaces.OpMidSession, SettingPlaces.OpUnverified })
            Assert.Equal(new[] { SettingPlaces.ModsPlayersNeed }, SettingPlaces.Find(SettingPlaces.JoinAdminId(op))!.AlsoAt);
        // The drawer memory is the window's own, never drawn.
        Assert.Equal(SettingPlaces.CfgOnly, SettingPlaces.Find("progenitor/Ui/ClosedDrawers")!.Home);
        Assert.False(SettingPlaces.DrawnOnProgenitorPage("progenitor/ServerMessage/Message", fromSharedFile: false));
        Assert.True(SettingPlaces.DrawnOnProgenitorPage(SettingPlaces.JoinAdminId(SettingPlaces.OpPhotoMode), fromSharedFile: false));
    }

    [Fact]
    public void Progenitors_registration_splits_into_two_homes_that_never_share_a_group()
    {
        var builder = new SettingsPageBuilderProbe();
        var sections = builder.Build(page =>
        {
            ProgenitorPage.BuildYourSettings(page);
            ProgenitorPage.BuildOnThisServer(page);
            ProgenitorPage.BuildServerTab(page, new ProgenitorPage.ServerParts
            {
                WhenPlayersJoin = p => p.Button("Show", () => { }, label: "Server message").InScope(SettingScope.ServerWide),
                ServerList = p => p.Section(ProgenitorPage.ServerListTitle).Info("Room left", () => "128").InScope(SettingScope.ServerWide),
            });
        });
        var page = ProgenitorPage.SectionsFor(sections, serverTab: false).Select(s => s.Title).ToArray();
        var server = ProgenitorPage.SectionsFor(sections, serverTab: true).Select(s => s.Title).ToArray();
        Assert.Empty(page.Intersect(server));
        Assert.Equal(ProgenitorPage.ServerTabTitles, server);
        Assert.Equal(new[]
        {
            ProgenitorPage.KeysTitle, ProgenitorPage.PhotoModeTitle, ProgenitorPage.BuildMenuTitle, ProgenitorPage.EquipmentTitle, ProgenitorPage.ChatTitle,
            ProgenitorPage.PrivateGamesTitle, ProgenitorPage.GetHelpTitle, ProgenitorPage.OnThisServerSection,
        }, page);
        Assert.Equal(new[] { "When players join", "Mods players need", "Server listing", ServerRules.Title, "Chat on this server", "Players without Progenitor", "Updates", "Operator" },
            ProgenitorPage.ServerTabTitles);
        Assert.Equal("Server rules", ProgenitorPage.NavCaption(ServerRules.Title));
        // CHAT4: the Server tab's Chat group is listed as Chat; its heading differs from Your settings' Chat, which stays on the page.
        Assert.Equal("Chat", ProgenitorPage.NavCaption(ProgenitorPage.ChatServerTitle));
        Assert.Contains(ProgenitorPage.ChatTitle, page);
        Assert.Contains(ProgenitorPage.ChatServerTitle, server);
    }

    // ---- drawers ----------------------------------------------------------------------------------

    [Fact]
    public void Only_a_group_of_more_than_four_settings_is_a_drawer()
    {
        Assert.False(SettingsDrawers.IsDrawer(0));
        Assert.False(SettingsDrawers.IsDrawer(4));
        Assert.True(SettingsDrawers.IsDrawer(5));
        Assert.Equal(4, SettingsDrawers.Threshold);

        // Links, dividers and items marked Uncounted (a fold button, the admin-only note) are not settings.
        var builder = new SettingsPageBuilderProbe();
        var sections = builder.Build(page => page.Section("Group")
            .Button("A", () => { }).Button("B", () => { }).Button("C", () => { }).Button("D", () => { })
            .Info("Note", () => "x").Uncounted()
            .Divider());
        Assert.Equal(4, SettingsDrawers.CountOf(sections.Single()));
        Assert.False(SettingsDrawers.IsDrawer(SettingsDrawers.CountOf(sections.Single())));
        var five = builder.Build(page => page.Section("Group")
            .Button("A", () => { }).Button("B", () => { }).Button("C", () => { }).Button("D", () => { }).Info("E", () => "e"));
        Assert.True(SettingsDrawers.IsDrawer(SettingsDrawers.CountOf(five.Single())));
    }

    [Fact]
    public void A_drawers_state_is_remembered_in_the_ui_cfg_and_survives_a_restart_and_a_Core_reload()
    {
        string path = Path.Combine(_dir, "progenitor.ui.cfg");
        var file = new ConfigFile(path, saveOnInit: true);
        CoreUiSettings.ClosedDrawers = file.Bind(SettingsDrawers.Section, SettingsDrawers.Key, "", SettingsDrawers.Description);
        string operatorKey = SettingsDrawers.KeyOf(SettingsDrawers.ScopeServer, "Operator");
        Assert.Equal("server/operator", operatorKey);
        // First time: every drawer is open.
        Assert.True(SettingsDrawers.IsOpen(operatorKey));
        Assert.True(SettingsDrawers.SetOpen(operatorKey, false));
        Assert.False(SettingsDrawers.SetOpen(operatorKey, false));
        Assert.True(SettingsDrawers.SetOpen(SettingsDrawers.KeyOf(SettingsDrawers.ScopePreferences, "Sources and downloads"), false));
        Assert.Equal("preferences/sources-and-downloads,server/operator", SettingsDrawers.Stored);
        Assert.Contains("ClosedDrawers = preferences/sources-and-downloads,server/operator", File.ReadAllText(path));

        // A Core reload forgets the entry object; the next generation binds the same key of the same file again.
        CoreUiSettings.Reset();
        CoreUiSettings.ClosedDrawers = file.Bind(SettingsDrawers.Section, SettingsDrawers.Key, "", SettingsDrawers.Description);
        Assert.False(SettingsDrawers.IsOpen(operatorKey));
        Assert.True(SettingsDrawers.IsOpen(SettingsDrawers.KeyOf(SettingsDrawers.ScopeServer, "Server rules")));
        Assert.True(SettingsDrawers.SetOpen(operatorKey, true));
        Assert.Equal("preferences/sources-and-downloads", SettingsDrawers.Stored);

        // Without a bound entry (a server, a test): this session's memory.
        CoreUiSettings.Reset();
        Assert.True(SettingsDrawers.IsOpen(operatorKey));
        SettingsDrawers.SetOpen(operatorKey, false);
        Assert.False(SettingsDrawers.IsOpen(operatorKey));
        Assert.Equal(new[] { "a/b", "c/d" }, SettingsDrawers.Parse(" a/b; c/d ,, A/B").OrderBy(k => k).ToArray());
    }

    // ---- the left list --------------------------------------------------------------------------

    [Fact]
    public void Group_words_resolve_by_caption_or_slug()
    {
        Assert.Equal("sources-and-downloads", SectionNav.Slug("Sources and downloads"));
        Assert.Equal("server-rules-applied-at-the-next-restart", SectionNav.Slug(ServerRules.Title));
        Assert.Equal("nav/install-mods-tab", SectionNav.Key("Install Mods tab"));
        var groups = ConfigurationPane.ConfigGroups;
        Assert.Equal(ConfigurationPane.SourcesGroup, SectionNav.Resolve(groups, "sources-and-downloads"));
        Assert.Equal(ConfigurationPane.SourcesGroup, SectionNav.Resolve(groups, "nav/Sources and downloads"));
        Assert.Equal(ConfigurationPane.LocalServerGroup, SectionNav.Resolve(groups, "LOCAL SERVER"));
        Assert.Null(SectionNav.Resolve(groups, "Mods menu"));
        Assert.Equal("nav/sources-and-downloads", ModsMenu.NormalizeSectionLabel("nav/Sources and downloads"));
        Assert.Equal("drawer-operator", ModsMenu.NormalizeSectionLabel("drawer/Operator"));
        Assert.Equal("link-purpose/server", ModsMenu.NormalizeSectionLabel("link-purpose/server"));
    }

    [Fact]
    public void The_highlight_is_the_group_at_the_top_of_the_view()
    {
        // Heading tops below the view's top edge; null = not drawn or hidden.
        Assert.Equal(-1, SectionNav.ActiveIndex(new float?[] { null, null }));
        Assert.Equal(0, SectionNav.ActiveIndex(new float?[] { 40f, 400f, 900f }));     // nothing reached the top: the first
        Assert.Equal(0, SectionNav.ActiveIndex(new float?[] { 0f, 400f, 900f }));
        Assert.Equal(1, SectionNav.ActiveIndex(new float?[] { -500f, 10f, 600f }));   // within the tolerance
        Assert.Equal(1, SectionNav.ActiveIndex(new float?[] { -900f, -300f, 200f }));
        Assert.Equal(2, SectionNav.ActiveIndex(new float?[] { -900f, -600f, -1f }));
        Assert.Equal(2, SectionNav.ActiveIndex(new float?[] { null, -600f, -1f }));
        Assert.Equal(1, SectionNav.ActiveIndex(new float?[] { null, 300f, 800f }));  // the first drawn group
    }

    [Fact]
    public void A_jump_holds_the_highlight_until_the_player_scrolls()
    {
        var t = new SectionNavTracker();
        Assert.True(t.Observe(0f, frame: 1));
        Assert.True(t.Follow("Look and navigation"));
        Assert.False(t.Follow("Look and navigation"));
        Assert.False(t.Observe(0f, frame: 2));

        // A jump to the last group: the page cannot bring it to the top; its own scroll does not take the highlight.
        t.Jump("Local server", frame: 10);
        Assert.Equal("Local server", t.Highlighted);
        Assert.Equal("Local server", t.Jumped);
        Assert.True(t.Pinned);
        Assert.False(t.Observe(350f, frame: 11));
        Assert.False(t.Observe(380f, frame: 12));
        Assert.False(t.Follow("Sources and downloads"));
        Assert.False(t.Observe(380f, frame: 10 + SectionNavTracker.SettleFrames + 5));
        Assert.Equal("Local server", t.Highlighted);

        // The player scrolls: the highlight follows the view again.
        Assert.True(t.Observe(200f, frame: 40));
        Assert.False(t.Pinned);
        Assert.True(t.Follow("Sources and downloads"));
        Assert.Equal("Sources and downloads", t.Highlighted);
        Assert.Equal("Local server", t.Jumped);
        t.Reset();
        Assert.Equal("", t.Highlighted);
    }

    /// <summary>A builder run outside the registry (the sections a registration would hold).</summary>
    private sealed class SettingsPageBuilderProbe
    {
        private int _n;

        internal List<ModSettingsSection> Build(Action<SettingsPageBuilder> build)
        {
            string id = "probe.three.homes." + (_n++).ToString(System.Globalization.CultureInfo.InvariantCulture);
            using var registration = ModSettingsPages.Register(new object(), id, build);
            return registration.Sections();
        }
    }
}

/// <summary>
/// Three homes in the Mods window: the Preferences tab's drawers and left list, the Server tab (admin, a player who is not
/// an admin, not connected, a server without Progenitor), Progenitor's page's left list, back and forward, the lock list
/// wherever a row is drawn, and the bridge ops. Game-free: the fake UI runtime.
/// </summary>
[Collection("ServerSettingsLock")]
public sealed class ThreeHomesMenuTests : IDisposable
{
    private readonly UiTests.FakeUiRuntime _runtime = new();
    private readonly TestLogSource _log = new();
    private readonly string _dir;
    private readonly object _owner = new();
    private readonly ConfigFile _shared;
    private readonly ConfigFile _ui;
    private readonly List<IDisposable> _handles = new();
    private readonly List<JoinAdminRequest> _sent = new();
    private JsonDocument? _rows;
    private SettingsScopeContext _context;
    private bool _admin = true;
    private JoinAdminState? _state;

    public ThreeHomesMenuTests()
    {
        UiLayoutBatch.ResetForTests();
        UiApi.ResetForTests();
        UiApi.SetRuntimeForTests(_runtime);
        Plugin.Logger = _log;
        HarmonySlotHandoff.SetSkipModules(null, refusedLiveWorld: false);
        Handshake.ResetTables();
        ModMenuInventory.ResetForTests();
        WorldEpoch.ResetForTests();
        SettingsCatalog.ResetForTests();
        ModSettingsPages.ResetForTests();
        ConfigSync.ResetForTests();
        Keybinds.ResetForTests();
        CoreUiSettings.Reset();
        SettingsDrawers.ResetMemory();
        ModsMenu.ResetNavigationForTests();
        ModsMenu.LedgerForTests = Array.Empty<InstalledLedgerRow>();
        ModsMenu.SetInstallCatalogForTests(null);
        ModsMenu.KeybindsPostForTests = step => step();
        _context = Joined(admin: true);
        ModsMenu.ScopeContextForTests = _ => _context;
        ConfigurationPane.ResetServerInstallStateForTests();
        _dir = Path.Combine(Path.GetTempPath(), "progenitor-three-homes-menu-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _shared = new ConfigFile(Path.Combine(_dir, ConfigHelper.SharedFileName), saveOnInit: true);
        _ui = new ConfigFile(Path.Combine(_dir, "progenitor.ui.cfg"), saveOnInit: true);
        CoreUiSettings.EditOtherModsSettings = _ui.Bind(CoreUiSettings.EditOtherModsSection, CoreUiSettings.EditOtherModsKey, false, "x");
        CoreUiSettings.MenuHistoryInnerTabs = _ui.Bind("Ui", "MenuHistoryInnerTabs", true, "x");
        CoreUiSettings.ClosedDrawers = _ui.Bind(SettingsDrawers.Section, SettingsDrawers.Key, "", SettingsDrawers.Description);
        ModsMenu.SetConfigFileForTests(_shared);
        ModsMenu.BindModsConfig(_shared);
        ModPagePane.DialogFactory = title => UiApi.Panel(title, _owner, SettingsPanel.PluginId);
        _state = State();
        JoinAdminRows.Reset();
        JoinAdminRows.AdminProbe = () => _admin;
        JoinAdminRows.StateProbe = () => _state;
        JoinAdminRows.SelfSteamId = () => 76561198000000011UL;
        JoinAdminRows.Requester = (request, answer) =>
        {
            if (request.Op != JoinAdminOps.Get) _sent.Add(request);
            return null;
        };
        ModsMenu.JoinAdminValueReader = JoinAdminRows.ReadPlace;
        ServerSettingsLock.Reset();
        ServerSettingsLock.ClientProbe = JoinAdminRows.LockedBy;
        ServerSettingsLock.ClientFingerprint = JoinAdminRows.LockFingerprint;
        _handles.Add(ModSettingsPages.Register(_owner, SettingsPanel.PluginId, page =>
        {
            ProgenitorPage.BuildYourSettings(page);
            ProgenitorPage.BuildOnThisServer(page);
            ProgenitorPage.BuildServerTab(page, new ProgenitorPage.ServerParts
            {
                WhenPlayersJoin = p => p
                    .Button("Show", () => { }, label: "Server message").InScope(SettingScope.ServerWide)
                    .Button("View", () => { }, label: "This server's mods").InScope(SettingScope.ServerWide)
                    .Button("Preview", () => { }, label: "Preview").InScope(SettingScope.ServerWide)
                    .Info("Countdown now", () => "3d", refreshSeconds: 0).InScope(SettingScope.ServerWide)
                    .Info("Next reset", () => "None", refreshSeconds: 0).InScope(SettingScope.ServerWide),
                PlayersJoiningRow = p => p.Button(JoinServerSettings.PlayersJoiningButton, () => { }, label: JoinServerSettings.PlayersJoiningLabel,
                    showIf: () => _admin).InScope(SettingScope.ServerWide),
                ServerList = p => p.Section(ProgenitorPage.ServerListTitle).Info("Room left", () => "128", refreshSeconds: 0).InScope(SettingScope.ServerWide),
                NoProgenitorMessage = p => p.Info("Message without Progenitor", () => "(default)", refreshSeconds: 0).InScope(SettingScope.ServerWide),
            });
        }));
    }

    public void Dispose()
    {
        _rows?.Dispose();
        foreach (var h in _handles) h.Dispose();
        // A left-list jump to Progenitor's groups opens the Required group; later tests expect it collapsed.
        ModsMenu.SetRequiredGroupExpandedForTests(false);
        ModsMenu.ScopeContextForTests = null;
        ModsMenu.JoinAdminValueReader = null;
        ModsMenu.ServerRulesValueReader = null;
        ModPagePane.DialogFactory = null;
        JoinAdminRows.Reset();
        ServerRulesRows.Reset();
        ServerSettingsLock.Reset();
        UiLayoutBatch.ResetForTests();
        ModsMenu.Close();
        ModSettingsPages.ResetForTests();
        ModsMenu.LedgerForTests = Array.Empty<InstalledLedgerRow>();
        ModsMenu.SetInstallCatalogForTests(null);
        ModsMenu.ResetTabBindingsForTests();
        ModsMenu.ResetNavigationForTests();
        ModsWorkspaceFrame.ResetForTests();
        ModsMenu.UninstallLifecycleWatch();
        Handshake.UninstallClientServerContextWatch();
        ModMenuInventory.ResetForTests();
        Handshake.ResetTables();
        WorldEpoch.ResetForTests();
        SettingsCatalog.ResetForTests();
        ConfigSync.ResetForTests();
        Keybinds.ResetForTests();
        CoreUiSettings.Reset();
        SettingsDrawers.ResetMemory();
        ConfigurationPane.ResetServerInstallStateForTests();
        ConfigHelper.UninstallAll();
        Plugin.Logger = null;
        UiApi.ResetForTests();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static SettingsScopeContext Joined(bool admin) => new()
    {
        Connection = SettingConnection.Progenitor,
        ServerPart = ServerPartState.Running,
        CanEditServerWide = admin,
        ServerName = "Night Garden",
        ServerAddress = "127.0.0.1:9976",
    };

    private static JoinAdminState State(params string[] lockList)
    {
        var s = new JoinAdminState { PolicySupported = true, Unverified = "allow", MidSession = "warn", OptionsSupported = true };
        s.Options[ServerOptionOps.AnswerTimeout] = "20";
        s.Options[ServerOptionOps.PhotoMode] = "true";
        s.Options[ServerOptionOps.AllySafeHits] = "true";
        s.Options[ServerOptionOps.RemoteInstall] = "true";
        s.Options[ServerOptionOps.AllowedSteamIds] = "";
        s.Options[ServerOptionOps.NoticeSeconds] = "5";
        s.Options[ServerOptionOps.NoticeRepeatSeconds] = "3";
        s.Options[ServerOptionOps.NoticeCreationLimitSeconds] = "600";
        foreach (string op in ServerChatOptions.AdminOps) s.Options[op] = op == ServerOptionOps.ChatRelayUrl ? "set" : ServerChatOptions.DefaultOf(op);
        s.Lock.AddRange(lockList);
        return s;
    }

    private static ServerRulesState RulesState()
    {
        var s = new ServerRulesState { PasswordSet = true, PasswordNextSet = true, RestartPending = 0, Territory = "Moderate", DaysRunning = 33,
            StartDate = "2026-09-02", AdminReserve = 4, MaxPlayersMax = 124 };
        foreach (var spec in ServerRules.Specs)
            s.Rows.Add(new ServerRuleRow { Key = spec.Key, Next = spec.Key switch
            {
                ServerRules.Name => "Night Garden", ServerRules.ClanSize => "4", ServerRules.Password => "set", ServerRules.GameModeType => "PvP",
                ServerRules.GameDifficulty => "Normal", ServerRules.BloodBoundEquipment => "true", ServerRules.CastleHeartDamageMode => ServerRules.Decaying,
                ServerRules.MaxConnectedUsers => "40", ServerRules.ResetDaysInterval => "0", _ => "Any",
            }, Loaded = null, Editable = true, Source = "local" });
        return s;
    }

    private void WithRules()
    {
        var rules = RulesState();
        ServerRulesRows.SupportedProbe = () => true;
        ServerRulesRows.StateProbe = () => rules;
        ServerRulesRows.CanEditProbe = () => _admin;
        ServerRulesRows.Requester = (_, _) => null;
    }

    private JsonElement Read(string pane)
    {
        _rows?.Dispose();
        _rows = JsonDocument.Parse(JsonSerializer.Serialize(ModsMenu.ReadVisibleWidgets(pane)));
        return _rows.RootElement;
    }

    private static List<JsonElement> All(JsonElement rows, string key)
    {
        var list = new List<JsonElement>();
        foreach (var row in rows.EnumerateArray())
            if (row.ValueKind == JsonValueKind.Object && row.TryGetProperty(key, out var value)) list.Add(value);
        return list;
    }

    private static List<JsonElement> AdminRows(JsonElement rows) =>
        All(rows, ModSettingsView.RowSetting).Where(s => s.GetProperty("kind").GetString() == "admin-setting").ToList();

    private void Open(string tab)
    {
        if (!ModsMenu.IsOpen) ModsMenu.Open();
        ModsMenu.SelectTab(tab);
    }

    private static string NavPlace() => (string)((Dictionary<string, object?>)ModsMenu.NavState()["current"]!)["place"]!;

    private static Dictionary<string, object?> SectionNavState() => (Dictionary<string, object?>)ModsMenu.NavState()["sectionNav"]!;

    private Dictionary<string, JsonElement> Sections(JsonElement rows) =>
        All(rows, ModSettingsView.RowSection).ToDictionary(s => s.GetProperty("title").GetString()!, s => s);

    // ---- Preferences ------------------------------------------------------------------------------

    [Fact]
    public void Preferences_groups_have_four_settings_or_fewer_so_each_is_a_plain_subheading()
    {
        Open("preferences");
        var sections = Sections(Read("content"));
        // The 2026-10-06 trim (user: "No user will care to change those"): Mods tab (1), Install Mods tab (2) and Sources and
        // downloads (3; the Advanced fold and its two rows are not counted) are gold subheadings, as is Local server.
        foreach (var (group, count) in new[] { (ConfigurationPane.ModsTabGroup, 1), (ConfigurationPane.InstallTabGroup, 2), (ConfigurationPane.SourcesGroup, 3) })
        {
            Assert.False(sections[group].GetProperty("drawer").GetBoolean(), group);
            Assert.Equal(count, sections[group].GetProperty("count").GetInt32());
            Assert.Null(_runtime.CellRow(ModSettingsScopes.DrawerKeyPrefix + SectionNav.Slug(group)));
        }

        Assert.False(sections[ConfigurationPane.LocalServerGroup].GetProperty("drawer").GetBoolean());
        Assert.Empty(ModsMenu.ConfigurationPaneForTests!.SettingsView!.DrawersReadback());

        // Opening the Advanced fold adds its two rows without turning Sources into a drawer.
        try
        {
            Assert.NotNull(ModsMenu.ClickVisibleWidget("content", ConfigurationPane.AdvancedShowCaption, null, null));
            ModsMenu.SelectTab("preferences");
            sections = Sections(Read("content"));
            Assert.False(sections[ConfigurationPane.SourcesGroup].GetProperty("drawer").GetBoolean());
            Assert.Equal(3, sections[ConfigurationPane.SourcesGroup].GetProperty("count").GetInt32());
            Assert.Contains(All(Read("content"), ModSettingsView.RowSetting), s => s.GetProperty("id").GetString() == "progenitor/Mods/IndexPath");
        }
        finally { ConfigurationPane.AdvancedOpen = false; }
    }

    [Fact]
    public void A_drawer_header_is_the_fold_marker_the_gold_title_and_the_count()
    {
        Open("server");
        var sections = Sections(Read("content"));
        var drawer = _runtime.CellRow(ModSettingsScopes.DrawerKeyPrefix + "operator")!;
        Assert.True(drawer.Expanded);
        Assert.True(sections[ProgenitorPage.OperatorTitle].GetProperty("open").GetBoolean(), "open the first time");
        // Live polish 2026-10-06: the fold marker (the game's arrow, gold, at the title's size) left of the title, then the
        // muted count in parentheses; the whole row lights under the pointer and takes the click.
        var cells = drawer.Cells();
        Assert.Equal(ModSettingsScopes.CellDrawerChevron, cells[0].Key);
        Assert.Equal(CellSkin.Expander, cells[0].Skin);
        Assert.Equal(ModSettingsScopes.SubheadingTone, cells[0].Tone);
        Assert.Equal(ModSettingsScopes.SubheadingFontSize, cells[0].FontSizeOverride);
        Assert.Equal(ModSettingsView.CellTitle, cells[1].Key);
        Assert.True(cells[1].SmallCaps);
        Assert.Equal(ModSettingsScopes.SubheadingTone, cells[1].Tone);
        Assert.Equal(ModSettingsScopes.CellDrawerCount, cells[2].Key);
        Assert.Equal("(5)", cells[2].Text);
        Assert.Equal(CellTone.Muted, cells[2].Tone);
        var spec = ModSettingsScopes.DrawerRow("Operator", 5, false);
        Assert.True(spec.QuietHover && spec.Soft && !spec.Plain);
        var chevron = sections[ProgenitorPage.OperatorTitle].GetProperty("chevron");
        Assert.Equal("open", chevron.GetProperty("state").GetString());
        Assert.Equal("left", chevron.GetProperty("side").GetString());
        Assert.True(chevron.GetProperty("shown").GetBoolean());
        Assert.True(sections[ProgenitorPage.OperatorTitle].GetProperty("titleShown").GetBoolean());
        Assert.NotNull(ModsMenu.ClickVisibleWidget("content", "drawer-operator/" + ModSettingsScopes.CellDrawerChevron, null, null));
        Assert.Equal("closed", Sections(Read("content"))[ProgenitorPage.OperatorTitle].GetProperty("chevron").GetProperty("state").GetString());
        Assert.False(drawer.Expanded);

        // Every group title on the three homes has one style: drawers and plain subheadings alike ("UI" drew lighter).
        var plain = ModSettingsScopes.PlainSubheadingRow(3, "Build menu", false);
        var title = plain.Cells.Single(c => c.Key == ModSettingsView.CellTitle);
        Assert.Equal(cells[1].Kind, title.Kind);
        Assert.Equal(cells[1].Tone, title.Tone);
        Assert.Equal(cells[1].FontSizeOverride, title.FontSizeOverride);
        Assert.True(title.SmallCaps);
        Assert.True(plain.Plain);
    }

    /// <summary>
    /// Live 2026-10-06 (user screenshot): after a value change the drawer headers drew <c>ION (5)</c> and <c>NLOADS (5)</c>,
    /// with no fold marker. The value change rewrote each drawer's count cell in place, and the in-place re-placement used
    /// the panel's full content width instead of the settings grid's, so the cells were packed from a left edge outside the
    /// grid row. On the real layout path (the column model at the width the row is placed at), a drawer header re-laid out
    /// in place keeps its marker and its whole title inside the row, at several grid widths.
    /// </summary>
    [Theory]
    [InlineData(640f, "Sources and downloads")]
    [InlineData(820f, "Look and navigation")]
    [InlineData(1100f, "When players join")]
    public void A_drawer_header_re_laid_out_in_place_keeps_its_marker_and_its_whole_title(float gridWidth, string group)
    {
        // The content row is wider than the grid row beside the description column.
        float contentWidth = gridWidth + 400f;
        var spec = ModSettingsScopes.DrawerRow(group, 5, false);

        Dictionary<string, object?> FitAt(float placedAt)
        {
            var cells = MenuPanelLayout.CellColumns(placedAt, spec)
                .Where(p => p.Visible)
                .Select(p => (p.Key, p.X - p.Width * .5f, p.Width))
                .ToList();
            // The row is drawn at the grid's width, centered on its own origin.
            return ModSettingsScopes.DrawerHeadingFit(-gridWidth * .5f, gridWidth, cells, group);
        }

        // The layout pass places the row at the grid width: the marker and the whole title are inside it.
        var laidOut = FitAt(gridWidth);
        Assert.Equal(true, laidOut["chevronShown"]);
        Assert.Equal(true, laidOut["titleShown"]);
        Assert.True((double)laidOut["titleWidth"]! >= (double)laidOut["titleNeeds"]!);

        // A value change rewrites the count in place: the re-placement reuses the width the layout pass placed the row at.
        float inPlace = MenuPanelLayout.InPlaceCellRowWidth(gridWidth, contentWidth);
        Assert.Equal(gridWidth, inPlace);
        var refreshed = FitAt(inPlace);
        Assert.Equal(true, refreshed["chevronShown"]);
        Assert.Equal(true, refreshed["titleShown"]);
        Assert.Equal(laidOut["titleLeft"], refreshed["titleLeft"]);
        Assert.Equal(laidOut["chevronLeft"], refreshed["chevronLeft"]);

        // The defect: re-placed at the content width, the marker and the start of the title fall left of the row.
        var broken = FitAt(contentWidth);
        Assert.Equal(false, broken["chevronShown"]);
        Assert.Equal(false, broken["titleShown"]);
        // Before the first layout pass there is no placed width yet: the content width stands in.
        Assert.Equal(contentWidth, MenuPanelLayout.InPlaceCellRowWidth(0f, contentWidth));
        Assert.Equal(contentWidth, MenuPanelLayout.InPlaceCellRowWidth(float.NaN, contentWidth));
    }

    [Fact]
    public void After_a_value_change_a_drawer_header_still_reads_its_marker_and_its_whole_title()
    {
        Open("server");
        var view = ModsMenu.ServerPaneForTests!.SettingsView!;
        var drawer = _runtime.CellRow(ModSettingsScopes.DrawerKeyPrefix + "operator")!;
        drawer.Applied.Clear();
        // A committed change re-evaluates the page in place: the count cell is rewritten (the path that broke live).
        view.Reevaluate();
        Assert.Contains(ModSettingsScopes.CellDrawerCount, drawer.Applied);
        var section = Sections(Read("content"))[ProgenitorPage.OperatorTitle];
        Assert.True(section.GetProperty("chevron").GetProperty("shown").GetBoolean());
        Assert.True(section.GetProperty("titleShown").GetBoolean());
        var heading = section.GetProperty("heading");
        Assert.True(heading.GetProperty("titleWidth").GetDouble() >= heading.GetProperty("titleNeeds").GetDouble());
        Assert.Equal(ModSettingsScopes.CellDrawerChevron, drawer.Cells()[0].Key);

        // A rebuild (another tab and back, the scroll restore) reads the same.
        Open("preferences");
        Open("server");
        section = Sections(Read("content"))[ProgenitorPage.OperatorTitle];
        Assert.True(section.GetProperty("chevron").GetProperty("shown").GetBoolean());
        Assert.True(section.GetProperty("titleShown").GetBoolean());
    }

    /// <summary>
    /// Live 2026-10-06 (third pass): the Local server labels started about 8 px left of the settings rows' names. On the
    /// real layout path (the settings grid's column model for a settings row's name, and the cell layout for these rows at
    /// the same grid width) the label's left edge is the name's left edge, at several grid widths.
    /// </summary>
    [Theory]
    [InlineData(640f)]
    [InlineData(820f)]
    [InlineData(1100f)]
    public void Local_server_labels_start_on_the_settings_rows_name_column(float gridWidth)
    {
        var name = ModSettingsRowGrid.For(gridWidth).Columns(hasControl: true);
        float nameLeft = name.LabelX - name.LabelWidth * .5f;
        foreach (var (spec, cell) in new[]
                 {
                     (ConfigurationPane.FolderRowSpec(@"C:\Program Files\VRising Dedicated Server"), ConfigurationPane.CellFolderLabel),
                     (ConfigurationPane.StatusRowSpec(ConfigurationPane.RowFolderStatus, ConfigurationPane.StatusRowLabel, @"Validated: C:\x"), ConfigurationPane.CellStatusLabel),
                     (ConfigurationPane.StatusRowSpec(ConfigurationPane.RowServerStatus, ConfigurationPane.ServerProgenitorRowName, ConfigurationPane.ServerRunningLine), ConfigurationPane.CellStatusLabel),
                 })
        {
            var label = MenuPanelLayout.CellColumns(gridWidth, spec).Single(p => p.Key == cell);
            Assert.Equal(nameLeft, label.X - label.Width * .5f, 2);
        }

        Assert.Equal(ModSettingsRowGrid.NameInset, ConfigurationPane.LayoutLabelOffset(ConfigurationPane.CellStatusLabel, gridWidth), 2);
    }

    [Fact]
    public void Local_server_is_laid_out_like_the_other_rows()
    {
        Open("preferences");
        var content = Read("content");
        // The folder: its name in the name column, the field, Apply and Clear inline; the status a muted info row.
        // Live 2026-10-06 (second pass): the field takes the rest of the row; Apply and Clear are on the line under it.
        var folder = _runtime.CellRow(ConfigurationPane.RowFolder)!;
        Assert.Equal(new[] { ConfigurationPane.CellNameLead, ConfigurationPane.CellFolderLabel, ConfigurationPane.CellFolderPath }, folder.Cells().Select(c => c.Key).ToArray());
        Assert.Equal(ConfigurationPane.ServerFolderLabel, folder.Cells()[1].Text);
        Assert.Equal(CellKind.Input, folder.Cells()[2].Kind);
        Assert.Equal(CellWidthMode.Fill, folder.Cells()[2].Width.Mode);
        var actions = _runtime.CellRow(ConfigurationPane.RowFolderActions)!;
        Assert.Equal(new[] { "Apply", "Clear" }, actions.Cells().Where(c => c.Kind == CellKind.Button).Select(c => c.Text).ToArray());
        Assert.All(actions.Cells().Where(c => c.Kind == CellKind.Button), c => Assert.Equal(CellSkin.KeyCap, c.Skin));
        Assert.True(_runtime.CellRows.IndexOf(actions) == _runtime.CellRows.IndexOf(folder) + 1);
        // The Local server rows are grid rows (the settings rows' width and left) and their label starts on the grid's name
        // column; widget_read reports where it was drawn.
        var labelColumn = All(Read("content"), ConfigurationPane.RowLabelColumn).ToList();
        Assert.Equal(2, labelColumn.Count);
        Assert.All(labelColumn, l => Assert.Equal((double)ModSettingsRowGrid.NameInset, l.GetProperty("layoutOffset").GetDouble(), 1));
        // Typing reaches the draft; Apply on the line under it applies it.
        Assert.True(UiCellRowHandle.TryInvoke(ConfigurationPane.RowFolderActions + "/" + ConfigurationPane.CellFolderClear, out _));
        var status = _runtime.CellRow(ConfigurationPane.RowFolderStatus)!;
        Assert.Equal(new[] { ConfigurationPane.CellNameLead, ConfigurationPane.CellStatusLabel, ConfigurationPane.CellStatusValue }, status.Cells().Select(c => c.Key).ToArray());
        Assert.All(status.Cells(), c => Assert.Equal(CellTone.Muted, c.Tone));
        Assert.Equal(ConfigurationPane.StatusRowLabel, status.Cells()[1].Text);
        Assert.Equal(ConfigurationPane.FolderNotConfigured, status.Cells()[2].Text);
        Assert.Equal(ConfigurationPane.FolderNotConfigured,
            All(Read("content"), ConfigurationPane.RowLocalStatus).Single().GetProperty("text").GetString());
        Assert.Equal("Advanced sources", ConfigurationPane.AdvancedLabel);
    }

    [Fact]
    public void Closing_a_drawer_hides_its_rows_in_place_and_stays_closed_across_a_rebuild_and_a_Core_reload()
    {
        Open("server");
        bool Shown() => AdminRows(Read("content")).Any(r => r.GetProperty("key").GetString() == ServerOptionOps.RemoteInstall);
        Assert.True(Shown());

        // A click on the header (widget_click label=drawer/<group>) closes it.
        Assert.NotNull(ModsMenu.ClickVisibleWidget("content", "drawer/Operator", null, null));
        Assert.False(Shown());
        Assert.False(_runtime.CellRow("drawer-operator")!.Expanded);
        Assert.Contains("server/operator", CoreUiSettings.ClosedDrawers!.Value);
        var section = Sections(Read("content"))[ProgenitorPage.OperatorTitle];
        Assert.False(section.GetProperty("open").GetBoolean());
        Assert.Equal(5, section.GetProperty("count").GetInt32());

        // Another tab and back, then a Core reload (the entry re-bound from the file): still closed.
        Open("preferences");
        Open("server");
        Assert.False(Shown());
        CoreUiSettings.Reset();
        CoreUiSettings.EditOtherModsSettings = _ui.Bind(CoreUiSettings.EditOtherModsSection, CoreUiSettings.EditOtherModsKey, false, "x");
        CoreUiSettings.MenuHistoryInnerTabs = _ui.Bind("Ui", "MenuHistoryInnerTabs", true, "x");
        CoreUiSettings.ClosedDrawers = _ui.Bind(SettingsDrawers.Section, SettingsDrawers.Key, "", SettingsDrawers.Description);
        ModsMenu.SelectTab("server");
        Assert.False(Shown());

        // The bridge op opens it again.
        var op = (Dictionary<string, object?>)ModsMenu.DrawerOp("operator", open: true);
        Assert.Equal(true, op["open"]);
        Assert.True(Shown());
        // Mod compatibility C3: Version matching made Mods players need a drawer (five settings); Players without Progenitor is not one.
        Assert.Throws<ArgumentException>(() => ModsMenu.DrawerOp(ProgenitorPage.PlayersWithoutProgenitorTitle, null));
    }

    [Fact]
    public void A_left_list_click_scrolls_to_its_group_opens_a_closed_drawer_highlights_it_and_is_a_place()
    {
        // Preferences lists its four groups.
        Open("preferences");
        Assert.Equal(ConfigurationPane.ConfigGroups, All(Read("list"), SectionNav.RowSectionNav).Select(n => n.GetProperty("group").GetString()).ToArray());
        Assert.Throws<ArgumentException>(() => ModsMenu.SectionOp(null, "Look and navigation"));

        SettingsDrawers.SetOpen("server/operator", false);
        Open("server");
        ModsMenu.TickNavigation();
        Assert.Equal("server", NavPlace());
        Assert.False(ModsMenu.ServerPaneForTests!.SettingsView!.DrawerOpen(ProgenitorPage.OperatorTitle));

        Assert.NotNull(ModsMenu.ClickVisibleWidget("list", "nav/Operator", null, null));
        ModsMenu.TickNavigation();
        Assert.Equal("server@" + ProgenitorPage.OperatorTitle, NavPlace());
        Assert.True(ModsMenu.ServerPaneForTests!.SettingsView!.DrawerOpen(ProgenitorPage.OperatorTitle));
        Assert.True(SettingsDrawers.IsOpen("server/operator"));
        var state = SectionNavState();
        Assert.Equal(ProgenitorPage.OperatorTitle, state["highlighted"]);
        var jump = (Dictionary<string, object?>)state["lastJump"]!;
        Assert.Equal(true, jump["found"]);
        Assert.Equal(true, jump["openedDrawer"]);
        Assert.True(_runtime.CellRow(SectionNav.Key(ProgenitorPage.OperatorTitle))!.Selected);
        Assert.False(_runtime.CellRow(SectionNav.Key(ProgenitorPage.WhenPlayersJoinTitle))!.Selected);
        var row = All(Read("list"), SectionNav.RowSectionNav).Single(n => n.GetProperty("group").GetString() == ProgenitorPage.OperatorTitle);
        Assert.True(row.GetProperty("highlighted").GetBoolean());

        // Another group, then back and forward walk the jumps.
        Assert.NotNull(ModsMenu.SectionOp(null, "server-list"));
        ModsMenu.TickNavigation();
        Assert.Equal("server@" + ProgenitorPage.ServerListTitle, NavPlace());
        Assert.True(ModsMenu.NavigateBack());
        ModsMenu.TickNavigation();
        Assert.Equal("server@" + ProgenitorPage.OperatorTitle, NavPlace());
        Assert.Equal(ProgenitorPage.OperatorTitle, SectionNavState()["highlighted"]);
        Assert.True(ModsMenu.NavigateForward());
        ModsMenu.TickNavigation();
        Assert.Equal("server@" + ProgenitorPage.ServerListTitle, NavPlace());
    }

    // ---- the Server tab -----------------------------------------------------------------------------

    [Fact]
    public void As_an_admin_the_Server_tab_is_the_servers_name_its_edit_rule_and_the_groups_in_order()
    {
        WithRules();
        Open("server");
        var content = Read("content");
        var head = All(content, ServerTabPane.RowHead).Single();
        Assert.Equal("ready", head.GetProperty("state").GetString());
        Assert.Equal("Night Garden", head.GetProperty("title").GetString());
        Assert.Equal("How this server is set up · admins edit, others read only", head.GetProperty("line").GetString());
        Assert.True(head.GetProperty("canEdit").GetBoolean());
        Assert.Equal(ProgenitorPage.ServerTabTitles, All(content, ModSettingsView.RowSection).Select(s => s.GetProperty("title").GetString()));

        var sections = Sections(content);
        Assert.True(sections[ProgenitorPage.OperatorTitle].GetProperty("drawer").GetBoolean());
        Assert.Equal(5, sections[ProgenitorPage.OperatorTitle].GetProperty("count").GetInt32());
        Assert.True(sections[ServerRules.Title].GetProperty("drawer").GetBoolean());
        Assert.True(sections[ProgenitorPage.WhenPlayersJoinTitle].GetProperty("drawer").GetBoolean());
        // Mod compatibility C3: Version matching is its fifth setting, so Mods players need is a drawer now.
        Assert.True(sections[ProgenitorPage.ModsPlayersNeedTitle].GetProperty("drawer").GetBoolean());
        Assert.False(sections[ProgenitorPage.PlayersWithoutProgenitorTitle].GetProperty("drawer").GetBoolean());
        // The admin edits: the admin channel rows are drawn, the admin-only note is not.
        Assert.Contains(AdminRows(content), r => r.GetProperty("key").GetString() == ServerOptionOps.RemoteInstall);
        Assert.DoesNotContain(All(content, ModSettingsView.RowSetting), r => r.GetProperty("key").GetString() == "info/" + ProgenitorPage.AdminOnlyGroupLabel);
        Assert.Equal(true, AdminRows(content).Single(r => r.GetProperty("key").GetString() == ServerRules.ClanSize).GetProperty("enabled").GetBoolean());

        // Its left list: the groups, Server rules by its short name.
        var nav = All(Read("list"), SectionNav.RowSectionNav).Select(n => n.GetProperty("group").GetString()).ToArray();
        Assert.Equal(new[] { "When players join", "Mods players need", "Server listing", "Server rules", "Chat", "Players without Progenitor", "Updates", "Operator" }, nav);
        Assert.NotNull(ModsMenu.ClickVisibleWidget("list", "nav/server-rules", null, null));
        ModsMenu.TickNavigation();
        Assert.Equal("server@Server rules", NavPlace());
        Assert.Equal(true, ((Dictionary<string, object?>)SectionNavState()["lastJump"]!)["found"]);
    }

    [Fact]
    public void A_player_who_is_not_an_admin_reads_the_server_rows_and_never_the_password()
    {
        WithRules();
        _admin = false;
        _context = Joined(admin: false);
        Open("server");
        var content = Read("content");
        var head = All(content, ServerTabPane.RowHead).Single();
        Assert.False(head.GetProperty("canEdit").GetBoolean());
        // No admin-channel row reaches a player who is not an admin; a group made only of them says so.
        Assert.DoesNotContain(AdminRows(content), r => JoinAdminOps.All.Contains(r.GetProperty("key").GetString()!)
            || r.GetProperty("key").GetString()!.StartsWith("notice", StringComparison.Ordinal));
        var notes = All(content, ModSettingsView.RowSetting)
            .Where(r => r.GetProperty("key").GetString()!.StartsWith("info/" + ProgenitorPage.AdminOnlyGroupLabel, StringComparison.Ordinal)).ToList();
        // Mods players need, Updates (AUTOUPDATE3) and Operator.
        Assert.Equal(3, notes.Count);
        Assert.All(notes, n => Assert.Equal(ProgenitorPage.AdminOnlyGroupNote, n.GetProperty("value").GetString()));
        // The rules read "Admin only · value"; the password only Set.
        string clan = ServerRules.Find(ServerRules.ClanSize)!.Label;
        Assert.Equal("Admin only · 4", _runtime.ValueOf("info", clan));
        Assert.Equal("Admin only · Set", _runtime.ValueOf("info", ServerRules.Find(ServerRules.Password)!.Label));
        Assert.False(_runtime.HasWidget("input", ServerRules.Find(ServerRules.Password)!.Label));
        Assert.DoesNotContain("hunter2", JsonSerializer.Serialize(ModsMenu.ReadVisibleWidgets(null)));
        Assert.Empty(_sent);
    }

    [Theory]
    [InlineData((int)SettingConnection.NotConnected, ServerTabPane.NotConnectedLine, "not-connected")]
    [InlineData((int)SettingConnection.NotProgenitor, ServerTabPane.NoProgenitorLine, "no-progenitor")]
    [InlineData((int)SettingConnection.Waiting, ServerTabPane.WaitingLine, "waiting")]
    public void Without_a_Progenitor_server_the_Server_tab_explains_itself_and_lists_its_groups_dimmed(int connectionValue, string line, string state)
    {
        var connection = (SettingConnection)connectionValue;
        _context = new SettingsScopeContext { Connection = connection, ServerName = "Night Garden", ServerAddress = "127.0.0.1:9976" };
        // A last server is known: only the not-connected state offers it.
        ModsMenu.LastServerFactsForTests = () => new LastServerFacts { Record = LastServer() };
        Open("server");
        var content = Read("content");
        var empty = All(content, ServerTabPane.RowEmpty).Single();
        Assert.Equal(state, empty.GetProperty("state").GetString());
        Assert.Equal(line, empty.GetProperty("line").GetString());
        Assert.Empty(All(content, ModSettingsView.RowSetting));
        Assert.Empty(All(content, ModSettingsView.RowSection));
        Assert.Equal(connection == SettingConnection.NotConnected ? ServerTabPane.Title : "Night Garden",
            All(content, ServerTabPane.RowHead).Single().GetProperty("title").GetString());

        // SERVEREMPTY1: one muted line per group, its name and what it holds.
        var groups = All(content, ServerTabPane.RowServerEmptyGroup);
        Assert.Equal(ServerGroups, groups.Select(g => g.GetProperty("group").GetString()).ToArray());
        Assert.All(groups, g => Assert.False(string.IsNullOrWhiteSpace(g.GetProperty("holds").GetString())));
        var nameCell = _runtime.CellRow(ServerTabPane.GroupKeyPrefix + "operator")!.Cells().Single(c => c.Key == ServerTabPane.CellGroupName);
        Assert.Equal(CellTone.Gold, nameCell.Tone);
        Assert.Equal(CellTone.Muted, _runtime.CellRow(ServerTabPane.GroupKeyPrefix + "operator")!.Cells().Single(c => c.Key == ServerTabPane.CellGroupHolds).Tone);
        var summary = All(content, ServerTabPane.RowServerEmpty).Single();
        Assert.Equal(state, summary.GetProperty("state").GetString());
        bool notConnected = connection == SettingConnection.NotConnected;
        Assert.Equal(notConnected, summary.GetProperty("joinShown").GetBoolean());
        Assert.Equal(notConnected ? JsonValueKind.Object : JsonValueKind.Null, summary.GetProperty("lastServer").ValueKind);
        Assert.Equal(notConnected ? 1 : 0, All(content, ServerTabPane.RowServerLast).Count);

        // The left list: every group, dimmed, never highlighted, no jump.
        var nav = All(Read("list"), SectionNav.RowSectionNav);
        Assert.Equal(ServerGroups, nav.Select(n => n.GetProperty("group").GetString()).ToArray());
        Assert.All(nav, n =>
        {
            Assert.True(n.GetProperty("dimmed").GetBoolean());
            Assert.False(n.GetProperty("highlighted").GetBoolean());
        });
        var dimmed = _runtime.CellRow(SectionNav.Key("Operator"))!;
        Assert.Equal(CellTone.Muted, dimmed.Cells().Single(c => c.Key == ModsListLayout.CellName).Tone);
        Assert.False(dimmed.Selected);
        Assert.Throws<ArgumentException>(() => ModsMenu.SectionOp("server", "Operator"));

        // Joining a Progenitor server redraws the tab with its groups.
        _context = Joined(admin: true);
        Assert.True(ModsMenu.NoteSettingsScopeChanged("join"));
        Assert.Equal("ready", All(Read("content"), ServerTabPane.RowHead).Single().GetProperty("state").GetString());
        Assert.NotEmpty(All(Read("list"), SectionNav.RowSectionNav));
    }

    private static readonly string[] ServerGroups =
        { "When players join", "Mods players need", "Server listing", "Server rules", "Chat", "Players without Progenitor", "Updates", "Operator" };

    private static LastServerRecord LastServer(bool password = false) => new()
    {
        Address = "127.0.0.1", Port = 9976, Protocol = "SteamIPv4", ServerName = "Progenitor Forms Test", NeedsPassword = password,
        Joined = "2026-10-06T12:00:00Z",
    };

    [Fact]
    public void Not_connected_the_last_server_is_offered_and_Join_connects_to_it()
    {
        _context = new SettingsScopeContext { Connection = SettingConnection.NotConnected };
        var joined = new List<string>();
        ModsMenu.LastServerFactsForTests = () => new LastServerFacts { Record = LastServer(password: true) };
        ModsMenu.LastServerJoinForTests = record =>
        {
            joined.Add(record.Endpoint + (record.ToTarget().PromptForPassword ? " prompt" : ""));
            return "joining";
        };
        Open("server");
        var content = Read("content");
        var summary = All(content, ServerTabPane.RowServerEmpty).Single();
        Assert.Equal("not-connected", summary.GetProperty("state").GetString());
        Assert.Equal("Progenitor Forms Test", summary.GetProperty("lastServer").GetProperty("name").GetString());
        Assert.Equal("127.0.0.1:9976", summary.GetProperty("lastServer").GetProperty("address").GetString());
        Assert.True(summary.GetProperty("joinShown").GetBoolean());
        Assert.True(summary.GetProperty("joinEnabled").GetBoolean());
        var row = _runtime.CellRow(ServerTabPane.LastServerKey)!;
        Assert.Equal("Progenitor Forms Test · 127.0.0.1:9976", row.Cells().Single(c => c.Key == ServerTabPane.CellLastName).Text);
        var join = row.Cells().Single(c => c.Key == ServerTabPane.CellJoin);
        Assert.Equal(CellKind.Button, join.Kind);
        Assert.Equal(CellSkin.KeyCap, join.Skin);
        Assert.Equal(CellTone.Normal, join.Tone);
        // Nothing of a password reaches the readback.
        Assert.DoesNotContain("hunter2", JsonSerializer.Serialize(ModsMenu.ReadVisibleWidgets(null)));

        // The bridge presses it: the join starts and the Mods window closes so its page or the game's prompt shows.
        Assert.NotNull(ModsMenu.ClickVisibleWidget("content", ServerTabPane.LastServerKey + "/" + ServerTabPane.CellJoin, null, null));
        // JOINPW1: even a record that says needsPassword connects without a password first (no prompt up front).
        Assert.Equal(new[] { "127.0.0.1:9976" }, joined);
        Assert.False(ModsMenu.IsOpen);
    }

    [Fact]
    public void The_last_server_Join_is_grayed_while_a_join_runs_and_hidden_without_a_record_or_while_hosting()
    {
        _context = new SettingsScopeContext { Connection = SettingConnection.NotConnected };
        var joined = new List<string>();
        var facts = new LastServerFacts { Record = LastServer(), JoinInProgress = true };
        ModsMenu.LastServerFactsForTests = () => facts;
        ModsMenu.LastServerJoinForTests = record =>
        {
            joined.Add(record.Endpoint);
            return "joining";
        };
        Open("server");
        var last = All(Read("content"), ServerTabPane.RowServerLast).Single();
        Assert.True(last.GetProperty("joinShown").GetBoolean());
        Assert.False(last.GetProperty("joinEnabled").GetBoolean());
        Assert.Equal("busy", last.GetProperty("state").GetString());
        Assert.Equal(CellTone.Disabled, _runtime.CellRow(ServerTabPane.LastServerKey)!.Cells().Single(c => c.Key == ServerTabPane.CellJoin).Tone);
        // The bridge refuses a grayed button (live 2026-10-14), and a press that reaches its slot anyway starts nothing.
        var refused = Assert.Throws<ArgumentException>(() => ModsMenu.ClickVisibleWidget("content", "server-last/join", null, null));
        Assert.Contains("disabled (not interactable)", refused.Message, StringComparison.Ordinal);
        Assert.Empty(joined);
        ushort joinSlot = UiCellRowHandle.VisibleRow(ServerTabPane.LastServerKey)?.CellSlotId(ServerTabPane.CellJoin) ?? 0;
        Assert.NotEqual(0, joinSlot);
        UiSlotTable.BootstrapDispatch(joinSlot, (byte)UiSlotKind.Click, 0f, false, null);
        Assert.Empty(joined);
        Assert.True(ModsMenu.IsOpen);
        Assert.Equal("busy", All(Read("content"), ServerTabPane.RowServerLast).Single().GetProperty("lastPress").GetString());

        // A private game this client hosts: no row.
        facts = new LastServerFacts { Record = LastServer(), HostingPrivateGame = true };
        Assert.True(ModsMenu.NoteSettingsScopeChanged("hosting"));
        var content = Read("content");
        Assert.Empty(All(content, ServerTabPane.RowServerLast));
        Assert.False(All(content, ServerTabPane.RowServerEmpty).Single().GetProperty("joinShown").GetBoolean());
        Assert.Null(_runtime.CellRow(ServerTabPane.LastServerKey));

        // No last server: no row either, the groups still listed.
        facts = new LastServerFacts();
        Assert.True(ModsMenu.NoteSettingsScopeChanged("none"));
        content = Read("content");
        Assert.Empty(All(content, ServerTabPane.RowServerLast));
        Assert.Equal(JsonValueKind.Null, All(content, ServerTabPane.RowServerEmpty).Single().GetProperty("lastServer").ValueKind);
        Assert.Equal(ServerGroups.Length, All(content, ServerTabPane.RowServerEmptyGroup).Count);
        Assert.Empty(joined);
    }

    [Fact]
    public void The_lock_list_applies_to_a_server_row_wherever_it_is_drawn()
    {
        _state = State(ServerSettingsLock.GroupOperator, ServerSettingsLock.GroupGameplay);
        Open("server");
        foreach (var row in AdminRows(Read("content")))
        {
            bool operatorRow = row.GetProperty("section").GetString() == ProgenitorPage.OperatorTitle;
            Assert.Equal(operatorRow, row.GetProperty("locked").GetBoolean());
        }

        // Progenitor's own two toggles are locked by "gameplay" on its page (the group word is unchanged).
        Open("mods");
        ModsMenu.OpenMod(SettingsPanel.PluginId);
        var page = AdminRows(Read("content"));
        Assert.Equal(new[] { ServerOptionOps.PhotoMode, ServerOptionOps.AllySafeHits }, page.Select(r => r.GetProperty("key").GetString()).ToArray());
        Assert.All(page, r => Assert.Equal("gameplay", r.GetProperty("lockedBy").GetString()));
        Assert.True(_runtime.FireToggle(JoinAdminRows.PhotoModeLabel, false));
        Assert.Empty(_sent);

        // Moving "If mods can't be checked" under Mods players need kept it in no-progenitor too (#178's rule).
        var groups = ServerSettingsLock.GroupsOf(SettingPlaces.JoinAdminId(SettingPlaces.OpUnverified));
        Assert.Contains(ServerSettingsLock.GroupModsPlayersNeed, groups);
        Assert.Contains(ServerSettingsLock.GroupNoProgenitor, groups);
        Assert.Contains(ServerSettingsLock.GroupGameplay, ServerSettingsLock.GroupsOf(SettingPlaces.JoinAdminId(SettingPlaces.OpAllySafeHits)));
        Assert.Contains(ServerSettingsLock.GroupJoin, ServerSettingsLock.GroupsOf("progenitor/ServerMessage/Message"));
    }

    // ---- Progenitor's page --------------------------------------------------------------------------

    // ---- the Mods tab's landing page --------------------------------------------------------------

    [Fact]
    public void The_landing_page_reads_back_its_rows_and_its_tiles_open_their_homes()
    {
        ModsMenu.Open();
        string content = System.Text.Json.JsonSerializer.Serialize(ModsMenu.ReadVisibleWidgets("content"));
        foreach (string key in new[] { ModsLanding.RowSubtitle, ModsLanding.RowIntro, ModsLanding.PointRowKey(1), ModsLanding.PointRowKey(4),
                     ModsLanding.RowTop, ModsLanding.RowBottom,
                     ModsLanding.RowLastBundle, ModsLanding.RowFooter })
            Assert.Contains("\"" + key + "\"", content, StringComparison.Ordinal);
        Assert.Contains("mods found on this client", content, StringComparison.Ordinal);
        Assert.Contains(System.Text.Json.JsonSerializer.Serialize(ModsLanding.Intro).Trim('"'), content, StringComparison.Ordinal);
        Assert.Contains(System.Text.Json.JsonSerializer.Serialize(ModsLanding.Footer).Trim('"'), content, StringComparison.Ordinal);
        Assert.Contains(ModsLanding.ServerSettingsAdminNote, content, StringComparison.Ordinal);

        Assert.NotNull(ModsMenu.ClickVisibleWidget("content", ModsLanding.YourSettingsCaption, null, null));
        ModsMenu.TickNavigation();
        Assert.Equal(ModsTab.Mods, ModsMenu.SelectedTabForTests);
        Assert.StartsWith("progenitor-page", NavPlace(), StringComparison.Ordinal);

        ModsMenu.Close();
        ModsMenu.Open();
        Assert.NotNull(ModsMenu.ClickVisibleWidget("content", ModsLanding.ServerSettingsCaption, null, null));
        ModsMenu.TickNavigation();
        Assert.Equal("progenitor-page@" + ProgenitorPage.OnThisServerTitle, NavPlace());

        ModsMenu.Close();
        ModsMenu.Open();
        Assert.NotNull(ModsMenu.ClickVisibleWidget("content", ModsLanding.PreferencesCaption, null, null));
        ModsMenu.TickNavigation();
        Assert.Equal(ModsTab.Preferences, ModsMenu.SelectedTabForTests);
    }

    /// <summary>
    /// The overview's Help guide tile is drawn only while Progenitor's guide is installed, reads back its target and file,
    /// and opens the guide's <c>file://</c> URI when pressed.
    /// What this would catch: a Help guide tile that opens nothing, or one drawn on a package without the guide.
    /// </summary>
    [Fact]
    public void The_landing_page_offers_the_help_guide_only_when_it_is_installed()
    {
        string bep = Path.Combine(Path.GetTempPath(), "progenitor-landing-help-" + Guid.NewGuid().ToString("N"), "BepInEx");
        Directory.CreateDirectory(bep);
        var opened = new List<string>();
        try
        {
            ModsMenu.HelpBepInExRootProbe = () => bep;
            ModsMenu.HelpOpener = opened.Add;
            ModsMenu.Open();
            string content = System.Text.Json.JsonSerializer.Serialize(ModsMenu.ReadVisibleWidgets("content"));
            Assert.DoesNotContain("\"" + ModsLanding.RowHelp + "\"", content, StringComparison.Ordinal);
            Assert.DoesNotContain(ModsLanding.HelpGuideCaption, content, StringComparison.Ordinal);
            ModsMenu.Close();

            string guide = Path.Combine(bep, "progenitor-core", "docs", "guide", "progenitor-guide.html");
            Directory.CreateDirectory(Path.GetDirectoryName(guide)!);
            File.WriteAllText(guide, "<html>guide</html>");
            ModsMenu.Open();
            content = System.Text.Json.JsonSerializer.Serialize(ModsMenu.ReadVisibleWidgets("content"));
            foreach (string key in new[] { ModsLanding.RowHelp, ModsLanding.RowHelpNotes })
                Assert.Contains("\"" + key + "\"", content, StringComparison.Ordinal);
            Assert.Contains(ModsLanding.HelpGuideNote, content, StringComparison.Ordinal);
            Assert.Contains("\"target\":\"" + ModsLanding.TargetHelpGuide + "\"", content, StringComparison.Ordinal);
            Assert.Contains("\"file\":\"docs/guide/progenitor-guide.html\"", content, StringComparison.Ordinal);
            // The footer still closes the page, after the third line.
            Assert.True(content.IndexOf("\"" + ModsLanding.RowHelpNotes + "\"", StringComparison.Ordinal)
                < content.IndexOf("\"" + ModsLanding.RowFooter + "\"", StringComparison.Ordinal));

            Assert.Empty(opened);
            Assert.NotNull(ModsMenu.ClickVisibleWidget("content", ModsLanding.HelpGuideCaption, null, null));
            Assert.Equal(new[] { ModHelp.FileUri(guide) }, opened.ToArray());
        }
        finally
        {
            ModsMenu.HelpBepInExRootProbe = null;
            ModsMenu.HelpOpener = null;
            try { Directory.Delete(Path.GetDirectoryName(bep)!, recursive: true); }
            catch { /* temp */ }
        }
    }

    /// <summary>
    /// The Help guide line is a 2×3 grid's last line: its one tile spans exactly the left tile of the pair line above (same
    /// left, same width), the empty right slot draws nothing, and the head and note rows' spans match.
    /// What this would catch: a lone tile stretched across the whole row, or one narrower than the tiles above it.
    /// </summary>
    [Theory]
    [InlineData(700f)]
    [InlineData(1100f)]
    public void The_help_guide_tile_lines_up_with_the_left_tile_above_it(float width)
    {
        var (pairHead, pairNote) = ModsLanding.BottomRow();
        var (head, note) = ModsLanding.HelpRow();
        var pair = CellTiles.Spans(CellLayout.Resolve(width, pairHead.Cells, pairHead.RightInset), pairHead.Cells);
        var single = CellTiles.Spans(CellLayout.Resolve(width, head.Cells, head.RightInset), head.Cells);
        var singleNote = CellTiles.Spans(CellLayout.Resolve(width, note.Cells, note.RightInset), note.Cells);
        Assert.Equal(2, pair.Count);
        var tile = Assert.Single(single);
        Assert.Equal(5, tile.Tile);
        Assert.Equal(pair[0].Left, tile.Left, 1);
        Assert.Equal(pair[0].Width, tile.Width, 1);
        var noteTile = Assert.Single(singleNote);
        Assert.Equal(tile.Left, noteTile.Left, 1);
        Assert.Equal(tile.Width, noteTile.Width, 1);
        Assert.Equal(CellSectionEdge.First, head.TileEdge);
        Assert.Equal(CellSectionEdge.Last, note.TileEdge);
        Assert.Equal(pairHead.Height, head.Height);
        Assert.Equal(pairNote.Height, note.Height);
    }

    [Theory]
    [InlineData(ModsLanding.YourSettingsCaption)]
    [InlineData(ModsLanding.ServerSettingsCaption)]
    [InlineData(ModsLanding.PreferencesCaption)]
    public void Back_after_a_landing_tile_returns_to_the_landing_and_Forward_to_the_tile(string caption)
    {
        ModsMenu.Open();
        ModsMenu.TickNavigation();
        string landing = NavPlace();
        int before = (int)ModsMenu.NavState()["count"]!;
        Assert.NotNull(ModsMenu.ClickVisibleWidget("content", caption, null, null));
        ModsMenu.TickNavigation();
        string target = NavPlace();
        Assert.NotEqual(landing, target);
        Assert.Equal(before + 1, (int)ModsMenu.NavState()["count"]!); // the landing stays the step before

        Assert.True(ModsMenu.NavigateBack());
        ModsMenu.TickNavigation();
        Assert.Equal(landing, NavPlace());
        Assert.Equal(ModsTab.Mods, ModsMenu.SelectedTabForTests);
        string content = System.Text.Json.JsonSerializer.Serialize(ModsMenu.ReadVisibleWidgets("content"));
        Assert.Contains("\"" + ModsLanding.RowTop + "\"", content, StringComparison.Ordinal); // the landing page itself

        Assert.True(ModsMenu.NavigateForward());
        ModsMenu.TickNavigation();
        Assert.Equal(target, NavPlace());
    }

    [Fact]
    public void Your_settings_selects_Progenitor_in_the_left_list_as_its_row_does()
    {
        ModsMenu.SetRequiredGroupExpandedForTests(false);
        ModsMenu.Open();
        Assert.NotNull(ModsMenu.ClickVisibleWidget("content", ModsLanding.YourSettingsCaption, null, null));
        ModsMenu.TickNavigation();
        var reveal = (Dictionary<string, object?>)ModsMenu.NavState()["sidebarReveal"]!;
        Assert.Equal(SettingsPanel.PluginId, reveal["plugin"]);
        Assert.Equal(true, reveal["found"]);    // the Required by Progenitor group is open, so its row is drawn
        Assert.Equal(true, reveal["selected"]); // and highlighted
        string sidebar = System.Text.Json.JsonSerializer.Serialize(ModsMenu.ReadVisibleWidgets("list"));
        Assert.Contains(ModsListLayout.RowKey(SettingsPanel.PluginId), sidebar, StringComparison.Ordinal);
    }

    [Fact]
    public void The_landing_pages_server_tile_says_how_to_reach_the_settings_when_not_an_admin()
    {
        _admin = false;
        ModsMenu.Open();
        string content = System.Text.Json.JsonSerializer.Serialize(ModsMenu.ReadVisibleWidgets("content"));
        Assert.Contains(ModsLanding.ServerSettingsJoinNote, content, StringComparison.Ordinal);
        Assert.NotNull(ModsMenu.ClickVisibleWidget("content", ModsLanding.ServerSettingsCaption, null, null));
        ModsMenu.TickNavigation();
        Assert.Equal(ModsTab.Server, ModsMenu.SelectedTabForTests);
    }

    [Fact]
    public void The_landing_pages_Collect_logs_tile_runs_the_collect_and_its_line_follows()
    {
        try
        {
            Progenitor.Support.SupportActions.ResetForTests();
            Progenitor.Support.SupportActions.Gather = () => null;
            long now = 5_000_000;
            Progenitor.Support.SupportActions.ClockMs = () => now;
            var scheduled = new List<Action>();
            UiInfoHandle.Scheduler = (_, action, _) => scheduled.Add(action);
            ModsMenu.Open();
            Assert.Equal(ModsLanding.CollectDescription, CollectNoteNow(out _)); // its description before any collect
            Assert.NotNull(ModsMenu.ClickVisibleWidget("content", ModsLanding.CollectCaption, null, null));
            Assert.Equal("Collecting logs is unavailable here.", Progenitor.Support.SupportActions.Status);
            Assert.Equal("Collecting logs is unavailable here.", CollectNoteNow(out string tileEdge)); // the outcome
            Assert.Equal("last", tileEdge);

            // Re-read on the page's one-second tick: the outcome for 10 s, then the description again.
            Assert.NotEmpty(scheduled);
            var tick = scheduled[^1];
            scheduled.Clear();
            now += 9_000;
            tick();
            Assert.Single(scheduled); // it keeps ticking while the page is up
            Assert.Equal("Collecting logs is unavailable here.", CollectNoteNow(out _));
            now += 1_500;
            scheduled[^1]();
            Assert.Equal(ModsLanding.CollectDescription, CollectNoteNow(out _));

            // A rebuilt page stops the old page's tick.
            ModsMenu.Open();
            scheduled.Clear();
            tick();
            Assert.Empty(scheduled);
        }
        finally
        {
            UiInfoHandle.Scheduler = null;
            Progenitor.Support.SupportActions.ResetForTests();
        }
    }

    private static string CollectNoteNow(out string tileEdge)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(ModsMenu.ReadVisibleWidgets("content")));
        string note = "";
        tileEdge = "";
        foreach (var row in doc.RootElement.EnumerateArray())
        {
            if (!row.TryGetProperty("key", out var key) || key.GetString() != ModsLanding.RowBottomNotes) continue;
            tileEdge = row.GetProperty("tileEdge").GetString() ?? "";
            foreach (var cell in row.GetProperty("cells").EnumerateArray())
                if (cell.GetProperty("key").GetString() == ModsLanding.CellCollectNote) note = cell.GetProperty("text").GetString() ?? "";
        }
        return note;
    }

    [Fact]
    public void Progenitors_groups_are_listed_under_it_and_a_click_lands_on_the_group()
    {
        Open("mods");
        Assert.True(ModsMenu.JumpToProgenitorGroup("On this server"));
        ModsMenu.TickNavigation();
        Assert.Equal(ModsTab.Mods, ModsMenu.SelectedTabForTests);
        Assert.Equal("progenitor-page@" + ProgenitorPage.OnThisServerTitle, NavPlace());
        var nav = All(Read("list"), SectionNav.RowSectionNav).Select(n => n.GetProperty("group").GetString()).ToArray();
        Assert.Equal(ProgenitorPage.NavGroups, nav);
        var state = SectionNavState();
        Assert.Equal("mods", state["tab"]);
        Assert.Equal(ProgenitorPage.OnThisServerTitle, state["highlighted"]);
        Assert.Equal(true, ((Dictionary<string, object?>)state["lastJump"]!)["found"]);
        Assert.True(_runtime.CellRow(SectionNav.Key(ProgenitorPage.OnThisServerTitle))!.Selected);

        // Another mod's page hides Progenitor's groups; Back returns to the group.
        Assert.NotNull(ModsMenu.ClickVisibleWidget("list", "nav/private-games", null, null));
        ModsMenu.TickNavigation();
        Assert.Equal("progenitor-page@" + ProgenitorPage.PrivateGamesTitle, NavPlace());
        Assert.True(ModsMenu.NavigateBack());
        ModsMenu.TickNavigation();
        Assert.Equal("progenitor-page@" + ProgenitorPage.OnThisServerTitle, NavPlace());
        // Its On this server toggles: an admin edits them.
        Assert.Equal(2, AdminRows(Read("content")).Count);
    }

    /// <summary>
    /// SERVERLINK1 (requested 2026-10-07: "at the top, add a link to the progenitor MOD server side settings"): under the
    /// Server tab's header line, a text link opens Progenitor's page at On this server; Back returns to the Server tab.
    /// </summary>
    [Fact]
    public void The_Server_tab_links_to_Progenitors_own_server_settings_and_Back_returns()
    {
        Open("server");
        ModsMenu.TickNavigation();
        var link = All(Read("content"), ConfigurationPane.RowPageLink)
            .Single(l => l.GetProperty("target").GetString() == SettingsLinks.TargetProgenitorPage);
        Assert.Equal(SettingsLinks.LandingOnThisServer, link.GetProperty("landing").GetString());
        Assert.Equal("Progenitor's own server settings »", link.GetProperty("label").GetString());
        Assert.Equal("link", link.GetProperty("skin").GetString());
        // Drawn as a link: one button cell in the link skin, only as wide as its words, on the row after the header line.
        var cell = _runtime.CellRow(SettingsLinks.ProgenitorServerKey)!.Cells().Single(c => c.Key == SettingsLinks.CellOpen);
        Assert.Equal(CellSkin.Link, cell.Skin);
        Assert.Equal(CellKind.Button, cell.Kind);
        Assert.True(cell.Width.Value < 400f);

        Assert.True(UiCellRowHandle.TryInvoke(SettingsLinks.ProgenitorServerKey + "/" + SettingsLinks.CellOpen, out _));
        ModsMenu.TickNavigation();
        Assert.Equal(ModsTab.Mods, ModsMenu.SelectedTabForTests);
        Assert.Equal("progenitor-page@" + ProgenitorPage.OnThisServerTitle, NavPlace());
        Assert.Equal(ProgenitorPage.OnThisServerTitle, SectionNavState()["highlighted"]);
        // The landing group has Progenitor's two toggles and the link back to the Server tab.
        Assert.Equal(2, AdminRows(Read("content")).Count);
        Assert.NotNull(_runtime.CellRow(SettingsLinks.ServerTabKey));
        Assert.True(ModsMenu.NavigateBack());
        ModsMenu.TickNavigation();
        Assert.Equal(ModsTab.Server, ModsMenu.SelectedTabForTests);
        Assert.Equal("server", NavPlace());
    }

    [Fact]
    public void A_player_who_sees_none_of_Progenitors_server_settings_gets_its_page_and_a_note()
    {
        _admin = false;
        _context = Joined(admin: false);
        Open("server");
        ModsMenu.TickNavigation();
        var link = All(Read("content"), ConfigurationPane.RowPageLink)
            .Single(l => l.GetProperty("target").GetString() == SettingsLinks.TargetProgenitorPage);
        Assert.Equal(SettingsLinks.LandingTop, link.GetProperty("landing").GetString());
        Assert.Equal("Progenitor's page »", link.GetProperty("label").GetString());
        Assert.Equal(SettingsLinks.ProgenitorPageNote, link.GetProperty("note").GetString());
        Assert.True(UiCellRowHandle.TryInvoke(SettingsLinks.ProgenitorServerKey + "/" + SettingsLinks.CellOpen, out _));
        ModsMenu.TickNavigation();
        Assert.Equal(ModsTab.Mods, ModsMenu.SelectedTabForTests);
        Assert.StartsWith("progenitor-page", NavPlace(), StringComparison.Ordinal);
        Assert.True(ModsMenu.NavigateBack());
        ModsMenu.TickNavigation();
        Assert.Equal("server", NavPlace());
    }

    [Fact]
    public void The_bridge_parses_section_drawer_and_the_server_tab()
    {
        static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement;
        var section = ModsUiArgs.Parse(Json("{\"action\":\"section\",\"tab\":\"server\",\"group\":\"server-rules\"}"));
        Assert.Equal("server-rules", section.Group);
        Assert.Equal("server", section.Tab);
        Assert.True(section.NeedsNextDrain);
        Assert.True(ModsUiArgs.RequiresOpenWorkspace("section"));
        var drawer = ModsUiArgs.Parse(Json("{\"action\":\"drawer\",\"group\":\"Operator\",\"open\":false}"));
        Assert.Equal(false, drawer.Open);
        Assert.Null(ModsUiArgs.Parse(Json("{\"action\":\"drawer\",\"group\":\"Operator\"}")).Open);
        Assert.Throws<ArgumentException>(() => ModsUiArgs.Parse(Json("{\"action\":\"drawer\"}")));
        Assert.Throws<ArgumentException>(() => ModsUiArgs.Parse(Json("{\"action\":\"section\",\"tab\":\"consent\",\"group\":\"x\"}")));
        Assert.True(ModsMenu.TryParseWorkspaceTab("server", out string name) && name == "server");
        Assert.True(ModsMenu.TryParseWorkspaceTab("configuration", out name) && name == "preferences");
    }
}
