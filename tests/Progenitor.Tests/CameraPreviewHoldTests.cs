using System;
using Progenitor.Cameras;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Game-free coverage of the held camera preview and the renewable server observation lease
/// (<c>CameraPreviewPolicy.cs</c>): the timeout and hold clock, every safety end and its order, the Escape
/// precedence, and lease renewal and expiry. The game reads that feed the samples are client-only.
/// </summary>
public class CameraPreviewHoldTests
{
    private static CameraPreviewSample Fine(float health = 100, float moved = 0) => new()
    {
        CameraOwned = true, SameWorld = true, CharacterRead = true, Health = health, MovedMeters = moved,
    };

    // ---- client preview: time ---------------------------------------------------------------

    [Fact]
    public void A_timed_preview_ends_on_its_seconds()
    {
        var p = new CameraPreviewPolicy(5, hold: false, now: 0);
        Assert.Equal(CameraPreviewEndReason.None, p.Decide(4.9, Fine()));
        Assert.Equal(CameraPreviewEndReason.Timeout, p.Decide(5.0, Fine()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(10.01)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Timed_seconds_outside_the_range_are_refused(double seconds) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new CameraPreviewPolicy(seconds, hold: false, now: 0));

    [Fact]
    public void Hold_removes_the_short_timeout_and_keeps_running_until_the_hold_limit()
    {
        var p = new CameraPreviewPolicy(5, hold: false, now: 0);
        Assert.Equal(CameraPreviewEndReason.None, p.Decide(3, Fine()));
        p.Hold(3);
        Assert.True(p.IsHeld);
        Assert.Equal(CameraPreviewEndReason.None, p.Decide(6, Fine()));
        Assert.Equal(CameraPreviewEndReason.None, p.Decide(3 + CameraPreviewPolicy.MaxHoldSeconds - 0.1, Fine()));
        Assert.Equal(CameraPreviewEndReason.HoldLimit, p.Decide(3 + CameraPreviewPolicy.MaxHoldSeconds, Fine()));
    }

    [Fact]
    public void Holding_again_restarts_the_hold_limit()
    {
        var p = new CameraPreviewPolicy(5, hold: true, now: 0);
        p.Hold(500);
        Assert.Equal(CameraPreviewEndReason.None, p.Decide(CameraPreviewPolicy.MaxHoldSeconds + 1, Fine()));
        Assert.Equal(CameraPreviewEndReason.HoldLimit, p.Decide(500 + CameraPreviewPolicy.MaxHoldSeconds, Fine()));
    }

    [Fact]
    public void Starting_held_never_times_out_at_the_timed_length()
    {
        var p = new CameraPreviewPolicy(CameraPreviewPolicy.MaxTimedSeconds, hold: true, now: 0);
        Assert.True(p.IsHeld);
        Assert.Equal(CameraPreviewEndReason.None, p.Decide(60, Fine()));
    }

    // ---- client preview: safety ends ---------------------------------------------------------

    [Fact]
    public void Losing_health_ends_the_preview_but_regeneration_does_not()
    {
        var p = new CameraPreviewPolicy(5, hold: true, now: 0);
        Assert.Equal(CameraPreviewEndReason.None, p.Decide(1, Fine(100)));
        Assert.Equal(CameraPreviewEndReason.None, p.Decide(2, Fine(101)));
        Assert.Equal(CameraPreviewEndReason.None, p.Decide(3, Fine(101.005f)));
        Assert.Equal(CameraPreviewEndReason.Damaged, p.Decide(4, Fine(99)));
    }

    [Fact]
    public void The_first_sample_sets_the_health_baseline()
    {
        var p = new CameraPreviewPolicy(5, hold: true, now: 0);
        Assert.Equal(CameraPreviewEndReason.None, p.Decide(1, Fine(40)));
    }

    [Fact]
    public void Death_ends_the_preview_ahead_of_damage()
    {
        var p = new CameraPreviewPolicy(5, hold: true, now: 0);
        p.Decide(1, Fine(100));
        var dead = Fine(0);
        Assert.Equal(CameraPreviewEndReason.Died, p.Decide(2, dead));
        var flagged = Fine(50);
        flagged.Dead = true;
        Assert.Equal(CameraPreviewEndReason.Died, new CameraPreviewPolicy(5, true, 0).Decide(1, flagged));
    }

    [Fact]
    public void Moving_past_the_limit_ends_the_preview()
    {
        var p = new CameraPreviewPolicy(5, hold: true, now: 0);
        Assert.Equal(CameraPreviewEndReason.None, p.Decide(1, Fine(moved: CameraPreviewPolicy.MoveLimitMeters)));
        Assert.Equal(CameraPreviewEndReason.Moved, p.Decide(2, Fine(moved: CameraPreviewPolicy.MoveLimitMeters + 0.01f)));
    }

    [Fact]
    public void Losing_the_camera_or_the_world_ends_the_preview_first()
    {
        var p = new CameraPreviewPolicy(5, hold: true, now: 0);
        var s = Fine(0, 50);
        s.CameraOwned = false;
        Assert.Equal(CameraPreviewEndReason.CameraTaken, p.Decide(1, s));
        s.CameraOwned = true; s.SameWorld = false;
        Assert.Equal(CameraPreviewEndReason.WorldChanged, p.Decide(1, s));
        s.SameWorld = true; s.CharacterRead = false;
        Assert.Equal(CameraPreviewEndReason.WorldChanged, p.Decide(1, s));
    }

    [Fact]
    public void Unreadable_values_end_the_preview_rather_than_leave_it_running()
    {
        var p = new CameraPreviewPolicy(5, hold: true, now: 0);
        Assert.Equal(CameraPreviewEndReason.Died, p.Decide(1, Fine(float.NaN)));
        Assert.Equal(CameraPreviewEndReason.Failed, p.Decide(1, Fine(float.PositiveInfinity)));
        Assert.Equal(CameraPreviewEndReason.Failed, p.Decide(1, Fine(100, float.NaN)));
    }

    // ---- client preview: Escape --------------------------------------------------------------

    [Theory]
    [InlineData(true, false, false, false, (int)CameraPreviewEscapeOwner.Dialog)]
    [InlineData(false, true, false, false, (int)CameraPreviewEscapeOwner.KeyCapture)]
    [InlineData(false, false, true, false, (int)CameraPreviewEscapeOwner.Chat)]
    [InlineData(false, false, false, true, (int)CameraPreviewEscapeOwner.GameMenu)]
    [InlineData(false, false, false, false, (int)CameraPreviewEscapeOwner.Preview)]
    [InlineData(true, true, true, true, (int)CameraPreviewEscapeOwner.Dialog)]
    public void Escape_ends_the_preview_only_when_nothing_above_it_takes_the_press(
        bool dialog, bool capture, bool chat, bool menu, int expectedOwner)
    {
        var expected = (CameraPreviewEscapeOwner)expectedOwner;
        var owner = CameraPreviewEscape.Decide(true, dialog, capture, chat, menu);
        Assert.Equal(expected, owner);
        Assert.Equal(expected == CameraPreviewEscapeOwner.Preview, CameraPreviewEscape.HoldsCloseKey(owner));
    }

    [Fact]
    public void With_no_preview_Escape_is_not_ours()
    {
        var owner = CameraPreviewEscape.Decide(false, false, false, false, false);
        Assert.Equal(CameraPreviewEscapeOwner.NoPreview, owner);
        Assert.False(CameraPreviewEscape.HoldsCloseKey(owner));
    }

    [Fact]
    public void Options_default_to_the_original_timed_preview_and_lease()
    {
        var preview = new CameraPreviewOptions();
        Assert.Equal(5, preview.Seconds);
        Assert.False(preview.Hold);
        Assert.Equal(-1, preview.CompressedHeight);
        var observe = new CameraObserveOptions();
        Assert.Equal(8, observe.Seconds);
        Assert.False(observe.Hold);
    }

    // ---- server lease ------------------------------------------------------------------------

    private static CameraObservationSample Here(float moved = 0) => new()
    {
        UserConnected = true, OriginOurs = true, CharacterRead = true, MovedMeters = moved,
    };

    [Fact]
    public void A_timed_lease_expires_without_renewal()
    {
        var p = new CameraObservationPolicy(8, hold: false, now: 0);
        Assert.Equal(CameraObservationEndReason.None, p.Decide(7.9, Here()));
        Assert.Equal(CameraObservationEndReason.Expired, p.Decide(8, Here()));
    }

    [Fact]
    public void Renew_moves_the_deadline_from_now_and_can_shorten_it()
    {
        var p = new CameraObservationPolicy(8, hold: false, now: 0);
        p.Renew(5, 30);
        Assert.Equal(35, p.Deadline);
        Assert.True(p.IsHeld);
        Assert.Equal(CameraObservationEndReason.None, p.Decide(34, Here()));
        p.Renew(34, 2);
        Assert.False(p.IsHeld);
        Assert.Equal(CameraObservationEndReason.Expired, p.Decide(36, Here()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(600.5)]
    [InlineData(double.NaN)]
    public void Renewals_outside_the_cap_are_refused(double seconds)
    {
        var p = new CameraObservationPolicy(8, hold: false, now: 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => p.Renew(1, seconds));
        Assert.Equal(8, p.Deadline);
    }

    [Fact]
    public void Hold_grants_the_full_cap_and_still_expires()
    {
        var p = new CameraObservationPolicy(8, hold: true, now: 10);
        Assert.Equal(10 + CameraObservationPolicy.MaxRenewSeconds, p.Deadline);
        Assert.Equal(CameraObservationEndReason.None, p.Decide(10 + CameraObservationPolicy.MaxRenewSeconds - 1, Here()));
        Assert.Equal(CameraObservationEndReason.Expired, p.Decide(10 + CameraObservationPolicy.MaxRenewSeconds, Here()));
    }

    [Fact]
    public void A_lease_ends_on_disconnect_supersession_or_leaving_the_area()
    {
        var p = new CameraObservationPolicy(8, hold: true, now: 0);
        var s = Here(100);
        s.UserConnected = false;
        Assert.Equal(CameraObservationEndReason.Disconnected, p.Decide(1, s));
        s.UserConnected = true; s.OriginOurs = false;
        Assert.Equal(CameraObservationEndReason.Superseded, p.Decide(1, s));
        s.OriginOurs = true; s.CharacterRead = false;
        Assert.Equal(CameraObservationEndReason.Disconnected, p.Decide(1, s));
        Assert.Equal(CameraObservationEndReason.None, p.Decide(1, Here(CameraObservationPolicy.LeaveDistanceMeters)));
        Assert.Equal(CameraObservationEndReason.LeftArea, p.Decide(1, Here(CameraObservationPolicy.LeaveDistanceMeters + 0.1f)));
        Assert.Equal(CameraObservationEndReason.Failed, p.Decide(1, Here(float.NaN)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10.5)]
    public void Timed_lease_seconds_outside_the_range_are_refused(double seconds) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new CameraObservationPolicy(seconds, hold: false, now: 0));
}
