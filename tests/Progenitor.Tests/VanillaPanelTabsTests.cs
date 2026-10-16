using System;
using System.Collections.Generic;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

[Collection("PluginLogger")]
public sealed class VanillaPanelTabsTests
{
    [Fact]
    public void TargetChangeAndCloseRestoreNativeViewBeforeNotifyingConsumer()
    {
        var runtime = new Runtime();
        using var tabs = new VanillaPanelTabs(runtime, VanillaUiSite.ContainerPanel, new[] { "Inventory", "Input" }, new object());
        var changes = new List<VanillaPanelChange>();
        tabs.PanelChanged += e => { Assert.Equal(0, tabs.Selected); Assert.Equal(0, runtime.Host.Selected); changes.Add(e.Change); };
        runtime.Frame!(Open(1));
        tabs.Selected = 1;
        runtime.Frame(Open(1));
        Assert.Equal(1, tabs.Selected);
        runtime.Frame(Open(2));
        tabs.Selected = 1;
        runtime.Frame(default);
        Assert.Equal(new[] { VanillaPanelChange.Opened, VanillaPanelChange.TargetChanged, VanillaPanelChange.Closed }, changes);
    }

    [Fact]
    public void OwnerSweepReleasesOnlyThatOwnersHostAndWatchExactlyOnce()
    {
        var owner = new object();
        var runtime = new Runtime();
        var other = new Runtime();
        using var a = new VanillaPanelTabs(runtime, VanillaUiSite.ContainerPanel, new[] { "Inventory", "Input" }, owner);
        using var b = new VanillaPanelTabs(other, VanillaUiSite.StationPanel, new[] { "Production", "Input" }, new object());
        VanillaPanelTabs.Drop(owner);
        a.Dispose();
        Assert.Equal(1, runtime.Host.Disposals);
        Assert.Equal(1, runtime.Watch.Disposals);
        Assert.Equal(0, other.Host.Disposals);
    }

    sealed class Subscriber
    {
        internal int Calls;
        internal void OnSelect(int _) => Calls++;
        internal void OnPanel(VanillaPanelEvent _) => Calls++;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static (WeakReference subscriber, WeakReference owner) SubscribeAndForget(VanillaPanelTabs tabs, object owner)
    {
        var s = new Subscriber();
        tabs.SelectionChanged += s.OnSelect;
        tabs.PanelChanged += s.OnPanel;
        return (new WeakReference(s), new WeakReference(owner));
    }

    [Fact]
    public void OwnerSweepLetsAPluginThatNeverUnsubscribedBeCollected()
    {
        var runtime = new Runtime();
        int before = VanillaPanelTabs.LiveCount;
        var tabs = CreateFor(runtime, out var refs);
        Assert.True(tabs.HasSubscribers);
        Assert.Equal(before + 1, VanillaPanelTabs.LiveCount);

        // The plugin unloads without unsubscribing or disposing: the Ui.RemoveAll sweep runs.
        DropOwnerOf(refs.owner);
        Assert.False(tabs.HasSubscribers);
        Assert.Equal(before, VanillaPanelTabs.LiveCount);
        Assert.Equal(1, runtime.Host.Disposals);
        Assert.Equal(1, runtime.Watch.Disposals);

        // The handle itself is still reachable (the runtime's frame callback, a Core cache) and
        // must not keep the plugin's subscriber or owner alive.
        for (int i = 0; i < 3 && (refs.subscriber.IsAlive || refs.owner.IsAlive); i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        Assert.False(refs.subscriber.IsAlive);
        Assert.False(refs.owner.IsAlive);
        GC.KeepAlive(tabs);
        runtime.Frame!(Open(1)); // a late frame after the sweep is inert
        Assert.False(tabs.HasSubscribers);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static void DropOwnerOf(WeakReference owner) => VanillaPanelTabs.Drop(owner.Target);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static VanillaPanelTabs CreateFor(Runtime runtime, out (WeakReference subscriber, WeakReference owner) refs)
    {
        var owner = new object();
        var tabs = new VanillaPanelTabs(runtime, VanillaUiSite.ContainerPanel, new[] { "Inventory", "Input" }, owner);
        refs = SubscribeAndForget(tabs, owner);
        return tabs;
    }

    [Fact]
    public void ReSelectingNativeTabStillTellsRuntimeToLeaveRedistribution()
    {
        var runtime = new Runtime();
        using var tabs = new VanillaPanelTabs(runtime, VanillaUiSite.StationPanel, new[] { "Production", "Input" }, new object());
        runtime.Host.Selected = 2;
        int notifications = 0;
        tabs.SelectionChanged += _ => notifications++;
        tabs.Selected = 0;
        Assert.Equal(0, runtime.Host.Selected);
        Assert.Equal(0, notifications);
        tabs.Selected = -1;
        tabs.Selected = 2;
        Assert.Equal(0, runtime.Host.Selected);
    }

    static VanillaSiteSample Open(int id) => new(true, id, true, false,
        target: new VanillaPanelTarget(VanillaPanelKind.Container, id, 1, id, 1));

    [Fact]
    public void ChangingRedistributionVisibilityRestoresNativeSelection()
    {
        var runtime = new Runtime();
        using var tabs = new VanillaPanelTabs(runtime, VanillaUiSite.StationPanel, new[] { "Production", "Input" }, new object());
        runtime.Frame!(Open(1));
        tabs.Selected = 1;
        tabs.ShowRedistribution = true;
        Assert.Equal(0, tabs.Selected);
        Assert.Equal(0, runtime.Host.Selected);
        Assert.True(runtime.Host.ShowRedistribution);
        tabs.Selected = 1;
        tabs.ShowRedistribution = true;
        Assert.Equal(1, tabs.Selected);
    }

    sealed class Runtime : CellRowTests.PlainRuntime, IUiRuntime
    {
        internal Action<VanillaSiteSample>? Frame;
        internal readonly Host Host = new();
        internal readonly Lease Watch = new();
        public IRuntimeVanillaTabs? CreateVanillaTabs(VanillaUiSite site, string[] captions, Action<int> selected) => Host;
        public IDisposable? WatchSite(VanillaUiSite site, Action<VanillaSiteSample> onFrame) { Frame = onFrame; return Watch; }
    }
    sealed class Lease : IDisposable { internal int Disposals; public void Dispose() => Disposals++; }
    sealed class Host : IRuntimeVanillaTabs
    {
        internal int Selected, Disposals;
        public bool ShowRedistribution { get; set; }
        public void Update(in VanillaSiteSample sample) { }
        public void Select(int index) => Selected = index;
        public void Mount(IRuntimePanel panel) { }
        public void Dispose() => Disposals++;
    }
}
