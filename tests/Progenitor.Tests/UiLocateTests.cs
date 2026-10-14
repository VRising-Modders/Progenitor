using System;
using System.Collections.Generic;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// P7 <c>ctx.Hud.Locate</c>: the game-free half. Screen placement (marker vs edge arrow, clamping with margins at
/// common resolutions and aspects, arrow angle, behind-camera flip), distance label, lifetime, pulse and fade, the cap
/// of eight with the oldest dropped, world-epoch and owner ends, and the drawn art.
/// </summary>
public sealed class UiLocateTests
{
    public static IEnumerable<object[]> Screens => new[]
    {
        new object[] { 1920f, 1080f },
        new object[] { 2560f, 1440f },
        new object[] { 3840f, 2160f },
        new object[] { 3440f, 1440f },
    };

    private const float Margin = 48f;
    private const float Top = 28f;

    [Theory]
    [MemberData(nameof(Screens))]
    public void A_point_in_view_gets_the_marker_where_it_projects(float w, float h)
    {
        var p = UiLocateProjection.Place(w * 0.3f, h * 0.6f, 12f, w, h, Margin, Top);
        Assert.True(p.OnScreen);
        Assert.Equal(w * 0.3f, p.X, 3);
        Assert.Equal(h * 0.6f, p.Y, 3);
    }

    [Theory]
    [MemberData(nameof(Screens))]
    public void Off_screen_points_pin_an_arrow_inside_the_margins_pointing_at_them(float w, float h)
    {
        var cases = new (float x, float y, float expectAngle)[]
        {
            (w * 3f, h * 0.5f, 0f),      // right
            (w * 0.5f, h * 4f, 90f),     // up
            (-w * 2f, h * 0.5f, 180f),   // left
            (w * 0.5f, -h * 3f, 270f),   // down
        };
        foreach (var (x, y, angle) in cases)
        {
            var p = UiLocateProjection.Place(x, y, 10f, w, h, Margin, Top);
            Assert.False(p.OnScreen);
            Assert.Equal(angle, p.AngleDegrees, 2);
            AssertInside(p, w, h);
        }

        // A corner direction: the arrow touches the inset rectangle and keeps the true angle.
        var corner = UiLocateProjection.Place(w * 2f, h * 2f, 10f, w, h, Margin, Top);
        Assert.False(corner.OnScreen);
        AssertInside(corner, w, h);
        double expected = Math.Atan2(h * 1.5, w * 1.5) * 180 / Math.PI;
        Assert.Equal(expected, corner.AngleDegrees, 2);
        bool onRight = Math.Abs(corner.X - (w - Margin)) < 0.01f;
        bool onTop = Math.Abs(corner.Y - (h - Margin - Top)) < 0.01f;
        Assert.True(onRight || onTop);
    }

    [Theory]
    [MemberData(nameof(Screens))]
    public void A_point_just_inside_the_screen_but_inside_the_margin_gets_the_arrow(float w, float h)
    {
        var p = UiLocateProjection.Place(w - 5f, h * 0.5f, 10f, w, h, Margin, Top);
        Assert.False(p.OnScreen);
        Assert.Equal(w - Margin, p.X, 2);
        Assert.Equal(0f, p.AngleDegrees, 2);
    }

    [Theory]
    [MemberData(nameof(Screens))]
    public void A_point_behind_the_camera_is_mirrored_and_always_an_arrow(float w, float h)
    {
        // Behind and projected to the left of center: the target is really to the right-behind; turn right.
        var p = UiLocateProjection.Place(w * 0.3f, h * 0.5f, -5f, w, h, Margin, Top);
        Assert.False(p.OnScreen);
        Assert.Equal(0f, p.AngleDegrees, 2);
        AssertInside(p, w, h);

        // Straight behind (projects onto the center): point down, the way to turn around.
        var straight = UiLocateProjection.Place(w * 0.5f, h * 0.5f, -5f, w, h, Margin, Top);
        Assert.False(straight.OnScreen);
        Assert.Equal(270f, straight.AngleDegrees, 2);
        Assert.Equal(Margin, straight.Y, 2);
    }

    [Fact]
    public void Placement_survives_bad_input()
    {
        var p = UiLocateProjection.Place(float.NaN, float.PositiveInfinity, float.NaN, 0f, -5f, float.NaN);
        Assert.False(p.OnScreen);
        Assert.False(float.IsNaN(p.X) || float.IsNaN(p.Y) || float.IsNaN(p.AngleDegrees));
        // A margin bigger than the screen is capped so the arrow still lands on screen.
        var huge = UiLocateProjection.Place(5000f, 540f, 1f, 1920f, 1080f, 5000f, 5000f);
        Assert.InRange(huge.X, 0f, 1920f);
        Assert.InRange(huge.Y, 0f, 1080f);
    }

    [Theory]
    [InlineData(0.4, "<1 m")]
    [InlineData(14.2, "14 m")]
    [InlineData(14.5, "15 m")]
    [InlineData(999.4, "999 m")]
    [InlineData(999.6, "1.0 km")]
    [InlineData(1234, "1.2 km")]
    [InlineData(-1, "")]
    [InlineData(double.NaN, "")]
    public void Distance_reads_like_the_game(double meters, string expected)
    {
        Assert.Equal(expected, UiLocateLook.Distance(meters));
    }

    [Fact]
    public void The_label_joins_name_and_distance()
    {
        Assert.Equal("Iron chest · 14 m", UiLocateLook.Label("Iron chest", 14));
        Assert.Equal("14 m", UiLocateLook.Label("", 14));
        Assert.Equal("Iron chest", UiLocateLook.Label("Iron chest", double.NaN));
        Assert.Equal("Iron chest · 3 m", UiLocateLook.Label(" Iron\nchest ", 3));
    }

    [Fact]
    public void The_marker_pulses_for_its_first_second_then_holds_still()
    {
        Assert.Equal(1f, UiLocateLook.Scale(0));
        Assert.True(UiLocateLook.Scale(0.25) > 1.05f);
        for (double t = 0; t < 1.0; t += 0.05)
            Assert.InRange(UiLocateLook.Scale(t), 1f, 1f + UiLocateLook.PulseAmplitude);
        Assert.Equal(1f, UiLocateLook.Scale(1.0));
        Assert.Equal(1f, UiLocateLook.Scale(5));
    }

    [Fact]
    public void Pings_fade_in_and_out_and_untimed_pings_never_fade_out()
    {
        Assert.Equal(0f, UiLocateLook.Alpha(0, 10));
        Assert.Equal(1f, UiLocateLook.Alpha(5, 10));
        Assert.InRange(UiLocateLook.Alpha(10 - UiLocateLook.FadeOutSeconds / 2, 10), 0.4f, 0.6f);
        Assert.Equal(0f, UiLocateLook.Alpha(10, 10));
        Assert.Equal(1f, UiLocateLook.Alpha(1e6, double.PositiveInfinity));
    }

    [Theory]
    [InlineData(10, 10)]
    [InlineData(0.2, 1)]
    [InlineData(1e9, UiLocateLook.MaxSeconds)]
    public void Timed_lifetimes_are_clamped(double requested, double expected)
    {
        Assert.Equal(expected, UiLocateLook.Seconds(requested));
    }

    [Fact]
    public void Zero_seconds_means_until_disposed()
    {
        Assert.True(double.IsPositiveInfinity(UiLocateLook.Seconds(0)));
        Assert.True(double.IsPositiveInfinity(UiLocateLook.Seconds(-1)));
    }

    [Fact]
    public void A_ninth_ping_drops_the_oldest()
    {
        var set = new UiLocateSet();
        var ended = new List<UiLocatePingHandle>();
        set.Ended += ended.Add;
        var owner = new object();
        var pings = new List<UiLocatePingHandle>();
        for (int i = 0; i < UiLocateLook.MaxPings; i++)
            pings.Add(set.Add(owner, UiLocateTarget.Point(i, 0, 0), "p" + i, 10, 0, 1));

        var ninth = set.Add(owner, UiLocateTarget.Entity(42, 1), "ninth", 10, 0, 1);
        Assert.Equal(UiLocateLook.MaxPings, set.Count);
        Assert.False(pings[0].Active);
        Assert.Equal("cap", pings[0].EndReason);
        Assert.True(pings[1].Active);
        Assert.True(ninth.Active);
        Assert.Equal(new[] { pings[0] }, ended);
    }

    [Fact]
    public void Sweep_ends_expired_pings_and_pings_from_another_world()
    {
        var set = new UiLocateSet();
        var owner = new object();
        var shortPing = set.Add(owner, UiLocateTarget.Point(0, 0, 0), "", 2, 0, 7);
        var untimed = set.Add(owner, UiLocateTarget.Point(0, 0, 0), "", 0, 0, 7);
        var otherWorld = set.Add(owner, UiLocateTarget.Point(0, 0, 0), "", 60, 0, 6);

        Assert.Equal(1, set.Sweep(1.0, 7)); // only the stale-epoch one
        Assert.Equal("world-changed", otherWorld.EndReason);
        Assert.True(shortPing.Active);

        Assert.Equal(1, set.Sweep(2.0, 7));
        Assert.Equal("expired", shortPing.EndReason);
        Assert.True(untimed.Active);
        Assert.Equal(0, set.Sweep(1e7, 7));
    }

    [Fact]
    public void Dispose_and_owner_unload_end_pings_once()
    {
        var set = new UiLocateSet();
        var ended = new List<UiLocatePingHandle>();
        set.Ended += ended.Add;
        var a = new object();
        var b = new object();
        var a1 = set.Add(a, UiLocateTarget.Point(0, 0, 0), "", 10, 0, 1);
        var a2 = set.Add(a, UiLocateTarget.Point(0, 0, 0), "", 10, 0, 1);
        var b1 = set.Add(b, UiLocateTarget.Point(0, 0, 0), "", 10, 0, 1);

        a1.Dispose();
        a1.Dispose();
        Assert.Equal("disposed", a1.EndReason);
        Assert.Equal(1, set.DropOwner(a));
        Assert.Equal("owner-unloaded", a2.EndReason);
        Assert.True(b1.Active);
        Assert.Equal(2, ended.Count);

        Assert.Equal(1, set.Clear());
        Assert.False(b1.Active);
        Assert.Equal(0, set.Count);
    }

    [Fact]
    public void Inert_pings_are_never_active()
    {
        var inert = UiLocatePingHandle.Inert();
        Assert.False(inert.Active);
        inert.Dispose();
    }

    [Fact]
    public void The_marker_art_is_a_diamond_ring_around_a_filled_diamond()
    {
        int s = UiLocateGlyph.TextureSize;
        Assert.Equal(1f, UiLocateGlyph.MarkerAlpha(s / 2, s / 2));          // filled center
        Assert.Equal(0f, UiLocateGlyph.MarkerAlpha(1, 1));                  // corners are clear (a rotated square)
        Assert.Equal(0f, UiLocateGlyph.MarkerAlpha(s / 2, (int)(s * 0.78f))); // the gap between ring and core
        Assert.Equal(1f, UiLocateGlyph.MarkerAlpha(s / 2, (int)(s * 0.94f))); // the ring near the top point
        // Symmetric under the square's mirrors.
        for (int i = 0; i < s; i += 7)
            Assert.Equal(UiLocateGlyph.MarkerAlpha(i, s / 3), UiLocateGlyph.MarkerAlpha(s - 1 - i, s / 3), 4);
    }

    [Fact]
    public void The_arrow_art_points_right_with_a_notched_back()
    {
        int s = UiLocateGlyph.TextureSize;
        Assert.Equal(1f, UiLocateGlyph.ArrowAlpha((int)(s * 0.8f), s / 2));  // near the tip, on the axis
        Assert.Equal(0f, UiLocateGlyph.ArrowAlpha((int)(s * 0.2f), s / 2));  // the notch on the axis is clear
        Assert.Equal(0f, UiLocateGlyph.ArrowAlpha(s - 2, s - 2));            // corners clear
        Assert.Equal(1f, UiLocateGlyph.ArrowAlpha((int)(s * 0.25f), (int)(s * 0.25f))); // a back barb
        for (int i = 0; i < s; i += 9)
            Assert.Equal(UiLocateGlyph.ArrowAlpha(i, s / 2 - 10), UiLocateGlyph.ArrowAlpha(i, s / 2 + 9), 4);
    }

    [Fact]
    public void Leaving_the_world_ends_every_ping_at_once()
    {
        var set = new UiLocateSet();
        var owner = new object();
        var a = set.Add(owner, UiLocateTarget.Point(0, 0, 0), "", 0, 0, 1);
        var b = set.Add(owner, UiLocateTarget.Entity(3, 1), "", 10, 0, 1);

        Assert.Equal(0, set.Readiness(0.5, worldAlive: true, characterPresent: true));
        Assert.Equal(2, set.Readiness(1.0, worldAlive: false, characterPresent: false));
        Assert.Equal("left-world", a.EndReason);
        Assert.Equal("left-world", b.EndReason);
        Assert.Equal(0, set.Count);
    }

    [Fact]
    public void A_missing_character_ends_pings_only_after_the_grace()
    {
        var set = new UiLocateSet();
        var ping = set.Add(new object(), UiLocateTarget.Point(0, 0, 0), "", 0, 0, 1);

        // A respawn: the character is gone briefly, then back; the ping survives.
        Assert.Equal(0, set.Readiness(1.0, true, false));
        Assert.Equal(0, set.Readiness(1.0 + UiLocateLook.NoCharacterGraceSeconds - 0.1, true, false));
        Assert.Equal(0, set.Readiness(4.5, true, true));
        Assert.True(ping.Active);

        // A kick: the client world stays, the character does not come back.
        Assert.Equal(0, set.Readiness(10.0, true, false));
        Assert.Equal(1, set.Readiness(10.0 + UiLocateLook.NoCharacterGraceSeconds, true, false));
        Assert.Equal("left-world", ping.EndReason);
    }

    [Fact]
    public void Locate_colors_come_from_the_kit_palette()
    {
        Assert.Equal(MenuPalette.PendText, UiLocateLook.Accent);
        Assert.Equal(MenuPalette.ClientText, UiLocateLook.LabelColor);
    }

    private static void AssertInside(UiLocatePlacement p, float w, float h)
    {
        Assert.InRange(p.X, Margin - 0.01f, w - Margin + 0.01f);
        Assert.InRange(p.Y, Margin - 0.01f, h - Margin - Top + 0.01f);
    }
}
