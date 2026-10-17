using System;
using System.Collections.Generic;
using System.Linq;
using Progenitor.Cameras;
using Progenitor.Ui;
using Progenitor.Units;
using Xunit;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.Tests;

/// <summary>
/// DOCK1: docked panels (<see cref="UiDockLayout"/>, <see cref="UiPlacement"/>) and the camera preview's framing offset
/// (<see cref="CameraFraming"/>). Game-free: the placement arithmetic across resolutions, its interplay with the window
/// row and the panel flow, and the handle-level API through the kit's fake runtime. The framing offset is CameraFramingTests.
/// </summary>
[Collection("PluginLogger")]
public sealed class UiDockTests : IDisposable
{
    private readonly UiTests.FakeUiRuntime _runtime = new();
    private readonly object _owner = new();

    public UiDockTests()
    {
        UiApi.ResetForTests();
        UiApi.SetRuntimeForTests(_runtime);
    }

    public void Dispose() => UiApi.ResetForTests();

    private const float Basic = UiWindowLayout.BasicFrameWidth;
    private const float Tabbed = UiWindowLayout.TabbedFrameWidth;

    private static UiDockSpec Left(float margin = 16f, UiDockAlign align = UiDockAlign.Center, float width = 0f, float fraction = 0f) =>
        new(UiDock.Left, align, margin, width, fraction);

    private static UiDockSpec Right(float margin = 16f, UiDockAlign align = UiDockAlign.Center, float width = 0f, float fraction = 0f) =>
        new(UiDock.Right, align, margin, width, fraction);

    public static IEnumerable<object[]> Screens() => new[]
    {
        new object[] { 1280f, 720f },
        new object[] { 1920f, 1080f },
        new object[] { 2560f, 1440f },
        new object[] { 2520f, 1080f },
        new object[] { 3840f, 1080f },
    };

    // ---- the dock itself ----------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Screens))]
    public void A_docked_frame_sits_its_margin_from_its_edge_and_inside_the_screen(float w, float h)
    {
        foreach (float margin in new[] { 0f, 16f, 40f })
        {
            var left = UiDockLayout.PlaceAlone(Left(margin), w, h);
            Assert.Equal(-w * 0.5f + margin + Basic * 0.5f, left.X, 3);
            var right = UiDockLayout.PlaceAlone(Right(margin), w, h);
            Assert.Equal(w * 0.5f - margin - Basic * 0.5f, right.X, 3);

            // Centered vertically; at its tallest it stays inside the margins.
            var mid = left.CenterFor(400f);
            Assert.Equal(0f, mid.Y, 3);
            var tallest = left.CenterFor(float.MaxValue);
            Assert.True(tallest.Y + left.MaxHeight * 0.5f <= h * 0.5f - PanelFlow.EdgeMargin + 0.01f);
            Assert.True(tallest.Y - left.MaxHeight * 0.5f >= -h * 0.5f + PanelFlow.EdgeMargin - 0.01f);
        }
    }

    [Theory]
    [MemberData(nameof(Screens))]
    public void A_top_aligned_dock_hangs_from_the_top_margin_and_grows_down(float w, float h)
    {
        var top = UiDockLayout.PlaceAlone(Left(align: UiDockAlign.Top), w, h);
        foreach (float height in new[] { 200f, 420f, 700f })
        {
            var at = top.CenterFor(height);
            float drawn = Math.Min(height, top.MaxHeight);
            Assert.Equal(h * 0.5f - PanelFlow.EdgeMargin, at.Y + drawn * 0.5f, 3);
        }

        // Under the Mods workspace's tab strip, never over it.
        float strip = UiWindowLayout.WorkspaceStripBottom(default, w, h);
        var under = UiDockLayout.PlaceAlone(Left(align: UiDockAlign.Top), w, h, stripBottom: strip);
        var placed = under.CenterFor(500f);
        Assert.True(placed.Y + Math.Min(500f, under.MaxHeight) * 0.5f <= strip - UiWindowLayout.StripClearance + 0.01f);
    }

    [Theory]
    [InlineData(1920f, 0.3f, 640f)]   // 30% of 1920 is 576: the kit frame's 640 floor
    [InlineData(2560f, 0.3f, 768f)]
    [InlineData(3840f, 0.3f, 1152f)]
    [InlineData(1920f, 0.5f, 960f)]
    public void A_width_fraction_follows_the_screen_with_the_kit_floor(float w, float fraction, float expected)
    {
        Assert.Equal(expected, UiDockLayout.FrameWidth(Left(fraction: fraction), w), 2);
        // A fraction wins over units; units alone are capped at 90% of the screen.
        Assert.Equal(expected, UiDockLayout.FrameWidth(Left(width: 2000f, fraction: fraction), w), 2);
        Assert.Equal(w * 0.9f, UiDockLayout.FrameWidth(Left(width: 99999f), w), 2);
        Assert.Equal(Basic, UiDockLayout.FrameWidth(Left(width: 300f), w), 2);
    }

    [Fact]
    public void Two_panels_docked_to_one_side_line_up_inward()
    {
        var docks = new[] { Left(), Left(width: 800f) };
        var first = UiDockLayout.Place(docks, 0, 3840f, 1080f, ownWidth: Basic);
        var second = UiDockLayout.Place(docks, 1, 3840f, 1080f, ownWidth: 800f);
        float firstRight = first.X + Basic * 0.5f;
        float secondLeft = second.X - 400f;
        Assert.Equal(-1920f + 16f + Basic * 0.5f, first.X, 3);
        Assert.Equal(PanelFlow.ColumnGap, secondLeft - firstRight, 3);
        // A stack that runs out of screen is clamped inside it.
        var many = Enumerable.Repeat(Left(), 4).ToArray();
        var last = UiDockLayout.Place(many, 3, 1920f, 1080f);
        Assert.True(last.X + Basic * 0.5f <= 960f + 0.01f);
    }

    [Fact]
    public void A_dock_ignores_the_other_panels_so_show_and_hide_never_move_it()
    {
        var alone = UiDockLayout.Place(new[] { Left() }, 0, 1920f, 1080f);
        var withRight = UiDockLayout.Place(new[] { Left(), Right() }, 0, 1920f, 1080f);
        Assert.Equal(alone.X, withRight.X, 3);
        Assert.Equal(alone.Top, withRight.Top, 3);
    }

    [Fact]
    public void Bad_dock_options_are_refused()
    {
        Assert.NotNull(UiDockLayout.Refuse((UiDock)7, new UiDockOptions()));
        Assert.NotNull(UiDockLayout.Refuse(UiDock.Left, new UiDockOptions { Margin = -1 }));
        Assert.NotNull(UiDockLayout.Refuse(UiDock.Left, new UiDockOptions { Margin = float.NaN }));
        Assert.NotNull(UiDockLayout.Refuse(UiDock.Left, new UiDockOptions { Width = -5 }));
        Assert.NotNull(UiDockLayout.Refuse(UiDock.Left, new UiDockOptions { WidthFraction = 0.95f }));
        Assert.NotNull(UiDockLayout.Refuse(UiDock.Left, new UiDockOptions { Align = (UiDockAlign)9 }));
        Assert.Null(UiDockLayout.Refuse(UiDock.Right, new UiDockOptions { WidthFraction = 0.3f, Align = UiDockAlign.Top }));
    }

    // ---- the window row and the panel flow keep clear ------------------------------------------------------------------

    /// <summary>The docked frame's left and right edges.</summary>
    private static (float L, float R) DockRect(UiDockSpec[] docks, int i, float w, float h)
    {
        float fw = UiDockLayout.FrameWidth(docks[i], w);
        var p = UiDockLayout.Place(docks, i, w, h, fw);
        return (p.X - fw * 0.5f, p.X + fw * 0.5f);
    }

    [Theory]
    [MemberData(nameof(Screens))]
    public void The_window_row_avoids_the_docked_area(float w, float h)
    {
        var dockSets = new[]
        {
            new[] { Left() },
            new[] { Right() },
            new[] { Left(), Right() },
            new[] { Left(fraction: 0.3f) },
        };
        var rowSets = new[] { new[] { Basic }, new[] { Basic, Basic }, new[] { Tabbed, Basic, Basic } };
        foreach (var docks in dockSets)
        {
            var (areaWidth, shift) = UiDockLayout.FreeArea(docks, w);
            bool avoided = areaWidth < w;
            foreach (var widths in rowSets)
            {
                for (int i = 0; i < widths.Length; i++)
                {
                    var spot = new PanelSpot(i, widths, false).WithDocks(docks);
                    var p = UiWindowLayout.Place(spot, w, h);
                    float fw = Math.Min(UiWindowLayout.FrameWidthFor(widths[i], areaWidth), areaWidth - 2f * PanelFlow.EdgeMargin);
                    float l = p.X - fw * 0.5f, r = p.X + fw * 0.5f;
                    string what = docks.Length + " dock(s), row " + string.Join("+", widths) + " #" + i + " on " + w + "x" + h;
                    Assert.True(l >= -w * 0.5f + PanelFlow.EdgeMargin - 0.01f && r <= w * 0.5f - PanelFlow.EdgeMargin + 0.01f, what + ": off screen");
                    if (!avoided || p.Depth > 0) continue;
                    for (int d = 0; d < docks.Length; d++)
                    {
                        var (dl, dr) = DockRect(docks, d, w, h);
                        Assert.True(l >= dr + PanelFlow.ColumnGap - 0.01f || r <= dl - PanelFlow.ColumnGap + 0.01f, what + ": covers dock " + d);
                    }
                }
            }

            // A single row window is centered on what the docks leave.
            if (avoided)
                Assert.Equal(shift, UiWindowLayout.Place(new PanelSpot(0, new[] { Basic }, false).WithDocks(docks), w, h).X, 3);
        }
    }

    [Fact]
    public void Without_docks_the_row_and_the_flow_are_unchanged()
    {
        var widths = new[] { Tabbed, Basic, Basic };
        for (int i = 0; i < widths.Length; i++)
        {
            var plain = UiWindowLayout.Place(new PanelSpot(i, widths, false), 1920f, 1080f);
            var empty = UiWindowLayout.Place(new PanelSpot(i, widths, false).WithDocks(Array.Empty<UiDockSpec>()), 1920f, 1080f);
            Assert.Equal(plain.X, empty.X);
            Assert.Equal(plain.Top, empty.Top);
        }

        Assert.Equal(PanelFlow.Offset(new PanelSpot(1, 2), Basic, 1920f, 1080f).X,
            PanelFlow.Offset(new PanelSpot(1, 2).WithDocks(null), Basic, 1920f, 1080f).X);
    }

    [Fact]
    public void Ordinary_flowed_panels_avoid_a_dock_too()
    {
        var docks = new[] { Left() };
        var (dl, dr) = DockRect(docks, 0, 1920f, 1080f);
        var one = PanelFlow.Offset(new PanelSpot(0, 1).WithDocks(docks), Basic, 1920f, 1080f);
        Assert.True(one.X - Basic * 0.5f >= dr + PanelFlow.ColumnGap - 0.01f);
        Assert.Equal(UiDockLayout.FreeArea(docks, 1920f).Shift, one.X, 3);
    }

    [Fact]
    public void A_screen_too_narrow_for_the_rest_ignores_the_dock_rather_than_push_panels_off()
    {
        // 1280 wide: a 640 left dock leaves less than a 640 frame and its margins.
        var docks = new[] { Left() };
        var (width, shift) = UiDockLayout.FreeArea(docks, 1280f);
        Assert.Equal(1280f, width);
        Assert.Equal(0f, shift);
        // The Satisvampory case on 1920: a 640 dock leaves room for one centered frame.
        var (w1080, s1080) = UiDockLayout.FreeArea(docks, 1920f);
        Assert.True(w1080 - 2f * PanelFlow.EdgeMargin >= Basic);
        Assert.True(s1080 > 0f);
    }

    [Fact]
    public void Spots_give_docked_panels_a_dock_spot_and_keep_them_out_of_the_row()
    {
        var shown = new[] { "pane", "dock", "w", "plain", "dock2" };
        var spots = UiWindowLayout.Spots(shown, p => p == "w" ? Basic : 0f, p => p == "pane", p => p == "plain",
            dock: p => p == "dock" ? Left() : p == "dock2" ? Right() : null);
        var d = spots.Single(s => s.Panel == "dock").Spot;
        Assert.True(d.IsDock);
        Assert.False(d.IsWindow);
        Assert.Equal(0, d.DockIndex);
        Assert.True(d.WorkspaceOpen);
        Assert.Equal(1, spots.Single(s => s.Panel == "dock2").Spot.DockIndex);
        var w = spots.Single(s => s.Panel == "w").Spot;
        Assert.True(w.IsWindow);
        Assert.Equal(2, w.Count); // the window and the floating panel; the docks are not in the row
        Assert.Equal(2, w.Docks!.Length);
        Assert.Null(spots.Single(s => s.Panel == "pane").Spot.Docks);
    }

    // ---- the API on a panel ----------------------------------------------------------------------------------------------

    [Fact]
    public void Dock_on_a_panel_leaves_the_row_and_the_others_keep_clear()
    {
        var find = UiApi.Panel("Find", _owner, "example.mod").GameWindow(640f);
        var other = UiApi.Panel("Other", _owner, "example.mod");
        Assert.Same(find, find.Dock(UiDock.Left, 24f));
        Assert.Equal(UiDock.Left, find.DockSide());
        find.Show();
        other.Show();
        var dockSpot = _runtime.SpotFor("Find");
        Assert.True(dockSpot.IsDock);
        Assert.Equal(24f, dockSpot.Docks![0].Margin);
        var otherSpot = _runtime.SpotFor("Other");
        Assert.False(otherSpot.IsDock);
        Assert.Equal(1, otherSpot.Count);
        Assert.NotNull(otherSpot.Docks);

        // Undocking re-flows it with the others.
        find.Dock(UiDock.None);
        Assert.Equal(UiDock.None, find.DockSide());
        Assert.False(_runtime.SpotFor("Find").IsDock);
        Assert.Equal(2, _runtime.SpotFor("Other").Count);
    }

    [Fact]
    public void A_docked_game_window_reports_the_width_it_is_drawn_at()
    {
        var wide = (UiPanelHandle)UiApi.Panel("Wide", _owner, "example.mod").GameWindow(880f);
        wide.Dock(UiDock.Right);
        Assert.Equal(880f, UiDockLayout.FrameWidth(wide.EffectiveDock!.Value, 1920f));
        wide.Dock(UiDock.Right, new UiDockOptions { WidthFraction = 0.4f });
        Assert.Equal(768f, UiDockLayout.FrameWidth(wide.EffectiveDock!.Value, 1920f));
        // An ordinary kit panel keeps the 640 frame whatever it asks.
        var plain = (UiPanelHandle)UiApi.Panel("Plain", _owner, "example.mod");
        plain.Dock(UiDock.Left, new UiDockOptions { Width = 900f });
        Assert.Equal(Basic, UiDockLayout.FrameWidth(plain.EffectiveDock!.Value, 1920f));
        Assert.Throws<ArgumentOutOfRangeException>(() => plain.Dock(UiDock.Left, new UiDockOptions { Margin = -3f }));
    }
}
