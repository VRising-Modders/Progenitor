using System;
using System.Collections.Generic;
using Progenitor.Dev.Ops;
using Progenitor.Install;
using Progenitor.Networking;
using Progenitor.Plugins;
using Progenitor.Ui;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// UIHDR2 kit geometry (the pinned header band the Configuration/consent panes still use), the LIST2 Install button captions,
/// the measured <c>rects</c> path and the release date on the list view.
/// Gen 39 evidence 117: the title started left of the pane, the caption drew
/// beside an empty button box and "released" showed the index stamp.
/// </summary>
public sealed class ListingHeaderGeometryTests
{
    /// <summary>LIST2: every caption the row and drawer Install buttons can show.</summary>
    private static readonly string[] KnownCaptions =
    {
        InstallModsCopy.Install,
        InstallModsCopy.InstallRestart,
        InstallModsCopy.Installing,
        InstallModsCopy.Installed,
        InstallModsCopy.InstalledPending,
        InstallModsCopy.UpdateRestart,
        InstallModsCopy.ToolNotPlugin,
        InstallModsCopy.CannotInstall,
        InstallModsCopy.Remove,
        InstallModsCopy.RemoveRestartToUnload,
    };

    [Theory]
    [InlineData(3840f, 1080f)]
    [InlineData(1600f, 1024f)]
    public void Header_columns_keep_the_title_inside_the_pane_and_the_action_on_the_right(float width, float height)
    {
        var frame = ModsWorkspaceFrame.Fallback(width, height);
        var pane = MenuPanelLayout.ForFrame(frame, sidebar: false);
        float row = pane.RowWidth;
        for (int buttons = 1; buttons <= 2; buttons++)
        {
            MenuPanelLayout.HeaderColumns(row, buttons, out float labelW, out float labelX, out float actionW, out float actionX);
            float labelLeft = labelX - labelW / 2f;
            float labelRight = labelX + labelW / 2f;
            float actionLeft = actionX - actionW / 2f;
            float actionRight = actionX + actionW / 2f;
            // Both columns lie inside the row and do not overlap; the title keeps the larger half.
            Assert.InRange(labelLeft, -row / 2f - 0.01f, -row / 2f + 0.01f);
            Assert.True(labelRight < actionLeft, "title column overlaps the action column");
            Assert.InRange(actionRight, row / 2f - 0.01f, row / 2f + 0.01f);
            Assert.True(labelW > actionW, "the action column took more than the title column");
            // Each button lies inside the action column, left to right, gap between.
            float previousRight = actionLeft - 1f;
            for (int i = 0; i < buttons; i++)
            {
                MenuPanelLayout.HeaderButton(row, i, buttons, out float bx, out float bw);
                float left = bx - bw / 2f;
                float right = bx + bw / 2f;
                Assert.True(left >= actionLeft - 0.01f && right <= actionRight + 0.01f, "button outside the action column");
                Assert.True(left > previousRight, "buttons overlap");
                previousRight = right;
            }
        }

        // The single-button overload is the one-button case.
        MenuPanelLayout.HeaderColumns(row, out float w1, out float x1, out float a1, out float ax1);
        MenuPanelLayout.HeaderColumns(row, 1, out float w2, out float x2, out float a2, out float ax2);
        Assert.Equal(w1, w2);
        Assert.Equal(x1, x2);
        Assert.Equal(a1, a2);
        Assert.Equal(ax1, ax2);
        // Revert: sizing the action column as the 180 px control column, or placing the
        // heading-row at labelX so FitLabel offsets the title twice, fails this.
    }

    [Theory]
    [InlineData(3840f, 1080f)]
    [InlineData(1600f, 1024f)]
    public void Every_known_caption_fits_inside_its_button(float width, float height)
    {
        var pane = MenuPanelLayout.ForFrame(ModsWorkspaceFrame.Fallback(width, height), sidebar: false);
        MenuPanelLayout.HeaderButton(pane.RowWidth, 0, 1, out _, out float single);
        Assert.Equal(MenuPanelLayout.HeaderButtonWidth, single, 3);
        foreach (var caption in KnownCaptions)
            Assert.True(MenuPanelLayout.HeaderCaptionFits(caption, single), caption + " does not fit " + single + " px");
        // Two buttons side by side still hold the two widest captions.
        MenuPanelLayout.HeaderButton(pane.RowWidth, 0, 2, out _, out float half);
        Assert.True(MenuPanelLayout.HeaderCaptionFits(InstallModsCopy.UpdateRestart, half));
        Assert.True(MenuPanelLayout.HeaderCaptionFits(InstallModsCopy.Install, half));
        Assert.False(MenuPanelLayout.HeaderCaptionFits(new string('W', 80), single));

        string runtime = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "UnityUiRuntime.cs");
        string place = SourcePin.MethodBody(runtime, "private void PlaceHeaderRows()");
        SourcePin.AssertUncommented(place, "MenuPanelLayout.HeaderButton(");
        SourcePin.AssertUncommented(place, "FitInlineButtonCaption(row.Go)");
        SourcePin.AssertUncommented(place, "PrepareHeaderButtonCaption(row.Go)");
        SourcePin.AssertUncommented(place, "PrepareHeaderTitle(row.Label)");
        // LIST2: the Install button is a cell of the listing row and of the drawer head; every
        // known caption fits the column model's button ask (the mock's 200 px, or wider for the caption).
        foreach (var caption in KnownCaptions)
        {
            var cell = new CellSpec(CellKind.Button, "install").With(text: caption, align: CellAlign.Right);
            Assert.True(CellLayout.CaptionFits(cell, CellLayout.AutoWidth(cell)), caption + " does not fit its cell");
        }
        // The reason is a line of the drawer's body (INSTALLSIDES1), never part of a caption.
        string pane2 = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "InstallModsPane.cs");
        string lines = SourcePin.MethodBody(pane2, "internal List<CellLine> DrawerLines(");
        SourcePin.AssertUncommented(lines, "lines.Add(new CellLine(CellLineKind.Warn, action.Reason));");
        // INSTALLSIDES2 item 5: the head's one muted cell is the per-side restart note.
        string drawer = SourcePin.MethodBody(pane2, "private void BuildDrawer(");
        SourcePin.AssertUncommented(drawer, "new CellSpec(CellKind.Muted, CellNote).With(text: HeadNoteText(entry, action), width: CellWidth.Fill(80f), minWidth: 80f)");
        string headNote = SourcePin.MethodBody(pane2, "internal string HeadNoteText(CatalogEntry entry, InstallRowAction action)");
        SourcePin.AssertUncommented(headNote, "return SideCells.RestartNote(SideOf(entry), ServerTarget, CurrentRoute.Kind,");
        // Revert: a 180 px button, a 22 px caption, or "label\nreason" as the caption fails this.
    }

    [Theory]
    [InlineData(3840f, 1080f)]
    [InlineData(1600f, 1024f)]
    public void Reason_wraps_within_the_action_column_and_grows_the_band_up_to_three_lines(float width, float height)
    {
        var pane = MenuPanelLayout.ForFrame(ModsWorkspaceFrame.Fallback(width, height), sidebar: false);
        MenuPanelLayout.HeaderColumns(pane.RowWidth, 1, out _, out _, out float actionW, out _);
        Assert.Equal(0, MenuPanelLayout.HeaderReasonLines("", actionW));
        Assert.Equal(1, MenuPanelLayout.HeaderReasonLines("restart the client", actionW));
        const string ecf = "download and inspect this listing before planning an install";
        int lines = MenuPanelLayout.HeaderReasonLines(ecf, actionW);
        Assert.InRange(lines, 1, 2);
        string longReason = string.Join(" ", Array.ConvertAll(new int[40], _ => "restart"));
        Assert.Equal(MenuPanelLayout.HeaderReasonMaxLines, MenuPanelLayout.HeaderReasonLines(longReason, actionW));
        // Estimated width of each wrapped line stays inside the column.
        float glyph = MenuPanelLayout.HeaderReasonFontSize * MenuPanelLayout.AverageGlyphEm;
        Assert.True(ecf.Length * glyph / lines <= actionW + glyph * 12f);

        float two = MenuPanelLayout.PinnedHeaderHeight();
        Assert.Equal(two, MenuPanelLayout.PinnedHeaderHeightFor(0));
        Assert.True(MenuPanelLayout.PinnedHeaderHeightFor(1) >= two);
        Assert.True(MenuPanelLayout.PinnedHeaderHeightFor(3) > MenuPanelLayout.PinnedHeaderHeightFor(1));
        Assert.Equal(MenuPanelLayout.PinnedHeaderHeightFor(3), MenuPanelLayout.PinnedHeaderHeightFor(9));
        // The band still splits the pane without overlapping the viewport at the tallest size.
        MenuPanelLayout.SplitPinnedHeader(pane, MenuPanelLayout.PinnedHeaderHeightFor(3), out var header, out var viewport);
        Assert.True(MenuPanelLayout.HeaderLiesAboveViewport(pane, header, viewport));

        string runtime = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "UnityUiRuntime.cs");
        string layout = SourcePin.MethodBody(runtime, "private void RunLayout(");
        SourcePin.AssertUncommented(layout, "MenuPanelLayout.PinnedHeaderHeightFor(HeaderReasonLineCount())");
        SourcePin.AssertUncommented(layout, "EnsureHeaderBand(headerChrome");
        string place = SourcePin.MethodBody(runtime, "private void PlaceHeaderRows()");
        SourcePin.AssertUncommented(place, "PrepareHeaderReason(row.Label)");
        SourcePin.AssertUncommented(place, "right: true");
        // Revert: a fixed two-row band with the reason clipped, or the reason placed in the
        // title column, fails this.
    }

    [Fact]
    public void Rects_measure_the_live_header_objects_in_client_pixels()
    {
        // The measured path: one header band, one viewport, the title as a label and the
        // action as a button, all through AddMeasured (client px), never FromCanvasLocal.
        string ops = SourcePin.ReadFile("src", "Progenitor.Dev", "Ops", "ModsUiOps.cs");
        string add = SourcePin.MethodBody(ops, "private static void AddListingHeader(");
        SourcePin.AssertUncommented(add, "ModsMenu.ListingHeaderObjects()");
        SourcePin.AssertUncommented(add, "AddMeasured(rects, \"header\", \"header\"");
        SourcePin.AssertUncommented(add, "AddMeasured(rects, \"viewport\", \"viewport\"");
        SourcePin.AssertUncommented(add, "AddMeasured(rects, \"header-title\", \"label\"");
        SourcePin.AssertUncommented(add, "AddMeasured(rects, \"action\", \"button\"");
        Assert.DoesNotContain("FromCanvasLocal", add, StringComparison.Ordinal);
        string widgets = SourcePin.MethodBody(ops, "private static void AddContentWidgets(");
        SourcePin.AssertUncommented(widgets, "if (kind == \"header\")");
        Assert.True(ModsUiGeometry.IsWorkspaceControlId("action"));
        Assert.True(ModsUiGeometry.IsWorkspaceControlId("header-title"));

        // A pane on screen at 3840×1080 measures to non-negative client pixels; the header
        // band and the viewport share the seam and the band sits above.
        var frame = ModsWorkspaceFrame.Fallback(3840f, 1080f);
        var pane = MenuPanelLayout.ForFrame(frame, sidebar: false);
        MenuPanelLayout.SplitPinnedHeader(pane, MenuPanelLayout.PinnedHeaderHeightFor(1), out var header, out var viewport);
        var headerRect = ModsUiGeometry.FromUnityScreen(
            header.X - header.Width / 2f + 1920f, header.Y - header.Height / 2f + 540f,
            header.Width, header.Height, 1080, 1f, visible: true, raycastTarget: false);
        var viewRect = ModsUiGeometry.FromUnityScreen(
            viewport.X - viewport.Width / 2f + 1920f, viewport.Y - viewport.Height / 2f + 540f,
            viewport.Width, viewport.Height, 1080, 1f, visible: true, raycastTarget: false);
        int hx = Convert.ToInt32(headerRect["x"]), hy = Convert.ToInt32(headerRect["y"]);
        int hw = Convert.ToInt32(headerRect["width"]), hh = Convert.ToInt32(headerRect["height"]);
        int vx = Convert.ToInt32(viewRect["x"]), vy = Convert.ToInt32(viewRect["y"]);
        int vw = Convert.ToInt32(viewRect["width"]), vh = Convert.ToInt32(viewRect["height"]);
        Assert.True(hx >= 0 && hy >= 0 && vx >= 0 && vy >= 0, "an on-screen pane reported a negative client x/y");
        Assert.False(ModsUiGeometry.Overlaps(hx, hy, hw, hh, vx, vy, vw, vh));
        Assert.True(hy + hh <= vy + 1);

        string menu = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "ModsMenu.cs");
        SourcePin.AssertUncommented(menu, "public static Dictionary<string, GameObject?> ListingHeaderObjects()");
        SourcePin.AssertUncommented(menu, "UnityUiRuntime.FindShownDetailHeader(out var band, out var viewport)");
        string runtime = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "UnityUiRuntime.cs");
        SourcePin.AssertUncommented(runtime, "private void EnsureHeaderBand(float headerChrome, float topChrome)");
        SourcePin.AssertUncommented(runtime, "internal const string HeaderBandName = \"header-band\"");
        // Revert: emitting DescribeListingHeaderRects' canvas-space numbers as rects rows
        // (x=-456 at gen 39), or a second kind: header row for the title, fails this.
    }

    [Fact]
    public void Released_comes_from_the_version_date_then_the_package_date_then_says_unknown()
    {
        var now = new DateTime(2026, 9, 12, 12, 0, 0, DateTimeKind.Utc);
        _ = now;
        CatalogUpdateTags.ResetProvidersForTests();
        CatalogUpdateTags.ConfiguredTagProvider = () => null;
        CatalogUpdateTags.BuildProvider = () => null;
        try
        {
            var pane = new InstallModsPane(new EmptyCatalog()) { TodayProbe = () => new DateTime(2026, 9, 12) };
            var version = Entry(releasedUtc: "2025-08-21T09:14:34.297636Z", dateUpdated: "2026-08-21T09:14:41Z");
            Assert.Equal("2025-08-21", InstallModsPane.ReleasedDate(version, out string source));
            Assert.Equal(InstallModsPane.ReleasedSourceVersion, source);
            Assert.Equal("2025-08-21", InstallModsPane.ReleasedText(version));
            Assert.Equal("by dyno · released 2025-08-21 · loads in unknown until checked", pane.MetaLine(version));
            // INSTALLCOLS1: the list's Released column is a short plain date; the drawer keeps YYYY-MM-DD.
            Assert.Equal("Aug '25", pane.RowCells(version).Single(c => c.Key == InstallModsPane.CellReleased).Text);

            var package = Entry(releasedUtc: null, dateUpdated: "2026-08-21T09:14:41Z");
            Assert.Equal("2026-08-21", InstallModsPane.ReleasedDate(package, out source));
            Assert.Equal(InstallModsPane.ReleasedSourcePackage, source);
            Assert.Equal("by dyno · updated 2026-08-21 · loads in unknown until checked", pane.MetaLine(package));

            var none = Entry(releasedUtc: null, dateUpdated: null);
            Assert.Equal("", InstallModsPane.ReleasedDate(none, out source));
            Assert.Equal(InstallModsPane.ReleasedSourceNone, source);
            Assert.Equal(InstallModsCopy.Dash, InstallModsPane.ReleasedText(none));
            string line = pane.MetaLine(none);
            Assert.StartsWith("by dyno · released unknown · ", line, StringComparison.Ordinal);
            // The index stamp is never shown as the release date.
            Assert.DoesNotContain("2026-09-12", line, StringComparison.Ordinal);
            Assert.Equal(InstallModsCopy.Dash, pane.RowCells(none).Single(c => c.Key == InstallModsPane.CellReleased).Text);
            // Recently released sorts the version date first, then the package date, then the dateless.
            var sorted = InstallModsPane.Sorted(new[] { none, package, version }, InstallSort.Released);
            Assert.Same(package, sorted[0]);
            Assert.Same(version, sorted[1]);
            Assert.Same(none, sorted[2]);
        }
        finally
        {
            CatalogUpdateTags.ResetProvidersForTests();
        }
        // Revert: formatting IndexStamp as "released", or dropping ReleasedUtc from the adapter, fails this.
    }

    [Fact]
    public void Adapter_reads_the_version_date_created_as_ReleasedUtc()
    {
        const string json = "[{\"name\":\"ExampleCommandFramework\",\"full_name\":\"dyno-ExampleCommandFramework\",\"owner\":\"dyno\","
            + "\"date_updated\":\"2026-08-21T09:14:41.702581Z\",\"is_deprecated\":false,\"categories\":[\"Server\"],"
            + "\"versions\":[{\"name\":\"ExampleCommandFramework\",\"full_name\":\"dyno-ExampleCommandFramework-0.11.0\","
            + "\"description\":\"Command framework\",\"version_number\":\"0.11.0\",\"dependencies\":[],"
            + "\"download_url\":\"https://thunderstore.io/package/download/dyno/ExampleCommandFramework/0.11.0/\","
            + "\"date_created\":\"2025-08-21T09:14:34.297636Z\",\"file_size\":120000,\"is_active\":true}]}]";
        Assert.True(ThunderstoreAdapter.TryLoadIndex(json, "2026-09-12T00:00:00Z", out var entries, out var error), error);
        var entry = Assert.Single(entries);
        Assert.Equal("2025-08-21T09:14:34.297636Z", entry.ReleasedUtc);
        Assert.Equal("2026-08-21T09:14:41.702581Z", entry.DateUpdated);
        Assert.Equal("2025-08-21", InstallModsPane.ReleasedDate(entry, out string source));
        Assert.Equal(InstallModsPane.ReleasedSourceVersion, source);
        var inspected = entry.WithInspection(CatalogProvenance.AdaptedListing, entry.Manifest, PackageSide.Server);
        Assert.Equal(entry.ReleasedUtc, inspected.ReleasedUtc);

        const string noDate = "[{\"name\":\"P\",\"full_name\":\"o-P\",\"owner\":\"o\",\"date_updated\":\"2026-09-01T00:00:00Z\","
            + "\"versions\":[{\"name\":\"P\",\"full_name\":\"o-P-1.0.0\",\"description\":\"d\",\"version_number\":\"1.0.0\","
            + "\"dependencies\":[],\"download_url\":\"https://x/\",\"file_size\":1,\"is_active\":true}]}]";
        Assert.True(ThunderstoreAdapter.TryLoadIndex(noDate, null, out var fallback, out error), error);
        Assert.Null(Assert.Single(fallback).ReleasedUtc);
        Assert.Equal(InstallModsPane.ReleasedSourcePackage, InstallModsPane.ReleasedSourceOf(fallback[0]));
        // Revert: dropping the date_created read in TryReadLatestVersion, or not passing it
        // through WithInspection, fails this.
    }

    private static CatalogEntry Entry(string? releasedUtc, string? dateUpdated) =>
        new(
            new ManifestMod("", "0.11.0", NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.Exact, false, "dyno", "dyno-ExampleCommandFramework"),
            CatalogProvenance.AdaptedListing,
            InstallReasons.SourceThunderstore,
            InstallReasons.SourceLabelThunderstore,
            "2026-09-12T00:00:00Z",
            displayName: "ExampleCommandFramework",
            author: "dyno",
            owner: "dyno",
            packageName: "ExampleCommandFramework",
            dateUpdated: dateUpdated,
            releasedUtc: releasedUtc);

    private sealed class EmptyCatalog : IInstallCatalog
    {
        public InstallProgress Status => InstallProgress.Idle;
        public Task<CatalogLoadResult> LoadIndexAsync(CancellationToken cancellationToken) => Task.FromResult(new CatalogLoadResult(true, null, 0, "stamp"));
        public IReadOnlyList<CatalogEntry> Search(string? query) => Array.Empty<CatalogEntry>();
        public bool TryGetEntry(string key, out CatalogEntry? entry) { entry = null; return false; }
        public Task<InspectCallResult> DownloadAndInspectAsync(string key, CancellationToken cancellationToken) => Task.FromResult(new InspectCallResult(false, InstallReasons.FetchNoUrl, null, null));
        public PlanCallResult Plan(string key, InstallTarget target) => new(false, InstallReasons.NotInspected, null);
        public ApproveCallResult Approve(string planHash) => new(false, InstallReasons.ApprovalMissing, null, false);
        public ExecuteCallResult ExecuteToPending(string planHash) => new(false, InstallReasons.ApprovalMissing, null, false);
        public Task<ListingDocumentSet> LoadListingDocumentsAsync(string key, CancellationToken cancellationToken) => Task.FromResult(ListingDocumentSet.Unavailable(key ?? "", InstallReasons.UnknownEntry));
    }
}
