using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Progenitor.Install;
using Progenitor.Networking;
using Progenitor.Plugins;
using Progenitor.Ui;
using Xunit;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.Tests;

/// <summary>
/// LIST2: the Install tab as the approved list view on the LIST1 cell rows. Geometry is
/// asserted through the column model at the three row widths the mock was approved at
/// (the 1248 px detail column of the 1664 px native frame at 3840×1080, the 860 px
/// detail column of the 1600×1024 fallback frame, and 1664 px for a future full-width
/// pane); behavior is driven through the pane's own click slots on a fake cell runtime.
/// Removing the optional-drop order fails <see cref="Rows_and_header_share_columns_and_drop_author_and_released_first"/>;
/// removing the drawer rebuild after the opened row fails <see cref="Row_click_opens_one_drawer_beneath_that_row_and_a_second_click_closes_it"/>;
/// removing the baseline count fails <see cref="Count_reads_N_listings_at_the_defaults_and_filtered_shown_when_narrowed"/>;
/// removing <c>ResumePrefetch</c> from <c>Build</c> fails <see cref="Descriptions_fetch_automatically_resume_across_panes_and_skip_the_disk_cache"/>;
/// removing the viewport budget fails <see cref="Icons_are_requested_for_the_visible_rows_only_and_painted_when_they_arrive"/>.
/// </summary>
[Collection("HarmonySlotHandoff")]
public sealed class InstallListViewTests : IDisposable
{
    private const string KnownClientBuild = InstallSearchFilterTests.KnownClientBuild;

    private readonly CellRowTests.FakeCellRuntime _runtime = new();
    private readonly List<IUiPanel> _panels = new();
    private readonly object _owner = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "progenitor-list2-" + Guid.NewGuid().ToString("N"));

    public InstallListViewTests()
    {
        UiLayoutBatch.ResetForTests();
        UiApi.ResetForTests();
        UiApi.SetRuntimeForTests(_runtime);
        CatalogUpdateTags.ResetProvidersForTests();
        CatalogUpdateTags.ConfiguredTagProvider = () => null;
        CatalogUpdateTags.BuildProvider = () => KnownClientBuild;
        InstallModsPane.ProcessFilterProbe = null;
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        // INSTALLUX2 item 1: the confirmation is a modal dialog on its own panel now. Ui.ResetForTests
        // only FORGETS the open one, so without this its rows outlive the test and the next test's
        // click on prompt:<key> resolves to the dead one.
        Progenitor.Ui.UiDialog.Current?.Close();
        foreach (var panel in _panels) panel.Dispose();
        UiLayoutBatch.ResetForTests();
        CatalogUpdateTags.ResetProvidersForTests();
        InstallModsPane.ProcessFilterProbe = null;
        UiApi.ResetForTests();
        try { Directory.Delete(_root, true); } catch { }
    }

    // ---- Geometry ---------------------------------------------------------------------

    public static IEnumerable<object[]> RowWidths()
    {
        // LIST3: the Install pane is full-width. The 3840×1080 native frame is 1664 wide
        // (gen 42): its full-width row is 1624 px (the old 1248 px detail column is gone).
        yield return new object[] { MenuPanelLayout.ForFrame(new ModsWorkspaceFrame(1664f, 916f, 0f, 0f, ModsWorkspaceFrame.PathNative), sidebar: false, InstallModsPane.PaneMode).RowWidth };
        // The 1600×1024 and 3840×1080 fallback frames resolve to the same capped width, so one fixture covers both.
        yield return new object[] { MenuPanelLayout.ForFrame(ModsWorkspaceFrame.Fallback(1600f, 1024f), sidebar: false, InstallModsPane.PaneMode).RowWidth };
        yield return new object[] { 1664f };
    }

    /// <summary>The full-width row at 3840×1080 (1664 px native frame minus the pane side padding).</summary>
    internal static float WideRowWidth =>
        MenuPanelLayout.ForFrame(new ModsWorkspaceFrame(1664f, 916f, 0f, 0f, ModsWorkspaceFrame.PathNative), sidebar: false, InstallModsPane.PaneMode).RowWidth;

    [Theory]
    [MemberData(nameof(RowWidths))]
    public void Rows_and_header_share_columns_and_drop_optional_columns_by_priority(float rowWidth)
    {
        var pane = new InstallModsPane(new FakeCatalog());
        var rowSpecs = pane.RowCells(Listing("dyno-ExampleCommandFramework", "ExampleCommandFramework", description: "Command framework for V Rising mods", downloads: 1234567, categories: new[] { "Server", "Oakveil Update" }));
        var headerSpecs = pane.HeaderCells();
        Assert.Equal(InstallColumns.Keys, rowSpecs.Select(s => s.Key));
        Assert.Equal(InstallColumns.HeaderKeys, headerSpecs.Select(s => s.Key));
        var rows = MenuPanelLayout.CellColumns(rowWidth, rowSpecs);
        var header = MenuPanelLayout.CellColumns(rowWidth, headerSpecs);
        // The shortcut is drawn over the Mod column on purpose (INSTALLSIDES1); nothing else overlaps.
        AssertNoOverlap(rows.Where(c => c.Key != InstallModsPane.CellBoth).ToArray(), rowWidth);
        AssertNoOverlap(header, rowWidth);
        // Every column the header shows starts where the row's cell of the same key starts.
        foreach (var h in header.Where(c => c.Visible))
        {
            var cell = rows.Single(c => c.Key == h.Key);
            Assert.True(cell.Visible, h.Key + " is in the header but not on the row at " + rowWidth);
            Assert.True(Math.Abs(h.Left - cell.Left) < .01f, h.Key + " row " + cell + " header " + h);
        }
        bool earlierKept = false;
        foreach (string key in InstallColumns.DropPriority)
        {
            bool visible = rows.Single(c => c.Key == key).Visible;
            if (earlierKept) Assert.True(visible, key + " dropped while a column that drops before it stayed at " + rowWidth);
            earlierKept |= visible;
        }
        Assert.True(rows.Single(c => c.Key == InstallModsPane.CellName).Width >= InstallColumns.ModMinWidth - .01f);
        Assert.True(rows.Single(c => c.Key == InstallModsPane.CellIcon).Visible);
        Assert.True(rows.Single(c => c.Key == InstallModsPane.CellExpand).Visible);
        Assert.True(rows.Single(c => c.Key == InstallModsPane.CellServer).Visible);
        Assert.Equal(53f, new CellRowSpec("k", rowSpecs, twoLine: true).Height);
        if (rowWidth >= WideRowWidth - .01f)
        {
            foreach (string key in InstallColumns.DropPriority)
                Assert.True(rows.Single(c => c.Key == key).Visible, key + " dropped on a wide row");
        }
    }

    [Theory]
    [MemberData(nameof(RowWidths))]
    public void Toolbar_filter_drawer_and_target_rows_fit_without_overlap(float rowWidth)
    {
        var catalog = new FakeCatalog();
        var pane = new InstallModsPane(catalog);
        var toolbar = MenuPanelLayout.CellColumns(rowWidth, pane.ToolbarCells());
        AssertNoOverlap(toolbar, rowWidth);
        Assert.True(toolbar.Single(c => c.Key == InstallModsPane.CellSearch).Visible);

        pane.Filters.Side = InstallSideFilter.Server;
        // INSTALLFILTERS1: the filter groups on their rows; each row fits without overlap.
        var filterSpecs = pane.FilterCells();
        var filters = new List<CellPlacement>();
        foreach (var (_, rowCells) in pane.FilterRowsFor(rowWidth))
        {
            var placedRow = MenuPanelLayout.CellColumns(rowWidth, rowCells);
            AssertNoOverlap(placedRow, rowWidth);
            filters.AddRange(placedRow);
        }
        foreach (var key in new[] { InstallModsPane.CellSideAny, InstallModsPane.CellSideServer, InstallModsPane.CellSideClient, InstallModsPane.CellSideBoth, InstallModsPane.CellShowMissing, InstallModsPane.CellShowUpdate, InstallModsPane.CellNoRestart, InstallModsPane.CellCurrent, InstallModsPane.CellClear })
            Assert.True(filters.Single(c => c.Key == key).Visible, key + " hidden at " + rowWidth);
        // The chips keep their captions readable on the wide rows; the two long chips shrink on the fallback row.
        if (rowWidth >= WideRowWidth - .01f)
        {
            foreach (var spec in filterSpecs.Where(s => s.Kind == CellKind.Button))
                Assert.True(CellLayout.CaptionFits(spec, filters.Single(c => c.Key == spec.Key).Width), spec.Key + " clipped at " + rowWidth);
        }
        Assert.Equal(CellTone.ChipOn, filterSpecs.Single(s => s.Key == InstallModsPane.CellSideServer).Tone);
        Assert.Equal(CellTone.ChipOff, filterSpecs.Single(s => s.Key == InstallModsPane.CellSideAny).Tone);
        Assert.Equal("Current game update (Oakveil)", filterSpecs.Single(s => s.Key == InstallModsPane.CellCurrent).Text);
        pane.Filters.Reset();

        var drawer = MenuPanelLayout.CellColumns(rowWidth, new List<CellSpec>
        {
            new CellSpec(CellKind.Text, InstallModsPane.CellTitle).With(text: "ExampleCommandFramework", width: CellWidth.Fraction(0.34f)),
            new CellSpec(CellKind.Mono, InstallModsPane.CellVersion).With(text: "0.10.4", width: CellWidth.Px(InstallModsPane.VersionWidth)),
            new CellSpec(CellKind.Badge, InstallModsPane.CellSide).With(text: "Server", width: CellWidth.Px(InstallModsPane.SideWidth), tone: CellTone.Server),
            new CellSpec(CellKind.Spacer, InstallModsPane.CellGap),
            new CellSpec(CellKind.Button, InstallModsPane.CellAction).With(text: InstallModsCopy.InstalledPending, tone: CellTone.Pending, align: CellAlign.Right),
        });
        AssertNoOverlap(drawer, rowWidth);
        Assert.True(CellLayout.CaptionFits(
            new CellSpec(CellKind.Button, "a").With(text: InstallModsCopy.InstalledPending),
            drawer.Single(c => c.Key == InstallModsPane.CellAction).Width));

        // INSTALLUX1 item 2 removed the target chooser row this used to place (the target comes from
        // [Install] DefaultTarget now), so what is measured here is the confirmation's own No / Yes
        // pair — and item 4's inset, which is why the player's "buttons on the right are cropped"
        // cannot come back: the pair ends one scrollbar band inside the row edge.
        var prompt = MenuPanelLayout.CellColumns(rowWidth, new CellRowSpec("prompt:dyno-Bags", new List<CellSpec>
        {
            new CellSpec(CellKind.Spacer, InstallModsPane.CellGap),
            new CellSpec(CellKind.Button, InstallModsPane.CellPromptNo).With(
                text: InstallModsCopy.PromptNo, align: CellAlign.Right,
                width: CellWidth.Px(CellLayout.ButtonWidth), minWidth: CellLayout.ButtonWidth),
            new CellSpec(CellKind.Button, InstallModsPane.CellPromptYes).With(
                text: InstallModsCopy.PromptYesInstall, align: CellAlign.Right,
                width: CellWidth.Px(CellLayout.ButtonWidth), minWidth: CellLayout.ButtonWidth),
        }, rightInset: HeadBlockLayout.RightColumnInset));
        AssertNoOverlap(prompt, rowWidth);
        float promptRight = rowWidth / 2f - HeadBlockLayout.RightColumnInset;
        Assert.Equal(promptRight, prompt.Single(c => c.Key == InstallModsPane.CellPromptYes).Right, 1);
        Assert.DoesNotContain(prompt, c => c.Key == "confirm");
        // Revert: dropping the rightInset from the prompt row puts Yes back on the clip boundary
        // (promptRight grows by RightColumnInset) and fails this.
    }

    private static void AssertNoOverlap(CellPlacement[] placed, float rowWidth)
    {
        foreach (var p in placed.Where(p => p.Visible))
        {
            Assert.True(p.Left >= -rowWidth / 2f - .01f, p + " starts left of the row");
            Assert.True(p.Right <= rowWidth / 2f + .01f, p + " ends right of the row");
        }
        for (int i = 0; i < placed.Length; i++)
            for (int j = i + 1; j < placed.Length; j++)
                Assert.False(CellLayout.Overlap(placed[i], placed[j]), placed[i] + " overlaps " + placed[j]);
    }

    // ---- Build shape ------------------------------------------------------------------

    [Fact]
    public void Build_is_search_toolbar_chips_header_then_one_two_line_row_per_listing_and_the_footer()
    {
        var catalog = new FakeCatalog();
        for (int i = 0; i < 24; i++)
            catalog.Entries.Add(Listing("o-p" + i, "Pack " + i, downloads: 1000 - i, categories: new[] { "Oakveil Update" }));
        var rig = new Rig(this, catalog);
        var pane = rig.Pane;
        // LIST3: the search is the toolbar's first cell (an input cell), not a kit input row.
        Assert.NotNull(pane.SearchCell);
        Assert.Equal(InstallModsCopy.SearchPlaceholder, pane.SearchCell!.Placeholder);
        Assert.Same(pane.SearchCell, pane.ToolbarRow!.Cells[0]);
        Assert.Equal(InstallModsPane.ToolbarKey, pane.ToolbarRow!.Key);
        Assert.Equal(InstallModsPane.FiltersKey, pane.FilterRow!.Key);
        Assert.Equal(InstallModsPane.HeaderKey, pane.HeaderRow!.Key);
        // POLISH2 item 1: the header's sorted marker sits on Released at the defaults.
        Assert.Equal(InstallModsPane.SortKeyReleased, pane.HeaderRow.SortedKey);
        Assert.Equal(24, pane.Rows.Count);
        Assert.All(pane.Rows, r => Assert.True(r.Row.Enabled));
        Assert.Equal(24, pane.Rows.Select(r => r.Row.Key).Distinct(StringComparer.Ordinal).Count());
        // The fake runtime saw the rows in panel order: toolbar, the two filter rows (INSTALLFILTERS1:
        // Side + Show, then Only + the summary + Clear), header, then 24 two-line rows.
        var hosted = _runtime.Rows;
        Assert.Equal(28, hosted.Count);
        Assert.False(hosted[0].TwoLine);
        Assert.False(hosted[1].TwoLine);
        Assert.False(hosted[2].TwoLine);
        Assert.True(hosted[3].Header);
        Assert.All(hosted.Skip(4), r => Assert.True(r.TwoLine));
        Assert.Null(pane.DrawerHeadRow);
        Assert.Empty(pane.DrawerHandles);
        Assert.Empty(pane.PendingBarHandles);
        Assert.Equal((24, 0, 24), pane.MatchCount);
        Assert.Equal("24 listings", pane.CountText());
        // Sorted by downloads: Pack 0 (1000) first.
        Assert.Equal("o-p0", pane.Rows[0].Row.Key);
        Assert.Equal("o-p23", pane.Rows[23].Row.Key);
        string src = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "InstallModsPane.cs");
        string build = SourcePin.MethodBody(src, "public void Build(");
        SourcePin.AssertUncommented(build, "Ui.PinMenuRows(panel, MenuRowPin.Top)");
        // INSTALLUX1 item 4: the header carries the same right inset as the rows under it.
        SourcePin.AssertUncommented(build, "panel.CellHeader(HeaderCells(), HeaderKey, HeadBlockLayout.RightColumnInset)");
        SourcePin.AssertUncommented(SourcePin.MethodBody(src, "private bool AddListingRow("), "twoLine: true");
        SourcePin.AssertUncommented(build, "ResumePrefetch()");
        SourcePin.AssertUncommented(build, "RequestVisibleIcons()");
        SourcePin.AssertNoStatementContaining(build, "sidebar: true");
        // Revert: a sidebar build, a single-line row, or a header without the sorted mark fails this.
    }

    /// <summary>
    /// The tab switch built every listing's row (~30 Unity objects each) before the first frame;
    /// live that was most of a 0.6 s switch over 300 listings. A deferred build draws the first
    /// screenful and the drawer's row, then the rest a frame at a time, and ends with the same
    /// rows in the same order a whole build makes. The count never reads short in between.
    /// What this would catch: a deferred build that loses, duplicates or reorders rows, a count
    /// that shows only the rows built so far, or a fill that keeps adding to a replaced build.
    /// </summary>
    [Fact]
    public void A_deferred_build_draws_a_screenful_then_fills_the_rest_in_order()
    {
        var catalog = new FakeCatalog();
        for (int i = 0; i < 60; i++)
            catalog.Entries.Add(Listing("o-p" + i, "Pack " + i, downloads: 1000 - i, categories: new[] { "Oakveil Update" }));

        var whole = new InstallModsPane(catalog);
        whole.Build(NewPanel(), true, false, null, () => { }, () => { }, DateTime.UtcNow);
        var order = whole.Rows.Select(r => r.Row.Key).ToArray();
        Assert.Equal(60, order.Length);

        var posted = new System.Collections.Concurrent.ConcurrentQueue<Action>();
        var pane = new InstallModsPane(catalog, null, posted.Enqueue) { DeferRowFill = true };
        pane.SetViewportRows(5);
        pane.Build(NewPanel(), true, false, null, () => { }, () => { }, DateTime.UtcNow);
        Assert.Equal(5 + InstallModsPane.IconLookahead, pane.Rows.Count);
        Assert.True(pane.FillPending);
        Assert.Equal("60 listings", pane.CountText());

        Drain(posted);
        Assert.False(pane.FillPending);
        Assert.Equal(order, pane.Rows.Select(r => r.Row.Key).ToArray());
        Assert.Equal("60 listings", pane.CountText());

        // The opened listing's row (and its drawer) is always in the first frame, however deep.
        pane.SelectListing(order[40]);
        pane.Build(NewPanel(), true, false, null, () => { }, () => { }, DateTime.UtcNow);
        Assert.Equal(41, pane.Rows.Count);
        Assert.NotNull(pane.DrawerHeadRow);

        // A new build replaces the list: the old fill's frames add nothing to it.
        var stale = posted.ToArray();
        pane.SelectListing(null);
        pane.Build(NewPanel(), true, false, null, () => { }, () => { }, DateTime.UtcNow);
        int fresh = pane.Rows.Count;
        foreach (var frame in stale) frame();
        Assert.Equal(fresh, pane.Rows.Count);
        Drain(posted);
        Assert.Equal(order, pane.Rows.Select(r => r.Row.Key).ToArray());
    }

    [Fact]
    public void Only_the_expand_button_opens_one_drawer_beneath_that_row_and_closes_it()
    {
        var catalog = new FakeCatalog();
        for (int i = 0; i < 24; i++)
            catalog.Entries.Add(Listing("o-p" + i, "Pack " + i, description: "one-liner " + i, downloads: 1000 - i, categories: new[] { "Server", "Oakveil Update" }));
        var rig = new Rig(this, catalog);
        var pane = rig.Pane;

        // INSTALLSIDES1: a click on the row itself, or on any of its buttons, opens nothing.
        UiCellRowHandle.TryInvoke("o-p5", out _);
        Assert.Null(pane.SelectedKey);
        Assert.Equal(1, rig.Rebuilds);

        rig.ClickRow("o-p5");
        Assert.Equal("o-p5", pane.SelectedKey);
        Assert.Equal(2, rig.Rebuilds);
        // The drawer is two rows right after row 5 in panel order: its head, then its scrolling body.
        var hosted = _runtime.Rows.Where(r => r.Alive).ToList();
        int rowAt = hosted.FindIndex(r => r.Text == "o-p5");
        Assert.True(rowAt > 2);
        Assert.Equal(InstallModsPane.DrawerKeyPrefix + "o-p5", hosted[rowAt + 1].Text);
        Assert.Equal(InstallModsPane.DrawerBodyKeyPrefix + "o-p5", hosted[rowAt + 2].Text);
        Assert.Equal("o-p6", hosted[rowAt + 3].Text);
        Assert.True(pane.Rows.Single(r => r.Row.Key == "o-p5").Row.Expanded);
        Assert.True(pane.Rows.Single(r => r.Row.Key == "o-p5").Row.Selected);
        Assert.Single(pane.Rows.Where(r => r.Row.Expanded));
        // The expand button turns: its glyph (the fallback face) says open.
        Assert.Equal(InstallModsCopy.GlyphCollapse, pane.Rows.Single(r => r.Row.Key == "o-p5").Row.Cells.Single(c => c.Key == InstallModsPane.CellExpand).Text);
        Assert.Equal(CellSkin.Expander, pane.Rows.Single(r => r.Row.Key == "o-p5").Row.Cells.Single(c => c.Key == InstallModsPane.CellExpand).Skin);

        var head = pane.DrawerHeadRow!;
        var body = pane.DrawerBodyRow!;
        Assert.Equal(SideCells.PrivateGamesLabel + ": " + SideCells.LoadsWithClient, head.Cells.Single(c => c.Key == InstallModsPane.CellNote).Text);
        Assert.Equal(InstallModsCopy.Collapse, head.Cells.Single(c => c.Key == InstallModsPane.CellCollapse).Text);
        Assert.True(head.Selected);
        Assert.True(body.Selected);
        Assert.DoesNotContain(head.Cells, c => c.Key == InstallModsPane.CellTitle);
        Assert.DoesNotContain(head.Cells, c => c.Key == InstallModsPane.CellVersion);
        var texts = DrawerTexts(pane);
        // The body: the one-liner (the row's second line is the author now), the facts, the README.
        Assert.Contains("one-liner 5", texts);
        Assert.Contains(InstallModsCopy.FactLatest + ": 1.0.0", texts);
        Assert.Contains(InstallModsCopy.FactDownloads + ": 995", texts);
        Assert.Contains(InstallModsCopy.FactSide + ": " + InstallModsCopy.SideFact(PackageSide.Server, fromTags: true), texts);
        Assert.Contains(InstallModsCopy.FactInstalledHere + ": " + InstallModsCopy.NotInstalledHere, texts);
        Assert.Contains(InstallModsCopy.LoadingDescription, texts);
        Assert.DoesNotContain(InstallModsCopy.ConfigureInMods, texts);
        Assert.DoesNotContain(texts, t => t.Contains("sha256", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("Pack 5\nby o", pane.Rows.Single(r => r.Row.Key == "o-p5").Row.Cells.Single(c => c.Key == InstallModsPane.CellName).Text);

        // Opening another row moves the drawer; the expand button of the open row closes it, and so
        // does the drawer's own Collapse.
        rig.ClickRow("o-p7");
        Assert.Equal("o-p7", pane.SelectedKey);
        Assert.Single(_runtime.Rows.Where(r => r.Alive && r.Text.StartsWith(InstallModsPane.DrawerKeyPrefix, StringComparison.Ordinal)));
        rig.ClickRow("o-p7");
        Assert.Null(pane.SelectedKey);
        Assert.Null(pane.DrawerHeadRow);
        Assert.Empty(_runtime.Rows.Where(r => r.Alive && r.Text.StartsWith(InstallModsPane.DrawerKeyPrefix, StringComparison.Ordinal)));
        Assert.All(pane.Rows, r => Assert.False(r.Row.Expanded));
        rig.ClickRow("o-p2");
        rig.ClickCell(InstallModsPane.DrawerKeyPrefix + "o-p2", InstallModsPane.CellCollapse);
        Assert.Null(pane.SelectedKey);
    }

    [Fact]
    public void Count_reads_N_listings_at_the_defaults_and_filtered_shown_when_narrowed()
    {
        var catalog = new FakeCatalog();
        catalog.Entries.Add(Listing("a-server", "Server Tools", categories: new[] { "Server", "Oakveil Update" }, description: "admin commands"));
        catalog.Entries.Add(Listing("a-client", "Client HUD", categories: new[] { "Client", "Oakveil Update" }, description: "hud commands"));
        catalog.Entries.Add(Listing("a-both", "Both Ways", categories: new[] { "Client", "Server", "Oakveil Update" }));
        catalog.Entries.Add(Listing("a-old", "Old Server", categories: new[] { "Server", "Gloomrot Update" }, description: "admin commands"));
        catalog.Entries.Add(Listing("a-tool", "Launcher", categories: new[] { "Tools" }));
        catalog.Entries.Add(Verified("example.hot", "Hot Mod", hotLoadable: true, categories: new[] { "Client", "Oakveil Update" }));
        var rig = new Rig(this, catalog);
        var pane = rig.Pane;
        Assert.True(pane.AtDefaults);
        Assert.Equal("4 listings", pane.CountText());
        Assert.Equal("4 listings", pane.ToolbarRow!.Cells.Single(c => c.Key == InstallModsPane.CellCount).Text);
        Assert.False(pane.FilterRows.SelectMany(r => r.Cells).Single(c => c.Key == InstallModsPane.CellClear).Visible);

        rig.ClickCell(InstallModsPane.FiltersKey, InstallModsPane.CellSideServer);
        Assert.Equal(InstallSideFilter.Server, pane.Filters.Side);
        Assert.False(pane.AtDefaults);
        Assert.Equal("3 listings filtered · 1 shown", pane.CountText());
        Assert.Equal((1, 3, 6), pane.MatchCount);
        Assert.True(pane.FilterRows.SelectMany(r => r.Cells).Single(c => c.Key == InstallModsPane.CellClear).Visible);
        Assert.Equal(CellTone.ChipOn, pane.FilterRow!.Cells.Single(c => c.Key == InstallModsPane.CellSideServer).Tone);
        Assert.Equal(CellTone.ChipOff, pane.FilterRow!.Cells.Single(c => c.Key == InstallModsPane.CellSideAny).Tone);
        Assert.Equal(1, rig.Rebuilds);

        // Widening beyond the defaults (current update off) shows more than the baseline: no "filtered" wording.
        rig.ClickCell(InstallModsPane.FiltersKey, InstallModsPane.CellSideAny);
        rig.ClickCell(InstallModsPane.FiltersOnlyKey, InstallModsPane.CellCurrent);
        Assert.False(pane.Filters.CurrentUpdateOnly);
        Assert.Equal("6 listings", pane.CountText());
        Assert.True(pane.FilterRows.SelectMany(r => r.Cells).Single(c => c.Key == InstallModsPane.CellClear).Visible);
        Assert.True(pane.Rows.Single(r => r.Row.Key == "a-tool").Row.Visible);

        // The no-restart chip keeps only the declared hot-loadable package.
        rig.ClickCell(InstallModsPane.FiltersOnlyKey, InstallModsPane.CellNoRestart);
        Assert.Equal(new[] { "Hot Mod" }, pane.VisibleEntries().Select(e => e.DisplayName).ToArray());
        Assert.Equal("3 listings filtered · 1 shown", pane.CountText());

        // Search narrows too and Clear filters restores no search / any side / the defaults.
        pane.Search = "commands";
        rig.ClickCell(InstallModsPane.FiltersOnlyKey, InstallModsPane.CellNoRestart);
        Assert.Equal(new[] { "Server Tools", "Client HUD", "Old Server" }, pane.VisibleEntries().Select(e => e.DisplayName).ToArray());
        Assert.Equal("1 listing filtered · 3 shown", pane.CountText());
        rig.ClickCell(InstallModsPane.FiltersOnlyKey, InstallModsPane.CellClear);
        Assert.True(pane.AtDefaults);
        Assert.Equal("", pane.Search);
        Assert.Equal("", pane.SearchCell!.Text);
        Assert.Equal(InstallSideFilter.Any, pane.Filters.Side);
        Assert.True(pane.Filters.CurrentUpdateOnly);
        Assert.False(pane.Filters.NoRestartOnly);
        Assert.Equal("4 listings", pane.CountText());
        Assert.False(pane.FilterRows.SelectMany(r => r.Cells).Single(c => c.Key == InstallModsPane.CellClear).Visible);
        Assert.Equal(1, rig.Rebuilds);
        Assert.Equal("1 listing filtered · 3 shown", InstallModsCopy.CountText(3, 1, atDefaults: false));
        Assert.Equal("3 listings", InstallModsCopy.CountText(3, 0, atDefaults: false));
        // Revert: counting filtered against the total instead of the defaults baseline, or a Clear that keeps the search, fails this.
    }

    [Fact]
    public void Header_click_sorts_by_the_column_and_a_second_click_reverses_it()
    {
        var catalog = new FakeCatalog();
        catalog.Entries.Add(Listing("o-b", "Beta", downloads: 10, releasedUtc: "2026-01-01T00:00:00Z", categories: new[] { "Oakveil Update" }));
        catalog.Entries.Add(Listing("o-a", "Alpha", downloads: 30, releasedUtc: "2026-03-01T00:00:00Z", categories: new[] { "Oakveil Update" }));
        catalog.Entries.Add(Listing("o-c", "Gamma", downloads: null, releasedUtc: null, categories: new[] { "Oakveil Update" }));
        catalog.Entries.Add(Listing("o-d", "Delta", downloads: 20, releasedUtc: "2026-02-01T00:00:00Z", categories: new[] { "Oakveil Update" }));
        var rig = new Rig(this, catalog);
        var pane = rig.Pane;
        // POLISH2 item 1: a fresh pane is sorted by Recently released — newest first (▼), the
        // undated Gamma last — and the header's Released column carries the mark.
        Assert.Equal(new[] { "Alpha", "Delta", "Beta", "Gamma" }, Names(pane));
        Assert.Equal(InstallSort.Released, pane.Sort);
        Assert.Equal(InstallModsPane.SortKeyReleased, pane.HeaderRow!.SortedKey);
        Assert.False(pane.HeaderRow!.SortAscending);
        // INSTALLCOLS1: every column is present in every sort — nothing appears or disappears.
        var keys = pane.Rows[0].Row.Cells.Select(c => c.Key).ToArray();

        rig.ClickCell(InstallModsPane.HeaderKey, InstallModsPane.CellName);
        Assert.Equal(InstallSort.Name, pane.Sort);
        Assert.Equal(2, rig.Rebuilds);
        Assert.Equal(new[] { "Alpha", "Beta", "Delta", "Gamma" }, Names(pane));
        Assert.Equal(InstallModsPane.SortKeyName, pane.HeaderRow!.SortedKey);
        Assert.True(pane.HeaderRow!.SortAscending, "Mod sorts A to Z first (▲)");
        Assert.Equal(keys, pane.Rows[0].Row.Cells.Select(c => c.Key).ToArray());

        // A second click on the sorted header reverses it.
        rig.ClickCell(InstallModsPane.HeaderKey, InstallModsPane.CellName);
        Assert.Equal(InstallSort.Name, pane.Sort);
        Assert.Equal(new[] { "Gamma", "Delta", "Beta", "Alpha" }, Names(pane));
        Assert.False(pane.HeaderRow!.SortAscending);

        rig.ClickCell(InstallModsPane.HeaderKey, InstallModsPane.CellReleased);
        Assert.Equal(InstallSort.Released, pane.Sort);
        Assert.Equal(new[] { "Alpha", "Delta", "Beta", "Gamma" }, Names(pane));
        Assert.Equal(InstallModsPane.SortKeyReleased, pane.HeaderRow!.SortedKey);
        Assert.False(pane.HeaderRow!.SortAscending);
        // Reversed: oldest first, and the undated listing stays last.
        rig.ClickCell(InstallModsPane.HeaderKey, InstallModsPane.CellReleased);
        Assert.Equal(new[] { "Beta", "Delta", "Alpha", "Gamma" }, Names(pane));
        Assert.True(pane.HeaderRow!.SortAscending);

        rig.ClickCell(InstallModsPane.HeaderKey, InstallModsPane.CellDownloads);
        Assert.Equal(InstallSort.Downloads, pane.Sort);
        Assert.False(pane.Filters.SortReversed, "a new column starts in its natural direction");
        Assert.Equal(new[] { "Alpha", "Delta", "Beta", "Gamma" }, Names(pane));
        rig.ClickCell(InstallModsPane.HeaderKey, InstallModsPane.CellDownloads);
        Assert.Equal(new[] { "Beta", "Delta", "Alpha", "Gamma" }, Names(pane));
        Assert.Equal(keys, pane.Rows[0].Row.Cells.Select(c => c.Key).ToArray());
        // Side and Version are not sort controls.
        Assert.False(UiCellRowHandle.TryInvoke(InstallModsPane.HeaderKey + "/" + InstallModsPane.CellSide, out _));

        // The sort and its direction survive a pane on the same catalog; Clear filters leaves them alone.
        pane.SetSort(InstallSort.Name, reversed: true);
        var again = new InstallModsPane(catalog);
        Assert.Equal(InstallSort.Name, again.Sort);
        Assert.True(again.Filters.SortReversed);
        again.ClearFilters();
        Assert.Equal(InstallSort.Name, again.Sort);
        Assert.True(again.Filters.SortReversed);
        // Revert: sorting without the rebuild, a missing-last rule that flips with the direction, or a header click that does not reverse fails this.

        static string[] Names(InstallModsPane p) => p.Rows.Select(r => r.Entry.DisplayName ?? "").ToArray();
    }

    // ---- Descriptions and icons --------------------------------------------------------

    /// <summary>
    /// DESCCACHE1: the first build ran before the index had loaded, which used to mark the disk
    /// cache "adopted" for good, so every listing that arrived later was downloaded again on every
    /// game start although its README was on disk (live 2026-09-29: "descriptions 49/299").
    /// </summary>
    [Fact]
    public void Descriptions_for_listings_that_arrive_after_the_first_build_come_from_disk_not_the_network()
    {
        var catalog = new FakeCatalog();
        var disk = new ListingDiskCache(_root);
        var b = Listing("a-b", "Beta", description: "x", categories: new[] { "Oakveil Update" });
        Assert.True(disk.WriteReadme("a", "b", "1.0.0", InstallModsPane.ReadmeStamp(b), "beta from disk with unicorns"));
        catalog.Documents[b.Key] = Docs(b.Key, "beta from the network");
        var clock = new StepClock();
        var posted = new System.Collections.Concurrent.ConcurrentQueue<Action>();
        var pane = new InstallModsPane(catalog, clock, posted.Enqueue);
        pane.SetDiskCache(disk);
        var panel = NewPanel();

        // The first build sees an empty index.
        pane.Build(panel, true, false, null, () => { }, () => { }, DateTime.UtcNow);
        Assert.Equal(0, catalog.DocumentLoadCalls);

        // The index arrives; the next prefetch pass reads Beta from disk and queues nothing.
        catalog.Entries.Add(b);
        Assert.Equal(0, pane.ResumePrefetch());
        Assert.Equal(0, catalog.DocumentLoadCalls);
        Assert.Equal("withReadme", pane.SearchScope(b));
        var progress = pane.DescriptionsProgress();
        Assert.Equal(1, progress.Done);
        Assert.Equal(1, progress.Total);
        Assert.NotEqual("running", progress.State);
        pane.Search = "unicorns";
        Assert.Equal(new[] { "Beta" }, pane.VisibleEntries().Select(e => e.DisplayName).ToArray());
        panel.Dispose();
    }

    [Fact]
    public void Descriptions_fetch_automatically_resume_across_panes_and_skip_the_disk_cache()
    {
        var catalog = new FakeCatalog();
        var a = Listing("a-a", "Alpha", description: "x", categories: new[] { "Oakveil Update" });
        var b = Listing("a-b", "Beta", description: "x", categories: new[] { "Oakveil Update" });
        var c = Listing("a-c", "Gamma", description: "x", categories: new[] { "Oakveil Update" });
        catalog.Entries.AddRange(new[] { a, b, c });
        catalog.Documents[a.Key] = Docs(a.Key, "alpha readme with dragons");
        catalog.Documents[b.Key] = Docs(b.Key, "beta readme");
        catalog.Documents[c.Key] = Docs(c.Key, "gamma readme with dragons");
        var disk = new ListingDiskCache(_root);
        // Beta's README is already on disk for this version and stamp: it is adopted, never fetched.
        Assert.True(disk.WriteReadme("a", "b", "1.0.0", InstallModsPane.ReadmeStamp(b), "beta from disk with unicorns"));
        var clock = new StepClock();
        catalog.Gate = new StepGate();
        var posted = new System.Collections.Concurrent.ConcurrentQueue<Action>();
        var pane = new InstallModsPane(catalog, clock, posted.Enqueue);
        pane.SetDiskCache(disk);
        var panel = NewPanel();
        pane.Build(panel, true, false, null, () => { }, () => { }, DateTime.UtcNow);

        // The run started on Build for the two missing READMEs, one in flight.
        Assert.True(pane.Prefetch.Running);
        Assert.Equal("withReadme", pane.SearchScope(b));
        Assert.Equal("oneLiner", pane.SearchScope(a));
        Assert.Equal(1, catalog.DocumentLoadCalls);
        Assert.Equal("descriptions 1/3", pane.DescriptionsText());
        // DESCCACHE1: the progress is readback only; the toolbar has no descriptions cell.
        Assert.DoesNotContain(pane.ToolbarRow!.Cells, x => x.Key == InstallModsPane.CellDescriptions);
        pane.Search = "unicorns";
        Assert.Equal(new[] { "Beta" }, pane.VisibleEntries().Select(e => e.DisplayName).ToArray());

        catalog.Gate.Release();
        Settle(() => clock.Delays.Count == 1);
        Drain(posted);
        Assert.Equal(ReadmePrefetchScheduler.MinSpacing, clock.Delays[0]);
        Assert.Equal("withReadme", pane.SearchScope(a));
        Assert.Equal("descriptions 2/3", pane.DescriptionsText());
        Assert.True(disk.TryReadReadme("a", "a", "1.0.0", InstallModsPane.ReadmeStamp(a), out string written));
        Assert.Contains("dragons", written, StringComparison.Ordinal);
        pane.Search = "dragons";
        Assert.Equal(new[] { "Alpha" }, pane.VisibleEntries().Select(e => e.DisplayName).ToArray());

        // Pane teardown cancels after the request in flight; a new pane on the same catalog
        // resumes with what is still missing (Gamma only) — nothing fetched twice.
        pane.CancelPrefetchDescriptions();
        Assert.Equal(ReadmePrefetchState.Cancelled, pane.Prefetch.State);
        Assert.Equal(1, catalog.DocumentLoadCalls);
        panel.Dispose();
        clock.Release();
        var resumed = new InstallModsPane(catalog, clock, posted.Enqueue);
        Assert.Equal(new[] { c.Key }, resumed.PendingReadmeKeys());
        resumed.Build(NewPanel(), true, false, null, () => { }, () => { }, DateTime.UtcNow);
        Assert.True(resumed.Prefetch.Running);
        Settle(() => catalog.DocumentLoadCalls == 2);
        catalog.Gate.Release();
        Settle(() => resumed.Prefetch.State == ReadmePrefetchState.Done);
        Drain(posted);
        Assert.Equal("", resumed.DescriptionsText());
        Assert.Equal(("done", 3, 3), resumed.DescriptionsProgress());
        resumed.Search = "dragons";
        Assert.Equal(new[] { "Alpha", "Gamma" }, resumed.VisibleEntries().Select(e => e.DisplayName).ToArray());
        Assert.Equal(2, catalog.DocumentLoadCalls);

        // A newer listing (a different release stamp) misses the cache and is fetched again.
        var newer = Listing("a-b", "Beta", description: "x", categories: new[] { "Oakveil Update" }, releasedUtc: "2026-10-01T00:00:00Z");
        Assert.False(disk.TryReadReadme("a", "b", "1.0.0", InstallModsPane.ReadmeStamp(newer), out _));
        var fresh = new FakeCatalog();
        fresh.Entries.Add(newer);
        var freshPane = new InstallModsPane(fresh);
        freshPane.SetDiskCache(disk);
        Assert.Equal(new[] { "a-b" }, freshPane.PendingReadmeKeys());
        Assert.Equal(1, freshPane.ResumePrefetch());
        Assert.True(freshPane.Prefetch.State == ReadmePrefetchState.Done || freshPane.Prefetch.State == ReadmePrefetchState.Running);

        string src = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "InstallModsPane.cs");
        Assert.DoesNotContain("Fetch descriptions", src, StringComparison.Ordinal);
        SourcePin.AssertUncommented(SourcePin.MethodBody(src, "public void NoteReadmeForSearch"), "WriteReadme(");
        // Revert: a manual fetch button, re-requesting cached versions, or resuming with the whole index fails this.
    }

    /// <summary>
    /// TSTRAFFIC1: a build used to queue the README of every listing in the index (299 live), one every
    /// 250 ms, on every Install open. Now it queues only the rows on screen — at most
    /// <see cref="InstallModsPane.ReadmePrefetchBudget"/> — and a rebuild with nothing new on screen queues nothing.
    /// Reverting <c>PendingReadmeKeys</c> to the whole index fails the counts below.
    /// </summary>
    [Fact]
    public void Build_prefetches_readmes_for_the_rows_on_screen_only_never_the_whole_index()
    {
        var catalog = new FakeCatalog();
        for (int i = 0; i < 40; i++)
        {
            var e = Listing("o-p" + i, "Pack " + i, description: "x", categories: new[] { "Oakveil Update" }, downloads: 1000 - i);
            catalog.Entries.Add(e);
            catalog.Documents[e.Key] = Docs(e.Key, "readme " + i);
        }

        var posted = new System.Collections.Concurrent.ConcurrentQueue<Action>();
        var pane = new InstallModsPane(catalog, new ImmediateClock(), posted.Enqueue);
        pane.SetViewportRows(6);
        Assert.Equal(6, pane.ReadmePrefetchBudget);
        pane.Build(NewPanel(), true, false, null, () => { }, () => { }, DateTime.UtcNow);
        Settle(() => pane.Prefetch.State == ReadmePrefetchState.Done && catalog.DocumentLoadCalls == 6);
        Drain(posted);
        Assert.Equal(6, catalog.DocumentLoadCalls);
        Assert.Equal(6, pane.Prefetch.Total);
        var visible = pane.VisibleEntries();
        for (int i = 0; i < 6; i++) Assert.Equal("withReadme", pane.SearchScope(visible[i]));
        Assert.Equal("oneLiner", pane.SearchScope(visible[6]));

        // A rebuild with the same rows on screen asks for nothing.
        pane.Build(NewPanel(), true, false, null, () => { }, () => { }, DateTime.UtcNow);
        Settle(() => !pane.Prefetch.Running);
        Assert.Equal(6, catalog.DocumentLoadCalls);

        // Scrolled to row 20: the next build asks for rows 20..25 only.
        float pitch = CellLayout.TwoLineRowHeight + PanelLayout.RowGap;
        pane.ScrollOffsetProbe = () => 20 * pitch + 1f;
        Assert.Equal(visible.Skip(20).Take(6).Select(e => e.Key).ToArray(), pane.PendingReadmeKeys().ToArray());
        pane.Build(NewPanel(), true, false, null, () => { }, () => { }, DateTime.UtcNow);
        Settle(() => pane.Prefetch.State == ReadmePrefetchState.Done && catalog.DocumentLoadCalls == 12);
        Drain(posted);
        Assert.Equal(12, catalog.DocumentLoadCalls);

        // A tall viewport is still capped.
        pane.SetViewportRows(50);
        Assert.Equal(InstallModsPane.MaxReadmePrefetchPerBuild, pane.ReadmePrefetchBudget);
        Assert.True(pane.PendingReadmeKeys().Count <= InstallModsPane.MaxReadmePrefetchPerBuild);

        string src = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "InstallModsPane.cs");
        string pending = SourcePin.MethodBody(src, "public List<string> PendingReadmeKeys()");
        SourcePin.AssertUncommented(pending, "ReadmePrefetchBudget");
        SourcePin.AssertNoStatementContaining(pending, "AllEntries()");
    }

    [Fact]
    public void Disk_cache_paths_and_stamps_are_as_documented()
    {
        var disk = new ListingDiskCache(_root);
        Assert.Equal(Path.Combine(_root, "progenitor-cache", "readme", "dyno", "ExampleCommandFramework", "0.10.4.txt"), disk.ReadmePath("dyno", "ExampleCommandFramework", "0.10.4"));
        string url = "https://gcdn.thunderstore.io/live/repository/icons/dyno-ExampleCommandFramework-0.10.4.png";
        string file = ListingDiskCache.IconFileName(url);
        Assert.Equal(64 + 4, file.Length);
        Assert.EndsWith(".png", file, StringComparison.Ordinal);
        Assert.Equal(Path.Combine(_root, "progenitor-cache", "icons", file), disk.IconPath(url));
        Assert.True(disk.WriteReadme("dyno", "ExampleCommandFramework", "0.10.4", "2026-08-30T00:00:00Z", "plain\ntext"));
        Assert.Equal("# released 2026-08-30T00:00:00Z\nplain\ntext", File.ReadAllText(disk.ReadmePath("dyno", "ExampleCommandFramework", "0.10.4")!));
        Assert.True(disk.TryReadReadme("dyno", "ExampleCommandFramework", "0.10.4", "2026-08-30T00:00:00Z", out string text));
        Assert.Equal("plain\ntext", text);
        Assert.False(disk.TryReadReadme("dyno", "ExampleCommandFramework", "0.10.4", "2026-09-30T00:00:00Z", out _));
        Assert.False(disk.TryReadReadme("dyno", "ExampleCommandFramework", "0.10.5", "2026-08-30T00:00:00Z", out _));
        Assert.True(disk.WriteIcon(url, new byte[] { 1, 2, 3 }));
        Assert.True(disk.TryReadIcon(url, out var bytes));
        Assert.Equal(new byte[] { 1, 2, 3 }, bytes);
        Assert.Equal("a_b", ListingDiskCache.Segment("a/b"));
        Assert.Equal("_", ListingDiskCache.Segment(".."));
        var off = new ListingDiskCache(null);
        Assert.False(off.Enabled);
        Assert.Null(off.ReadmePath("a", "b", "c"));
        Assert.False(off.WriteReadme("a", "b", "c", "s", "t"));
        Assert.True(ThunderstoreAdapter.TryBuildIconUrl(Listing("dyno-ExampleCommandFramework", "ECF", version: "0.10.4"), out string built));
        Assert.Equal(url, built);
        // The icon fetch answers from disk before the network and writes a network hit back.
        int network = 0;
        var fetch = disk.WrapIconFetch((u, ct) => { network++; return Task.FromResult(new CatalogBytesFetchResult(true, null, new byte[] { 9 }, 1, 200)); });
        Assert.Equal(new byte[] { 1, 2, 3 }, fetch(url, CancellationToken.None).Result.Bytes);
        Assert.Equal(0, network);
        Assert.Equal(new byte[] { 9 }, fetch("https://x/other.png", CancellationToken.None).Result.Bytes);
        Assert.Equal(1, network);
        Assert.True(disk.TryReadIcon("https://x/other.png", out _));
        // Revert: a per-package (not per-version) README file, an unhashed icon name, or ignoring the stamp fails this.
    }

    [Fact]
    public void Icons_are_requested_for_the_visible_rows_only_and_painted_when_they_arrive()
    {
        var catalog = new FakeCatalog();
        for (int i = 0; i < 40; i++)
            catalog.Entries.Add(Listing("o-p" + i, "Pack " + i, downloads: 1000 - i, categories: new[] { "Oakveil Update" }, version: "1.0." + i));
        var decoder = new FakeDecoder();
        var fetch = new FakeIconFetch();
        var icons = new IconCache(fetch.FetchAsync, decoder, new ImmediateClock());
        var pane = new InstallModsPane(catalog, new NeverClock(), null);
        pane.SetIconCache(icons);
        pane.SetViewportRows(6);
        var panel = NewPanel();
        pane.Build(panel, true, false, null, () => { }, () => { }, DateTime.UtcNow);
        // 6 viewport rows + 10 lookahead: 16 requests, in row order, none for row 16+.
        Assert.Equal(16, pane.RequestedIconUrls.Count);
        Assert.Contains(InstallModsPane.IconUrlFor(catalog.Entries[0])!, pane.RequestedIconUrls);
        Assert.Contains(InstallModsPane.IconUrlFor(catalog.Entries[15])!, pane.RequestedIconUrls);
        Assert.DoesNotContain(InstallModsPane.IconUrlFor(catalog.Entries[16])!, pane.RequestedIconUrls);
        // The cache keeps one fetch in flight; the other fifteen wait their turn.
        Settle(() => fetch.Started == 1);
        Assert.True(icons.InFlight);
        Assert.Equal(15, icons.QueuedCount);
        // Icons that arrive are painted on their rows through Arrived; the placeholder stands until then.
        var fake = _runtime.Rows.Single(r => r.Text == "o-p0");
        Assert.False(fake.Images.ContainsKey(InstallModsPane.CellIcon));
        fetch.Respond(InstallModsPane.IconUrlFor(catalog.Entries[0])!);
        Settle(() => fake.Images.ContainsKey(InstallModsPane.CellIcon));
        Assert.Same(decoder.Handles[InstallModsPane.IconUrlFor(catalog.Entries[0])!], fake.Images[InstallModsPane.CellIcon]);
        // Filtering changes what is visible; the budget follows the visible rows ("Pack" and "3": 3, 13, 23, 30..39).
        pane.Search = "Pack 3";
        Assert.Equal(27, pane.RequestedIconUrls.Count);
        Assert.Contains(InstallModsPane.IconUrlFor(catalog.Entries[30])!, pane.RequestedIconUrls);
        pane.CancelIconRequests();
        Assert.Empty(pane.RequestedIconUrls);
        icons.CancelAll();
        // Revert: requesting every row's icon on Build, or never painting on Arrived, fails this.
    }

    // ---- The install flow --------------------------------------------------------------

    [Fact]
    public async Task Install_restart_with_one_target_runs_download_check_and_pending_and_shows_the_bar()
    {
        var catalog = new FlowCatalog();
        var entry = Listing("dyno-Bags", "BagPlus", categories: new[] { "Server", "Oakveil Update" });
        var other = Listing("dyno-Other", "Other", categories: new[] { "Oakveil Update" });
        catalog.Entries.AddRange(new[] { entry, other });
        catalog.Plans[entry.Key] = RestartPlan(entry, "h1");
        var rig = new Rig(this, catalog);
        var pane = rig.Pane;
        // INSTALLSIDES1: no server (the private games this client hosts load its plugins), so the
        // Server mod installs from the Client cell.
        Assert.Equal(SideCells.InstallCaption, Cell(pane, entry.Key, InstallModsPane.CellClientA).Text);
        Assert.Equal(CellSkin.PopupButton, Cell(pane, entry.Key, InstallModsPane.CellClientA).Skin);
        Assert.Equal(SideCells.SameAsClient, Cell(pane, entry.Key, InstallModsPane.CellServer).Text);

        // The client Install starts the download behind the open box; it opens no drawer.
        rig.Install(entry.Key);
        Assert.Null(pane.SelectedKey);
        Assert.Equal(entry.Key, pane.FlowKey);
        Assert.Equal(InstallActionPhase.Downloading, pane.Phase);
        Assert.Equal(1, catalog.DownloadCalls);
        Assert.Equal(entry.Key, catalog.LastDownloadKey);
        rig.Rebuild();
        Assert.Equal(InstallModsCopy.Installing, Cell(pane, entry.Key, InstallModsPane.CellClient).Text);
        Assert.False(Cell(pane, entry.Key, InstallModsPane.CellClientA).Visible);
        Assert.Equal("installing", pane.ListingToWidget(entry)["state"]);
        // A second press while busy is one call (the box owns the decision).
        Assert.False(pane.OnSideClick(entry.Key, server: false, SideButton.Install));
        Assert.Equal(1, catalog.DownloadCalls);

        await rig.Complete(new InspectCallResult(true, null, entry, null));
        // INSTALL2 item 3: the check is followed by the y/n box, not by the write. One target, so
        // the box carries no chooser, and nothing has been approved or written yet.
        Assert.Equal(InstallActionPhase.Inspected, pane.Phase);
        Assert.Equal(InstallPromptKind.Install, pane.Prompt);
        Assert.Equal(entry.Key, pane.PromptKey);
        Assert.False(pane.TargetChooserOpen);
        Assert.Equal(0, catalog.ApproveCalls);
        Assert.Equal(0, catalog.ExecuteCalls);
        Assert.Equal("Install BagPlus 1.0.0 on this game?", pane.PromptHeadingText(entry, InstallPromptKind.Install));
        Assert.Contains(
            "Install BagPlus 1.0.0 on this game?",
            pane.PromptHandles.Select(h => h.Text));
        var promptRow = (IUiCellRow)pane.PromptHandles.Last();
        Assert.Equal(InstallModsPane.PromptKeyPrefix + entry.Key, promptRow.Key);
        Assert.Equal(InstallModsCopy.PromptNo, promptRow.Cells.Single(c => c.Key == InstallModsPane.CellPromptNo).Text);
        Assert.Equal(InstallModsCopy.PromptYesInstall, promptRow.Cells.Single(c => c.Key == InstallModsPane.CellPromptYes).Text);
        Assert.Equal(CellTone.Ok, promptRow.Cells.Single(c => c.Key == InstallModsPane.CellPromptYes).Tone);
        // Yes is the only thing that writes.
        rig.ClickPromptYes(entry.Key);
        Assert.Equal(InstallActionPhase.Done, pane.Phase);
        Assert.Equal(InstallPromptKind.None, pane.Prompt);
        Assert.False(pane.TargetChooserOpen);
        Assert.Equal(1, catalog.ApproveCalls);
        Assert.Equal(1, catalog.ExecuteCalls);
        Assert.Equal("h1-client", catalog.LastExecutedHash);
        // INSTALLUX2 item 2: the status WORD is a plain label now. This row was written by THIS
        // click, which is the one pending state INSTALL2 leaves unclickable, so there is no button
        // beside it and `actions` is empty.
        Assert.Equal(InstallStatusLabels.PendingGameRestart, Cell(pane, entry.Key, InstallModsPane.CellClient).Text);
        Assert.Equal(CellTone.Info, Cell(pane, entry.Key, InstallModsPane.CellClient).Tone);
        Assert.False(Cell(pane, entry.Key, InstallModsPane.CellClientA).Visible);
        Assert.Empty((IReadOnlyList<string>)pane.ListingToWidget(entry)["actions"]!);
        Assert.Equal("pending", pane.ListingToWidget(entry)["state"]);
        Assert.Equal(true, pane.ListingToWidget(entry)["restart"]);
        rig.ClickRow(entry.Key);
        var texts = DrawerTexts(pane);
        Assert.Equal(SideCells.PrivateGamesLabel + ": " + SideCells.LoadsWithClient, pane.DrawerHeadRow!.Cells.Single(c => c.Key == InstallModsPane.CellNote).Text);
        Assert.Contains(InstallModsCopy.ConfigureInMods, texts);
        // The pending action row sits under the list. A game restart has no button: nothing in the
        // game can restart the game.
        Assert.Equal(2, pane.PendingBarHandles.Count);
        Assert.Equal("1 mod installed — restart the game to apply. »", pane.PendingBarText);
        Assert.Equal("", pane.PendingRestartCaption(pane.PendingSides()));
        Assert.Equal(1, pane.PendingCount());
        Assert.Equal(0, pane.PendingUninstallCount());
        // INSTALL2 item 5: the item row is built collapsed and the bar's text expands it.
        Assert.False(pane.PendingBarHandles[1].Visible);
        Assert.False(pane.PendingListOpen);
        rig.ClickCell(InstallModsPane.PendingRowKey, InstallModsPane.CellPendingText);
        Assert.True(pane.PendingListOpen);
        Assert.True(pane.PendingBarHandles[1].Visible);
        var item = (IUiCellRow)pane.PendingBarHandles[1];
        Assert.Equal(InstallModsPane.PendingItemKeyPrefix + entry.Key, item.Key);
        Assert.Equal(
            InstallModsCopy.PendingItemToLoad,
            item.Cells.Single(c => c.Key == InstallModsPane.CellPendingState).Text);
        Assert.True(pane.TryDescribeSidebarRow(pane.PendingBarHandles[0], out var bar));
        var pending = (Dictionary<string, object?>)bar!["pending"]!;
        Assert.Equal(1, pending["count"]);
        Assert.Equal(0, pending["uninstall"]);
        Assert.Equal(true, pending["open"]);
        var barItems = (List<Dictionary<string, object?>>)pending["items"]!;
        Assert.Equal(entry.Key, barItems.Single()["key"]);
        Assert.Equal("pending", barItems.Single()["state"]);
        // F20: no stop route here, so the press opens no request (and the bar shows no button).
        pane.RequestRestart();
        Assert.False(pane.RestartRequested);
        Assert.Equal(0, catalog.ExecuteCalls - 1);
        var action = pane.ActionToWidget(entry);
        Assert.Equal("done", action["phase"]);
        Assert.Equal("pending", action["state"]);
        Assert.NotNull(action["result"]);
        // The other row is untouched; the same pane on the catalog remembers the pending write.
        Assert.Equal(SideCells.InstallCaption, Cell(pane, other.Key, InstallModsPane.CellClientA).Text);
        var again = new InstallModsPane(catalog);
        Assert.Equal(InstallListingState.Pending, again.ActionFor(entry).State);
        Assert.Equal("2 mods installed — restart the game to apply. »", InstallModsCopy.PendingBar(2));
        Assert.Equal("2 mods installed · 1 uninstalled — restart the game to apply. »", InstallModsCopy.PendingBar(2, 1));
        Assert.Equal("1 uninstalled — restart the game to apply. »", InstallModsCopy.PendingBar(0, 1));
        string src = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "InstallModsPane.cs");
        string restart = SourcePin.MethodBody(src, "public void RequestRestart()");
        SourcePin.AssertNoStatementContaining(restart, "Process.");
        SourcePin.AssertNoStatementContaining(restart, "Application.Quit");
        // Revert: skipping the download/check, executing without approval, or a Restart now that starts a process fails this.
    }

    [Fact]
    public async Task Two_targets_show_the_chooser_after_the_check_and_the_chosen_target_writes_at_once()
    {
        var catalog = new FlowCatalog();
        var entry = Listing("dyno-Bags", "BagPlus", categories: new[] { "Server", "Oakveil Update" });
        catalog.Entries.Add(entry);
        catalog.Plans[entry.Key] = RestartPlan(entry, "h1");
        var server = ValidatedFolder();
        var rig = new Rig(this, catalog, server);
        var pane = rig.Pane;
        rig.ClickRow(entry.Key);
        rig.InstallOnServer(entry.Key);
        Assert.Equal(InstallActionPhase.Downloading, pane.Phase);
        // INSTALLUX1 item 1: the box is already open on the click, while the download runs, and it
        // says what the listing alone supports plus "Checking the package…". Item 2: no chooser.
        Assert.Equal(InstallPromptKind.Install, pane.Prompt);
        Assert.False(pane.PromptPlanReady);
        Assert.False(pane.TargetChooserOpen);
        Assert.Contains(InstallSummary.Checking, pane.InstallPromptBody(entry));
        await rig.Complete(new InspectCallResult(true, null, entry, null));
        // INSTALLSIDES1: the Server cell's Install writes the local server's folder, whatever the setting.
        Assert.Equal(InstallTarget.LocalServer, pane.Target);
        Assert.Equal(InstallPromptKind.Install, pane.Prompt);
        Assert.True(pane.PromptPlanReady);
        Assert.Equal(InstallActionPhase.Inspected, pane.Phase);
        Assert.Equal(0, catalog.ApproveCalls);
        Assert.Equal(0, catalog.ExecuteCalls);
        // The box is the heading, the body and the No / Yes row — nothing else.
        var promptRow = (IUiCellRow)pane.PromptHandles.Last();
        Assert.Equal(InstallModsPane.PromptKeyPrefix + entry.Key, promptRow.Key);
        Assert.Equal(
            // DIALOGS1: the shared dialog layout centers the pair between two equal spacers.
            new[] { "dialog-left", InstallModsPane.CellPromptNo, InstallModsPane.CellPromptYes, "dialog-right" },
            promptRow.Cells.Select(c => c.Key).ToArray());
        // The listing's own side is the body's word, not the target's.
        Assert.StartsWith("Server mod", pane.InstallPromptBody(entry));
        // INSTALL2 item 3: the row keeps its Install caption while the box is open — the box owns
        // the decision, and a second press on the row is ignored rather than restarting anything.
        Assert.Equal(SideCells.InstallCaption, Cell(pane, entry.Key, InstallModsPane.CellServerA).Text);
        Assert.DoesNotContain(pane.DrawerHeadRow!.Cells, c => c.Key == InstallModsPane.CellAction);
        Assert.False(pane.OnSideClick(entry.Key, server: true, SideButton.Install));
        Assert.Equal(1, catalog.DownloadCalls);
        Assert.Equal(0, catalog.ExecuteCalls);

        // Yes writes, for the target the setting resolved.
        rig.ClickPromptYes(entry.Key);
        Assert.Equal(InstallActionPhase.Done, pane.Phase);
        Assert.Equal(1, catalog.ApproveCalls);
        Assert.Equal(1, catalog.ExecuteCalls);
        Assert.Equal("h1-localServer", catalog.LastExecutedHash);
        Assert.False(pane.TargetChooserOpen);
        // Beside its Cancel install the words take two lines.
        Assert.Equal("Pending server\nrestart", Cell(pane, entry.Key, InstallModsPane.CellServer).Text);
        Assert.Equal(SideCells.CancelInstallCaption, Cell(pane, entry.Key, InstallModsPane.CellServerA).Text);
        Assert.Equal("localServer", pane.ActionToWidget(entry)["target"]);
        Assert.Equal(false, pane.ActionToWidget(entry)["chooser"]);
        // Job LIST4 (A): the chooser disappearing is not the feedback — the same rebuild that
        // takes it away turns the row's button into the pending caption and raises the pending
        // bar, so the player can see the install landed.
        Assert.Equal(CellTone.Info, Cell(pane, entry.Key, InstallModsPane.CellServer).Tone);
        // This client holds nothing of it: the write went to the server's folder alone.
        Assert.Equal(SideCells.NoClientSide, Cell(pane, entry.Key, InstallModsPane.CellClient).Text);
        Assert.Equal(2, pane.PendingBarHandles.Count);
        Assert.Equal(1, pane.PendingCount());
        Assert.Equal(SideCells.ServerLabel + ": " + SideCells.LoadsAtServerRestart, pane.DrawerHeadRow!.Cells.Single(c => c.Key == InstallModsPane.CellNote).Text);
        Assert.Null(pane.ActionToWidget(entry)["error"]);
        // Revert: putting a target chooser back inside the box, opening the box only after the
        // inspection, or ignoring [Install] DefaultTarget fails this.
    }

    /// <summary>
    /// Job LIST4 (A), rewritten for INSTALLUX1: the write Yes attempted failed. Nothing may be
    /// silent — the row keeps an installable caption and the drawer carries the error under it.
    /// </summary>
    [Fact]
    public async Task A_yes_whose_write_fails_closes_the_box_and_leaves_the_error_under_the_row()
    {
        var catalog = new FlowCatalog();
        var entry = Listing("dyno-Bags", "BagPlus", categories: new[] { "Server", "Oakveil Update" });
        catalog.Entries.Add(entry);
        catalog.Plans[entry.Key] = RestartPlan(entry, "h1");
        catalog.ExecuteError = "the pending folder is read-only";
        var rig = new Rig(this, catalog, ValidatedFolder());
        var pane = rig.Pane;
        rig.ClickRow(entry.Key);
        rig.InstallOnServer(entry.Key);
        await rig.Complete(new InspectCallResult(true, null, entry, null));
        Assert.False(pane.TargetChooserOpen);
        Assert.True(pane.PromptPlanReady);
        // INSTALLUX1 item 1: Yes is the only thing that writes, and this write fails.
        rig.ClickPromptYes(entry.Key);

        Assert.Equal(InstallActionPhase.Failed, pane.Phase);
        Assert.Equal(InstallPromptKind.None, pane.Prompt);
        Assert.False(pane.TargetChooserOpen);
        Assert.Equal(0, pane.PendingCount());
        Assert.Equal("the pending folder is read-only", pane.ActionToWidget(entry)["error"]);
        Assert.Equal("the pending folder is read-only", pane.ActionFor(entry).Reason);
        Assert.Contains("the pending folder is read-only", DrawerTexts(pane));
        Assert.Equal(SideCells.InstallCaption, Cell(pane, entry.Key, InstallModsPane.CellServerA).Text);
        // Revert: dropping panel.Note(action.Reason) from BuildDrawer, or leaving the box open
        // on a failed write with no reason row, fails this.
    }

    /// <summary>
    /// INSTALLSIDES2 readback: <c>drawer:&lt;key&gt;.anchoredTo {key, rowY, wantedY, offset}</c> — the
    /// last toggle's row, measured after the rebuild, where it was before, and the offset written.
    /// (LIST4 item 3's reveal and its <c>scrolledTo</c> are gone: a toggle keeps the list still.)
    /// </summary>
    [Fact]
    public void The_opened_drawer_reads_back_the_anchor()
    {
        var catalog = new FakeCatalog();
        var entry = Listing("o-p1", "Pack 1", description: "one-liner 1", categories: new[] { "Oakveil Update" });
        catalog.Entries.Add(entry);
        var rig = new Rig(this, catalog);
        var pane = rig.Pane;
        pane.AnchorProbe = (key, wanted) => wanted == null ? (412f, 300f) : (412f, 300f);
        rig.ClickRow(entry.Key);
        Assert.True(pane.TryDescribeSidebarRow(pane.DrawerHeadRow!, out var extra));
        var at = (Dictionary<string, object?>)extra!["anchoredTo"]!;
        Assert.Equal(new[] { "key", "rowY", "wantedY", "offset" }, at.Keys.ToArray());
        Assert.Equal(entry.Key, at["key"]);
        Assert.Equal(412d, at["wantedY"]);
        Assert.Equal(300d, at["offset"]);
        Assert.False(extra.ContainsKey("scrolledTo"));
    }

    [Fact]
    public async Task Blocked_plan_reads_Cannot_install_with_the_exact_reason_and_a_failed_download_retries()
    {
        var catalog = new FlowCatalog();
        var entry = Listing("dyno-Plain", "Plain BepInEx", categories: new[] { "Oakveil Update" });
        catalog.Entries.Add(entry);
        catalog.Plans[entry.Key] = new InstallPlan(
            "b", entry, InstallTarget.Client, InstallDecision.UnknownBlocked, InstallReasons.OrdinaryBepInExBlocked,
            Array.Empty<ArtifactHash>(), Array.Empty<PlanDependency>(), null, null);
        var rig = new Rig(this, catalog);
        var pane = rig.Pane;
        rig.ClickRow(entry.Key);
        rig.Install(entry.Key);
        await rig.Complete(new InspectCallResult(false, InstallReasons.FetchTransport, null, null));
        Assert.Equal(InstallActionPhase.Failed, pane.Phase);
        Assert.Equal(SideCells.InstallCaption, Cell(pane, entry.Key, InstallModsPane.CellClientA).Text);
        Assert.Contains(InstallReasons.FetchTransport, DrawerTexts(pane));
        Assert.Equal(InstallReasons.FetchTransport, pane.ActionFor(entry).Reason);

        // INSTALLUX1 item 1: the box opened on the first click and now carries the download's own
        // failure with a single Close. Close is what lets the row be clicked again.
        Assert.Equal(InstallPromptKind.Install, pane.Prompt);
        Assert.Equal(InstallReasons.FetchTransport, pane.PromptRefusal);
        // INSTALLUX2 item 1: a refusal's one button is Close, under its own cell key.
        rig.ClickCell(InstallModsPane.PromptKeyPrefix + entry.Key, InstallModsPane.CellPromptClose);
        Assert.Equal(InstallPromptKind.None, pane.Prompt);

        // A second click is a genuine retry; this time the planner refuses.
        rig.Install(entry.Key);
        Assert.Equal(2, catalog.DownloadCalls);
        await rig.Complete(new InspectCallResult(true, null, entry, null));
        var action = pane.ActionFor(entry);
        Assert.Equal(InstallListingState.Blocked, action.State);
        Assert.Equal(InstallModsCopy.CannotInstall, action.Label);
        Assert.Equal(InstallReasons.OrdinaryBepInExBlocked, action.Reason);
        Assert.False(action.Enabled);
        // INSTALLSIDES1: the Client cell says so and offers nothing; the reason is the drawer's.
        Assert.Equal(SideCells.CannotInstall, Cell(pane, entry.Key, InstallModsPane.CellClient).Text);
        Assert.Equal(CellTone.Amber, Cell(pane, entry.Key, InstallModsPane.CellClient).Tone);
        Assert.False(Cell(pane, entry.Key, InstallModsPane.CellClientA).Visible);
        Assert.Contains(InstallReasons.OrdinaryBepInExBlocked, DrawerTexts(pane));
        Assert.Equal(0, catalog.ExecuteCalls);
        Assert.Equal("blocked", pane.ListingToWidget(entry)["state"]);
        Assert.False(pane.OnSideClick(entry.Key, server: false, SideButton.Install));
        Assert.Equal(2, catalog.DownloadCalls);
        // Revert: a generic "restart required" instead of the planner reason, or executing a blocked plan, fails this.
    }

    [Fact]
    public async Task No_restart_install_reads_Installed_and_Configure_in_Mods_opens_the_plugin()
    {
        var catalog = new FlowCatalog();
        var entry = Verified("example.hot", "Hot Mod", hotLoadable: true, categories: new[] { "Client", "Oakveil Update" });
        catalog.Entries.Add(entry);
        catalog.Plans[entry.Key] = new InstallPlan(
            "n", entry, InstallTarget.Client, InstallDecision.ApplyNow, "hot-load declared",
            Array.Empty<ArtifactHash>(), Array.Empty<PlanDependency>(), "example.hot", "example.hot.dll", executorAvailable: true);
        var rig = new Rig(this, catalog);
        var pane = rig.Pane;
        string? opened = null;
        pane.SetOpenInstalled(id => opened = id);
        Assert.Equal(SideCells.InstallCaption, Cell(pane, entry.Key, InstallModsPane.CellClientA).Text);
        Assert.Equal(false, pane.ListingToWidget(entry)["restart"]);
        rig.Install(entry.Key);
        await rig.Complete(new InspectCallResult(true, null, entry, null));
        // INSTALL2 item 3: the check opens the y/n box, and its body says the hot load costs nothing.
        Assert.Equal(InstallPromptKind.Install, pane.Prompt);
        Assert.Contains(InstallSummary.LoadsInSeconds, pane.PromptBodyText(entry, InstallPromptKind.Install));
        rig.ClickPromptYes(entry.Key);
        var action = pane.ActionFor(entry);
        Assert.Equal(InstallListingState.Installed, action.State);
        Assert.Equal(InstallModsCopy.Installed, action.Label);
        Assert.Equal(CellTone.Ok, action.Tone);
        Assert.False(action.Restart);
        rig.ClickRow(entry.Key);
        Assert.StartsWith(SideCells.ClientLabel + ": " + SideCells.LoadsNow, pane.DrawerHeadRow!.Cells.Single(c => c.Key == InstallModsPane.CellNote).Text);
        Assert.DoesNotContain(InstallModsCopy.RestartRequired, DrawerTexts(pane));
        Assert.Empty(pane.PendingBarHandles);
        Assert.Equal(0, pane.PendingCount());
        rig.ClickDrawerButton(InstallModsCopy.ConfigureInMods);
        Assert.Equal("example.hot", opened);
        Assert.Equal("installed", pane.ListingToWidget(entry)["state"]);
        Assert.Equal(InstallModsCopy.LoadsInClient, pane.ListingToWidget(entry)["loadsIn"]);
        // Revert: reading a pending label for a hot-load plan with an executor, or a Configure link that opens nothing, fails this.
    }

    [Fact]
    public void Installed_state_from_disk_reads_Installed_or_Update_and_Tools_are_never_installable()
    {
        byte[] bytes = { 1, 2, 3, 4 };
        string sha = InstalledState.HashBytes(bytes);
        var catalog = new FlowCatalog();
        var loaded = new CatalogEntry(
            new ManifestMod("example.mod", "1.0.0", NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.None, true, "example", "example-Mod", sha, 4),
            CatalogProvenance.ManifestVerified, InstallReasons.SourceThunderstore, InstallReasons.SourceLabelThunderstore, "2026-09-11T00:00:00Z",
            displayName: "Mod", side: PackageSide.Client, owner: "example", packageName: "Mod", categories: new[] { "Oakveil Update" });
        var tool = Listing("x-Launcher", "Launcher", categories: new[] { "Tools", "Oakveil Update" });
        catalog.Entries.AddRange(new[] { loaded, tool });
        string reload = Path.Combine(_root, "progenitor-reload", "example.mod.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(reload)!);
        File.WriteAllBytes(reload, bytes);
        var rig = new Rig(this, catalog);
        var pane = rig.Pane;
        pane.SetInstalledQuery(new InstalledStateQuery(new[] { new InstalledFile(reload, InstalledState.ReloadKind, sha) }, loadedGeneration: "33"));
        rig.Rebuild();
        // INSTALLSIDES1: the Client cell says Installed; nothing in our ledgers claims the copy, so
        // it offers no button, and its drawer (opened by its expand button) links to the Mods tab.
        Assert.Equal(SideCells.InstalledPrefix, Cell(pane, loaded.Key, InstallModsPane.CellClient).Text);
        Assert.Equal(CellTone.OkBright, Cell(pane, loaded.Key, InstallModsPane.CellClient).Tone);
        Assert.Empty((IReadOnlyList<string>)pane.ListingToWidget(loaded)["actions"]!);
        Assert.False(Cell(pane, loaded.Key, InstallModsPane.CellClientA).Visible);
        rig.ClickRow(loaded.Key);
        Assert.Equal(loaded.Key, pane.SelectedKey);
        Assert.Equal(0, catalog.DownloadCalls);
        Assert.Contains(InstallModsCopy.ConfigureInMods, DrawerTexts(pane));

        // A pending file whose hash no longer matches the listing is an update.
        pane.SetInstalledQuery(new InstalledStateQuery(new[] { new InstalledFile(reload, InstalledState.ReloadKind, "deadbeef") }));
        rig.Rebuild();
        Assert.Equal("update", pane.ListingToWidget(loaded)["state"]);

        // The tool row: never installable, loads in —. INSTALLCOLS1: "Tool, not a plugin" is the
        // Status column's muted text and there is no button to press at all.
        var toolAction = pane.ActionFor(tool);
        Assert.Equal(InstallListingState.Tool, toolAction.State);
        Assert.Equal(InstallModsCopy.ToolNotPlugin, toolAction.Label);
        Assert.Equal("Tool, not a plugin", InstallModsPane.RowStatusText(toolAction));
        Assert.Equal("", InstallModsPane.RowButtonText(toolAction));
        Assert.Equal("Tool, not a plugin", Cell(pane, tool.Key, InstallModsPane.CellClient).Text);
        Assert.Equal(CellTone.Muted, Cell(pane, tool.Key, InstallModsPane.CellClient).Tone);
        Assert.False(Cell(pane, tool.Key, InstallModsPane.CellClientA).Visible);
        Assert.False(toolAction.Enabled);
        Assert.False(UiCellRowHandle.TryInvoke(tool.Key + "/" + InstallModsPane.CellClientA, out _));
        Assert.False(pane.OnInstallClick(tool.Key));
        Assert.Equal(0, catalog.DownloadCalls);
        Assert.NotEqual(tool.Key, pane.SelectedKey);
        Assert.Equal(InstallModsCopy.Dash, pane.ListingToWidget(tool)["loadsIn"]);
        rig.ClickRow(tool.Key);
        Assert.DoesNotContain(InstallModsCopy.RestartRequired, DrawerTexts(pane));
        Assert.DoesNotContain(InstallModsCopy.NoRestartNeeded, DrawerTexts(pane));
        Assert.True(pane.IsVisible(tool));
        // Revert: hiding tools, installing one, or an Installed row that downloads again fails this.
    }

    [Fact]
    public void Loads_in_follows_the_side_until_the_inspector_probe_answers()
    {
        var server = Listing("a-s", "S", categories: new[] { "Server" });
        var client = Listing("a-c", "C", categories: new[] { "Client" });
        var both = Listing("a-b", "B", categories: new[] { "Client", "Server" });
        var unknown = Listing("a-u", "U", categories: new[] { "Oakveil Update" });
        Assert.Equal(InstallModsCopy.LoadsInServer, InstallModsPane.LoadsIn(InstallModsPane.SideOf(server)));
        Assert.Equal(InstallModsCopy.LoadsInClient, InstallModsPane.LoadsIn(InstallModsPane.SideOf(client)));
        Assert.Equal(InstallModsCopy.LoadsInBoth, InstallModsPane.LoadsIn(InstallModsPane.SideOf(both)));
        Assert.Equal(InstallModsCopy.LoadsInUnknown, InstallModsPane.LoadsIn(InstallModsPane.SideOf(unknown)));
        Assert.Equal("the server (private games I host, or a dedicated server)", InstallModsCopy.LoadsInServer);
        Assert.Equal("my client and the server", InstallModsCopy.LoadsInBoth);
        Assert.Equal("unknown until checked", InstallModsCopy.LoadsInUnknown);
        // The hook: once the inspector knows the package's [BepInProcess], its answer wins.
        InstallModsPane.ProcessFilterProbe = e => e.Key == "a-u" ? PackageSide.Both : null;
        Assert.Equal(PackageSide.Both, InstallModsPane.SideOf(unknown));
        Assert.Equal(PackageSide.Server, InstallModsPane.SideOf(server));
        var pane = new InstallModsPane(new FakeCatalog());
        Assert.Equal("by a · released 2026-09-11 · loads in my client and the server", pane.MetaLine(unknown));
        Assert.Equal("both", pane.ListingToWidget(unknown)["side"]);
        InstallModsPane.ProcessFilterProbe = null;
        Assert.Equal("unknown", pane.ListingToWidget(unknown)["side"]);
        // A stale tag is warned about on the meta line, never blocked.
        var stale = Listing("a-old", "Old", categories: new[] { "Server", "Gloomrot Update" });
        Assert.EndsWith(InstallModsCopy.StaleWarnMark + "Gloomrot" + InstallModsCopy.StaleSuffix, pane.MetaLine(stale), StringComparison.Ordinal);
        Assert.Equal(InstallListingState.None, pane.ActionFor(stale).State);
        Assert.True(pane.ActionFor(stale).Enabled);
        var cells = pane.RowCells(stale);
        Assert.Equal("Built for Gloomrot", cells.Single(c => c.Key == InstallModsPane.CellName).NoteLead);
        Assert.Equal(CellTone.Warn, cells.Single(c => c.Key == InstallModsPane.CellName).NoteLeadTone);
        var tagged = Listing("a-t", "T", categories: new[] { "Server", "Oakveil Update" });
        Assert.Equal("", pane.RowCells(tagged).Single(c => c.Key == InstallModsPane.CellName).NoteLead);
        Assert.Equal("Oakveil", InstallModsPane.UpdateTagText(tagged));
        Assert.Equal(InstallModsCopy.UpdateTagNone, InstallModsPane.UpdateTagText(Listing("a-x", "X")));
        // Revert: deriving loads-in from the target instead of the side, or ignoring the probe, fails this.
    }

    // ---- README in the drawer -----------------------------------------------------------

    [Fact]
    public async Task Drawer_readme_renders_the_blocks_capped_with_Read_full_description_and_searches_after_it_lands()
    {
        var catalog = new FakeCatalog();
        var entry = Listing("dyno-ECF", "ECF", description: "Command framework", categories: new[] { "Oakveil Update" });
        catalog.Entries.Add(entry);
        var parts = new List<string> { "# ExampleCommandFramework", "A comprehensive framework for **mod developers**." };
        int n = (MarkdownPlain.DefaultMaxChars / 80) + 4;
        for (int i = 0; i < n; i++) parts.Add("p" + i + " " + new string('x', 78));
        catalog.Documents[entry.Key] = Docs(entry.Key, string.Join("\n\n", parts));
        catalog.HoldPrefetch = true;
        var rig = new Rig(this, catalog);
        var pane = rig.Pane;
        rig.ClickRow(entry.Key);
        Assert.Contains(InstallModsCopy.LoadingDescription, DrawerTexts(pane));
        Assert.Equal(ListingDocState.Loading, pane.ReadmeState);
        Assert.False(pane.ListingDocumentsSettled);

        // The on-demand fetch (ModsMenu.QueueListingDocuments) lands and the drawer is rebuilt.
        catalog.HoldPrefetch = false;
        var docs = await pane.BeginLoadListingDocuments(CancellationToken.None);
        pane.ApplyListingDocuments(docs);
        rig.Rebuild();
        Assert.True(pane.ListingDocumentsSettled);
        Assert.Equal(ListingDocState.Ready, pane.ReadmeState);
        // INSTALLSIDES2 item 4 (option A): the compact drawer shows a short description — the
        // README's opening paragraphs, clamped — and no headings; the whole README is in the reader.
        var lines = pane.DrawerBodyRow!.Cells.Single(c => c.Key == InstallModsPane.CellBody).Lines;
        Assert.True(lines.Any(l => l.Kind == CellLineKind.Body && l.Text.StartsWith("A comprehensive framework for mod developers.", StringComparison.Ordinal)),
            string.Join(" | ", lines.Take(16).Select(l => l.Kind + ":" + l.Text)));
        Assert.DoesNotContain(lines, l => l.Kind == CellLineKind.Heading);
        Assert.All(lines.Where(l => l.Kind == CellLineKind.Body), l => Assert.True(l.Text.Length <= InstallDrawerLayout.ExcerptChars + 1, l.Text));
        Assert.DoesNotContain(InstallModsCopy.LoadingDescription, DrawerTexts(pane));
        var readFull = pane.DrawerHeadRow!.Cells.Single(c => c.Key == InstallModsPane.CellReadFull);
        Assert.Equal(InstallModsCopy.ReadFullDescription, readFull.Text);
        Assert.Equal(CellSkin.PopupButton, readFull.Skin);
        Assert.DoesNotContain(lines, l => l.Text.StartsWith("p" + (n - 1) + " ", StringComparison.Ordinal));
        Assert.True(pane.ReadmeBlockCount >= n);
        rig.ClickDrawerButton(InstallModsCopy.ReadFullDescription);
        // The reader: the game's popup with one scrolling block, the headings and the whole README.
        var reader = UiDialog.Current;
        Assert.NotNull(reader);
        Assert.Equal(InstallModsPane.ReaderDialogKind, reader!.Kind);
        Assert.Equal(InstallModsCopy.ReaderClose, reader.Content.CancelCaption);
        Assert.Contains(reader.Content.Reader, l => l.Kind == CellLineKind.Heading && l.Text == "ExampleCommandFramework");
        Assert.Contains(reader.Content.Reader, l => l.Text.StartsWith("p" + (n - 1) + " ", StringComparison.Ordinal));
        Assert.True(reader.Content.ReaderHeight <= DialogLayout.ReaderHeight);
        Assert.NotNull(reader.View!.ReaderRow);
        Assert.False(reader.View.ReaderRow!.Cells.Single(c => c.Key == UiDialogs.CellReader).NoScroll);
        Assert.True(pane.FullDescriptionShown);
        // The drawer did not grow.
        Assert.Same(lines, pane.DrawerBodyRow!.Cells.Single(c => c.Key == InstallModsPane.CellBody).Lines);
        reader.Close();
        Assert.False(pane.FullDescriptionShown);
        var readme = pane.ReadmeToWidget();
        Assert.Equal("ready", readme.GetType().GetProperty("state")!.GetValue(readme));
        Assert.True(pane.TryDescribeSidebarRow(pane.ReadmeBodyHandle!, out var extra));
        Assert.Equal(entry.Key, extra!["drawer"]);
        Assert.NotNull(extra["readme"]);
        // The fetched text is searchable now.
        pane.Search = "comprehensive";
        Assert.Equal(new[] { "ECF" }, pane.VisibleEntries().Select(e => e.DisplayName).ToArray());
        Assert.Equal("withReadme", pane.SearchScope(entry));
        // An unavailable README states the reason.
        var missing = new FakeCatalog();
        var none = Listing("o-none", "None", categories: new[] { "Oakveil Update" });
        missing.Entries.Add(none);
        missing.HoldPrefetch = true;
        var rig2 = new Rig(this, missing);
        rig2.ClickRow(none.Key);
        missing.HoldPrefetch = false;
        rig2.Pane.ApplyListingDocuments(await rig2.Pane.BeginLoadListingDocuments(CancellationToken.None));
        rig2.Rebuild();
        Assert.Equal(ListingDocState.Unavailable, rig2.Pane.ReadmeState);
        Assert.Contains(DrawerTexts(rig2.Pane), t => t.StartsWith(InstallModsCopy.DescriptionUnavailablePrefix, StringComparison.Ordinal) && t.Contains(InstallReasons.UnknownEntry, StringComparison.Ordinal));
        // Revert: one label for the whole README, no cap, or a README that never becomes searchable fails this.
    }

    // ---- README-STRUCT1: the drawer keeps the README's structure in a later generation -----------

    private const string StructuredReadme = "Intro paragraph.\n\n## Features\n\n- Relic Codex\n\n## Requirements\n\n- BepInExPack";

    /// <summary>INSTALLSIDES2: the README's structured lines are the reader's (the drawer shows an excerpt).</summary>
    private static IReadOnlyList<CellLine> BodyLines(InstallModsPane pane) =>
        pane.ReaderLines(pane.Rows.Single(r => r.Row.Key == pane.DrawerKey).Entry);

    /// <summary>
    /// Live 2026-10-03: "Features" and "Requirements" were headings on the first game start and
    /// plain body text after a Core hot swap. The catalog's fetched documents live in the Core
    /// generation's memory; the next generation (a new catalog and pane state here) had only the
    /// disk cache's plain search text, one paragraph per line. The markdown is now cached beside
    /// it and the drawer renders from it, with no network request.
    /// </summary>
    [Fact]
    public async Task A_later_generation_draws_the_readme_headings_from_the_disk_cache()
    {
        var disk = new ListingDiskCache(_root);
        var entry = Listing("a-g", "Almanacs", description: "x", categories: new[] { "Oakveil Update" });

        // Generation 1: fetched, drawn with headings, written to disk.
        var first = new FakeCatalog();
        first.Entries.Add(entry);
        first.Documents[entry.Key] = Docs(entry.Key, StructuredReadme);
        first.HoldPrefetch = true;
        var rig = new Rig(this, first);
        rig.Pane.SetDiskCache(disk);
        rig.ClickRow(entry.Key);
        first.HoldPrefetch = false;
        rig.Pane.ApplyListingDocuments(await rig.Pane.BeginLoadListingDocuments(CancellationToken.None));
        rig.Rebuild();
        Assert.Contains(BodyLines(rig.Pane), l => l.Kind == CellLineKind.Heading && l.Text == "Features");
        Assert.True(disk.TryReadReadmeMarkdown("a", "g", "1.0.0", InstallModsPane.ReadmeStamp(entry), out string md));
        Assert.Contains("## Requirements", md, StringComparison.Ordinal);

        rig.Panel.Dispose();

        // Generation 2: a new catalog with nothing in memory and nothing on the network.
        var second = new FakeCatalog { HoldPrefetch = true };
        second.Entries.Add(entry);
        var rig2 = new Rig(this, second);
        rig2.Pane.SetDiskCache(disk);
        rig2.Rebuild();
        rig2.ClickRow(entry.Key);
        var lines = BodyLines(rig2.Pane);
        string dump = string.Join(" | ", lines.Select(l => l.Kind + ":" + l.Text));
        Assert.True(lines.Any(l => l.Kind == CellLineKind.Heading && l.Text == "Features"), dump);
        Assert.Contains(lines, l => l.Kind == CellLineKind.Heading && l.Text == "Requirements");
        Assert.DoesNotContain(lines, l => l.Kind == CellLineKind.Body && l.Text == "Features");
        // Settled: the Mods menu asks the network for nothing.
        Assert.True(rig2.Pane.ListingDocumentsSettled);
    }

    /// <summary>
    /// A README cached before the markdown was kept (plain text only): the drawer shows the text at
    /// once, asks the network once for the markdown, and draws the headings when it lands. A failed
    /// ask keeps the text and is not repeated.
    /// </summary>
    [Fact]
    public async Task A_plain_only_cached_readme_asks_once_for_its_structure()
    {
        var disk = new ListingDiskCache(_root);
        var entry = Listing("a-g", "Almanacs", description: "x", categories: new[] { "Oakveil Update" });
        Assert.True(disk.WriteReadme("a", "g", "1.0.0", InstallModsPane.ReadmeStamp(entry), "Intro paragraph.\nFeatures\nRelic Codex"));

        var catalog = new FakeCatalog { HoldPrefetch = true };
        catalog.Entries.Add(entry);
        catalog.Documents[entry.Key] = Docs(entry.Key, StructuredReadme);
        var rig = new Rig(this, catalog);
        rig.Pane.SetDiskCache(disk);
        rig.Rebuild();
        rig.ClickRow(entry.Key);
        Assert.True(BodyLines(rig.Pane).Any(l => l.Kind == CellLineKind.Body && l.Text == "Features"),
            string.Join(" | ", BodyLines(rig.Pane).Select(l => l.Kind + ":" + l.Text)));
        Assert.Equal(ListingDocState.Ready, rig.Pane.ReadmeState);
        Assert.False(rig.Pane.ListingDocumentsSettled);
        catalog.HoldPrefetch = false;
        rig.Pane.ApplyListingDocuments(await rig.Pane.BeginLoadListingDocuments(CancellationToken.None));
        rig.Rebuild();
        Assert.Contains(BodyLines(rig.Pane), l => l.Kind == CellLineKind.Heading && l.Text == "Features");
        Assert.True(rig.Pane.ListingDocumentsSettled);

        // Offline: the ask fails, the plain text stays, and it is settled (no retry loop).
        var other = Listing("a-h", "Hollow", description: "x", categories: new[] { "Oakveil Update" });
        Assert.True(disk.WriteReadme("a", "h", "1.0.0", InstallModsPane.ReadmeStamp(other), "Only text\nMore text"));
        rig.Panel.Dispose();
        var offline = new FakeCatalog { HoldPrefetch = true };
        offline.Entries.Add(other);
        var rig2 = new Rig(this, offline);
        rig2.Pane.SetDiskCache(disk);
        rig2.Rebuild();
        rig2.ClickRow(other.Key);
        Assert.False(rig2.Pane.ListingDocumentsSettled);
        offline.HoldPrefetch = false;
        rig2.Pane.ApplyListingDocuments(await rig2.Pane.BeginLoadListingDocuments(CancellationToken.None));
        rig2.Rebuild();
        Assert.True(rig2.Pane.ListingDocumentsSettled);
        Assert.Equal(ListingDocState.Ready, rig2.Pane.ReadmeState);
        Assert.Contains(BodyLines(rig2.Pane), l => l.Text == "Only text");
    }

    // ---- Readback -------------------------------------------------------------------------

    [Fact]
    public void Readback_extras_name_the_listing_the_toolbar_counts_and_the_drawer()
    {
        var catalog = new FlowCatalog();
        var entry = Listing("dyno-Bags", "BagPlus", categories: new[] { "Server", "Oakveil Update" }, downloads: 42);
        catalog.Entries.Add(entry);
        var rig = new Rig(this, catalog);
        var pane = rig.Pane;
        rig.ClickRow(entry.Key);
        var row = pane.Rows.Single().Row;
        Assert.True(pane.TryDescribeSidebarRow(row, out var extra));
        var listing = (Dictionary<string, object?>)extra!["listing"]!;
        // INSTORD2 added ledger and files: what the install ledgers say about this listing at rest.
        // HOT3 added hotLoaded: whether this row's Remove is a hot unload or owes a restart.
        // HOT4 added halves and serverHalfNote: the two processes of a Both install can disagree.
        // INSTALL2 added uninstallPending and newTag: the row's files go at the next restart, and
        // the mock's small "new" tag for a mod this session hot loaded.
        // LINK1 added rating: the provider's like count for this listing (v1 `rating_score`), null
        // when the provider published none.
        Assert.Equal(
            new[] { "id", "state", "sides", "client", "server", "shortcut", "drawerOpen", "nsfw", "restart", "side", "loadsIn", "label", "reason", "hotLoaded", "uninstallPending", "newTag", "actions", "managed", "status", "button", "installed", "released", "releasedDate", "downloads", "updateNote", "plainUninstall", "currentUpdate", "searchScope", "icon", "ledger", "files", "halves", "serverHalfNote", "action" },
            listing.Keys.ToArray());
        // INSTALLSIDES1: the two cells the row draws, each with its words, tone and buttons.
        var clientCell = (Dictionary<string, object?>)listing["client"]!;
        Assert.Equal(new[] { "kind", "text", "tone", "buttons", "note" }, clientCell.Keys.ToArray());
        Assert.Equal(SideCells.KindNotInstalled, clientCell["kind"]);
        Assert.Equal(new List<string> { "install" }, clientCell["buttons"]);
        Assert.Equal(SideCells.KindSameAsClient, ((Dictionary<string, object?>)listing["server"]!)["kind"]);
        Assert.Equal("none", listing["shortcut"]);
        Assert.Equal(true, listing["drawerOpen"]);
        Assert.Equal(false, listing["nsfw"]);
        Assert.False(listing.ContainsKey("rating"), "ratings are removed (decided 2026-09-29)");
        Assert.Equal(false, listing["uninstallPending"]);
        Assert.Equal(false, listing["newTag"]);
        var ledger = (Dictionary<string, object?>)listing["ledger"]!;
        Assert.Equal(new[] { "client", "server", "version" }, ledger.Keys.ToArray());
        Assert.Equal(false, ledger["client"]);
        Assert.Equal(false, ledger["server"]);
        Assert.Null(ledger["version"]);
        Assert.Empty((List<Dictionary<string, object?>>)listing["files"]!);
        // ICON2: every row carries its icon state; with no cache wired the URL is simply not known yet.
        var icon = (Dictionary<string, object?>)listing["icon"]!;
        Assert.Equal(new[] { "state", "cached", "error" }, icon.Keys.ToArray());
        Assert.Equal("missing", icon["state"]);
        Assert.Equal(false, icon["cached"]);
        Assert.Equal(entry.Key, listing["id"]);
        Assert.Equal("none", listing["state"]);
        Assert.Equal(true, listing["restart"]);
        Assert.Equal("server", listing["side"]);
        Assert.Equal(InstallModsCopy.LoadsInServer, listing["loadsIn"]);
        var described = row.Describe();
        Assert.Equal("cellrow", described["kind"]);
        Assert.Equal(true, described["expanded"]);
        Assert.NotNull(described["rect"]);
        var cells = (List<object?>)described["cells"]!;
        Assert.Contains(cells, c => (string?)((Dictionary<string, object?>)c!)["key"] == InstallModsPane.CellClientA);
        Assert.Contains(cells, c => (string?)((Dictionary<string, object?>)c!)["key"] == InstallModsPane.CellExpand
            && (string?)((Dictionary<string, object?>)c!)["skin"] == "expander");

        Assert.True(pane.TryDescribeSidebarRow(pane.ToolbarRow!, out var toolbar));
        var count = (Dictionary<string, object?>)toolbar!["count"]!;
        Assert.Equal(1, count["shown"]);
        Assert.Equal(0, count["filtered"]);
        Assert.Equal(1, count["total"]);
        var descriptions = (Dictionary<string, object?>)toolbar["descriptions"]!;
        Assert.Equal(new[] { "state", "done", "total" }, descriptions.Keys.ToArray());
        // POLISH2 item 1: the readback's sort key is the new default.
        Assert.Equal("released", toolbar["sort"]);
        // ICON2: the toolbar totals, so a stalled icon queue is readable without a screenshot.
        var iconTotals = (Dictionary<string, object?>)toolbar["icons"]!;
        Assert.Equal(
            new[] { "ready", "queued", "loading", "failed", "inFlightUrl", "lastError", "fetches", "stalledSeconds" },
            iconTotals.Keys.ToArray());
        Assert.Equal(0, iconTotals["ready"]);
        Assert.Null(iconTotals["inFlightUrl"]);
        Assert.True(pane.TryDescribeSidebarRow(pane.FilterRow!, out var filters));
        var f = (Dictionary<string, object?>)filters!["filters"]!;
        Assert.Equal("any", f["side"]);
        Assert.Equal("Oakveil Update", f["currentUpdateTag"]);
        Assert.Equal("adapter", f["currentUpdateSource"]);
        Assert.True(pane.TryDescribeSidebarRow(pane.HeaderRow!, out var header));
        // POLISH2 item 1: the header row's readback names the same new default.
        Assert.Equal("released", header!["sort"]);
        // INSTALLCOLS1: the direction, and the column set with its widths and dropped columns.
        Assert.Equal("desc", header["sortDirection"]);
        Assert.Equal("desc", toolbar["sortDirection"]);
        var columns = (Dictionary<string, object?>)header["columns"]!;
        Assert.Equal(new[] { "set", "keys", "rowWidth", "widths", "dropped", "dropPriority", "sortable" }, columns.Keys.ToArray());
        Assert.Equal(InstallColumns.ColumnNames, columns["set"]);
        Assert.Equal(new[] { "released", "downloads" }, (IReadOnlyList<string>)columns["dropPriority"]!);
        Assert.Equal(new[] { "name", "installed", "released", "downloads" }, (List<string>)columns["sortable"]!);
        Assert.Equal(InstallColumns.HeaderKeys.Count, ((Dictionary<string, object?>)columns["widths"]!).Count);

        Assert.True(pane.TryDescribeSidebarRow(pane.DrawerHeadRow!, out var head));
        Assert.Equal(entry.Key, head!["drawer"]);
        var action = (Dictionary<string, object?>)head["action"]!;
        Assert.Equal(InstallModsCopy.Install, action["label"]);
        Assert.Equal("idle", action["phase"]);
        Assert.Equal(false, action["chooser"]);
        foreach (var handle in pane.DrawerHandles)
        {
            Assert.True(pane.TryDescribeSidebarRow(handle, out var d));
            Assert.Equal(entry.Key, d!["drawer"]);
        }
        Assert.Equal("drawer:" + entry.Key, pane.DrawerHeadRow!.Key);
        // Job LIST4 item 1: the head has no action cell to invoke any more, so the one button is
        // addressed on the listing row. The action object stays readable under the drawer key as
        // an alias for one release, and is now also on the row that owns the button.
        Assert.False(UiCellRowHandle.TryInvoke("drawer:" + entry.Key + "/" + InstallModsPane.CellAction, out _));
        Assert.True(UiCellRowHandle.TryInvoke(entry.Key + "/" + InstallModsPane.CellClientA, out var invoked));
        Assert.Equal("cell", invoked["kind"]);
        // INSTALLSIDES1: the drawer body reads back its size against the list viewport.
        Assert.True(pane.TryDescribeSidebarRow(pane.DrawerBodyRow!, out var bodyExtra));
        var drawerBody = (Dictionary<string, object?>)bodyExtra!["drawerBody"]!;
        Assert.Equal(new[] { "height", "lines", "contentEstimate", "listViewport", "blockShare" }, drawerBody.Keys.ToArray());
        Assert.NotNull(bodyExtra["readme"]);
        Assert.Equal(InstallActionPhase.Downloading, pane.Phase);
        Assert.True(pane.TryDescribeSidebarRow(pane.Rows.Single(r => r.Row.Key == entry.Key).Row, out var rowExtra));
        var rowAction = (Dictionary<string, object?>)((Dictionary<string, object?>)rowExtra!["listing"]!)["action"]!;
        Assert.Equal(InstallModsCopy.Installing, rowAction["label"]);
        Assert.Equal("installing", rowAction["state"]);
        // Revert: dropping a key from `listing`, or a drawer row without `drawer`, fails this.
    }

    // ---- INSTALLUX1: one y/n box, a target from a setting, no cropped buttons -------------

    /// <summary>
    /// INSTALLUX1 item 1, the user's decision verbatim: "when they click install a popup opens and
    /// confirms, install MOD (mod name) y/n. then that is it". The box is up on the click \u2014 before
    /// the download, before the inspection \u2014 with the heading it will keep, the sentences the
    /// listing alone supports, "Checking the package\u2026", and the same <c>Yes, install</c> caption it
    /// will have when the plan lands. Nothing about the box changes shape when the plan arrives.
    /// </summary>
    [Fact]
    public async Task The_box_opens_on_the_install_click_before_the_inspection_and_keeps_its_buttons()
    {
        var catalog = new FlowCatalog();
        var entry = Listing("dyno-Bags", "BagPlus", categories: new[] { "Client", "Oakveil Update" });
        catalog.Entries.Add(entry);
        catalog.Plans[entry.Key] = RestartPlan(entry, "h1");
        var rig = new Rig(this, catalog);
        var pane = rig.Pane;

        rig.Install(entry.Key);

        // Open, on this click, while the download is still running.
        Assert.Equal(InstallActionPhase.Downloading, pane.Phase);
        Assert.Equal(InstallPromptKind.Install, pane.Prompt);
        Assert.Equal(entry.Key, pane.PromptKey);
        Assert.False(pane.PromptPlanReady);
        Assert.Null(pane.PromptRefusal);
        Assert.Equal("Install BagPlus 1.0.0 on this game?", pane.PromptHeadingText(entry, InstallPromptKind.Install));
        string checking = pane.InstallPromptBody(entry);
        Assert.Equal("Client mod by dyno, version 1.0.0. " + InstallSummary.Checking, checking);
        var buttons = (IUiCellRow)pane.PromptHandles.Last();
        Assert.Equal(InstallModsCopy.PromptNo, buttons.Cells.Single(c => c.Key == InstallModsPane.CellPromptNo).Text);
        Assert.Equal(InstallModsCopy.PromptYesInstall, buttons.Cells.Single(c => c.Key == InstallModsPane.CellPromptYes).Text);
        var asked = (Dictionary<string, object?>)pane.ActionToWidget(entry)["prompt"]!;
        Assert.Equal(false, asked["ready"]);
        Assert.Equal(false, asked["chooser"]);
        Assert.Null(asked["refusal"]);
        Assert.Equal(InstallModsCopy.PromptYesInstall, asked["yes"]);

        await rig.Complete(new InspectCallResult(true, null, entry, null));

        // Ready: the same box, the same buttons, a fuller body.
        Assert.Equal(InstallPromptKind.Install, pane.Prompt);
        Assert.True(pane.PromptPlanReady);
        Assert.DoesNotContain(InstallSummary.Checking, pane.InstallPromptBody(entry));
        Assert.Contains("Loads at the next restart of V Rising.", pane.InstallPromptBody(entry));
        Assert.Equal(
            InstallModsCopy.PromptYesInstall,
            ((IUiCellRow)pane.PromptHandles.Last()).Cells.Single(c => c.Key == InstallModsPane.CellPromptYes).Text);
        Assert.Equal(0, catalog.ExecuteCalls);
        // Revert: opening the box from OpenInstallPrompt alone (after the inspection) leaves Prompt
        // None on the click and fails the first half; dropping the Checking sentence fails the body.
    }

    // ---- INSTALLUX2: the modal dialog, the row pair, the side word ------------------------

    /// <summary>
    /// INSTALLUX2 item 1, the user on the INSTALLUX1 build: "I click install on and it expands like
    /// the old behavior asking me if I want to stall like this instead of popping up a dialogue."
    /// The confirmation is a modal dialog on its own panel - heading, body and the two buttons under
    /// the handles the bridge already publishes (<c>prompt:&lt;key&gt;</c> / <c>promptNo</c> /
    /// <c>promptYes</c>) - and NOTHING prompt-shaped is drawn in the row or its drawer. The readback
    /// says <c>presentation: dialog</c> and <c>mods_ui status</c> can see the box.
    /// What this would catch: BuildPrompt coming back into the drawer (the drawer would carry the
    /// heading again), a dialog built with fresh cell keys (every widget_click on promptYes breaks),
    /// or a dialog that opens only at the pane's next rebuild rather than on the click.
    /// </summary>
    [Fact]
    public void The_confirmation_is_a_dialog_with_the_published_handles_and_the_drawer_draws_none_of_it()
    {
        var catalog = new FlowCatalog();
        var entry = Listing("dyno-Bags", "BagPlus", categories: new[] { "Client", "Oakveil Update" });
        catalog.Entries.Add(entry);
        catalog.Plans[entry.Key] = RestartPlan(entry, "h1");
        var rig = new Rig(this, catalog);
        var pane = rig.Pane;
        // The injectable factory is the seam that keeps this test game-free.
        var dialogPanels = new List<IUiPanel>();
        pane.DialogPanelFactory = title =>
        {
            var panel = NewPanel();
            dialogPanels.Add(panel);
            return panel;
        };

        rig.Install(entry.Key);

        Assert.NotEmpty(dialogPanels);
        var dialog = pane.PromptDialog;
        Assert.NotNull(dialog);
        Assert.True(dialog!.IsOpen);
        Assert.Equal(entry.Key, dialog.Key);
        Assert.Equal("install", dialog.Kind);
        Assert.NotSame(rig.Panel, dialog.Panel);
        var described = UiDialog.Describe();
        Assert.Equal(true, described["open"]);
        Assert.Equal(entry.Key, described["key"]);
        Assert.Equal("install", described["kind"]);

        // The handles INSTALL2/INSTALLUX1 published, on the dialog's own row.
        var buttons = dialog.Prompt.Buttons!;
        Assert.Equal(InstallModsPane.PromptKeyPrefix + entry.Key, buttons.Key);
        Assert.Equal(InstallModsCopy.PromptNo, buttons.Cells.Single(c => c.Key == InstallModsPane.CellPromptNo).Text);
        Assert.Equal(InstallModsCopy.PromptYesInstall, buttons.Cells.Single(c => c.Key == InstallModsPane.CellPromptYes).Text);
        Assert.Equal("Install BagPlus 1.0.0 on this game?", dialog.Prompt.Heading!.Text);

        // Nothing prompt-shaped in the row's drawer: no heading, no body, no button row.
        Assert.DoesNotContain("Install BagPlus 1.0.0 on this game?", DrawerTexts(pane));
        Assert.DoesNotContain(
            InstallModsPane.PromptKeyPrefix + entry.Key,
            pane.DrawerHandles.OfType<IUiCellRow>().Select(r => r.Key));

        var asked = (Dictionary<string, object?>)pane.ActionToWidget(entry)["prompt"]!;
        Assert.Equal("dialog", asked["presentation"]);
        Assert.Equal(InstallModsCopy.PromptYesInstall, asked["yes"]);
        Assert.Equal(InstallModsCopy.PromptNo, asked["no"]);

        // The published No handle still decides: it closes the box and writes nothing.
        rig.ClickPromptNo(entry.Key);
        Assert.Equal(InstallPromptKind.None, pane.Prompt);
        Assert.Null(pane.PromptDialog);
        Assert.Equal(false, UiDialog.Describe()["open"]);
        Assert.Equal(0, catalog.ExecuteCalls);
        // Revert: drawing the prompt in the drawer again, or renaming the cells, fails this.
    }

    /// <summary>
    /// INSTALLUX2 item 1: Escape answers the open dialog with No. The kit calls
    /// <c>UiDialog.NotifyEscape</c> from the key hook before the sweep that hides shown panels;
    /// without the pane binding its No to the dialog the box would vanish with the pane still
    /// believing a confirmation is open, and the row could never be clicked again.
    /// What this would catch: binding only Yes, or closing the dialog without telling the pane.
    /// </summary>
    [Fact]
    public void Escape_answers_the_open_confirmation_with_No()
    {
        var catalog = new FlowCatalog();
        var entry = Listing("dyno-Bags", "BagPlus", categories: new[] { "Client", "Oakveil Update" });
        catalog.Entries.Add(entry);
        catalog.Plans[entry.Key] = RestartPlan(entry, "h1");
        var rig = new Rig(this, catalog);
        var pane = rig.Pane;
        pane.DialogPanelFactory = _ => NewPanel();

        rig.Install(entry.Key);
        Assert.Equal(InstallPromptKind.Install, pane.Prompt);

        Assert.True(UiDialog.NotifyEscape());

        Assert.Equal(InstallPromptKind.None, pane.Prompt);
        Assert.Null(pane.PromptDialog);
        Assert.Equal(0, catalog.ExecuteCalls);
        Assert.False(UiDialog.NotifyEscape());
        // Revert: dropping the OnNo binding leaves Prompt Install after Escape.
    }

    /// <summary>
    /// INSTALLUX2 item 1: a refusal is the same dialog with its body replaced and ONE button -
    /// <c>Close</c>, under the new <c>promptClose</c> cell - because there is nothing to say yes to.
    /// What this would catch: a refusal that keeps a Yes (a player could confirm an install the
    /// planner refused), or Close reusing promptNo, which would make a refusal and a y/n box
    /// indistinguishable to a bridge client.
    /// </summary>
    [Fact]
    public async Task A_refused_install_dialog_has_only_Close()
    {
        var catalog = new FlowCatalog();
        var entry = Listing("dyno-Bags", "BagPlus", categories: new[] { "Client", "Oakveil Update" });
        catalog.Entries.Add(entry);
        catalog.Plans[entry.Key] = RestartPlan(entry, "h1");
        var rig = new Rig(this, catalog);
        var pane = rig.Pane;
        pane.DialogPanelFactory = _ => NewPanel();

        rig.Install(entry.Key);
        await rig.Complete(new InspectCallResult(false, InstallReasons.FetchTransport, null, null));

        Assert.Equal(InstallPromptKind.Install, pane.Prompt);
        Assert.Equal(InstallReasons.FetchTransport, pane.PromptRefusal);
        var buttons = pane.PromptDialog!.Prompt.Buttons!;
        Assert.DoesNotContain(InstallModsPane.CellPromptYes, buttons.Cells.Select(c => c.Key));
        Assert.DoesNotContain(InstallModsPane.CellPromptNo, buttons.Cells.Select(c => c.Key));
        Assert.Equal(InstallModsCopy.PromptClose, buttons.Cells.Single(c => c.Key == InstallModsPane.CellPromptClose).Text);
        var asked = (Dictionary<string, object?>)pane.ActionToWidget(entry)["prompt"]!;
        Assert.Equal("dialog", asked["presentation"]);
        Assert.Equal("", asked["yes"]);
        Assert.Equal(InstallModsCopy.PromptClose, asked["no"]);

        rig.ClickCell(InstallModsPane.PromptKeyPrefix + entry.Key, InstallModsPane.CellPromptClose);
        Assert.Equal(InstallPromptKind.None, pane.Prompt);
        // Revert: leaving the Yes cell on a refusal, or closing under promptNo, fails this.
    }

    /// <summary>
    /// INSTALLUX2 item 2, the user: "I then have to click install / new to start the uninstall. That
    /// is bad UX. Let's get rid of the install new features. Just change it to an uninstall button."
    /// An installed row states what it IS as a plain label and offers the one thing left to do beside
    /// it: Installed + Uninstall, and Uninstalled - restart to remove + Reactivate. No caption
    /// anywhere carries the retired <c>new</c> tag, and <c>listing.actions[]</c> names the buttons
    /// actually drawn.
    /// What this would catch: the status word going back into the button (the removal would hide
    /// behind the word again), the `new` tag returning to a hot-loaded row, or actions[] reporting a
    /// button the row does not draw.
    /// </summary>
    [Fact]
    public void An_installed_row_is_a_status_label_and_one_button_and_no_caption_says_new()
    {
        var bytes = new byte[] { 7, 7 };
        string sha = InstalledState.HashBytes(bytes);
        var catalog = new FlowCatalog();
        var entry = Listing("dyno-Bags", "BagPlus", categories: new[] { "Client", "Oakveil Update" });
        catalog.Entries.Add(entry);
        var rig = new Rig(this, catalog);
        var pane = rig.Pane;
        pane.LedgerProbe = _ => true;
        pane.HotLedgerProbe = _ => true;
        string reload = Path.Combine(_root, "progenitor-reload", "bagplus.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(reload)!);
        File.WriteAllBytes(reload, bytes);
        pane.SetInstalledQuery(new InstalledStateQuery(
            new[] { new InstalledFile(reload, InstalledState.ReloadKind, sha) }, loadedGeneration: "33"));
        rig.Rebuild();

        // Installed: the green words in the Client cell, Uninstall as the button beside them.
        Assert.Equal(SideCells.InstalledPrefix, Cell(pane, entry.Key, InstallModsPane.CellClient).Text);
        Assert.Equal(CellTone.OkBright, Cell(pane, entry.Key, InstallModsPane.CellClient).Tone);
        Assert.Equal(CellKind.Muted, Cell(pane, entry.Key, InstallModsPane.CellClient).Kind);
        Assert.Equal(SideCells.UninstallCaption, Cell(pane, entry.Key, InstallModsPane.CellClientA).Text);
        Assert.Equal(CellKind.Button, Cell(pane, entry.Key, InstallModsPane.CellClientA).Kind);
        Assert.Equal(new[] { "uninstall" }, (IReadOnlyList<string>)pane.ListingToWidget(entry)["actions"]!);
        Assert.Equal(false, pane.ListingToWidget(entry)["newTag"]);
        // MANAGED1: a ledgered row is ours, so it never carries the managed-elsewhere label.
        Assert.Equal("", pane.ListingToWidget(entry)["managed"]);
        Assert.Equal("", pane.ActionFor(entry).Managed);

        // No caption on any row, in any state, carries the retired tag.
        foreach (var cell in pane.Rows.SelectMany(r => r.Row.Cells))
            Assert.DoesNotContain(InstallModsCopy.NewTag, cell.Text);

        // Pending uninstall: orange text, and (INSTALLCOLS1) a button that says what it cancels.
        pane.UninstallPendingProbe = _ => true;
        rig.Rebuild();
        Assert.Equal(InstallModsCopy.StatusRemovedAtNextStart, pane.ListingToWidget(entry)["status"]);
        Assert.Equal(InstallModsCopy.StatusRemovedAtNextStart, Cell(pane, entry.Key, InstallModsPane.CellClient).Text.Replace("\n", " "));
        Assert.Equal(CellTone.Info, Cell(pane, entry.Key, InstallModsPane.CellClient).Tone);
        Assert.Equal(SideCells.CancelUninstallCaption, Cell(pane, entry.Key, InstallModsPane.CellClientA).Text);
        Assert.Equal(new[] { "reactivate" }, (IReadOnlyList<string>)pane.ListingToWidget(entry)["actions"]!);

        // Not installed: the single Install button, and an empty status cell holding the column.
        pane.UninstallPendingProbe = _ => false;
        pane.LedgerProbe = _ => false;
        pane.HotLedgerProbe = _ => false;
        pane.SetInstalledQuery(new InstalledStateQuery(Array.Empty<InstalledFile>(), loadedGeneration: "33"));
        rig.Rebuild();
        Assert.Equal(SideCells.NotInstalled, Cell(pane, entry.Key, InstallModsPane.CellClient).Text);
        Assert.Equal(SideCells.InstallCaption, Cell(pane, entry.Key, InstallModsPane.CellClientA).Text);
        Assert.Equal(new[] { "install" }, (IReadOnlyList<string>)pane.ListingToWidget(entry)["actions"]!);
        // Revert: putting the status word back in the button, or the `new` tag back in ActionFor,
        // fails this.
    }

    /// <summary>
    /// MANAGED1 (design decision): an installed copy the install ledger does not name - copied by
    /// hand, deployed by a script, or another manager's - offers no Uninstall. The button slot holds
    /// a muted, non-clickable "Managed elsewhere" label and the drawer says how to remove it, naming
    /// the files the scan found. What this would catch: an Uninstall button (or any action) on a row
    /// this manager did not install, the label drawn as a button, or the drawer note going missing.
    /// </summary>
    [Fact]
    public void An_installed_row_the_ledger_does_not_name_says_Managed_elsewhere_and_offers_no_Uninstall()
    {
        var bytes = new byte[] { 7, 8 };
        string sha = InstalledState.HashBytes(bytes);
        var catalog = new FlowCatalog();
        // The listing's own sha matches the scanned copy, so the scan finds it with no ledger row.
        var entry = new CatalogEntry(
            new ManifestMod("dyno.bags", "1.0.0", NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.None, false, "dyno", "dyno-Bags", sha, 2),
            CatalogProvenance.ManifestVerified, InstallReasons.SourceThunderstore, InstallReasons.SourceLabelThunderstore, "2026-09-11T00:00:00Z",
            displayName: "BagPlus", side: PackageSide.Client, owner: "dyno", packageName: "Bags", categories: new[] { "Client", "Oakveil Update" });
        catalog.Entries.Add(entry);
        var rig = new Rig(this, catalog);
        var pane = rig.Pane;
        pane.DialogPanelFactory = _ => NewPanel();
        pane.LedgerProbe = _ => false;
        pane.HotLedgerProbe = _ => false;
        string reload = Path.Combine(_root, "progenitor-reload", "bagplus.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(reload)!);
        File.WriteAllBytes(reload, bytes);
        pane.SetInstalledQuery(new InstalledStateQuery(
            new[] { new InstalledFile(reload, InstalledState.ReloadKind, sha) }, loadedGeneration: "33"));
        rig.Rebuild();

        // INSTALLCOLS1: the Status column's text says "Managed elsewhere" in the muted tone, across
        // the whole column; there is no button (the button cell stays in the model, hidden).
        // INSTALLSIDES1: the Client cell states the copy and who manages it, muted, with no button.
        var status = Cell(pane, entry.Key, InstallModsPane.CellClient);
        Assert.Equal("Installed · " + InstallModsCopy.ManagedElsewhere, status.Text.Replace("\n", " · "));
        Assert.Equal(CellTone.Muted, status.Tone);
        Assert.Equal(InstallColumns.SideColumnWidth, status.Width.Value, 1);
        var slot = Cell(pane, entry.Key, InstallModsPane.CellClientA);
        Assert.False(slot.Visible);
        Assert.Equal("", slot.Text);
        var readback = pane.ListingToWidget(entry);
        Assert.Empty((IReadOnlyList<string>)readback["actions"]!);
        Assert.Equal(InstallModsCopy.ManagedElsewhere, readback["managed"]);
        string reason = (string)readback["reason"]!;
        Assert.StartsWith(InstallModsCopy.ManagedElsewhereNote, reason, StringComparison.Ordinal);
        Assert.Contains(InstallModsCopy.ManagedElsewhereFilesPrefix + reload, reason, StringComparison.Ordinal);

        // Nothing to press; its drawer (the expand button) carries the note; nothing is removed.
        Assert.False(pane.OnInstallClick(entry.Key));
        Assert.False(pane.OnSideClick(entry.Key, server: false, SideButton.Uninstall));
        rig.ClickRow(entry.Key);
        Assert.Equal(InstallPromptKind.None, pane.Prompt);
        Assert.Equal(entry.Key, pane.SelectedKey);
        Assert.Contains(DrawerTexts(pane), t => t.StartsWith(InstallModsCopy.ManagedElsewhereNote, StringComparison.Ordinal));
        Assert.True(File.Exists(reload));

        // With no ledger view at all nothing is known about who installed it: plain Installed, no label.
        pane.LedgerProbe = null;
        rig.Rebuild();
        Assert.False(Cell(pane, entry.Key, InstallModsPane.CellClientA).Visible);
        Assert.Equal(SideCells.InstalledPrefix, Cell(pane, entry.Key, InstallModsPane.CellClient).Text);
        Assert.Equal("", pane.ListingToWidget(entry)["managed"]);
        // Revert: returning the old enabled:false Installed with no Managed label, or enabling
        // Uninstall for a row the ledger does not name, fails this.
    }

    /// <summary>
    /// MANAGED1: a plugin the Mods inventory has loaded with no Progenitor file on disk is
    /// managed-elsewhere. When the inventory names the other manager the label says
    /// <c>Managed by &lt;tool&gt;</c> if that fits the slot, else <c>Managed elsewhere</c>. With no
    /// files known the note is the explanation alone.
    /// </summary>
    [Fact]
    public void A_mod_another_tool_installed_offers_uninstall_when_the_preloader_can_act()
    {
        var catalog = new FlowCatalog();
        var entry = Listing("acme-HuntsAndGates", "HuntsAndGates", categories: new[] { "Client", "Oakveil Update" });
        catalog.Entries.Add(entry);
        var rig = new Rig(this, catalog);
        var pane = rig.Pane;
        pane.LedgerProbe = _ => false;
        pane.SetInstalledQuery(new InstalledStateQuery(
            inventory: new[] { new InstalledInventoryRow("acme-HuntsAndGates", "1.0.0", "Local") }));
        var pressed = new List<(string, string, bool)>();
        var offer = new InstallModsPane.PlainOffer { CanUninstall = true };
        InstallModsPane.PlainOfferProbe = id => id == "acme-HuntsAndGates" ? offer : null;
        InstallModsPane.PlainActionHandler = (id, name, cancel) => pressed.Add((id, name, cancel));
        try
        {
            // The preloader can name its files: Uninstall beside the Installed word, and the press
            // goes to the next-start flow (its own confirm), never the ledgered-install prompt.
            rig.Rebuild();
            Assert.Equal("Installed 1.0.0", Cell(pane, entry.Key, InstallModsPane.CellClient).Text);
            Assert.Equal(SideCells.UninstallCaption, Cell(pane, entry.Key, InstallModsPane.CellClientA).Text);
            Assert.Equal(CellKind.Button, Cell(pane, entry.Key, InstallModsPane.CellClientA).Kind);
            Assert.Equal(true, pane.ListingToWidget(entry)["plainUninstall"]);
            rig.ClickSide(entry.Key, server: false, SideCells.UninstallCaption);
            Assert.Equal(("acme-HuntsAndGates", false), (pressed[0].Item1, pressed[0].Item3));
            Assert.Equal(InstallPromptKind.None, pane.Prompt);

            // A removal waits for the next start: "Removed at next start" and (INSTALLCOLS1) an
            // explicit "Cancel uninstall", never a bare Cancel.
            offer = new InstallModsPane.PlainOffer { CanUninstall = true, RemovalPending = true };
            rig.Rebuild();
            Assert.Equal(InstallStatusLabels.RemovalPendingGameRestart, pane.ListingToWidget(entry)["status"]);
            Assert.Equal("Removal pending\ngame restart", Cell(pane, entry.Key, InstallModsPane.CellClient).Text);
            Assert.Equal("Cancel uninstall", Cell(pane, entry.Key, InstallModsPane.CellClientA).Text);
            Assert.Equal(InstallModsCopy.PlainCancel, Cell(pane, entry.Key, InstallModsPane.CellClientA).Text);
            rig.ClickSide(entry.Key, server: false, SideCells.CancelUninstallCaption);
            Assert.True(pressed[1].Item3, "Cancel");

            // After a start that could not remove it, the row says so plainly and offers Uninstall again.
            offer = new InstallModsPane.PlainOffer { CanUninstall = true, LastFailure = "Could not be removed at the last start: x changed." };
            rig.Rebuild();
            Assert.Equal("Could not be removed at the last start: x changed.", pane.ActionFor(entry).Reason);

            // Where it cannot act: Managed elsewhere, with why in words on the Status column's
            // second line (INSTALLCOLS1) and on the drawer note.
            offer = new InstallModsPane.PlainOffer { CanUninstall = false, Why = ModSwitch.ShortWhy(Progenitor.PluginActions.PluginFileActions.WhyShared("Other")) };
            rig.Rebuild();
            // INSTALLSIDES1: the Client cell names who manages it; the reason is the drawer note.
            Assert.Equal("Installed 1.0.0 · " + InstallModsCopy.ManagedElsewhere, Cell(pane, entry.Key, InstallModsPane.CellClient).Text);
            Assert.False(Cell(pane, entry.Key, InstallModsPane.CellClientA).Visible);
            Assert.StartsWith("Shares its folder with another mod. ", pane.ActionFor(entry).Reason, StringComparison.Ordinal);
            Assert.Equal("Another mod depends on it", ModSwitch.ShortWhy(Progenitor.PluginActions.PluginFileActions.WhyNeededBy("X")));
        }
        finally
        {
            InstallModsPane.PlainOfferProbe = null;
            InstallModsPane.PlainActionHandler = null;
        }
    }

    [Fact]
    public void A_managed_elsewhere_row_names_its_manager_when_it_fits()
    {
        var catalog = new FlowCatalog();
        var entry = Listing("acme-HuntsAndGates", "HuntsAndGates", categories: new[] { "Client", "Oakveil Update" });
        catalog.Entries.Add(entry);
        var rig = new Rig(this, catalog);
        var pane = rig.Pane;
        pane.LedgerProbe = _ => false;

        pane.SetInstalledQuery(new InstalledStateQuery(
            inventory: new[] { new InstalledInventoryRow("acme-HuntsAndGates", "1.0.0", "Local", null, "Gale") }));
        rig.Rebuild();
        Assert.Equal("Installed 1.0.0 · Managed by Gale", Cell(pane, entry.Key, InstallModsPane.CellClient).Text);
        Assert.Equal(CellKind.Muted, Cell(pane, entry.Key, InstallModsPane.CellClient).Kind);
        Assert.False(Cell(pane, entry.Key, InstallModsPane.CellClientA).Visible);
        Assert.Empty((IReadOnlyList<string>)pane.ListingToWidget(entry)["actions"]!);
        Assert.Equal(InstallModsCopy.ManagedElsewhereNote, pane.ActionFor(entry).Reason);

        // A name too long for the slot falls back; so does an unknown one.
        pane.SetInstalledQuery(new InstalledStateQuery(
            inventory: new[] { new InstalledInventoryRow("acme-HuntsAndGates", "1.0.0", "Local", null, "r2modman") }));
        rig.Rebuild();
        Assert.Equal("Installed 1.0.0 · " + InstallModsCopy.ManagedElsewhere, Cell(pane, entry.Key, InstallModsPane.CellClient).Text);
        pane.SetInstalledQuery(new InstalledStateQuery(
            inventory: new[] { new InstalledInventoryRow("acme-HuntsAndGates", "1.0.0", "Local") }));
        rig.Rebuild();
        Assert.Equal("Installed 1.0.0 · " + InstallModsCopy.ManagedElsewhere, Cell(pane, entry.Key, InstallModsPane.CellClient).Text);

        // Even a row the ledger names is managed elsewhere when none of its files is on disk and the
        // loaded copy came from somewhere else: there is nothing of ours to uninstall.
        pane.LedgerProbe = _ => true;
        rig.Rebuild();
        Assert.Equal("Installed 1.0.0 · " + InstallModsCopy.ManagedElsewhere, Cell(pane, entry.Key, InstallModsPane.CellClient).Text);
        Assert.Empty((IReadOnlyList<string>)pane.ListingToWidget(entry)["actions"]!);

        Assert.Equal(InstallModsCopy.ManagedElsewhere, InstallModsCopy.ManagedBy(null));
        Assert.Equal(InstallModsCopy.ManagedElsewhere, InstallModsCopy.ManagedBy("  "));
        Assert.True(InstallModsCopy.ManagedElsewhere.Length <= InstallModsCopy.InstallRestart.Length);
    }

    /// <summary>
    /// INSTALLUX2 item 2: clicking the row's Uninstall button is what opens the uninstall dialog -
    /// the same state machine INSTALL2 wrote, reached from a button that says what it does.
    /// What this would catch: the button writing on the press with no confirmation, or the click
    /// falling through to the install flow because the row's cell key moved.
    /// </summary>
    [Fact]
    public void The_Uninstall_button_opens_the_uninstall_dialog()
    {
        var bytes = new byte[] { 7, 7 };
        string sha = InstalledState.HashBytes(bytes);
        var catalog = new FlowCatalog();
        var entry = Listing("dyno-Bags", "BagPlus", categories: new[] { "Client", "Oakveil Update" });
        catalog.Entries.Add(entry);
        var rig = new Rig(this, catalog);
        var pane = rig.Pane;
        pane.DialogPanelFactory = _ => NewPanel();
        pane.LedgerProbe = _ => true;
        pane.HotLedgerProbe = _ => true;
        string reload = Path.Combine(_root, "progenitor-reload", "bagplus.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(reload)!);
        File.WriteAllBytes(reload, bytes);
        pane.SetInstalledQuery(new InstalledStateQuery(
            new[] { new InstalledFile(reload, InstalledState.ReloadKind, sha) }, loadedGeneration: "33"));
        rig.Rebuild();

        rig.ClickSide(entry.Key, server: false, SideCells.UninstallCaption);

        Assert.Equal(InstallPromptKind.Uninstall, pane.Prompt);
        Assert.Equal("uninstall", pane.PromptDialog!.Kind);
        Assert.Equal(
            pane.PromptHeadingText(entry, InstallPromptKind.Uninstall),
            pane.PromptDialog.Prompt.Heading!.Text);
        Assert.Equal(
            InstallModsCopy.PromptYesUninstall,
            pane.PromptDialog.Prompt.Buttons!.Cells.Single(c => c.Key == InstallModsPane.CellPromptYes).Text);
        Assert.Equal(0, catalog.ExecuteCalls);
        var asked = (Dictionary<string, object?>)pane.ActionToWidget(entry)["prompt"]!;
        Assert.Equal("uninstall", asked["kind"]);
        Assert.Equal("dialog", asked["presentation"]);
        // Revert: an Uninstall button that marks on the press leaves Prompt None here.
    }

    /// <summary>
    /// INSTALLUX2 item 3: the confirmation says <c>Client and server mod by ...</c> for a listing
    /// that carries both halves; Server and Client are untouched.
    /// <para>POLISH3 item D updated the last two asserts in place: the wording moved into
    /// <c>InstallSummary.SideWord(PackageSide)</c> itself, so the source of the word and the pane's
    /// seam now answer the same thing and the pane no longer overrides anything. The old assert
    /// pinned <c>InstallSummary.SideWord(PackageSide.Both) == "Both"</c>, which was the second
    /// spelling the fold removed.</para>
    /// What this would catch: either overload drifting back to "Both", which is the sentence the
    /// user saw ("Both mod by Progenitor"), or the two overloads disagreeing about the same side.
    /// </summary>
    [Fact]
    public void A_both_sided_listing_reads_Client_and_server_mod_by()
    {
        Assert.Equal("Client and server", InstallModsPane.PromptSideWord(PackageSide.Both));
        Assert.Equal("Server", InstallModsPane.PromptSideWord(PackageSide.Server));
        Assert.Equal("Client", InstallModsPane.PromptSideWord(PackageSide.Client));
        // POLISH3 item D: one answer, whichever overload is asked.
        Assert.Equal("Client and server", InstallSummary.SideWord(PackageSide.Both));
        Assert.Equal("Client and server", InstallSummary.SideWord(InstallTarget.Both));
        Assert.Equal(InstallSummary.BothSideWord, InstallSummary.SideWord(PackageSide.Both));
        Assert.Equal(
            "Client and server mod by Progenitor, version 0.1.0.",
            InstallSummary.HeadSentence(InstallSummary.SideWord(PackageSide.Both), "Progenitor", "0.1.0"));
        // Revert: putting "Both" back in either SideWord overload puts "Both mod by ..." in the box.
    }

    /// <summary>
    /// INSTALLUX1 item 1: "a Yes click before it finishes is honored when it finishes \u2026 no second
    /// click". One press, one write, and no write before the plan exists.
    /// </summary>
    [Fact]
    public async Task A_yes_pressed_while_the_package_is_still_being_checked_writes_once_when_it_lands()
    {
        var catalog = new FlowCatalog();
        var entry = Listing("dyno-Bags", "BagPlus", categories: new[] { "Client", "Oakveil Update" });
        catalog.Entries.Add(entry);
        catalog.Plans[entry.Key] = RestartPlan(entry, "h1");
        var rig = new Rig(this, catalog);
        var pane = rig.Pane;

        rig.Install(entry.Key);
        Assert.False(pane.PromptPlanReady);
        rig.ClickPromptYes(entry.Key);

        // Nothing is written yet, and the box has not changed.
        Assert.True(pane.PromptYesPending);
        Assert.Equal(0, catalog.ApproveCalls);
        Assert.Equal(0, catalog.ExecuteCalls);
        Assert.Equal(InstallPromptKind.Install, pane.Prompt);

        await rig.Complete(new InspectCallResult(true, null, entry, null));

        // Honored exactly once, and the box is gone.
        Assert.Equal(1, catalog.ApproveCalls);
        Assert.Equal(1, catalog.ExecuteCalls);
        Assert.Equal(InstallActionPhase.Done, pane.Phase);
        Assert.Equal(InstallPromptKind.None, pane.Prompt);
        Assert.False(pane.PromptYesPending);
        Assert.Equal(InstallStatusLabels.PendingGameRestart, Cell(pane, entry.Key, InstallModsPane.CellClient).Text);
        // Revert: dropping the _promptYesPending branch makes the early Yes a no-op (0 executes) and
        // honoring it twice makes ExecuteCalls 2.
    }

    /// <summary>
    /// INSTALLUX1 item 1: the body's side word is the LISTING's, never the target's. A Server
    /// listing installed to the client (no local server is configured) is still "Server mod", and
    /// the body says why it went to the client (item 2's fallback sentence).
    /// </summary>
    [Fact]
    public async Task The_body_says_the_listings_side_and_names_the_missing_local_server()
    {
        var catalog = new FlowCatalog();
        var entry = Listing("tovan-BossRoster", "BossRoster", categories: new[] { "Server", "Oakveil Update" }, version: "2.1.5");
        catalog.Entries.Add(entry);
        catalog.Plans[entry.Key] = RestartPlan(entry, "h1");
        var rig = new Rig(this, catalog);
        var pane = rig.Pane;

        rig.LegacyInstall(entry.Key);
        await rig.Complete(new InspectCallResult(true, null, entry, null));

        // No local server: Follow the mod's side sends a Server mod to the client and says so.
        Assert.Equal(InstallTarget.Client, pane.Target);
        Assert.Equal(true, pane.ActionToWidget(entry)["targetFallback"]);
        string body = pane.InstallPromptBody(entry);
        Assert.StartsWith("Server mod by tovan, version 2.1.5.", body);
        Assert.EndsWith(DefaultInstallTarget.NoLocalServerSentence, body);
        // Revert: passing the target's side word to InstallSummary.Describe reads "Client mod"
        // here; dropping the fallback sentence loses the end of the body.
    }

    /// <summary>
    /// INSTALLUX1 item 2: the four values of <c>[Install] DefaultTarget</c> and the fallback. Pure
    /// rule, no pane: the setting plus the listing's side plus "is a local server configured".
    /// </summary>
    [Fact]
    public void The_default_install_target_setting_resolves_every_value_and_falls_back_to_the_client()
    {
        // Follow the mod's side, with a server configured.
        Assert.Equal(
            InstallTarget.LocalServer,
            DefaultInstallTarget.Resolve(DefaultInstallTargetSetting.FollowSide, PackageSide.Server, true).Target);
        Assert.Equal(
            InstallTarget.Client,
            DefaultInstallTarget.Resolve(DefaultInstallTargetSetting.FollowSide, PackageSide.Client, true).Target);
        Assert.Equal(
            InstallTarget.Both,
            DefaultInstallTarget.Resolve(DefaultInstallTargetSetting.FollowSide, PackageSide.Both, true).Target);
        Assert.Equal(
            InstallTarget.Client,
            DefaultInstallTarget.Resolve(DefaultInstallTargetSetting.FollowSide, PackageSide.Unknown, true).Target);

        // The three pinned values.
        Assert.Equal(
            InstallTarget.Both,
            DefaultInstallTarget.Resolve(DefaultInstallTargetSetting.Both, PackageSide.Client, true).Target);
        Assert.Equal(
            InstallTarget.Client,
            DefaultInstallTarget.Resolve(DefaultInstallTargetSetting.Client, PackageSide.Server, true).Target);
        Assert.Equal(
            InstallTarget.LocalServer,
            DefaultInstallTarget.Resolve(DefaultInstallTargetSetting.Server, PackageSide.Client, true).Target);

        // With no local server every server-naming answer is the client, and says it fell back.
        foreach (var setting in DefaultInstallTarget.All)
        {
            var resolved = DefaultInstallTarget.Resolve(setting, PackageSide.Server, false);
            Assert.Equal(InstallTarget.Client, resolved.Target);
            Assert.Equal(setting != DefaultInstallTargetSetting.Client, resolved.FellBackToClient);
        }

        // A client answer never claims a fallback.
        Assert.False(DefaultInstallTarget.Resolve(DefaultInstallTargetSetting.Client, PackageSide.Client, false).FellBackToClient);

        // Parsing: the stored value, the caption, an unknown string and empty.
        Assert.Equal(DefaultInstallTargetSetting.Server, DefaultInstallTarget.Parse("localServer"));
        Assert.Equal(DefaultInstallTargetSetting.Server, DefaultInstallTarget.Parse(DefaultInstallTarget.LegacyLocalServerCaption));
        Assert.Equal(DefaultInstallTargetSetting.Server, DefaultInstallTarget.Parse("server"));
        Assert.Equal(DefaultInstallTargetSetting.Server, DefaultInstallTarget.Parse(DefaultInstallTarget.ServerCaption));
        Assert.Equal(DefaultInstallTargetSetting.FollowSide, DefaultInstallTarget.Parse("nonsense"));
        Assert.Equal(DefaultInstallTargetSetting.FollowSide, DefaultInstallTarget.Parse(""));
        Assert.Equal(DefaultInstallTargetSetting.FollowSide, DefaultInstallTarget.Default);
        // Revert: defaulting to Both, or resolving LocalServer without the configured-server check,
        // fails a line here.
    }

    /// <summary>
    /// INSTALLUX1 item 2: the pane installs by the setting, and the setting alone. Each value sends
    /// the same Client listing somewhere different, with no question asked in the box.
    /// </summary>
    [Theory]
    [InlineData("followSide", "client")]
    [InlineData("client", "client")]
    [InlineData("localServer", "localServer")]
    [InlineData("both", "both")]
    public async Task Every_setting_value_is_the_install_targets_whole_answer_and_no_chooser_is_drawn(
        string stored, string expected)
    {
        var setting = DefaultInstallTarget.Parse(stored);
        var catalog = new FlowCatalog();
        var entry = Listing("dyno-Bags", "BagPlus", categories: new[] { "Client", "Oakveil Update" });
        catalog.Entries.Add(entry);
        catalog.Plans[entry.Key] = RestartPlan(entry, "h1");
        var rig = new Rig(this, catalog, ValidatedFolder());
        var pane = rig.Pane;
        pane.DefaultTargetProbe = () => setting;

        rig.LegacyInstall(entry.Key);
        await rig.Complete(new InspectCallResult(true, null, entry, null));

        Assert.Equal(expected, pane.ActionToWidget(entry)["target"]);
        Assert.Equal(DefaultInstallTarget.ValueOf(setting), pane.ActionToWidget(entry)["targetSetting"]);
        // Item 2: nothing in the box asks where; the readback's chooser key is always false.
        Assert.False(pane.TargetChooserOpen);
        Assert.Equal(false, pane.ActionToWidget(entry)["chooser"]);
        var box = (IUiCellRow)pane.PromptHandles.Last();
        Assert.Equal(
            // DIALOGS1: the shared dialog layout centers the pair between two equal spacers.
            new[] { "dialog-left", InstallModsPane.CellPromptNo, InstallModsPane.CellPromptYes, "dialog-right" },
            box.Cells.Select(c => c.Key).ToArray());
        // No row keyed target:<key> exists on the panel at all.
        Assert.DoesNotContain(pane.PromptHandles.OfType<IUiCellRow>(), r => r.Key.StartsWith("target:", StringComparison.Ordinal));
        // Revert: restoring BuildTargetChooser puts a `target:<key>` row back into PromptHandles.
    }

    /// <summary>
    /// INSTALLUX1 item 4, the user: "buttons on the right are cropped". The header, every listing
    /// row, the confirmation's button row and the pending bar's rows all end one scrollbar band
    /// inside the row's right edge, at the two live resolutions.
    ///
    /// <para>At 3840\u00d71080 the pane's rows are 1484..2732 (width 1248) and the content edge is
    /// 2720; the numbers here are the widths, which is what the column model sees.</para>
    /// </summary>
    [Theory]
    [InlineData(1248f)]
    [InlineData(1108f)]
    public async Task Every_install_row_kind_ends_one_scrollbar_band_inside_the_row_edge(float rowWidth)
    {
        var catalog = new FlowCatalog();
        var entry = Listing("dyno-Bags", "BagPlus", categories: new[] { "Client", "Oakveil Update" });
        catalog.Entries.Add(entry);
        catalog.Plans[entry.Key] = RestartPlan(entry, "h1");
        var rig = new Rig(this, catalog);
        var pane = rig.Pane;
        rig.Install(entry.Key);
        await rig.Complete(new InspectCallResult(true, null, entry, null));
        // Snapshot the open confirmation's button row before Yes closes it.
        var openBox = (IUiCellRow)pane.PromptHandles.Last();
        var boxSpec = new CellRowSpec(openBox.Key, openBox.Cells.ToList(), rightInset: openBox.RightInset);
        rig.ClickPromptYes(entry.Key);

        float inset = HeadBlockLayout.RightColumnInset;
        float edge = rowWidth / 2f - inset;

        // 1. the column header.
        Assert.Equal(inset, pane.HeaderRow!.RightInset, 2);
        var header = MenuPanelLayout.CellColumns(rowWidth, new CellRowSpec(pane.HeaderRow!.Key, pane.HeaderRow!.Cells, rightInset: pane.HeaderRow!.RightInset));
        Assert.True(header.Where(c => c.Visible).All(c => c.Right <= edge + .01f), "a header cell runs past the content edge");
        Assert.Equal(edge, header.Where(c => c.Visible).Max(c => c.Right), 1);

        // 2. the listing row, whose Install button is the one the player said was cropped.
        var row = pane.Rows.Single(r => r.Row.Key == entry.Key).Row;
        Assert.Equal(inset, row.RightInset, 2);
        var cells = MenuPanelLayout.CellColumns(rowWidth, new CellRowSpec(row.Key, row.Cells, rightInset: row.RightInset));
        Assert.True(cells.Where(c => c.Visible).All(c => c.Right <= edge + .01f), "a listing cell runs past the content edge");
        // The row's last column ends there too.
        Assert.Equal(edge, cells.Where(c => c.Visible).Max(c => c.Right), 1);

        // 3. the pending bar's expanded row.
        var pending = pane.PendingBarHandles.OfType<IUiCellRow>().Single(r => r.Key != InstallModsPane.PendingRowKey);
        Assert.StartsWith(InstallModsPane.PendingItemKeyPrefix, pending.Key);
        Assert.Equal(inset, pending.RightInset, 2);
        var pendingCells = MenuPanelLayout.CellColumns(rowWidth, new CellRowSpec(pending.Key, pending.Cells, rightInset: pending.RightInset));
        Assert.Equal(edge, pendingCells.Single(c => c.Key == InstallModsPane.CellPendingState).Right, 1);

        // 4. the confirmation's No / Yes row, as it stood while the box was open. DIALOGS1: the box is
        // a dialog of its own, not a row in this pane, and its pair is centered in the dialog's row
        // (nothing near either clip edge), so it carries no inset.
        Assert.StartsWith(InstallModsPane.PromptKeyPrefix, boxSpec.Key);
        Assert.Equal(0f, boxSpec.RightInset, 2);
        var boxCells = MenuPanelLayout.CellColumns(DialogLayout.RowWidth, boxSpec);
        var no = boxCells.Single(c => c.Key == InstallModsPane.CellPromptNo);
        var yes = boxCells.Single(c => c.Key == InstallModsPane.CellPromptYes);
        Assert.Equal(0f, (no.Left + yes.Right) / 2f, 1);
        // Revert: dropping the rightInset from any of the first three rows puts its right-hand cell
        // back on rowWidth/2 \u2014 the clip boundary the scrollbar sits over \u2014 and fails that line.
    }

    /// <summary>
    /// INSTALLUX1 item 7, the user: "why are the images on the left black. Caching issue?". Live
    /// (deploy #71) 149 rows read <c>icon.state missing</c> on the scrolled-to screen while
    /// <c>progenitor-cache/icons/</c> held 102 files: the request covered only the first
    /// <c>ViewportRows + IconLookahead</c> rows and ran only from <c>Build</c>.
    ///
    /// <para>Now a scroll asks for the window around the row on screen, and every row outside it is
    /// asked through the DISK lane only \u2014 so a far row whose file is on disk becomes ready, and one
    /// whose file is not is never put on the network lane.</para>
    /// </summary>
    [Fact]
    public void A_scroll_requests_the_new_window_and_never_puts_a_far_row_on_the_network_lane()
    {
        var catalog = new FakeCatalog();
        for (int i = 0; i < 60; i++)
            catalog.Entries.Add(Listing(
                "o-p" + i.ToString("D2"), "Pack " + i, downloads: 1000 - i,
                categories: new[] { "Oakveil Update" }, version: "1.0." + i));
        var fetch = new FakeIconFetch();
        var decoder = new FakeDecoder();
        var icons = new IconCache(fetch.FetchAsync, decoder, new ImmediateClock(), action => action());
        // Only one far row's icon is on disk: row 40. Everything else misses.
        string onDisk = InstallModsPane.IconUrlFor(catalog.Entries[40])!;
        icons.DiskProbe = url => string.Equals(url, onDisk, StringComparison.Ordinal) ? new byte[] { 1, 2, 3, 4 } : null;

        var pane = new InstallModsPane(catalog, new NeverClock(), null);
        pane.SetIconCache(icons);
        pane.SetViewportRows(6);
        float offset = 0f;
        pane.ScrollOffsetProbe = () => offset;
        var panel = NewPanel();
        pane.Build(panel, true, false, null, () => { }, () => { }, DateTime.UtcNow);

        // At the top: the first 6 + 10 rows are the network window; the disk lane found row 40.
        Assert.Contains(InstallModsPane.IconUrlFor(catalog.Entries[0]), pane.RequestedIconUrls);
        Assert.Contains(InstallModsPane.IconUrlFor(catalog.Entries[15]), pane.RequestedIconUrls);
        Assert.DoesNotContain(InstallModsPane.IconUrlFor(catalog.Entries[30]), pane.RequestedIconUrls);
        Settle(() => icons.StateOf(onDisk) == IconState.Ready);
        Assert.Equal(1, icons.DiskHitCount);
        // Row 40 was never fetched: the disk lane answered it.
        Assert.DoesNotContain(onDisk, pane.RequestedIconUrls);

        // Scroll to row 30 and tell the pane the list moved.
        float pitch = CellLayout.TwoLineRowHeight + PanelLayout.RowGap;
        offset = 30f * pitch;
        Assert.Equal(30, pane.FirstVisibleRowIndex());
        UiReveal.RaiseScrolled();

        // The rows on the new screen were asked for, on the network lane.
        Assert.Contains(InstallModsPane.IconUrlFor(catalog.Entries[30]), pane.RequestedIconUrls);
        Assert.Contains(InstallModsPane.IconUrlFor(catalog.Entries[35]), pane.RequestedIconUrls);
        Assert.Contains(InstallModsPane.IconUrlFor(catalog.Entries[25]), pane.RequestedIconUrls);
        // Far outside it, in both directions, nothing new reached the network lane.
        Assert.DoesNotContain(InstallModsPane.IconUrlFor(catalog.Entries[59]), pane.RequestedIconUrls);
        Assert.DoesNotContain(InstallModsPane.IconUrlFor(catalog.Entries[50]), pane.RequestedIconUrls);
        // And a far row whose file was NOT on disk is still Missing, not Failed: the disk-only miss
        // retires the entry rather than queuing a fetch.
        string far = InstallModsPane.IconUrlFor(catalog.Entries[59])!;
        Settle(() => icons.StateOf(far) == IconState.Missing);
        Assert.Equal(IconState.Missing, icons.StateOf(far));
        pane.CancelIconRequests();
        icons.ReleaseAll();
        // Revert: making RequestVisibleIcons count from row 0 again leaves row 30 unrequested after
        // the scroll; dropping the DiskOnly branch in RunDiskAsync puts row 59 on the network lane.
    }

    // ---- Helpers --------------------------------------------------------------------------

    private static CellSpec Cell(InstallModsPane pane, string key, string cell) =>
        pane.Rows.Single(r => r.Row.Key == key).Row.Cells.Single(c => c.Key == cell);

    private IUiPanel NewPanel()
    {
        var panel = UiApi.Panel("Install mods", _owner, "example.list2");
        _panels.Add(panel);
        return panel;
    }

    private static void Settle(Func<bool> condition) =>
        Assert.True(SpinWait.SpinUntil(condition, 5000), "the step did not settle");

    private static void Drain(System.Collections.Concurrent.ConcurrentQueue<Action> posted)
    {
        while (posted.TryDequeue(out var action)) action();
    }

    private static ListingDocumentSet Docs(string key, string readme) =>
        new(key, "1.0.0", true, null, readme, true, null, "");

    private static InstallPlan RestartPlan(CatalogEntry entry, string hash) =>
        new(
            hash, entry, InstallTarget.Client, InstallDecision.RestartClientRequired,
            InstallReasons.RestartClient, Array.Empty<ArtifactHash>(), Array.Empty<PlanDependency>(),
            entry.Manifest.Id, entry.Manifest.Id + ".dll");

    internal static CatalogEntry Listing(
        string key,
        string display,
        string description = "",
        IReadOnlyList<string>? categories = null,
        long? downloads = null,
        string? releasedUtc = "2026-09-11T00:00:00Z",
        string? dateUpdated = null,
        string version = "1.0.0")
    {
        int dash = key.IndexOf('-');
        string ns = dash > 0 ? key.Substring(0, dash) : "n";
        string name = dash > 0 ? key.Substring(dash + 1) : key;
        return new CatalogEntry(
            new ManifestMod("", version, NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.None, false, ns, key),
            CatalogProvenance.AdaptedListing,
            InstallReasons.SourceThunderstore,
            InstallReasons.SourceLabelThunderstore,
            "2026-09-12T00:00:00Z",
            displayName: display,
            author: ns,
            description: description,
            selectedVersionFullName: key + "-" + version,
            downloadUrl: "https://thunderstore.io/package/download/" + ns + "/" + name + "/" + version + "/",
            owner: ns,
            packageName: name,
            categories: categories,
            dateUpdated: dateUpdated,
            releasedUtc: releasedUtc,
            downloads: downloads);
    }

    private static CatalogEntry Verified(string id, string display, bool hotLoadable, IReadOnlyList<string>? categories = null) =>
        new(
            new ManifestMod(id, "1.0.0", NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.None, hotLoadable, "example", id, new string('a', 64), 1),
            CatalogProvenance.ManifestVerified,
            InstallReasons.SourceThunderstore,
            InstallReasons.SourceLabelThunderstore,
            "2026-09-11T00:00:00Z",
            displayName: display,
            author: "example",
            side: PackageSide.Client,
            owner: "example",
            packageName: id,
            categories: categories,
            releasedUtc: "2026-09-11T00:00:00Z");

    private LocalServerFolder ValidatedFolder()
    {
        string path = Path.Combine(_root, "server");
        Directory.CreateDirectory(Path.Combine(path, LocalServerFolder.BepInExFolderName));
        File.WriteAllBytes(Path.Combine(path, LocalServerFolder.ServerExecutableName), new byte[] { 0 });
        var folder = new LocalServerFolder();
        var applied = folder.Apply(path);
        Assert.True(applied.Ok, applied.Refusal);
        Assert.True(folder.HasValidatedServer);
        return folder;
    }

    /// <summary>
    /// The pane mounted the way ModsMenu mounts it: one panel on the fake cell runtime,
    /// rebuilt inline on the rebuild hook (ModsMenu posts the same rebuild on the next Drain).
    /// Clicks go through the same Bootstrap slots the game dispatches.
    /// </summary>
    // ---- REMOTEINST1: the one Server target and the remote route --------------------------------

    private static RemoteInstallStatus RemoteStatus(bool admin = true, bool enabled = true, bool allowed = true,
        params RemoteServerRow[] rows) =>
        new(0, enabled, admin, allowed && admin && enabled, RemoteReachability.Reachable, "", allowed && admin && enabled, rows);

    private static (RemoteInstallClient Client, List<RemoteInstallRequest> Sent) RemoteClient(RemoteInstallStatus status)
    {
        var sent = new List<RemoteInstallRequest>();
        var client = new RemoteInstallClient
        {
            Send = bytes =>
            {
                Assert.True(RemoteInstallWire.TryDecodeRequest(bytes, out var request));
                sent.Add(request);
                return true;
            },
        };
        client.Accept(status);
        return (client, sent);
    }

    private static void Connect(InstallModsPane pane, RemoteInstallClient client, bool localConfigured = false, bool connectedIsLocal = false)
    {
        pane.RemoteServer = client;
        pane.ServerRouteProbe = () => ServerRouteSelector.Select(
            client.Inputs(localConfigured, connected: true, connectedIsLocal: connectedIsLocal, handshakeOk: true));
    }

    // ---- CONFIRMTARGET1/2: the confirmation names the server, as facts ------------------------

    private LocalServerFolder NamedFolder(string name)
    {
        var folder = ValidatedFolder();
        string settings = Path.Combine(folder.Current.CanonicalPath, "Settings");
        Directory.CreateDirectory(settings);
        File.WriteAllText(Path.Combine(settings, "ServerHostSettings.json"), "{ \"Name\": \"" + name + "\", \"Port\": 9976 }");
        return folder;
    }

    private static InstallPlan ServerRestartPlan(CatalogEntry entry, InstallTarget target, params OrdinaryDependency[] dependencies) =>
        new("h1", entry, target,
            target == InstallTarget.Both ? InstallDecision.RestartBothRequired : InstallDecision.RestartServerRequired,
            InstallReasons.RestartServer, Array.Empty<ArtifactHash>(), Array.Empty<PlanDependency>(),
            entry.Manifest.Id, entry.Manifest.Id + ".dll", ordinaryDependencies: dependencies);

    private static (string Label, string Value)[] Facts(InstallModsPane pane, CatalogEntry entry, InstallPromptKind kind) =>
        pane.PromptFacts(entry, kind).Select(f => (f.Label, f.Value)).ToArray();

    [Fact]
    public async Task A_local_server_install_shows_a_short_title_and_the_server_as_facts()
    {
        var catalog = new FlowCatalog();
        var entry = Listing("iTestTeam-RustyRepairs", "RustyRepairs", categories: new[] { "Server", "Oakveil Update" }, version: "1.2.2");
        catalog.Entries.Add(entry);
        catalog.Plans[entry.Key] = ServerRestartPlan(entry, InstallTarget.LocalServer,
            new OrdinaryDependency("dyno-ExampleCommandFramework", true, OrdinaryDependencies.BySatisfiedLedger, null));
        var folder = NamedFolder("Progenitor Forms Test");
        var rig = new Rig(this, catalog, folder);

        rig.InstallOnServer(entry.Key);
        await rig.Complete(new InspectCallResult(true, null, entry, null));
        Assert.Equal(InstallTarget.LocalServer, rig.Pane.Target);

        Assert.Equal("Install RustyRepairs 1.2.2 on Progenitor Forms Test?", rig.Pane.PromptHeadingText(entry, InstallPromptKind.Install));
        string path = ServerIdentity.MiddleEllipsis(folder.Current.CanonicalPath, ServerIdentity.FolderMax);
        Assert.Equal(new[]
        {
            (InstallModsCopy.FactMod, "RustyRepairs 1.2.2 by iTestTeam"),
            // REQRULE6: where it goes first, then which server and what kind.
            (InstallModsCopy.FactInstallsOn, InstallModsCopy.ServerOnly),
            (InstallModsCopy.FactServer, "Progenitor Forms Test (on this PC)"),
            (InstallModsCopy.FactFolder, path),
            (InstallModsCopy.FactTakesEffect, InstallModsCopy.EffectServerRestart),
            (InstallModsCopy.FactRequires, "ExampleCommandFramework — installed on that server"),
        }, Facts(rig.Pane, entry, InstallPromptKind.Install));
        Assert.Equal(DialogFactTone.Ok, rig.Pane.PromptFacts(entry, InstallPromptKind.Install).Last().Tone);
        Assert.Equal(InstallModsCopy.NoteServerRestart, rig.Pane.PromptNote(entry, InstallPromptKind.Install));
        Assert.Equal(InstallModsCopy.PromptYesInstall, rig.Pane.PromptYesText(InstallPromptKind.Install));

        // The dialog draws them: no body paragraph, one fact row each, the note, and the readback.
        var dialog = UiDialog.Current;
        Assert.NotNull(dialog);
        Assert.Equal("", dialog!.Content.Body);
        Assert.Equal(6, dialog.View!.FactRows.Count);
        Assert.NotNull(dialog.View.NoteRow);
        var described = UiDialog.Describe();
        var facts = Assert.IsType<List<Dictionary<string, object?>>>(described["facts"]);
        Assert.Equal("Installs on", facts[1]["label"]);
        Assert.Equal("The server only", facts[1]["value"]);
        Assert.Equal("Server", facts[2]["label"]);
        Assert.Equal("Progenitor Forms Test (on this PC)", facts[2]["value"]);
        Assert.Equal(InstallModsCopy.NoteServerRestart, described["note"]);
        var prompt = (Dictionary<string, object?>)rig.Pane.ActionToWidget(entry)["prompt"]!;
        Assert.Equal(6, ((List<Dictionary<string, object?>>)prompt["facts"]!).Count);
        Assert.Equal(false, prompt["update"]);
        // The bridge's sentence summary is unchanged in shape and names the server.
        Assert.StartsWith("Server: Progenitor Forms Test — local folder " + path + "\n", (string)prompt["body"]!);
        // Revert: ShowPromptDialog passing PromptBodyText as the body draws the old paragraph (Body non-empty).
    }

    [Fact]
    public async Task An_unnamed_local_server_reads_your_local_server_and_its_folder()
    {
        var catalog = new FlowCatalog();
        var entry = Listing("iTestTeam-RustyRepairs", "RustyRepairs", categories: new[] { "Server", "Oakveil Update" }, version: "1.2.2");
        catalog.Entries.Add(entry);
        catalog.Plans[entry.Key] = ServerRestartPlan(entry, InstallTarget.LocalServer);
        var folder = ValidatedFolder();
        var rig = new Rig(this, catalog, folder);

        rig.InstallOnServer(entry.Key);
        await rig.Complete(new InspectCallResult(true, null, entry, null));

        var facts = Facts(rig.Pane, entry, InstallPromptKind.Install);
        Assert.Contains((InstallModsCopy.FactServer, InstallModsCopy.LocalServerUnnamed + " (on this PC)"), facts);
        Assert.Contains((InstallModsCopy.FactFolder, ServerIdentity.MiddleEllipsis(folder.Current.CanonicalPath, ServerIdentity.FolderMax)), facts);
        Assert.DoesNotContain(facts, f => f.Label == InstallModsCopy.FactRequires);
        Assert.DoesNotContain(facts, f => f.Label == InstallModsCopy.FactAlsoOn);
    }

    [Fact]
    public async Task A_client_and_server_install_lists_this_game_under_also_on()
    {
        var catalog = new FlowCatalog();
        var entry = Listing("dyno-Bags", "BagPlus", categories: new[] { "Client", "Server", "Oakveil Update" }, version: "2.0.0");
        catalog.Entries.Add(entry);
        catalog.Plans[entry.Key] = ServerRestartPlan(entry, InstallTarget.Both,
            new OrdinaryDependency("dyno-ExampleCommandFramework", true, OrdinaryDependencies.BySatisfiedClosure, null));
        var folder = NamedFolder("Progenitor Forms Test");
        var rig = new Rig(this, catalog, folder);

        rig.ClickCell(entry.Key, InstallModsPane.CellBoth);
        await rig.Complete(new InspectCallResult(true, null, entry, null));
        Assert.Equal(InstallTarget.Both, rig.Pane.Target);

        Assert.Equal("Install BagPlus 2.0.0 on this game and Progenitor Forms Test?", rig.Pane.PromptHeadingText(entry, InstallPromptKind.Install));
        var facts = Facts(rig.Pane, entry, InstallPromptKind.Install);
        // F39 item 3: the Installs on line names both sides; no "Also on" repeats it.
        Assert.Contains((InstallModsCopy.FactInstallsOn, "This game and Progenitor Forms Test"), facts);
        Assert.DoesNotContain(facts, f => f.Label == InstallModsCopy.FactAlsoOn);
        Assert.Contains((InstallModsCopy.FactTakesEffect, InstallModsCopy.EffectBothRestart), facts);
        Assert.Contains((InstallModsCopy.FactRequires, "ExampleCommandFramework — " + InstallModsCopy.RequiresAlongside), facts);
        Assert.Equal(InstallModsCopy.NoteBothRestart, rig.Pane.PromptNote(entry, InstallPromptKind.Install));
        string body = rig.Pane.PromptBodyText(entry, InstallPromptKind.Install);
        Assert.StartsWith("On this game and on your local server Progenitor Forms Test (", body);
        Assert.EndsWith("Needs ExampleCommandFramework, which is not on this game or that server yet, so it will be installed alongside.", body);
    }

    /// <summary>
    /// F6 (live 2026-10-07): the client cell's install box of a both-sides mod on the local route offers
    /// [Yes, client only] and [Yes, install on both]. It names both sides the way the Server cell's box names its
    /// server (name, folder, when the server copy loads) and says which answer installs which side: the server only
    /// with the both answer, this client with either. Game-free; not live-verified.
    /// </summary>
    /// <summary>
    /// F39 (live 2026-10-08): "Yes, install on both" opened a second box ("Install HotDemo 0.1.0 on this game and …?") and
    /// only its Yes wrote both halves; in game a server notice covered that box and the client half was never written. The
    /// first box's answer is now the Yes: both halves are written at once, with no second box.
    /// </summary>
    [Fact]
    public async Task Yes_install_on_both_installs_both_halves_without_a_second_box()
    {
        var catalog = new FlowCatalog();
        var entry = Listing("dyno-Bags", "BagPlus", categories: new[] { "Client", "Server", "Oakveil Update" }, version: "2.0.0");
        catalog.Entries.Add(entry);
        catalog.Plans[entry.Key] = RestartPlan(entry, "h1");
        var rig = new Rig(this, catalog, NamedFolder("Progenitor Forms Test"));

        rig.Install(entry.Key);
        await rig.Complete(new InspectCallResult(true, null, entry, null));
        Assert.Equal(InstallModsCopy.PromptYesInstallBoth, Assert.Single(UiDialog.Current!.Content.ExtraButtons).Caption);
        Assert.True(UiCellRowHandle.TryInvoke(UiDialogs.ExtraRow(InstallModsPane.CellPromptBoth) + "/" + InstallModsPane.CellPromptBoth, out _));
        // The both-sides flow checks the package again; the Yes is already given and waits for that plan.
        Assert.True(rig.Pane.PromptYesPending);
        Assert.Equal(0, catalog.ExecuteCalls);
        await rig.Complete(new InspectCallResult(true, null, entry, null));

        Assert.Equal(InstallPromptKind.None, rig.Pane.Prompt);
        Assert.Equal(1, catalog.ExecuteCalls);
        Assert.Equal("h1-both", catalog.LastExecutedHash);
    }

    [Fact]
    public async Task A_client_cell_box_offering_both_names_each_side_and_which_answer_installs_it()
    {
        var catalog = new FlowCatalog();
        var entry = Listing("dyno-Bags", "BagPlus", categories: new[] { "Client", "Server", "Oakveil Update" }, version: "2.0.0");
        catalog.Entries.Add(entry);
        catalog.Plans[entry.Key] = RestartPlan(entry, "h1");
        var folder = NamedFolder("Progenitor Forms Test");
        var rig = new Rig(this, catalog, folder);

        rig.Install(entry.Key);
        await rig.Complete(new InspectCallResult(true, null, entry, null));
        Assert.Equal(InstallTarget.Client, rig.Pane.Target);
        var dialog = UiDialog.Current!;
        Assert.Equal(InstallModsCopy.PromptYesClientOnly, dialog.Content.MainCaption);
        Assert.Equal(InstallModsCopy.PromptYesInstallBoth, Assert.Single(dialog.Content.ExtraButtons).Caption);

        string path = ServerIdentity.MiddleEllipsis(folder.Current.CanonicalPath, ServerIdentity.FolderMax);
        Assert.Equal(new[]
        {
            (InstallModsCopy.FactMod, "BagPlus 2.0.0 by dyno"),
            (InstallModsCopy.FactInstallsOn, "This game only, or this game and Progenitor Forms Test with " + InstallModsCopy.PromptYesInstallBoth),
            (InstallModsCopy.FactServer, "Progenitor Forms Test (on this PC; only with " + InstallModsCopy.PromptYesInstallBoth + ")"),
            (InstallModsCopy.FactFolder, path),
            (InstallModsCopy.FactServerCopy, InstallModsCopy.EffectRemote),
            (InstallModsCopy.FactThisClient, InstallModsCopy.EffectGameRestart + " (both answers)"),
        }, Facts(rig.Pane, entry, InstallPromptKind.Install));
        // The dialog draws the same rows.
        Assert.Equal(6, dialog.View!.FactRows.Count);
        rig.Pane.PromptNo();

        // The Server cell's box is unchanged: its server, its folder, and this client under Not changed.
        rig.InstallOnServer(entry.Key);
        var serverFacts = Facts(rig.Pane, entry, InstallPromptKind.Install);
        Assert.Contains((InstallModsCopy.FactServer, "Progenitor Forms Test (on this PC)"), serverFacts);
        Assert.Contains((InstallModsCopy.FactFolder, path), serverFacts);
        Assert.DoesNotContain(serverFacts, f => f.Label == InstallModsCopy.FactThisClient);
        rig.Pane.PromptNo();
    }

    [Fact]
    public async Task Dependency_rows_say_installed_alongside_or_blocking_and_continue_under_one_label()
    {
        var catalog = new FlowCatalog();
        var entry = Listing("iTestTeam-RustyRepairs", "RustyRepairs", categories: new[] { "Server", "Oakveil Update" }, version: "1.2.2");
        catalog.Entries.Add(entry);
        catalog.Plans[entry.Key] = ServerRestartPlan(entry, InstallTarget.LocalServer,
            new OrdinaryDependency("dyno-ExampleCommandFramework", true, OrdinaryDependencies.BySatisfiedLedger, null),
            new OrdinaryDependency("orvik-KinsmanCommands", true, OrdinaryDependencies.BySatisfiedClosure, null),
            new OrdinaryDependency("owner-Missing", false, OrdinaryDependencies.BySatisfiedMissing, "not in the index"),
            new OrdinaryDependency("BepInEx-BepInExPack_V_Rising", true, OrdinaryDependencies.BySatisfiedBepInEx, null));
        var rig = new Rig(this, catalog, NamedFolder("Progenitor Forms Test"));

        rig.InstallOnServer(entry.Key);
        await rig.Complete(new InspectCallResult(true, null, entry, null));

        var requires = rig.Pane.PromptFacts(entry, InstallPromptKind.Install)
            .SkipWhile(f => f.Label != InstallModsCopy.FactRequires).ToArray();
        Assert.Equal(3, requires.Length); // the loader pack is not something the player installs
        Assert.Equal((InstallModsCopy.FactRequires, "ExampleCommandFramework — installed on that server", DialogFactTone.Ok),
            (requires[0].Label, requires[0].Value, requires[0].Tone));
        Assert.Equal(("", "KinsmanCommands — will be installed alongside", DialogFactTone.Normal),
            (requires[1].Label, requires[1].Value, requires[1].Tone));
        Assert.Equal(("", "Missing — missing (blocks the install)", DialogFactTone.Danger),
            (requires[2].Label, requires[2].Value, requires[2].Tone));
    }

    [Fact]
    public void A_remote_install_shows_the_address_and_names_it_in_its_progress()
    {
        var catalog = new FlowCatalog();
        var entry = Listing("tovan-BossRoster", "BossRoster", categories: new[] { "Server", "Oakveil Update" }, version: "2.1.5");
        catalog.Entries.Add(entry);
        var rig = new Rig(this, catalog);
        var (client, sent) = RemoteClient(RemoteStatus());
        Connect(rig.Pane, client);
        rig.Pane.ServerIdentityProbe = remote => remote
            ? ServerIdentity.Connected("", "203.0.113.5", 9876)
            : ServerIdentity.Local("", "");

        rig.Rebuild();
        rig.InstallOnServer(entry.Key);
        Assert.Equal("Install BossRoster 2.1.5 on 203.0.113.5:9876?", rig.Pane.PromptHeadingText(entry, InstallPromptKind.Install));
        Assert.Equal(new[]
        {
            (InstallModsCopy.FactMod, "BossRoster 2.1.5 by tovan"),
            (InstallModsCopy.FactInstallsOn, InstallModsCopy.ServerOnly),
            // REQRULE6: a remote server names its kind and its address, never a folder.
            (InstallModsCopy.FactServer, InstallModsCopy.ConnectedServerUnnamed + " (remote)"),
            (InstallModsCopy.FactAddress, "203.0.113.5:9876"),
            (InstallModsCopy.FactTakesEffect, InstallModsCopy.EffectRemote),
        }, Facts(rig.Pane, entry, InstallPromptKind.Install));
        Assert.Equal("", rig.Pane.PromptNote(entry, InstallPromptKind.Install));
        Assert.StartsWith("Server: connected at 203.0.113.5:9876\nServer mod by tovan, version 2.1.5.",
            rig.Pane.PromptBodyText(entry, InstallPromptKind.Install));

        rig.ClickPromptYes(entry.Key);
        var request = Assert.Single(sent);
        client.Accept(new RemoteInstallProgress(request.Nonce, RemoteInstallOp.Install, RemoteInstallStage.Downloading,
            RemoteInstallReason.None, entry.Key, "2.1.5", ""));
        rig.Pane.NoteRemoteChanged();
        Assert.Equal("Downloading on 203.0.113.5:9876…", rig.Pane.ActionFor(entry).Reason);

        client.Accept(new RemoteInstallProgress(request.Nonce, RemoteInstallOp.Install, RemoteInstallStage.PendingRestart,
            RemoteInstallReason.None, entry.Key, "2.1.5", ""));
        rig.Pane.NoteRemoteChanged();
        Assert.Equal(InstallListingState.Pending, rig.Pane.ActionFor(entry).State);
        Assert.StartsWith("Pending restart of 203.0.113.5:9876.", rig.Pane.ActionFor(entry).Reason);
        Assert.Equal("Pending restart of 203.0.113.5:9876 · to load", rig.Pane.PendingItemStateText(entry.Key, toLoad: true));
    }

    /// <summary>Review M2 (REQRULE6): a whole-row Cancel install of a copy pending on the connected server names that server.</summary>
    [Fact]
    public void A_whole_row_cancel_of_a_remote_pending_copy_names_the_server()
    {
        var catalog = new FlowCatalog();
        var entry = Listing("tovan-BossRoster", "BossRoster", categories: new[] { "Server", "Oakveil Update" }, version: "2.1.5");
        catalog.Entries.Add(entry);
        var rig = new Rig(this, catalog);
        var (client, _) = RemoteClient(RemoteStatus(rows: new RemoteServerRow(entry.Key, "2.1.5", RemoteRowState.Pending, ServerInstallLedger.RouteRemote)));
        Connect(rig.Pane, client);
        rig.Pane.ServerIdentityProbe = remote => ServerIdentity.Connected("", "203.0.113.5", 9876);
        rig.Pane.NoteRemoteChanged();
        Assert.Equal("Cancel installing BossRoster on 203.0.113.5:9876?", rig.Pane.PromptHeadingText(entry, InstallPromptKind.CancelInstall));
    }

    [Fact]
    public void A_remote_update_and_uninstall_read_as_update_and_uninstall()
    {
        var catalog = new FlowCatalog();
        var entry = Listing("tovan-BossRoster", "BossRoster", categories: new[] { "Server", "Oakveil Update" }, version: "2.1.5");
        catalog.Entries.Add(entry);
        var rig = new Rig(this, catalog);
        var (client, _) = RemoteClient(RemoteStatus(rows: new RemoteServerRow(entry.Key, "2.0.0", RemoteRowState.Loaded, ServerInstallLedger.RouteRemote)));
        Connect(rig.Pane, client);
        rig.Pane.ServerIdentityProbe = remote => ServerIdentity.Connected("", "203.0.113.5", 9876);
        rig.Pane.NoteRemoteChanged();
        Assert.Equal(InstallListingState.Update, rig.Pane.ActionFor(entry).State);

        rig.Rebuild();
        rig.ClickSide(entry.Key, server: true, SideCells.UpdateCaption);
        Assert.True(rig.Pane.IsUpdatePrompt);
        Assert.Equal("Update BossRoster to 2.1.5 on 203.0.113.5:9876?", rig.Pane.PromptHeadingText(entry, InstallPromptKind.Install));
        Assert.Equal(InstallModsCopy.PromptYesUpdate, rig.Pane.PromptYesText(InstallPromptKind.Install));
        Assert.Equal(true, ((Dictionary<string, object?>)rig.Pane.ActionToWidget(entry)["prompt"]!)["update"]);
        rig.Pane.PromptNo();
        Assert.False(rig.Pane.IsUpdatePrompt);

        client.Accept(RemoteStatus(rows: new RemoteServerRow(entry.Key, "2.1.5", RemoteRowState.Loaded, ServerInstallLedger.RouteRemote)));
        rig.Pane.NoteRemoteChanged();
        rig.Rebuild();
        rig.ClickSide(entry.Key, server: true, SideCells.UninstallCaption);
        Assert.Equal(InstallPromptKind.Uninstall, rig.Pane.Prompt);
        Assert.Equal("Uninstall BossRoster from 203.0.113.5:9876?", rig.Pane.PromptHeadingText(entry, InstallPromptKind.Uninstall));
        Assert.Equal(InstallModsCopy.PromptYesUninstall, rig.Pane.PromptYesText(InstallPromptKind.Uninstall));
        Assert.Equal(new[]
        {
            (InstallModsCopy.FactMod, "BossRoster 2.1.5 by tovan"),
            (InstallModsCopy.FactRemovesFrom, InstallModsCopy.ServerOnly),
            (InstallModsCopy.FactServer, InstallModsCopy.ConnectedServerUnnamed + " (remote)"),
            (InstallModsCopy.FactAddress, "203.0.113.5:9876"),
            // F33 (live 2026-10-08): the status says the copy loaded at the server's start, so it goes at its next restart.
            (InstallModsCopy.FactTakesEffect, InstallModsCopy.EffectServerRestart),
            (InstallModsCopy.FactSettings, InstallModsCopy.SettingsKeptValue),
        }, Facts(rig.Pane, entry, InstallPromptKind.Uninstall));
        Assert.Equal("", rig.Pane.PromptNote(entry, InstallPromptKind.Uninstall));
    }

    /// <summary>
    /// F33 (live 2026-10-08): remote boxes read the effect from the server's status (a hot copy: now), and an install from
    /// the server's own local index names that source. Game-free; not live-verified.
    /// </summary>
    [Fact]
    public void Remote_boxes_take_the_effect_from_the_servers_status_and_name_its_local_source()
    {
        RemoteServerRow Row(RemoteRowState state) => new("o-X", "1.0.0", state, ServerInstallLedger.RouteRemote);
        Assert.Equal(InstallModsCopy.EffectUnloadNow, InstallModsPane.RemoteEffect(Row(RemoteRowState.Hot), uninstall: true));
        Assert.Equal(InstallModsCopy.EffectNow, InstallModsPane.RemoteEffect(Row(RemoteRowState.Hot), uninstall: false));
        Assert.Equal(InstallModsCopy.EffectServerRestart, InstallModsPane.RemoteEffect(Row(RemoteRowState.Loaded), uninstall: true));
        Assert.Equal(InstallModsCopy.EffectRemoteCancelsPending, InstallModsPane.RemoteEffect(Row(RemoteRowState.Pending), uninstall: true));
        Assert.Equal(InstallModsCopy.EffectRemote, InstallModsPane.RemoteEffect(null, uninstall: false));
        Assert.Equal("Night Garden's local test source (0.2.0)", InstallModsCopy.ServerLocalSourceFact("Night Garden", "0.2.0"));
        string src = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "InstallModsPane.cs");
        Assert.Contains("AddRemoteSource(entry, server, facts);", src, StringComparison.Ordinal);
    }

    [Fact]
    public void An_admin_on_a_remote_capable_server_installs_a_server_mod_on_that_server()
    {
        var catalog = new FlowCatalog();
        var entry = Listing("tovan-BossRoster", "BossRoster", categories: new[] { "Server", "Oakveil Update" }, version: "2.1.5");
        catalog.Entries.Add(entry);
        var rig = new Rig(this, catalog);
        var (client, sent) = RemoteClient(RemoteStatus());
        Connect(rig.Pane, client);

        var resolved = rig.Pane.ResolvedTargetFor(entry);
        Assert.True(resolved.ToConnectedServer);
        Assert.False(resolved.LocalInstall);
        Assert.Equal(ServerRouteKind.Remote, rig.Pane.CurrentRoute.Kind);

        rig.Rebuild();
        rig.InstallOnServer(entry.Key);
        Assert.Equal(InstallPromptKind.Install, rig.Pane.Prompt);
        // Nothing is downloaded here: the server fetches it itself.
        Assert.Equal(0, catalog.DownloadCalls);
        Assert.Contains(InstallModsCopy.RemoteInstallBody, rig.Pane.InstallPromptBody(entry));

        rig.ClickPromptYes(entry.Key);
        var request = Assert.Single(sent);
        Assert.Equal(RemoteInstallOp.Install, request.Op);
        Assert.Equal("tovan-BossRoster", request.Package);
        Assert.Equal("2.1.5", request.Version);
        Assert.Equal(0, catalog.ExecuteCalls);

        client.Accept(new RemoteInstallProgress(request.Nonce, RemoteInstallOp.Install, RemoteInstallStage.Downloading,
            RemoteInstallReason.None, entry.Key, "2.1.5", ""));
        rig.Rebuild();
        Assert.Equal(InstallStatusLabels.Downloading, Cell(rig.Pane, entry.Key, InstallModsPane.CellServer).Text!.Replace("\n", " "));
        Assert.Equal(SideCells.CancelInstallCaption, Cell(rig.Pane, entry.Key, InstallModsPane.CellServerA).Text);

        client.Accept(new RemoteInstallProgress(request.Nonce, RemoteInstallOp.Install, RemoteInstallStage.Failed,
            RemoteInstallReason.ThunderstoreUnreachable, entry.Key, "2.1.5", "timeout"));
        rig.Pane.NoteRemoteChanged();
        var action = rig.Pane.ActionFor(entry);
        Assert.Equal(InstallStatusLabels.CantReachThunderstore, action.StatusText);
        Assert.Equal("The server can't reach Thunderstore (timed out). Your host may block outbound connections.", action.Reason);
    }

    [Fact]
    public void A_server_copy_reads_its_route_and_uninstalls_through_it()
    {
        var catalog = new FlowCatalog();
        var entry = Listing("tovan-BossRoster", "BossRoster", categories: new[] { "Server", "Oakveil Update" }, version: "2.1.5");
        catalog.Entries.Add(entry);
        var rig = new Rig(this, catalog);
        var (client, sent) = RemoteClient(RemoteStatus(rows: new RemoteServerRow(entry.Key, "2.1.5", RemoteRowState.Loaded, ServerInstallLedger.RouteRemote)));
        Connect(rig.Pane, client);
        rig.Pane.NoteRemoteChanged();

        rig.Rebuild();
        Assert.Equal("Installed 2.1.5", Cell(rig.Pane, entry.Key, InstallModsPane.CellServer).Text);
        Assert.Equal(SideCells.UninstallCaption, Cell(rig.Pane, entry.Key, InstallModsPane.CellServerA).Text);
        Assert.Equal(SideCells.NoClientSide, Cell(rig.Pane, entry.Key, InstallModsPane.CellClient).Text);

        rig.ClickSide(entry.Key, server: true, SideCells.UninstallCaption);
        Assert.Equal(InstallPromptKind.Uninstall, rig.Pane.Prompt);
        // CONFIRMTARGET1: the server line first; with no join record the server is "the connected server".
        Assert.StartsWith("Server: the connected server\n" + InstallModsCopy.RemoteUninstallBody, rig.Pane.PromptBodyText(entry, InstallPromptKind.Uninstall));
        rig.ClickPromptYes(entry.Key);
        var request = Assert.Single(sent);
        Assert.Equal(RemoteInstallOp.Uninstall, request.Op);
        Assert.Equal(entry.Key, request.Package);
        Assert.Equal(InstallListingState.Installing, rig.Pane.ActionFor(entry).State);
        client.Accept(new RemoteInstallProgress(request.Nonce, RemoteInstallOp.Uninstall, RemoteInstallStage.Removed,
            RemoteInstallReason.None, entry.Key, "", ""));

        // A row the local folder route wrote says so; an older version on the server offers Update.
        client.Accept(RemoteStatus(rows: new RemoteServerRow(entry.Key, "2.0.0", RemoteRowState.Loaded, "")));
        var update = rig.Pane.ActionFor(entry);
        Assert.Equal(InstallListingState.Update, update.State);
        Assert.Equal(ServerRouteCopy.InstalledLocal, update.StatusText);
        Assert.Contains("2.0.0", update.Reason);
    }

    [Fact]
    public void Without_admin_the_server_row_says_why_and_install_still_falls_back_to_the_client()
    {
        var catalog = new FlowCatalog();
        var entry = Listing("tovan-BossRoster", "BossRoster", categories: new[] { "Server", "Oakveil Update" }, version: "2.1.5");
        catalog.Entries.Add(entry);
        var rig = new Rig(this, catalog);
        var (client, sent) = RemoteClient(RemoteStatus(admin: false));
        Connect(rig.Pane, client);
        rig.Pane.NoteRemoteChanged();

        Assert.Equal(ServerRouteKind.None, rig.Pane.CurrentRoute.Kind);
        Assert.Equal(ServerRouteBlock.NotAdmin, rig.Pane.CurrentRoute.Block);
        rig.Rebuild();
        // Not on its admin list: the server shares no rows with this player, so its copy is unknown.
        // INSTALLSIDES2 item 3: the state stays readable (not grayed); the button an admin would get
        // is one dim, disabled Admin only in the Server column (its lock drawn only if the font has it).
        Assert.Equal(SideCells.UnknownWord, Cell(rig.Pane, entry.Key, InstallModsPane.CellServer).Text!.Replace("\n", " · "));
        Assert.False(Cell(rig.Pane, entry.Key, InstallModsPane.CellServer).Grayed);
        var locked = Cell(rig.Pane, entry.Key, InstallModsPane.CellServerA);
        Assert.True(locked.Visible);
        Assert.Equal(SideCells.AdminOnlyCaption, locked.Text);
        Assert.Equal(CellTone.Disabled, locked.Tone);
        Assert.Equal(CellColumn.Server, locked.Column);
        Assert.Equal(SideCells.LockGlyph, locked.Glyph);
        Assert.False(rig.Pane.OnSideClick(entry.Key, server: true, SideButton.AdminOnly));
        Assert.Equal(InstallStatusLabels.NeedsAdminList, rig.Pane.ActionFor(entry).StatusText);
        Assert.Contains("adminlist.txt", rig.Pane.CurrentRoute.Help);
        Assert.Contains("adminauth", rig.Pane.CurrentRoute.Help);

        var resolved = rig.Pane.ResolvedTargetFor(entry);
        Assert.False(resolved.ToConnectedServer);
        Assert.True(resolved.FellBackToClient);
        Assert.Equal(InstallTarget.Client, resolved.Target);
        Assert.EndsWith(ServerRouteCopy.NotAdmin + "; installing to my client.", InstallModsPane.FallbackSentence(resolved.Route));
        Assert.Empty(sent);

        // A client-side row carries no note.
        var clientEntry = Listing("team-Hud", "Hud", categories: new[] { "Client" });
        Assert.Equal("", rig.Pane.ActionFor(clientEntry).StatusText);
    }

    [Fact]
    public void The_local_folder_route_wins_when_connected_to_that_server_and_nothing_changes_without_a_channel()
    {
        var catalog = new FlowCatalog();
        var entry = Listing("tovan-BossRoster", "BossRoster", categories: new[] { "Server", "Oakveil Update" }, version: "2.1.5");
        catalog.Entries.Add(entry);
        var rig = new Rig(this, catalog);
        // No channel and no probe: exactly the pre-REMOTEINST1 answer (no folder => no server).
        Assert.Equal(ServerRouteKind.None, rig.Pane.CurrentRoute.Kind);
        Assert.Equal(ServerRouteBlock.NoServer, rig.Pane.CurrentRoute.Block);
        Assert.Equal("", rig.Pane.ActionFor(entry).StatusText);

        var (client, _) = RemoteClient(RemoteStatus());
        Connect(rig.Pane, client, localConfigured: true, connectedIsLocal: true);
        Assert.Equal(ServerRouteKind.Local, rig.Pane.CurrentRoute.Kind);
        var resolved = rig.Pane.ResolvedTargetFor(entry);
        Assert.False(resolved.ToConnectedServer);
        Assert.Equal(InstallTarget.LocalServer, resolved.Target);
    }

    // ---- REMOTEINST1: per-side status ----------------------------------------------------------

    private static CatalogEntry DeclaredBoth(string key, string version = "1.0.0")
    {
        var listing = Listing(key, key, categories: new[] { "Server", "Client", "Oakveil Update" }, version: version);
        return listing.WithInspection(CatalogProvenance.ManifestVerified, listing.Manifest, PackageSide.Both, Array.Empty<PluginDependency>());
    }

    [Fact]
    public void A_declared_both_sided_mod_on_the_server_only_says_missing_on_client()
    {
        var catalog = new FlowCatalog();
        var entry = DeclaredBoth("team-Pair");
        catalog.Entries.Add(entry);
        var rig = new Rig(this, catalog);
        var (client, _) = RemoteClient(RemoteStatus(rows: new RemoteServerRow(entry.Key, "1.0.0", RemoteRowState.Loaded, ServerInstallLedger.RouteRemote)));
        Connect(rig.Pane, client);

        var action = rig.Pane.ActionFor(entry);
        Assert.Equal(InstallListingState.Sides, action.State);
        Assert.Equal(SideStatusRules.MissingOnClient, action.StatusText);
        Assert.Equal(SideStatusRules.InstallOnClientCaption, InstallModsPane.RowButtonText(action));
        Assert.Equal(CellTone.Warn, InstallModsPane.RowStatusTone(action));
        var sides = rig.Pane.SidesToWidget(entry);
        Assert.Equal("absent", sides["client"]);
        Assert.Equal("installed", sides["server"]);
        Assert.Equal("remote", sides["serverSource"]);
        Assert.Equal(true, sides["needsBoth"]);
        Assert.Equal(false, sides["mismatch"]);
        Assert.Equal("Removes it from the server (remote).", rig.Pane.UninstallSidesSentence(entry));
    }

    [Fact]
    public void A_mod_only_tagged_server_and_client_gets_no_missing_warning()
    {
        var catalog = new FlowCatalog();
        var entry = Listing("team-Tagged", "Tagged", categories: new[] { "Server", "Client", "Oakveil Update" });
        catalog.Entries.Add(entry);
        var rig = new Rig(this, catalog);
        var (client, _) = RemoteClient(RemoteStatus(rows: new RemoteServerRow(entry.Key, "1.0.0", RemoteRowState.Loaded, ServerInstallLedger.RouteRemote)));
        Connect(rig.Pane, client);

        var action = rig.Pane.ActionFor(entry);
        Assert.NotEqual(InstallListingState.Sides, action.State);
        Assert.NotEqual(SideStatusRules.MissingOnClient, action.StatusText);
        var sides = rig.Pane.SidesToWidget(entry);
        Assert.Equal(false, sides["needsBoth"]);
        Assert.Equal("installed", sides["server"]); // still shown as information
        Assert.Equal("", sides["label"]);
    }

    [Fact]
    public void A_server_half_without_a_route_is_named_as_left_in_place()
    {
        var catalog = new FlowCatalog();
        var entry = DeclaredBoth("team-Pair");
        catalog.Entries.Add(entry);
        var rig = new Rig(this, catalog);
        var (client, _) = RemoteClient(RemoteStatus(admin: false));
        Connect(rig.Pane, client);
        rig.Pane.ServerHandshakeProbe = _ => (true, "1.0.0");

        Assert.Equal("handshake", rig.Pane.SidesToWidget(entry)["serverSource"]);
        Assert.Equal("Left in place: the server (no route to it from here).", rig.Pane.UninstallSidesSentence(entry));
    }

    // ---- UI polish (2026-10-01): where-captions and the pending action row ---------------------

    [Fact]
    public void Install_buttons_say_where_and_the_status_says_the_restart()
    {
        var local = new ServerRoute(ServerRouteKind.Local, ServerRouteBlock.None);
        var remote = new ServerRoute(ServerRouteKind.Remote, ServerRouteBlock.None);
        string Caption(DefaultInstallTargetSetting setting, PackageSide side, ServerRoute route, bool update = false) =>
            InstallModsPane.WhereCaption(DefaultInstallTarget.Resolve(setting, side, route), update);

        Assert.Equal(InstallModsCopy.Install, Caption(DefaultInstallTargetSetting.FollowSide, PackageSide.Client, local));
        Assert.Equal(InstallModsCopy.InstallOnServer, Caption(DefaultInstallTargetSetting.FollowSide, PackageSide.Server, local));
        Assert.Equal(InstallModsCopy.InstallOnServer, Caption(DefaultInstallTargetSetting.FollowSide, PackageSide.Server, remote));
        Assert.Equal(InstallModsCopy.InstallOnBoth, Caption(DefaultInstallTargetSetting.FollowSide, PackageSide.Both, local));
        Assert.Equal(InstallModsCopy.InstallOnBoth, Caption(DefaultInstallTargetSetting.FollowSide, PackageSide.Both, remote));
        Assert.Equal(InstallModsCopy.UpdateOnServer, Caption(DefaultInstallTargetSetting.Server, PackageSide.Client, remote, update: true));
        Assert.Equal(InstallModsCopy.UpdateHere, Caption(DefaultInstallTargetSetting.Client, PackageSide.Server, remote, update: true));
        // No server route: a server mod installs to the client, and the button says so.
        Assert.Equal(InstallModsCopy.Install,
            Caption(DefaultInstallTargetSetting.FollowSide, PackageSide.Server, new ServerRoute(ServerRouteKind.None, ServerRouteBlock.NoServer)));
        // INSTALLSIDES1: the row's buttons are per side, so they say what they do and the cell says
        // where; the shortcut names both.
        foreach (string caption in new[] { SideCells.InstallBothCaption, SideCells.UpdateBothCaption })
            Assert.True(CellLayout.EstimateTextWidth(caption, InstallColumns.ShortcutFontSize) <= InstallColumns.ShortcutWidth - 8f, caption);
    }

    [Fact]
    public void A_remote_pending_copy_raises_the_action_row_naming_the_connected_server()
    {
        var catalog = new FlowCatalog();
        var entry = Listing("tovan-BoneDrops", "BoneDrops", categories: new[] { "Server", "Oakveil Update" }, version: "1.0.0");
        catalog.Entries.Add(entry);
        var rig = new Rig(this, catalog);
        var (client, _) = RemoteClient(RemoteStatus(rows: new RemoteServerRow(entry.Key, "1.0.0", RemoteRowState.Pending, ServerInstallLedger.RouteRemote)));
        Connect(rig.Pane, client);
        bool restarted = false;
        rig.Pane.RemoteRestartRequest = _ => restarted = true;
        rig.Pane.DialogPanelFactory = _ => NewPanel();

        Assert.Equal(1, rig.Pane.PendingCount());
        Assert.Equal(InstallModsCopy.SidesServer, rig.Pane.PendingSides());
        Assert.Equal(InstallModsCopy.StopServerToApply, rig.Pane.PendingRestartCaption(rig.Pane.PendingSides()));
        rig.Rebuild();
        Assert.Equal("1 mod installed — restart the connected server to apply. »", rig.Pane.PendingBarText);
        rig.ClickCell(InstallModsPane.PendingRowKey, InstallModsPane.CellPendingRestart);
        // F20: the press opens the confirmation; only its yes sends.
        Assert.False(restarted);
        Assert.NotNull(rig.Pane.StopDialog);
        rig.ClickCell(InstallModsPane.StopDialogRowKey, InstallModsPane.CellStopYes);
        Assert.True(restarted);
        Assert.Equal("1 mod installed — restart the local server to apply. »", InstallModsCopy.PendingBar(1, 0, InstallModsCopy.SidesServer, remote: false));
    }

    // ---- F20: Stop server to apply — confirmation, one request, its answer ---------------------

    /// <summary>F20: the replies the fake server gave, by nonce (a private inbox, so parallel tests never share one).</summary>
    private readonly Dictionary<int, StopServerReply> _stopReplies = new();

    private void Answer(StopServerReply reply) => _stopReplies[reply.Nonce] = reply;

    private (Rig Rig, CatalogEntry Entry, List<int> Sent) RemoteStopRig(IReadOnlyList<string>? players = null)
    {
        var catalog = new FlowCatalog();
        var entry = Listing("tovan-BoneDrops", "BoneDrops", categories: new[] { "Server", "Oakveil Update" }, version: "1.0.0");
        catalog.Entries.Add(entry);
        var rig = new Rig(this, catalog);
        var (client, _) = RemoteClient(RemoteStatus(rows: new RemoteServerRow(entry.Key, "1.0.0", RemoteRowState.Pending, ServerInstallLedger.RouteRemote)));
        Connect(rig.Pane, client);
        var sent = new List<int>();
        rig.Pane.RemoteRestartRequest = nonce => { sent.Add(nonce); return true; };
        rig.Pane.DialogPanelFactory = _ => NewPanel();
        rig.Pane.OnlinePlayersProbe = () => players;
        rig.Pane.StopReplyProbe = nonce => _stopReplies.TryGetValue(nonce, out var r) ? r : null;
        rig.Rebuild();
        return (rig, entry, sent);
    }

    private static IUiCellRow PendingRow(InstallModsPane pane) => (IUiCellRow)pane.PendingBarHandles[0];

    private static CellSpec PendingCell(InstallModsPane pane, string key) => PendingRow(pane).Cells.Single(c => c.Key == key);

    [Fact]
    public void Stop_server_to_apply_confirms_naming_the_server_the_players_and_how_it_starts_again()
    {
        var (rig, _, sent) = RemoteStopRig(new[] { "Alpha", "Beta" });
        rig.ClickCell(InstallModsPane.PendingRowKey, InstallModsPane.CellPendingRestart);

        var dialog = rig.Pane.StopDialog;
        Assert.NotNull(dialog);
        Assert.Empty(sent);
        var facts = dialog!.Content.Facts.Select(f => (f.Label, f.Value)).ToArray();
        Assert.Contains((InstallModsCopy.StopFactPlayers, "2 (this client's list, you included): Alpha, Beta"), facts);
        Assert.Contains((InstallModsCopy.StopFactStartsAgain, InstallModsCopy.StartsAgainRemote), facts);
        Assert.Contains(facts, f => f.Label == InstallModsCopy.StopFactServer && f.Value.Length > 0);
        Assert.Equal(InstallModsCopy.StopServerNote, dialog.Content.Note);
        Assert.Equal(InstallModsCopy.StopServerYes, dialog.Content.MainCaption);
        Assert.StartsWith("Stop ", dialog.Content.Title);

        rig.ClickCell(InstallModsPane.StopDialogRowKey, InstallModsPane.CellStopNo);
        Assert.Empty(sent);
        Assert.Equal(StopRequestPhase.Idle, rig.Pane.StopRequest.Phase);
        // Revert: send on the button press again and a stray click stops a server with players on it.
    }

    [Fact]
    public void Players_the_client_cannot_see_are_said_to_be_unknown()
    {
        Assert.Equal(InstallModsCopy.PlayersUnknown, InstallModsCopy.PlayerCounter(null));
        Assert.Equal(InstallModsCopy.PlayersNone, InstallModsCopy.PlayerCounter(Array.Empty<string>()));
        var many = Enumerable.Range(1, 9).Select(i => "P" + i).ToArray();
        Assert.Equal("9 (this client's list, you included): P1, P2, P3, P4, P5, P6 and 3 more", InstallModsCopy.PlayerCounter(many));

        var (rig, _, _) = RemoteStopRig(players: null);
        rig.ClickCell(InstallModsPane.PendingRowKey, InstallModsPane.CellPendingRestart);
        Assert.Contains(rig.Pane.StopDialog!.Content.Facts, f => f.Label == InstallModsCopy.StopFactPlayers && f.Value == InstallModsCopy.PlayersUnknown);
    }

    [Fact]
    public void One_request_in_flight_disables_the_button_and_a_second_press_sends_nothing()
    {
        var (rig, _, sent) = RemoteStopRig();
        rig.ClickCell(InstallModsPane.PendingRowKey, InstallModsPane.CellPendingRestart);
        rig.ClickCell(InstallModsPane.StopDialogRowKey, InstallModsPane.CellStopYes);

        Assert.Single(sent);
        Assert.True(rig.Pane.StopRequest.InFlight);
        Assert.Equal(InstallModsCopy.StoppingServer, PendingCell(rig.Pane, InstallModsPane.CellPendingRestart).Text);
        Assert.Equal(CellTone.Disabled, PendingCell(rig.Pane, InstallModsPane.CellPendingRestart).Tone);
        Assert.StartsWith("Asking ", PendingCell(rig.Pane, InstallModsPane.CellPendingText).Text);

        // The bridge refuses the grayed button (live 2026-10-14); a press that reaches its slot anyway sends nothing.
        Assert.False(UiCellRowHandle.TryInvoke(InstallModsPane.PendingRowKey + "/" + InstallModsPane.CellPendingRestart, out var refusedDetail));
        Assert.NotNull(UiClickGate.RefusalOf(refusedDetail));
        rig.DispatchCell(InstallModsPane.PendingRowKey, InstallModsPane.CellPendingRestart);
        Assert.Null(rig.Pane.StopDialog);
        Assert.Single(sent);
        // A rebuild while in flight keeps the state.
        rig.Rebuild();
        Assert.Equal(InstallModsCopy.StoppingServer, PendingCell(rig.Pane, InstallModsPane.CellPendingRestart).Text);
        // Revert: drop the InFlight check and the live F20 double click sends two requests again.
    }

    [Fact]
    public void A_refusal_is_shown_on_the_footer_with_its_reason_and_the_button_comes_back()
    {
        var (rig, _, sent) = RemoteStopRig();
        rig.ClickCell(InstallModsPane.PendingRowKey, InstallModsPane.CellPendingRestart);
        rig.ClickCell(InstallModsPane.StopDialogRowKey, InstallModsPane.CellStopYes);

        Answer(StopServerReply.For(sent[0], accepted: false, StopReplyReason.NotAdmin));
        rig.Pane.TickStopRequest();

        Assert.Equal(StopRequestPhase.Refused, rig.Pane.StopRequest.Phase);
        Assert.False(rig.Pane.StopRequest.InFlight);
        string line = PendingCell(rig.Pane, InstallModsPane.CellPendingText).Text;
        Assert.Contains(" refused: ", line);
        Assert.Contains(StopServerEvents.RefusalText(StopReplyReason.NotAdmin), line);
        Assert.Equal(InstallModsCopy.StopServerToApply, PendingCell(rig.Pane, InstallModsPane.CellPendingRestart).Text);
        Assert.True(rig.Pane.TryDescribeSidebarRow(rig.Pane.PendingBarHandles[0], out var bar));
        var stop = (Dictionary<string, object?>)((Dictionary<string, object?>)bar!["pending"]!)["stop"]!;
        Assert.Equal("refused", stop["state"]);
        Assert.Equal(new List<string> { "waiting", "refused" }, stop["states"]);
        // Revert: never read the reply (the pre-F20 pane) and a refusal is indistinguishable from silence.
    }

    [Fact]
    public void An_accepted_stop_reads_stopping_then_stopped_when_the_server_goes()
    {
        var (rig, _, sent) = RemoteStopRig();
        bool connected = true;
        rig.Pane.ConnectedProbe = () => connected;
        rig.ClickCell(InstallModsPane.PendingRowKey, InstallModsPane.CellPendingRestart);
        rig.ClickCell(InstallModsPane.StopDialogRowKey, InstallModsPane.CellStopYes);

        Answer(StopServerReply.For(sent[0], accepted: true, StopReplyReason.Saving));
        rig.Pane.TickStopRequest();
        Assert.Equal(StopRequestPhase.Stopping, rig.Pane.StopRequest.Phase);
        Assert.Contains("accepted: saving the world, then stopping", PendingCell(rig.Pane, InstallModsPane.CellPendingText).Text);
        Assert.Equal(InstallModsCopy.StoppingServer, PendingCell(rig.Pane, InstallModsPane.CellPendingRestart).Text);

        connected = false;
        rig.Pane.TickStopRequest();
        Assert.Equal(StopRequestPhase.Stopped, rig.Pane.StopRequest.Phase);
        Assert.EndsWith(InstallModsCopy.StoppedRemoteTail, PendingCell(rig.Pane, InstallModsPane.CellPendingText).Text);
    }

    [Fact]
    public void No_answer_in_ten_seconds_says_the_server_did_not_answer()
    {
        var (rig, _, _) = RemoteStopRig();
        DateTime now = new(2026, 10, 7, 23, 0, 0, DateTimeKind.Utc);
        rig.Pane.StopClock = () => now;
        rig.ClickCell(InstallModsPane.PendingRowKey, InstallModsPane.CellPendingRestart);
        rig.ClickCell(InstallModsPane.StopDialogRowKey, InstallModsPane.CellStopYes);

        now = now.AddSeconds(ServerStopRequest.AnswerTimeoutSeconds - 1);
        rig.Pane.TickStopRequest();
        Assert.True(rig.Pane.StopRequest.InFlight);
        now = now.AddSeconds(1);
        rig.Pane.TickStopRequest();

        Assert.Equal(StopRequestPhase.NoAnswer, rig.Pane.StopRequest.Phase);
        Assert.StartsWith("The server did not answer the stop request.", PendingCell(rig.Pane, InstallModsPane.CellPendingText).Text);
        Assert.Equal(InstallModsCopy.StopServerToApply, PendingCell(rig.Pane, InstallModsPane.CellPendingRestart).Text);
        // Revert: drop the timeout and the footer says "Asking…" forever, as silent as the live F21 run.
    }

    [Fact]
    public void The_local_route_without_a_graceful_route_says_why_and_offers_no_yes()
    {
        var catalog = new FlowCatalog();
        var entry = Listing("dyno-Bags", "BagPlus", categories: new[] { "Server", "Oakveil Update" });
        catalog.Entries.Add(entry);
        var rig = new Rig(this, catalog, ValidatedFolder());
        var sent = new List<int>();
        rig.Pane.RemoteRestartRequest = nonce => { sent.Add(nonce); return true; };
        rig.Pane.DialogPanelFactory = _ => NewPanel();
        // What ServerProcessControl.GracefulAvailable answers with no command route; production
        // calls it (pinned below) and its own tests cover each reason.
        rig.Pane.LocalStopGate = _ => ServerProcessControl.ReasonNoTransport;
        string src = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "InstallModsPane.cs");
        SourcePin.AssertUncommented(src, "return ServerProcessControl.GracefulAvailable(root, out string why) ? \"\" : why;");

        rig.Pane.RequestRestart();

        var dialog = rig.Pane.StopDialog;
        Assert.NotNull(dialog);
        Assert.Equal("", dialog!.Content.MainCaption);
        Assert.Equal(InstallModsCopy.StopServerClose, dialog.Content.CancelCaption);
        Assert.Equal(InstallModsCopy.StopUnavailable(ServerProcessControl.ReasonNoTransport), dialog.Content.Note);
        Assert.Contains((InstallModsCopy.StopFactStartsAgain, InstallModsCopy.StartsAgainLocal),
            dialog.Content.Facts.Select(f => (f.Label, f.Value)));
        Assert.Empty(sent);
        // Revert: skip ServerProcessControl.GracefulAvailable and the local route sends a stop to
        // whatever this client happens to be connected to.
    }

    /// <summary>
    /// F30 (live 2026-10-08): the configured local dedicated server Progenitor can start again gets "Save, stop and start
    /// again": a real restart that starts the folder's server once it stopped. Where Progenitor cannot start it, the honest
    /// "Stop server to apply" stays. Game-free; not live-verified.
    /// </summary>
    [Fact]
    public void The_local_server_progenitor_can_start_is_saved_stopped_and_started_again()
    {
        var catalog = new FlowCatalog();
        var entry = Listing("dyno-Bags", "BagPlus", categories: new[] { "Server", "Oakveil Update" });
        catalog.Entries.Add(entry);
        var folder = ValidatedFolder();
        string root = folder.Current.CanonicalPath!;
        string exe = Path.Combine(root, LocalServerFolder.ServerExecutableName);
        // Review (2026-10-08): the real wait-and-start path runs, with the process list, the exit wait and the start faked.
        ServerProcessControl.ResetForTests();
        try
        {
            ServerProcessInfo? running = null;
            ServerProcessControl.RunningProbe = _ => running;
            var exits = new List<int>();
            ServerProcessControl.PidExitProbe = (pid, path, _) => { exits.Add(pid); return true; };
            var starts = new List<(string Exe, IReadOnlyList<string> Args)>();
            ServerProcessControl.StartOverride = (e, a, _) => { starts.Add((e, a)); return 5151; };
            ServerProcessControl.SleepMs = _ => { };

            var rig = new Rig(this, catalog, folder);
            var sent = new List<int>();
            rig.Pane.RemoteRestartRequest = nonce => { sent.Add(nonce); return true; };
            rig.Pane.DialogPanelFactory = _ => NewPanel();
            rig.Pane.LocalStopGate = _ => "";
            rig.Pane.StopReplyProbe = nonce => _stopReplies.TryGetValue(nonce, out var r) ? r : null;
            bool connected = true;
            rig.Pane.ConnectedProbe = () => connected;
            rig.Pane.StartAgainRunner = run => run();

            // Not running (nothing to replay): the honest Stop server to apply.
            Assert.False(rig.Pane.CanStartAgain());
            Assert.Equal(InstallModsCopy.StopServerToApply, rig.Pane.PendingRestartCaption(InstallModsCopy.SidesServer));
            // Running, but its command line was not read (review M-a): still no restart offer.
            running = new ServerProcessInfo(4242, exe);
            Assert.False(rig.Pane.CanStartAgain());
            rig.Pane.RequestRestart();
            Assert.Equal(InstallModsCopy.StopServerYes, rig.Pane.StopDialog!.Content.MainCaption);
            Assert.Contains((InstallModsCopy.StopFactStartsAgain, InstallModsCopy.StartsAgainLocal), rig.Pane.StopDialog!.Content.Facts.Select(f => (f.Label, f.Value)));
            rig.ClickCell(InstallModsPane.StopDialogRowKey, InstallModsPane.CellStopNo);

            running = new ServerProcessInfo(4242, exe, "\"" + exe + "\" -persistentDataPath \"" + Path.Combine(root, "save") + "\" -saveName world1");
            Assert.True(rig.Pane.CanStartAgain());
            Assert.Equal(InstallModsCopy.StopAndStartAgain, rig.Pane.PendingRestartCaption(InstallModsCopy.SidesServer));
            rig.Pane.RequestRestart();
            var dialog = rig.Pane.StopDialog!;
            Assert.Equal(InstallModsCopy.StopAndStartAgain, dialog.Content.MainCaption);
            Assert.StartsWith("Restart ", dialog.Content.Title);
            Assert.Contains((InstallModsCopy.StopFactStartsAgain, InstallModsCopy.StartsAgainByProgenitor), dialog.Content.Facts.Select(f => (f.Label, f.Value)));
            rig.ClickCell(InstallModsPane.StopDialogRowKey, InstallModsPane.CellStopYes);
            Assert.Single(sent);

            Answer(StopServerReply.For(sent[0], accepted: true, StopReplyReason.Saving));
            rig.Pane.TickStopRequest();
            Assert.Empty(starts);
            running = null;
            connected = false;
            rig.Pane.TickStopRequest();
            Assert.Equal(StopRequestPhase.Stopped, rig.Pane.StopRequest.Phase);
            // The stopped process's own pid was waited on, then the record this stop wrote was replayed.
            Assert.Equal(new[] { 4242 }, exits);
            Assert.Single(starts);
            Assert.Equal(exe, starts[0].Exe);
            Assert.Equal(new[] { "-persistentDataPath", Path.Combine(root, "save"), "-saveName", "world1" }, starts[0].Args);
            Assert.EndsWith("stopped and started again. Rejoin it when it's back.", rig.Pane.StartAgainLine, StringComparison.Ordinal);
        }
        finally { ServerProcessControl.ResetForTests(); }
    }

    /// <summary>F30: a start that fails after the stop says why and the manual way; nothing else is tried.</summary>
    [Fact]
    public void A_start_again_that_fails_says_why_and_how_to_start_it()
    {
        string line = InstallModsCopy.StartAgainFailedLine("Night Garden", ServerProcessControl.ReasonStillRunning);
        Assert.StartsWith("Night Garden stopped, but Progenitor couldn't start it again: The process did not exit. ", line, StringComparison.Ordinal);
        Assert.EndsWith(InstallModsCopy.StoppedLocalTail, line, StringComparison.Ordinal);
    }

    [Fact]
    public void The_drawer_button_uses_the_same_confirmation_and_says_save_and_stop()
    {
        Assert.Equal("Save and stop server", ServerRouteCopy.RestartNowCaption);
        Assert.DoesNotContain("Restart", InstallModsCopy.StopServerToApply);
        string src = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "InstallModsPane.cs");
        SourcePin.AssertUncommented(src, "head.OnClick(CellRestartNow, RequestRestart);");
    }

    /// <summary>INSTALLSIDES1: every word the open drawer shows — its head row's cells and its body's lines.</summary>
    internal static List<string> DrawerTexts(InstallModsPane pane)
    {
        var texts = new List<string>();
        if (pane.DrawerHeadRow != null) texts.AddRange(pane.DrawerHeadRow.Cells.Select(c => c.Text));
        var body = pane.DrawerBodyRow?.Cells.SingleOrDefault(c => c.Key == InstallModsPane.CellBody);
        // INSTALLSIDES2 item 4: a two-fact line reads as its two facts.
        if (body != null)
            foreach (var l in body.Lines)
            {
                if (l.HasSecond)
                {
                    texts.Add(l.Label + ": " + l.Text);
                    texts.Add(l.Label2 + ": " + l.Text2);
                }
                else texts.Add(l.ToString());
            }
        return texts;
    }

    private sealed class Rig
    {
        private readonly InstallListViewTests _owner;
        private readonly LocalServerFolder? _server;

        public Rig(InstallListViewTests owner, IInstallCatalog catalog, LocalServerFolder? server = null)
        {
            _owner = owner;
            _server = server;
            Catalog = catalog;
            // The automatic README run fetches its first key on Build and then parks on the
            // clock: nothing lands from a background thread while the test drives the pane.
            Pane = new InstallModsPane(catalog, new NeverClock(), null);
            Pane.SetDetailRebuild(Rebuild);
            Rebuild();
        }

        public IInstallCatalog Catalog { get; }
        public InstallModsPane Pane { get; }
        public IUiPanel Panel { get; private set; } = null!;
        public int Rebuilds { get; private set; }
        public Task? Download { get; private set; }

        public void Rebuild()
        {
            Rebuilds++;
            Panel?.Dispose();
            Panel = _owner.NewPanel();
            Pane.Build(Panel, true, false, _server, () => { }, () => { }, DateTime.UtcNow);
        }

        /// <summary>INSTALLSIDES1: open or close a row's drawer — its expand button, the only control that does.</summary>
        public void ClickRow(string key)
        {
            Assert.True(UiCellRowHandle.TryInvoke(key + "/" + InstallModsPane.CellExpand, out var detail), "no expand button on " + key);
            Assert.Equal(key + "/" + InstallModsPane.CellExpand, detail["label"]);
        }

        /// <summary>INSTALLSIDES1: press the button captioned <paramref name="caption"/> in a row's Client or Server cell.</summary>
        public void ClickSide(string key, bool server, string caption)
        {
            var row = Pane.Rows.Single(r => r.Row.Key == key).Row;
            string[] slots = server
                ? new[] { InstallModsPane.CellServerA, InstallModsPane.CellServerB }
                : new[] { InstallModsPane.CellClientA, InstallModsPane.CellClientB };
            var cell = row.Cells.FirstOrDefault(c => slots.Contains(c.Key) && c.Visible && c.Text == caption);
            Assert.True(cell != null, "no " + caption + " in the " + (server ? "server" : "client") + " cell of " + key
                + ": [" + string.Join(", ", row.Cells.Where(c => slots.Contains(c.Key)).Select(c => c.Key + "=" + c.Text + (c.Visible ? "" : "(hidden)"))) + "]");
            ClickCell(key, cell!.Key);
        }

        /// <summary>The client cell's Install (the old one-button row's install, aimed at this client).</summary>
        public void Install(string key) => ClickSide(key, server: false, SideCells.InstallCaption);

        /// <summary>The server cell's Install (the picked server: the local folder, or the connected one).</summary>
        public void InstallOnServer(string key) => ClickSide(key, server: true, SideCells.InstallCaption);

        /// <summary>
        /// The combined action the old one-button row ran, by <c>[Install] DefaultTarget</c>. No cell
        /// carries it any more (INSTALLSIDES1); the pane keeps it as the setting's one reader.
        /// </summary>
        public void LegacyInstall(string key)
        {
            Pane.OnInstallClick(key);
            Download = Pane.ActiveDownload ?? Download;
        }

        public void ClickCell(string rowKey, string cellKey)
        {
            Assert.True(UiCellRowHandle.TryInvoke(rowKey + "/" + cellKey, out _), "no visible cell " + rowKey + "/" + cellKey);
            Download = Pane.ActiveDownload ?? Download;
        }

        /// <summary>The cell's own click slot, dispatched directly: a press that bypasses the bridge's grayed-control refusal.</summary>
        public void DispatchCell(string rowKey, string cellKey)
        {
            ushort id = UiCellRowHandle.VisibleRow(rowKey)?.CellSlotId(cellKey) ?? 0;
            Assert.NotEqual(0, id);
            UiSlotTable.BootstrapDispatch(id, (byte)UiSlotKind.Click, 0f, false, null);
        }

        public void ClickHandle(IUiHandle handle)
        {
            var owned = (UiHandle)handle;
            Assert.NotEqual(0, owned.WidgetId);
            UiSlotTable.BootstrapDispatch(owned.WidgetId, (byte)UiSlotKind.Click, 0f, false, null);
        }

        /// <summary>INSTALLSIDES1: a drawer action is a button cell on the drawer's head row.</summary>
        public void ClickDrawerButton(string caption)
        {
            var head = Pane.DrawerHeadRow;
            Assert.NotNull(head);
            var cell = head!.Cells.Single(c => c.Kind == CellKind.Button && c.Text == caption);
            ClickCell(head.Key, cell.Key);
        }

        /// <summary>INSTALL2 item 3: press Yes in the y/n box open under <paramref name="key"/>'s row.</summary>
        public void ClickPromptYes(string key) =>
            ClickCell(InstallModsPane.PromptKeyPrefix + key, InstallModsPane.CellPromptYes);

        /// <inheritdoc cref="ClickPromptYes"/>
        public void ClickPromptNo(string key) =>
            ClickCell(InstallModsPane.PromptKeyPrefix + key, InstallModsPane.CellPromptNo);

        /// <summary>Complete the fake download and wait for the pane's flow task (the completion is posted inline here).</summary>
        public async Task Complete(InspectCallResult result)
        {
            var flow = Download ?? Pane.ActiveDownload;
            ((FlowCatalog)Catalog).Complete(result);
            if (flow != null) await flow;
        }
    }

    /// <summary>A catalog whose download completes when the test says so; plans hash per target.</summary>
    private sealed class FlowCatalog : IInstallCatalog
    {
        private TaskCompletionSource<InspectCallResult>? _pending;

        public List<CatalogEntry> Entries { get; } = new();
        public Dictionary<string, InstallPlan> Plans { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int DownloadCalls { get; private set; }
        public int ApproveCalls { get; private set; }
        public int ExecuteCalls { get; private set; }
        public string? LastDownloadKey { get; private set; }
        public string? LastExecutedHash { get; private set; }

        public InstallProgress Status => InstallProgress.Idle;

        public void Complete(InspectCallResult result)
        {
            var tcs = _pending;
            _pending = null;
            Assert.NotNull(tcs);
            tcs!.SetResult(result);
        }

        public Task<CatalogLoadResult> LoadIndexAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new CatalogLoadResult(true, null, Entries.Count, "stamp"));

        public IReadOnlyList<CatalogEntry> Search(string? query) => Entries;

        public bool TryGetEntry(string key, out CatalogEntry? entry)
        {
            entry = Entries.Find(e => string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase));
            return entry != null;
        }

        public Task<InspectCallResult> DownloadAndInspectAsync(string key, CancellationToken cancellationToken)
        {
            DownloadCalls++;
            LastDownloadKey = key;
            _pending = new TaskCompletionSource<InspectCallResult>();
            return _pending.Task;
        }

        public PlanCallResult Plan(string key, InstallTarget target)
        {
            if (!Plans.TryGetValue(key, out var plan))
                return new PlanCallResult(false, InstallReasons.NotInspected, null);
            var forTarget = new InstallPlan(
                plan.PlanHash + "-" + InstallModsPane.TargetName(target),
                plan.Entry, target, plan.Decision, plan.Reason, plan.Artifacts, plan.Dependencies, plan.PluginId, plan.PluginFileName,
                executorAvailable: plan.ExecutorAvailable, ordinaryDependencies: plan.OrdinaryDependencies);
            return new PlanCallResult(true, null, forTarget);
        }

        public ApproveCallResult Approve(string planHash)
        {
            ApproveCalls++;
            return new ApproveCallResult(true, null, planHash, false);
        }

        /// <summary>Job LIST4 (A): set to make the write refuse, so the chip click's error path runs.</summary>
        public string? ExecuteError { get; set; }

        public ExecuteCallResult ExecuteToPending(string planHash)
        {
            ExecuteCalls++;
            LastExecutedHash = planHash;
            if (ExecuteError != null) return new ExecuteCallResult(false, ExecuteError, null, false);
            return new ExecuteCallResult(true, null, "pending/" + planHash, false);
        }

        public Task<ListingDocumentSet> LoadListingDocumentsAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(ListingDocumentSet.Unavailable(key ?? "", InstallReasons.UnknownEntry));
    }

    /// <summary>A catalog with documents; the automatic README run can be held at the gate or refused outright.</summary>
    private sealed class FakeCatalog : IInstallCatalog
    {
        public List<CatalogEntry> Entries { get; } = new();
        public Dictionary<string, ListingDocumentSet> Documents { get; } = new(StringComparer.OrdinalIgnoreCase);
        public StepGate? Gate { get; set; }
        /// <summary>While true every document load answers "unavailable" without counting: the run finishes at once.</summary>
        public bool HoldPrefetch { get; set; }
        private int _documentLoadCalls;
        public int DocumentLoadCalls => Volatile.Read(ref _documentLoadCalls);

        public InstallProgress Status => InstallProgress.Idle;

        public Task<CatalogLoadResult> LoadIndexAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new CatalogLoadResult(true, null, Entries.Count, "stamp"));

        public IReadOnlyList<CatalogEntry> Search(string? query) => Entries;

        public bool TryGetEntry(string key, out CatalogEntry? entry)
        {
            entry = Entries.Find(e => string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase));
            return entry != null;
        }

        public Task<InspectCallResult> DownloadAndInspectAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(new InspectCallResult(false, InstallReasons.FetchNoUrl, null, null));

        public PlanCallResult Plan(string key, InstallTarget target) => new(false, InstallReasons.NotInspected, null);

        public ApproveCallResult Approve(string planHash) => new(true, null, planHash, false);

        public ExecuteCallResult ExecuteToPending(string planHash) => new(true, null, "pending/" + planHash, false);

        public async Task<ListingDocumentSet> LoadListingDocumentsAsync(string key, CancellationToken cancellationToken)
        {
            if (HoldPrefetch)
                return ListingDocumentSet.Unavailable(key ?? "", InstallReasons.FetchCancelled);
            Task? wait = Gate?.Wait();
            Interlocked.Increment(ref _documentLoadCalls);
            if (wait != null) await wait.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (Documents.TryGetValue(key ?? "", out var set)) return set;
            return ListingDocumentSet.Unavailable(key ?? "", InstallReasons.UnknownEntry);
        }
    }

    private sealed class StepGate
    {
        private readonly object _lock = new();
        private TaskCompletionSource<bool>? _tcs;

        public Task Wait()
        {
            lock (_lock)
            {
                _tcs ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                return _tcs.Task;
            }
        }

        public void Release()
        {
            TaskCompletionSource<bool>? tcs;
            lock (_lock)
            {
                tcs = _tcs;
                _tcs = null;
            }
            tcs?.SetResult(true);
        }
    }

    private sealed class StepClock : ICatalogClock
    {
        private readonly object _lock = new();
        private readonly List<TimeSpan> _delays = new();
        private TaskCompletionSource<bool>? _tcs;

        public List<TimeSpan> Delays
        {
            get { lock (_lock) return new List<TimeSpan>(_delays); }
        }

        public DateTime UtcNow => new(2026, 9, 12, 0, 0, 0, DateTimeKind.Utc);

        public Task Delay(TimeSpan duration, CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
            lock (_lock)
            {
                _tcs = tcs;
                _delays.Add(duration);
            }
            return tcs.Task;
        }

        public void Release()
        {
            TaskCompletionSource<bool>? tcs;
            lock (_lock)
            {
                tcs = _tcs;
                _tcs = null;
            }
            tcs?.TrySetResult(true);
        }
    }

    private sealed class ImmediateClock : ICatalogClock
    {
        public DateTime UtcNow => new(2026, 9, 12, 9, 0, 0, DateTimeKind.Utc);
        public Task Delay(TimeSpan duration, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>A clock whose delay never elapses (it only cancels): the README run parks after its first key.</summary>
    private sealed class NeverClock : ICatalogClock
    {
        public DateTime UtcNow => new(2026, 9, 12, 9, 0, 0, DateTimeKind.Utc);

        public Task Delay(TimeSpan duration, CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
            return tcs.Task;
        }
    }

    private sealed class FakeDecoder : IIconDecoder
    {
        public readonly Dictionary<string, object> Handles = new(StringComparer.Ordinal);
        public object? Placeholder { get; } = new object();

        public object? Decode(byte[] bytes, string url)
        {
            var handle = new object();
            Handles[url] = handle;
            return handle;
        }

        public void Destroy(object handle) { }
    }

    /// <summary>Holds every icon fetch until <see cref="Respond"/> names its URL; unanswered fetches stay pending.</summary>
    private sealed class FakeIconFetch
    {
        private readonly object _lock = new();
        private readonly Dictionary<string, TaskCompletionSource<CatalogBytesFetchResult>> _pending = new(StringComparer.Ordinal);
        private int _started;
        public int Started => Volatile.Read(ref _started);
        public int Failed;

        public Task<CatalogBytesFetchResult> FetchAsync(string url, CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<CatalogBytesFetchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_lock) _pending[url] = tcs;
            Interlocked.Increment(ref _started);
            ct.Register(() => tcs.TrySetCanceled(ct));
            return tcs.Task;
        }

        public void Respond(string url)
        {
            TaskCompletionSource<CatalogBytesFetchResult>? tcs;
            lock (_lock) _pending.TryGetValue(url, out tcs);
            Assert.NotNull(tcs);
            tcs!.TrySetResult(new CatalogBytesFetchResult(true, null, new byte[] { 0x89, 0x50, 0x4E, 0x47 }, 4, 200));
        }
    }
}
