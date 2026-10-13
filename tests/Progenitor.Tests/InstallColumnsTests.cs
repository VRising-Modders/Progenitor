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
/// INSTALLCOLS1: the Install list's column model (<see cref="InstallColumns"/>), after the user's
/// 2026-09-29 look at 3840×1080 ("I think we can do better on our column management here. Also
/// maybe cancel should be cancel uninstall."). Game-free: the column arithmetic, the cell kinds and
/// texts, the header sort, the saved sort, and the one install-time rule (<see cref="InstallTimes"/>)
/// the Installed column shares with the Keybinds tab.
/// </summary>
public sealed class InstallColumnsTests : IDisposable
{
    private const string KnownClientBuild = InstallSearchFilterTests.KnownClientBuild;
    private static readonly DateTime Today = new(2026, 9, 29);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "progenitor-installcols-" + Guid.NewGuid().ToString("N"));

    public InstallColumnsTests()
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

    /// <summary>The full-width row at 3840×1080 (the 1664 px native frame) and at the 1600×1024 fallback.</summary>
    private static float Row3840 =>
        MenuPanelLayout.ForFrame(new ModsWorkspaceFrame(1664f, 916f, 0f, 0f, ModsWorkspaceFrame.PathNative), sidebar: false, InstallModsPane.PaneMode).RowWidth;

    private static float Row1600 =>
        MenuPanelLayout.ForFrame(ModsWorkspaceFrame.Fallback(1600f, 1024f), sidebar: false, InstallModsPane.PaneMode).RowWidth;

    public static IEnumerable<object[]> Widths()
    {
        yield return new object[] { "3840x1080", Row3840 };
        yield return new object[] { "1600x1024", Row1600 };
    }

    private InstallModsPane NewPane(FakeCatalog? catalog = null) =>
        new(catalog ?? new FakeCatalog()) { TodayProbe = () => Today };

    // ---- The stable set ------------------------------------------------------------------

    [Fact]
    public void Every_sort_and_every_row_state_has_the_same_column_set_and_each_header_spans_its_row_cells()
    {
        var catalog = new FakeCatalog();
        var a = InstallListViewTests.Listing("o-a", "Alpha", downloads: 3, categories: new[] { "Oakveil Update" });
        var b = InstallListViewTests.Listing("o-b", "Beta", downloads: null, releasedUtc: null, categories: new[] { "Gloomrot Update" });
        catalog.Entries.AddRange(new[] { a, b });
        var pane = NewPane(catalog);
        var expected = InstallColumns.Keys.ToArray();
        Assert.Equal(InstallColumns.HeaderKeys.ToArray(), pane.HeaderCells().Select(c => c.Key).ToArray());
        // Defect 1 (INSTALLCOLS1): no column comes and goes with the sort. Every sort, both directions.
        foreach (InstallSort sort in Enum.GetValues(typeof(InstallSort)))
        {
            foreach (bool reversed in new[] { false, true })
            {
                pane.SetSort(sort, reversed);
                foreach (var entry in new[] { a, b })
                {
                    Assert.Equal(expected, pane.RowCells(entry).Select(c => c.Key).ToArray());
                    Assert.Equal(InstallColumns.HeaderKeys.ToArray(), pane.HeaderCells().Select(c => c.Key).ToArray());
                }
            }
        }

        // INSTALLSIDES1: a header column spans exactly its row cells — the Client and Server headers
        // the state words and both button slots — whatever buttons a row shows.
        pane.SetSort(InstallSort.Released);
        var header = InstallColumns.Resolve(InstallColumns.LiveRowWidth, pane.HeaderCells());
        foreach (var entry in new[] { a, b })
        {
            var row = InstallColumns.Resolve(InstallColumns.LiveRowWidth, pane.RowCells(entry));
            foreach (var (head, first, last) in new[]
            {
                (InstallModsPane.CellClient, InstallModsPane.CellClient, new[] { InstallModsPane.CellClientB, InstallModsPane.CellClientA, InstallModsPane.CellClient }),
                (InstallModsPane.CellServer, InstallModsPane.CellServer, new[] { InstallModsPane.CellServerB, InstallModsPane.CellServerA, InstallModsPane.CellServer }),
            })
            {
                var h = header.Single(p => p.Key == head);
                Assert.Equal(h.Left, row.Single(p => p.Key == first).Left, 1);
                var end = last.Select(k => row.Single(p => p.Key == k)).First(p => p.Visible);
                Assert.Equal(h.Right, end.Right, 1);
                Assert.Equal(InstallColumns.SideColumnWidth, h.Width, 1);
            }
            foreach (string key in new[] { InstallModsPane.CellReleased, InstallModsPane.CellDownloads, InstallModsPane.CellName })
            {
                Assert.Equal(header.Single(p => p.Key == key).Left, row.Single(p => p.Key == key).Left, 1);
                Assert.Equal(header.Single(p => p.Key == key).Right, row.Single(p => p.Key == key).Right, 1);
            }
        }

        // A tool has no buttons: its slots are hidden plain cells under the same keys.
        var tool = InstallListViewTests.Listing("o-t", "Tool", categories: new[] { "Tools", "Oakveil Update" });
        var cells = pane.RowCells(tool);
        Assert.Equal(expected, cells.Select(c => c.Key).ToArray());
        foreach (string slot in new[] { InstallModsPane.CellClientA, InstallModsPane.CellClientB, InstallModsPane.CellServerA, InstallModsPane.CellServerB, InstallModsPane.CellBoth })
        {
            Assert.False(cells.Single(c => c.Key == slot).Visible);
            Assert.NotEqual(CellKind.Button, cells.Single(c => c.Key == slot).Kind);
        }
        // Version and Installed are no longer columns (the side cells carry versions; the drawer the date).
        Assert.DoesNotContain(cells, c => c.Key is InstallModsPane.CellVersion or InstallModsPane.CellInstalled or InstallModsPane.CellAuthor or InstallModsPane.CellInstall);
    }

    [Theory]
    [MemberData(nameof(Widths))]
    public void Widths_fill_the_row_exactly_with_no_negative_width_and_no_dead_gap(string name, float rowWidth)
    {
        var pane = NewPane();
        // The side columns grow with the row the pane is drawn on.
        pane.ColumnRowWidthProbe = () => rowWidth;
        var entry = InstallListViewTests.Listing("dyno-ExampleCommandFramework", "ExampleCommandFramework",
            description: "Command framework for V Rising mods", downloads: 1234567, categories: new[] { "Server", "Oakveil Update" });
        foreach (var cells in new[] { pane.HeaderCells(), pane.RowCells(entry) })
        {
            var placed = InstallColumns.Resolve(rowWidth, cells);
            // The overlay (the shortcut) takes no slot: it is drawn over the Mod column.
            var slots = cells.Where(c => c.OverlayOf == null).Select(c => c.Key).ToHashSet();
            var visible = placed.Where(p => p.Visible && slots.Contains(p.Key)).OrderBy(p => p.Left).ToList();
            Assert.All(placed, p => Assert.True(p.Width >= 0f, p + " is negative at " + name));
            float sum = visible.Sum(p => p.Width) + (visible.Count - 1) * CellLayout.Gap + HeadBlockLayout.RightColumnInset;
            Assert.Equal(rowWidth, sum, 1);
            for (int i = 1; i < visible.Count; i++)
                Assert.Equal(visible[i - 1].Right + CellLayout.Gap, visible[i].Left, 1);
            Assert.Equal(-rowWidth / 2f, visible[0].Left, 1);
            Assert.Equal(rowWidth / 2f - HeadBlockLayout.RightColumnInset, visible[^1].Right, 1);
            var mod = placed.Single(p => p.Key == InstallModsPane.CellName);
            Assert.True(mod.Width >= InstallColumns.ModMinWidth - .01f, "Mod under its floor at " + name);
            Assert.Equal(InstallColumns.ExpandWidth, placed.Single(p => p.Key == InstallModsPane.CellExpand).Width, 1);
            // INSTALLSIDES2 item 2: no Side column.
            Assert.DoesNotContain(placed, p => p.Key == InstallModsPane.CellSide);
        }

        float modWidth = InstallColumns.Resolve(rowWidth, pane.HeaderCells()).Single(p => p.Key == InstallModsPane.CellName).Width;
        // INSTALLSIDES1: the two side columns replace Author, Version, Installed and Status;
        // INSTALLSIDES2 dropped Side and (live review) widened the side columns: 330 px each at the
        // 1236 px row, growing by half of every extra pixel up to 440 px (1624 px row: 440 px).
        Assert.Equal(name == "3840x1080" ? 404f : 236f, modWidth, 0);
        Assert.Equal(name == "3840x1080" ? 440f : 330f, InstallColumns.SideColumnWidthFor(rowWidth), 0);
    }

    [Fact]
    public void At_the_live_1236_px_row_every_column_shows_and_nothing_drops()
    {
        Assert.Equal(1236f, InstallColumns.LiveRowWidth);
        Assert.Equal(1236f, Row1600, 1);
        var pane = NewPane();
        var entry = InstallListViewTests.Listing("o-a", "Alpha", downloads: 12345, categories: new[] { "Oakveil Update" });
        foreach (InstallSort sort in Enum.GetValues(typeof(InstallSort)))
        {
            pane.SetSort(sort);
            foreach (var cells in new[] { pane.HeaderCells(), pane.RowCells(entry) })
            {
                Assert.Empty(InstallColumns.DroppedAt(InstallColumns.LiveRowWidth, cells));
                var placed = InstallColumns.Resolve(InstallColumns.LiveRowWidth, cells);
                foreach (var cell in cells.Where(c => c.Visible))
                    Assert.True(placed.Single(p => p.Key == cell.Key).Visible, cell.Key + " hidden at 1236");
                Assert.Equal(236f, placed.Single(p => p.Key == InstallModsPane.CellName).Width, 0);
            }
        }
        Assert.Equal(new[] { 28f, 330f, 98f, 118f },
            new[] { InstallColumns.ExpandWidth, InstallColumns.SideColumnWidth, InstallColumns.DateWidth, InstallColumns.DownloadsWidth });
    }

    /// <summary>
    /// Live 2026-09-29 at 3840×1080: <c>RELEASED ▼</c> drew <c>RELEASED…</c> (the sort mark lost)
    /// and <c>DOWNLOADS</c> drew <c>DOWNLO…</c>. No header is ever ellipsized: every column is at
    /// least its caption's measured width with the ▼ / ▲ slot, whichever column is sorted, at the
    /// live 1236 px row and at the narrow widths where columns drop.
    /// </summary>
    [Fact]
    public void Every_header_caption_fits_its_column_with_the_sort_indicator_at_every_width()
    {
        // The calibration: the two live truncations must come out as truncations at 84 px.
        Assert.True(InstallColumns.HeaderCaptionWidth(InstallModsCopy.HeaderReleased, sortable: true) > 84f);
        Assert.True(InstallColumns.HeaderCaptionWidth(InstallModsCopy.HeaderDownloads, sortable: false) > 84f);
        // The indicator slot is always counted for a sortable column, and ▲ and ▼ take the same slot.
        Assert.True(InstallColumns.HeaderCaptionWidth("Released", true) > InstallColumns.HeaderCaptionWidth("Released", false));
        Assert.Equal(CellLayout.HeaderCaption("Released", true, false).Length, CellLayout.HeaderCaption("Released", true, true).Length);

        var pane = NewPane();
        var widths = new[] { InstallColumns.LiveRowWidth, Row3840, 1050f, 960f, 900f, 800f, 700f };
        foreach (InstallSort sort in Enum.GetValues(typeof(InstallSort)))
        {
            foreach (bool reversed in new[] { false, true })
            {
                pane.SetSort(sort, reversed);
                var header = pane.HeaderCells();
                foreach (float width in widths)
                {
                    var placed = InstallColumns.Resolve(width, header);
                    foreach (var cell in header.Where(c => c.Text.Length > 0))
                    {
                        var p = placed.Single(x => x.Key == cell.Key);
                        if (!p.Visible) continue;
                        // VANILLA1: measured as drawn — small caps, own case, at the header size.
                        Assert.True(cell.SmallCaps);
                        Assert.Equal(InstallColumns.HeaderFontSize, cell.FontSize);
                        float need = InstallColumns.HeaderCaptionWidth(cell.Text, !string.IsNullOrEmpty(cell.SortKey), cell.SmallCaps, cell.FontSize);
                        Assert.True(p.Width >= need - .01f,
                            cell.Key + " header '" + CellLayout.HeaderCaption(cell.Text, true, pane.SortAscending, true) + "' needs " + need + " in " + p.Width + " at " + width + " sorted by " + sort);
                        // And by the kit's own estimate (the button-caption test's measure).
                        Assert.True(CellLayout.EstimateTextWidth(CellLayout.HeaderCaption(cell.Text, !string.IsNullOrEmpty(cell.SortKey), true, true), cell.FontSize) <= p.Width - 4f);
                    }
                    Assert.True(placed.Single(x => x.Key == InstallModsPane.CellName).Width >= InstallColumns.ModMinWidth - .01f);
                }
            }
        }
    }

    [Fact]
    public void Dates_and_counts_fit_their_columns_and_side_captions_fit_their_buttons()
    {
        Assert.True(CellLayout.CaptionFits(new CellSpec(CellKind.Muted, "d").With(text: "Sep 23"), InstallColumns.DateWidth));
        Assert.True(CellLayout.CaptionFits(new CellSpec(CellKind.Muted, "d").With(text: "Sep '25"), InstallColumns.DateWidth));
        Assert.True(CellLayout.CaptionFits(new CellSpec(CellKind.Muted, "n").With(text: "999,999"), InstallColumns.DownloadsWidth));
        Assert.True(CellLayout.CaptionFits(new CellSpec(CellKind.Muted, "n").With(text: "999.9M"), InstallColumns.DownloadsWidth));
        Assert.Equal("999,999", InstallColumns.CompactCount(999_999));
        Assert.Equal("1.2M", InstallColumns.CompactCount(1_234_567));
        Assert.Equal("12M", InstallColumns.CompactCount(12_000_000));
        Assert.Equal(InstallModsCopy.Dash, InstallColumns.CompactCount(null));
        foreach (var (caption, width) in new[]
        {
            (InstallModsCopy.HeaderReleased, InstallColumns.DateWidth), (InstallModsCopy.HeaderDownloads, InstallColumns.DownloadsWidth),
            (InstallModsCopy.HeaderClient, InstallColumns.SideColumnWidth),
        })
        {
            string shown = CellLayout.HeaderCaption(caption, sorted: true, ascending: true);
            Assert.True(CellLayout.EstimateTextWidth(shown, CellLayout.HeaderFontSize) <= width - 4f, shown + " does not fit");
        }

        // Every side button caption fits its own slot at the side buttons' face.
        foreach (var button in new[] { SideButton.Install, SideButton.Update, SideButton.Uninstall, SideButton.CancelInstall, SideButton.CancelUninstall, SideButton.AdminOnly, SideButton.Authenticate })
        {
            string caption = SideCells.Caption(button);
            var spec = new CellSpec(CellKind.Button, "b").With(text: caption, fontSize: InstallColumns.SideButtonFontSize);
            Assert.True(CellLayout.CaptionFits(spec, InstallColumns.SideButtonWidth(caption)), caption);
        }
        // The widest pair a cell shows (Update + Uninstall) still leaves the words a readable slot.
        Assert.True(InstallColumns.SideTextWidth(SideCells.UpdateCaption, SideCells.UninstallCaption) >= 100f);
        Assert.Equal(InstallColumns.SideColumnWidth, InstallColumns.SideTextWidth(), 1);
    }

    [Fact]
    public void Headers_align_like_their_columns()
    {
        var pane = NewPane();
        var header = pane.HeaderCells();
        var row = pane.RowCells(InstallListViewTests.Listing("o-a", "Alpha", downloads: 5, categories: new[] { "Oakveil Update" }));
        foreach (var cell in header)
            Assert.Equal(row.Single(c => c.Key == cell.Key).Align, cell.Align);
        Assert.Equal(CellAlign.Right, header.Single(c => c.Key == InstallModsPane.CellDownloads).Align);
        Assert.Equal(InstallModsCopy.HeaderClient, header.Single(c => c.Key == InstallModsPane.CellClient).Text);
        Assert.Equal(InstallModsPane.SortKeyInstalled, header.Single(c => c.Key == InstallModsPane.CellClient).SortKey);
        Assert.Null(header.Single(c => c.Key == InstallModsPane.CellServer).SortKey);
        Assert.Equal("", header.Single(c => c.Key == InstallModsPane.CellExpand).Text);
        // Without a picker the Server header says Server; with one it names the picked server.
        Assert.Equal(ServerPicker.ServerWord, header.Single(c => c.Key == InstallModsPane.CellServer).Text);
    }

    // ---- Narrow panes ----------------------------------------------------------------------

    [Fact]
    public void Narrow_panes_drop_Released_then_Downloads_and_never_a_side_column()
    {
        Assert.Equal(new[] { InstallModsPane.CellReleased, InstallModsPane.CellDownloads }, InstallColumns.DropPriority.ToArray());
        var pane = NewPane();
        pane.SetSort(InstallSort.Name);
        var header = pane.HeaderCells();

        Assert.Empty(InstallColumns.DroppedAt(Row3840, header));
        Assert.Empty(InstallColumns.DroppedAt(InstallColumns.LiveRowWidth, header));
        // INSTALLSIDES2: no Side column, 330 px side columns: Released drops below 1150 px, Downloads below 1044 px.
        Assert.Empty(InstallColumns.DroppedAt(1160f, header));
        Assert.Equal(new[] { InstallModsPane.CellReleased }, InstallColumns.DroppedAt(1100f, header).ToArray());
        Assert.Equal(new[] { InstallModsPane.CellReleased, InstallModsPane.CellDownloads }, InstallColumns.DroppedAt(1040f, header).ToArray());

        int last = 0;
        for (float width = Row3840; width >= 960f; width -= 7f)
        {
            var dropped = InstallColumns.DroppedAt(width, header);
            Assert.Equal(InstallColumns.DropPriority.Take(dropped.Count).ToArray(), dropped.ToArray());
            Assert.True(dropped.Count >= last, "a column came back on a narrower row at " + width);
            last = dropped.Count;
            var placed = InstallColumns.Resolve(width, header);
            foreach (string key in new[] { InstallModsPane.CellClient, InstallModsPane.CellServer })
            {
                Assert.True(placed.Single(p => p.Key == key).Visible);
                Assert.Equal(InstallColumns.SideColumnWidth, placed.Single(p => p.Key == key).Width, 1);
            }
            Assert.True(placed.Max(p => p.Visible ? p.Right : float.MinValue) <= width / 2f - HeadBlockLayout.RightColumnInset + .5f, "overflow at " + width);
        }
        Assert.Equal(2, last);

        // The active sort column is never dropped; the next one in the priority goes instead.
        pane.SetSort(InstallSort.Released);
        var sortedHeader = pane.HeaderCells();
        Assert.Equal(new[] { InstallModsPane.CellDownloads }, InstallColumns.DroppedAt(1050f, sortedHeader).ToArray());
        Assert.True(InstallColumns.Resolve(1050f, sortedHeader).Single(p => p.Key == InstallModsPane.CellReleased).Visible);
        pane.SetSort(InstallSort.Downloads);
        Assert.True(InstallColumns.Resolve(1050f, pane.HeaderCells()).Single(p => p.Key == InstallModsPane.CellDownloads).Visible);
        pane.SetSort(InstallSort.Name);

        pane.ColumnRowWidthProbe = () => Row1600;
        var columns = pane.ColumnsToWidget();
        Assert.Empty((IReadOnlyList<string>)columns["dropped"]!);
        pane.ColumnRowWidthProbe = () => 960f;
        columns = pane.ColumnsToWidget();
        Assert.Equal(new[] { "released", "downloads" }, (IReadOnlyList<string>)columns["dropped"]!);
        Assert.Equal(0d, ((Dictionary<string, object?>)columns["widths"]!)[InstallModsPane.CellReleased]);
        Assert.Contains(InstallModsPane.SortKeyReleased, (List<string>)columns["sortable"]!);
    }

    // ---- Cells -----------------------------------------------------------------------------

    [Fact]
    public void Date_and_count_cells_are_plain_text_and_versions_live_in_the_side_cells()
    {
        var pane = NewPane();
        var entry = InstallListViewTests.Listing("o-a", "Alpha", downloads: 1234, releasedUtc: "2026-09-23T10:00:00Z", version: "2.42.82",
            categories: new[] { "Oakveil Update" });
        var cells = pane.RowCells(entry);
        Assert.DoesNotContain(cells, c => c.Kind == CellKind.Mono);
        foreach (string key in new[] { InstallModsPane.CellReleased, InstallModsPane.CellDownloads, InstallModsPane.CellClient, InstallModsPane.CellServer })
            Assert.Equal(CellKind.Muted, cells.Single(c => c.Key == key).Kind);
        Assert.Equal("Sep 23", cells.Single(c => c.Key == InstallModsPane.CellReleased).Text);
        Assert.Equal("1,234", cells.Single(c => c.Key == InstallModsPane.CellDownloads).Text);
        Assert.Equal(SideCells.NotInstalled, cells.Single(c => c.Key == InstallModsPane.CellClient).Text);
        // The Mod column's second line is the author.
        Assert.Equal("Alpha\nby o", cells.Single(c => c.Key == InstallModsPane.CellName).Text);
    }

    [Fact]
    public void Short_dates_drop_the_year_only_in_the_current_year()
    {
        Assert.Equal("Sep 23", InstallColumns.ShortDate(new DateTime(2026, 9, 23), Today));
        Assert.Equal("Sep 3", InstallColumns.ShortDate(new DateTime(2026, 9, 3), Today));
        Assert.Equal("Dec '25", InstallColumns.ShortDate(new DateTime(2025, 12, 31), Today));
        Assert.Equal("Jan '27", InstallColumns.ShortDate(new DateTime(2027, 1, 1), Today));
        Assert.Equal("2025-12-31", InstallColumns.FullDate(new DateTime(2025, 12, 31)));
        var pane = NewPane();
        var old = InstallListViewTests.Listing("o-a", "A", releasedUtc: "2025-08-21T09:14:34Z");
        Assert.Equal("Aug '25", pane.ReleasedShortText(old));
        Assert.Equal("2025-08-21", pane.ListingToWidget(old)["releasedDate"]);
        Assert.Equal("Aug 21", pane.ReleasedShortText(InstallListViewTests.Listing("o-b", "B", releasedUtc: null, dateUpdated: "2026-08-21T09:14:41Z")));
        Assert.Equal(InstallModsCopy.Dash, pane.ReleasedShortText(InstallListViewTests.Listing("o-c", "C", releasedUtc: null)));
    }

    [Fact]
    public void Status_words_and_their_explicit_labels()
    {
        static InstallRowAction A(InstallListingState state, string label = "", bool enabled = true, string managed = "", string plain = "", string why = "") =>
            new(state, label, CellTone.Normal, enabled, false, "", managed, plain, why);

        // The combined action's words still answer the listing readback (status / button).
        var cases = new (InstallRowAction Action, string Status, string Button)[]
        {
            (A(InstallListingState.None, InstallModsCopy.Install), "", "Install"),
            (A(InstallListingState.Installed, InstallModsCopy.Installed), "Installed", "Uninstall"),
            (A(InstallListingState.PendingUninstall, InstallModsCopy.Reactivate), "Removal pending game restart", "Cancel uninstall"),
            (A(InstallListingState.Pending, InstallModsCopy.InstalledPending), "Pending game restart", "Cancel install"),
            (A(InstallListingState.Tool, InstallModsCopy.ToolNotPlugin, enabled: false), "Tool, not a plugin", ""),
        };
        foreach (var (action, status, button) in cases)
        {
            Assert.Equal(status, InstallModsPane.RowStatusText(action));
            Assert.Equal(button, InstallModsPane.RowButtonText(action));
            Assert.NotEqual("Cancel", InstallModsPane.RowButtonText(action));
        }

        // INSTALLSIDES1: every state label shows whole in a side cell beside the one button it comes
        // with — at most two lines at the side face, none cut.
        float oneButton = InstallColumns.SideTextWidth(SideCells.CancelUninstallCaption);
        foreach (string label in InstallStatusLabels.All.Concat(new[]
        {
            SideCells.MissingServerHasIt, SideCells.MissingClientHasIt, SideCells.NotInstalled, SideCells.NoClientSide, SideCells.NoServerSide, SideCells.ServerCantReachThunderstore,
            SideCells.SameAsClient, SideCells.NotLoadedThere, "Installed 2.42.82",
        }))
        {
            string lines = InstallColumns.StatusLines(label, oneButton, InstallColumns.SideTextFontSize);
            Assert.True(lines.Split('\n').Length <= InstallColumns.StatusMaxLines, label + " takes more than two lines");
            foreach (string line in lines.Split('\n'))
                Assert.True(InstallColumns.Fits(line, oneButton, InstallColumns.SideTextFontSize), "'" + line + "' of '" + label + "' does not fit");
        }

        // A route-blocked server cell has no button, so its note has the whole column.
        foreach (string blocked in new[]
        {
            "Not installed · Ask a server admin", "1.0.0 · update 2.0.0 · Ask a server admin", "Unknown · No server",
            // REMOTEUNREACH1: the per-block words.
            "Not installed · Server can't reach Thunderstore", "1.0.0 · update 2.0.0 · Server can't reach Thunderstore",
            "Not installed · No Progenitor on server", "Not installed · Remote installs off", "Not installed · Checking server…",
        })
        {
            string lines = InstallColumns.StatusLines(blocked, InstallColumns.SideTextWidth(), InstallColumns.SideTextFontSize);
            Assert.True(lines.Split('\n').Length <= InstallColumns.StatusMaxLines, blocked);
            foreach (string line in lines.Split('\n'))
                Assert.True(InstallColumns.Fits(line, InstallColumns.SideTextWidth(), InstallColumns.SideTextFontSize), line);
        }

        // Two buttons (Update + Uninstall) beside a version pair: the dot breaks it into two lines.
        float two = InstallColumns.SideTextWidth(SideCells.UpdateCaption, SideCells.UninstallCaption);
        Assert.Equal("1.0.3\nupdate 1.1.0", InstallColumns.StatusLines("1.0.3 · update 1.1.0", two, InstallColumns.SideTextFontSize));
        Assert.Equal("Removal pending\nserver restart",
            InstallColumns.StatusLines(InstallStatusLabels.RemovalPendingServerRestart, oneButton, InstallColumns.SideTextFontSize));
        Assert.Equal("Cancel uninstall", InstallModsCopy.PlainCancel);
        Assert.Equal("Cancel install", InstallModsCopy.CancelInstallButton);
        Assert.Equal("Cancel uninstall", ModsMenu.PlainCancelCaption);
        Assert.Equal("Cancel uninstall", ModPageCopy.CancelUninstall);
        Assert.Equal("Cancel install", ModPageCopy.CancelInstall);
    }

    // ---- Sort ----------------------------------------------------------------------------------

    [Fact]
    public void Saved_sort_carries_over_and_old_keys_migrate_to_the_default()
    {
        // The first three sorts keep their numbers; the new ones are added after them.
        Assert.Equal(0, (int)InstallSort.Downloads);
        Assert.Equal(1, (int)InstallSort.Released);
        Assert.Equal(2, (int)InstallSort.Name);
        foreach (InstallSort sort in Enum.GetValues(typeof(InstallSort)))
        {
            Assert.True(InstallModsPane.TryParseSortKey(InstallModsPane.SortKeyOf(sort), out var back));
            Assert.Equal(sort, back);
        }
        // Stage 3 removed likes and ratings: their keys (and anything unknown) fall back to Recently released.
        foreach (string? old in new[] { "likes", "rating", "LIKES", "", null, "update" })
        {
            Assert.False(InstallModsPane.TryParseSortKey(old, out var fallback));
            Assert.Equal(InstallSort.Released, fallback);
        }
        Assert.True(InstallModsPane.TryParseSortKey(" Downloads ", out var trimmed));
        Assert.Equal(InstallSort.Downloads, trimmed);

        // A chosen sort and direction survive a new pane on the same catalog and Clear filters.
        var catalog = new FakeCatalog();
        var pane = NewPane(catalog);
        Assert.Equal(InstallSort.Released, pane.Sort);
        Assert.False(pane.SortAscending);
        pane.HeaderSort(InstallSort.Downloads);
        pane.HeaderSort(InstallSort.Downloads);
        Assert.True(pane.Filters.SortReversed);
        Assert.True(pane.SortAscending);
        var again = NewPane(catalog);
        Assert.Equal(InstallSort.Downloads, again.Sort);
        Assert.True(again.Filters.SortReversed);
        again.ClearFilters();
        Assert.Equal(InstallSort.Downloads, again.Sort);
        Assert.True(again.Filters.Clone().SortReversed);
        // A new column starts in its natural direction: A to Z for Mod and Author, newest first for dates.
        again.HeaderSort(InstallSort.Author);
        Assert.False(again.Filters.SortReversed);
        Assert.True(again.SortAscending);
        again.HeaderSort(InstallSort.Installed);
        Assert.False(again.SortAscending);
    }

    [Fact]
    public void Sort_indicator_shows_the_direction_on_the_active_header_only()
    {
        Assert.Equal("RELEASED ▼", CellLayout.HeaderCaption("Released", sorted: true, ascending: false));
        Assert.Equal("MOD ▲", CellLayout.HeaderCaption("Mod", sorted: true, ascending: true));
        Assert.Equal("AUTHOR", CellLayout.HeaderCaption("Author", sorted: false, ascending: true));
        Assert.Equal(CellLayout.SortIndicator.Length, CellLayout.SortIndicatorAscending.Length);
        Assert.True(InstallColumns.NaturalDescending(InstallSort.Released));
        Assert.True(InstallColumns.NaturalDescending(InstallSort.Installed));
        Assert.True(InstallColumns.NaturalDescending(InstallSort.Downloads));
        Assert.False(InstallColumns.NaturalDescending(InstallSort.Name));
        Assert.False(InstallColumns.NaturalDescending(InstallSort.Author));
        Assert.Equal("asc", InstallColumns.DirectionName(InstallSort.Released, reversed: true));
    }

    [Fact]
    public void Author_sort_is_A_to_Z_then_reversed_with_unknown_authors_last()
    {
        var entries = new List<CatalogEntry>
        {
            InstallListViewTests.Listing("zed-A", "A"),
            InstallListViewTests.Listing("amy-B", "B"),
            InstallListViewTests.Listing("mia-C", "C"),
        };
        Assert.Equal(new[] { "B", "C", "A" }, InstallModsPane.Sorted(entries, InstallSort.Author, false, null).Select(e => e.DisplayName).ToArray());
        Assert.Equal(new[] { "A", "C", "B" }, InstallModsPane.Sorted(entries, InstallSort.Author, true, null).Select(e => e.DisplayName).ToArray());
    }

    // ---- Installed -----------------------------------------------------------------------------

    [Fact]
    public void Install_time_prefers_the_ledger_then_the_file_creation_time_then_its_last_write()
    {
        var ledgerAt = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);
        var created = new DateTime(2026, 9, 5, 8, 0, 0, DateTimeKind.Utc);
        var written = new DateTime(2025, 1, 5, 8, 0, 0, DateTimeKind.Utc);
        Assert.Equal((ledgerAt, InstallTimeSource.Ledger), Of(InstallTimes.Pick(ledgerAt, created, written)));
        Assert.Equal((created, InstallTimeSource.FileCreated), Of(InstallTimes.Pick(null, created, written)));
        Assert.Equal((written, InstallTimeSource.FileWritten), Of(InstallTimes.Pick(null, null, written)));
        // An implausible creation time (the 1601 zero stamp, anything before 2000) is not evidence.
        Assert.Equal((written, InstallTimeSource.FileWritten), Of(InstallTimes.Pick(DateTime.MinValue, new DateTime(1601, 1, 1), written)));
        Assert.False(InstallTimes.Pick(null, null, null).Known);
        Assert.Equal(InstallTimes.SourceNone, InstallTimes.Pick(null, null, null).SourceName);

        // The ledger: the earliest row naming the plugin, by its pluginId or by the file name.
        var ledger = new ServerInstallLedger("1", "", "", ledgerAt, new[]
        {
            new ServerInstallLedgerEntry("BepInEx/progenitor-reload/bags.dll", "a", 1, "mod", "package:dyno-Bags@1.0.0", ledgerAt.AddDays(3), pluginId: "dyno.bags"),
            new ServerInstallLedgerEntry("BepInEx/progenitor-pending/index/bags.dll", "a", 1, "pending", "package:dyno-Bags@1.0.0", ledgerAt),
            new ServerInstallLedgerEntry("BepInEx/progenitor-reload/other.dll", "b", 1, "mod", "package:x-Other@1.0.0", ledgerAt.AddDays(-9), pluginId: "x.other"),
        });
        Assert.Equal(ledgerAt, InstallTimes.LedgerTime(ledger, "dyno.bags", "bags.dll"));
        Assert.Equal(ledgerAt.AddDays(3), InstallTimes.LedgerTime(ledger, "dyno.bags", null));
        Assert.Null(InstallTimes.LedgerTime(ledger, "nobody", "nothing.dll"));

        // A real file: with no ledger row its creation time decides; a ledger row wins over it.
        string file = Path.Combine(_root, "plugins", "Bags.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, new byte[] { 1 });
        File.SetCreationTimeUtc(file, created);
        File.SetLastWriteTimeUtc(file, written);
        Assert.Equal((created, InstallTimeSource.FileCreated), Of(InstallTimes.Of("nobody", file, (ServerInstallLedger?)null)));
        Assert.Equal((ledgerAt, InstallTimeSource.Ledger), Of(InstallTimes.Of("dyno.bags", file, ledger)));
        File.SetCreationTimeUtc(file, new DateTime(1990, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal((written, InstallTimeSource.FileWritten), Of(InstallTimes.Of("nobody", file, (ServerInstallLedger?)null)));
        Assert.False(InstallTimes.Of("nobody", Path.Combine(_root, "missing.dll"), (ServerInstallLedger?)null).Known);
        Assert.Null(InstallTimes.PluginLocation("dyno.bags"));

        static (DateTime?, InstallTimeSource) Of(InstallTime t) => (t.Utc, t.Source);
    }

    [Fact]
    public void Installed_here_reads_the_ledger_then_the_plugin_file_and_says_so_when_not_installed()
    {
        var catalog = new FakeCatalog();
        var ledgered = InstallListViewTests.Listing("dyno-Bags", "Bags", categories: new[] { "Oakveil Update" });
        var other = InstallListViewTests.Listing("acme-Hunts", "Hunts", categories: new[] { "Oakveil Update" });
        var none = InstallListViewTests.Listing("o-None", "None", categories: new[] { "Oakveil Update" });
        catalog.Entries.AddRange(new[] { ledgered, other, none });
        var pane = NewPane(catalog);
        pane.LedgerProbe = _ => true;

        var ledgerAt = new DateTime(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc);
        string reload = Path.Combine(_root, "progenitor-reload", "bags.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(reload)!);
        File.WriteAllBytes(reload, new byte[] { 3 });
        string plugin = Path.Combine(_root, "plugins", "Hunts.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(plugin)!);
        File.WriteAllBytes(plugin, new byte[] { 4 });
        var created = new DateTime(2025, 12, 24, 12, 0, 0, DateTimeKind.Utc);
        File.SetCreationTimeUtc(plugin, created);
        InstallModsPane.PluginFileProbe = id => id == "acme-Hunts" ? plugin : null;
        pane.SetInstalledQuery(new InstalledStateQuery(
            new[] { new InstalledFile(reload, InstalledState.ReloadKind, "sha", InstalledState.ClientRoot) },
            new[] { new InstalledInventoryRow("acme-Hunts", "1.0.0", "Local") },
            new[]
            {
                new InstalledLedgerRow("dyno-Bags", "sha", "1.0.0", "bags.dll", "package:dyno-Bags@1.0.0", "mod",
                    InstalledState.ClientRoot, reload, "dyno.bags", ledgerAt),
            }));

        // The ledgered install: the ledger's time, as a local short date.
        var bags = pane.InstalledTimeFor(ledgered);
        Assert.Equal(InstallTimeSource.Ledger, bags.Source);
        Assert.Equal(ledgerAt, bags.Utc);
        // INSTALLSIDES1: the install date is the drawer's Installed here fact, not a column.
        Assert.Contains(pane.DrawerLines(ledgered, pane.ActionFor(ledgered), out _),
            l => l.Kind == CellLineKind.Fact && l.Label == InstallModsCopy.FactInstalledHere && l.Text == InstallColumns.FullDate(ledgerAt.ToLocalTime()));
        var readback = (Dictionary<string, object?>)pane.ListingToWidget(ledgered)["installed"]!;
        Assert.Equal("ledger", readback["source"]);

        // Another tool's install: its plugin file's creation time (with the year: not this year).
        var hunts = pane.InstalledTimeFor(other);
        Assert.Equal(InstallTimeSource.FileCreated, hunts.Source);
        Assert.Equal(created, hunts.Utc);
        Assert.EndsWith(" '25", pane.InstalledText(other), StringComparison.Ordinal);
        Assert.StartsWith("2025-12-2", (string)((Dictionary<string, object?>)pane.ListingToWidget(other)["installed"]!)["fullDate"]!, StringComparison.Ordinal);

        // Not installed: blank, and no evidence.
        Assert.Contains(pane.DrawerLines(none, pane.ActionFor(none), out _),
            l => l.Label == InstallModsCopy.FactInstalledHere && l.Text == InstallModsCopy.NotInstalledHere);
        Assert.Equal("none", ((Dictionary<string, object?>)pane.ListingToWidget(none)["installed"]!)["source"]);

        // Sorted by Installed: newest first, not-installed last — and still last reversed.
        pane.SetSort(InstallSort.Installed);
        Assert.Equal(new[] { "Bags", "Hunts", "None" }, pane.VisibleEntries().Select(e => e.DisplayName).ToArray());
        pane.SetSort(InstallSort.Installed, reversed: true);
        Assert.Equal(new[] { "Hunts", "Bags", "None" }, pane.VisibleEntries().Select(e => e.DisplayName).ToArray());
        // Revert: sorting a missing install time by direction puts None first when reversed.
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
}
