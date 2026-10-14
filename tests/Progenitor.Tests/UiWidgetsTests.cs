using System;
using System.Collections.Generic;
using System.Linq;
using Progenitor.Ui;
using Xunit;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.Tests;

/// <summary>
/// P7: the composed widgets (ordered list, picker, tabs, tooltips) through a fake runtime that draws cell rows and
/// implements <see cref="IWidgetRuntime"/>, plus their game-free models and layout rules. Every assertion is an
/// outcome: the order after a click, the rows a render left visible, the event a plugin received.
/// </summary>
[Collection("UiStatic")]
public sealed class UiWidgetsTests : IDisposable
{
    private readonly WidgetRuntimeFake _runtime = new();
    private readonly object _owner = new();
    private readonly List<IUiPanel> _panels = new();

    public UiWidgetsTests()
    {
        UiApi.ResetForTests();
        UiApi.SetRuntimeForTests(_runtime);
    }

    public void Dispose()
    {
        foreach (var p in _panels) p.Dispose();
        UiApi.ResetForTests();
    }

    private IUiPanel NewPanel()
    {
        var panel = UiApi.Panel("Widgets", _owner, "example.widgets");
        _panels.Add(panel);
        return panel;
    }

    private static List<UiListItem> Items(params string[] ids) =>
        ids.Select(id => new UiListItem(id, "Item " + id) { Detail = id == "b" ? "Accepts ore" : "" }).ToList();

    private CellRowTests.FakeCellRow Row(string key) => _runtime.Rows.Single(r => r.Text == key);

    private static void Click(CellRowTests.FakeCellRow row, string cell) =>
        UiSlotTable.BootstrapDispatch(row.WiredIdOf(cell), (byte)UiSlotKind.Click, 0f, false, null);

    // ---- Ordered list model ------------------------------------------------------------------

    [Fact]
    public void Model_moves_removes_and_refuses_duplicate_ids()
    {
        var m = new UiOrderedListModel(Items("a", "b", "c"), 0);
        Assert.Equal(UiOrderedListModel.DefaultCapacity, m.Capacity);
        Assert.True(m.Move(0, 2, out var change));
        Assert.Equal(new[] { "b", "c", "a" }, m.Order());
        Assert.Equal("a", change.Id);
        Assert.Equal(0, change.From);
        Assert.Equal(2, change.To);
        Assert.False(m.Move(1, 1, out _));
        Assert.True(m.Move(1, 99, out _)); // clamped to the end
        Assert.Equal(new[] { "b", "a", "c" }, m.Order());
        Assert.True(m.Remove("a", out var removed, out int at));
        Assert.Equal("a", removed!.Id);
        Assert.Equal(1, at);
        Assert.False(m.Remove("zz", out _, out _));
        Assert.Throws<ArgumentException>(() => m.SetItems(Items("x", "x")));
        Assert.Equal(new[] { "b", "c" }, m.Order()); // a refused SetItems changed nothing
    }

    [Fact]
    public void Model_capacity_follows_the_request_and_reports_overflow()
    {
        Assert.Equal(3, UiOrderedListModel.CapacityFor(3, 10));
        Assert.Equal(12, UiOrderedListModel.CapacityFor(0, 12));
        Assert.Equal(UiOrderedListModel.MaxCapacity, UiOrderedListModel.CapacityFor(0, 500));
        var m = new UiOrderedListModel(Items("a", "b", "c", "d"), 2);
        Assert.Equal(2, m.Drawn);
        Assert.Equal(2, m.Overflow);
    }

    [Fact]
    public void Drawers_open_only_for_items_with_detail_and_survive_a_reorder()
    {
        var m = new UiOrderedListModel(Items("a", "b", "c"), 0);
        Assert.False(m.SetExpanded("a", true)); // no detail
        Assert.True(m.SetExpanded("b", true));
        Assert.True(m.Move(1, 0, out _));
        Assert.True(m.IsExpanded("b"));
        m.SetItems(Items("c", "a")); // b left: its drawer state goes with it
        Assert.False(m.IsExpanded("b"));
    }

    [Theory]
    [InlineData(500f, 0)] // above every slot
    [InlineData(185f, 0)] // nearer slot 0 (200) than slot 1 (160): no change until the halfway line
    [InlineData(179f, 1)] // just past the halfway line (180)
    [InlineData(121f, 2)] // slot 2's center
    [InlineData(10f, 3)]  // below every slot: the last
    public void Drop_index_is_the_slot_whose_center_is_nearest(float pointerY, int expected)
    {
        var centers = new List<float> { 200f, 160f, 120f, 80f };
        Assert.Equal(expected, UiOrderedListModel.DropIndex(pointerY, centers));
    }

    [Fact]
    public void Drop_index_skips_unreadable_rows_and_refuses_when_none_read()
    {
        Assert.Equal(2, UiOrderedListModel.DropIndex(130f, new List<float> { 200f, float.NaN, 120f }));
        Assert.Equal(-1, UiOrderedListModel.DropIndex(130f, new List<float> { float.NaN }));
        Assert.Equal(-1, UiOrderedListModel.DropIndex(float.NaN, new List<float> { 1f }));
    }

    [Fact]
    public void A_drag_previews_the_drop_and_moves_once_on_release()
    {
        var m = new UiOrderedListModel(Items("a", "b", "c", "d"), 0);
        Assert.True(m.BeginDrag(0));
        Assert.False(m.BeginDrag(1)); // one drag at a time
        Assert.True(m.DragTo(2));
        Assert.Equal(new[] { "b", "c", "a", "d" }, m.Display().Select(i => i.Id));
        Assert.Equal(new[] { "a", "b", "c", "d" }, m.Order()); // nothing moved yet
        Assert.True(m.EndDrag(out var change));
        Assert.Equal(new[] { "b", "c", "a", "d" }, m.Order());
        Assert.Equal(2, change.To);
        Assert.False(m.Dragging);

        Assert.True(m.BeginDrag(1));
        Assert.False(m.EndDrag(out _)); // dropped where it started
        Assert.False(m.BeginDrag(7)); // empty slot
    }

    // ---- Ordered list handle -------------------------------------------------------------------

    [Fact]
    public void Up_down_and_remove_buttons_reorder_and_raise_events()
    {
        var panel = NewPanel();
        var list = panel.OrderedList("targets", Items("a", "b", "c"));
        var moves = new List<UiListReorder>();
        var removed = new List<string>();
        list.OnReorder(moves.Add);
        list.OnRemove(i => removed.Add(i.Id));

        Click(Row("targets:row0"), UiWidgetLayout.CellDown);
        Assert.Equal(new[] { "b", "a", "c" }, list.Items.Select(i => i.Id));
        Assert.Single(moves);
        Assert.Equal("a", moves[0].Id);
        Assert.Equal(new[] { "b", "a", "c" }, moves[0].Order);

        Click(Row("targets:row0"), UiWidgetLayout.CellUp); // the top row cannot go up: nothing happens
        Assert.Single(moves);

        Click(Row("targets:row2"), UiWidgetLayout.CellRemove);
        Assert.Equal(new[] { "b", "a" }, list.Items.Select(i => i.Id));
        Assert.Equal(new[] { "c" }, removed);
        Assert.False(Row("targets:row2").Visible); // the emptied slot is hidden
        Assert.True(Row("targets:row1").Visible);
    }

    [Fact]
    public void Rows_are_rewritten_in_place_in_the_new_order()
    {
        var panel = NewPanel();
        var list = panel.OrderedList("targets", Items("a", "b", "c"));
        int rowsBefore = _runtime.Rows.Count;
        list.Move("c", 0);
        Assert.Equal(rowsBefore, _runtime.Rows.Count); // no row was made or destroyed
        var title = Row("targets:row0").Cells().Single(c => c.Key == UiWidgetLayout.CellTitle);
        Assert.Equal("Item c", title.Text);
    }

    [Fact]
    public void The_expander_opens_the_drawer_row_and_reports_it()
    {
        var panel = NewPanel();
        var list = panel.OrderedList("targets", Items("a", "b"));
        var opened = new List<(string, bool)>();
        list.OnExpand((i, on) => opened.Add((i.Id, on)));
        Assert.False(Row("targets:drawer1").Visible);
        Click(Row("targets:row1"), UiWidgetLayout.CellExpand);
        Assert.True(list.IsExpanded("b"));
        Assert.True(Row("targets:drawer1").Visible);
        Assert.Equal("Accepts ore", Row("targets:drawer1").Cells().Single(c => c.Key == UiWidgetLayout.CellDetail).Text);
        Assert.Equal(new[] { ("b", true) }, opened);
        Click(Row("targets:row0"), UiWidgetLayout.CellExpand); // a has no drawer
        Assert.False(list.IsExpanded("a"));
    }

    [Fact]
    public void A_grip_drag_moves_the_row_to_the_slot_under_the_pointer()
    {
        var panel = NewPanel();
        var list = panel.OrderedList("targets", Items("a", "b", "c"));
        var moves = new List<UiListReorder>();
        list.OnReorder(moves.Add);
        for (int i = 0; i < 3; i++) // slots stacked down the screen, 40 px apart (up is larger)
            Row("targets:row" + i).RectOverride = ScreenRect.FromCorners(0f, 400f - 40f * (i + 1), 600f, 400f - 40f * i);

        _runtime.Held = true;
        _runtime.Pointer = PointerSample.At(10f, 500f);
        _runtime.Press(Row("targets:row0"), UiWidgetLayout.CellGrip, UiPointerEventKind.Down);
        Assert.Single(_runtime.Ticks);
        Assert.True(((UiOrderedListHandle)list).HeldAt(0)); // the held row is lit (the one held-row style, UiTileState.Held)

        _runtime.Pointer = PointerSample.At(10f, 0f); // below every row
        _runtime.Tick();
        Assert.True(((UiOrderedListHandle)list).HeldAt(2)); // the preview put a in the last slot
        Assert.False(((UiOrderedListHandle)list).HeldAt(0));
        Assert.Equal(CellTone.Gold, Row("targets:row2").Cells().Single(c => c.Key == UiWidgetLayout.CellTitle).Tone);
        Assert.Equal("Item a", Row("targets:row2").Cells().Single(c => c.Key == UiWidgetLayout.CellTitle).Text);
        Assert.Empty(moves);

        _runtime.Press(Row("targets:row0"), UiWidgetLayout.CellGrip, UiPointerEventKind.Up);
        Assert.Equal(new[] { "b", "c", "a" }, list.Items.Select(i => i.Id));
        Assert.Single(moves);
        Assert.Empty(_runtime.Ticks); // the frame tick went with the drag
    }

    [Fact]
    public void A_release_missed_by_the_grip_still_drops_on_the_next_frame()
    {
        var panel = NewPanel();
        var list = panel.OrderedList("targets", Items("a", "b"));
        _runtime.Held = true;
        _runtime.Press(Row("targets:row1"), UiWidgetLayout.CellGrip, UiPointerEventKind.Down);
        _runtime.Pointer = PointerSample.At(10f, 999f);
        _runtime.Tick();
        _runtime.Held = false;
        _runtime.Tick();
        Assert.Equal(new[] { "b", "a" }, list.Items.Select(i => i.Id));
        Assert.Empty(_runtime.Ticks);
    }

    [Fact]
    public void A_right_press_or_a_hidden_list_starts_no_drag()
    {
        var panel = NewPanel();
        var list = panel.OrderedList("targets", Items("a", "b"));
        _runtime.Held = false; // not the primary button
        _runtime.Press(Row("targets:row0"), UiWidgetLayout.CellGrip, UiPointerEventKind.Down);
        Assert.Empty(_runtime.Ticks);
        list.Visible = false;
        _runtime.Held = true;
        _runtime.Press(Row("targets:row0"), UiWidgetLayout.CellGrip, UiPointerEventKind.Down);
        Assert.Empty(_runtime.Ticks);
    }

    [Fact]
    public void Dispose_releases_every_row_and_the_drag_tick()
    {
        var panel = NewPanel();
        var list = panel.OrderedList("targets", Items("a", "b"));
        _runtime.Held = true;
        _runtime.Press(Row("targets:row0"), UiWidgetLayout.CellGrip, UiPointerEventKind.Down);
        list.Dispose();
        Assert.Empty(_runtime.Ticks);
        Assert.True(_runtime.Rows.Where(r => r.Text.StartsWith("targets:", StringComparison.Ordinal)).All(r => r.Released));
        Assert.Equal(UiHandleState.Disposed, list.State);
    }

    [Fact]
    public void The_footer_says_empty_and_overflow()
    {
        var panel = NewPanel();
        var list = panel.OrderedList("t", Items("a", "b", "c"), new UiOrderedListOptions { Capacity = 2 });
        Assert.Equal(1, list.Overflow);
        Assert.True(Row("t:footer").Visible);
        Assert.Contains("1 more item", Row("t:footer").Cells()[0].Text);
        list.SetItems(Array.Empty<UiListItem>());
        Assert.Equal("Nothing here yet.", Row("t:footer").Cells()[0].Text);
    }

    [Fact]
    public void On_a_runtime_that_cannot_draw_the_list_keeps_state()
    {
        UiApi.SetRuntimeForTests(new CellRowTests.PlainRuntime());
        var panel = NewPanel();
        var list = panel.OrderedList("t", Items("a", "b"));
        Assert.True(list.Move("b", 0));
        Assert.Equal(new[] { "b", "a" }, list.Items.Select(i => i.Id));
    }

    // ---- Picker -------------------------------------------------------------------------------

    private static List<UiPickerEntry> Entries() => new()
    {
        new UiPickerEntry("ore", "Iron Ore") { Section = "Resources", ItemPrefab = 1 },
        new UiPickerEntry("ingot", "Iron Ingot") { Section = "Resources" },
        new UiPickerEntry("dark", "Dark Iron Ingot") { Section = "Resources", DisabledReason = "Locked" },
        new UiPickerEntry("furnace", "Furnace") { Section = "Stations", Note = "plot A" },
        new UiPickerEntry("chest", "Iron chest") { Section = "Stations", Keywords = new[] { "storage" } },
    };

    [Theory]
    [InlineData("Iron Ore", 0)]
    [InlineData("iron", 1)]
    [InlineData("ingot", 2)]
    [InlineData("ir in", 3)]
    [InlineData("ron", 4)]
    public void Rank_prefers_exact_then_prefix_then_word_then_all_words_then_substring(string query, int rank)
    {
        var e = new UiPickerEntry("x", query == "ingot" || query == "ir in" ? "Iron Ingot" : "Iron Ore");
        Assert.Equal(rank, UiPickerModel.Rank(e, UiPickerModel.Fold(query)));
    }

    [Fact]
    public void Matching_folds_case_accents_and_keywords()
    {
        var e = new UiPickerEntry("x", "Crème Brûlée") { Keywords = new[] { "Dessert" } };
        Assert.Equal(1, UiPickerModel.Rank(e, UiPickerModel.Fold("creme")));
        Assert.Equal(5, UiPickerModel.Rank(e, UiPickerModel.Fold("dess")));
        Assert.Equal(-1, UiPickerModel.Rank(e, UiPickerModel.Fold("steak")));
    }

    [Fact]
    public void Sections_keep_the_callers_order_and_grayed_entries_follow_their_rank()
    {
        var m = new UiPickerModel(Entries(), multi: false, rows: 8, selected: null) { Query = "iron" };
        Assert.Equal(new[] { "ore", "ingot", "dark", "chest" }, m.Matches.Select(e => e.Id));
        var lines = m.Lines(out int hidden);
        Assert.Equal(0, hidden);
        Assert.Equal(new[] { "# Resources", "Iron Ore", "Iron Ingot", "Dark Iron Ingot", "# Stations", "Iron chest" },
            lines.Select(l => l.ToString()));
    }

    [Fact]
    public void Lines_never_end_on_a_heading_and_count_what_did_not_fit()
    {
        var m = new UiPickerModel(Entries(), multi: false, rows: 4, selected: null);
        var lines = m.Lines(out int hidden);
        Assert.Equal(4, lines.Count);
        Assert.Equal(UiPickerLineKind.Entry, lines[^1].Kind);
        Assert.Equal(2, hidden); // # Resources + 3 entries; Stations' two did not fit
        Assert.Contains("5 entries", m.Status(hidden));
    }

    [Fact]
    public void Autofill_suggests_the_best_pickable_prefix_match()
    {
        var m = new UiPickerModel(Entries(), multi: false, rows: 8, selected: null) { Query = "iron i" };
        Assert.Equal("Iron Ingot", m.Suggestion);
        m.Query = "dark";
        Assert.Equal("", m.Suggestion); // the only prefix match is grayed
        m.Query = "Furnace";
        Assert.Equal("", m.Suggestion); // nothing left to complete
        m.Query = "";
        Assert.Equal("", m.Suggestion);
    }

    [Fact]
    public void Single_select_replaces_multi_select_flips_and_grayed_entries_refuse()
    {
        var single = new UiPickerModel(Entries(), multi: false, rows: 8, selected: new[] { "ore" });
        Assert.True(single.Click("furnace"));
        Assert.Equal(new[] { "furnace" }, single.Selected);
        Assert.False(single.Click("dark"));

        var multi = new UiPickerModel(Entries(), multi: true, rows: 8, selected: null);
        Assert.True(multi.Click("ore"));
        Assert.True(multi.Click("chest"));
        Assert.True(multi.Click("ore"));
        Assert.Equal(new[] { "chest" }, multi.Selected);
        multi.SetEntries(Entries().Where(e => e.Id != "chest"));
        Assert.Empty(multi.Selected); // the selected entry left
    }

    [Fact]
    public void Picker_rows_filter_as_typed_and_a_click_picks()
    {
        var panel = NewPanel();
        var picker = panel.Picker("pick", Entries());
        IReadOnlyList<UiPickerEntry>? picked = null;
        picker.OnPicked(p => picked = p);
        picker.Query = "furn";
        Assert.Equal("Stations", Row("pick:line0").Cells().Single(c => c.Key == UiWidgetLayout.CellTitle).Text);
        Assert.Equal("Furnace", Row("pick:line1").Cells().Single(c => c.Key == UiWidgetLayout.CellTitle).Text);
        Assert.False(Row("pick:line2").Visible);
        Row("pick:line1").PointerClick();
        Assert.NotNull(picked);
        Assert.Equal("furnace", picked!.Single().Id);
        Assert.True(Row("pick:line1").Selected);
        Assert.Equal(new[] { "furnace" }, picker.Selected);
    }

    [Fact]
    public void Hovering_a_grayed_row_shows_its_reason()
    {
        var panel = NewPanel();
        var handle = (UiPickerHandle)panel.Picker("pick", Entries(), new UiPickerOptions { Rows = 8 });
        handle.Query = "dark";
        Row("pick:line1").PointerClick(); // grayed: nothing picked
        Assert.Empty(handle.Selected);
        _runtime.Hover(Row("pick:line1"), true, handle);
        Assert.Equal(("Dark Iron Ingot", "Locked"), _runtime.Tooltip);
        _runtime.Hover(Row("pick:line1"), false, handle);
        Assert.Null(_runtime.Tooltip);
    }

    [Fact]
    public void Accepting_the_suggestion_completes_and_selects()
    {
        var panel = NewPanel();
        var picker = panel.Picker("pick", Entries());
        picker.Query = "iron o";
        Assert.Equal("Iron Ore", picker.Suggestion);
        Assert.True(picker.AcceptSuggestion());
        Assert.Equal("Iron Ore", picker.Query);
        Assert.Equal(new[] { "ore" }, picker.Selected);
        Assert.True(_runtime.ItemIconAsks >= 1); // the item's own icon was asked for (the runtime caches it)
    }

    // ---- Tabs -----------------------------------------------------------------------------------

    [Fact]
    public void Tabs_switch_their_content_and_badges_read_the_count()
    {
        var panel = NewPanel();
        var tabs = panel.Tabs("t", new[] { "Targets", "Rules" });
        var a = tabs.Add(0, panel.Label("first"));
        var b = tabs.Add(1, panel.Label("second"));
        var changes = new List<int>();
        tabs.OnChanged(changes.Add);
        Assert.True(a.Visible);
        Assert.False(b.Visible);
        Click(Row("t:tabs"), "tab1");
        Assert.Equal(1, tabs.Selected);
        Assert.False(a.Visible);
        Assert.True(b.Visible);
        Assert.Equal(new[] { 1 }, changes);
        tabs.SetBadge(0, 120);
        var badge = Row("t:tabs").Cells().Single(c => c.Key == "badge0");
        Assert.Equal("99+", badge.Text);
        Assert.True(badge.Visible);
        tabs.SetBadge(0, 0);
        Assert.False(Row("t:tabs").Cells().Single(c => c.Key == "badge0").Visible);
        var chip = Row("t:tabs").Cells().Single(c => c.Key == "tab1");
        Assert.Equal(CellTone.ChipOn, chip.Tone);
    }

    [Theory]
    [InlineData(0, "")]
    [InlineData(-3, "")]
    [InlineData(7, "7")]
    [InlineData(99, "99")]
    [InlineData(100, "99+")]
    public void Badge_text(int count, string text) => Assert.Equal(text, UiBadge.Text(count));

    // ---- Layout ---------------------------------------------------------------------------------

    [Fact]
    public void Every_widget_row_fits_its_cells_at_every_checked_width()
    {
        var rows = new List<List<CellSpec>>
        {
            UiWidgetLayout.ListRowCells(reorder: true, remove: true, expand: true, icon: true),
            UiWidgetLayout.ListRowCells(reorder: false, remove: true, expand: false, icon: false),
            UiPickerLayout.LineCells(multi: true, icon: true),
            UiPickerLayout.SearchCells("Search"),
            UiTabsLayout.Cells(new[] { "Targets", "Rules", "Advanced" }, 0),
        };
        foreach (var cells in rows)
        {
            foreach (var c in cells.Where(c => c.Kind == CellKind.Badge)) { c.Text = "12"; c.Visible = true; }
            foreach (var c in cells.Where(c => c.Key == UiWidgetLayout.CellTitle)) c.Text = "Storage chest on the north wall";
            foreach (var c in cells.Where(c => c.Key == UiWidgetLayout.CellNote)) c.Text = "plot A";
            foreach (float width in UiWidgetLayout.CheckedRowWidths)
            {
                var placed = MenuPanelLayout.CellColumns(width, cells);
                float left = -width / 2f, right = width / 2f;
                foreach (var p in placed.Where(p => p.Visible))
                {
                    Assert.True(p.Left >= left - .01f && p.Right <= right + .01f, p + " leaves the " + width + " row");
                    Assert.True(p.Width >= CellLayout.MinWidthOf(cells[p.Index]) - .01f, p + " under its floor at " + width);
                }

                for (int i = 0; i < placed.Length; i++)
                    for (int j = i + 1; j < placed.Length; j++)
                    {
                        // An overlay is drawn inside its own target (a tab's badge inside its chip); nothing else may overlap.
                        if (cells[j].OverlayOf == cells[i].Key || cells[i].OverlayOf == cells[j].Key) continue;
                        Assert.False(CellLayout.Overlap(placed[i], placed[j]), placed[i] + " overlaps " + placed[j] + " at " + width);
                    }
            }
        }
    }

    [Fact]
    public void Glyph_buttons_line_up_from_row_to_row()
    {
        var withBadge = UiWidgetLayout.ListRowCells(true, true, true, true);
        withBadge.Single(c => c.Kind == CellKind.Badge).Text = "12";
        var without = UiWidgetLayout.ListRowCells(true, true, true, true);
        without.Single(c => c.Kind == CellKind.Badge).Visible = false;
        var a = MenuPanelLayout.CellColumns(760f, withBadge);
        var b = MenuPanelLayout.CellColumns(760f, without);
        foreach (var key in new[] { UiWidgetLayout.CellUp, UiWidgetLayout.CellDown, UiWidgetLayout.CellExpand, UiWidgetLayout.CellRemove })
            Assert.Equal(a.Single(p => p.Key == key).Left, b.Single(p => p.Key == key).Left, 2);
    }

    [Theory]
    [InlineData(1920f, 1080f)]
    [InlineData(2560f, 1440f)]
    [InlineData(3840f, 2160f)]
    [InlineData(3440f, 1440f)]
    public void The_tooltip_stays_on_the_canvas_beside_its_anchor(float w, float h)
    {
        var size = UiTooltipLayout.Size(500f, 20f, 300f, 60f);
        Assert.Equal(UiTooltipLayout.MaxWidth, size.Width);
        // Right of an anchor in the middle.
        var mid = UiTooltipLayout.Place(400f, 400f, 600f, 440f, size.Width, size.Height, w, h);
        Assert.Equal(600f + UiTooltipLayout.Gap, mid.X);
        Assert.Equal(440f - size.Height, mid.Y, 2);
        // Flipped left of an anchor at the right edge.
        var right = UiTooltipLayout.Place(w - 120f, 400f, w - 20f, 440f, size.Width, size.Height, w, h);
        Assert.True(right.X + size.Width <= w - 120f, "did not flip left");
        // Clamped inside near the bottom-left corner.
        var corner = UiTooltipLayout.Place(0f, 0f, 30f, 10f, size.Width, size.Height, w, h);
        Assert.True(corner.Y >= UiTooltipLayout.Margin && corner.X >= UiTooltipLayout.Margin);
        Assert.True(corner.X + size.Width <= w - UiTooltipLayout.Margin && corner.Y + size.Height <= h - UiTooltipLayout.Margin);
    }

    // ---- The Widgets sample page ---------------------------------------------------------------

    private sealed class FakeHud : Progenitor.ControlsSample.IWidgetsHud
    {
        public readonly List<string> Calls = new();
        public void Notify(string text, string detail, UiNoticeKind kind) => Calls.Add("notify " + kind);
        public void Pick(Action<string> done) { Calls.Add("pick"); done("Container ContainerMenu (1:1)"); }
        public string LocatePicked() { Calls.Add("locate"); return "pinged"; }
    }

    [Fact]
    public void The_widgets_page_builds_every_widget_and_reports_their_events()
    {
        var panel = NewPanel();
        var log = new List<string>();
        var demo = new Progenitor.ControlsSample.WidgetsDemo(log.Add, new FakeHud());
        demo.Build(panel);
        Assert.NotNull(demo.List);
        Assert.NotNull(demo.Picker);
        Click(Row(Progenitor.ControlsSample.WidgetsDemo.ListKey + ":row0"), UiWidgetLayout.CellDown);
        Assert.StartsWith("list reorder chest-a 0->1", demo.LastStatus);
        demo.Picker!.Query = "cryst";
        Assert.True(demo.Picker.AcceptSuggestion());
        Assert.Equal("picker = crystal", demo.LastStatus);
        Assert.Contains(log, l => l.StartsWith(Progenitor.ControlsSample.ControlsDemo.LogPrefix + "widget ", StringComparison.Ordinal));
        // Only the first tab's content is shown.
        Assert.True(demo.List!.Visible);
        Assert.False(demo.Picker.Visible);
    }

    [Fact]
    public void The_toy_highlighter_and_checker_mark_the_sample_script()
    {
        var spans = Progenitor.ControlsSample.WidgetsDemo.Highlight(Progenitor.ControlsSample.WidgetsDemo.SampleScript);
        Assert.Contains(spans, s => s.Color == "#7D7378"); // the comment
        Assert.All(spans, s => Assert.True(s.End <= Progenitor.ControlsSample.WidgetsDemo.SampleScript.Length));
        var errors = Progenitor.ControlsSample.WidgetsDemo.Check(Progenitor.ControlsSample.WidgetsDemo.SampleScript);
        Assert.Equal(new[] { 4 }, errors.Select(e => e.Line));
    }

    /// <summary>Every public widget and every <c>ctx.Hud</c> call has a demo on the Widgets page.</summary>
    [Fact]
    public void Every_widget_and_hud_call_is_demoed()
    {
        // P7b: the page is partial; each widget's demo may live in its own WidgetsDemo.<Name>.cs.
        string demo = string.Concat(System.IO.Directory
            .GetFiles(System.IO.Path.Combine(SourcePin.RepoRoot(), "samples", "Progenitor.ControlsSample"), "WidgetsDemo*.cs")
            .OrderBy(p => p, StringComparer.Ordinal)
            .Select(System.IO.File.ReadAllText));
        string plugin = SourcePin.ReadFile("samples", "Progenitor.ControlsSample", "Plugin.cs");
        foreach (var m in typeof(UiWidgets).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                     .Select(m => m.Name).Distinct())
            Assert.True(demo.Contains("." + m + "(", StringComparison.Ordinal), "WidgetsDemo does not demo UiWidgets." + m);
        foreach (var m in new[] { "Notify", "PickInWorld", "Locate" })
            Assert.True(plugin.Contains("Hud." + m + "(", StringComparison.Ordinal), "the Controls sample does not call ctx.Hud." + m);
    }

    [Theory]
    [InlineData(1920f, 1080f)]
    [InlineData(3840f, 2160f)]
    public void A_full_width_row_gets_the_tooltip_under_it_or_over_it_never_on_it(float w, float h)
    {
        var size = UiTooltipLayout.Size(200f, 20f, 300f, 60f);
        // Near the bottom: no room under the row, so over it.
        var low = UiTooltipLayout.Place(0f, 20f, w, 60f, size.Width, size.Height, w, h);
        Assert.True(low.Y >= 60f, "covers the row near the bottom");
        // Near the top: under it.
        var high = UiTooltipLayout.Place(0f, h - 60f, w, h - 20f, size.Width, size.Height, w, h);
        Assert.True(high.Y + size.Height <= h - 60f, "covers the row near the top");
        // Both stay on the canvas.
        foreach (var p in new[] { low, high })
            Assert.True(p.Y >= UiTooltipLayout.Margin && p.Y + size.Height <= h - UiTooltipLayout.Margin);
    }

    [Fact]
    public void The_picker_filters_a_full_item_list_without_refolding_per_keystroke()
    {
        var entries = Enumerable.Range(0, 4000)
            .Select(i => new UiPickerEntry("item" + i, "Item Ingredient Number " + i) { Section = "S" + (i % 12), Keywords = new[] { "prefab_" + i } })
            .ToList();
        var m = new UiPickerModel(entries, multi: false, rows: 8, selected: null);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        foreach (var q in new[] { "i", "it", "ite", "item", "item ", "item i", "item in", "number 39", "prefab_3999", "zzz" })
            m.Query = q;
        watch.Stop();
        m.Query = "prefab_3999";
        Assert.Equal("item3999", Assert.Single(m.Matches).Id); // keywords still match, folded once
        Assert.True(watch.ElapsedMilliseconds < 2000, "10 queries over 4,000 entries took " + watch.ElapsedMilliseconds + " ms");
    }

    [Fact]
    public void The_icon_and_expander_columns_follow_set_items()
    {
        var panel = NewPanel();
        var list = panel.OrderedList("t", new[] { new UiListItem("a", "A") });
        Assert.False(Row("t:row0").Cells().Single(c => c.Key == UiWidgetLayout.CellIcon).Visible);
        Assert.False(Row("t:row0").Cells().Single(c => c.Key == UiWidgetLayout.CellExpand).Visible);
        list.SetItems(new[] { new UiListItem("a", "A") { ItemPrefab = 5, Detail = "more" } });
        Assert.True(Row("t:row0").Cells().Single(c => c.Key == UiWidgetLayout.CellIcon).Visible);
        Assert.True(Row("t:row0").Cells().Single(c => c.Key == UiWidgetLayout.CellExpand).Visible);
    }

    [Fact]
    public void Closing_the_panel_mid_drag_ends_the_drag_without_an_event()
    {
        var panel = NewPanel();
        var list = panel.OrderedList("t", Items("a", "b"));
        int events = 0;
        list.OnReorder(_ => events++);
        _runtime.Held = true;
        var grip = _runtime.Watches[(Row("t:row0"), UiWidgetLayout.CellGrip)];
        grip(new UiPointerEvent(UiPointerEventKind.Down, true));
        Assert.Single(_runtime.Ticks);
        panel.Dispose(); // the plugin unloads, Ui.RemoveAll, or the page closes
        Assert.Empty(_runtime.Ticks);
        Assert.True(list.IsDisposed);
        grip(new UiPointerEvent(UiPointerEventKind.Up, false)); // a late release
        Assert.Equal(0, events);
    }

    [Fact]
    public void A_shown_tooltip_asks_to_hide_once_its_panel_is_hidden()
    {
        var panel = NewPanel();
        panel.Show();
        var handle = (UiPickerHandle)panel.Picker("pick", Entries());
        handle.Query = "dark";
        _runtime.Hover(Row("pick:line1"), true, handle);
        Assert.NotNull(_runtime.TooltipStillShown);
        Assert.True(_runtime.TooltipStillShown!());
        panel.Hide(); // Escape's sweep: no pointer exit reaches the row
        Assert.False(_runtime.TooltipStillShown());
    }

    [Fact]
    public void Widget_teardowns_run_once_newest_first()
    {
        var order = new List<string>();
        UiWidgetLifetime.Register("a", () => order.Add("a"));
        UiWidgetLifetime.Register("b", () => order.Add("b"));
        UiWidgetLifetime.Register("a", () => order.Add("a2")); // a name registers once
        UiWidgetLifetime.Register("c", () => throw new InvalidOperationException("boom"));
        Assert.Equal(2, UiWidgetLifetime.Uninstall());
        Assert.Equal(new[] { "b", "a" }, order);
        Assert.Equal(0, UiWidgetLifetime.Uninstall());
    }
}

/// <summary>P7: a fake runtime that draws cell rows (<see cref="CellRowTests.FakeCellRow"/>) and implements the widget half.</summary>
internal sealed class WidgetRuntimeFake : CellRowTests.PlainRuntime, IWidgetRuntime
{
    public readonly List<CellRowTests.FakeCellRow> Rows = new();
    public readonly List<Action> Ticks = new();
    public readonly Dictionary<(CellRowTests.FakeCellRow, string), Action<UiPointerEvent>> Watches = new();
    public PointerSample Pointer = PointerSample.At(0f, 0f);
    public bool Held;
    public (string Title, string Body)? Tooltip;
    public int ItemIconAsks;

    public override IRuntimeCellRow? AddCellRow(IRuntimePanel panel, CellRowSpec spec, bool header)
    {
        var row = new CellRowTests.FakeCellRow(null, spec, header);
        Rows.Add(row);
        return row;
    }

    public Func<bool>? TooltipStillShown;

    public void ShowTooltip(ScreenRect anchor, string title, string body, Func<bool>? stillShown = null)
    {
        Tooltip = (title, body);
        TooltipStillShown = stillShown;
    }

    public void HideTooltip() => Tooltip = null;

    public IDisposable? WatchCell(IRuntimeCellRow row, string cellKey, Action<UiPointerEvent> handler, object? owner)
    {
        var key = ((CellRowTests.FakeCellRow)row, cellKey);
        Watches[key] = handler;
        return new ActionDisposable(() => Watches.Remove(key));
    }

    public IDisposable? WatchHover(IRuntimeWidget widget, Action<bool> onHover, object? owner) => null;

    public IDisposable EveryFrame(Action tick)
    {
        Ticks.Add(tick);
        return new ActionDisposable(() => Ticks.Remove(tick));
    }

    public string DrawableGlyph(string preferred, string fallback) => preferred;

    public PointerSample ReadPointer() => Pointer;

    public bool PrimaryHeld() => Held;

    public object? ItemIcon(int prefab)
    {
        ItemIconAsks++;
        return null;
    }

    public string? ItemName(int prefab) => null;

    public void Tick()
    {
        foreach (var t in Ticks.ToArray()) t();
    }

    public void Press(CellRowTests.FakeCellRow row, string cell, UiPointerEventKind kind) =>
        Watches[(row, cell)](new UiPointerEvent(kind, Held));

    /// <summary>Drive a row's hover the way the kit row's pointer slot does (the picker's private handler).</summary>
    public void Hover(CellRowTests.FakeCellRow row, bool on, UiPickerHandle picker)
    {
        int slot = int.Parse(row.Text.Substring(row.Text.LastIndexOf("line", StringComparison.Ordinal) + 4));
        picker.HoverSlotForTests(slot, on);
    }
}
