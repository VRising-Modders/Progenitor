using System;
using System.Collections.Generic;
using System.Linq;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Which game windows count as <see cref="VanillaUiSite.StationPanel"/>, which open window wins,
/// what target it reports, and where <c>AttachPanelTabs</c> draws. Game-free.
/// </summary>
[Collection("PluginLogger")]
public sealed class VanillaStationWindowsTests
{
    // The resolver fills its scan in this order (VanillaPanelSiteResolver.EnsureScan): keep in step.
    private static readonly VanillaPanelKind[] ScanOrder =
    {
        VanillaPanelKind.Workstation, VanillaPanelKind.RefinementStation, VanillaPanelKind.PrisonCell,
        VanillaPanelKind.SalvageStation, VanillaPanelKind.UnitStation,
    };

    [Fact]
    public void CoveredWindowsAreInTheResolverScanOrder()
    {
        Assert.Equal(ScanOrder, VanillaStationWindows.Covered.Select(w => w.Kind).ToArray());
    }

    [Theory]
    [InlineData("WorkstationSubMenu", VanillaPanelKind.Workstation)]
    [InlineData("RefinementstationSubMenu", VanillaPanelKind.RefinementStation)]
    [InlineData("PrisonSubMenu", VanillaPanelKind.PrisonCell)]
    [InlineData("SalvagestationSubMenu", VanillaPanelKind.SalvageStation)]
    [InlineData("UnitSpawnerstationSubMenu", VanillaPanelKind.UnitStation)]
    [InlineData("ContainerSubMenu", VanillaPanelKind.None)]
    [InlineData("ServantCoffinstationSubMenu", VanillaPanelKind.None)]
    [InlineData("ResearchstationSubMenu", VanillaPanelKind.None)]
    [InlineData("CastleHeartSubMenu", VanillaPanelKind.None)]
    [InlineData("", VanillaPanelKind.None)]
    [InlineData(null, VanillaPanelKind.None)]
    public void SubMenuClassifiesToItsKind(string? subMenu, VanillaPanelKind expected)
    {
        Assert.Equal(expected, VanillaStationWindows.KindOfSubMenu(subMenu));
    }

    [Fact]
    public void EveryCoveredWindowNamesItsMapperAndExamplesAndNoWindowIsInBothLists()
    {
        foreach (var w in VanillaStationWindows.Covered)
        {
            Assert.True(w.Covered);
            Assert.EndsWith("MenuMapper", w.Mapper);
            Assert.EndsWith("SubMenu", w.SubMenu);
            Assert.False(string.IsNullOrWhiteSpace(w.Examples));
            Assert.False(string.IsNullOrWhiteSpace(w.Note));
        }

        foreach (var w in VanillaStationWindows.NotCovered)
        {
            Assert.False(w.Covered);
            Assert.False(w.HostsTabs);
            Assert.False(string.IsNullOrWhiteSpace(w.Note));
            Assert.Equal(VanillaPanelKind.None, VanillaStationWindows.KindOfSubMenu(w.SubMenu));
        }

        var covered = VanillaStationWindows.Covered.Select(w => w.SubMenu).ToHashSet();
        Assert.Empty(VanillaStationWindows.NotCovered.Where(w => covered.Contains(w.SubMenu)));
        Assert.Equal(VanillaStationWindows.Covered.Length, covered.Count);
    }

    [Fact]
    public void AnOpenPrisonCellResolvesToTheCellStationEntity()
    {
        var states = Closed();
        states[2] = new StationWindowState(VanillaPanelKind.PrisonCell, shown: true, targetIndex: 4711, targetVersion: 3);

        Assert.True(VanillaStationWindows.TryResolve(states, out int index, out var target));
        Assert.Equal(2, index);
        Assert.Equal(VanillaPanelKind.PrisonCell, target.Kind);
        Assert.Equal(4711, target.EntityIndex);
        Assert.Equal(3, target.EntityVersion);
    }

    [Theory]
    [InlineData(0, VanillaPanelKind.Workstation)]
    [InlineData(1, VanillaPanelKind.RefinementStation)]
    [InlineData(2, VanillaPanelKind.PrisonCell)]
    [InlineData(3, VanillaPanelKind.SalvageStation)]
    [InlineData(4, VanillaPanelKind.UnitStation)]
    public void EachCoveredWindowReportsItsOwnKindAndMapperTarget(int slot, VanillaPanelKind kind)
    {
        var states = Closed();
        states[slot] = new StationWindowState(kind, true, 100 + slot, 1);

        Assert.True(VanillaStationWindows.TryResolve(states, out int index, out var target));
        Assert.Equal(slot, index);
        Assert.Equal(kind, target.Kind);
        Assert.Equal(100 + slot, target.EntityIndex);
    }

    [Fact]
    public void AShownWindowWithoutATargetIsClosed()
    {
        var states = Closed();
        states[2] = new StationWindowState(VanillaPanelKind.PrisonCell, true, 0, 0);

        Assert.Equal(-1, VanillaStationWindows.Pick(states));
        Assert.False(VanillaStationWindows.TryResolve(states, out _, out var target));
        Assert.Equal(default, target);
    }

    [Fact]
    public void AHiddenWindowWithAStaleTargetIsClosed()
    {
        var states = Closed();
        states[3] = new StationWindowState(VanillaPanelKind.SalvageStation, false, 55, 2);
        Assert.Equal(-1, VanillaStationWindows.Pick(states));
    }

    [Fact]
    public void WhenTwoWindowsOverlapForAFrameTheEarlierInOrderWins()
    {
        var states = Closed();
        states[0] = new StationWindowState(VanillaPanelKind.Workstation, true, 10, 1);
        states[2] = new StationWindowState(VanillaPanelKind.PrisonCell, true, 20, 1);
        Assert.Equal(0, VanillaStationWindows.Pick(states));
    }

    [Fact]
    public void AKindThatIsNotAStationNeverPicks()
    {
        var states = new[]
        {
            new StationWindowState(VanillaPanelKind.Container, true, 1, 1),
            new StationWindowState(VanillaPanelKind.CastleHeart, true, 2, 1),
            new StationWindowState(VanillaPanelKind.None, true, 3, 1),
        };
        Assert.Equal(-1, VanillaStationWindows.Pick(states));
        Assert.Equal(-1, VanillaStationWindows.Pick(null!));
    }

    [Theory]
    [InlineData(VanillaUiSite.StationPanel, VanillaPanelKind.Workstation, true)]
    [InlineData(VanillaUiSite.StationPanel, VanillaPanelKind.RefinementStation, true)]
    [InlineData(VanillaUiSite.StationPanel, VanillaPanelKind.PrisonCell, true)]
    [InlineData(VanillaUiSite.StationPanel, VanillaPanelKind.SalvageStation, true)]
    [InlineData(VanillaUiSite.StationPanel, VanillaPanelKind.UnitStation, false)]
    [InlineData(VanillaUiSite.StationPanel, VanillaPanelKind.Container, false)]
    [InlineData(VanillaUiSite.ContainerPanel, VanillaPanelKind.Container, true)]
    [InlineData(VanillaUiSite.ContainerPanel, VanillaPanelKind.PrisonCell, false)]
    [InlineData(VanillaUiSite.InventoryPanel, VanillaPanelKind.PrisonCell, false)]
    [InlineData(VanillaUiSite.CastleHeartPanel, VanillaPanelKind.CastleHeart, false)]
    public void TabsDrawOnlyWhereTheWindowHasATabRow(VanillaUiSite site, VanillaPanelKind kind, bool hosts)
    {
        Assert.Equal(hosts, VanillaStationWindows.HostsTabs(site, kind));
    }

    [Fact]
    public void EveryWindowThatHostsTabsHasTheGameTabRow()
    {
        foreach (var w in VanillaStationWindows.Covered)
            Assert.Equal(w.RoutingTabs, w.HostsTabs);
    }

    [Fact]
    public void PrisonCellTabsOpenOnTheCellAndUnitStationTabsStayClosed()
    {
        var runtime = new Runtime();
        using var tabs = new VanillaPanelTabs(runtime, VanillaUiSite.StationPanel, new[] { "Production", "Input", "Output" }, new object());
        var events = new List<VanillaPanelEvent>();
        tabs.PanelChanged += events.Add;

        var cell = new VanillaPanelTarget(VanillaPanelKind.PrisonCell, 4711, 3, 40, 1, 1);
        runtime.Frame!(Open(1, cell));
        Assert.Single(events);
        Assert.Equal(VanillaPanelChange.Opened, events[0].Change);
        Assert.Equal(cell, events[0].Target);
        Assert.True(runtime.Host.LastResolved);

        // The player walks from the cell to a tomb: the tabs see the cell close and nothing open.
        runtime.Frame(Open(2, new VanillaPanelTarget(VanillaPanelKind.UnitStation, 812, 1, 41, 1, 1)));
        Assert.Equal(2, events.Count);
        Assert.Equal(VanillaPanelChange.Closed, events[1].Change);
        Assert.False(runtime.Host.LastResolved);

        runtime.Frame(Open(2, new VanillaPanelTarget(VanillaPanelKind.UnitStation, 812, 1, 41, 1, 1)));
        Assert.Equal(2, events.Count);
        Assert.False(runtime.Host.LastResolved);
    }

    [Fact]
    public void SalvageStationTabsOpenAndReportTheStation()
    {
        var runtime = new Runtime();
        using var tabs = new VanillaPanelTabs(runtime, VanillaUiSite.StationPanel, new[] { "Production", "Input" }, new object());
        VanillaPanelEvent? seen = null;
        tabs.PanelChanged += e => seen = e;
        var devourer = new VanillaPanelTarget(VanillaPanelKind.SalvageStation, 77, 2, 42, 1, 1);
        runtime.Frame!(Open(5, devourer));
        Assert.NotNull(seen);
        Assert.Equal(VanillaPanelChange.Opened, seen!.Value.Change);
        Assert.Equal(VanillaPanelKind.SalvageStation, seen.Value.Target.Kind);
        Assert.Equal(77, seen.Value.Target.EntityIndex);
    }

    private static StationWindowState[] Closed() =>
        ScanOrder.Select(k => new StationWindowState(k, false, 0, 0)).ToArray();

    private static VanillaSiteSample Open(int id, VanillaPanelTarget target) => new(true, id, true, false, target: target);

    private sealed class Runtime : CellRowTests.PlainRuntime, IUiRuntime
    {
        internal Action<VanillaSiteSample>? Frame;
        internal readonly Host Host = new();
        public IRuntimeVanillaTabs? CreateVanillaTabs(VanillaUiSite site, string[] captions, Action<int> selected) => Host;
        public IDisposable? WatchSite(VanillaUiSite site, Action<VanillaSiteSample> onFrame) { Frame = onFrame; return null; }
    }

    private sealed class Host : IRuntimeVanillaTabs
    {
        internal bool LastResolved;
        public bool ShowRedistribution { get; set; }
        public void Update(in VanillaSiteSample sample) => LastResolved = sample.Resolved;
        public void Select(int index) { }
        public void Mount(IRuntimePanel panel) { }
        public void Dispose() { }
    }
}
