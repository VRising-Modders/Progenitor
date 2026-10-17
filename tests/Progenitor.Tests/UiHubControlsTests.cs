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
        var plain = NewPanel().Tabs("plain", new[] { "Find", "Chests" }, new UiTabsOptions());
        var game = NewPanel(gameWindow: true).Tabs("game", new[] { "Find", "Chests" }, new UiTabsOptions());
        Assert.Equal("chips", plain.Describe()["look"]);
        Assert.Equal("game", game.Describe()["look"]);
        Assert.True(((UiTabsHandle)game).HistoryOn);
        // The overload without options keeps its old look and has no history, on a game window too.
        var legacy = NewPanel(gameWindow: true).Tabs("legacy", new[] { "Find", "Chests" });
        Assert.Equal("chips", legacy.Describe()["look"]);
        Assert.False(((UiTabsHandle)legacy).HistoryOn);
        Assert.Equal(CellSkin.Chip, Row("legacy:tabs").Cells().Single(c => c.Key == "tab0").Skin);
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

    [Theory]
    [InlineData(640f)]
    [InlineData(1040f)]
    [InlineData(1440f)]
    [InlineData(1920f)]
    public void Game_tabs_stand_on_the_strips_baseline_at_every_window_width(float window)
    {
        var panel = NewPanel(gameWindow: true);
        panel.Tabs("t", new[] { "Find", "Chests", "About" }, new UiTabsOptions());
        var spec = Row("t:tabs").Spec;
        // The game tab row draws the baseline; a chip row does not.
        Assert.Equal(CellRowSkin.GameTabStrip, spec.Skin);
        NewPanel().Tabs("c", new[] { "Find", "Chests" }, new UiTabsOptions());
        Assert.Equal(CellRowSkin.Kit, Row("c:tabs").Spec.Skin);

        // Every tab fits the row at this window's content width, so the baseline (the whole row) runs under all of them.
        float rowWidth = DialogLayout.ContentRowWidth(DialogLayout.FrameWidthFor(window, 3840f));
        var placed = MenuPanelLayout.CellColumns(rowWidth, spec).Where(p => p.Key.StartsWith("tab", StringComparison.Ordinal)).ToList();
        Assert.Equal(3, placed.Count);
        Assert.All(placed, p => Assert.True(p.Visible, p.Key + " dropped at " + window));

        // Vertically: each tab's bottom edge is the row's bottom edge, and the 2-unit baseline's bottom row is that same
        // row, so the tabs cover the line and the selected tab's fill runs into it (the workspace strip's measured geometry).
        foreach (float rowHeight in new[] { spec.Height, 48f, 53f, 64f })
        {
            foreach (var tab in spec.Cells.Where(c => c.Skin == CellSkin.GameTab))
            {
                float h = Math.Min(tab.HeightOverride, rowHeight);
                float top = UiTabsLayout.GameTabTop(rowHeight, h);
                var (lineTop, lineHeight) = UiTabsLayout.Baseline(rowHeight);
                Assert.Equal(rowHeight, top + h, 3);
                Assert.Equal(rowHeight, lineTop + lineHeight, 3);
                Assert.Equal(UiTabsLayout.BaselineHeight, lineHeight);
                Assert.True(top <= lineTop, "the tab covers its baseline");
            }
        }

        // The default row: the tab fills it exactly (no gap above or below the art).
        Assert.Equal(0f, UiTabsLayout.GameTabTop(spec.Height, UnityUiRuntimeMetrics.RowHeight));
    }

    private static UiTabsLayout.StripRow SR(bool active = true, bool strip = false, MenuRowPin pin = MenuRowPin.Content, bool line = false) =>
        new(active, strip, pin, line);

    /// <summary>The lines drawn between a strip's tabs and the content under it: its baseline and the next shown row's top line.</summary>
    private static int LinesUnderStrip(IReadOnlyList<UiTabsLayout.StripRow> rows, int strip)
    {
        var hidden = UiTabsLayout.TopLinesHidden(rows);
        int lines = rows[strip].TabStrip ? 1 : 0;
        for (int j = strip + 1; j < rows.Count; j++)
        {
            bool below = rows[j].Pin == MenuRowPin.Content || (rows[strip].Pin == MenuRowPin.Top && rows[j].Pin == MenuRowPin.Top);
            if (!below || !rows[j].Active) continue;
            if (rows[j].TopLine && !hidden[j]) lines++;
            break;
        }

        return lines;
    }

    [Fact]
    public void A_game_tab_row_first_in_a_game_window_leaves_exactly_one_line_under_its_tabs()
    {
        // Satisvampory's hub (live 2026-10-17): the tab row, then SearchInput (a settings-grid row whose top gradient was
        // the second line), then more rows.
        var rows = new[] { SR(strip: true), SR(line: true), SR(line: true), SR(line: true) };
        Assert.Equal(new[] { false, true, false, false }, UiTabsLayout.TopLinesHidden(rows));
        Assert.Equal(1, LinesUnderStrip(rows, 0));
        // The one line is the strip's baseline, and the tabs stand on it.
        float rowHeight = UnityUiRuntimeMetrics.RowHeight;
        var (lineTop, lineHeight) = UiTabsLayout.Baseline(rowHeight);
        Assert.Equal(lineTop + lineHeight, UiTabsLayout.GameTabTop(rowHeight, rowHeight) + rowHeight, 3);

        // The runtime's real rows: the strip is a game tab row with its baseline.
        var panel = NewPanel(gameWindow: true);
        panel.Tabs("t", new[] { "Find", "Chests" }, new UiTabsOptions());
        Assert.Equal(CellRowSkin.GameTabStrip, Row("t:tabs").Spec.Skin);
    }

    [Fact]
    public void The_row_under_the_strip_follows_the_selected_tab_and_skips_other_pins()
    {
        // A hidden tab's rows are skipped: the first shown row (a list row with its separator) drops its line.
        var switched = new[] { SR(strip: true), SR(active: false, line: true), SR(active: false), SR(line: true), SR(line: true) };
        Assert.Equal(new[] { false, false, false, true, false }, UiTabsLayout.TopLinesHidden(switched));
        Assert.Equal(1, LinesUnderStrip(switched, 0));

        // A removed row still listed (its object destroyed) is read as not shown and passed over the same way.
        var removed = new[] { SR(strip: true), SR(active: false, line: true), SR(line: true) };
        Assert.Equal(new[] { false, false, true }, UiTabsLayout.TopLinesHidden(removed));

        // A top-pinned tab row (Look = Game on a panel that pins it), a bottom-pinned button row listed before the content.
        var window = new[] { SR(pin: MenuRowPin.Top), SR(strip: true, pin: MenuRowPin.Top), SR(pin: MenuRowPin.Bottom, line: true), SR(line: true) };
        Assert.Equal(new[] { false, false, false, true }, UiTabsLayout.TopLinesHidden(window));
        Assert.Equal(1, LinesUnderStrip(window, 1));

        // A content tab row never reaches a top-pinned row listed after it.
        var content = new[] { SR(strip: true), SR(pin: MenuRowPin.Top, line: true), SR(line: true) };
        Assert.Equal(new[] { false, false, true }, UiTabsLayout.TopLinesHidden(content));

        // A first row with no line of its own: nothing is hidden, and the row after it keeps its line.
        var label = new[] { SR(strip: true), SR(), SR(line: true) };
        Assert.Equal(new[] { false, false, false }, UiTabsLayout.TopLinesHidden(label));
        Assert.Equal(1, LinesUnderStrip(label, 0));

        // Chip tabs draw no baseline, so the row under them keeps its line; a hidden strip hides nothing.
        Assert.Equal(new[] { false, false }, UiTabsLayout.TopLinesHidden(new[] { SR(), SR(line: true) }));
        Assert.Equal(new[] { false, false }, UiTabsLayout.TopLinesHidden(new[] { SR(active: false, strip: true), SR(line: true) }));
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
        var tabs = panel.Tabs("t", new[] { "Equipment and crafting" }, new UiTabsOptions());
        float before = Row("t:tabs").Cells().Single(c => c.Key == "tab0").MinWidth;
        tabs.SetBadge(0, 12);
        float after = Row("t:tabs").Cells().Single(c => c.Key == "tab0").MinWidth;
        Assert.True(after > before);
    }

    [Fact]
    public void The_standard_windows_keep_their_chip_tabs_and_gain_a_history_that_ends_with_the_window()
    {
        var window = UiApi.Window(UiWindowPreset.Header, "Iron Chest", _owner, "example.hub", new UiWindowOptions { Tabs = new[] { "Contents", "Rules", "Log" } });
        try
        {
            var tabs = (UiTabsHandle)window.Tabs!;
            Assert.Equal("chips", tabs.Describe()["look"]);
            Assert.True(tabs.HistoryOn);
            window.Show();
            tabs.Selected = 1;
            tabs.Selected = 2;
            Assert.Equal(new[] { 0, 1, 2 }, tabs.History!.Entries);
            // Closing the window ends its history; the next opening starts at the tab it shows.
            window.Hide();
            window.Show();
            Assert.Equal(new[] { 2 }, tabs.History!.Entries);
            Assert.False(tabs.Back());
        }
        finally { window.Dispose(); }
    }

    [Fact]
    public void A_floating_window_reopened_starts_a_new_history()
    {
        var panel = NewPanel(gameWindow: true);
        var tabs = (UiTabsHandle)panel.Tabs("t", new[] { "A", "B", "C" }, new UiTabsOptions());
        panel.Show();
        tabs.Selected = 1;
        tabs.Selected = 2;
        panel.Hide(); // Escape, or the mod's own Hide
        panel.Show();
        Assert.False(tabs.Back());
        tabs.Selected = 0;
        Assert.Equal(new[] { 2, 0 }, tabs.History!.Entries);
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
        var tabs = panel.Tabs("t", new[] { "Find", "Chests", "About" }, new UiTabsOptions());
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
        Assert.Equal(-1, UiTabHistoryRoute.Pick(Array.Empty<UiTabHistoryRoute.Candidate>(), pointerOverPanel: false));
        // index, draw order (canvas sort order), shown order, under the pointer, holds the input
        var candidates = new[]
        {
            new UiTabHistoryRoute.Candidate(0, 5010, 5, underPointer: false, holdsInput: true),
            new UiTabHistoryRoute.Candidate(1, 5012, 3, underPointer: true, holdsInput: false),
            new UiTabHistoryRoute.Candidate(2, 5014, 9, underPointer: false, holdsInput: true),
            new UiTabHistoryRoute.Candidate(3, 5011, 8, underPointer: true, holdsInput: true),
        };
        // Under the pointer: the one drawn on top wins, even though another was shown later.
        Assert.Equal(1, UiTabHistoryRoute.Pick(candidates, pointerOverPanel: true));
        var notUnder = candidates.Where(c => !c.UnderPointer).ToArray();
        // Holding the input: only while the pointer is over no panel (not the Mods workspace, not another window).
        Assert.Equal(2, UiTabHistoryRoute.Pick(notUnder, pointerOverPanel: false));
        Assert.Equal(-1, UiTabHistoryRoute.Pick(notUnder, pointerOverPanel: true));
        // Same draw order: the one shown later.
        Assert.Equal(7, UiTabHistoryRoute.Pick(new[]
        {
            new UiTabHistoryRoute.Candidate(6, 5010, 1, true, false),
            new UiTabHistoryRoute.Candidate(7, 5010, 2, true, false),
        }, pointerOverPanel: true));
        Assert.Equal(-1, UiTabHistoryRoute.Pick(new[] { new UiTabHistoryRoute.Candidate(0, 0, 1, false, false) }, pointerOverPanel: false));
    }

    [Fact]
    public void Back_and_forward_go_to_the_shown_window_under_the_pointer_and_not_to_a_hidden_one()
    {
        var front = NewPanel(gameWindow: true);
        var back = NewPanel(gameWindow: true);
        var frontTabs = front.Tabs("front", new[] { "A", "B" }, new UiTabsOptions());
        var backTabs = back.Tabs("back", new[] { "A", "B" }, new UiTabsOptions());
        Click(Row("front:tabs"), "tab1");
        Click(Row("back:tabs"), "tab1");
        UiTabHistoryRoute.UnderPointer = p => ReferenceEquals(p, front);

        // Nothing shown: nothing takes the press (the workspace would).
        Assert.False(UiTabHistoryRoute.TryNavigate(back: true));
        front.Show();
        back.Show();
        UiTabHistoryRoute.ResetCache();
        Assert.True(UiTabHistoryRoute.TryNavigate(back: true));
        Assert.Equal(0, frontTabs.Selected);
        Assert.Equal(1, backTabs.Selected);
        // At the start of its history the window does not take Back: it goes on to the workspace.
        Assert.False(UiTabHistoryRoute.TryNavigate(back: true));
        Assert.True(UiTabHistoryRoute.TryNavigate(back: false));
        Assert.Equal(1, frontTabs.Selected);
        Assert.False(UiTabHistoryRoute.TryNavigate(back: false));

        // With the pointer over no panel, a window that holds the input while shown takes it.
        UiTabHistoryRoute.UnderPointer = _ => false;
        back.CaptureMode = UiCaptureMode.WhileShown;
        UiTabHistoryRoute.ResetCache();
        Assert.Same(backTabs, UiTabHistoryRoute.Target());
        // The answer is taken once a frame: the same frame keeps it, the next poll reads again.
        back.CaptureMode = UiCaptureMode.WhenHovered;
        Assert.Same(backTabs, UiTabHistoryRoute.Target());
        Keybinds.Pump();
        Assert.Null(UiTabHistoryRoute.Target());
    }

    [Fact]
    public void A_window_tab_row_steps_with_the_controllers_tab_triggers()
    {
        var tabs = (UiTabsHandle)NewPanel(gameWindow: true).Tabs("t", new[] { "A", "B" }, new UiTabsOptions());
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
        var find = search.ActionButton("Find", found.Add);
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
        var find = panel.SearchInput("Search", "").ActionButton("Find", _ => { });
        Assert.True(find.Enabled);
        Assert.Empty(_runtime.InlineButtons);
        var stray = new UiInputHandle("x", "root", null, null, false, 0);
        Assert.Throws<ArgumentException>(() => stray.ActionButton("Go", _ => { }));
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
    public void Scans_back_off_after_misses_and_come_back_when_one_learns_an_icon()
    {
        Assert.Equal(UiBuildingIcons.ScanEveryFrames, UiBuildingIcons.ScanInterval);
        UiBuildingIcons.ScanDone(learnt: false);
        Assert.Equal(2 * UiBuildingIcons.ScanEveryFrames, UiBuildingIcons.ScanInterval);
        UiBuildingIcons.ScanDone(learnt: false);
        Assert.Equal(4 * UiBuildingIcons.ScanEveryFrames, UiBuildingIcons.ScanInterval);
        for (int i = 0; i < 20; i++) UiBuildingIcons.ScanDone(learnt: false);
        Assert.Equal(UiBuildingIcons.MaxScanEveryFrames, UiBuildingIcons.ScanInterval);
        Assert.True(UiBuildingIcons.MayScan(1000));
        Assert.False(UiBuildingIcons.MayScan(1000 + UiBuildingIcons.MaxScanEveryFrames - 1));
        UiBuildingIcons.ScanDone(learnt: true);
        Assert.Equal(UiBuildingIcons.ScanEveryFrames, UiBuildingIcons.ScanInterval);

        int v = UiBuildingIcons.Version;
        var sprite = new object();
        UiBuildingIcons.Note(7, sprite);
        Assert.Equal(v + 1, UiBuildingIcons.Version);
        UiBuildingIcons.Note(7, sprite); // the same sprite again: nothing new learnt
        Assert.Equal(v + 1, UiBuildingIcons.Version);
    }

    // The four buildings of the Core 2026.10.84 report (client prefab ids).
    private const int MineralsChestT02 = -1155345803; // TM_Castle_Container_Specialized_Minerals_T02
    private const int WoodChest = -251472465;         // TM_Stash_Chest_Wood_General
    private const int SmallFurnace = -1150411622;     // TM_RefinementStation_Furnace_Small
    private const int Fabricator = -465055967;        // TM_RefinementStation_Fabricator

    [Fact]
    public void A_stale_build_menu_cell_never_names_the_icon_of_a_blueprint_with_its_own_icon()
    {
        // The report: a pooled cell still showing an earlier sprite (a quill) while the Furnace's and the
        // Fabricator's sprites stream in. Each of the four blueprints names an icon asset of its own.
        var quill = new object();
        foreach (int prefab in new[] { MineralsChestT02, WoodChest, SmallFurnace, Fabricator })
        {
            UiBuildingIcons.NoteKey(prefab, hasBlueprintKey: true);
            Assert.False(UiBuildingIcons.AcceptsCell(prefab, cellLoading: false));
            Assert.False(UiBuildingIcons.AcceptsCell(prefab, cellLoading: true));
            Assert.Equal("none", UiBuildingIcons.SourceName(prefab));
        }

        // Each resolves to its own blueprint sprite, read back as "blueprint".
        var sprites = new Dictionary<int, object>
        {
            [MineralsChestT02] = new(), [WoodChest] = new(), [SmallFurnace] = new(), [Fabricator] = new(),
        };
        foreach (var (prefab, sprite) in sprites) UiBuildingIcons.Note(prefab, sprite, BuildingIconSource.Blueprint);
        foreach (var (prefab, sprite) in sprites)
        {
            Assert.Same(sprite, UiBuildingIcons.Get(prefab));
            Assert.Equal("blueprint", UiBuildingIcons.SourceName(prefab));
        }

        // A cell icon arriving later does not replace a blueprint icon.
        UiBuildingIcons.Note(SmallFurnace, quill, BuildingIconSource.BuildMenu);
        UiBuildingIcons.Note(Fabricator, quill);
        Assert.Same(sprites[SmallFurnace], UiBuildingIcons.Get(SmallFurnace));
        Assert.Same(sprites[Fabricator], UiBuildingIcons.Get(Fabricator));
    }

    [Fact]
    public void A_cell_icon_is_kept_only_for_a_building_whose_blueprint_names_no_icon_and_withdrawn_when_one_does()
    {
        var cellSprite = new object();
        Assert.False(UiBuildingIcons.AcceptsCell(-77, cellLoading: false)); // not asked yet: no cell is trusted
        UiBuildingIcons.NoteKey(-77, hasBlueprintKey: false);
        Assert.True(UiBuildingIcons.KeyKnown(-77));
        Assert.False(UiBuildingIcons.AcceptsCell(-77, cellLoading: true)); // its own sprite has not arrived
        Assert.True(UiBuildingIcons.AcceptsCell(-77, cellLoading: false));
        UiBuildingIcons.Note(-77, cellSprite, BuildingIconSource.BuildMenu);
        Assert.Equal("build-menu", UiBuildingIcons.SourceName(-77));

        // Game data later names an icon asset for it: the cell's icon is withdrawn and a waiting row redraws.
        int v = UiBuildingIcons.Version;
        UiBuildingIcons.NoteKey(-77, hasBlueprintKey: true);
        Assert.True(UiBuildingIcons.Version > v);
        Assert.Null(UiBuildingIcons.Get(-77));
        Assert.Equal("none", UiBuildingIcons.SourceName(-77));
        Assert.False(UiBuildingIcons.AcceptsCell(-77, cellLoading: false));
    }

    [Fact]
    public void Missed_buildings_wait_for_the_next_scan_until_an_icon_is_learnt_and_Clear_forgets_everything()
    {
        UiBuildingIcons.Want(SmallFurnace);
        UiBuildingIcons.Want(Fabricator);
        UiBuildingIcons.Want(0);
        Assert.Equal(new[] { Fabricator, SmallFurnace }.OrderBy(x => x), UiBuildingIcons.WaitingNow().OrderBy(x => x));
        UiBuildingIcons.Note(SmallFurnace, new object(), BuildingIconSource.Blueprint);
        Assert.Equal(new[] { Fabricator }, UiBuildingIcons.WaitingNow());
        UiBuildingIcons.Want(SmallFurnace); // known now: not waiting again
        Assert.Equal(new[] { Fabricator }, UiBuildingIcons.WaitingNow());

        UiBuildingIcons.NoteKey(WoodChest, hasBlueprintKey: false);
        UiBuildingIcons.Clear();
        Assert.Empty(UiBuildingIcons.WaitingNow());
        Assert.False(UiBuildingIcons.KeyKnown(WoodChest));
        Assert.Equal(0, UiBuildingIcons.Count);
        Assert.Equal(BuildingIconSource.None, UiBuildingIcons.Source(SmallFurnace));
    }

    [Fact]
    public void A_waiting_row_draws_its_building_icon_once_the_cache_learns_it_and_stops_waiting()
    {
        var panel = NewPanel(gameWindow: true);
        var table = (UiDataTableHandle)panel.DataTable("w", new[] { new UiTableColumn("name", "Building") });
        table.SetRows(new[] { new UiTableRow("smithy") { BuildingPrefab = -300 }.Text("name", "Smithy") });
        panel.Show();
        Assert.Equal(-300, table.WaitingBuilding);
        int asks = _runtime.BuildingAsks.Count;
        // While it waits the table asks again each frame (the runtime's back-off decides whether a scan runs).
        foreach (var t in _runtime.Ticks.ToArray()) t();
        Assert.True(_runtime.BuildingAsks.Count > asks);

        // The game draws the Smithy in its build menu: the cache learns it and the row draws it, no SetRows needed.
        var sprite = new object();
        _runtime.Buildings[-300] = sprite;
        UiBuildingIcons.Note(-300, sprite);
        foreach (var t in _runtime.Ticks.ToArray()) t();
        Assert.Same(sprite, Row("w:row0").Images[UiDataTableLayout.CellIcon]);
        Assert.Equal(0, table.WaitingBuilding);
        int ticks = _runtime.Ticks.Count;
        foreach (var t in _runtime.Ticks.ToArray()) t();
        Assert.Equal(ticks, _runtime.Ticks.Count);
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
        Assert.Equal(Progenitor.ControlsSample.GalleryHub.MinRows, hub.Table.Describe()["minRows"]);

        // The row actions: the first row cannot move up; down swaps it with the next; remove drops a row.
        var first = hub.Rows()[0];
        Assert.False(hub.Table.PressRowAction(first.Id, "up"));
        Assert.True(hub.Table.PressRowAction(first.Id, "down"));
        Assert.Equal(first.Id, hub.Table.Rows[1].Id);
        Assert.True(hub.Table.PressRowAction(first.Id, "remove"));
        Assert.Equal(2, hub.Table.Rows.Count);
        Assert.Contains(log, l => l.EndsWith("hub row remove " + first.Id, StringComparison.Ordinal));
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
