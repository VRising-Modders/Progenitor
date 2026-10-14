using System;
using System.Collections.Generic;
using System.Linq;
using Progenitor.Ui;
using Xunit;
using UiApi = Progenitor.Ui.Ui;
using L = Progenitor.Ui.UiMasterDetailLayout;

namespace Progenitor.Tests;

/// <summary>
/// P7b: the master-detail widget through the widget fake runtime (<see cref="WidgetRuntimeFake"/>): the split at the
/// checked widths, lines drawn into the fixed rows, the two halves scrolling apart, selection, the member list reordered
/// through the ordered list's model, search, empty states, and the demo page. Every assertion is an outcome.
/// </summary>
[Collection("UiStatic")]
public sealed class UiMasterDetailTests : IDisposable
{
    private readonly WidgetRuntimeFake _runtime = new();
    private readonly object _owner = new();
    private readonly List<IUiPanel> _panels = new();

    public UiMasterDetailTests()
    {
        UiApi.ResetForTests();
        UiWheelClaims.Reset();
        UiApi.SetRuntimeForTests(_runtime);
        UiTerms.Teardown();
        UiHelpStore.UseForTests(new UiHelpMemoryStore());
        UiTerms.Clock = () => _now;
    }

    private double _now = 100;

    public void Dispose()
    {
        foreach (var p in _panels) p.Dispose();
        UiWheelClaims.Reset();
        UiTerms.Teardown();
        UiHelpStore.UseForTests(null);
        UiTerms.Clock = () => 0;
        UiWidgetLifetime.Uninstall(); // the wheel claims registered their teardown: leave the registry as found
        UiApi.ResetForTests();
    }

    private IUiPanel NewPanel()
    {
        var panel = UiApi.Panel("Master detail", _owner, "example.md");
        _panels.Add(panel);
        return panel;
    }

    private CellRowTests.FakeCellRow Row(string key) => _runtime.Rows.Single(r => r.Text == key);

    private static CellSpec Cell(CellRowTests.FakeCellRow row, string key) => row.Cells().Single(c => c.Key == key);

    private static void Click(CellRowTests.FakeCellRow row, string cell) =>
        UiSlotTable.BootstrapDispatch(row.WiredIdOf(cell), (byte)UiSlotKind.Click, 0f, false, null);

    private static List<UiMasterEntry> Entries(int n) =>
        Enumerable.Range(1, n).Select(i => new UiMasterEntry("e" + i, "Entry " + i) { Note = "note " + i }).ToList();

    private static List<UiListItem> Members(params string[] ids) =>
        ids.Select(id => new UiListItem(id, "Member " + id) { Detail = id == "b" ? "Holds ore.\nSecond line." : "" }).ToList();

    private static UiCellRowHandle Handle(UiMasterDetailHandle md, int r) => (UiCellRowHandle)md.RowAt(r);

    private UiMasterDetailHandle Build(UiMasterDetailOptions? options = null, int entries = 3)
    {
        var md = (UiMasterDetailHandle)NewPanel().MasterDetail("md", options ?? new UiMasterDetailOptions { Rows = 8, NewCaption = "+ New belt" });
        md.SetEntries(Entries(entries));
        return md;
    }

    private static List<UiDetailLine> Detail(params string[] members) => new()
    {
        UiDetailLine.Title("Entry 1", new UiDetailButton("rename", "Rename")),
        UiDetailLine.Line("3 chests · 2 plots"),
        UiDetailLine.MemberList(Members(members)),
        UiDetailLine.Actions(new UiDetailButton("add", "+ Add…"), new UiDetailButton("pick", "Pick")),
        UiDetailLine.Heading("Used by", 2),
        UiDetailLine.Link("use-1", "Input of Furnace 1", "list entry"),
        UiDetailLine.Link("use-2", "Rule, line 6"),
        UiDetailLine.Actions(new UiDetailButton("locate", "Locate all"), new UiDetailButton("delete", "Delete") { Enabled = false }),
    };

    // ---- the split ------------------------------------------------------------------------------

    [Theory]
    [InlineData(560f)]
    [InlineData(760f)]
    [InlineData(1108f)]
    [InlineData(1248f)]
    public void The_split_clamps_the_caption_and_keeps_the_divider_still(float width)
    {
        float f = L.DefaultFraction;
        float title = L.TitleWidth(width, f);
        Assert.InRange(title, L.TitleMin, L.TitleMax);
        Assert.InRange(L.NoteWidth(width, f), L.NoteMin, L.NoteMax);
        Assert.True(L.TitleWidth(10000f, f) == L.TitleMax, "the caption is capped on a very wide row");
        Assert.Equal(L.TitleMin, L.TitleWidth(100f, f));
        // The master half grows with the row but never past its caps.
        Assert.True(L.MasterWidth(1248f, f, true, true) >= L.MasterWidth(560f, f, true, true));
        Assert.True(L.DetailWidth(width, f, true, true) > UiWidgetLayout.CaptionMinWidth + 3 * UiWidgetLayout.IconButtonWidth,
            "the detail half holds a member caption and its arrows and remove at " + width);
    }

    [Fact]
    public void The_fraction_and_rows_are_clamped()
    {
        Assert.Equal(L.DefaultFraction, L.FractionFor(0f));
        Assert.Equal(L.MinFraction, L.FractionFor(0.01f));
        Assert.Equal(L.MaxFraction, L.FractionFor(0.9f));
        Assert.Equal(L.DefaultRows, L.RowsFor(0));
        Assert.Equal(L.MinRows, L.RowsFor(2));
        Assert.Equal(L.MaxRows, L.RowsFor(99));
    }

    /// <summary>
    /// Every row the widget draws — the search row, entries, the new line, a title with a button, text, a heading, links,
    /// two-button action lines, members with every column, a drawer, an empty line — resolved at every checked width, with
    /// and without master icons and counts: no cell leaves the row, none overlaps, none is under its floor, the master
    /// caption keeps 120, and the divider is at the same x on every row (the formula's).
    /// </summary>
    [Fact]
    public void Every_row_fits_and_the_divider_lines_up_at_every_checked_width()
    {
        foreach (bool icons in new[] { false, true })
        foreach (bool counts in new[] { false, true })
        {
            _runtime.Rows.Clear();
            var entries = Entries(3);
            if (icons) entries[0].ItemPrefab = 7;
            if (counts) entries[1].Badge = "12";
            var md = (UiMasterDetailHandle)NewPanel().MasterDetail("md", new UiMasterDetailOptions { Rows = 14, NewCaption = "+ New belt" });
            md.SetEntries(entries);
            md.Select("e1");
            var members = Members("a", "b", "c");
            members[0].ItemPrefab = 5;
            members[0].Note = "plot 2 · 40 m";
            members[0].Badge = "3";
            md.SetDetail(new[]
            {
                UiDetailLine.Title("A belt with a long name on the right side", new UiDetailButton("rename", "Rename")),
                UiDetailLine.Line("3 chests · 2 plots"),
                UiDetailLine.MemberList(members),
                UiDetailLine.Actions(new UiDetailButton("add", "+ Add…"), new UiDetailButton("pick", "Pick")),
                UiDetailLine.Heading("Used by", 4),
                UiDetailLine.Link("u", "Input of Furnace 1", "list entry"),
                UiDetailLine.Actions(new UiDetailButton("locate", "Locate all"), new UiDetailButton("delete", "Delete")),
            });
            Assert.True(md.SetMemberExpanded("b", true));

            foreach (float width in UiWidgetLayout.CheckedRowWidths)
            {
                float? dividerLeft = null;
                float expected = -width / 2f + L.MasterWidth(width, L.DefaultFraction, icons, counts);
                for (int r = 0; r < md.Rows; r++)
                {
                    var cells = Row("md:row" + r).Cells();
                    var placed = MenuPanelLayout.CellColumns(width, cells.ToList());
                    string where = "row " + r + " at " + width + (icons ? " icons" : "") + (counts ? " counts" : "");
                    foreach (var p in placed.Where(p => p.Visible))
                    {
                        Assert.True(p.Left >= -width / 2f - .01f && p.Right <= width / 2f + .01f, p + " leaves the row, " + where);
                        Assert.True(p.Width >= CellLayout.MinWidthOf(cells[p.Index]) - .01f, p + " under its floor, " + where);
                    }

                    for (int i = 0; i < placed.Length; i++)
                        for (int j = i + 1; j < placed.Length; j++)
                            Assert.False(CellLayout.Overlap(placed[i], placed[j]), placed[i] + " overlaps " + placed[j] + ", " + where);

                    var divider = placed.Single(p => p.Key == L.CellDivider);
                    Assert.True(divider.Visible, where);
                    dividerLeft ??= divider.Left;
                    Assert.Equal(dividerLeft.Value, divider.Left, 2);
                    Assert.Equal(expected, divider.Left, 1);
                    var caption = placed.FirstOrDefault(p => p.Key == L.CellMTitle || p.Key == L.CellMSearch);
                    Assert.True(caption.Width >= UiWidgetLayout.CaptionMinWidth - .01f, "master caption squeezed, " + where);
                    // Every visible detail cell is right of the divider.
                    foreach (var p in placed.Where(p => p.Visible && L.DetailKeys.Contains(p.Key)))
                        Assert.True(p.Left >= divider.Right - .01f, p + " crosses the divider, " + where);
                }
            }

            md.Dispose();
        }
    }

    // ---- lines into rows ---------------------------------------------------------------------------

    [Fact]
    public void Lines_are_drawn_into_the_fixed_rows_by_kind()
    {
        var md = Build(new UiMasterDetailOptions { Rows = 14, NewCaption = "+ New belt" });
        md.Select("e1");
        md.SetDetail(Detail("a", "b"));

        // Row 0: the search field on the left, the title line on the right with its button.
        var r0 = Row("md:row0");
        Assert.Equal("Entry 1", Cell(r0, L.CellText).Text);
        Assert.True(Cell(r0, L.CellText).Visible);
        Assert.Equal("Rename", Cell(r0, L.CellButton(0)).Text);
        Assert.True(Cell(r0, L.CellButton(0)).Visible);
        Assert.False(Cell(r0, L.CellButton(1)).Visible);
        Assert.Equal("3", Cell(r0, L.CellMNote).Text); // the search row's status: the entry count

        // Row 1: entry 1, selected: the entry band on its master half only, beside the muted fact line.
        var r1 = Row("md:row1");
        Assert.Equal("Entry 1", Cell(r1, L.CellMTitle).Text);
        Assert.Equal(UiTileState.Selected, Handle(md, 1).TileStateOf(L.MasterTile));
        Assert.Equal(UiTileState.Hidden, Handle(md, 2).TileStateOf(L.MasterTile));
        Assert.Equal(UiTileState.Rule, Handle(md, 1).TileStateOf(L.DividerTile));
        Assert.Equal("3 chests · 2 plots", Cell(r1, L.CellText).Text);
        Assert.Equal(CellTone.Muted, Cell(r1, L.CellText).Tone);

        // Rows 2 and 3: the members, with grip, arrows, expander and remove; the first member's up is faint.
        var r2 = Row("md:row2");
        Assert.Equal("Entry 2", Cell(r2, L.CellMTitle).Text);
        Assert.Equal(CellTone.Normal, Cell(r2, L.CellMTitle).Tone);
        Assert.Equal("", Cell(r2, L.CellMark).Text); // no glyph: the lead only makes room for the band's stripe
        Assert.Equal("Member a", Cell(r2, L.CellText).Text);
        foreach (var key in new[] { L.CellGrip, L.CellUp, L.CellDown, L.CellExpand, L.CellRemove }) Assert.True(Cell(r2, key).Visible, key);
        // The badge and the glyph buttons are packed from the row's right edge, in the ordered list's order.
        var placedR2 = MenuPanelLayout.CellColumns(1108f, r2.Cells().ToList());
        Assert.Equal(1108f / 2f, placedR2.Single(p => p.Key == L.CellRemove).Right, 1);
        Assert.True(placedR2.Single(p => p.Key == L.CellUp).Right < placedR2.Single(p => p.Key == L.CellDown).Left);
        Assert.Equal(CellTone.Faint, Cell(r2, L.CellUp).Tone);
        Assert.Equal(CellTone.Faint, Cell(r2, L.CellExpand).Tone); // a has no drawer
        Assert.Equal(CellTone.Normal, Cell(Row("md:row3"), L.CellExpand).Tone);

        // Row 4: the end of the master list is the new-entry line; the detail's action line has two buttons and no text.
        var r4 = Row("md:row4");
        Assert.Equal("+ New belt", Cell(r4, L.CellMTitle).Text);
        Assert.Equal(CellTone.Info, Cell(r4, L.CellMTitle).Tone);
        Assert.False(Cell(r4, L.CellText).Visible);
        Assert.Equal(new[] { "+ Add…", "Pick" }, new[] { Cell(r4, L.CellButton(0)).Text, Cell(r4, L.CellButton(1)).Text });

        // Heading in gold, links with a note, the disabled button drawn disabled.
        Assert.Equal("Used by (2)", Cell(Row("md:row5"), L.CellText).Text);
        Assert.Equal(CellTone.Gold, Cell(Row("md:row5"), L.CellText).Tone);
        Assert.Equal("Input of Furnace 1", Cell(Row("md:row6"), L.CellLink).Text);
        Assert.True(Cell(Row("md:row6"), L.CellDNote).Visible);
        Assert.False(Cell(Row("md:row7"), L.CellDNote).Visible);
        Assert.Equal(CellTone.Disabled, Cell(Row("md:row8"), L.CellButton(1)).Tone);

        // Past the last line: every detail cell hidden, the master empty.
        var r9 = Row("md:row9");
        Assert.All(L.DetailKeys, k => Assert.False(Cell(r9, k).Visible, k));
        Assert.Equal("", Cell(r9, L.CellMTitle).Text);
    }

    [Fact]
    public void Buttons_links_and_the_new_line_raise_actions_and_a_disabled_button_does_not()
    {
        var md = Build(new UiMasterDetailOptions { Rows = 14, NewCaption = "+ New belt" });
        var actions = new List<UiDetailAction>();
        md.OnAction(actions.Add);
        md.Select("e1");
        md.SetDetail(Detail("a", "b"));

        Click(Row("md:row0"), L.CellButton(0));       // Rename
        Click(Row("md:row6"), L.CellLink);            // a link
        Click(Row("md:row8"), L.CellButton(1));       // Delete, disabled
        Row("md:row4").PointerClick();                // + New belt
        Assert.Equal(new[] { "title/rename", "use-1/open", "master/new" }, actions.Select(a => a.LineId + "/" + a.ActionId));
        Assert.Equal("e1", actions[0].EntryId);
        Assert.Equal(0, actions[0].Tab);
    }

    // ---- selection -------------------------------------------------------------------------------

    [Fact]
    public void A_row_click_on_the_master_half_selects_and_one_on_the_detail_half_does_not()
    {
        var md = Build();
        var selected = new List<string>();
        md.OnSelected(selected.Add);
        float dividerX = Row("md:row1").CellRects().Single(p => p.Key == L.CellDivider).Value.MinX;

        _runtime.Pointer = PointerSample.At(dividerX + 200f, 120f); // over this row's detail half
        Assert.True(Row("md:row2").PointerClick());
        Assert.Empty(selected);

        _runtime.Pointer = PointerSample.At(dividerX - 100f, 120f); // over the master half
        Row("md:row2").PointerClick();
        Assert.Equal(new[] { "e2" }, selected);
        Assert.Equal("e2", md.SelectedId);

        Row("md:row2").PointerClick(); // the same entry again: no change, no event
        Assert.Single(selected);
        Assert.True(md.Select(null));
        Assert.Equal(new[] { "e2", "" }, selected);
        Assert.False(md.Select("nope"));
    }

    [Fact]
    public void Hover_selected_and_selected_hover_are_distinct_bands_on_the_master_half_only()
    {
        var md = Build();
        md.Select("e1");
        float dividerX = Row("md:row1").CellRects().Single(p => p.Key == L.CellDivider).Value.MinX;

        _runtime.Pointer = PointerSample.At(dividerX - 100f, 120f); // over the master half of every fake row
        Row("md:row2").FireHoverForTests(md, 2, true);
        Assert.Single(_runtime.Ticks); // the half is followed while the row is hovered
        Assert.Equal(UiTileState.Hover, Handle(md, 2).TileStateOf(L.MasterTile));
        Row("md:row1").FireHoverForTests(md, 1, true);
        Assert.Equal(UiTileState.SelectedHover, Handle(md, 1).TileStateOf(L.MasterTile));
        Assert.Equal(UiTileState.Hidden, Handle(md, 2).TileStateOf(L.MasterTile)); // one hovered row at a time

        _runtime.Pointer = PointerSample.At(dividerX + 200f, 120f); // moved onto the detail half
        _runtime.Tick();
        Assert.Equal(UiTileState.Selected, Handle(md, 1).TileStateOf(L.MasterTile));
        Row("md:row1").FireHoverForTests(md, 1, false);
        Assert.Empty(_runtime.Ticks);
        Assert.Equal(UiTileState.Selected, Handle(md, 1).TileStateOf(L.MasterTile));

        // The four looks are different colors, and only the selected ones carry the stripe.
        var fills = new[] { UiTileState.Hover, UiTileState.Selected, UiTileState.SelectedHover, UiTileState.Rule }
            .Select(s => UiTileLook.Fill(s).ToString()).ToList();
        Assert.Equal(fills.Count, fills.Distinct().Count());
        Assert.True(UiTileLook.Stripe(UiTileState.Selected) && UiTileLook.Stripe(UiTileState.SelectedHover));
        Assert.False(UiTileLook.Stripe(UiTileState.Hover));
        // The band is the Mods list entry's own.
        Assert.Equal(ModsListLayout.RowFill(true).ToString(), UiTileLook.Fill(UiTileState.Selected).ToString());
    }

    [Fact]
    public void The_band_spans_the_master_cells_and_the_rule_the_divider()
    {
        var md = Build();
        var cells = Row("md:row1").Cells().ToList();
        var placed = MenuPanelLayout.CellColumns(760f, cells);
        var spans = CellTiles.Spans(placed, cells);
        var band = spans.Single(s => s.Tile == L.MasterTile);
        var rule = spans.Single(s => s.Tile == L.DividerTile);
        Assert.Equal(-380f, band.Left, 1); // from the row's left edge
        Assert.True(band.Right <= rule.Left, "the band reaches into the divider");
        Assert.Equal(L.DividerWidth, rule.Width, 1);
        Assert.DoesNotContain(Row("md:row0").Cells(), c => c.Tile == L.MasterTile); // the search row has no band
        Assert.All(Enumerable.Range(0, md.Rows), r => Assert.True(Row("md:row" + r).Cells().Single(c => c.Key == L.CellDivider).Tile == L.DividerTile));
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("7", "  7")]
    [InlineData("12", " 12")]
    [InlineData("99+", "99+")]
    [InlineData("12345", "123")]
    public void Master_counts_are_right_aligned_in_tabular_figures(string badge, string drawn)
    {
        Assert.Equal(drawn, L.CountText(badge));
        var md = Build();
        md.SetEntries(new[] { new UiMasterEntry("a", "A") { Badge = badge }, new UiMasterEntry("b", "B") { Badge = "5" } });
        var cell = Cell(Row("md:row1"), L.CellMCount);
        Assert.Equal(CellKind.Mono, cell.Kind);
        Assert.Equal(drawn, cell.Text);
    }

    [Fact]
    public void Selecting_reveals_the_entry_and_set_entries_drops_a_selection_that_left()
    {
        var md = Build(new UiMasterDetailOptions { Rows = 6 }, entries: 20); // 5 master slots under the search row
        Assert.True(md.Select("e12"));
        Assert.InRange(11, md.MasterTop, md.MasterTop + 4); // e12 is line 11
        md.SetEntries(Entries(5));
        Assert.Null(md.SelectedId);
        Assert.Equal(0, md.MasterTop);
    }

    // ---- scrolling -------------------------------------------------------------------------------

    [Fact]
    public void The_halves_scroll_independently_and_clamp()
    {
        var md = Build(new UiMasterDetailOptions { Rows = 6, NewCaption = "+ New" }, entries: 12);
        md.Select("e1");
        md.SetDetail(Detail("a", "b", "c", "d", "e"));
        // Master: 12 entries + the new line = 13 lines in 5 slots; detail: 5 + 5 members + 4 = 12 lines in 6 rows.
        Assert.True(md.ScrollMaster(3));
        Assert.Equal(3, md.MasterTop);
        Assert.Equal(0, md.DetailTop);
        Assert.Equal("Entry 4", Cell(Row("md:row1"), L.CellMTitle).Text);
        Assert.Equal("3 chests · 2 plots", Cell(Row("md:row1"), L.CellText).Text); // the detail did not move

        Assert.True(md.ScrollDetail(100));
        Assert.Equal(6, md.DetailTop); // 12 lines - 6 rows
        Assert.Equal(3, md.MasterTop);
        Assert.False(md.ScrollDetail(1)); // at the end
        Assert.True(md.ScrollMaster(100));
        Assert.Equal(8, md.MasterTop); // 13 - 5
        Assert.Equal("+ New", Cell(Row("md:row5"), L.CellMTitle).Text);
        Assert.Equal("9–12 of 12", Cell(Row("md:row0"), L.CellMNote).Text);
    }

    [Fact]
    public void The_wheel_over_each_half_scrolls_that_half_and_hands_on_at_the_end()
    {
        var md = Build(new UiMasterDetailOptions { Rows = 6, NewCaption = "+ New" }, entries: 12);
        md.Select("e1");
        md.SetDetail(Detail("a", "b", "c", "d", "e"));
        _panels.Last().Show(); // the halves take the wheel only while the panel is shown
        float dividerX = Row("md:row0").CellRects().Single(p => p.Key == L.CellDivider).Value.MinX;

        Assert.True(UiWheelClaims.TryTake(dividerX - 50f, 120f, -120f)); // toward the end, over the master half
        Assert.Equal(L.LinesPerNotch, md.MasterTop);
        Assert.Equal(0, md.DetailTop);
        Assert.True(UiWheelClaims.TryTake(dividerX + 300f, 120f, -120f)); // over the detail half
        Assert.Equal(L.LinesPerNotch, md.DetailTop);
        Assert.True(UiWheelClaims.TryTake(dividerX + 300f, 120f, 120f)); // back toward the start
        Assert.Equal(0, md.DetailTop);
        while (md.ScrollDetail(1)) { }
        Assert.False(UiWheelClaims.TryTake(dividerX + 300f, 120f, -120f)); // at its end: the panel takes the notch
    }

    [Fact]
    public void Controller_up_on_the_first_entry_row_and_down_on_the_last_row_scroll_the_master_list()
    {
        var md = Build(new UiMasterDetailOptions { Rows = 6 }, entries: 12);
        Assert.False(md.Steer(1, NavCommand.Up)); // at the top: the focus may leave (to the search row)
        Assert.True(md.Steer(5, NavCommand.Down));
        Assert.Equal(1, md.MasterTop);
        Assert.False(md.Steer(3, NavCommand.Down)); // not the last row: the focus moves
        Assert.True(md.Steer(1, NavCommand.Up));
        Assert.Equal(0, md.MasterTop);
    }

    // ---- members ---------------------------------------------------------------------------------

    [Fact]
    public void Member_arrows_remove_and_expand_use_the_ordered_list_model()
    {
        var md = Build(new UiMasterDetailOptions { Rows = 14 });
        md.Select("e1");
        md.SetDetail(Detail("a", "b", "c"));
        var moves = new List<UiListReorder>();
        var removed = new List<string>();
        md.OnReorder(moves.Add);
        md.OnRemove(i => removed.Add(i.Id));

        Click(Row("md:row2"), L.CellDown); // a: 0 -> 1
        Assert.Equal(new[] { "b", "a", "c" }, md.Members.Select(m => m.Id));
        Assert.Equal("a 0->1", moves.Single().ToString());
        Assert.Equal("Member b", Cell(Row("md:row2"), L.CellText).Text); // rewritten in place

        Click(Row("md:row2"), L.CellExpand); // b's drawer: two lines under it
        Assert.Equal("Holds ore.", Cell(Row("md:row3"), L.CellText).Text);
        Assert.Equal("Second line.", Cell(Row("md:row4"), L.CellText).Text);
        Assert.True(Cell(Row("md:row3"), L.CellIndent).Visible);
        Assert.Equal("Member a", Cell(Row("md:row5"), L.CellText).Text);

        Click(Row("md:row5"), L.CellRemove);
        Assert.Equal(new[] { "a" }, removed);
        Assert.Equal(new[] { "b", "c" }, md.Members.Select(m => m.Id));
        Assert.True(md.MoveMember("c", 0));
        Assert.Equal(new[] { "c", "b" }, md.Members.Select(m => m.Id));
    }

    [Fact]
    public void A_grip_drag_moves_a_member_through_the_shared_drag_step()
    {
        var md = Build(new UiMasterDetailOptions { Rows = 8 });
        md.Select("e1");
        md.SetDetail(new[] { UiDetailLine.Title("E"), UiDetailLine.MemberList(Members("a", "b", "c")) });
        var moves = new List<UiListReorder>();
        md.OnReorder(moves.Add);
        for (int r = 0; r < md.Rows; r++) // rows stacked down the screen, 40 px apart (up is larger)
            Row("md:row" + r).RectOverride = ScreenRect.FromCorners(0f, 600f - 40f * (r + 1), 1664f, 600f - 40f * r);

        _runtime.Held = true;
        _runtime.Press(Row("md:row1"), L.CellGrip, UiPointerEventKind.Down); // member a
        Assert.Single(_runtime.Ticks);
        _runtime.Pointer = PointerSample.At(1000f, 0f); // below every row: the last member slot
        _runtime.Tick();
        Assert.Equal("Member a", Cell(Row("md:row3"), L.CellText).Text); // the preview
        Assert.Equal(CellTone.Gold, Cell(Row("md:row3"), L.CellText).Tone);
        Assert.Empty(moves);
        _runtime.Held = false;
        _runtime.Tick(); // a release the grip did not see
        Assert.Equal(new[] { "b", "c", "a" }, md.Members.Select(m => m.Id));
        Assert.Single(moves);
        Assert.Empty(_runtime.Ticks);
    }

    [Fact]
    public void A_second_member_list_and_duplicate_ids_are_refused_without_changing_anything()
    {
        var md = Build();
        md.SetDetail(new[] { UiDetailLine.MemberList(Members("a")) });
        Assert.Throws<ArgumentException>(() => md.SetDetail(new[] { UiDetailLine.MemberList(Members("x")), UiDetailLine.MemberList(Members("y")) }));
        Assert.Throws<ArgumentException>(() => md.SetDetail(new[] { UiDetailLine.MemberList(Members("x", "x")) }));
        Assert.Equal(new[] { "a" }, md.Members.Select(m => m.Id));
        Assert.Throws<ArgumentException>(() => UiDetailLine.Actions(new UiDetailButton("1", "a"), new UiDetailButton("2", "b"),
            new UiDetailButton("3", "c"), new UiDetailButton("4", "d")));
        Assert.Throws<ArgumentException>(() => md.SetEntries(new[] { new UiMasterEntry("x", "X"), new UiMasterEntry("x", "Y") }));
    }

    // ---- search and empty states ---------------------------------------------------------------------

    [Fact]
    public void Search_filters_by_title_note_and_keywords_and_keeps_the_callers_order()
    {
        var md = Build(new UiMasterDetailOptions { Rows = 8, NewCaption = "+ New belt" }, entries: 0);
        md.SetEntries(new[]
        {
            new UiMasterEntry("ore", "Ore in") { Note = "plot 2" },
            new UiMasterEntry("line", "Line 1") { Keywords = new[] { "furnace" } },
            new UiMasterEntry("jewel", "Jewel cabinets") { Note = "here" },
            new UiMasterEntry("core", "Core room") { Note = "plot 3" },
        });
        var searches = new List<string>();
        md.OnSearch(searches.Add);

        md.Query = "plot";
        Assert.Equal(new[] { "ore", "core" }, md.Matches.Select(e => e.Id)); // caller's order, by note
        Assert.Equal("2 of 4", Cell(Row("md:row0"), L.CellMNote).Text);
        md.Query = "FURN";
        Assert.Equal(new[] { "line" }, md.Matches.Select(e => e.Id)); // keyword, folded
        md.Query = "ore";
        Assert.Equal(new[] { "ore", "core" }, md.Matches.Select(e => e.Id)); // prefix and substring both match
        md.Query = "zzz";
        Assert.Empty(md.Matches);
        Assert.Equal("No match for \"zzz\".", Cell(Row("md:row1"), L.CellMTitle).Text);
        Assert.Equal(CellTone.Muted, Cell(Row("md:row1"), L.CellMTitle).Tone);
        Assert.Equal("+ New belt", Cell(Row("md:row2"), L.CellMTitle).Text);
        Assert.Equal(new[] { "plot", "FURN", "ore", "zzz" }, searches);
        Assert.Equal("zzz", Cell(Row("md:row0"), L.CellMSearch).Text);
    }

    [Fact]
    public void Empty_states_fill_both_halves()
    {
        var md = Build(new UiMasterDetailOptions
        {
            Rows = 6,
            NewCaption = "+ New belt",
            EmptyText = "No belts yet.",
            DetailEmptyText = "A belt is an ordered set of containers.\nMake one with + New belt.",
        }, entries: 0);
        Assert.Equal("No belts yet.", Cell(Row("md:row1"), L.CellMTitle).Text);
        Assert.Equal("+ New belt", Cell(Row("md:row2"), L.CellMTitle).Text);
        Assert.Equal("A belt is an ordered set of containers.", Cell(Row("md:row0"), L.CellText).Text);
        Assert.Equal("Make one with + New belt.", Cell(Row("md:row1"), L.CellText).Text);
        Assert.Equal(CellTone.Muted, Cell(Row("md:row1"), L.CellText).Tone);
        Assert.Equal("", Cell(Row("md:row0"), L.CellMNote).Text); // no count with no entries

        md.SetEntries(Entries(1));
        md.Select("e1");
        md.SetDetail(new[] { UiDetailLine.Title("Entry 1"), UiDetailLine.MemberList(Array.Empty<UiListItem>(), emptyText: "No chests on this belt yet.") });
        Assert.Equal("No chests on this belt yet.", Cell(Row("md:row1"), L.CellText).Text);
        Assert.Empty(md.Members);
    }

    // ---- tabs ------------------------------------------------------------------------------------

    [Fact]
    public void Each_tab_keeps_its_own_entries_selection_query_and_detail()
    {
        var md = (UiMasterDetailHandle)NewPanel().MasterDetail("md", new UiMasterDetailOptions { Tabs = new[] { "Belts", "Categories" }, Rows = 8 });
        Assert.NotNull(md.Tabs);
        Assert.True(Row("md:tabs:tabs").Visible);
        md.SetEntries(0, Entries(2));
        md.SetEntries(1, new[] { new UiMasterEntry("flowers", "Flowers") });
        md.SetTexts(1, newCaption: "+ New category");
        md.Select("e2");
        md.SetDetail(new[] { UiDetailLine.Title("Entry 2") });
        md.Query = "entry";

        md.Tabs!.Selected = 1;
        Assert.Equal(1, md.Tab);
        Assert.Null(md.SelectedId);
        Assert.Equal("", md.Query);
        Assert.Equal("Flowers", Cell(Row("md:row1"), L.CellMTitle).Text);
        Assert.Equal("+ New category", Cell(Row("md:row2"), L.CellMTitle).Text);
        Assert.Equal("", Cell(Row("md:row0"), L.CellMSearch).Text);

        md.Tabs.Selected = 0;
        Assert.Equal("e2", md.SelectedId);
        Assert.Equal("entry", md.Query);
        Assert.Equal("Entry 2", Cell(Row("md:row0"), L.CellText).Text);
    }

    [Fact]
    public void Hiding_and_disposing_release_the_rows_the_drag_and_the_wheel()
    {
        var md = Build();
        md.SetDetail(new[] { UiDetailLine.MemberList(Members("a", "b")) });
        _runtime.Held = true;
        _runtime.Press(Row("md:row0"), L.CellGrip, UiPointerEventKind.Down);
        Assert.Single(_runtime.Ticks);
        md.Visible = false;
        Assert.Empty(_runtime.Ticks);
        Assert.False(Row("md:row0").Visible);
        Assert.Equal(2, UiWheelClaims.Count);
        md.Dispose();
        Assert.Equal(0, UiWheelClaims.Count);
        Assert.True(_runtime.Rows.Where(r => r.Text.StartsWith("md:", StringComparison.Ordinal)).All(r => r.Released));
    }

    [Fact]
    public void On_a_runtime_that_cannot_draw_the_widget_keeps_state()
    {
        UiApi.SetRuntimeForTests(new CellRowTests.PlainRuntime());
        var md = NewPanel().MasterDetail("md");
        md.SetEntries(Entries(2));
        Assert.True(md.Select("e1"));
        md.SetDetail(new[] { UiDetailLine.MemberList(Members("a", "b")) });
        Assert.True(md.MoveMember("b", 0));
        Assert.Equal(new[] { "b", "a" }, md.Members.Select(m => m.Id));
        Assert.Equal("masterdetail", md.Describe()["kind"]);
    }

    // ---- terms ---------------------------------------------------------------------------

    [Fact]
    public void Entry_and_heading_terms_follow_their_rows_and_explain_on_their_own_half()
    {
        UiTerms.Define("example.md", "Belt", "Chests used together.");
        UiTerms.Define("example.md", "Used by", "What names this belt.");
        var md = Build(new UiMasterDetailOptions { Rows = 8 }, entries: 0);
        md.SetEntries(new[]
        {
            new UiMasterEntry("a", "Ore in") { Term = "Belt", Tooltip = "ignored while the term explains" },
            new UiMasterEntry("b", "Line 1") { Tooltip = "Its own tooltip." },
        });
        md.SetDetail(new[] { UiDetailLine.Title("Ore in"), UiDetailLine.Heading("Used by", 2, term: "Used by"), UiDetailLine.Line("x") });
        Assert.Equal("Belt", md.MasterTermAt(1));
        Assert.Equal("", md.MasterTermAt(2));
        Assert.Equal("", md.MasterTermAt(0)); // the search row
        Assert.Equal("Used by", md.DetailTermAt(1));
        Assert.Equal("", md.DetailTermAt(0));
        Assert.Equal(2, UiTerms.Marked.Count);

        // The marker follows the entry when the list filters (rows are reused slots).
        md.Query = "line";
        Assert.Equal("", md.MasterTermAt(1));
        md.Query = "";
        Assert.Equal("Belt", md.MasterTermAt(1));

        float dividerX = Row("md:row1").CellRects().Single(p => p.Key == L.CellDivider).Value.MinX;
        _runtime.Pointer = PointerSample.At(dividerX - 100f, 120f); // the master half
        md.HoverRowForTests(1, true);
        Assert.Equal(("Belt", "Chests used together."), _runtime.Tooltip);
        Assert.Equal(1, UiTerms.Hovers("example.md", "Belt"));
        _runtime.Pointer = PointerSample.At(dividerX + 200f, 120f); // the detail half: row 1 shows the heading
        _runtime.Tick();
        Assert.Equal(("Used by", "What names this belt."), _runtime.Tooltip);
        md.HoverRowForTests(1, false);
        Assert.Null(_runtime.Tooltip);

        _runtime.Pointer = PointerSample.At(dividerX - 100f, 120f);
        md.HoverRowForTests(2, true); // no term: the entry's own tooltip
        Assert.Equal(("Line 1", "Its own tooltip."), _runtime.Tooltip);
        md.HoverRowForTests(2, false);

        md.Dispose();
        Assert.Empty(UiTerms.Marked);
    }

    [Fact]
    public void An_undefined_term_keeps_the_plain_tooltip()
    {
        var md = Build(new UiMasterDetailOptions { Rows = 6 }, entries: 0);
        md.SetEntries(new[] { new UiMasterEntry("a", "Ore in") { Term = "Nowhere", Tooltip = "Plain." } });
        float dividerX = Row("md:row1").CellRects().Single(p => p.Key == L.CellDivider).Value.MinX;
        _runtime.Pointer = PointerSample.At(dividerX - 100f, 120f);
        md.HoverRowForTests(1, true);
        Assert.Equal(("Ore in", "Plain."), _runtime.Tooltip);
    }

    // ---- the demo ----------------------------------------------------------------------------------

    private sealed class RepeatHud : Progenitor.ControlsSample.IWidgetsHud, Progenitor.ControlsSample.IWidgetsRepeatPickHud
    {
        public void Notify(string text, string detail, UiNoticeKind kind) { }
        public void Pick(Action<string> done) => done("Container ContainerMenu (1:1)");
        public string LocatePicked() => "pinged";

        public void PickSeveral(Action<string, int> picked, Action<int> finished)
        {
            picked("Container ContainerMenu (1:1)", 1);
            picked("Container ContainerMenu (2:1)", 2);
            finished(2);
        }
    }

    [Fact]
    public void The_demo_builds_a_belts_screen_and_reports_every_event()
    {
        var panel = NewPanel();
        var log = new List<string>();
        var demo = new Progenitor.ControlsSample.WidgetsDemo(log.Add, new RepeatHud());
        var tabs = panel.Tabs("t", new[] { "One", "Two" });
        demo.BuildMasterDetail(panel, tabs, 1);
        var md = (UiMasterDetailHandle)demo.MasterDetail!;
        Assert.Equal(16, md.Entries.Count);
        Assert.True(UiTerms.IsDefined(panel.PluginId, Progenitor.ControlsSample.WidgetsDemo.BeltTerm));
        Assert.All(md.Entries, e => Assert.Equal(Progenitor.ControlsSample.WidgetsDemo.BeltTerm, e.Term));
        tabs.Selected = 1;

        Assert.True(md.Select("belt-ore"));
        Assert.Equal("md select belt-ore", demo.LastStatus);
        Assert.Equal(6, md.Members.Count);
        Click(Row(Progenitor.ControlsSample.WidgetsDemo.MasterDetailKey + ":row2"), L.CellDown);
        Assert.StartsWith("md reorder ch-1 0->1", demo.LastStatus);
        md.Query = "ore";
        Assert.StartsWith("md search \"ore\"", demo.LastStatus);
        md.Query = "";

        // Pick (the action line under the members) arms the repeating pick; each target joins the belt.
        Click(Row(Progenitor.ControlsSample.WidgetsDemo.MasterDetailKey + ":row8"), L.CellButton(1));
        Assert.Contains(log, l => l.EndsWith("widget md action tab 0 belt-ore actions/pick", StringComparison.Ordinal));
        Assert.Contains(log, l => l.EndsWith("widget pick several 2 = Container ContainerMenu (2:1)", StringComparison.Ordinal));
        Assert.Equal("pick several finished = 2", demo.LastStatus);
        Assert.Equal(8, md.Members.Count);

        // + New belt, then Delete it.
        md.Tabs!.Selected = 1;
        Assert.Equal("md tab = Categories", demo.LastStatus);
        Assert.Equal(3, md.Entries.Count);
        md.Tabs.Selected = 0;
        int before = md.Entries.Count;
        md.ScrollMaster(100);
        Row(Progenitor.ControlsSample.WidgetsDemo.MasterDetailKey + ":row" + (md.Rows - 1)).PointerClick(); // the last line: + New belt
        Assert.Equal(before + 1, md.Entries.Count);
        Assert.StartsWith("belt-new-", md.SelectedId);
        Assert.All(log, l => Assert.StartsWith(Progenitor.ControlsSample.ControlsDemo.LogPrefix + "widget ", l));
    }
}

/// <summary>Drives a master-detail row's hover the way the kit row's pointer slot does.</summary>
internal static class UiMasterDetailHoverForTests
{
    internal static void FireHoverForTests(this CellRowTests.FakeCellRow row, UiMasterDetailHandle md, int r, bool on) =>
        md.HoverRowForTests(r, on);
}
