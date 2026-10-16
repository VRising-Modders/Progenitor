using System;
using System.Collections.Generic;
using System.Linq;
using Progenitor.Ui;
using Xunit;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.Tests;

/// <summary>
/// HUB1: the hub controls through a fake runtime that records what the kit asked of it: the game window tabs, the kept
/// tab height, the tab history and its back/forward precedence, compact dropdown rows, a field's action button, centered
/// button rows, key hints, a table that fills its panel, and castle building icons; plus their game-free layout math.
/// Nothing here has run in the game.
/// </summary>
[Collection("UiStatic")]
public sealed class UiHubControlsTests : IDisposable
{
    private readonly HubRuntimeFake _runtime = new();
    private readonly object _owner = new();
    private readonly List<IUiPanel> _panels = new();

    public UiHubControlsTests()
    {
        UiApi.ResetForTests();
        UiApi.SetRuntimeForTests(_runtime);
    }

    public void Dispose()
    {
        foreach (var p in _panels) p.Dispose();
        UiApi.ResetForTests();
        // A data table's wheel claim registers a process-wide teardown; leave none behind for the next suite.
        UiWidgetLifetime.Uninstall();
    }

    private IUiPanel NewPanel(bool gameWindow = false)
    {
        var panel = UiApi.Panel("Hub", _owner, "example.hub");
        _panels.Add(panel);
        if (gameWindow) panel.GameWindow();
        return panel;
    }

    private CellRowTests.FakeCellRow Row(string key) => _runtime.Rows.Single(r => r.Text == key);

    private static void Click(CellRowTests.FakeCellRow row, string cell) =>
        UiSlotTable.BootstrapDispatch(row.WiredIdOf(cell), (byte)UiSlotKind.Click, 0f, false, null);

    // ---- game window tabs ----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(UiTabsLook.Auto, true, true)]
    [InlineData(UiTabsLook.Auto, false, false)]
    [InlineData(UiTabsLook.Game, false, true)]
    [InlineData(UiTabsLook.Chips, true, false)]
    public void The_look_is_the_game_tab_on_a_game_window_unless_asked(UiTabsLook look, bool gameWindow, bool game) =>
        Assert.Equal(game, UiTabsLayout.UsesGameTabs(look, gameWindow));

    [Fact]
    public void Tabs_on_a_game_window_are_the_games_window_tabs_and_chips_elsewhere()
    {
        var plain = NewPanel().Tabs("plain", new[] { "Find", "Chests" });
        var game = NewPanel(gameWindow: true).Tabs("game", new[] { "Find", "Chests" });
        Assert.Equal("chips", plain.Describe()["look"]);
        Assert.Equal("game", game.Describe()["look"]);
        var chip = Row("plain:tabs").Cells().Single(c => c.Key == "tab0");
        var tab = Row("game:tabs").Cells().Single(c => c.Key == "tab0");
        Assert.Equal(CellSkin.Chip, chip.Skin);
        Assert.Equal(CellSkin.GameTab, tab.Skin);
        Assert.Equal(UiTabsLayout.GameTabFontSize, tab.FontSize);
        Assert.Equal(CellAlign.Center, tab.Align);
        Assert.Equal(UnityUiRuntimeMetrics.RowHeight, tab.HeightOverride);
        // The selected game tab keeps the selected tone (the clone's own selected state), the other the rest tone.
        Assert.Equal(CellTone.ChipOn, tab.Tone);
        Assert.Equal(CellTone.ChipOff, Row("game:tabs").Cells().Single(c => c.Key == "tab1").Tone);
    }

    [Fact]
    public void A_game_tab_is_as_wide_as_its_caption_and_grows_by_its_badge()
    {
        Assert.Equal(UiTabsLayout.GameTabMinWidth, UiTabsLayout.GameTabWidth("Log", "", false));
        float wide = UiTabsLayout.GameTabWidth("Equipment and crafting", "", false);
        Assert.True(wide > UiTabsLayout.GameTabMinWidth);
        Assert.True(wide >= UiChipsText.Width("Equipment and crafting", UiTabsLayout.GameTabFontSize) + UiTabsLayout.GameTabPad);
        Assert.True(UiTabsLayout.GameTabWidth("Equipment and crafting", "12", false) > wide);

        var panel = NewPanel(gameWindow: true);
        var tabs = panel.Tabs("t", new[] { "Equipment and crafting" });
        float before = Row("t:tabs").Cells().Single(c => c.Key == "tab0").MinWidth;
        tabs.SetBadge(0, 12);
        float after = Row("t:tabs").Cells().Single(c => c.Key == "tab0").MinWidth;
        Assert.True(after > before);
    }

    [Fact]
    public void The_standard_windows_tabs_are_game_tabs_too()
    {
        var window = UiApi.Window(UiWindowPreset.Header, "Iron Chest", _owner, "example.hub", new UiWindowOptions { Tabs = new[] { "Contents", "Rules" } });
        try { Assert.Equal("game", window.Tabs!.Describe()["look"]); }
        finally { window.Dispose(); }
    }

    // ---- kept height ---------------------------------------------------------------------------------------------------

    [Fact]
    public void A_window_with_tabs_keeps_its_height_by_default_and_may_ask_a_least_height()
    {
        var p1 = NewPanel(gameWindow: true);
        p1.Tabs("a", new[] { "One", "Two" });
        Assert.Contains(_runtime.HeightRules, r => r.Keep && r.Min == 0f);

        _runtime.HeightRules.Clear();
        var p2 = NewPanel(gameWindow: true);
        var tabs = p2.Tabs("b", new[] { "One", "Two" }, new UiTabsOptions { KeepHeight = false, MinContentHeight = 420f });
        Assert.Equal((false, 420f), _runtime.HeightRules.Single());
        Assert.Equal(420f, tabs.Describe()["minContentHeight"]);

        _runtime.HeightRules.Clear();
        NewPanel().Tabs("c", new[] { "One" }, new UiTabsOptions { KeepHeight = false });
        Assert.Empty(_runtime.HeightRules);
    }

    [Theory]
    [InlineData(-5f, 0f)]
    [InlineData(float.NaN, 0f)]
    [InlineData(300f, 300f)]
    [InlineData(99999f, UiTabsLayout.MaxMinHeight)]
    public void A_least_height_is_clamped(float asked, float used) => Assert.Equal(used, UiTabsLayout.MinHeight(asked));

    [Fact]
    public void The_kept_height_never_shrinks_while_open_and_stays_under_the_cap()
    {
        float h = UiWindowLayout.StableHeight(0f, 600f, 900f);
        h = UiWindowLayout.StableHeight(h, 300f, 900f);
        Assert.Equal(600f, h);
        h = UiWindowLayout.StableHeight(h, 1200f, 900f);
        Assert.Equal(900f, h);
    }

    // ---- tab history ---------------------------------------------------------------------------------------------------

    [Fact]
    public void History_pushes_walks_back_and_forward_and_drops_the_forward_part()
    {
        var h = new UiTabHistory(0);
        h.Push(1);
        h.Push(2);
        Assert.Equal(new[] { 0, 1, 2 }, h.Entries);
        Assert.Equal(1, h.Back());
        Assert.Equal(0, h.Back());
        Assert.Equal(-1, h.Back());
        Assert.Equal(1, h.Forward());
        h.Push(3); // a new selection after going back drops the forward part (2)
        Assert.Equal(new[] { 0, 1, 3 }, h.Entries);
        Assert.Equal(-1, h.Forward());
        h.Push(3); // the tab it is on: nothing pushed
        Assert.Equal(3, h.Entries.Count);
        for (int i = 0; i < UiTabHistory.MaxEntries + 10; i++) h.Push(i % 2 == 0 ? 4 : 5);
        Assert.Equal(UiTabHistory.MaxEntries, h.Entries.Count);
    }

    [Fact]
    public void A_tab_row_records_clicks_and_back_and_forward_raise_OnChanged_as_a_click()
    {
        var panel = NewPanel(gameWindow: true);
        var tabs = panel.Tabs("t", new[] { "Find", "Chests", "About" });
        var changes = new List<int>();
        tabs.OnChanged(changes.Add);
        Click(Row("t:tabs"), "tab1");
        Click(Row("t:tabs"), "tab2");
        Assert.True(tabs.Back());
        Assert.Equal(1, tabs.Selected);
        Assert.True(tabs.Back());
        Assert.Equal(0, tabs.Selected);
        Assert.False(tabs.Back());
        Assert.True(tabs.Forward());
        Assert.Equal(1, tabs.Selected);
        Assert.Equal(new[] { 1, 2, 1, 0, 1 }, changes);
        // Clicking after going back drops the forward part: About is gone from it.
        Click(Row("t:tabs"), "tab0");
        Assert.False(tabs.Forward());
        Assert.Equal(new[] { 0, 1, 0 }, ((UiTabsHandle)tabs).History!.Entries);
    }

    [Fact]
    public void History_off_keeps_no_history()
    {
        var tabs = NewPanel().Tabs("t", new[] { "A", "B" }, new UiTabsOptions { History = false });
        Click(Row("t:tabs"), "tab1");
        Assert.False(tabs.Back());
        Assert.Equal(1, tabs.Selected);
    }

    [Fact]
    public void The_window_under_the_pointer_wins_then_the_one_holding_the_input_then_none()
    {
        Assert.Equal(-1, UiTabHistoryRoute.Pick(Array.Empty<UiTabHistoryRoute.Candidate>()));
        var candidates = new[]
        {
            new UiTabHistoryRoute.Candidate(0, 5, underPointer: false, holdsInput: true),
            new UiTabHistoryRoute.Candidate(1, 3, underPointer: true, holdsInput: false),
            new UiTabHistoryRoute.Candidate(2, 9, underPointer: false, holdsInput: true),
            new UiTabHistoryRoute.Candidate(3, 4, underPointer: true, holdsInput: true),
        };
        Assert.Equal(3, UiTabHistoryRoute.Pick(candidates)); // the newest under the pointer
        Assert.Equal(2, UiTabHistoryRoute.Pick(candidates.Where(c => !c.UnderPointer).ToArray())); // the newest holding the input
        Assert.Equal(-1, UiTabHistoryRoute.Pick(new[] { new UiTabHistoryRoute.Candidate(0, 1, false, false) }));
    }

    [Fact]
    public void Back_and_forward_go_to_the_shown_window_under_the_pointer_and_not_to_a_hidden_one()
    {
        var front = NewPanel(gameWindow: true);
        var back = NewPanel(gameWindow: true);
        var frontTabs = front.Tabs("front", new[] { "A", "B" });
        var backTabs = back.Tabs("back", new[] { "A", "B" });
        Click(Row("front:tabs"), "tab1");
        Click(Row("back:tabs"), "tab1");
        UiTabHistoryRoute.UnderPointer = p => ReferenceEquals(p, front);

        // Nothing shown: nothing takes the press (the workspace would).
        Assert.False(UiTabHistoryRoute.TryNavigate(back: true));
        front.Show();
        back.Show();
        Assert.True(UiTabHistoryRoute.TryNavigate(back: true));
        Assert.Equal(0, frontTabs.Selected);
        Assert.Equal(1, backTabs.Selected);
        Assert.True(UiTabHistoryRoute.TryNavigate(back: false));
        Assert.Equal(1, frontTabs.Selected);

        // With the pointer over neither, a window that holds the input while shown takes it.
        UiTabHistoryRoute.UnderPointer = _ => false;
        back.CaptureMode = UiCaptureMode.WhileShown;
        Assert.Same(backTabs, UiTabHistoryRoute.Target());
        back.CaptureMode = UiCaptureMode.WhenHovered;
        Assert.Null(UiTabHistoryRoute.Target());
    }

    [Fact]
    public void A_window_tab_row_steps_with_the_controllers_tab_triggers()
    {
        var tabs = (UiTabsHandle)NewPanel(gameWindow: true).Tabs("t", new[] { "A", "B" });
        Assert.False(tabs.Step(-1));
        Assert.True(tabs.Step(1));
        Assert.Equal(1, tabs.Selected);
        Assert.False(tabs.Step(1));
        Assert.True(tabs.Back()); // a trigger step is a selection, so it is in the history
    }

    // ---- compact dropdowns -----------------------------------------------------------------------------------------------

    [Fact]
    public void A_compact_dropdown_is_its_caption_and_a_bar_as_wide_as_its_widest_option()
    {
        Assert.Equal(0f, UiCompactChoiceLayout.LabelWidth(""));
        float label = UiCompactChoiceLayout.LabelWidth("Scope");
        Assert.True(label >= UiChipsText.Width("Scope", UiCompactChoiceLayout.LabelFontSize));
        Assert.Equal(UiCompactChoiceLayout.MaxLabelWidth, UiCompactChoiceLayout.LabelWidth(new string('W', 80)));
        float narrow = UiCompactChoiceLayout.BarWidth(new[] { "Any", "All" });
        float wide = UiCompactChoiceLayout.BarWidth(new[] { "Any", "Everything in the castle" });
        Assert.True(wide > narrow);
        Assert.True(wide >= UiChipsText.Width("Everything in the castle", UiCompactChoiceLayout.ValueFontSize) + UiCompactChoiceLayout.BarChrome);
        Assert.Equal(UiCompactChoiceLayout.MinBarWidth, UiCompactChoiceLayout.BarWidth(new[] { "A" }));
        Assert.Equal(UiCompactChoiceLayout.MaxBarWidth, UiCompactChoiceLayout.BarWidth(new[] { new string('W', 90) }));
    }

    [Fact]
    public void Three_filters_share_one_row_and_a_full_row_wraps_to_a_second_line()
    {
        var items = new List<(float, float)>
        {
            (UiCompactChoiceLayout.LabelWidth("Scope"), UiCompactChoiceLayout.BarWidth(new[] { "Castle", "Territory", "All" })),
            (UiCompactChoiceLayout.LabelWidth("Match"), UiCompactChoiceLayout.BarWidth(new[] { "Any word", "All words" })),
            (UiCompactChoiceLayout.LabelWidth("Show"), UiCompactChoiceLayout.BarWidth(new[] { "Everything", "Full only" })),
        };
        // Short options fit a Basic window's 560-unit row; the hub's longer ones fit the wider rows.
        var shortItems = new List<(float, float)>
        {
            (UiCompactChoiceLayout.LabelWidth("Scope"), UiCompactChoiceLayout.BarWidth(new[] { "Castle", "All" })),
            (UiCompactChoiceLayout.LabelWidth("Match"), UiCompactChoiceLayout.BarWidth(new[] { "Any", "All" })),
            (UiCompactChoiceLayout.LabelWidth("Show"), UiCompactChoiceLayout.BarWidth(new[] { "All", "Full" })),
        };
        Assert.All(UiCompactChoiceLayout.Place(560f, shortItems), p => Assert.Equal(0, p.Line));
        foreach (float row in new[] { 760f, 960f })
        {
            var places = UiCompactChoiceLayout.Place(row, items);
            Assert.All(places, p => Assert.Equal(0, p.Line));
            float right = places[2].Left + UiCompactChoiceLayout.ItemWidth(items[2].Item1, items[2].Item2);
            Assert.True(right <= row, "the row's last filter ends inside " + row);
            for (int i = 1; i < places.Count; i++)
                Assert.Equal(places[i - 1].Left + UiCompactChoiceLayout.ItemWidth(items[i - 1].Item1, items[i - 1].Item2) + UiCompactChoiceLayout.ItemGap, places[i].Left, 3);
            Assert.Equal(UiCompactChoiceLayout.LineHeight, UiCompactChoiceLayout.RowHeight(UiCompactChoiceLayout.Lines(places)));
        }

        var narrowRow = UiCompactChoiceLayout.Place(320f, items);
        Assert.Equal(0, narrowRow[0].Line);
        Assert.True(UiCompactChoiceLayout.Lines(narrowRow) >= 2);
        Assert.Equal(0f, narrowRow.First(p => p.Line == 1).Left);
        Assert.Equal(2 * UiCompactChoiceLayout.LineHeight + UiCompactChoiceLayout.LineGap, UiCompactChoiceLayout.RowHeight(2));
    }

    [Fact]
    public void CompactDropdown_asks_the_runtime_for_the_measured_widths_and_moves_sideways()
    {
        var panel = NewPanel(gameWindow: true);
        panel.BeginInline();
        var scope = panel.CompactDropdown("Scope", new[] { "castle", "all" }, "castle", new[] { "Castle", "All" });
        var match = panel.CompactDropdown("Match", new[] { "any", "all" }, "any");
        panel.EndInline();
        Assert.Equal(2, _runtime.Compact.Count);
        Assert.Equal(UiCompactChoiceLayout.LabelWidth("Scope"), _runtime.Compact[0].Label);
        Assert.Equal(UiCompactChoiceLayout.BarWidth(new[] { "Castle", "All" }), _runtime.Compact[0].Bar);
        Assert.True(((UiChoiceHandle)scope).Compact);
        Assert.Equal("castle", scope.Value);
        var changed = new List<string>();
        match.OnChanged(changed.Add);
        Assert.True(((UiChoiceHandle)match).ChooseIndex(1));
        Assert.Equal(new[] { "all" }, changed);
    }

    // ---- a field's action button -----------------------------------------------------------------------------------------

    [Fact]
    public void Action_puts_the_button_on_the_fields_row_and_a_click_runs_it_with_the_text()
    {
        var panel = NewPanel(gameWindow: true);
        var search = panel.SearchInput("Search", "iron");
        var found = new List<string>();
        var find = search.Action("Find", found.Add);
        Assert.Single(_runtime.InlineButtons);
        Assert.Equal("Find", _runtime.InlineButtons[0].Text);
        var widget = (CellRowTests.FakeWidget)((UiHandle)find).Runtime!;
        UiSlotTable.BootstrapDispatch(widget.WiredId, (byte)UiSlotKind.Click, 0f, false, null);
        Assert.Equal(new[] { "iron" }, found);
        find.Interactable = false;
        UiSlotTable.BootstrapDispatch(widget.WiredId, (byte)UiSlotKind.Click, 0f, false, null);
        Assert.Single(found);
    }

    [Fact]
    public void Action_falls_back_to_a_button_of_its_own_and_refuses_a_field_off_any_panel()
    {
        _runtime.JoinRows = false;
        var panel = NewPanel();
        var find = panel.SearchInput("Search", "").Action("Find", _ => { });
        Assert.True(find.Enabled);
        Assert.Empty(_runtime.InlineButtons);
        var stray = new UiInputHandle("x", "root", null, null, false, 0);
        Assert.Throws<ArgumentException>(() => stray.Action("Go", _ => { }));
    }

    // ---- centered buttons and key hints -------------------------------------------------------------------------------------

    [Fact]
    public void A_centered_row_is_centered_as_a_group_and_a_too_wide_one_starts_at_the_left()
    {
        var one = PageButtonLayout.Lefts(new[] { 140f }, 560f, centered: true);
        Assert.Equal(210f, one[0]);
        var two = PageButtonLayout.Lefts(new[] { 140f, 200f }, 560f, centered: true);
        float group = 140f + PageButtonLayout.Gap + 200f;
        Assert.Equal((560f - group) / 2f, two[0]);
        Assert.Equal(two[0] + 140f + PageButtonLayout.Gap, two[1]);
        Assert.Equal(group, PageButtonLayout.GroupWidth(new[] { 140f, 200f }));
        Assert.Equal(0f, PageButtonLayout.Lefts(new[] { 400f, 400f }, 560f, centered: true)[0]);
        Assert.Equal(PageButtonLayout.Lefts(new[] { 140f, 200f }), PageButtonLayout.Lefts(new[] { 140f, 200f }, 560f, centered: false));
    }

    [Fact]
    public void Centered_asks_the_runtime_to_center_the_buttons_row()
    {
        var panel = NewPanel(gameWindow: true);
        var b = panel.Button("Return").Centered();
        Assert.Single(_runtime.Centered);
        Assert.Same(((UiHandle)b).Runtime, _runtime.Centered[0]);
    }

    [Fact]
    public void A_key_hint_names_a_key_a_registered_bind_or_a_game_action()
    {
        var binds = new List<IKeybindInfo>
        {
            new Bind("example.hub", "Open", new KeyChord(UiKey.F7, KeyModifiers.Ctrl)),
            new Bind("example.hub", "Unbound", KeyChord.None),
            new Bind("other.mod", "Open", new KeyChord(UiKey.F2)),
        };
        Func<string, string?> game = a => a == "Interact" ? "F" : null;
        Assert.Equal("Esc", UiKeyHint.Resolve("Esc", "example.hub", binds, game));
        Assert.Equal(new KeyChord(UiKey.F7, KeyModifiers.Ctrl).DisplayText, UiKeyHint.Resolve("Open", "example.hub", binds, game));
        Assert.Equal(new KeyChord(UiKey.F2).DisplayText, UiKeyHint.Resolve("Open", "other.mod", binds, game));
        Assert.Equal("", UiKeyHint.Resolve("Unbound", "example.hub", binds, game));
        Assert.Equal("F", UiKeyHint.Resolve("game:Interact", "example.hub", binds, game));
        Assert.Equal("", UiKeyHint.Resolve("game:Jump", "example.hub", binds, game));
        Assert.Equal("", UiKeyHint.Resolve("  ", "example.hub", binds, game));
        Assert.Equal(UiKeyHint.MaxLength, UiKeyHint.Resolve(new string('x', 40), "example.hub", binds, game).Length);
        Assert.True(UiKeyHint.Follows("Open", "example.hub", binds));
        Assert.True(UiKeyHint.Follows("game:Interact", "example.hub", binds));
        Assert.False(UiKeyHint.Follows("Esc", "example.hub", binds));
    }

    [Fact]
    public void A_key_cap_is_never_narrower_than_it_is_tall_and_the_button_keeps_its_room()
    {
        Assert.Equal(0f, UiKeyHint.CapWidth(""));
        Assert.True(UiKeyHint.CapWidth("F") >= UiKeyHint.CapHeight);
        Assert.True(UiKeyHint.CapWidth("Ctrl + F7") > UiKeyHint.CapHeight);
        Assert.Equal(UiKeyHint.CapWidth("Esc") + UiKeyHint.Gap + UiKeyHint.RightInset, UiKeyHint.Room("Esc"));
        Assert.Equal(0f, UiKeyHint.Room(""));
    }

    [Fact]
    public void KeyHint_draws_the_cap_and_removes_it_with_an_empty_hint()
    {
        var b = NewPanel(gameWindow: true).Button("Return").KeyHint("Esc");
        Assert.Equal("Esc", UiKeyHints.ShownOn(b));
        Assert.Equal(new[] { "Esc" }, _runtime.Hints.Select(h => h.Text));
        b.KeyHint("Enter");
        Assert.Equal("Enter", _runtime.Hints.Single().Text);
        b.KeyHint("");
        Assert.True(_runtime.Hints.Single().Disposed);
        Assert.Equal(0, UiKeyHints.Count);
    }

    [Fact]
    public void A_key_hint_goes_with_its_button()
    {
        var b = NewPanel(gameWindow: true).Button("Return").KeyHint("Esc");
        b.Dispose();
        UiKeyHints.Tick();
        Assert.Equal(0, UiKeyHints.Count);
        Assert.True(_runtime.Hints.Single().Disposed);
    }

    // ---- a table that fills its panel ------------------------------------------------------------------------------------------

    [Theory]
    // maxViewport, content, shown rows, built: the rows that fit with everything else on the panel.
    [InlineData(800f, 300f, 0, 30, 10)]   // 300 of other rows leave 500: ten 48-unit rows
    [InlineData(800f, 300f + 12 * 48f, 12, 30, 10)] // the same panel while 12 rows are drawn
    [InlineData(3000f, 100f, 0, 30, 30)]  // capped at the built rows
    [InlineData(200f, 180f, 0, 30, 4)]    // never under four
    public void Fill_rows_fit_the_tallest_viewport(float max, float content, int shown, int built, int rows) =>
        Assert.Equal(rows, UiTableViewport.FillRows(max, content, shown, UiTableViewport.SlotPitch, built));

    [Fact]
    public void Fill_rows_are_unknown_before_a_layout()
    {
        Assert.Equal(-1, UiTableViewport.FillRows(float.NaN, 100f, 0, 48f, 30));
        Assert.Equal(-1, UiTableViewport.FillRows(0f, 100f, 0, 48f, 30));
        Assert.Equal(48f, UiTableViewport.SlotPitch);
    }

    [Fact]
    public void A_viewport_uses_part_of_its_built_rows()
    {
        var v = new UiTableViewport(12, UiTableViewport.MaxSlots);
        Assert.Equal(30, v.Built);
        Assert.Equal(12, v.Slots);
        v.SetCount(100);
        v.SetTop(95);
        Assert.Equal(88, v.Top);
        Assert.True(v.SetSlots(20));
        Assert.Equal(80, v.Top); // the top is clamped to the new window
        Assert.False(v.SetSlots(20));
        Assert.True(v.SetSlots(99));
        Assert.Equal(30, v.Slots);
        Assert.True(v.SetSlots(1));
        Assert.Equal(UiTableViewport.MinSlots, v.Slots);
    }

    [Fact]
    public void A_filling_table_builds_its_most_rows_and_uses_what_the_panel_room_allows()
    {
        var panel = NewPanel(gameWindow: true);
        var table = panel.DataTable("hub", new[] { new UiTableColumn("name", "Building") }, new UiDataTableOptions { FillHeight = true });
        table.SetRows(Enumerable.Range(0, 50).Select(i => new UiTableRow("r" + i).Text("name", "Chest " + i)));
        Assert.Equal(30, table.Describe()["builtRows"]);
        Assert.Equal(12, table.VisibleRows);
        panel.Show();
        _runtime.Room = (900f, 200f + 12 * UiTableViewport.SlotPitch);
        for (int i = 0; i < UiDataTableHandle.FillEveryFrames; i++)
            foreach (var t in _runtime.Ticks.ToArray()) t();
        Assert.Equal(14, table.VisibleRows); // (900 - 200) / 48
    }

    // ---- building icons ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void Building_icons_are_cached_forgotten_when_unloaded_and_scanned_at_most_twice_a_second()
    {
        var sprite = new object();
        Assert.Null(UiBuildingIcons.Get(42));
        UiBuildingIcons.Note(42, sprite);
        UiBuildingIcons.Note(0, sprite);
        UiBuildingIcons.Note(43, null);
        Assert.Equal(1, UiBuildingIcons.Count);
        Assert.Same(sprite, UiBuildingIcons.Get(42, _ => true));
        Assert.Null(UiBuildingIcons.Get(42, _ => false));
        Assert.Equal(0, UiBuildingIcons.Count);
        Assert.True(UiBuildingIcons.MayScan(100));
        Assert.False(UiBuildingIcons.MayScan(100 + UiBuildingIcons.ScanEveryFrames - 1));
        Assert.True(UiBuildingIcons.MayScan(100 + UiBuildingIcons.ScanEveryFrames));
        UiBuildingIcons.Clear();
        Assert.True(UiBuildingIcons.MayScan(0));
    }

    [Fact]
    public void A_row_naming_a_building_draws_its_icon_and_keeps_an_aligned_blank_without_one()
    {
        var known = new object();
        _runtime.Buildings[-100] = known;
        var panel = NewPanel(gameWindow: true);
        var table = panel.DataTable("b", new[] { new UiTableColumn("name", "Building") });
        table.SetRows(new[]
        {
            new UiTableRow("chest") { BuildingPrefab = -100 }.Text("name", "Chest"),
            new UiTableRow("smithy") { BuildingPrefab = -200 }.Text("name", "Smithy"),
        });
        panel.Show();
        var first = Row("b:row0");
        var second = Row("b:row1");
        Assert.Same(known, first.Images[UiDataTableLayout.CellIcon]);
        Assert.True(first.Cells().Single(c => c.Key == UiDataTableLayout.CellIcon).Visible);
        Assert.False(second.Cells().Single(c => c.Key == UiDataTableLayout.CellIcon).Visible);
        Assert.True(second.Cells().Single(c => c.Key == UiDataTableLayout.CellIconPad).Visible);
        Assert.Contains(-200, _runtime.BuildingAsks);
    }

    // ---- the gallery's hub window --------------------------------------------------------------------------------------------

    [Fact]
    public void The_gallery_hub_window_builds_every_hub_control()
    {
        var log = new List<string>();
        using var hub = new Progenitor.ControlsSample.GalleryHub(
            () => UiApi.Panel(Progenitor.ControlsSample.GalleryHub.Title, _owner, "example.hub"), name => name.Length, log.Add, "Open");
        Assert.Equal("opened", hub.Toggle());
        Assert.Equal("game", hub.Tabs!.Describe()["look"]);
        Assert.Equal(3, hub.Filters.Count);
        Assert.Equal(3, _runtime.Compact.Count);
        Assert.Equal("Find", _runtime.InlineButtons.Single().Text);
        Assert.Equal(true, hub.Table!.Describe()["fillHeight"]);
        Assert.All(hub.Rows(), r => Assert.NotEqual(0, r.BuildingPrefab));
        Assert.Single(_runtime.Centered);
        Assert.Single(_runtime.Hints);
        Assert.Contains(_runtime.HeightRules, r => r.Keep);
        Assert.Equal("closed", hub.Toggle());
    }

    // ---- fakes -------------------------------------------------------------------------------------------------------------------

    private sealed class Bind : IKeybindInfo
    {
        internal Bind(string plugin, string id, KeyChord chord)
        {
            PluginId = plugin;
            Id = id;
            Chord = chord;
        }

        public string PluginId { get; }

        public string Id { get; }

        public string Description => "";

        public KeyChord Chord { get; }

        public KeyChord DefaultChord => Chord;
    }

    internal sealed class Hint : IRuntimeKeyHint
    {
        internal string Text = "";
        internal bool Disposed;

        public void SetText(string text) => Text = text;

        public void Dispose() => Disposed = true;
    }

    internal sealed class HubRuntimeFake : CellRowTests.PlainRuntime, IUiRuntime, IWidgetRuntime
    {
        public readonly List<CellRowTests.FakeCellRow> Rows = new();
        public readonly List<Action> Ticks = new();
        public readonly List<(bool Keep, float Min)> HeightRules = new();
        public readonly List<(float Label, float Bar)> Compact = new();
        public readonly List<CellRowTests.FakeWidget> InlineButtons = new();
        public readonly List<IRuntimeWidget> Centered = new();
        public readonly List<Hint> Hints = new();
        public readonly Dictionary<int, object> Buildings = new();
        public readonly List<int> BuildingAsks = new();
        public bool JoinRows = true;
        public (float Max, float Content)? Room;

        public override IRuntimeCellRow? AddCellRow(IRuntimePanel panel, CellRowSpec spec, bool header)
        {
            var row = new CellRowTests.FakeCellRow(null, spec, header);
            Rows.Add(row);
            return row;
        }

        public void SetHeightRule(IRuntimePanel panel, bool keep, float minContentHeight) => HeightRules.Add((keep, minContentHeight));

        public IRuntimeWidget? AddCompactChoice(
            IRuntimePanel panel, string text, IReadOnlyList<UiChoiceOption> options, string value, float labelWidth, float barWidth)
        {
            Compact.Add((labelWidth, barWidth));
            return new CellRowTests.FakeWidget { Text = text, StringValue = value };
        }

        public IRuntimeWidget? JoinFieldRowButton(IRuntimePanel panel, IRuntimeWidget field, string text)
        {
            if (!JoinRows) return null;
            var w = new CellRowTests.FakeWidget { Text = text };
            InlineButtons.Add(w);
            return w;
        }

        public bool CenterButtonRow(IRuntimePanel panel, IRuntimeWidget button)
        {
            Centered.Add(button);
            return true;
        }

        public IRuntimeKeyHint? AttachKeyHint(IRuntimeWidget button, string text)
        {
            var h = new Hint { Text = text };
            Hints.Add(h);
            return h;
        }

        public bool PanelRoom(IUiPanel panel, out float maxViewport, out float content)
        {
            maxViewport = Room?.Max ?? 0f;
            content = Room?.Content ?? 0f;
            return Room != null;
        }

        public object? BuildingIcon(int prefab)
        {
            BuildingAsks.Add(prefab);
            return Buildings.TryGetValue(prefab, out var icon) ? icon : null;
        }

        public void ShowTooltip(ScreenRect anchor, string title, string body, Func<bool>? stillShown = null) { }

        public void HideTooltip() { }

        public IDisposable? WatchCell(IRuntimeCellRow row, string cellKey, Action<UiPointerEvent> handler, object? owner) => null;

        public IDisposable? WatchHover(IRuntimeWidget widget, Action<bool> onHover, object? owner) => null;

        public IDisposable EveryFrame(Action tick)
        {
            Ticks.Add(tick);
            return new ActionDisposable(() => Ticks.Remove(tick));
        }

        public string DrawableGlyph(string preferred, string fallback) => preferred;

        public new PointerSample ReadPointer() => PointerSample.At(0f, 0f);

        public bool PrimaryHeld() => false;

        public object? ItemIcon(int prefab) => null;

        public string? ItemName(int prefab) => null;
    }
}
