using System;
using System.Collections.Generic;
using Progenitor;
using Progenitor.Networking;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Game-free coverage of the join-hang mitigation: the config gate, the slot-vs-own-caller
/// ordering, the collect sequence and its log line, and the plugin chain on the LaunchGame slot
/// (so Dev's host_capture keeps working while Core holds Bootstrap's one dispatcher).
/// Shares the HarmonySlotHandoff collection: both touch the same static slot state.
/// </summary>
[Collection("HarmonySlotHandoff")]
public class GcBeforeLaunchTests : IDisposable
{
    private readonly List<string> _log = new();
    private readonly FakeGc _gc = new();

    public GcBeforeLaunchTests()
    {
        HarmonySlotHandoff.Use(null);
        GcBeforeLaunch.Reset();
        GcBeforeLaunch.UseRuntime(_gc);
        GcBeforeLaunch.LogSink = _log.Add;
    }

    public void Dispose()
    {
        GcBeforeLaunch.Reset();
        GcBeforeLaunch.UseRuntime(null);
        GcBeforeLaunch.LogSink = null;
        HarmonySlotHandoff.Use(null);
    }

    private sealed class FakeGc : GcBeforeLaunch.IGcRuntime
    {
        public readonly List<string> Calls = new();
        public int Gen2 = 5;
        public long Heap = 300L * 1024 * 1024;
        public bool FinalizersFinish = true;
        public bool ThrowOnCollect;
        public int WaitTimeout;

        public int Gen2Count { get { Calls.Add("gen2"); return Gen2; } }
        public long HeapBytes { get { Calls.Add("heap"); return Heap; } }

        public void CollectFull()
        {
            Calls.Add("collect");
            if (ThrowOnCollect) throw new InvalidOperationException("boom");
            Gen2++;
            Heap -= 50L * 1024 * 1024;
        }

        public bool WaitForPendingFinalizers(int timeoutMs)
        {
            Calls.Add("finalizers");
            WaitTimeout = timeoutMs;
            return FinalizersFinish;
        }

        public long Timestamp => 0;
        public double ElapsedMs(long start) => 41.6;
    }

    private static void HostCapturePrefix(object? launchData) { }

    [Fact]
    public void Gate_defaults_on_and_follows_the_reader()
    {
        Assert.True(GcBeforeLaunch.DefaultEnabled);
        Assert.True(GcBeforeLaunch.Enabled);
        GcBeforeLaunch.SetEnabledReader(() => false);
        Assert.False(GcBeforeLaunch.Enabled);
        GcBeforeLaunch.SetEnabledReader(() => throw new InvalidOperationException());
        Assert.True(GcBeforeLaunch.Enabled);
        Assert.Equal("Diagnostics", GcBeforeLaunch.ConfigSection);
        Assert.Equal("CollectBeforeWorldLoad", GcBeforeLaunch.ConfigKey);
    }

    [Fact]
    public void Own_caller_collects_only_when_the_slot_is_not_armed()
    {
        Assert.True(GcBeforeLaunch.ShouldCollectInOwnCaller(enabled: true, slotArmed: false));
        Assert.False(GcBeforeLaunch.ShouldCollectInOwnCaller(enabled: true, slotArmed: true));
        Assert.False(GcBeforeLaunch.ShouldCollectInOwnCaller(enabled: false, slotArmed: false));
        Assert.True(GcBeforeLaunch.ShouldCollectInSlot(enabled: true));
        Assert.False(GcBeforeLaunch.ShouldCollectInSlot(enabled: false));

        Assert.True(GcBeforeLaunch.BeforeOwnLaunch("connect"));
        Assert.Single(_log);

        GcBeforeLaunch.SetSlotArmed(true);
        Assert.False(GcBeforeLaunch.BeforeOwnLaunch("connect"));
        Assert.Single(_log);
    }

    [Fact]
    public void Collect_runs_full_then_finalizers_then_full_and_logs_one_line()
    {
        GcBeforeLaunch.OnSlotLaunch();

        Assert.Equal(new[] { "gen2", "heap", "collect", "finalizers", "collect", "gen2", "heap" }, _gc.Calls);
        Assert.Equal(GcBeforeLaunch.FinalizerWaitMs, _gc.WaitTimeout);
        Assert.Equal(
            "GcBeforeLaunch: collected before LaunchGame in 42 ms (gen2 count 5->7, heap 300.0->200.0 MB)",
            Assert.Single(_log));
    }

    [Fact]
    public void A_finalizer_wait_that_times_out_is_reported_not_fatal()
    {
        _gc.FinalizersFinish = false;
        GcBeforeLaunch.OnSlotLaunch();
        Assert.Equal(2, _gc.Calls.FindAll(c => c == "collect").Count);
        Assert.EndsWith("[finalizer wait exceeded 2000 ms]", Assert.Single(_log));
    }

    [Fact]
    public void Disabled_gate_collects_nothing_and_logs_one_skip_per_launch()
    {
        GcBeforeLaunch.SetEnabledReader(() => false);

        GcBeforeLaunch.OnSlotLaunch();
        Assert.Empty(_gc.Calls);
        Assert.Equal(GcBeforeLaunch.SkippedLine("slot"), Assert.Single(_log));

        // Armed: the own caller stays silent, the slot already logged the skip.
        GcBeforeLaunch.SetSlotArmed(true);
        Assert.False(GcBeforeLaunch.BeforeOwnLaunch("connect"));
        Assert.Single(_log);

        // Not armed: the own caller is the only path, so it logs.
        GcBeforeLaunch.SetSlotArmed(false);
        Assert.False(GcBeforeLaunch.BeforeOwnLaunch("connect"));
        Assert.Equal(2, _log.Count);
        Assert.Contains("CollectBeforeWorldLoad=false", _log[1]);
        Assert.Empty(_gc.Calls);
    }

    [Fact]
    public void A_throwing_runtime_never_escapes_into_LaunchGame()
    {
        _gc.ThrowOnCollect = true;
        GcBeforeLaunch.OnSlotLaunch();
        Assert.False(GcBeforeLaunch.BeforeOwnLaunch("connect"));
        Assert.All(_log, l => Assert.Contains("threw", l));
    }

    [Fact]
    public void Plugin_bind_of_LaunchGame_chains_behind_Core_while_the_slot_is_armed()
    {
        var fake = new HarmonySlotHandoff.FakeSlot();
        HarmonySlotHandoff.Use(fake);
        Action<object?> core = _ => { };
        Assert.True(HarmonySlotHandoff.TryBindPluginDirect(HarmonySlotHandoff.LaunchGame, core, "progenitor-core"));
        GcBeforeLaunch.SetSlotArmed(true);

        Action<object?> dev = HostCapturePrefix;
        Assert.True(HarmonySlotHandoff.TryBindPlugin(HarmonySlotHandoff.LaunchGame, dev, "progenitor-reload:Progenitor.Dev:1"));

        // Bootstrap's slot still holds Core's dispatcher; Dev's is chained.
        Assert.Same(core, fake.Get(HarmonySlotHandoff.LaunchGame));
        Assert.Same(dev, GcBeforeLaunch.Chained);
        Assert.Equal("progenitor-reload:Progenitor.Dev:1", GcBeforeLaunch.ChainedOwner);

        // A second plugin is refused, the same owner rebinds.
        Assert.False(HarmonySlotHandoff.TryBindPlugin(HarmonySlotHandoff.LaunchGame, new Action<object?>(_ => { }), "other"));
        Assert.True(HarmonySlotHandoff.TryBindPlugin(HarmonySlotHandoff.LaunchGame, dev, "progenitor-reload:Progenitor.Dev:1"));

        // Dev's force-clear (HostCapture.ClearBinds passes no owner) drops only the chain.
        HarmonySlotHandoff.ClearPlugin(HarmonySlotHandoff.LaunchGame);
        Assert.Null(GcBeforeLaunch.Chained);
        Assert.Same(core, fake.Get(HarmonySlotHandoff.LaunchGame));
    }

    [Fact]
    public void Without_an_armed_slot_a_plugin_binds_Bootstrap_directly()
    {
        var fake = new HarmonySlotHandoff.FakeSlot();
        HarmonySlotHandoff.Use(fake);
        Action<object?> dev = HostCapturePrefix;
        Assert.True(HarmonySlotHandoff.TryBindPlugin(HarmonySlotHandoff.LaunchGame, dev, "dev"));
        Assert.Same(dev, fake.Get(HarmonySlotHandoff.LaunchGame));
        Assert.Null(GcBeforeLaunch.Chained);

        // Core arriving second is refused by owner and stays in fallback mode.
        Assert.False(HarmonySlotHandoff.TryBindPluginDirect(HarmonySlotHandoff.LaunchGame, new Action<object?>(_ => { }), "progenitor-core"));
        Assert.False(GcBeforeLaunch.SlotArmed);
    }

    [Fact]
    public void Unload_sweeps_drop_a_chained_dispatcher()
    {
        GcBeforeLaunch.SetSlotArmed(true);
        Action<object?> dev = HostCapturePrefix;

        Assert.True(GcBeforeLaunch.TryChain(dev, "dev-alc"));
        Assert.False(GcBeforeLaunch.ClearChain("someone-else"));
        Assert.NotNull(GcBeforeLaunch.Chained);
        HarmonySlotHandoff.ClearOwned("dev-alc");
        Assert.Null(GcBeforeLaunch.Chained);

        Assert.True(GcBeforeLaunch.TryChain(dev, "dev-alc"));
        HarmonySlotHandoff.ClearPluginContext(null, "dev-alc");
        Assert.Null(GcBeforeLaunch.Chained);

        // The reloader sweep matches the declaring assembly even under a different owner name.
        Assert.True(GcBeforeLaunch.TryChain(dev, "named-by-hand"));
        Assert.True(GcBeforeLaunch.ClearChainForAssemblies(new[] { typeof(GcBeforeLaunchTests).Assembly }));
        Assert.Null(GcBeforeLaunch.Chained);

        Assert.False(GcBeforeLaunch.TryChain(dev, ""));
    }

    [Fact]
    public void Reset_unarms_and_drops_the_chain()
    {
        GcBeforeLaunch.SetSlotArmed(true);
        GcBeforeLaunch.SetEnabledReader(() => false);
        Assert.True(GcBeforeLaunch.TryChain(new Action<object?>(HostCapturePrefix), "dev"));
        GcBeforeLaunch.Reset();
        Assert.False(GcBeforeLaunch.SlotArmed);
        Assert.True(GcBeforeLaunch.Enabled);
        Assert.Null(GcBeforeLaunch.Chained);
    }

    [Fact]
    public void Log_line_shape_is_the_documented_one()
    {
        Assert.Equal(
            "GcBeforeLaunch: collected before LaunchGame in 7 ms (gen2 count 1->3, heap 1.5->0.5 MB)",
            GcBeforeLaunch.FormatCollected(7.2, 1, 3, 1536L * 1024, 512L * 1024));
    }
}
