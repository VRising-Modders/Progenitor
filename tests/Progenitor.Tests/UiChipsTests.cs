using System;
using System.Collections.Generic;
using System.Linq;
using Progenitor.Ui;
using Xunit;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.Tests;

/// <summary>
/// P7b: the chip set (<see cref="UiWidgets.Chips"/>): the game-free packer (exact wrap points, a chip wider than the row,
/// line capacity, the more chip's count), the packed lines resolved by the kit's own column model at every checked width,
/// and the handle through the widget fake runtime (remove, add, toggle, more, controller reach, dispose).
/// </summary>
[Collection("UiStatic")]
public sealed class UiChipsTests : IDisposable
{
    private readonly WidgetRuntimeFake _runtime = new();
    private readonly object _owner = new();
    private readonly List<IUiPanel> _panels = new();

    public UiChipsTests()
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
        var panel = UiApi.Panel("Chips", _owner, "example.chips");
        _panels.Add(panel);
        return panel;
    }

    private CellRowTests.FakeCellRow Row(string key) => _runtime.Rows.Single(r => r.Text == key);

    private static void Click(CellRowTests.FakeCellRow row, string cell)
    {
        ushort id = row.WiredIdOf(cell);
        Assert.NotEqual(0, id);
        UiSlotTable.BootstrapDispatch(id, (byte)UiSlotKind.Click, 0f, false, null);
    }

    private static CellSpec Cell(CellRowTests.FakeCellRow row, string key) => row.Cells().Single(c => c.Key == key);

    private static List<UiChipsLayout.ChipPiece> Pieces(params string[] captions) =>
        captions.Select((c, i) => new UiChipsLayout.ChipPiece(i, c, false, true)).ToList();

    private static float Outer(string caption, bool icon = false, bool remove = true) =>
        UiChipsLayout.ChipWidth(caption, icon, remove, icon ? UiChipsLayout.ItemChipHeight : UiChipsLayout.TextChipHeight);

    // ---- Geometry ------------------------------------------------------------------------------

    [Fact]
    public void A_chip_is_one_cell_with_the_icon_and_remove_glyph_inside_its_margins()
    {
        // "Ore" at 16: 3 glyphs x 16 x 0.55 = 26.4, one glyph of slack 8.8, the pads 8 / 8: 51.2, whole 52.
        Assert.Equal(52f, UiChipsLayout.ChipWidth("Ore", false, false, 31f));
        // With the remove glyph the right margin is 4 + 20 + 2 = 26: 26.4 + 8.8 + 8 + 26 = 69.2, whole 70.
        Assert.Equal(70f, UiChipsLayout.ChipWidth("Ore", false, true, 31f));
        // With the icon as well the left margin is 6 + 24 + 6 = 36: 97.2, whole 98.
        Assert.Equal(98f, UiChipsLayout.ChipWidth("Ore", true, true, 36f));
        Assert.Equal(36f, UiChipsLayout.ChipWidth("+", false, false, 36f)); // the add chip is square
        Assert.Equal(8f, UiChipsLayout.Spacing);
        Assert.Equal("Iron Ore · 120", UiChipsLayout.Caption(new UiChip("a", "Iron Ore") { Note = "120" }));
    }

    [Fact]
    public void Chip_height_follows_the_art_slices_and_the_icon()
    {
        // The chip art's top and bottom slices take 9 each: a 24 icon needs 9 + 24 + 9 = 42 of art, drawn at 36 with the
        // icon well inset; text chips are the Install chip's 31. Every chip of a set has the set's height.
        var items = UiChipsLayout.Pack(new List<UiChipsLayout.ChipPiece> { new(0, "Ore", true, true), new(1, "Belt", false, true) }, 760f, 3, 8, "+");
        Assert.Equal(UiChipsLayout.ItemChipHeight, items.ChipHeight);
        var text = UiChipsLayout.Pack(Pieces("Ore", "Gems"), 760f, 3, 8, "+");
        Assert.Equal(UiChipsLayout.TextChipHeight, text.ChipHeight);
        Assert.InRange(UiChipsLayout.TextChipHeight, 24f, 31f);
        Assert.True(UiChipsLayout.ChipIconSize <= UiChipsLayout.ItemChipHeight - 2f * 6f, "the icon pokes out of the chip art");
        // Between two lines of 36 chips: the row's air above and below them and the line gap, 8 like the gap along a line,
        // so the controller's focus frame (3 outside a chip) never touches the chip above or below.
        Assert.Equal(UiChipsLayout.Spacing, UiChipsLayout.LineHeight - UiChipsLayout.ItemChipHeight + UiChipsLayout.LineGap);

        var cells = UiChipsLayout.LineCells(4, "×");
        UiChipsLayout.Apply(cells, items, 0);
        Assert.Equal(36f, cells.Single(c => c.Key == "chip0").HeightOverride);
        Assert.Equal(36f, cells.Single(c => c.Key == "chip2").HeightOverride); // the add chip matches
        Assert.Equal(24f, cells.Single(c => c.Key == "icon0").HeightOverride);
        Assert.Equal(UiChipsLayout.CaptionLeft(true), cells.Single(c => c.Key == "chip0").CaptionInsetLeft);
        Assert.Equal(0f, cells.Single(c => c.Key == "chip1").CaptionInsetLeft); // no icon: the symmetric pad, centered
        Assert.Equal(0f, cells.Single(c => c.Key == "chip2").CaptionInsetRight);
    }

    // ---- Packing -------------------------------------------------------------------------------

    [Fact]
    public void Chips_wrap_exactly_where_the_next_one_would_leave_the_row()
    {
        var captions = new[] { "Ores belt", "Wood belt", "Stone", "Gems", "Cloth" };
        float three = Outer(captions[0]) + Outer(captions[1]) + Outer(captions[2]) + 2f * UiChipsLayout.Spacing;

        var exact = UiChipsLayout.Pack(Pieces(captions), three, 3, 8, null);
        Assert.Equal(new[] { 0, 1, 2 }, exact.Lines[0].Select(p => p.Index));
        Assert.Equal(new[] { 3, 4 }, exact.Lines[1].Select(p => p.Index));
        Assert.Equal(three, exact.Lines[0][2].Right, 3);
        Assert.Equal(Outer(captions[0]) + UiChipsLayout.Spacing, exact.Lines[0][1].Left, 3);

        var short1 = UiChipsLayout.Pack(Pieces(captions), three - 1f, 3, 8, null);
        Assert.Equal(new[] { 0, 1 }, short1.Lines[0].Select(p => p.Index));
        Assert.Equal(2, short1.Lines[1][0].Index); // the third chip now starts line 1
        Assert.Equal(0f, short1.Lines[1][0].Left);
        Assert.Equal(5, short1.Shown);
        Assert.Equal(0, short1.Overflow);
    }

    [Fact]
    public void A_chip_wider_than_the_row_is_shortened_with_an_ellipsis_and_stays_inside()
    {
        string longName = string.Concat(Enumerable.Repeat("Everything the north wall keeps ", 6));
        var pack = UiChipsLayout.Pack(new List<UiChipsLayout.ChipPiece>
        {
            new(0, "Ore", false, true),
            new(1, longName, true, true),
            new(2, "Wood", false, true),
        }, 560f, 3, 8, null);

        var wide = pack.Of(1)!;
        Assert.True(wide.Truncated);
        Assert.EndsWith("…", wide.Shown);
        Assert.Equal(longName, wide.Caption);
        Assert.Equal(1, wide.Line); // it starts its own line
        Assert.Equal(0f, wide.Left);
        Assert.True(wide.Right <= 560f, wide + " leaves the row");
        // The shortened caption fits between the icon and the remove glyph.
        Assert.True(CellLayout.EstimateTextWidth(wide.Shown, UiChipsLayout.CaptionFontSize)
                    <= wide.Width - UiChipsLayout.CaptionLeft(true) - UiChipsLayout.CaptionRight(true) - UiChipsLayout.CaptionSlack + .01f);
        Assert.Equal(2, pack.Of(2)!.Line);
        Assert.False(pack.Of(0)!.Truncated);
    }

    [Fact]
    public void Line_capacity_caps_chips_per_line_and_lines_and_the_rest_go_on_the_more_chip()
    {
        var ten = Pieces(Enumerable.Range(0, 10).Select(i => "C" + i).ToArray());

        var perLine = UiChipsLayout.Pack(ten, 1248f, 8, 3, null);
        Assert.All(perLine.Lines, l => Assert.True(l.Count <= 3));
        Assert.Equal(4, perLine.Lines.Count);
        Assert.Equal(10, perLine.Shown);

        // 2 lines x 3 = 6 slots: five chips and "+5 more".
        var capped = UiChipsLayout.Pack(ten, 1248f, 2, 3, null);
        Assert.Equal(5, capped.Shown);
        Assert.Equal(5, capped.Overflow);
        var more = capped.Of(UiChipsLayout.MoreIndex)!;
        Assert.Equal("+5 more", more.Shown);
        Assert.Equal(1, more.Line);
        Assert.Equal(2, more.Slot);

        // With the add chip as well: four chips, the more chip, the add chip last.
        var withAdd = UiChipsLayout.Pack(ten, 1248f, 2, 3, "+");
        Assert.Equal(4, withAdd.Shown);
        Assert.Equal(6, withAdd.Overflow);
        Assert.Equal(new[] { 0, 1, 2, 3, UiChipsLayout.MoreIndex, UiChipsLayout.AddIndex },
            withAdd.Lines.SelectMany(l => l).Select(p => p.Index));
    }

    [Fact]
    public void A_narrow_row_counts_what_did_not_fit_on_the_more_chip()
    {
        var captions = Enumerable.Range(0, 12).Select(i => "Category number " + i).ToArray();
        var pack = UiChipsLayout.Pack(Pieces(captions), 560f, 2, 16, null);
        Assert.Equal(2, pack.Lines.Count);
        Assert.Equal(12, pack.Shown + pack.Overflow);
        Assert.True(pack.Overflow > 0);
        Assert.Equal(UiChipsLayout.MoreCaption(pack.Overflow), pack.Of(UiChipsLayout.MoreIndex)!.Shown);
        Assert.All(pack.Lines.SelectMany(l => l), p => Assert.True(p.Right <= 560f + .01f));
    }

    [Fact]
    public void An_empty_set_draws_the_add_chip_and_the_empty_text()
    {
        var pack = UiChipsLayout.Pack(new List<UiChipsLayout.ChipPiece>(), 560f, 3, 8, "+");
        Assert.True(pack.Empty);
        Assert.Single(pack.Lines);
        Assert.Equal(UiChipsLayout.AddIndex, pack.Lines[0].Single().Index);
        Assert.Equal(0, pack.Overflow);
    }

    // ---- Layout fit -------------------------------------------------------------------------------

    private static List<UiChipsLayout.ChipPiece> MixedSet() => new()
    {
        new(0, "Crystal · 240", true, true),
        new(1, "Coarse Thread · 75", true, true),
        new(2, "Planks · 1,320", true, false),
        new(3, "Ores belt", false, true),
        new(4, "Everything the north wall workshop keeps for the tailor and the loom and the rest of the castle", false, true),
        new(5, "Wood", false, false),
        new(6, "Gems", false, true),
        new(7, "Blood essence", false, false),
        new(8, "Miscellaneous", false, true),
        new(9, "Ore", true, true),
        new(10, "Seeds", false, true),
        new(11, "Potions", false, false),
    };

    [Fact]
    public void Every_line_fits_its_cells_at_every_checked_width()
    {
        foreach (float width in UiWidgetLayout.CheckedRowWidths)
        {
            foreach (string? add in new[] { null, "+", "+ Add belt" })
            {
                foreach (int perLine in new[] { 2, 5, 8, 16 })
                {
                    var pack = UiChipsLayout.Pack(MixedSet(), width, 3, perLine, add);
                    for (int line = 0; line < Math.Max(1, pack.Lines.Count); line++)
                    {
                        var cells = UiChipsLayout.LineCells(perLine, UiWidgetLayout.RemoveGlyph);
                        UiChipsLayout.Apply(cells, pack, line);
                        var placed = MenuPanelLayout.CellColumns(width, cells);
                        string at = " at " + width + " (line " + line + ", " + perLine + " per line, add " + (add ?? "none") + ")";
                        foreach (var p in placed.Where(p => p.Visible))
                        {
                            Assert.True(p.Left >= -width / 2f - .01f && p.Right <= width / 2f + .01f, p + " leaves the row" + at);
                            Assert.True(p.Width >= CellLayout.MinWidthOf(cells[p.Index]) - .01f, p + " under its floor" + at);
                        }

                        // Chips never overlap each other; an icon or a remove glyph sits inside its own chip and nowhere else.
                        for (int i = 0; i < placed.Length; i++)
                            for (int j = i + 1; j < placed.Length; j++)
                            {
                                if (!placed[i].Visible || !placed[j].Visible) continue;
                                string? ownerI = cells[i].OverlayOf ?? cells[i].Key;
                                string? ownerJ = cells[j].OverlayOf ?? cells[j].Key;
                                if (ownerI == ownerJ) continue;
                                Assert.False(CellLayout.Overlap(placed[i], placed[j]), placed[i] + " overlaps " + placed[j] + at);
                            }

                        if (line >= pack.Lines.Count) continue;
                        foreach (var chip in pack.Lines[line])
                        {
                            // The column model puts every chip exactly where the packer said (no squeeze, no drift).
                            var cell = placed.Single(p => p.Key == UiChipsLayout.ChipKey(chip.Slot));
                            Assert.Equal(-width / 2f + chip.Left, cell.Left, 2);
                            Assert.Equal(chip.Width, cell.Width, 2);
                            float captionRoom = chip.Width - UiChipsLayout.CaptionLeft(chip.Icon) - UiChipsLayout.CaptionRight(chip.Remove);
                            Assert.True(CellLayout.EstimateTextWidth(chip.Shown, UiChipsLayout.CaptionFontSize) <= captionRoom + .01f,
                                chip.Shown + " does not fit between its margins" + at);
                            var icon = placed.Single(p => p.Key == UiChipsLayout.IconKey(chip.Slot));
                            var remove = placed.Single(p => p.Key == UiChipsLayout.RemoveKey(chip.Slot));
                            Assert.Equal(chip.Icon, icon.Visible);
                            Assert.Equal(chip.Remove, remove.Visible);
                            if (chip.Icon)
                            {
                                Assert.Equal(cell.Left + UiChipsLayout.IconInset, icon.Left, 2);
                                Assert.True(icon.Right <= cell.Left + UiChipsLayout.CaptionLeft(true) + .01f, "the icon runs into the caption" + at);
                            }

                            if (chip.Remove)
                            {
                                Assert.Equal(cell.Right - UiChipsLayout.RemoveInset, remove.Right, 2);
                                Assert.True(remove.Left >= cell.Right - UiChipsLayout.CaptionRight(true) - .01f, "the glyph runs into the caption" + at);
                            }
                        }
                    }
                }
            }
        }
    }

    [Fact]
    public void An_overlay_sits_at_either_end_of_its_target_with_its_inset()
    {
        var cells = new List<CellSpec>
        {
            new CellSpec(CellKind.Button, "chip").With(text: "Ore", width: CellWidth.Px(100f), minWidth: 100f),
            new CellSpec(CellKind.Image, "icon") { OverlayOf = "chip", OverlayLeft = true, OverlayInset = 6f }.With(width: CellWidth.Px(24f)),
            new CellSpec(CellKind.Button, "x") { OverlayOf = "chip", OverlayInset = 4f }.With(text: "×", width: CellWidth.Px(20f), minWidth: 20f),
            new CellSpec(CellKind.Button, "plain") { OverlayOf = "chip" }.With(text: "!", width: CellWidth.Px(10f), minWidth: 10f),
        };
        var placed = CellLayout.Resolve(560f, cells);
        Assert.Equal(-280f, placed[0].Left, 2);
        Assert.Equal(-274f, placed[1].Left, 2);
        Assert.Equal(-184f, placed[2].Right, 2);
        Assert.Equal(-180f, placed[3].Right, 2); // an ordinary overlay is still flush right
    }

    [Fact]
    public void The_row_width_is_read_back_through_the_canvas_scale()
    {
        Assert.Equal(1108f, UiChipsLayout.LayoutWidth(2216f, 112f, 56f));
        Assert.Equal(560f, UiChipsLayout.LayoutWidth(840f, 84f, 56f));
        Assert.Equal(0f, UiChipsLayout.LayoutWidth(840f, 0f, 56f));
        Assert.Equal(0f, UiChipsLayout.LayoutWidth(float.NaN, 84f, 56f));
    }

    // ---- Handle ---------------------------------------------------------------------------------

    private static List<UiChip> Belts() => new()
    {
        new UiChip("ores", "Ores belt"),
        new UiChip("wood", "Wood belt") { Removable = false },
        new UiChip("crystal", "Crystal") { ItemPrefab = -257494203, Note = "240" },
    };

    [Fact]
    public void The_remove_glyph_removes_the_chip_then_reports_it()
    {
        var chips = NewPanel().Chips("belts", Belts(), new UiChipsOptions { RowWidth = 1108f });
        var removed = new List<string>();
        int countSeen = -1;
        chips.OnRemove(c =>
        {
            removed.Add(c.Id);
            countSeen = chips.Chips.Count;
        });

        var line = Row("belts:line0");
        Assert.True(Cell(line, "x0").Visible);
        Assert.False(Cell(line, "x1").Visible); // not removable: no glyph
        Assert.True(Cell(line, "icon2").Visible); // the item chip has its icon well
        Assert.Equal("Crystal · 240", Cell(line, "chip2").Text);
        Assert.True(_runtime.ItemIconAsks > 0);

        Click(line, "x0");
        Assert.Equal(new[] { "ores" }, removed);
        Assert.Equal(2, countSeen); // the set had already changed when the handler ran
        Assert.Equal(new[] { "wood", "crystal" }, chips.Chips.Select(c => c.Id));
        Assert.Equal("Wood belt", Cell(line, "chip0").Text); // rewritten in place
        Assert.False(Cell(line, "chip2").Visible);
        Assert.False(chips.Remove("ores"));
    }

    [Fact]
    public void The_add_chip_and_the_more_chip_raise_their_events()
    {
        var many = Enumerable.Range(0, 9).Select(i => new UiChip("c" + i, "Cat " + i)).ToList();
        var chips = NewPanel().Chips("cats", many, new UiChipsOptions { Add = true, MaxLines = 1, MaxPerLine = 4, RowWidth = 1248f });
        int adds = 0;
        IReadOnlyList<UiChip>? hidden = null;
        chips.OnAdd(() => adds++);
        chips.OnMore(h => hidden = h);
        Assert.Equal(2, chips.Shown);
        Assert.Equal(7, chips.Overflow);
        var line = Row("cats:line0");
        Assert.Equal("+7 more", Cell(line, "chip2").Text);
        Assert.Equal("+", Cell(line, "chip3").Text);
        Assert.False(Cell(line, "x2").Visible);

        Click(line, "chip3");
        Assert.Equal(1, adds);
        Click(line, "chip2");
        Assert.Equal(Enumerable.Range(2, 7).Select(i => "c" + i), hidden!.Select(c => c.Id));
    }

    [Fact]
    public void Toggle_mode_flips_selection_before_the_click_event_and_draws_the_gold_plate()
    {
        var filters = new List<UiChip> { new("ore", "Ore") { Selected = true }, new("gem", "Gems"), new("wood", "Wood") };
        var chips = NewPanel().Chips("f", filters, new UiChipsOptions { Toggle = true, Remove = false, RowWidth = 760f });
        var seen = new List<string>();
        chips.OnClick(c => seen.Add(c.Id + "=" + chips.IsSelected(c.Id)));
        var line = Row("f:line0");
        Assert.Equal(CellTone.ChipOn, Cell(line, "chip0").Tone);
        Assert.Equal(CellTone.ChipOff, Cell(line, "chip1").Tone);
        Assert.All(new[] { "x0", "x1", "x2" }, k => Assert.False(Cell(line, k).Visible));

        Click(line, "chip1");
        Click(line, "chip0");
        Assert.Equal(new[] { "gem=True", "ore=False" }, seen);
        Assert.Equal(new[] { "gem" }, chips.SelectedIds);
        Assert.Equal(CellTone.ChipOn, Cell(line, "chip1").Tone);
        Assert.Equal(CellTone.ChipOff, Cell(line, "chip0").Tone);

        Assert.True(chips.SetSelected("wood", true)); // no event
        Assert.Equal(2, seen.Count);
        Assert.Equal(new[] { "gem", "wood" }, chips.SelectedIds);
    }

    [Fact]
    public void A_selected_chip_draws_its_remove_glyph_on_the_plate_tone()
    {
        var chips = NewPanel().Chips("sel", Belts(), new UiChipsOptions { RowWidth = 1108f });
        var line = Row("sel:line0");
        Assert.Equal(CellTone.ChipOff, Cell(line, "x0").Tone);
        Assert.True(chips.SetSelected("ores", true));
        Assert.Equal(CellTone.ChipOn, Cell(line, "chip0").Tone);
        Assert.Equal(CellTone.ChipOn, Cell(line, "x0").Tone);
        Assert.Equal(CellWrap.Glyph, Cell(line, "x0").Wrap); // drawn centered and not underlined
        Assert.Equal("chip0", Cell(line, "x0").OverlayOf);    // inside the chip's art
        Assert.Equal("chip2", Cell(line, "icon2").OverlayOf);
    }

    [Fact]
    public void A_plain_click_only_reports_and_keeps_the_selection()
    {
        var chips = NewPanel().Chips("b", Belts(), new UiChipsOptions { RowWidth = 760f });
        var opened = new List<string>();
        chips.OnClick(c => opened.Add(c.Id));
        Click(Row("b:line0"), "chip1");
        Assert.Equal(new[] { "wood" }, opened);
        Assert.Empty(chips.SelectedIds);
    }

    [Fact]
    public void Lines_beyond_the_chips_are_hidden_and_shown_again_as_chips_arrive()
    {
        var chips = NewPanel().Chips("w", new[] { new UiChip("a", "Alpha") }, new UiChipsOptions { RowWidth = 560f, MaxPerLine = 2, Add = true });
        Assert.True(Row("w:line0").Visible);
        Assert.False(Row("w:line1").Visible);
        int rows = _runtime.Rows.Count;
        chips.SetChips(Enumerable.Range(0, 5).Select(i => new UiChip("c" + i, "Chip " + i)));
        Assert.Equal(rows, _runtime.Rows.Count); // no row made or destroyed
        Assert.True(Row("w:line1").Visible);
        Assert.True(Row("w:line2").Visible);
        Assert.Throws<ArgumentException>(() => chips.SetChips(new[] { new UiChip("x", "X"), new UiChip("x", "Y") }));
        Assert.Equal(5, chips.Chips.Count); // a refused SetChips changed nothing

        chips.SetChips(Array.Empty<UiChip>());
        var line = Row("w:line0");
        Assert.True(Cell(line, UiChipsLayout.EmptyKey).Visible);
        Assert.Equal("+", Cell(line, "chip0").Text);
        Assert.False(Row("w:line1").Visible);
    }

    [Fact]
    public void Every_chip_and_its_remove_button_is_a_controller_stop_in_reading_order()
    {
        var chips = NewPanel().Chips("n", Belts(), new UiChipsOptions { RowWidth = 1108f, Add = true });
        var row = (UiCellRowHandle)((UiChipsHandle)chips).LineRows[0];
        Assert.Equal(new[] { "chip0", "x0", "chip1", "chip2", "x2", "chip3" }, row.NavCellKeys());
        Assert.Equal(row.NavCellKeys(), UiNav.Stops(row).Select(s => s.Part));
    }

    [Fact]
    public void A_measured_set_packs_for_the_row_it_is_drawn_in()
    {
        var chips = (UiChipsHandle)NewPanel().Chips("m", Belts());
        // The fake row reads 1664 screen pixels wide at a scale of 1.
        Assert.Equal(1664f, chips.PackedWidth);
        Assert.Single(_runtime.Ticks);
        var fixedSet = (UiChipsHandle)NewPanel().Chips("fixed", Belts(), new UiChipsOptions { RowWidth = 760f });
        Assert.Equal(760f, fixedSet.PackedWidth);
        Assert.Single(_runtime.Ticks); // a fixed width needs no measuring tick
    }

    [Fact]
    public void A_long_chip_gets_its_full_caption_as_the_tooltip_title()
    {
        string longName = string.Concat(Enumerable.Repeat("Everything the north wall keeps ", 6)).Trim();
        var chips = (UiChipsHandle)NewPanel().Chips("t", new[] { new UiChip("long", longName) }, new UiChipsOptions { RowWidth = 560f });
        var line = Row("t:line0");
        Assert.EndsWith("…", Cell(line, "chip0").Text);
        UiCellRowHandle.TryHoverCell("t:line0", "chip0", true);
        Assert.Equal(longName, _runtime.Tooltip?.Title);
        UiCellRowHandle.TryHoverCell("t:line0", "chip0", false);
        Assert.Null(_runtime.Tooltip);
    }

    [Fact]
    public void Dispose_releases_every_line_and_the_measure_tick_and_ignores_late_clicks()
    {
        var chips = NewPanel().Chips("d", Belts());
        var removed = new List<string>();
        chips.OnRemove(c => removed.Add(c.Id));
        var line = Row("d:line0");
        ushort removeSlot = line.WiredIdOf("x0");
        chips.Dispose();
        Assert.Empty(_runtime.Ticks);
        Assert.True(_runtime.Rows.Where(r => r.Text.StartsWith("d:", StringComparison.Ordinal)).All(r => r.Released));
        Assert.Equal(UiHandleState.Disposed, chips.State);
        UiSlotTable.BootstrapDispatch(removeSlot, (byte)UiSlotKind.Click, 0f, false, null);
        Assert.Empty(removed);
        Assert.False(chips.Remove("ores"));
    }

    [Fact]
    public void On_a_runtime_that_cannot_draw_the_set_keeps_state()
    {
        UiApi.SetRuntimeForTests(new CellRowTests.PlainRuntime());
        var chips = NewPanel().Chips("s", Belts());
        Assert.True(chips.Remove("ores"));
        Assert.True(chips.SetSelected("wood", true));
        Assert.Equal(new[] { "wood" }, chips.SelectedIds);
        var described = chips.Describe();
        Assert.Equal("chips", described["kind"]);
        Assert.Equal(2, ((List<Dictionary<string, object?>>)described["chips"]!).Count);
    }

    [Fact]
    public void The_chips_demo_builds_and_reports_every_event()
    {
        var panel = NewPanel();
        var log = new List<string>();
        var demo = new Progenitor.ControlsSample.WidgetsDemo(log.Add, null);
        var tabs = panel.Tabs("demo-tabs-chips", new[] { "Chips" });
        demo.BuildChips(panel, tabs, 0);
        Assert.NotNull(demo.ItemChips);

        Click(Row(Progenitor.ControlsSample.WidgetsDemo.BeltChipsKey + ":line0"), "x0");
        Assert.Equal("chips belt remove belt-ores", demo.LastStatus);
        int addSlot = ((UiChipsHandle)demo.BeltChips!).Pack.Of(UiChipsLayout.AddIndex)!.Slot;
        Click(Row(Progenitor.ControlsSample.WidgetsDemo.BeltChipsKey + ":line0"), UiChipsLayout.ChipKey(addSlot));
        Assert.Equal("chips belt add belt-new-1", demo.LastStatus);
        Assert.Contains(demo.BeltChips.Chips, c => c.Id == "belt-new-1");

        Click(Row(Progenitor.ControlsSample.WidgetsDemo.FilterChipsKey + ":line0"), "chip1");
        Assert.Equal("chips filter cat-ingot = on (cat-ore,cat-ingot)", demo.LastStatus);
        Assert.True(demo.FilterChips!.Overflow > 0);
        var more = ((UiChipsHandle)demo.FilterChips).Pack.Of(UiChipsLayout.MoreIndex)!;
        Click(Row(Progenitor.ControlsSample.WidgetsDemo.FilterChipsKey + ":line" + more.Line), UiChipsLayout.ChipKey(more.Slot));
        Assert.StartsWith("chips filter more " + demo.FilterChips.Overflow, demo.LastStatus);
        Assert.All(log, l => Assert.StartsWith(Progenitor.ControlsSample.ControlsDemo.LogPrefix + "widget chips ", l));
    }
}

/// <summary>
/// P7b help on chips: a chip's caption explains a glossary term (<see cref="UiChip.Term"/>): the marker follows the chip
/// as the set re-packs, hovering the chip shows the term's line in place of its tooltip, the chip keeps room for the
/// marker, and every marker goes with the set.
/// </summary>
[Collection("UiStatic")]
public sealed class UiChipsTermTests : IDisposable
{
    private const string PluginA = "example.chips";

    private readonly HelpRuntimeFake _runtime = new();
    private readonly UiHelpMemoryStore _store = new();
    private readonly object _owner = new();
    private readonly List<IUiPanel> _panels = new();

    public UiChipsTermTests()
    {
        UiApi.ResetForTests();
        UiApi.SetRuntimeForTests(_runtime);
        UiTerms.Teardown();
        UiTips.Teardown();
        UiHelpStore.UseForTests(_store);
        UiTerms.Clock = () => 100;
    }

    public void Dispose()
    {
        foreach (var p in _panels) p.Dispose();
        UiTerms.Teardown();
        UiTips.Teardown();
        UiWidgetLifetime.Uninstall();
        UiHelpStore.UseForTests(null);
        UiTerms.Clock = () => 0;
        CoreUiSettings.Reset();
        UiApi.ResetForTests();
    }

    private IUiPanel NewPanel()
    {
        var panel = UiApi.Panel("Chips", _owner, PluginA);
        _panels.Add(panel);
        panel.Show();
        return panel;
    }

    [Fact]
    public void A_chip_with_a_term_keeps_room_for_the_marker_after_its_caption()
    {
        float reserve = UiTermMarkerArt.SizeFor(UiChipsLayout.CaptionFontSize) + UiTermMarkerArt.Gap;
        Assert.Equal(16f, reserve); // 16 x 0.72 = 11.5, rounded 12, and the 4 gap
        Assert.Equal(UiChipsLayout.ChipWidth("Ore", false, true, 31f) + reserve,
            UiChipsLayout.ChipWidth("Ore", false, true, 31f, term: true));
        Assert.Equal(UiChipsLayout.ChipWidth("Ore", true, false, 36f) + reserve,
            UiChipsLayout.ChipWidth("Ore", true, false, 36f, term: true));

        var pack = UiChipsLayout.Pack(new List<UiChipsLayout.ChipPiece>
        {
            new(0, "Ores belt", true, true, term: true),
            new(1, "Wood", false, false, term: true),
            new(2, "Gems", false, false),
        }, 760f, 3, 8, null);
        var cells = UiChipsLayout.LineCells(4, "×");
        UiChipsLayout.Apply(cells, pack, 0);
        // The caption's right margin covers the marker: past the remove glyph on an item chip, past the pad on a plain one.
        Assert.Equal(UiChipsLayout.CaptionRight(true) + reserve, cells.Single(c => c.Key == "chip0").CaptionInsetRight);
        Assert.Equal(CellLayout.ButtonPad * .5f + reserve, cells.Single(c => c.Key == "chip1").CaptionInsetRight);
        Assert.Equal(0f, cells.Single(c => c.Key == "chip2").CaptionInsetRight); // no term: centered on the whole chip
        foreach (var p in pack.Lines[0])
        {
            float room = p.Width - UiChipsLayout.CaptionLeft(p.Icon) - UiChipsLayout.CaptionRight(p.Remove, p.Term);
            Assert.True(CellLayout.EstimateTextWidth(p.Shown, UiChipsLayout.CaptionFontSize) <= room + .01f, p.Shown);
        }

        // A long caption with a term is shortened so the marker still fits inside the row.
        var wide = UiChipsLayout.Pack(new List<UiChipsLayout.ChipPiece>
        {
            new(0, string.Concat(Enumerable.Repeat("Everything the north wall keeps ", 6)), false, true, term: true),
        }, 560f, 3, 8, null).Of(0)!;
        Assert.True(wide.Truncated);
        Assert.True(wide.Right <= 560f);
        Assert.True(CellLayout.EstimateTextWidth(wide.Shown, UiChipsLayout.CaptionFontSize)
                    <= wide.Width - UiChipsLayout.CaptionLeft(false) - UiChipsLayout.CaptionRight(true, true) + .01f);
    }

    [Fact]
    public void A_chips_term_follows_its_chip_and_its_hover_explains_the_term()
    {
        UiTerms.Define(PluginA, "Belt", "Chests used together.");
        var chips = NewPanel().Chips("tc", new[]
        {
            new UiChip("ores", "Ores belt") { Term = "Belt", Tooltip = "ignored while the term explains" },
            new UiChip("bin", "Iron bin") { Tooltip = "A single chest." },
        }, new UiChipsOptions { RowWidth = 760f });
        var handle = (UiChipsHandle)chips;
        Assert.Equal("Belt", handle.TermAt(0, 0));
        Assert.Equal("", handle.TermAt(0, 1));
        var first = _runtime.Markers.Single();
        Assert.Equal("chip0", first.CellKey);
        Assert.Equal("Belt", ((List<Dictionary<string, object?>>)chips.Describe()["chips"]!)[0]["term"]);

        // Re-packed: the chip moves to slot 1, the marker goes with it.
        chips.SetChips(new[] { new UiChip("bin", "Iron bin") { Tooltip = "A single chest." }, new UiChip("ores", "Ores belt") { Term = "Belt" } });
        Assert.Equal("", handle.TermAt(0, 0));
        Assert.Equal("Belt", handle.TermAt(0, 1));
        Assert.True(first.Disposed);

        Assert.True(UiCellRowHandle.TryHoverCell("tc:line0", "chip1", true));
        Assert.Equal(("Belt", "Chests used together."), _runtime.Tooltip); // the term wins over a plain tooltip
        Assert.True(UiCellRowHandle.TryHoverCell("tc:line0", "chip1", false));
        Assert.Null(_runtime.Tooltip);
        Assert.True(UiCellRowHandle.TryHoverCell("tc:line0", "chip0", true));
        Assert.Equal(("Iron bin", "A single chest."), _runtime.Tooltip); // a chip without a term keeps its tooltip
        UiCellRowHandle.TryHoverCell("tc:line0", "chip0", false);
        Assert.Equal(1, UiTerms.Hovers(PluginA, "Belt"));

        chips.Remove("ores");
        Assert.Equal("", handle.TermAt(0, 1)); // the emptied slot drops its marker
        chips.SetChips(new[] { new UiChip("ores", "Ores belt") { Term = "Belt" } });
        chips.Dispose();
        Assert.Empty(UiTerms.Marked);
    }
}
