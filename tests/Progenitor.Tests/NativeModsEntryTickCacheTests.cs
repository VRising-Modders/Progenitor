using System;
using System.Collections.Generic;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Game-free coverage of TickBody's per-host walk cache, Play-backdrop consult
/// gate, HUD recast skip, and <c>Ui: tick-breakdown</c> line. Removal of the
/// host-equality short-circuit or the Play-view gate is the FindFont stall:
/// a scene walk every in-world frame.
/// </summary>
[Collection("MainThreadQueue")]
public sealed class NativeModsEntryTickCacheTests : IDisposable
{
    public NativeModsEntryTickCacheTests()
    {
        NativeModsEntry.OwnedTopTabCache.Invalidate();
        UiTickBreakdown.ResetForTests();
        UiTickTiming.ResetForTests();
    }

    public void Dispose()
    {
        NativeModsEntry.OwnedTopTabCache.Invalidate();
        UiTickBreakdown.ResetForTests();
        UiTickTiming.ResetForTests();
    }

    [Fact]
    public void Unchanged_host_tick_performs_zero_scene_walks()
    {
        var cache = new NativeModsEntry.HostWalkCache();
        int walks = 0;
        int Walk() { walks++; return 2; }
        var host = new IntPtr(42);
        Assert.Equal(2, cache.Resolve(host, fingerprint: 8, cachedAlive: true, Walk));
        Assert.Equal(1, walks);
        Assert.Equal(2, cache.Resolve(host, fingerprint: 8, cachedAlive: true, Walk));
        Assert.Equal(2, cache.Resolve(host, fingerprint: 8, cachedAlive: true, Walk));
        Assert.Equal(1, walks);
    }

    [Fact]
    public void Host_change_performs_exactly_one_walk()
    {
        var cache = new NativeModsEntry.HostWalkCache();
        int walks = 0;
        int Walk() { walks++; return walks; }
        cache.Resolve(new IntPtr(1), fingerprint: 8, cachedAlive: true, Walk);
        Assert.Equal(1, walks);
        int afterChange = cache.Resolve(new IntPtr(2), fingerprint: 8, cachedAlive: true, Walk);
        Assert.Equal(2, walks);
        Assert.Equal(2, afterChange);
        cache.Resolve(new IntPtr(2), fingerprint: 8, cachedAlive: true, Walk);
        Assert.Equal(2, walks);
    }

    [Fact]
    public void ChildCount_change_or_dead_cache_walks_again()
    {
        var cache = new NativeModsEntry.HostWalkCache();
        int walks = 0;
        int Walk() { walks++; return 1; }
        var host = new IntPtr(7);
        cache.Resolve(host, fingerprint: 8, cachedAlive: true, Walk);
        cache.Resolve(host, fingerprint: 9, cachedAlive: true, Walk);
        Assert.Equal(2, walks);
        cache.Resolve(host, fingerprint: 9, cachedAlive: false, Walk);
        Assert.Equal(3, walks);
        cache.Resolve(IntPtr.Zero, fingerprint: 9, cachedAlive: true, Walk);
        Assert.Equal(3, walks);
        Assert.Equal(0, cache.Resolve(IntPtr.Zero, fingerprint: 9, cachedAlive: true, Walk));
    }

    [Fact]
    public void Play_backdrop_cache_is_not_consulted_when_no_play_view_is_current()
    {
        Assert.False(NativeModsEntry.ShouldConsultPlayBackdropCache(playViewCurrent: false));
        Assert.True(NativeModsEntry.ShouldConsultPlayBackdropCache(playViewCurrent: true));
        Assert.False(NativeModsEntry.ShouldWalkPlayBackdrop(
            playViewCurrent: false, templateAlive: false, attemptedThisView: false));
        Assert.False(NativeModsEntry.ShouldWalkPlayBackdrop(
            playViewCurrent: true, templateAlive: true, attemptedThisView: false));
        Assert.False(NativeModsEntry.ShouldWalkPlayBackdrop(
            playViewCurrent: true, templateAlive: false, attemptedThisView: true));
        Assert.True(NativeModsEntry.ShouldWalkPlayBackdrop(
            playViewCurrent: true, templateAlive: false, attemptedThisView: false));

        int walks = 0;
        void Walk() => walks++;
        bool playCurrent = false;
        if (NativeModsEntry.ShouldConsultPlayBackdropCache(playCurrent)
            && NativeModsEntry.ShouldWalkPlayBackdrop(playCurrent, templateAlive: false, attemptedThisView: false))
            Walk();
        Assert.Equal(0, walks);
    }

    [Fact]
    public void Unchanged_hud_pointer_does_not_recast()
    {
        var ptr = new IntPtr(11);
        Assert.True(NativeModsEntry.ShouldReuseHudMenu(ptr, ptr, cachedAlive: true));
        Assert.False(NativeModsEntry.ShouldReuseHudMenu(ptr, new IntPtr(12), cachedAlive: true));
        Assert.False(NativeModsEntry.ShouldReuseHudMenu(ptr, ptr, cachedAlive: false));
        Assert.False(NativeModsEntry.ShouldReuseHudMenu(ptr, IntPtr.Zero, cachedAlive: true));
    }

    [Fact]
    public void Tick_breakdown_log_names_every_step()
    {
        string line = UiTickBreakdown.Format(1, 2, 3, 52, 4, 5, 6);
        Assert.Equal(
            "Ui: tick-breakdown hudLookup=1 ownedTabCount=2 tabVisualRead=3 playBackdropCache=52 readNative=4 hold/restore=5 other=6",
            line);
        Assert.True(UiTickBreakdown.ShouldLog(true, 0, long.MinValue));
        Assert.False(UiTickBreakdown.ShouldLog(false, 0, long.MinValue));
        Assert.False(UiTickBreakdown.ShouldLog(true, 999, 0));
        Assert.True(UiTickBreakdown.ShouldLog(true, 1000, 0));
    }

    [Fact]
    public void Tick_breakdown_logs_once_per_second_when_timing_is_on()
    {
        var log = new List<string>();
        UiTickBreakdown.LogSink = log.Add;
        long t = 0;
        UiTickBreakdown.NowMs = () => t;
        UiTickBreakdown.Finish();
        Assert.Empty(log);

        UiTickTiming.Enabled = true;
        UiTickBreakdown.Finish();
        Assert.Single(log);
        Assert.StartsWith("Ui: tick-breakdown ", log[0]);
        Assert.Contains("hudLookup=", log[0]);
        Assert.Contains("ownedTabCount=", log[0]);
        Assert.Contains("tabVisualRead=", log[0]);
        Assert.Contains("playBackdropCache=", log[0]);
        Assert.Contains("readNative=", log[0]);
        Assert.Contains("hold/restore=", log[0]);
        Assert.Contains("other=", log[0]);

        t = 999;
        UiTickBreakdown.Finish();
        Assert.Single(log);

        t = 1000;
        UiTickBreakdown.Finish();
        Assert.Equal(2, log.Count);
    }
}

public sealed class NativeModsEntryTickCacheSourceTests
{
    [Fact]
    public void TickBody_gates_play_backdrop_and_reuses_hud_and_owned_tab_cache()
    {
        string src = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "NativeModsEntry.cs");
        string tick = SourcePin.MethodBody(src, "private static void TickBody()");
        SourcePin.AssertStatement(tick, "if (ShouldConsultPlayBackdropCache(");
        SourcePin.AssertUncommented(tick, "TryResolveFullscreenMenu(");
        SourcePin.AssertNoStatementContaining(tick, "hud.CurrentMenu?.TryCast<FullscreenMenu>()");
        string count = SourcePin.MethodBody(src, "private static int CountOwnedTopTabsIn(Transform? parent)");
        SourcePin.AssertUncommented(count, "OwnedTopTabCache.Resolve");
        SourcePin.AssertNoStatementContaining(count, "GetComponentsInChildren");
        string chrome = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "ModsWorkspaceChrome.cs");
        string cache = SourcePin.MethodBody(chrome, "internal static void TryCacheLivePlayBackdrop()");
        SourcePin.AssertUncommented(cache, "ShouldConsultPlayBackdropCache(");
        SourcePin.AssertUncommented(cache, "ShouldWalkPlayBackdrop(");
    }
}
