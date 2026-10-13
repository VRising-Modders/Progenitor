using System;
using System.Collections.Generic;
using Progenitor.Networking;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

public sealed class ModsWorkspaceFrameTests : IDisposable
{
    public ModsWorkspaceFrameTests() => ModsWorkspaceFrame.ResetForTests();

    /// <summary>HOSTS1 round 8: the panes take the whole frame when the strip stands in the band above it.</summary>
    private static float PaneHeightFor(in ModsWorkspaceFrame frame) =>
        MenuPanelLayout.StripConsumesFrameTop(frame)
            ? frame.Height - UnityUiRuntimeMetrics.RowHeight + frame.TabLift
            : frame.Height;

    /// <summary>HOSTS1: an in-game frame is recorded only once it has settled, shown, on a measured canvas.</summary>
    private static void Settle(float canvasW, float canvasH, in ModsWorkspaceFrame applied)
    {
        for (int i = 0; i < NativeFrameSettle.Frames; i++)
            WorkspaceHostLayout.ObserveInGame(canvasW, canvasH, applied, shownInGame: true, canvasMeasured: true);
    }
    public void Dispose() => ModsWorkspaceFrame.ResetForTests();

    [Fact]
    public void Fallback_ForCanvas_keeps_the_previous_1920x1080_cards()
    {
        var left = MenuPanelLayout.ForCanvas(1920f, 1080f, true);
        var right = MenuPanelLayout.ForCanvas(1920f, 1080f, false);
        Assert.InRange(left.Width, 300f, 360f);
        Assert.InRange(right.Width, 700f, 900f);
        Assert.Equal(800f, left.Height);
        Assert.Equal(left.Height, right.Height);
        Assert.Equal(ModsWorkspaceFrame.PathFallback, ModsWorkspaceFrame.Fallback(1920f, 1080f).Path);
    }

    [Fact]
    public void Footer_chrome_union_grows_the_frame_and_panes_to_client_bottom_1000()
    {
        // Live gen25 3840×1080: ViewParent (1088, 72, 1664, 870) bottom 942;
        // footer strip (1088, 942, 1664, 58) paints to ≈ y 1000. LAY4's
        // BackgroundImage equaled ViewParent; the extent rule replaces it.
        const float screenW = 3840f, screenH = 1080f;
        const float clientX = 1088f, clientY = 72f, clientW = 1664f, clientH = 870f;
        var container = NativeContentRect.FromClient(clientX, clientY, clientW, clientH, screenW, screenH);
        var footer = NativeContentRect.FromClient(clientX, 942f, clientW, 58f, screenW, screenH);
        var graphics = new[]
        {
            new NativeChromeGraphic(
                "FullscreenMenu/EscapeMenuView/ConnectionInfoRect",
                ModsFullscreenMenuProbe.TypeImage,
                NativeFrameExtent.ZoneHosted,
                footer),
        };

        var extent = NativeFrameExtent.Grow(container, graphics, out string why);
        Assert.Contains(NativeFrameExtent.ViewParentName, why, StringComparison.Ordinal);
        Assert.Contains("ConnectionInfoRect", why, StringComparison.Ordinal);
        extent.ToClient(screenW, screenH, out float gx, out float gy, out float gw, out float gh);
        Assert.Equal(clientX, gx, 3);
        Assert.Equal(clientY, gy, 3);
        Assert.Equal(clientW, gw, 3);
        Assert.Equal(928f, gh, 3);
        Assert.Equal(1000f, gy + gh, 3);

        // As read: gen25 is the collapsed native layout; round 8 pins it down to the vanilla window.
        WorkspaceHostLayout.PinInGameFrame = false;
        var grown = ModsWorkspaceFrame.Resolve(screenW, screenH, "top-tab", container, extent);
        Assert.Equal(ModsWorkspaceFrame.PathNative, grown.Path);
        Assert.True(ModsWorkspaceFrame.MatchesNative(grown, extent));
        Assert.False(ModsWorkspaceFrame.MatchesNative(grown, container));
        Assert.Equal(928f, grown.Height, 3);
        Assert.Equal(why, ModsWorkspaceFrame.LastFrameExtentWhy);
        Assert.Equal(why, ModsMenu.WorkspaceFrameExtentWhy());
        var sidebar = MenuPanelLayout.ForFrame(grown, true);
        var detail = MenuPanelLayout.ForFrame(grown, false);
        Assert.Equal(grown.Height - UnityUiRuntimeMetrics.RowHeight + grown.TabLift, sidebar.Height, 3);
        Assert.Equal(sidebar.Height, detail.Height, 3);
        Assert.True(sidebar.Height > clientH - UnityUiRuntimeMetrics.RowHeight + grown.TabLift);
        ModsWorkspaceFrame.CoverCrop(grown.Width, grown.Height,
            ModsWorkspaceFrame.ImageWidth, ModsWorkspaceFrame.ImageHeight, out float dw, out float dh);
        Assert.True(dw >= grown.Width - 0.01f);
        Assert.True(dh >= grown.Height - 0.01f);
        float frameLeft = grown.X - grown.Width * 0.5f;
        float frameBottom = grown.Y - grown.Height * 0.5f;
        ModsWorkspaceFrame.ClippedVisibleRect(
            grown.X - dw * 0.5f, grown.Y - dh * 0.5f, dw, dh,
            frameLeft, frameBottom, grown.Width, grown.Height,
            out float visX, out float visY, out float visW, out float visH);
        Assert.True(ModsWorkspaceFrame.VisibleRectCoversFrame(
            visX, visY, visW, visH, frameLeft, frameBottom, grown.Width, grown.Height));

        var wide = NativeContentRect.FromClient(0f, 942f, 3840f, 58f, screenW, screenH);
        Assert.False(NativeFrameExtent.QualifiesAsFooter(
            container, wide, NativeFrameExtent.ZoneElsewhere));
        var unrelated = NativeContentRect.FromClient(40f, 942f, 200f, 58f, screenW, screenH);
        Assert.False(NativeFrameExtent.QualifiesAsFooter(
            container, unrelated, NativeFrameExtent.ZoneElsewhere));
        Assert.False(NativeFrameExtent.QualifiesAsFooter(
            container, footer, NativeFrameExtent.ZoneOuterTab));
        var noFooter = NativeFrameExtent.Grow(container, Array.Empty<NativeChromeGraphic>(), out string vpWhy);
        Assert.Equal(NativeFrameExtent.WhyViewParent, vpWhy);
        Assert.Equal(container.Height, noFooter.Height, 3);
        Assert.True(ModsWorkspaceFrame.MatchesNative(
            ModsWorkspaceFrame.Resolve(screenW, screenH, "top-tab", container, noFooter), container));

        var missing = NativeContentRect.Missing(ModsWorkspaceFrame.BackgroundWhyNoHosted);
        ModsWorkspaceFrame.ResetForTests();
        WorkspaceHostLayout.PinInGameFrame = false;
        var fallback = ModsWorkspaceFrame.Resolve(screenW, screenH, "top-tab", container, missing);
        Assert.True(ModsWorkspaceFrame.MatchesNative(fallback, container));
        Assert.Equal(container.Height, fallback.Height, 3);

        var overhanging = NativeBackgroundOverhang.From(container, extent);
        Assert.True(overhanging.Measured);
        Assert.Equal(58f, overhanging.Bottom, 3);
        var derived = overhanging.Apply(container);
        Assert.Equal(extent.Height, derived.Height, 3);
        ModsWorkspaceFrame.ResetForTests();
        WorkspaceHostLayout.PinInGameFrame = false;
        var cached = ModsWorkspaceFrame.Resolve(
            screenW, screenH, "top-tab", container,
            derived.WithWhy(ModsWorkspaceFrame.BackgroundWhyCached));
        Assert.True(ModsWorkspaceFrame.MatchesNative(cached, extent));
        Assert.Equal(ModsWorkspaceFrame.BackgroundWhyCached, ModsWorkspaceFrame.LastBackgroundWhy);

        ModsWorkspaceFrame.ResetForTests();
        var inGame = ModsWorkspaceFrame.Resolve(screenW, screenH, "top-tab", container, extent);
        Settle(screenW, screenH, inGame);
        var title = ModsWorkspaceFrame.Resolve(screenW, screenH, "main-menu", NativeContentRect.Missing("title-screen"));
        // HOSTS1: this gen25 frame (ViewParent top 72, no stale inner-tab row) is the canonical frame, so
        // the title reproduces it.
        Assert.Equal(ModsWorkspaceFrame.PathTitleHost, title.Path);
        Assert.Equal(WorkspaceHostLayout.SourceRecorded, ModsWorkspaceFrame.LastTitleSource);
        Assert.Equal(inGame.Width, title.Width, 3);
        Assert.Equal(inGame.Height, title.Height, 3);
        Assert.Equal(inGame.Y, title.Y, 3);
        // Revert: sizing the in-game frame to ViewParent / BackgroundImage
        // (LAY4) leaves the black band under the workspace. Removing the
        // footer union, the width cap, or the outer-tab exclusion restores it.
    }

    [Fact]
    public void Footer_chrome_overhang_is_applied_at_1600x1024()
    {
        const float screenW = 1600f, screenH = 1024f;
        var container = new NativeContentRect(true, 1520f, 864f, 0f, -40f, "");
        const float overhang = 58f;
        var footer = new NativeContentRect(
            true, 1520f, overhang, 0f, container.Bottom - overhang * 0.5f, "");
        var extent = NativeFrameExtent.Grow(
            container,
            new[]
            {
                new NativeChromeGraphic(
                    "FullscreenMenu/Footer",
                    ModsFullscreenMenuProbe.TypeImage,
                    NativeFrameExtent.ZoneElsewhere,
                    footer),
            },
            out _);
        var frame = ModsWorkspaceFrame.Resolve(screenW, screenH, "top-tab", container, extent);
        Assert.True(ModsWorkspaceFrame.MatchesNative(frame, extent));
        Assert.Equal(container.Height + overhang, frame.Height, 3);
        var pane = MenuPanelLayout.ForFrame(frame, true);
        Assert.Equal(PaneHeightFor(frame), pane.Height, 3);
        var strip = MenuPanelLayout.ForStrip(frame);
        Assert.True(MenuPanelLayout.StripConsumesFrameTop(frame)
            ? MenuPanelLayout.StripLiesInsideFrame(frame, strip)
            : MenuPanelLayout.StripLiesInTopInset(frame, strip));
        // Revert: dropping the extent argument at this aspect clips the art to ViewParent.
        // LAY4 BackgroundImage overhang test replaced by this footer-union rule.
    }

    [Fact]
    public void Native_container_frame_matches_the_resolved_rect_within_tolerance()
    {
        var native = new NativeContentRect(true, 1600f, 860f, 12f, -40f, "");
        var frame = ModsWorkspaceFrame.Resolve(1920f, 1080f, "top-tab", native);
        Assert.Equal(ModsWorkspaceFrame.PathNative, frame.Path);
        Assert.True(ModsWorkspaceFrame.MatchesNative(frame, native));
        Assert.InRange(Math.Abs(frame.Width - native.Width), 0f, ModsWorkspaceFrame.MatchTolerance);
        Assert.InRange(Math.Abs(frame.Height - native.Height), 0f, ModsWorkspaceFrame.MatchTolerance);
        var sidebar = MenuPanelLayout.ForFrame(frame, true);
        var detail = MenuPanelLayout.ForFrame(frame, false);
        Assert.Equal(PaneHeightFor(frame), sidebar.Height, 3);
        Assert.Equal(sidebar.Height, detail.Height, 3);
        Assert.True(sidebar.Width <= MenuPanelLayout.SidebarMaxWidth + 0.01f);
        Assert.True(detail.Width > sidebar.Width);
        Assert.True(sidebar.X + sidebar.Width / 2f < detail.X - detail.Width / 2f);
    }

    [Theory]
    [InlineData(1600f, 1024f, 1480f, 820f)]
    [InlineData(1920f, 1080f, 1700f, 900f)]
    public void Windowed_and_1080_native_rects_are_used_as_measured(float canvasW, float canvasH, float nativeW, float nativeH)
    {
        var native = new NativeContentRect(true, nativeW, nativeH, 0f, -50f, "");
        var frame = ModsWorkspaceFrame.Resolve(canvasW, canvasH, "top-tab", native);
        Assert.Equal(ModsWorkspaceFrame.PathNative, frame.Path);
        Assert.True(ModsWorkspaceFrame.MatchesNative(frame, native));
    }

    [Fact]
    public void Ultrawide_native_rect_is_aspect_capped_so_panes_do_not_stretch()
    {
        // 3840x1080-like content area, ~3.55:1. Sidebar would become a strip if we used full width.
        var native = new NativeContentRect(true, 3600f, 900f, 0f, -40f, "");
        var frame = ModsWorkspaceFrame.Resolve(3840f, 1080f, "top-tab", native);
        Assert.Equal(ModsWorkspaceFrame.PathNativeCapped, frame.Path);
        Assert.False(ModsWorkspaceFrame.MatchesNative(frame, native));
        Assert.Equal(native.Height, frame.Height);
        Assert.True(frame.Width / frame.Height <= ModsWorkspaceFrame.MaxAspect + 0.001f);
        Assert.Equal(native.Height * ModsWorkspaceFrame.MaxAspect, frame.Width, 3);
        var sidebar = MenuPanelLayout.ForFrame(frame, true);
        Assert.True(sidebar.Width <= MenuPanelLayout.SidebarMaxWidth + 0.01f);
        Assert.Equal(native.Y, frame.Y);
        Assert.Equal(native.X, frame.X);
    }

    [Fact]
    public void Missing_native_container_is_native_pending_at_the_canonical_size_and_names_the_path()
    {
        // HOSTS1 round 4: no floating card before the native read; the canonical frame, never presented.
        var native = NativeContentRect.Missing("no-viewparent");
        var frame = ModsWorkspaceFrame.Resolve(1920f, 1080f, "top-tab", native);
        Assert.Equal(ModsWorkspaceFrame.PathNativePending, frame.Path);
        Assert.Equal(NativeFrameModel.Width, frame.Width);
        Assert.Equal(NativeFrameModel.Height, frame.Height);
        Assert.False(ModsWorkspaceFrame.MayPresent("top-tab", nativeFound: false, frame.Path));
        string log = ModsWorkspaceFrame.FormatLog(frame, native, ModsWorkspaceFrame.FallbackReason(native, "top-tab"));
        Assert.StartsWith(ModsWorkspaceFrame.LogPrefix + ModsWorkspaceFrame.PathNativePending, log);
        Assert.Contains("reason=no-viewparent", log);
        Assert.Contains("native=none", log);
        var zeros = NativeContentRect.FromOverlayLocal(0f, 0f, 0f, 0f);
        Assert.False(zeros.Found);
        Assert.Equal("degenerate", zeros.Why);
        var measured = NativeContentRect.FromOverlayLocal(-800f, -430f, 800f, 430f);
        Assert.True(measured.Found);
        Assert.Equal("", measured.Why);
        Assert.Equal(1600f, measured.Width);
        Assert.Equal(860f, measured.Height);
        Assert.Equal(0f, measured.X);
        Assert.Equal(0f, measured.Y);
    }

    [Fact]
    public void Main_menu_reuses_the_in_game_ratio_and_the_same_sidebar_split()
    {
        ModsWorkspaceFrame.ResetForTests();
        // The live canonical frame (r2 3840x1080 / p1 1600x1024: ViewParent 1664x870, top 72).
        var native = new NativeContentRect(true, 1664f, 916f, 0f, 10f, "");
        var inGame = ModsWorkspaceFrame.Resolve(1920f, 1080f, "top-tab", native);
        Settle(1920f, 1080f, inGame);
        var title = ModsWorkspaceFrame.Resolve(1920f, 1080f, "main-menu", NativeContentRect.Missing("title-screen"));
        Assert.Equal(ModsWorkspaceFrame.PathNative, inGame.Path);
        Assert.Equal(ModsWorkspaceFrame.PathTitleHost, title.Path);
        Assert.True(ModsWorkspaceFrame.SameRatioAndSplit(inGame, title));
        MenuPanelLayout.Split(inGame.Width, out float inSide, out float inDetail, out _);
        MenuPanelLayout.Split(title.Width, out float titleSide, out float titleDetail, out _);
        Assert.InRange(inSide / inGame.Width, 0.15f, 0.35f);
        Assert.Equal(inSide / inGame.Width, titleSide / title.Width, 3);
        Assert.Equal(inDetail / inGame.Width, titleDetail / title.Width, 3);
        Assert.True(title.Width <= 1920f);
        Assert.True(title.Height <= 1080f);
    }

    [Fact]
    public void Main_menu_without_a_recorded_frame_uses_the_in_game_model_not_the_fallback_cards()
    {
        // HOSTS1: a cold title (no in-game frame this process or on disk) drew the 1276x800 floating
        // card; it now draws the vanilla window (round 8: 1664x916 at (0,-46), screen y 127..1043).
        ModsWorkspaceFrame.ResetForTests();
        var title = ModsWorkspaceFrame.Resolve(
            1920f, 1080f, "main-menu", NativeContentRect.Missing("title-screen"));
        Assert.Equal(ModsWorkspaceFrame.PathTitleHost, title.Path);
        Assert.Equal(WorkspaceHostLayout.SourceModel, ModsWorkspaceFrame.LastTitleSource);
        Assert.Equal(1664f, title.Width, 3);
        Assert.Equal(916f, title.Height, 3);
        Assert.Equal(-46f, title.Y, 3);
        Assert.NotEqual(ModsWorkspaceFrame.Fallback(1920f, 1080f).Width, title.Width);
    }

    [Fact]
    public void Ultrawide_main_menu_carries_the_16x9_frame_without_stretching()
    {
        ModsWorkspaceFrame.ResetForTests();
        var native = new NativeContentRect(true, 1664f, 916f, 0f, 10f, "");
        var inGame = ModsWorkspaceFrame.Resolve(1920f, 1080f, "top-tab", native);
        Settle(1920f, 1080f, inGame);
        var title = ModsWorkspaceFrame.Resolve(3840f, 1080f, "main-menu", NativeContentRect.Missing("title-screen"));
        Assert.Equal(ModsWorkspaceFrame.PathTitleHost, title.Path);
        Assert.Equal(WorkspaceHostLayout.SourceCarried, ModsWorkspaceFrame.LastTitleSource);
        Assert.True(title.Width / title.Height <= ModsWorkspaceFrame.MaxAspect + 0.001f);
        Assert.Equal(inGame.Width, title.Width, 3);
        Assert.Equal(inGame.Height, title.Height, 3);
        Assert.Equal(inGame.Y, title.Y, 3);
    }

    [Fact]
    public void Cover_crop_fills_the_frame_without_stretching_the_1672x941_image()
    {
        const float iw = ModsWorkspaceFrame.ImageWidth;
        const float ih = ModsWorkspaceFrame.ImageHeight;
        Assert.Equal(1672f, iw);
        Assert.Equal(941f, ih);
        ModsWorkspaceFrame.CoverCrop(1600f, 800f, iw, ih, out float w1, out float h1);
        Assert.True(w1 >= 1600f - 0.01f);
        Assert.True(h1 >= 800f - 0.01f);
        Assert.Equal(iw / ih, w1 / h1, 3);
        ModsWorkspaceFrame.CoverCrop(1276f, 800f, iw, ih, out float w2, out float h2);
        Assert.True(w2 >= 1276f - 0.01f);
        Assert.Equal(800f, h2, 2);
        Assert.Equal(iw / ih, w2 / h2, 3);
        ModsWorkspaceFrame.CoverCrop(3600f, 900f, iw, ih, out float w3, out float h3);
        Assert.Equal(3600f, w3, 2);
        Assert.True(h3 >= 900f - 0.01f);
        Assert.Equal(iw / ih, w3 / h3, 3);
    }

    [Fact]
    public void Backdrop_drawability_requires_cross_canvas_order_not_sibling_index()
    {
        Assert.True(ModsWorkspaceFrame.BackdropIsDrawable(
            assigned: true, active: true, graphicEnabled: true, alpha: 1f,
            rectW: 1600f, rectH: 860f,
            backdropSorting: ModsCanvasBand.Backdrop, panelsSorting: ModsCanvasBand.Panels, meshCount: 1));
        Assert.False(ModsWorkspaceFrame.BackdropIsDrawable(
            assigned: true, active: true, graphicEnabled: true, alpha: 1f,
            rectW: 0f, rectH: 860f,
            backdropSorting: ModsCanvasBand.Backdrop, panelsSorting: ModsCanvasBand.Panels, meshCount: 1));
        Assert.False(ModsWorkspaceFrame.BackdropIsDrawable(
            assigned: true, active: true, graphicEnabled: true, alpha: 0f,
            rectW: 1600f, rectH: 860f,
            backdropSorting: ModsCanvasBand.Backdrop, panelsSorting: ModsCanvasBand.Panels, meshCount: 1));
        // Live defect: two canvases at OverlaySorting 5000. Sibling index cannot see this.
        Assert.False(ModsWorkspaceFrame.BackdropIsDrawable(
            assigned: true, active: true, graphicEnabled: true, alpha: 1f,
            rectW: 1600f, rectH: 860f,
            backdropSorting: UiOwnership.OverlaySorting, panelsSorting: UiOwnership.OverlaySorting, meshCount: 1));
        Assert.False(ModsWorkspaceFrame.BackdropIsDrawable(
            assigned: false, active: true, graphicEnabled: true, alpha: 1f,
            rectW: 1600f, rectH: 860f,
            backdropSorting: ModsCanvasBand.Backdrop, panelsSorting: ModsCanvasBand.Panels, meshCount: 1));
        Assert.False(ModsWorkspaceFrame.BackdropIsDrawable(
            assigned: true, active: true, graphicEnabled: true, alpha: 1f,
            rectW: 1600f, rectH: 860f,
            backdropSorting: ModsCanvasBand.Backdrop, panelsSorting: ModsCanvasBand.Panels, meshCount: 0));
    }

    [Fact]
    public void Workspace_panes_are_clear_and_one_scrim_supports_content()
    {
        Assert.Equal(0f, ModsWorkspaceFrame.PanelFillAlpha);
        Assert.True(ModsWorkspaceFrame.PanelFillAlpha < 0.92f);
        Assert.InRange(ModsWorkspaceFrame.ScrimA, 0.35f, 0.7f);
        float textOnScrim = MenuRowStyle.ContrastRatio(0.86f, 0.86f, 0.86f, ModsWorkspaceFrame.ScrimR, ModsWorkspaceFrame.ScrimG, ModsWorkspaceFrame.ScrimB);
        Assert.True(textOnScrim > 4f);
        Assert.True(ModsWorkspaceFrame.PanesShareFillAlpha(
            ModsWorkspaceFrame.PanelFillAlpha, ModsWorkspaceFrame.PanelFillAlpha));
        // Live defect: CreatePanel default 0.92 vs menu 0.78 (or any mismatch) must fail.
        Assert.False(ModsWorkspaceFrame.PanesShareFillAlpha(0.92f, ModsWorkspaceFrame.PanelFillAlpha));
        Assert.False(ModsWorkspaceFrame.PanesShareFillAlpha(0.70f, 0.78f));
    }

    [Fact]
    public void Fullscreen_root_is_not_footer_at_1080p()
    {
        var view = NativeContentRect.FromClient(128, 128, 1664, 870, 1920, 1080);
        var root = NativeContentRect.FromClient(0, 0, 1920, 1080, 1920, 1080);
        Assert.False(NativeFrameExtent.QualifiesAsFooter(view, root, NativeFrameExtent.ZoneElsewhere));
        var footer = NativeContentRect.FromClient(128, 998, 1664, 46, 1920, 1080);
        Assert.True(NativeFrameExtent.QualifiesAsFooter(view, footer, NativeFrameExtent.ZoneElsewhere));
    }

    [Fact]
    public void Canvas_band_is_strictly_increasing_and_fails_the_equal_5000_case()
    {
        Assert.True(ModsCanvasBand.LayerOrderIsResolved(
            ModsCanvasBand.Blocker, ModsCanvasBand.Backdrop, ModsCanvasBand.Panels, ModsCanvasBand.Overlay));
        Assert.Equal(UiOwnership.OverlaySorting, ModsCanvasBand.Overlay);
        Assert.Equal(ModsCanvasBand.Blocker, NativeModsEntry.CurtainCanvasSortingOrder);
        // Live defect: curtain 4999, backdrop 5000, panels 5000, consent 5000.
        Assert.False(ModsCanvasBand.LayerOrderIsResolved(
            UiOwnership.OverlaySorting - 1, UiOwnership.OverlaySorting,
            UiOwnership.OverlaySorting, UiOwnership.OverlaySorting));
        Assert.True(ModsCanvasBand.NativeOccupiesBand(UiOwnership.OverlaySorting));
        Assert.True(ModsCanvasBand.NativeOccupiesBand(UiOwnership.OverlaySorting - 1));
        Assert.Equal(100, ModsCanvasBand.NativeMaxBelowOverlay(new[] { 1, 100, 9000, 5000 }));
        Assert.False(ModsCanvasBand.CountTowardNativeMax("overlay-1", new[] { "progenitor-ui-core" }));
        Assert.False(ModsCanvasBand.CountTowardNativeMax("progenitor-ui-core-mods-workspace", null));
        Assert.True(ModsCanvasBand.CountTowardNativeMax("CanvasMain", null));
        var filtered = new List<int>();
        foreach (var c in new (int Order, string Name, string[] Ancestors)[]
        {
            (2, "CanvasMain", Array.Empty<string>()),
            (4999, "overlay-1", new[] { "progenitor-ui-core" }),
            (4998, "progenitor-ui-core-mods-workspace", Array.Empty<string>()),
            (9000, "CanvasUnscaledMainMenu", Array.Empty<string>()),
        })
        {
            if (!ModsCanvasBand.CountTowardNativeMax(c.Name, c.Ancestors)) continue;
            filtered.Add(c.Order);
        }
        Assert.Equal(2, ModsCanvasBand.NativeMaxBelowOverlay(filtered));
        // Live gen4: name-only IsOwned let overlay-1 at Panels 4999 through.
        Assert.Equal(4999, ModsCanvasBand.NativeMaxBelowOverlay(new[] { 2, 4999 }));
    }

    [Fact]
    public void Clipped_visible_rect_covers_the_whole_frame_and_rejects_the_detail_pane_remnant()
    {
        // Live session: pre-mask image 1421×800 at x=1209, frame 1276×800 at x=1282.
        ModsWorkspaceFrame.ClippedVisibleRect(
            1209f, 210f, 1421f, 800f, 1282f, 210f, 1276f, 800f,
            out float visX, out float visY, out float visW, out float visH);
        Assert.Equal(1282f, visX, 0);
        Assert.Equal(1276f, visW, 0);
        Assert.True(ModsWorkspaceFrame.VisibleRectCoversFrame(
            visX, visY, visW, visH, 1282f, 210f, 1276f, 800f));
        // Pixels occupied only the detail pane (~916 wide). That must fail.
        Assert.False(ModsWorkspaceFrame.VisibleRectCoversFrame(
            1642f, 210f, 916f, 796f, 1282f, 210f, 1276f, 800f));
        Assert.NotEqual(1421f, visW);
    }

    [Fact]
    public void Right_weighted_cover_crop_keeps_the_figure_at_ultrawide_and_windowed_aspects()
    {
        const float iw = ModsWorkspaceFrame.ImageWidth;
        const float ih = ModsWorkspaceFrame.ImageHeight;
        var wide = ModsWorkspaceFrame.Fallback(3840f, 1080f);
        ModsWorkspaceFrame.VisibleSourceRect(
            wide.Width, wide.Height, iw, ih,
            ModsWorkspaceFrame.CoverAlignX, ModsWorkspaceFrame.CoverAlignY,
            out float srcX, out float srcY, out float srcW, out float srcH);
        Assert.True(ModsWorkspaceFrame.FigureSurvivesCrop(srcX, srcY, srcW, srcH, iw, ih));

        var windowed = ModsWorkspaceFrame.Fallback(1600f, 1024f);
        ModsWorkspaceFrame.VisibleSourceRect(
            windowed.Width, windowed.Height, iw, ih,
            ModsWorkspaceFrame.CoverAlignX, ModsWorkspaceFrame.CoverAlignY,
            out srcX, out srcY, out srcW, out srcH);
        Assert.True(ModsWorkspaceFrame.FigureSurvivesCrop(srcX, srcY, srcW, srcH, iw, ih));

        var native = new NativeContentRect(true, 1480f, 820f, 0f, -50f, "");
        var inGame = ModsWorkspaceFrame.Resolve(1600f, 1024f, "top-tab", native);
        ModsWorkspaceFrame.VisibleSourceRect(
            inGame.Width, inGame.Height, iw, ih,
            ModsWorkspaceFrame.CoverAlignX, ModsWorkspaceFrame.CoverAlignY,
            out srcX, out srcY, out srcW, out srcH);
        Assert.True(ModsWorkspaceFrame.FigureSurvivesCrop(srcX, srcY, srcW, srcH, iw, ih));
    }

    [Fact]
    public void Main_menu_must_not_cover_outside_the_workspace_frame()
    {
        var frame = ModsWorkspaceFrame.Fallback(3840f, 1080f);
        // Alpha-0 full-screen blocker is allowed (in-game curtain shape).
        Assert.False(ModsWorkspaceFrame.CoversOutsideFrame(
            0f, 0f, 3840f, 1080f, NativeModsEntry.MainMenuCurtain.A,
            frame.X, frame.Y, frame.Width, frame.Height));
        // Previous haze (alpha 0.58, full screen) must fail.
        Assert.True(ModsWorkspaceFrame.CoversOutsideFrame(
            0f, 0f, 3840f, 1080f, 0.58f, frame.X, frame.Y, frame.Width, frame.Height));
        // Pre-mask 1421-wide image at alpha 1 extends outside the 1276 frame.
        Assert.True(ModsWorkspaceFrame.CoversOutsideFrame(
            1209f, 210f, 1421f, 800f, 1f, 1282f, 210f, 1276f, 800f));
        ModsWorkspaceFrame.ClippedVisibleRect(
            1209f, 210f, 1421f, 800f, 1282f, 210f, 1276f, 800f,
            out float x, out float y, out float w, out float h);
        Assert.False(ModsWorkspaceFrame.CoversOutsideFrame(
            x, y, w, h, 1f, 1282f, 210f, 1276f, 800f));
    }

    [Fact]
    public void Native_tab_while_mods_open_leaves_exactly_one_screen_and_the_native_tab_selected()
    {
        var after = ModsTabSwitch.AfterNativeTabActivated();
        Assert.False(after.ModsShown);
        Assert.False(after.NativeViewCovered);
        Assert.Equal(1, after.VisibleScreenCount);
        Assert.False(after.ModsTabSelected);
        Assert.False(after.OpeningNativeTabStillHighlighted);
    }

    [Fact]
    public void Opening_mods_from_a_native_tab_dismisses_that_page_and_highlights_only_mods()
    {
        var after = ModsTabSwitch.AfterModsOpenedFromNativeTab();
        Assert.True(ModsTabSwitch.IsSingleModsScreen(after));
        Assert.True(after.ModsShown);
        Assert.False(after.NativeViewCovered);
        Assert.Equal(1, after.VisibleScreenCount);
        Assert.True(after.ModsTabSelected);
        Assert.False(after.OpeningNativeTabStillHighlighted);
        // Live gen4: Spellbook stayed drawn, SPELLBOOK+MODS both lit. That must fail.
        var liveWrong = new ModsTabSwitch.Outcome
        {
            ModsShown = true,
            NativeViewCovered = true,
            VisibleScreenCount = 2,
            ModsTabSelected = true,
            OpeningNativeTabStillHighlighted = true,
        };
        Assert.False(ModsTabSwitch.IsSingleModsScreen(liveWrong));
        Assert.Equal(NativeTabSelection.Command.HideHosted,
            NativeTabSelection.OnOpen(default, liveView: 2).Command);
    }

    [Fact]
    public void Escape_close_on_the_top_tab_site_dismisses_fullscreen_menu_search_clear_does_not()
    {
        Assert.True(ModsWorkspaceFrame.ShouldDismissFullscreenMenuOnEscape("top-tab", searchCleared: false));
        Assert.False(ModsWorkspaceFrame.ShouldDismissFullscreenMenuOnEscape("top-tab", searchCleared: true));
        Assert.False(ModsWorkspaceFrame.ShouldDismissFullscreenMenuOnEscape("main-menu", searchCleared: false));
        Assert.False(ModsWorkspaceFrame.ShouldDismissFullscreenMenuOnEscape("shortcut", searchCleared: false));
    }

    [Fact]
    public void Hosted_view_change_while_mods_is_open_still_closes_the_workspace()
    {
        Assert.Equal(
            NativeModsEntry.HostedViewChange.Close,
            NativeModsEntry.DecideHostedViewChange(
                settled: true, curtainPresent: true, hostedViewChanged: true,
                workspaceSuspended: false));
        var after = ModsTabSwitch.AfterNativeTabActivated();
        Assert.Equal(1, after.VisibleScreenCount);
        Assert.False(after.ModsShown);
    }

    [Fact]
    public void Split_keeps_the_sidebar_a_sidebar_when_the_frame_grows()
    {
        MenuPanelLayout.Split(1276f, out float smallSide, out float smallDetail, out _);
        MenuPanelLayout.Split(2000f, out float largeSide, out float largeDetail, out _);
        Assert.Equal(MenuPanelLayout.SidebarMaxWidth, largeSide);
        Assert.True(largeDetail > smallDetail);
        Assert.True(largeSide <= smallSide + 0.01f || largeSide == MenuPanelLayout.SidebarMaxWidth);
        Assert.True(largeSide / 2000f < 0.2f);
    }

    [Fact]
    public void Log_line_is_one_info_and_changes_when_the_path_does()
    {
        var native = NativeContentRect.Missing("no-container");
        var frame = ModsWorkspaceFrame.Fallback(1920f, 1080f);
        string a = ModsWorkspaceFrame.FormatLog(frame, native, "no-container");
        Assert.True(ModsWorkspaceFrame.ShouldLog(a));
        ModsWorkspaceFrame.NoteLogged(a);
        Assert.False(ModsWorkspaceFrame.ShouldLog(a));
        var measured = new NativeContentRect(true, 1600f, 860f, 0f, 0f, "");
        string b = ModsWorkspaceFrame.FormatLog(
            ModsWorkspaceFrame.Resolve(1920f, 1080f, "top-tab", measured), measured, "");
        Assert.True(ModsWorkspaceFrame.ShouldLog(b));
        Assert.Contains("path=native-container", b);
    }

    [Fact]
    public void Native_frame_extent_is_read_like_viewparent_and_sizes_the_runtime_frame()
    {
        Assert.True(ModsWorkspaceFrame.IsNativeBackgroundName("Background"));
        Assert.True(ModsWorkspaceFrame.IsNativeBackgroundName("BackgroundImage"));
        Assert.False(ModsWorkspaceFrame.IsNativeBackgroundName("MenuBackgroundImage"));
        string chrome = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "ModsWorkspaceChrome.cs");
        string readExtent = SourcePin.MethodBody(chrome, "internal static NativeContentRect ReadNativeFrameExtent(");
        SourcePin.AssertUncommented(readExtent, "CollectMenuGraphics(");
        SourcePin.AssertUncommented(readExtent, "NativeFrameExtent.Grow(");
        SourcePin.AssertNoStatementContaining(readExtent, "GetComponentsInChildren");
        SourcePin.AssertNoStatementContaining(readExtent, "FindObjectsOfType");
        string overlay = SourcePin.MethodBody(chrome, "private static NativeContentRect ReadOverlayRect(");
        SourcePin.AssertUncommented(overlay, "TransformPoint(");
        SourcePin.AssertUncommented(overlay, "OverlayIsReady(");
        SourcePin.AssertNoStatementContaining(overlay, "GetWorldCorners");
        string ensure = SourcePin.MethodBody(chrome, "internal static void Ensure()");
        SourcePin.AssertUncommented(ensure, "ForceUpdateCanvases()");
        SourcePin.AssertUncommented(ensure, "SyncFrame()");
        string sync = SourcePin.MethodBody(chrome, "internal static void SyncFrame()");
        SourcePin.AssertUncommented(sync, "ReadCachedExtent(");
        SourcePin.AssertUncommented(sync, "Resolve(cw, ch, site, _native, _nativeExtent)");
        SourcePin.AssertUncommented(sync, "MayLayout(");
        SourcePin.AssertUncommented(sync, "SetPresented(");
        string tick = SourcePin.MethodBody(chrome, "private static void Tick()");
        SourcePin.AssertUncommented(tick, "TryPresentAfterNativeFrame()");
        string cached = SourcePin.MethodBody(chrome, "private static NativeContentRect ReadCachedExtent(");
        SourcePin.AssertUncommented(cached, "LastOverhang.Apply(");
        SourcePin.AssertUncommented(cached, "_extentCached");
        string runtime = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "UnityUiRuntime.cs");
        string geo = SourcePin.MethodBody(runtime, "private void UpdateMenuGeometry(");
        SourcePin.AssertUncommented(geo, "NativeExtent");
        SourcePin.AssertUncommented(geo, "Resolve(width, height, site, native, extent)");
        SourcePin.AssertNoStatementContaining(geo, "ReadNativeFrameExtent(");
        SourcePin.AssertNoStatementContaining(geo, "GetComponentsInChildren");
        string layout = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "MenuPanelLayout.cs");
        string grow = SourcePin.MethodBody(layout, "internal static NativeContentRect Grow(");
        SourcePin.AssertUncommented(grow, "QualifiesAsFooter(");
        SourcePin.AssertUncommented(grow, "ViewParentName");
        // Revert: reading only ViewParent / BackgroundImage, or using GetWorldCorners,
        // or walking chrome every pane tick, restores the black band.
    }

    [Fact]
    public void In_game_first_show_requires_a_native_frame_title_keeps_fallback()
    {
        Assert.True(ModsWorkspaceFrame.RequiresNativeFrame("top-tab"));
        Assert.False(ModsWorkspaceFrame.RequiresNativeFrame("main-menu"));
        Assert.False(ModsWorkspaceFrame.RequiresNativeFrame("shortcut"));
        Assert.False(ModsWorkspaceFrame.OverlayIsReady(0f, 1080f));
        Assert.False(ModsWorkspaceFrame.OverlayIsReady(1920f, 0f));
        Assert.True(ModsWorkspaceFrame.OverlayIsReady(1920f, 1080f));

        Assert.True(ModsWorkspaceFrame.MayPresent("main-menu", nativeFound: false, ModsWorkspaceFrame.PathFallback));
        Assert.True(ModsWorkspaceFrame.MayLayout("main-menu", nativeFound: false, ModsWorkspaceFrame.PathFallback));
        Assert.False(ModsWorkspaceFrame.MayPresent("top-tab", nativeFound: false, ModsWorkspaceFrame.PathFallback));
        Assert.False(ModsWorkspaceFrame.MayLayout("top-tab", nativeFound: false, ModsWorkspaceFrame.PathFallback));
        Assert.False(ModsWorkspaceFrame.MayPresent("top-tab", nativeFound: true, ModsWorkspaceFrame.PathFallback));
        Assert.True(ModsWorkspaceFrame.MayPresent("top-tab", nativeFound: true, ModsWorkspaceFrame.PathNative));
        Assert.True(ModsWorkspaceFrame.MayPresent("top-tab", nativeFound: true, ModsWorkspaceFrame.PathNativeCapped));

        ModsWorkspaceFrame.BeginFirstShow();
        Assert.False(ModsWorkspaceFrame.Shown);
        Assert.Equal(0, ModsWorkspaceFrame.LayoutBeforeShow);
        ModsWorkspaceFrame.NoteHiddenLayout();
        Assert.Equal(1, ModsWorkspaceFrame.LayoutBeforeShow);
        ModsWorkspaceFrame.NoteShown(4, ModsWorkspaceFrame.PathNative);
        Assert.True(ModsWorkspaceFrame.Shown);
        Assert.Equal(4, ModsWorkspaceFrame.FirstShowFrame);
        Assert.Equal(ModsWorkspaceFrame.PathNative, ModsWorkspaceFrame.ShownPath);
        Assert.Equal(
            "Ui: workspace shown frame=4 framePath=" + ModsWorkspaceFrame.PathNative,
            ModsWorkspaceFrame.FormatShownLog(4, ModsWorkspaceFrame.PathNative));
        ModsWorkspaceFrame.NoteHiddenLayout();
        Assert.Equal(1, ModsWorkspaceFrame.LayoutBeforeShow);
        // Revert: presenting a fallback frame on the top-tab site is the gen-36 flash.
    }
}
