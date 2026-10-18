using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
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

[Collection("PluginLogger")]
public sealed class ModsMenuTests : IDisposable
{
    private readonly UiTests.FakeUiRuntime _runtime = new();
    private readonly TestLogSource _log = new();
    public ModsMenuTests()
    {
        UiLayoutBatch.ResetForTests();
        UiApi.ResetForTests();
        UiApi.SetRuntimeForTests(_runtime);
        Plugin.Logger = _log;
    }
    public void Dispose()
    {
        UiLayoutBatch.ResetForTests();
        ModsMenu.Close();
        ModsMenu.ResetTabBindingsForTests();
        ModsWorkspaceFrame.ResetForTests();
        ModsMenu.UninstallLifecycleWatch();
        Handshake.UninstallClientServerContextWatch();
        Plugin.Logger = null;
        UiApi.ResetForTests();
    }

    private void BeginLifecycle()
    {
        Handshake.ResetTables();
        ModMenuInventory.ResetForTests();
        Handshake.UninstallClientServerContextWatch();
        WorldEpoch.ResetForTests();
        Handshake.InstallClientServerContextWatch();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Install_side_uses_configured_folder_when_path_changes_after_config_binding(bool both)
    {
        string root = Path.Combine(Path.GetTempPath(), "progenitor-install-target-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "server", LocalServerFolder.BepInExFolderName));
        File.WriteAllBytes(Path.Combine(root, "server", LocalServerFolder.ServerExecutableName), new byte[] { 0 });
        var file = new BepInEx.Configuration.ConfigFile(Path.Combine(root, "progenitor.cfg"), true);
        try
        {
            ModsMenu.SetConfigFileForTests(file);
            ModsMenu.BindModsConfig(file);
            // The setting may be loaded or edited before the Install workspace opens.
            ConfigurationPane.Bind(file).DedicatedServerPath.Value = Path.Combine(root, "server");
            var catalog = new InstallSidesPaneTests.FakeCatalog();
            var entry = new CatalogEntry(new ManifestMod("", "1.0.0", NetworkCompatibilityLevel.NoNeedForSync,
                NetworkVersionRule.None, false, "o", "o-both"), CatalogProvenance.ManifestVerified,
                InstallReasons.SourceThunderstore, InstallReasons.SourceLabelThunderstore, "2026-10-15T00:00:00Z",
                side: PackageSide.Both, owner: "o", packageName: "both", categories: new[] { "Client", "Server", "Oakveil Update" });
            catalog.Entries.Add(entry);
            ModsMenu.SetInstallCatalogForTests(catalog);
            ModsMenu.Open();
            ModsMenu.SelectTab("install");
            var pane = ModsMenu.InstallPaneForTests!;
            pane.ServerRouteProbe = () => new ServerRoute(ServerRouteKind.Local, ServerRouteBlock.None);
            Assert.True(both ? pane.OnBothClick(entry.Key) : pane.OnSideClick(entry.Key, true, SideButton.Install));
            Assert.Equal(both ? InstallTarget.Both : InstallTarget.LocalServer, pane.Target);
            pane.PromptNo();
        }
        finally
        {
            ModsMenu.Close();
            ModsMenu.ResetTabBindingsForTests();
            Directory.Delete(root, true);
        }
    }

    /// <summary>
    /// Live order of one physical Escape: EventSystem Cancel unfocuses, then
    /// <c>UiInputContext.Update</c> invokes 27 and 17 back-to-back. A test that
    /// only fires 27 with EventSystem focus still set cannot see the close consumer
    /// win.
    /// </summary>
    private void PressEscapeLikeClient()
    {
        _runtime.LoseEventSystemFocus();
        UiApi.PressKeyForTests(UiSearchEscape.EscapeKeyCode, down: true);
        UiApi.PressKeyForTests(UiSearchEscape.BackAction, down: true);
    }

    private void ReleaseEscapeLikeClient() => ModsMenu.NoteEscapeReleased();

    [Fact]
    public void Unload_closes_custom_page_and_releases_its_callback_and_cursor()
    {
        using var menu = ModsMenu.Register(new object(), "example", "Example");
        IUiButton? button = null;
        menu.Page("Status", p => { button = p.Button("Action"); button.OnClick(() => { }); });
        var panel = ModsMenu.OpenPage("EXAMPLE", "Status");
        Assert.True(ModsMenu.IsOpen);
        Assert.Equal(UiCaptureMode.WhileShown, panel.CaptureMode);
        menu.Dispose();
        Assert.False(ModsMenu.IsOpen);
        Assert.True(panel.IsDisposed);
        Assert.True(button!.IsDisposed);
        Assert.Empty(menu.Pages);
        Assert.Equal(0, _runtime.Capture);
    }

    [Fact]
    public void Opening_at_main_menu_hides_the_title_view_and_close_restores_it()
    {
        NativeModsEntry.SetOwnedForTests("main-menu", curtain: false, observers: 0);
        try
        {
            ModsMenu.Open(ModsMenu.ReasonModsUi);
            Assert.True(ModsMenu.IsOpen);
            Assert.True(NativeModsEntry.TitleViewIsHidden);
            Assert.Equal(true, ModsMenu.TitleVideoPlaying);
            Assert.True(NativeModsEntry.PlayBackdropIsHeld);
            Assert.Equal(
                NativeModsEntry.PlayBackdropHold.SourcePrefab,
                NativeModsEntry.DescribePlayBackdrop()["source"]);
            Assert.False(NativeModsEntry.HostedViewIsHidden);
            ModsMenu.Close(ModsMenu.ReasonModsUi);
            Assert.False(ModsMenu.IsOpen);
            Assert.False(NativeModsEntry.TitleViewIsHidden);
            Assert.Equal(true, ModsMenu.TitleVideoPlaying);
            Assert.False(NativeModsEntry.PlayBackdropIsHeld);
        }
        finally
        {
            NativeModsEntry.SetOwnedForTests("none", curtain: false, observers: 0);
        }
    }

    [Fact]
    public void Play_backdrop_live_clone_is_held_and_none_is_not()
    {
        NativeModsEntry.SetOwnedForTests("main-menu", curtain: false, observers: 0);
        try
        {
            NativeModsEntry.SetPlayBackdropAcquisitionForTests(
                NativeModsEntry.PlayBackdropHold.SourceLiveClone, "LiveSprite", "LiveMat", "UI/LiveFog");
            ModsMenu.Open(ModsMenu.ReasonModsUi);
            var live = NativeModsEntry.DescribePlayBackdrop();
            Assert.Equal(true, live["present"]);
            Assert.Equal(NativeModsEntry.PlayBackdropHold.SourceLiveClone, live["source"]);
            Assert.Equal("LiveSprite", live["sprite"]);
            ModsMenu.Close(ModsMenu.ReasonModsUi);
            Assert.False(NativeModsEntry.PlayBackdropIsHeld);

            NativeModsEntry.SetPlayBackdropAcquisitionForTests(NativeModsEntry.PlayBackdropHold.SourceNone);
            NativeModsEntry.HoldTitleViewIfMainMenu();
            Assert.True(NativeModsEntry.TitleViewIsHidden);
            Assert.False(NativeModsEntry.PlayBackdropIsHeld);
            Assert.Equal(false, NativeModsEntry.DescribePlayBackdrop()["present"]);
            NativeModsEntry.RestoreTitleView();
        }
        finally
        {
            NativeModsEntry.SetOwnedForTests("none", curtain: false, observers: 0);
        }
    }

    [Fact]
    public void Opening_from_the_top_tab_hides_the_hosted_native_view_and_close_restores_it()
    {
        NativeModsEntry.SetOwnedForTests("top-tab", curtain: true, observers: 1);
        try
        {
            ModsMenu.Open(ModsMenu.ReasonModsUi);
            Assert.True(ModsMenu.IsOpen);
            Assert.True(NativeModsEntry.HostedViewIsHidden);
            Assert.True(ModsMenu.HostedViewIsHidden);
            Assert.False(NativeModsEntry.TitleViewIsHidden);
            ModsMenu.Close(ModsMenu.ReasonModsUi);
            Assert.False(ModsMenu.IsOpen);
            Assert.False(NativeModsEntry.HostedViewIsHidden);
        }
        finally
        {
            NativeModsEntry.SetOwnedForTests("none", curtain: false, observers: 0);
        }
    }

    [Fact]
    public void In_game_open_with_native_frame_shows_on_the_first_frame()
    {
        NativeModsEntry.SetOwnedForTests("top-tab", curtain: true, observers: 1);
        ModsMenu.DeferUntilNativeForTests = true;
        ModsMenu.NativeFoundForTests = true;
        ModsMenu.FrameForTests = 12;
        var native = NativeContentRect.FromClient(1088f, 72f, 1664f, 928f, 3840f, 1080f);
        ModsWorkspaceFrame.Resolve(3840f, 1080f, "top-tab", native, native);
        try
        {
            ModsMenu.Open();
            Assert.True(ModsMenu.IsOpen);
            Assert.False(ModsMenu.AwaitingNativeFrame);
            Assert.True(ModsWorkspaceFrame.Shown);
            Assert.Equal(12, ModsWorkspaceFrame.FirstShowFrame);
            Assert.Equal(0, ModsWorkspaceFrame.LayoutBeforeShow);
            Assert.Equal(ModsWorkspaceFrame.PathNative, ModsWorkspaceFrame.ShownPath);
            Assert.Contains(_log.Infos, line =>
                line == ModsWorkspaceFrame.FormatShownLog(12, ModsWorkspaceFrame.PathNative));
            string json = JsonSerializer.Serialize(ModsMenu.DescribeGeometry());
            Assert.Contains("\"firstShowFrame\":12", json);
            Assert.Contains("\"layoutBeforeShow\":0", json);
            Assert.Contains("\"framePath\":\"native-container\"", json);
            string menu = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "ModsMenu.cs");
            string begin = SourcePin.MethodBody(menu, "private static IUiPanel Begin(");
            SourcePin.AssertUncommented(begin, "WorkspaceOpenTiming.Span(\"prepare\", PrepareWorkspaceShow)");
            string prepare = SourcePin.MethodBody(menu, "private static void PrepareWorkspaceShow()");
            SourcePin.AssertUncommented(prepare, "MayPresentNow(");
            SourcePin.AssertUncommented(prepare, "BeginFirstShow()");
        }
        finally
        {
            ModsMenu.Close();
            NativeModsEntry.SetOwnedForTests("none", curtain: false, observers: 0);
        }
    }

    [Fact]
    public void In_game_open_stays_hidden_until_native_then_lays_out_once()
    {
        NativeModsEntry.SetOwnedForTests("top-tab", curtain: true, observers: 1);
        ModsMenu.DeferUntilNativeForTests = true;
        ModsMenu.NativeFoundForTests = false;
        ModsMenu.FrameForTests = 3;
        try
        {
            ModsMenu.Open();
            Assert.True(ModsMenu.IsOpen);
            Assert.True(ModsMenu.AwaitingNativeFrame);
            Assert.False(ModsWorkspaceFrame.Shown);
            Assert.Equal(0, ModsWorkspaceFrame.LayoutBeforeShow);
            Assert.False(ModsMenu.WorkspaceStripIsShown);

            for (int i = 0; i < 4; i++)
            {
                ModsMenu.FrameForTests = 3 + i;
                ModsMenu.TryPresentAfterNativeFrame();
                Assert.True(ModsMenu.AwaitingNativeFrame);
                Assert.False(ModsWorkspaceFrame.Shown);
                Assert.Equal(0, ModsWorkspaceFrame.LayoutBeforeShow);
            }

            ModsMenu.NativeFoundForTests = true;
            ModsMenu.FrameForTests = 7;
            var native = NativeContentRect.FromClient(1088f, 72f, 1664f, 928f, 3840f, 1080f);
            ModsWorkspaceFrame.Resolve(3840f, 1080f, "top-tab", native, native);
            ModsMenu.TryPresentAfterNativeFrame();
            Assert.False(ModsMenu.AwaitingNativeFrame);
            Assert.True(ModsMenu.IsOpen);
            Assert.True(ModsWorkspaceFrame.Shown);
            Assert.Equal(7, ModsWorkspaceFrame.FirstShowFrame);
            Assert.Equal(0, ModsWorkspaceFrame.LayoutBeforeShow);
            Assert.Equal(ModsWorkspaceFrame.PathNative, ModsWorkspaceFrame.ShownPath);
            Assert.True(ModsMenu.WorkspaceStripIsShown);
            Assert.Contains(_log.Infos, line =>
                line == ModsWorkspaceFrame.FormatShownLog(7, ModsWorkspaceFrame.PathNative));
        }
        finally
        {
            ModsMenu.Close();
            NativeModsEntry.SetOwnedForTests("none", curtain: false, observers: 0);
        }
    }

    [Fact]
    public void Title_site_still_shows_on_fallback()
    {
        NativeModsEntry.SetOwnedForTests("main-menu", curtain: false, observers: 0);
        ModsMenu.DeferUntilNativeForTests = true;
        ModsMenu.NativeFoundForTests = false;
        ModsMenu.FrameForTests = 4;
        try
        {
            ModsMenu.Open();
            Assert.True(ModsMenu.IsOpen);
            Assert.False(ModsMenu.AwaitingNativeFrame);
            Assert.True(ModsWorkspaceFrame.Shown);
            Assert.Equal(ModsWorkspaceFrame.PathFallback, ModsWorkspaceFrame.ShownPath);
            Assert.Equal(0, ModsWorkspaceFrame.LayoutBeforeShow);
            Assert.Contains(_log.Infos, line =>
                line == ModsWorkspaceFrame.FormatShownLog(4, ModsWorkspaceFrame.PathFallback));
        }
        finally
        {
            ModsMenu.Close();
            NativeModsEntry.SetOwnedForTests("none", curtain: false, observers: 0);
        }
    }

    [Fact]
    public void Owned_tab_click_while_open_is_a_no_op_and_closed_opens()
    {
        NativeModsEntry.SetOwnedForTests("top-tab", curtain: true, observers: 1);
        try
        {
            Assert.Equal(
                NativeModsEntry.EntryClickAction.Open,
                NativeModsEntry.DecideOwnedButtonClick(suspended: false, workspaceOpen: false));
            ModsMenu.Open();
            Assert.True(ModsMenu.IsOpen);
            Assert.Equal(ModsTab.Mods, ModsMenu.SelectedTabForTests);
            Assert.True(_runtime.FireClick(ModsTabStrip.InstallModsCaption));
            Assert.Equal(ModsTab.InstallMods, ModsMenu.SelectedTabForTests);
            int closes = ModsMenu.CloseCallsForTests;
            Assert.Equal(
                NativeModsEntry.EntryClickAction.Ignore,
                NativeModsEntry.DecideOwnedButtonClick(suspended: false, workspaceOpen: true));
            Assert.True(ModsMenu.IsOpen);
            Assert.Equal(ModsTab.InstallMods, ModsMenu.SelectedTabForTests);
            Assert.Equal(closes, ModsMenu.CloseCallsForTests);

            ModsMenu.Toggle(ModsMenu.ReasonShortcutFor(new KeyChord(UiKey.F10)));
            Assert.False(ModsMenu.IsOpen);
            Assert.Equal(closes + 1, ModsMenu.CloseCallsForTests);
        }
        finally
        {
            ModsMenu.Close();
            NativeModsEntry.SetOwnedForTests("none", curtain: false, observers: 0);
        }
    }

    [Fact]
    public void Escape_and_every_unload_reason_restore_the_title_view()
    {
        NativeModsEntry.SetOwnedForTests("main-menu", curtain: false, observers: 0);
        try
        {
            ModsMenu.Open(ModsMenu.ReasonModsUi);
            Assert.True(NativeModsEntry.TitleViewIsHidden);
            Assert.Equal(true, ModsMenu.TitleVideoPlaying);
            Assert.True(NativeModsEntry.PlayBackdropIsHeld);
            ModsMenu.NotifyHiddenByEscape();
            Assert.False(NativeModsEntry.TitleViewIsHidden);
            Assert.Equal(true, ModsMenu.TitleVideoPlaying);
            Assert.False(NativeModsEntry.PlayBackdropIsHeld);

            foreach (PluginUnloadReason reason in Enum.GetValues(typeof(PluginUnloadReason)))
            {
                NativeModsEntry.HoldTitleViewIfMainMenu();
                Assert.True(NativeModsEntry.TitleViewIsHidden);
                Assert.Equal(true, ModsMenu.TitleVideoPlaying);
                Assert.True(NativeModsEntry.PlayBackdropIsHeld);
                var after = NativeModsEntry.TitleViewHold.AfterUnload(
                    NativeModsEntry.TestTitleHold, reason);
                var playAfter = NativeModsEntry.PlayBackdropHold.AfterUnload(
                    NativeModsEntry.TestPlayHold, reason);
                NativeModsEntry.RestoreTitleView();
                Assert.False(after.Held);
                Assert.False(playAfter.Held);
                Assert.False(NativeModsEntry.TitleViewIsHidden);
                Assert.Equal(true, ModsMenu.TitleVideoPlaying);
                Assert.False(NativeModsEntry.PlayBackdropIsHeld);
            }

            NativeModsEntry.HoldTitleViewIfMainMenu();
            Assert.True(NativeModsEntry.TitleViewIsHidden);
            Assert.Equal(true, ModsMenu.TitleVideoPlaying);
            Assert.True(NativeModsEntry.PlayBackdropIsHeld);
            ModsMenu.Uninstall();
            Assert.False(NativeModsEntry.TitleViewIsHidden);
            Assert.Equal(true, ModsMenu.TitleVideoPlaying);
            Assert.False(NativeModsEntry.PlayBackdropIsHeld);
            Assert.False(NativeModsEntry.HostedViewIsHidden);
        }
        finally
        {
            NativeModsEntry.SetOwnedForTests("none", curtain: false, observers: 0);
        }
    }

    [Fact]
    public void Exception_mid_open_restores_the_title_view()
    {
        NativeModsEntry.SetOwnedForTests("main-menu", curtain: false, observers: 0);
        try
        {
            NativeModsEntry.HoldTitleViewIfMainMenu();
            Assert.True(NativeModsEntry.TitleViewIsHidden);
            Assert.Equal(true, ModsMenu.TitleVideoPlaying);
            Assert.True(NativeModsEntry.PlayBackdropIsHeld);
            var held = NativeModsEntry.TestTitleHold;
            var restored = NativeModsEntry.TitleViewHold.AfterExceptionDuringOpen(held);
            var playRestored = NativeModsEntry.PlayBackdropHold.AfterExceptionDuringOpen(
                NativeModsEntry.TestPlayHold);
            NativeModsEntry.RestoreTitleView();
            Assert.False(restored.Held);
            Assert.False(playRestored.Held);
            Assert.False(NativeModsEntry.TitleViewIsHidden);
            Assert.Equal(true, ModsMenu.TitleVideoPlaying);
            Assert.False(NativeModsEntry.PlayBackdropIsHeld);
        }
        finally
        {
            NativeModsEntry.SetOwnedForTests("none", curtain: false, observers: 0);
        }
    }

    [Fact]
    public void Failed_page_builder_disposes_partial_widgets_and_offers_recovery()
    {
        using var menu = ModsMenu.Register(new object(), "example", "Example");
        IUiButton? partial = null;
        menu.Page("Broken", p => { partial = p.Button("Half built"); throw new Exception("oops"); });
        var recovery = ModsMenu.OpenPage("example", "Broken");
        Assert.True(partial!.IsDisposed);
        Assert.True(ModsMenu.IsOpen);
        Assert.False(recovery.IsDisposed);
        Assert.Equal(3, _runtime.PanelCount);
    }

    [Fact]
    public void Duplicate_mod_registration_cannot_replace_a_live_contribution()
    {
        using var original = ModsMenu.Register(new object(), "example", "Original");
        Assert.Throws<InvalidOperationException>(() => ModsMenu.Register(new object(), "EXAMPLE", "Replacement"));
        Assert.False(original.IsDisposed);
        original.Dispose();
        using var replacement = ModsMenu.Register(new object(), "example", "Replacement");
        original.Dispose();
        Assert.False(replacement.IsDisposed);
    }

    [Fact]
    public void Owner_sweep_releases_only_its_registration()
    {
        var owner = new object();
        using var first = ModsMenu.Register(owner, "first", "First");
        using var second = ModsMenu.Register(new object(), "second", "Second");
        ModsMenu.RemoveAll(owner);
        Assert.True(first.IsDisposed);
        Assert.False(second.IsDisposed);
        Assert.Throws<ObjectDisposedException>(() => first.Page("Late", _ => { }));
    }

    [Fact]
    public void Owner_sweep_disposes_a_settings_panel_hidden_by_escape()
    {
        var owner = new object();
        using var menu = ModsMenu.Register(owner, "example", "Example");
        var panel = SettingsPanel.OpenForMod("example");
        panel.Hide();
        Assert.False(SettingsPanel.IsOpen);
        ModsMenu.RemoveAll(owner);
        Assert.True(panel.IsDisposed);
        Assert.Empty(SettingsPanel.BoundRowIds());
    }

    [Fact]
    public void Unrelated_plugin_unload_does_not_close_another_mods_settings()
    {
        using var menu = ModsMenu.Register(new object(), "example", "Example");
        var panel = SettingsPanel.OpenForMod("example");
        ModsMenu.RemoveAll(new object());
        Assert.False(panel.IsDisposed);
        Assert.True(SettingsPanel.IsOpen);
    }

    [Fact]
    public void Close_from_a_custom_page_closes_the_workspace()
    {
        using var menu = ModsMenu.Register(new object(), "example", "Example");
        menu.Page("Status", p => { p.Button("Action"); });
        var page = ModsMenu.OpenPage("example", "Status");
        Assert.True(ModsMenu.IsOpen);
        Assert.False(page.IsDisposed);
        ModsMenu.Close();
        Assert.False(ModsMenu.IsOpen);
        Assert.True(page.IsDisposed);
        Assert.Equal(0, _runtime.Capture);
    }

    [Fact]
    public void CloseOrBack_from_a_custom_page_returns_to_the_mod_overview()
    {
        using var menu = ModsMenu.Register(new object(), "example", "Example");
        menu.Page("Status", p => { p.Button("Action"); });
        ModsMenu.OpenPage("example", "Status");
        Assert.Equal("back-to-mod", ModsMenu.CloseOrBack());
        Assert.True(ModsMenu.IsOpen);
        string json = JsonSerializer.Serialize(ModsMenu.DescribeWorkspace());
        Assert.Contains("\"page\":\"overview\"", json);
        Assert.Contains("\"plugin\":\"example\"", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CloseOrBack_from_overview_closes_the_workspace()
    {
        ModsMenu.Open();
        Assert.True(ModsMenu.IsOpen);
        Assert.Equal("close", ModsMenu.CloseOrBack());
        Assert.False(ModsMenu.IsOpen);
        Assert.Equal(0, _runtime.Capture);
    }

    [Theory]
    [InlineData(27)]
    [InlineData(17)]
    public void Escape_hide_logs_reason_escape_and_does_not_dispose(int keyCode)
    {
        using var menu = ModsMenu.Register(new object(), "example", "Example");
        var panel = ModsMenu.Open();
        Assert.True(ModsMenu.IsOpen);
        Assert.False(panel.IsDisposed);

        UiApi.PressKeyForTests(keyCode, down: true);

        Assert.False(ModsMenu.IsOpen);
        Assert.False(panel.IsDisposed);
        Assert.Contains(_log.Infos, line =>
            line.StartsWith(ModsMenu.WorkspaceTransitionPrefix + "hide ", StringComparison.Ordinal)
            && line.Contains("reason=escape", StringComparison.Ordinal)
            && line.Contains("caller=NotifyHiddenByEscape", StringComparison.Ordinal));
        Assert.DoesNotContain(_log.Infos, line =>
            line.StartsWith(ModsMenu.WorkspaceTransitionPrefix + "close ", StringComparison.Ordinal));
        string json = JsonSerializer.Serialize(ModsMenu.DescribeWorkspace());
        Assert.Contains("\"open\":false", json);
        Assert.Equal(1, _log.Infos.Count(line =>
            line.StartsWith(ModsMenu.WorkspaceTransitionPrefix + "hide ", StringComparison.Ordinal)));
    }

    [Fact]
    public void CloseOrBack_from_menu_hosted_settings_returns_to_the_mod()
    {
        using var menu = ModsMenu.Register(new object(), "example", "Example");
        ModsMenu.Open();
        ModsMenu.Select("example", "settings", null);
        Assert.True(SettingsPanel.IsMenuHostedOpen);
        Assert.Equal("back-to-mod", ModsMenu.CloseOrBack());
        Assert.True(ModsMenu.IsOpen);
        Assert.False(SettingsPanel.IsMenuHostedOpen);
    }

    [Fact]
    public void Select_and_search_refuse_when_the_workspace_is_closed()
    {
        var select = Assert.Throws<ArgumentException>(() => ModsMenu.Select("example", "settings", "Server"));
        Assert.Equal(ModsUiArgs.WorkspaceClosedError, select.Message);

        var selectTab = Assert.Throws<ArgumentException>(() => ModsMenu.SelectTab("install"));
        Assert.Equal(ModsUiArgs.WorkspaceClosedError, selectTab.Message);

        var search = Assert.Throws<ArgumentException>(() => ModsMenu.ApplySearch("x"));
        Assert.Equal(ModsUiArgs.WorkspaceClosedError, search.Message);

        var read = Assert.Throws<ArgumentException>(() => ModsMenu.ReadVisibleWidgets(null));
        Assert.Equal(ModsUiArgs.WorkspaceClosedError, read.Message);

        var click = Assert.Throws<ArgumentException>(() => ModsMenu.ClickVisibleWidget(null, "Close Mods", null, null));
        Assert.Equal(ModsUiArgs.WorkspaceClosedError, click.Message);
    }

    [Fact]
    public void FullscreenMenu_request_for_top_tab_is_a_no_op_in_the_test_build()
    {
        Assert.False(ModsMenu.TryRequestFullscreenMenu());
        Assert.Equal("none", ModsMenu.DescribeHudMenuType());
    }

    [Fact]
    public void DescribeWorkspace_uses_owned_bookkeeping_and_does_not_throw()
    {
        var described = ModsMenu.DescribeWorkspace();
        string json = JsonSerializer.Serialize(described);
        Assert.Contains("\"open\":false", json);
        Assert.Contains("\"panes\":0", json);
        Assert.Contains("\"curtain\":false", json);
        Assert.Contains("\"modsCapture\":false", json);
        Assert.Contains("\"observers\":0", json);
        Assert.Contains("\"site\":\"none\"", json);
        ModsMenu.Open();
        json = JsonSerializer.Serialize(ModsMenu.DescribeWorkspace());
        Assert.Contains("\"open\":true", json);
        Assert.Contains("\"modsCapture\":true", json);
        Assert.Contains("\"panes\":", json);
        NativeModsEntry.SetOwnedForTests("top-tab", curtain: true, observers: 1);
        ModsMenu.Close();
        Assert.False(ModsMenu.IsOpen);
        Assert.False(NativeModsEntry.DescribeOwned().Curtain);
        Assert.Equal(
            NativeModsEntry.FormatCurtainDroppedLog(NativeModsEntry.CurtainDropClose),
            NativeModsEntry.TestCurtainDroppedLog);
        json = JsonSerializer.Serialize(ModsMenu.DescribeWorkspace());
        Assert.Contains("\"modsCapture\":false", json);
        Assert.Contains("\"suspended\":false", json);
        Assert.Contains("\"serverList\":", json);
        Assert.Contains("\"state\":\"unavailable\"", json);
        Assert.Contains("\"serverListEpoch\":", json);
    }

    [Fact]
    public void Sidebar_rebuild_preserves_the_selected_plugin_and_page()
    {
        using var alpha = ModsMenu.Register(new object(), "alpha.mod", "Alpha");
        using var zed = ModsMenu.Register(new object(), "zed.mod", "Zed");
        ModsMenu.Open();
        ModsMenu.Select("zed.mod", "settings", null);
        Assert.True(SettingsPanel.IsMenuHostedOpen);

        ModsMenu.RebuildSidebar(ModsMenu.ReasonInventoryRefresh);

        Assert.True(ModsMenu.IsOpen);
        Assert.True(SettingsPanel.IsMenuHostedOpen);
        string json = JsonSerializer.Serialize(ModsMenu.DescribeWorkspace());
        Assert.Contains("\"plugin\":\"zed.mod\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"page\":\"settings\"", json);
        Assert.Contains(_log.Infos, line =>
            line.StartsWith(ModsMenu.WorkspaceTransitionPrefix + "sidebar-rebuild ", StringComparison.Ordinal)
            && line.Contains("reason=inventory-refresh", StringComparison.Ordinal)
            && line.Contains("caller=", StringComparison.Ordinal));
    }

    [Fact]
    public void Open_and_close_transition_lines_carry_a_reason()
    {
        ModsMenu.Open();
        Assert.Contains(_log.Infos, line =>
            line.StartsWith(ModsMenu.WorkspaceTransitionPrefix + "open ", StringComparison.Ordinal)
            && line.Contains("from=closed", StringComparison.Ordinal)
            && line.Contains("reason=", StringComparison.Ordinal)
            && line.Contains("caller=", StringComparison.Ordinal));
        ModsMenu.Close();
        Assert.Contains(_log.Infos, line =>
            line.StartsWith(ModsMenu.WorkspaceTransitionPrefix + "close ", StringComparison.Ordinal)
            && line.Contains("reason=", StringComparison.Ordinal)
            && line.Contains("caller=", StringComparison.Ordinal));
    }

    [Fact]
    public void Open_from_the_owned_button_logs_user_button_not_unknown()
    {
        ModsMenu.Open(ModsMenu.ReasonUserButton, "NativeModsEntry");
        Assert.Contains(_log.Infos, line =>
            line.StartsWith(ModsMenu.WorkspaceTransitionPrefix + "open ", StringComparison.Ordinal)
            && line.Contains("reason=user-button", StringComparison.Ordinal)
            && line.Contains("caller=NativeModsEntry", StringComparison.Ordinal));
    }

    [Fact]
    public void Shortcut_toggle_logs_the_key()
    {
        string reason = ModsMenu.ReasonShortcutFor(new KeyChord(UiKey.F10));
        Assert.Equal("shortcut:F10", reason);
        ModsMenu.Toggle(reason);
        Assert.Contains(_log.Infos, line =>
            line.StartsWith(ModsMenu.WorkspaceTransitionPrefix + "open ", StringComparison.Ordinal)
            && line.Contains("reason=shortcut:F10", StringComparison.Ordinal));
        ModsMenu.Toggle(reason);
        Assert.Contains(_log.Infos, line =>
            line.StartsWith(ModsMenu.WorkspaceTransitionPrefix + "close ", StringComparison.Ordinal)
            && line.Contains("reason=shortcut:F10", StringComparison.Ordinal));
    }

    [Fact]
    public void World_ended_closes_the_workspace_once_and_does_not_leave_a_shortcut_site()
    {
        BeginLifecycle();
        ModsMenu.Open();
        Assert.True(ModsMenu.IsOpen);
        string openJson = JsonSerializer.Serialize(ModsMenu.DescribeWorkspace());
        Assert.Contains("\"site\":\"shortcut\"", openJson);
        Assert.Equal(0, ModsMenu.LifecycleCloseCount);

        WorldEpoch.Advance(ClientWorldEpoch.EndedSource);

        Assert.False(ModsMenu.IsOpen);
        Assert.Equal(1, ModsMenu.LifecycleCloseCount);
        Assert.Equal(0, _runtime.Capture);
        Assert.Equal(1, _log.Infos.Count(line => line.StartsWith(ModsMenu.WorkspaceClosedLogPrefix, StringComparison.Ordinal)));
        Assert.Contains(ModsMenu.WorkspaceClosedLogPrefix + Handshake.WorkspaceCloseWorldEnded, _log.Infos);
        string json = JsonSerializer.Serialize(ModsMenu.DescribeWorkspace());
        Assert.Contains("\"open\":false", json);
        Assert.Contains("\"site\":\"none\"", json);
        Assert.Contains("\"modsCapture\":false", json);
        Assert.DoesNotContain("\"site\":\"shortcut\"", json);

        WorldEpoch.Advance(ClientWorldEpoch.EndedSource);
        Assert.Equal(1, ModsMenu.LifecycleCloseCount);
        Assert.Equal(1, _log.Infos.Count(line => line.StartsWith(ModsMenu.WorkspaceClosedLogPrefix, StringComparison.Ordinal)));
    }

    [Fact]
    public void Connection_ended_closes_the_workspace_once()
    {
        BeginLifecycle();
        ModsMenu.Open();
        Handshake.NotifyClientConnectionEnded();
        Assert.False(ModsMenu.IsOpen);
        Assert.Equal(1, ModsMenu.LifecycleCloseCount);
        Assert.Contains(ModsMenu.WorkspaceClosedLogPrefix + Handshake.WorkspaceCloseServerContextCleared, _log.Infos);
        Handshake.NotifyClientConnectionEnded();
        Assert.Equal(1, ModsMenu.LifecycleCloseCount);
    }

    [Fact]
    public void Handshake_epoch_change_closes_the_workspace_once()
    {
        BeginLifecycle();
        ModsMenu.Open();
        Handshake.NotifyHandshakeEpochChanged();
        Assert.False(ModsMenu.IsOpen);
        Assert.Equal(1, ModsMenu.LifecycleCloseCount);
        Assert.Contains(ModsMenu.WorkspaceClosedLogPrefix + Handshake.WorkspaceCloseServerContextCleared, _log.Infos);
        Handshake.NotifyHandshakeEpochChanged();
        Assert.Equal(1, ModsMenu.LifecycleCloseCount);
    }

    [Fact]
    public void Core_only_refresh_of_a_surviving_world_does_not_close()
    {
        BeginLifecycle();
        ModsMenu.Open();
        WorldEpoch.Advance(ClientWorldEpoch.CreatedSource);
        Assert.True(ModsMenu.IsOpen);
        Assert.Equal(0, ModsMenu.LifecycleCloseCount);
        Assert.DoesNotContain(_log.Infos, line => line.StartsWith(ModsMenu.WorkspaceClosedLogPrefix, StringComparison.Ordinal));
        Handshake.InvalidateClientServerContext("disconnect");
        Assert.True(ModsMenu.IsOpen);
        Assert.Equal(0, ModsMenu.LifecycleCloseCount);
    }

    [Fact]
    public void DescribeWorkspace_reports_server_list_unavailable_after_clear_and_available_after_restore()
    {
        BeginLifecycle();
        var catalog = new ManifestEnvelope(
            new ManifestBody(1, "Test", "", new ManifestOperator("", "", ""), null,
                new[] { new ManifestMod("example.mod", "1.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact, false) }),
            "");
        Assert.True(ModMenuInventory.RestoreServerContext("handshake-ok", Array.Empty<Handshake.ModRecord>(), catalog, 9));
        string json = JsonSerializer.Serialize(ModsMenu.DescribeWorkspace());
        Assert.Contains("\"state\":\"available\"", json);
        Assert.Contains("\"count\":1", json);
        Assert.Contains("\"serverListEpoch\":9", json);

        Assert.True(Handshake.InvalidateClientServerContext("disconnect", 9));
        json = JsonSerializer.Serialize(ModsMenu.DescribeWorkspace());
        Assert.Contains("\"state\":\"unavailable\"", json);
        Assert.Contains("\"serverListEpoch\":9", json);
        Assert.DoesNotContain("\"count\":", json);
    }

    [Fact]
    public void Search_filter_is_a_pure_string_match_and_workspace_capture_is_WhileShown()
    {
        using var menu = ModsMenu.Register(new object(), "alpha.mod", "Alpha");
        var panel = ModsMenu.Open();
        Assert.Equal(UiCaptureMode.WhileShown, panel.CaptureMode);

        Assert.True(ModsMenu.MatchesSearch("alpha.mod", "Alpha", "ALP"));
        Assert.True(ModsMenu.MatchesSearch("alpha.mod", "Alpha", "mod"));
        Assert.True(ModsMenu.MatchesSearch("alpha.mod", "Alpha", "  "));
        Assert.True(ModsMenu.MatchesSearch("alpha.mod", "Alpha", ""));
        Assert.False(ModsMenu.MatchesSearch("alpha.mod", "Alpha", "zzz"));
        Assert.False(ModsMenu.MatchesSearch("progenitor.sample", "Progenitor Sample", "fixture"));
    }

    [Fact]
    public void Typed_search_updates_status_and_filters_case_insensitively_and_clear_restores()
    {
        using var alpha = ModsMenu.Register(new object(), "alpha.mod", "Alpha");
        using var zed = ModsMenu.Register(new object(), "zed.mod", "Zed");
        ModsMenu.Open();

        string openJson = JsonSerializer.Serialize(ModsMenu.DescribeWorkspace());
        Assert.Contains("\"search\":\"\"", openJson);
        Assert.Contains("\"id\":\"alpha.mod\"", openJson);
        Assert.Contains("\"id\":\"zed.mod\"", openJson);
        Assert.Contains("\"visibleRows\":", openJson);
        Assert.Contains("\"totalRows\":", openJson);
        Assert.Contains("\"listScroll\":", openJson);

        Assert.True(_runtime.FireText(ModsMenu.SearchPlaceholder, "zEd"));
        string filtered = JsonSerializer.Serialize(ModsMenu.DescribeWorkspace());
        Assert.Contains("\"search\":\"zEd\"", filtered);
        Assert.Contains("\"id\":\"zed.mod\"", filtered);
        Assert.DoesNotContain("\"id\":\"alpha.mod\"", filtered);

        ModsMenu.ApplySearch("ALPHA");
        string byId = JsonSerializer.Serialize(ModsMenu.DescribeWorkspace());
        Assert.Contains("\"search\":\"ALPHA\"", byId);
        Assert.Contains("\"id\":\"alpha.mod\"", byId);
        Assert.DoesNotContain("\"id\":\"zed.mod\"", byId);

        ModsMenu.ApplySearch("zzz");
        string empty = JsonSerializer.Serialize(ModsMenu.DescribeWorkspace());
        Assert.Contains("\"search\":\"zzz\"", empty);
        Assert.Contains("\"visibleRows\":0", empty);
        var widgets = ModsMenu.ReadVisibleWidgets("list");
        Assert.Contains(widgets, w => JsonSerializer.Serialize(w).Contains("No matching mods.", StringComparison.Ordinal));

        ModsMenu.ApplySearch("");
        string restored = JsonSerializer.Serialize(ModsMenu.DescribeWorkspace());
        Assert.Contains("\"search\":\"\"", restored);
        Assert.Contains("\"id\":\"alpha.mod\"", restored);
        Assert.Contains("\"id\":\"zed.mod\"", restored);
        Assert.DoesNotContain("No matching mods.", restored);
    }

    [Fact]
    public void Each_search_character_updates_status_and_filters_through_the_text_slot()
    {
        using var alpha = ModsMenu.Register(new object(), "alpha.mod", "Alpha");
        using var fixture = ModsMenu.Register(new object(), "zed.mod", "Fixture");
        ModsMenu.Open();

        Assert.True(_runtime.FireText(ModsMenu.SearchPlaceholder, "f"));
        string afterF = JsonSerializer.Serialize(ModsMenu.DescribeWorkspace());
        Assert.Contains("\"search\":\"f\"", afterF);
        Assert.Contains("\"id\":\"zed.mod\"", afterF);
        Assert.DoesNotContain("\"id\":\"alpha.mod\"", afterF);

        Assert.True(_runtime.FireText(ModsMenu.SearchPlaceholder, "fi"));
        string afterFi = JsonSerializer.Serialize(ModsMenu.DescribeWorkspace());
        Assert.Contains("\"search\":\"fi\"", afterFi);
        Assert.Contains("\"id\":\"zed.mod\"", afterFi);

        Assert.True(_runtime.FireText(ModsMenu.SearchPlaceholder, "fix"));
        string afterFix = JsonSerializer.Serialize(ModsMenu.DescribeWorkspace());
        Assert.Contains("\"search\":\"fix\"", afterFix);
        Assert.Contains("\"id\":\"zed.mod\"", afterFix);
        Assert.Contains("\"visibleRows\":1", afterFix);
    }

    [Fact]
    public void Typed_mods_search_keeps_the_same_sidebar_and_batches_layout()
    {
        using var alpha = ModsMenu.Register(new object(), "alpha.mod", "Alpha");
        using var fixture = ModsMenu.Register(new object(), "zed.mod", "Fixture");
        ModsMenu.Open();
        var panel = ModsMenu.SidebarHeldForTests;
        Assert.NotNull(panel);

        Assert.True(_runtime.FireText(ModsMenu.SearchPlaceholder, "f"));
        Assert.Same(panel, ModsMenu.SidebarHeldForTests);
        Assert.True(_runtime.FireText(ModsMenu.SearchPlaceholder, "fi"));
        Assert.Same(panel, ModsMenu.SidebarHeldForTests);
        Assert.True(_runtime.FireText(ModsMenu.SearchPlaceholder, "fix"));
        Assert.Same(panel, ModsMenu.SidebarHeldForTests);
        Assert.Equal("fix", _runtime.WidgetValue("input", ModsMenu.SearchPlaceholder));

        string commit = SourcePin.MethodBody(
            SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "ModsMenu.cs"),
            "private static void CommitSearch");
        SourcePin.AssertUncommented(commit, "RefilterCatalogRows()");
        SourcePin.AssertNoStatementContaining(commit, "QueueSearchRebuild()");
        string refilter = SourcePin.MethodBody(
            SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "ModsMenu.cs"),
            "private static void RefilterCatalogRows");
        SourcePin.AssertUncommented(refilter, "UiLayoutBatch.Begin()");
        SourcePin.AssertUncommented(refilter, "UiLayoutBatch.End()");
        SourcePin.AssertUncommented(refilter, "row.Button.Visible");
        string build = SourcePin.MethodBody(
            SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "ModsMenu.cs"),
            "private static void BuildSidebarCore(");
        SourcePin.AssertUncommented(build, "RefilterCatalogRows()");
        SourcePin.AssertNoStatementContaining(build, "MatchesSearch(item.Key");
        // Revert: CommitSearch rebuilding the sidebar (QueueSearchRebuild / RebuildSidebar)
        // replaces the focused field. Dropping Begin/End around RefilterCatalogRows
        // runs one Relayout per toggled row.
    }

    [Fact]
    public void Sidebar_search_slot_is_wired_to_onValueChanged_at_creation()
    {
        string runtime = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "UnityUiRuntime.cs");
        string addInput = SourcePin.MethodBody(runtime, "public IRuntimeWidget? AddInput(IRuntimePanel panel");
        SourcePin.AssertUncommented(addInput, "field.onEndEdit");
        SourcePin.AssertUncommented(addInput, "host.UsesLiveText");
        SourcePin.AssertUncommented(addInput, "LiveTextEvent(field)");
        SourcePin.AssertNoStatementContaining(addInput, "field.onValueChanged");
        SourcePin.AssertStatementExactly(runtime, "private static object LiveTextEvent(TMP_InputField field) => field.onValueChanged;");
        SourcePin.AssertStatementExactly(runtime, "public bool UsesLiveText => _sidebar;");

        string menu = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "ModsMenu.cs");
        SourcePin.AssertNoStatementContaining(menu, "TryWireUi");
        SourcePin.AssertNoStatementContaining(menu, "BindSearchLiveFilter");
        SourcePin.AssertUncommented(menu, "search.OnChanged(OnSearchFieldChanged)");
        SourcePin.AssertUncommented(menu, "SyncSearchFromLiveField()");
        SourcePin.AssertUncommented(runtime, "ArmTextField(go, field)");
        SourcePin.AssertUncommented(runtime, "FocusOwnedField(found)");
        SourcePin.AssertUncommented(runtime, "field.ActivateInputField()");
        SourcePin.AssertUncommented(runtime, "PumpTypedKeys(");
        SourcePin.AssertUncommented(runtime, "ApplyTypedChar(field,");
        SourcePin.AssertUncommented(menu, "Ui.PinMenuRows(panel, MenuRowPin.Top)");
        SourcePin.AssertUncommented(menu, "Ui.PinMenuRows(panel, MenuRowPin.Content)");
        SourcePin.AssertUncommented(menu, "Ui.PinMenuRows(panel, MenuRowPin.Bottom)");
        SourcePin.AssertUncommented(menu, "Ui.StyleMenuRow(panel, MenuRowTone.Search)");
        SourcePin.AssertUncommented(menu, "panel.Input(SearchPlaceholder, _search)");
        SourcePin.AssertUncommented(addInput, "AttachSearchHints(");
        SourcePin.AssertUncommented(addInput, "BringSearchHintToFront(");
        SourcePin.AssertUncommented(runtime, "SyncSearchHint(field)");
        SourcePin.AssertUncommented(runtime, "tmp.text = text");
        SourcePin.AssertUncommented(runtime, "font.HasCharacter(s[i])");
        SourcePin.AssertUncommented(runtime, "SearchFieldColumns(ContentRowWidth)");
        SourcePin.AssertNoStatementContaining(runtime, "field.placeholder =");
        string attachPlaceholder = SourcePin.MethodBody(runtime, "private static void AttachPlaceholder(");
        SourcePin.AssertUncommented(attachPlaceholder, "tmp.overflowMode = TextOverflowModes.Overflow");
        SourcePin.AssertUncommented(attachPlaceholder, "tmp.enableWordWrapping = false");
        SourcePin.AssertNoStatementContaining(attachPlaceholder, "TextOverflowModes.Ellipsis");
        string attachHints = SourcePin.MethodBody(runtime, "private static void AttachSearchHints(");
        SourcePin.AssertUncommented(attachHints, "SetAsLastSibling()");
        string ops = SourcePin.ReadFile("src", "Progenitor.Dev", "Ops", "ModsUiOps.cs");
        SourcePin.AssertUncommented(ops, "WriteHintDrawability(");
        SourcePin.AssertUncommented(ops, "prefix + \"Drawable\"");
        SourcePin.AssertUncommented(ops, "prefix + \"Alpha\"");
        SourcePin.AssertUncommented(ops, "prefix + \"MeshCount\"");
        SourcePin.AssertUncommented(ops, "SearchHintIsDrawable(");
        SourcePin.AssertUncommented(ops, "WriteCaretDrawability(");
        SourcePin.AssertUncommented(ops, "caretDrawable");
        SourcePin.AssertUncommented(ops, "caretBlinkOn");
        SourcePin.AssertUncommented(ops, "caretIndex");
        SourcePin.AssertUncommented(ops, "caretClientY");
        SourcePin.AssertUncommented(ops, "caretClientH");
        SourcePin.AssertUncommented(ops, "fieldClientY");
        SourcePin.AssertUncommented(ops, "fieldClientH");
        SourcePin.AssertUncommented(ops, "caretContainedVertically");
        SourcePin.AssertUncommented(ops, "caretOnTextLine");
        SourcePin.AssertUncommented(ops, "OwnedCaretIsDrawable(");
        SourcePin.AssertUncommented(ops, "OwnedCaretContainedVertically(");
        SourcePin.AssertUncommented(runtime, "AttachOwnedCaret(");
        SourcePin.AssertUncommented(runtime, "SyncOwnedCarets()");
        SourcePin.AssertUncommented(runtime, "HideAllOwnedCarets()");
        SourcePin.AssertUncommented(runtime, "field.readOnly = true");
        SourcePin.AssertUncommented(runtime, "field.caretWidth = 0");
        SourcePin.AssertNoStatementContaining(runtime, "field.readOnly = false");
        // PR #6 (search caret fix): the caret X is measured from the accepted prefix with the
        // label's own font and spacing, because the input callback precedes TMP's glyph-cache
        // refresh; the earlier `textInfo.characterInfo` / `xAdvance` / `origin` read is gone.
        // TEXTAREA1: the prefix is the caret's own visual line (UiCaretLayout), measured as a
        // difference against a visible end mark so trailing spaces count and margins cancel.
        // TEXTAREA2: measured on the field's hidden ruler label, and TMP's own lines first.
        SourcePin.AssertUncommented(runtime, "ruler.GetPreferredValues(s + CaretMeasureMark).x - ruler.GetPreferredValues(CaretMeasureMark).x");
        SourcePin.AssertUncommented(runtime, "TryCaretFromTmpLines(");
        SourcePin.AssertNoStatementContaining(runtime, "info.characterInfo");
        string placeCaret = SourcePin.MethodBody(runtime, "private static void PlaceOwnedCaret(");
        SourcePin.AssertUncommented(placeCaret, "AnchoredFromParentLocal(");
        SourcePin.AssertUncommented(placeCaret, "ClampCenter(");
        SourcePin.AssertUncommented(placeCaret, "ClampHeight(");
        SourcePin.AssertNoStatementContaining(placeCaret, "rt.anchoredPosition = new Vector2(local.x, local.y)");
        string measureCaret = SourcePin.MethodBody(runtime, "private static void MeasureOwnedCaret(");
        SourcePin.AssertUncommented(measureCaret, "LineCenterY(");
        SourcePin.AssertUncommented(measureCaret, "UiCaretLayout.Locate(");
        SourcePin.AssertUncommented(measureCaret, "tmp.margin");
        SourcePin.AssertNoStatementContaining(measureCaret, "midY = (asc + desc)");
        string attachCaret = SourcePin.MethodBody(runtime, "private static void AttachOwnedCaret(");
        SourcePin.AssertUncommented(attachCaret, "armed.Field.textViewport");
        SourcePin.AssertUncommented(attachCaret, "RectMask2D");
        SourcePin.AssertUncommented(runtime, "TryAdd<RectMask2D>(go)");
        // The quad-built magnifier fallback is gone (user review 2026-10-04: no vanilla counterpart, it
        // read as a box with a tail). Without a font glyph the hint draws the word alone.
        Assert.DoesNotContain("DrawSearchGlyphIcon", runtime);
        string attachGlyph = SourcePin.MethodBody(runtime, "private static void AttachSearchGlyph(");
        SourcePin.AssertUncommented(attachGlyph, "TryDestroyImmediate(go);");
    }

    [Fact]
    public void Selecting_a_row_does_not_prefix_the_caption()
    {
        using var alpha = ModsMenu.Register(new object(), "alpha.mod", "Alpha");
        using var beta = ModsMenu.Register(new object(), "beta.mod", "Beta");
        ModsMenu.Open();
        string before = SidebarCaption("alpha.mod");
        ModsMenu.OpenMod("alpha.mod");
        string after = SidebarCaption("alpha.mod");
        Assert.Equal("Alpha", before);
        Assert.Equal(before, after);
        Assert.DoesNotContain("\u203a", after);
        Assert.Equal("Beta", SidebarCaption("beta.mod"));
    }

    private static string SidebarCaption(string pluginId)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(ModsMenu.DescribeWorkspace()));
        foreach (var row in doc.RootElement.GetProperty("entries").EnumerateArray())
        {
            if (row.GetProperty("id").GetString() == pluginId)
                return row.GetProperty("caption").GetString() ?? "";
        }

        return "";
    }

    [Fact]
    public void Selecting_a_row_preserves_a_specific_list_scroll_position()
    {
        using var alpha = ModsMenu.Register(new object(), "alpha.mod", "Alpha");
        using var beta = ModsMenu.Register(new object(), "beta.mod", "Beta");
        ModsMenu.Open();
        ModsMenu.ListScrollForTests = 0.77f;
        ModsMenu.OpenMod("beta.mod");
        Assert.InRange(ModsMenu.ListScrollForTests, 0.76f, 0.78f);

        ModsMenu.Close();
        ModsMenu.ListScrollForTests = 0.77f;
        ModsMenu.Open();
        Assert.Equal(1f, ModsMenu.ListScrollForTests);
    }

    [Fact]
    public void Search_caret_is_absent_until_focused_and_tracks_the_owned_index()
    {
        using var alpha = ModsMenu.Register(new object(), "alpha.mod", "Alpha");
        ModsMenu.Open();
        Assert.False(_runtime.WidgetCaretDrawable("input", ModsMenu.SearchPlaceholder));
        Assert.Equal(0, _runtime.WidgetCaret("input", ModsMenu.SearchPlaceholder));
        Assert.Equal(0f, _runtime.WidgetCaretX("input", ModsMenu.SearchPlaceholder));

        var keys = new[] { UiKey.B, UiKey.L, UiKey.O, UiKey.O, UiKey.D };
        float previousX = -1f;
        int expectedCaret = 0;
        foreach (var key in keys)
        {
            Assert.True(_runtime.TypeTextKey(ModsMenu.SearchPlaceholder, key));
            expectedCaret++;
            Assert.True(_runtime.WidgetCaretDrawable("input", ModsMenu.SearchPlaceholder));
            Assert.True(_runtime.WidgetCaretBlinkOn("input", ModsMenu.SearchPlaceholder));
            Assert.Equal(expectedCaret, _runtime.WidgetCaret("input", ModsMenu.SearchPlaceholder));
            float x = _runtime.WidgetCaretX("input", ModsMenu.SearchPlaceholder);
            Assert.Equal(expectedCaret * UiTests.FakeUiRuntime.FakeCaretAdvance, x);
            Assert.True(x > previousX);
            previousX = x;
            Assert.True(_runtime.WidgetCaretFitsInField("input", ModsMenu.SearchPlaceholder));
            Assert.True(_runtime.WidgetCaretOnTextLine("input", ModsMenu.SearchPlaceholder));
        }

        Assert.Equal("blood", _runtime.WidgetValue("input", ModsMenu.SearchPlaceholder));
        Assert.Equal(5 * UiTests.FakeUiRuntime.FakeCaretAdvance, _runtime.WidgetCaretX("input", ModsMenu.SearchPlaceholder));
        Assert.Empty(_runtime.RenderedPlaceholders("input"));
        Assert.True(_runtime.WidgetCaretFitsInField("input", ModsMenu.SearchPlaceholder));

        _runtime.ClearOwnedFocus();
        Assert.False(_runtime.WidgetCaretDrawable("input", ModsMenu.SearchPlaceholder));
        Assert.False(_runtime.WidgetCaretBlinkOn("input", ModsMenu.SearchPlaceholder));
    }

    [Fact]
    public void Search_caret_tracks_backspace_delete_home_end_arrows_and_coexists_with_the_placeholder()
    {
        using var alpha = ModsMenu.Register(new object(), "alpha.mod", "Alpha");
        ModsMenu.Open();
        foreach (var key in new[] { UiKey.A, UiKey.B, UiKey.C, UiKey.D })
            Assert.True(_runtime.TypeTextKey(ModsMenu.SearchPlaceholder, key));
        Assert.Equal(4 * UiTests.FakeUiRuntime.FakeCaretAdvance, _runtime.WidgetCaretX("input", ModsMenu.SearchPlaceholder));

        Assert.True(_runtime.TypeTextKey(ModsMenu.SearchPlaceholder, UiKey.LeftArrow));
        Assert.True(_runtime.TypeTextKey(ModsMenu.SearchPlaceholder, UiKey.LeftArrow));
        Assert.Equal(2, _runtime.WidgetCaret("input", ModsMenu.SearchPlaceholder));
        Assert.Equal(2 * UiTests.FakeUiRuntime.FakeCaretAdvance, _runtime.WidgetCaretX("input", ModsMenu.SearchPlaceholder));
        Assert.True(_runtime.WidgetCaretDrawable("input", ModsMenu.SearchPlaceholder));

        Assert.True(_runtime.TypeTextKey(ModsMenu.SearchPlaceholder, UiKey.Backspace));
        Assert.Equal("acd", _runtime.WidgetValue("input", ModsMenu.SearchPlaceholder));
        Assert.Equal(1, _runtime.WidgetCaret("input", ModsMenu.SearchPlaceholder));
        Assert.Equal(UiTests.FakeUiRuntime.FakeCaretAdvance, _runtime.WidgetCaretX("input", ModsMenu.SearchPlaceholder));

        Assert.True(_runtime.TypeTextKey(ModsMenu.SearchPlaceholder, UiKey.Delete));
        Assert.Equal("ad", _runtime.WidgetValue("input", ModsMenu.SearchPlaceholder));
        Assert.Equal(1, _runtime.WidgetCaret("input", ModsMenu.SearchPlaceholder));
        Assert.Equal(UiTests.FakeUiRuntime.FakeCaretAdvance, _runtime.WidgetCaretX("input", ModsMenu.SearchPlaceholder));

        Assert.True(_runtime.TypeTextKey(ModsMenu.SearchPlaceholder, UiKey.Home));
        Assert.Equal(0f, _runtime.WidgetCaretX("input", ModsMenu.SearchPlaceholder));
        Assert.True(_runtime.TypeTextKey(ModsMenu.SearchPlaceholder, UiKey.End));
        Assert.Equal(2 * UiTests.FakeUiRuntime.FakeCaretAdvance, _runtime.WidgetCaretX("input", ModsMenu.SearchPlaceholder));

        Assert.True(_runtime.TypeTextKey(ModsMenu.SearchPlaceholder, UiKey.A, ctrl: true));
        Assert.True(_runtime.TypeTextKey(ModsMenu.SearchPlaceholder, UiKey.Backspace));
        Assert.Equal("", _runtime.WidgetValue("input", ModsMenu.SearchPlaceholder));
        Assert.Equal(0f, _runtime.WidgetCaretX("input", ModsMenu.SearchPlaceholder));
        Assert.True(_runtime.WidgetCaretDrawable("input", ModsMenu.SearchPlaceholder));
        Assert.True(_runtime.WidgetCaretFitsInField("input", ModsMenu.SearchPlaceholder));
        Assert.True(_runtime.WidgetCaretOnTextLine("input", ModsMenu.SearchPlaceholder));
        Assert.Contains(ModsMenu.SearchPlaceholder, _runtime.RenderedPlaceholders("input"));
        Assert.Equal(ModsMenu.SearchPlaceholder.Length, _runtime.PlaceholderMeshCount("input", ModsMenu.SearchPlaceholder));
        Assert.True(MenuRowStyle.HintGraphicIsDrawable(
            assigned: true, active: true, graphicEnabled: true,
            alpha: MenuRowStyle.SearchPlaceholderA, rectW: 282f, rectH: 20f,
            hintSiblingIndex: 2, fillSiblingIndex: -1,
            meshCount: _runtime.PlaceholderMeshCount("input", ModsMenu.SearchPlaceholder)));
    }

    [Fact]
    public void Typing_blood_one_key_at_a_time_puts_blood_in_the_search_field()
    {
        using var alpha = ModsMenu.Register(new object(), "alpha.mod", "Alpha");
        using var blood = ModsMenu.Register(new object(), "blood.mod", "BloodPool");
        ModsMenu.Open();

        var keys = new[] { UiKey.B, UiKey.L, UiKey.O, UiKey.O, UiKey.D };
        int expectedCaret = 0;
        foreach (var key in keys)
        {
            Assert.True(_runtime.TypeTextKey(ModsMenu.SearchPlaceholder, key));
            expectedCaret++;
            Assert.Equal(expectedCaret, _runtime.WidgetCaret("input", ModsMenu.SearchPlaceholder));
            Assert.Equal(0, _runtime.WidgetNativeCaret("input", ModsMenu.SearchPlaceholder));
        }

        Assert.Equal("blood", _runtime.WidgetValue("input", ModsMenu.SearchPlaceholder));
        Assert.Equal(5, _runtime.WidgetCaret("input", ModsMenu.SearchPlaceholder));
        Assert.NotEqual(0, _runtime.WidgetCaret("input", ModsMenu.SearchPlaceholder));
        Assert.NotEqual("doolb", _runtime.WidgetValue("input", ModsMenu.SearchPlaceholder));
        string json = JsonSerializer.Serialize(ModsMenu.DescribeWorkspace());
        Assert.Contains("\"search\":\"blood\"", json);
        Assert.Contains("\"id\":\"blood.mod\"", json);
        Assert.DoesNotContain("\"id\":\"alpha.mod\"", json);
        Assert.DoesNotContain("bb", _runtime.WidgetValue("input", ModsMenu.SearchPlaceholder));
        Assert.DoesNotContain("dd", _runtime.WidgetValue("input", ModsMenu.SearchPlaceholder));
    }

    [Fact]
    public void Search_typing_into_the_middle_inserts_at_the_caret()
    {
        using var alpha = ModsMenu.Register(new object(), "alpha.mod", "Alpha");
        ModsMenu.Open();
        foreach (var key in new[] { UiKey.A, UiKey.B, UiKey.C, UiKey.D })
            Assert.True(_runtime.TypeTextKey(ModsMenu.SearchPlaceholder, key));
        Assert.Equal("abcd", _runtime.WidgetValue("input", ModsMenu.SearchPlaceholder));
        Assert.Equal(4, _runtime.WidgetCaret("input", ModsMenu.SearchPlaceholder));

        Assert.True(_runtime.TypeTextKey(ModsMenu.SearchPlaceholder, UiKey.LeftArrow));
        Assert.True(_runtime.TypeTextKey(ModsMenu.SearchPlaceholder, UiKey.LeftArrow));
        Assert.Equal(2, _runtime.WidgetCaret("input", ModsMenu.SearchPlaceholder));

        Assert.True(_runtime.TypeTextKey(ModsMenu.SearchPlaceholder, UiKey.X, shift: true));
        Assert.Equal("abXcd", _runtime.WidgetValue("input", ModsMenu.SearchPlaceholder));
        Assert.Equal(3, _runtime.WidgetCaret("input", ModsMenu.SearchPlaceholder));
        Assert.Equal(0, _runtime.WidgetNativeCaret("input", ModsMenu.SearchPlaceholder));
    }

    [Fact]
    public void Search_backspace_after_b_in_abcd_yields_acd()
    {
        using var alpha = ModsMenu.Register(new object(), "alpha.mod", "Alpha");
        ModsMenu.Open();
        foreach (var key in new[] { UiKey.A, UiKey.B, UiKey.C, UiKey.D })
            Assert.True(_runtime.TypeTextKey(ModsMenu.SearchPlaceholder, key));
        Assert.True(_runtime.TypeTextKey(ModsMenu.SearchPlaceholder, UiKey.LeftArrow));
        Assert.True(_runtime.TypeTextKey(ModsMenu.SearchPlaceholder, UiKey.LeftArrow));
        Assert.Equal(2, _runtime.WidgetCaret("input", ModsMenu.SearchPlaceholder));

        Assert.True(_runtime.TypeTextKey(ModsMenu.SearchPlaceholder, UiKey.Backspace));
        Assert.Equal("acd", _runtime.WidgetValue("input", ModsMenu.SearchPlaceholder));
        Assert.Equal(1, _runtime.WidgetCaret("input", ModsMenu.SearchPlaceholder));
    }

    [Fact]
    public void Search_backspace_at_position_0_does_nothing()
    {
        using var alpha = ModsMenu.Register(new object(), "alpha.mod", "Alpha");
        ModsMenu.Open();
        foreach (var key in new[] { UiKey.A, UiKey.B })
            Assert.True(_runtime.TypeTextKey(ModsMenu.SearchPlaceholder, key));
        Assert.True(_runtime.TypeTextKey(ModsMenu.SearchPlaceholder, UiKey.Home));
        Assert.Equal(0, _runtime.WidgetCaret("input", ModsMenu.SearchPlaceholder));
        Assert.Equal("ab", _runtime.WidgetValue("input", ModsMenu.SearchPlaceholder));

        Assert.True(_runtime.TypeTextKey(ModsMenu.SearchPlaceholder, UiKey.Backspace));
        Assert.Equal("ab", _runtime.WidgetValue("input", ModsMenu.SearchPlaceholder));
        Assert.Equal(0, _runtime.WidgetCaret("input", ModsMenu.SearchPlaceholder));
    }

    [Fact]
    public void Search_backspace_deletes_an_active_selection()
    {
        using var alpha = ModsMenu.Register(new object(), "alpha.mod", "Alpha");
        ModsMenu.Open();
        foreach (var key in new[] { UiKey.A, UiKey.B, UiKey.C, UiKey.D })
            Assert.True(_runtime.TypeTextKey(ModsMenu.SearchPlaceholder, key));
        Assert.True(_runtime.TypeTextKey(ModsMenu.SearchPlaceholder, UiKey.Home));
        Assert.True(_runtime.TypeTextKey(ModsMenu.SearchPlaceholder, UiKey.RightArrow, shift: true));
        Assert.True(_runtime.TypeTextKey(ModsMenu.SearchPlaceholder, UiKey.RightArrow, shift: true));
        Assert.Equal(2, _runtime.WidgetCaret("input", ModsMenu.SearchPlaceholder));
        Assert.Equal(0, _runtime.WidgetAnchor("input", ModsMenu.SearchPlaceholder));

        Assert.True(_runtime.TypeTextKey(ModsMenu.SearchPlaceholder, UiKey.Backspace));
        Assert.Equal("cd", _runtime.WidgetValue("input", ModsMenu.SearchPlaceholder));
        Assert.Equal(0, _runtime.WidgetCaret("input", ModsMenu.SearchPlaceholder));
    }

    [Fact]
    public void Escape_on_a_focused_non_empty_search_field_clears_once_and_keeps_the_workspace_open()
    {
        using var alpha = ModsMenu.Register(new object(), "alpha.mod", "Alpha");
        using var blood = ModsMenu.Register(new object(), "blood.mod", "BloodPool");
        using var zed = ModsMenu.Register(new object(), "zed.mod", "Zed");
        ModsMenu.Open();

        int before = ModsMenu.SearchFilterChanges;
        foreach (var key in new[] { UiKey.B, UiKey.L, UiKey.O, UiKey.O, UiKey.D })
            Assert.True(_runtime.TypeTextKey(ModsMenu.SearchPlaceholder, key));
        Assert.Equal("blood", _runtime.WidgetValue("input", ModsMenu.SearchPlaceholder));
        Assert.True(_runtime.TryReadSearchField(out _, out bool focused));
        Assert.True(focused);
        int afterType = ModsMenu.SearchFilterChanges;
        Assert.Equal(before + 5, afterType);

        // Production: EventSystem Cancel, then 27, then 17. UIC2 only fired 27 with
        // EventSystem focus still set, so the paired BackAction close never ran.
        Assert.True(_runtime.EventSystemFocused);
        PressEscapeLikeClient();
        Assert.False(_runtime.EventSystemFocused);

        Assert.True(ModsMenu.IsOpen);
        Assert.Equal("", _runtime.WidgetValue("input", ModsMenu.SearchPlaceholder));
        Assert.Equal(afterType + 1, ModsMenu.SearchFilterChanges);
        Assert.Contains(ModsMenu.SearchPlaceholder, _runtime.RenderedPlaceholders("input"));
        Assert.Equal(ModsMenu.SearchPlaceholder.Length, _runtime.PlaceholderMeshCount("input", ModsMenu.SearchPlaceholder));
        Assert.Contains(_log.Infos, line =>
            line == UiSearchEscape.Format(
                UiSearchEscape.Decision.Clear, focused: true, length: 5, alreadyCleared: false));
        Assert.Equal(1, _log.Infos.Count(line => line.StartsWith(UiSearchEscape.LogPrefix, StringComparison.Ordinal)));
        string json = JsonSerializer.Serialize(ModsMenu.DescribeWorkspace());
        Assert.Contains("\"search\":\"\"", json);
        Assert.Contains("\"id\":\"alpha.mod\"", json);
        Assert.Contains("\"id\":\"blood.mod\"", json);
        Assert.Contains("\"id\":\"zed.mod\"", json);
        Assert.DoesNotContain(_log.Infos, line =>
            line.StartsWith(ModsMenu.WorkspaceTransitionPrefix + "hide ", StringComparison.Ordinal)
            && line.Contains("reason=escape", StringComparison.Ordinal));
    }

    [Fact]
    public void Escape_on_the_top_tab_site_records_a_fullscreen_menu_dismiss()
    {
        NativeModsEntry.SetOwnedForTests("top-tab", curtain: true, observers: 7);
        using var alpha = ModsMenu.Register(new object(), "alpha.mod", "Alpha");
        ModsMenu.Open();
        Assert.True(ModsMenu.IsOpen);
        PressEscapeLikeClient();
        Assert.False(ModsMenu.IsOpen);
        Assert.True(ModsWorkspaceFrame.LastEscapeDismissedHud);
    }

    [Fact]
    public void Escape_on_the_main_menu_site_does_not_dismiss_a_fullscreen_menu()
    {
        NativeModsEntry.SetOwnedForTests("main-menu", curtain: true, observers: 0);
        using var alpha = ModsMenu.Register(new object(), "alpha.mod", "Alpha");
        ModsMenu.Open();
        PressEscapeLikeClient();
        Assert.False(ModsMenu.IsOpen);
        Assert.False(ModsWorkspaceFrame.LastEscapeDismissedHud);
    }

    [Fact]
    public void Escape_on_an_empty_search_field_closes_the_workspace()
    {
        using var alpha = ModsMenu.Register(new object(), "alpha.mod", "Alpha");
        ModsMenu.Open();
        Assert.True(ModsMenu.IsOpen);
        Assert.Equal("", _runtime.WidgetValue("input", ModsMenu.SearchPlaceholder));

        PressEscapeLikeClient();

        Assert.False(ModsMenu.IsOpen);
        Assert.Contains(_log.Infos, line =>
            line.StartsWith(ModsMenu.WorkspaceTransitionPrefix + "hide ", StringComparison.Ordinal)
            && line.Contains("reason=escape", StringComparison.Ordinal));
        Assert.Contains(_log.Infos, line =>
            line == UiSearchEscape.Format(
                UiSearchEscape.Decision.Close, focused: false, length: 0, alreadyCleared: false));
    }

    [Fact]
    public void Escape_clears_then_a_second_escape_leaves_the_field_and_a_third_closes()
    {
        using var alpha = ModsMenu.Register(new object(), "alpha.mod", "Alpha");
        ModsMenu.Open();
        Assert.True(_runtime.TypeTextKey(ModsMenu.SearchPlaceholder, UiKey.Z));
        Assert.Equal("z", _runtime.WidgetValue("input", ModsMenu.SearchPlaceholder));

        PressEscapeLikeClient();
        Assert.True(ModsMenu.IsOpen);
        Assert.Equal("", _runtime.WidgetValue("input", ModsMenu.SearchPlaceholder));
        // The clear rebuilds the list; live, RestoreSearchFocus gives the rebuilt box focus again.
        Assert.True(_runtime.FocusSearchField());

        // A second 27+17 without a release is a held key, not a new press.
        PressEscapeLikeClient();
        Assert.True(ModsMenu.IsOpen);
        Assert.Equal(0, _runtime.LeaveSearchCalls);

        // SEARCHCLEAR1: the second press leaves the empty field; the workspace stays.
        ReleaseEscapeLikeClient();
        PressEscapeLikeClient();
        Assert.True(ModsMenu.IsOpen);
        Assert.Equal(1, _runtime.LeaveSearchCalls);
        Assert.True(_runtime.TryReadSearchField(out _, out bool focusedAfterLeave));
        Assert.False(focusedAfterLeave);
        Assert.Contains(_log.Infos, line =>
            line == UiSearchEscape.Format(UiSearchEscape.Decision.Leave, focused: true, length: 0, alreadyCleared: false));
        Assert.DoesNotContain(_log.Infos, line =>
            line.StartsWith(ModsMenu.WorkspaceTransitionPrefix + "hide ", StringComparison.Ordinal));

        // The third closes.
        ReleaseEscapeLikeClient();
        PressEscapeLikeClient();
        Assert.False(ModsMenu.IsOpen);
    }

    [Fact]
    public void A_search_left_by_close_is_gone_at_the_next_open()
    {
        using var alpha = ModsMenu.Register(new object(), "alpha.mod", "Alpha");
        using var beta = ModsMenu.Register(new object(), "beta.mod", "Beta");
        ModsMenu.Open();
        foreach (var key in new[] { UiKey.U, UiKey.P, UiKey.D })
            Assert.True(_runtime.TypeTextKey(ModsMenu.SearchPlaceholder, key));
        Assert.Equal("upd", ModsMenu.ModsSearchForTests);

        // The user report (2026-10-09): text typed for something else stayed in the search, and the
        // next open read "No matching mods".
        ModsMenu.Close();
        Assert.Equal("", ModsMenu.ModsSearchForTests);
        ModsMenu.Open();
        Assert.Equal("", ModsMenu.ModsSearchForTests);
        Assert.Equal("", _runtime.WidgetValue("input", ModsMenu.SearchPlaceholder));
    }

    [Fact]
    public void A_search_left_by_an_escape_that_hides_the_workspace_is_gone_at_the_next_open()
    {
        using var alpha = ModsMenu.Register(new object(), "alpha.mod", "Alpha");
        ModsMenu.Open();
        Assert.True(_runtime.TypeTextKey(ModsMenu.SearchPlaceholder, UiKey.Z));
        _runtime.ClearOwnedFocus();

        PressEscapeLikeClient();
        Assert.False(ModsMenu.IsOpen);
        Assert.Equal("", ModsMenu.ModsSearchForTests);
        ReleaseEscapeLikeClient();

        ModsMenu.Open();
        Assert.Equal("", ModsMenu.ModsSearchForTests);
        Assert.Equal("", _runtime.WidgetValue("input", ModsMenu.SearchPlaceholder));
    }

    [Fact]
    public void Switching_tabs_while_the_workspace_stays_open_keeps_the_search()
    {
        using var alpha = ModsMenu.Register(new object(), "alpha.mod", "Alpha");
        ModsMenu.Open();
        Assert.True(_runtime.TypeTextKey(ModsMenu.SearchPlaceholder, UiKey.Z));
        Assert.Equal("z", ModsMenu.ModsSearchForTests);

        ModsMenu.SelectWorkspaceTab(ModsTab.Preferences);
        ModsMenu.SelectWorkspaceTab(ModsTab.Mods);
        Assert.True(ModsMenu.IsOpen);
        Assert.Equal("z", ModsMenu.ModsSearchForTests);
    }

    [Fact]
    public void Escape_on_an_unfocused_non_empty_search_field_closes_the_workspace()
    {
        using var alpha = ModsMenu.Register(new object(), "alpha.mod", "Alpha");
        ModsMenu.Open();
        Assert.True(_runtime.TypeTextKey(ModsMenu.SearchPlaceholder, UiKey.Z));
        _runtime.ClearOwnedFocus();
        Assert.True(_runtime.TryReadSearchField(out string text, out bool focused));
        Assert.Equal("z", text);
        Assert.False(focused);

        PressEscapeLikeClient();
        Assert.False(ModsMenu.IsOpen);
        Assert.Contains(_log.Infos, line =>
            line == UiSearchEscape.Format(
                UiSearchEscape.Decision.Close, focused: false, length: 1, alreadyCleared: false));
    }

    [Fact]
    public void Search_held_backspace_deletes_at_the_initial_delay_then_the_repeat_interval()
    {
        using var alpha = ModsMenu.Register(new object(), "alpha.mod", "Alpha");
        ModsMenu.Open();
        foreach (var key in new[] { UiKey.A, UiKey.B, UiKey.C, UiKey.D, UiKey.E })
            Assert.True(_runtime.TypeTextKey(ModsMenu.SearchPlaceholder, key));
        Assert.Equal("abcde", _runtime.WidgetValue("input", ModsMenu.SearchPlaceholder));

        var repeat = new UiKeyRepeat();
        void Pump(bool held, bool edge, long nowMs)
        {
            var command = UiTypedKeys.Decide(
                UiKey.Backspace, ctrl: false, alt: false, meta: false, shift: false);
            bool fire = edge;
            if (UiKeyRepeat.IsRepeatable(command.Kind))
            {
                if (repeat.Observe(UiKey.Backspace, held, edge, nowMs))
                    fire = true;
            }
            else
                repeat.Reset(UiKey.Backspace);
            if (fire)
                Assert.True(_runtime.TypeTextKey(ModsMenu.SearchPlaceholder, UiKey.Backspace));
        }

        Pump(held: true, edge: true, nowMs: 0);
        Assert.Equal("abcd", _runtime.WidgetValue("input", ModsMenu.SearchPlaceholder));

        Pump(held: true, edge: false, nowMs: 399);
        Assert.Equal("abcd", _runtime.WidgetValue("input", ModsMenu.SearchPlaceholder));

        Pump(held: true, edge: false, nowMs: UiKeyRepeat.InitialDelayMs);
        Assert.Equal("abc", _runtime.WidgetValue("input", ModsMenu.SearchPlaceholder));

        Pump(held: true, edge: false, nowMs: UiKeyRepeat.InitialDelayMs + UiKeyRepeat.RepeatIntervalMs);
        Assert.Equal("ab", _runtime.WidgetValue("input", ModsMenu.SearchPlaceholder));

        Pump(held: false, edge: false, nowMs: 600);
        Assert.Equal("ab", _runtime.WidgetValue("input", ModsMenu.SearchPlaceholder));

        Pump(held: true, edge: false, nowMs: 1000);
        Assert.Equal("ab", _runtime.WidgetValue("input", ModsMenu.SearchPlaceholder));

        _runtime.ClearOwnedFocus();
        repeat.Reset();
        Pump(held: true, edge: false, nowMs: 1400);
        Assert.Equal("ab", _runtime.WidgetValue("input", ModsMenu.SearchPlaceholder));
    }

    [Fact]
    public void Search_field_does_not_accept_ctrl_or_ctrl_a_as_characters()
    {
        using var alpha = ModsMenu.Register(new object(), "alpha.mod", "Alpha");
        ModsMenu.Open();
        Assert.True(_runtime.TypeTextKey(ModsMenu.SearchPlaceholder, UiKey.B));
        Assert.Equal("b", _runtime.WidgetValue("input", ModsMenu.SearchPlaceholder));

        Assert.True(_runtime.TypeTextKey(ModsMenu.SearchPlaceholder, UiKey.LeftCtrl, ctrl: true));
        Assert.Equal("b", _runtime.WidgetValue("input", ModsMenu.SearchPlaceholder));
        Assert.DoesNotContain("5", _runtime.WidgetValue("input", ModsMenu.SearchPlaceholder));

        Assert.True(_runtime.TypeTextKey(ModsMenu.SearchPlaceholder, UiKey.A, ctrl: true));
        Assert.Equal("b", _runtime.WidgetValue("input", ModsMenu.SearchPlaceholder));
        Assert.DoesNotContain("a", _runtime.WidgetValue("input", ModsMenu.SearchPlaceholder));
    }

    [Fact]
    public void Overview_refresh_button_resets_the_list_scroll_to_the_top()
    {
        using var alpha = ModsMenu.Register(new object(), "alpha.mod", "Alpha");
        using var beta = ModsMenu.Register(new object(), "beta.mod", "Beta");
        ModsMenu.Open();
        Assert.Equal(1f, ModsMenu.ListScrollForTests);

        ModsMenu.OpenMod("beta.mod");
        ModsMenu.ListScrollForTests = 0.4f;
        // Open() while already open is not this button: it still preserves.
        ModsMenu.Open();
        Assert.Equal(0.4f, ModsMenu.ListScrollForTests);

        var click = ModsMenu.ClickVisibleWidget("list", "Overview / refresh", null, null);
        using (var clickDoc = JsonDocument.Parse(JsonSerializer.Serialize(click)))
            Assert.Equal(1, clickDoc.RootElement.GetProperty("invoked").GetInt32());

        // Unity ScrollRect.verticalNormalizedPosition: 1 is the top, 0 is the bottom.
        Assert.Equal(1f, ModsMenu.ListScrollForTests);
        Assert.NotEqual(0.4f, ModsMenu.ListScrollForTests);
        Assert.NotEqual(0f, ModsMenu.ListScrollForTests);

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(ModsMenu.DescribeWorkspace()));
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("plugin").ValueKind);
        Assert.Equal("overview", doc.RootElement.GetProperty("page").GetString());
        Assert.Equal(1.0, doc.RootElement.GetProperty("listScroll").GetProperty("position").GetDouble());
        Assert.True(doc.RootElement.TryGetProperty("contentScroll", out _));

        ModsMenu.Close();
        ModsMenu.ListScrollForTests = 0.4f;
        ModsMenu.Open();
        Assert.Equal(1f, ModsMenu.ListScrollForTests);
    }

    [Fact]
    public void Overview_refresh_clears_search_once_restores_the_placeholder_and_keeps_scroll_at_top()
    {
        using var alpha = ModsMenu.Register(new object(), "alpha.mod", "Alpha");
        using var blood = ModsMenu.Register(new object(), "blood.mod", "BloodPool");
        using var zed = ModsMenu.Register(new object(), "zed.mod", "Zed");
        ModsMenu.Open();

        int beforeType = ModsMenu.SearchFilterChanges;
        Assert.True(_runtime.FireText(ModsMenu.SearchPlaceholder, "zEd"));
        Assert.Equal(beforeType + 1, ModsMenu.SearchFilterChanges);
        string filtered = JsonSerializer.Serialize(ModsMenu.DescribeWorkspace());
        Assert.Contains("\"search\":\"zEd\"", filtered);
        Assert.Contains("\"id\":\"zed.mod\"", filtered);
        Assert.DoesNotContain("\"id\":\"alpha.mod\"", filtered);
        Assert.Empty(_runtime.RenderedPlaceholders("input"));
        Assert.Equal(0, _runtime.PlaceholderMeshCount("input", ModsMenu.SearchPlaceholder));

        ModsMenu.ListScrollForTests = 0.4f;
        int afterFilter = ModsMenu.SearchFilterChanges;
        var click = ModsMenu.ClickVisibleWidget("list", "Overview / refresh", null, null);
        using (var clickDoc = JsonDocument.Parse(JsonSerializer.Serialize(click)))
            Assert.Equal(1, clickDoc.RootElement.GetProperty("invoked").GetInt32());

        Assert.Equal(afterFilter + 1, ModsMenu.SearchFilterChanges);
        Assert.Equal("", _runtime.WidgetValue("input", ModsMenu.SearchPlaceholder));
        Assert.Contains(ModsMenu.SearchPlaceholder, _runtime.RenderedPlaceholders("input"));
        Assert.Equal(ModsMenu.SearchPlaceholder.Length, _runtime.PlaceholderMeshCount("input", ModsMenu.SearchPlaceholder));
        Assert.Equal(ModsMenu.SearchPlaceholder.Length, _runtime.PlaceholderMeshCount("input", ModsMenu.SearchPlaceholder));
        Assert.True(MenuRowStyle.HintGraphicIsDrawable(
            assigned: true, active: true, graphicEnabled: true,
            alpha: MenuRowStyle.SearchPlaceholderA, rectW: 282f, rectH: 20f,
            hintSiblingIndex: 1, fillSiblingIndex: -1,
            meshCount: _runtime.PlaceholderMeshCount("input", ModsMenu.SearchPlaceholder)));

        string restored = JsonSerializer.Serialize(ModsMenu.DescribeWorkspace());
        Assert.Contains("\"search\":\"\"", restored);
        Assert.Contains("\"id\":\"alpha.mod\"", restored);
        Assert.Contains("\"id\":\"blood.mod\"", restored);
        Assert.Contains("\"id\":\"zed.mod\"", restored);
        Assert.Equal(1f, ModsMenu.ListScrollForTests);

        using var doc = JsonDocument.Parse(restored);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("plugin").ValueKind);
        Assert.Equal("overview", doc.RootElement.GetProperty("page").GetString());
        Assert.Equal(1.0, doc.RootElement.GetProperty("listScroll").GetProperty("position").GetDouble());

        Assert.True(_runtime.FireText(ModsMenu.SearchPlaceholder, "zEd"));
        int afterRetype = ModsMenu.SearchFilterChanges;
        ModsMenu.OpenMod("zed.mod");
        Assert.Equal(afterRetype, ModsMenu.SearchFilterChanges);
        Assert.Equal("zEd", _runtime.WidgetValue("input", ModsMenu.SearchPlaceholder));

        ModsMenu.RefreshDirectory();
        Assert.Equal(afterRetype, ModsMenu.SearchFilterChanges);
        Assert.Equal("zEd", _runtime.WidgetValue("input", ModsMenu.SearchPlaceholder));

        using var extra = ModsMenu.Register(new object(), "extra.mod", "Extra");
        ModsMenu.RefreshDirectory();
        Assert.Equal(afterRetype, ModsMenu.SearchFilterChanges);
        Assert.Equal("zEd", _runtime.WidgetValue("input", ModsMenu.SearchPlaceholder));
    }

    [Fact]
    public void DescribeWorkspace_listScroll_is_not_the_content_pane_position()
    {
        using var alpha = ModsMenu.Register(new object(), "alpha.mod", "Alpha");
        using var beta = ModsMenu.Register(new object(), "beta.mod", "Beta");
        ModsMenu.Open();
        ModsMenu.OpenMod("beta.mod");
        ModsMenu.ListScrollForTests = 0.728f;
        ModsMenu.ContentScrollForTests = 0f;
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(ModsMenu.DescribeWorkspace()));
        Assert.Equal(0.728, doc.RootElement.GetProperty("listScroll").GetProperty("position").GetDouble());
        Assert.Equal(0.0, doc.RootElement.GetProperty("contentScroll").GetProperty("position").GetDouble());
        Assert.NotEqual(
            doc.RootElement.GetProperty("contentScroll").GetProperty("position").GetDouble(),
            doc.RootElement.GetProperty("listScroll").GetProperty("position").GetDouble());
    }

    [Fact]
    public void Sidebar_search_uses_the_placeholder_not_a_detached_label()
    {
        using var alpha = ModsMenu.Register(new object(), "alpha.mod", "Alpha");
        ModsMenu.Open();
        // Container caption is not what draws. The empty field's hint TMP is.
        Assert.DoesNotContain("Search", _runtime.WidgetTexts("input"));
        Assert.DoesNotContain("Search mods", _runtime.WidgetTexts("input"));
        Assert.Contains(ModsMenu.SearchPlaceholder, _runtime.RenderedPlaceholders("input"));
        Assert.Equal(new[] { "(icon)" }, _runtime.RenderedGlyphs("input"));
        Assert.True(MenuRowStyle.SearchHintVisible(""));
        Assert.False(MenuRowStyle.SearchHintVisible("zEd"));
    }

    [Fact]
    public void DescribeWorkspace_reports_overflow_once_the_list_has_enough_rows()
    {
        var owner = new object();
        var regs = new List<IDisposable>();
        try
        {
            for (int i = 1; i <= 24; i++)
                regs.Add(ModsMenu.Register(owner, "overflow.mod." + i.ToString("00"), "Overflow " + i.ToString("00")));
            ModsMenu.Open();
            string json = JsonSerializer.Serialize(ModsMenu.DescribeWorkspace());
            Assert.Contains("\"overflow\":true", json);
            Assert.Contains("\"position\":", json);
            using var doc = JsonDocument.Parse(json);
            int total = doc.RootElement.GetProperty("totalRows").GetInt32();
            int visible = doc.RootElement.GetProperty("visibleRows").GetInt32();
            Assert.True(total >= 24);
            Assert.True(visible < total);
            Assert.InRange(visible, 8, 20);
        }
        finally
        {
            foreach (var reg in regs) reg.Dispose();
        }
    }

    /// <summary>
    /// The named 2026-09-10 freeze: <c>FindFont</c> swept the whole scene per sidebar row, and
    /// each fixture <c>Dispose</c> rebuilt that sidebar. N rows must not produce N sweeps or N
    /// rebuilds. Drives <see cref="ModsMenu.RefreshDirectory"/> and
    /// <see cref="ModMenuRegistration.Dispose"/> through the real runtime seam, not a source pin.
    /// </summary>
    [Fact]
    public void Sidebar_rebuild_scene_sweeps_and_fixture_dispose_stay_bounded()
    {
        const int n = 200;
        var owner = new object();
        var regs = new List<IDisposable>(n);
        try
        {
            ModsMenu.Open();
            for (int i = 1; i <= n; i++)
                regs.Add(ModsMenu.Register(owner, "freeze.row." + i.ToString("000"), "Freeze " + i.ToString("000")));

            UiFontLookup.Invalidate();
            int sweepsBefore = _runtime.SceneSweepCount;
            int showsBefore = _runtime.ShowCount;
            ModsMenu.RefreshDirectory();
            int sweeps = _runtime.SceneSweepCount - sweepsBefore;
            int shows = _runtime.ShowCount - showsBefore;
            Assert.Equal(1, sweeps);
            Assert.Equal(1, shows);

            int disposeSweepsBefore = _runtime.SceneSweepCount;
            int disposeShowsBefore = _runtime.ShowCount;
            foreach (var reg in regs) reg.Dispose();
            regs.Clear();
            Assert.Equal(0, _runtime.ShowCount - disposeShowsBefore);
            Assert.Equal(0, _runtime.SceneSweepCount - disposeSweepsBefore);
            Assert.True(ModsMenu.DirectoryRefreshQueuedForTests);

            ModsMenu.FlushDirectoryRefreshForTests();
            Assert.False(ModsMenu.DirectoryRefreshQueuedForTests);
            Assert.Equal(1, _runtime.ShowCount - disposeShowsBefore);
            string json = JsonSerializer.Serialize(ModsMenu.DescribeWorkspace());
            Assert.DoesNotContain("freeze.row.", json, StringComparison.Ordinal);
        }
        finally
        {
            foreach (var reg in regs) reg.Dispose();
            ModsMenu.FlushDirectoryRefreshForTests();
        }
    }

    [Fact]
    public void Strip_has_five_buttons_and_gold_on_the_selected_tab()
    {
        ModsMenu.Open();
        var rows = ModsMenu.LastStripRowsForTests;
        // Three homes (decisions 2026-10-06): Mods / Install Mods / Keybinds / Preferences / Server.
        Assert.Equal(5, rows.Length);
        Assert.Equal(ModsTabStrip.KeybindsCaption, rows[2].Caption);
        Assert.Equal(ModsTabStrip.PreferencesCaption, rows[3].Caption);
        Assert.Equal(ModsTabStrip.ServerCaption, rows[4].Caption);
        Assert.Equal(MenuRowTone.Catalog, rows[4].Tone);
        Assert.Equal(ModsTabStrip.ModsCaption, rows[0].Caption);
        Assert.Equal(MenuRowTone.Selected, rows[0].Tone);
        Assert.Equal(ModsTabStrip.InstallModsCaption, rows[1].Caption);
        Assert.Equal(MenuRowTone.Catalog, rows[1].Tone);
        Assert.Equal(MenuRowTone.Catalog, rows[2].Tone);
        Assert.NotEqual(MenuRowTone.Chrome, rows[0].Tone);

        var widgets = ModsMenu.ReadVisibleWidgets(null);
        string json = JsonSerializer.Serialize(widgets);
        Assert.Contains(ModsTabStrip.ModsCaption, json);
        Assert.Contains(ModsTabStrip.InstallModsCaption, json);
        Assert.Contains(ModsTabStrip.PreferencesCaption, json);

        ModsMenu.SelectTab("install");
        rows = ModsMenu.LastStripRowsForTests;
        Assert.Equal(MenuRowTone.Catalog, rows[0].Tone);
        Assert.Equal(MenuRowTone.Selected, rows[1].Tone);
        Assert.Equal(MenuRowTone.Catalog, rows[2].Tone);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(ModsMenu.DescribeWorkspace()));
        Assert.Equal("install", doc.RootElement.GetProperty("tab").GetString());
        // Revert: dropping ModsTabStrip.Build, or using Chrome for the selected tab, fails this.
    }

    [Fact]
    public void Overview_widgets_stay_centre_while_Configuration_content_opts_into_left()
    {
        ModsMenu.Open();
        string overview = JsonSerializer.Serialize(ModsMenu.ReadVisibleWidgets("content"));
        Assert.Contains("\"align\":\"centre\"", overview);
        Assert.DoesNotContain("\"align\":\"left\"", overview);

        string dir = Path.Combine(Path.GetTempPath(), "progenitor-align1-cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = new BepInEx.Configuration.ConfigFile(Path.Combine(dir, ConfigHelper.SharedFileName), saveOnInit: true);
        try
        {
            HarmonySlotHandoff.SetSkipModules(null, refusedLiveWorld: false);
            ModsMenu.SetConfigFileForTests(file);
            ModsMenu.BindModsConfig(file);
            ModsMenu.SelectTab("configuration");
            string content = JsonSerializer.Serialize(ModsMenu.ReadVisibleWidgets("content"));
            Assert.Contains("\"align\":\"left\"", content);
        }
        finally
        {
            ConfigHelper.UninstallAll();
            try { Directory.Delete(dir, recursive: true); }
            catch { /* temp */ }
        }

        string deny = SourcePin.ReadFile("src", "Progenitor.Core", "Networking", "ModManifestDenyPanel.cs");
        SourcePin.AssertNoStatementContaining(deny, "ContentAlignment");
        string open = SourcePin.MethodBody(
            SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "ModsMenu.cs"),
            "public static IUiPanel Open(");
        SourcePin.AssertNoStatementContaining(open, "ContentAlignment");
        // Revert: leaving Configuration on center, or opting overview/deny into left, fails this.
    }

    [Fact]
    public void Strip_has_no_title_and_rects_rows_have_exactly_one_selected()
    {
        ModsMenu.Open();
        var widgets = ModsMenu.ReadVisibleWidgets(null);
        string json = JsonSerializer.Serialize(widgets);
        Assert.DoesNotContain(ModsMenu.StripPanelTitle, json, StringComparison.Ordinal);
        Assert.DoesNotContain("Mods workspace tabs", json, StringComparison.Ordinal);

        var strip = ModsMenu.DescribeStripRows(visible: true);
        Assert.Equal(5, strip.Length);
        string stripJson = JsonSerializer.Serialize(strip);
        Assert.Contains("\"kind\":\"tab-strip\"", stripJson);
        Assert.Contains("\"name\":\"Mods\"", stripJson);
        Assert.Contains("\"name\":\"Install Mods\"", stripJson);
        Assert.Contains("\"name\":\"Preferences\"", stripJson);
        Assert.Contains("\"name\":\"Server\"", stripJson);
        int selected = 0;
        foreach (var row in strip)
        {
            using var el = JsonDocument.Parse(JsonSerializer.Serialize(row));
            if (el.RootElement.GetProperty("selected").GetBoolean()) selected++;
            Assert.True(el.RootElement.GetProperty("visible").GetBoolean());
            Assert.True(el.RootElement.GetProperty("width").GetSingle() > 1f);
            Assert.True(el.RootElement.GetProperty("height").GetSingle() > 1f);
        }

        Assert.Equal(1, selected);

        ModsMenu.SelectTab("configuration");
        strip = ModsMenu.DescribeStripRows(visible: true);
        selected = 0;
        string selectedName = "";
        foreach (var row in strip)
        {
            using var el = JsonDocument.Parse(JsonSerializer.Serialize(row));
            if (!el.RootElement.GetProperty("selected").GetBoolean()) continue;
            selected++;
            selectedName = el.RootElement.GetProperty("name").GetString() ?? "";
        }

        Assert.Equal(1, selected);
        Assert.Equal(ModsTabStrip.PreferencesCaption, selectedName);

        string menu = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "ModsMenu.cs");
        string body = SourcePin.MethodBody(menu, "private static void BuildStrip");
        SourcePin.AssertUncommented(body, "ConfigureStripPanel(panel)");
        SourcePin.AssertNoStatementContaining(body, "ConfigureMenuPanel(");
        string configure = SourcePin.MethodBody(menu, "private static void ConfigureStripPanel");
        SourcePin.AssertUncommented(configure, "ConfigureMenuStrip(");
        string runtime = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "UnityUiRuntime.cs");
        SourcePin.AssertUncommented(runtime, "MenuPanelLayout.ForStrip(frame)");
        SourcePin.AssertUncommented(runtime, "Title.SetActive(false)");
        SourcePin.AssertUncommented(runtime, "host.IsSidebar || host.IsStrip");
        string stripConfig = SourcePin.MethodBody(runtime, "public void ConfigureStrip");
        SourcePin.AssertUncommented(stripConfig, "_scrollbar.SetActive(false)");
        string pass = SourcePin.MethodBody(runtime, "private void RunLayout(");
        Assert.DoesNotContain("_scrollbar.SetActive(", pass);
        // Revert: dropping ConfigureStrip, or leaving the titled panel chrome, fails this.
    }

    [Fact]
    public void Strip_hides_with_the_workspace()
    {
        ModsMenu.Open();
        Assert.True(ModsMenu.StripIsShown);
        Assert.Equal(5, ModsMenu.DescribeStripRows(visible: true).Length);

        ModsMenu.Close();
        Assert.False(ModsMenu.IsOpen);
        Assert.False(ModsMenu.StripIsShown);
        Assert.Empty(ModsMenu.LastStripRowsForTests);
        Assert.Empty(ModsMenu.DescribeStripRows(visible: false));
        // Revert: skipping Strip.Take on Close fails this.
    }

    [Fact]
    public void Switching_tabs_keeps_IsOpen_and_capture_and_does_not_call_Close()
    {
        ModsMenu.Open();
        Assert.True(ModsMenu.IsOpen);
        Assert.True(ModsMenu.StripCaptureHeldForTests);
        int closes = ModsMenu.CloseCallsForTests;
        int capture = _runtime.Capture;
        Assert.True(capture > 0);

        ModsMenu.SelectTab("install");
        Assert.True(ModsMenu.IsOpen);
        Assert.Equal(closes, ModsMenu.CloseCallsForTests);
        Assert.True(_runtime.Capture > 0);
        Assert.True(ModsMenu.StripCaptureHeldForTests);
        Assert.Equal(ModsTab.InstallMods, ModsMenu.SelectedTabForTests);

        ModsMenu.SelectTab("configuration");
        Assert.True(ModsMenu.IsOpen);
        Assert.Equal(closes, ModsMenu.CloseCallsForTests);
        Assert.True(ModsMenu.StripCaptureHeldForTests);

        ModsMenu.SelectTab("mods");
        Assert.True(ModsMenu.IsOpen);
        Assert.Equal(closes, ModsMenu.CloseCallsForTests);
        Assert.Equal(ModsTab.Mods, ModsMenu.SelectedTabForTests);

        string body = SourcePin.MethodBody(
            SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "ModsMenu.cs"),
            "internal static void SelectWorkspaceTab");
        SourcePin.AssertNoStatementContaining(body, "Close(");
        // Revert: SelectWorkspaceTab calling Close() increments CloseCallsForTests.
    }

    /// <summary>
    /// INSTALLUX2 item 4: a mod hot-installed on the Install tab is in the Mods list the next time
    /// the Mods tab is drawn, with no reopen of the workspace. Live symptom this fixes: HotDemo,
    /// hot-installed to Both, did not appear under Mods until the workspace was closed and opened
    /// again, because <c>NoteInventoryChanged</c> returns early while another tab is selected and
    /// nothing ever came back to rebuild the list.
    /// What removal breaks: the user installs a mod, switches to Mods to configure it, and it is
    /// simply not there - so the install reads as having failed when it did not.
    /// </summary>
    [Fact]
    public void A_hot_load_on_the_install_tab_refreshes_the_mods_list_on_the_next_tab_select()
    {
        using var alpha = ModsMenu.Register(new object(), "alpha.mod", "Alpha");
        ModsMenu.Open();
        ModsMenu.SelectTab("install");

        // The hot load completes while the Install tab is the selected one: this is the hook
        // InstallModsPane raises through its InventoryRefresh seam.
        using var hot = ModsMenu.Register(new object(), "example.hotdemo", "HotDemo");
        ModsMenu.NoteInventoryChanged(InstallModsPane.InventoryReasonHotLoaded);
        _log.Clear();

        ModsMenu.SelectTab("mods");

        Assert.Contains(_log.Infos, line =>
            line.StartsWith(ModsMenu.WorkspaceTransitionPrefix + "sidebar-rebuild ", StringComparison.Ordinal)
            && line.Contains("reason=" + ModsMenu.ReasonInventoryRefresh, StringComparison.Ordinal));
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(ModsMenu.DescribeWorkspace()));
        Assert.Contains(
            doc.RootElement.GetProperty("entries").EnumerateArray(),
            entry => string.Equals(entry.GetProperty("id").GetString(), "example.hotdemo", StringComparison.OrdinalIgnoreCase));

        // The debt is paid once: a second select with nothing new does not rebuild again.
        ModsMenu.SelectTab("install");
        _log.Clear();
        ModsMenu.SelectTab("mods");
        Assert.DoesNotContain(_log.Infos, line =>
            line.StartsWith(ModsMenu.WorkspaceTransitionPrefix + "sidebar-rebuild ", StringComparison.Ordinal)
            && line.Contains("reason=" + ModsMenu.ReasonInventoryRefresh, StringComparison.Ordinal));
    }

    /// <summary>
    /// INSTALLUX2 item 4, the second trigger: the install ledger moved (a pending install written,
    /// or an uninstall marker) while another tab was selected, so the Mods tab rebuilds on select.
    /// The stamp is read, never consumed - the Install tab's own <c>RecheckLedgers</c> clears the
    /// catalog service's copy of it, so a Mods tab that asked that call whether anything changed
    /// would be told "no" for a change the Install tab had already swallowed.
    /// What removal breaks: an install that only wrote a pending file (no hot load, so no
    /// inventory hook) never reaches the Mods list until the workspace is reopened - which is the
    /// same live symptom, for the restart-route half of the installs.
    /// </summary>
    [Fact]
    public void A_moved_install_ledger_refreshes_the_mods_list_on_the_next_tab_select()
    {
        string stamp = "ledger-1";
        ModsMenu.InstallLedgerStampProbe = () => stamp;
        using var alpha = ModsMenu.Register(new object(), "alpha.mod", "Alpha");
        ModsMenu.Open();
        ModsMenu.SelectTab("install");

        // Nothing moved: the select costs no rebuild.
        _log.Clear();
        ModsMenu.SelectTab("mods");
        Assert.DoesNotContain(_log.Infos, line =>
            line.StartsWith(ModsMenu.WorkspaceTransitionPrefix + "sidebar-rebuild ", StringComparison.Ordinal)
            && line.Contains("reason=" + ModsMenu.ReasonInventoryRefresh, StringComparison.Ordinal));

        ModsMenu.SelectTab("install");
        stamp = "ledger-2";
        _log.Clear();

        ModsMenu.SelectTab("mods");

        Assert.Contains(_log.Infos, line =>
            line.StartsWith(ModsMenu.WorkspaceTransitionPrefix + "sidebar-rebuild ", StringComparison.Ordinal)
            && line.Contains("reason=" + ModsMenu.ReasonInventoryRefresh, StringComparison.Ordinal));
    }

    [Fact]
    public void Install_tab_has_no_installed_mod_sidebar_and_footer_is_refresh_index_and_close()
    {
        using var menu = ModsMenu.Register(new object(), "alpha.mod", "Alpha");
        ModsMenu.Open();
        ModsMenu.SelectTab("install");
        // LIST2: the Install tab is one list-view pane; there is no sidebar pane at all.
        Assert.Empty(ModsMenu.ReadVisibleWidgets("list"));
        Assert.Null(ModsMenu.SidebarHeldForTests);
        var content = ModsMenu.ReadVisibleWidgets("content");
        string json = JsonSerializer.Serialize(content);
        Assert.DoesNotContain("Overview / refresh", json);
        Assert.DoesNotContain("alpha.mod", json);
        // LIST3 item 9: no Refresh index / Close Mods rows on the Install tab (the index refreshes
        // itself on open; Escape and the native tabs close the workspace).
        Assert.DoesNotContain(InstallModsCopy.RefreshIndex, json);
        Assert.DoesNotContain(InstallModsCopy.CloseMods, json);
        Assert.False(InstallModsPane.FooterHasOverviewRefresh);
        Assert.Equal(ModsSidebarKind.CatalogListings, ModsTabState.SidebarFor(ModsTab.InstallMods));
        string load = SourcePin.MethodBody(
            SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "ModsMenu.cs"),
            "private static void QueueInstallIndexLoad");
        SourcePin.AssertNoStatementContaining(load, ".Result");
        SourcePin.AssertUncommented(load, "BeginLoadIndex");
        SourcePin.AssertUncommented(load, "MainThread.Post");
        string docs = SourcePin.MethodBody(
            SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "ModsMenu.cs"),
            "private static void QueueListingDocuments");
        SourcePin.AssertNoStatementContaining(docs, ".Result");
        SourcePin.AssertUncommented(docs, "BeginLoadListingDocuments");
        SourcePin.AssertUncommented(docs, "MainThread.Post");
        SourcePin.AssertUncommented(docs, "ListingDocumentsSettled");
        string complete = SourcePin.MethodBody(
            SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "ModsMenu.cs"),
            "private static void CompleteListingDocuments");
        SourcePin.AssertUncommented(complete, "ApplyListingDocuments");
        SourcePin.AssertUncommented(complete, "BuildInstallDetail");
        string describe = SourcePin.MethodBody(
            SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "ModsMenu.cs"),
            "private static object DescribeWidget");
        SourcePin.AssertUncommented(describe, "ReadmeToWidget");
        SourcePin.AssertUncommented(describe, "cellRow.Describe()");
        string detail = SourcePin.MethodBody(
            SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "ModsMenu.cs"),
            "private static void BuildInstallDetail");
        SourcePin.AssertUncommented(detail, "SetInstalledQuery");
        string rects = SourcePin.MethodBody(
            SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "ModsMenu.cs"),
            "public static object[] DescribeListingHeaderRects");
        SourcePin.AssertUncommented(rects, "SplitPinnedHeader");
        // Revert: calling BuildSidebar() on Install puts Overview / refresh and alpha.mod in the list.
    }

    [Fact]
    public void Configuration_tab_has_no_mod_sidebar()
    {
        string dir = Path.Combine(Path.GetTempPath(), "progenitor-wire1-cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = new BepInEx.Configuration.ConfigFile(Path.Combine(dir, ConfigHelper.SharedFileName), saveOnInit: true);
        try
        {
            HarmonySlotHandoff.SetSkipModules(null, refusedLiveWorld: false);
            using var menu = ModsMenu.Register(new object(), "alpha.mod", "Alpha");
            ModsMenu.SetConfigFileForTests(file);
            ModsMenu.BindModsConfig(file);
            ModsMenu.Open();
            ModsMenu.SelectTab("configuration");
            var list = ModsMenu.ReadVisibleWidgets("list");
            string json = JsonSerializer.Serialize(list);
            Assert.DoesNotContain("Overview / refresh", json);
            Assert.DoesNotContain("alpha.mod", json);
            Assert.Contains(ConfigurationPane.SourcesGroup, json);
            Assert.Contains(InstallModsCopy.CloseMods, json);
            Assert.False(ModsTabState.IsModCatalogueSidebar(ModsTabState.SidebarFor(ModsTab.Preferences)));
            var content = JsonSerializer.Serialize(ModsMenu.ReadVisibleWidgets("content"));
            Assert.Contains(ConfigurationPane.PreferencesTitle, content);
            // Three homes: the purpose line's links replace the Progenitor settings button.
            Assert.DoesNotContain("Progenitor settings", content);
            Assert.Contains(SettingsLinks.PurposeKey, content);
            Assert.Contains("Use Thunderstore", content);
            Assert.DoesNotContain("[Mods] UseThunderstore", content);
            // Revert: BuildSidebar() on Configuration puts the installed-mod list back.
            // Caption pin: the kit renders the presentation label, not the action id.
        }
        finally
        {
            ConfigHelper.UninstallAll();
            try { Directory.Delete(dir, recursive: true); }
            catch { /* temp */ }
        }
    }

    [Fact]
    public void Per_tab_search_selection_and_scroll_round_trip()
    {
        using var alpha = ModsMenu.Register(new object(), "alpha.mod", "Alpha");
        using var zed = ModsMenu.Register(new object(), "zed.mod", "Zed");
        ModsMenu.Open();
        ModsMenu.ApplySearch("zed");
        ModsMenu.Select("zed.mod", "overview", null);
        ModsMenu.ListScrollForTests = 0.4f;

        ModsMenu.SelectTab("install");
        Assert.Equal(ModsTab.InstallMods, ModsMenu.SelectedTabForTests);
        Assert.True(ModsMenu.IsOpen);

        ModsMenu.SelectTab("mods");
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(ModsMenu.DescribeWorkspace()));
        Assert.Equal("mods", doc.RootElement.GetProperty("tab").GetString());
        Assert.Equal("zed", doc.RootElement.GetProperty("search").GetString());
        Assert.Equal("zed.mod", doc.RootElement.GetProperty("plugin").GetString());
        Assert.Equal(0.4f, ModsMenu.ListScrollForTests);
        Assert.Equal("zed", ModsMenu.TabStateForTests!.Mods.Search);
        Assert.Equal(ModsWorkspaceIdentity.Installed("zed.mod"), ModsMenu.TabStateForTests.Mods.Selection);
        // Revert: one shared _search/_selected/_listScroll without copying into ModsTabState.Mods fails this.
    }

    [Fact]
    public void Catalog_identity_is_never_passed_to_OpenMod()
    {
        ModsMenu.Open();
        ModsMenu.SelectTab("install");
        var select = Assert.Throws<ArgumentException>(() => ModsMenu.Select("dyno-ExampleCommandFramework", "overview", null));
        Assert.Contains("Mods tab", select.Message);
        string install = SourcePin.MethodBody(
            SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "ModsMenu.cs"),
            "private static void BuildInstallDetail");
        SourcePin.AssertNoStatementContaining(install, "OpenMod");
        // Revert: Select() on Install calling OpenMod, or BuildInstallDetail wiring OpenMod, fails this.
    }

    [Fact]
    public void Each_strip_button_selects_its_own_tab_and_never_calls_Close()
    {
        ModsMenu.Open();
        int closes = ModsMenu.CloseCallsForTests;
        Assert.True(_runtime.ClickIsStripButton(ModsTabStrip.ModsCaption));
        Assert.True(_runtime.FireClick(ModsTabStrip.ModsCaption));
        Assert.True(ModsMenu.IsOpen);
        Assert.Equal(ModsTab.Mods, ModsMenu.SelectedTabForTests);
        Assert.Equal(closes, ModsMenu.CloseCallsForTests);

        Assert.True(_runtime.ClickIsStripButton(ModsTabStrip.InstallModsCaption));
        Assert.True(_runtime.FireClick(ModsTabStrip.InstallModsCaption));
        Assert.True(ModsMenu.IsOpen);
        Assert.Equal(ModsTab.InstallMods, ModsMenu.SelectedTabForTests);
        Assert.Equal(closes, ModsMenu.CloseCallsForTests);

        Assert.True(_runtime.ClickIsStripButton(ModsTabStrip.PreferencesCaption));
        Assert.True(_runtime.FireClick(ModsTabStrip.PreferencesCaption));
        Assert.True(ModsMenu.IsOpen);
        Assert.Equal(ModsTab.Preferences, ModsMenu.SelectedTabForTests);
        Assert.Equal(closes, ModsMenu.CloseCallsForTests);

        var mods = ModsMenu.ClickVisibleWidget(null, ModsTabStrip.ModsCaption, null, null);
        using (var doc = JsonDocument.Parse(JsonSerializer.Serialize(mods)))
            Assert.Equal(ModsTabStrip.ModsCaption, doc.RootElement.GetProperty("label").GetString());
        Assert.Equal(ModsTab.Mods, ModsMenu.SelectedTabForTests);
        Assert.Equal(closes, ModsMenu.CloseCallsForTests);
        Assert.False(_runtime.ClickIsStripButton("Overview / refresh"));
        Assert.False(_runtime.ClickIsStripButton("Close Mods"));
        string strip = SourcePin.MethodBody(
            SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "ModsTabStrip.cs"),
            "public static void Build(IModsTabStripSurface surface");
        SourcePin.AssertUncommented(strip, "onSelect(Order[index])");
        SourcePin.AssertNoStatementContaining(strip, "Close(");
        string select = SourcePin.MethodBody(
            SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "ModsMenu.cs"),
            "internal static void SelectWorkspaceTab");
        SourcePin.AssertNoStatementContaining(select, "Close(");
        // Revert: wiring Mods→Close, a shifted OnClick, or resolving a strip click as a sidebar row fails this.
    }

    [Fact]
    public void Strip_ugui_listener_selects_order_i_with_a_hidden_title_row()
    {
        ModsMenu.Open();
        int closes = ModsMenu.CloseCallsForTests;
        Assert.True(_runtime.StripHasHiddenTitle);
        Assert.True(_runtime.InsertHiddenStripTitle());
        for (int i = 0; i < ModsTabStrip.Order.Length; i++)
        {
            Assert.True(_runtime.FireUguiStripListener(i));
            Assert.Equal(ModsTabStrip.Order[i], ModsMenu.SelectedTabForTests);
            Assert.True(ModsMenu.IsOpen);
            Assert.Equal(closes, ModsMenu.CloseCallsForTests);
        }

        Assert.Equal(-1, ModsTabStrip.ClickSlotAmongRows(false, 0));
        Assert.Equal(0, ModsTabStrip.ClickSlotAmongRows(true, 0));
        string add = SourcePin.MethodBody(
            SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "UnityUiRuntime.cs"),
            "public IRuntimeWidget? AddButton");
        SourcePin.AssertUncommented(add, "PrepareStripButtonCaption(");
        string place = SourcePin.MethodBody(
            SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "UnityUiRuntime.cs"),
            "private void PlaceStripRows");
        SourcePin.AssertUncommented(place, "PrepareStripButtonCaption(");
        // Revert: driving ClickAt, or binding onClick by all-rows index, fails this.
    }

    [Fact]
    public void Strip_click_at_live_rect_centre_selects_that_tab_not_the_next()
    {
        ModsMenu.Open();
        int closes = ModsMenu.CloseCallsForTests;
        // HOSTS1 round 13: tabs are sized to their captions (Spellbook's minimum, 2-unit gaps), so the
        // click points are each slot's own center on the strip the hit test derives from the live rects.
        var geometry = ModsTabStrip.GeometryFromLeftOrigin(1088f, 72f, 1664f, 40f);
        float[] cx = Enumerable.Range(0, 5).Select(k => MenuPanelLayout.StripButton(geometry, k, 5).X).ToArray();
        // Live too-wide row RTs: each button's ScreenRect filled the whole strip, so
        // last-match at the Mods center invoked Install. Union + HitIndex keeps the slot.
        AssignLiveStripRects(overlapping: true);
        Assert.True(_runtime.ClickAt(cx[0], 92f));
        Assert.Equal(ModsTab.Mods, ModsMenu.SelectedTabForTests);
        Assert.Equal(closes, ModsMenu.CloseCallsForTests);

        AssignLiveStripRects(overlapping: true);
        Assert.True(_runtime.ClickAt(cx[1], 92f));
        Assert.Equal(ModsTab.InstallMods, ModsMenu.SelectedTabForTests);
        Assert.Equal(closes, ModsMenu.CloseCallsForTests);

        AssignLiveStripRects(overlapping: true);
        Assert.True(_runtime.ClickAt(cx[2], 92f));
        Assert.Equal(ModsTab.Keybinds, ModsMenu.SelectedTabForTests);
        Assert.Equal(closes, ModsMenu.CloseCallsForTests);

        AssignLiveStripRects(overlapping: true);
        Assert.True(_runtime.ClickAt(cx[3], 92f));
        Assert.Equal(ModsTab.Preferences, ModsMenu.SelectedTabForTests);
        Assert.Equal(closes, ModsMenu.CloseCallsForTests);

        AssignLiveStripRects(overlapping: false);
        Assert.True(_runtime.ClickAt(cx[0], 92f));
        Assert.Equal(ModsTab.Mods, ModsMenu.SelectedTabForTests);
        AssignLiveStripRects(overlapping: false);
        Assert.True(_runtime.ClickAt(cx[1], 92f));
        Assert.Equal(ModsTab.InstallMods, ModsMenu.SelectedTabForTests);
        AssignLiveStripRects(overlapping: false);
        Assert.True(_runtime.ClickAt(cx[2], 92f));
        Assert.Equal(ModsTab.Keybinds, ModsMenu.SelectedTabForTests);
        AssignLiveStripRects(overlapping: false);
        Assert.True(_runtime.ClickAt(cx[3], 92f));
        Assert.Equal(ModsTab.Preferences, ModsMenu.SelectedTabForTests);
        AssignLiveStripRects(overlapping: false);
        Assert.True(_runtime.ClickAt(cx[4], 92f));
        Assert.Equal(ModsTab.Server, ModsMenu.SelectedTabForTests);
        Assert.Equal(closes, ModsMenu.CloseCallsForTests);

        string runtime = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "UnityUiRuntime.cs");
        string clickStrip = SourcePin.MethodBody(runtime, "private static bool ClickStripButtonAt");
        SourcePin.AssertUncommented(clickStrip, "TryStripHitIndex(");
        SourcePin.AssertNoStatementContaining(clickStrip, "ScreenRectOf(rt)");
        string focused = SourcePin.MethodBody(runtime, "private static TMP_InputField? FocusedArmedField");
        SourcePin.AssertUncommented(focused, "if (_ownedFocusField != null && FindArmed(_ownedFocusField) != null)");
        // Revert: ForEachRow last-match on overlapping ScreenRectOf, or ClickAt looking up by caption, fails this.
    }

    private void AssignLiveStripRects(bool overlapping)
    {
        float width = overlapping ? 1664f : 220f;
        float installX = overlapping ? 1088f : 1324f;
        float keybindsX = overlapping ? 1088f : 1560f;
        float preferencesX = overlapping ? 1088f : 1796f;
        float serverX = overlapping ? 1088f : 2032f;
        Assert.True(_runtime.SetHitRect("button", ModsTabStrip.ModsCaption, 1088f, 72f, width, 40f));
        Assert.True(_runtime.SetHitRect("button", ModsTabStrip.InstallModsCaption, installX, 72f, width, 40f));
        Assert.True(_runtime.SetHitRect("button", ModsTabStrip.ServerCaption, serverX, 72f, width, 40f));
        Assert.True(_runtime.SetHitRect("button", ModsTabStrip.KeybindsCaption, keybindsX, 72f, width, 40f));
        Assert.True(_runtime.SetHitRect("button", ModsTabStrip.PreferencesCaption, preferencesX, 72f, width, 40f));
    }

    [Fact]
    public void Install_search_lives_in_the_list_pane_and_typed_keys_land()
    {
        // LIST2: the search field is the first pinned row of the one list-view (content) pane;
        // the rows are cell rows, which the plain fake runtime cannot host, so this test runs on
        // LIST1's fake cell runtime and drives the field through the same text slot the game wires.
        var cells = new CellRowTests.FakeCellRuntime();
        UiApi.SetRuntimeForTests(cells);
        var catalog = new InstallSearchCatalog();
        catalog.Entries.Add(InstallListing("owner-blood", "Blood Craft"));
        catalog.Entries.Add(InstallListing("owner-other", "Other Pack"));
        ModsMenu.SetInstallCatalogForTests(catalog);
        ModsMenu.Open();
        ModsMenu.SelectTab("install");
        ModsMenu.DrainInstallIndexForTests();
        var panel = ModsMenu.ContentHeldForTests;
        Assert.NotNull(panel);
        Assert.Null(ModsMenu.SidebarHeldForTests);
        var pane = ModsMenu.InstallPaneForTests!;
        // LIST3: the search is the toolbar's input cell; its text slot is the cell's own.
        var toolbar = (UiCellRowHandle)pane.ToolbarRow!;
        ushort slot = toolbar.CellSlotId(InstallModsPane.CellSearch);
        Assert.NotEqual(0, slot);
        Assert.Equal(InstallModsCopy.SearchPlaceholder, pane.SearchCell!.Placeholder);
        Assert.Equal(CellKind.Input, pane.SearchCell.Kind);

        UiSlotTable.BootstrapDispatch(slot, (byte)UiSlotKind.Text, 0f, false, "abc");
        ModsMenu.TickInstallPaneForTests();
        Assert.Same(panel, ModsMenu.ContentHeldForTests);
        using (var doc = JsonDocument.Parse(JsonSerializer.Serialize(ModsMenu.DescribeWorkspace())))
            Assert.Equal("abc", doc.RootElement.GetProperty("search").GetString());
        Assert.Equal("abc", pane.Search);
        Assert.Equal("abc", pane.SearchCell!.Text);

        string runtime = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "UnityUiRuntime.cs");
        string focus = SourcePin.MethodBody(runtime, "private static bool FocusFieldAt");
        SourcePin.AssertUncommented(focus, "Consider(_armedFields[i].Field)");
        SourcePin.AssertUncommented(focus, "GetComponentInChildren<TMP_InputField>(true)");
        string tick = SourcePin.MethodBody(
            SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "ModsMenu.cs"),
            "private static void TickInstallPane");
        SourcePin.AssertNoStatementContaining(tick, "BuildInstallSidebar");
        string menu = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "ModsMenu.cs");
        SourcePin.AssertNoStatementContaining(SourcePin.MethodBody(menu, "private static void BuildInstallPanes"), "BuildInstallSidebar");
        SourcePin.AssertUncommented(SourcePin.MethodBody(menu, "private static void BuildInstallPanes"), "ParkSidebar(Sidebar.Take());");
        // Revert: a sidebar pane on the Install tab, or a search edit that rebuilds the pane, fails this.
    }

    [Fact]
    public void Install_search_types_through_the_list_pane_and_refilters_the_rows_in_place()
    {
        var cells = new CellRowTests.FakeCellRuntime();
        UiApi.SetRuntimeForTests(cells);
        var catalog = new InstallSearchCatalog();
        catalog.Entries.Add(InstallListing("owner-blood", "Blood Craft"));
        catalog.Entries.Add(InstallListing("owner-other", "Other Pack"));
        ModsMenu.SetInstallCatalogForTests(catalog);
        ModsMenu.Open();
        // HOSTS1 round 5: the Mods list is kept (parked, hidden) across the tab switch; its rows stay alive.
        var kept = cells.Rows.Where(r => r.Alive).ToList();
        ModsMenu.SelectTab("install");
        ModsMenu.DrainInstallIndexForTests();
        var panel = ModsMenu.ContentHeldForTests;
        Assert.NotNull(panel);
        Assert.True(ModsMenu.SidebarParkedForTests);
        int hosted = cells.Rows.Count(r => r.Alive && !kept.Contains(r));
        // Two listings and the pinned rows: toolbar, the two filter rows (INSTALLFILTERS1), header.
        Assert.Equal(2 + 4, hosted);

        ModsMenu.ApplySearch("blood");
        ModsMenu.TickInstallPaneForTests();
        Assert.Same(panel, ModsMenu.ContentHeldForTests);
        Assert.Equal(hosted, cells.Rows.Count(r => r.Alive && !kept.Contains(r)));
        using (var doc = JsonDocument.Parse(JsonSerializer.Serialize(ModsMenu.DescribeWorkspace())))
            Assert.Equal("blood", doc.RootElement.GetProperty("search").GetString());
        // The rows are cell rows in the content pane; a filtered-out row is hidden, not rebuilt,
        // and the readback carries the LIST1 shape plus the listing extras.
        using (var filtered = JsonDocument.Parse(JsonSerializer.Serialize(ModsMenu.ReadVisibleWidgets("content"))))
        {
            string json = filtered.RootElement.GetRawText();
            Assert.Contains("Blood Craft", json, StringComparison.Ordinal);
            Assert.DoesNotContain("Other Pack", json, StringComparison.Ordinal);
            Assert.Contains("\"cellrow\"", json, StringComparison.Ordinal);
            Assert.Contains("\"cellheader\"", json, StringComparison.Ordinal);
            Assert.Contains("\"listing\"", json, StringComparison.Ordinal);
            Assert.Contains("\"count\"", json, StringComparison.Ordinal);
            Assert.Contains("\"descriptions\"", json, StringComparison.Ordinal);
            Assert.Contains("\"loadsIn\"", json, StringComparison.Ordinal);
            Assert.Contains("1 listing filtered", json, StringComparison.Ordinal);
        }
        Assert.Empty(ModsMenu.ReadVisibleWidgets("list"));

        // INSTALLSIDES1: the row's expand button through the bridge opens the drawer beneath it (a
        // rebuild on the next Drain); the row itself opens nothing.
        // The bridge dispatches a cell row through the row registry (ModsUiOps.DoWidgetClick → UiCellRowHandle.TryInvoke).
        UiCellRowHandle.TryInvoke("owner-blood", out _);
        Assert.Null(ModsMenu.InstallPaneForTests!.SelectedKey);
        Assert.True(UiCellRowHandle.TryInvoke("owner-blood/" + InstallModsPane.CellExpand, out var clicked));
        Assert.Equal("owner-blood", ModsMenu.InstallPaneForTests!.SelectedKey);
        Progenitor.Dev.MainThread.Drain();
        using (var opened = JsonDocument.Parse(JsonSerializer.Serialize(ModsMenu.ReadVisibleWidgets("content"))))
        {
            string json = opened.RootElement.GetRawText();
            Assert.Contains("\"drawer\":\"owner-blood\"", json, StringComparison.Ordinal);
            Assert.Contains(InstallModsPane.DrawerKeyPrefix + "owner-blood", json, StringComparison.Ordinal);
            Assert.Contains("\"expanded\":true", json, StringComparison.Ordinal);
        }

        ModsMenu.ApplySearch("");
        Assert.True(ModsMenu.IsOpen);
        using (var cleared = JsonDocument.Parse(JsonSerializer.Serialize(ModsMenu.DescribeWorkspace())))
            Assert.Equal("", cleared.RootElement.GetProperty("search").GetString());
        using (var restored = JsonDocument.Parse(JsonSerializer.Serialize(ModsMenu.ReadVisibleWidgets("content"))))
        {
            string json = restored.RootElement.GetRawText();
            Assert.Contains("Blood Craft", json, StringComparison.Ordinal);
            Assert.Contains("Other Pack", json, StringComparison.Ordinal);
        }

        string tick = SourcePin.MethodBody(
            SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "ModsMenu.cs"),
            "private static void TickInstallPane");
        SourcePin.AssertNoStatementContaining(tick, "BuildInstallSidebar");
        string apply = SourcePin.MethodBody(
            SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "ModsMenu.cs"),
            "public static void ApplySearch");
        SourcePin.AssertNoStatementContaining(apply, "BuildInstallDetail");
        string pane = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "InstallModsPane.cs");
        SourcePin.AssertUncommented(pane, "RefilterListings()");
        // LIST3: the search is the toolbar's input cell; its per-keystroke handler is the cell's OnChanged.
        SourcePin.AssertUncommented(pane, "_toolbar.OnChanged(CellSearch");
        SourcePin.AssertNoStatementContaining(SourcePin.MethodBody(pane, "public void Build("), "panel.Input(");
        // Revert: a search edit rebuilding the pane, or status.search ignoring the Install field, fails this.
    }

    [Fact]
    public void Widget_read_rows_carry_the_tmp_snapshot_and_status_carries_fontDonor()
    {
        var tmp = TmpReadback.LabelDefaults(UiFontDonor.BodyFontName);
        ModsMenu.TmpProbeForTests = tmp;
        try
        {
            UiFontLookup.RememberDonor(
                UiFontDonor.BodyFontName, "", null, UiFontDonor.SourceAssetList);
            ModsMenu.Open();
            var widgets = ModsMenu.ReadVisibleWidgets(null);
            Assert.NotEmpty(widgets);
            string json = JsonSerializer.Serialize(widgets);
            Assert.Contains("\"tmp\":", json);
            Assert.Contains("\"characterSpacing\":0", json);
            Assert.Contains("\"font\":\"Nocturne Serif Regular SDF\"", json);
            using var row = JsonDocument.Parse(JsonSerializer.Serialize(widgets[0]));
            Assert.True(row.RootElement.TryGetProperty("tmp", out var tmpEl));
            Assert.Equal(0, tmpEl.GetProperty("characterSpacing").GetDouble());
            Assert.Equal(0, tmpEl.GetProperty("fontStyle").GetInt32());
            Assert.False(tmpEl.GetProperty("enableAutoSizing").GetBoolean());
            Assert.Equal(1, tmpEl.GetProperty("lossyScale").GetProperty("x").GetDouble());

            using var status = JsonDocument.Parse(JsonSerializer.Serialize(ModsMenu.DescribeWorkspace()));
            Assert.Equal(UiFontDonor.BodyFontName, status.RootElement.GetProperty("fontDonor").GetProperty("font").GetString());
            Assert.Equal("", status.RootElement.GetProperty("fontDonor").GetProperty("path").GetString());
            Assert.Equal(UiFontDonor.SourceAssetList, status.RootElement.GetProperty("fontDonor").GetProperty("source").GetString());
        }
        finally
        {
            ModsMenu.TmpProbeForTests = null;
            UiFontLookup.Invalidate();
        }
        // Revert: dropping tmp from DescribeWidget, or fontDonor from DescribeWorkspace, fails this.
    }

    private static CatalogEntry InstallListing(string key, string display) =>
        new(
            new ManifestMod("", "1.0.0", NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.None, false, "n", key),
            CatalogProvenance.AdaptedListing,
            InstallReasons.SourceThunderstore,
            InstallReasons.SourceLabelThunderstore,
            "2026-09-11T00:00:00Z",
            displayName: display,
            description: "",
            packageName: key);

    private sealed class InstallSearchCatalog : IInstallCatalog
    {
        public List<CatalogEntry> Entries { get; } = new();

        public InstallProgress Status => InstallProgress.Idle;

        public Task<CatalogLoadResult> LoadIndexAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new CatalogLoadResult(true, null, Entries.Count, "stamp"));

        public IReadOnlyList<CatalogEntry> Search(string? query)
        {
            if (string.IsNullOrWhiteSpace(query)) return Entries;
            var hits = new List<CatalogEntry>();
            for (int i = 0; i < Entries.Count; i++)
                if (InstallModsPane.MatchesSearch(Entries[i], query)) hits.Add(Entries[i]);
            return hits;
        }

        public bool TryGetEntry(string key, out CatalogEntry? entry)
        {
            for (int i = 0; i < Entries.Count; i++)
            {
                if (!string.Equals(Entries[i].Key, key, StringComparison.OrdinalIgnoreCase)) continue;
                entry = Entries[i];
                return true;
            }

            entry = null;
            return false;
        }

        public Task<InspectCallResult> DownloadAndInspectAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(new InspectCallResult(false, InstallReasons.FetchNoUrl, null, null));

        public PlanCallResult Plan(string key, InstallTarget target) =>
            new(false, InstallReasons.NotInspected, null);

        public ApproveCallResult Approve(string planHash) =>
            new(false, InstallReasons.ApprovalMissing, null, false);

        public ExecuteCallResult ExecuteToPending(string planHash) =>
            new(false, InstallReasons.ApprovalMissing, null, false);

        public Task<ListingDocumentSet> LoadListingDocumentsAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(ListingDocumentSet.Unavailable(key ?? "", InstallReasons.UnknownEntry));
    }
}
