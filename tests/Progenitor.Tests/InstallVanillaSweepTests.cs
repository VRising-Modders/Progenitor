using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Progenitor.Install;
using Progenitor.Ui;
using Xunit;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.Tests;

/// <summary>
/// VANILLA1 (user, 2026-10-03: "give this page another sweep to make it feel first class / part of
/// the Vanilla program"): the Install tab drawn in the game's own idiom — column washes with an edge
/// bar and the game's top gradient instead of opaque boxed bands, side headers over the game's
/// divider, small-caps header captions in their own case, chips on the game's popup sprite, the search
/// on the game's value box, a quiet icon well, the game's faint line between entries, and secondary
/// figures in the resting gray. The approved structure (per-side columns, filter groups, chevron
/// drawer, picker) is unchanged. The toolbar and filter rows fit without overlap at 1236, 1664 and
/// 860 px; the header and listing rows at 1236, 1664 and the 1600x1024 fallback's full-width row
/// (the narrowest row the full-width pane gets). An 860 px listing row cannot hold the two fixed side
/// columns (2 x 330 px plus the Mod floor): that limit dates from INSTALLSIDES2 and is pinned below
/// so it cannot silently become an overlap.
/// Game-free: the palette, the specs and the layout model; the runtime parts are source-pinned.
/// </summary>
public sealed class InstallVanillaSweepTests : IDisposable
{
    private const string KnownClientBuild = InstallSearchFilterTests.KnownClientBuild;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "progenitor-vanilla1-" + Guid.NewGuid().ToString("N"));

    public InstallVanillaSweepTests()
    {
        UiLayoutBatch.ResetForTests();
        UiApi.ResetForTests();
        CatalogUpdateTags.ResetProvidersForTests();
        CatalogUpdateTags.ConfiguredTagProvider = () => null;
        CatalogUpdateTags.BuildProvider = () => KnownClientBuild;
        InstallModsPane.ProcessFilterProbe = null;
        InstallModsPane.PluginFileProbe = null;
        InstallModsPane.PlainOfferProbe = null;
        InstallTimes.ClockUtc = null;
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        UiLayoutBatch.ResetForTests();
        CatalogUpdateTags.ResetProvidersForTests();
        InstallModsPane.PluginFileProbe = null;
        InstallModsPane.PlainOfferProbe = null;
        InstallTimes.ClockUtc = null;
        UiApi.ResetForTests();
        try { Directory.Delete(_root, true); } catch { }
    }

    /// <summary>The widths the sweep is checked at: the live row, the 3840x1080 frame's 1664 px, and a narrow 860 px.</summary>
    public static IEnumerable<object[]> SweepWidths()
    {
        yield return new object[] { 1236f };
        yield return new object[] { 1664f };
        yield return new object[] { 860f };
    }

    /// <summary>The listing-row widths: the live row, the 1664 px frame, and the 1600x1024 fallback's full-width row.</summary>
    public static IEnumerable<object[]> ListWidths()
    {
        yield return new object[] { 1236f };
        yield return new object[] { 1664f };
        yield return new object[] { Row1600 };
    }

    private static float Row1600 =>
        MenuPanelLayout.ForFrame(ModsWorkspaceFrame.Fallback(1600f, 1024f), sidebar: false, InstallModsPane.PaneMode).RowWidth;

    private static readonly CatalogEntry[] Listings =
    {
        InstallListViewTests.Listing("v-client", "ClientThing", downloads: 1116, categories: new[] { "Client", "Oakveil Update" }),
        InstallListViewTests.Listing("v-server", "ServerThing", downloads: 2675, categories: new[] { "Server", "Oakveil Update" }),
        InstallListViewTests.Listing("v-both", "BothThing", downloads: 1234567, categories: new[] { "Server", "Client", "Oakveil Update" }),
        InstallListViewTests.Listing("v-tool", "ToolThing", downloads: 517, categories: new[] { "Tools", "Oakveil Update" }),
        InstallListViewTests.Listing("v-long", "AVeryLongModNameThatGoesOnAndOnForever", downloads: 0, categories: new[] { "Oakveil Update" }),
    };

    private static InstallModsPane NewPane(float rowWidth)
    {
        var catalog = new InstallSidesPaneTests.FakeCatalog();
        catalog.Entries.AddRange(Listings);
        return new InstallModsPane(catalog) { ColumnRowWidthProbe = () => rowWidth };
    }

    private static void AssertNoOverlap(CellPlacement[] placed, float rowWidth, string what, ISet<string>? overlays = null)
    {
        var shown = placed.Where(p => p.Visible && (overlays == null || !overlays.Contains(p.Key))).ToArray();
        for (int i = 0; i < shown.Length; i++)
        {
            Assert.True(shown[i].Width >= 0f, shown[i] + " is negative at " + rowWidth + " in " + what);
            Assert.True(shown[i].Left >= -rowWidth / 2f - .01f && shown[i].Right <= rowWidth / 2f + .01f, shown[i] + " leaves the row at " + rowWidth + " in " + what);
            for (int j = i + 1; j < shown.Length; j++)
                Assert.False(CellLayout.Overlap(shown[i], shown[j]), shown[i] + " overlaps " + shown[j] + " at " + rowWidth + " in " + what);
        }
    }

    // ---- palette: washes, edges, separator --------------------------------------------------

    [Fact]
    public void Column_washes_keep_blue_and_red_identity_over_the_pane_and_every_side_word_stays_readable()
    {
        // The game blends in linear space (calibrated in The_linear_blend_model_matches_the_live_capture).
        var client = MenuPalette.OverLinear(MenuPalette.ColumnClientWash, MenuPalette.PaneUnder);
        var server = MenuPalette.OverLinear(MenuPalette.ColumnServerWash, MenuPalette.PaneUnder);
        // Even over the pane's dark red, the client wash is bluer than it is red; the server wash is red.
        Assert.True(client.B > client.R + 0.08f && client.B > client.G, client.ToString());
        Assert.True(server.R > server.G + 0.15f && server.R > server.B + 0.15f, server.ToString());
        // Round 2 ("go quieter on the cells"): a gentle wash, well under the 40% that read as blocks.
        Assert.InRange(MenuPalette.ColumnClientWash.A, 0.15f, 0.25f);
        Assert.InRange(MenuPalette.ColumnServerWash.A, 0.15f, 0.25f);
        Assert.True(client.Luminance < MenuPalette.OverLinear(MenuPalette.ColumnClientWash.WithAlpha(0.40f), MenuPalette.PaneUnder).Luminance * 0.75f);
        Assert.True(client.Luminance <= MenuPalette.ColumnClientCellFill.Luminance + 0.01f);
        // Every word color a side cell uses clears WCAG 4.5:1 on the composited wash.
        foreach (var band in new[] { client, server })
        {
            foreach (var word in new[] { MenuPalette.Muted, MenuPalette.Text, MenuPalette.OkText, MenuPalette.InfoText, MenuPalette.PendText })
                Assert.True(Wcag(word, band) >= 4.49f, word + " on " + band + ": " + Wcag(word, band));
        }
        // Header captions (the column's light text) on the bare pane.
        Assert.True(Wcag(MenuPalette.ColumnClientButtonText, MenuPalette.PaneUnder) >= 7f);
        Assert.True(Wcag(MenuPalette.ColumnServerButtonText, MenuPalette.PaneUnder) >= 7f);
    }

    [Fact]
    public void The_linear_blend_model_matches_the_live_capture()
    {
        // installsides-38-sweep (Core 2026.10.39.1): the 40% client and server washes and the 6% soft
        // hover over the pane, as read from the capture. An sRGB blend would predict #202a40 for the
        // client wash; the game's linear blend predicts within a few levels.
        static void Near(PaletteColor c, int r, int g, int b)
        {
            Assert.InRange(c.R * 255f, r - 4f, r + 4f);
            Assert.InRange(c.G * 255f, g - 4f, g + 4f);
            Assert.InRange(c.B * 255f, b - 4f, b + 4f);
        }
        Near(MenuPalette.OverLinear(MenuPalette.ColumnClientWash.WithAlpha(0.40f), MenuPalette.PaneUnder), 31, 66, 102);
        Near(MenuPalette.OverLinear(MenuPalette.ColumnServerWash.WithAlpha(0.40f), MenuPalette.PaneUnder), 91, 22, 27);
        Near(MenuPalette.OverLinear(ModsListLayout.RowHover(false), MenuPalette.PaneUnder), 67, 58, 54);
    }

    /// <summary>
    /// Round 2 (user: "fix the hover"): on a hovered row every side-cell word tone — muted, normal,
    /// installed green, amber, pending blue — keeps 4.5:1 on a column wash and on the bare list, the
    /// Mod column's warn note keeps 4.5:1 on the bare list, and the deliberately dim grayed words lose
    /// at most 15% of their resting contrast (the soft hover cut them from 2.6:1 to about 1.4:1).
    /// </summary>
    [Fact]
    public void Hover_is_a_quiet_lift_that_keeps_every_text_tone_readable()
    {
        var pane = MenuPalette.PaneUnder;
        var hovered = MenuPalette.OverLinear(MenuPalette.RowHoverQuiet, pane);
        Assert.True(hovered.Luminance > pane.Luminance, "the hover still lifts the row");
        var sideWords = new[] { MenuPalette.Muted, MenuPalette.Text, MenuPalette.OkText, MenuPalette.PendText, MenuPalette.InfoText };
        foreach (var under in new[] { pane, hovered })
        {
            foreach (var wash in new[] { MenuPalette.ColumnClientWash, MenuPalette.ColumnServerWash })
            {
                var band = MenuPalette.OverLinear(wash, under);
                foreach (var word in sideWords)
                    Assert.True(Wcag(word, band) >= 4.5f, word + " on " + band + ": " + Wcag(word, band));
            }
            foreach (var word in sideWords.Append(MenuPalette.Warn))
                Assert.True(Wcag(word, under) >= 4.5f, word + " on " + under + ": " + Wcag(word, under));
        }
        Assert.True(Wcag(MenuPalette.GrayedText, hovered) >= 0.85f * Wcag(MenuPalette.GrayedText, pane));
        // The old soft hover failed exactly this: grayed words nearly vanished.
        var soft = MenuPalette.OverLinear(ModsListLayout.RowHover(false), pane);
        Assert.True(Wcag(MenuPalette.GrayedText, soft) < 0.6f * Wcag(MenuPalette.GrayedText, pane));
        // The hover is the game's entry hue, not a gray lift.
        Assert.True(MenuPalette.RowHoverQuiet.B > MenuPalette.RowHoverQuiet.R && MenuPalette.RowHoverQuiet.A <= 0.06f);
    }

    [Fact]
    public void Column_look_is_wash_edge_and_top_for_a_row_a_rule_for_a_header_and_only_a_faint_edge_when_grayed()
    {
        foreach (var column in new[] { CellColumn.Client, CellColumn.Server })
        {
            var accent = column == CellColumn.Client ? MenuPalette.ColumnClientRule : MenuPalette.ColumnServerRule;
            var (wash, edge, top) = MenuPalette.ColumnLook(column, header: false, grayed: false);
            Assert.True(wash.A > 0f);
            Assert.Equal(accent, edge);
            Assert.True(top.A > 0f && top.A < edge.A, "the top gradient is fainter than the edge");

            var (hWash, hEdge, hTop) = MenuPalette.ColumnLook(column, header: true, grayed: false);
            Assert.Equal(0f, hWash.A);   // no box behind a header caption
            Assert.Equal(accent, hEdge); // the divider under it
            Assert.Equal(0f, hTop.A);

            var (gWash, gEdge, gTop) = MenuPalette.ColumnLook(column, header: false, grayed: true);
            Assert.Equal(0f, gWash.A);
            Assert.Equal(0f, gTop.A);
            Assert.InRange(gEdge.A, 0.05f, 0.3f);
        }
        var none = MenuPalette.ColumnLook(CellColumn.None, false, false);
        Assert.Equal(0f, none.Wash.A + none.Edge.A + none.Top.A);
        // The separator is the game's steel entry gradient, faint.
        Assert.True(MenuPalette.RowSeparator.B > MenuPalette.RowSeparator.R && MenuPalette.RowSeparator.A <= 0.25f);
        Assert.InRange(MenuPalette.ColumnEdgeWidth, 2f, 6f);
        Assert.True(MenuPalette.ColumnEdgeWidth <= InstallColumns.BandPad, "the edge bar sits in the band's pad, left of the words");
    }

    // ---- typography -------------------------------------------------------------------------

    [Fact]
    public void Header_captions_keep_their_case_in_small_caps_and_readback_matches()
    {
        var pane = NewPane(1236f);
        pane.SetSort(InstallSort.Downloads);
        foreach (var cell in pane.HeaderCells().Where(c => c.Text.Length > 0))
        {
            Assert.True(cell.SmallCaps, cell.Key);
            Assert.Equal(InstallColumns.HeaderFontSize, cell.FontSize);
            Assert.NotEqual(cell.Text.ToUpperInvariant(), cell.Text);
        }
        Assert.Equal("Downloads" + CellLayout.SortIndicator, CellLayout.HeaderCaption(InstallModsCopy.HeaderDownloads, true, false, smallCaps: true));
        Assert.Equal("Mod", CellLayout.HeaderCaption(InstallModsCopy.HeaderMod, false, false, smallCaps: true));
        // The kit's other headers (the Mods list) keep their upper case.
        Assert.Equal("MOD", CellLayout.HeaderCaption("Mod", sorted: false));
        // Small caps measure narrower than all caps at the same size, wider than nothing.
        float small = InstallColumns.HeaderCaptionWidth("Downloads", true, smallCaps: true, InstallColumns.HeaderFontSize);
        float upper = InstallColumns.HeaderCaptionWidth("Downloads", true, smallCaps: false, InstallColumns.HeaderFontSize);
        Assert.True(small < upper && small > 60f, small + " vs " + upper);
    }

    [Fact]
    public void Secondary_figures_are_muted_and_the_count_is_quiet()
    {
        var pane = NewPane(1236f);
        foreach (var entry in Listings)
        {
            var cells = pane.RowCells(entry);
            Assert.Equal(CellTone.Muted, cells.Single(c => c.Key == InstallModsPane.CellDownloads).Tone);
            Assert.Equal(CellKind.Muted, cells.Single(c => c.Key == InstallModsPane.CellReleased).Kind);
            Assert.Equal(CellSkin.IconWell, cells.Single(c => c.Key == InstallModsPane.CellIcon).Skin);
        }
        var toolbar = pane.ToolbarCells();
        Assert.Equal(CellTone.Muted, toolbar.Single(c => c.Key == InstallModsPane.CellCount).Tone);
        Assert.Equal(CellSkin.ValueBox, toolbar.Single(c => c.Key == InstallModsPane.CellSearch).Skin);
    }

    // ---- chips ------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(SweepWidths))]
    public void Chips_are_the_games_popup_sprite_and_every_caption_fits_inside_the_sprites_ends(float rowWidth)
    {
        var pane = NewPane(rowWidth);
        foreach (var (key, cells) in pane.FilterRowsFor(rowWidth))
        {
            var placed = MenuPanelLayout.CellColumns(rowWidth, cells);
            foreach (var chip in cells.Where(c => c.Kind == CellKind.Button && c.Visible))
            {
                Assert.Equal(CellSkin.Chip, chip.Skin);
                var p = placed.Single(x => x.Key == chip.Key);
                if (!p.Visible || chip.Width.Mode != CellWidthMode.Px) continue;
                // The caption has the plate's room: the width minus the sprite's decorated ends.
                float room = p.Width - 2f * InstallColumns.ButtonTintInsetX;
                Assert.True(CellLayout.EstimateTextWidth(chip.Text, chip.FontSize) <= room + .5f,
                    chip.Key + " '" + chip.Text + "' needs more than " + room + " at " + rowWidth + " in " + key);
            }
        }
    }

    // ---- layout: no overlap at 1236 / 1664 / 860 ---------------------------------------------

    [Theory]
    [MemberData(nameof(SweepWidths))]
    public void Toolbar_and_filter_rows_place_without_overlap(float rowWidth)
    {
        var pane = NewPane(rowWidth);
        AssertNoOverlap(MenuPanelLayout.CellColumns(rowWidth, pane.ToolbarCells()), rowWidth, "toolbar");
        foreach (var (key, cells) in pane.FilterRowsFor(rowWidth))
            AssertNoOverlap(MenuPanelLayout.CellColumns(rowWidth, cells), rowWidth, key);
    }

    [Fact]
    public void The_listing_rows_need_more_than_860_px_and_the_full_width_pane_always_gives_it()
    {
        // The narrowest listing row: expand, icon, the Mod floor, both fixed side columns, gaps.
        float need = InstallColumns.ExpandWidth + CellLayout.ImageSize + InstallColumns.ModMinWidth
            + 2f * InstallColumns.SideColumnWidth + 4f * CellLayout.Gap + HeadBlockLayout.RightColumnInset;
        Assert.True(need > 860f, need.ToString());
        Assert.True(Row1600 >= need + InstallColumns.DownloadsWidth + CellLayout.Gap, "fallback row " + Row1600 + " vs " + need);
        Assert.True(InstallColumns.LiveRowWidth >= need + InstallColumns.DownloadsWidth + CellLayout.Gap);
    }

    [Theory]
    [MemberData(nameof(ListWidths))]
    public void Header_and_listing_rows_place_without_overlap_and_header_captions_fit(float rowWidth)
    {
        var pane = NewPane(rowWidth);
        foreach (InstallSort sort in Enum.GetValues(typeof(InstallSort)))
        {
            pane.SetSort(sort);
            var header = pane.HeaderCells();
            var placedHeader = InstallColumns.Resolve(rowWidth, header);
            AssertNoOverlap(placedHeader, rowWidth, "header by " + sort);
            foreach (var cell in header.Where(c => c.Text.Length > 0))
            {
                var p = placedHeader.Single(x => x.Key == cell.Key);
                if (!p.Visible) continue;
                float need = InstallColumns.HeaderCaptionWidth(cell.Text, !string.IsNullOrEmpty(cell.SortKey), cell.SmallCaps, cell.FontSize);
                Assert.True(p.Width >= need - .01f, cell.Key + " needs " + need + " in " + p.Width + " at " + rowWidth);
            }

            foreach (var entry in Listings)
            {
                var cells = pane.RowCells(entry);
                var overlays = cells.Where(c => c.OverlayOf != null).Select(c => c.Key).ToHashSet();
                var placed = InstallColumns.Resolve(rowWidth, cells);
                AssertNoOverlap(placed, rowWidth, entry.Key + " by " + sort, overlays);
                // The side columns line up with their headers (the bands and the header rules align).
                foreach (string side in new[] { InstallModsPane.CellClient, InstallModsPane.CellServer })
                    Assert.Equal(placedHeader.Single(x => x.Key == side).Left, placed.Single(x => x.Key == side).Left, 1);
            }
        }
    }

    // ---- the runtime draws it (source pins) ----------------------------------------------------

    [Fact]
    public void The_runtime_draws_washes_edges_dividers_separators_chips_value_box_and_small_caps()
    {
        string runtime = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "UnityUiRuntime.cs");
        string band = SourcePin.MethodBody(runtime, "private void BuildBand(CellColumn column)");
        // No kit rim around a column any more; the game's top gradient and divider when borrowed.
        Assert.DoesNotContain("AddRim(", band, StringComparison.Ordinal);
        SourcePin.AssertUncommented(band, "KeybindArtDonors.TryHighlightTop(out var top, out _)");
        SourcePin.AssertUncommented(band, "KeybindArtDonors.TryRule(out var rule, out var type, out _)");
        SourcePin.AssertUncommented(band, "new Vector2(MenuPalette.ColumnEdgeWidth, 0f)");
        string paint = SourcePin.MethodBody(runtime, "private void PaintBands()");
        SourcePin.AssertUncommented(paint, "MenuPalette.ColumnLook(pair.Key, _header, ColumnGrayed(pair.Key));");

        string build = SourcePin.MethodBody(runtime, "public void Build()");
        SourcePin.AssertUncommented(build, "if (!_header && _spec.Separator && !_spec.JoinAbove)");
        SourcePin.AssertUncommented(build, "rule.color = ToColor(MenuPalette.RowSeparator)");

        string header = SourcePin.MethodBody(runtime, "private void BuildHeaderCell(Cell cell)");
        SourcePin.AssertUncommented(header, "| FontStyles.SmallCaps;");
        string write = SourcePin.MethodBody(runtime, "private void WriteText(Cell cell, float width)");
        SourcePin.AssertUncommented(write, "CellLayout.HeaderCaption(text, sorted, _sortAscending, spec.SmallCaps);");

        SourcePin.AssertUncommented(runtime, "if (cell.Spec.Skin == CellSkin.Chip && PaintChip(cell)) return;");
        string chip = SourcePin.MethodBody(runtime, "private static bool PaintChip(Cell cell)");
        SourcePin.AssertUncommented(chip, "GameArt.SettingsArtPart.PopupButton");
        SourcePin.AssertUncommented(chip, "SetPlate(cell, on ? ToColor(MenuPalette.ChipOnPlate) : (Color?)null);");
        SourcePin.AssertUncommented(chip, "ApplyPopupStates(cell);");
        SourcePin.AssertUncommented(chip, "return false;");

        SourcePin.AssertUncommented(runtime, "if (spec.Skin == CellSkin.ValueBox) PaintValueBoxField(control, field);");
        string look = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "UnityUiRuntime.SettingsLook.cs");
        string box = SourcePin.MethodBody(look, "private static void PaintValueBoxField(");
        SourcePin.AssertUncommented(box, "if (!PaintPart(box, SettingsArtPart.ValueBox, Color.white, kit)) return;");
        SourcePin.AssertUncommented(box, "ApplySpriteSwap(field, SettingsArtPart.ValueBoxHover, null);");

        SourcePin.AssertUncommented(runtime, "if (spec.Skin == CellSkin.IconWell)");

        string handles = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "UiHandles.cs");
        Assert.Contains("_sortAscending, cell.SmallCaps);", handles, StringComparison.Ordinal);
    }

    [Fact]
    public void Listing_rows_are_soft_with_separators_and_the_drawer_and_pending_bar_match()
    {
        Assert.True(InstallModsPane.ListRowsSoft);
        string pane = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "InstallModsPane.cs");
        string add = SourcePin.MethodBody(pane, "private bool AddListingRow(IUiPanel panel, CatalogEntry entry)");
        SourcePin.AssertUncommented(add, "soft: ListRowsSoft, separator: true, quietHover: true, hover: share));");
        string runtime = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "UnityUiRuntime.cs");
        string paint = SourcePin.MethodBody(runtime, "private static void PaintCellRowButton(");
        SourcePin.AssertUncommented(paint, "var hover = quietHover && !selected ? MenuPalette.RowHoverQuiet");
        Assert.True(new CellRowSpec("k", Array.Empty<CellSpec>(), quietHover: true).QuietHover);
        string drawer = SourcePin.MethodBody(pane, "private void BuildDrawer(IUiPanel panel, CatalogEntry entry)");
        SourcePin.AssertUncommented(drawer, "joinAbove: true, indent: InstallDrawerLayout.Indent, soft: ListRowsSoft));");
        SourcePin.AssertUncommented(drawer, "joinAbove: true, soft: ListRowsSoft));");
        string pending = SourcePin.MethodBody(pane, "private void BuildPendingBar(IUiPanel panel, int pending)");
        SourcePin.AssertUncommented(pending, "rightInset: HeadBlockLayout.RightColumnInset, separator: true));");
        // A joined row (the drawer) never draws a separator through the block it continues.
        Assert.False(new CellRowSpec("k", Array.Empty<CellSpec>()).Separator);
        Assert.True(new CellRowSpec("k", Array.Empty<CellSpec>(), separator: true).Separator);
    }

    private static float Wcag(PaletteColor a, PaletteColor b)
    {
        static double Lin(float c) => c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        static double L(PaletteColor c) => 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
        double la = L(a), lb = L(b);
        return (float)((Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05));
    }
}
