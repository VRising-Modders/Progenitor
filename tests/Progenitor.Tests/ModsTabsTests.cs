using System;
using System.Collections.Generic;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Game-free coverage of the three-tab Mods workspace model and strip. Removing a guard
/// named in a test comment must fail that test.
/// </summary>
public sealed class ModsTabsTests
{
    [Fact]
    public void Switch_keeps_workspace_open_and_does_not_request_close()
    {
        var state = new ModsTabState(workspaceOpen: true, inputCaptureHeld: true);
        var result = state.SelectTab(ModsTab.InstallMods);
        Assert.Equal(ModsTab.InstallMods, state.Selected);
        Assert.True(result.WorkspaceStillOpen);
        Assert.True(result.InputCaptureHeld);
        Assert.False(result.RequestedClose);
        Assert.True(state.WorkspaceOpen);
        Assert.True(state.InputCaptureHeld);
        // Revert: SelectTab calling Close or flipping WorkspaceOpen/RequestedClose fails this.
    }

    [Fact]
    public void Switch_to_each_tab_retains_per_tab_search_selection_and_scroll()
    {
        var state = new ModsTabState();
        state.Mods.Search = "wolf";
        state.Mods.Selection = ModsWorkspaceIdentity.Installed("progenitor.sample");
        state.Mods.Scroll = 0.4f;
        state.InstallMods.Search = "blood";
        state.InstallMods.Selection = ModsWorkspaceIdentity.Catalog("dyno-ExampleCommandFramework");
        state.InstallMods.Scroll = 0.7f;
        state.Configuration.Search = "sources";
        state.Configuration.Selection = ModsWorkspaceIdentity.Config(ConfigurationPane.SourcesGroup);
        state.Configuration.Scroll = 1f;

        state.SelectTab(ModsTab.InstallMods);
        state.SelectTab(ModsTab.Preferences);
        state.SelectTab(ModsTab.Mods);

        Assert.Equal("wolf", state.Mods.Search);
        Assert.Equal(ModsWorkspaceIdentity.Installed("progenitor.sample"), state.Mods.Selection);
        Assert.Equal(0.4f, state.Mods.Scroll);
        Assert.Equal("blood", state.InstallMods.Search);
        Assert.Equal(ModsWorkspaceIdentity.Catalog("dyno-ExampleCommandFramework"), state.InstallMods.Selection);
        Assert.Equal(0.7f, state.InstallMods.Scroll);
        Assert.Equal("sources", state.Configuration.Search);
        // Revert: one shared search/selection field across tabs fails the retained Install values.
    }

    [Fact]
    public void Sidebars_are_separate_models_and_configuration_is_not_a_mod_catalogue()
    {
        Assert.Equal(ModsSidebarKind.InstalledMods, ModsTabState.SidebarFor(ModsTab.Mods));
        Assert.Equal(ModsSidebarKind.CatalogListings, ModsTabState.SidebarFor(ModsTab.InstallMods));
        Assert.Equal(ModsSidebarKind.ConfigGroups, ModsTabState.SidebarFor(ModsTab.Preferences));
        Assert.True(ModsTabState.IsModCatalogueSidebar(ModsSidebarKind.InstalledMods));
        Assert.True(ModsTabState.IsModCatalogueSidebar(ModsSidebarKind.CatalogListings));
        Assert.False(ModsTabState.IsModCatalogueSidebar(ModsSidebarKind.ConfigGroups));
        Assert.False(ModsTabState.IsModCatalogueSidebar(ModsSidebarKind.None));
        // Revert: treating config groups as a mod catalog makes IsModCatalogueSidebar true.
    }

    [Fact]
    public void Catalog_identity_never_equals_installed_plugin_identity_and_cannot_open_as_a_plugin()
    {
        const string same = "progenitor.sample";
        var catalog = ModsWorkspaceIdentity.Catalog(same);
        var installed = ModsWorkspaceIdentity.Installed(same);
        Assert.NotEqual(catalog, installed);
        Assert.False(catalog.CanOpenAsLoadedPlugin);
        Assert.True(installed.CanOpenAsLoadedPlugin);
        Assert.False(ModsTabState.CatalogRowOpensAsLoadedPlugin(catalog));
        var state = new ModsTabState();
        state.SelectTab(ModsTab.InstallMods);
        state.InstallMods.Selection = catalog;
        Assert.False(state.InstallMods.Selection!.Value.CanOpenAsLoadedPlugin);
        // Revert: equality that ignores Kind, or CanOpenAsLoadedPlugin true for Catalog, fails this.
    }

    [Fact]
    public void The_workspace_has_five_tabs_in_the_three_homes_order()
    {
        // Three homes (decided 2026-10-06): Mods · Install Mods · Keybinds · Preferences · Server.
        Assert.Equal(5, ModsTabStrip.Order.Length);
        Assert.Equal(new[] { ModsTab.Mods, ModsTab.InstallMods, ModsTab.Keybinds, ModsTab.Preferences, ModsTab.Server }, ModsTabStrip.Order);
        Assert.Equal(ModsSidebarKind.ConfigGroups, ModsTabState.SidebarFor(ModsTab.Server));
        Assert.NotSame(new ModsTabState().Page(ModsTab.Server), new ModsTabState().Page(ModsTab.Mods));
        Assert.DoesNotContain("consent", Array.ConvertAll(ModsTabStrip.Order, t => ModsTabState.StatusName(t)));
    }

    [Fact]
    public void Escape_clears_a_focused_search_then_requests_the_existing_close_path()
    {
        var state = new ModsTabState();
        state.Mods.Search = "abc";
        Assert.Equal(ModsEscapeAction.ClearSearch, state.OnEscape(searchFocused: true, "abc"));
        Assert.Equal("", state.Mods.Search);
        // SEARCHCLEAR1: a focused empty box is left first; only an unfocused box closes.
        Assert.Equal(ModsEscapeAction.LeaveSearch, state.OnEscape(searchFocused: true, ""));
        Assert.Equal(ModsEscapeAction.CloseWorkspace, state.OnEscape(searchFocused: false, ""));
        Assert.Equal(ModsEscapeAction.CloseWorkspace, state.OnEscape(searchFocused: false, "abc"));
        Assert.True(state.WorkspaceOpen);
        // Revert: skipping the clear stage or closing inside OnEscape (WorkspaceOpen false) fails this.
    }

    [Fact]
    public void Tab_model_never_calls_ModsMenu_Close()
    {
        string tabs = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "ModsTabs.cs");
        string strip = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "ModsTabStrip.cs");
        SourcePin.AssertNoStatementContaining(tabs, "ModsMenu.Close");
        SourcePin.AssertNoStatementContaining(strip, "ModsMenu.Close");
        // Revert: adding ModsMenu.Close() to the tab model or strip fails this pin.
    }

    [Fact]
    public void Strip_uses_catalogue_gold_for_the_selected_tab_and_invokes_the_callback_without_closing()
    {
        var surface = new RecordingStrip();
        bool closed = false;
        ModsTab chosen = ModsTab.Mods;
        ModsTabStrip.Build(surface, ModsTab.InstallMods, tab =>
        {
            if (closed) throw new InvalidOperationException("close");
            chosen = tab;
        });

        Assert.Equal(5, surface.Rows.Count);
        Assert.Equal(ModsTabStrip.KeybindsCaption, surface.Rows[2].Text);
        Assert.Equal(ModsTabStrip.PreferencesCaption, surface.Rows[3].Text);
        Assert.Equal(ModsTabStrip.ServerCaption, surface.Rows[4].Text);
        Assert.Equal(MenuRowTone.Catalog, surface.Rows[4].Tone);
        Assert.Equal(MenuRowTone.Catalog, surface.Rows[0].Tone);
        Assert.Equal(ModsTabStrip.ModsCaption, surface.Rows[0].Text);
        Assert.Equal(MenuRowTone.Selected, surface.Rows[1].Tone);
        Assert.Equal(ModsTabStrip.InstallModsCaption, surface.Rows[1].Text);
        Assert.Equal(MenuRowTone.Catalog, surface.Rows[2].Tone);
        Assert.Equal(MenuRowTone.Selected, ModsTabStrip.ToneFor(ModsTab.Mods, ModsTab.Mods));
        Assert.NotEqual(MenuRowTone.Chrome, ModsTabStrip.ToneFor(ModsTab.Mods, ModsTab.Mods));

        surface.Buttons[0].Click!();
        Assert.Equal(ModsTab.Mods, chosen);
        Assert.False(closed);
        surface.Buttons[1].Click!();
        Assert.Equal(ModsTab.InstallMods, chosen);
        Assert.False(closed);
        surface.Buttons[2].Click!();
        Assert.Equal(ModsTab.Keybinds, chosen);
        surface.Buttons[3].Click!();
        Assert.Equal(ModsTab.Preferences, chosen);
        surface.Buttons[4].Click!();
        Assert.Equal(ModsTab.Server, chosen);
        Assert.False(closed);
        // Revert: chrome/selected swap, a shifted OnClick index, or onSelect calling Close, fails this.
    }

    [Fact]
    public void Strip_hit_selects_that_button_and_is_never_a_sidebar_row()
    {
        var frame = ModsWorkspaceFrame.Fallback(3840f, 1080f);
        var strip = MenuPanelLayout.ForStrip(frame);
        var sidebar = MenuPanelLayout.ForFrame(frame, sidebar: true);
        for (int i = 0; i < ModsTabStrip.Order.Length; i++)
        {
            var button = MenuPanelLayout.StripButton(strip, i, ModsTabStrip.Order.Length);
            int hit = ModsTabStrip.HitIndex(strip, button.X, button.Y);
            Assert.Equal(i, hit);
            Assert.Equal(ModsTabStrip.Order[i], ModsTabStrip.Order[hit]);
            Assert.False(ModsTabStrip.MapsToSidebarRow(hit));
        }

        Assert.Equal(-1, ModsTabStrip.HitIndex(strip, strip.X, sidebar.Y));
        Assert.True(ModsTabStrip.MapsToSidebarRow(-1));
        string runtime = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "UnityUiRuntime.cs");
        SourcePin.AssertUncommented(runtime, "ClickStripButtonAt(");
        SourcePin.AssertUncommented(runtime, "if (panel == null || !panel.Alive || !panel.Visible || !panel.IsStrip) continue;");
        string clickStrip = SourcePin.MethodBody(runtime, "private static bool ClickStripButtonAt");
        SourcePin.AssertUncommented(clickStrip, "TryStripHitIndex(");
        string hitIndex = SourcePin.MethodBody(runtime, "private static bool TryStripHitIndex");
        SourcePin.AssertUncommented(hitIndex, "ModsTabStrip.HitIndex(");
        SourcePin.AssertUncommented(hitIndex, "ModsTabStrip.GeometryFromLeftOrigin(");
        string scroll = SourcePin.MethodBody(runtime, "internal static ScrollRect? FindShownScroll");
        SourcePin.AssertUncommented(scroll, "!panel.IsStrip");
        // Revert: mapping a strip hit onto sidebar row indices, or skipping the strip for clicks, fails this.
    }

    [Fact]
    public void Compact_strip_centres_hit_their_slot_and_unused_space_does_not()
    {
        // Client-generation-14 measured left-origin strip rows. Centers are x+w/2, y+h/2.
        var strip = ModsTabStrip.GeometryFromLeftOrigin(1088f, 72f, 1664f, 40f);
        // HOSTS1 round 13: caption-sized tabs; each slot's own center hits that slot.
        for (int k = 0; k < 3; k++)
            Assert.Equal(k, ModsTabStrip.HitIndex(strip, MenuPanelLayout.StripButton(strip, k, ModsTabStrip.Order.Length).X, 92f));
        Assert.Equal(ModsTab.Mods, ModsTabStrip.Order[0]);
        Assert.Equal(ModsTab.InstallMods, ModsTabStrip.Order[1]);
        Assert.Equal(ModsTab.Keybinds, ModsTabStrip.Order[2]);
        // Revert: treating left-origin x as center, or last-matching overlapping rows, maps 1360 to Install.
    }

    [Fact]
    public void Ugui_onclick_slot_ignores_a_hidden_title_row()
    {
        // Live: strip panel still has Title.SetActive(false). Binding onClick by
        // row index among all rows made button i fire Order[i+1].
        Assert.Equal(-1, ModsTabStrip.ClickSlotAmongRows(rowIsButton: false, buttonRowsBeforeThis: 0));
        Assert.Equal(0, ModsTabStrip.ClickSlotAmongRows(rowIsButton: true, buttonRowsBeforeThis: 0));
        Assert.Equal(1, ModsTabStrip.ClickSlotAmongRows(rowIsButton: true, buttonRowsBeforeThis: 1));
        Assert.Equal(2, ModsTabStrip.ClickSlotAmongRows(rowIsButton: true, buttonRowsBeforeThis: 2));
        Assert.Equal(ModsTab.Mods, ModsTabStrip.Order[ModsTabStrip.ClickSlotAmongRows(true, 0)]);
        Assert.Equal(ModsTab.InstallMods, ModsTabStrip.Order[ModsTabStrip.ClickSlotAmongRows(true, 1)]);
        Assert.Equal(ModsTab.Keybinds, ModsTabStrip.Order[ModsTabStrip.ClickSlotAmongRows(true, 2)]);
        Assert.Equal(ModsTab.Server, ModsTabStrip.Order[ModsTabStrip.ClickSlotAmongRows(true, 4)]);
        string build = SourcePin.MethodBody(
            SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "ModsTabStrip.cs"),
            "public static void Build(IModsTabStripSurface surface");
        SourcePin.AssertUncommented(build, "ClickSlotAmongRows(");
        SourcePin.AssertUncommented(build, "onSelect(Order[index])");
        string runtime = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "UnityUiRuntime.cs");
        SourcePin.AssertUncommented(runtime, "PrepareStripButtonCaption(");
        string prepare = SourcePin.MethodBody(runtime, "private static void PrepareStripButtonCaption");
        SourcePin.AssertUncommented(prepare, "tmp.raycastTarget = false");
        SourcePin.AssertUncommented(prepare, "Stretch(");
        // Revert: binding by all-rows index, or leaving the caption as a full-strip raycast, is +1.
    }

    [Fact]
    public void Status_names_round_trip_for_mods_ui_select_tab()
    {
        Assert.Equal("mods", ModsTabState.StatusName(ModsTab.Mods));
        Assert.Equal("install", ModsTabState.StatusName(ModsTab.InstallMods));
        Assert.Equal("preferences", ModsTabState.StatusName(ModsTab.Preferences));
        Assert.Equal("server", ModsTabState.StatusName(ModsTab.Server));
        // Three homes: the old name stays an alias.
        Assert.True(ModsTabState.TryParseStatusName("configuration", out var alias));
        Assert.Equal(ModsTab.Preferences, alias);
        Assert.True(ModsTabState.TryParseStatusName("server", out var server));
        Assert.Equal(ModsTab.Server, server);
        Assert.True(ModsTabState.TryParseStatusName("install-mods", out var tab));
        Assert.Equal(ModsTab.InstallMods, tab);
        Assert.False(ModsTabState.TryParseStatusName("consent", out _));
        // Revert: mapping consent as a tab name makes TryParse succeed.
    }

    private sealed class RecordingStrip : IModsTabStripSurface
    {
        private MenuRowTone _next;
        public List<(MenuRowTone Tone, string Text)> Rows { get; } = new();
        public List<RecordingButton> Buttons { get; } = new();

        public void StyleNext(MenuRowTone tone) => _next = tone;

        public IUiButton Button(string text)
        {
            var button = new RecordingButton { Text = text };
            Rows.Add((_next, text));
            Buttons.Add(button);
            return button;
        }
    }

    private sealed class RecordingButton : IUiButton
    {
        public string Text { get; set; } = "";
        public bool Visible { get; set; } = true;
        public bool Enabled => true;
        public bool IsDisposed { get; private set; }
        public UiHandleState State => UiHandleState.Built;
        public string PluginId => "test";
        public string RootName => "test";
        public Action? Click { get; private set; }

        public IDisposable OnClick(Action handler)
        {
            Click = handler;
            return this;
        }

        public void Dispose() => IsDisposed = true;
    }
}
