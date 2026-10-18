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

    // ---- top and bottom strips ---------------------------------------------------------------------------------------

    private static UiDockSpec Top(float margin = 16f, UiDockAlign align = UiDockAlign.Center, float offset = 0f, float width = 0f) =>
        new(UiDock.Top, align, margin, width, 0f, offset);

    private static UiDockSpec Bottom(float margin = 16f, UiDockAlign align = UiDockAlign.Center, float offset = 0f, float width = 0f) =>
        new(UiDock.Bottom, align, margin, width, 0f, offset);

    /// <summary>16:9 through 32:9 on the canvas reference height, plus a 1440 canvas.</summary>
    public static IEnumerable<object[]> Aspects() => new[]
    {
        new object[] { 1920f, 1080f },   // 16:9
        new object[] { 2560f, 1080f },   // 21:9 (2560x1080)
        new object[] { 2520f, 1080f },   // 21:9 (3440x1440 on a height-matched canvas)
        new object[] { 3840f, 1080f },   // 32:9
        new object[] { 2560f, 1440f },
    };

    [Theory]
    [MemberData(nameof(Aspects))]
    public void A_top_strip_hangs_its_margin_below_the_top_and_a_bottom_strip_stands_its_margin_above_the_bottom(float w, float h)
    {
        foreach (float margin in new[] { 0f, 16f, 60f })
        {
            var top = UiDockLayout.PlaceAlone(Top(margin), w, h);
            var bottom = UiDockLayout.PlaceAlone(Bottom(margin), w, h);
            foreach (float height in new[] { 200f, 320f, float.MaxValue })
            {
                float drawn = Math.Min(height, top.MaxHeight);
                Assert.Equal(h * 0.5f - margin, top.CenterFor(height).Y + drawn * 0.5f, 3);
                drawn = Math.Min(height, bottom.MaxHeight);
                Assert.Equal(-h * 0.5f + margin, bottom.CenterFor(height).Y - drawn * 0.5f, 3);
            }

            // At its tallest a strip stays on the screen.
            var tallest = top.CenterFor(float.MaxValue);
            Assert.True(tallest.Y - top.MaxHeight * 0.5f >= -h * 0.5f - 0.01f);
        }
    }

    [Theory]
    [MemberData(nameof(Aspects))]
    public void A_strip_aligns_start_center_or_end_along_its_edge(float w, float h)
    {
        foreach (var make in new Func<UiDockAlign, UiDockSpec>[] { a => Top(align: a), a => Bottom(align: a) })
        {
            Assert.Equal(-w * 0.5f + PanelFlow.EdgeMargin + Basic * 0.5f, UiDockLayout.PlaceAlone(make(UiDockAlign.Start), w, h).X, 3);
            Assert.Equal(0f, UiDockLayout.PlaceAlone(make(UiDockAlign.Center), w, h).X, 3);
            Assert.Equal(w * 0.5f - PanelFlow.EdgeMargin - Basic * 0.5f, UiDockLayout.PlaceAlone(make(UiDockAlign.End), w, h).X, 3);
        }
    }

    [Fact]
    public void A_strip_offset_moves_it_along_the_edge_and_stays_on_screen()
    {
        Assert.Equal(-300f, UiDockLayout.PlaceAlone(Top(offset: -300f), 1920f, 1080f).X, 3);
        Assert.Equal(-1920f + 16f + Basic * 0.5f + 100f, UiDockLayout.PlaceAlone(Top(align: UiDockAlign.Start, offset: 100f), 3840f, 1080f).X, 3);
        // Pushed past the edge: clamped against it.
        Assert.Equal(960f - Basic * 0.5f, UiDockLayout.PlaceAlone(Bottom(offset: 1900f), 1920f, 1080f).X, 3);
        Assert.Equal(-960f + Basic * 0.5f, UiDockLayout.PlaceAlone(Bottom(align: UiDockAlign.Start, offset: -500f), 1920f, 1080f).X, 3);
    }

    [Fact]
    public void Strips_with_one_edge_and_alignment_line_up_along_it()
    {
        // Two centered top strips: one row centered on the screen, a column gap apart.
        var centered = new[] { Top(), Top(width: 800f) };
        var a = UiDockLayout.Place(centered, 0, 3840f, 1080f, Basic);
        var b = UiDockLayout.Place(centered, 1, 3840f, 1080f, 800f);
        Assert.Equal(PanelFlow.ColumnGap, (b.X - 400f) - (a.X + Basic * 0.5f), 3);
        Assert.Equal(0f, ((a.X - Basic * 0.5f) + (b.X + 400f)) * 0.5f, 3);

        // Start strips go rightward from the left margin; End strips leftward from the right margin.
        var starts = new[] { Top(align: UiDockAlign.Start), Top(align: UiDockAlign.Start) };
        Assert.Equal(-1920f + 16f + Basic * 1.5f + PanelFlow.ColumnGap, UiDockLayout.Place(starts, 1, 3840f, 1080f).X, 3);
        var ends = new[] { Bottom(align: UiDockAlign.End), Bottom(align: UiDockAlign.End) };
        Assert.Equal(1920f - 16f - Basic * 0.5f, UiDockLayout.Place(ends, 0, 3840f, 1080f).X, 3);
        Assert.Equal(1920f - 16f - Basic * 1.5f - PanelFlow.ColumnGap, UiDockLayout.Place(ends, 1, 3840f, 1080f).X, 3);

        // Other edges, other alignments and the side docks do not move a strip.
        var alone = UiDockLayout.PlaceAlone(Top(), 1920f, 1080f);
        var mixed = new[] { Left(), Bottom(), Top(align: UiDockAlign.Start), Top(), Right() };
        var inMix = UiDockLayout.Place(mixed, 3, 1920f, 1080f);
        Assert.Equal(alone.X, inMix.X, 3);
        Assert.Equal(alone.Top, inMix.Top, 3);
        Assert.Equal(alone.Bottom, inMix.Bottom, 3);
    }

    [Fact]
    public void Strips_leave_the_side_docks_the_window_row_and_the_flow_where_they_were()
    {
        // A strip reserves no width: the others are laid out exactly as without it.
        Assert.Equal((1920f, 0f), UiDockLayout.FreeArea(new[] { Top(), Bottom() }, 1920f));
        var sideOnly = new[] { Left() };
        var withStrip = new[] { Top(), Left(), Bottom(align: UiDockAlign.End) };
        Assert.Equal(UiDockLayout.FreeArea(sideOnly, 2560f), UiDockLayout.FreeArea(withStrip, 2560f));
        Assert.Equal(UiDockLayout.Place(sideOnly, 0, 2560f, 1080f).X, UiDockLayout.Place(withStrip, 1, 2560f, 1080f).X, 3);
        var row = new[] { Basic, Basic };
        Assert.Equal(UiWindowLayout.Place(new PanelSpot(1, row, false).WithDocks(sideOnly), 2560f, 1080f).X,
            UiWindowLayout.Place(new PanelSpot(1, row, false).WithDocks(withStrip), 2560f, 1080f).X, 3);
        Assert.Equal(PanelFlow.Offset(new PanelSpot(0, 1), Basic, 1920f, 1080f).X,
            PanelFlow.Offset(new PanelSpot(0, 1).WithDocks(new[] { Top() }), Basic, 1920f, 1080f).X, 3);
    }

    [Fact]
    public void A_top_strip_stays_under_the_workspace_tab_strip()
    {
        float strip = UiWindowLayout.WorkspaceStripBottom(default, 1920f, 1080f);
        var under = UiDockLayout.PlaceAlone(Top(margin: 0f), 1920f, 1080f, stripBottom: strip);
        var placed = under.CenterFor(240f);
        Assert.Equal(strip - UiWindowLayout.StripClearance, placed.Y + 120f, 3);
    }

    [Theory]
    [MemberData(nameof(Aspects))]
    public void The_container_gap_recipe_stays_on_the_center_line_and_follows_a_resize(float w, float h)
    {
        // The recommended placement for a strip over the container window's center gap: a centered top (or bottom)
        // dock. The game's container menu sets its two 620-wide halves 448 apart in one centered row (read from the shipped
        // prefab; the on-screen centering is still to be checked live), so the gap's center line is x 0 on every aspect.
        // The strip's center stays there, and its vertical place does not depend on the canvas width.
        var recipe = Top(margin: 24f);
        var here = UiDockLayout.PlaceAlone(recipe, w, h);
        var at169 = UiDockLayout.PlaceAlone(recipe, 1920f, h);
        Assert.Equal(0f, here.X, 3);
        Assert.Equal(at169.Top, here.Top, 3);
        Assert.Equal(at169.MaxHeight, here.MaxHeight, 3);
        Assert.Equal(0f, UiDockLayout.PlaceAlone(Bottom(margin: 24f), w, h).X, 3);
    }

    [Fact]
    public void Side_docks_keep_their_alignments_and_gain_start_and_end()
    {
        foreach (float h in new[] { 720f, 1080f, 1440f })
        {
            var top = UiDockLayout.PlaceAlone(Left(align: UiDockAlign.Top), 1920f, h);
            var start = UiDockLayout.PlaceAlone(Left(align: UiDockAlign.Start), 1920f, h);
            Assert.Equal(top.CenterFor(300f).Y, start.CenterFor(300f).Y, 3);
            Assert.Equal(top.X, start.X, 3);
            var end = UiDockLayout.PlaceAlone(Right(align: UiDockAlign.End), 1920f, h);
            Assert.Equal(-h * 0.5f + PanelFlow.EdgeMargin, end.CenterFor(300f).Y - 150f, 3);
            Assert.Equal(0f, UiDockLayout.PlaceAlone(Right(), 1920f, h).CenterFor(300f).Y, 3);
        }
    }

    [Fact]
    public void Strip_options_are_checked_against_the_edge()
    {
        Assert.Null(UiDockLayout.Refuse(UiDock.Top, new UiDockOptions { Align = UiDockAlign.Start, Offset = -200f }));
        Assert.Null(UiDockLayout.Refuse(UiDock.Bottom, new UiDockOptions { Align = UiDockAlign.End }));
        Assert.Null(UiDockLayout.Refuse(UiDock.Left, new UiDockOptions { Align = UiDockAlign.End }));
        // Top is a side dock's word; a strip says Start.
        Assert.NotNull(UiDockLayout.Refuse(UiDock.Top, new UiDockOptions { Align = UiDockAlign.Top }));
        Assert.NotNull(UiDockLayout.Refuse(UiDock.Left, new UiDockOptions { Offset = 10f }));
        Assert.NotNull(UiDockLayout.Refuse(UiDock.Top, new UiDockOptions { Offset = float.NaN }));
        Assert.NotNull(UiDockLayout.Refuse(UiDock.Top, new UiDockOptions { Offset = 5000f }));
        Assert.NotNull(UiDockLayout.Refuse(UiDock.Bottom, new UiDockOptions { Margin = 500f }));
    }

    [Fact]
    public void A_panel_docked_to_the_top_gets_a_dock_spot_and_the_others_keep_their_row()
    {
        var strip = UiApi.Panel("Strip", _owner, "example.mod").GameWindow();
        var other = UiApi.Panel("Other", _owner, "example.mod");
        strip.Dock(UiDock.Top, new UiDockOptions { Margin = 24f, Align = UiDockAlign.Center, Offset = 12f });
        Assert.Equal(UiDock.Top, strip.DockSide());
        strip.Show();
        other.Show();
        var spot = _runtime.SpotFor("Strip");
        Assert.True(spot.IsDock);
        Assert.Equal(UiDock.Top, spot.Docks![0].Side);
        Assert.Equal(12f, spot.Docks[0].Offset);
        var otherSpot = _runtime.SpotFor("Other");
        Assert.Equal(1, otherSpot.Count);
        Assert.Equal(PanelFlow.Offset(new PanelSpot(0, 1), Basic, 1920f, 1080f).X, PanelFlow.Offset(otherSpot, Basic, 1920f, 1080f).X, 3);
        Assert.Throws<ArgumentOutOfRangeException>(() => strip.Dock(UiDock.Bottom, new UiDockOptions { Align = UiDockAlign.Top }));
        Assert.Equal(UiDock.Top, strip.DockSide());
    }
}
