using System;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

public sealed class MenuPanelLayoutTests
{
    [Theory]
    [InlineData(640f, 480f)]
    [InlineData(1024f, 768f)]
    [InlineData(1664f, 870f)]
    [InlineData(1920f, 1080f)]
    [InlineData(3440f, 1440f)]
    public void Panes_fit_canvas_without_overlap_and_share_top_and_bottom(float width, float height)
    {
        var left = MenuPanelLayout.ForCanvas(width, height, true);
        var right = MenuPanelLayout.ForCanvas(width, height, false);
        Assert.Equal(left.Height, right.Height);
        Assert.Equal(left.Y, right.Y);
        Assert.True(left.X + left.Width / 2f < right.X - right.Width / 2f);
        Assert.True(left.X - left.Width / 2f >= -width / 2f);
        Assert.True(right.X + right.Width / 2f <= width / 2f);
        Assert.True(left.Y + left.Height / 2f < height / 2f);
        Assert.True(left.Y - left.Height / 2f > -height / 2f);
        Assert.True(left.Height > PanelLayout.HeaderHeight);
        Assert.True(left.RowWidth > 0f);
    }

    [Fact]
    public void Normal_canvas_reserves_native_header_and_uses_readable_design_widths()
    {
        var left = MenuPanelLayout.ForCanvas(1920f, 1080f, true);
        var right = MenuPanelLayout.ForCanvas(1920f, 1080f, false);
        Assert.InRange(left.Width, 300f, 360f);
        Assert.InRange(right.Width, 700f, 900f);
        Assert.True(left.Y + left.Height / 2f <= 540f - MenuPanelLayout.TopInset);
        Assert.Equal(800f, left.Height);
        Assert.True(PanelLayout.Scrolls(900f, PanelLayout.ViewportHeightFor(left.Height)));
        Assert.False(PanelLayout.Scrolls(100f, PanelLayout.ViewportHeightFor(right.Height)));
    }

    [Theory]
    [InlineData(640f)]
    [InlineData(1024f)]
    [InlineData(1920f)]
    public void Caption_and_control_columns_fit_both_panes_when_resized(float canvasWidth)
    {
        foreach (bool sidebar in new[] { true, false })
        {
            var pane = MenuPanelLayout.ForCanvas(canvasWidth, 1080f, sidebar);
            var columns = MenuPanelLayout.Columns(pane.RowWidth, true);
            Assert.True(columns.LabelWidth > 0f);
            Assert.True(columns.ControlWidth > 0f);
            Assert.True(columns.LabelX + columns.LabelWidth / 2f < columns.ControlLeft);
            Assert.InRange(columns.LabelX - columns.LabelWidth / 2f, -pane.RowWidth / 2f - .01f, 0f);
            Assert.True(columns.ControlX + columns.ControlWidth / 2f <= pane.RowWidth / 2f + .01f);
            var labelOnly = MenuPanelLayout.Columns(pane.RowWidth, false);
            Assert.Equal(pane.RowWidth, labelOnly.LabelWidth);
            Assert.Equal(0f, labelOnly.LabelX);
        }
    }

    [Fact]
    public void Unknown_canvas_uses_design_space_fallback_without_nan_geometry()
    {
        var unknown = MenuPanelLayout.ForCanvas(float.NaN, float.PositiveInfinity, true);
        var fallback = MenuPanelLayout.ForCanvas(1920f, 1080f, true);
        Assert.Equal(fallback.Width, unknown.Width);
        Assert.Equal(fallback.Height, unknown.Height);
        Assert.Equal(fallback.X, unknown.X);
        Assert.Equal(fallback.Y, unknown.Y);
    }

    [Fact]
    public void ClipLabel_keeps_short_captions_and_ellipsises_long_ones_inside_the_row()
    {
        var pane = MenuPanelLayout.ForCanvas(1280f, 720f, true);
        Assert.Equal("Progenitor Sample", MenuPanelLayout.ClipLabel("Progenitor Sample", pane.RowWidth));
        Assert.Equal("", MenuPanelLayout.ClipLabel("", pane.RowWidth));
        Assert.Equal("", MenuPanelLayout.ClipLabel(null, pane.RowWidth));

        string longId = "progenitor.sample.consentdemo.overflow-row-with-a-long-caption";
        string clipped = MenuPanelLayout.ClipLabel(longId, pane.RowWidth);
        Assert.True(clipped.Length < longId.Length);
        Assert.EndsWith("\u2026", clipped);
        Assert.StartsWith("progenitor.sample", clipped);
        Assert.True(clipped.Length * MenuPanelLayout.CaptionFontSize * MenuPanelLayout.AverageGlyphEm
            <= pane.RowWidth + MenuPanelLayout.CaptionFontSize);
    }

    [Fact]
    public void ListOverflows_is_false_for_a_short_list_and_true_once_rows_fill_the_pane()
    {
        Assert.False(MenuPanelLayout.ListOverflows(MenuPanelLayout.SidebarRowCount(2), 1920f, 1080f));
        Assert.False(MenuPanelLayout.ListOverflows(MenuPanelLayout.SidebarRowCount(2), 1280f, 720f));
        Assert.True(MenuPanelLayout.ListOverflows(MenuPanelLayout.SidebarRowCount(24), 1920f, 1080f));
        Assert.True(MenuPanelLayout.ListOverflows(MenuPanelLayout.SidebarRowCount(24), 1280f, 720f));
        Assert.True(MenuPanelLayout.ListOverflows(MenuPanelLayout.SidebarRowCount(12), 1280f, 720f));
        Assert.Equal(MenuPanelLayout.ListChromeRows + 1, MenuPanelLayout.SidebarRowCount(0));
        Assert.Equal(MenuPanelLayout.ListChromeRows + 5, MenuPanelLayout.SidebarRowCount(5));
    }

    [Fact]
    public void VisibleCatalogRows_is_the_rendered_subset_not_the_whole_catalog()
    {
        Assert.Equal(0, MenuPanelLayout.VisibleCatalogRows(0, 1920f, 1080f));
        int visible = MenuPanelLayout.VisibleCatalogRows(51, 1920f, 1080f, normalizedPosition: 1f);
        Assert.InRange(visible, 8, 20);
        Assert.True(visible < 51);
        int mid = MenuPanelLayout.VisibleCatalogRows(51, 1920f, 1080f, normalizedPosition: 0.7209f);
        Assert.InRange(mid, 8, 20);
        Assert.True(mid < 51);
        Assert.Equal(2, MenuPanelLayout.VisibleCatalogRows(2, 1920f, 1080f));
    }

    [Fact]
    public void A_row_scrolled_above_the_viewport_is_not_the_raycast_target_at_the_search_field()
    {
        const float searchY = 290f;
        const float searchH = 40f;
        const float viewportTop = 338f;
        const float viewportHeight = 600f;
        const float rowTop = 311f;
        const float rowHeight = 40f;

        Assert.False(MenuPanelLayout.RowIsRaycastTarget(rowTop, rowHeight, viewportTop, viewportHeight, searchY + searchH * 0.5f));
        Assert.True(MenuPanelLayout.RowIsRaycastTarget(viewportTop, rowHeight, viewportTop, viewportHeight, viewportTop + 10f));
    }

    [Fact]
    public void Wheel_notches_move_normalized_scroll_toward_the_user()
    {
        const float rowStep = 48f;
        float next = MenuPanelLayout.NextScrollPosition(1f, -120f, 2400f, 600f, rowStep);
        Assert.True(next < 1f);
        Assert.True(next > 0f);
        float back = MenuPanelLayout.NextScrollPosition(next, 120f, 2400f, 600f, rowStep);
        Assert.InRange(back, 0.99f, 1f);
        Assert.Equal(1f, MenuPanelLayout.NextScrollPosition(1f, -120f, 400f, 600f, rowStep));
        float moved = (1f - next) * (2400f - 600f) / rowStep;
        Assert.InRange(moved, 1f, 3f);
        Assert.Equal(MenuPanelLayout.WheelRowsPerNotch, moved, 3);
    }

    [Fact]
    public void A_single_wheel_event_produces_a_single_scroll_delta()
    {
        const float uguiLeftover = 96f;
        float pump = MenuPanelLayout.PumpPixelsPerNotch;
        Assert.Equal(0f, MenuPanelLayout.UguiScrollSensitivity);
        Assert.Equal(0f, MenuPanelLayout.UguiRowsPerNotch);
        Assert.Equal(pump, MenuPanelLayout.PixelsForWheelEvent(uguiLeftover, pump, uguiConsumed: false));
        Assert.Equal(uguiLeftover, MenuPanelLayout.PixelsForWheelEvent(uguiLeftover, pump, uguiConsumed: true));
        Assert.NotEqual(uguiLeftover + pump, MenuPanelLayout.PixelsForWheelEvent(uguiLeftover, pump, uguiConsumed: false));
        Assert.NotEqual(uguiLeftover + pump, MenuPanelLayout.PixelsForWheelEvent(uguiLeftover, pump, uguiConsumed: true));
        Assert.Equal("pump", MenuPanelLayout.WheelMover);
        Assert.Equal(UiShownInput.AppliedUgui, UiShownInput.AppliedToken(true, true, false));
        Assert.Equal(UiShownInput.AppliedNone, UiShownInput.AppliedToken(true, false, false));
        Assert.Equal(UiShownInput.AppliedBoth, UiShownInput.AppliedToken(true, true, true));
        Assert.Equal(UiShownInput.AppliedPump, UiShownInput.AppliedToken(true, false, true));
        Assert.Equal(
            MenuPanelLayout.RowStep * MenuPanelLayout.WheelRowsPerNotch,
            MenuPanelLayout.PumpPixelsPerNotch,
            3);
    }

    [Fact]
    public void A_held_wheel_reading_is_not_a_second_notch()
    {
        Assert.False(UiShownInput.IsNewNotch(0f, 0f, false));
        Assert.True(UiShownInput.IsNewNotch(120f, 0f, false));
        Assert.False(UiShownInput.IsNewNotch(120f, 120f, true));
        Assert.True(UiShownInput.IsNewNotch(-120f, 120f, true));
        Assert.True(UiShownInput.IsNewNotch(120f, 0f, false));
        Assert.False(UiShownInput.HasWheelDelta(0f));
        Assert.True(UiShownInput.HasWheelDelta(120f));
    }

    [Fact]
    public void Shown_input_line_names_the_mover_and_the_notch()
    {
        string pass = UiShownInput.Format(
            shown: 2, hoverPump: false, deltaY: 120f, notch: true, hit: "Mods",
            pos: 0.241f, nextPos: 0.291f, content: 2464f, view: 568f,
            applied: UiShownInput.AppliedPump, path: MenuPanelLayout.WheelMover,
            keys: 0, typed: '\0', focused: false, selected: null,
            click: false, activated: false);
        Assert.StartsWith("Ui: shown-input ", pass);
        Assert.Contains("deltaY=120", pass);
        Assert.Contains("notch=1", pass);
        Assert.Contains("hit='Mods'", pass);
        Assert.Contains("pos=0.241->0.291", pass);
        Assert.Contains("applied=pump", pass);
        Assert.Contains("path=pump", pass);

        string dropped = UiShownInput.Format(
            shown: 2, hoverPump: false, deltaY: 120f, notch: true, hit: "Mods",
            pos: 0.241f, nextPos: null, content: 2464f, view: 568f,
            applied: UiShownInput.AppliedNone, path: MenuPanelLayout.WheelMover,
            keys: 0, typed: '\0', focused: false, selected: null,
            click: false, activated: false);
        Assert.Contains("notch=1", dropped);
        Assert.Contains("applied=none", dropped);
        Assert.DoesNotContain("->", dropped);

        string doubled = UiShownInput.Format(
            shown: 2, hoverPump: false, deltaY: 120f, notch: true, hit: "Mods",
            pos: 0.241f, nextPos: 0.341f, content: 2464f, view: 568f,
            applied: UiShownInput.AppliedBoth, path: MenuPanelLayout.WheelMover,
            keys: 0, typed: '\0', focused: false, selected: null,
            click: false, activated: false);
        Assert.Contains("notch=1", doubled);
        Assert.Contains("applied=both", doubled);

        string held = UiShownInput.Format(
            shown: 2, hoverPump: false, deltaY: 120f, notch: false, hit: "Mods",
            pos: 0.241f, nextPos: null, content: 2464f, view: 568f,
            applied: UiShownInput.AppliedNone, path: MenuPanelLayout.WheelMover,
            keys: 0, typed: '\0', focused: false, selected: null,
            click: false, activated: false);
        Assert.Contains("notch=0", held);
        Assert.Contains("applied=none", held);
    }

    [Fact]
    public void One_notch_moves_the_same_few_rows_from_five_to_five_hundred()
    {
        Assert.InRange(MenuPanelLayout.WheelRowsPerNotch, 1f, 3f);
        Assert.Equal(0f, MenuPanelLayout.UguiRowsPerNotch);
        Assert.Equal(
            MenuPanelLayout.RowStep * MenuPanelLayout.WheelRowsPerNotch,
            MenuPanelLayout.PumpPixelsPerNotch,
            3);

        float view = MenuPanelLayout.ListViewportHeight(MenuPanelLayout.ForCanvas(1920f, 1080f, true).Height);
        foreach (int n in new[] { 5, 51, 500 })
        {
            var layout = new PanelLayout();
            for (int i = 0; i < n; i++)
                layout.NextRow(UnityUiRuntimeMetrics.RowHeight);
            float content = layout.ContentHeight;
            float moved = MenuPanelLayout.RowsMovedByNotch(content, view, MenuPanelLayout.RowStep);
            if (content <= view)
            {
                Assert.Equal(0f, moved);
                continue;
            }

            Assert.InRange(moved, 1f, 3f);
            Assert.Equal(MenuPanelLayout.WheelRowsPerNotch, moved, 3);
        }
    }

    [Fact]
    public void A_captured_mid_list_position_round_trips_at_point_seven_seven()
    {
        const float captured = 0.77f;
        const float content = 2400f;
        const float view = 600f;
        Assert.True(MenuPanelLayout.CanApplyNormalized(content, view));
        float y = MenuPanelLayout.ContentOffsetY(captured, content, view);
        Assert.InRange(MenuPanelLayout.NormalizedFromOffset(y, content, view), 0.769f, 0.771f);
        Assert.False(MenuPanelLayout.CanApplyNormalized(16f, 568f));
        Assert.Equal(captured, MenuPanelLayout.ClampNormalized(captured));
        Assert.Equal(0f, MenuPanelLayout.ContentOffsetY(captured, 16f, 568f));
    }

    /// <summary>
    /// Live 2026-09-10: capture 0.778, restore applied 0.778, then two settle layouts
    /// moved the list to the bottom (normalized 0). The existing round-trip test
    /// asserts at restore time and misses that. This drives restore <b>and</b> the
    /// subsequent settle, with Unity's post-restore zero as <c>currentY</c>, and
    /// asserts the position after settle — not at restore.
    /// </summary>
    [Fact]
    public void A_restore_then_settle_layout_keeps_the_captured_mid_list_position()
    {
        const float captured = 0.778f;
        const float content = 2464f;
        const float view = 546f;
        float capturedY = MenuPanelLayout.ContentOffsetY(captured, content, view);
        // Live clobber: Unity's settle pass lands at normalized 0 (the bottom),
        // not at anchoredPosition y=0 (the top).
        float bottomY = MenuPanelLayout.ContentOffsetY(0f, content, view);

        SidebarScrollLayout.Apply(
            hasPending: true, captured, capturedY,
            resetScroll: true, firstShow: true, RulerSettleState.Armed,
            currentY: bottomY, content, view,
            out float afterShow, out _);
        Assert.InRange(MenuPanelLayout.NormalizedFromOffset(afterShow, content, view), 0.777f, 0.779f);

        // RestoreScroll writes the captured offset and re-arms so settle can peek it.
        // This is the value the old test stopped at — it must not be the assertion
        // that closes the defect.
        float afterRestore = MenuPanelLayout.ContentOffsetY(captured, content, view);
        Assert.InRange(MenuPanelLayout.NormalizedFromOffset(afterRestore, content, view), 0.777f, 0.779f);

        // Live: two re-layouts after restore, each with the content already at the bottom.
        SidebarScrollLayout.Apply(
            hasPending: true, captured, capturedY,
            resetScroll: false, firstShow: false, RulerSettleState.Final,
            currentY: bottomY, content, view,
            out _, out bool pendingAfterFinal);

        SidebarScrollLayout.Apply(
            hasPending: pendingAfterFinal, captured, capturedY,
            resetScroll: false, firstShow: false, RulerSettleState.Settled,
            currentY: bottomY, content, view,
            out float afterSettled, out bool pendingAfterSettled);

        // Final position after settle, not the position at restore time.
        float finalPos = MenuPanelLayout.NormalizedFromOffset(afterSettled, content, view);
        Assert.InRange(finalPos, 0.777f, 0.779f);
        Assert.NotEqual(0f, finalPos);
        Assert.False(pendingAfterSettled);

        string line = SidebarScrollLayout.FormatPreserveLog(
            "list", captured, finalPos, content, view, RulerSettle.Name(RulerSettleState.Settled));
        Assert.StartsWith("Mods workspace: scroll-preserve ", line);
        Assert.Contains("capture=0.778", line);
        Assert.Contains("restore=0.778", line);
        Assert.Contains("settle=settled", line);
        Assert.Contains("applied=offset", line);
    }

    [Fact]
    public void Overview_reset_and_a_fresh_open_stay_at_the_top_through_settle()
    {
        const float content = 2464f;
        const float view = 546f;

        SidebarScrollLayout.Apply(
            hasPending: true, pendingNormalized: 1f, pendingOffsetY: 0f,
            resetScroll: true, firstShow: true, RulerSettleState.Armed,
            currentY: 0f, content, view,
            out float afterShow, out bool pendingAfterShow);
        Assert.True(pendingAfterShow);
        Assert.Equal(0f, afterShow);
        Assert.Equal(1f, MenuPanelLayout.NormalizedFromOffset(afterShow, content, view));

        SidebarScrollLayout.Apply(
            hasPending: pendingAfterShow, pendingNormalized: 1f, pendingOffsetY: 0f,
            resetScroll: false, firstShow: false, RulerSettleState.Settled,
            currentY: 0f, content, view,
            out float afterSettled, out bool pendingAfterSettled);
        Assert.False(pendingAfterSettled);
        Assert.Equal(0f, afterSettled);
        Assert.Equal(1f, MenuPanelLayout.NormalizedFromOffset(afterSettled, content, view));

        SidebarScrollLayout.Apply(
            hasPending: false, pendingNormalized: 0.778f, pendingOffsetY: 400f,
            resetScroll: true, firstShow: true, RulerSettleState.Armed,
            currentY: 400f, content, view,
            out float freshShow, out bool freshPending);
        Assert.False(freshPending);
        Assert.Equal(0f, freshShow);
        Assert.Equal(1f, MenuPanelLayout.NormalizedFromOffset(freshShow, content, view));

        SidebarScrollLayout.Apply(
            hasPending: false, pendingNormalized: 0.778f, pendingOffsetY: 400f,
            resetScroll: false, firstShow: false, RulerSettleState.Settled,
            currentY: 0f, content, view,
            out float freshSettled, out bool freshPendingAfter);
        Assert.False(freshPendingAfter);
        Assert.Equal(0f, freshSettled);
        Assert.Equal(1f, MenuPanelLayout.NormalizedFromOffset(freshSettled, content, view));
    }

    [Fact]
    public void After_settle_a_layout_pass_keeps_the_wheel_pump_offset()
    {
        const float content = 2464f;
        const float view = 546f;
        float wheelY = MenuPanelLayout.ContentOffsetY(0.65f, content, view);

        Assert.False(SidebarScrollLayout.KeepPending(RulerSettleState.Settled, firstShow: false));
        SidebarScrollLayout.Apply(
            hasPending: false, pendingNormalized: 0.778f, pendingOffsetY: 400f,
            resetScroll: false, firstShow: false, RulerSettleState.Final,
            currentY: wheelY, content, view,
            out float kept, out bool pending);
        Assert.False(pending);
        Assert.Equal(wheelY, kept);
        Assert.InRange(MenuPanelLayout.NormalizedFromOffset(kept, content, view), 0.64f, 0.66f);
    }

    [Fact]
    public void Selected_row_style_is_a_fill_not_a_glyph()
    {
        Assert.Equal(
            MenuRowStyle.CatalogCaption("Progenitor Sample", 300f),
            MenuRowStyle.CatalogCaption("Progenitor Sample", 300f));
        Assert.Equal("Progenitor Sample", MenuRowStyle.CatalogCaption("Progenitor Sample", 300f));
        Assert.DoesNotContain("\u203a", MenuRowStyle.CatalogCaption("Alpha", 300f));
        Assert.DoesNotContain(">", MenuRowStyle.CatalogCaption("Alpha", 300f));
        Assert.True(MenuRowStyle.SelectedR > MenuRowStyle.NormalR);
        Assert.True(MenuRowStyle.SelectedG > MenuRowStyle.NormalG);
        Assert.NotEqual(MenuRowStyle.SelectedR, MenuRowStyle.HoverR);
    }

    [Fact]
    public void Chrome_rows_do_not_share_catalogue_or_selected_style()
    {
        Assert.NotEqual(MenuRowStyle.ChromeR, MenuRowStyle.NormalR);
        Assert.NotEqual(MenuRowStyle.ChromeG, MenuRowStyle.NormalG);
        Assert.NotEqual(MenuRowStyle.ChromeB, MenuRowStyle.NormalB);
        Assert.NotEqual(MenuRowStyle.ChromeR, MenuRowStyle.SelectedR);
        Assert.NotEqual(MenuRowStyle.ChromeG, MenuRowStyle.SelectedG);
        Assert.NotEqual(MenuRowStyle.ChromeB, MenuRowStyle.SelectedB);
        Assert.NotEqual(MenuRowStyle.ChromeHoverR, MenuRowStyle.HoverR);
        Assert.NotEqual(MenuRowStyle.ChromeHoverR, MenuRowStyle.SelectedHoverR);
        Assert.NotEqual(MenuRowStyle.ChromeHoverG, MenuRowStyle.SelectedHoverG);
        Assert.True(MenuRowStyle.ChromeFontSize < MenuRowStyle.CatalogFontSize);
        Assert.True(MenuRowStyle.ChromeInset >= 12f);
        Assert.NotEqual(MenuRowStyle.ChromeBandR, MenuRowStyle.NormalR);
        Assert.NotEqual(MenuRowStyle.ChromeBandR, MenuRowStyle.SelectedR);
        Assert.NotEqual(MenuRowStyle.ChromeBorderR, MenuRowStyle.NormalR);
        Assert.NotEqual(MenuRowStyle.ChromeBorderG, MenuRowStyle.NormalG);
        Assert.NotEqual(MenuRowStyle.ChromeBorderB, MenuRowStyle.NormalB);
        Assert.NotEqual(MenuRowStyle.ChromeBorderR, MenuRowStyle.SelectedR);
        Assert.NotEqual(MenuRowStyle.ChromeBorderG, MenuRowStyle.SelectedG);
        Assert.NotEqual(MenuRowStyle.ChromeBorderB, MenuRowStyle.SelectedB);
        Assert.NotEqual(MenuRowStyle.ChromeBorderHoverR, MenuRowStyle.HoverR);
        Assert.NotEqual(MenuRowStyle.ChromeBorderHoverR, MenuRowStyle.SelectedHoverR);
        Assert.NotEqual(MenuRowStyle.ChromeBorderHoverG, MenuRowStyle.SelectedHoverG);
        Assert.NotEqual(MenuRowStyle.ChromeBorderHoverB, MenuRowStyle.SelectedHoverB);
        Assert.True(MenuRowStyle.ChromeBorderA > 0.5f);
        Assert.True(MenuPanelLayout.FooterGap >= 8f);
        Assert.True(MenuPanelLayout.FooterPadTop >= 8f);
        Assert.True(MenuPanelLayout.FooterPadBottom >= 8f);
        Assert.True(MenuPanelLayout.FooterButtonGap > PanelLayout.RowGap - 0.01f);
        Assert.Equal(
            MenuPanelLayout.FooterGap,
            MenuPanelLayout.BottomChromeHeight(MenuPanelLayout.ListBottomChromeRows)
                - MenuPanelLayout.FooterBandHeight(MenuPanelLayout.ListBottomChromeRows),
            3);
        float pane = MenuPanelLayout.ForCanvas(1920f, 1080f, true).Height;
        float view = PanelLayout.ViewportHeightFor(pane);
        float list = MenuPanelLayout.ListViewportHeight(pane);
        float bottom = MenuPanelLayout.BottomChromeHeight(MenuPanelLayout.ListBottomChromeRows);
        float top = MenuPanelLayout.ChromeHeight(MenuPanelLayout.ListTopChromeRows);
        Assert.Equal(view, list + top + bottom, 3);
        Assert.True(list > 0f);
        Assert.True(bottom > MenuPanelLayout.FooterBandHeight(MenuPanelLayout.ListBottomChromeRows));
    }

    [Fact]
    public void List_scroll_is_the_sidebar_even_when_the_content_pane_is_leftmost_and_at_zero()
    {
        // Live bug: leftmost-x picked the detail pane at 0 while the list sat at 0.728.
        const float listPos = 0.728f;
        const float contentPos = 0f;
        Assert.Equal(listPos, WorkspacePaneScroll.ListPosition(
            aSidebar: false, aPosition: contentPos,
            bSidebar: true, bPosition: listPos));
        Assert.Equal(contentPos, WorkspacePaneScroll.ContentPosition(
            aSidebar: false, aPosition: contentPos,
            bSidebar: true, bPosition: listPos));
        Assert.NotEqual(contentPos, WorkspacePaneScroll.ListPosition(false, contentPos, true, listPos));
        Assert.Equal(listPos, WorkspacePaneScroll.ListPosition(true, listPos, false, contentPos));
        Assert.True(WorkspacePaneScroll.ListOverflow(true, true, false, false));
        Assert.True(WorkspacePaneScroll.ListOverflow(false, false, true, true));
        Assert.False(WorkspacePaneScroll.ListOverflow(false, true, true, false));
    }

    [Fact]
    public void Search_field_style_is_not_a_catalogue_row_selected_gold_or_footer_chrome()
    {
        Assert.Equal("Search", MenuRowStyle.SearchPlaceholder);
        Assert.Equal("search-row", MenuRowStyle.SearchRowName);
        Assert.Equal("search-hint-word", MenuRowStyle.SearchHintWordName);
        Assert.Equal("search-hint-glyph", MenuRowStyle.SearchHintGlyphName);

        Assert.NotEqual(MenuRowStyle.SearchR, MenuRowStyle.NormalR);
        Assert.NotEqual(MenuRowStyle.SearchG, MenuRowStyle.NormalG);
        Assert.NotEqual(MenuRowStyle.SearchB, MenuRowStyle.NormalB);
        Assert.NotEqual(MenuRowStyle.SearchR, MenuRowStyle.SelectedR);
        Assert.NotEqual(MenuRowStyle.SearchG, MenuRowStyle.SelectedG);
        Assert.NotEqual(MenuRowStyle.SearchB, MenuRowStyle.SelectedB);
        Assert.NotEqual(MenuRowStyle.SearchR, MenuRowStyle.ChromeR);
        Assert.NotEqual(MenuRowStyle.SearchG, MenuRowStyle.ChromeG);
        Assert.NotEqual(MenuRowStyle.SearchB, MenuRowStyle.ChromeB);
        Assert.NotEqual(MenuRowStyle.SearchR, MenuRowStyle.ChromeBandR);

        Assert.NotEqual(MenuRowStyle.SearchBorderR, MenuRowStyle.SelectedR);
        Assert.NotEqual(MenuRowStyle.SearchBorderG, MenuRowStyle.SelectedG);
        Assert.NotEqual(MenuRowStyle.SearchBorderB, MenuRowStyle.SelectedB);
        Assert.NotEqual(MenuRowStyle.SearchBorderR, MenuRowStyle.ChromeBorderR);
        Assert.NotEqual(MenuRowStyle.SearchBorderG, MenuRowStyle.ChromeBorderG);
        Assert.NotEqual(MenuRowStyle.SearchBorderB, MenuRowStyle.ChromeBorderB);
        Assert.True(MenuRowStyle.SearchBorderA > 0.5f);
        Assert.Equal(MenuRowStyle.ChromeLabelR, MenuRowStyle.SearchPlaceholderR);
        Assert.Equal(MenuRowStyle.ChromeLabelG, MenuRowStyle.SearchPlaceholderG);
        Assert.Equal(MenuRowStyle.ChromeLabelB, MenuRowStyle.SearchPlaceholderB);
        Assert.Equal(MenuRowStyle.ChromeLabelA, MenuRowStyle.SearchPlaceholderA);
        Assert.True(MenuRowStyle.SearchPlaceholderA >= MenuRowStyle.SearchHintMinAlpha);
        Assert.True(MenuRowStyle.SearchA > 0.9f);

        var pane = MenuPanelLayout.ForCanvas(1920f, 1080f, sidebar: true);
        var search = MenuPanelLayout.SearchFieldColumns(pane.RowWidth);
        var twoColumn = MenuPanelLayout.Columns(pane.RowWidth, hasControl: true);
        var catalog = MenuPanelLayout.Columns(pane.RowWidth, hasControl: false);
        Assert.Equal(pane.RowWidth, search.ControlWidth);
        Assert.Equal(0f, search.LabelWidth);
        Assert.Equal(0f, search.ControlX);
        Assert.Equal(catalog.LabelWidth, search.ControlWidth);
        Assert.True(search.ControlWidth > twoColumn.ControlWidth);
        Assert.True(search.HasControl);

        var viaHelper = MenuPanelLayout.ColumnsForMenuRow(pane.RowWidth, hasControl: true, searchField: true);
        Assert.Equal(search.ControlWidth, viaHelper.ControlWidth);
        Assert.Equal(search.ControlX, viaHelper.ControlX);
        var editor = MenuPanelLayout.ColumnsForMenuRow(pane.RowWidth, hasControl: true, searchField: false);
        Assert.Equal(twoColumn.ControlWidth, editor.ControlWidth);
        Assert.Equal(twoColumn.ControlX, editor.ControlX);
    }

    [Fact]
    public void Search_hint_word_and_glyph_are_the_strings_that_would_draw()
    {
        Assert.Equal("Search", MenuRowStyle.SearchPlaceholder);
        Assert.Equal("\u2315", MenuRowStyle.SearchGlyphPreferred);
        Assert.Equal("\u26B2", MenuRowStyle.SearchGlyphAlt);
        Assert.True(MenuRowStyle.SearchHintVisible(""));
        Assert.True(MenuRowStyle.SearchHintVisible(null));
        Assert.False(MenuRowStyle.SearchHintVisible("a"));

        Assert.Equal(MenuRowStyle.SearchGlyphPreferred, MenuRowStyle.PickSearchGlyph(s => s == MenuRowStyle.SearchGlyphPreferred));
        Assert.Equal(MenuRowStyle.SearchGlyphAlt, MenuRowStyle.PickSearchGlyph(s => s == MenuRowStyle.SearchGlyphAlt));
        Assert.Equal("", MenuRowStyle.PickSearchGlyph(_ => false));
        Assert.Equal("", MenuRowStyle.PickSearchGlyph(null));
    }

    [Fact]
    public void Search_hint_alpha_and_contrast_against_fill_are_above_threshold_and_not_behind_fill()
    {
        Assert.Equal(0.85f, MenuRowStyle.SearchHintMinAlpha);
        Assert.Equal(3f, MenuRowStyle.SearchHintMinContrastVsFill);
        Assert.True(MenuRowStyle.SearchPlaceholderA >= MenuRowStyle.SearchHintMinAlpha);

        float contrast = MenuRowStyle.ContrastRatio(
            MenuRowStyle.SearchPlaceholderR, MenuRowStyle.SearchPlaceholderG, MenuRowStyle.SearchPlaceholderB,
            MenuRowStyle.SearchR, MenuRowStyle.SearchG, MenuRowStyle.SearchB);
        Assert.True(
            contrast >= MenuRowStyle.SearchHintMinContrastVsFill,
            "hint vs fill contrast is " + contrast.ToString("0.000") + ", need >= " + MenuRowStyle.SearchHintMinContrastVsFill);

        float vsWhite = MenuRowStyle.ContrastRatio(
            MenuRowStyle.SearchPlaceholderR, MenuRowStyle.SearchPlaceholderG, MenuRowStyle.SearchPlaceholderB,
            1f, 1f, 1f);
        Assert.True(vsWhite > 1.01f, "hint must be dimmer than typed white");

        // Fill Image is on the field GO (sibling -1 = parent graphic, behind every child).
        Assert.True(MenuRowStyle.HintIsNotBehindFill(hintSiblingIndex: 1, fillSiblingIndex: -1));
        Assert.True(MenuRowStyle.HintIsNotBehindFill(hintSiblingIndex: 1, fillSiblingIndex: 0));
        Assert.False(MenuRowStyle.HintIsNotBehindFill(hintSiblingIndex: 0, fillSiblingIndex: 1));
        Assert.False(MenuRowStyle.HintIsNotBehindFill(hintSiblingIndex: -1, fillSiblingIndex: -1));

        Assert.True(MenuRowStyle.HintGraphicIsDrawable(
            assigned: true, active: true, graphicEnabled: true,
            alpha: MenuRowStyle.SearchPlaceholderA, rectW: 282f, rectH: 20f,
            hintSiblingIndex: 1, fillSiblingIndex: -1, meshCount: 6));
        Assert.False(MenuRowStyle.HintGraphicIsDrawable(
            assigned: true, active: true, graphicEnabled: true,
            alpha: 0.1f, rectW: 282f, rectH: 20f,
            hintSiblingIndex: 1, fillSiblingIndex: -1, meshCount: 6));
        Assert.False(MenuRowStyle.HintGraphicIsDrawable(
            assigned: true, active: true, graphicEnabled: true,
            alpha: MenuRowStyle.SearchPlaceholderA, rectW: 0f, rectH: 0f,
            hintSiblingIndex: 1, fillSiblingIndex: -1, meshCount: 6));
        Assert.False(MenuRowStyle.HintGraphicIsDrawable(
            assigned: true, active: true, graphicEnabled: true,
            alpha: MenuRowStyle.SearchPlaceholderA, rectW: 282f, rectH: 20f,
            hintSiblingIndex: 0, fillSiblingIndex: 1, meshCount: 6));
        Assert.False(MenuRowStyle.HintGraphicIsDrawable(
            assigned: true, active: true, graphicEnabled: true,
            alpha: MenuRowStyle.SearchPlaceholderA, rectW: 282f, rectH: 20f,
            hintSiblingIndex: 1, fillSiblingIndex: -1, meshCount: 0));
        Assert.False(MenuRowStyle.HintGraphicIsDrawable(
            assigned: false, active: true, graphicEnabled: true,
            alpha: MenuRowStyle.SearchPlaceholderA, rectW: 282f, rectH: 20f,
            hintSiblingIndex: 1, fillSiblingIndex: -1, meshCount: 6));
    }

    [Theory]
    [InlineData(3840f, 1080f)]
    [InlineData(1600f, 1024f)]
    public void Strip_sits_in_the_frame_top_inset_and_spans_the_panes_at_both_aspects(float width, float height)
    {
        ModsWorkspaceFrame.ResetForTests();
        try
        {
            AssertStripOnFrame(ModsWorkspaceFrame.Fallback(width, height));

            var native = new NativeContentRect(true, Math.Min(width - 80f, 1700f), Math.Min(height - 160f, 900f), 0f, -40f, "");
            AssertStripOnFrame(ModsWorkspaceFrame.Resolve(width, height, "top-tab", native));

            // Path, not site: top-tab without a container still uses the title inset.
            AssertStripOnFrame(ModsWorkspaceFrame.Resolve(width, height, "top-tab", NativeContentRect.Missing("no-viewparent")));

            ModsWorkspaceFrame.Resolve(1920f, 1080f, "top-tab", native);
            AssertStripOnFrame(ModsWorkspaceFrame.Resolve(width, height, "main-menu", NativeContentRect.Missing("title-screen")));
        }
        finally { ModsWorkspaceFrame.ResetForTests(); }
        // Revert: placing the strip via PanelFlow, or sizing it as a sidebar/detail pane, fails this.
        // Revert: keying ForStrip off the site string puts the strip inside a fallback frame at top-tab.
    }

    [Fact]
    public void Title_fallback_strip_stays_in_the_top_inset_at_3840x1080()
    {
        var frame = ModsWorkspaceFrame.Fallback(3840f, 1080f);
        var strip = MenuPanelLayout.ForStrip(frame);
        Assert.False(MenuPanelLayout.StripConsumesFrameTop(frame));
        Assert.True(MenuPanelLayout.StripLiesInTopInset(frame, strip));
        // TABS2 (user screenshot 2026-09-29): the strip now stands on the pane top instead of floating
        // Gap (16) above it; live gen9 had it at client y=154.
        Assert.Equal(170f, ClientTop(strip.Y + strip.Height * 0.5f, 1080f), 3);
        // Revert: putting the title strip inside the frame drops it onto the panes.
    }

    [Fact]
    public void Native_container_strip_lies_inside_the_live_3840x1080_viewparent()
    {
        // Live gen9: ViewParent client (1088, 72, 1664, 870) at 3840×1080.
        const float screenW = 3840f, screenH = 1080f;
        const float clientX = 1088f, clientY = 72f, clientW = 1664f, clientH = 870f;
        float unityBottom = screenH - clientY - clientH;
        var native = new NativeContentRect(
            true, clientW, clientH,
            clientX + clientW * 0.5f - screenW * 0.5f,
            unityBottom + clientH * 0.5f - screenH * 0.5f,
            "");
        ModsWorkspaceFrame.ResetForTests();
        // As read: this fixture is the collapsed native layout; round 8 pins it down to the vanilla window.
        WorkspaceHostLayout.PinInGameFrame = false;
        try
        {
            var frame = ModsWorkspaceFrame.Resolve(screenW, screenH, "top-tab", native);
            Assert.Equal(ModsWorkspaceFrame.PathNative, frame.Path);
            var strip = MenuPanelLayout.ForStrip(frame);
            var pane = MenuPanelLayout.ForFrame(frame, true);
            Assert.True(MenuPanelLayout.StripLiesInsideFrame(frame, strip));
            Assert.False(MenuPanelLayout.StripLiesInTopInset(frame, strip));
            float stripClientY = ClientTop(strip.Y + strip.Height * 0.5f, screenH);
            float paneClientY = ClientTop(pane.Y + pane.Height * 0.5f, screenH);
            Assert.Equal(clientY - frame.TabLift, stripClientY, 3);
            Assert.True(stripClientY >= 48f);
            Assert.Equal(clientY + UnityUiRuntimeMetrics.RowHeight - frame.TabLift, paneClientY, 3);
            Assert.True(paneClientY >= stripClientY + strip.Height - 0.5f);
            var left = MenuPanelLayout.StripButton(strip, 0, 3);
            Assert.Equal(clientX, left.X - left.Width * 0.5f + screenW * 0.5f, 3);
        }
        finally { ModsWorkspaceFrame.ResetForTests(); }
        // Revert: ForStrip always using the TopInset band puts strip client y at 16, over the outer tab bar.
    }

    [Fact]
    public void Native_footer_strip_stays_at_viewparent_top_while_panes_use_the_overhang()
    {
        const float screenW = 3840f, screenH = 1080f;
        const float clientX = 1088f, clientY = 72f, clientW = 1664f, clientH = 870f;
        const float overhang = 58f;
        var container = NativeContentRect.FromClient(clientX, clientY, clientW, clientH, screenW, screenH);
        var footer = NativeContentRect.FromClient(clientX, clientY + clientH, clientW, overhang, screenW, screenH);
        var extent = NativeFrameExtent.Grow(
            container,
            new[]
            {
                new NativeChromeGraphic(
                    "FullscreenMenu/EscapeMenuView/ConnectionInfoRect",
                    ModsFullscreenMenuProbe.TypeImage,
                    NativeFrameExtent.ZoneHosted,
                    footer),
            },
            out _);
        ModsWorkspaceFrame.ResetForTests();
        // As read: this fixture is the collapsed native layout; round 8 pins it down to the vanilla window.
        WorkspaceHostLayout.PinInGameFrame = false;
        try
        {
            var frame = ModsWorkspaceFrame.Resolve(screenW, screenH, "top-tab", container, extent);
            var strip = MenuPanelLayout.ForStrip(frame);
            var pane = MenuPanelLayout.ForFrame(frame, true);
            Assert.True(MenuPanelLayout.StripLiesInsideFrame(frame, strip));
            float stripClientY = ClientTop(strip.Y + strip.Height * 0.5f, screenH);
            float paneClientY = ClientTop(pane.Y + pane.Height * 0.5f, screenH);
            float paneBottom = ClientTop(pane.Y - pane.Height * 0.5f, screenH);
            Assert.Equal(clientY - frame.TabLift, stripClientY, 3);
            Assert.Equal(clientY + UnityUiRuntimeMetrics.RowHeight - frame.TabLift, paneClientY, 3);
            Assert.Equal(clientY + clientH + overhang, paneBottom, 3);
            Assert.Equal(clientH + overhang - UnityUiRuntimeMetrics.RowHeight + frame.TabLift, pane.Height, 3);
        }
        finally { ModsWorkspaceFrame.ResetForTests(); }
        // Revert: leaving pane height on ViewParent wastes the footer band under the art.
        // LAY4 BackgroundImage overhang test replaced by this footer-union rule.
    }

    [Fact]
    public void Native_container_strip_lies_inside_the_frame_at_1600x1024()
    {
        // Same synthetic native rect LAY2 used at this aspect (1520×864 at canvas (0, −40)).
        const float screenW = 1600f, screenH = 1024f;
        ModsWorkspaceFrame.ResetForTests();
        try
        {
            var native = new NativeContentRect(true, 1520f, 864f, 0f, -40f, "");
            var frame = ModsWorkspaceFrame.Resolve(screenW, screenH, "top-tab", native);
            Assert.Equal(ModsWorkspaceFrame.PathNative, frame.Path);
            var strip = MenuPanelLayout.ForStrip(frame);
            var pane = MenuPanelLayout.ForFrame(frame, true);
            // HOSTS1 round 8: this frame leaves the band above it (top 120 of 1080, lift 40), so the strip
            // stands there, bottom on the window top, and the panes take the whole frame.
            Assert.True(MenuPanelLayout.StripInBand(frame));
            float frameClientY = ClientTop(frame.Y + frame.Height * 0.5f, screenH);
            float stripClientY = ClientTop(strip.Y + strip.Height * 0.5f, screenH);
            float paneClientY = ClientTop(pane.Y + pane.Height * 0.5f, screenH);
            Assert.Equal(frameClientY - strip.Height, stripClientY, 3);
            Assert.Equal(frameClientY, paneClientY, 3);
        }
        finally { ModsWorkspaceFrame.ResetForTests(); }
        // Revert: leaving panes at the full native height overlaps the in-frame strip.
    }

    [Fact]
    public void Strip_buttons_are_one_horizontal_row_inside_the_strip()
    {
        var frame = ModsWorkspaceFrame.Fallback(3840f, 1080f);
        var strip = MenuPanelLayout.ForStrip(frame);
        var left = MenuPanelLayout.StripButton(strip, 0, 3);
        var mid = MenuPanelLayout.StripButton(strip, 1, 3);
        var right = MenuPanelLayout.StripButton(strip, 2, 3);
        Assert.Equal(strip.Y, left.Y);
        Assert.Equal(strip.Y, mid.Y);
        Assert.Equal(strip.Y, right.Y);
        Assert.Equal(strip.Height, left.Height);
        Assert.True(left.X + left.Width / 2f < mid.X - mid.Width / 2f);
        Assert.True(mid.X + mid.Width / 2f < right.X - right.Width / 2f);
        Assert.InRange(left.X - left.Width / 2f, strip.X - strip.Width / 2f - 0.5f, strip.X);
        Assert.Equal(left.Width, mid.Width);
        Assert.Equal(mid.Width, right.Width);
        Assert.True(right.X + right.Width / 2f < strip.X + strip.Width / 2f);
        Assert.Equal(-1, ModsTabStrip.HitIndex(strip, strip.X + strip.Width / 2f - 1f, strip.Y));
        // Revert: stacking the three buttons vertically (PlaceRow without the strip branch) fails this.
    }

    [Fact]
    public void Pinned_header_places_the_viewport_below_the_header_without_overlap()
    {
        var frame = ModsWorkspaceFrame.Fallback(3840f, 1080f);
        var pane = MenuPanelLayout.ForFrame(frame, sidebar: false);
        float band = MenuPanelLayout.PinnedHeaderHeight();
        Assert.Equal(MenuPanelLayout.ChromeHeight(MenuPanelLayout.PinnedHeaderRows), band);
        MenuPanelLayout.SplitPinnedHeader(pane, band, out var header, out var viewport);
        Assert.True(MenuPanelLayout.HeaderLiesAboveViewport(pane, header, viewport));
        float headerBottom = header.Y - header.Height * 0.5f;
        float viewTop = viewport.Y + viewport.Height * 0.5f;
        Assert.Equal(headerBottom, viewTop, 3);
        Assert.True(header.Y > viewport.Y);
        Assert.Equal(pane.Width, header.Width, 3);
        Assert.Equal(pane.Width, viewport.Width, 3);
        MenuPanelLayout.HeaderColumns(pane.RowWidth, out float labelW, out _, out float actionW, out _);
        Assert.True(labelW > 0f);
        Assert.True(actionW > 0f);
        Assert.True(labelW + actionW < pane.RowWidth);
        string runtime = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "UnityUiRuntime.cs");
        SourcePin.AssertUncommented(runtime, "private void PlaceHeaderRows()");
        SourcePin.AssertUncommented(SourcePin.MethodBody(runtime, "private void PlaceRow("), "PlaceHeaderRows()");
        // Revert: putting the header inside the scrolling content, or overlapping the viewport, fails this.
    }

    private static void AssertStripOnFrame(in ModsWorkspaceFrame frame)
    {
        var strip = MenuPanelLayout.ForStrip(frame);
        var sidebar = MenuPanelLayout.ForFrame(frame, true);
        var detail = MenuPanelLayout.ForFrame(frame, false);
        // HOSTS1 round 8: in the band above the vanilla window the strip starts 24 in, like Spellbook's tabs.
        float inset = MenuPanelLayout.StripInBand(frame) ? WorkspaceHostLayout.StripLeftInset : 0f;
        Assert.Equal(frame.Width - inset, strip.Width, 3);
        Assert.Equal(frame.X + inset * .5f, strip.X, 3);
        Assert.Equal(MenuPanelLayout.StripInBand(frame) ? WorkspaceHostLayout.TabHeight : UnityUiRuntimeMetrics.RowHeight, strip.Height);
        float paneTop = sidebar.Y + sidebar.Height / 2f;
        float stripBottom = strip.Y - strip.Height / 2f;
        float stripTop = strip.Y + strip.Height / 2f;
        if (MenuPanelLayout.StripConsumesFrameTop(frame))
        {
            Assert.True(MenuPanelLayout.StripLiesInsideFrame(frame, strip));
            float frameTop = frame.Y + frame.Height / 2f;
            Assert.True(stripTop <= frameTop + frame.TabLift + 0.5f);
            Assert.True(paneTop <= stripBottom + 0.5f);
            Assert.Equal(stripBottom, paneTop, 3);
            Assert.Equal(sidebar.Y, detail.Y, 3);
            Assert.Equal(sidebar.Height, detail.Height, 3);
        }
        else
        {
            Assert.True(MenuPanelLayout.StripLiesInTopInset(frame, strip));
            Assert.True(stripBottom >= paneTop - 0.5f);
            Assert.True(stripTop <= paneTop + MenuPanelLayout.TopInset + 0.5f);
        }

        float panesLeft = Math.Min(sidebar.X - sidebar.Width / 2f, detail.X - detail.Width / 2f);
        float panesRight = Math.Max(sidebar.X + sidebar.Width / 2f, detail.X + detail.Width / 2f);
        Assert.InRange(strip.X - strip.Width / 2f, panesLeft + inset - 0.5f, panesLeft + inset + 0.5f);
        Assert.InRange(strip.X + strip.Width / 2f, panesRight - 0.5f, panesRight + 0.5f);
        Assert.True(strip.Height < PanelLayout.HeaderHeight);
    }

    /// <summary>
    /// TABS2 (user screenshot 2026-09-29): at the title the strip floated ~14 px above the frame line.
    /// In every host the tab bottoms must meet the artwork pane top that ModsWorkspaceChrome.PlaceHolder
    /// draws the trim from (the trim itself sits TrimLift above it, over the tab bottoms, as in game).
    /// </summary>
    [Theory]
    [InlineData(3840f, 1080f)]
    [InlineData(1600f, 1024f)]
    public void Tab_bottoms_meet_the_frame_line_at_the_title_and_in_game(float screenW, float screenH)
    {
        ModsWorkspaceFrame.ResetForTests();
        try
        {
            // Title: no native container.
            var title = ModsWorkspaceFrame.Resolve(screenW, screenH, "main-menu", NativeContentRect.Missing("title-screen-no-container"));
            AssertTabsMeetFrameLine(title);

            // In game: a ViewParent-shaped native rect (72 px below the top, 870/1080 of the height, centered).
            float clientH = screenH * 870f / 1080f, clientW = Math.Min(screenW, 1664f), clientY = 72f;
            float unityBottom = screenH - clientY - clientH;
            var native = new NativeContentRect(true, clientW, clientH, 0f, unityBottom + clientH * 0.5f - screenH * 0.5f, "");
            var inGame = ModsWorkspaceFrame.Resolve(screenW, screenH, "top-tab", native);
            Assert.Equal(ModsWorkspaceFrame.PathNative, inGame.Path);
            AssertTabsMeetFrameLine(inGame);
        }
        finally { ModsWorkspaceFrame.ResetForTests(); }
    }

    private static void AssertTabsMeetFrameLine(in ModsWorkspaceFrame frame)
    {
        var strip = MenuPanelLayout.ForStrip(frame);
        var pane = MenuPanelLayout.ForFrame(frame, false, MenuPaneMode.FullWidth);
        float paneTop = pane.Y + pane.Height * 0.5f;
        float trimTop = paneTop + MenuPanelLayout.TrimLift;
        for (int i = 0; i < ModsTabStrip.Order.Length; i++)
        {
            var tab = MenuPanelLayout.StripButton(strip, i, ModsTabStrip.Order.Length);
            float tabBottom = tab.Y - tab.Height * 0.5f;
            Assert.InRange(tabBottom - paneTop, -1f, 1f);
            Assert.InRange(trimTop - tabBottom, MenuPanelLayout.TrimLift - 1f, MenuPanelLayout.TrimLift + 1f);
        }
    }

    /// <summary>Client-pixel Y of a canvas-space top edge (origin top-left, canvas origin at screen center).</summary>
    private static float ClientTop(float canvasTop, float screenHeight) => screenHeight * 0.5f - canvasTop;
}
