using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Progenitor.Install;
using Progenitor.Networking;
using Progenitor.Plugins;
using Progenitor.Ui;
using System.Linq;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// LIST2: the Install tab's toolbar, filter chip row and column header as cell rows. The chip
/// row is one <c>CellRow</c> (Side + four single-choice chips, No restart needed, Current
/// update (tag), Clear filters right-packed and hidden at the defaults), asserted through the
/// column model at the 1248 px native detail column, the 860 px 1600×1024 fallback column and
/// 1664 px. The SEARCH3 five-row block this file used to pin is gone with the sidebar.
/// </summary>
public sealed class InstallFilterBlockTests : IDisposable
{
    public InstallFilterBlockTests()
    {
        UiLayoutBatch.ResetForTests();
        // TAG1: the current update comes from the game-build adapter, pinned to the known client build here.
        CatalogUpdateTags.ResetProvidersForTests();
        CatalogUpdateTags.ConfiguredTagProvider = () => null;
        CatalogUpdateTags.BuildProvider = () => InstallSearchFilterTests.KnownClientBuild;
    }

    public void Dispose()
    {
        // INSTALLUX2 item 1: the confirmation is a modal dialog on its own panel now. Ui.ResetForTests
        // only FORGETS the open one, so without this its rows outlive the test and the next test's
        // click on prompt:<key> resolves to the dead one.
        Progenitor.Ui.UiDialog.Current?.Close();
        UiLayoutBatch.ResetForTests();
        CatalogUpdateTags.ResetProvidersForTests();
    }

    /// <summary>INSTALLFILTERS1: the filter bar at each approved width, and a narrow one where the Show group wraps.</summary>
    public static IEnumerable<object[]> Widths()
    {
        yield return new object[] { 1248f };
        yield return new object[] { MenuPanelLayout.ForFrame(ModsWorkspaceFrame.Fallback(1600f, 1024f), sidebar: false).RowWidth };
        yield return new object[] { 1664f };
        yield return new object[] { 860f };
    }

    [Theory]
    [MemberData(nameof(Widths))]
    public void Filter_bar_is_three_labeled_groups_that_wrap_whole_and_clear_shows_only_off_the_defaults(float rowWidth)
    {
        var pane = new InstallModsPane(new FakeCatalog());
        var rows = pane.FilterRowsFor(rowWidth);
        var keys = rows.SelectMany(r => r.Cells).Select(c => c.Key).Where(k => k != InstallModsPane.CellGroupGap).ToArray();
        Assert.Equal(
            new[]
            {
                InstallModsPane.CellSideCaption, InstallModsPane.CellSideAny, InstallModsPane.CellSideClient, InstallModsPane.CellSideServer,
                InstallModsPane.CellSideBoth,
                InstallModsPane.CellShowCaption, InstallModsPane.CellShowAll, InstallModsPane.CellShowInstalled, InstallModsPane.CellShowMissing,
                InstallModsPane.CellShowUpdate,
                InstallModsPane.CellOnlyCaption, InstallModsPane.CellNoRestart, InstallModsPane.CellCurrent, InstallModsPane.CellSummary,
                InstallModsPane.CellClear,
            },
            keys);
        // Two rows when Side and Show fit side by side (the live 1236 px row and wider), three when narrow.
        bool wide = rowWidth >= 1200f;
        Assert.Equal(wide ? new[] { InstallModsPane.FiltersKey, InstallModsPane.FiltersOnlyKey }
                          : new[] { InstallModsPane.FiltersKey, InstallModsPane.FiltersShowKey, InstallModsPane.FiltersOnlyKey },
            rows.Select(r => r.Key).ToArray());
        var all = rows.SelectMany(r => r.Cells).ToList();
        Assert.False(all.Single(c => c.Key == InstallModsPane.CellClear).Visible);
        // Selected chips are the filled gold face; unselected the quiet dark face.
        Assert.Equal(CellTone.ChipOn, all.Single(c => c.Key == InstallModsPane.CellSideAny).Tone);
        Assert.Equal(CellTone.ChipOff, all.Single(c => c.Key == InstallModsPane.CellSideClient).Tone);
        Assert.Equal(CellTone.ChipOn, all.Single(c => c.Key == InstallModsPane.CellShowAll).Tone);
        Assert.Equal(CellTone.ChipOn, all.Single(c => c.Key == InstallModsPane.CellCurrent).Tone);
        Assert.Equal(CellTone.ChipOff, all.Single(c => c.Key == InstallModsPane.CellNoRestart).Tone);
        Assert.Equal("Current game update (Oakveil)", all.Single(c => c.Key == InstallModsPane.CellCurrent).Text);
        Assert.Equal("All 0", all.Single(c => c.Key == InstallModsPane.CellShowAll).Text);
        // Every row: its cells fit the row without overlapping.
        foreach (var (key, cells) in rows)
        {
            Assert.Equal(UnityUiRuntimeMetrics.RowHeight, new CellRowSpec(key, cells).Height);
            var placed = MenuPanelLayout.CellColumns(rowWidth, cells);
            for (int i = 0; i < placed.Length; i++)
                for (int j = i + 1; j < placed.Length; j++)
                    Assert.False(CellLayout.Overlap(placed[i], placed[j]), placed[i] + " overlaps " + placed[j] + " at " + rowWidth + " in " + key);
            foreach (var p in placed.Where(p => p.Visible))
                Assert.True(p.Right <= rowWidth / 2f + .01f && p.Left >= -rowWidth / 2f - .01f, p + " leaves the row at " + rowWidth);
            // A chip is never cut: its placed width is at least its ask.
            foreach (var c in cells.Where(c => c.Kind == CellKind.Button && c.Visible && c.Width.Mode == CellWidthMode.Px))
                Assert.True(placed.Single(p => p.Key == c.Key).Width >= c.Width.Value - .5f, c.Key + " squeezed at " + rowWidth);
        }

        // Off the defaults the Clear chip shows, right-packed on the Only row.
        pane.Filters.NoRestartOnly = true;
        var narrowed = pane.OnlyCells();
        Assert.True(narrowed.Single(c => c.Key == InstallModsPane.CellClear).Visible);
        Assert.Equal(CellTone.ChipOn, narrowed.Single(c => c.Key == InstallModsPane.CellNoRestart).Tone);
        var clear = MenuPanelLayout.CellColumns(rowWidth, narrowed).Single(c => c.Key == InstallModsPane.CellClear);
        Assert.InRange(clear.Right, rowWidth / 2f - .01f, rowWidth / 2f + .01f);
        pane.Filters.Reset();

        // The current-update chip carries the bare label while the game build is unknown; the
        // chip is then off (nothing is hidden) whatever the flag says.
        CatalogUpdateTags.BuildProvider = () => InstallSearchFilterTests.UnmappedBuild;
        var unknown = new InstallModsPane(new FakeCatalog()).OnlyCells();
        Assert.Equal(InstallModsCopy.CurrentUpdateChip, unknown.Single(c => c.Key == InstallModsPane.CellCurrent).Text);
        Assert.Equal(CellTone.ChipOff, unknown.Single(c => c.Key == InstallModsPane.CellCurrent).Tone);
        Assert.DoesNotContain("Progenitor", string.Join(" ", all.Select(c => c.Text)), StringComparison.Ordinal);
    }

    [Fact]
    public void Chip_tokens_make_the_selected_state_unmistakable()
    {
        Assert.Equal((MenuPalette.ChipOnFill, MenuPalette.ChipOnBorder, MenuPalette.ChipOnText), MenuPalette.Button(CellTone.ChipOn));
        Assert.Equal((MenuPalette.ChipOffFill, MenuPalette.ChipOffBorder, MenuPalette.ChipOffText), MenuPalette.Button(CellTone.ChipOff));
        Assert.Equal(PaletteColor.Hex(0x6b4f22), MenuPalette.ChipOnFill);
        Assert.Equal(PaletteColor.Hex(0xe0b25a), MenuPalette.ChipOnBorder);
        Assert.Equal(PaletteColor.Hex(0xfff3dc), MenuPalette.ChipOnText);
        Assert.Equal(PaletteColor.Hex(0x211a14), MenuPalette.ChipOffFill);
        Assert.Equal(PaletteColor.Hex(0x4a3c2f), MenuPalette.ChipOffBorder);
        Assert.Equal(PaletteColor.Hex(0xcbbda7), MenuPalette.ChipOffText);
        // The faces differ clearly: the selected one is several times brighter.
        Assert.True(MenuPalette.ChipOnFill.Luminance > MenuPalette.ChipOffFill.Luminance * 2f);
        // An attention chip keeps the dark face with an amber rim.
        var attention = MenuPalette.Button(CellTone.ChipAttention);
        Assert.Equal(MenuPalette.ChipOffFill, attention.Fill);
        Assert.Equal(MenuPalette.ChipAttentionBorder, attention.Border);
        Assert.Equal(CellTone.ChipAttention, InstallModsPane.ShowChipTone(false, InstallShowFilter.MissingSide, 2));
        Assert.Equal(CellTone.ChipOff, InstallModsPane.ShowChipTone(false, InstallShowFilter.MissingSide, 0));
        Assert.Equal(CellTone.ChipOff, InstallModsPane.ShowChipTone(false, InstallShowFilter.Installed, 5));
        Assert.Equal(CellTone.ChipOn, InstallModsPane.ShowChipTone(true, InstallShowFilter.NeedsUpdate, 3));
    }

    [Fact]
    public void Toolbar_is_search_and_count_and_the_header_names_the_stable_columns()
    {
        var pane = new InstallModsPane(new FakeCatalog());
        // LIST3: the search field leads the toolbar and the count is plain text (not mono).
        // INSTALLCOLS1: the "Sort: …" button is gone; the header is the one sort control.
        var toolbar = pane.ToolbarCells();
        Assert.Equal(
            new[] { InstallModsPane.CellSearch, InstallModsPane.CellCount, InstallModsPane.CellGap },
            toolbar.Select(c => c.Key).ToArray());
        Assert.Equal(CellKind.Input, toolbar[0].Kind);
        Assert.Equal(InstallModsCopy.SearchPlaceholder, toolbar[0].Placeholder);
        Assert.Equal(CellKind.Text, toolbar[1].Kind);
        Assert.Equal("0 listings", toolbar[1].Text);
        Assert.DoesNotContain(toolbar, c => c.Kind == CellKind.Button);
        Assert.Equal("descriptions 12/296", InstallModsCopy.DescriptionsText(true, 12, 296));
        Assert.Equal("", InstallModsCopy.DescriptionsText(false, 296, 296));
        var header = pane.HeaderCells();
        Assert.Equal(
            // INSTALLSIDES1: the expand button and the icon, Mod (the author is its second line), the
            // Client and Server columns (the Server caption names the picked server; with no picker it
            // is plain Server), Released and Downloads. INSTALLSIDES2 dropped Side.
            new[] { "", "", "Mod", "Client", "Server", "Released", "Downloads" },
            header.Select(c => c.Text).ToArray());
        Assert.Equal("name", header.Single(c => c.Key == InstallModsPane.CellName).SortKey);
        Assert.Equal("released", header.Single(c => c.Key == InstallModsPane.CellReleased).SortKey);
        // The Client column sorts by when this machine installed the mod.
        Assert.Equal("installed", header.Single(c => c.Key == InstallModsPane.CellClient).SortKey);
        Assert.Equal("downloads", header.Single(c => c.Key == InstallModsPane.CellDownloads).SortKey);
        Assert.DoesNotContain(header, c => c.Key == InstallModsPane.CellSide);
        // INSTALLSIDES2 item 1: the side headers carry their column's tint.
        Assert.Equal(CellColumn.Client, header.Single(c => c.Key == InstallModsPane.CellClient).Column);
        Assert.Equal(CellColumn.Server, header.Single(c => c.Key == InstallModsPane.CellServer).Column);
        Assert.Null(header.Single(c => c.Key == InstallModsPane.CellServer).SortKey);
        Assert.False(header.Single(c => c.Key == InstallModsPane.CellClient).Optional);
        Assert.False(header.Single(c => c.Key == InstallModsPane.CellServer).Optional);
        // The active sort column (Released, the default) never drops; the others stay optional.
        Assert.False(header.Single(c => c.Key == InstallModsPane.CellReleased).Optional);
        Assert.True(header.Single(c => c.Key == InstallModsPane.CellDownloads).Optional);
        pane.SetSort(InstallSort.Name);
        Assert.True(pane.HeaderCells().Single(c => c.Key == InstallModsPane.CellReleased).Optional);
        // LIST3: three pinned top rows (toolbar with the search, filters, header) and no footer.
        Assert.Equal(3, InstallModsPane.TopChromeRows);
        Assert.Equal(0, InstallModsPane.FooterRows);
        // Revert: the Sort button back on the toolbar, an Update column, or a fourth top row fails this.
    }

    private sealed class FakeCatalog : IInstallCatalog
    {
        public List<CatalogEntry> Entries { get; } = new();
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
        public Task<ListingDocumentSet> LoadListingDocumentsAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(ListingDocumentSet.Unavailable(key ?? "", InstallReasons.UnknownEntry));
    }
}
