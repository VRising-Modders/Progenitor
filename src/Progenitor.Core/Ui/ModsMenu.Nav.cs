using System;
using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>
/// NAV1: the Mods workspace as a controller sees it (<see cref="UiNav"/>): two panes, the left list and the page.
/// A in the list opens the row's page and the focus follows; B on the page goes back to the list, and B in the list
/// is Escape. The triggers step through the tabs in strip order.
/// </summary>
public static partial class ModsMenu
{
    /// <summary>The open reason of a tab a controller's trigger selected (the transition log names it).</summary>
    internal const string ReasonNavTab = "nav-tab";

    /// <summary>The workspace as a navigation surface, or null when it is not open.</summary>
    internal static NavSurface? NavSurface()
    {
        if (!IsOpen) return null;
        var list = new List<UiHandle>();
        AddNavChildren(list, Sidebar.Live);
        var page = new List<UiHandle>();
        AddNavChildren(page, Owned.Live);
        AddNavChildren(page, HostedSettingsPanel());
        string tab = _tabState == null ? "" : ModsTabState.StatusName(_tabState.Selected);
        return new NavSurface("workspace:" + tab, new IReadOnlyList<UiHandle>[] { list, page })
        {
            StartPane = 0,
            MasterDetail = true,
            Tab = NavTab,
        };
    }

    /// <summary>Whether <paramref name="panel"/> is one of the workspace's own panels (the strip, the list, the page).</summary>
    internal static bool IsWorkspacePanel(IUiPanel? panel)
    {
        if (panel == null) return false;
        return ReferenceEquals(panel, Strip.Live) || ReferenceEquals(panel, Sidebar.Live)
            || ReferenceEquals(panel, Owned.Live) || ReferenceEquals(panel, HostedSettingsPanel());
    }

    /// <summary>The previous (-1) or next (+1) tab in strip order; nothing past either end. True when the tab moved.</summary>
    internal static bool NavTab(int delta)
    {
        if (!IsOpen || delta == 0) return false;
        EnsureTabState();
        var order = ModsTabStrip.Order;
        int at = Array.IndexOf(order, _tabState!.Selected);
        int next = at + (delta > 0 ? 1 : -1);
        if (at < 0 || next < 0 || next >= order.Length) return false;
        Plugin.Logger?.LogDiag("Ui: nav tab " + ModsTabState.StatusName(order[next]) + " reason=" + ReasonNavTab);
        SelectWorkspaceTab(order[next]);
        return true;
    }

    private static void AddNavChildren(List<UiHandle> list, IUiPanel? panel)
    {
        if (panel is not UiPanelHandle host || host.IsDisposed) return;
        foreach (var child in host.ChildrenSnapshot())
            if (child != null && !child.IsDisposed) list.Add(child);
    }
}
