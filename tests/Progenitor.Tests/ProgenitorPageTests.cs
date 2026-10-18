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
/// Settings organization stage 4 (docs/settings-organization.md, "Progenitor's page"): Your settings on Progenitor's
/// mod page — the link to the Configuration tab first, then Keys (every Core keybind and the clash rule), Gameplay,
/// UI and Private games (<c>[Hosted] Enabled</c> and the uninstall row moved from the Configuration tab). Game-free.
/// </summary>
[Collection("PluginLogger")]
public sealed class ProgenitorPageTests : IDisposable
{
    private readonly UiTests.FakeUiRuntime _runtime = new();
    private readonly TestLogSource _log = new();
    private readonly string _dir;
    private readonly object _owner = new();
    private readonly ConfigFile _shared;
    private readonly ConfigFile _ui;
    private readonly List<IDisposable> _handles = new();
    private JsonDocument? _rows;

    public ProgenitorPageTests()
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
        ModsMenu.ResetNavigationForTests();
        ModsMenu.LedgerForTests = Array.Empty<InstalledLedgerRow>();
        ModsMenu.SetInstallCatalogForTests(null);
        ModsMenu.KeybindsPostForTests = step => step();
        ConfigurationPane.ResetServerInstallStateForTests();
        _dir = Path.Combine(Path.GetTempPath(), "progenitor-page-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_dir, "BepInEx"));
        ConfigurationPane.ClientRootProbe = () => Path.Combine(_dir, "BepInEx");
        _shared = new ConfigFile(Path.Combine(_dir, ConfigHelper.SharedFileName), saveOnInit: true);
        _ui = new ConfigFile(Path.Combine(_dir, "progenitor.ui.cfg"), saveOnInit: true);
        ModsMenu.SetConfigFileForTests(_shared);
        ModsMenu.BindModsConfig(_shared);
    }

    public void Dispose()
    {
        _rows?.Dispose();
        foreach (var h in _handles) h.Dispose();
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
        ConfigurationPane.ResetServerInstallStateForTests();
        ConfigHelper.UninstallAll();
        Plugin.Logger = null;
        UiApi.ResetForTests();
        try { Directory.Delete(_dir, true); } catch { }
    }

    /// <summary>The entries the owning modules bind (ui.cfg is Core's own source; the shared file is swept as foreign).</summary>
    private void BindModules(bool hosted = true)
    {
        SettingsCatalog.Register(SettingsPanel.PluginId, _ui, _owner);
        SettingsCatalog.Register(SettingsPanel.PluginId, _shared, new object(), foreign: true);
        _ui.Bind("Keybinds", "EnforceGameWins", true, "The game wins.");
        _ui.Bind("PhotoMode", "Enabled", false, "Photo mode.");
        _ui.Bind("PhotoMode", "HideHudOnEnter", true, "Hide the HUD.");
        _ui.Bind("PhotoMode", "HideNameplates", true, "Hide nameplates.");
        _ui.Bind("PhotoMode", "LookSensitivity", 1f, "Look speed.");
        _ui.Bind("BuildSearch", "Enabled", true, "Search.");
        _ui.Bind("BuildSearch", "ClearOnClose", true, "Clear.");
        _ui.Bind("BuildSearch", "Breadcrumbs", true, "Crumbs.");
        _ui.Bind("DyeAll", "Enabled", true, "Dye all.");
        _ui.Bind("Chat", "VisibleSeconds", 30, "Seconds.");
        _ui.Bind("Chat", "KeepVisible", false, "Keep.");
        _ui.Bind("Chat", "StayWhileHovered", true, "Hover.");
        _ui.Bind("Chat", "ClickSafe", true, "Click.");
        _ui.Bind("Chat", "ReplyShortcut", true, "Reply.");
        _ui.Bind("Chat", "NameCompletion", true, "Complete.");
        foreach (var key in new[] { "View", "Timestamps", "CollapseRepeats", "KeepHistory", "Mentions", "MentionSound", "LockPosition", "Recall", "LongMessages", "ChannelColor", "CommandHints" })
            _ui.Bind("Chat", key, true, key + ".");
        _ui.Bind("Chat", "HistoryLines", 500, "Lines.");
        _ui.Bind("Chat", "MentionWords", "", "Words.");
        _ui.Bind("Chat", "MutedPlayers", "", "Muted.");
        _ui.Bind("Chat", "FontSize", 0, "Size.");
        _ui.Bind("Chat", "BackgroundOpacity", -1, "Opacity.");
        _ui.Bind("Ui", "CaptureMode", "WhenHovered", "Capture.");
        if (hosted) _shared.Bind("Hosted", "Enabled", true, "Hosted.");
        _shared.Bind("Hosted", "DryRun", false, "Dry run.");
        Keybinds.Register(typeof(ProgenitorPageTests), SettingsPanel.PluginId, "OpenSettingsPanel", _ui, new KeyChord(UiKey.F10), "Open.", () => { });
        Keybinds.Register(typeof(ProgenitorPageTests), SettingsPanel.PluginId, "PhotoMode", _ui, new KeyChord(UiKey.F9), "Photo.", () => { });
        _handles.Add(ModSettingsPages.Register(_owner, SettingsPanel.PluginId, page =>
        {
            ProgenitorPage.BuildYourSettings(page);
            ProgenitorPage.BuildOnThisServer(page);
        }));
    }

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

    private void OpenPage()
    {
        if (!ModsMenu.IsOpen) ModsMenu.Open();
        ModsMenu.OpenMod(SettingsPanel.PluginId);
    }

    private static JsonElement Setting(JsonElement rows, string id) =>
        All(rows, ModSettingsView.RowSetting).Single(s => s.GetProperty("id").GetString() == id);

    [Fact]
    public void Your_settings_is_Keys_Photo_mode_Build_menu_Equipment_Chat_Private_games_and_Get_help_then_On_this_server()
    {
        BindModules();
        OpenPage();
        var rows = Content();

        // Three homes (decided 2026-10-06): Gameplay is split into Photo mode and Build menu; no link to Preferences.
        var subheadings = All(rows, ModSettingsView.RowSection).Select(s => s.GetProperty("title").GetString()).ToArray();
        Assert.Equal(new[]
        {
            ProgenitorPage.KeysTitle, ProgenitorPage.PhotoModeTitle, ProgenitorPage.BuildMenuTitle, ProgenitorPage.EquipmentTitle, ProgenitorPage.ChatTitle,
            ProgenitorPage.PrivateGamesTitle, ProgenitorPage.GetHelpTitle,
        }, subheadings);
        Assert.Equal(subheadings.Append(ProgenitorPage.OnThisServerTitle).ToArray(), ProgenitorPage.NavGroups.ToArray());
        // On this server: the scope heading, with the edit rule on its line, and the link to the Server tab (everyone sees it).
        var scopes = All(rows, ModSettingsScopes.RowScopeSection).Select(s => s.GetProperty("heading").GetString()).ToArray();
        Assert.Equal(new[] { "Your preferences", ProgenitorPage.OnThisServerTitle }, scopes);
        var link = All(rows, ConfigurationPane.RowPageLink).Single();
        Assert.Equal(SettingsLinks.TargetServerTab, link.GetProperty("target").GetString());
        Assert.Equal(ProgenitorPage.LinkServerText, link.GetProperty("label").GetString());
        Assert.Contains(_runtime.CellRow(SettingsLinks.ServerTabKey)!.Cells(), c => c.Key == SettingsLinks.CellOpen && c.Text == "Server »");
        // Nothing else server-related: no server message, Server rules, Players joining or Operator rows on the page.
        Assert.DoesNotContain(All(rows, ModSettingsView.RowSetting), s =>
            (s.GetProperty("id").GetString() ?? "").Contains("/ServerMessage/") || (s.GetProperty("id").GetString() ?? "").Contains("/ServerRules/"));

        // Keys: every Core keybind; each keybind is at home here with the Keybinds tab as its mirror.
        var keys = Setting(rows, "progenitor/Keybinds/OpenSettingsPanel");
        Assert.Equal(SettingPlaces.Keys, keys.GetProperty("home").GetString());
        Assert.Equal("keybinds-tab", keys.GetProperty("alsoAt")[0].GetString());
        Assert.Equal(ProgenitorPage.KeysTitle, keys.GetProperty("section").GetString());
        Assert.Equal(ProgenitorPage.PhotoModeTitle, Setting(rows, "progenitor/PhotoMode/Enabled").GetProperty("section").GetString());
        Assert.Equal(SettingPlaces.PhotoMode, Setting(rows, "progenitor/PhotoMode/Enabled").GetProperty("home").GetString());
        Assert.Equal(ProgenitorPage.BuildMenuTitle, Setting(rows, "progenitor/BuildSearch/Enabled").GetProperty("section").GetString());
        Assert.Equal(SettingPlaces.BuildMenu, Setting(rows, "progenitor/BuildSearch/Enabled").GetProperty("home").GetString());
        // DYEALL in Core: only the switch under Equipment. Covered pieces and the hold time are not settings.
        Assert.Equal(ProgenitorPage.EquipmentTitle, Setting(rows, "progenitor/DyeAll/Enabled").GetProperty("section").GetString());
        Assert.Equal(SettingPlaces.Equipment, Setting(rows, "progenitor/DyeAll/Enabled").GetProperty("home").GetString());
        Assert.Equal("toggle", Setting(rows, "progenitor/DyeAll/Enabled").GetProperty("control").GetString());
        Assert.DoesNotContain(All(rows, ModSettingsView.RowSetting), s => s.GetProperty("id").GetString() is
            "progenitor/DyeAll/IncludeCovered" or "progenitor/DyeAll/HoldSeconds");
        // CHAT1: the chat box's four settings, under Chat.
        foreach (var key in new[] { "VisibleSeconds", "KeepVisible", "StayWhileHovered", "ClickSafe", "ReplyShortcut", "NameCompletion" })
        {
            Assert.Equal(ProgenitorPage.ChatTitle, Setting(rows, "progenitor/Chat/" + key).GetProperty("section").GetString());
            Assert.Equal(SettingPlaces.Chat, Setting(rows, "progenitor/Chat/" + key).GetProperty("home").GetString());
        }
        // The 2026-10-06 trim (user: "No user will care to change those"): the clash rule and mouse capture stay in the
        // ui.cfg only, so the UI group is gone.
        Assert.DoesNotContain(All(rows, ModSettingsView.RowSetting), s => s.GetProperty("id").GetString() is
            "progenitor/Keybinds/EnforceGameWins" or "progenitor/Ui/CaptureMode");
        Assert.DoesNotContain("UI", subheadings);

        // [Hosted] Enabled is Progenitor's own (the shared file), editable here; DryRun stays in the file.
        var hosted = Setting(rows, "progenitor/Hosted/Enabled");
        Assert.Equal("toggle", hosted.GetProperty("control").GetString());
        Assert.Equal(SettingPlaces.PrivateGames, hosted.GetProperty("home").GetString());
        Assert.DoesNotContain(All(rows, ModSettingsView.RowSetting), s => s.GetProperty("id").GetString() == "progenitor/Hosted/DryRun");
        // No automatic General or Keys section is left over.
        Assert.DoesNotContain(subheadings, t => t == ModSettingsLayout.GeneralTitle);
    }

    [Fact]
    public void The_trimmed_rows_are_not_found_by_search_and_Reset_all_leaves_their_values_alone()
    {
        BindModules();
        // Both changed from their defaults in the file (an operator's edit).
        var capture = (ConfigEntry<string>)_ui[new ConfigDefinition("Ui", "CaptureMode")];
        var clash = (ConfigEntry<bool>)_ui[new ConfigDefinition("Keybinds", "EnforceGameWins")];
        capture.Value = "Always";
        clash.Value = false;
        OpenPage();
        var view = ModsMenu.ModPage!.SettingsView!;

        // The 2026-10-06 trim: no row, so no search finds them.
        foreach (string query in new[] { "capture", "clash", "mouse" })
        {
            view.ApplySearch(query);
            Assert.DoesNotContain(All(Content(), ModSettingsView.RowSetting), r => r.GetProperty("id").GetString() is
                "progenitor/Ui/CaptureMode" or "progenitor/Keybinds/EnforceGameWins");
        }

        view.ApplySearch("");
        // Reset all acts on the rows the page draws: a hidden value is the file's, and stays as the operator set it.
        var resettable = view.ResettableRows().Select(r => r.RowId).ToList();
        Assert.DoesNotContain("progenitor/Ui/CaptureMode", resettable);
        Assert.DoesNotContain("progenitor/Keybinds/EnforceGameWins", resettable);
        foreach (var row in view.ResettableRows()) ModSettingsLayout.Reset(row);
        Assert.Equal("Always", capture.Value);
        Assert.False(clash.Value);
    }

    [Fact]
    public void A_row_whose_module_has_not_bound_it_yet_is_left_out_and_appears_once_bound()
    {
        BindModules(hosted: false);
        OpenPage();
        Assert.DoesNotContain(All(Content(), ModSettingsView.RowSetting), s => s.GetProperty("id").GetString() == "progenitor/Hosted/Enabled");
        _shared.Bind("Hosted", "Enabled", true, "Hosted.");
        OpenPage();
        Assert.Contains(All(Content(), ModSettingsView.RowSetting), s => s.GetProperty("id").GetString() == "progenitor/Hosted/Enabled");
    }

    [Fact]
    public void The_link_opens_the_Server_tab_and_Back_returns_to_the_page()
    {
        BindModules();
        OpenPage();
        ModsMenu.TickNavigation();
        Assert.True(UiCellRowHandle.TryInvoke(SettingsLinks.ServerTabKey + "/" + SettingsLinks.CellOpen, out _));
        ModsMenu.TickNavigation();
        Assert.Equal(ModsTab.Server, ModsMenu.SelectedTabForTests);
        Assert.Equal("server", (string)((Dictionary<string, object?>)ModsMenu.NavState()["current"]!)["place"]!);
        Assert.True(ModsMenu.NavigateBack());
        ModsMenu.TickNavigation();
        Assert.Equal(ModsTab.Mods, ModsMenu.SelectedTabForTests);
        Assert.Equal("progenitor-page", (string)((Dictionary<string, object?>)ModsMenu.NavState()["current"]!)["place"]!);
    }

    [Fact]
    public void Uninstall_is_on_the_page_under_Private_games_and_no_longer_on_the_Configuration_tab()
    {
        BindModules();
        OpenPage();
        var rows = Content();
        var uninstall = All(rows, ModSettingsView.RowSetting).Single(s => s.GetProperty("key").GetString() == "button/" + ProgenitorPage.UninstallCaption);
        Assert.Equal(ProgenitorPage.PrivateGamesTitle, uninstall.GetProperty("section").GetString());
        Assert.Equal(ClientUninstall.PromptHeading, uninstall.GetProperty("confirm").GetString());
        Assert.False(All(rows, ProgenitorPage.RowUninstall).First().GetProperty("requested").GetBoolean());

        // Yes (the confirmed action): the request is written; the row gives way to the request line and Keep Progenitor.
        var item = ModsMenu.ModPage!.SettingsView!.Sections.SelectMany(s => s.Items)
            .Single(i => i.Kind == ModSettingsItemKind.Button && i.Label == ProgenitorPage.UninstallCaption);
        item.ButtonAction!();
        Assert.True(ConfigurationPane.ClientUninstallRequested);
        Assert.True(File.Exists(Path.Combine(_dir, "BepInEx", ClientUninstall.FileName)));
        OpenPage();
        rows = Content();
        Assert.True(All(rows, ProgenitorPage.RowUninstall).First().GetProperty("requested").GetBoolean());
        var keep = All(rows, ModSettingsView.RowSetting).Single(s => s.GetProperty("key").GetString() == "button/" + ProgenitorPage.KeepCaption);
        Assert.True(keep.GetProperty("visible").GetBoolean());
        // Hidden rows are not read back: the Uninstall button gave way.
        Assert.DoesNotContain(All(rows, ModSettingsView.RowSetting),
            s => s.GetProperty("key").GetString() == "button/" + ProgenitorPage.UninstallCaption);

        ModsMenu.ModPage!.SettingsView!.Sections.SelectMany(s => s.Items)
            .Single(i => i.Kind == ModSettingsItemKind.Button && i.Label == ProgenitorPage.KeepCaption).ButtonAction!();
        Assert.False(ConfigurationPane.ClientUninstallRequested);

        // The Configuration tab no longer draws the row.
        ModsMenu.SelectTab("configuration");
        Assert.False(_runtime.FireClick(ConfigurationPane.UninstallClientButton));
        string build = SourcePin.MethodBody(SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "ConfigurationPane.cs"), "public void Build(");
        Assert.DoesNotContain("BuildClientUninstall(", build, StringComparison.Ordinal);
    }
}
