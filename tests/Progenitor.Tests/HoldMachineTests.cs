using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// HOLD1: Core's shared press-and-hold machine (<see cref="HoldMachine"/>) and ring math
/// (<see cref="HoldRingMath"/>), as Core's Dye all and the debug tools' map teleport drive them. Dye all's own
/// press rule is covered in <c>DyeAllTests</c>.
/// </summary>
public class HoldMachineTests
{
    [Fact]
    public void Hold_fills_then_fires_once_then_waits_for_release()
    {
        var m = new HoldMachine();
        Assert.True(m.Press(0, accept: true, holdSeconds: 1.0, now: 5.0));

        var quarter = m.Tick(5.25, targetVisible: true, enabled: true);
        Assert.Equal(HoldPhase.Charging, quarter.Phase);
        Assert.Equal(0.25f, quarter.Progress, 3);
        Assert.False(quarter.Fire);

        var done = m.Tick(6.0, true, true);
        Assert.True(done.Fire);
        Assert.Equal(1f, done.Progress);
        Assert.True(m.Fired);

        var after = m.Tick(6.5, true, true);
        Assert.False(after.Fire);
        Assert.Equal(HoldPhase.Completed, after.Phase);

        m.Release(0);
        var idle = m.Tick(6.6, true, true);
        Assert.Equal(HoldPhase.Idle, idle.Phase);
        Assert.Equal(HoldCancel.None, idle.Cancelled);
        Assert.Equal(-1, m.Target);
    }

    [Theory]
    [InlineData(HoldCancel.Released)]
    [InlineData(HoldCancel.Left)]
    public void Early_release_or_leaving_cancels_without_firing(HoldCancel how)
    {
        var m = new HoldMachine();
        m.Press(0, true, 1.0, 0.0);
        m.Tick(0.5, true, true);
        if (how == HoldCancel.Released) m.Release(0); else m.Leave(0);

        var step = m.Tick(0.6, true, true);
        Assert.Equal(HoldPhase.Idle, step.Phase);
        Assert.Equal(how, step.Cancelled);
        Assert.False(step.Fire);
        // Reported once, then quiet; the elapsed time that would have filled the ring fires nothing.
        Assert.Equal(HoldCancel.None, m.Tick(2.0, true, true).Cancelled);
        Assert.False(m.Tick(3.0, true, true).Fire);
    }

    [Fact]
    public void Surface_closing_feature_off_and_unload_each_cancel()
    {
        var m = new HoldMachine();
        m.Press(0, true, 1.0, 0.0);
        Assert.Equal(HoldCancel.Closed, m.Tick(0.2, targetVisible: false, enabled: true).Cancelled);

        m.Press(0, true, 1.0, 1.0);
        Assert.Equal(HoldCancel.Disabled, m.Tick(1.2, true, enabled: false).Cancelled);

        m.Press(0, true, 1.0, 2.0);
        m.Cancel(HoldCancel.Unloaded);
        Assert.Equal(HoldCancel.Unloaded, m.Tick(2.1, true, true).Cancelled);

        m.Press(0, true, 1.0, 3.0);
        m.Cancel(HoldCancel.None); // an unspecified cancel reads as Closed
        Assert.Equal(HoldCancel.Closed, m.Tick(3.1, true, true).Cancelled);
    }

    [Fact]
    public void Refused_press_second_press_and_other_targets_are_ignored()
    {
        var m = new HoldMachine();
        Assert.False(m.Press(0, accept: false, 1.0, 0.0));
        Assert.False(m.Press(-1, accept: true, 1.0, 0.0));
        Assert.Equal(HoldPhase.Idle, m.Phase);

        Assert.True(m.Press(1, true, 1.0, 0.0));
        Assert.False(m.Press(2, true, 1.0, 0.1));
        m.Release(2);
        m.Leave(2);
        Assert.Equal(HoldPhase.Charging, m.Tick(0.5, true, true).Phase);
        Assert.Equal(1, m.Target);
    }

    [Fact]
    public void Leaving_after_the_fire_does_not_cancel_and_a_closed_surface_ends_the_wait()
    {
        var m = new HoldMachine();
        m.Press(0, true, 1.0, 0.0);
        Assert.True(m.Tick(1.0, true, true).Fire);
        m.Leave(0);
        Assert.Equal(HoldPhase.Completed, m.Tick(1.1, true, true).Phase);
        var closed = m.Tick(1.2, targetVisible: false, enabled: true);
        Assert.Equal(HoldPhase.Idle, closed.Phase);
        Assert.Equal(HoldCancel.None, closed.Cancelled);
    }

    [Theory]
    [InlineData(0.0, HoldMachine.MinHoldSeconds)]
    [InlineData(99.0, HoldMachine.MaxHoldSeconds)]
    [InlineData(double.NaN, HoldMachine.DefaultHoldSeconds)]
    [InlineData(double.NegativeInfinity, HoldMachine.DefaultHoldSeconds)]
    [InlineData(1.5, 1.5)]
    public void Hold_time_is_clamped(double configured, double expected)
    {
        Assert.Equal(expected, HoldMachine.ClampHold(configured));
        var m = new HoldMachine();
        m.Press(0, true, configured, 0.0);
        Assert.Equal(expected, m.HoldSeconds);
    }

    [Fact]
    public void Ring_texture_is_a_band_and_the_pulse_grows_and_fades()
    {
        int n = HoldRingMath.TextureSize;
        Assert.Equal(0f, HoldRingMath.Alpha(n / 2, n / 2, n));
        Assert.Equal(0f, HoldRingMath.Alpha(0, 0, n));
        float mid = (n / 2f - 1f) * (1f + HoldRingMath.InnerFraction) / 2f;
        Assert.Equal(1f, HoldRingMath.Alpha(n / 2 + (int)mid, n / 2, n), 3);

        Assert.Equal((1f, 1f, false), HoldRingMath.Pulse(0));
        var half = HoldRingMath.Pulse(HoldRingMath.PulseSeconds / 2);
        Assert.True(half.Scale > 1f && half.Alpha < 1f && !half.Done);
        Assert.True(HoldRingMath.Pulse(HoldRingMath.PulseSeconds).Done);
    }
}
