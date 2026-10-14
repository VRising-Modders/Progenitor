using System;
using System.Collections.Generic;
using System.Linq;
using Progenitor.Ui;
using Xunit;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.Tests;

/// <summary>
/// P7b: the picker's multiple selection — the check column, the selected count on the status line, the Select all and
/// Clear links, and a row press (the controller's A) flipping an entry. Outcomes only.
/// </summary>
[Collection("UiStatic")]
public sealed class UiPickerMultiTests : IDisposable
{
    private readonly WidgetRuntimeFake _runtime = new();
    private readonly object _owner = new();
    private readonly List<IUiPanel> _panels = new();

    public UiPickerMultiTests()
    {
        UiApi.ResetForTests();
        UiApi.SetRuntimeForTests(_runtime);
    }

    public void Dispose()
    {
        foreach (var p in _panels) p.Dispose();
        UiApi.ResetForTests();
    }

    private CellRowTests.FakeCellRow Row(string key) => _runtime.Rows.Single(r => r.Text == key);

    private static CellSpec Cell(CellRowTests.FakeCellRow row, string key) => row.Cells().Single(c => c.Key == key);

    private static void Click(CellRowTests.FakeCellRow row, string cell) =>
        UiSlotTable.BootstrapDispatch(row.WiredIdOf(cell), (byte)UiSlotKind.Click, 0f, false, null);

    private static List<UiPickerEntry> Entries() => new()
    {
        new UiPickerEntry("ore", "Iron Ore") { Section = "Resources" },
        new UiPickerEntry("ingot", "Iron Ingot") { Section = "Resources" },
        new UiPickerEntry("dark", "Dark Iron Ingot") { Section = "Resources", DisabledReason = "Locked" },
        new UiPickerEntry("furnace", "Furnace") { Section = "Stations" },
        new UiPickerEntry("chest", "Iron chest") { Section = "Stations" },
    };

    private IUiPicker Multi(int rows = 8)
    {
        var panel = UiApi.Panel("Picker", _owner, "example.picker");
        _panels.Add(panel);
        return panel.Picker("p", Entries(), new UiPickerOptions { Multi = true, Rows = rows });
    }

    [Fact]
    public void Select_all_takes_every_pickable_match_once_and_clear_empties_it()
    {
        var picker = Multi();
        var picked = new List<IReadOnlyList<UiPickerEntry>>();
        picker.OnPicked(picked.Add);
        picker.Query = "iron";
        Assert.Equal(3, picker.SelectAllMatches()); // ore, ingot, chest; "Dark Iron Ingot" is grayed
        Assert.Equal(new[] { "ore", "ingot", "chest" }, picker.Selected);
        Assert.Single(picked); // one event for the whole batch
        Assert.Equal(0, picker.SelectAllMatches()); // nothing left to add: no event
        Assert.Single(picked);

        picker.Query = "";
        Assert.Equal(1, picker.SelectAllMatches()); // only furnace was missing
        Assert.Equal(4, picker.Selected.Count);
        picker.ClearSelection();
        Assert.Empty(picker.Selected);
        Assert.Equal(3, picked.Count);
    }

    [Fact]
    public void The_status_line_counts_the_selection_and_shows_only_the_links_with_work()
    {
        var picker = Multi();
        var status = Row("p:status");
        Assert.Equal("5 entries.", Cell(status, "status").Text);
        Assert.True(Cell(status, UiPickerHandle.CellAll).Visible);
        Assert.False(Cell(status, UiPickerHandle.CellClear).Visible);

        Assert.True(picker.Select("ore"));
        Assert.Equal("5 entries. 1 selected.", Cell(status, "status").Text);
        Assert.True(Cell(status, UiPickerHandle.CellClear).Visible);

        Click(status, UiPickerHandle.CellAll);
        Assert.Equal(4, picker.Selected.Count);
        Assert.Equal("5 entries. 4 selected.", Cell(status, "status").Text);
        Assert.False(Cell(status, UiPickerHandle.CellAll).Visible); // every pickable entry is selected

        Click(status, UiPickerHandle.CellClear);
        Assert.Empty(picker.Selected);
        Assert.Equal("5 entries.", Cell(status, "status").Text);
        Assert.False(Cell(status, UiPickerHandle.CellClear).Visible);
    }

    [Fact]
    public void A_row_press_flips_an_entry_and_draws_its_check_mark()
    {
        var picker = Multi();
        // Line 0 is the Resources heading; line 1 is Iron Ore.
        Assert.True(Row("p:line1").PointerClick()); // the row click (the controller's A on the row)
        Assert.Equal(new[] { "ore" }, picker.Selected);
        Assert.Equal("", Cell(Row("p:line1"), UiWidgetLayout.CellCheck).Text); // no text glyph: nothing underlined
        Assert.Equal(UiDrawnMark.CheckMark, ((UiPickerHandle)picker).CheckMarkAt(1));
        Assert.Equal(CellTone.Gold, Cell(Row("p:line1"), UiWidgetLayout.CellCheck).Tone);
        Click(Row("p:line1"), UiWidgetLayout.CellCheck); // the check box itself flips it back
        Assert.Empty(picker.Selected);
        Assert.Equal(UiDrawnMark.None, ((UiPickerHandle)picker).CheckMarkAt(1));
        Assert.Equal("", Cell(Row("p:line1"), UiWidgetLayout.CellCheck).Text);
        Row("p:line3").PointerClick(); // Dark Iron Ingot: grayed, refused
        Assert.Empty(picker.Selected);
    }

    [Fact]
    public void Single_mode_has_no_select_all_and_its_status_row_is_unchanged()
    {
        var panel = UiApi.Panel("Picker", _owner, "example.picker");
        _panels.Add(panel);
        var picker = panel.Picker("s", Entries());
        Assert.Equal(0, picker.SelectAllMatches());
        Assert.Empty(picker.Selected);
        Assert.Equal(new[] { "status", "use" }, Row("s:status").Cells().Select(c => c.Key));
    }

    [Fact]
    public void The_multi_status_row_fits_at_every_checked_width()
    {
        var cells = UiPickerLayout.StatusCells(multi: true);
        cells[0].Text = "12 matches. Keep typing to narrow them down. 4 selected.";
        cells[1].Text = "Use “Iron Ore”";
        foreach (var c in cells) c.Visible = true;
        foreach (float width in UiWidgetLayout.CheckedRowWidths)
        {
            var placed = MenuPanelLayout.CellColumns(width, cells);
            foreach (var p in placed.Where(p => p.Visible))
            {
                Assert.True(p.Left >= -width / 2f - .01f && p.Right <= width / 2f + .01f, p + " leaves the " + width + " row");
                Assert.True(p.Width >= CellLayout.MinWidthOf(cells[p.Index]) - .01f, p + " under its floor at " + width);
            }

            for (int i = 0; i < placed.Length; i++)
                for (int j = i + 1; j < placed.Length; j++)
                    Assert.False(CellLayout.Overlap(placed[i], placed[j]), placed[i] + " overlaps " + placed[j] + " at " + width);
            // The two actions are never dropped: only the suggestion link is optional.
            Assert.True(placed.Single(p => p.Key == UiPickerHandle.CellAll).Visible, "Select all dropped at " + width);
            Assert.True(placed.Single(p => p.Key == UiPickerHandle.CellClear).Visible, "Clear dropped at " + width);
        }
    }

    [Fact]
    public void Only_a_resolved_icon_gets_a_well_and_the_captions_still_line_up()
    {
        var panel = UiApi.Panel("Picker", _owner, "example.picker");
        _panels.Add(panel);
        panel.Picker("ic", new[]
        {
            new UiPickerEntry("crystal", "Crystal") { Icon = new object() },          // an icon
            new UiPickerEntry("ore", "Iron Ore") { ItemPrefab = 5 },                 // a prefab whose icon does not resolve here
            new UiPickerEntry("furnace", "Furnace") { Note = "plot A" },             // a station: no icon
        });
        var crystal = Row("ic:line0");
        Assert.True(Cell(crystal, UiWidgetLayout.CellIcon).Visible);
        Assert.False(Cell(crystal, UiWidgetLayout.CellIconPad).Visible);
        foreach (var key in new[] { "ic:line1", "ic:line2" })
        {
            Assert.False(Cell(Row(key), UiWidgetLayout.CellIcon).Visible, "an empty well on " + key);
            Assert.True(Cell(Row(key), UiWidgetLayout.CellIconPad).Visible);
        }

        float? captionLeft = null;
        foreach (var key in new[] { "ic:line0", "ic:line1", "ic:line2" })
        {
            var placed = MenuPanelLayout.CellColumns(760f, new CellRowSpec(key, Row(key).Cells().ToList(), rightInset: UiPickerLayout.RightInset));
            float left = placed.Single(p => p.Key == UiWidgetLayout.CellTitle).Left;
            captionLeft ??= left;
            Assert.Equal(captionLeft.Value, left, 2);
        }
    }

    [Fact]
    public void The_note_keeps_the_rows_right_inset_instead_of_touching_the_edge()
    {
        var panel = UiApi.Panel("Picker", _owner, "example.picker");
        _panels.Add(panel);
        var picker = (UiPickerHandle)panel.Picker("n", new[] { new UiPickerEntry("furnace", "Furnace") { Note = "plot A" } });
        Assert.Equal(UiPickerLayout.RightInset, picker.SlotAt(0).RightInset);
        var row = Row("n:line0");
        foreach (float width in UiWidgetLayout.CheckedRowWidths)
        {
            var placed = MenuPanelLayout.CellColumns(width, new CellRowSpec("n", row.Cells().ToList(), rightInset: UiPickerLayout.RightInset));
            var note = placed.Single(p => p.Key == UiWidgetLayout.CellNote);
            Assert.True(note.Visible);
            Assert.Equal(width / 2f - UiPickerLayout.RightInset, note.Right, 1);
        }
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]   // a panel with no scroll view is never read back, whenever it was armed
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    public void A_no_scroll_panel_is_never_read_back(bool pending, bool noScroll, bool read) =>
        Assert.Equal(read, ScrollbarVerdict.ShouldReadBack(pending, noScroll));

    [Fact]
    public void The_pick_band_is_marked_no_scroll_before_anything_lays_it_out()
    {
        string source = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "WorldPickBackend.cs");
        string show = SourcePin.MethodBody(source, "private static void ShowBand()");
        int noScroll = show.IndexOf("Ui.NoScrollPanel(panel);", StringComparison.Ordinal);
        Assert.True(noScroll > 0, "ShowBand does not mark the band no-scroll");
        foreach (var later in new[] { "panel.Footer", "Ui.HidePaneTitle(panel)", "Ui.SelfPlacePanel(panel)", "panel.Show()" })
            Assert.True(noScroll < show.IndexOf(later, StringComparison.Ordinal), "the band is laid out (" + later + ") before it is marked no-scroll");
    }
}
