using System;
using System.Collections.Generic;
using System.Linq;
using Progenitor.Ui;
using Xunit;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.Tests;

/// <summary>
/// KIT1: the settings-kit pieces the redesigned Mods page needed — the two-column
/// <see cref="UiHeadBlock"/>, <see cref="LabelTone"/> colors, the icon-only
/// <see cref="IUiIconLink"/>, a sidebar row with a marker and a state dot
/// (<see cref="IUiMarkedButton"/>), and the in-pane <see cref="UiPromptBlock"/>.
///
/// <para>Game-free throughout: the column model is arithmetic
/// (<see cref="MenuPanelLayout.CellColumns"/>) and the handles run on
/// <see cref="CellRowTests.FakeCellRuntime"/>, the same fake cell-row runtime LIST1 uses. The two
/// fixtures are the ones the mock was approved at: 3840×1080, whose native frame is 1664×916 and
/// whose detail pane is a 1248 px row, and the 1600×1024 fallback.</para>
///
/// <para><b>What removal breaks.</b> Dropping <see cref="HeadBlockLayout.Columns"/>'s 45 % cap fails
/// <see cref="Head_block_keeps_the_title_unclipped_and_the_right_column_flush_at_both_fixtures"/>
/// (the right column eats the title). Dropping the explicit row height from
/// <see cref="CellRowSpec"/> fails
/// <see cref="A_wrapping_one_liner_grows_its_row_and_the_badges_stay_at_the_top"/> (a four-line
/// description would be drawn in a 40 px row). Dropping
/// <see cref="CellSpec.FontSizeOverride"/> fails <see cref="The_title_is_drawn_at_the_mocks_display_size"/>.
/// Dropping <see cref="HeadBlockLayout.RightColumnInset"/> (or resolving a head row's cells without
/// its <see cref="CellRowSpec.RightInset"/>) fails
/// <see cref="The_right_column_stops_one_scrollbar_band_inside_the_row_edge"/> — the action button
/// goes back on the pane viewport's clip, which is HEAD2's cropped Uninstall.
/// Mapping <see cref="LabelTone.Ok"/> to the badge rim instead of the word color fails
/// <see cref="Label_tones_resolve_from_the_palette_and_stay_readable_on_the_pane_fill"/>. Putting the
/// prompt's Yes and No on their own rows fails
/// <see cref="The_prompt_block_is_a_heading_a_muted_body_and_one_right_aligned_pair"/>.</para>
/// </summary>
public sealed class HeadBlockTests : IDisposable
{
    private readonly CellRowTests.FakeCellRuntime _runtime = new();
    private readonly object _owner = new();
    private readonly List<IUiPanel> _panels = new();

    public HeadBlockTests()
    {
        UiApi.ResetForTests();
        UiApi.SetRuntimeForTests(_runtime);
    }

    public void Dispose()
    {
        // The cell-row registry is process-wide: every panel goes before the next test looks a key up.
        foreach (var panel in _panels) panel.Dispose();
        UiApi.ResetForTests();
        ModsWorkspaceFrame.ResetForTests();
    }

    private IUiPanel Panel()
    {
        var panel = UiApi.Panel("Mods", _owner, "example.kit1");
        _panels.Add(panel);
        return panel;
    }

    // ---- fixtures ---------------------------------------------------------------------------

    /// <summary>The detail pane's row width at the two fixtures the mock was approved at.</summary>
    public static IEnumerable<object[]> RowWidths()
    {
        yield return new object[] { RowWidthAt3840 };
        yield return new object[] { RowWidthAt1600 };
    }

    /// <summary>3840×1080 in game: the native frame is 1664×916, so the detail pane's row is 1248 px.</summary>
    internal static float RowWidthAt3840 => MenuPanelLayout
        .ForFrame(new ModsWorkspaceFrame(1664f, 916f, 0f, 0f, ModsWorkspaceFrame.PathNative), sidebar: false)
        .RowWidth;

    internal static float RowWidthAt1600 => MenuPanelLayout
        .ForFrame(ModsWorkspaceFrame.Fallback(1600f, 1024f), sidebar: false)
        .RowWidth;

    /// <summary>The mock's CryptSnooze page: a Progenitor package whose server half is missing.</summary>
    private static HeadSpec MockHead(string? oneLiner = null, string? warning = null) => new()
    {
        Key = "page",
        Title = "CryptSnooze",
        Meta = new[]
        {
            HeadMeta.Mono(HeadBlockLayout.CellVersion, "2.0.4"),
            HeadMeta.Mono(HeadBlockLayout.CellReleased, "2025-05-18", optional: true),
            new HeadMeta(HeadBlockLayout.CellAuthor, "by Corvane", HeadMetaKind.Text, optional: true),
        },
        ToggleCaption = "Like · 132",
        OneLiner = oneLiner ?? "A VRising mod that speeds up time while you sleep in your coffin.",
        Warning = warning,
        StatusLabel = "Status: ",
        StatusWord = "Installed",
        StatusTone = LabelTone.Ok,
        ActionCaption = "Uninstall",
        ActionTone = LabelTone.Orange,
        Badges = new[] { new HeadBadge("Server", CellTone.Server) },
    };

    // ---- geometry ---------------------------------------------------------------------------

    /// <summary>
    /// The head block is the mock's grid at both fixtures: the title, meta line and one-liner on the
    /// left, the status word, the action button and the badges packed against the row's content edge
    /// (HEAD2: the row less its right inset), nothing overlapping, nothing outside the content, and
    /// the title never ellipsized.
    /// What this would catch: a right column that squeezes the title to its floor (which is what
    /// happens without the 45 % cap), or right-aligned cells that pack from the wrong end.
    /// </summary>
    [Theory]
    [MemberData(nameof(RowWidths))]
    public void Head_block_keeps_the_title_unclipped_and_the_right_column_flush_at_both_fixtures(float rowWidth)
    {
        var rows = HeadBlockLayout.Build(MockHead(), rowWidth);
        Assert.Equal(3, rows.Count);
        Assert.Equal(new[] { "page-title", "page-meta", "page-oneliner" }, rows.Select(r => r.Key).ToArray());

        float left = -rowWidth / 2f;
        // HEAD2: the content edge, one scrollbar band inside the row's own right edge.
        float right = HeadBlockLayout.ContentRight(rowWidth);
        foreach (var row in rows)
        {
            var placed = MenuPanelLayout.CellColumns(rowWidth, row);
            foreach (var p in placed.Where(p => p.Visible))
            {
                Assert.True(p.Left >= left - .01f, row.Key + " " + p + " starts left of the row");
                Assert.True(p.Right <= right + .01f, row.Key + " " + p + " ends right of the content");
            }
            for (int i = 0; i < placed.Length; i++)
                for (int j = i + 1; j < placed.Length; j++)
                    Assert.False(CellLayout.Overlap(placed[i], placed[j]), row.Key + ": " + placed[i] + " overlaps " + placed[j]);
            Assert.True(
                HeadBlockLayout.RightColumnIsFlush(rowWidth, row, placed),
                row.Key + "'s right column is not packed against the content edge");
        }

        // The title keeps a readable column and is never ellipsized at either fixture.
        var titleRow = rows[0];
        var titlePlaced = MenuPanelLayout.CellColumns(rowWidth, titleRow);
        var title = titlePlaced.Single(p => p.Key == HeadBlockLayout.CellTitle);
        var titleSpec = titleRow.Cells.Single(c => c.Key == HeadBlockLayout.CellTitle);
        Assert.True(CellLayout.CaptionFits(titleSpec, title.Width), "the display title is clipped at " + rowWidth);
        Assert.InRange(title.Left, left - .01f, left + .01f);

        // The status word ends on the content edge, with `Status:` to its left.
        var word = titlePlaced.Single(p => p.Key == HeadBlockLayout.CellStatusWord);
        var statusLabel = titlePlaced.Single(p => p.Key == HeadBlockLayout.CellStatusLabel);
        Assert.InRange(word.Right, right - .01f, right + .01f);
        Assert.True(statusLabel.Right < word.Left, "`Status:` must sit left of the word");
        Assert.True(title.Right < statusLabel.Left, "the title must not reach the status column");
    }

    /// <summary>
    /// The right column is one column: the status word, the action button and the last badge all end
    /// on the same edge, which is what makes the mock's block read as two columns rather than three
    /// ragged lines.
    /// What this would catch: a right column laid out per row from its own width, so the action button
    /// and the badges no longer line up under the status.
    /// </summary>
    [Theory]
    [MemberData(nameof(RowWidths))]
    public void The_right_column_of_every_head_row_ends_on_the_same_edge(float rowWidth)
    {
        var rows = HeadBlockLayout.Build(MockHead(), rowWidth);
        var edges = new List<float>();
        foreach (var row in rows)
        {
            var placed = MenuPanelLayout.CellColumns(rowWidth, row);
            var rightmost = placed
                .Where((p, i) => p.Visible && row.Cells[i].Align == CellAlign.Right)
                .OrderByDescending(p => p.Right)
                .First();
            edges.Add(rightmost.Right);
        }

        Assert.Equal(3, edges.Count);
        foreach (var edge in edges) Assert.InRange(edge, edges[0] - .01f, edges[0] + .01f);
        // And that edge is the content edge, not the row's own (HEAD2).
        Assert.InRange(
            edges[0], HeadBlockLayout.ContentRight(rowWidth) - .01f, HeadBlockLayout.ContentRight(rowWidth) + .01f);
    }

    /// <summary>
    /// HEAD2 (user, 2026-09-13, live 3840×1080: "the uninstall button is too far to the right, it is
    /// getting cropped"): the right column stops one scrollbar band short of the row's own right
    /// edge, so the widest thing in it — the mock's action button — is drawn inside the pane's
    /// clipped viewport rather than on its boundary. Pinned at both fixtures with the mock's button
    /// width and two badges: every cell of every head row ends at or before the content edge, the
    /// status word, the action button and the last badge all end exactly on it, and nothing overlaps.
    /// What this would catch: the gen-1 geometry, where <c>mods_ui rects</c> read <c>status-word</c>
    /// and <c>badge-0</c> ending at x 2732 on a 1484..2732 row — the viewport's own clip, with the
    /// scrollbar immediately beyond it — and the live Uninstall button came out cut off.
    /// </summary>
    [Theory]
    [MemberData(nameof(RowWidths))]
    public void The_right_column_stops_one_scrollbar_band_inside_the_row_edge(float rowWidth)
    {
        var spec = MockHead(warning: ModPageCopy.WarningServerHalfMissingBody);
        spec.Badges = new[]
        {
            new HeadBadge(ModPageCopy.BadgeServer, CellTone.Server),
            new HeadBadge(ModPageCopy.BadgeClient, CellTone.Client),
        };
        var rows = HeadBlockLayout.Build(spec, rowWidth);
        Assert.Equal(4, rows.Count);

        // The inset is the pane's own band: the bar, plus the gap it keeps from the frame edge.
        Assert.Equal(PanelLayout.ScrollbarWidth + PanelLayout.ScrollbarInset, HeadBlockLayout.RightColumnInset);
        float content = HeadBlockLayout.ContentRight(rowWidth);
        Assert.Equal(rowWidth / 2f - HeadBlockLayout.RightColumnInset, content);

        foreach (var row in rows)
        {
            Assert.Equal(HeadBlockLayout.RightColumnInset, row.RightInset);
            var placed = MenuPanelLayout.CellColumns(rowWidth, row);
            foreach (var p in placed.Where(p => p.Visible))
                Assert.True(p.Right <= content + .01f, row.Key + " " + p + " enters the scrollbar band");
            for (int i = 0; i < placed.Length; i++)
                for (int j = i + 1; j < placed.Length; j++)
                    Assert.False(CellLayout.Overlap(placed[i], placed[j]), row.Key + ": " + placed[i] + " overlaps " + placed[j]);
        }

        // The action button keeps the mock's column width and ends on the content edge.
        var meta = MenuPanelLayout.CellColumns(rowWidth, rows.Single(r => r.Key == "page-meta"));
        var action = meta.Single(p => p.Key == HeadBlockLayout.CellAction);
        Assert.True(action.Width >= CellLayout.ButtonWidth - .01f, "the action button lost its width to the inset");
        Assert.InRange(action.Right, content - .01f, content + .01f);

        // Both badges are inside the band, side by side, with the last one on the edge.
        var third = MenuPanelLayout.CellColumns(rowWidth, rows.Single(r => r.Key == "page-oneliner"));
        var firstBadge = third.Single(p => p.Key == HeadBlockLayout.CellBadge + "-0");
        var lastBadge = third.Single(p => p.Key == HeadBlockLayout.CellBadge + "-1");
        Assert.InRange(lastBadge.Right, content - .01f, content + .01f);
        Assert.True(firstBadge.Right <= lastBadge.Left + .01f, "the badges must not share a span");

        // The status word above them, and the two wrapping lines beside them, end there as well: the
        // whole block has one right edge, which is the edge the settings rows below it read as.
        var word = MenuPanelLayout.CellColumns(rowWidth, rows[0]).Single(p => p.Key == HeadBlockLayout.CellStatusWord);
        Assert.InRange(word.Right, content - .01f, content + .01f);
        var warningBody = MenuPanelLayout.CellColumns(rowWidth, rows.Single(r => r.Key == "page-warning"))
            .Single(p => p.Key == HeadBlockLayout.CellWarning);
        Assert.InRange(warningBody.Right, content - .01f, content + .01f);
        Assert.Equal(
            HeadBlockLayout.WarningBodyWidth(rowWidth),
            warningBody.Width,
            1);
    }

    /// <summary>
    /// POLISH3 item C: the mod page's <c>»</c> store link — an icon link placed by the generic
    /// settings row's control column, which ends flush with the row — takes the SAME right inset the
    /// head block's right column takes, so the link ends at the head block's content edge instead of
    /// 12 px further right, half under the pane's vertical scrollbar (HEAD2 flagged it; live at
    /// 3840×1080 the row is 1248 px wide, the content edge is x 2720 and the link ended at 2732).
    /// Only the icon link moves: an editor control still fills its column from the left, so no other
    /// pane's rows shift.
    /// What this would catch: <c>PlaceSquareControl</c> going back to the column's bare right edge,
    /// or the inset being applied to <c>PlaceControl</c> as well, which would shrink every choice,
    /// slider and input on every settings page by a scrollbar band.
    /// </summary>
    [Theory]
    [MemberData(nameof(RowWidths))]
    public void The_icon_link_ends_at_the_same_right_edge_as_the_head_block_s_right_column(float rowWidth)
    {
        var columns = MenuPanelLayout.Columns(rowWidth, hasControl: true);
        float size = MenuPanelLayout.IconLinkSize;

        // Row-relative, exactly as the runtime places a center-pivoted rect.
        float linkRight = MenuPanelLayout.IconLinkRight(columns, size);
        float content = HeadBlockLayout.ContentRight(rowWidth);
        Assert.InRange(linkRight, content - .01f, content + .01f);

        // The link keeps its square and is the inset's width clear of the row's own edge.
        Assert.InRange(
            MenuPanelLayout.IconLinkCentreX(columns, size) + (size * .5f),
            linkRight - .01f,
            linkRight + .01f);
        Assert.InRange(
            (columns.ControlLeft + columns.ControlWidth) - linkRight,
            HeadBlockLayout.RightColumnInset - .01f,
            HeadBlockLayout.RightColumnInset + .01f);

        // An editor control that fills the column is untouched: it still starts at the column's left
        // edge and ends on the row edge, which is what every other pane's rows are laid out against.
        Assert.Equal(rowWidth / 2f, columns.ControlLeft + columns.ControlWidth, 2);
    }

    /// <summary>
    /// A description too long for one line grows its row rather than being clipped, and the badges
    /// beside it stay at the top of the grown row instead of floating in its middle.
    /// What this would catch: a wrapping cell in a fixed 40 px row (the lines would be drawn outside
    /// it, which is what a cell row's never-changing height means if the height is not asked for).
    /// </summary>
    [Fact]
    public void A_wrapping_one_liner_grows_its_row_and_the_badges_stay_at_the_top()
    {
        float rowWidth = RowWidthAt1600;
        const string longOne =
            "Speeds up the passage of time while you sleep in your coffin, and keeps on speeding it "
            + "up until somebody wakes you, which on a busy server is sooner than you would like, so "
            + "the multiplier is configurable and the wake-up is announced in chat as well.";
        var rows = HeadBlockLayout.Build(MockHead(oneLiner: longOne), rowWidth);
        var third = rows.Single(r => r.Key == "page-oneliner");

        int lines = HeadBlockLayout.OneLinerLines(longOne, rowWidth);
        Assert.True(lines > 1, "a three-sentence description must wrap");
        Assert.Equal(HeadBlockLayout.WrappedRowHeight(lines), third.Height);
        Assert.True(third.Height > UnityUiRuntimeMetrics.RowHeight, "the row did not grow for the wrapped text");

        var oneLiner = third.Cells.Single(c => c.Key == HeadBlockLayout.CellOneLiner);
        Assert.Equal(CellWrap.Wrap, oneLiner.Wrap);
        Assert.Equal(CellVAlign.Top, oneLiner.VAlign);
        foreach (var badge in third.Cells.Where(c => c.Kind == CellKind.Badge))
        {
            Assert.Equal(CellVAlign.Top, badge.VAlign);
            Assert.Equal(CellAlign.Right, badge.Align);
        }

        // A one-line description keeps the ordinary row height, so the block does not grow for nothing.
        var shortRows = HeadBlockLayout.Build(MockHead(oneLiner: "Sleep faster."), rowWidth);
        Assert.Equal(
            UnityUiRuntimeMetrics.RowHeight,
            shortRows.Single(r => r.Key == "page-oneliner").Height);
        Assert.True(
            HeadBlockLayout.BlockHeight(rowWidth, longOne, null)
            > HeadBlockLayout.BlockHeight(rowWidth, "Sleep faster.", null),
            "the block's height must follow its one-liner");
    }

    /// <summary>
    /// The warning line is the red <c>(!)</c> in its own fixed cell plus the wrapped body beside it,
    /// and the marker never eats the body's width.
    /// What this would catch: a marker folded into the body text (the mock draws it in a different
    /// color, so it cannot be part of the sentence), or a body measured at the full row width and
    /// therefore under-tall.
    /// </summary>
    [Theory]
    [MemberData(nameof(RowWidths))]
    public void The_warning_line_puts_its_marker_in_a_red_cell_of_its_own(float rowWidth)
    {
        const string body =
            "This mod needs its server component and only the client half is installed here."
            + " It will not work on the servers you join until the server has it too.";
        var rows = HeadBlockLayout.Build(MockHead(warning: body), rowWidth);
        Assert.Equal(4, rows.Count);
        var line = rows.Single(r => r.Key == "page-warning");

        var marker = line.Cells.Single(c => c.Key == HeadBlockLayout.CellWarningMarker);
        Assert.Equal("(!)", marker.Text);
        Assert.Equal(CellTone.Danger, marker.Tone);
        Assert.Equal(MenuPalette.Red, MenuPalette.TextColor(marker.Tone));

        var text = line.Cells.Single(c => c.Key == HeadBlockLayout.CellWarning);
        Assert.Equal(CellWrap.Wrap, text.Wrap);
        // The body is the mock's warning-line orange, which is a reading color; the marker is the
        // red, which is not (see MenuPalette.ReadingTones).
        Assert.Equal(CellTone.Orange, text.Tone);
        Assert.DoesNotContain("(!)", text.Text);

        var placed = MenuPanelLayout.CellColumns(rowWidth, line);
        var markerPlaced = placed.Single(p => p.Key == HeadBlockLayout.CellWarningMarker);
        var textPlaced = placed.Single(p => p.Key == HeadBlockLayout.CellWarning);
        Assert.InRange(markerPlaced.Width, HeadBlockLayout.MarkerWidth - .01f, HeadBlockLayout.MarkerWidth + .01f);
        Assert.True(markerPlaced.Right <= textPlaced.Left + .01f, "the marker must lead the line");
        Assert.False(CellLayout.Overlap(markerPlaced, textPlaced));
        Assert.Equal(HeadBlockLayout.WrappedRowHeight(HeadBlockLayout.WarningLines(body, rowWidth)), line.Height);
    }

    /// <summary>
    /// The title is drawn at the mock's 34 px display size, not at the 20 px a text cell's kind gives
    /// it, and that size is what its column is measured with.
    /// What this would catch: a title at body size (the page would have no visual head at all), or an
    /// override that changes the size without changing the measurement, which would ellipsize it.
    /// </summary>
    [Fact]
    public void The_title_is_drawn_at_the_mocks_display_size()
    {
        var rows = HeadBlockLayout.Build(MockHead(), RowWidthAt3840);
        var title = rows[0].Cells.Single(c => c.Key == HeadBlockLayout.CellTitle);
        Assert.Equal(HeadBlockLayout.TitleFontSize, title.FontSize);
        Assert.Equal(34f, HeadBlockLayout.TitleFontSize);
        Assert.True(
            CellLayout.AutoWidth(title) > CellLayout.EstimateTextWidth(title.Text, CellLayout.TextFontSize),
            "the column must be measured at the display size, not the kind's");
        // A cell that asks for nothing keeps its kind's size.
        Assert.Equal(CellLayout.TextFontSize, new CellSpec(CellKind.Text, "plain").FontSize);
        // And the row is tall enough for the face.
        Assert.True(HeadBlockLayout.TitleRowHeight >= HeadBlockLayout.TitleFontSize, "the title row is shorter than its face");
        Assert.Equal(HeadBlockLayout.TitleRowHeight, rows[0].Height);
    }

    /// <summary>
    /// F11 (live 2026-10-07): "ReloadExample" drew as "ReloadExamp…" on a wide Mods page. The title was
    /// an auto cell sized by the glyph estimate, which runs short of the 34 px display face. It now
    /// fills the row up to the status column, so a 13-character name has far more than its estimate.
    /// What this would catch: a title cell sized to its own caption estimate again.
    /// </summary>
    [Theory]
    [MemberData(nameof(RowWidths))]
    [InlineData(UnityUiRuntimeMetrics.RowWidth)]
    public void A_short_title_gets_the_rest_of_its_row_not_its_estimate(float rowWidth)
    {
        foreach (var spec in new[]
                 {
                     // The Mods page's own head: no status on the title row (the framework line owns it).
                     new HeadSpec { Key = "page", Title = "ReloadExample", MetaLast = true },
                     new HeadSpec { Key = "page", Title = "ReloadExample", StatusLabel = "Status: ", StatusWord = "Pending restart · to load" },
                 })
        {
            var row = HeadBlockLayout.Build(spec, rowWidth)[0];
            var placed = MenuPanelLayout.CellColumns(rowWidth, row);
            var title = placed.Single(p => p.Key == HeadBlockLayout.CellTitle);
            var cell = row.Cells.Single(c => c.Key == HeadBlockLayout.CellTitle);
            float estimate = CellLayout.EstimateTextWidth(cell.Text, HeadBlockLayout.TitleFontSize);
            Assert.True(title.Visible);
            // The real detail panes (both fixtures) and a title row without a status: far beyond the
            // estimate. The 560 px fallback row with a long status: never less than the estimate, which is
            // what the auto cell had.
            bool roomy = rowWidth > UnityUiRuntimeMetrics.RowWidth || spec.StatusWord.Length == 0;
            float want = roomy ? estimate * 1.5f : estimate;
            Assert.True(title.Width >= want, "title width " + title.Width + " for an estimate of " + estimate + " at " + rowWidth);
            for (int i = 0; i < placed.Length; i++)
                for (int j = i + 1; j < placed.Length; j++)
                    Assert.False(CellLayout.Overlap(placed[i], placed[j]), placed[i] + " overlaps " + placed[j]);
            Assert.True(title.Right <= HeadBlockLayout.ContentRight(rowWidth) + .01f);
        }
    }

    /// <summary>
    /// A narrow pane drops the optional meta columns rather than squeezing the title, and the action
    /// button keeps a width its caption fits in.
    /// What this would catch: a head block that only works at the wide fixture.
    /// </summary>
    [Fact]
    public void A_narrow_pane_drops_the_optional_meta_columns_before_it_squeezes_anything()
    {
        var rows = HeadBlockLayout.Build(MockHead(), 520f);
        var meta = rows.Single(r => r.Key == "page-meta");
        var placed = MenuPanelLayout.CellColumns(520f, meta);
        var released = placed.Single(p => p.Key == HeadBlockLayout.CellReleased);
        var version = placed.Single(p => p.Key == HeadBlockLayout.CellVersion);
        var action = placed.Single(p => p.Key == HeadBlockLayout.CellAction);
        Assert.True(released.Dropped || !released.Visible, "the optional released column must go first");
        Assert.True(version.Visible, "the version is not optional");
        Assert.True(action.Visible, "the action button must survive a narrow pane");
        for (int i = 0; i < placed.Length; i++)
            for (int j = i + 1; j < placed.Length; j++)
                Assert.False(CellLayout.Overlap(placed[i], placed[j]), placed[i] + " overlaps " + placed[j]);
    }


    /// <summary>
    /// The meta line carries the mock's separators, and each one lives in the text of the value it
    /// introduces rather than in a cell of its own — so a pane narrow enough to drop `by author`
    /// drops its dot with it and never leaves one dangling.
    /// What this would catch: separator cells, which <see cref="CellLayout.Resolve"/> would drop one
    /// step after the value to their right (it drops optional cells from the right), leaving
    /// `2.0.4 · 2025-05-18 ·` on a narrow pane; or separators glued to the value ahead,
    /// which would leave `2.0.4 ·` when the date went.
    /// </summary>
    [Fact]
    public void The_meta_line_carries_its_separators_inside_the_cell_they_introduce()
    {
        var rows = HeadBlockLayout.Build(MockHead(), RowWidthAt3840);
        var meta = rows.Single(r => r.Key == "page-meta");

        Assert.Equal("2.0.4", meta.Cells.Single(c => c.Key == HeadBlockLayout.CellVersion).Text);
        Assert.Equal("· 2025-05-18", meta.Cells.Single(c => c.Key == HeadBlockLayout.CellReleased).Text);
        Assert.Equal("· by Corvane", meta.Cells.Single(c => c.Key == HeadBlockLayout.CellAuthor).Text);
        Assert.Equal("2.0.4", HeadBlockLayout.MetaCellText("2.0.4", first: true));
        Assert.Equal("· 2.0.4", HeadBlockLayout.MetaCellText("2.0.4", first: false));
        Assert.Equal("", HeadBlockLayout.MetaCellText("", first: false));

        // No cell exists only to hold a dot.
        Assert.DoesNotContain(HeadBlockLayout.MetaSeparator, meta.Cells.Select(c => c.Key));
        Assert.All(meta.Cells, c => Assert.NotEqual(HeadBlockLayout.MetaSeparator, c.Text));

        // The runtime splits it off so it can be drawn muted and outside a mono mspace run.
        Assert.True(HeadBlockLayout.TrySplitMetaSeparator("· 2025-05-18", out string value));
        Assert.Equal("2025-05-18", value);
        Assert.False(HeadBlockLayout.TrySplitMetaSeparator("2.0.4", out value));
        Assert.Equal("2.0.4", value);

        // A pane narrow enough to drop the author drops its dot with it, because they are one cell.
        var narrow = MenuPanelLayout.CellColumns(520f, meta);
        var author = narrow.Single(p => p.Key == HeadBlockLayout.CellAuthor);
        Assert.False(author.Visible, "the author column should go on a 520 px pane");
        foreach (var p in narrow.Where(p => p.Visible))
            Assert.NotEqual(
                HeadBlockLayout.MetaSeparator,
                meta.Cells.Single(c => c.Key == p.Key).Text.Trim());
    }

    /// <summary>
    /// The inline toggle is a chip, not an action button: it asks for its caption plus a button's
    /// padding, and the floor keeps an empty one clickable.
    /// What this would catch: the `CellWidth.Px(170)` live gen 1 drew, and the 200 px a
    /// <see cref="CellKind.Button"/>'s auto width would ask for.
    /// </summary>
    [Fact]
    public void The_inline_toggle_is_sized_to_its_caption()
    {
        float rowWidth = RowWidthAt3840;
        var meta = HeadBlockLayout.Build(MockHead(), rowWidth).Single(r => r.Key == "page-meta");
        var like = meta.Cells.Single(c => c.Key == HeadBlockLayout.CellLike);
        var placed = MenuPanelLayout.CellColumns(rowWidth, meta)
            .Single(p => p.Key == HeadBlockLayout.CellLike);

        float caption = CellLayout.EstimateTextWidth(like.Text, HeadBlockLayout.MetaFontSize);
        Assert.Equal(HeadBlockLayout.ToggleWidth(like.Text), placed.Width, 1);
        Assert.True(placed.Width <= caption + CellLayout.ButtonPad + 0.01f, "wider than its caption plus padding");
        Assert.True(placed.Width < CellLayout.ButtonWidth, "an inline chip must not take the action column's width");
        Assert.True(CellLayout.CaptionFits(like, placed.Width), "the caption must still fit");

        // A short caption keeps the clickable floor; the action button beside it keeps its own width.
        Assert.Equal(HeadBlockLayout.MinToggleWidth, HeadBlockLayout.ToggleWidth(""));
        Assert.Equal(HeadBlockLayout.MinToggleWidth, HeadBlockLayout.ToggleWidth("Like"));
        Assert.True(HeadBlockLayout.ToggleWidth("♥ Liked · 1,234,567 (saved locally)") > HeadBlockLayout.MinToggleWidth);
        Assert.True(
            MenuPanelLayout.CellColumns(rowWidth, meta)
                .Single(p => p.Key == HeadBlockLayout.CellAction).Width >= CellLayout.ButtonWidth - 0.01f);
    }

    // ---- tones ------------------------------------------------------------------------------

    /// <summary>
    /// Every <see cref="LabelTone"/> resolves to a palette color, the three state tones are the
    /// mock's, and each one is readable on the pane fill.
    /// What this would catch: a status word painted with the badge <i>rim</i> color, which is authored
    /// to be read against its own fill and comes out at about 4.1:1 on the pane — under the 4.5 a
    /// caption needs. A reader would call that green "dark" and a color-blind reader would not see it
    /// at all.
    /// </summary>
    [Fact]
    public void Label_tones_resolve_from_the_palette_and_stay_readable_on_the_pane_fill()
    {
        Assert.Equal(MenuPalette.OkText, MenuPalette.LabelColor(LabelTone.Ok));
        Assert.Equal(MenuPalette.PendText, MenuPalette.LabelColor(LabelTone.Pending));
        Assert.Equal(MenuPalette.OrangeText, MenuPalette.LabelColor(LabelTone.Orange));
        Assert.Equal(MenuPalette.Warn, MenuPalette.LabelColor(LabelTone.Warn));
        Assert.Equal(MenuPalette.Muted, MenuPalette.LabelColor(LabelTone.Muted));
        Assert.Equal(MenuPalette.Red, MenuPalette.LabelColor(LabelTone.Danger));
        Assert.Equal(MenuPalette.Text, MenuPalette.LabelColor(LabelTone.Normal));

        // Every tone a sentence or a status word may be drawn in clears the caption floor.
        foreach (var tone in MenuPalette.ReadingTones)
        {
            var colour = MenuPalette.LabelColor(tone);
            Assert.True(
                colour.ContrastAgainst(MenuPalette.PaneFill) >= MenuPalette.MinCaptionContrast,
                tone + " reads at " + colour.ContrastAgainst(MenuPalette.PaneFill).ToString("0.00") + " on the pane fill");
        }

        // The badge rim is the color this deliberately does not use for a word.
        Assert.True(
            MenuPalette.Ok.ContrastAgainst(MenuPalette.PaneFill) < MenuPalette.MinCaptionContrast,
            "if the rim were readable as a word there would be no reason for a second value");

        // Warn and Danger are the two emphasis marks, and are honestly outside the reading set: the
        // mock's red is about 2.9 on the pane. Nothing is said in color alone — the warning sentence
        // beside the red marker is drawn in Orange, which is inside the set.
        Assert.DoesNotContain(LabelTone.Danger, MenuPalette.ReadingTones);
        Assert.DoesNotContain(LabelTone.Warn, MenuPalette.ReadingTones);
        Assert.True(MenuPalette.LabelColor(LabelTone.Danger).ContrastAgainst(MenuPalette.PaneFill) < MenuPalette.MinCaptionContrast);
        Assert.Equal(MenuPalette.OrangeText, MenuPalette.TextColor(CellTone.Orange));

        // The tones are the ones an Install cell row uses, so a badge and a page label agree.
        Assert.Equal(CellTone.Ok, LabelTones.ToCellTone(LabelTone.Ok));
        Assert.Equal(CellTone.Pending, LabelTones.ToCellTone(LabelTone.Pending));
        Assert.Equal(CellTone.Orange, LabelTones.ToCellTone(LabelTone.Orange));
        Assert.Equal(CellTone.Danger, LabelTones.ToCellTone(LabelTone.Danger));
        Assert.Equal(CellTone.Normal, LabelTones.ToCellTone(LabelTone.Normal));

        // Names round-trip, because they are the readback.
        foreach (LabelTone tone in Enum.GetValues<LabelTone>())
            Assert.Equal(tone, LabelTones.Parse(LabelTones.Name(tone)));
        Assert.Equal("", LabelTones.Name((LabelTone?)null));
    }

    /// <summary>
    /// A toned label keeps its tone in its own state and reports it, so the readback says which color
    /// was applied even on a runtime that paints nothing.
    /// What this would catch: a tone written straight to TMP and nowhere else — a reader, and every
    /// test, would then have no way to know what color a word is.
    /// </summary>
    [Fact]
    public void A_toned_label_reports_the_tone_it_was_given()
    {
        var panel = Panel();
        var label = panel.Label("Installed", LabelTone.Ok);
        Assert.Equal(LabelTone.Ok, label.Tone);
        Assert.Equal("ok", label.Describe()["tone"]);
        Assert.Equal("label", label.Describe()["kind"]);

        label.SetTone(LabelTone.Orange);
        Assert.Equal(LabelTone.Orange, label.Tone);
        label.SetText("Pending restart · to uninstall", LabelTone.Orange);
        Assert.Equal("Pending restart · to uninstall", label.Text);
        Assert.Equal("orange", label.Describe()["tone"]);

        // A panel this runtime cannot draw still keeps the tone.
        var offline = UiToneLabelHandle.Disabled(null, LabelTone.Pending);
        Assert.False(offline.Enabled);
        Assert.Equal(LabelTone.Pending, offline.Tone);
        offline.SetTone(LabelTone.Ok);
        Assert.Equal(LabelTone.Ok, offline.Tone);
    }

    // ---- the icon link ----------------------------------------------------------------------

    /// <summary>
    /// The icon link is a square button whose readback says what it is, what it means and where it
    /// goes, and whose glyph is one the vanilla font actually has.
    /// What this would catch: a link whose destination is only in a closure (a reader could not tell
    /// where it goes), or a glyph outside WGL4, which the game's TMP font draws as the missing-glyph
    /// box — the live evidence for that is in docs/mods-menu.md.
    /// </summary>
    [Fact]
    public void The_icon_link_reads_back_its_kind_tooltip_and_url()
    {
        var panel = Panel();
        int clicks = 0;
        var link = panel.IconLink(
            MenuPanelLayout.IconLinkGlyph, "Open the store page", () => clicks++,
            url: "https://thunderstore.io/c/v-rising/p/Corvane/CryptSnooze/");

        var read = link.Describe();
        Assert.Equal("link", read["kind"]);
        Assert.Equal("Open the store page", read["tooltip"]);
        Assert.Equal("https://thunderstore.io/c/v-rising/p/Corvane/CryptSnooze/", read["url"]);
        Assert.Equal(MenuPanelLayout.IconLinkGlyph, read["glyph"]);

        // WGL4 (Latin-1, in fact), so the vanilla TMP font has it.
        Assert.Equal("»", MenuPanelLayout.IconLinkGlyph);
        Assert.True(MenuPanelLayout.IconLinkGlyph[0] <= 0x00ff, "the fallback glyph must stay inside Latin-1");
        Assert.True(MenuPanelLayout.IconLinkSize > 0f && MenuPanelLayout.IconLinkFontSize < MenuPanelLayout.IconLinkSize);

        // The url is state, not paint: it can be rewritten without rebuilding the row.
        link.Url = "";
        Assert.Equal("", link.Describe()["url"]);

        // A link with no listing still exists, so the readback can say the url is empty.
        var offline = UiIconLinkHandle.Disabled(null, MenuPanelLayout.IconLinkGlyph, "Open the store page");
        Assert.False(offline.Enabled);
        Assert.Equal("", offline.Describe()["url"]);
        offline.SetSprite(new object());
        Assert.Equal(MenuPanelLayout.IconLinkGlyph, offline.Glyph);
        Assert.Equal(0, clicks);
    }

    // ---- the sidebar row --------------------------------------------------------------------

    /// <summary>
    /// A sidebar row's marker and dot are parts of the row, not characters in its caption: the caption
    /// is the mod's name clipped to the space they leave, and both are in the readback by name.
    /// What this would catch: the pre-KIT1 caption (<c>(!) HuntsAndGates ●</c>), where a search match,
    /// a bridge click and a screen reader all saw the glyphs as part of the name, and the state was a
    /// shape rather than a color.
    /// </summary>
    [Fact]
    public void A_sidebar_row_carries_its_marker_and_dot_beside_the_caption()
    {
        var panel = Panel();
        var row = panel.Button("HuntsAndGates", ModPageCopy.WarningMarker, LabelTone.Pending);
        var read = row.Describe();
        Assert.Equal("button", read["kind"]);
        Assert.Equal("HuntsAndGates", read["label"]);
        Assert.Equal("(!)", read["marker"]);
        Assert.Equal("pending", read["dot"]);

        // Both can change without rebuilding the row: a pending install becomes installed in place.
        row.SetDot(null);
        Assert.Null(row.Describe()["dot"]);
        row.SetMarker(null);
        Assert.Equal("", row.Describe()["marker"]);

        // An ordinary installed row asks for neither.
        var plain = panel.Button("CryptSnooze", null, null);
        Assert.Equal("", plain.Describe()["marker"]);
        Assert.Null(plain.Describe()["dot"]);
    }

    /// <summary>
    /// The caption is clipped to what the marker and the dot leave, so a long id cannot run under
    /// either of them, and a row that asks for neither keeps the whole width it always had.
    /// What this would catch: a caption measured at the full row width, which at the live 213 px
    /// sidebar is how a long id overflowed in the first place.
    /// </summary>
    [Fact]
    public void The_marked_caption_is_clipped_to_the_space_the_marker_and_dot_leave()
    {
        const float rowWidth = 320f;
        float plain = MenuRowStyle.CaptionWidthFor(rowWidth, marker: false, dot: false);
        float marked = MenuRowStyle.CaptionWidthFor(rowWidth, marker: true, dot: true);
        Assert.Equal(rowWidth, plain);
        Assert.True(marked < plain, "a marker and a dot must cost the caption width");
        Assert.Equal(
            rowWidth - MenuRowStyle.RowMarkPad - MenuRowStyle.RowMarkerWidth
            - MenuRowStyle.RowMarkPad - MenuRowStyle.RowDotSize - MenuRowStyle.RowMarkPad,
            marked);

        const string longId = "ExampleTeam-HuntsAndGatesAndEverythingElseThisAuthorEverShipped";
        string clipped = MenuRowStyle.MarkedCaption(longId, rowWidth, marker: true, dot: true);
        Assert.True(clipped.Length < longId.Length, "a long name must be ellipsized");
        Assert.EndsWith("…", clipped);
        Assert.DoesNotContain("(!)", clipped);
        Assert.Equal("CryptSnooze", MenuRowStyle.MarkedCaption("CryptSnooze", rowWidth, false, false));
        // The parts have somewhere to be drawn.
        Assert.True(MenuRowStyle.RowMarkerWidth > 0f && MenuRowStyle.RowDotSize > 0f);
    }

    // ---- the prompt block -------------------------------------------------------------------

    /// <summary>
    /// The prompt is one block: a heading, a muted body, and Yes and No on one row packed against its
    /// right edge with Yes outermost — the mock's <c>justify-content:flex-end</c> pair.
    /// What this would catch: two full-width stacked buttons (the pre-KIT1 shape), or a Yes that is
    /// not the rightmost control, which is the one the player's hand goes to.
    /// </summary>
    [Fact]
    public void The_prompt_block_is_a_heading_a_muted_body_and_one_right_aligned_pair()
    {
        var panel = Panel();
        int yes = 0, no = 0;
        var block = panel.Prompt(
            "modPrompt-uninstall", "Uninstall CryptSnooze 2.0.4?",
            "Removed from your client at the next restart of V Rising.",
            "Yes, uninstall", LabelTone.Orange, "No, keep it");

        Assert.NotNull(block.Heading);
        Assert.NotNull(block.Body);
        Assert.NotNull(block.Buttons);
        Assert.Equal("Uninstall CryptSnooze 2.0.4?", block.Heading!.Text);
        Assert.Equal("Removed from your client at the next restart of V Rising.", block.Body!.Text);
        Assert.Equal("modPrompt-uninstall-buttons", block.Buttons!.Key);

        var cells = block.Buttons.Cells;
        var yesCell = cells.Single(c => c.Key == UiPromptBlock.CellYes);
        var noCell = cells.Single(c => c.Key == UiPromptBlock.CellNo);
        Assert.Equal(CellKind.Button, yesCell.Kind);
        Assert.Equal(CellAlign.Right, yesCell.Align);
        Assert.Equal(CellAlign.Right, noCell.Align);
        Assert.Equal(CellTone.Orange, yesCell.Tone);
        Assert.Equal(CellTone.Normal, noCell.Tone);

        float rowWidth = RowWidthAt3840;
        var placed = MenuPanelLayout.CellColumns(rowWidth, cells);
        var yesPlaced = placed.Single(p => p.Key == UiPromptBlock.CellYes);
        var noPlaced = placed.Single(p => p.Key == UiPromptBlock.CellNo);
        Assert.InRange(yesPlaced.Right, rowWidth / 2f - .01f, rowWidth / 2f + .01f);
        Assert.True(noPlaced.Right <= yesPlaced.Left + .01f, "No must sit left of Yes");
        Assert.False(CellLayout.Overlap(yesPlaced, noPlaced));

        block.OnYes(() => yes++);
        block.OnNo(() => no++);
        Assert.True(UiCellRowHandle.TryInvoke("modPrompt-uninstall-buttons/" + UiPromptBlock.CellYes, out _));
        Assert.Equal(1, yes);
        Assert.Equal(0, no);
        Assert.True(UiCellRowHandle.TryInvoke("No, keep it", out _));
        Assert.Equal(1, no);
    }

    // ---- the block through a panel ----------------------------------------------------------

    /// <summary>
    /// Built on a panel the head block is three cell rows with the page's keys, its toggle and action
    /// cells are clickable through the row's own slots, and the status can be rewritten in place
    /// without rebuilding the page.
    /// What this would catch: an action button with no slot (the mock's Uninstall would be a picture of
    /// a button), or a status change that needs the whole page rebuilt, which loses the scroll.
    /// </summary>
    [Fact]
    public void A_head_block_on_a_panel_is_clickable_and_rewritable_in_place()
    {
        var panel = Panel();
        int likes = 0, actions = 0;
        var block = panel.HeadBlock(MockHead(), RowWidthAt3840);

        Assert.True(block.Enabled);
        Assert.Equal(3, block.Rows.Count);
        Assert.Equal("page-title", block.TitleRow!.Key);
        Assert.Equal("page-meta", block.MetaRow!.Key);
        Assert.Equal("page-oneliner", block.OneLinerRow!.Key);
        Assert.Null(block.WarningRow);
        Assert.Equal(RowWidthAt3840, block.RowWidth);

        block.OnToggle(() => likes++);
        block.OnAction(() => actions++);
        Assert.True(UiCellRowHandle.TryInvoke("page-meta/" + HeadBlockLayout.CellLike, out _));
        Assert.True(UiCellRowHandle.TryInvoke("page-meta/" + HeadBlockLayout.CellAction, out _));
        Assert.Equal(1, likes);
        Assert.Equal(1, actions);

        // In place: the spec is the value of record and the runtime is told about that cell only.
        var row = _runtime.Rows.Single(r => r.Text == "page-title");
        row.Applied.Clear();
        Assert.True(block.SetStatus("Pending restart · to uninstall", LabelTone.Orange));
        Assert.Equal(new[] { HeadBlockLayout.CellStatusWord }, row.Applied.ToArray());
        var word = block.TitleRow.Cells.Single(c => c.Key == HeadBlockLayout.CellStatusWord);
        Assert.Equal("Pending restart · to uninstall", word.Text);
        Assert.Equal(CellTone.Orange, word.Tone);
        Assert.True(block.SetAction("Reactivate", LabelTone.Ok));
        Assert.Equal(CellTone.Ok, block.MetaRow.Cells.Single(c => c.Key == HeadBlockLayout.CellAction).Tone);
        Assert.True(block.SetToggle("♥ Liked · 133", on: true));
        Assert.Equal(CellTone.Gold, block.MetaRow.Cells.Single(c => c.Key == HeadBlockLayout.CellLike).Tone);

        // Each row describes itself with a rect per cell, which is where the page's geometry is read.
        var described = block.TitleRow.Describe();
        Assert.Equal("cellrow", described["kind"]);
        Assert.NotNull(described["rect"]);
        var titleCell = ((List<object?>)described["cells"]!)
            .Cast<Dictionary<string, object?>>()
            .Single(c => (string?)c["key"] == HeadBlockLayout.CellTitle);
        Assert.NotNull(titleCell["rect"]);
    }

    /// <summary>
    /// A head block on a runtime with no cell-row lane keeps every fact and draws nothing, the way
    /// every other kit handle degrades.
    /// What this would catch: a block that throws (or silently loses the status) on the server or on a
    /// panel that could not be built.
    /// </summary>
    [Fact]
    public void A_head_block_with_no_runtime_lane_keeps_its_state()
    {
        var block = UiHeadBlock.Build(null, MockHead(), RowWidthAt3840);
        Assert.False(block.Enabled);
        Assert.Empty(block.Rows);
        Assert.Null(block.TitleRow);
        Assert.False(block.SetStatus("Installed", LabelTone.Ok));
        Assert.Same(Noop.Instance, block.OnAction(() => { }));
        Assert.Equal("CryptSnooze", block.Spec.Title);

        // And an empty spec is a block, not an exception.
        Assert.Empty(HeadBlockLayout.Build(null, 1248f));
        var bare = HeadBlockLayout.Build(new HeadSpec(), 1248f);
        Assert.Equal(2, bare.Count);
    }

    /// <summary>
    /// The two fixtures really are the two the mock was approved at, so a geometry test that passes
    /// here has been measured at the pane widths the live captures came from.
    /// </summary>
    [Fact]
    public void The_fixtures_are_the_1248_px_detail_pane_and_the_1600_fallback()
    {
        Assert.Equal(1248f, RowWidthAt3840);
        Assert.True(RowWidthAt1600 < RowWidthAt3840 && RowWidthAt1600 > 600f, "the 1600 fixture is the narrower pane");
        // The right column never takes more than the 45 % a pinned header's action column may.
        foreach (float width in new[] { RowWidthAt3840, RowWidthAt1600, 520f })
        {
            HeadBlockLayout.Columns(width, 4000f, out float left, out float right);
            Assert.True(right <= (width - CellLayout.Gap) * .45f + .01f, "the right column broke its cap at " + width);
            Assert.True(left >= right, "the left column must keep the larger half at " + width);
            // HEAD2: the two columns, the gap between them and the right inset are the whole row —
            // the inset is reserved by the model, not taken out of a squeezed column.
            Assert.InRange(
                left + right + CellLayout.Gap + HeadBlockLayout.RightColumnInset, width - .01f, width + .01f);
            Assert.InRange(HeadBlockLayout.ContentWidth(width), width - HeadBlockLayout.RightColumnInset - .01f, width);
        }
        // A column that asks for little still gets the mock's minimum.
        HeadBlockLayout.Columns(RowWidthAt3840, 10f, out _, out float min);
        Assert.Equal(HeadBlockLayout.RightColumnMinWidth, min);
    }
}
