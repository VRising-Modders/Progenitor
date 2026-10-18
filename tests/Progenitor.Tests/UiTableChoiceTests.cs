using System;
using System.Collections.Generic;
using System.Linq;
using Progenitor.Ui;
using Xunit;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.Tests;

/// <summary>
/// The data table's choice column (<see cref="UiTableColumnKind.Choice"/>, <see cref="UiTableChoice"/>,
/// <see cref="UiTableCell.Choice"/>), game-free: the cell's words, tone and icon, a click that asks for the next option
/// through <see cref="IUiDataTable.OnEdit"/> and waits for Resolve or Reject, row reuse while scrolling, filler rows, the
/// navigation stop, sorting and the readback; and the block and log marks.
/// </summary>
[Collection("UiStatic")]
public sealed class UiTableChoiceTests : IDisposable
{
    private readonly TableRuntimeFake _runtime = new();
    private readonly object _owner = new();
    private readonly List<IUiPanel> _panels = new();
    private readonly UiHelpMemoryStore _help = new();

    public UiTableChoiceTests()
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

    private const string Rule = "rule";

    private static readonly string View = UiDataTableLayout.ViewKey(Rule);

    private static readonly string Button = UiDataTableLayout.ChoiceKey(Rule);

    private static UiTableChoice[] Rules() => new[]
    {
        new UiTableChoice("allowed", "Allowed", LabelTone.Ok, UiRowActionIcon.Check),
        new UiTableChoice("blocked", "Blocked", LabelTone.Orange, UiRowActionIcon.Block),
    };

    private static List<UiTableColumn> Columns(bool editable = true) => new()
    {
        new UiTableColumn("name", "Item") { Hideable = false },
        new UiTableColumn(Rule, "Rule", UiTableColumnKind.Choice) { Editable = editable, Width = 110f, Choices = Rules() },
    };

    private static UiTableRow R(string id, string name, string rule) => new UiTableRow(id).Text("name", name).Choice(Rule, rule);

    private static List<UiTableRow> Three() => new() { R("a", "Iron ore", "allowed"), R("b", "Copper ore", "blocked"), R("c", "Stone", "allowed") };

    private (IUiDataTable Table, UiDataTableHandle Handle) NewTable(IEnumerable<UiTableRow>? rows = null, UiDataTableOptions? options = null,
        List<UiTableColumn>? columns = null)
    {
        var panel = UiApi.Panel("Table", _owner, "example.choice");
        _panels.Add(panel);
        var table = panel.DataTable("t", columns ?? Columns(), options ?? new UiDataTableOptions { VisibleRows = 6, EditTimeout = TimeSpan.Zero });
        table.SetRows(rows ?? Three());
        return (table, (UiDataTableHandle)table);
    }

    private CellRowTests.FakeCellRow Row(string key) => _runtime.Rows.Single(r => r.Text == key);

    private static CellSpec Cell(CellRowTests.FakeCellRow row, string key) => row.Cells().Single(c => c.Key == key);

    private static void Click(CellRowTests.FakeCellRow row, string cell) =>
        UiSlotTable.BootstrapDispatch(row.WiredIdOf(cell), (byte)UiSlotKind.Click, 0f, false, null);

    // ---- The cell -----------------------------------------------------------------------------------

    [Fact]
    public void A_choice_cell_shows_its_options_words_tone_and_icon_on_the_button()
    {
        var (_, handle) = NewTable();
        var row0 = Row("t:row0");
        Assert.True(Cell(row0, Button).Visible);
        Assert.False(Cell(row0, View).Visible);
        Assert.Equal("Allowed", Cell(row0, Button).Text);
        Assert.Equal(CellTone.Ok, Cell(row0, Button).Tone);
        Assert.Equal(CellSkin.Choice, Cell(row0, Button).Skin);
        Assert.Equal(("check", MenuPalette.TextColor(CellTone.Ok)), _runtime.MarkOf(row0, Button));
        Assert.Equal(UiMarkSide.Leading, _runtime.SideOf(row0, Button)); // keeps its room; a number's spinner does not

        var row1 = Row("t:row1");
        Assert.Equal("Blocked", Cell(row1, Button).Text);
        Assert.Equal(CellTone.Orange, Cell(row1, Button).Tone);
        Assert.Equal("block", handle.CellMarkAt(1, Button));
    }

    [Fact]
    public void A_click_asks_for_the_next_option_and_the_cell_waits_for_Resolve()
    {
        var (table, handle) = NewTable();
        var edits = new List<UiTableEdit>();
        table.OnEdit(edits.Add);
        Click(Row("t:row0"), Button);

        var edit = Assert.Single(edits);
        Assert.Equal("a", edit.RowId);
        Assert.Equal(Rule, edit.Column);
        Assert.Equal("allowed", edit.OldChoice);
        Assert.Equal("blocked", edit.NewChoice);
        Assert.Equal(0d, edit.OldValue);
        Assert.Equal(1d, edit.NewValue);
        Assert.False(edit.Clear);
        Assert.Equal(UiTableEditState.Pending, edit.State);

        // The pending look: the asked-for words in the pending tone after the spinner, on the button (it stays a
        // navigation stop), and a press does nothing until the answer.
        var row0 = Row("t:row0");
        Assert.True(Cell(row0, Button).Visible);
        Assert.False(Cell(row0, View).Visible);
        Assert.Equal("Blocked", Cell(row0, Button).Text);
        Assert.Equal(CellTone.Pending, Cell(row0, Button).Tone);
        Assert.StartsWith("spinner:", handle.CellMarkAt(0, Button));
        Assert.Contains(UiNav.Stops((UiHandle)handle.SlotAt(0)), t => t.Part == Button);
        Click(row0, Button);
        Assert.Single(edits);
        Assert.Contains("1 waiting", table.StatusText);
        Assert.Null(table.Edit("a", Rule, "")); // a second ask while it waits is refused
        Assert.Equal("Waiting for the server to answer the last change.", table.StatusReason);

        Assert.True(edit.Resolve());
        Assert.Equal(UiTableEditState.Resolved, edit.State);
        Assert.Equal("blocked", table.Rows[0][Rule]!.Choice); // written into the caller's row
        Assert.True(Cell(row0, Button).Visible);
        Assert.Equal("Blocked", Cell(row0, Button).Text);
        Assert.Equal(CellTone.Orange, Cell(row0, Button).Tone);
        Assert.Equal("block", handle.CellMarkAt(0, Button)); // the spinner went with the answer
        Assert.Empty(table.PendingEdits);
    }

    [Fact]
    public void Reject_rolls_the_cell_back_and_says_why()
    {
        var (table, handle) = NewTable();
        UiTableEdit? edit = null;
        table.OnEdit(e => edit = e);
        Click(Row("t:row1"), Button); // b: blocked -> allowed (the cycle wraps)
        Assert.Equal("allowed", edit!.NewChoice);
        Assert.True(edit.Reject("Only clan members can change this."));
        Assert.Equal(UiTableEditState.Rejected, edit.State);
        Assert.Equal("blocked", table.Rows[1][Rule]!.Choice);
        var row1 = Row("t:row1");
        Assert.Equal("Blocked", Cell(row1, Button).Text);
        Assert.Equal(CellTone.Orange, Cell(row1, Button).Tone);
        Assert.Equal("block", handle.CellMarkAt(1, Button));
        Assert.Equal("Copper ore, Rule: Only clan members can change this.", table.StatusReason);
        handle.HoverSlotForTests(1, true);
        Assert.Contains("Rule: Only clan members can change this.", _runtime.Tooltip?.Body);
        Assert.False(edit.Resolve()); // a late answer does nothing
    }

    [Fact]
    public void An_edit_with_no_answer_times_out_like_a_number_edit()
    {
        double now = 0;
        var (table, _) = NewTable(options: new UiDataTableOptions { VisibleRows = 6, EditTimeout = TimeSpan.FromSeconds(5), Clock = () => now });
        var edit = table.Edit("c", Rule, "");
        Assert.NotNull(edit);
        now = 6;
        _runtime.Tick();
        Assert.Equal(UiTableEditState.Rejected, edit!.State);
        Assert.Equal(UiDataTableModel.TimeoutReason, edit.Reason);
        Assert.Equal("Allowed", Cell(Row("t:row2"), Button).Text);
    }

    [Fact]
    public void Edit_takes_an_option_id_and_refuses_unknown_ones_and_the_same_one()
    {
        var (table, _) = NewTable();
        Assert.Null(table.Edit("a", Rule, "allowed")); // unchanged: nothing raised, no reason
        Assert.Equal("", table.StatusReason);
        Assert.Null(table.Edit("a", Rule, "maybe"));
        Assert.Equal("\"maybe\" is not one of the choices.", table.StatusReason);
        var edit = table.Edit("a", Rule, "blocked");
        Assert.Equal("blocked", edit!.NewChoice);
    }

    [Fact]
    public void A_row_with_no_option_starts_the_cycle_at_the_first_and_sorts_last()
    {
        var rows = Three();
        rows.Add(new UiTableRow("d").Text("name", "Wood").Set(Rule, new UiTableCell("Unset")));
        var (table, _) = NewTable(rows);
        Assert.Equal("Unset", Cell(Row("t:row3"), Button).Text); // its own text
        Assert.Equal("allowed", table.Edit("d", Rule, "")!.NewChoice);

        Assert.True(table.SortBy(Rule));
        Assert.Equal(new[] { "a", "c", "b", "d" }, ((List<Dictionary<string, object?>>)table.Describe()["slots"]!).Select(s => (string)s["id"]!));
        Assert.True(table.SortBy(Rule)); // descending: the option order flips, the empty cell stays last
        Assert.Equal(new[] { "b", "a", "c", "d" }, ((List<Dictionary<string, object?>>)table.Describe()["slots"]!).Select(s => (string)s["id"]!));
    }

    [Fact]
    public void A_locked_row_or_a_column_that_is_not_editable_draws_plain_text_with_the_icon()
    {
        var rows = Three();
        rows[0].Locked = true;
        var (table, handle) = NewTable(rows);
        var row0 = Row("t:row0");
        Assert.False(Cell(row0, Button).Visible);
        Assert.True(Cell(row0, View).Visible);
        Assert.Equal("Allowed", Cell(row0, View).Text);
        Assert.Equal(CellTone.Ok, Cell(row0, View).Tone);
        Assert.Equal("check", handle.CellMarkAt(0, View));
        Assert.Null(table.Edit("a", Rule, ""));
        Assert.Equal("You cannot change Rule here.", table.StatusReason);

        var (plain, _) = NewTable(columns: Columns(editable: false));
        Assert.Null(plain.Edit("a", Rule, ""));
        Assert.DoesNotContain(_runtime.Rows.Last(r => r.Text == "t:row0").Cells(), c => c.Key == Button);
    }

    // ---- Rows, scrolling, fillers --------------------------------------------------------------------

    [Fact]
    public void A_click_after_scrolling_asks_for_the_row_now_in_that_slot()
    {
        var rows = Enumerable.Range(0, 20).Select(i => R("r" + i.ToString("00"), "Ore " + i, i % 2 == 0 ? "allowed" : "blocked")).ToList();
        var (table, handle) = NewTable(rows, new UiDataTableOptions { VisibleRows = 5, EditTimeout = TimeSpan.Zero });
        var asked = new List<string>();
        table.OnEdit(e => asked.Add(e.RowId + "=" + e.NewChoice));
        Click(Row("t:row0"), Button); // r00: waits
        table.TopIndex = 12;
        // Slot 0 now draws r12, which does not wait: the button is back, in r12's words.
        Assert.True(Cell(Row("t:row0"), Button).Visible);
        Assert.Equal("Allowed", Cell(Row("t:row0"), Button).Text);
        Assert.Equal(CellTone.Ok, Cell(Row("t:row0"), Button).Tone);
        Assert.Equal("check", handle.CellMarkAt(0, Button));
        Click(Row("t:row0"), Button);
        Click(Row("t:row1"), Button); // r13
        Assert.Equal(new[] { "r00=blocked", "r12=blocked", "r13=allowed" }, asked);
        table.TopIndex = 0;
        Assert.Equal("Blocked", Cell(Row("t:row0"), Button).Text); // r00 still waits when it comes back
        Assert.Equal(CellTone.Pending, Cell(Row("t:row0"), Button).Tone);
        Assert.StartsWith("spinner:", handle.CellMarkAt(0, Button));
    }

    [Fact]
    public void A_refresh_while_waiting_keeps_the_spinner_and_the_answer_lands_on_the_new_row()
    {
        var (table, _) = NewTable();
        UiTableEdit? edit = null;
        table.OnEdit(e => edit = e);
        Click(Row("t:row2"), Button);
        table.SetRows(Three());
        Assert.Equal(CellTone.Pending, Cell(Row("t:row2"), Button).Tone);
        edit!.Resolve();
        Assert.Equal("blocked", table.Rows[2][Rule]!.Choice);
        Assert.Equal("Blocked", Cell(Row("t:row2"), Button).Text);
    }

    [Fact]
    public void Filler_rows_have_no_choice_button_and_take_no_click()
    {
        var (table, handle) = NewTable(Three().Take(1), new UiDataTableOptions { VisibleRows = 6, MinRows = 4 });
        Assert.Equal(3, handle.Fillers);
        var filler = Row("t:row2");
        Assert.False(Cell(filler, Button).Visible);
        Assert.True(Cell(filler, View).Visible);
        Assert.Equal("", Cell(filler, View).Text);
        Assert.Equal("", handle.CellMarkAt(2, Button));
        Assert.Equal("", handle.CellMarkAt(2, View));
        int raised = 0;
        table.OnEdit(_ => raised++);
        handle.ChoiceSlot(2, table.Columns[1]);
        Assert.Equal(0, raised);
        Assert.Empty(UiNav.Stops((UiHandle)handle.SlotAt(2)));
    }

    [Fact]
    public void Group_headers_and_drawer_lines_draw_no_choice()
    {
        var rows = Three();
        rows[0].Groups = new[] { "Ores" };
        rows[0].Detail = "Mined in the Farbane woods";
        var (table, handle) = NewTable(rows, new UiDataTableOptions { VisibleRows = 8, Grouped = true });
        table.SetExpanded("a", true);
        // Lines: Ores header, a, a's drawer line, Other header, b, c.
        var header = Row("t:row0");
        Assert.False(Cell(header, Button).Visible);
        Assert.Equal("", handle.CellMarkAt(0, View));
        var drawer = Row("t:row2");
        Assert.False(Cell(drawer, Button).Visible);
        Assert.False(Cell(drawer, View).Visible);
        Assert.True(Cell(Row("t:row1"), Button).Visible);
    }

    [Fact]
    public void A_hidden_choice_column_draws_nothing()
    {
        var (table, handle) = NewTable();
        Assert.True(table.SetColumnVisible(Rule, false));
        Assert.False(Cell(Row("t:row0"), Button).Visible);
        Assert.False(Cell(Row("t:row0"), View).Visible);
        Assert.Equal("", handle.CellMarkAt(0, Button));
    }

    [Fact]
    public void The_choice_button_is_a_navigation_stop_on_rows_that_can_change()
    {
        var rows = Three();
        rows[1].Locked = true;
        var (_, handle) = NewTable(rows);
        Assert.Contains(UiNav.Stops((UiHandle)handle.SlotAt(0)), t => t.Part == Button);
        Assert.DoesNotContain(UiNav.Stops((UiHandle)handle.SlotAt(1)), t => t.Part == Button);
    }

    // ---- Layout, types, readback ---------------------------------------------------------------------

    [Fact]
    public void A_choice_column_is_wide_enough_for_its_longest_option_and_its_icon()
    {
        var col = new UiTableColumn(Rule, "R", UiTableColumnKind.Choice) { Choices = Rules() };
        float need = CellLayout.EstimateTextWidth("Allowed", CellLayout.TextFontSize) + UiDataTableLayout.ChoiceIconRoom;
        Assert.True(UiDataTableLayout.WidthOf(col, fill: false) >= need);
        Assert.True(UiDataTableLayout.ChoiceNeed(col) >= need);
        var cols = Columns();
        var body = UiDataTableLayout.RowCells(cols, multi: false);
        var view = body.Single(c => c.Key == View);
        var button = body.Single(c => c.Key == Button);
        Assert.Equal(view.Width.Value, button.Width.Value);
        Assert.Equal(CellKind.Button, button.Kind);
        Assert.Equal(CellLayout.TextFontSize, button.FontSize);
        Assert.True(UiDataTableLayout.NeedsLeftJustify(cols, cols[1])); // after the fill column, words still read left
    }

    [Fact]
    public void Choices_need_distinct_ids_and_an_id()
    {
        Assert.Throws<ArgumentException>(() => new UiTableChoice(" ", "x"));
        var dup = new List<UiTableColumn>
        {
            new UiTableColumn(Rule, "Rule", UiTableColumnKind.Choice) { Choices = new[] { new UiTableChoice("a", "A"), new UiTableChoice("a", "B") } },
        };
        Assert.Throws<ArgumentException>(() => new UiDataTableModel(dup, new UiDataTableOptions()));
        var c = new UiTableChoice("x", "");
        Assert.Equal("x", c.Text);
        Assert.Null(c.Icon);
        Assert.Equal(UiDrawnMark.None, UiDataTableLayout.MarkOf(c));
        Assert.Null(UiDataTableModel.NextChoice(new UiTableColumn("e", "E", UiTableColumnKind.Choice), null));
    }

    [Fact]
    public void The_readback_names_the_options_each_slots_choice_and_the_waiting_edit()
    {
        var (table, _) = NewTable();
        Click(Row("t:row0"), Button);
        var d = table.Describe();
        var column = ((List<Dictionary<string, object?>>)d["columns"]!).Single(c => (string)c["key"]! == Rule);
        Assert.Equal("choice", column["kind"]);
        Assert.Equal(true, column["editable"]);
        var choices = (List<Dictionary<string, object?>>)column["choices"]!;
        Assert.Equal(new[] { "allowed", "blocked" }, choices.Select(c => (string)c["id"]!));
        Assert.Equal("block", choices[1]["icon"]);
        Assert.Equal("orange", choices[1]["tone"]);
        var slots = (List<Dictionary<string, object?>>)d["slots"]!;
        var s0 = (Dictionary<string, object?>)((Dictionary<string, object?>)slots[0]["choices"]!)[Rule]!;
        Assert.Equal("allowed", s0["choice"]);
        Assert.Equal(true, s0["pending"]);
        Assert.Equal(Button, s0["cell"]); // a waiting cell is still the button (an inert stop)
        var s1 = (Dictionary<string, object?>)((Dictionary<string, object?>)slots[1]["choices"]!)[Rule]!;
        Assert.Equal(Button, s1["cell"]);
        var pending = Assert.Single((List<Dictionary<string, object?>>)d["pending"]!);
        Assert.Equal("blocked", pending["newChoice"]);
        Assert.Equal("allowed", pending["oldChoice"]);
    }

    // ---- The block and log marks ---------------------------------------------------------------------

    [Fact]
    public void The_block_mark_is_a_ring_with_a_slash_and_reads_back_by_name()
    {
        var mark = UiDrawnMark.BlockMark;
        Assert.True(mark.IsIcon);
        Assert.Equal("block", mark.Name);
        Assert.True(UiDrawnMark.TryParse("block", out var parsed) && parsed.Equals(mark));
        Assert.False(UiDrawnMark.TryParse("block:1", out _));
        Assert.Equal(UiDrawnMark.BlockMark, UiDataTableLayout.MarkOf(UiRowActionIcon.Block));
        int n = UiDrawnMarkArt.TextureHeight;
        Assert.Equal((n, n), UiDrawnMarkArt.TextureSize(mark));
        int r = (int)(UiDrawnMarkArt.BlockRadius * n);
        Assert.True(UiDrawnMarkArt.Alpha(mark, n / 2 + r, n / 2) > 0.5f); // the ring, right
        Assert.True(UiDrawnMarkArt.Alpha(mark, n / 2, n / 2 - r) > 0.5f); // the ring, bottom
        Assert.True(UiDrawnMarkArt.Alpha(mark, n / 2, n / 2) > 0.9f); // the slash through the center
        int q = (int)(0.15f * n);
        Assert.True(UiDrawnMarkArt.Alpha(mark, n / 2 - q, n / 2 + q) > 0.5f); // the slash, top left
        Assert.True(UiDrawnMarkArt.Alpha(mark, n / 2 + q, n / 2 - q) > 0.5f); // the slash, bottom right
        Assert.True(UiDrawnMarkArt.Alpha(mark, n / 2 + q, n / 2 + q) < 0.1f); // the other diagonal is open
        Assert.True(UiDrawnMarkArt.Alpha(mark, 1, 1) < 0.1f); // the corner is empty
    }

    [Fact]
    public void The_log_mark_is_a_page_with_lines_and_a_drawn_button_icon()
    {
        var mark = UiDrawnMark.LogMark;
        Assert.True(mark.IsIcon);
        Assert.Equal("log", mark.Name);
        Assert.True(UiDrawnMark.TryParse("log", out var parsed) && parsed.Equals(mark));
        Assert.Equal(UiDrawnMark.LogMark, UiDataTableLayout.MarkOf(UiRowActionIcon.Log));
        Assert.Equal("mark:log", VanillaButtonIcon.Drawn("log").ToString());
        int n = UiDrawnMarkArt.TextureHeight;
        int Px(float share) => (int)(share * n);
        Assert.True(UiDrawnMarkArt.Alpha(mark, Px(UiDrawnMarkArt.LogLeft), n / 2) > 0.5f); // the page's left side
        Assert.True(UiDrawnMarkArt.Alpha(mark, n / 2, Px(UiDrawnMarkArt.LogTop)) > 0.5f); // its top
        foreach (float y in UiDrawnMarkArt.LogLines)
            Assert.True(UiDrawnMarkArt.Alpha(mark, Px(0.4f), Px(y)) > 0.5f, "line at " + y);
        Assert.True(UiDrawnMarkArt.Alpha(mark, n / 2, Px(0.65f)) < 0.1f); // between two lines
        Assert.True(UiDrawnMarkArt.Alpha(mark, 1, 1) < 0.1f); // outside the page
        // The stroke: about 2 of 22 pixels, as the game's rename pencil.
        Assert.InRange(UiDrawnMarkArt.LogStroke * 2f * 22f, 1.8f, 2.2f);
    }

    [Fact]
    public void The_settings_mark_is_a_cog_outline_in_the_log_stroke_and_a_drawn_button_icon()
    {
        var mark = UiDrawnMark.SettingsMark;
        Assert.True(mark.IsIcon);
        Assert.Equal("settings", mark.Name);
        Assert.True(UiDrawnMark.TryParse("settings", out var parsed) && parsed.Equals(mark));
        Assert.False(UiDrawnMark.TryParse("settings:1", out _));
        Assert.Equal(UiDrawnMark.SettingsMark, UiDataTableLayout.MarkOf(UiRowActionIcon.Settings));
        Assert.Equal("mark:settings", VanillaButtonIcon.Drawn("settings").ToString());
        int n = UiDrawnMarkArt.TextureHeight;
        Assert.Equal((n, n), UiDrawnMarkArt.TextureSize(mark));

        // Alpha at a point given in shares of the texture from its center (x right, y up).
        float At(float dx, float dy) => UiDrawnMarkArt.Alpha(mark, (int)Math.Floor((0.5f + dx) * n), (int)Math.Floor((0.5f + dy) * n));
        float Polar(float r, double degrees) => At(r * (float)Math.Cos(degrees * Math.PI / 180), r * (float)Math.Sin(degrees * Math.PI / 180));

        Assert.True(Polar(UiDrawnMarkArt.CogTip, 90) > 0.5f, "the top tooth's tip");
        Assert.True(Polar(UiDrawnMarkArt.CogTip, 150) > 0.5f, "the next tooth's tip, 60 degrees on");
        Assert.True(Polar(UiDrawnMarkArt.CogBody, 120) > 0.5f, "the body's rim between two teeth");
        Assert.True(Polar(UiDrawnMarkArt.CogTip, 120) < 0.1f, "open between two teeth");
        Assert.True(Polar((UiDrawnMarkArt.CogBody + UiDrawnMarkArt.CogTip) / 2, 90) < 0.1f, "a tooth is an outline, hollow inside");
        Assert.True(Polar(UiDrawnMarkArt.CogHub, 0) > 0.5f, "the hub ring");
        Assert.True(At(0, 0) < 0.1f, "the hub is open");
        Assert.True(Polar((UiDrawnMarkArt.CogHub + UiDrawnMarkArt.CogBody) / 2, 45) < 0.1f, "between the hub and the rim");
        Assert.True(UiDrawnMarkArt.Alpha(mark, 1, 1) < 0.1f, "the corner is empty");
        Assert.Equal(6, UiDrawnMarkArt.CogTeeth);

        // Six-fold: every tooth is the same.
        for (int k = 0; k < UiDrawnMarkArt.CogTeeth; k++)
            Assert.True(Polar(UiDrawnMarkArt.CogTip, 90 + 60 * k) > 0.5f, "tooth " + k);
    }
}
