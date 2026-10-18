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

    static VanillaSiteSample Open(int id, VanillaPanelKind kind = VanillaPanelKind.Container) => new(true, id, true, false,
        target: new VanillaPanelTarget(kind, id, 1, id, 1));

    [Fact]
    public void ChangingRedistributionVisibilityRestoresNativeSelection()
    {
        var runtime = new Runtime();
        using var tabs = new VanillaPanelTabs(runtime, VanillaUiSite.StationPanel, new[] { "Production", "Input" }, new object());
        runtime.Frame!(Open(1, VanillaPanelKind.Workstation));
        tabs.Selected = 1;
        tabs.ShowRedistribution = true;
        Assert.Equal(0, tabs.Selected);
        Assert.Equal(0, runtime.Host.Selected);
        Assert.True(runtime.Host.ShowRedistribution);
        tabs.Selected = 1;
        tabs.ShowRedistribution = true;
        Assert.Equal(1, tabs.Selected);
    }

    [Fact]
    public void DisablingTheSelectedTabFallsBackToTheNativeTabAndRefusesSelection()
    {
        var runtime = new Runtime();
        using var tabs = new VanillaPanelTabs(runtime, VanillaUiSite.StationPanel, new[] { "Production", "Input", "Output" }, new object());
        runtime.Frame!(Open(1, VanillaPanelKind.Workstation));
        var picks = new List<int>();
        tabs.SelectionChanged += picks.Add;
        tabs.Selected = 2;
        tabs.SetTabEnabled(2, false, "Needs power");
        Assert.Equal(0, tabs.Selected);
        Assert.Equal(0, runtime.Host.Selected);
        Assert.Equal(new[] { 2, 0 }, picks);
        Assert.False(tabs.IsTabEnabled(2));
        Assert.Equal("Needs power", tabs.TabReason(2));
        Assert.Equal((2, false, "Needs power"), runtime.Host.States[^1]);

        // A click (the runtime's selected callback) or a consumer cannot select it.
        runtime.Selected!(2);
        tabs.Selected = 2;
        Assert.Equal(0, tabs.Selected);
        Assert.Equal(new[] { 2, 0 }, picks);

        tabs.SetTabEnabled(2, true, "ignored while enabled");
        Assert.True(tabs.IsTabEnabled(2));
        Assert.Equal("", tabs.TabReason(2));
        Assert.Equal((2, true, ""), runtime.Host.States[^1]);
        tabs.Selected = 2;
        Assert.Equal(2, tabs.Selected);
    }

    [Fact]
    public void DisablingTheNativeTabMovesToTheFirstEnabledTab()
    {
        var runtime = new Runtime();
        using var tabs = new VanillaPanelTabs(runtime, VanillaUiSite.ContainerPanel, new[] { "Inventory", "Input", "Output" }, new object());
        runtime.Frame!(Open(1));
        var picks = new List<int>();
        tabs.SelectionChanged += picks.Add;
        tabs.SetTabEnabled(1, false, "Locked");
        Assert.Empty(picks); // not the selected tab
        tabs.SetTabEnabled(0, false, "Locked");
        Assert.Equal(2, tabs.Selected);
        Assert.Equal(2, runtime.Host.Selected);
        Assert.Equal(new[] { 2 }, picks);
        // Every tab disabled: back to the game's own view.
        tabs.SetTabEnabled(2, false, "");
        Assert.Equal(0, tabs.Selected);
        Assert.Equal(new[] { 2, 0 }, picks);
    }

    [Fact]
    public void ANewTargetOpensOnTheFirstEnabledTabAfterPanelChanged()
    {
        var runtime = new Runtime();
        using var tabs = new VanillaPanelTabs(runtime, VanillaUiSite.ContainerPanel, new[] { "Inventory", "Input" }, new object());
        var order = new List<string>();
        tabs.PanelChanged += e => order.Add("panel " + e.Change);
        tabs.SelectionChanged += i => order.Add("select " + i);
        tabs.SetTabEnabled(0, false, "Read only");
        runtime.Frame!(Open(1));
        Assert.Equal(1, tabs.Selected);
        Assert.Equal(1, runtime.Host.Selected);
        runtime.Frame(Open(2));
        runtime.Frame(default);
        Assert.Equal(new[] { "panel Opened", "select 1", "panel TargetChanged", "select 1", "panel Closed" }, order);
    }

    [Fact]
    public void TabStateIgnoresOutOfRangeIndexesAndDisposal()
    {
        var runtime = new Runtime();
        var tabs = new VanillaPanelTabs(runtime, VanillaUiSite.ContainerPanel, new[] { "Inventory", "Input" }, new object());
        tabs.SetTabEnabled(-1, false, "x");
        tabs.SetTabEnabled(2, false, "x");
        Assert.Empty(runtime.Host.States);
        Assert.True(tabs.IsTabEnabled(5));
        tabs.SetTabEnabled(1, false, "x");
        tabs.SetTabEnabled(1, false, "x"); // unchanged: not sent again
        Assert.Single(runtime.Host.States);
        tabs.Dispose();
        tabs.SetTabEnabled(1, true);
        Assert.Single(runtime.Host.States);
    }

    [Fact]
    public void ChangingRedistributionVisibilityAvoidsADisabledNativeTab()
    {
        var runtime = new Runtime();
        using var tabs = new VanillaPanelTabs(runtime, VanillaUiSite.StationPanel, new[] { "Production", "Input", "Output" }, new object());
        runtime.Frame!(Open(1, VanillaPanelKind.Workstation));
        tabs.Selected = 2;
        tabs.SetTabEnabled(0, false, "");
        tabs.ShowRedistribution = true;
        Assert.Equal(1, tabs.Selected);
        Assert.Equal(1, runtime.Host.Selected);
    }

    [Theory]
    [InlineData(0, new[] { false, false }, 0)]
    [InlineData(1, new[] { false, true }, 0)]
    [InlineData(0, new[] { true, false, false }, 1)]
    [InlineData(2, new[] { true, true, false }, 2)]
    [InlineData(1, new[] { true, true, true }, 0)]
    [InlineData(-1, new[] { false, false }, 0)]
    public void FallbackPrefersTheSelectionThenTabZeroThenTheFirstEnabled(int selected, bool[] disabled, int expected)
        => Assert.Equal(expected, VanillaPanelTabFallback.Resolve(selected, disabled));

    [Fact]
    public void TheRedistributionTabFollowsTheNativeTabSoItsClickCannotDesyncTheSelection()
    {
        // Two captions: strip index 2 is Redistribution. Its click would select the game's view (tab 0);
        // with tab 0 disabled the plugin refuses that, so the strip must refuse the click too.
        Assert.False(VanillaPanelTabFallback.IsOff(2, new[] { false, true }));
        Assert.True(VanillaPanelTabFallback.IsOff(2, new[] { true, false }));
        Assert.True(VanillaPanelTabFallback.IsOff(1, new[] { false, true }));
        Assert.False(VanillaPanelTabFallback.IsOff(-1, new[] { true, true }));

        // The plugin side agrees: a disabled tab 0 is never selected, so the strip never shows Redistribution
        // while the plugin believes a custom tab is up.
        var runtime = new Runtime();
        using var tabs = new VanillaPanelTabs(runtime, VanillaUiSite.StationPanel, new[] { "Production", "Input" }, new object());
        runtime.Frame!(Open(1, VanillaPanelKind.Workstation));
        tabs.Selected = 1;
        tabs.SetTabEnabled(0, false, "Managed by the mod");
        runtime.Selected!(0);
        Assert.Equal(1, tabs.Selected);
        Assert.Equal(1, runtime.Host.Selected);
    }

    [Fact]
    public void WithEveryTabDisabledNoTabIsHighlighted()
    {
        var all = new[] { true, true };
        Assert.False(VanillaPanelTabFallback.Highlights(0, 0, all));
        Assert.False(VanillaPanelTabFallback.Highlights(2, 2, all)); // Redistribution follows tab 0
        Assert.True(VanillaPanelTabFallback.Highlights(1, 1, new[] { true, false }));
        Assert.False(VanillaPanelTabFallback.Highlights(0, 1, new[] { false, false }));
    }

    [Fact]
    public void DisabledLookIsTheDonorsOnlyWhenItsDisabledStateDrawsDifferently()
    {
        var white = (1f, 1f, 1f, 1f);
        var gray = (.78f, .78f, .78f, .5f);
        var clear = (1f, 1f, 1f, 0f);
        Assert.True(VanillaTabDisabledLook.Draws(1, white, gray, false, false));
        Assert.False(VanillaTabDisabledLook.Draws(1, white, white, false, false));
        Assert.False(VanillaTabDisabledLook.Draws(1, clear, (0f, 0f, 0f, 0f), false, false));
        Assert.True(VanillaTabDisabledLook.Draws(2, white, white, true, false));
        Assert.False(VanillaTabDisabledLook.Draws(2, white, gray, false, false));
        Assert.True(VanillaTabDisabledLook.Draws(3, white, white, false, true));
        Assert.False(VanillaTabDisabledLook.Draws(0, white, gray, true, true));
        Assert.InRange(VanillaTabDisabledLook.FaintAlpha, .2f, .8f);
    }

    /// <summary>A plugin's own implementation compiled before SetTabEnabled existed.</summary>
    sealed class OlderTabs : IVanillaPanelTabs
    {
        public int Selected { get; set; }
        public bool ShowRedistribution { get; set; }
        public void Mount(IUiPanel panel) { }
        public event Action<VanillaPanelEvent>? PanelChanged { add { } remove { } }
        public event Action<int>? SelectionChanged { add { } remove { } }
        public void Dispose() { }
    }

    [Fact]
    public void SetTabEnabledIsADefaultMemberSoOlderImplementationsStillWork()
    {
        IVanillaPanelTabs tabs = new OlderTabs();
        tabs.SetTabEnabled(1, false, "x");
        Assert.True(tabs.IsTabEnabled(1));
        var method = typeof(IVanillaPanelTabs).GetMethod(nameof(IVanillaPanelTabs.SetTabEnabled))!;
        Assert.False(method.IsAbstract);
        Assert.False(typeof(IVanillaPanelTabs).GetMethod(nameof(IVanillaPanelTabs.IsTabEnabled))!.IsAbstract);
    }

    sealed class Runtime : CellRowTests.PlainRuntime, IUiRuntime
    {
        internal Action<VanillaSiteSample>? Frame;
        internal readonly Host Host = new();
        internal readonly Lease Watch = new();
        internal Action<int>? Selected;
        public IRuntimeVanillaTabs? CreateVanillaTabs(VanillaUiSite site, string[] captions, Action<int> selected) { Selected = selected; return Host; }
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
        internal readonly List<(int, bool, string)> States = new();
        public void SetTabEnabled(int index, bool enabled, string reason) => States.Add((index, enabled, reason));
        public void Dispose() => Disposals++;
    }
}
