using System;
using System.Collections.Generic;
using System.Text.Json;
using Progenitor.Clock;
using Progenitor.Dev.Ops;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// World speed beyond the policy maths: the bridge's single-lease slot, the status rows (which lease
/// governs), the <c>time</c> op's argument parsing, and source pins that every release path is wired
/// (Core uninstall, Dev unload, plugin tracking).
/// </summary>
public class TimeScaleBridgeTests
{
    private sealed class FakeLease : IDisposable
    {
        public bool Released;
        public int Disposals;

        public void Dispose()
        {
            Disposals++;
            Released = true;
        }
    }

    private static TimeScaleBridgeSlot<FakeLease> Slot() => new(l => !l.Released);

    [Fact]
    public void A_new_setting_replaces_the_old_lease_after_it_is_taken()
    {
        var slot = Slot();
        var first = new FakeLease();
        var second = new FakeLease();
        var order = new List<string>();

        Assert.True(slot.Replace(() => first));
        Assert.True(slot.Replace(() =>
        {
            // The old lease is still live while the new one is being taken: no flicker through normal speed.
            order.Add(first.Released ? "old released first" : "new taken first");
            return second;
        }));

        Assert.Equal(new[] { "new taken first" }, order);
        Assert.True(first.Released);
        Assert.False(second.Released);
        Assert.True(slot.Holds);
    }

    [Fact]
    public void A_refused_setting_keeps_the_old_lease()
    {
        var slot = Slot();
        var first = new FakeLease();
        slot.Replace(() => first);

        Assert.False(slot.Replace(() => null));
        Assert.False(first.Released);
        Assert.True(slot.Holds);
    }

    [Fact]
    public void Release_drops_the_lease_once_and_reports_whether_it_was_live()
    {
        var slot = Slot();
        var lease = new FakeLease();
        slot.Replace(() => lease);

        Assert.True(slot.Release());
        Assert.Equal(1, lease.Disposals);
        Assert.False(slot.Holds);
        Assert.False(slot.Release());
        Assert.Equal(1, lease.Disposals);
    }

    [Fact]
    public void A_lease_released_elsewhere_is_not_held()
    {
        var slot = Slot();
        var lease = new FakeLease();
        slot.Replace(() => lease);
        lease.Released = true; // Core unload, a world load or Core uninstall released it

        Assert.False(slot.Holds);
        Assert.False(slot.Release());
    }

    [Fact]
    public void Status_rows_mark_only_the_newest_lease_as_governing()
    {
        var stack = new TimeOfDayOverrides<string>();
        stack.Push("plugin.a");
        stack.Push("progenitor.dev bridge");
        var rows = TimeScaleLeaseInfo.FromStack(stack.Snapshot(),
            s => (s, s == "plugin.a" ? TimeScaleOverrideKind.SlowMotion : TimeScaleOverrideKind.Freeze, s == "plugin.a" ? 0.5f : 0f));

        Assert.Equal(2, rows.Count);
        Assert.Equal("plugin.a", rows[0].Owner);
        Assert.False(rows[0].Governs);
        Assert.Equal("progenitor.dev bridge", rows[1].Owner);
        Assert.True(rows[1].Governs);
        Assert.Equal(TimeScaleOverrideKind.Freeze, rows[1].Kind);
    }

    [Fact]
    public void Snapshot_is_a_copy()
    {
        var stack = new TimeOfDayOverrides<string>();
        stack.Push("a");
        var snap = stack.Snapshot();
        stack.Push("b");
        Assert.Single(snap);
    }

    [Fact]
    public void Owner_tag_trims_and_never_prints_blank()
    {
        Assert.Equal("progenitor.dev bridge", new TimeScaleOwnerTag(" progenitor.dev bridge ").Label);
        Assert.Equal("unnamed", new TimeScaleOwnerTag("  ").Label);
    }

    private static TimeRequest Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return TimeArgs.Parse(doc.RootElement.Clone());
    }

    [Theory]
    [InlineData("{}", "Status")]
    [InlineData("{\"action\":\"status\"}", "Status")]
    [InlineData("{\"action\":\"FREEZE\"}", "Freeze")]
    [InlineData("{\"action\":\"resume\"}", "Resume")]
    [InlineData("{\"action\":\"release\"}", "Release")]
    public void Actions_parse(string json, string expected) => Assert.Equal(expected, Parse(json).Action.ToString());

    [Theory]
    [InlineData("{\"action\":\"speed\",\"value\":0.25}", 0.25f)]
    [InlineData("{\"action\":\"speed\",\"value\":\"0.5\"}", 0.5f)]
    [InlineData("{\"action\":\"speed\",\"value\":1}", 1f)]
    [InlineData("{\"action\":\"speed\",\"value\":0.001}", 0.001f)]
    public void Speed_takes_the_slow_motion_range(string json, float value)
    {
        var r = Parse(json);
        Assert.Equal(TimeAction.Speed, r.Action);
        Assert.Equal(value, r.Value, 5);
    }

    [Theory]
    [InlineData("{\"action\":\"speed\"}")]
    [InlineData("{\"action\":\"speed\",\"value\":0}")]
    [InlineData("{\"action\":\"speed\",\"value\":1.5}")]
    [InlineData("{\"action\":\"speed\",\"value\":-1}")]
    [InlineData("{\"action\":\"speed\",\"value\":\"fast\"}")]
    [InlineData("{\"action\":\"pause\"}")]
    [InlineData("{\"action\":3}")]
    public void Bad_requests_are_refused(string json) => Assert.Throws<ArgumentException>(() => Parse(json));

    [Fact]
    public void Bridge_range_matches_slow_motion()
    {
        Assert.Equal(TimeScalePolicy.MinSlowMotion, TimeArgs.MinSpeed);
        Assert.Equal(TimeScalePolicy.MaxSlowMotion, TimeArgs.MaxSpeed);
    }

    [Fact]
    public void Every_release_path_is_wired()
    {
        string core = SourcePin.ReadFile("src", "Progenitor.Core", "Plugin.cs");
        SourcePin.AssertUncommented(core, "Clock.TimeScaleRuntime.Install();");
        SourcePin.AssertUncommented(core, "Clock.TimeScaleRuntime.Uninstall();");

        string dev = SourcePin.ReadFile("src", "Progenitor.Dev", "Plugin.cs");
        SourcePin.AssertUncommented(dev, "Ops.TimeOps.Shutdown();");

        string time = SourcePin.ReadFile("src", "Progenitor.Core", "Clock", "PluginTime.cs");
        SourcePin.AssertUncommented(time, "_ctx.Track(handle);");
        SourcePin.AssertUncommented(time, "lease.AfterRelease = _ => _ctx.Untrack(handle);");
    }
}
