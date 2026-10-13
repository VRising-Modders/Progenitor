using Progenitor.Clock;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// The ownership marker behind <c>ctx.Time</c>: a world autosaved during a hold must not leave its
/// day/night cycle stopped after a restart, and the first override after that restart must restore
/// the value from before the original override, not the saved "stopped".
/// </summary>
public class TimeOfDayPolicyTests
{
    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("TRUE", true)]
    public void Marker_round_trips(string text, bool value)
    {
        Assert.Equal(value, TimeOfDayPolicy.Decode(text));
        Assert.Equal(value, TimeOfDayPolicy.Decode(TimeOfDayPolicy.Encode(value)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("yes")]
    public void Anything_else_is_no_marker(string? text) => Assert.Null(TimeOfDayPolicy.Decode(text));

    [Fact]
    public void Autosave_during_a_hold_then_restart_restores_the_prior_value()
    {
        // Prior false, hold set the flag, the save kept flag=true and the marker; restart, no override.
        Assert.Equal(TimeOfDayPolicy.Recovery.RestoreAndClear, TimeOfDayPolicy.Decide(false, overrideLive: false, flagNow: true));
    }

    [Fact]
    public void A_marker_whose_prior_already_holds_is_just_cleared()
    {
        Assert.Equal(TimeOfDayPolicy.Recovery.ClearMarker, TimeOfDayPolicy.Decide(false, false, false));
        Assert.Equal(TimeOfDayPolicy.Recovery.ClearMarker, TimeOfDayPolicy.Decide(true, false, true));
    }

    [Fact]
    public void An_admin_stop_before_the_first_override_is_restored_as_stopped()
    {
        Assert.Equal(TimeOfDayPolicy.Recovery.RestoreAndClear, TimeOfDayPolicy.Decide(true, false, false));
    }

    [Fact]
    public void No_marker_or_a_live_override_means_no_recovery()
    {
        Assert.Equal(TimeOfDayPolicy.Recovery.None, TimeOfDayPolicy.Decide(null, false, true));
        Assert.Equal(TimeOfDayPolicy.Recovery.None, TimeOfDayPolicy.Decide(false, overrideLive: true, flagNow: true));
    }

    [Fact]
    public void The_first_override_after_a_restart_uses_the_markers_prior()
    {
        // The loaded flag is true only because a save kept an earlier hold's "stopped".
        Assert.False(TimeOfDayPolicy.PriorForFirstTake(markerPrior: false, flagNow: true));
        // No marker: the flag as it is (an admin's own stop is kept).
        Assert.True(TimeOfDayPolicy.PriorForFirstTake(null, true));
        Assert.False(TimeOfDayPolicy.PriorForFirstTake(null, false));
    }

    [Theory]
    [InlineData(0, false, false)]   // server boot, save not loaded yet
    [InlineData(1, false, true)]    // load completed in this Core generation
    [InlineData(0, true, true)]     // Core hot-swapped into a running world
    public void Overrides_wait_for_the_world_load(int loads, bool runningWorld, bool allowed) =>
        Assert.Equal(allowed, TimeOfDayPolicy.WorldLoadComplete(loads, runningWorld));
}
