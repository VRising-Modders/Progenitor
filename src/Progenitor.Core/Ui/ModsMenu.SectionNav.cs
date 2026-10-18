using System;
using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>
/// Three homes (docs/settings-organization.md, "Three homes (2026-10-06)"): the Server tab, and the left lists of
/// Preferences, Server and Progenitor's page. A left-list click scrolls the content to its group, opens it when it is a
/// closed drawer, and highlights the item; the highlight follows the scroll (<see cref="TickSectionNav"/>). Each jump is a
/// place in the back/forward history (the tab and the group).
/// </summary>
public static partial class ModsMenu
{
    private static ServerTabPane? _serverPane;

    /// <summary>The left-list highlight per tab (Preferences, Server, and Mods for Progenitor's page).</summary>
    private static readonly Dictionary<ModsTab, SectionNavTracker> SectionTrackers = new();

    /// <summary>The left-list rows drawn for Preferences and Server: (row, tab, group).</summary>
    private static readonly List<(IUiCellRow Row, ModsTab Tab, string Group)> _sectionNavRows = new();

    /// <summary>SERVEREMPTY1: the Server tab's dimmed left-list rows in its empty states: (row, group). No highlight, no jump.</summary>
    private static readonly List<(IUiCellRow Row, string Group)> _dimmedNavRows = new();

    /// <summary>Tests only: the Last server facts (else none in tests; live: the file, Bootstrap and the join flow).</summary>
    internal static Func<Progenitor.Networking.LastServerFacts>? LastServerFactsForTests;

    /// <summary>Tests only: what the Last server Join does (else the join flow's reconnect, after closing the Mods window).</summary>
    internal static Func<Progenitor.Networking.LastServerRecord, string>? LastServerJoinForTests;

    /// <summary>A jump waiting for its tab's content to be drawn: (tab, group).</summary>
    private static (ModsTab Tab, string Group)? _sectionJump;

    /// <summary>What the last jump measured (readback <c>sectionJump</c> on <c>nav_state</c>).</summary>
    private static Dictionary<string, object?>? _sectionJumped;

    /// <summary>Progenitor's page reached at a group through its left list, until the Mods page changes.</summary>
    private static string _navModsGroupPlugin = "";

    private static string _navModsGroup = "";

    private static float _sectionOffsetSeen = float.NaN;

    private static SectionNavTracker TrackerFor(ModsTab tab)
    {
        if (!SectionTrackers.TryGetValue(tab, out var tracker))
        {
            tracker = new SectionNavTracker();
            SectionTrackers[tab] = tracker;
        }

        return tracker;
    }

    /// <summary>The groups the left list shows on <paramref name="tab"/> now.</summary>
    internal static IReadOnlyList<string> SectionGroupsFor(ModsTab tab) => tab switch
    {
        ModsTab.Preferences => ConfigurationPane.ConfigGroups,
        ModsTab.Server => ServerTabPane.NavGroupsFor(_serverPane?.State ?? ServerTabPane.StateOf(SafeServerContext())),
        ModsTab.Mods => ProgenitorPage.NavGroups,
        _ => Array.Empty<string>(),
    };

    private static SettingsScopeContext SafeServerContext()
    {
        try { return SettingsScopeContextFor(SettingsPanel.PluginId); }
        catch { return new SettingsScopeContext(); }
    }

    // ---- the Server tab ---------------------------------------------------------------------------

    /// <summary>Three homes: draw the Server tab — its left list of groups, then the content (<see cref="ServerTabPane"/>).</summary>
    private static void BuildServerPanes()
    {
        EnsureBindings();
        _serverPane?.Release();
        var pane = new ServerTabPane(() => SettingsScopeContextFor(SettingsPanel.PluginId), LastServerFactsNow, JoinLastServerFromTab)
        {
#if !PROGENITOR_TESTS
            RouteProbe = () => CurrentRouteDecision().Route,
#endif
        };
        _serverPane = pane;
        var state = ServerTabPane.StateOf(SafeServerContext());

        ParkSidebar(Sidebar.Take());
        var sidebar = Ui.Panel(ServerTabPane.Title, Owner, SettingsPanel.PluginId);
        sidebar.CaptureMode = UiCaptureMode.WhileShown;
        Ui.ConfigureMenuPanel(sidebar, sidebar: true);
        Sidebar.Adopt(sidebar);
        if (sidebar.Enabled)
        {
            Ui.PinMenuRows(sidebar, MenuRowPin.Content);
            AddSectionNavRows(sidebar, ModsTab.Server, ServerTabPane.NavGroupsFor(state));
            AddDimmedNavRows(sidebar, ServerTabPane.DimmedNavGroupsFor(state));
            Ui.PinMenuRows(sidebar, MenuRowPin.Bottom);
            Ui.StyleMenuRow(sidebar, MenuRowTone.Chrome);
            var close = sidebar.Button(InstallModsCopy.CloseMods);
            close.OnClick(() => Close(ReasonUserButton));
            close.GameButton();
        }

        ShowWorkspacePanel(sidebar);

        CloseContent();
        var panel = Ui.Panel(ServerTabPane.Title, Owner, SettingsPanel.PluginId);
        panel.CaptureMode = UiCaptureMode.WhileShown;
        Ui.ConfigureMenuPanel(panel, sidebar: false);
        Owned.Adopt(panel);
        if (panel.Enabled)
        {
            try
            {
                pane.Build(panel, BuildServerTabSections, view =>
                {
                    view.DrawerScope = SettingsDrawers.ScopeServer;
                    view.DrawerToggled = (_, _) => NoteSectionScrollMoved();
                });
            }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: the Server tab threw: " + e.Message); }
        }

        ShowWorkspacePanel(panel);
        ConsumeSectionJump(ModsTab.Server);
        NoteNavigation();
    }

    /// <summary>The Server tab after a server answer: rebuilt when what it draws changed, else re-evaluated in place.</summary>
    private static bool NoteServerTabScopeChanged(string reason)
    {
        if (!IsOpen || _tabState?.Selected != ModsTab.Server || _serverPane == null) return false;
        try
        {
            if (_serverPane.NeedsRebuild())
            {
                Plugin.Logger?.LogInfo("Ui: the Server tab rebuilt for the server's answer (" + reason + ")");
                CaptureScroll();
                float content = _contentScroll, offset = _contentOffsetY;
                BuildServerPanes();
                _contentScroll = content;
                _contentOffsetY = offset;
                RestoreScroll();
                return true;
            }

            _serverPane.SettingsView?.Reevaluate();
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: the Server tab's refresh threw: " + e.Message);
            return false;
        }
    }

    /// <summary>SERVEREMPTY1: the Last server row's facts now.</summary>
    private static Progenitor.Networking.LastServerFacts LastServerFactsNow()
    {
        var probe = LastServerFactsForTests;
        if (probe != null) return probe();
#if PROGENITOR_TESTS
        return new Progenitor.Networking.LastServerFacts();
#else
        return new Progenitor.Networking.LastServerFacts
        {
            Record = Progenitor.Networking.JoinFlowClient.ReadLastServer(),
            HostingPrivateGame = BootstrapLink.IsHostingPrivateGame,
            JoinInProgress = Progenitor.Networking.JoinFlowClient.JoinInProgress,
        };
#endif
    }

    /// <summary>
    /// SERVEREMPTY1: the Last server row's <b>Join</b>: Join now's reconnect to the recorded server (the Joining page, or the
    /// game's password prompt for a server joined with a password). Once it started, the Mods window closes so the page or
    /// the prompt can be seen.
    /// </summary>
    private static string JoinLastServerFromTab(Progenitor.Networking.LastServerRecord record)
    {
        var hook = LastServerJoinForTests;
        string result = hook != null ? hook(record) : Progenitor.Networking.JoinFlowClient.JoinLastServer(record);
        if (string.Equals(result, "joining", StringComparison.Ordinal) && IsOpen) Close(ReasonServerJoin);
        return result;
    }

    /// <summary>The close reason when the Server tab's Last server Join starts a join.</summary>
    internal const string ReasonServerJoin = "server-join";

    // ---- the left lists ---------------------------------------------------------------------------

    /// <summary>
    /// SERVEREMPTY1: the Server tab's groups dimmed in its empty states: the left list's row look in the muted tone, a plain
    /// row (no hover, no click, never highlighted), keyed <c>nav/&lt;slug&gt;</c> as the live rows are.
    /// </summary>
    private static void AddDimmedNavRows(IUiPanel sidebar, IReadOnlyList<string> groups)
    {
        _dimmedNavRows.RemoveAll(r => r.Row.IsDisposed);
        if (groups.Count == 0) return;
        float width = SidebarRowWidth();
        foreach (string group in groups)
        {
            var row = sidebar.CellRow(SectionNav.DimmedRow(group, width));
            _dimmedNavRows.Add((row, group));
        }
    }

    /// <summary>Draw <paramref name="tab"/>'s left-list rows (Preferences, Server), highlighted as the tracker says.</summary>
    private static void AddSectionNavRows(IUiPanel sidebar, ModsTab tab, IReadOnlyList<string> groups)
    {
        _sectionNavRows.RemoveAll(r => r.Tab == tab || r.Row.IsDisposed);
        var tracker = TrackerFor(tab);
        tracker.Reset();
        string jumped = _tabState?.Page(tab).Selection is { Kind: ModsIdentityKind.ConfigGroup } g ? g.Value : "";
        float width = SidebarRowWidth();
        foreach (string group in groups)
        {
            string captured = group;
            // MENUKIT2 round D: the Mods list's row look, not a full-width flat button.
            var row = sidebar.CellRow(SectionNav.Row(group, width));
            row.SetSelected(string.Equals(group, jumped, StringComparison.OrdinalIgnoreCase));
            row.OnRowClick(() => JumpToGroup(tab, captured));
            _sectionNavRows.Add((row, tab, group));
        }
    }

    /// <summary>
    /// Progenitor's groups under its entry in the Mods list (the Required group), shown while its page is open. The rows
    /// are list extras, so a kept list keeps them.
    /// </summary>
    private static void AddProgenitorNavRows(IUiPanel panel, float rowWidth)
    {
        foreach (string group in ProgenitorPage.NavGroups)
        {
            string captured = group;
            var row = panel.CellRow(SectionNav.Row(group, rowWidth, subRow: true));
            row.OnRowClick(() => JumpToProgenitorGroup(captured));
            _listExtras.Add((row, SectionNav.RowSectionNav, () => NavRowReadback(ModsTab.Mods, captured)));
        }

        RefreshProgenitorNavRows();
    }

    /// <summary>Show Progenitor's left-list rows only while its page is the one open, and paint the highlight.</summary>
    private static void RefreshProgenitorNavRows()
    {
        bool shown = string.Equals(_selected, SettingsPanel.PluginId, StringComparison.OrdinalIgnoreCase)
            && (_tabState == null || _tabState.Selected == ModsTab.Mods);
        string highlighted = TrackerFor(ModsTab.Mods).Highlighted;
        foreach (var (handle, key, _) in _listExtras)
        {
            if (!string.Equals(key, SectionNav.RowSectionNav, StringComparison.Ordinal) || handle is not IUiCellRow row || row.IsDisposed) continue;
            try
            {
                if (row.Visible != shown) row.Visible = shown;
                row.SetSelected(shown && SectionNav.Key(highlighted) == row.Key);
            }
            catch { /* a row that cannot repaint keeps its look */ }
        }
    }

    /// <summary>Paint every left-list row of <paramref name="tab"/> with its tracker's highlight.</summary>
    private static void PaintSectionNav(ModsTab tab)
    {
        if (tab == ModsTab.Mods)
        {
            RefreshProgenitorNavRows();
            return;
        }

        string highlighted = TrackerFor(tab).Highlighted;
        foreach (var (row, rowTab, group) in _sectionNavRows)
        {
            if (rowTab != tab || row.IsDisposed) continue;
            try { row.SetSelected(string.Equals(group, highlighted, StringComparison.OrdinalIgnoreCase)); }
            catch { /* keeps its look */ }
        }
    }

    private static Dictionary<string, object?> NavRowReadback(ModsTab tab, string group)
    {
        var tracker = TrackerFor(tab);
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["tab"] = ModsTabState.StatusName(tab),
            ["group"] = group,
            ["slug"] = SectionNav.Slug(group),
            ["highlighted"] = string.Equals(tracker.Highlighted, group, StringComparison.OrdinalIgnoreCase),
            ["jumped"] = string.Equals(tracker.Jumped, group, StringComparison.OrdinalIgnoreCase),
        };
    }

    /// <summary>The <c>widget_read</c> extra of a Preferences or Server left-list row, or null.</summary>
    private static Dictionary<string, object?>? DescribeSectionNavRow(IUiHandle handle)
    {
        foreach (var (row, tab, group) in _sectionNavRows)
            if (ReferenceEquals(row, handle))
                return new Dictionary<string, object?>(StringComparer.Ordinal) { [SectionNav.RowSectionNav] = NavRowReadback(tab, group) };
        foreach (var (row, group) in _dimmedNavRows)
        {
            if (!ReferenceEquals(row, handle)) continue;
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [SectionNav.RowSectionNav] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["tab"] = ModsTabState.StatusName(ModsTab.Server),
                    ["group"] = group,
                    ["slug"] = SectionNav.Slug(group),
                    ["highlighted"] = false,
                    ["jumped"] = false,
                    ["dimmed"] = true,
                },
            };
        }

        return null;
    }

    // ---- jumps ------------------------------------------------------------------------------------

    /// <summary>
    /// A left-list click (or <c>mods_ui section</c>): scroll <paramref name="tab"/>'s content to <paramref name="group"/>,
    /// open it when it is a closed drawer, highlight the item. Selects the tab first when another is shown. The one
    /// per-frame observer records the place (the tab and the group).
    /// </summary>
    internal static bool JumpToGroup(ModsTab tab, string group)
    {
        if (!IsOpen) return false;
        if (tab == ModsTab.Mods) return JumpToProgenitorGroup(group);
        EnsureTabState();
        string? resolved = SectionNav.Resolve(SectionGroupsFor(tab), group);
        if (resolved == null) return false;
        _tabState!.Page(tab).Selection = ModsWorkspaceIdentity.Config(resolved);
        _sectionJump = (tab, resolved);
        if (_tabState.Selected != tab) SelectWorkspaceTab(tab);
        else ConsumeSectionJump(tab);
        return true;
    }

    /// <summary>Progenitor's page's left list: open the page (the Required group too) and jump to <paramref name="group"/>.</summary>
    internal static bool JumpToProgenitorGroup(string group)
    {
        if (!IsOpen) return false;
        string? resolved = SectionNav.Resolve(ProgenitorPage.NavGroups, group);
        if (resolved == null) return false;
        EnsureTabState();
        string id = SettingsPanel.PluginId;
        _navModsGroupPlugin = id;
        _navModsGroup = resolved;
        _sectionJump = (ModsTab.Mods, resolved);
        bool onPage = _tabState!.Selected == ModsTab.Mods && string.Equals(_selected, id, StringComparison.OrdinalIgnoreCase) && _modPage != null;
        if (!onPage)
        {
            if (!_requiredExpanded)
            {
                _requiredExpanded = true;
                _tabState.Mods.RequiredGroupExpanded = true;
            }

            JumpToProgenitorPage();
        }

        ConsumeSectionJump(ModsTab.Mods);
        return true;
    }

    /// <summary>The heading <paramref name="group"/> scrolls to on <paramref name="tab"/>, or null when it is not drawn.</summary>
    private static IUiHandle? SectionHeading(ModsTab tab, string group) => tab switch
    {
        ModsTab.Preferences => _configPane?.GroupHeading(group),
        ModsTab.Server => _serverPane?.GroupHeading(group),
        ModsTab.Mods => _modPage?.SettingsView?.HeadingFor(group),
        _ => null,
    };

    private static ModSettingsView? SectionView(ModsTab tab) => tab switch
    {
        ModsTab.Preferences => _configPane?.SettingsView,
        ModsTab.Server => _serverPane?.SettingsView,
        ModsTab.Mods => _modPage?.SettingsView,
        _ => null,
    };

    /// <summary>
    /// After <paramref name="tab"/>'s content is drawn: a jump waiting for it opens its drawer, highlights its item and
    /// scrolls its heading to the top on the next frame (when the layout has settled).
    /// </summary>
    private static void ConsumeSectionJump(ModsTab tab)
    {
        if (_sectionJump is not { } jump || jump.Tab != tab) return;
        _sectionJump = null;
        string group = jump.Group;
        var view = SectionView(tab);
        bool opened = view?.DrawerOpen(group) == false && view.SetDrawerOpen(group, true);
        var heading = SectionHeading(tab, group);
        var tracker = TrackerFor(tab);
        tracker.Jump(group, ReadShowFrame());
        PaintSectionNav(tab);
        var record = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["tab"] = ModsTabState.StatusName(tab),
            ["group"] = group,
            ["found"] = heading != null,
            ["openedDrawer"] = opened,
            ["measured"] = false,
            ["landed"] = false,
        };
        _sectionJumped = record;
        if (heading == null) return;
        var panel = Owned.Held;
        void Reveal()
        {
            if (panel == null || panel.IsDisposed || heading.IsDisposed) return;
            if (UiReveal.TryReveal(panel, heading, heading, toTop: true, out float rowTop, out float viewportTop, out float viewportHeight))
            {
                record["measured"] = true;
                record["rowTop"] = Math.Round(rowTop, 1);
                record["viewportTop"] = Math.Round(viewportTop, 1);
                record["viewportHeight"] = Math.Round(viewportHeight, 1);
                record["landed"] = Math.Abs(rowTop - viewportTop) < 1f || viewportTop < rowTop;
            }
        }

        PostKeybindsStep(Reveal);
    }

    /// <summary>A drawer opened or closed: the next frame works out the group at the top again.</summary>
    internal static void NoteSectionScrollMoved() => _sectionOffsetSeen = float.NaN;

    /// <summary>The tab whose left list follows the scroll now, or null.</summary>
    private static ModsTab? SectionNavTab()
    {
        if (!IsOpen || _tabState == null) return null;
        var tab = _tabState.Selected;
        if (tab is ModsTab.Preferences or ModsTab.Server) return tab;
        if (tab == ModsTab.Mods && string.Equals(_selected, SettingsPanel.PluginId, StringComparison.OrdinalIgnoreCase) && _modPage != null)
            return ModsTab.Mods;
        return null;
    }

    /// <summary>
    /// Three homes, once a frame (from <see cref="TickNavigation"/>): when the content's scroll offset moved, the group at
    /// the top of the view becomes the highlighted left-list item. One offset read per frame; the headings are measured only
    /// when it moved.
    /// </summary>
    internal static void TickSectionNav()
    {
        if (SectionNavTab() is not { } tab) return;
        var panel = Owned.Held;
        if (panel == null || panel.IsDisposed) return;
        if (!UiReveal.TryOffset(panel, out float offset)) return;
        var tracker = TrackerFor(tab);
        bool forced = float.IsNaN(_sectionOffsetSeen);
        _sectionOffsetSeen = offset;
        if (!tracker.Observe(offset, ReadShowFrame()) && !(forced && !tracker.Pinned)) return;
        if (tracker.Follow(GroupAtTop(tab, panel))) PaintSectionNav(tab);
    }

    /// <summary>The left-list group whose heading is at the top of <paramref name="panel"/>'s view, or null.</summary>
    private static string? GroupAtTop(ModsTab tab, IUiPanel panel)
    {
        var groups = SectionGroupsFor(tab);
        var tops = new List<float?>(groups.Count);
        foreach (string group in groups)
        {
            var heading = SectionHeading(tab, group);
            float? top = null;
            if (heading is { IsDisposed: false, Visible: true } && UiReveal.TryAnchor(panel, heading, write: false, 0f, out float rowY, out _))
                top = rowY;
            tops.Add(top);
        }

        int index = SectionNav.ActiveIndex(tops);
        return index >= 0 ? groups[index] : null;
    }

    /// <summary>
    /// <c>mods_ui section {tab?, group}</c>: the left-list click, by name or slug, on the given tab (default: the one shown).
    /// </summary>
    public static object SectionOp(string? tab, string? group)
    {
        if (!IsOpen) throw new ArgumentException(WorkspaceClosedMessage);
        EnsureTabState();
        ModsTab target = _tabState!.Selected;
        if (!string.IsNullOrWhiteSpace(tab) && !ModsTabState.TryParseStatusName(tab, out target))
            throw new ArgumentException("args.tab must be mods, server or preferences (got '" + tab + "')");
        var groups = SectionGroupsFor(target);
        if (groups.Count == 0)
            throw new ArgumentException("the " + ModsTabState.StatusName(target) + " tab has no left-list groups now");
        if (SectionNav.Resolve(groups, group) == null)
            throw new ArgumentException("args.group must be one of: " + string.Join(", ", groups) + " (got '" + group + "')");
        bool jumped = JumpToGroup(target, group!);
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["tab"] = ModsTabState.StatusName(target),
            ["group"] = SectionNav.Resolve(groups, group),
            ["jumped"] = jumped,
            ["jump"] = _sectionJumped,
            ["readback"] = "next-drain",
        };
    }

    /// <summary>
    /// <c>mods_ui drawer {group, open?}</c>: toggle (or set) a drawer of the tab shown, as a click on its header does.
    /// </summary>
    public static object DrawerOp(string? group, bool? open)
    {
        if (!IsOpen) throw new ArgumentException(WorkspaceClosedMessage);
        var tab = _tabState?.Selected ?? ModsTab.Mods;
        var view = SectionView(tab);
        if (view == null) throw new ArgumentException("the " + ModsTabState.StatusName(tab) + " tab shows no settings drawers now");
        bool? now = view.DrawerOpen(group);
        if (now == null)
            throw new ArgumentException("no drawer named '" + group + "' on the " + ModsTabState.StatusName(tab) + " tab (drawers: "
                + string.Join(", ", DrawerNames(view)) + ")");
        bool want = open ?? !now.Value;
        view.SetDrawerOpen(group, want);
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["tab"] = ModsTabState.StatusName(tab),
            ["group"] = group,
            ["open"] = view.DrawerOpen(group),
            ["stored"] = SettingsDrawers.Stored,
            ["drawers"] = view.DrawersReadback(),
        };
    }

    private static IEnumerable<string> DrawerNames(ModSettingsView view)
    {
        foreach (var d in view.DrawersReadback())
            if (d is Dictionary<string, object?> row && row.TryGetValue("group", out var g)) yield return g as string ?? "";
    }

    /// <summary>The left lists and drawers for <c>mods_ui nav_state</c>: <c>sectionNav {tab, groups, highlighted, jumped, drawers, lastJump}</c>.</summary>
    internal static Dictionary<string, object?> SectionNavReadback()
    {
        var tab = SectionNavTab();
        var d = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["tab"] = tab is { } t ? ModsTabState.StatusName(t) : null,
            ["lastJump"] = _sectionJumped,
            ["storedClosedDrawers"] = SettingsDrawers.Stored,
        };
        if (tab is { } shown)
        {
            d["groups"] = new List<string>(SectionGroupsFor(shown));
            foreach (var pair in TrackerFor(shown).Readback()) d[pair.Key] = pair.Value;
            d["drawers"] = SectionView(shown)?.DrawersReadback();
        }

        return d;
    }

    /// <summary>
    /// <c>widget_click label=nav/&lt;group&gt;</c> or <c>drawer/&lt;group&gt;</c> (a caption or a slug) as the row key the
    /// rows carry (<c>nav/&lt;slug&gt;</c>, <c>drawer-&lt;slug&gt;</c>); any other label unchanged.
    /// </summary>
    internal static string? NormalizeSectionLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return label;
        string l = label!.Trim();
        if (l.StartsWith(SectionNav.KeyPrefix, StringComparison.OrdinalIgnoreCase))
            return SectionNav.Key(l.Substring(SectionNav.KeyPrefix.Length));
        if (l.StartsWith("drawer/", StringComparison.OrdinalIgnoreCase))
            return ModSettingsScopes.DrawerKeyPrefix + SectionNav.Slug(l.Substring("drawer/".Length));
        return label;
    }

    /// <summary>Forget the left lists and the Server tab (the workspace closed, or Core unloads).</summary>
    private static void ResetSectionNav()
    {
        _serverPane?.Release();
        _serverPane = null;
        _sectionNavRows.Clear();
        SectionTrackers.Clear();
        _sectionJump = null;
        _sectionJumped = null;
        _navModsGroupPlugin = "";
        _navModsGroup = "";
        _sectionOffsetSeen = float.NaN;
    }

#if PROGENITOR_TESTS
    internal static ServerTabPane? ServerPaneForTests => _serverPane;

    internal static SectionNavTracker SectionTrackerForTests(ModsTab tab) => TrackerFor(tab);
#endif
}
