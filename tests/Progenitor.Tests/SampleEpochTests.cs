using System;
using System.Collections.Generic;
using Progenitor.Plugins;
using Progenitor.Sample;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Job FSAMP. Game-free coverage of <c>.epoch</c> (the world-epoch demo), the
/// <c>Sample &lt;area&gt;:</c> log helper the FSAMP commands share, and the unload-reason branch
/// the Sample's <c>OnUnload(reason)</c> takes. These pin the exact reply and log lines
/// <c>docs/bridge-ops.md</c>'s Sample table tells an operator to expect.
/// </summary>
[Collection("PluginLogger")]
public class SampleEpochTests : IDisposable
{
    private readonly List<string> _lines = new();

    public SampleEpochTests()
    {
        SampleEpoch.Reset();
        SampleLog.Clear();
        SampleLog.Sink = line => _lines.Add(line);
    }

    public void Dispose()
    {
        SampleEpoch.Reset();
        SampleLog.Clear();
    }

    [Fact]
    public void A_line_is_formatted_as_Sample_area_message()
    {
        Assert.Equal("Sample epoch: hello", SampleLog.Format("epoch", "hello"));
        Assert.Equal("Sample epoch: hello", SampleLog.Info("epoch", "hello"));
        Assert.Equal(new[] { "Sample epoch: hello" }, _lines);
    }

    [Fact]
    public void The_ring_keeps_the_newest_lines_and_evicts_the_oldest()
    {
        for (int i = 0; i < SampleLog.Capacity + 5; i++)
            SampleLog.Info("epoch", "n" + i);

        var recent = SampleLog.Recent(3);
        Assert.Equal(
            new[]
            {
                "Sample epoch: n" + (SampleLog.Capacity + 2),
                "Sample epoch: n" + (SampleLog.Capacity + 3),
                "Sample epoch: n" + (SampleLog.Capacity + 4),
            },
            recent);
    }

    [Fact]
    public void Recent_never_returns_more_than_it_has()
    {
        Assert.Empty(SampleLog.Recent(3));
        SampleLog.Info("epoch", "one");
        Assert.Single(SampleLog.Recent(3));
        Assert.Empty(SampleLog.Recent(0));
    }

    [Fact]
    public void Without_a_stamp_the_reply_says_to_run_castle_first()
    {
        var lines = SampleEpoch.Lines("world: epoch=2 loads=1", SampleEpoch.Label, SampleEpoch.StampedEpoch, false);

        Assert.Equal(2, lines.Count);
        Assert.Equal("world: epoch=2 loads=1", lines[0]);
        Assert.Equal(SampleEpoch.NoStampReply, lines[1]);
    }

    [Fact]
    public void The_first_stamp_wins_and_a_later_castle_does_not_overwrite_it()
    {
        Assert.True(SampleEpoch.Stamp("castle#13 heart 45:1", 2));
        Assert.False(SampleEpoch.Stamp("castle#13 heart 60:1", 3));

        Assert.True(SampleEpoch.HasStamp);
        Assert.Equal("castle#13 heart 45:1", SampleEpoch.Label);
        Assert.Equal(2, SampleEpoch.StampedEpoch);
        Assert.Contains("Sample epoch: stamped castle#13 heart 45:1 in world load epoch 2", _lines);
    }

    [Fact]
    public void A_stamp_with_no_epoch_or_no_label_is_refused()
    {
        Assert.False(SampleEpoch.Stamp("castle#13 heart 45:1", 0));
        Assert.False(SampleEpoch.Stamp("", 2));
        Assert.False(SampleEpoch.HasStamp);
        Assert.Empty(_lines);
    }

    [Fact]
    public void A_current_stamp_reports_IsStale_False_and_keeps_the_id()
    {
        SampleEpoch.Stamp("castle#13 heart 45:1", 2);
        var lines = SampleEpoch.Lines("world: epoch=2 loads=1", SampleEpoch.Label, SampleEpoch.StampedEpoch, isStale: false);

        Assert.Equal("world: epoch=2 loads=1", lines[0]);
        Assert.Contains("IsStale=False", lines[1]);
        Assert.Contains("castle#13 heart 45:1", lines[1]);
        Assert.Contains("stamped in epoch 2", lines[1]);
    }

    [Fact]
    public void A_stale_stamp_says_so_and_says_to_drop_the_id()
    {
        SampleEpoch.Stamp("castle#13 heart 45:1", 2);
        var lines = SampleEpoch.Lines("world: epoch=3 loads=2", SampleEpoch.Label, SampleEpoch.StampedEpoch, isStale: true);

        Assert.Equal("world: epoch=3 loads=2", lines[0]);
        Assert.Contains("IsStale=True", lines[1]);
        Assert.Contains("Drop it.", lines[1]);
    }

    /// <summary>
    /// The third line is the static route: <c>WorldEpoch.Current</c> and
    /// <c>WorldEpoch.FormatStale</c> straight off the Core statics, in the same wording
    /// <c>CastleHandle.DisabledReason</c> uses. <c>ctx.WorldState</c> is a facade over them, and
    /// a helper with no <c>PluginContext</c> in reach reads them directly.
    /// </summary>
    [Fact]
    public void A_stamped_reply_carries_the_static_WorldEpoch_line()
    {
        Progenitor.WorldState.WorldEpoch.ResetForTests();
        SampleEpoch.Stamp("castle#13 heart 45:1", 2);

        var lines = SampleEpoch.Lines("world: epoch=1 loads=0", SampleEpoch.Label, SampleEpoch.StampedEpoch, isStale: true);

        Assert.Equal(3, lines.Count);
        int current = Progenitor.WorldState.WorldEpoch.Current;
        Assert.Contains("WorldEpoch.Current=" + current, lines[2], StringComparison.Ordinal);
        Assert.Contains(Progenitor.WorldState.WorldEpoch.FormatStale(2, current), lines[2], StringComparison.Ordinal);
        Assert.Contains("never subtract them", lines[2], StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>WorldEpoch.Advance</c> is Core's report that the game finished a world load, not a
    /// counter a plugin may nudge: calling it publishes <c>WorldLoaded</c>, makes every
    /// <c>ICastleHandle</c> stale and drops every god-mode guard from the old epoch. There is
    /// deliberately no <c>.epoch bump</c>, and the reply says so rather than leaving it to a
    /// doc paragraph.
    /// </summary>
    [Fact]
    public void Nothing_in_the_epoch_sample_calls_Advance_and_the_reply_says_why()
    {
        SourcePin.AssertNoStatementContaining(Source(), "WorldEpoch.Advance");
        Assert.Contains("Only Core advances the counter", SampleEpoch.StaticLine(2), StringComparison.Ordinal);
        Assert.Contains("did not happen", SampleEpoch.StaticLine(2), StringComparison.Ordinal);
    }

    [Fact]
    public void An_unavailable_world_state_still_produces_two_lines()
    {
        var lines = SampleEpoch.Lines("", "", 0, false);
        Assert.Equal("world: (unavailable)", lines[0]);
        Assert.Equal(SampleEpoch.NoStampReply, lines[1]);
    }

    [Theory]
    [InlineData(PluginUnloadReason.Swap, "keep")]
    [InlineData(PluginUnloadReason.Shutdown, "keep")]
    [InlineData(PluginUnloadReason.Unknown, "keep")]
    [InlineData(PluginUnloadReason.Removal, "sweep")]
    public void The_unload_line_names_the_reason_and_the_branch_it_takes(PluginUnloadReason reason, string verb)
    {
        string line = SampleLog.UnloadLine(reason);

        Assert.StartsWith("Sample unload: reason=" + reason + " -> " + verb + " ", line);
        Assert.Contains("Core-owned handles are swept on every reason either way", line);
        // The branch is the policy call, never a re-derivation from the enum.
        Assert.Equal(verb == "keep", PluginUnloadReasonPolicy.KeepsState(reason));
    }

    private static string Source()
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            string candidate = System.IO.Path.Combine(dir.FullName, "samples", "Progenitor.Sample", "SampleEpoch.cs");
            if (System.IO.File.Exists(candidate))
                return System.IO.File.ReadAllText(candidate).Replace("\r\n", "\n");
            dir = dir.Parent;
        }

        throw new System.IO.FileNotFoundException("samples/Progenitor.Sample/SampleEpoch.cs not found from " + AppContext.BaseDirectory);
    }
}
