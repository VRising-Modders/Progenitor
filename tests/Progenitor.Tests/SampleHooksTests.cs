using System;
using System.Collections.Generic;
using Progenitor.Sample;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Job FEX1 (RD6 J1). Game-free coverage of the <c>ctx.Hooks</c> example: the token parse, the
/// counter arithmetic the two Before/After pairs drive, the bounded Skip budget, and every line
/// <c>docs/bridge-ops.md</c> tells an operator to grep for. The hooks themselves need a live ECS
/// world and are behind <c>!PROGENITOR_TESTS</c>.
/// </summary>
[Collection("PluginLogger")]
public class SampleHooksTests : IDisposable
{
    private readonly List<string> _lines = new();

    public SampleHooksTests()
    {
        SampleHooks.Reset();
        SampleLog.Clear();
        SampleLog.Sink = line => _lines.Add(line);
    }

    public void Dispose()
    {
        SampleHooks.Reset();
        SampleLog.Clear();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("status")]
    [InlineData("STATUS")]
    public void A_bare_command_is_a_status_read(string token)
    {
        Assert.True(SampleHooks.TryParse(token, "", out var kind, out int frames));
        Assert.Equal(SampleHooks.Kind.Status, kind);
        Assert.Equal(0, frames);
    }

    [Fact]
    public void Skip_without_a_number_arms_one_frame()
    {
        Assert.True(SampleHooks.TryParse("skip", "", out var kind, out int frames));
        Assert.Equal(SampleHooks.Kind.Skip, kind);
        Assert.Equal(SampleHooks.DefaultSkipFrames, frames);
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData(" 5 ", 5)]
    public void Skip_takes_a_frame_count_inside_the_cap(string frames, int expected)
    {
        Assert.True(SampleHooks.TryParse("skip", frames, out var kind, out int parsed));
        Assert.Equal(SampleHooks.Kind.Skip, kind);
        Assert.Equal(expected, parsed);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("6")]
    [InlineData("-1")]
    [InlineData("lots")]
    // Refused, not clamped: a typo replies usage rather than skipping the maximum.
    public void An_out_of_range_frame_count_is_refused(string frames)
    {
        Assert.False(SampleHooks.TryParse("skip", frames, out _, out _));
        Assert.Equal("Usage: .hooks [status] | .hooks skip [1-5]", SampleHooks.UsageReply);
    }

    [Theory]
    [InlineData("on")]
    [InlineData("off")]
    [InlineData("1")]
    public void An_unknown_verb_is_refused(string token)
    {
        Assert.False(SampleHooks.TryParse(token, "", out _, out _));
    }

    [Fact]
    public void The_counters_start_at_zero_and_count_each_call()
    {
        Assert.Equal(0, SampleHooks.ManagedBefore);
        Assert.Equal(1, SampleHooks.CountManagedBefore());
        Assert.Equal(2, SampleHooks.CountManagedBefore());
        Assert.Equal(1, SampleHooks.CountManagedAfter());
        Assert.Equal(1, SampleHooks.CountBurstBefore());
        Assert.Equal(1, SampleHooks.CountBurstAfter());

        Assert.Equal(2, SampleHooks.ManagedBefore);
        Assert.Equal(1, SampleHooks.ManagedAfter);
        Assert.Equal(1, SampleHooks.BurstBefore);
        Assert.Equal(1, SampleHooks.BurstAfter);
    }

    [Fact]
    // The whole point of the heartbeat: it is the exact string the live check greps for.
    public void The_heartbeat_line_is_the_documented_one()
    {
        Assert.Equal(
            "sample.hooks: ServerBootstrapSystem before=61 after=61; MoveEntitySystem before=59 after=58",
            SampleHooks.HeartbeatLine(61, 61, 59, 58));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(599, false)]
    [InlineData(600, true)]
    [InlineData(1200, true)]
    public void The_heartbeat_fires_every_600_frames_and_never_on_frame_zero(long frame, bool expected)
    {
        Assert.Equal(expected, SampleHooks.IsHeartbeatFrame(frame));
    }

    [Fact]
    public void Arming_a_skip_spends_exactly_that_many_frames()
    {
        Assert.True(SampleHooks.TryArmSkip(2, out int armed));
        Assert.Equal(2, armed);
        Assert.Equal(2, SampleHooks.SkipBudget);

        Assert.True(SampleHooks.TryConsumeSkip(out int remaining));
        Assert.Equal(1, remaining);
        Assert.True(SampleHooks.TryConsumeSkip(out remaining));
        Assert.Equal(0, remaining);

        Assert.False(SampleHooks.TryConsumeSkip(out remaining));
        Assert.Equal(0, remaining);
        Assert.Equal(0, SampleHooks.SkipBudget);
    }

    [Fact]
    // Repeating the command must not grow the budget: a bounded demo that can be extended
    // without bound is not bounded.
    public void A_second_arm_while_one_is_running_is_refused_and_does_not_extend_it()
    {
        Assert.True(SampleHooks.TryArmSkip(3, out _));
        Assert.False(SampleHooks.TryArmSkip(5, out int remaining));
        Assert.Equal(3, remaining);
        Assert.Equal(3, SampleHooks.SkipBudget);
        Assert.Equal(
            "A skip is already armed (3 frame(s) left). Let it finish, then ask again.",
            SampleHooks.SkipBusyReply(remaining));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(-1)]
    public void An_out_of_range_arm_is_refused_outright(int frames)
    {
        Assert.False(SampleHooks.TryArmSkip(frames, out _));
        Assert.Equal(0, SampleHooks.SkipBudget);
    }

    [Fact]
    public void The_status_and_skip_replies_name_both_systems_and_the_after_rule()
    {
        string status = SampleHooks.StatusReply(10, 10, 9, 8, 1);
        Assert.Contains("ServerBootstrapSystem (managed): before=10 after=10", status);
        Assert.Contains("MoveEntitySystem (Burst): before=9 after=8", status);
        Assert.Contains("skip budget=1 frame(s)", status);

        string armed = SampleHooks.SkipArmedReply(2);
        Assert.Contains("Skip for 2 frame(s) on MoveEntitySystem", armed);
        // The dispatch rule a reader would otherwise report as a bug: an After hook with the
        // default OnlyWhenSystemRuns does not run on a frame a Before hook skipped.
        Assert.Contains("After hook does not run on a skipped frame", armed);
        // Job FEX2 (R56 finding 8). The reply must not tell the admin arming it that the cost is a
        // frame of nothing: MoveEntitySystem's query carries EntityInput and
        // NetworkInterpolated_Shared, so a server-side skip is a correction on every client that
        // already predicted the frame.
        Assert.Contains("corrected", armed);
        Assert.Contains("rubber-banding", armed);
        Assert.DoesNotContain("and nothing else", armed);

        Assert.Equal(
            "skipped MoveEntitySystem update this frame (remaining=0)",
            SampleHooks.SkippedFrameLine(0));
    }

    [Fact]
    public void Reset_forgets_the_counters_and_any_armed_skip()
    {
        SampleHooks.CountManagedBefore();
        SampleHooks.CountBurstAfter();
        Assert.True(SampleHooks.TryArmSkip(1, out _));

        SampleHooks.Detach();

        Assert.Equal(0, SampleHooks.ManagedBefore);
        Assert.Equal(0, SampleHooks.BurstAfter);
        Assert.Equal(0, SampleHooks.SkipBudget);
    }
}
