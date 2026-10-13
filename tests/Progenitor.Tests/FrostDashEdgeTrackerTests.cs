using Progenitor.Sample;
using Xunit;

namespace Progenitor.Tests;

public class FrostDashEdgeTrackerTests
{
    [Fact]
    public void Repeated_samples_emit_one_pair_per_dash()
    {
        var tracker = new FrostDashEdgeTracker(false);
        bool[] samples = { false, false, true, true, true, false, false, true, true, false };
        FrostDashEdge[] expected = {
            FrostDashEdge.None, FrostDashEdge.None, FrostDashEdge.Start,
            FrostDashEdge.None, FrostDashEdge.None, FrostDashEdge.End,
            FrostDashEdge.None, FrostDashEdge.Start, FrostDashEdge.None, FrostDashEdge.End
        };
        for (int i = 0; i < samples.Length; i++)
            Assert.Equal(expected[i], tracker.Observe(samples[i]));
    }

    [Fact]
    public void Enabling_mid_dash_ignores_orphan_end_then_accepts_next_complete_dash()
    {
        var tracker = new FrostDashEdgeTracker(true);
        Assert.Equal(FrostDashEdge.None, tracker.Observe(true));
        Assert.Equal(FrostDashEdge.None, tracker.Observe(false));
        Assert.Equal(FrostDashEdge.None, tracker.Observe(false));
        Assert.Equal(FrostDashEdge.Start, tracker.Observe(true));
        Assert.Equal(FrostDashEdge.End, tracker.Observe(false));
    }

    [Fact]
    public void Reset_during_dash_cancels_pending_trailing_effect()
    {
        var tracker = new FrostDashEdgeTracker(false);
        Assert.Equal(FrostDashEdge.Start, tracker.Observe(true));
        tracker.Reset(true);
        Assert.Equal(FrostDashEdge.None, tracker.Observe(true));
        Assert.Equal(FrostDashEdge.None, tracker.Observe(false));
        Assert.Equal(FrostDashEdge.Start, tracker.Observe(true));
        Assert.Equal(FrostDashEdge.End, tracker.Observe(false));
    }

    [Fact]
    public void Reset_after_dash_was_missed_cancels_stale_end()
    {
        var tracker = new FrostDashEdgeTracker(false);
        Assert.Equal(FrostDashEdge.Start, tracker.Observe(true));
        tracker.Reset(false);
        Assert.Equal(FrostDashEdge.None, tracker.Observe(false));
        Assert.Equal(FrostDashEdge.Start, tracker.Observe(true));
        Assert.Equal(FrostDashEdge.End, tracker.Observe(false));
    }

    [Fact]
    public void Separate_players_do_not_complete_each_others_dash()
    {
        var first = new FrostDashEdgeTracker(false);
        var second = new FrostDashEdgeTracker(true);
        Assert.Equal(FrostDashEdge.Start, first.Observe(true));
        Assert.Equal(FrostDashEdge.None, second.Observe(false));
        Assert.Equal(FrostDashEdge.End, first.Observe(false));
        Assert.Equal(FrostDashEdge.None, second.Observe(false));
    }
}
