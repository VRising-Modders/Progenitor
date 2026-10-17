using System;
using System.Collections.Generic;
using System.Linq;
using Progenitor.Ui;
using Xunit;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.Tests;

/// <summary>
/// LIST1: the settings-kit cell row behind the Install list view. The column model
/// (<see cref="MenuPanelLayout.CellColumns"/>) is exercised at the three widths the mock was
/// approved at — the 1664 px in-game frame (gen 42), the 1248 px detail column and the
/// 1600×1024 fixture — and the handle is exercised through a fake runtime so the in-place
/// rewrite, the expanded / selected state, the header's sort mark and the readback shape are
/// asserted without Unity. Removing <c>CellLayout.Resolve</c>'s optional-drop loop fails
/// <see cref="Narrow_row_drops_optional_columns_from_the_right_before_squeezing_text"/>;
/// removing the right-edge packing fails
/// <see cref="Columns_pack_right_cells_at_the_right_edge_and_never_overlap"/>; removing
/// <c>ApplyCell</c> from <c>SetCell</c> fails
/// <see cref="SetCell_rewrites_in_place_without_a_panel_relayout"/>.
/// </summary>
public sealed class CellRowTests : IDisposable
{
    private readonly FakeCellRuntime _runtime = new();
    private readonly object _owner = new();

    public CellRowTests()
    {
        UiApi.ResetForTests();
        UiApi.SetRuntimeForTests(_runtime);
    }

    private readonly List<IUiPanel> _panels = new();

    public void Dispose()
    {
        // The row registry is process-wide: every panel this test built is disposed so its
        // rows leave the registry before the next test looks a key up.
        foreach (var panel in _panels) panel.Dispose();
        UiApi.ResetForTests();
    }

    // ---- Column model ----------------------------------------------------------------

    /// <summary>The mock's row: icon, name + one-liner, author (optional), side, version, released (optional), downloads, update, install.</summary>
    internal static List<CellSpec> MockRow(bool twoLine = true) => new()
    {
        new CellSpec(CellKind.Image, "icon"),
        new CellSpec(CellKind.Text, "name").With(text: "ExampleCommandFramework\nCommand framework for V Rising mods", width: CellWidth.Fraction(.26f), wrap: twoLine ? CellWrap.TwoLine : CellWrap.Ellipsis),
        new CellSpec(CellKind.Muted, "author").With(text: "dyno", width: CellWidth.Px(120f), optional: true),
        new CellSpec(CellKind.Badge, "side").With(tone: CellTone.Server),
        new CellSpec(CellKind.Mono, "version").With(text: "0.10.4", width: CellWidth.Px(80f)),
        new CellSpec(CellKind.Mono, "released").With(text: "2026-08-30", width: CellWidth.Px(110f), optional: true),
        new CellSpec(CellKind.Spacer, "gap"),
        new CellSpec(CellKind.Mono, "downloads").With(text: "1,234,567", width: CellWidth.Px(100f), align: CellAlign.Right),
        new CellSpec(CellKind.Muted, "update").With(text: "Oakveil", width: CellWidth.Px(90f), align: CellAlign.Right),
        new CellSpec(CellKind.Button, "install").With(text: "Install · restart", tone: CellTone.Gold, align: CellAlign.Right),
    };

    public static IEnumerable<object[]> Widths()
    {
        yield return new object[] { 1664f };
        yield return new object[] { 1248f };
        var frame = ModsWorkspaceFrame.Fallback(1600f, 1024f);
        yield return new object[] { MenuPanelLayout.ForFrame(frame, sidebar: false).RowWidth };
    }

    [Theory]
    [MemberData(nameof(Widths))]
    public void Columns_pack_right_cells_at_the_right_edge_and_never_overlap(float rowWidth)
    {
        var specs = MockRow();
        var placed = MenuPanelLayout.CellColumns(rowWidth, specs);
        Assert.Equal(specs.Count, placed.Length);
        float left = -rowWidth / 2f;
        float right = rowWidth / 2f;
        // Every visible cell lies inside the row.
        foreach (var p in placed.Where(p => p.Visible))
        {
            Assert.True(p.Left >= left - .01f, p + " starts left of the row");
            Assert.True(p.Right <= right + .01f, p + " ends right of the row");
            Assert.True(p.Width >= CellLayout.MinWidthOf(specs[p.Index]) - .01f, p + " is under its floor");
        }
        // No two visible cells overlap.
        for (int i = 0; i < placed.Length; i++)
            for (int j = i + 1; j < placed.Length; j++)
                Assert.False(CellLayout.Overlap(placed[i], placed[j]), placed[i] + " overlaps " + placed[j]);
        // The install button is flush with the right edge; the icon flush with the left.
        var install = placed.Single(p => p.Key == "install");
        Assert.InRange(install.Right, right - .01f, right + .01f);
        var icon = placed.Single(p => p.Key == "icon");
        Assert.InRange(icon.Left, left - .01f, left + .01f);
        Assert.Equal(CellLayout.ImageSize, icon.Width);
        // Right-aligned cells keep their spec order right to left: downloads < update < install.
        var downloads = placed.Single(p => p.Key == "downloads");
        var update = placed.Single(p => p.Key == "update");
        Assert.True(downloads.Right < update.Left && update.Right < install.Left, "right group out of order");
        // Left/centre cells are separated by exactly the gap.
        var name = placed.Single(p => p.Key == "name");
        Assert.InRange(name.Left - icon.Right, CellLayout.Gap - .01f, CellLayout.Gap + .01f);
    }

    [Theory]
    [MemberData(nameof(Widths))]
    public void Fraction_and_auto_widths_resolve_and_the_spacer_absorbs_the_remainder(float rowWidth)
    {
        var specs = MockRow();
        var placed = MenuPanelLayout.CellColumns(rowWidth, specs);
        var name = placed.Single(p => p.Key == "name");
        Assert.InRange(name.Width, rowWidth * .26f - .01f, rowWidth * .26f + .01f);
        var side = placed.Single(p => p.Key == "side");
        Assert.True(side.Width >= CellLayout.MinBadgeWidth, "badge under its floor");
        Assert.True(side.Width < 120f, "auto badge wider than its caption warrants");
        var install = placed.Single(p => p.Key == "install");
        Assert.True(install.Width >= CellLayout.ButtonWidth - .01f, "button narrower than the mock's column");
        Assert.True(CellLayout.CaptionFits(specs[install.Index], install.Width), "install caption does not fit its button");
        // The spacer closes the row: left group + gap + spacer + gap + right group == row width.
        var gap = placed.Single(p => p.Key == "gap");
        // The last visible cell before the spacer ('released' unless the row dropped it).
        var beforeGap = placed.Take(gap.Index).Last(p => p.Visible);
        var downloads = placed.Single(p => p.Key == "downloads");
        Assert.True(gap.Visible);
        Assert.InRange(gap.Left - beforeGap.Right, CellLayout.Gap - .01f, CellLayout.Gap + .01f);
        Assert.InRange(downloads.Left - gap.Right, CellLayout.Gap - .01f, CellLayout.Gap + .01f);
    }

    [Fact]
    public void Narrow_row_drops_optional_columns_from_the_right_before_squeezing_text()
    {
        var specs = MockRow();
        // Wide enough for everything.
        var wide = MenuPanelLayout.CellColumns(1664f, specs);
        Assert.All(wide.Where(p => specs[p.Index].Kind != CellKind.Spacer), p => Assert.True(p.Visible, p + " hidden on a wide row"));

        // Fixed asks: icon 36 + author 120 + version 80 + released 110 + downloads 100 + update 90
        // + button 200 + badge ~62 = 798, nine gaps 72, name 26 %. Everything fits from 1176 px;
        // without 'released' from 1016 px; without 'author' too from ~845 px. 1100 px is the
        // band where exactly the rightmost optional column ('released') has to go.
        float narrow = 1100f;
        var one = MenuPanelLayout.CellColumns(narrow, specs);
        var released = one.Single(p => p.Key == "released");
        var author = one.Single(p => p.Key == "author");
        Assert.False(released.Visible);
        Assert.True(released.Dropped);
        Assert.True(author.Visible, "author dropped before released");
        Assert.True(one.Single(p => p.Key == "name").Width >= CellLayout.MinTextWidth, "name squeezed under its floor while an optional column was still shown");

        // Narrower still: author goes too, and the name keeps its readable floor.
        var both = MenuPanelLayout.CellColumns(960f, specs);
        Assert.False(both.Single(p => p.Key == "author").Visible);
        Assert.True(both.Single(p => p.Key == "author").Dropped);
        Assert.True(both.Single(p => p.Key == "name").Width >= CellLayout.MinTextWidth - .01f);
        Assert.True(both.Single(p => p.Key == "name").Visible);
        for (int i = 0; i < both.Length; i++)
            for (int j = i + 1; j < both.Length; j++)
                Assert.False(CellLayout.Overlap(both[i], both[j]), both[i] + " overlaps " + both[j]);
    }

    [Fact]
    public void Hidden_cells_take_no_width_and_keep_their_key()
    {
        var specs = MockRow();
        specs.Single(s => s.Key == "update").Visible = false;
        var placed = MenuPanelLayout.CellColumns(1664f, specs);
        var update = placed.Single(p => p.Key == "update");
        Assert.False(update.Visible);
        Assert.False(update.Dropped);
        Assert.Equal(0f, update.Width);
        var downloads = placed.Single(p => p.Key == "downloads");
        var install = placed.Single(p => p.Key == "install");
        Assert.InRange(install.Left - downloads.Right, CellLayout.Gap - .01f, CellLayout.Gap + .01f);
    }

    [Fact]
    public void Degenerate_widths_and_empty_specs_never_throw()
    {
        Assert.Empty(MenuPanelLayout.CellColumns(1000f, Array.Empty<CellSpec>()));
        var placed = MenuPanelLayout.CellColumns(float.NaN, MockRow());
        Assert.Equal(MockRow().Count, placed.Length);
        var tiny = MenuPanelLayout.CellColumns(10f, MockRow());
        Assert.Equal(MockRow().Count, tiny.Length);
    }

    [Fact]
    public void Two_line_row_is_53_px_and_a_plain_row_is_the_kit_row_height()
    {
        Assert.Equal(53f, new CellRowSpec("a", MockRow(), twoLine: true).Height);
        Assert.Equal(UnityUiRuntimeMetrics.RowHeight, new CellRowSpec("a", MockRow(), twoLine: false).Height);
        Assert.Equal(53f, CellLayout.TwoLineRowHeight);
    }

    [Fact]
    public void Header_captions_are_uppercase_and_only_the_sorted_column_carries_the_indicator()
    {
        // LIST3: the indicator is U+25BC (a WGL4 glyph); U+25BE drew as a missing-glyph box live.
        Assert.Equal("DOWNLOADS" + CellLayout.SortIndicator, CellLayout.HeaderCaption("Downloads", sorted: true));
        Assert.Equal(" ▼", CellLayout.SortIndicator);
        Assert.Equal("MOD", CellLayout.HeaderCaption("Mod", sorted: false));
        Assert.Equal("", CellLayout.HeaderCaption(null, sorted: false));
    }

    [Fact]
    public void Caption_fit_uses_the_same_estimate_the_kit_clips_with()
    {
        var button = new CellSpec(CellKind.Button, "b").With(text: "Installed · restart to load");
        Assert.True(CellLayout.CaptionFits(button, CellLayout.AutoWidth(button)));
        Assert.False(CellLayout.CaptionFits(button, CellLayout.MinButtonWidth));
        var mono = new CellSpec(CellKind.Mono, "m").With(text: "1,234,567");
        Assert.True(CellLayout.EstimateTextWidth(mono.Text, mono.FontSize, mono: true) > CellLayout.EstimateTextWidth(mono.Text, mono.FontSize));
    }

    // ---- Palette ----------------------------------------------------------------------

    [Fact]
    public void Palette_side_colours_are_the_vanilla_pair_and_distinct()
    {
        var server = MenuPalette.Badge(CellTone.Server);
        var client = MenuPalette.Badge(CellTone.Client);
        var both = MenuPalette.Badge(CellTone.Both);
        var unknown = MenuPalette.Badge(CellTone.Normal);
        Assert.NotEqual(server.Border, client.Border);
        Assert.Equal(MenuPalette.Gold, client.Border);
        Assert.Equal(MenuPalette.ServerBorder, server.Border);
        // Both is the split: server fill on the left, client fill on the right.
        Assert.Equal(MenuPalette.ServerFill, both.Fill);
        Assert.Equal(MenuPalette.ClientFill, MenuPalette.BadgeRightFill(CellTone.Both));
        Assert.Equal(MenuPalette.Muted, unknown.Text);
        // Captions read on their fills.
        // Authored Unity colors, no sRGB decode (MenuRowStyle's convention): the mock's
        // badge captions clear 3.5 on their fills; the body text clears 7 on the row.
        Assert.True(server.Text.ContrastAgainst(server.Fill) >= 3.5f);
        Assert.True(client.Text.ContrastAgainst(client.Fill) >= 3.5f);
        Assert.True(MenuPalette.Text.ContrastAgainst(MenuPalette.RowFill) >= 7f);
        Assert.True(MenuPalette.Muted.ContrastAgainst(MenuPalette.RowFill) >= 3f);
        Assert.Equal("Server", MenuPalette.SideCaption(CellTone.Server));
        Assert.Equal("Both", MenuPalette.SideCaption(CellTone.Both));
        Assert.Equal("—", MenuPalette.SideCaption(CellTone.Normal));
        // Button tones: Install is gold-rimmed, Installed ok-rimmed, Update warn-rimmed, Tool muted.
        Assert.Equal(MenuPalette.Gold, MenuPalette.Button(CellTone.Gold).Border);
        Assert.Equal(MenuPalette.Ok, MenuPalette.Button(CellTone.Ok).Border);
        Assert.Equal(MenuPalette.Warn, MenuPalette.Button(CellTone.Warn).Border);
        Assert.Equal(MenuPalette.Muted, MenuPalette.Button(CellTone.Disabled).Text);
        Assert.Equal(MenuPalette.RowSelected, MenuPalette.RowFillFor(true));
        Assert.Equal(MenuPalette.RowFill, MenuPalette.RowFillFor(false));
    }

    // ---- Handle -----------------------------------------------------------------------

    private IUiPanel NewPanel()
    {
        var panel = UiApi.Panel("List", _owner, "example.list1");
        _panels.Add(panel);
        return panel;
    }

    [Fact]
    public void Row_is_a_panel_child_with_its_key_as_text_and_its_own_click_slot()
    {
        var panel = NewPanel();
        var row = panel.CellRow(new CellRowSpec("dyno-ExampleCommandFramework", MockRow(), twoLine: true));
        Assert.True(row.Enabled);
        Assert.Equal("dyno-ExampleCommandFramework", row.Key);
        Assert.Equal("dyno-ExampleCommandFramework", row.Text);
        Assert.Single(_runtime.Rows);
        Assert.True(_runtime.Rows[0].TwoLine);
        int clicks = 0;
        using var lease = row.OnRowClick(() => clicks++);
        var handle = (UiHandle)row;
        Assert.NotEqual(0, handle.WidgetId);
        UiSlotTable.BootstrapDispatch(handle.WidgetId, (byte)UiSlotKind.Click, 0f, false, null);
        Assert.Equal(1, clicks);
    }

    [Fact]
    public void Button_cells_raise_their_own_click_not_the_row_click()
    {
        var panel = NewPanel();
        var row = panel.CellRow(new CellRowSpec("k", MockRow()));
        int rowClicks = 0, installClicks = 0;
        row.OnRowClick(() => rowClicks++);
        row.OnClick("install", () => installClicks++);
        // The button cell got its own wired id, distinct from the row's.
        var fake = _runtime.Rows[0];
        ushort installId = fake.WiredIdOf("install");
        Assert.NotEqual(0, installId);
        Assert.NotEqual(((UiHandle)row).WidgetId, installId);
        UiSlotTable.BootstrapDispatch(installId, (byte)UiSlotKind.Click, 0f, false, null);
        Assert.Equal(1, installClicks);
        Assert.Equal(0, rowClicks);
        // Non-button cells get a no-op lease.
        Assert.Same(Noop.Instance, row.OnClick("author", () => { }));
        Assert.Same(Noop.Instance, row.OnClick("missing", () => { }));
    }

    [Fact]
    public void SetCell_rewrites_in_place_without_a_panel_relayout()
    {
        var panel = NewPanel();
        var row = panel.CellRow(new CellRowSpec("k", MockRow()));
        var fake = _runtime.Rows[0];
        int relayoutsBefore = _runtime.Relayouts;
        Assert.True(row.SetCell("install", text: "Installed", tone: CellTone.Ok));
        Assert.True(row.SetCell("released", visible: false));
        Assert.False(row.SetCell("nope", text: "x"));
        Assert.Equal("Installed", row.Cells.Single(c => c.Key == "install").Text);
        Assert.Equal(CellTone.Ok, row.Cells.Single(c => c.Key == "install").Tone);
        Assert.False(row.Cells.Single(c => c.Key == "released").Visible);
        // The runtime repainted exactly those two cells and the panel was not laid out again.
        Assert.Equal(new[] { "install", "released" }, fake.Applied);
        Assert.Equal(relayoutsBefore, _runtime.Relayouts);
        // The image cell takes a sprite handle; a non-image key refuses.
        var sprite = new object();
        Assert.True(row.SetImage("icon", sprite));
        Assert.Same(sprite, fake.Images["icon"]);
        Assert.False(row.SetImage("name", sprite));
    }

    [Fact]
    public void Expanded_and_selected_state_forward_to_the_runtime_and_read_back()
    {
        var panel = NewPanel();
        var row = panel.CellRow(new CellRowSpec("k", MockRow()));
        var fake = _runtime.Rows[0];
        Assert.False(row.Expanded);
        Assert.False(row.Selected);
        row.SetExpanded(true);
        row.SetSelected(true);
        Assert.True(row.Expanded);
        Assert.True(row.Selected);
        Assert.True(fake.Expanded);
        Assert.True(fake.Selected);
        row.SetExpanded(false);
        Assert.False(fake.Expanded);
        var read = row.Describe();
        Assert.Equal(false, read["expanded"]);
        Assert.Equal(true, read["selected"]);
    }

    /// <summary>
    /// INSTALLCOLS1 kit additions: a <see cref="CellWidthMode.Fill"/> cell takes the remainder the
    /// spacers used to get; <see cref="CellSpec.DropOrder"/> picks which optional cell goes first
    /// (0 keeps the old rightmost-first rule); a header can mark its sorted column ascending.
    /// </summary>
    [Fact]
    public void Fill_cells_take_the_remainder_drop_order_picks_the_victim_and_the_header_shows_direction()
    {
        var fill = new List<CellSpec>
        {
            new CellSpec(CellKind.Text, "name").With(text: "Mod", width: CellWidth.Fill(100f)),
            new CellSpec(CellKind.Spacer, "gap"),
            new CellSpec(CellKind.Text, "n").With(text: "1", width: CellWidth.Px(80f), align: CellAlign.Right),
        };
        var placed = CellLayout.Resolve(600f, fill);
        Assert.Equal(600f - 80f - 2 * CellLayout.Gap, placed[0].Width, 1);
        Assert.Equal(0f, placed[1].Width, 1);
        Assert.Equal(placed[0].Right + CellLayout.Gap * 2, placed[2].Left, 1);

        static CellSpec Opt(string key, int order)
        {
            var spec = new CellSpec(CellKind.Text, key).With(text: key, width: CellWidth.Px(100f), optional: true);
            spec.DropOrder = order;
            return spec;
        }
        var ordered = new List<CellSpec>
        {
            new CellSpec(CellKind.Text, "name").With(text: "Mod", width: CellWidth.Fill(100f)),
            Opt("a", 2), Opt("b", 1), Opt("c", 3),
        };
        // 100 + 3×100 + gaps does not fit 390: "b" (order 1) goes first, then "a" (2); "c" stays.
        var dropped = CellLayout.Resolve(300f, ordered);
        Assert.True(dropped[2].Dropped);
        Assert.True(dropped[1].Dropped);
        Assert.True(dropped[3].Visible);
        // Without an order the rightmost optional cell goes first, as it always did.
        var plain = new List<CellSpec>
        {
            new CellSpec(CellKind.Text, "name").With(text: "Mod", width: CellWidth.Fill(100f)),
            Opt("a", 0), Opt("b", 0),
        };
        var old = CellLayout.Resolve(300f, plain);
        Assert.True(old[2].Dropped);
        Assert.True(old[1].Visible);

        var panel = NewPanel();
        var header = panel.CellHeader(new List<CellSpec>
        {
            new CellSpec(CellKind.Text, "name").With(text: "Mod", width: CellWidth.Fill(100f), sortKey: "name"),
        });
        header.SetSorted("name", ascending: true);
        Assert.True(header.SortAscending);
        var read = header.Describe();
        Assert.Equal(true, read["sortAscending"]);
        Assert.Equal("MOD" + CellLayout.SortIndicatorAscending, ((Dictionary<string, object?>)((List<object?>)read["cells"]!)[0]!)["text"]);
        header.SetSorted("name");
        Assert.False(header.SortAscending);
    }

    [Fact]
    public void Header_marks_one_sorted_column_and_raises_OnSort_with_the_cell_sort_key()
    {
        var panel = NewPanel();
        var cells = new List<CellSpec>
        {
            new CellSpec(CellKind.Text, "icon").With(text: "", width: CellWidth.Px(36f)),
            new CellSpec(CellKind.Text, "name").With(text: "Mod", width: CellWidth.Fraction(.26f), sortKey: "name"),
            new CellSpec(CellKind.Text, "author").With(text: "Author", width: CellWidth.Px(120f), optional: true),
            new CellSpec(CellKind.Spacer, "gap"),
            new CellSpec(CellKind.Text, "downloads").With(text: "Downloads", width: CellWidth.Px(100f), align: CellAlign.Right, sortKey: "dl"),
        };
        var header = panel.CellHeader(cells);
        Assert.True(header.Enabled);
        Assert.Equal("header", header.Key);
        Assert.Null(header.SortedKey);
        header.SetSorted("dl");
        Assert.Equal("dl", header.SortedKey);
        var fake = _runtime.Rows[0];
        Assert.True(fake.Header);
        Assert.Equal("dl", fake.SortedKey);
        var read = header.Describe();
        Assert.Equal("cellheader", read["kind"]);
        Assert.Equal("dl", read["sortedKey"]);
        var described = (List<object?>)read["cells"]!;
        var downloads = (Dictionary<string, object?>)described[4]!;
        var name = (Dictionary<string, object?>)described[1]!;
        Assert.Equal("DOWNLOADS" + CellLayout.SortIndicator, downloads["text"]);
        Assert.Equal("MOD", name["text"]);
        // Unknown key clears the mark.
        header.SetSorted("nope");
        Assert.Null(header.SortedKey);
        Assert.Equal("MOD", ((Dictionary<string, object?>)((List<object?>)header.Describe()["cells"]!)[1]!)["text"]);

        // Only sortable cells got a click slot; clicking one raises OnSort with its sort key.
        Assert.NotEqual(0, fake.WiredIdOf("name"));
        Assert.NotEqual(0, fake.WiredIdOf("downloads"));
        Assert.Equal(0, fake.WiredIdOf("author"));
        var sorted = new List<string>();
        using var lease = header.OnSort(sorted.Add);
        UiSlotTable.BootstrapDispatch(fake.WiredIdOf("downloads"), (byte)UiSlotKind.Click, 0f, false, null);
        UiSlotTable.BootstrapDispatch(fake.WiredIdOf("name"), (byte)UiSlotKind.Click, 0f, false, null);
        Assert.Equal(new[] { "dl", "name" }, sorted);
        lease.Dispose();
        UiSlotTable.BootstrapDispatch(fake.WiredIdOf("name"), (byte)UiSlotKind.Click, 0f, false, null);
        Assert.Equal(2, sorted.Count);
    }

    [Fact]
    public void Readback_carries_kind_key_cells_rects_expanded_and_selected()
    {
        var panel = NewPanel();
        var row = panel.CellRow(new CellRowSpec("dyno-ECF", MockRow(), twoLine: true));
        row.SetSelected(true);
        var read = row.Describe();
        Assert.Equal("cellrow", read["kind"]);
        Assert.Equal("dyno-ECF", read["key"]);
        Assert.Equal("dyno-ECF", read["label"]);
        Assert.Equal(true, read["twoLine"]);
        Assert.Equal(53f, read["height"]);
        Assert.Equal(true, read["selected"]);
        Assert.Equal(false, read["expanded"]);
        var cells = (List<object?>)read["cells"]!;
        Assert.Equal(MockRow().Count, cells.Count);
        var install = (Dictionary<string, object?>)cells[^1]!;
        Assert.Equal(new[] { "key", "kind", "text", "tone", "visible", "rect" }, install.Keys.ToArray());
        Assert.Equal("install", install["key"]);
        Assert.Equal("button", install["kind"]);
        Assert.Equal("Install · restart", install["text"]);
        Assert.Equal("gold", install["tone"]);
        Assert.Equal(true, install["visible"]);
        // The fake runtime measures every cell: rects are {x,y,w,h} in screen pixels.
        var rect = (Dictionary<string, object?>)install["rect"]!;
        Assert.Equal(new[] { "x", "y", "w", "h" }, rect.Keys.ToArray());
        Assert.Equal(200f, rect["w"]);
        var rowRect = (Dictionary<string, object?>)read["rect"]!;
        Assert.Equal(1664f, rowRect["w"]);
        // The bridge finds the row by key while it is visible, and not once hidden or disposed.
        Assert.NotNull(UiCellRowHandle.DescribeVisible("dyno-ECF"));
        row.Visible = false;
        Assert.Null(UiCellRowHandle.DescribeVisible("dyno-ECF"));
        row.Visible = true;
        row.Dispose();
        Assert.Null(UiCellRowHandle.DescribeVisible("dyno-ECF"));
    }

    [Fact]
    public void Bridge_click_reaches_the_row_the_cell_or_a_caption()
    {
        var panel = NewPanel();
        var row = panel.CellRow(new CellRowSpec("dyno-ECF", MockRow()));
        int rowClicks = 0, installClicks = 0;
        row.OnRowClick(() => rowClicks++);
        row.OnClick("install", () => installClicks++);
        Assert.True(UiCellRowHandle.TryInvoke("dyno-ECF", out var detail));
        Assert.Equal("cellrow", detail["kind"]);
        Assert.Equal(1, rowClicks);
        Assert.True(UiCellRowHandle.TryInvoke("dyno-ECF/install", out detail));
        Assert.Equal("cell", detail["kind"]);
        Assert.Equal(1, installClicks);
        Assert.True(UiCellRowHandle.TryInvoke("install · restart", out detail));
        Assert.Equal(2, installClicks);
        Assert.False(UiCellRowHandle.TryInvoke("dyno-ECF/author", out _));
        Assert.False(UiCellRowHandle.TryInvoke("nobody", out _));
        Assert.Equal(1, rowClicks);
    }

    /// <summary>
    /// Live 2026-10-14: the bridge dispatched a grayed control's slot and reported it clicked. A cell drawn
    /// <see cref="CellTone.Disabled"/> (the runtime sets its button non-interactable) and a row that is not interactable
    /// are refused with the reason, by key, by caption and through a dialog's own row, and nothing runs. A bare caption
    /// prefers a clickable match over a grayed one. What this would catch: a bridge click succeeding where a player's
    /// cannot.
    /// </summary>
    [Fact]
    public void Bridge_click_refuses_a_disabled_cell_or_a_row_that_is_not_interactable()
    {
        var panel = NewPanel();
        var row = panel.CellRow(new CellRowSpec("dyno-ECF", MockRow()));
        int rowClicks = 0, installClicks = 0;
        row.OnRowClick(() => rowClicks++);
        row.OnClick("install", () => installClicks++);
        Assert.True(row.SetCell("install", tone: CellTone.Disabled));

        Assert.False(UiCellRowHandle.TryInvoke("dyno-ECF/install", out var detail));
        string? why = UiClickGate.RefusalOf(detail);
        Assert.NotNull(why);
        Assert.Contains("is disabled (not interactable); not clicked", why!, StringComparison.Ordinal);
        Assert.Contains("'Install · restart'", why!, StringComparison.Ordinal);
        Assert.False(UiCellRowHandle.TryInvoke("install · restart", out detail));
        Assert.NotNull(UiClickGate.RefusalOf(detail));
        var cellRow = (UiCellRowHandle)row;
        Assert.False(cellRow.TryInvokeCell("install", out detail));
        Assert.NotNull(UiClickGate.RefusalOf(detail));
        Assert.Equal(0, installClicks);

        // A second row showing the same caption, clickable: a bare caption reaches it, not the grayed one.
        var other = panel.CellRow(new CellRowSpec("other-ECF", MockRow()));
        int otherClicks = 0;
        other.OnClick("install", () => otherClicks++);
        Assert.True(UiCellRowHandle.TryInvoke("install · restart", out detail));
        Assert.Equal("other-ECF/install", detail["label"]);
        Assert.Equal(1, otherClicks);
        Assert.Equal(0, installClicks);

        // The tone back to normal: clickable again.
        Assert.True(row.SetCell("install", tone: CellTone.Gold));
        Assert.True(UiCellRowHandle.TryInvoke("dyno-ECF/install", out _));
        Assert.Equal(1, installClicks);

        // A row that is not interactable refuses its own click and its cells', with its help line as the reason.
        cellRow.Help = "Needs the server part of this mod.";
        row.Interactable = false;
        Assert.False(UiCellRowHandle.TryInvoke("dyno-ECF", out detail));
        Assert.Contains("Needs the server part of this mod.", UiClickGate.RefusalOf(detail)!, StringComparison.Ordinal);
        Assert.False(cellRow.TryInvokeRow(out detail));
        Assert.NotNull(UiClickGate.RefusalOf(detail));
        Assert.False(UiCellRowHandle.TryInvoke("dyno-ECF/install", out _));
        Assert.Equal(0, rowClicks);
        Assert.Equal(1, installClicks);
        row.Interactable = true;
        Assert.True(UiCellRowHandle.TryInvoke("dyno-ECF", out _));
        Assert.Equal(1, rowClicks);
    }

    [Fact]
    public void Disposing_the_row_releases_every_slot_and_the_registry_entry()
    {
        var panel = NewPanel();
        int before = UiCellRowHandle.LiveCountForTests;
        var row = panel.CellRow(new CellRowSpec("k", MockRow()));
        var fake = _runtime.Rows[0];
        ushort rowId = ((UiHandle)row).WidgetId;
        ushort installId = fake.WiredIdOf("install");
        Assert.Equal(before + 1, UiCellRowHandle.LiveCountForTests);
        row.Dispose();
        Assert.True(row.IsDisposed);
        Assert.Equal(before, UiCellRowHandle.LiveCountForTests);
        Assert.Contains(rowId, _runtime.Unwired);
        Assert.Contains(installId, _runtime.Unwired);
        Assert.True(fake.Released);
        row.Dispose();
        Assert.Equal(before, UiCellRowHandle.LiveCountForTests);
    }

    [Fact]
    public void A_runtime_without_cell_rows_yields_a_disabled_row_that_keeps_its_state()
    {
        var plain = new PlainRuntime();
        UiApi.SetRuntimeForTests(plain);
        var panel = UiApi.Panel("List", _owner, "example.list1");
        _panels.Add(panel);
        var row = panel.CellRow(new CellRowSpec("k", MockRow()));
        Assert.False(row.Enabled);
        Assert.Equal("k", row.Key);
        Assert.True(row.SetCell("install", text: "Installed"));
        Assert.Equal("Installed", row.Cells.Single(c => c.Key == "install").Text);
        row.SetSelected(true);
        Assert.True(row.Selected);
        Assert.Same(Noop.Instance, row.OnRowClick(() => { }));
        var read = row.Describe();
        Assert.Equal("cellrow", read["kind"]);
        Assert.Null(read["rect"]);
        Assert.Null(UiCellRowHandle.DescribeVisible("k"));
        var header = panel.CellHeader(MockRow());
        Assert.False(header.Enabled);
        Assert.Same(Noop.Instance, header.OnSort(_ => { }));
    }

    // ---- Fakes ------------------------------------------------------------------------

    /// <summary>A runtime that hosts cell rows and records what the handle asks of them.</summary>
    internal sealed class FakeCellRuntime : PlainRuntime
    {
        public readonly List<FakeCellRow> Rows = new();

        /// <summary>LIST3: the (sidebar, mode) pairs the menu configured panes with, in order.</summary>
        public readonly List<(bool Sidebar, MenuPaneMode Mode)> ConfiguredPanes = new();

        public override IRuntimeCellRow? AddCellRow(IRuntimePanel panel, CellRowSpec spec, bool header)
        {
            var row = new FakeCellRow(this, spec, header);
            Rows.Add(row);
            return row;
        }

        public override void ConfigureMenuPanel(IRuntimePanel panel, bool sidebar, MenuPaneMode mode) =>
            ConfiguredPanes.Add((sidebar, mode));
    }

    internal sealed class FakeCellRow : IRuntimeCellRow
    {
        /// <summary>
        /// Null when another suite's fake runtime hosts the row (KIT1: <c>UiTests.FakeUiRuntime</c>
        /// reuses this row so there is one fake cell row in the suite, not two). A row with no
        /// runtime simply has nowhere to count a relayout.
        /// </summary>
        private readonly FakeCellRuntime? _runtime;
        private readonly CellRowSpec _spec;
        private readonly Dictionary<string, FakeWidget> _widgets = new();
        public readonly List<string> Applied = new();
        public readonly Dictionary<string, object?> Images = new();
        public bool Expanded, Selected, Released, Header, TwoLine;
        public string? SortedKey;
        private bool _visible = true;

        public FakeCellRow(FakeCellRuntime? runtime, CellRowSpec spec, bool header)
        {
            _runtime = runtime;
            _spec = spec;
            Header = header;
            TwoLine = spec.TwoLine;
            foreach (var cell in spec.Cells)
            {
                // LIST3: input cells get a widget too (a text slot is wired to it).
                if (UiCellRowHandle.HasWidget(cell, header))
                    _widgets[cell.Key] = new FakeWidget();
            }
        }

        public ushort WiredIdOf(string key) => _widgets.TryGetValue(key, out var w) ? w.WiredId : (ushort)0;

        /// <summary>SETTINGS1 stage 2: the cells this row was built with (text, kind, alignment as specified).</summary>
        public IReadOnlyList<CellSpec> Cells() => _spec.Cells;
        public CellRowSpec Spec => _spec;

        public IRuntimeWidget? CellWidget(string key) => _widgets.TryGetValue(key, out var w) ? w : null;
        public void ApplyCell(CellSpec spec) => Applied.Add(spec.Key);
        public void SetImage(string key, object? sprite) => Images[key] = sprite;
        public void SetExpanded(bool expanded) => Expanded = expanded;
        public void SetSelected(bool selected) => Selected = selected;
        public void SetSorted(string? sortKey) => SortedKey = sortKey;

        public IReadOnlyList<KeyValuePair<string, ScreenRect>> CellRects()
        {
            var list = new List<KeyValuePair<string, ScreenRect>>();
            // HEAD2: the row, not only its cells, so a rect read back here carries the row's own
            // right inset exactly as UnityCellRow.PlaceCells applies it.
            var placed = MenuPanelLayout.CellColumns(1664f, _spec);
            foreach (var p in placed)
            {
                if (!p.Visible) continue;
                list.Add(new KeyValuePair<string, ScreenRect>(p.Key, ScreenRect.FromCorners(832f + p.Left, 100f, 832f + p.Right, 100f + _spec.Height)));
            }
            return list;
        }

        /// <summary>P7: a test may place the row (an ordered list's slots stacked down the screen).</summary>
        public ScreenRect? RectOverride;

        public ScreenRect RowRect() => RectOverride ?? ScreenRect.FromCorners(0f, 100f, 1664f, 100f + _spec.Height);

        public string Text { get => _spec.Key; set { } }
        public bool Visible { get => _visible; set { if (_visible != value && _runtime != null) _runtime.Relayouts++; _visible = value; } }
        public bool BoolValue { get; set; }
        public float FloatValue { get; set; }
        public string StringValue { get; set; } = "";
        public bool Alive => !Released;

        /// <summary>
        /// RADIOCLICK1: as the Unity row: a click target only when <see cref="CellRowSpec.RowClickAtBuild"/> says the
        /// row is built with one, or after <see cref="EnsureRowClick"/>. Before, every fake row had one, which is why the
        /// suite never saw a plain row's bound click that no pointer could reach.
        /// </summary>
        public object? ClickEvent => _click;
        private object? _click;
        private bool _clickInit;

        public object? EnsureRowClick()
        {
            InitClick();
            _click ??= new object();
            EnsuredClick = true;
            return _click;
        }

        private void InitClick()
        {
            if (_clickInit) return;
            _clickInit = true;
            if (CellRowSpec.RowClickAtBuild(_spec, Header)) _click = new object();
        }

        /// <summary>RADIOCLICK1: whether <see cref="EnsureRowClick"/> ran (a plain row asked for a click target).</summary>
        public bool EnsuredClick;

        /// <summary>RADIOCLICK1: the slot the row's click event is wired to (0: not wired).</summary>
        public ushort RowWiredId;

        /// <summary>
        /// RADIOCLICK1: a pointer click on the row, as the input module delivers it: only through the row's own click
        /// event and the slot wired to it. False (nothing ran) when the row has no click target or it is not wired.
        /// </summary>
        public bool PointerClick()
        {
            InitClick();
            if (Released || _click == null || RowWiredId == 0) return false;
            UiSlotTable.BootstrapDispatch(RowWiredId, (byte)UiSlotKind.Click, 0f, false, null);
            return true;
        }

        /// <summary>RADIOCLICK1: the runtime's TryWire for this row's click: refused when there is no click event.</summary>
        public bool WireClick(ushort id)
        {
            InitClick();
            if (_click == null) return false;
            RowWiredId = id;
            return true;
        }

        public object? FloatEvent => null;
        public object? BoolEvent => null;
        public object? TextEvent => null;
        public object? PointerEnterEvent => null;
        public object? PointerExitEvent => null;
        public void Release() => Released = true;
    }

    internal sealed class FakeWidget : IRuntimeWidget
    {
        public ushort WiredId;
        public string Text { get; set; } = "";
        public bool Visible { get; set; } = true;
        public bool BoolValue { get; set; }
        public float FloatValue { get; set; }
        public string StringValue { get; set; } = "";
        public bool Alive => true;
        public object? ClickEvent { get; } = new object();
        public object? FloatEvent => null;
        public object? BoolEvent => null;
        public object? TextEvent => null;
        public object? PointerEnterEvent => null;
        public object? PointerExitEvent => null;
        public void Release() { }
    }

    private sealed class FakePanel : IRuntimePanel
    {
        public string Text { get; set; } = "";
        public bool Visible { get; set; } = true;
        public bool BoolValue { get; set; }
        public float FloatValue { get; set; }
        public string StringValue { get; set; } = "";
        public bool Alive => true;
        public object? ClickEvent => null;
        public object? FloatEvent => null;
        public object? BoolEvent => null;
        public object? TextEvent => null;
        public object? PointerEnterEvent => null;
        public object? PointerExitEvent => null;
        public void Release() { }
    }

    /// <summary>The smallest client runtime: panels and wiring only; no cell rows unless overridden.</summary>
    internal class PlainRuntime : IUiRuntime
    {
        public int Relayouts;
        public readonly List<ushort> Unwired = new();
        public bool IsClient => true;
        public virtual IRuntimePanel? CreatePanel(string title, string rootName) => new FakePanel { Text = title };

        /// <summary>INSTALLUX2: the modal-dialog chrome ask, recorded by the suite that cares.</summary>
        public virtual void DialogChrome(IRuntimePanel panel) { }
        public virtual IRuntimeCellRow? AddCellRow(IRuntimePanel panel, CellRowSpec spec, bool header) => null;
        public IRuntimeWidget? AddButton(IRuntimePanel panel, string text) => new FakeWidget { Text = text };
        public IRuntimeWidget? AddToggle(IRuntimePanel panel, string text, bool initial) => new FakeWidget { Text = text, BoolValue = initial };
        public IRuntimeWidget? AddLabel(IRuntimePanel panel, string text) => new FakeWidget { Text = text };
        public IRuntimeWidget? AddSlider(IRuntimePanel panel, string text, float min, float max, float initial) => new FakeWidget { Text = text, FloatValue = initial };
        public IRuntimeWidget? AddInput(IRuntimePanel panel, string text, string initial) => new FakeWidget { Text = text, StringValue = initial };
        public virtual void ConfigureMenuPanel(IRuntimePanel panel, bool sidebar, MenuPaneMode mode) { }
        public void PlacePanel(IRuntimePanel panel, PanelSpot spot) { }
        public virtual void Show(IRuntimePanel panel) { }
        public void Hide(IRuntimePanel panel) { }

        public bool TryWire(ushort widgetId, UiSlotKind kind, IRuntimeWidget widget)
        {
            // Click slots (button / sortable cells) and, since LIST3, text slots (input cells).
            if (widget is FakeWidget fake && (kind == UiSlotKind.Click || kind == UiSlotKind.Text)) fake.WiredId = widgetId;
            // RADIOCLICK1: as UnityUiRuntime.TryWire: no click event, nothing wired.
            if (widget is FakeCellRow row && kind == UiSlotKind.Click) return row.WireClick(widgetId);
            return true;
        }

        public void Unwire(ushort widgetId) => Unwired.Add(widgetId);
        public void UnwireMany(IReadOnlyList<ushort> ids) => Unwired.AddRange(ids);
        public int DestroyRoot(string rootName) => 0;
        public int DestroyByPrefix(string prefix) => 0;
        public void SetCapture(bool capture) { }
        public ScreenRect PanelRect(IRuntimePanel panel) => ScreenRect.Unknown;
        public PointerSample ReadPointer() => PointerSample.Unreadable;
    }
}

internal static class CellToneTestExtensions
{
    /// <summary>The unknown side is any tone the badge table has no side for; <see cref="CellTone.Normal"/> is the one the pane passes.</summary>
    internal static CellTone Unknown() => CellTone.Normal;
}
