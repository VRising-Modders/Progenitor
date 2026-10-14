using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Progenitor.Install;
using Progenitor.Ui;
using Xunit;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.Tests;

/// <summary>
/// LIST3: the Install list view polished to the approved mock, asserted through the column
/// model at the two frames the mock was reviewed at — the 1664×916 native frame of 3840×1080
/// (gen 8 capture) and the 1600×1024 fallback frame — now as full-width rows, plus the kit
/// additions (the input cell and the full-width pane mode) and the automatic index refresh.
/// Removing the side chips' shared width fails <see cref="Side_chips_share_one_width_that_fits_Server_so_Any_and_Both_never_ellipsise"/>;
/// removing the input cell from the toolbar fails <see cref="Toolbar_leads_with_the_search_field_counts_in_plain_text_and_has_no_sort_button"/>;
/// narrowing the Downloads column back to 100 px fails <see cref="Header_captions_fit_their_columns_including_DOWNLOADS_with_the_sort_indicator"/>;
/// listing every tag again fails <see cref="Update_note_under_the_name_warns_only_when_the_listing_misses_the_current_update"/>;
/// dropping the full-width mode fails <see cref="Full_width_mode_gives_the_Install_pane_the_whole_frame_and_leaves_the_other_panes_split"/>;
/// restoring the title band or the footer fails <see cref="At_least_ten_listing_rows_fit_the_916_px_native_frame"/>;
/// refetching on every open fails <see cref="Index_refresher_serves_the_cache_at_once_replaces_it_when_the_fetch_lands_and_refetches_only_when_stale"/>.
/// </summary>
public sealed class InstallListPolishTests : IDisposable
{
    private const string KnownClientBuild = InstallSearchFilterTests.KnownClientBuild;

    private readonly CellRowTests.FakeCellRuntime _runtime = new();
    private readonly List<IUiPanel> _panels = new();
    private readonly object _owner = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "progenitor-list3-" + Guid.NewGuid().ToString("N"));

    public InstallListPolishTests()
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

    /// <summary>The native 3840×1080 frame (1664×916, gen 42/gen 8) and the 1600×1024 fallback, as full-width panes.</summary>
    public static IEnumerable<object[]> Frames()
    {
        yield return new object[] { "3840x1080", 1664f, 916f, ModsWorkspaceFrame.PathNative };
        var fallback = ModsWorkspaceFrame.Fallback(1600f, 1024f);
        yield return new object[] { "1600x1024", fallback.Width, fallback.Height, fallback.Path };
    }

    private static ModsWorkspaceFrame Frame(float width, float height, string path) => new(width, height, 0f, 0f, path);

    private static float FullWidthRow(float width, float height, string path) =>
        MenuPanelLayout.ForFrame(Frame(width, height, path), sidebar: false, InstallModsPane.PaneMode).RowWidth;

    // ---- Defect 1: side chips ------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Frames))]
    public void Side_chips_share_one_width_that_fits_Server_so_Any_and_Both_never_ellipsise(string name, float width, float height, string path)
    {
        float rowWidth = FullWidthRow(width, height, path);
        var pane = new InstallModsPane(new FakeCatalog());
        // INSTALLFILTERS1: the filter groups sit on their rows; each row is placed on its own.
        CellPlacement[] PlaceRows(InstallModsPane p)
        {
            var all = new List<CellPlacement>();
            foreach (var (_, rowCells) in p.FilterRowsFor(rowWidth))
            {
                var placedRow = MenuPanelLayout.CellColumns(rowWidth, rowCells);
                AssertNoOverlap(placedRow, rowWidth);
                all.AddRange(placedRow);
            }
            return all.ToArray();
        }
        var specs = pane.FilterCells();
        var placed = PlaceRows(pane);
        var sideKeys = new[] { InstallModsPane.CellSideAny, InstallModsPane.CellSideServer, InstallModsPane.CellSideClient, InstallModsPane.CellSideBoth };
        foreach (string key in sideKeys)
        {
            var spec = specs.Single(s => s.Key == key);
            var cell = placed.Single(p => p.Key == key);
            Assert.True(cell.Visible, key + " hidden at " + name);
            // One shared width for the four chips: the widest side caption plus the slack.
            Assert.InRange(cell.Width, InstallModsPane.SideChipWidth - .01f, InstallModsPane.SideChipWidth + .01f);
            Assert.True(CellLayout.CaptionFits(spec, cell.Width), key + " caption clipped at " + name);
            // Preferred width = the caption estimate + button padding + one glyph of slack; the cell is never narrower.
            float preferred = CellLayout.EstimateTextWidth(spec.Text, spec.FontSize) + CellLayout.ButtonPad + InstallModsPane.ChipCaptionSlack;
            Assert.True(cell.Width >= preferred - .01f, key + " narrower than its preferred width at " + name);
        }
        Assert.True(InstallModsPane.SideChipWidth >= InstallModsPane.ChipNaturalWidth(InstallSearchFilters.ServerCaption) - .01f);
        Assert.True(InstallModsPane.ChipCaptionSlack > 0f);
        // Every shown chip on the row keeps its caption at these widths (Clear filters is hidden at the defaults).
        foreach (var spec in specs.Where(s => s.Kind == CellKind.Button && s.Visible))
            Assert.True(CellLayout.CaptionFits(spec, placed.Single(p => p.Key == spec.Key).Width), spec.Key + " clipped at " + name);
        pane.Filters.Side = InstallSideFilter.Server;
        var narrowed = pane.FilterCells();
        var narrowedPlaced = PlaceRows(pane);
        var clear = narrowed.Single(s => s.Key == InstallModsPane.CellClear);
        Assert.True(clear.Visible);
        Assert.True(CellLayout.CaptionFits(clear, narrowedPlaced.Single(p => p.Key == InstallModsPane.CellClear).Width), "clear clipped at " + name);
        pane.Filters.Reset();
        // Revert: sizing Any / Both to their own (under-estimated) captions fails the shared-width assertion.
    }

    // ---- Defects 2–4: the toolbar and the input cell --------------------------------------

    [Theory]
    [MemberData(nameof(Frames))]
    public void Toolbar_leads_with_the_search_field_counts_in_plain_text_and_has_no_sort_button(string name, float width, float height, string path)
    {
        float rowWidth = FullWidthRow(width, height, path);
        var pane = new InstallModsPane(new FakeCatalog());
        var specs = pane.ToolbarCells();
        var placed = MenuPanelLayout.CellColumns(rowWidth, specs);
        AssertNoOverlap(placed, rowWidth);
        Assert.Equal(
            new[] { InstallModsPane.CellSearch, InstallModsPane.CellCount, InstallModsPane.CellGap },
            specs.Select(s => s.Key).ToArray());
        var search = placed.Single(p => p.Key == InstallModsPane.CellSearch);
        Assert.True(search.Visible);
        Assert.InRange(search.Left, -rowWidth / 2f - .01f, -rowWidth / 2f + .01f);
        Assert.InRange(search.Width, InstallModsPane.SearchCellWidth - .01f, InstallModsPane.SearchCellWidth + .01f);
        Assert.Equal(CellKind.Input, specs[0].Kind);
        Assert.Equal(InstallModsCopy.SearchPlaceholder, specs[0].Placeholder);
        Assert.True(CellLayout.CaptionFits(specs[0], search.Width), "placeholder clipped at " + name);
        // The count is body text: no mono (letter-spaced) cell anywhere on the toolbar.
        Assert.Equal(CellKind.Text, specs.Single(s => s.Key == InstallModsPane.CellCount).Kind);
        Assert.DoesNotContain(specs, s => s.Kind == CellKind.Mono);
        Assert.True(placed.Single(p => p.Key == InstallModsPane.CellCount).Visible, "count hidden at " + name);
        // INSTALLCOLS1 defect 7: no "Sort: …" button repeating the header's sort mark.
        Assert.DoesNotContain(specs, s => s.Kind == CellKind.Button);
        // Revert: a mono count cell, the Sort button back, or the search as a kit input row fails this.
    }

    [Fact]
    public void Input_cell_reads_back_kind_text_and_placeholder_and_its_text_slot_filters_in_place()
    {
        var catalog = new FakeCatalog();
        catalog.Entries.Add(InstallListViewTests.Listing("o-blood", "Blood Craft", description: "rituals", categories: new[] { "Oakveil Update" }));
        catalog.Entries.Add(InstallListViewTests.Listing("o-other", "Other Pack", description: "bags", categories: new[] { "Oakveil Update" }));
        var pane = new InstallModsPane(catalog, new NeverClock(), null);
        var panel = NewPanel();
        pane.Build(panel, true, false, null, () => { }, () => { }, DateTime.UtcNow);
        var toolbar = (UiCellRowHandle)pane.ToolbarRow!;
        int hosted = _runtime.Rows.Count;

        // Readback: `{kind: input, text, placeholder}` on the search cell; no placeholder key elsewhere.
        var described = toolbar.Describe();
        var cells = ((List<object?>)described["cells"]!).Cast<Dictionary<string, object?>>().ToList();
        var search = cells.Single(c => (string?)c["key"] == InstallModsPane.CellSearch);
        Assert.Equal("input", search["kind"]);
        Assert.Equal("", search["text"]);
        Assert.Equal(InstallModsCopy.SearchPlaceholder, search["placeholder"]);
        Assert.Equal("text", cells.Single(c => (string?)c["key"] == InstallModsPane.CellCount)["kind"]);
        Assert.DoesNotContain("placeholder", cells.Single(c => (string?)c["key"] == InstallModsPane.CellCount).Keys);

        // The fake runtime wired a text slot to the input cell; a keystroke lands through it.
        ushort slot = toolbar.CellSlotId(InstallModsPane.CellSearch);
        Assert.NotEqual(0, slot);
        Assert.Equal(slot, _runtime.Rows.Single(r => r.Text == InstallModsPane.ToolbarKey).WiredIdOf(InstallModsPane.CellSearch));
        UiSlotTable.BootstrapDispatch(slot, (byte)UiSlotKind.Text, 0f, false, "blood");
        Assert.Equal("blood", pane.Search);
        Assert.Equal("blood", pane.SearchCell!.Text);
        Assert.Equal("blood", ((Dictionary<string, object?>)((List<object?>)toolbar.Describe()["cells"]!).Cast<Dictionary<string, object?>>().Single(c => (string?)c["key"] == InstallModsPane.CellSearch))["text"]);
        Assert.Equal(hosted, _runtime.Rows.Count);
        Assert.True(pane.Rows.Single(r => r.Row.Key == "o-blood").Row.Visible);
        Assert.False(pane.Rows.Single(r => r.Row.Key == "o-other").Row.Visible);
        // The pane writes the field only when its value differs (a keystroke never rewrites the typed field).
        var fakeRow = _runtime.Rows.Single(r => r.Text == InstallModsPane.ToolbarKey);
        int applied = fakeRow.Applied.Count(k => k == InstallModsPane.CellSearch);
        pane.Search = "blood";
        Assert.Equal(applied, fakeRow.Applied.Count(k => k == InstallModsPane.CellSearch));
        pane.ClearFilters();
        Assert.Equal("", pane.SearchCell!.Text);
        Assert.Equal(applied + 1, fakeRow.Applied.Count(k => k == InstallModsPane.CellSearch));
        // An input key is not a click target, and OnClick on it is a no-op lease.
        Assert.False(UiCellRowHandle.TryInvoke(InstallModsPane.ToolbarKey + "/" + InstallModsPane.CellSearch, out _));
        Assert.Same(Noop.Instance, toolbar.OnClick(InstallModsPane.CellSearch, () => { }));
        Assert.Same(Noop.Instance, toolbar.OnChanged(InstallModsPane.CellCount, _ => { }));
        Assert.Equal("input", UiCellRowHandle.KindName(CellKind.Input));
        // Revert: wiring the input cell as a click slot, or omitting `placeholder` from its readback, fails this.
    }

    // ---- Defect 5: the header and the Update column --------------------------------------

    [Theory]
    [MemberData(nameof(Frames))]
    public void Header_captions_fit_their_columns_including_DOWNLOADS_with_the_sort_indicator(string name, float width, float height, string path)
    {
        float rowWidth = FullWidthRow(width, height, path);
        var pane = new InstallModsPane(new FakeCatalog());
        var specs = pane.HeaderCells();
        var placed = MenuPanelLayout.CellColumns(rowWidth, specs);
        AssertNoOverlap(placed, rowWidth);
        foreach (var spec in specs.Where(s => s.Kind != CellKind.Spacer && s.Text.Length > 0))
        {
            var cell = placed.Single(p => p.Key == spec.Key);
            if (!cell.Visible) continue;
            // Every sortable column may carry the indicator: measure the widest caption it can show.
            string caption = CellLayout.HeaderCaption(spec.Text, sorted: !string.IsNullOrEmpty(spec.SortKey));
            float need = CellLayout.EstimateTextWidth(caption, CellLayout.HeaderFontSize);
            Assert.True(need <= cell.Width - 4f + .01f, spec.Key + " header '" + caption + "' needs " + need + " in " + cell.Width + " at " + name);
        }
        var downloads = placed.Single(p => p.Key == InstallModsPane.CellDownloads);
        Assert.True(downloads.Visible);
        Assert.InRange(downloads.Width, InstallModsPane.DownloadsWidth - .01f, InstallModsPane.DownloadsWidth + .01f);
        // INSTALLCOLS1: 84 px is the narrowest width `DOWNLOADS ▼` fits (the row is 1236 px live).
        Assert.True(InstallModsPane.DownloadsWidth >= 84f);
        Assert.Equal("DOWNLOADS" + CellLayout.SortIndicator, CellLayout.HeaderCaption(InstallModsCopy.HeaderDownloads, sorted: true));
        // Revert: DownloadsWidth = 100 (the gen 8 `DOWNLOAD…`) fails the fit at either frame.
    }

    [Fact]
    public void Update_note_under_the_name_warns_only_when_the_listing_misses_the_current_update()
    {
        var pane = new InstallModsPane(new FakeCatalog());
        Assert.Equal("Oakveil Update", pane.CurrentUpdateTag);
        var current = InstallListViewTests.Listing("a-c", "C", categories: new[] { "Server", "Gloomrot Update", "1.0 Update", "Oakveil Update" });
        var stale = InstallListViewTests.Listing("a-s", "S", categories: new[] { "Server", "Gloomrot Update", "1.0 Update" });
        var untagged = InstallListViewTests.Listing("a-u", "U", categories: new[] { "Server" });

        // INSTALLCOLS1: nothing when the listing carries the current update (defect 4: the Update
        // column said `Oakveil` on every row); `Built for <last tag>` when it does not.
        Assert.Equal("", InstallModsPane.UpdateNoteText(current, "Oakveil Update"));
        Assert.Equal("Built for 1.0", InstallModsPane.UpdateNoteText(stale, "Oakveil Update"));
        Assert.Equal(InstallModsCopy.NoUpdateTag, InstallModsPane.UpdateNoteText(untagged, "Oakveil Update"));
        // With no current tag known there is nothing to warn against.
        Assert.Equal("", InstallModsPane.UpdateNoteText(stale, null));

        // The one placement: the lead of the name cell's second line, in the warn tone, before
        // the one-liner. There is no Update column and no note in the Status column.
        var currentName = pane.RowCells(current).Single(c => c.Key == InstallModsPane.CellName);
        Assert.Equal("", currentName.NoteLead);
        var staleCells = pane.RowCells(stale);
        var staleName = staleCells.Single(c => c.Key == InstallModsPane.CellName);
        Assert.Equal("Built for 1.0", staleName.NoteLead);
        Assert.Equal(CellTone.Warn, staleName.NoteLeadTone);
        Assert.Equal(CellWrap.TwoLine, staleName.Wrap);
        Assert.DoesNotContain(staleCells, c => c.Key == "update");
        Assert.DoesNotContain("Built for", staleCells.Single(c => c.Key == InstallModsPane.CellClient).Text);
        Assert.DoesNotContain("Built for", staleCells.Single(c => c.Key == InstallModsPane.CellServer).Text);
        Assert.Equal("Built for 1.0", pane.ListingToWidget(stale)["updateNote"]);
        Assert.Equal("", pane.ListingToWidget(current)["updateNote"]);
        // The drawer's Update tag fact still lists every tag the author set.
        Assert.Equal("Gloomrot, 1.0, Oakveil", InstallModsPane.UpdateTagText(current));
        Assert.Equal(InstallModsCopy.UpdateTagNone, InstallModsPane.UpdateTagText(untagged));
        // Revert: an Update column back in the row, or a note on a current listing, fails this.
    }

    // ---- Items 6 and 8: full-width, title-less pane --------------------------------------

    [Theory]
    [MemberData(nameof(Frames))]
    public void Full_width_mode_gives_the_Install_pane_the_whole_frame_and_leaves_the_other_panes_split(string name, float width, float height, string path)
    {
        var frame = Frame(width, height, path);
        var full = MenuPanelLayout.ForFrame(frame, sidebar: false, MenuPaneMode.FullWidth);
        var split = MenuPanelLayout.ForFrame(frame, sidebar: false, MenuPaneMode.Split);
        var sidebar = MenuPanelLayout.ForFrame(frame, sidebar: true, MenuPaneMode.FullWidth);
        MenuPanelLayout.Split(frame.Width, out float sidebarWidth, out float detailWidth, out float gap);
        // The viewport is the frame's inner width: the pane spans the frame and is centered on it.
        Assert.InRange(full.Width, frame.Width - .01f, frame.Width + .01f);
        Assert.InRange(full.X, frame.X - .01f, frame.X + .01f);
        Assert.InRange(full.RowWidth, frame.Width - PanelLayout.PadSide * 2f - .01f, frame.Width - PanelLayout.PadSide * 2f + .01f);
        Assert.Equal(split.Height, full.Height);
        Assert.Equal(split.Y, full.Y);
        Assert.True(full.Width > split.Width + sidebarWidth - .01f, "full width does not absorb the sidebar column at " + name);
        // The split geometry is untouched, and a sidebar keeps its column whatever mode is asked for.
        Assert.InRange(split.Width, detailWidth - .01f, detailWidth + .01f);
        Assert.InRange(sidebar.Width, sidebarWidth - .01f, sidebarWidth + .01f);
        Assert.Equal(MenuPanelLayout.ForFrame(frame, sidebar: true).Width, sidebar.Width);
        Assert.Equal(MenuPanelLayout.ForFrame(frame, sidebar: false).Width, split.Width);
        Assert.True(gap > 0f);
        // No title band on the full-width pane: the whole pane height is viewport before the pinned chrome.
        Assert.Equal(full.Height, MenuPanelLayout.PaneViewportHeight(full.Height, MenuPaneMode.FullWidth));
        Assert.Equal(PanelLayout.ViewportHeightFor(split.Height), MenuPanelLayout.PaneViewportHeight(split.Height, MenuPaneMode.Split));
        Assert.Equal("full-width", MenuPaneModes.Name(MenuPaneMode.FullWidth));
        Assert.Equal("split", MenuPaneModes.Name(MenuPaneMode.Split));
        Assert.Equal(MenuPaneMode.FullWidth, InstallModsPane.PaneMode);
    }

    [Fact]
    public void Only_the_Install_pane_is_configured_full_width_and_the_runtime_hides_its_title()
    {
        // The facade hands the mode to the runtime; the bool overload is the split mode.
        var panel = NewPanel();
        UiApi.ConfigureMenuPanel(panel, sidebar: false, MenuPaneMode.FullWidth);
        UiApi.ConfigureMenuPanel(panel, sidebar: false);
        UiApi.ConfigureMenuPanel(panel, sidebar: true);
        Assert.Equal(new[] { (false, MenuPaneMode.FullWidth), (false, MenuPaneMode.Split), (true, MenuPaneMode.Split) }, _runtime.ConfiguredPanes.ToArray());

        string menu = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "ModsMenu.cs");
        string install = SourcePin.MethodBody(menu, "private static void BuildInstallDetail");
        SourcePin.AssertUncommented(install, "Ui.ConfigureMenuPanel(panel, sidebar: false, InstallModsPane.PaneMode)");
        SourcePin.AssertNoStatementContaining(SourcePin.MethodBody(menu, "private static void BuildConfigurationPanes"), "PaneMode");
        SourcePin.AssertNoStatementContaining(SourcePin.MethodBody(menu, "private static void BuildSidebar"), "PaneMode");
        SourcePin.AssertNoStatementContaining(SourcePin.MethodBody(menu, "private static void BuildConfigurationPanes"), "FullWidth");
        SourcePin.AssertUncommented(SourcePin.MethodBody(menu, "private static int InstallViewportRows"), "InstallModsPane.PaneMode");
        SourcePin.AssertUncommented(SourcePin.MethodBody(menu, "public static object DescribeGeometry"), "layout = MenuPaneModes.Name(paneMode)");

        string runtime = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "UnityUiRuntime.cs");
        string configure = SourcePin.MethodBody(runtime, "public void ConfigureMenu(bool sidebar, MenuPaneMode mode)");
        SourcePin.AssertUncommented(configure, "_mode = sidebar ? MenuPaneMode.Split : mode");
        SourcePin.AssertUncommented(configure, "Title.SetActive(false)");
        // KIT1 gen 1 added a second way to reach a zero band — a Split pane that draws its own name
        // asks for it (`Ui.HidePaneTitle`, docs/ui.md) — so the pin is the full-width half of the
        // expression. What it still catches is unchanged: a full-width pane that keeps its title band.
        // KEYS3: the Keybinds tab's list and description panes are titleless too (MenuPaneModes.Titleless).
        SourcePin.AssertUncommented(runtime, "_menuPane && (MenuPaneModes.Titleless(_mode) || _hideTitle) ? 0f : PanelLayout.HeaderHeight");
        Assert.True(MenuPaneModes.Titleless(MenuPaneMode.FullWidth));
        Assert.False(MenuPaneModes.Titleless(MenuPaneMode.Split));
        SourcePin.AssertUncommented(SourcePin.MethodBody(runtime, "private void FitChromeInsets"), "float top = WorkspaceHostLayout.ListViewportTop(TitleBand, topChrome, PlacedPinnedTopRows())");
        SourcePin.AssertUncommented(SourcePin.MethodBody(runtime, "private void UpdateMenuGeometry"), "MenuPanelLayout.ForFrame(frame, _sidebar, _mode)");
        // ALIGN2 stands: the runtime never writes TMP spacing.
        SourcePin.AssertNoStatementContaining(SourcePin.MethodBody(runtime, "private void BuildCell(Cell cell)"), "characterSpacing =");
        SourcePin.AssertNoStatementContaining(SourcePin.MethodBody(runtime, "private void BuildCell(Cell cell)"), "wordSpacing =");
        // Revert: configuring Configuration or Mods full-width, or a title band kept in full-width mode, fails this.
    }

    // ---- Items 7, 8 and 9: the row budget ------------------------------------------------

    [Fact]
    public void At_least_ten_listing_rows_fit_the_916_px_native_frame()
    {
        var pane = MenuPanelLayout.ForFrame(Frame(1664f, 916f, ModsWorkspaceFrame.PathNative), sidebar: false, InstallModsPane.PaneMode);
        int rows = InstallModsPane.VisibleListingRows(pane.Height);
        Assert.True(rows >= 10, "only " + rows + " rows fit (" + InstallModsPane.ListViewportHeight(pane.Height) + " px list)");
        Assert.Equal(3, InstallModsPane.TopChromeRows);
        Assert.Equal(0, InstallModsPane.FooterRows);
        Assert.Empty(InstallModsPane.SidebarFooterCaptions);
        // The pending bar (two rows) still leaves ten.
        Assert.True(InstallModsPane.VisibleListingRows(pane.Height, pendingBar: true) >= 10);
        // The LIST2 chrome — title band, four top rows and the two-row footer — was the 8-row view of the gen 8 capture.
        float list2 = PanelLayout.ViewportHeightFor(pane.Height) - MenuPanelLayout.ChromeHeight(4) - MenuPanelLayout.ChromeHeight(2);
        int list2Rows = (int)Math.Floor((list2 + PanelLayout.RowGap) / (CellLayout.TwoLineRowHeight + PanelLayout.RowGap));
        Assert.True(list2Rows < 10, "the LIST2 chrome already fit " + list2Rows + " rows");
        Assert.Equal(53f, CellLayout.TwoLineRowHeight);

        string src = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "InstallModsPane.cs");
        string build = SourcePin.MethodBody(src, "public void Build(");
        SourcePin.AssertNoStatementContaining(build, "panel.Input(");
        SourcePin.AssertNoStatementContaining(build, "RefreshIndex");
        SourcePin.AssertNoStatementContaining(build, "CloseMods");
        SourcePin.AssertNoStatementContaining(src, "InstallModsCopy.RefreshIndex).OnClick");
        // INSTALL2 item 5: the bar now also appears for a pending uninstall, so the early return
        // asks about both totals. What this pin is protecting is unchanged: no pending, no bar, and
        // therefore no two rows of chrome eating the list.
        SourcePin.AssertUncommented(
            SourcePin.MethodBody(src, "private void BuildPendingBar"), "if (pending <= 0 && uninstall <= 0) return");
        // Revert: a separate search row, a footer, or a title band on the Install pane fails the ten-row budget.
    }

    // ---- Item 10: the automatic index refresh ----------------------------------------------

    [Fact]
    public async Task Index_refresher_rebuilt_with_the_catalog_keeps_the_session_check()
    {
        // TSTRAFFIC1: joining a local server rebuilds the catalog and its refresher; the rebuild must not check again.
        var clock = new FixedClock(new DateTime(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc));
        CatalogLoadResult Load(string json, string? stamp) => new(true, null, 1, stamp);
        Task<CatalogTextFetchResult> Fetch(CancellationToken ct) => Task.FromResult(new CatalogTextFetchResult(true, null, "[]", 2, 200));
        var first = new InstallIndexRefresher(_root, Load, Fetch, clock);
        Assert.True((await first.RefreshAsync(CancellationToken.None)).Ok);

        var rebuilt = new InstallIndexRefresher(_root, Load, Fetch, clock);
        Assert.True(rebuilt.ShouldFetch(clock.UtcNow));
        rebuilt.InheritSession(first);
        Assert.True(rebuilt.FetchedThisSession);
        Assert.False(rebuilt.ShouldFetch(clock.UtcNow.AddMinutes(5)));
        Assert.True(rebuilt.ShouldFetch(clock.UtcNow + InstallIndexRefresher.RefreshInterval + TimeSpan.FromMinutes(1)));
        // The new catalog is still served the cache.
        Assert.False(rebuilt.CacheServed);
        Assert.True(rebuilt.TryServeCache(out _));

        // Nothing fetched before: nothing to carry.
        var fresh = new InstallIndexRefresher(_root, Load, Fetch, clock);
        var again = new InstallIndexRefresher(_root, Load, Fetch, clock);
        again.InheritSession(fresh);
        Assert.True(again.ShouldFetch(clock.UtcNow));
    }

    [Fact]
    public async Task Index_refresher_serves_the_cache_at_once_replaces_it_when_the_fetch_lands_and_refetches_only_when_stale()
    {
        var clock = new FixedClock(new DateTime(2026, 9, 12, 12, 0, 0, DateTimeKind.Utc));
        var loads = new List<(string Json, string? Stamp)>();
        var fetches = 0;
        string fetched = "[{\"fetched\":true}]";
        var refresher = new InstallIndexRefresher(
            _root,
            (json, stamp) => { loads.Add((json, stamp)); return new CatalogLoadResult(true, null, 1, stamp); },
            ct => { fetches++; return Task.FromResult(new CatalogTextFetchResult(true, null, fetched, fetched.Length, 200)); },
            clock);
        Assert.Equal(Path.Combine(_root, "progenitor-cache", "index.json"), refresher.IndexPath);
        Assert.Equal(Path.Combine(_root, "progenitor-cache", "index.stamp"), refresher.StampPath);

        // No cache yet: nothing is served, the first open fetches.
        Assert.False(refresher.TryServeCache(out _));
        Assert.True(refresher.ShouldFetch(clock.UtcNow));
        Assert.Empty(loads);

        // A cached index from an earlier session (an hour old) is served at once on open.
        Directory.CreateDirectory(Path.GetDirectoryName(refresher.IndexPath!)!);
        File.WriteAllText(refresher.IndexPath!, "[{\"cached\":true}]");
        File.WriteAllText(refresher.StampPath!, CatalogFetcher.FormatStamp(clock.UtcNow.AddHours(-1)));
        Assert.True(refresher.TryServeCache(out var served));
        Assert.True(served.Ok);
        Assert.Single(loads);
        Assert.Equal("[{\"cached\":true}]", loads[0].Json);
        Assert.Equal(CatalogFetcher.FormatStamp(clock.UtcNow.AddHours(-1)), loads[0].Stamp);
        Assert.True(refresher.CacheServed);
        Assert.Equal(clock.UtcNow.AddHours(-1), refresher.CacheStampUtc);
        Assert.False(refresher.TryServeCache(out _));
        Assert.Single(loads);
        // The first open of the session fetches even though the cache is younger than the interval.
        Assert.True(InstallIndexRefresher.RefreshInterval > TimeSpan.FromHours(1) || refresher.ShouldFetch(clock.UtcNow));
        Assert.True(refresher.ShouldFetch(clock.UtcNow));

        // The fetch lands: the catalog gets the new body, the cache is rewritten with the fetch time.
        var result = await refresher.RefreshAsync(CancellationToken.None);
        Assert.True(result.Ok);
        Assert.Equal(1, fetches);
        Assert.Equal(2, loads.Count);
        Assert.Equal(fetched, loads[1].Json);
        Assert.Equal(CatalogFetcher.FormatStamp(clock.UtcNow), loads[1].Stamp);
        Assert.Equal(fetched, File.ReadAllText(refresher.IndexPath!));
        Assert.Equal(CatalogFetcher.FormatStamp(clock.UtcNow), File.ReadAllText(refresher.StampPath!));
        Assert.True(refresher.FetchedThisSession);
        Assert.Equal(clock.UtcNow, refresher.CacheStampUtc);

        // A second open within the interval does not refetch; past the interval it does.
        Assert.False(refresher.ShouldFetch(clock.UtcNow.AddMinutes(5)));
        Assert.False(refresher.ShouldFetch(clock.UtcNow + InstallIndexRefresher.RefreshInterval));
        Assert.True(refresher.ShouldFetch(clock.UtcNow + InstallIndexRefresher.RefreshInterval + TimeSpan.FromMinutes(1)));
        Assert.True(InstallIndexRefresher.RefreshInterval >= ReadmePrefetchScheduler.MinSpacing);

        // A failed fetch leaves the cache and the catalog alone.
        var failing = new InstallIndexRefresher(
            _root,
            (json, stamp) => { loads.Add((json, stamp)); return new CatalogLoadResult(true, null, 1, stamp); },
            ct => Task.FromResult(new CatalogTextFetchResult(false, InstallReasons.FetchTransport, null, 0, 503)),
            clock);
        var failed = await failing.RefreshAsync(CancellationToken.None);
        Assert.False(failed.Ok);
        Assert.Equal(InstallReasons.FetchTransport, failed.Error);
        Assert.Equal(2, loads.Count);
        Assert.Equal(fetched, File.ReadAllText(refresher.IndexPath!));
        Assert.False(failing.FetchedThisSession);
        Assert.True(failing.ShouldFetch(clock.UtcNow));

        // A new session finds the fetched index on disk and serves it.
        var later = new InstallIndexRefresher(_root, (json, stamp) => { loads.Add((json, stamp)); return new CatalogLoadResult(true, null, 1, stamp); }, ct => throw new InvalidOperationException("not fetched"), clock);
        Assert.True(later.TryServeCache(out _));
        Assert.Equal(fetched, loads[2].Json);
        Assert.True(later.ShouldFetch(clock.UtcNow));

        // ModsMenu: once per open, reset on Close; no manual refresh row; Retry on the error surface is the one manual path.
        string menu = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "ModsMenu.cs");
        SourcePin.AssertUncommented(SourcePin.MethodBody(menu, "private static IUiPanel Begin("), "EnsureIndexForOpen()");
        string open = SourcePin.MethodBody(menu, "private static void EnsureIndexForOpen");
        SourcePin.AssertUncommented(open, "if (_indexOpenHandled) return");
        SourcePin.AssertUncommented(open, "refresher.TryServeCache(out var cached)");
        SourcePin.AssertUncommented(open, "if (refresher.ShouldFetch(DateTime.UtcNow)) QueueInstallIndexLoad()");
        SourcePin.AssertUncommented(SourcePin.MethodBody(menu, "public static void Close("), "_indexOpenHandled = false");
        SourcePin.AssertNoStatementContaining(SourcePin.MethodBody(menu, "internal static void SelectWorkspaceTab"), "EnsureIndexForOpen");
        SourcePin.AssertUncommented(SourcePin.MethodBody(menu, "private static void QueueInstallIndexLoad"), "pane.BeginIndexRefresh(refresher, ct, force)");
        // Revert: fetching on every open (no session latch), or serving the cache only after the fetch, fails this.
    }

    [Fact]
    public void Pane_keeps_a_served_list_while_a_refresh_runs_and_swaps_it_when_the_refresh_lands()
    {
        var catalog = new FakeCatalog();
        catalog.Entries.Add(InstallListViewTests.Listing("o-cached", "Cached", categories: new[] { "Oakveil Update" }));
        var pane = new InstallModsPane(catalog, new NeverClock(), null);
        // The cache was served (NoteLoadResult ok): the list is Ready before any fetch.
        pane.NoteLoadResult(new CatalogLoadResult(true, null, 1, "stamp"));
        Assert.Equal(InstallPaneSurface.Ready, pane.Surface(true, false));

        var gate = new TaskCompletionSource<CatalogTextFetchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var refresher = new InstallIndexRefresher(
            null,
            (json, stamp) =>
            {
                catalog.Entries.Clear();
                catalog.Entries.Add(InstallListViewTests.Listing("o-fresh", "Fresh", categories: new[] { "Oakveil Update" }));
                return new CatalogLoadResult(true, null, 1, stamp);
            },
            ct => gate.Task,
            new FixedClock(new DateTime(2026, 9, 12, 12, 0, 0, DateTimeKind.Utc)));
        var task = pane.BeginIndexRefresh(refresher, CancellationToken.None);
        Assert.True(pane.IndexRefreshing);
        Assert.True(refresher.Fetching);
        // Meanwhile the cached rows stay: the surface is Ready, the build shows them.
        Assert.Equal(InstallPaneSurface.Ready, pane.Surface(true, false));
        pane.Build(NewPanel(), true, false, null, () => { }, () => { }, DateTime.UtcNow);
        Assert.Equal(new[] { "o-cached" }, pane.Rows.Select(r => r.Row.Key).ToArray());

        // A failure over the served list keeps it (no error surface).
        var failing = new InstallIndexRefresher(null, (json, stamp) => new CatalogLoadResult(true, null, 1, stamp), ct => Task.FromResult(new CatalogTextFetchResult(false, "offline", null, 0, 0)));
        var failedTask = pane.BeginIndexRefresh(failing, CancellationToken.None);
        pane.NoteLoadResult(failedTask.Result);
        Assert.Null(pane.LastError);
        Assert.Equal(InstallPaneSurface.Ready, pane.Surface(true, false));
        Assert.False(pane.IndexRefreshing);

        // The fetch lands: the list is rebuilt from the new index.
        gate.SetResult(new CatalogTextFetchResult(true, null, "[]", 2, 200));
        pane.NoteLoadResult(task.Result);
        Assert.True(task.Result.Ok);
        pane.Build(NewPanel(), true, false, null, () => { }, () => { }, DateTime.UtcNow);
        Assert.Equal(new[] { "o-fresh" }, pane.Rows.Select(r => r.Row.Key).ToArray());

        // With nothing on show a running refresh reads Loading; a failed one is the error surface with Retry.
        var empty = new FakeCatalog();
        var cold = new InstallModsPane(empty, new NeverClock(), null);
        Assert.Equal(InstallPaneSurface.IndexNotLoaded, cold.Surface(true, false));
        var pending = new TaskCompletionSource<CatalogTextFetchResult>();
        var coldRefresh = new InstallIndexRefresher(null, (json, stamp) => new CatalogLoadResult(true, null, 0, stamp), ct => pending.Task);
        cold.BeginIndexRefresh(coldRefresh, CancellationToken.None);
        Assert.Equal(InstallPaneSurface.Loading, cold.Surface(true, false));
        cold.NoteLoadResult(new CatalogLoadResult(false, "offline", 0, null));
        Assert.Equal(InstallPaneSurface.Error, cold.Surface(true, false));
        Assert.Equal("offline", cold.LastError);
        pending.SetCanceled();
        // Revert: an error surface over cached rows, or Loading over a served list, fails this.
    }

    // ---- Helpers ---------------------------------------------------------------------------

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

    private IUiPanel NewPanel()
    {
        var panel = UiApi.Panel("Install mods", _owner, "example.list3");
        _panels.Add(panel);
        return panel;
    }

    private sealed class FixedClock : ICatalogClock
    {
        public FixedClock(DateTime utcNow) { UtcNow = utcNow; }
        public DateTime UtcNow { get; }
        public Task Delay(TimeSpan duration, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>A clock whose delay never elapses (it only cancels): the README run parks after its first key, off the test thread.</summary>
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

    /// <summary>A catalog over a list the test edits; no documents, no downloads.</summary>
    private sealed class FakeCatalog : IInstallCatalog
    {
        public List<CatalogEntry> Entries { get; } = new();

        public InstallProgress Status => InstallProgress.Idle;

        public Task<CatalogLoadResult> LoadIndexAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new CatalogLoadResult(true, null, Entries.Count, "stamp"));

        public IReadOnlyList<CatalogEntry> Search(string? query) => Entries.ToArray();

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
            Task.FromResult(ListingDocumentSet.Unavailable(key ?? "", InstallReasons.FetchCancelled));
    }

    // ---- LIMITS1: typing in the search field ----------------------------------------------------

    /// <summary>
    /// Live: each keystroke in Search mods cost 114–191 ms in the input handler (re-filter plus the
    /// Show counts re-deciding every row's side cells). Now a keystroke only records the text (the field
    /// echoes it at once); the list re-filters once typing pauses, in place, touching only the rows
    /// whose visibility changes, and the side decisions are reused across re-filters.
    /// </summary>
    [Fact]
    public void Typing_debounces_the_refilter_and_filters_rows_in_place_without_a_rebuild()
    {
        var catalog = new FakeCatalog();
        for (int i = 0; i < 150; i++)
            catalog.Entries.Add(InstallListViewTests.Listing("o-m" + i, (i % 10 == 0 ? "Blood " : "Pack ") + i, description: "one-liner " + i,
                categories: new[] { "Oakveil Update" }));
        var pane = new InstallModsPane(catalog, new NeverClock(), null);
        var delayed = new List<(double Seconds, Action Action)>();
        pane.PostDelayedHook = (seconds, action) => delayed.Add((seconds, action));
        var panel = NewPanel();
        pane.Build(panel, true, false, null, () => { }, () => { }, DateTime.UtcNow);
        var toolbar = (UiCellRowHandle)pane.ToolbarRow!;
        int hosted = _runtime.Rows.Count;
        ushort slot = toolbar.CellSlotId(InstallModsPane.CellSearch);
        var typing0 = pane.TypingToWidget();
        int refilters0 = (int)typing0["refilters"]!;

        // Three keystrokes: the field takes each at once; nothing is re-filtered yet.
        foreach (string text in new[] { "b", "bl", "blo" })
            UiSlotTable.BootstrapDispatch(slot, (byte)UiSlotKind.Text, 0f, false, text);
        Assert.Equal("blo", pane.Search);
        Assert.Equal("blo", pane.SearchCell!.Text);
        Assert.True(pane.SearchRefilterPending);
        Assert.Equal(refilters0, (int)pane.TypingToWidget()["refilters"]!);
        Assert.True(pane.Rows.Single(r => r.Row.Key == "o-m1").Row.Visible);
        Assert.Equal(3, delayed.Count);
        Assert.All(delayed, d => Assert.Equal(InstallModsPane.SearchDebounceSeconds, d.Seconds));

        // The two superseded timers do nothing; the last one re-filters once.
        delayed[0].Action();
        delayed[1].Action();
        Assert.Equal(refilters0, (int)pane.TypingToWidget()["refilters"]!);
        delayed[2].Action();
        var typing = pane.TypingToWidget();
        Assert.Equal(refilters0 + 1, (int)typing["refilters"]!);
        Assert.False(pane.SearchRefilterPending);
        Assert.False((bool)typing["pending"]!);
        // In place: the same rows, no new panel rows; 135 of the 150 rows were hidden.
        Assert.Equal(hosted, _runtime.Rows.Count);
        Assert.False(pane.Rows.Single(r => r.Row.Key == "o-m1").Row.Visible);
        Assert.True(pane.Rows.Single(r => r.Row.Key == "o-m10").Row.Visible);
        Assert.Equal(135, (int)typing["lastChangedRows"]!);

        // A further letter that changes nothing touches no row.
        UiSlotTable.BootstrapDispatch(slot, (byte)UiSlotKind.Text, 0f, false, "bloo");
        delayed[^1].Action();
        Assert.Equal(0, (int)pane.TypingToWidget()["lastChangedRows"]!);
        // The Show counts reuse the decisions: one per listing the search keeps, kept across re-filters.
        Assert.Equal(15, pane.SideCacheCount);

        // Game-free cost (the fake runtime has no Unity layout; the live number is the readback's).
        // Best of 20, not the mean: a preempted run on a busy CI runner must not decide the result.
        double perRefilter = double.MaxValue;
        for (int i = 0; i < 20; i++)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            pane.RefilterListings();
            perRefilter = Math.Min(perRefilter, watch.Elapsed.TotalMilliseconds);
        }
        pane.InvalidateSideCache();
        var cold = System.Diagnostics.Stopwatch.StartNew();
        pane.RefilterListings();
        cold.Stop();
        Assert.True(perRefilter < 50, "warm refilter " + perRefilter + " ms; cold " + cold.Elapsed.TotalMilliseconds + " ms");
        Assert.Equal(InstallModsPane.RefilterBudgetMs, (double)pane.TypingToWidget()["budgetMs"]!);
    }

    [Fact]
    public void Without_a_delayed_marshaler_a_keystroke_refilters_at_once()
    {
        var catalog = new FakeCatalog();
        catalog.Entries.Add(InstallListViewTests.Listing("o-blood", "Blood Pack", categories: new[] { "Oakveil Update" }));
        catalog.Entries.Add(InstallListViewTests.Listing("o-other", "Other Pack", categories: new[] { "Oakveil Update" }));
        var pane = new InstallModsPane(catalog, new NeverClock(), null);
        pane.Build(NewPanel(), true, false, null, () => { }, () => { }, DateTime.UtcNow);
        var toolbar = (UiCellRowHandle)pane.ToolbarRow!;
        UiSlotTable.BootstrapDispatch(toolbar.CellSlotId(InstallModsPane.CellSearch), (byte)UiSlotKind.Text, 0f, false, "blood");
        Assert.False(pane.SearchRefilterPending);
        Assert.False(pane.Rows.Single(r => r.Row.Key == "o-other").Row.Visible);
    }

    // ---- SEARCHCLEAR1: the Install search's clear button, Escape and reset on close ------------

    [Fact]
    public void The_install_search_is_a_clearable_search_box()
    {
        var catalog = new FakeCatalog();
        catalog.Entries.Add(InstallListViewTests.Listing("o-blood", "Blood Pack", categories: new[] { "Oakveil Update" }));
        var pane = new InstallModsPane(catalog, new NeverClock(), null);
        var search = pane.ToolbarCells().Single(c => c.Key == InstallModsPane.CellSearch);
        Assert.Equal(CellKind.Input, search.Kind);
        Assert.True(search.Clearable);
        // The other toolbar cells are not search boxes.
        Assert.DoesNotContain(pane.ToolbarCells(), c => c.Key != InstallModsPane.CellSearch && c.Clearable);
    }

    [Fact]
    public void An_emptied_install_search_shows_every_row_at_once_while_typing_still_waits()
    {
        var catalog = new FakeCatalog();
        catalog.Entries.Add(InstallListViewTests.Listing("o-blood", "Blood Pack", categories: new[] { "Oakveil Update" }));
        catalog.Entries.Add(InstallListViewTests.Listing("o-other", "Other Pack", categories: new[] { "Oakveil Update" }));
        var pane = new InstallModsPane(catalog, new NeverClock(), null);
        var delayed = new List<(double Seconds, Action Action)>();
        pane.PostDelayedHook = (seconds, action) => delayed.Add((seconds, action));
        pane.Build(NewPanel(), true, false, null, () => { }, () => { }, DateTime.UtcNow);
        var toolbar = (UiCellRowHandle)pane.ToolbarRow!;
        ushort slot = toolbar.CellSlotId(InstallModsPane.CellSearch);

        UiSlotTable.BootstrapDispatch(slot, (byte)UiSlotKind.Text, 0f, false, "blood");
        Assert.True(pane.SearchRefilterPending);
        delayed[^1].Action();
        Assert.False(pane.Rows.Single(r => r.Row.Key == "o-other").Row.Visible);

        // The clear button (or the last Backspace) empties the box through the same event: no wait.
        UiSlotTable.BootstrapDispatch(slot, (byte)UiSlotKind.Text, 0f, false, "");
        Assert.Equal("", pane.Search);
        Assert.False(pane.SearchRefilterPending);
        Assert.True(pane.Rows.Single(r => r.Row.Key == "o-other").Row.Visible);
        Assert.Single(delayed);
    }

    [Fact]
    public void The_install_search_resets_on_close_and_a_pending_refilter_is_dropped()
    {
        var catalog = new FakeCatalog();
        catalog.Entries.Add(InstallListViewTests.Listing("o-blood", "Blood Pack", categories: new[] { "Oakveil Update" }));
        catalog.Entries.Add(InstallListViewTests.Listing("o-other", "Other Pack", categories: new[] { "Oakveil Update" }));
        var pane = new InstallModsPane(catalog, new NeverClock(), null);
        var delayed = new List<(double Seconds, Action Action)>();
        pane.PostDelayedHook = (seconds, action) => delayed.Add((seconds, action));
        pane.Build(NewPanel(), true, false, null, () => { }, () => { }, DateTime.UtcNow);
        var toolbar = (UiCellRowHandle)pane.ToolbarRow!;
        UiSlotTable.BootstrapDispatch(toolbar.CellSlotId(InstallModsPane.CellSearch), (byte)UiSlotKind.Text, 0f, false, "upd");
        Assert.True(pane.SearchRefilterPending);

        pane.ResetSearchForClose();
        Assert.Equal("", pane.Search);
        Assert.False(pane.SearchRefilterPending);
        Assert.True(pane.Rows.Single(r => r.Row.Key == "o-other").Row.Visible);
        // The superseded timer does not bring the old search back.
        delayed[^1].Action();
        Assert.Equal("", pane.Search);
        Assert.True(pane.Rows.Single(r => r.Row.Key == "o-blood").Row.Visible);
    }

    [Fact]
    public void The_remembered_install_filters_never_carry_the_search()
    {
        // SEARCHCLEAR1 decision: the Preferences switch remembers filters and sort, not the search text,
        // so a remembered session cannot reopen on a stale search.
        var filters = new InstallSearchFilters();
        string text = InstallFilterMemory.Encode(InstallFilterSnapshot.Of(filters, InstallSimpleShow.All, InstallSort.Released, false));
        Assert.DoesNotContain("search", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("query", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(typeof(InstallFilterSnapshot).GetProperties(), p => p.Name.Contains("Search", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_side_decision_cache_is_dropped_whenever_a_decision_can_change()
    {
        string pane = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "InstallModsPane.cs");
        SourcePin.AssertUncommented(SourcePin.MethodBody(pane, "public void SetInstalledQuery("), "InvalidateSideCache();");
        SourcePin.AssertUncommented(SourcePin.MethodBody(pane, "public void Build("), "InvalidateSideCache();");
        SourcePin.AssertUncommented(SourcePin.MethodBody(pane, "internal void NoteRemoteChanged()"), "InvalidateSideCache();");
        SourcePin.AssertUncommented(SourcePin.MethodBody(pane, "internal bool PickServer(string id)"), "InvalidateSideCache();");
        SourcePin.AssertUncommented(SourcePin.MethodBody(pane, "private void SyncActionCells(CatalogEntry entry)"), "_sideCache.Remove(entry.Key);");
    }
}
