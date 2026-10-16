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
    [InlineData(13f)]       // shorter than the caption
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
}
