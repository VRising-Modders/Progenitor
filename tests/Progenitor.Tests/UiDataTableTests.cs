using System;
using System.Collections.Generic;
using System.Linq;
using Progenitor.Ui;
using Xunit;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.Tests;

/// <summary>
/// P7b: the data table (<see cref="UiWidgets.DataTable"/>) through a fake runtime that draws cell rows, reports the click
/// modifiers and the focused value box, and hands out a fake scroll bar; plus its game-free model, viewport and layout.
/// Every assertion is an outcome: the order after a header click, the cells a slot draws, the event a plugin received.
/// </summary>
[Collection("UiStatic")]
public sealed class UiDataTableTests : IDisposable
{
    private readonly TableRuntimeFake _runtime = new();
    private readonly object _owner = new();
    private readonly List<IUiPanel> _panels = new();
    private double _now;

    public UiDataTableTests()
    {
        UiApi.ResetForTests();
        UiWidgetLifetime.Uninstall();
        UiTerms.Teardown();
        UiHelpStore.UseForTests(_help);
        UiApi.SetRuntimeForTests(_runtime);
    }

    private readonly UiHelpMemoryStore _help = new();

    public void Dispose()
    {
        foreach (var p in _panels) p.Dispose();
        UiApi.ResetForTests();
        // The wheel claims register their teardown; run it so no registration leaks into another suite.
        UiWidgetLifetime.Uninstall();
        UiWheelClaims.Reset();
        UiTerms.Teardown();
        UiHelpStore.UseForTests(null);
    }

    private IUiPanel NewPanel()
    {
        var panel = UiApi.Panel("Table", _owner, "example.table");
        _panels.Add(panel);
        return panel;
    }

    private CellRowTests.FakeCellRow Row(string key) => _runtime.Rows.Single(r => r.Text == key);

    private static CellSpec Cell(CellRowTests.FakeCellRow row, string key) => row.Cells().Single(c => c.Key == key);

    private static void Click(CellRowTests.FakeCellRow row, string cell) =>
        UiSlotTable.BootstrapDispatch(row.WiredIdOf(cell), (byte)UiSlotKind.Click, 0f, false, null);

    private static void Type(CellRowTests.FakeCellRow row, string cell, string text) =>
        UiSlotTable.BootstrapDispatch(row.WiredIdOf(cell), (byte)UiSlotKind.Text, 0f, false, text);

    private static List<UiTableColumn> Columns() => new()
    {
        new UiTableColumn("name", "Name") { Hideable = false },
        new UiTableColumn("total", "Total", UiTableColumnKind.Number) { Width = 80f, Subtotal = UiTableSubtotal.Sum },
        new UiTableColumn("cap", "Cap", UiTableColumnKind.Number) { Width = 90f, Editable = true },
        new UiTableColumn("note", "Note") { Width = 120f },
    };

    private static UiTableRow R(string id, string name, double? total, double? cap = null, params string[] groups) =>
        new UiTableRow(id) { Groups = groups }.Text("name", name).Number("total", total).Number("cap", cap).Text("note", "n" + id);

    private static List<UiTableRow> Sample() => new()
    {
        R("a", "Iron", 5, 100, "Ores"),
        R("b", "copper", 3, null, "Ores"),
        R("c", "Ash", 5, 50, "Alchemy"),
        R("d", "Bone", null, 10, "Alchemy", "Ores"),
        R("e", "Clay", 3, null),
    };

    private UiDataTableOptions Options(bool multi = true, int visible = 4) => new()
    {
        MultiSelect = multi,
        VisibleRows = visible,
        Clock = () => _now,
    };

    private UiDataTableModel Model(IEnumerable<UiTableRow>? rows = null, UiDataTableOptions? options = null)
    {
        var m = new UiDataTableModel(Columns(), options ?? Options());
        m.SetRows(rows ?? Sample());
        return m;
    }

    private static string[] Ids(UiDataTableModel m) =>
        m.Lines().Where(l => l.Kind == UiTableLineKind.Row).Select(l => l.Row!.Id).ToArray();

    private (IUiDataTable Table, UiDataTableHandle Handle) NewTable(IEnumerable<UiTableRow>? rows = null, UiDataTableOptions? options = null)
    {
        var panel = NewPanel();
        var table = panel.DataTable("t", Columns(), options ?? Options());
        table.SetRows(rows ?? Sample());
        return (table, (UiDataTableHandle)table);
    }

    private static List<UiTableRow> Many(int n) =>
        Enumerable.Range(0, n).Select(i => R("r" + i.ToString("000"), "Item " + i.ToString("000"), i % 10, i)).ToList();

    // ---- Sort ---------------------------------------------------------------------------------------

    [Fact]
    public void A_header_click_sorts_by_one_column_and_a_second_click_flips_it()
    {
        var m = Model();
        Assert.Equal(new[] { "a", "b", "c", "d", "e" }, Ids(m)); // no sort: the caller's order
        Assert.True(m.ClickSort("name", add: false));
        Assert.Equal(new[] { "c", "d", "e", "b", "a" }, Ids(m)); // case-insensitive A to Z
        Assert.True(m.ClickSort("name", add: false));
        Assert.Equal(new[] { "a", "b", "e", "d", "c" }, Ids(m));
        Assert.False(m.Sort[0].Ascending);
        Assert.True(m.ClickSort("total", add: false)); // a plain click replaces the key
        Assert.Single(m.Sort);
        Assert.Equal("total", m.Sort[0].Column);
    }

    [Fact]
    public void Shift_click_adds_the_next_key_flips_an_existing_one_and_ties_keep_the_callers_order()
    {
        var m = Model();
        m.ClickSort("total", add: false);
        // Ties on total (3: b, e; 5: a, c) keep the caller's order; the empty total sorts last.
        Assert.Equal(new[] { "b", "e", "a", "c", "d" }, Ids(m));
        Assert.True(m.ClickSort("name", add: true));
        Assert.Equal(new[] { new UiTableSortKey("total", true), new UiTableSortKey("name", true) }, m.Sort);
        Assert.Equal(new[] { "e", "b", "c", "a", "d" }, Ids(m)); // within 3: Clay, copper; within 5: Ash, Iron
        Assert.True(m.ClickSort("name", add: true)); // flips the second key in place
        Assert.Equal(new[] { new UiTableSortKey("total", true), new UiTableSortKey("name", false) }, m.Sort);
        Assert.Equal(new[] { "b", "e", "a", "c", "d" }, Ids(m));
        Assert.False(m.ClickSort("nope", add: true));
    }

    [Fact]
    public void Empty_cells_sort_last_in_both_directions_unless_the_column_says_otherwise()
    {
        var m = Model();
        m.SetSort(new[] { new UiTableSortKey("cap", true) });
        Assert.Equal(new[] { "d", "c", "a", "b", "e" }, Ids(m)); // 10, 50, 100, then unlimited
        m.SetSort(new[] { new UiTableSortKey("cap", false) });
        Assert.Equal(new[] { "a", "c", "d", "b", "e" }, Ids(m)); // still last
        var cols = Columns();
        cols[2].NullsLast = false;
        var m2 = new UiDataTableModel(cols, Options());
        m2.SetRows(Sample());
        m2.SetSort(new[] { new UiTableSortKey("cap", true) });
        Assert.Equal(new[] { "b", "e", "d", "c", "a" }, Ids(m2)); // empty is the smallest
    }

    [Fact]
    public void A_text_cell_with_a_value_sorts_by_it()
    {
        var cols = new List<UiTableColumn> { new("name", "Name"), new("status", "Status") };
        var m = new UiDataTableModel(cols, Options());
        m.SetRows(new[]
        {
            new UiTableRow("x").Text("name", "x").Set("status", new UiTableCell("Short") { Value = 4 }),
            new UiTableRow("y").Text("name", "y").Set("status", new UiTableCell("At cap") { Value = 2 }),
            new UiTableRow("z").Text("name", "z").Set("status", new UiTableCell("")),
        });
        m.SetSort(new[] { new UiTableSortKey("status", false) });
        Assert.Equal(new[] { "x", "y", "z" }, Ids(m)); // by severity, blank last
    }

    [Fact]
    public void One_key_draws_the_arrow_and_several_keys_add_the_order_digit_in_the_same_drawn_gold_mark()
    {
        var (table, handle) = NewTable();
        var sorts = new List<string>();
        table.OnSortChanged(s => sorts.Add(string.Join(",", s)));
        var header = Row("t:header");
        Click(header, "c:total");
        Assert.Equal("sort-up", handle.HeaderMarks()["total"]);
        Assert.Equal("", handle.HeaderMarks()["name"]);
        Assert.Equal(("sort-up", MenuPalette.HeaderSorted), _runtime.MarkOf(header, "c:total"));
        Assert.Equal("Total", Cell(header, "c:total").Text); // the caption stays words
        Assert.Null(header.SortedKey); // the header's font indicator is never used
        _runtime.Modifiers = UiClickModifiers.Shift;
        Click(header, "c:name");
        _runtime.Modifiers = UiClickModifiers.None;
        Assert.Equal(("sort-up:1", MenuPalette.HeaderSorted), _runtime.MarkOf(header, "c:total"));
        Assert.Equal(("sort-up:2", MenuPalette.HeaderSorted), _runtime.MarkOf(header, "c:name"));
        Assert.Equal("Name", Cell(header, "c:name").Text);
        _runtime.Modifiers = UiClickModifiers.Shift;
        Click(header, "c:total");
        _runtime.Modifiers = UiClickModifiers.None;
        Assert.Equal("sort-down:1", _runtime.MarkOf(header, "c:total").Mark);
        table.Sort = Array.Empty<UiTableSortKey>();
        Assert.Equal("", _runtime.MarkOf(header, "c:total").Mark);
        Assert.Equal(new[] { "total asc", "total asc,name asc", "total desc,name asc", "" }, sorts);
        Assert.NotEqual(0, header.WiredIdOf("c:note")); // a sortable caption is clickable
    }

    [Fact]
    public void The_marks_are_drawn_whatever_the_font_has_and_no_ascii_stand_in_appears()
    {
        _runtime.AsciiOnly = true; // a font without ▲ ▼ ◐
        var (table, handle) = NewTable();
        table.Sort = new[] { new UiTableSortKey("total", false), new UiTableSortKey("name", true) };
        Assert.Equal("sort-down:1", handle.HeaderMarks()["total"]);
        Assert.Equal("Total", Cell(Row("t:header"), "c:total").Text);
        table.Edit("a", "cap", "9");
        Assert.Equal("9", Cell(Row("t:row1"), "c:cap").Text); // sorted: a is second (total 5 desc, then by name: Ash, Iron)
        Assert.All(_runtime.Rows.SelectMany(r => r.Cells()), c => Assert.DoesNotMatch(@"(^| )[\^v\-\\|/][0-9]?$", c.Text));
    }

    [Theory]
    [InlineData("", 0, 0, 0)]
    [InlineData("sort-up", 1, 0, 0)]
    [InlineData("sort-down:3", 2, 3, 0)]
    [InlineData("spinner:2", 3, 0, 2)]
    public void Mark_names_read_back_to_the_same_mark(string name, int shape, int digit, int frame)
    {
        Assert.True(UiDrawnMark.TryParse(name, out var mark));
        Assert.Equal(((UiMarkShape)shape, digit, frame), (mark.Shape, mark.Digit, mark.Frame));
        Assert.Equal(name, mark.Name);
        Assert.False(UiDrawnMark.TryParse("sort-up:0", out _));
        Assert.False(UiDrawnMark.TryParse("spinner:4", out _));
        Assert.False(UiDrawnMark.TryParse("arrow", out _));
    }

    [Fact]
    public void The_sort_mark_follows_the_sort_state()
    {
        var none = Array.Empty<UiTableSortKey>();
        var one = new[] { new UiTableSortKey("total", false) };
        var two = new[] { new UiTableSortKey("name", true), new UiTableSortKey("total", false) };
        Assert.True(UiDataTableLayout.SortMark(none, "total").IsNone);
        Assert.Equal("sort-down", UiDataTableLayout.SortMark(one, "total").Name);
        Assert.True(UiDataTableLayout.SortMark(one, "name").IsNone);
        Assert.Equal("sort-up:1", UiDataTableLayout.SortMark(two, "name").Name);
        Assert.Equal("sort-down:2", UiDataTableLayout.SortMark(two, "total").Name);
    }

    private static float[,] Paint(UiDrawnMark mark)
    {
        var (w, h) = UiDrawnMarkArt.TextureSize(mark);
        var a = new float[w, h];
        for (int x = 0; x < w; x++)
            for (int y = 0; y < h; y++) a[x, y] = UiDrawnMarkArt.Alpha(mark, x, y);
        return a;
    }

    private static float Sum(float[,] a, int x0, int x1, int y0, int y1)
    {
        float s = 0f;
        for (int x = x0; x < x1; x++)
            for (int y = y0; y < y1; y++) s += a[x, y];
        return s;
    }

    [Fact]
    public void The_arrows_point_their_way_and_the_digit_sits_at_the_right()
    {
        var up = Paint(UiDrawnMark.Sort(true));
        var down = Paint(UiDrawnMark.Sort(false));
        int h = UiDrawnMarkArt.TextureHeight, w = UiDrawnMarkArt.ArrowWidth;
        // Up: wide at the bottom, a point at the top; down: the mirror image.
        Assert.True(Sum(up, 0, w, 0, h / 2) > 2f * Sum(up, 0, w, h / 2, h));
        Assert.True(Sum(down, 0, w, h / 2, h) > 2f * Sum(down, 0, w, 0, h / 2));
        for (int x = 0; x < w; x++)
            for (int y = 0; y < h; y++) Assert.Equal(up[x, y], down[x, h - 1 - y], 3);
        Assert.Equal(1f, up[w / 2, h / 2], 2); // solid inside
        Assert.Equal(0f, up[0, h - 1], 2); // clear in the corner
        // With an order digit the texture is wider and the digit has ink right of the arrow; every digit differs.
        var withDigit = UiDrawnMark.Sort(true, 2);
        Assert.Equal(w + UiDrawnMarkArt.DigitGap + UiDrawnMarkArt.DigitWidth, UiDrawnMarkArt.TextureSize(withDigit).Width);
        var inks = new HashSet<string>();
        for (int d = 1; d <= 9; d++)
        {
            var art = Paint(UiDrawnMark.Sort(true, d));
            float ink = Sum(art, w + UiDrawnMarkArt.DigitGap, w + UiDrawnMarkArt.DigitGap + UiDrawnMarkArt.DigitWidth, 0, h);
            Assert.True(ink > 20f, "digit " + d + " has no ink");
            // About the arrow's height (the mockup's digit, 70% of the caption's capitals): ink in the upper half too,
            // nothing in the top rows.
            Assert.True(Sum(art, w + UiDrawnMarkArt.DigitGap, art.GetLength(0), h / 2, h) > 10f, "digit " + d + " is too small");
            Assert.Equal(0f, Sum(art, w + UiDrawnMarkArt.DigitGap, art.GetLength(0), h - 1, h));
            inks.Add(ink.ToString("0"));
        }

        Assert.True(inks.Count >= 7, "the digits look alike");
        // A digit beside the arrow at the header's size, not a speck: about 70% of the caption's capitals.
        float drawnDigit = UiDrawnMarkArt.Height(withDigit, 15f) * UiDrawnMarkArt.DigitHeightShare;
        Assert.InRange(drawnDigit, 15f * 0.7f * 0.65f, 15f * 0.7f * 1.1f);
        Assert.True(UiDrawnMarkArt.Width(withDigit, 15f) < 22f);
    }

    [Fact]
    public void The_spinner_turns_its_gap_a_quarter_each_frame()
    {
        int n = UiDrawnMarkArt.TextureHeight;
        var frames = Enumerable.Range(0, 4).Select(f => Paint(UiDrawnMark.Spinner(f))).ToArray();
        // Each frame is a ring with one quarter open; the open quarter moves clockwise.
        (int X, int Y) Probe(double degrees)
        {
            double r = n * 0.375, rad = degrees * Math.PI / 180.0;
            return ((int)(n / 2.0 + r * Math.Sin(rad)), (int)(n / 2.0 + r * Math.Cos(rad)));
        }

        for (int f = 0; f < 4; f++)
        {
            var gap = Probe(f * 90 + 45);
            var ink = Probe(f * 90 + 45 + 180);
            Assert.True(frames[f][gap.X, gap.Y] < 0.1f, "frame " + f + " is not open at " + (f * 90 + 45) + "°");
            Assert.True(frames[f][ink.X, ink.Y] > 0.9f, "frame " + f + " has no ring opposite its gap");
            Assert.Equal(0f, frames[f][n / 2, n / 2], 2); // hollow
        }

        Assert.Equal(0, UiDataTableLayout.SpinnerFrame(0.1));
        Assert.Equal(1, UiDataTableLayout.SpinnerFrame(0.3));
        Assert.Equal(3, UiDataTableLayout.SpinnerFrame(0.8));
        Assert.Equal(0, UiDataTableLayout.SpinnerFrame(1.0));
        Assert.Equal(0, UiDataTableLayout.SpinnerFrame(double.NaN));
        Assert.Equal(1, UiDrawnMark.Spinner(5).Frame);
    }

    [Theory]
    [InlineData(false, 10f, 50f, 0f, 100f, 12f, 60f)]   // after the text, gap 4
    [InlineData(false, 10f, 98f, 0f, 100f, 12f, 94f)]   // kept inside the rect
    [InlineData(true, 60f, 98f, 0f, 100f, 12f, 50f)]  // before the text
    [InlineData(true, float.NaN, float.NaN, 0f, 100f, 12f, 90f)] // unknown bounds: from the right edge
    public void A_mark_sits_beside_its_text_inside_the_rect(bool before, float min, float max, float lo, float hi, float width, float expected) =>
        Assert.Equal(expected, UiDrawnMarkArt.CenterX(before ? UiMarkSide.Before : UiMarkSide.After, min, max, lo, hi, width), 2);

    // ---- Columns ------------------------------------------------------------------------------------

    [Fact]
    public void Hiding_a_column_hides_its_header_and_cells_and_always_shown_columns_refuse()
    {
        var (table, _) = NewTable();
        var changes = new List<string>();
        table.OnColumnsChanged(h => changes.Add(string.Join(",", h)));
        Assert.True(table.SetColumnVisible("note", false));
        Assert.False(Cell(Row("t:header"), "c:note").Visible);
        Assert.False(Cell(Row("t:row0"), "c:note").Visible);
        Assert.False(table.SetColumnVisible("name", false)); // always shown
        Assert.False(table.SetColumnVisible("note", false)); // already hidden
        table.HiddenColumns = new[] { "total", "name", "nope" };
        Assert.Equal(new[] { "total" }, table.HiddenColumns);
        Assert.True(Cell(Row("t:row0"), "c:note").Visible);
        Assert.Equal(new[] { "note", "total" }, changes);
        Assert.Equal("Columns · 1 hidden", Cell(Row("t:footer"), UiDataTableLayout.CellColumns).Text);
    }

    [Fact]
    public void The_chooser_offers_hideable_columns_as_chips_and_a_chip_toggles_its_column()
    {
        var (table, _) = NewTable();
        var chips = _runtime.Rows.Where(r => r.Text.StartsWith("t:columns", StringComparison.Ordinal)).ToList();
        Assert.Single(chips);
        Assert.Equal(new[] { "col:total", "col:cap", "col:note" }, chips[0].Cells().Select(c => c.Key)); // not Name
        Assert.False(chips[0].Visible);
        Click(Row("t:footer"), UiDataTableLayout.CellColumns);
        Assert.True(table.ChooserOpen);
        Assert.True(chips[0].Visible);
        Click(chips[0], "col:cap");
        Assert.Equal(new[] { "cap" }, table.HiddenColumns);
        Assert.Equal(CellTone.ChipOff, Cell(chips[0], "col:cap").Tone);
        Click(chips[0], "col:cap");
        Assert.Empty(table.HiddenColumns);
        Assert.Equal(CellTone.ChipOn, Cell(chips[0], "col:cap").Tone);
        Click(Row("t:footer"), UiDataTableLayout.CellColumns);
        Assert.False(chips[0].Visible);
    }

    [Fact]
    public void A_column_marked_hidden_starts_hidden()
    {
        var cols = Columns();
        cols[3].Hidden = true;
        var table = NewPanel().DataTable("t", cols, Options());
        Assert.Equal(new[] { "note" }, table.HiddenColumns);
    }

    // ---- Selection ----------------------------------------------------------------------------------

    [Fact]
    public void Click_ctrl_and_shift_select_and_shift_ranges_follow_the_sorted_order()
    {
        var m = Model();
        m.ClickSort("name", add: false); // c, d, e, b, a
        Assert.True(m.Click(0, UiClickModifiers.None));
        Assert.Equal(new[] { "c" }, m.Selected);
        Assert.True(m.Click(2, UiClickModifiers.Shift)); // c..e in body order
        Assert.Equal(new[] { "c", "d", "e" }, m.Selected);
        Assert.True(m.Click(4, UiClickModifiers.Ctrl)); // add a
        Assert.Equal(new[] { "c", "d", "e", "a" }, m.Selected);
        Assert.True(m.Click(1, UiClickModifiers.Ctrl)); // remove d; d is the anchor now
        Assert.Equal(new[] { "c", "e", "a" }, m.Selected);
        Assert.True(m.Click(3, UiClickModifiers.Shift)); // d..b
        Assert.Equal(new[] { "d", "e", "b" }, m.Selected);
        Assert.True(m.Click(0, UiClickModifiers.Shift | UiClickModifiers.Ctrl)); // add c..d
        Assert.Equal(new[] { "d", "e", "b", "c" }, m.Selected);
        Assert.True(m.Click(3, UiClickModifiers.None));
        Assert.Equal(new[] { "b" }, m.Selected);
        Assert.False(m.Click(3, UiClickModifiers.None)); // the same: no change
    }

    [Fact]
    public void Single_select_ignores_the_modifiers()
    {
        var m = Model(options: Options(multi: false));
        m.Click(0, UiClickModifiers.None);
        m.Click(2, UiClickModifiers.Shift);
        Assert.Equal(new[] { "c" }, m.Selected);
        m.Click(3, UiClickModifiers.Ctrl);
        Assert.Equal(new[] { "d" }, m.Selected);
    }

    [Fact]
    public void Selection_is_by_id_across_a_re_sort_and_a_refresh_and_vanished_ids_drop_silently()
    {
        var (table, handle) = NewTable(options: Options(visible: 5));
        var events = new List<int>();
        table.OnSelectionChanged(s => events.Add(s.Count));
        Row("t:row1").PointerClick(); // b
        _runtime.Modifiers = UiClickModifiers.Ctrl;
        Row("t:row3").PointerClick(); // d
        _runtime.Modifiers = UiClickModifiers.None;
        Assert.Equal(new[] { "b", "d" }, table.Selected);
        Assert.True(Row("t:row1").Selected);
        table.SortBy("name");
        Assert.Equal(new[] { "b", "d" }, table.Selected);
        // c, d, e, b, a: the highlight moved with the rows
        Assert.True(Row("t:row1").Selected);
        Assert.True(Row("t:row3").Selected);
        Assert.False(Row("t:row0").Selected);
        Assert.Equal(UiDrawnMark.CheckBoxMark, handle.CheckMarkAt(3)); // the drawn checked box, not a text glyph
        Assert.Equal("", Cell(Row("t:row3"), UiDataTableLayout.CellCheck).Text);
        table.SetRows(Sample().Where(r => r.Id != "d"));
        Assert.Equal(new[] { "b" }, table.Selected);
        Assert.Equal(new[] { 1, 2 }, events); // the refresh raised nothing
        Assert.Contains("1 selected", table.StatusText);
        Assert.NotNull(handle);
    }

    [Fact]
    public void The_check_cell_toggles_like_ctrl_click_for_a_controller()
    {
        var (table, _) = NewTable();
        Click(Row("t:row0"), UiDataTableLayout.CellCheck);
        Click(Row("t:row2"), UiDataTableLayout.CellCheck);
        Assert.Equal(new[] { "a", "c" }, table.Selected);
        Click(Row("t:row0"), UiDataTableLayout.CellCheck);
        Assert.Equal(new[] { "c" }, table.Selected);
    }

    [Fact]
    public void A_shift_range_reaches_rows_outside_the_drawn_slots()
    {
        var (table, handle) = NewTable(Many(600), Options(visible: 12));
        Row("t:row0").PointerClick();
        table.TopIndex = 500;
        _runtime.Modifiers = UiClickModifiers.Shift;
        handle.ClickSlot(5); // line 505
        _runtime.Modifiers = UiClickModifiers.None;
        Assert.Equal(506, table.Selected.Count);
        Assert.Equal("r000", table.Selected[0]);
        Assert.Equal("r505", table.Selected[^1]);
        Assert.Contains("506 selected", table.StatusText);
    }

    // ---- Drawers ------------------------------------------------------------------------------------

    [Fact]
    public void The_expander_opens_drawer_lines_under_the_row_one_line_per_detail_line()
    {
        var rows = Sample();
        rows[0].Detail = "Plot A: 3\nPlot B: 2";
        var (table, _) = NewTable(rows, Options(visible: 6));
        var opened = new List<(string, bool)>();
        table.OnExpand((id, on) => opened.Add((id, on)));
        Assert.True(Cell(Row("t:row0"), UiDataTableLayout.CellExpand).Visible);
        Assert.False(Cell(Row("t:row1"), UiDataTableLayout.CellExpand).Visible); // no drawer: the pad keeps the column
        Assert.True(Cell(Row("t:row1"), UiDataTableLayout.CellExpandPad).Visible);
        Click(Row("t:row0"), UiDataTableLayout.CellExpand);
        Assert.True(Row("t:row0").Expanded);
        Assert.Equal("Plot A: 3", Cell(Row("t:row1"), UiDataTableLayout.CellDetail).Text);
        Assert.True(Cell(Row("t:row1"), UiDataTableLayout.CellDetail).Visible);
        Assert.False(Cell(Row("t:row1"), "c:name").Visible);
        Assert.Equal("Plot B: 2", Cell(Row("t:row2"), UiDataTableLayout.CellDetail).Text);
        Assert.Equal("Iron", Cell(Row("t:row0"), "c:name").Text);
        Assert.Equal("copper", Cell(Row("t:row3"), "c:name").Text); // b moved down two lines
        Assert.Equal(new[] { ("a", true) }, opened);
        Assert.Equal(8, table.LineCount); // five rows, two drawer lines, the total
    }

    [Fact]
    public void A_long_drawer_is_capped_and_says_how_many_lines_more()
    {
        var row = new UiTableRow("x") { Detail = "1\n2\n\n3\n4\n5" };
        Assert.Equal(new[] { "1", "2", "3 more lines" }, UiDataTableModel.DetailLines(row, 3));
        Assert.Equal(new[] { "1", "2", "3", "4", "5" }, UiDataTableModel.DetailLines(row, 8));
    }

    // ---- Edits --------------------------------------------------------------------------------------

    [Fact]
    public void Typing_keeps_a_draft_until_the_field_is_left_then_the_edit_waits_and_resolves()
    {
        var (table, handle) = NewTable();
        var edits = new List<UiTableEdit>();
        table.OnEdit(edits.Add);
        var row0 = Row("t:row0");
        Assert.True(Cell(row0, "e:cap").Visible); // a value box
        Assert.False(Cell(row0, "c:cap").Visible);
        Assert.Equal("100", Cell(row0, "e:cap").Text);
        _runtime.Focused = (row0, "e:cap");
        Type(row0, "e:cap", "1");
        Type(row0, "e:cap", "12");
        Assert.Equal(1, handle.DraftCount);
        _runtime.Tick(); // still in the field
        Assert.Empty(edits);
        _runtime.Focused = null; // Enter or a click elsewhere
        _runtime.Tick();
        var edit = Assert.Single(edits);
        Assert.Equal(("a", "cap", 100d, 12d, false), (edit.RowId, edit.Column, edit.OldValue!.Value, edit.NewValue!.Value, edit.Clear));
        Assert.Equal(UiTableEditState.Pending, edit.State);
        Assert.False(Cell(row0, "e:cap").Visible); // locked while waiting
        Assert.Equal("12", Cell(row0, "c:cap").Text); // the words are the value; the spinner is drawn before it
        Assert.Equal(CellTone.Pending, Cell(row0, "c:cap").Tone);
        Assert.Equal("spinner:0", handle.SpinnerAt(0, "cap"));
        Assert.Equal(("spinner:0", MenuPalette.TextColor(CellTone.Pending)), _runtime.MarkOf(row0, "c:cap"));
        Assert.Equal(UiMarkSide.Before, _runtime.SideOf(row0, "c:cap"));
        Assert.Contains("1 waiting", table.StatusText);
        _now = 0.3;
        _runtime.Tick();
        Assert.Equal("spinner:1", handle.SpinnerAt(0, "cap")); // the spinner turns
        Assert.True(edit.Resolve());
        Assert.Equal("", _runtime.MarkOf(row0, "c:cap").Mark); // gone with the answer
        Assert.False(edit.Resolve()); // answered once
        Assert.Equal(UiTableEditState.Resolved, edit.State);
        Assert.True(Cell(row0, "e:cap").Visible);
        Assert.Equal("12", Cell(row0, "e:cap").Text);
        Assert.Equal(12d, table.Rows[0]["cap"]!.Value);
        Assert.Empty(table.PendingEdits);
        Assert.False(handle.Ticking);
    }

    [Fact]
    public void Our_own_rewrite_of_a_field_is_not_typing()
    {
        var (table, handle) = NewTable();
        Type(Row("t:row0"), "e:cap", "100"); // the echo of what the table wrote
        Assert.Equal(0, handle.DraftCount);
        Assert.Empty(table.PendingEdits);
    }

    [Fact]
    public void A_reject_restores_the_old_value_and_shows_the_reason_under_the_table_and_on_the_row()
    {
        var (table, handle) = NewTable();
        var edit = table.Edit("a", "cap", "20000")!;
        Assert.True(edit.Reject("Above the cap"));
        Assert.Equal(UiTableEditState.Rejected, edit.State);
        Assert.Equal("100", Cell(Row("t:row0"), "e:cap").Text);
        Assert.Equal(100d, table.Rows[0]["cap"]!.Value);
        Assert.Equal("Iron, Cap: Above the cap", table.StatusReason);
        Assert.True(Row("t:reason").Visible);
        Assert.Equal(CellTone.Orange, Cell(Row("t:reason"), UiDataTableLayout.CellReason).Tone);
        handle.HoverSlotForTests(0, true);
        Assert.Equal(("Iron", "Cap: Above the cap"), _runtime.Tooltip);
        table.Edit("a", "cap", "40")!.Resolve(); // a later edit clears the line
        Assert.Equal("", table.StatusReason);
        Assert.False(Row("t:reason").Visible);
    }

    [Fact]
    public void An_unanswered_edit_times_out_with_no_answer_from_the_server()
    {
        var options = Options();
        options.EditTimeout = TimeSpan.FromSeconds(15);
        var (table, _) = NewTable(options: options);
        var edit = table.Edit("a", "cap", "7")!;
        _now = 14.9;
        _runtime.Tick();
        Assert.Equal(UiTableEditState.Pending, edit.State);
        _now = 15.1;
        _runtime.Tick();
        Assert.Equal(UiTableEditState.Rejected, edit.State);
        Assert.Equal(UiDataTableModel.TimeoutReason, edit.Reason);
        Assert.Contains("No answer from the server", table.StatusReason);
        Assert.False(edit.Resolve()); // a late answer changes nothing
        Assert.Equal(100d, table.Rows[0]["cap"]!.Value);
        Assert.Empty(_runtime.Ticks);
    }

    [Fact]
    public void A_refresh_while_an_edit_waits_keeps_the_spinner_and_the_answer_lands_on_the_new_row()
    {
        var (table, _) = NewTable();
        var edit = table.Edit("a", "cap", "8")!;
        var fresh = Sample();
        fresh[0].Number("cap", 999); // the server's older snapshot
        table.SetRows(fresh);
        Assert.Equal("spinner:0", ((UiDataTableHandle)table).SpinnerAt(0, "cap"));
        Assert.Equal("8", Cell(Row("t:row0"), "c:cap").Text);
        Assert.False(Cell(Row("t:row0"), "e:cap").Visible);
        Assert.Single(table.PendingEdits);
        edit.Resolve("8 (set)");
        Assert.Equal("8 (set)", Cell(Row("t:row0"), "e:cap").Text);
        Assert.Equal(8d, fresh[0]["cap"]!.Value);
    }

    [Fact]
    public void Clearing_returns_to_the_inherited_value_and_bad_or_locked_edits_are_refused_with_a_reason()
    {
        var rows = Sample();
        rows[1].Number("cap", 25, inherited: true);
        rows[2].Locked = true;
        var (table, _) = NewTable(rows);
        Assert.Equal("", Cell(Row("t:row1"), "e:cap").Text); // inherited: the field's gray hint
        Assert.Equal("25", Cell(Row("t:row1"), "e:cap").Placeholder);
        Assert.Null(table.Edit("b", "cap", "")); // already inherited: nothing to clear
        Assert.Equal("", table.StatusReason);
        var clear = table.Edit("a", "cap", "  ")!;
        Assert.True(clear.Clear);
        Assert.Null(clear.NewValue);
        Assert.Equal("Inherited", Cell(Row("t:row0"), "c:cap").Text); // the value box's gray hint word
        Assert.Equal(CellTone.Muted, Cell(Row("t:row0"), "c:cap").Tone);
        Assert.Equal("spinner:0", ((UiDataTableHandle)table).SpinnerAt(0, "cap"));
        clear.Resolve("10");
        Assert.True(table.Rows[0]["cap"]!.Inherited);
        Assert.Equal("10", Cell(Row("t:row0"), "e:cap").Placeholder);
        Assert.Null(table.Edit("a", "cap", "ten"));
        Assert.Equal("\"ten\" is not a number.", table.StatusReason);
        Assert.Null(table.Edit("c", "cap", "5"));
        Assert.Equal("You cannot change Cap here.", table.StatusReason);
        Assert.False(Cell(Row("t:row2"), "e:cap").Visible); // locked: plain text
        Assert.Equal("50", Cell(Row("t:row2"), "c:cap").Text);
        Assert.Null(table.Edit("a", "total", "5"));
        Assert.Equal("Total cannot be edited here.", table.StatusReason);
        var first = table.Edit("e", "cap", "1,250.5")!;
        Assert.Equal(1250.5, first.NewValue);
        Assert.Null(table.Edit("e", "cap", "3"));
        Assert.Equal("Waiting for the server to answer the last change.", table.StatusReason);
    }

    [Fact]
    public void A_draft_whose_row_scrolls_away_commits_before_the_slot_shows_another_row()
    {
        var (table, handle) = NewTable(Many(40), Options(visible: 6));
        var edits = new List<UiTableEdit>();
        table.OnEdit(edits.Add);
        _runtime.Focused = (Row("t:row0"), "e:cap");
        Type(Row("t:row0"), "e:cap", "77");
        Assert.True(handle.Wheel(+1));
        var edit = Assert.Single(edits);
        Assert.Equal(("r000", 77d), (edit.RowId, edit.NewValue!.Value));
        Assert.Equal("Item 002", Cell(Row("t:row0"), "c:name").Text);
        Assert.Equal("2", Cell(Row("t:row0"), "e:cap").Text); // the slot shows its new row's value
    }

    // ---- Grouping -----------------------------------------------------------------------------------

    [Fact]
    public void Grouped_rows_fold_under_sorted_groups_with_subtotals_and_a_total_that_counts_each_row_once()
    {
        var m = Model();
        m.Grouped = true;
        var lines = m.Lines();
        string Describe(UiTableLine l) => l.Kind switch
        {
            UiTableLineKind.GroupHeader => "[" + l.Group + "]",
            UiTableLineKind.Subtotal => "sub " + l.Group,
            UiTableLineKind.Total => "total",
            _ => l.Row!.Id,
        };
        Assert.Equal(
            new[] { "[Alchemy]", "c", "d", "sub Alchemy", "[Ores]", "a", "b", "d", "sub Ores", "[Other]", "e", "sub Other", "total" },
            lines.Select(Describe));
        var total = m.Column("total")!;
        Assert.Equal(5d, UiDataTableModel.Aggregate(m.RowsIn("Alchemy"), total)); // 5 + empty
        Assert.Equal(8d, UiDataTableModel.Aggregate(m.RowsIn("Ores"), total));
        Assert.Equal(16d, UiDataTableModel.Aggregate(m.Rows, total)); // d once, though it sits in two groups
        m.ClickSort("name", add: false);
        Assert.Equal(new[] { "c", "d", "d", "b", "a", "e" }, Ids(m)); // sorted within each group
        Assert.True(m.SetCollapsed("Ores", true));
        Assert.Equal(new[] { "c", "d", "e" }, Ids(m));
        Assert.Contains(m.Lines(), l => l.Kind == UiTableLineKind.Subtotal && l.Group == "Ores"); // a folded group keeps its subtotal
    }

    [Fact]
    public void The_grouped_table_draws_headers_subtotals_and_folds_on_a_click()
    {
        var (table, _) = NewTable(options: Options(visible: 8));
        table.Grouped = true;
        Assert.Equal(new[] { "Alchemy", "Ores", "Other" }, table.Groups);
        Assert.Equal("Alchemy (2)", Cell(Row("t:row0"), "c:name").Text);
        Assert.Equal(CellTone.Gold, Cell(Row("t:row0"), "c:name").Tone);
        Assert.True(Row("t:row0").Expanded); // the arrow points down while open
        Assert.Equal("Subtotal", Cell(Row("t:row3"), "c:name").Text);
        Assert.Equal("5", Cell(Row("t:row3"), "c:total").Text);
        Assert.False(Cell(Row("t:row3"), "e:cap").Visible);
        Row("t:row0").PointerClick(); // fold Alchemy
        Assert.True(table.IsGroupCollapsed("Alchemy"));
        Assert.False(Row("t:row0").Expanded);
        Assert.Equal("Subtotal", Cell(Row("t:row1"), "c:name").Text);
        Assert.Equal("Ores (3)", Cell(Row("t:row2"), "c:name").Text);
        table.Grouped = false;
        Assert.Equal("Iron", Cell(Row("t:row0"), "c:name").Text);
        Assert.Equal(6, table.LineCount); // five rows and the total
    }

    // ---- Viewport and virtualization ------------------------------------------------------------------

    [Fact]
    public void The_viewport_clamps_steps_whole_lines_and_maps_the_bar_both_ways()
    {
        var v = new UiTableViewport(12);
        Assert.Equal(12, v.Slots);
        Assert.Equal(4, new UiTableViewport(1).Slots);
        Assert.Equal(30, new UiTableViewport(99).Slots);
        v.SetCount(600);
        Assert.Equal(588, v.MaxTop);
        Assert.False(v.Wheel(-1)); // at the top: the notch goes to the panel
        Assert.True(v.Wheel(+1));
        Assert.Equal(UiTableViewport.RowsPerNotch, v.Top);
        Assert.Equal(2, UiTableViewport.RowsPerNotch);
        Assert.True(v.SetTop(10_000));
        Assert.Equal(588, v.Top);
        Assert.False(v.Wheel(+1));
        Assert.Equal(1f, v.BarPosition);
        Assert.Equal(12f / 600f, v.BarSize, 4);
        Assert.Equal(294, v.TopForBar(0.5f));
        Assert.Equal(0, v.TopForBar(-3f));
        v.SetTop(v.TopForBar(0.25f));
        Assert.Equal(147, v.Top);
        Assert.Equal(0.25f, v.BarPosition, 3);
        Assert.Equal(5, v.SlotOf(152));
        Assert.Equal(-1, v.SlotOf(100));
        Assert.True(v.EnsureShown(10));
        Assert.Equal(10, v.Top);
        Assert.True(v.EnsureShown(40));
        Assert.Equal(29, v.Top);
        v.SetCount(5); // fewer lines than slots
        Assert.Equal(0, v.Top);
        Assert.Equal(5, v.Shown);
        Assert.Equal(1f, v.BarSize);
        Assert.False(v.Scrolls);
    }

    [Fact]
    public void Six_hundred_rows_build_only_the_slot_rows_and_scrolling_rewrites_them_in_place()
    {
        var (table, _) = NewTable(Many(600), Options(visible: 12));
        int slots = _runtime.Rows.Count(r => r.Text.StartsWith("t:row", StringComparison.Ordinal));
        Assert.Equal(12, slots);
        int rowsBefore = _runtime.Rows.Count;
        table.TopIndex = 300;
        Assert.Equal(rowsBefore, _runtime.Rows.Count); // no row made or destroyed
        // Every slot stays shown: no row is shown or hidden, so nothing is laid out again.
        Assert.All(Enumerable.Range(0, 12), i => Assert.True(Row("t:row" + i).Visible));
        Assert.Equal("Item 300", Cell(Row("t:row0"), "c:name").Text);
        Assert.Equal("Item 311", Cell(Row("t:row11"), "c:name").Text);
        Assert.Equal("Showing 301–312 of 600", table.StatusText);
        Assert.True(table.ScrollTo("r599"));
        Assert.Equal(588, table.TopIndex);
        Assert.Equal("Item 599", Cell(Row("t:row11"), "c:name").Text);
    }

    [Fact]
    public void The_wheel_over_the_body_scrolls_the_table_and_hands_the_notch_on_at_the_ends()
    {
        var (table, _) = NewTable(Many(30), Options(visible: 10));
        _panels[0].Show();
        Assert.Equal(1, UiWheelClaims.Count);
        var top = ((UiPanelHandle)_panels[0]).RuntimePanel;
        var view = ScreenRect.FromCorners(0f, 0f, 4000f, 4000f);
        // Another panel on top, or the body clipped out of the visible viewport: the notch is not the table's.
        Assert.False(UiWheelClaims.TryTake(10f, 120f, -120f, new object(), view));
        Assert.False(UiWheelClaims.TryTake(10f, 120f, -120f, top, ScreenRect.FromCorners(0f, 300f, 4000f, 4000f)));
        Assert.Equal(0, table.TopIndex);
        // The fake rows sit at y 100..140; a notch toward the player is a negative delta.
        Assert.False(UiWheelClaims.TryTake(10f, 120f, +120f, top, view)); // at the top: the panel's
        Assert.True(UiWheelClaims.TryTake(10f, 120f, -120f, top, view));
        Assert.Equal(2, table.TopIndex);
        Assert.False(UiWheelClaims.TryTake(10f, 500f, -120f, top, view)); // outside the body
        table.TopIndex = 99;
        Assert.Equal(21, table.TopIndex); // 31 lines (30 rows and the total) in 10 slots
        Assert.False(UiWheelClaims.TryTake(10f, 120f, -120f, top, view)); // at the end: the panel's
        Assert.True(UiWheelClaims.TryTake(10f, 120f, +120f, top, view));
        Assert.Equal(19, table.TopIndex);
    }

    [Fact]
    public void The_scroll_bar_follows_the_window_and_a_drag_moves_it()
    {
        var (table, _) = NewTable(Many(100), Options(visible: 10));
        var bar = _runtime.Bar!;
        Assert.Equal(("t:row0", "t:row9"), (bar.Top, bar.Bottom));
        // 101 lines (100 rows and the total) in 10 slots: the top runs 0..91.
        Assert.Equal(10f / 101f, bar.Size, 4);
        Assert.Equal(0f, bar.Position);
        table.TopIndex = 45;
        Assert.Equal(45f / 91f, bar.Position, 4);
        bar.OnDragged!(1f);
        Assert.Equal(91, table.TopIndex);
        Assert.Equal("Item 091", Cell(Row("t:row0"), "c:name").Text);
        bar.OnDragged(0.5f);
        Assert.Equal(46, table.TopIndex); // the nearest whole line (45.5 rounds away from zero)
    }

    [Fact]
    public void The_controller_scrolls_one_line_at_the_edge_slots_and_the_stick_scrolls()
    {
        var (table, handle) = NewTable(Many(30), Options(visible: 10));
        Assert.False(handle.SteerSlot(0, NavCommand.Up)); // at the top: focus leaves the table
        Assert.False(handle.SteerSlot(4, NavCommand.Down)); // a middle slot: focus moves
        Assert.True(handle.SteerSlot(9, NavCommand.Down));
        Assert.Equal(1, table.TopIndex);
        Assert.True(handle.SteerSlot(0, NavCommand.Up));
        Assert.Equal(0, table.TopIndex);
        for (int i = 0; i < 8; i++) Assert.True(handle.StickSlot(-1f)); // stick down: 0.25 line a frame
        Assert.Equal(2, table.TopIndex);
        Assert.True(handle.StickSlot(1f));
        table.TopIndex = 0;
        Assert.False(handle.StickSlot(1f)); // at the top: the panel's
    }

    [Fact]
    public void Selection_drawers_and_waiting_edits_survive_scrolling()
    {
        var rows = Many(60);
        rows[3].Detail = "detail";
        var (table, _) = NewTable(rows, Options(visible: 6));
        table.SetSelected(new[] { "r003" });
        table.SetExpanded("r003", true);
        table.Edit("r003", "cap", "5");
        table.TopIndex = 40;
        Assert.DoesNotContain(Enumerable.Range(0, 6), i => Row("t:row" + i).Selected);
        table.TopIndex = 0;
        Assert.True(Row("t:row3").Selected);
        Assert.True(Row("t:row3").Expanded);
        Assert.Equal("detail", Cell(Row("t:row4"), UiDataTableLayout.CellDetail).Text);
        Assert.StartsWith("spinner:", ((UiDataTableHandle)table).SpinnerAt(3, "cap"));
    }

    // ---- Layout -------------------------------------------------------------------------------------

    public static IEnumerable<object[]> Tables() => new[]
    {
        new object[] { Columns() },
        new object[] { Progenitor.ControlsSample.WidgetsDemo.TableColumns() },
    };

    [Theory]
    [MemberData(nameof(Tables))]
    public void Every_table_row_fits_its_cells_at_every_checked_width_and_the_header_lines_up(List<UiTableColumn> columns)
    {
        foreach (bool multi in new[] { false, true })
        {
            var body = UiDataTableLayout.RowCells(columns, multi);
            var header = UiDataTableLayout.HeaderCells(columns, multi);
            foreach (var c in body.Where(c => c.Kind is CellKind.Text or CellKind.Mono)) c.Text = "Dark Silver Ingot";
            // A data row: the view cells (the value boxes stand in for editable ones, as wide).
            foreach (var c in body.Where(c => c.Key.StartsWith("e:", StringComparison.Ordinal)))
            {
                c.Visible = true;
                body.Single(v => v.Key == "c:" + c.Key.Substring(2)).Visible = false;
            }

            foreach (float width in UiWidgetLayout.CheckedRowWidths)
            {
                var bodyRow = new CellRowSpec("row", body, rightInset: UiDataTableLayout.ScrollInset);
                var headerRow = new CellRowSpec("header", header, rightInset: UiDataTableLayout.ScrollInset);
                AssertFits(MenuPanelLayout.CellColumns(width, bodyRow), body, width, UiDataTableLayout.ScrollInset);
                var placedHeader = MenuPanelLayout.CellColumns(width, headerRow);
                AssertFits(placedHeader, header, width, UiDataTableLayout.ScrollInset);
                var placedBody = MenuPanelLayout.CellColumns(width, bodyRow);
                foreach (var col in columns)
                {
                    var h = placedHeader.Single(p => p.Key == "c:" + col.Key);
                    var b = placedBody.Single(p => p.Key == (col.Editable ? "e:" : "c:") + col.Key);
                    Assert.Equal(h.Visible, b.Visible);
                    if (!h.Visible) continue;
                    Assert.Equal(h.Left, b.Left, 2);
                    Assert.Equal(h.Right, b.Right, 2);
                    // The caption with a two-key mark fits its column.
                    Assert.True(UiDataTableLayout.HeaderNeed(col) <= h.Width + .01f, col.Key + " caption does not fit at " + width);
                }

                // Always-shown columns are never dropped.
                foreach (var col in columns.Where(c => !c.Hideable))
                    Assert.True(placedHeader.Single(p => p.Key == "c:" + col.Key).Visible, col.Key + " dropped at " + width);
            }
        }

        foreach (float width in UiWidgetLayout.CheckedRowWidths)
        {
            var footer = UiDataTableLayout.FooterCells(true);
            footer[0].Text = "Showing 588–600 of 600 · 600 items · 12 selected · 3 waiting";
            AssertFits(MenuPanelLayout.CellColumns(width, footer), footer, width, 0f);
            foreach (var chips in UiDataTableLayout.ChooserRows(columns.Where(c => c.Hideable).ToList()))
                AssertFits(MenuPanelLayout.CellColumns(width, chips), chips, width, 0f);
        }
    }

    private static void AssertFits(CellPlacement[] placed, IReadOnlyList<CellSpec> cells, float width, float inset)
    {
        float left = -width / 2f, right = width / 2f - inset;
        foreach (var p in placed.Where(p => p.Visible))
        {
            Assert.True(p.Left >= left - .01f && p.Right <= right + .01f, p + " leaves the " + width + " row");
            Assert.True(p.Width >= CellLayout.MinWidthOf(cells[p.Index]) - .01f, p + " under its floor at " + width);
        }

        for (int i = 0; i < placed.Length; i++)
            for (int j = i + 1; j < placed.Length; j++)
                Assert.False(CellLayout.Overlap(placed[i], placed[j]), placed[i] + " overlaps " + placed[j] + " at " + width);
    }

    [Fact]
    public void Columns_after_the_fill_column_pack_right_in_the_callers_order()
    {
        var cells = UiDataTableLayout.RowCells(Columns(), multi: false);
        var placed = MenuPanelLayout.CellColumns(1248f, new CellRowSpec("r", cells, rightInset: UiDataTableLayout.ScrollInset));
        float name = placed.Single(p => p.Key == "c:name").Left;
        float total = placed.Single(p => p.Key == "c:total").Left;
        float note = placed.Single(p => p.Key == "c:note").Left;
        float expand = placed.Single(p => p.Key == UiDataTableLayout.CellExpand).Left;
        Assert.True(name < total && total < note && note < expand);
        Assert.Equal(1248f / 2f - UiDataTableLayout.ScrollInset, placed.Single(p => p.Key == UiDataTableLayout.CellExpand).Right, 2);
    }

    // ---- Lifetime -----------------------------------------------------------------------------------

    [Fact]
    public void Disposing_the_panel_releases_the_wheel_claim_the_tick_and_the_scroll_bar()
    {
        var (table, _) = NewTable(Many(50));
        table.Edit("r001", "cap", "4");
        Assert.Single(_runtime.Ticks);
        Assert.Equal(1, UiWheelClaims.Count);
        var bar = _runtime.Bar!;
        _panels[0].Dispose();
        Assert.True(table.IsDisposed);
        Assert.Empty(_runtime.Ticks);
        Assert.Equal(0, UiWheelClaims.Count);
        Assert.True(bar.Disposed);
        table.SetRows(Many(3)); // a late refresh is ignored
        Assert.Equal(50, table.Rows.Count);
    }

    [Fact]
    public void Duplicate_ids_and_column_keys_are_refused()
    {
        var panel = NewPanel();
        Assert.Throws<ArgumentException>(() => panel.DataTable("x", new[] { new UiTableColumn("a", "A"), new UiTableColumn("a", "B") }));
        var table = panel.DataTable("y", Columns(), Options());
        table.SetRows(Sample());
        Assert.Throws<ArgumentException>(() => table.SetRows(new[] { R("q", "Q", 1), R("q", "Q2", 2) }));
        Assert.Equal(5, table.Rows.Count); // a refused refresh changed nothing
    }

    [Fact]
    public void Describe_reads_back_columns_sort_hidden_window_selection_and_pending_edits()
    {
        var (table, _) = NewTable(Many(40), Options(visible: 6));
        table.Sort = new[] { new UiTableSortKey("total", false) };
        table.SetColumnVisible("note", false);
        table.TopIndex = 3;
        table.SetSelected(new[] { "r009", "r019" });
        table.Edit("r009", "cap", "2");
        var d = table.Describe();
        Assert.Equal("datatable", d["kind"]);
        Assert.Equal(new[] { "note" }, (List<string>)d["hidden"]!);
        Assert.Equal(3, d["top"]);
        Assert.Equal(40, d["rows"]);
        Assert.Equal(6, d["visibleRows"]);
        var slots = (List<Dictionary<string, object?>>)d["slots"]!;
        Assert.Equal(6, slots.Count);
        Assert.Equal("t:row0", slots[0]["row"]);
        Assert.Equal(new[] { "r009", "r019" }, (List<string>)d["selected"]!);
        var pending = Assert.Single((List<Dictionary<string, object?>>)d["pending"]!);
        Assert.Equal("r009", pending["row"]);
        var sort = Assert.Single((List<Dictionary<string, object?>>)d["sort"]!);
        Assert.Equal(false, sort["ascending"]);
    }

    // ---- Escape, inherited values, groups under a sort ------------------------------------------------

    [Fact]
    public void Escape_in_a_value_box_reverts_the_typing_leaves_the_field_and_keeps_the_panel()
    {
        var (table, handle) = NewTable();
        _panels[0].Show();
        var edits = new List<UiTableEdit>();
        table.OnEdit(edits.Add);
        var row0 = Row("t:row0");
        _runtime.Focused = (row0, "e:cap");
        Type(row0, "e:cap", "777");
        Assert.Equal(1, handle.DraftCount);
        UiApi.PressKeyForTests(27, true);
        UiApi.PressKeyForTests(17, true); // the paired Back of the same press
        Assert.Equal(0, handle.DraftCount);
        Assert.Null(_runtime.Focused); // the box gave up the keyboard
        Assert.Equal(1, _runtime.Released);
        Assert.Equal("100", Cell(row0, "e:cap").Text); // the value is back
        _runtime.Tick();
        Assert.Empty(edits); // nothing committed
        Assert.Equal(UiHandleState.Shown, _panels[0].State); // the press went no further
        // With no box focused, Escape is the panel's again.
        UiApi.PressKeyForTests(27, true);
        Assert.NotEqual(UiHandleState.Shown, _panels[0].State);
    }

    [Fact]
    public void Enter_or_focus_loss_still_commits_after_an_escape()
    {
        var (table, _) = NewTable();
        var edits = new List<UiTableEdit>();
        table.OnEdit(edits.Add);
        var row0 = Row("t:row0");
        _runtime.Focused = (row0, "e:cap");
        Type(row0, "e:cap", "5");
        Assert.True(((UiDataTableHandle)table).EscapeField());
        _runtime.Focused = (row0, "e:cap");
        Type(row0, "e:cap", "6");
        _runtime.Focused = null; // Enter
        _runtime.Tick();
        Assert.Equal(6d, Assert.Single(edits).NewValue);
    }

    [Fact]
    public void An_inherited_value_is_named_in_words_not_by_its_tone_alone()
    {
        var rows = Sample();
        rows[0].Number("cap", 200, inherited: true, source: "category Ore");
        rows[0].Set("total", new UiTableCell("") { Value = 5, Inherited = true });
        var (_, handle) = NewTable(rows);
        Assert.Equal("", Cell(Row("t:row0"), "e:cap").Text); // an empty box with the value as its gray hint
        Assert.Equal("200", Cell(Row("t:row0"), "e:cap").Placeholder);
        Assert.Equal(CellTone.Muted, Cell(Row("t:row0"), "c:total").Tone);
        handle.HoverSlotForTests(0, true);
        Assert.Equal(("Iron", "Total 5: inherited\nCap 200: inherited from category Ore"), _runtime.Tooltip);
    }

    [Fact]
    public void Groups_stay_together_under_any_sort_and_subtotals_take_no_selection()
    {
        var (table, handle) = NewTable(options: Options(visible: 13));
        table.Grouped = true;
        table.Sort = new[] { new UiTableSortKey("total", false) };
        var d = (List<Dictionary<string, object?>>)table.Describe()["slots"]!;
        var kinds = d.Select(s => (string)s["kind"]! + ":" + (s["group"] ?? "")).ToList();
        // Each group's header, its rows, its subtotal, in name order, whatever the sort.
        Assert.Equal(new[] { "group:Alchemy", "row:Alchemy", "row:Alchemy", "subtotal:Alchemy", "group:Ores" }, kinds.Take(5));
        Assert.Equal("c", (string)d[1]["id"]!); // total 5 before the empty one
        var sub = d.FindIndex(s => (string)s["kind"]! == "subtotal");
        handle.ClickSlot(sub, UiClickModifiers.None);
        Assert.Empty(table.Selected);
        handle.ClickSlot(1, UiClickModifiers.None);
        _runtime.Modifiers = UiClickModifiers.Shift;
        handle.ClickSlot(6); // a row of Ores: the range crosses the subtotal and the next header
        _runtime.Modifiers = UiClickModifiers.None;
        Assert.DoesNotContain(table.Selected, id => id.Length == 0);
        Assert.Equal(new[] { "c", "d", "a", "b" }, table.Selected); // rows only, each once
        Assert.Equal("Subtotal", Cell(Row("t:row" + sub), "c:name").Text);
    }

    [Fact]
    public void Number_columns_are_always_packed_right_so_their_figures_line_up()
    {
        var cols = new List<UiTableColumn>
        {
            new("name", "Name") { Width = 160f },
            new("total", "Total", UiTableColumnKind.Number) { Width = 80f },
            new("note", "Note") { Width = 0f }, // asks to fill, but sits after a number column
        };
        Assert.Equal(0, UiDataTableLayout.FillIndex(cols));
        var cells = UiDataTableLayout.RowCells(cols, multi: false);
        Assert.Equal(CellAlign.Right, cells.Single(c => c.Key == "c:total").Align);
        Assert.Equal(CellKind.Text, cells.Single(c => c.Key == "c:total").Kind); // proportional: no spaced separators
        Assert.Equal("1,234,567", UiDataTableModel.FormatValue(1234567, cols[1]));
    }

    // ---- Review round 2: focus, caches, dropped edits, tooltips ----------------------------------------

    [Theory]
    [InlineData("wheel")]
    [InlineData("sort")]
    [InlineData("refresh")]
    [InlineData("group")]
    public void A_focused_value_box_gives_up_the_keyboard_when_its_row_leaves_the_slot(string how)
    {
        var (table, handle) = NewTable(Many(40), Options(visible: 6));
        var edits = new List<UiTableEdit>();
        table.OnEdit(edits.Add);
        var row0 = Row("t:row0");
        _runtime.Focused = (row0, "e:cap"); // clicked into, nothing typed yet
        switch (how)
        {
            case "wheel": Assert.True(handle.Wheel(+1)); break;
            case "sort": table.SortBy("name"); table.SortBy("name"); break; // descending: r039 first
            case "refresh": table.SetRows(Many(40).Skip(1)); break;
            case "group": table.Grouped = true; break; // a group header takes slot 0
        }

        Assert.Null(_runtime.Focused); // released before the slot was rewritten
        Assert.Equal(1, _runtime.Released);
        Assert.Equal(1, handle.FieldsReleased);
        // The next key cannot reach the row that moved into the slot.
        Type(row0, "e:cap", "9");
        Assert.Equal(0, handle.DraftCount);
        Assert.Empty(edits);
    }

    [Fact]
    public void A_draft_commits_and_its_box_is_released_when_a_sort_moves_its_row()
    {
        var (table, handle) = NewTable(Many(40), Options(visible: 6));
        var edits = new List<UiTableEdit>();
        table.OnEdit(edits.Add);
        var row0 = Row("t:row0");
        _runtime.Focused = (row0, "e:cap");
        Type(row0, "e:cap", "77");
        table.Sort = new[] { new UiTableSortKey("name", false) };
        Assert.Equal(("r000", 77d), (Assert.Single(edits).RowId, edits[0].NewValue!.Value));
        Assert.Null(_runtime.Focused);
        Assert.Equal("39", Cell(row0, "e:cap").Text); // the slot shows its new row's own value
        Type(row0, "e:cap", "1");
        Assert.Single(edits);
        Assert.Equal(0, handle.DraftCount);
    }

    [Fact]
    public void Group_rows_and_subtotals_are_computed_once_per_change_not_per_render()
    {
        var model = Model(Many(300).Select((r, i) => { r.Groups = new[] { "G" + (i % 7) }; return r; }).ToList());
        model.Grouped = true;
        var total = model.Column("total")!;
        model.Lines();
        Assert.Equal(model.SubtotalOf("G1", total), model.SubtotalOf("G1", total));
        int computed = model.AggregateComputations;
        for (int i = 0; i < 50; i++) // a scroll re-renders the same lines
        {
            model.SubtotalOf("G1", total);
            model.SubtotalOf(null, total);
            Assert.Equal(43, model.RowsIn("G1").Count);
        }

        Assert.Equal(computed + 1, model.AggregateComputations); // only the grand total was new
        double? before = model.SubtotalOf("G1", total);
        var edit = model.Begin(model.RowsIn("G1")[0].Id, "cap", "5", (e, ok, t) => model.Complete(e, ok, t), out _)!;
        Assert.True(model.Complete(edit, true, null));
        model.SubtotalOf("G1", total);
        Assert.True(model.AggregateComputations > computed + 1); // a resolved value drops the cached subtotals
        Assert.Equal(before, model.SubtotalOf("G1", total));
        model.ClickSort("total", add: false);
        Assert.Equal(43, model.RowsIn("G1").Count); // recomputed under the new sort
    }

    [Fact]
    public void A_refresh_without_the_row_drops_its_waiting_edit_and_a_late_answer_does_nothing()
    {
        var (table, _) = NewTable();
        var edit = table.Edit("a", "cap", "8")!;
        Assert.Contains("1 waiting", table.StatusText);
        var rest = Sample().Where(r => r.Id != "a").ToList();
        table.SetRows(rest);
        Assert.Empty(table.PendingEdits);
        Assert.DoesNotContain("waiting", table.StatusText);
        Assert.Equal(UiTableEditState.Rejected, edit.State);
        Assert.Equal(UiDataTableModel.RowGoneReason, edit.Reason);
        Assert.Equal("", table.StatusReason); // not a refusal the player needs to read
        Assert.False(edit.Resolve("8"));
        table.SetRows(Sample()); // the row comes back with its own value
        Assert.Equal(100d, table.Rows[0]["cap"]!.Value);
        Assert.Empty(_runtime.Ticks);
    }

    [Fact]
    public void With_no_timeout_a_waiting_edit_never_expires()
    {
        var options = Options();
        options.EditTimeout = TimeSpan.Zero;
        var (table, _) = NewTable(options: options);
        var edit = table.Edit("a", "cap", "7")!;
        _now = 3600;
        _runtime.Tick();
        Assert.Equal(UiTableEditState.Pending, edit.State);
        Assert.Equal("", table.StatusReason);
    }

    [Theory]
    [InlineData("stick")]
    [InlineData("steer")]
    [InlineData("bar")]
    [InlineData("scrollto")]
    [InlineData("refresh")]
    [InlineData("sort")]
    public void The_row_tooltip_hides_whenever_the_body_moves(string how)
    {
        var rows = Many(40);
        rows[0].Tooltip = "first";
        var (table, handle) = NewTable(rows, Options(visible: 6));
        handle.HoverSlotForTests(0, true);
        Assert.NotNull(_runtime.Tooltip);
        switch (how)
        {
            case "stick": for (int i = 0; i < 4; i++) handle.StickSlot(-1f); break;
            case "steer": handle.SteerSlot(5, NavCommand.Down); break;
            case "bar": _runtime.Bar!.OnDragged!(0.5f); break;
            case "scrollto": table.ScrollTo("r030"); break;
            case "refresh": table.SetRows(Many(40)); break;
            case "sort": table.SortBy("total"); break;
        }

        Assert.Null(_runtime.Tooltip);
    }

    // ---- Live review round 3 --------------------------------------------------------------------------

    [Fact]
    public void Enter_commits_on_the_next_frame_once_the_box_gave_up_the_keyboard()
    {
        var (table, handle) = NewTable();
        var edits = new List<UiTableEdit>();
        table.OnEdit(edits.Add);
        var row0 = Row("t:row0");
        _runtime.Focused = (row0, "e:cap");
        Type(row0, "e:cap", "42");
        _runtime.Tick();
        Assert.Empty(edits); // still typing
        _runtime.Focused = null; // Enter: the runtime takes the keyboard from the box
        _runtime.Tick();
        Assert.Equal(42d, Assert.Single(edits).NewValue);
        Assert.Equal(0, handle.DraftCount);
    }

    [Fact]
    public void Every_value_box_is_right_aligned_and_commits_on_enter()
    {
        NewTable(options: Options(visible: 4));
        Assert.Equal(4, _runtime.Setups.Count); // one editable column, four slots
        Assert.All(_runtime.Setups, s => Assert.Equal(("e:cap", true, true, true), (s.Cell, s.Right, s.Enter, s.SelectAll)));
    }

    [Fact]
    public void A_row_without_an_icon_draws_no_well_and_a_known_item_draws_its_icon()
    {
        var rows = Sample();
        rows[0].ItemPrefab = 77; // the game has this icon
        rows[1].ItemPrefab = 78; // the game has none
        var sprite = new object();
        _runtime.Icons[77] = sprite;
        NewTable(rows);
        Assert.True(Cell(Row("t:row0"), UiDataTableLayout.CellIcon).Visible);
        Assert.Same(sprite, Row("t:row0").Images[UiDataTableLayout.CellIcon]);
        Assert.False(Cell(Row("t:row1"), UiDataTableLayout.CellIcon).Visible); // no empty well
        Assert.True(Cell(Row("t:row1"), UiDataTableLayout.CellIconPad).Visible); // the blank keeps the column
        Assert.False(Cell(Row("t:row2"), UiDataTableLayout.CellIcon).Visible);
        Assert.True(Cell(Row("t:header"), UiDataTableLayout.CellIcon).Visible);
    }

    [Fact]
    public void An_empty_status_stays_empty_though_its_severity_sorts()
    {
        var cols = new List<UiTableColumn> { new("name", "Name"), new("status", "Status") { Width = 160f } };
        var table = NewPanel().DataTable("t", cols, Options());
        table.SetRows(new[]
        {
            new UiTableRow("x").Text("name", "x").Set("status", new UiTableCell("") { Value = 0 }),
            new UiTableRow("y").Text("name", "y").Set("status", new UiTableCell("Short") { Value = 4 }),
        });
        Assert.Equal("", Cell(Row("t:row0"), "c:status").Text);
        Assert.Equal("Short", Cell(Row("t:row1"), "c:status").Text);
        table.Sort = new[] { new UiTableSortKey("status", false) };
        Assert.Equal("Short", Cell(Row("t:row0"), "c:status").Text);
    }

    [Fact]
    public void A_sorted_caption_turns_gold_and_back()
    {
        var (table, handle) = NewTable();
        table.SortBy("total");
        Assert.True(handle.CaptionGold("total"));
        Assert.False(handle.CaptionGold("name"));
        Assert.Equal(MenuPalette.HeaderSorted, _runtime.TextColors["t:header/c:total"]);
        table.Sort = new[] { new UiTableSortKey("name", true), new UiTableSortKey("total", false) };
        Assert.True(handle.CaptionGold("name") && handle.CaptionGold("total"));
        table.Sort = Array.Empty<UiTableSortKey>();
        Assert.False(handle.CaptionGold("total"));
        Assert.Null(_runtime.TextColors["t:header/c:total"]);
        // A caption rewritten (hidden and shown again) is gold again while it sorts.
        table.SortBy("total");
        table.SetColumnVisible("total", true);
        Assert.Equal(MenuPalette.HeaderSorted, _runtime.TextColors["t:header/c:total"]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(12)]
    public void The_columns_button_fits_its_longest_caption(int hideable)
    {
        float width = UiDataTableLayout.ColumnsButtonWidthFor(hideable);
        var button = UiDataTableLayout.FooterCells(true, hideable)[1];
        Assert.Equal(width, button.Width.Value);
        for (int hidden = 0; hidden <= hideable; hidden++)
        {
            button.Text = UiDataTableLayout.ColumnsCaption(hidden);
            Assert.True(CellLayout.CaptionFits(button, width - UiDataTableLayout.DropdownArrowRoom), button.Text + " is clipped");
        }

        Assert.Equal("Columns · 2 hidden", UiDataTableLayout.ColumnsCaption(2));
    }

    [Fact]
    public void The_demo_status_column_holds_its_longest_word_and_plurals_read_right()
    {
        var status = Progenitor.ControlsSample.WidgetsDemo.TableColumns().Single(c => c.Key == "status");
        var cell = new CellSpec(CellKind.Text, "c:status").With(text: "Below reserve");
        Assert.True(CellLayout.CaptionFits(cell, UiDataTableLayout.WidthOf(status, fill: false)));
        Assert.Equal("1 chest", Progenitor.ControlsSample.WidgetsDemo.Chests(1));
        Assert.Equal("2 chests", Progenitor.ControlsSample.WidgetsDemo.Chests(2));
        Assert.DoesNotContain(Progenitor.ControlsSample.WidgetsDemo.TableRows(), r => r.Detail.Contains(" 1 chests", StringComparison.Ordinal));
    }

    // ---- Live review round 5 --------------------------------------------------------------------------

    [Fact]
    public void Text_columns_read_left_justified_wherever_they_pack_and_numbers_stay_right()
    {
        var cols = Columns(); // name (fill), total, cap, note (text, after the fill)
        Assert.False(UiDataTableLayout.NeedsLeftJustify(cols, cols[0])); // packed from the left already
        Assert.False(UiDataTableLayout.NeedsLeftJustify(cols, cols[1]));
        Assert.True(UiDataTableLayout.NeedsLeftJustify(cols, cols[3]));
        NewTable(options: Options(visible: 4));
        Assert.Equal(CellAlign.Left, _runtime.Justified["t:header/c:note"]);
        for (int i = 0; i < 4; i++) Assert.Equal(CellAlign.Left, _runtime.Justified["t:row" + i + "/c:note"]);
        Assert.DoesNotContain(_runtime.Justified.Keys, k => k.EndsWith("/c:total", StringComparison.Ordinal));
    }

    [Fact]
    public void Overflow_tooltips_are_off_by_default_and_a_row_tooltip_still_shows()
    {
        Assert.False(new UiDataTableOptions().OverflowTooltips);
        var rows = Sample();
        rows[0].Text("note", "A very long note that no 120-unit column can hold");
        rows[1].Text("note", "Another very long note that no 120-unit column can hold");
        rows[1].Tooltip = "Kept in the castle";
        var (_, handle) = NewTable(rows);
        handle.HoverSlotForTests(0, true); // only a cut value: no box repeating the row
        Assert.Null(_runtime.Tooltip);
        handle.HoverSlotForTests(1, true); // the row's own tooltip, without the cut value
        Assert.Equal("Kept in the castle", _runtime.Tooltip!.Value.Body);
    }

    [Fact]
    public void A_single_select_status_line_has_no_selected_count_and_multi_select_keeps_it()
    {
        var (single, _) = NewTable(Many(8), Options(multi: false, visible: 4));
        single.SetSelected(new[] { "r001" });
        Assert.Single(single.Selected);
        Assert.Equal("Showing 1–4 of 8", single.StatusText);
        Assert.DoesNotContain("selected", single.StatusText);

        var (multi, _) = NewTable(Many(8), Options(multi: true, visible: 4));
        multi.SetSelected(new[] { "r001" });
        Assert.Single(multi.Selected);
        Assert.Equal("Showing 1–4 of 8 · 1 selected", multi.StatusText);
    }

    [Fact]
    public void A_value_its_column_cuts_is_read_whole_in_the_row_tooltip()
    {
        var rows = Sample();
        rows[0].Text("note", "A very long note that no 120-unit column can hold");
        var options = Options();
        options.OverflowTooltips = true;
        var (_, handle) = NewTable(rows, options);
        handle.HoverSlotForTests(0, true);
        Assert.Contains("Note: A very long note that no 120-unit column can hold", _runtime.Tooltip!.Value.Body);
        handle.HoverSlotForTests(1, true); // "nb" fits: no tooltip
        Assert.Null(_runtime.Tooltip);
        var demo = Progenitor.ControlsSample.WidgetsDemo.TableColumns();
        var category = demo.Single(c => c.Key == "category");
        Assert.False(UiDataTableLayout.IsCut(demo, category, "Alchemy, Building")); // the demo's longest category fits
    }

    [Fact]
    public void Body_rows_take_the_focus_frame_and_the_check_column_shows_an_empty_box_until_selected()
    {
        var (table, handle) = NewTable(options: Options(visible: 4));
        Assert.Equal(new[] { "t:row0", "t:row1", "t:row2", "t:row3" }, _runtime.Framed);
        Assert.Equal(UiDrawnMark.CheckEmptyMark, handle.CheckMarkAt(0));
        table.SetSelected(new[] { "a" });
        Assert.Equal(UiDrawnMark.CheckBoxMark, handle.CheckMarkAt(0));
        Assert.Equal(UiDrawnMark.CheckEmptyMark, handle.CheckMarkAt(1));
    }

    [Fact]
    public void The_check_box_marks_draw_a_box_and_the_checked_one_adds_the_check()
    {
        Assert.True(UiDrawnMark.TryParse("check-empty", out var empty) && empty.Equals(UiDrawnMark.CheckEmptyMark));
        Assert.True(UiDrawnMark.TryParse("check-box", out var box) && box.Equals(UiDrawnMark.CheckBoxMark));
        Assert.True(UiDrawnMark.TryParse("check", out var check) && check.Equals(UiDrawnMark.CheckMark)); // kept
        var e = Paint(UiDrawnMark.CheckEmptyMark);
        var b = Paint(UiDrawnMark.CheckBoxMark);
        int n = UiDrawnMarkArt.TextureHeight;
        Assert.True(e[n / 2, (int)(n * 0.16f)] > 0.5f && e[(int)(n * 0.16f), n / 2] > 0.5f); // the outline
        Assert.True(e[n / 2, n / 2] < 0.1f); // hollow
        Assert.True(Sum(b, 0, n, 0, n) > Sum(e, 0, n, 0, n) + 30f); // the check inside
        Assert.True(b[(int)(n * 0.45f), (int)(n * 0.37f)] > 0.5f);
    }

    [Fact]
    public void The_scroll_bar_is_as_wide_as_the_page_bar_and_fits_the_inset()
    {
        Assert.Equal(PanelLayout.ScrollbarWidth, UiDataTableLayout.ScrollBarWidth);
        Assert.True(UiDataTableLayout.ScrollInset >= UiDataTableLayout.ScrollBarWidth + 6f);
    }

    // ---- Live review round 6 --------------------------------------------------------------------------

    [Fact]
    public void A_left_justified_text_column_after_a_right_justified_one_keeps_a_lead_before_its_caption()
    {
        var cols = Columns(); // note (text) follows cap (number, right-justified)
        NewTable(options: Options(visible: 4));
        Assert.Equal(UiDataTableLayout.LeftTextLead, _runtime.Leads["t:header/c:note"]);
        Assert.Equal(UiDataTableLayout.LeftTextLead, _runtime.Leads["t:row0/c:note"]);
        // The lead is part of the column's width on the header and the body alike.
        var header = UiDataTableLayout.HeaderCells(cols, multi: false).Single(c => c.Key == "c:note");
        var body = UiDataTableLayout.RowCells(cols, multi: false).Single(c => c.Key == "c:note");
        Assert.Equal(UiDataTableLayout.WidthOf(cols[3], fill: false) + UiDataTableLayout.LeftTextLead, header.Width.Value, 2);
        Assert.Equal(header.Width.Value, body.Width.Value, 2);
    }

    [Fact]
    public void Totals_and_every_number_are_proportional_text_with_unspaced_grouping()
    {
        var rows = Many(3);
        rows[0].Number("total", 266_000);
        rows[1].Number("total", 400);
        var (_, _) = NewTable(rows, Options(visible: 6));
        var totalLine = Enumerable.Range(0, 6).Select(i => Row("t:row" + i)).First(r => Cell(r, "c:name").Text == "Total");
        var total = Cell(totalLine, "c:total");
        Assert.Equal(CellKind.Text, total.Kind); // no monospace run spacing the comma
        Assert.Equal("266,402", total.Text);
        Assert.All(UiDataTableLayout.RowCells(Columns(), multi: true), c => Assert.NotEqual(CellKind.Mono, c.Kind));
    }

    // ---- Live review round 7 --------------------------------------------------------------------------

    [Fact]
    public void A_wanted_term_marker_keeps_its_room_even_before_it_is_drawn()
    {
        float room = UiTermMarkerArt.SizeFor(15f) + UiTermMarkerArt.Gap;
        Assert.Equal(room, UiDataTableLayout.TermRoomFor(wanted: true, 15f));
        Assert.Equal(0f, UiDataTableLayout.TermRoomFor(wanted: false, 15f));
        // The header's width already counts that room, so the reserved margin never pushes the caption into an ellipsis.
        var cap = new UiTableColumn("cap", "Cap", UiTableColumnKind.Number) { Width = 1f, Term = "Cap" };
        float caption = InstallColumns.HeaderCaptionWidth("Cap", false, smallCaps: true, InstallColumns.HeaderFontSize);
        Assert.True(UiDataTableLayout.WidthOf(cap, fill: false) >= caption + room + UiDrawnMarkArt.SortRoom(InstallColumns.HeaderFontSize) - .01f);
    }

    [Fact]
    public void A_term_bound_while_the_table_is_on_a_hidden_tab_is_wanted_when_the_tab_shows()
    {
        var panel = NewPanel();
        UiTerms.Define(panel.PluginId, "Cap", "Stations stop making an item once you hold this many.");
        var tabs = panel.Tabs("tabs", new[] { "First", "Table" });
        var cols = Columns();
        cols[2].Term = "Cap";
        var table = tabs.Add(1, panel.DataTable("t", cols, Options())); // built hidden
        Assert.False(table.Visible);
        var binding = ((UiDataTableHandle)table).TermOf("cap")!;
        Assert.True(binding.MarkerWanted); // wanted from the start: the runtime keeps its room, so the first show draws it
        tabs.Selected = 1;
        Assert.True(table.Visible);
        Assert.True(binding.MarkerWanted);
    }

    [Theory]
    [InlineData(0, true, false, 1)] // the pass after the click: select the whole value
    [InlineData(1, true, true, 2)]    // the field's own click and activation may have moved it: again
    [InlineData(3, true, true, 2)]
    [InlineData(4, true, true, 0)]    // held long enough
    [InlineData(1, true, false, 0)]   // a key changed the selection: typing or an arrow wins
    [InlineData(0, false, false, 0)]  // another field took the keyboard
    [InlineData(2, false, true, 0)]
    public void The_first_focus_select_all_is_selected_then_held_until_a_key(int passes, bool same, bool whole, int expected) =>
        Assert.Equal((UiSelectAllStep)expected, UiFocusSelectAll.Step(passes, same, whole));

    // ---- Live review round 8 --------------------------------------------------------------------------

    [Fact]
    public void The_readback_reports_what_a_value_box_shows_its_value_or_its_hint()
    {
        var panel = NewPanel();
        var spec = new CellRowSpec("box", new List<CellSpec>
        {
            new CellSpec(CellKind.Input, "e:cap") { Skin = CellSkin.ValueBox }.With(text: "", placeholder: "10", width: CellWidth.Px(90f)),
        });
        var row = panel.CellRow(spec);
        Dictionary<string, object?> Box() => (Dictionary<string, object?>)((List<object?>)row.Describe()["cells"]!)[0]!;
        Assert.Equal("", Box()["text"]); // the value: empty (inherited)
        Assert.Equal("10", Box()["placeholder"]);
        Assert.Equal("10", Box()["shown"]); // what the screen shows
        row.SetCell("e:cap", "40");
        Assert.Equal("40", Box()["shown"]);
        Assert.Equal("", UiCellRowHandle.InputShown("", " ")); // the table's blank hint shows nothing
    }

    [Fact]
    public void An_inherited_table_cell_reads_back_its_shown_inherited_value()
    {
        var rows = Sample();
        rows[1].Number("cap", 25, inherited: true);
        NewTable(rows);
        var cell = Cell(Row("t:row1"), "e:cap");
        Assert.Equal("25", UiCellRowHandle.InputShown(cell.Text, cell.Placeholder));
        var own = Cell(Row("t:row0"), "e:cap");
        Assert.Equal("100", UiCellRowHandle.InputShown(own.Text, own.Placeholder));
    }

    // ---- Term markers on the header -------------------------------------------------------

    [Fact]
    public void A_column_term_marks_its_caption_and_its_hover_explains_the_term_before_any_tooltip()
    {
        var cols = Columns();
        cols[2].Term = "Cap";
        cols[2].Tooltip = "plain tooltip";
        var panel = NewPanel();
        UiTerms.Define(panel.PluginId, "Cap", "Stations stop making an item once you hold this many.");
        var table = panel.DataTable("t", cols, Options());
        var handle = (UiDataTableHandle)table;
        var binding = handle.TermOf("cap")!;
        Assert.NotNull(binding);
        Assert.Null(handle.TermOf("total"));
        Assert.True(binding.MarkerWanted); // the circle-i shows after the caption
        Assert.Equal("Cap", Cell(Row("t:header"), "c:cap").Text); // the caption stays its words
        handle.HoverHeaderForTests("cap", true);
        Assert.Equal(("Cap", "Stations stop making an item once you hold this many."), _runtime.Tooltip);
        Assert.Equal(1, UiTerms.Hovers(panel.PluginId, "Cap"));
        handle.HoverHeaderForTests("cap", false);
        Assert.Null(_runtime.Tooltip);
        // An undefined term falls back to the column's plain tooltip.
        var other = (UiDataTableHandle)NewPanel().DataTable("u", new[] { new UiTableColumn("x", "X") { Term = "Nope", Tooltip = "plain" } }, Options());
        other.HoverHeaderForTests("x", true);
        Assert.Equal(("X", "plain"), _runtime.Tooltip);
        panel.Dispose();
        Assert.True(binding.Dead); // released with the table
    }

    [Fact]
    public void A_termed_sortable_column_keeps_room_for_the_caption_the_term_marker_and_the_sort_mark()
    {
        var plain = new UiTableColumn("cap", "Cap", UiTableColumnKind.Number) { Width = 1f };
        var termed = new UiTableColumn("cap", "Cap", UiTableColumnKind.Number) { Width = 1f, Term = "Cap" };
        float caption = InstallColumns.HeaderCaptionWidth("Cap", false, smallCaps: true, InstallColumns.HeaderFontSize);
        float term = UiTermMarkerArt.SizeFor(InstallColumns.HeaderFontSize) + UiTermMarkerArt.Gap;
        float sort = UiDrawnMarkArt.SortRoom(InstallColumns.HeaderFontSize);
        Assert.Equal(caption + sort, UiDataTableLayout.HeaderNeed(plain), 2);
        Assert.Equal(caption + term + sort, UiDataTableLayout.HeaderNeed(termed), 2);
        Assert.True(UiDataTableLayout.WidthOf(termed, fill: false) >= caption + term + sort - .01f);
    }

    // ---- The demo ------------------------------------------------------------------------------------

    [Fact]
    public void The_table_demo_builds_600_rows_and_answers_edits_like_a_server()
    {
        var panel = NewPanel();
        var log = new List<string>();
        var demo = new Progenitor.ControlsSample.WidgetsDemo(log.Add, null);
        var tabs = panel.Tabs("tabs", new[] { "Table" });
        demo.BuildTable(panel, tabs, 0);
        var table = demo.Table!;
        Assert.Equal(600, table.Rows.Count);
        Assert.Equal(600, table.Rows.Select(r => r["item"]!.Text).Distinct().Count());
        Assert.Equal(12, _runtime.Rows.Count(r => r.Text.StartsWith(Progenitor.ControlsSample.WidgetsDemo.TableKey + ":row", StringComparison.Ordinal)));
        var even = table.Edit("item-000", "reserve", "40")!;
        Assert.Equal(UiTableEditState.Resolved, even.State);
        var over = table.Edit("item-001", "cap", "12000")!;
        Assert.Equal(UiTableEditState.Rejected, over.State);
        Assert.Contains("Above the cap", table.StatusReason);
        var odd = table.Edit("item-002", "cap", "7")!;
        Assert.Equal(UiTableEditState.Pending, odd.State);
        Assert.Equal(1, demo.ConfirmPending());
        Assert.Equal(UiTableEditState.Resolved, odd.State);
        var clear = table.Edit("item-000", "reserve", "")!;
        Assert.Equal(UiTableEditState.Resolved, clear.State);
        Assert.True(table.Rows[0]["reserve"]!.Inherited);
        table.Grouped = true;
        Assert.Contains("Building", table.Groups);
        table.SortBy("total");
        Assert.Equal("table sort = total+", demo.LastStatus);
        Assert.Contains(log, l => l.Contains("widget table waiting item-002 cap", StringComparison.Ordinal));
        Assert.Contains(log, l => l.Contains("widget table rejected item-001 cap: Above the cap", StringComparison.Ordinal));
        // The Reserve and Cap captions explain themselves.
        var handle = (UiDataTableHandle)table;
        Assert.True(UiTerms.IsDefined(panel.PluginId, "Reserve"));
        Assert.True(handle.TermOf("reserve")!.MarkerWanted);
        Assert.True(handle.TermOf("cap")!.MarkerWanted);
    }

    [Fact]
    public void The_demo_rows_are_the_same_on_every_call()
    {
        var a = Progenitor.ControlsSample.WidgetsDemo.TableRows();
        var b = Progenitor.ControlsSample.WidgetsDemo.TableRows();
        Assert.Equal(a.Select(r => r.Id + r["item"]!.Text + r["total"]!.Value), b.Select(r => r.Id + r["item"]!.Text + r["total"]!.Value));
        Assert.Contains(a, r => r.Groups.Count == 2);
        Assert.Contains(a, r => r["cap"]!.Value == null);
    }
}

/// <summary>P7b: a fake runtime for the data table: cell rows, click modifiers, a focusable value box, a scroll bar.</summary>
internal sealed class TableRuntimeFake : CellRowTests.PlainRuntime, IWidgetRuntime, IWidgetCellField, IWidgetMarks
{
    public readonly List<CellRowTests.FakeCellRow> Rows = new();
    public readonly List<Action> Ticks = new();
    public UiClickModifiers Modifiers;
    public (CellRowTests.FakeCellRow Row, string Cell)? Focused;
    public (string Title, string Body)? Tooltip;
    public bool AsciiOnly;
    public FakeBar? Bar;

    public string Check => AsciiOnly ? UiWidgetLayout.CheckFallback : UiWidgetLayout.CheckGlyph;

    public override IRuntimeCellRow? AddCellRow(IRuntimePanel panel, CellRowSpec spec, bool header)
    {
        var row = new CellRowTests.FakeCellRow(null, spec, header);
        Rows.Add(row);
        return row;
    }

    public void ShowTooltip(ScreenRect anchor, string title, string body, Func<bool>? stillShown = null) => Tooltip = (title, body);

    public void HideTooltip() => Tooltip = null;

    public IDisposable? WatchCell(IRuntimeCellRow row, string cellKey, Action<UiPointerEvent> handler, object? owner) => null;

    public IDisposable? WatchHover(IRuntimeWidget widget, Action<bool> onHover, object? owner) => null;

    public IDisposable EveryFrame(Action tick)
    {
        Ticks.Add(tick);
        return new ActionDisposable(() => Ticks.Remove(tick));
    }

    public string DrawableGlyph(string preferred, string fallback)
    {
        if (!AsciiOnly) return preferred;
        foreach (char c in preferred) if (c > 0x7F) return fallback;
        return preferred;
    }

    PointerSample IWidgetRuntime.ReadPointer() => PointerSample.Unreadable;

    public bool PrimaryHeld() => false;

    /// <summary>The game item icons the fake knows, by prefab (an object stands in for a sprite).</summary>
    public readonly Dictionary<int, object> Icons = new();

    public object? ItemIcon(int prefab) => Icons.TryGetValue(prefab, out var icon) ? icon : null;

    public string? ItemName(int prefab) => null;

    public UiClickModifiers ClickModifiers() => Modifiers;

    public IWidgetScrollBar? AttachScrollBar(IRuntimeCellRow top, IRuntimeCellRow bottom, object? owner)
    {
        Bar = new FakeBar { Top = top.Text, Bottom = bottom.Text };
        return Bar;
    }

    public bool CellFieldFocused(IRuntimeCellRow row, string cellKey) =>
        Focused is { } f && ReferenceEquals(f.Row, row) && f.Cell == cellKey;

    public void SetCellHint(IRuntimeCellRow row, string cellKey, string hint) { }

    /// <summary>The value boxes set up (right-aligned, Enter commits), by row key and cell.</summary>
    public readonly List<(string Row, string Cell, bool Right, bool Enter, bool SelectAll)> Setups = new();

    public void SetupCellField(IRuntimeCellRow row, string cellKey, bool rightAligned, bool enterCommits, bool selectAllOnFocus) =>
        Setups.Add((row.Text, cellKey, rightAligned, enterCommits, selectAllOnFocus));

    /// <summary>The rows set to take NAV1's frame.</summary>
    public readonly List<string> Framed = new();

    public void FrameNavFocus(IRuntimeCellRow row) => Framed.Add(row.Text);

    /// <summary>The cells justified apart from their packing, by row key and cell.</summary>
    public readonly Dictionary<string, CellAlign> Justified = new();

    public readonly Dictionary<string, float> Leads = new();

    public void SetCellJustify(IRuntimeCellRow row, string cellKey, CellAlign align, float leadPad)
    {
        Justified[row.Text + "/" + cellKey] = align;
        Leads[row.Text + "/" + cellKey] = leadPad;
    }

    /// <summary>The last color each header caption was asked to take (null: its own tone).</summary>
    public readonly Dictionary<string, PaletteColor?> TextColors = new();

    public void SetCellTextColor(IRuntimeCellRow row, string cellKey, PaletteColor? color) => TextColors[row.Text + "/" + cellKey] = color;

    public int Released;

    public void ReleaseCellField(IRuntimeCellRow row, string cellKey)
    {
        if (CellFieldFocused(row, cellKey)) Focused = null;
        Released++;
    }

    // ---- IWidgetMarks ----------------------------------------------------------------------------

    private readonly Dictionary<(IRuntimeCellRow, string), FakeMark> _marks = new();

    public IUiMarkView? AttachMark(IRuntimeCellRow row, string cellKey, UiMarkSide side)
    {
        var view = new FakeMark { Side = side };
        _marks[(row, cellKey)] = view;
        view.Gone = () => _marks.Remove((row, cellKey));
        return view;
    }

    /// <summary>The mark drawn beside a cell's text now (its name and color); empty when none or never attached.</summary>
    public (string Mark, PaletteColor Color) MarkOf(CellRowTests.FakeCellRow row, string cell) =>
        _marks.TryGetValue((row, cell), out var m) ? (m.Mark.Name, m.Color) : ("", default);

    public UiMarkSide SideOf(CellRowTests.FakeCellRow row, string cell) => _marks[(row, cell)].Side;

    public int MarkCount => _marks.Count;

    private sealed class FakeMark : IUiMarkView
    {
        public UiDrawnMark Mark;
        public PaletteColor Color;
        public UiMarkSide Side;
        public Action? Gone;

        public void Set(UiDrawnMark mark, PaletteColor color)
        {
            Mark = mark;
            Color = color;
        }

        public void Dispose() => Gone?.Invoke();
    }

    public void Tick()
    {
        foreach (var t in Ticks.ToArray()) t();
    }

    internal sealed class FakeBar : IWidgetScrollBar
    {
        public string Top = "", Bottom = "";
        public float Size = 1f, Position;
        public bool Disposed;

        public void Set(float size, float position)
        {
            Size = size;
            Position = position;
        }

        public bool Visible => Size < 1f;

        public Action<float>? OnDragged { get; set; }

        public void Dispose() => Disposed = true;
    }
}
