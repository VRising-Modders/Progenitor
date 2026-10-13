using System.Collections.Generic;
using Progenitor.Bootstrap;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Game-free coverage of the IL2CPP detour-cache pin check: empty slot is not a pin,
/// collectible method/target entries are reported with context + method, and a plugin
/// whose methods are in a fake cache is marked never-collectable (skip the 60-frame wait).
/// </summary>
public class DetourCachePolicyTests
{
    [Fact]
    public void Format_lines_are_the_grep_strings()
    {
        Assert.Equal(
            "plugin-alc progenitor-reload:Progenitor.Dev:1: pinned by IL2CPP detour cache: Progenitor.Dev.Ops.HostCapture+LaunchGameHook.Prefix",
            DetourCachePolicy.FormatPluginAlcPinned(
                "progenitor-reload:Progenitor.Dev:1",
                "Progenitor.Dev.Ops.HostCapture+LaunchGameHook.Prefix"));
        Assert.Equal(
            "pin-diag HarmonyIL2CPP DelegateCache count=43 core=0 collectible=1",
            DetourCachePolicy.FormatDelegateCacheLog(43, 0, 1));
        Assert.Equal(
            "  cache alc=progenitor-reload:Progenitor.Dev:1 method=Progenitor.Dev.Ops.HostCapture+LaunchGameHook.Prefix",
            DetourCachePolicy.FormatCacheEntry(
                "progenitor-reload:Progenitor.Dev:1",
                "Progenitor.Dev.Ops.HostCapture+LaunchGameHook.Prefix"));
        Assert.Equal(CoreReloadPolicy.UnnamedAlcName, DetourCachePolicy.NamedAlc(null));
        Assert.Equal(CoreReloadPolicy.UnnamedAlcName, DetourCachePolicy.NamedAlc(""));
    }

    [Fact]
    public void Empty_cache_is_vanilla_not_a_pin()
    {
        var lines = new List<string>();
        Assert.Equal(0, DetourCachePolicy.DescribeCollectible(null, lines));
        Assert.Equal(0, DetourCachePolicy.DescribeCollectible(System.Array.Empty<DetourCachePolicy.Entry>(), lines));
        Assert.Empty(lines);
        Assert.Empty(DetourCachePolicy.MethodsPinningContext(null, "Progenitor.Dev", "progenitor-reload:Dev:1"));
        Assert.False(DetourCachePolicy.SkipPluginAlcWait(pinnedByDetourCache: false));
        Assert.Equal(CoreReloadPolicy.PluginAlcWaitFrames, DetourCachePolicy.PluginAlcWaitFrames(false));
    }

    [Fact]
    public void Fake_cache_reports_collectible_method_and_target()
    {
        var cache = new[]
        {
            new DetourCachePolicy.Entry
            {
                MethodName = "Progenitor.Bootstrap.HarmonyPatches+LaunchGameHook.Prefix",
                MethodAssemblyName = "Progenitor.Bootstrap",
                MethodAlcName = "",
                MethodAlcCollectible = false,
            },
            new DetourCachePolicy.Entry
            {
                MethodName = "Progenitor.Dev.Ops.HostCapture+LaunchGameHook.Prefix",
                MethodAssemblyName = "Progenitor.Dev",
                MethodAlcName = "progenitor-reload:Progenitor.Dev:1",
                MethodAlcCollectible = true,
            },
            new DetourCachePolicy.Entry
            {
                MethodName = "System.Action.Invoke",
                MethodAssemblyName = "System.Private.CoreLib",
                MethodAlcCollectible = false,
                TargetTypeName = "Progenitor.Dev.Ops.ChatObserve+SendHook",
                TargetAssemblyName = "Progenitor.Dev",
                TargetAlcName = "progenitor-reload:Progenitor.Dev:1",
                TargetAlcCollectible = true,
            },
        };

        var lines = new List<string>();
        int n = DetourCachePolicy.DescribeCollectible(cache, lines);
        Assert.Equal(2, n);
        Assert.Equal(2, lines.Count);
        Assert.Contains("progenitor-reload:Progenitor.Dev:1", lines[0]);
        Assert.Contains("HostCapture+LaunchGameHook.Prefix", lines[0]);
        Assert.Contains("ChatObserve+SendHook", lines[1]);
        Assert.Equal(
            "pin-diag HarmonyIL2CPP DelegateCache count=3 core=0 collectible=2",
            DetourCachePolicy.FormatDelegateCacheLog(3, 0, n));

        var methods = DetourCachePolicy.MethodsPinningContext(
            cache, "Progenitor.Dev", "progenitor-reload:Progenitor.Dev:1");
        Assert.Equal(2, methods.Count);
        Assert.Contains("Progenitor.Dev.Ops.HostCapture+LaunchGameHook.Prefix", methods);
        Assert.True(DetourCachePolicy.SkipPluginAlcWait(pinnedByDetourCache: true));
        Assert.Equal(0, DetourCachePolicy.PluginAlcWaitFrames(true));
    }

    [Fact]
    public void PinsContext_ignores_other_plugins_and_default_alc()
    {
        var entry = new DetourCachePolicy.Entry
        {
            MethodName = "Progenitor.Sample.Plugin.OnLoad",
            MethodAssemblyName = "Progenitor.Sample",
            MethodAlcName = "progenitor-reload:Progenitor.Sample:1",
            MethodAlcCollectible = true,
        };
        Assert.False(DetourCachePolicy.PinsContext(entry, "Progenitor.Dev", "progenitor-reload:Progenitor.Dev:1"));
        Assert.True(DetourCachePolicy.PinsContext(entry, "Progenitor.Sample", null));
        Assert.True(DetourCachePolicy.PinsContext(entry, null, "progenitor-reload:Progenitor.Sample:1"));

        var bootstrap = new DetourCachePolicy.Entry
        {
            MethodName = "Progenitor.Bootstrap.HarmonyPatches+ChatHook.Prefix",
            MethodAssemblyName = "Progenitor.Bootstrap",
            MethodAlcCollectible = false,
        };
        Assert.False(DetourCachePolicy.IsCollectiblePin(bootstrap, out _, out _));
        Assert.False(DetourCachePolicy.PinsContext(bootstrap, "Progenitor.Bootstrap", null));
    }

    [Fact]
    public void FindStaticListObjectField_accepts_a_renamed_List_object_field()
    {
        Assert.Equal("DelegateCache", DetourCachePolicy.CacheFieldNames[0]);
        var named = DetourCachePolicy.FindStaticListObjectField(typeof(NamedCacheHost));
        Assert.NotNull(named);
        Assert.Equal("DelegateCache", named!.Name);

        var renamed = DetourCachePolicy.FindStaticListObjectField(typeof(RenamedCacheHost));
        Assert.NotNull(renamed);
        Assert.Equal("Trampolines", renamed!.Name);
        Assert.True(DetourCachePolicy.IsStaticListObject(renamed));

        Assert.Null(DetourCachePolicy.FindStaticListObjectField(typeof(MissingCacheHost)));
        Assert.Null(DetourCachePolicy.FindStaticListObjectField(null));
        Assert.Equal(
            "pin-diag HarmonyIL2CPP DelegateCache: no static List<object> field; skip-wait disabled",
            DetourCachePolicy.FormatCacheMissing());
    }

    [Fact]
    public void Missing_cache_field_fails_closed_and_waits_the_normal_frames()
    {
        Assert.False(DetourCachePolicy.SkipPluginAlcWait(pinnedByDetourCache: true, cacheAvailable: false));
        Assert.Equal(
            CoreReloadPolicy.PluginAlcWaitFrames,
            DetourCachePolicy.PluginAlcWaitFrames(pinnedByDetourCache: true, cacheAvailable: false));
        Assert.True(DetourCachePolicy.SkipPluginAlcWait(pinnedByDetourCache: true, cacheAvailable: true));
        Assert.Equal(0, DetourCachePolicy.PluginAlcWaitFrames(true, cacheAvailable: true));
        Assert.False(DetourCachePolicy.SkipPluginAlcWait(false, cacheAvailable: true));
    }

    private sealed class NamedCacheHost
    {
        private static readonly List<object> DelegateCache = new();
    }

    private sealed class RenamedCacheHost
    {
        private static readonly List<object> Trampolines = new();
    }

    private sealed class MissingCacheHost
    {
        private static readonly System.Collections.Concurrent.ConcurrentBag<object> Bag = new();
    }
}
