using System;
using Progenitor.Systems;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Game-free coverage of the default AfterSimulation fallback state machine: do not count
/// until the world exists, reset on WorldReady, engage after N world frames, switch back if
/// SimulationSystemGroup later fires, and keep DescribeLabel honest.
/// </summary>
public class DefaultAnchorWatchTests
{
    private static DefaultAnchorWatch Small(int ticks = 5) => new(ticks);

    [Fact]
    public void Production_tick_count_is_600()
    {
        Assert.Equal(600, DefaultAnchorWatch.FallbackTicks);
    }

    [Fact]
    public void Starts_pending_with_no_frames()
    {
        var w = Small();
        Assert.Equal(DefaultAnchorWatch.LiveAnchor.Pending, w.Live);
        Assert.Equal("pending", w.DescribeLabel());
        Assert.Equal(0, w.FramesAfterWorld);
        Assert.False(w.WorldExists);
        Assert.False(w.GroupFired);
        Assert.False(w.UseFallback);
        Assert.False(w.ReceivedWorldReady);
    }

    [Fact]
    public void Frames_before_the_world_exists_do_not_count_or_engage()
    {
        var w = Small(3);
        for (int i = 0; i < 20; i++)
            Assert.False(w.NotifyFrame(worldExists: false));

        Assert.Equal(0, w.FramesAfterWorld);
        Assert.Equal(DefaultAnchorWatch.LiveAnchor.Pending, w.Live);
        Assert.False(w.UseFallback);
        Assert.False(w.WorldExists);
        Assert.Equal("pending", w.DescribeLabel());
    }

    [Fact]
    public void Seeing_the_world_without_WorldReady_still_counts()
    {
        var w = Small(5);
        Assert.False(w.NotifyFrame(true));
        Assert.Equal(1, w.FramesAfterWorld);
        Assert.True(w.WorldExists);
        Assert.False(w.ReceivedWorldReady);
        Assert.Equal("pending", w.DescribeLabel());
    }

    [Fact]
    public void WorldReady_resets_the_counter()
    {
        var w = Small(10);
        for (int i = 0; i < 7; i++)
            w.NotifyFrame(true);
        Assert.Equal(7, w.FramesAfterWorld);

        w.NotifyWorldReady();

        Assert.Equal(0, w.FramesAfterWorld);
        Assert.True(w.WorldExists);
        Assert.True(w.ReceivedWorldReady);
        Assert.Equal(DefaultAnchorWatch.LiveAnchor.Pending, w.Live);
        Assert.False(w.UseFallback);
    }

    [Fact]
    public void World_disappearing_pauses_the_count()
    {
        var w = Small(20);
        w.NotifyWorldReady();
        for (int i = 0; i < 4; i++)
            w.NotifyFrame(true);

        for (int i = 0; i < 50; i++)
            Assert.False(w.NotifyFrame(false));

        Assert.Equal(4, w.FramesAfterWorld);
        Assert.False(w.WorldExists);
        Assert.Equal(DefaultAnchorWatch.LiveAnchor.Pending, w.Live);
    }

    [Fact]
    public void Fallback_engages_after_N_world_frames_without_the_group()
    {
        var w = Small(3);
        w.NotifyWorldReady();
        Assert.False(w.NotifyFrame(true));
        Assert.False(w.NotifyFrame(true));
        Assert.True(w.NotifyFrame(true));

        Assert.Equal(DefaultAnchorWatch.LiveAnchor.Bootstrap, w.Live);
        Assert.Equal("bootstrap", w.DescribeLabel());
        Assert.True(w.UseFallback);
        Assert.False(w.GroupFired);
        Assert.Equal(3, w.FramesAfterWorld);

        Assert.False(w.NotifyFrame(true));
        Assert.Equal("bootstrap", w.DescribeLabel());
    }

    [Fact]
    public void Group_before_fallback_never_engages()
    {
        var w = Small(3);
        w.NotifyWorldReady();
        Assert.False(w.NotifyGroupFired());
        Assert.Equal(DefaultAnchorWatch.LiveAnchor.Group, w.Live);
        Assert.Equal("group", w.DescribeLabel());

        for (int i = 0; i < 20; i++)
            Assert.False(w.NotifyFrame(true));

        Assert.Equal("group", w.DescribeLabel());
        Assert.False(w.UseFallback);
        Assert.True(w.GroupFired);
    }

    [Fact]
    public void Group_after_fallback_switches_back_once()
    {
        var w = Small(2);
        w.NotifyWorldReady();
        Assert.False(w.NotifyFrame(true));
        Assert.True(w.NotifyFrame(true));
        Assert.Equal("bootstrap", w.DescribeLabel());

        Assert.True(w.NotifyGroupFired());
        Assert.Equal(DefaultAnchorWatch.LiveAnchor.Group, w.Live);
        Assert.Equal("group", w.DescribeLabel());
        Assert.False(w.UseFallback);
        Assert.True(w.GroupFired);

        Assert.False(w.NotifyGroupFired());
        Assert.Equal("group", w.DescribeLabel());
        Assert.False(w.NotifyFrame(true));
    }

    [Fact]
    public void WorldReady_after_fallback_does_not_unengage()
    {
        var w = Small(2);
        w.NotifyWorldReady();
        w.NotifyFrame(true);
        w.NotifyFrame(true);
        Assert.True(w.UseFallback);

        w.NotifyWorldReady();
        Assert.Equal(0, w.FramesAfterWorld);
        Assert.True(w.UseFallback);
        Assert.Equal("bootstrap", w.DescribeLabel());
    }

    [Fact]
    public void WorldReady_after_group_keeps_group()
    {
        var w = Small();
        w.NotifyGroupFired();
        w.NotifyWorldReady();
        Assert.Equal("group", w.DescribeLabel());
        Assert.False(w.UseFallback);
        Assert.Equal(0, w.FramesAfterWorld);
    }

    [Fact]
    public void Constructor_rejects_non_positive_ticks()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DefaultAnchorWatch(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DefaultAnchorWatch(-1));
    }

    [Fact]
    public void Full_600_tick_countdown_engages_only_with_a_world()
    {
        var w = new DefaultAnchorWatch();
        for (int i = 0; i < DefaultAnchorWatch.FallbackTicks; i++)
            Assert.False(w.NotifyFrame(false));
        Assert.Equal("pending", w.DescribeLabel());

        w.NotifyWorldReady();
        for (int i = 0; i < DefaultAnchorWatch.FallbackTicks - 1; i++)
            Assert.False(w.NotifyFrame(true));
        Assert.Equal("pending", w.DescribeLabel());
        Assert.True(w.NotifyFrame(true));
        Assert.Equal("bootstrap", w.DescribeLabel());
    }
}
