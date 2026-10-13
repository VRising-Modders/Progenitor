using System;
using Progenitor.Clock;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// The game-free rules behind <c>ctx.Time.Freeze</c> / <c>ctx.Time.SlowMotion</c>: accepted scales,
/// the replicated precision, write suppression, and the per-save marker that keeps a world saved
/// during a freeze from coming back frozen.
/// </summary>
public class TimeScalePolicyTests
{
    [Theory]
    [InlineData(0.001f)]
    [InlineData(0.25f)]
    [InlineData(1f)]
    public void Slow_motion_accepts_the_replicated_range(float scale) => TimeScalePolicy.RequireSlowMotion(scale);

    [Theory]
    [InlineData(0f)]
    [InlineData(0.0009f)]
    [InlineData(-0.5f)]
    [InlineData(1.01f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void Slow_motion_refuses_everything_else(float scale) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => TimeScalePolicy.RequireSlowMotion(scale));

    [Theory]
    [InlineData(0.25f, 0.25f)]
    [InlineData(0.12345f, 0.123f)]
    [InlineData(0.0004f, 0f)]
    [InlineData(0.0005f, 0.001f)]
    [InlineData(250f, 100f)]
    [InlineData(-1f, 0f)]
    public void Clients_receive_three_decimals_in_0_to_100(float scale, float wire) =>
        Assert.Equal(wire, TimeScalePolicy.OnTheWire(scale), 5);

    [Fact]
    public void The_smallest_slow_motion_still_reaches_clients_as_moving() =>
        Assert.True(TimeScalePolicy.OnTheWire(TimeScalePolicy.MinSlowMotion) > 0f);

    [Fact]
    public void A_held_value_is_not_rewritten_every_frame()
    {
        Assert.False(TimeScalePolicy.NeedsWrite(0f, 0f));
        Assert.False(TimeScalePolicy.NeedsWrite(0.25f, 0.25f + 5e-5f));
        Assert.True(TimeScalePolicy.NeedsWrite(1f, 0f));
        Assert.True(TimeScalePolicy.NeedsWrite(float.NaN, 1f));
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(0f)]
    [InlineData(0.35f)]
    [InlineData(2.5f)]
    public void Marker_round_trips(float value) =>
        Assert.Equal(value, TimeScalePolicy.Decode(TimeScalePolicy.Encode(value)));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("fast")]
    [InlineData("NaN")]
    [InlineData("-1")]
    [InlineData("101")]
    public void Anything_else_is_no_marker(string? text) => Assert.Null(TimeScalePolicy.Decode(text));

    [Fact]
    public void Marker_is_culture_invariant()
    {
        var saved = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.Equal("0.5", TimeScalePolicy.Encode(0.5f));
            Assert.Equal(0.5f, TimeScalePolicy.Decode("0.5"));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = saved;
        }
    }

    [Fact]
    public void Saved_during_a_freeze_then_restart_restores_the_prior_speed() =>
        Assert.Equal(TimeScalePolicy.Recovery.RestoreAndClear, TimeScalePolicy.Decide(1f, leaseLive: false, now: 0f));

    [Fact]
    public void A_marker_whose_prior_already_holds_is_just_cleared()
    {
        // The game does not keep TimeScale across a restart (or it was already restored): nothing to write.
        Assert.Equal(TimeScalePolicy.Recovery.ClearMarker, TimeScalePolicy.Decide(1f, false, 1f));
        Assert.Equal(TimeScalePolicy.Recovery.ClearMarker, TimeScalePolicy.Decide(0.5f, false, 0.5f));
    }

    [Fact]
    public void No_marker_or_a_live_lease_means_leave_it()
    {
        Assert.Equal(TimeScalePolicy.Recovery.None, TimeScalePolicy.Decide(null, false, 0f));
        Assert.Equal(TimeScalePolicy.Recovery.None, TimeScalePolicy.Decide(1f, true, 0f));
    }

    [Fact]
    public void First_lease_after_a_frozen_save_records_the_original_speed_not_the_frozen_one()
    {
        Assert.Equal(1f, TimeScalePolicy.PriorForFirstTake(1f, now: 0f));
        Assert.Equal(0.5f, TimeScalePolicy.PriorForFirstTake(null, now: 0.5f));
    }

    [Fact]
    public void Leases_stack_newest_first_and_hand_back_on_release()
    {
        var stack = new TimeOfDayOverrides<object>();
        object slow = new(), freeze = new();
        stack.Push(slow);
        stack.Push(freeze);
        Assert.Same(freeze, stack.Current);
        stack.Remove(freeze);
        Assert.Same(slow, stack.Current);
        stack.Remove(slow);
        Assert.Null(stack.Current);
    }

    [Fact]
    public void Reading_says_what_governs()
    {
        Assert.Equal("frozen (Freeze; engine 0, not paused)",
            new TimeScaleReading(0f, false, 0f, TimeScaleOverrideKind.Freeze).ToString());
        Assert.Equal("0.25x (SlowMotion; engine 0.25, not paused)",
            new TimeScaleReading(0.25f, false, 0.25f, TimeScaleOverrideKind.SlowMotion).ToString());
        Assert.Equal("1x (no lease; engine 1, not paused)",
            new TimeScaleReading(1f, false, 1f, TimeScaleOverrideKind.None).ToString());
    }
}
