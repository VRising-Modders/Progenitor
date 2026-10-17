using System;
using System.Collections.Generic;
using System.Linq;
using Progenitor.Ui;
using Xunit;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.Tests;

/// <summary>
/// The data table's row actions (<see cref="UiTableColumnKind.Actions"/>, <see cref="UiTableRow.Actions"/>,
/// <see cref="IUiDataTable.OnRowAction"/>) and its filler rows (<see cref="UiDataTableOptions.MinRows"/>), game-free: the
/// layout at every checked width, the drawn faces, clicks, disabled buttons, tooltips, and the blank rows that keep the
/// table's height.
/// </summary>
[Collection("UiStatic")]
public sealed class UiTableRowActionsTests : IDisposable
{
    private readonly TableRuntimeFake _runtime = new();
    private readonly object _owner = new();
    private readonly List<IUiPanel> _panels = new();
    private readonly UiHelpMemoryStore _help = new();

    public UiTableRowActionsTests()
    {
        UiApi.ResetForTests();
        UiWidgetLifetime.Uninstall();
        UiTerms.Teardown();
        UiHelpStore.UseForTests(_help);
        UiApi.SetRuntimeForTests(_runtime);
    }

    public void Dispose()
    {
        foreach (var p in _panels) p.Dispose();
        UiApi.ResetForTests();
        UiWidgetLifetime.Uninstall();
        UiWheelClaims.Reset();
        UiTerms.Teardown();
        UiHelpStore.UseForTests(null);
    }

    private const string Act = "act";

    /// <summary>The Satisvampory station tab's shape: icon · Name · Type · Plot · Status, then the row actions.</summary>
    private static List<UiTableColumn> Columns(bool hideableActions = false) => new()
    {
        new UiTableColumn("name", "Name") { Hideable = false },
        new UiTableColumn("type", "Type") { Width = 110f },
        new UiTableColumn("plot", "Plot") { Width = 90f },
        new UiTableColumn("status", "Status") { Width = 120f },
        new UiTableColumn(Act, "", UiTableColumnKind.Actions) { Hideable = hideableActions },
    };

    private static UiTableRow R(string id, string name, bool first = false, bool last = false) =>
        new UiTableRow(id).Text("name", name).Text("type", "Chest").Text("plot", "Plot 1").Text("status", "Linked")
            .Action("locate", UiRowActionIcon.Locate, "Find in the world")
            .Action("up", UiRowActionIcon.Up, "Move up", enabled: !first)
            .Action("down", UiRowActionIcon.Down, "Move down", enabled: !last)
            .Action("remove", UiRowActionIcon.Remove, "Remove");

    private static List<UiTableRow> Three() => new() { R("a", "Iron chest", first: true), R("b", "Wood chest"), R("c", "Stone chest", last: true) };

    private (IUiDataTable Table, UiDataTableHandle Handle) NewTable(IEnumerable<UiTableRow>? rows = null, UiDataTableOptions? options = null,
        List<UiTableColumn>? columns = null)
    {
        var panel = UiApi.Panel("Table", _owner, "example.actions");
        _panels.Add(panel);
        var table = panel.DataTable("t", columns ?? Columns(), options ?? new UiDataTableOptions { VisibleRows = 6 });
        table.SetRows(rows ?? Three());
        return (table, (UiDataTableHandle)table);
    }

    private CellRowTests.FakeCellRow Row(string key) => _runtime.Rows.Single(r => r.Text == key);

    private static CellSpec Cell(CellRowTests.FakeCellRow row, string key) => row.Cells().Single(c => c.Key == key);

    private static void Click(CellRowTests.FakeCellRow row, string cell) =>
        UiSlotTable.BootstrapDispatch(row.WiredIdOf(cell), (byte)UiSlotKind.Click, 0f, false, null);

    private static string A(int i) => UiDataTableLayout.ActionKey(Act, i);

    private static string Pad(int i) => UiDataTableLayout.ActionPadKey(Act, i);

    // ---- Layout -------------------------------------------------------------------------------------

    [Fact]
    public void An_actions_column_never_sorts_never_fills_and_is_always_shown_by_default()
    {
        var col = new UiTableColumn(Act, "", UiTableColumnKind.Actions);
        Assert.False(col.Sortable);
        Assert.False(col.Hideable);
        Assert.Equal(4, col.ActionSlots);
        var cols = new List<UiTableColumn> { col, new UiTableColumn("name", "Name"), new UiTableColumn("n", "N", UiTableColumnKind.Number) };
        Assert.Equal(1, UiDataTableLayout.FillIndex(cols)); // the actions column has no width, but it never fills
        Assert.Equal(4 * UiWidgetLayout.IconButtonWidth + 3 * CellLayout.Gap, UiDataTableLayout.WidthOf(col, fill: false));
        col.ActionSlots = 99;
        Assert.Equal(UiDataTableLayout.MaxActionSlots, UiDataTableLayout.ActionSlotsOf(col));
        col.ActionSlots = 0;
        Assert.Equal(1, UiDataTableLayout.ActionSlotsOf(col));
    }

    [Fact]
    public void A_table_takes_one_actions_column_and_sorting_by_it_is_refused()
    {
        var two = Columns();
        two.Add(new UiTableColumn("more", "", UiTableColumnKind.Actions));
        Assert.Throws<ArgumentException>(() => new UiDataTableModel(two, new UiDataTableOptions()));

        var (table, _) = NewTable();
        Assert.False(table.SortBy(Act));
        table.Sort = new[] { new UiTableSortKey(Act) };
        Assert.Empty(table.Sort);
        Assert.Null(Cell(Row("t:header"), "c:" + Act).SortKey);
    }

    [Fact]
    public void The_action_buttons_line_up_under_the_header_and_fit_at_every_checked_width()
    {
        var columns = Columns();
        foreach (bool multi in new[] { false, true })
        {
            var body = UiDataTableLayout.RowCells(columns, multi);
            var header = UiDataTableLayout.HeaderCells(columns, multi);
            foreach (var c in body.Where(c => c.Key.StartsWith("a:", StringComparison.Ordinal))) c.Visible = true;
            foreach (var c in body.Where(c => c.Kind == CellKind.Text)) c.Text = "Dark Silver Ingot";
            foreach (float width in UiWidgetLayout.CheckedRowWidths)
            {
                var placedBody = MenuPanelLayout.CellColumns(width, new CellRowSpec("row", body, rightInset: UiDataTableLayout.ScrollInset));
                var placedHeader = MenuPanelLayout.CellColumns(width, new CellRowSpec("header", header, rightInset: UiDataTableLayout.ScrollInset));
                foreach (var p in placedBody.Where(p => p.Visible))
                    Assert.True(p.Left >= -width / 2f - .01f && p.Right <= width / 2f - UiDataTableLayout.ScrollInset + .01f, p + " leaves the " + width + " row");
                for (int i = 0; i < placedBody.Length; i++)
                    for (int j = i + 1; j < placedBody.Length; j++)
                        Assert.False(CellLayout.Overlap(placedBody[i], placedBody[j]), placedBody[i] + " overlaps " + placedBody[j] + " at " + width);

                var h = placedHeader.Single(p => p.Key == "c:" + Act);
                var first = placedBody.Single(p => p.Key == A(0));
                var last = placedBody.Single(p => p.Key == A(3));
                Assert.True(h.Visible && first.Visible && last.Visible, "actions dropped at " + width);
                Assert.Equal(h.Left, first.Left, 2);
                Assert.Equal(h.Right, last.Right, 2);
                // The buttons run left to right in their order, 28 units each.
                for (int a = 0; a < 3; a++)
                    Assert.True(placedBody.Single(p => p.Key == A(a)).Right <= placedBody.Single(p => p.Key == A(a + 1)).Left + .01f);
                Assert.Equal(UiWidgetLayout.IconButtonWidth, first.Width, 2);
                // The other columns still sit under their captions.
                foreach (var col in columns.Where(c => c.Kind != UiTableColumnKind.Actions))
                {
                    var hc = placedHeader.Single(p => p.Key == "c:" + col.Key);
                    var bc = placedBody.Single(p => p.Key == "c:" + col.Key);
                    Assert.Equal(hc.Left, bc.Left, 2);
                    Assert.Equal(hc.Right, bc.Right, 2);
                }
            }
        }
    }

    // ---- Drawing and clicks ------------------------------------------------------------------------

    [Fact]
    public void Each_row_draws_its_actions_faint_when_disabled_and_blanks_where_it_has_none()
    {
        var rows = Three();
        rows.Add(new UiTableRow("d").Text("name", "Loose").Action("locate", UiRowActionIcon.Locate)); // one action only
        var (_, handle) = NewTable(rows);
        var row0 = Row("t:row0");
        Assert.Equal(new[] { "locate", "up", "down", "remove" }, Enumerable.Range(0, 4).Select(a => handle.ActionMarkAt(0, a).Name));
        Assert.All(Enumerable.Range(0, 4), a => Assert.True(Cell(row0, A(a)).Visible));
        Assert.All(Enumerable.Range(0, 4), a => Assert.False(Cell(row0, Pad(a)).Visible));
        Assert.False(handle.ActionEnabledAt(0, 1)); // the first row's up
        Assert.Equal(CellTone.Faint, Cell(row0, A(1)).Tone);
        Assert.True(handle.ActionEnabledAt(0, 2));
        Assert.False(handle.ActionEnabledAt(2, 2)); // the last data row's down

        var row3 = Row("t:row3");
        Assert.True(Cell(row3, A(0)).Visible);
        Assert.All(Enumerable.Range(1, 3), a => Assert.False(Cell(row3, A(a)).Visible));
        Assert.All(Enumerable.Range(1, 3), a => Assert.True(Cell(row3, Pad(a)).Visible)); // the places keep their width
        Assert.Equal("", handle.ActionMarkAt(3, 1).Name);
    }

    [Fact]
    public void A_click_raises_the_rows_action_and_a_disabled_one_raises_nothing()
    {
        var (table, _) = NewTable();
        var got = new List<string>();
        table.OnRowAction((row, action) => got.Add(row + "/" + action));
        Click(Row("t:row1"), A(1)); // b, up
        Click(Row("t:row0"), A(1)); // a, up: disabled
        Click(Row("t:row2"), A(3)); // c, remove
        Assert.Equal(new[] { "b/up", "c/remove" }, got);
        Assert.Empty(table.Selected); // a button click is not a row click

        Assert.True(table.PressRowAction("a", "down"));
        Assert.False(table.PressRowAction("a", "up")); // disabled
        Assert.False(table.PressRowAction("a", "nope"));
        Assert.False(table.PressRowAction("zz", "down"));
        Assert.Equal("a/down", got.Last());
    }

    [Fact]
    public void The_caller_moves_a_row_and_the_new_order_redraws_its_enabled_arrows()
    {
        var (table, handle) = NewTable();
        var order = new List<string> { "a", "b", "c" };
        string[] names = { "Iron chest", "Wood chest", "Stone chest" };
        List<UiTableRow> Build() => order.Select((id, i) => R(id, names["abc".IndexOf(id[0])], first: i == 0, last: i == order.Count - 1)).ToList();
        table.OnRowAction((row, action) =>
        {
            int i = order.IndexOf(row);
            int j = action == "up" ? i - 1 : action == "down" ? i + 1 : -1;
            if (j < 0 || j >= order.Count) return;
            (order[i], order[j]) = (order[j], order[i]);
            table.SetRows(Build());
        });
        Click(Row("t:row2"), A(1)); // c up
        Assert.Equal(new[] { "a", "c", "b" }, order);
        Assert.Equal("Stone chest", Cell(Row("t:row1"), "c:name").Text);
        Assert.True(handle.ActionEnabledAt(1, 2)); // c is no longer last
        Assert.False(handle.ActionEnabledAt(2, 2)); // b is
    }

    [Fact]
    public void Hovering_an_action_shows_its_tooltip_and_leaving_hides_it()
    {
        var (_, handle) = NewTable();
        handle.HoverAction(0, 0, true);
        Assert.Equal("Find in the world", _runtime.Tooltip?.Title);
        handle.HoverAction(0, 0, false);
        Assert.Null(_runtime.Tooltip);
        handle.HoverAction(0, 1, true); // a disabled button still says what it is
        Assert.Equal("Move up", _runtime.Tooltip?.Title);
        handle.HoverAction(0, 1, false);
        // Through the button cell's own pointer slot, as the runtime's trigger delivers it.
        UiSlotTable.BootstrapDispatch(Row("t:row2").WiredIdOf(A(3)), (byte)UiSlotKind.PointerEnter, 0f, false, null);
        Assert.Equal("Remove", _runtime.Tooltip?.Title);
        UiSlotTable.BootstrapDispatch(Row("t:row2").WiredIdOf(A(3)), (byte)UiSlotKind.PointerExit, 0f, false, null);
        Assert.Null(_runtime.Tooltip);
    }

    [Fact]
    public void Lines_that_are_not_rows_draw_blank_action_places()
    {
        var rows = Three();
        rows[0].Groups = new[] { "Ores" };
        var (_, handle) = NewTable(rows, new UiDataTableOptions { VisibleRows = 8, Grouped = true });
        // Line 0 is the Ores group header.
        var header = Row("t:row0");
        Assert.All(Enumerable.Range(0, 4), a => Assert.False(Cell(header, A(a)).Visible));
        Assert.All(Enumerable.Range(0, 4), a => Assert.True(Cell(header, Pad(a)).Visible));
        Assert.Equal("", handle.ActionMarkAt(0, 0).Name);
        Assert.Equal("locate", handle.ActionMarkAt(1, 0).Name);
    }

    [Fact]
    public void An_actions_column_made_hideable_is_still_always_shown_and_never_in_the_chooser()
    {
        var (table, _) = NewTable(columns: Columns(hideableActions: true));
        Assert.False(table.SetColumnVisible(Act, false));
        table.HiddenColumns = new[] { Act, "plot" };
        Assert.Equal(new[] { "plot" }, table.HiddenColumns);
        Assert.True(Cell(Row("t:row0"), A(0)).Visible);
        Assert.True(Cell(Row("t:header"), "c:" + Act).Visible);
        Assert.DoesNotContain(_runtime.Rows.SelectMany(r => r.Cells()), c => c.Key == UiDataTableLayout.ChipKey(Act));
        var columns = (List<Dictionary<string, object?>>)table.Describe()["columns"]!;
        Assert.Equal(false, columns.Single(c => (string)c["key"]! == Act)["hideable"]);
        // A narrow row never drops it either.
        Assert.All(UiDataTableLayout.RowCells(Columns(hideableActions: true), multi: false).Where(c => c.Key.StartsWith("a:", StringComparison.Ordinal)),
            c => Assert.False(c.Optional));
    }

    [Fact]
    public void A_click_after_scrolling_raises_the_action_of_the_row_now_in_that_slot()
    {
        var rows = Enumerable.Range(0, 20).Select(i => R("r" + i.ToString("00"), "Chest " + i, first: i == 0, last: i == 19)).ToList();
        var (table, _) = NewTable(rows, new UiDataTableOptions { VisibleRows = 5 });
        var got = new List<string>();
        table.OnRowAction((row, action) => got.Add(row + "/" + action));
        table.TopIndex = 12;
        Click(Row("t:row0"), A(3)); // slot 0 now draws r12
        Click(Row("t:row4"), A(2)); // slot 4 draws r16
        Assert.True(table.ScrollTo("r19"));
        Click(Row("t:row4"), A(2)); // r19: its down is disabled
        Click(Row("t:row4"), A(1));
        Assert.Equal(new[] { "r12/remove", "r16/down", "r19/up" }, got);
    }

    [Fact]
    public void Leaving_an_action_button_for_the_row_shows_the_rows_own_tooltip_again()
    {
        var rows = Three();
        rows[0].Tooltip = "Linked to the furnace";
        var (_, handle) = NewTable(rows);
        handle.HoverSlotForTests(0, true);
        Assert.Equal("Linked to the furnace", _runtime.Tooltip?.Body);
        handle.HoverAction(0, 2, true);
        Assert.Equal("Move down", _runtime.Tooltip?.Title);
        handle.HoverAction(0, 2, false); // still in the row
        Assert.Equal("Linked to the furnace", _runtime.Tooltip?.Body);
        handle.HoverSlotForTests(0, false);
        handle.HoverAction(0, 2, true);
        handle.HoverAction(0, 2, false); // the row was left first: nothing comes back
        Assert.Null(_runtime.Tooltip);
    }

    [Fact]
    public void The_readback_lists_each_slots_actions_and_the_column_kind()
    {
        var (table, _) = NewTable();
        var d = table.Describe();
        var columns = (List<Dictionary<string, object?>>)d["columns"]!;
        Assert.Equal("actions", columns.Single(c => (string)c["key"]! == Act)["kind"]);
        var slots = (List<Dictionary<string, object?>>)d["slots"]!;
        var actions = (List<Dictionary<string, object?>>)slots[0]["actions"]!;
        Assert.Equal(new[] { "locate", "up", "down", "remove" }, actions.Select(a => (string)a["id"]!));
        Assert.Equal(false, actions[1]["enabled"]);
        Assert.Equal(A(3), actions[3]["cell"]);
        Assert.Equal("locate", actions[0]["icon"]);
    }

    [Fact]
    public void A_row_action_needs_an_id_and_the_fluent_adder_keeps_the_order()
    {
        Assert.Throws<ArgumentException>(() => new UiRowAction(" ", UiRowActionIcon.Up));
        var row = new UiTableRow("x").Action("a", UiRowActionIcon.Chest).Action("b", UiRowActionIcon.Check, enabled: false);
        Assert.Equal(new[] { "a", "b" }, row.Actions.Select(a => a.Id));
        Assert.False(row.Actions[1].Enabled);
        row.Actions = null!;
        Assert.Empty(row.Actions);
    }

    // ---- The locate mark ------------------------------------------------------------------------------

    [Fact]
    public void The_locate_mark_is_a_ring_with_a_dot_and_ticks_and_reads_back_by_name()
    {
        var mark = UiDrawnMark.LocateMark;
        Assert.True(mark.IsIcon);
        Assert.Equal("locate", mark.Name);
        Assert.True(UiDrawnMark.TryParse("locate", out var parsed) && parsed.Equals(mark));
        Assert.False(UiDrawnMark.TryParse("locate:1", out _));
        int n = UiDrawnMarkArt.TextureHeight;
        Assert.Equal((n, n), UiDrawnMarkArt.TextureSize(mark));
        Assert.True(UiDrawnMarkArt.Alpha(mark, n / 2, n / 2) > 0.9f); // the center dot
        Assert.True(UiDrawnMarkArt.Alpha(mark, n / 2 + (int)(0.28f * n), n / 2) > 0.5f); // the ring
        Assert.True(UiDrawnMarkArt.Alpha(mark, n / 2, (int)(0.12f * n)) > 0.5f); // the bottom tick
        Assert.True(UiDrawnMarkArt.Alpha(mark, (int)(0.12f * n), (int)(0.12f * n)) < 0.1f); // the corner is empty
        Assert.True(UiDrawnMarkArt.Alpha(mark, n / 2 + (int)(0.16f * n), n / 2) < 0.1f); // between the dot and the ring
        Assert.Equal(UiDrawnMark.UpMark, UiDataTableLayout.MarkOf(UiRowActionIcon.Up));
        Assert.Equal(UiDrawnMark.RemoveMark, UiDataTableLayout.MarkOf(UiRowActionIcon.Remove));
    }

    // ---- Filler rows (MinRows) ---------------------------------------------------------------------------

    [Fact]
    public void The_viewport_counts_fillers_up_to_the_rows_in_use()
    {
        var v = new UiTableViewport(8);
        v.SetCount(2);
        Assert.Equal(4, v.Fillers(6));
        Assert.Equal(6, v.Fillers(30)); // never past the slots
        Assert.Equal(0, v.Fillers(0));
        Assert.Equal(0, v.Fillers(-3));
        v.SetCount(20);
        Assert.Equal(0, v.Fillers(8)); // the lines fill the body
    }

    [Fact]
    public void MinRows_draws_blank_rows_under_few_rows_that_take_no_click_or_selection()
    {
        var (table, handle) = NewTable(Three().Take(2), new UiDataTableOptions { VisibleRows = 8, MinRows = 6 });
        Assert.Equal(4, handle.Fillers);
        Assert.All(Enumerable.Range(0, 6), i => Assert.True(Row("t:row" + i).Visible));
        Assert.False(Row("t:row6").Visible);
        Assert.False(Row("t:row7").Visible);
        var filler = Row("t:row3");
        Assert.Equal("", Cell(filler, "c:name").Text);
        Assert.All(Enumerable.Range(0, 4), a => Assert.False(Cell(filler, A(a)).Visible));
        Assert.All(Enumerable.Range(0, 4), a => Assert.True(Cell(filler, Pad(a)).Visible));
        filler.PointerClick();
        Assert.Empty(table.Selected);
        Assert.False(filler.Selected);
        handle.HoverSlotForTests(3, true);
        Assert.Null(_runtime.Tooltip);
        Assert.Equal(4, table.Describe()["fillers"]);
        Assert.Equal(2, ((List<Dictionary<string, object?>>)table.Describe()["slots"]!).Count); // fillers are not lines

        // More rows: the fillers give way one by one, and none are left once the rows fill MinRows.
        table.SetRows(Three());
        Assert.Equal(3, handle.Fillers);
        Assert.Equal("Stone chest", Cell(Row("t:row2"), "c:name").Text);
        table.SetRows(Enumerable.Range(0, 7).Select(i => R("r" + i, "Chest " + i)));
        Assert.Equal(0, handle.Fillers);
        Assert.False(Row("t:row7").Visible);
        // No rows at all: the body keeps its height with fillers alone.
        table.SetRows(Array.Empty<UiTableRow>());
        Assert.Equal(6, handle.Fillers);
        Assert.True(Row("t:row5").Visible);
        Assert.Equal("Nothing to show.", table.StatusText);
    }

    [Fact]
    public void A_filler_is_inert_skipped_by_the_navigation_and_has_no_check_cell_until_it_is_a_row_again()
    {
        var (table, handle) = NewTable(Three().Take(2), new UiDataTableOptions { VisibleRows = 8, MinRows = 8, MultiSelect = true });
        for (int i = 2; i < 8; i++)
        {
            var filler = Row("t:row" + i);
            Assert.True(handle.IsFillerSlot(i));
            Assert.True(_runtime.Inert["t:row" + i]); // no click, no hover wash
            Assert.True(UiNav.IsSkipped(handle.SlotAt(i)));
            Assert.False(UiNav.IsStop((UiHandle)handle.SlotAt(i)));
            Assert.False(Cell(filler, UiDataTableLayout.CellCheck).Visible);
            Assert.True(Cell(filler, UiDataTableLayout.CellCheckPad).Visible); // the blank keeps the columns
        }

        // The table was built empty (all fillers), then the rows came: those slots are rows again.
        Assert.False(_runtime.Inert["t:row0"]);
        Assert.False(UiNav.IsSkipped(handle.SlotAt(0)));
        Assert.True(Cell(Row("t:row0"), UiDataTableLayout.CellCheck).Visible);
        Assert.False(Cell(Row("t:row0"), UiDataTableLayout.CellCheckPad).Visible);
        Click(Row("t:row4"), UiDataTableLayout.CellCheck);
        Assert.Empty(table.Selected);

        // More rows: the slot is a row again, with its click, wash, stop and check cell.
        table.SetRows(Three());
        Assert.False(handle.IsFillerSlot(2));
        Assert.False(_runtime.Inert["t:row2"]);
        Assert.False(UiNav.IsSkipped(handle.SlotAt(2)));
        Assert.True(Cell(Row("t:row2"), UiDataTableLayout.CellCheck).Visible);
        Assert.False(Cell(Row("t:row2"), UiDataTableLayout.CellCheckPad).Visible);
        Assert.True(_runtime.Inert["t:row3"]);
    }

    [Fact]
    public void MinRows_leaves_scrolling_past_VisibleRows_unchanged()
    {
        var rows = Enumerable.Range(0, 20).Select(i => R("r" + i.ToString("00"), "Chest " + i)).ToList();
        var (table, handle) = NewTable(rows, new UiDataTableOptions { VisibleRows = 5, MinRows = 12 });
        Assert.Equal(5, table.VisibleRows);
        Assert.Equal(0, handle.Fillers);
        table.TopIndex = 15;
        Assert.Equal(15, table.TopIndex);
        Assert.Equal("Chest 19", Cell(Row("t:row4"), "c:name").Text);
        Assert.Equal(0, handle.Fillers);
    }

    [Fact]
    public void Without_MinRows_a_short_table_hides_its_unused_slots_as_before()
    {
        var (_, handle) = NewTable(Three().Take(2), new UiDataTableOptions { VisibleRows = 6 });
        Assert.Equal(0, handle.Fillers);
        Assert.True(Row("t:row1").Visible);
        Assert.False(Row("t:row2").Visible);
    }
}
