using System;
using System.Collections.Generic;
using BepInEx.Configuration;

namespace Progenitor.Ui;

/// <summary>
/// KEYS4 (requested 2026-09-30, docs/mods-menu.md#back-and-forward): back and forward inside the Mods
/// workspace, like a web browser, on the mouse's back and forward buttons by default. The keys are two
/// Core keybinds in the <see cref="KeybindScope.Menu"/> scope (<see cref="RegisterNavigationKeys"/>), so
/// they are remappable in the Keybinds tab, fire only while the workspace is open (either host), and never
/// clash with a gameplay key such as Auto Run on the forward button.
///
/// <para>The history (<see cref="WorkspaceNavHistory"/>) records every place the player arrives at: a tab,
/// a mod, its settings, a custom or all-settings page, a Configuration section, and the jumps between the
/// Mods page's Keys section and the Keybinds tab. It is cleared when the workspace closes or opens anew.
/// Back and forward are ignored while a dialog is open or a key capture waits (the capture takes the mouse
/// button as the new key instead; the pump reads it before any bind fires).</para>
/// </summary>
public static partial class ModsMenu
{
    internal const string NavigateBackId = "NavigateBack";
    internal const string NavigateForwardId = "NavigateForward";

    internal static readonly KeyChord DefaultNavigateBack = new(UiKey.MouseBack);
    internal static readonly KeyChord DefaultNavigateForward = new(UiKey.MouseForward);

    internal const string ReasonNavigate = "navigate";

    private static readonly WorkspaceNavHistory Nav = new();
    private static bool _navRestoring;

    /// <summary>The Keybinds tab's section a link jumped to (the step's section), or empty for the default.</summary>
    private static string _navKeybindsSection = "";

    /// <summary>
    /// KEYS5: the mod whose page was reached through a Keybinds → Mods link (its Keys section), until the
    /// Mods page changes. View state, not a one-shot flag, so every read of the current place agrees.
    /// </summary>
    private static string _navModsKeysPlugin = "";

    /// <summary>The last back/forward: what it did, for the readback.</summary>
    private static string _navLast = "";

    internal const string NavClearedCoreReload = "core-reload";
    internal const string NavClearedWorldChange = "world-change";

    /// <summary>
    /// KEYS6: why the history last started empty: <see cref="NavClearedCoreReload"/> (a new Core generation,
    /// which is also every game start; the history is never written to disk) or
    /// <see cref="NavClearedWorldChange"/> (a world joined or left, or a server change).
    /// </summary>
    private static string _navClearedReason = NavClearedCoreReload;

    private static int _navClearedCount;

    /// <summary>KEYS6: the client world the history belongs to (<c>ClientWorldEpoch.ObservedSequence</c>, 0 at the title).</summary>
    private static ulong _navWorld = SafeWorldSequence();

    private static ulong SafeWorldSequence()
    {
        try { return Progenitor.WorldState.ClientWorldEpoch.ObservedSequence; }
        catch { return 0; }
    }

    /// <summary>
    /// KEYS6: the history belongs to one world. A join, a leave to the title, or a server change (the client
    /// world's identity moved) clears it, whether the workspace is open or not.
    /// </summary>
    private static void CheckNavWorld()
    {
        ulong world = SafeWorldSequence();
        if (world == _navWorld) return;
        ulong was = _navWorld;
        _navWorld = world;
        // KEYS7 (live 2026-09-30: the first game tabs after a Core swap were lost): a new Core generation starts
        // with the world unknown (0) until the epoch watch notes it. Game menu places exist only in a world, so
        // when they were recorded under 0 and 0 becomes a world, that is the watch catching up, not a join.
        if (was == 0 && world != 0 && _navGameSeenAtZero)
        {
            _navGameSeenAtZero = false;
            return;
        }

        _navGameSeenAtZero = false;
        ClearNavigationHistory(NavClearedWorldChange);
    }

    /// <summary>KEYS7: a game menu place was recorded while the world read 0 (a Core generation's first frames in a world).</summary>
    private static bool _navGameSeenAtZero;

    /// <summary>KEYS6: empty the history (a world change, or a lifecycle close for a server change).</summary>
    internal static void ClearNavigationHistory(string reason)
    {
        Nav.Clear();
        ResetNavViewState();
        _navRestore = null;
        _navPendingInner = null;
        _navClearedReason = reason ?? "";
        _navClearedCount++;
        _navLast = "";
    }

    /// <summary>
    /// Register the two navigation keys in Core's cfg (<c>progenitor.ui.cfg</c>, <c>[Keybinds]</c>).
    /// Menu scope; allowed while our panels hold the input (they always do while the workspace is open) and
    /// at the title screen, where no chat window exists. Mouse buttons are never printable, so the
    /// title-screen exemption cannot eat a typed character.
    /// </summary>
    internal static IKeybindHandle[] RegisterNavigationKeys(object owner, ConfigFile file) => new[]
    {
        Keybinds.RegisterScoped(owner, SettingsPanel.PluginId, NavigateBackId, file, DefaultNavigateBack,
            "Go back to the previous place in the Mods menu (like a browser's back button). Only while the Mods menu is open.",
            () => NavigateBack(ReasonNavigate), null, null, allowWhileCaptured: true, allowWhenChatUnknown: true, KeybindScope.Menu),
        Keybinds.RegisterScoped(owner, SettingsPanel.PluginId, NavigateForwardId, file, DefaultNavigateForward,
            "Go forward again after going back in the Mods menu. Only while the Mods menu is open.",
            () => NavigateForward(ReasonNavigate), null, null, allowWhileCaptured: true, allowWhenChatUnknown: true, KeybindScope.Menu),
        // HUB1: the keyboard's back and forward (a browser's Alt+Left and Alt+Right), for the workspace and window tabs alike.
        Keybinds.RegisterScoped(owner, SettingsPanel.PluginId, NavigateBackKeysId, file, DefaultNavigateBackKeys,
            "Go back with the keyboard: the previous place in the Mods menu, or the previous tab of the window under the pointer.",
            () => NavigateBack(ReasonNavigate), null, null, allowWhileCaptured: true, allowWhenChatUnknown: true, KeybindScope.Menu),
        Keybinds.RegisterScoped(owner, SettingsPanel.PluginId, NavigateForwardKeysId, file, DefaultNavigateForwardKeys,
            "Go forward with the keyboard after going back, in the Mods menu or a window's tabs.",
            () => NavigateForward(ReasonNavigate), null, null, allowWhileCaptured: true, allowWhenChatUnknown: true, KeybindScope.Menu),
    };

    internal const string NavigateBackKeysId = "NavigateBackKeys";
    internal const string NavigateForwardKeysId = "NavigateForwardKeys";

    internal static readonly KeyChord DefaultNavigateBackKeys = new(UiKey.LeftArrow, KeyModifiers.Alt);
    internal static readonly KeyChord DefaultNavigateForwardKeys = new(UiKey.RightArrow, KeyModifiers.Alt);

    /// <summary>
    /// HUB1: the navigation keys fire while a menu the history covers is open, or a window with a tab history is under the
    /// pointer or holds the input (<see cref="UiTabHistoryRoute"/>), in play too.
    /// </summary>
    internal static bool NavigationScopeOpen
    {
        get
        {
            if (MenuContextOpen) return true;
            try { return UiTabHistoryRoute.Target() != null; }
            catch { return false; }
        }
    }

    /// <summary>Back one place. False (nothing happens) at the start, while a dialog is open or a key capture waits, or when closed.</summary>
    internal static bool NavigateBack(string? reason = null) => Navigate(back: true, reason);

    /// <summary>Forward one place. False at the end of the history and in the same cases as <see cref="NavigateBack"/>.</summary>
    internal static bool NavigateForward(string? reason = null) => Navigate(back: false, reason);

    /// <summary>
    /// KEYS7: a menu the history covers is open: the Mods workspace (either host), or the game's full-screen
    /// menu on any tab. The navigation keys fire only then (<see cref="Keybinds.MenuScopeOpen"/>).
    /// </summary>
    internal static bool MenuContextOpen => (IsOpen) || GameMenuHistory.SafeMenuOpen();

    /// <summary>
    /// KEYS7 (live 2026-09-30: a Back from the in-game Mods view to Spellbook appended Spellbook, because the
    /// observer saw the workspace close and the tab change on later frames as the player's own navigation):
    /// a back or forward in progress. It holds the target and the place it left, is issued (the tab click, the
    /// menu or workspace open) as soon as it can be, and settles only when the observer sees the target: then
    /// the history's entry becomes the place reached and nothing is added. While it is in progress the observer
    /// records nothing. A place that is neither the start nor the target, seen after
    /// <see cref="NavSettleGraceFrames"/>, is the player navigating elsewhere: the restore is dropped and that
    /// place is recorded as usual. After <see cref="NavPendingTimeoutFrames"/> it is dropped.
    /// </summary>
    private sealed class NavRestore
    {
        internal NavRestore(NavStep target, NavStep? from)
        {
            Target = target;
            From = from;
        }

        internal NavStep Target { get; }

        internal NavStep? From { get; }

        internal int Frames { get; set; }

        internal bool Issued { get; set; }

        internal bool OpenRequested { get; set; }
    }

    private static NavRestore? _navRestore;

    /// <summary>KEYS7: an inner tab to select once its page is shown, after a game step's top tab.</summary>
    private static (string Tab, int Inner)? _navPendingInner;

    /// <summary>Frames a restore waits for its target before it is dropped.</summary>
    internal const int NavPendingTimeoutFrames = 300;

    /// <summary>Frames in which the in-between views of a restore (the workspace closing, the tab the menu returns to) are not the player's navigation.</summary>
    internal const int NavSettleGraceFrames = 30;

    private static bool Navigate(bool back, string? reason)
    {
        string what = back ? "back" : "forward";
        // HUB1: a window with a tab history under the pointer, or holding the input, takes the press before the workspace.
        if (UiDialog.Current == null && !Keybinds.Capturing && Keybinds.RebindingId == null && UiTabHistoryRoute.TryNavigate(back))
            return NavDone(what + ": a window's tabs");
        if (!MenuContextOpen) return NavDone(what + ": ignored (no menu is open)");
        if (_navRestore != null) return NavDone(what + ": ignored (the last one is still arriving)");
        if (UiDialog.Current != null) return NavDone(what + ": ignored (a dialog is open)");
        if (Keybinds.Capturing || Keybinds.RebindingId != null) return NavDone(what + ": ignored (a key capture is waiting)");
        // KEYS6: a place that no longer exists (a mod uninstalled, unloaded or turned off; a custom page, settings
        // group, Keybinds section or Configuration section gone) is skipped and dropped from the history.
        NavStep? from = CurrentPlace();
        int before = Nav.Count;
        var step = Nav.Move(back, IsNavStepValid);
        int pruned = before - Nav.Count;
        if (pruned > 0) LogInfoOnce(WorkspaceTransitionPrefix + "history dropped " + pruned + " place(s) that no longer exist");
        if (step is not { } to) return NavDone(what + ": nothing " + (back ? "before" : "after") + " this place");
        var restore = new NavRestore(to, from);
        _navRestore = restore;
        try
        {
            IssueRestore(restore);
            // A restore that is already there (the workspace view, rebuilt now) settles at once.
            TrySettleRestore(restore);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: Mods workspace " + what + " to " + to + " threw: " + e.Message);
        }

        LogInfoOnce(WorkspaceTransitionPrefix + "navigate " + what + " to=" + to + " reason=" + (reason ?? ReasonNavigate));
        _navLast = what + ": " + to;
        return true;
    }

    private static bool NavDone(string why)
    {
        _navLast = why;
        return false;
    }

    /// <summary>
    /// Record where the workspace is now, when it is a new place. KEYS5 (live 2026-09-30: Configuration's
    /// <b>Progenitor settings</b> page was not a step, so Back skipped to the tab before): the one place a
    /// step is taken is <see cref="TickNavigation"/>, which the keybind pump runs once a frame, after every
    /// click, link, bridge op or dialog answer of that frame has settled; it reads the whole view
    /// (<see cref="CurrentNavStep"/>), so no entry point can be missed and a half-built view is never
    /// recorded. The few calls at the ends of builds only record the same settled view a frame earlier. Not
    /// while a back or forward is arriving, and not while suspended.
    /// </summary>
    private static void NoteNavigation()
    {
        CheckNavWorld();
        if (_navRestoring || _navRestore != null) return;
        try
        {
            // KEYS7: the place is the workspace's view when it is open, else the game menu's tab.
            if (CurrentPlace() is not { } step) return;
            if (step.IsGame && _navWorld == 0) _navGameSeenAtZero = true;
            // An Install Mods list that selects its first listing by itself once the index arrives is the
            // same place, not a new one.
            if (Nav.Current is { } at && at.Tab == ModsTab.InstallMods && step.Tab == ModsTab.InstallMods
                && at.Plugin.Length == 0 && step.Plugin.Length > 0)
            {
                Nav.ReplaceCurrent(step);
                return;
            }

            Nav.Record(step);
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: Mods workspace history threw: " + e.Message); }
    }

    /// <summary>
    /// KEYS5: the frame observer (from <see cref="Keybinds.Pump"/>): the settled view is a step when it is new.
    /// KEYS6: also clears the history on a world change while the workspace is closed. KEYS7: first carries a
    /// back or forward in progress.
    /// </summary>
    internal static void TickNavigation()
    {
        ContinueRestore();
        NoteNavigation();
        // Three homes: the left list's highlight follows the scroll.
        try { TickSectionNav(); }
        catch (Exception e) { LogInfoOnce("Ui: section highlight threw: " + e.Message); }
    }

    /// <summary>
    /// KEYS7: where the player is now for the history: the workspace's view while it is open (in game that is
    /// the MODS top tab), else the game's full-screen menu tab (and its inner tab when known), else nowhere.
    /// </summary>
    private static NavStep? CurrentPlace()
    {
        if (IsOpen && _tabState != null) return CurrentNavStep();
        return GameMenuHistory.Current();
    }

    /// <summary>
    /// KEYS7: act for <paramref name="r"/> when it can. A workspace target with the workspace open is restored
    /// now; with it closed (a game tab is shown) the workspace is opened once the way the Open Mods menu key
    /// opens it (in a world through the MODS tab) and restored when it is up. A game target is selected by the
    /// tab click once the game's menu is up and the workspace is not its own window (else the workspace closes
    /// and the menu is asked to open, once).
    /// </summary>
    private static void IssueRestore(NavRestore r)
    {
        if (r.Issued) return;
        var target = r.Target;
        if (!target.IsGame)
        {
            if (IsOpen)
            {
                _navRestoring = true;
                try { RestoreNavStep(target); }
                finally { _navRestoring = false; }
                r.Issued = true;
                return;
            }

            if (!r.OpenRequested)
            {
                r.OpenRequested = true;
                OpenForKeyConflict();
                if (IsOpen) IssueRestore(r);
            }

            return;
        }

        var probe = GameMenuHistory.Probe;
        bool standalone = IsOpen
            && !string.Equals(NativeModsEntry.DescribeOwned().Site, "top-tab", StringComparison.Ordinal);
        if (probe.MenuOpen && !standalone)
        {
            r.Issued = probe.SelectTab(target.Game) || string.Equals(probe.CurrentTab, target.Game, StringComparison.OrdinalIgnoreCase);
            return;
        }

        if (!r.OpenRequested)
        {
            r.OpenRequested = true;
            if (standalone) Close(ReasonNavigate);
            probe.OpenMenu();
        }
    }

    /// <summary>KEYS7: true when the observer sees <paramref name="r"/>'s target; the history's entry becomes the place reached.</summary>
    private static bool TrySettleRestore(NavRestore r)
    {
        if (!r.Issued) return false;
        var observed = CurrentPlace();
        if (observed is not { } seen) return false;
        bool arrived = r.Target.IsGame
            ? seen.IsGame && string.Equals(seen.Game, r.Target.Game, StringComparison.OrdinalIgnoreCase)
            : !seen.IsGame;
        if (!arrived) return false;
        _navRestore = null;
        try { Nav.ReplaceCurrent(r.Target.IsGame && r.Target.Inner >= 0 && GameMenuHistory.InnerTabsEnabled ? r.Target : seen); }
        catch { /* the entry stands */ }
        if (r.Target.IsGame && r.Target.Inner >= 0 && GameMenuHistory.InnerTabsEnabled)
            _navPendingInner = (r.Target.Game, r.Target.Inner);
        return true;
    }

    /// <summary>KEYS7: once a frame: issue, settle, or drop the restore in progress, then any inner tab owed.</summary>
    private static void ContinueRestore()
    {
        ContinueInnerTab();
        if (_navRestore is not { } r) return;
        try
        {
            r.Frames++;
            IssueRestore(r);
            if (TrySettleRestore(r))
            {
                ContinueInnerTab();
                return;
            }

            // A game tab click that did not land (the menu was still settling): click once more.
            if (r.Target.IsGame && r.Issued && r.Frames == NavSettleGraceFrames / 3
                && GameMenuHistory.Probe.MenuOpen && !(IsOpen))
                GameMenuHistory.Probe.SelectTab(r.Target.Game);

            if (r.Frames > NavPendingTimeoutFrames)
            {
                Plugin.Logger?.LogWarning("Ui: menu history: " + r.Target + " was not reached in time; the history stays where it was.");
                _navRestore = null;
                return;
            }

            // The player went somewhere else meanwhile (neither where the restore started nor its target): the
            // restore is dropped, and the observer records that place as usual.
            if (r.Frames > NavSettleGraceFrames && CurrentPlace() is { } seen && !(r.From is { } from && from.Equals(seen)))
            {
                Plugin.Logger?.LogInfo("Ui: menu history: " + r.Target + " was left for " + seen + "; recorded as a new place.");
                _navRestore = null;
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: menu history restore threw: " + e.Message);
            _navRestore = null;
        }
    }

    private static void ContinueInnerTab()
    {
        if (_navPendingInner is not { } inner) return;
        var probe = GameMenuHistory.Probe;
        if (!probe.MenuOpen || !GameMenuHistory.InnerTabsEnabled)
        {
            _navPendingInner = null;
            return;
        }

        if (!string.Equals(probe.CurrentTab, inner.Tab, StringComparison.OrdinalIgnoreCase)) return;
        _navPendingInner = null;
        try
        {
            var tabs = probe.InnerTabs;
            // A count of -1 is unknown (the Spellbook's is not read): the index recorded from the page is used.
            if (tabs != null && (tabs.Count < 0 || inner.Inner < tabs.Count)) tabs.Select(inner.Inner);
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: menu history inner tab restore threw: " + e.Message); }
    }

    /// <summary>KEYS5: a Configuration settings page's <b>Back to Configuration</b>.</summary>
    internal static void BackToConfiguration()
    {
        if (!IsOpen || _tabState == null) return;
        if (_tabState.Selected != ModsTab.Preferences) SelectWorkspaceTab(ModsTab.Preferences);
        else BuildConfigurationPanes();
    }

    private static NavStep CurrentNavStep()
    {
        var tab = _tabState?.Selected ?? ModsTab.Mods;
        switch (tab)
        {
            case ModsTab.Preferences:
            {
                // Settings organization stage 3: the raw Progenitor settings pane inside Configuration is gone (the tab's
                // link row goes to Progenitor's page instead), so Configuration is one place per nav section.
                string prefGroup = _tabState?.Configuration.Selection is { Kind: ModsIdentityKind.ConfigGroup } pg ? pg.Value : "";
                return new NavStep(tab, page: "main", section: prefGroup);
            }
            case ModsTab.Keybinds:
                return new NavStep(tab, section: _navKeybindsSection);
            case ModsTab.Server:
            {
                // Three homes: the Server tab is one place per left-list group it jumped to.
                string serverGroup = _tabState?.Server.Selection is { Kind: ModsIdentityKind.ConfigGroup } sg ? sg.Value : "";
                return new NavStep(tab, page: "main", section: serverGroup);
            }
            case ModsTab.InstallMods:
                // KEYS5: the listing whose details are open.
                return new NavStep(tab, _installPane?.SelectedKey ?? "");
            default:
                string plugin = _selected ?? "";
                string kind = CurrentPageKind();
                string page = kind == "custom" ? "custom"
                    : kind == "settings" ? (_pageId.Length > 0 ? "settings" : "settings-all")
                    : plugin.Length > 0 ? "mod" : "overview";
                string pageId = page is "custom" or "settings" ? _pageId : "";
                bool keys = page == "mod" && plugin.Length > 0
                    && string.Equals(_navModsKeysPlugin, plugin, StringComparison.OrdinalIgnoreCase);
                // Three homes: Progenitor's page reached through its left list is one place per group.
                string pageGroup = !keys && page == "mod" && plugin.Length > 0
                    && string.Equals(_navModsGroupPlugin, plugin, StringComparison.OrdinalIgnoreCase) ? _navModsGroup : "";
                return new NavStep(tab, plugin, page, pageId, keys ? "keys" : pageGroup);
        }
    }

    /// <summary>Bring the workspace back to <paramref name="step"/> through the ordinary paths.</summary>
    private static void RestoreNavStep(NavStep step)
    {
        EnsureTabState();
        switch (step.Tab)
        {
            case ModsTab.InstallMods:
                _tabState!.InstallMods.Selection = step.Plugin.Length > 0 ? ModsWorkspaceIdentity.Catalog(step.Plugin) : null;
                if (_tabState.Selected != ModsTab.InstallMods) SelectWorkspaceTab(ModsTab.InstallMods);
                else if (step.Plugin.Length > 0)
                {
                    EnsureInstallPane().SelectListing(step.Plugin);
                    RefreshInstallDetailIfSelectionChanged();
                }

                return;
            case ModsTab.Preferences:
            case ModsTab.Server:
                // Three homes: a group jump scrolls to its group again (and opens its drawer); the tab alone opens at its top.
                if (step.Section.Length > 0 && JumpToGroup(step.Tab, step.Section)) return;
                _tabState!.Page(step.Tab).Selection = null;
                if (_tabState.Selected != step.Tab) SelectWorkspaceTab(step.Tab);
                else if (step.Tab == ModsTab.Server) BuildServerPanes();
                else BuildConfigurationPanes();
                return;
            case ModsTab.Keybinds:
                // A jump (a link or the tab's own section jumps) scrolls to its section again.
                string section = step.Section.Length > 0 ? step.Section : KeybindsPane.JumpMods;
                JumpWorkspace(ModsTab.Keybinds, null, section);
                return;
        }

        if (step.Section == "keys" && step.Plugin.Length > 0)
        {
            JumpWorkspace(ModsTab.Mods, step.Plugin, "keys");
            return;
        }

        if (step.Section.Length > 0 && step.Plugin.Length > 0
            && string.Equals(step.Plugin, SettingsPanel.PluginId, StringComparison.OrdinalIgnoreCase))
        {
            JumpToProgenitorGroup(step.Section);
            return;
        }

        if (_tabState!.Selected != ModsTab.Mods)
        {
            _tabState.Mods.Selection = step.Plugin.Length > 0 ? ModsWorkspaceIdentity.Installed(step.Plugin) : null;
            SelectWorkspaceTab(ModsTab.Mods);
            if (CurrentNavStep().Equals(step)) return;
        }

        switch (step.Page)
        {
            case "custom":
                OpenPage(step.Plugin, step.PageId, ReasonNavigate);
                return;
            case "settings":
                Select(step.Plugin, "settings", step.PageId);
                return;
            case "settings-all":
                Select(step.Plugin, "settings", null);
                return;
            case "mod":
                OpenMod(step.Plugin, ReasonNavigate);
                return;
            default:
                Open(ReasonNavigate);
                return;
        }
    }

    /// <summary>
    /// <c>mods_ui nav_back</c> / <c>nav_forward</c>: press the navigation keybind through
    /// <see cref="Keybinds.SimulatePress(string?, string)"/>, the gates and winner rule a real mouse button
    /// meets, and return the dispatch with the history. Works with the workspace closed (the dispatch then
    /// says the bind works only while the Mods menu is open).
    /// </summary>
    public static object NavigateOp(bool back)
    {
        var dispatch = Keybinds.SimulatePress(SettingsPanel.PluginId, back ? NavigateBackId : NavigateForwardId);
        var list = new List<object?>();
        foreach (var d in dispatch)
            list.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["plugin"] = d.PluginId,
                ["bind"] = d.Id,
                ["chord"] = d.Chord,
                ["fired"] = d.Fired,
                ["reason"] = d.Reason,
            });
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["dispatch"] = list,
            ["nav"] = NavState(),
            ["readback"] = "next-drain",
        };
    }

    /// <summary>
    /// KEYS7: <c>mods_ui menu_tab_state</c>: the game's full-screen menu as the history reads it (open, the
    /// current top tab, the inner tab when known, which tabs are shown) and the step it would record.
    /// </summary>
    public static Dictionary<string, object?> MenuTabState()
    {
        var d = GameMenuHistory.Describe();
        d["workspaceOpen"] = IsOpen;
        d["menuContextOpen"] = MenuContextOpen;
        d["place"] = CurrentPlace()?.Readback();
        return d;
    }

    /// <summary>The history for <c>mods_ui nav_state</c>: <c>{index, count, canBack, canForward, steps, last, open}</c>.</summary>
    public static Dictionary<string, object?> NavState()
    {
        CheckNavWorld();
        var d = Nav.Readback();
        // KEYS6: the history is kept while the workspace is closed; this says when it last started empty.
        d["clearedReason"] = _navClearedReason;
        d["clearedCount"] = _navClearedCount;
        d["world"] = _navWorld.ToString(System.Globalization.CultureInfo.InvariantCulture);
        d["last"] = _navLast;
        d["open"] = IsOpen;
        d["current"] = CurrentPlace()?.Readback();
        d["menuOpen"] = MenuContextOpen;
        d["pending"] = _navRestore?.Target.ToString();
        // The landing's Your settings: whether Progenitor's left-list row was found, highlighted and scrolled into view.
        d["sidebarReveal"] = _sidebarRevealed;
        // A text field's focus holds key binds back (mouse buttons excepted); this says whether one has it now.
        try { d["textFocus"] = Keybinds.TextFocusReadback(); }
        catch (Exception e) { d["textFocus"] = "error: " + e.Message; }
        d["pendingFrames"] = _navRestore?.Frames;
        // Three homes: the left list's groups and highlight, the drawers, and what the last jump measured.
        try { d["sectionNav"] = SectionNavReadback(); }
        catch (Exception e) { d["sectionNav"] = "error: " + e.Message; }
        return d;
    }

    private static NavStep CurrentNavStepPeek() => CurrentNavStep();

    /// <summary>
    /// KEYS6: the view state the history reads (not the history itself), reset when the workspace closes or
    /// opens anew. The history survives a close: reopening records the opened view as a new place.
    /// </summary>
    private static void ResetNavViewState()
    {
        // KEYS7: a back or forward in progress survives the workspace closing (leaving the in-game Mods view for
        // a game tab closes it on the way); only a history clear drops it.
        _navKeybindsSection = "";
        _navModsKeysPlugin = "";
        _navModsGroupPlugin = "";
        _navModsGroup = "";
    }

    /// <summary>
    /// KEYS6: whether <paramref name="step"/> is a place that still exists. Mods and settings steps need
    /// their plugin listed and not unloaded or turned off; a custom page or settings group must still be
    /// registered; a Keybinds mod section needs a key of that mod; a Configuration section must be one of
    /// the tab's sections. An Install listing is always kept (the list shows what it can).
    /// </summary>
    internal static bool IsNavStepValid(NavStep step)
    {
        try
        {
            // KEYS7: a game tab the menu no longer shows (a requirement not met) is skipped.
            if (step.IsGame) return GameMenuHistory.Probe.IsTabAvailable(step.Game);
            switch (step.Tab)
            {
                case ModsTab.InstallMods:
                    return true;
                case ModsTab.Server:
                    return true;
                case ModsTab.Preferences:
                    if (step.Section.Length > 0 && !ContainsIgnoreCase(ConfigurationPane.ConfigGroups, step.Section)) return false;
                    // Settings organization stage 3: the raw settings pane inside Configuration is no longer a place.
                    return step.Page != "settings-all";
                case ModsTab.Keybinds:
                    return KeybindsSectionExists(step.Section);
            }

            if (step.Plugin.Length == 0) return true;
            if (!PluginPageExists(step.Plugin)) return false;
            switch (step.Page)
            {
                case "custom":
                    return Mods.TryGetValue(step.Plugin, out var custom) && !custom.IsDisposed && custom.Pages.ContainsKey(step.PageId);
                case "settings":
                    if (!Mods.TryGetValue(step.Plugin, out var reg) || reg.IsDisposed) return false;
                    foreach (var g in reg.Groups)
                        if (string.Equals(g.Title, step.PageId, StringComparison.OrdinalIgnoreCase)) return true;
                    return false;
                default:
                    return true;
            }
        }
        catch { return false; }
    }

    private static bool ContainsIgnoreCase(IEnumerable<string> list, string value)
    {
        foreach (var s in list)
            if (string.Equals(s, value, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>KEYS6: the plugin still has a page: listed, and not unloaded or turned off.</summary>
    private static bool PluginPageExists(string pluginId)
    {
        if (string.IsNullOrEmpty(pluginId)) return false;
        if (string.Equals(pluginId, SettingsPanel.PluginId, StringComparison.OrdinalIgnoreCase)) return true;
        if (Progenitor.Plugins.ModRegistry.TryGet(pluginId, out var entry)
            && entry.Outcome is Progenitor.Plugins.ModLoadOutcome.Unloaded or Progenitor.Plugins.ModLoadOutcome.SkippedOff)
            return false;
        return CurrentSidebarModel(applySearch: false).Find(pluginId) != null;
    }

    private static bool KeybindsSectionExists(string section)
    {
        if (section.Length == 0 || string.Equals(section, KeybindsPane.JumpMods, StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(section, KeybindsPane.JumpGame, StringComparison.OrdinalIgnoreCase))
        {
            var picture = KeybindArbiter.Current();
            return picture.GameAvailable || KeybindGameCache.DisplayClaims() != null;
        }

        if (!section.StartsWith(KeybindsTabLayout.ModSectionPrefix, StringComparison.OrdinalIgnoreCase)) return true;
        string owner = section.Substring(KeybindsTabLayout.ModSectionPrefix.Length);
        foreach (var v in KeybindArbiter.Current().Verdicts)
            if (v.Claim.Source != KeybindSource.Game && string.Equals(v.Claim.OwnerId, owner, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

#if PROGENITOR_TESTS
    internal static void ResetNavigationForTests()
    {
        Nav.Clear();
        ResetNavViewState();
        _navRestore = null;
        _navPendingInner = null;
        _navLast = "";
        _navClearedReason = NavClearedCoreReload;
        _navClearedCount = 0;
        _navWorld = SafeWorldSequence();
        _navGameSeenAtZero = false;
        LastServerFactsForTests = null;
        LastServerJoinForTests = null;
        _dimmedNavRows.Clear();
        _sidebarRevealed = null;
        _landingCollectWaiting = false;
        _landingCollectUntil = long.MinValue;
    }
#endif

#if PROGENITOR_TESTS
    internal static WorkspaceNavHistory NavForTests => Nav;
#endif
}
