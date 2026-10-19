using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BepInEx.Configuration;
using Progenitor.Config;
using Progenitor.Install;
using Progenitor.Networking;
using Progenitor.Ui;
using Progenitor.WorldState;
using Xunit;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.Tests;

/// <summary>
/// Settings organization stage 3 (docs/settings-organization.md): the Configuration tab holds mod-manager preferences
/// on the mod page's row kit, its first row links to Progenitor's page (landing at Server settings for an admin of the
/// joined server), the Progenitor group is gone, <c>EditOtherModsSettings</c> is live, the Install filters and sort
/// can be remembered, and Progenitor's page no longer lists rows homed elsewhere. Game-free: the fake UI runtime.
/// </summary>
[Collection("PluginLogger")]
public sealed class ConfigurationOrganizationTests : IDisposable
{
    private readonly UiTests.FakeUiRuntime _runtime = new();
    private readonly TestLogSource _log = new();
    private readonly string _dir;
    private readonly object _owner = new();
    private readonly ConfigFile _shared;
    private readonly List<IDisposable> _handles = new();
    private JsonDocument? _rows;
    private SettingsScopeContext _context = new();

    public ConfigurationOrganizationTests()
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
        CoreUiSettings.Reset();
        ModsMenu.ResetNavigationForTests();
        ModsMenu.LedgerForTests = Array.Empty<InstalledLedgerRow>();
        ModsMenu.SetInstallCatalogForTests(null);
        ModsMenu.ScopeContextForTests = _ => _context;
        ModsMenu.KeybindsPostForTests = step => step();
        ConfigurationPane.ResetServerInstallStateForTests();
        _dir = Path.Combine(Path.GetTempPath(), "progenitor-config-org-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _shared = new ConfigFile(Path.Combine(_dir, ConfigHelper.SharedFileName), saveOnInit: true);
        ModsMenu.SetConfigFileForTests(_shared);
        ModsMenu.BindModsConfig(_shared);
    }

    public void Dispose()
    {
        _rows?.Dispose();
        foreach (var h in _handles) h.Dispose();
        ModsMenu.ScopeContextForTests = null;
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
        CoreUiSettings.Reset();
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

    private JsonElement Content()
    {
        _rows?.Dispose();
        _rows = JsonDocument.Parse(JsonSerializer.Serialize(ModsMenu.ReadVisibleWidgets("content")));
        return _rows.RootElement;
    }

    private static List<JsonElement> All(JsonElement rows, string key)
    {
        var list = new List<JsonElement>();
        foreach (var row in rows.EnumerateArray())
            if (row.ValueKind == JsonValueKind.Object && row.TryGetProperty(key, out var value)) list.Add(value);
        return list;
    }

    private void OpenConfiguration()
    {
        if (!ModsMenu.IsOpen) ModsMenu.Open();
        ModsMenu.SelectTab("configuration");
    }

    private static string NavPlace() => (string)((Dictionary<string, object?>)ModsMenu.NavState()["current"]!)["place"]!;

    // ---- the tab ----------------------------------------------------------------------------------

    [Fact]
    public void The_purpose_line_is_first_and_every_drawn_setting_is_at_home_on_the_Preferences_tab()
    {
        var ui = new ConfigFile(Path.Combine(_dir, "progenitor.ui.cfg"), saveOnInit: true);
        CoreUiSettings.EditOtherModsSettings = ui.Bind(CoreUiSettings.EditOtherModsSection, CoreUiSettings.EditOtherModsKey, false, "x");
        CoreUiSettings.ServerBrowserProgenitorOnly = ui.Bind(CoreUiSettings.ServerBrowserSection, CoreUiSettings.ServerBrowserProgenitorOnlyKey, false, "x");
        CoreUiSettings.TitleScreenVideo = ui.Bind(TitleVideoPolicy.Section, TitleVideoPolicy.Key, true, "x");
        OpenConfiguration();
        var rows = Content();

        // The title is the game's section header; the purpose line (the user's words) and its two links come first.
        Assert.Equal(ConfigurationPane.PurposeText, All(rows, ConfigurationPane.RowPurpose).Single().GetString());
        var link = All(rows, ConfigurationPane.RowPageLink).Single();
        Assert.Equal(new[] { SettingsLinks.TargetMods, SettingsLinks.TargetServerTab },
            link.GetProperty("targets").EnumerateArray().Select(t => t.GetString()).ToArray());
        var linkRow = _runtime.CellRow(SettingsLinks.PurposeKey)!;
        Assert.Contains(linkRow.Cells(), c => c.Key == SettingsLinks.CellMods && c.Text == "Mods »");
        Assert.Contains(linkRow.Cells(), c => c.Key == SettingsLinks.CellServer && c.Text == "Server »");
        int linkIndex = _runtime.CellRows.IndexOf(linkRow);
        int firstGroup = _runtime.CellRows.FindIndex(r => r.Text.StartsWith(ModSettingsScopes.DrawerKeyPrefix, StringComparison.Ordinal)
            || r.Text.StartsWith(ModSettingsView.SectionKeyPrefix, StringComparison.Ordinal));
        Assert.True(firstGroup >= 0 && linkIndex < firstGroup, "the purpose links come before the first group");

        // Three groups on the row kit, then Local server: the user's four groups in order.
        var sections = All(rows, ModSettingsView.RowSection).Select(s => s.GetProperty("title").GetString()).ToArray();
        Assert.Equal(new[]
        {
            ConfigurationPane.ModsTabGroup, ConfigurationPane.InstallTabGroup, ConfigurationPane.SourcesGroup, ConfigurationPane.UpdatesGroup,
            ConfigurationPane.JoinGameGroup, ConfigurationPane.TitleScreenGroup, ConfigurationPane.LocalServerGroup,
        }, sections);
        Assert.Equal(sections, ConfigurationPane.ConfigGroups);
        var settings = All(rows, ModSettingsView.RowSetting).Where(s => s.GetProperty("kind").GetString() == "setting").ToList();
        Assert.NotEmpty(settings);
        Assert.All(settings, s => Assert.Equal(SettingPlaces.Preferences, s.GetProperty("home").GetString()));
        var ids = settings.Select(s => s.GetProperty("settingId").GetString()).ToList();
        foreach (string id in new[]
                 {
                     "progenitor/Mods/InstallView", "progenitor/Settings/EditOtherModsSettings",
                     "progenitor/Mods/RememberInstallFilters",
                     "progenitor/Mods/UseThunderstore", "progenitor/Mods/ShowNsfwContent",
                     "progenitor/Install/MaxDownloadMB",
                     "progenitor/ServerBrowser/ProgenitorOnly",
                     // TITLEVIDEO1: Preferences · Title screen.
                     "progenitor/TitleScreen/Video",
                     // AUTOUPDATE1 (2026-10-09): Preferences · Updates.
                     "progenitor/AutoUpdate/Mods", "progenitor/AutoUpdate/PromptBeforeJoin",
                     // AUTOUPDATE2: Major versions.
                     "progenitor/AutoUpdate/MajorVersions",
                 })
            Assert.Contains(id, ids);
        // The 2026-10-06 trim (user: "No user will care to change those"): these stay in the cfg file only.
        foreach (string id in new[]
                 {
                     "progenitor/Workspace/BackgroundIntensity", "progenitor/Workspace/BackgroundContrast",
                     "progenitor/Ui/MenuHistoryInnerTabs", "progenitor/Ui/AdminAuthButton",
                     "progenitor/Install/CurrentUpdateTag",
                 })
            Assert.DoesNotContain(id, ids);
        // The advanced pair is folded until asked for.
        Assert.DoesNotContain("progenitor/Mods/IndexPath", ids);
        // No Progenitor group, no raw Progenitor settings button, and no server setting.
        Assert.DoesNotContain(ConfigurationPane.ConfigGroups, g => g == "Progenitor");
        Assert.False(_runtime.FireClick("Progenitor settings"));
        Assert.DoesNotContain(ids, id => id!.Contains("/ServerMessage/") || id.Contains("/JoinAdmin/") || id.Contains("/ServerRules/"));
    }

    [Fact]
    public void The_Advanced_fold_shows_the_package_index_path_and_the_frame_switch()
    {
        OpenConfiguration();
        Assert.True(ModsMenu.ClickVisibleWidget("content", ConfigurationPane.AdvancedShowCaption, null, null) != null);
        ModsMenu.SelectTab("configuration");
        var ids = All(Content(), ModSettingsView.RowSetting).Select(s => s.GetProperty("id").GetString()).ToList();
        Assert.Contains("progenitor/Mods/IndexPath", ids);
        Assert.Contains("progenitor/Workspace/CanonicalInGameFrame", ids);
    }

    [Fact]
    public void Install_view_is_one_stored_value_on_the_Configuration_tab_and_the_Install_toolbar()
    {
        OpenConfiguration();
        var pane = ModsMenu.ConfigurationPaneForTests!;
        var row = pane.SettingsView!.Sections.SelectMany(s => s.Rows).Single(r => r.Key == "InstallView");
        Assert.Equal("Simple", row.Read());
        Assert.Equal(SettingWriteState.Accepted, SettingsCatalog.Submit(row, "Advanced").State);

        // The toolbar's switch reads the same entry (its probe), and settings_places agrees.
        var install = new InstallModsPane(new InstallSidesPaneTests.FakeCatalog())
        {
            ViewProbe = () => ModsMenu.InstallViewForTests,
        };
        Assert.Equal(InstallView.Advanced, install.View);
        var view = install.ViewToWidget();
        Assert.Equal("progenitor/Mods/InstallView", view["settingId"]);
        Assert.Equal("Advanced", view["value"]);
        var places = JsonDocument.Parse(JsonSerializer.Serialize(ModsMenu.SettingsPlacesReadback())).RootElement;
        Assert.Equal("Advanced", places.GetProperty("progenitor/Mods/InstallView").GetProperty("value").GetString());
        Assert.Contains("InstallView = Advanced", File.ReadAllText(_shared.ConfigFilePath));
    }

    // ---- the purpose links ---------------------------------------------------------------------------

    [Fact]
    public void The_purpose_links_open_the_Mods_and_Server_tabs_and_Back_returns_to_Preferences()
    {
        OpenConfiguration();
        ModsMenu.TickNavigation();
        Assert.Equal("preferences", NavPlace());

        Assert.True(UiCellRowHandle.TryInvoke(SettingsLinks.PurposeKey + "/" + SettingsLinks.CellServer, out _));
        ModsMenu.TickNavigation();
        Assert.Equal(ModsTab.Server, ModsMenu.SelectedTabForTests);
        Assert.Equal("server", NavPlace());

        Assert.True(ModsMenu.NavigateBack());
        ModsMenu.TickNavigation();
        Assert.Equal(ModsTab.Preferences, ModsMenu.SelectedTabForTests);
        Assert.Equal("preferences", NavPlace());

        Assert.True(UiCellRowHandle.TryInvoke(SettingsLinks.PurposeKey + "/" + SettingsLinks.CellMods, out _));
        ModsMenu.TickNavigation();
        Assert.Equal(ModsTab.Mods, ModsMenu.SelectedTabForTests);
    }

    // ---- Edit other mods' settings is live -------------------------------------------------------------

    [Fact]
    public void Edit_other_mods_settings_redraws_an_open_page_so_a_foreign_row_becomes_editable_at_once()
    {
        var other = new ConfigFile(Path.Combine(_dir, "other.mod.cfg"), saveOnInit: true);
        other.Bind("General", "Loud", true, "Be loud.");
        SettingsCatalog.Register("other.mod", other, new object(), foreign: true);
        ModsMenu.Open();
        ModsMenu.OpenMod("other.mod");
        string Control() => All(Content(), ModSettingsView.RowSetting)
            .Single(s => s.GetProperty("id").GetString() == "other.mod/General/Loud").GetProperty("control").GetString()!;
        Assert.Equal("readonly", Control());

        CoreUiSettings.ApplyForeignEditPolicy(true, "test");
        Assert.True(SettingsCatalog.AllowForeignEdits);
        Assert.Equal("toggle", Control());

        CoreUiSettings.ApplyForeignEditPolicy(false, "test");
        Assert.Equal("readonly", Control());
    }

    [Fact]
    public void The_host_follows_the_switch_with_a_handler_on_its_own_file()
    {
        string host = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "UiKeybindHost.cs");
        string body = SourcePin.MethodBody(host, "private static void BindForeignEditPolicy");
        SourcePin.AssertUncommented(body, "entry.SettingChanged +=");
        SourcePin.AssertUncommented(body, "CoreUiSettings.ApplyForeignEditPolicy(entry.Value");
    }

    // ---- Remember Install filters and sort -----------------------------------------------------------

    [Fact]
    public void Filter_memory_round_trips_and_a_bad_part_keeps_its_default()
    {
        var snapshot = new InstallFilterSnapshot
        {
            Side = InstallSideFilter.Server, Show = InstallShowFilter.NeedsUpdate, ProgenitorSupported = true,
            CurrentUpdateOnly = false, NoRestartOnly = true, Sort = InstallSort.Downloads, SortReversed = true,
            SimpleShow = InstallSimpleShow.Updates, SimpleSort = InstallSort.Name, SimpleSortReversed = true,
        };
        string text = InstallFilterMemory.Encode(snapshot);
        Assert.True(InstallFilterMemory.TryDecode(text, out var back));
        Assert.Equal(text, InstallFilterMemory.Encode(back));

        Assert.True(InstallFilterMemory.TryDecode("v1;side=Nowhere;sort=Downloads;cur=2", out var partial));
        Assert.Equal(InstallSideFilter.Any, partial.Side);
        Assert.Equal(InstallSort.Downloads, partial.Sort);
        Assert.True(partial.CurrentUpdateOnly);
        Assert.False(InstallFilterMemory.TryDecode("", out _));
        Assert.False(InstallFilterMemory.TryDecode("v9;side=Server", out _));
    }

    [Fact]
    public void With_the_switch_on_the_filters_are_saved_and_a_new_session_starts_from_them()
    {
        OpenConfiguration();
        var bindings = ModsMenu.BindingsForTests!;
        Assert.False(bindings.RememberInstallFilters!.Value);

        // Off (the default): nothing is saved and a new session starts from the defaults.
        var first = new InstallModsPane(new InstallSidesPaneTests.FakeCatalog());
        first.Filters.Side = InstallSideFilter.Client;
        first.SaveFilterMemory();
        Assert.Equal("", bindings.InstallFiltersRemembered!.Value);

        // On: saved, and the next catalog's (the next session's) state starts from them.
        bindings.RememberInstallFilters.Value = true;
        first.Filters.Sort = InstallSort.Downloads;
        first.SaveFilterMemory();
        Assert.StartsWith("v1;side=Client;", bindings.InstallFiltersRemembered.Value);
        var next = new InstallModsPane(new InstallSidesPaneTests.FakeCatalog());
        Assert.Equal(InstallSideFilter.Client, next.Filters.Side);
        Assert.Equal(InstallSort.Downloads, next.Filters.Sort);

        // Off again: the remembered text is ignored.
        bindings.RememberInstallFilters.Value = false;
        var later = new InstallModsPane(new InstallSidesPaneTests.FakeCatalog());
        Assert.Equal(InstallSideFilter.Any, later.Filters.Side);
    }

    // ---- Progenitor's page no longer lists rows homed elsewhere --------------------------------------

    [Fact]
    public void Progenitors_page_leaves_out_the_Configuration_rows_the_shared_files_others_and_the_folder_actions()
    {
        var ui = new ConfigFile(Path.Combine(_dir, "progenitor.ui.cfg"), saveOnInit: true);
        ui.Bind("Ui", "CaptureMode", "WhenHovered", "How panels capture the mouse.");
        ui.Bind(CoreUiSettings.EditOtherModsSection, CoreUiSettings.EditOtherModsKey, false, "x");
        SettingsCatalog.Register(SettingsPanel.PluginId, ui, _owner);
        SettingsCatalog.Register(SettingsPanel.PluginId, _shared, new object(), foreign: true);
        _shared.Bind("Diagnostics", "WatchCore", false, "x");
        OpenConfiguration(); // registers the folder actions under progenitor
        var ids = ModsMenu.BuildSettingsSections(SettingsPanel.PluginId, null).SelectMany(s => s.Rows).Select(r => r.RowId).ToList();
        // The 2026-10-06 trim: [Ui] CaptureMode is cfg-only now, and is not swept onto the page as an unknown own row either.
        Assert.DoesNotContain("progenitor/Ui/CaptureMode", ids);
        Assert.DoesNotContain("progenitor/Settings/EditOtherModsSettings", ids);
        Assert.DoesNotContain("progenitor/Workspace/BackgroundIntensity", ids);
        Assert.DoesNotContain("progenitor/Mods/UseThunderstore", ids);
        Assert.DoesNotContain("progenitor/Diagnostics/WatchCore", ids);
        Assert.DoesNotContain(ids, id => id.Contains("/" + SettingBinding.ActionSection + "/", StringComparison.Ordinal));
    }
}
