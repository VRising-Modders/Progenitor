using System;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>The placement math and self-check of the <c>AttachPanelTabs</c> strip on station windows. Game-free.</summary>
public sealed class VanillaTabStripLayoutTests
{
    private static ScreenRect R(float x, float y, float w, float h) => ScreenRect.FromCorners(x, y, x + w, y + h);

    [Fact]
    public void DonorHeightIsUsedWhenPlausible()
    {
        Assert.Equal(38f, VanillaTabStripLayout.TabHeight(38f, 20f, out bool fromDonor));
        Assert.True(fromDonor);
    }

    [Theory]
    [InlineData(0f)]        // the donor tab read before its layout ran (the prison cell window)
    [InlineData(13f)]       // shorter than MinTabHeight
    [InlineData(float.NaN)] // no donor
    [InlineData(400f)]      // a stretched rect
    public void UnusableDonorHeightFallsBackToOneThatFitsTheCaption(float measured)
    {
        float h = VanillaTabStripLayout.TabHeight(measured, 22.5f, out bool fromDonor);
        Assert.False(fromDonor);
        Assert.True(h >= 22.5f + 2 * VanillaTabStripLayout.CaptionPadding);
        Assert.True(h >= VanillaTabStripLayout.FallbackTabHeight);
        Assert.True(h <= VanillaTabStripLayout.MaxTabHeight);
    }

    [Fact]
    public void ADonorHeightInRangeIsAcceptedEvenUnderTheCaptionFloor()
    {
        // The caption floor applies to the fallback only: a real 26-unit tab with a 20-unit caption is kept.
        Assert.Equal(26f, VanillaTabStripLayout.TabHeight(26f, 20f, out bool fromDonor));
        Assert.True(fromDonor);
    }

    [Fact]
    public void FallbackIsTheOldNoDonorDefault()
    {
        Assert.Equal(49f, VanillaTabStripLayout.TabHeight(float.NaN, 0f, out _));
        Assert.Equal(49f, VanillaTabStripLayout.TabHeight(0f, 22.5f, out _));
    }

    [Fact]
    public void HeightMovesFromFallbackToTheDonorOnceItIsLaidOut()
    {
        // Frame 100: the donor tab is read before its layout ran (zero tall).
        var height = new VanillaTabStripHeight(0f, 22.5f, 100);
        Assert.False(height.FromDonor);
        Assert.Equal(49f, height.Height);

        // While on the fallback the donor is read every frame.
        Assert.True(height.Due(101));
        Assert.False(height.Read(0f, 101));
        Assert.True(height.Due(102));

        // Frame 102: the game laid the tab out at 38 units; the strip resizes once.
        Assert.True(height.Read(38f, 102));
        Assert.True(height.FromDonor);
        Assert.Equal(38f, height.Height);

        // From the donor, reads are throttled.
        Assert.False(height.Due(103));
        Assert.False(height.Due(102 + VanillaTabStripLayout.HeightReadInterval - 1));
        Assert.True(height.Due(102 + VanillaTabStripLayout.HeightReadInterval));
        Assert.False(height.Read(38.2f, 132));

        // A later collapse to zero returns to the fallback and to per-frame reads.
        Assert.True(height.Read(0f, 162));
        Assert.False(height.FromDonor);
        Assert.True(height.Due(163));
    }

    [Fact]
    public void ChecksAreThrottledAndMovedChecksRateLimited()
    {
        Assert.False(VanillaTabStripLayout.CheckDue(false, 129, 100));
        Assert.True(VanillaTabStripLayout.CheckDue(false, 130, 100));
        Assert.False(VanillaTabStripLayout.CheckDue(true, 104, 100));
        Assert.True(VanillaTabStripLayout.CheckDue(true, 105, 100));
    }

    [Fact]
    public void LogGateLogsOnlyWhenTwoChecksInARowAgreeAndOnlyOnce()
    {
        var gate = new VanillaTabStripLogGate();
        var ok = new VanillaTabStripCheck("ok", "", 1f);
        var covered = new VanillaTabStripCheck("covered", "canvas list order 5040", 1f);
        var clipped = new VanillaTabStripCheck("clipped", "RectMask2D body", .5f);

        Assert.False(gate.ShouldLog(covered)); // first sighting: a window still laying out
        Assert.False(gate.ShouldLog(ok));      // gone again: a transient cover is never logged
        Assert.False(gate.ShouldLog(covered));
        Assert.False(gate.ShouldLog(clipped)); // a different state does not count as a repeat
        Assert.True(gate.ShouldLog(clipped));  // two in a row
        Assert.False(gate.ShouldLog(clipped)); // once per strip
        Assert.False(gate.ShouldLog(covered));
        Assert.False(gate.ShouldLog(covered));

        gate.Reset();                           // a new strip
        Assert.False(gate.ShouldLog(covered));
        Assert.True(gate.ShouldLog(covered));
    }

    [Fact]
    public void StripSitsUnderTheBonusRowAndThePanelUnderTheStrip()
    {
        var p = VanillaTabStripLayout.Place(anchorTop: -150f, bonusBottom: -120f, visibleTop: null, height: 38f);
        Assert.Equal(-140f, p.Top);
        Assert.Equal(-178f, p.Bottom);
        Assert.Equal(-178f - VanillaTabStripLayout.PanelGap, p.PanelTop);
        Assert.False(p.Clamped);
        // The panel never overlaps the strip's full height.
        Assert.True(p.PanelTop < p.Bottom);
    }

    [Fact]
    public void WithoutABonusRowTheGamesTabRowTopIsUsed()
    {
        var p = VanillaTabStripLayout.Place(anchorTop: -150f, bonusBottom: null, visibleTop: null, height: 38f);
        Assert.Equal(-150f, p.Top);
    }

    [Fact]
    public void StripIsMovedDownInsideAParentMask()
    {
        var p = VanillaTabStripLayout.Place(anchorTop: -150f, bonusBottom: -120f, visibleTop: -160f, height: 38f);
        Assert.Equal(-160f, p.Top);
        Assert.True(p.Clamped);
        Assert.Equal(-198f - VanillaTabStripLayout.PanelGap, p.PanelTop);
    }

    [Fact]
    public void AMaskBelowTheStripsTopDoesNotLiftIt()
    {
        var p = VanillaTabStripLayout.Place(anchorTop: -150f, bonusBottom: -120f, visibleTop: 0f, height: 38f);
        Assert.Equal(-140f, p.Top);
        Assert.False(p.Clamped);
    }

    [Fact]
    public void NonFiniteInputsDoNotProduceNaN()
    {
        var p = VanillaTabStripLayout.Place(float.NaN, float.NaN, float.PositiveInfinity, float.NaN);
        Assert.False(float.IsNaN(p.Top));
        Assert.Equal(0f, p.Height);
    }

    [Fact]
    public void CheckReportsOkInsideMasksAndUncovered()
    {
        var strip = R(100, 800, 560, 38);
        var c = VanillaTabStripLayout.Check(strip, new[] { ("RectMask2D body", R(80, 100, 600, 738.5f)) },
            new[] { ("sibling far", R(100, 100, 560, 200)) }, 12f);
        Assert.True(c.Ok);
        Assert.Equal(1f, c.Visible);
    }

    [Fact]
    public void CheckReportsTheMaskThatCutsTheTopHalf()
    {
        var strip = R(100, 800, 560, 40);
        var c = VanillaTabStripLayout.Check(strip,
            new[] { ("RectMask2D outer", R(0, 0, 1000, 1000)), ("RectMask2D body", R(80, 100, 600, 720)) },
            Array.Empty<(string, ScreenRect)>(), 12f);
        Assert.Equal("clipped", c.State);
        Assert.Equal("RectMask2D body", c.By);
        Assert.InRange(c.Visible, .45f, .55f);
    }

    [Fact]
    public void CheckReportsASiblingDrawnOverTheStrip()
    {
        var strip = R(100, 800, 560, 38);
        var c = VanillaTabStripLayout.Check(strip, Array.Empty<(string, ScreenRect)>(),
            new[] { ("sibling Header", R(90, 820, 600, 100)) }, 12f);
        Assert.Equal("covered", c.State);
        Assert.Equal("sibling Header", c.By);
    }

    [Fact]
    public void TouchingEdgesAreNeitherClippedNorCovered()
    {
        var strip = R(100, 800, 560, 38);
        var c = VanillaTabStripLayout.Check(strip, new[] { ("RectMask2D body", R(100, 0, 560, 838)) },
            new[] { ("sibling panel", R(100, 700, 560, 100)) }, 12f);
        Assert.True(c.Ok, c.State + " " + c.By);
    }

    [Fact]
    public void AZeroHeightStripIsDegenerate()
    {
        // The live bug: the strip was as tall as its donor tab read on the first frame (zero).
        var c = VanillaTabStripLayout.Check(R(100, 800, 560, 0), Array.Empty<(string, ScreenRect)>(),
            Array.Empty<(string, ScreenRect)>(), 12f);
        Assert.Equal("degenerate", c.State);
        Assert.False(VanillaTabStripLayout.Check(ScreenRect.Unknown, Array.Empty<(string, ScreenRect)>(),
            Array.Empty<(string, ScreenRect)>(), 12f).Ok);
    }

    [Theory]
    [InlineData(170f, 6f)]
    [InlineData(13f, 6f)]
    [InlineData(9f, 4f)]  // the caption keeps one unit
    [InlineData(1f, 0f)]
    [InlineData(0f, 0f)]
    [InlineData(float.NaN, 0f)]
    public void CaptionInsetNeverLeavesTheCaptionWithoutWidth(float tabWidth, float inset)
        => Assert.Equal(inset, VanillaTabCaption.InsetFor(tabWidth));

    [Fact]
    public void CaptionOffsetIsCenterMinusCenter()
    {
        var tab = R(100, 200, 170, 38);
        Assert.Equal((0f, 0f), VanillaTabCaption.Offset(tab, R(106, 200, 158, 38)));
        // A label sized for a narrower donor tab and left where the donor put it: left of center, low.
        Assert.Equal((-15f, -2f), VanillaTabCaption.Offset(tab, R(106, 198, 128, 38)));
        Assert.True(float.IsNaN(VanillaTabCaption.Offset(tab, ScreenRect.Unknown).Dx));
    }

    [Theory]
    [InlineData("Production", 92f)]
    [InlineData("Input", 38f)]
    [InlineData("Output", 50f)]
    public void ACaptionStretchedOverItsTabIsCenteredForAnyLength(string caption, float textWidth)
    {
        _ = caption;
        var tab = R(1210, 600, 170, 38);
        float inset = VanillaTabCaption.InsetFor(170f);
        var rect = R(1210 + inset, 600, 170 - 2 * inset, 38);
        // Center-aligned text is drawn about the rect's center; a descender lowers the glyphs only.
        var text = R(1295 - textWidth * .5f, 609, textWidth, 18);
        Assert.True(VanillaTabCaption.Centered(tab, rect, text));
    }

    [Fact]
    public void AnOffCenterCaptionRectOrTextIsReported()
    {
        var tab = R(0, 0, 170, 38);
        var rect = R(6, 0, 158, 38);
        Assert.False(VanillaTabCaption.Centered(tab, R(0, 0, 158, 38), ScreenRect.Unknown));   // rect left of center
        Assert.False(VanillaTabCaption.Centered(tab, R(6, -3, 158, 38), ScreenRect.Unknown));  // rect low
        Assert.False(VanillaTabCaption.Centered(tab, rect, R(70, 10, 40, 18)));                // text right of center
        Assert.True(VanillaTabCaption.Centered(tab, rect, R(65, 6, 40, 18)));                  // glyphs low only: line-centered
        Assert.True(VanillaTabCaption.Centered(tab, rect, ScreenRect.Unknown));                // nothing drawn yet
        Assert.False(VanillaTabCaption.Centered(ScreenRect.Unknown, rect, ScreenRect.Unknown));
    }

    [Theory]
    [InlineData(146f, 40f, true, true)]    // the frame itself
    [InlineData(122.5f, 31f, true, true)]  // a state overlay nearly as big
    [InlineData(146f, 4f, true, true)]     // an underline or top rule: wide enough one way
    [InlineData(20f, 40f, true, true)]     // an end cap: tall enough one way
    [InlineData(24f, 20f, true, false)]    // a small state graphic
    [InlineData(146f, 40f, false, false)]  // frame-sized, but not one of the button's state graphics
    public void FrameArtIsAStateGraphicSpanningTheFrameOneWay(float w, float h, bool target, bool frame)
        => Assert.Equal(frame, VanillaTabCaption.IsFrameArt(w, h, 146f, 40f, target, buttonHasTargets: true));

    [Theory]
    [InlineData(146f, 40f, true)]
    [InlineData(146f, 4f, false)]  // without named targets a thin line is not trusted as frame
    [InlineData(24f, 24f, false)]
    public void WithoutNamedTargetsFrameArtMustFillBothWays(float w, float h, bool frame)
        => Assert.Equal(frame, VanillaTabCaption.IsFrameArt(w, h, 146f, 40f, graphicTarget: false, buttonHasTargets: false));

    [Fact]
    public void NoVisibleImagesMeansNoFrameArt()
        => Assert.False(VanillaTabCaption.IsFrameArt(10f, 10f, 0f, 0f, true, true));

    [Theory]
    [InlineData("Animator", true)]
    [InlineData("Animation", true)]
    [InlineData("HorizontalLayoutGroup", true)]
    [InlineData("GridLayoutGroup", true)]
    [InlineData("ContentSizeFitter", true)]
    [InlineData("AspectRatioFitter", true)]
    [InlineData("LayoutElement", false)]
    [InlineData("Image", false)]
    [InlineData(null, false)]
    public void LayoutDriversAreNamed(string? type, bool driver)
        => Assert.Equal(driver, VanillaTabCaption.IsLayoutDriver(type));

    [Fact]
    public void ArtBoxPrefersTheLargestVisibleFrameImage()
    {
        var frame = R(2164, 600, 146, 31);
        var images = new (ScreenRect, bool, bool, bool)[]
        {
            (R(2164, 600, 190.3f, 38), true, true, false), // the tab's own root image: follows the tab rect
            (R(2150, 590, 220, 60), true, false, false),   // a larger glow that is not frame art
            (frame, true, false, true),
            (R(2170, 600, 160, 38), false, false, true),   // a hidden state overlay
            (R(2200, 605, 20, 20), true, false, false),    // an icon
            (ScreenRect.Unknown, true, false, true),
        };
        Assert.Equal(frame, VanillaTabCaption.ArtBox(images));
        Assert.False(VanillaTabCaption.ArtBox(Array.Empty<(ScreenRect, bool, bool, bool)>()).IsKnown);
    }

    [Fact]
    public void ArtBoxUsesTheRootImageOnlyWhenNothingElseDraws()
    {
        var root = R(0, 0, 190, 38);
        var icon = R(10, 10, 20, 20);
        Assert.Equal(icon, VanillaTabCaption.ArtBox(new (ScreenRect, bool, bool, bool)[] { (root, true, true, false), (icon, true, false, false) }));
        Assert.Equal(root, VanillaTabCaption.ArtBox(new (ScreenRect, bool, bool, bool)[] { (root, true, true, false), (icon, false, false, false) }));
    }

    [Fact]
    public void ACaptionCenteredOnTheTabButNotOnItsArtIsNotCentered()
    {
        // The live readback that passed falsely: glyphs centered on a 190.3 wide tab whose frame
        // art kept the donor's 146-wide box at the tab's left edge.
        var tab = R(2164, 600, 190.3f, 38);
        var caption = R(2170, 600, 178.3f, 38);
        var glyphs = R(2218, 610, 82.4f, 18);
        Assert.True(VanillaTabCaption.Centered(tab, caption, glyphs));
        var art = VanillaTabCaption.ArtBox(new (ScreenRect, bool, bool, bool)[]
            { (tab, true, true, false), (R(2164, 600, 146, 38), true, false, true) });
        Assert.False(VanillaTabCaption.Centered(art, caption, glyphs));
        // Once the art is refitted to the tab, the two agree.
        art = VanillaTabCaption.ArtBox(new (ScreenRect, bool, bool, bool)[] { (tab, true, true, false), (tab, true, false, true) });
        Assert.True(VanillaTabCaption.Centered(art, caption, glyphs));
    }

    [Fact]
    public void ProportionalKeepsInsetsAndSpillAsShares()
    {
        // A donor tab 146 x 40 at (0,0); its frame inset 4 on each side and spilling 6 below.
        var share = VanillaTabCaption.Proportional(0, 0, 146, 40, 4, -6, 142, 40);
        Assert.NotNull(share);
        var (x0, y0, x1, y1) = share!.Value;
        Assert.Equal(4f / 146f, x0, 4);
        Assert.Equal(-6f / 40f, y0, 4);
        Assert.Equal(142f / 146f, x1, 4);
        Assert.Equal(1f, y1, 4);
        // Equal shares in from each side: the frame stays centered on a tab of any width.
        Assert.Equal(x0, 1f - x1, 4);
    }

    [Theory]
    [InlineData(0f, 40f)]  // the donor read before its layout ran
    [InlineData(146f, 0f)]
    [InlineData(float.NaN, 40f)]
    public void ProportionalRefusesAnUnusableDonorTab(float w, float h)
        => Assert.Null(VanillaTabCaption.Proportional(0, 0, w, h, 4, 0, 100, 40));

    [Fact]
    public void ProportionalRefusesAnEmptyOrFarBox()
    {
        Assert.Null(VanillaTabCaption.Proportional(0, 0, 146, 40, 10, 10, 10, 30));      // no width
        Assert.Null(VanillaTabCaption.Proportional(0, 0, 146, 40, 400, 0, 500, 40));     // more than a tab past the edge
        Assert.NotNull(VanillaTabCaption.Proportional(0, 0, 146, 40, -146, 0, 292, 40)); // exactly one tab of spill each way
    }

    [Fact]
    public void DrawnRectRemovesSpritePadding()
    {
        var rect = R(0, 0, 100, 40);
        var drawn = VanillaTabCaption.Drawn(rect, 10, 2, 0, 2);
        Assert.Equal(10f, drawn.MinX);
        Assert.Equal(100f, drawn.MaxX);
        Assert.Equal(2f, drawn.MinY);
        Assert.Equal(38f, drawn.MaxY);
        Assert.Equal((5f, 0f), VanillaTabCaption.Offset(rect, drawn)); // padding on the left moves the art right

        var collapsed = VanillaTabCaption.Drawn(rect, 80, 0, 80, 0);
        Assert.Equal(50f, collapsed.MinX);
        Assert.Equal(50f, collapsed.MaxX);
        Assert.Equal(rect, VanillaTabCaption.Drawn(rect, float.NaN, 0, 0, 0));
        Assert.False(VanillaTabCaption.Drawn(ScreenRect.Unknown, 1, 1, 1, 1).IsKnown);
    }

    [Fact]
    public void DriftIgnoresFloatNoiseAndCatchesNaN()
    {
        Assert.False(VanillaTabCaption.Drifted(6.001f, 6f));
        Assert.True(VanillaTabCaption.Drifted(6.5f, 6f));
        Assert.True(VanillaTabCaption.Drifted(float.NaN, 0f));
    }

    // The Blood Press readback (live, Core 2026.10.93): placed strip, top 779, 49 high, panel top
    // 724, so the strip's bottom is 730, the bonus row's bottom 799 (top = bonus bottom - 20).
    // The recipe list reached into the strip.

    [Fact]
    public void BloodPressStripIsPlacedUnderTheBonusRow()
    {
        var p = VanillaTabStripLayout.Place(900f, 799f, null, 49f);
        Assert.Equal(779f, p.Top);
        Assert.Equal(730f, p.Bottom);
        Assert.Equal(724f, p.PanelTop);
        Assert.False(p.Raised);
        Assert.Equal(0f, p.Overlap);
    }

    [Fact]
    public void ContentUnderTheStripLeavesItWhereItIs()
    {
        var p = VanillaTabStripLayout.Place(900f, 799f, null, 49f);
        Assert.Equal(p.Top, VanillaTabStripLayout.ClearContent(p, 724f, 799f).Top);
        Assert.Equal(p.Top, VanillaTabStripLayout.ClearContent(p, 730.5f, 799f).Top); // touching, within tolerance
        Assert.Equal(p.Top, VanillaTabStripLayout.ClearContent(p, null, 799f).Top);
    }

    [Fact]
    public void ContentReachingIntoTheStripRaisesItOntoTheContent()
    {
        var p = VanillaTabStripLayout.Place(900f, 799f, null, 49f);
        var clear = VanillaTabStripLayout.ClearContent(p, 740f, 799f);
        Assert.True(clear.Raised);
        Assert.Equal(789f, clear.Top);
        Assert.Equal(740f, clear.Bottom);
        Assert.Equal(0f, clear.Overlap);
    }

    [Fact]
    public void TheStripIsNeverRaisedIntoTheBonusRowAndReportsWhatStillOverlaps()
    {
        var p = VanillaTabStripLayout.Place(900f, 799f, null, 49f);
        // The first recipe row's top at 770: standing on it would put the strip's top at 819, past the bonus row.
        var clear = VanillaTabStripLayout.ClearContent(p, 770f, 799f);
        Assert.True(clear.Raised);
        Assert.Equal(799f, clear.Top);
        Assert.Equal(20f, clear.Overlap, 3);
        // A strip already at its ceiling is not moved, and the overlap is reported.
        var stuck = VanillaTabStripLayout.ClearContent(p, 770f, 779f);
        Assert.False(stuck.Raised);
        Assert.Equal(779f, stuck.Top);
        Assert.Equal(40f, stuck.Overlap, 3);
    }

    // The prison cell readback (live, Core 2026.10.93): a 190.3 x 49 tab whose own image draws
    // Tab_Normal (320 x 94, simple, preserve aspect), with the baseline trims TabDivider (bottom 0,
    // 8 high) and TabGradient (bottom 2.4, 8 high) drawn over the tabs' bottom edge.

    [Fact]
    public void PreserveAspectDrawsAWideTabsArtByItsPivot()
    {
        var tab = R(2164, 858, 190.3f, 49);
        var corner = VanillaTabCaption.PreserveAspect(tab, 320, 94, 0f, 1f);
        Assert.Equal(2164f, corner.MinX, 1);
        Assert.Equal(49f * 320f / 94f, corner.MaxX - corner.MinX, 1);   // 166.8: narrower than the tab
        Assert.Equal(49f, corner.MaxY - corner.MinY, 1);
        Assert.True(VanillaTabCaption.Offset(tab, corner).Dx < -11f);   // the art sits left; a centered caption reads right

        var centered = VanillaTabCaption.PreserveAspect(tab, 320, 94, .5f, .5f);
        Assert.Equal(0f, VanillaTabCaption.Offset(tab, centered).Dx, 2);
        Assert.Equal(0f, VanillaTabCaption.Offset(tab, centered).Dy, 2);
    }

    [Fact]
    public void PreserveAspectDrawsATallTabsArtByItsPivotToo()
    {
        var tab = R(0, 0, 100, 60);
        var top = VanillaTabCaption.PreserveAspect(tab, 320, 94, 0f, 1f);
        Assert.Equal(100f, top.MaxX - top.MinX, 1);
        Assert.Equal(60f, top.MaxY, 1);                                  // against the top: a centered caption reads low
        Assert.True(VanillaTabCaption.Offset(tab, top).Dy > 10f);
        Assert.Equal(0f, VanillaTabCaption.Offset(tab, VanillaTabCaption.PreserveAspect(tab, 320, 94, .5f, .5f)).Dy, 2);
        Assert.Equal(tab, VanillaTabCaption.PreserveAspect(tab, 0, 94, .5f, .5f));
    }

    [Fact]
    public void TrimsOverTheTabsBottomEdgeRaiseTheCaptionBand()
    {
        var band = VanillaTabCaption.CoveredInsets(0f, 49f, new[] { (2.4f, 10.4f), (0f, 8f) });
        Assert.Equal(10.4f, band.Bottom, 2);                           // chained, in either order
        Assert.Equal(0f, band.Top);
        Assert.Equal((0f, 0f), VanillaTabCaption.CoveredInsets(0f, 49f, new[] { (-8f, 0f) }));  // under the tabs: nothing hidden
        Assert.Equal((0f, 0f), VanillaTabCaption.CoveredInsets(0f, 49f, new[] { (20f, 24f) })); // a band in the middle
        Assert.Equal((0f, 0f), VanillaTabCaption.CoveredInsets(0f, 49f, new[] { (-1f, 60f) })); // covers it all
        Assert.Equal(4f, VanillaTabCaption.CoveredInsets(0f, 49f, new[] { (45f, 52f) }).Top, 2);
    }

    [Fact]
    public void TheVisibleArtExcludesFullWidthTrimsOverItsEdge()
    {
        var tab = R(2164, 858, 190.3f, 49);
        var divider = R(2152, 858, 604, 8);
        var visible = VanillaTabCaption.Uncovered(tab, new[] { divider, R(2160, 860.4f, 588, 8) });
        Assert.Equal(868.4f, visible.MinY, 1);
        Assert.Equal(907f, visible.MaxY, 1);

        // The live false pass: glyphs centered on the tab rect read about 5 px low on the visible art.
        var caption = R(2170, 858, 178.3f, 49);
        var glyphs = R(2218, 872.9f, 82.4f, 19.2f);
        Assert.True(VanillaTabCaption.Centered(tab, caption, glyphs));
        Assert.False(VanillaTabCaption.Centered(visible, caption, glyphs));
        // With the caption band raised by the covered 10.4 units, it is centered again.
        Assert.True(VanillaTabCaption.Centered(visible, R(2170, 868.4f, 178.3f, 38.6f), R(2218, 878.1f, 82.4f, 19.2f)));

        // A narrower image over the art (another tab's piece, an icon) hides no edge.
        Assert.Equal(tab, VanillaTabCaption.Uncovered(tab, new[] { R(2170, 858, 40, 8) }));
    }
}
