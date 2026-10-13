using System;

namespace Progenitor.Ui;

/// <summary>
/// What the shortcut route needs from the game. <see cref="NativeModsEntry"/> supplies the live
/// host; tests supply a fake. No Unity types.
/// </summary>
internal interface IModsShortcutHost
{
    /// <summary>A client world with a HUD exists (false at the title screen).</summary>
    bool InWorld { get; }

    /// <summary>The HUD's current menu is the game's FullscreenMenu (the Esc menu with the top bar).</summary>
    bool MenuUp { get; }

    /// <summary>
    /// The FullscreenMenu is up and its outer tab bar is active in the hierarchy — the state in
    /// which a player can click MODS. <c>SimpleStunSelectable.DoStateTransition</c> returns early on
    /// an inactive object and <c>OuterTabEntry.Refresh</c> repaints only when <c>ForceHighlight</c>
    /// changes, so selecting MODS before the bar is active can leave the previous tab painted
    /// selected for good.
    /// </summary>
    bool MenuReady { get; }

    /// <summary>
    /// Re-run the game's own state transition on every native outer tab the mapper does not select
    /// (the same <c>ForceHighlight</c> setter <c>OuterTabEntry.Refresh</c> uses), so one painted
    /// while inactive shows its Normal look. Owned MODS entry and the selected entry untouched.
    /// </summary>
    void RepaintUnselectedOuterTabs();

    /// <summary>The HUD's current menu is some other menu (inventory, map…), neither None nor FullscreenMenu.</summary>
    bool OtherMenuUp { get; }

    /// <summary>
    /// F10SETTLE: the primary mouse button is held right now (the framework's own sample). While a
    /// raised menu settles, a held button means the player is clicking, so a native tab change is
    /// theirs and closes Mods as usual.
    /// </summary>
    bool PrimaryPointerHeld { get; }

    /// <summary>The owned MODS outer tab is registered with the game's menu mapper (native route).</summary>
    bool NativeRouteActive { get; }

    /// <summary>The owned hand-built MODS top-tab button is attached (imitation route).</summary>
    bool ImitationTabAttached { get; }

    /// <summary>
    /// The imitation route is attached only because the native route refused a first attach that is
    /// being retried (NATIVE2: the mapper had not initialized this menu yet). The retry does not run
    /// while the workspace is open, so opening now would pin the imitation route for this session.
    /// </summary>
    bool NativeRoutePending { get; }

    bool WorkspaceOpen { get; }

    /// <summary>
    /// Native route only: the mapper still shows the owned MODS page after the workspace closed,
    /// so the return transition to a native page has not run yet.
    /// </summary>
    bool NativeReturnPending { get; }

    /// <summary>Reason of the last <see cref="ModsMenu.Close"/> (empty when none).</summary>
    string LastCloseReason { get; }

    /// <summary><c>HUDMenuManager.GoToMenu(FullscreenMenu)</c>; false when refused.</summary>
    bool RequestMenu();

    /// <summary><c>HUDMenuManager.GoToMenu(None)</c>: back to gameplay.</summary>
    bool DismissMenu();

    /// <summary>The owned native MODS tab's own click path (typed <c>SetState</c> + open).</summary>
    bool OpenNative(out string why);

    /// <summary>The owned imitation MODS button's own click path.</summary>
    bool OpenImitation(string reason, out string why);

    /// <summary>The standalone workspace window (<see cref="ModsMenu.Open"/>).</summary>
    void OpenStandalone(string reason);

    void Log(string line);
}

/// <summary>What a shortcut press does while the workspace is closed or opening.</summary>
internal enum ShortcutPress
{
    /// <summary>The workspace is open: close it (unchanged <see cref="ModsMenu.Toggle"/>).</summary>
    Close = 0,

    /// <summary>A routed open is still waiting: the second press cancels it.</summary>
    CancelPending = 1,

    /// <summary>No world (title screen): the standalone window, as before.</summary>
    Standalone = 2,

    /// <summary>In a world: open through the game's FullscreenMenu on the MODS tab.</summary>
    ThroughMenu = 3,
}

/// <summary>One frame of a routed open that is waiting for the menu and the MODS tab.</summary>
internal enum ShortcutPendingStep
{
    Wait = 0,

    /// <summary>Something else already opened the workspace (a MODS click): adopt it and stop.</summary>
    Adopt = 1,

    OpenNative = 2,
    OpenImitation = 3,

    /// <summary>The menu or the tab never arrived: the standalone window, logged once.</summary>
    Fallback = 4,

    /// <summary>The menu this open waited on went down (Escape): the player backed out.</summary>
    CancelMenuClosed = 5,

    /// <summary>Another HUD menu took over (inventory, map): the player moved on.</summary>
    CancelOtherMenu = 6,
}

/// <summary>What happens after a workspace the shortcut opened over a menu it raised.</summary>
internal enum ShortcutAfterClose
{
    /// <summary>Still open.</summary>
    Keep = 0,

    /// <summary>Nothing to undo (menu already down, or the player chose another native tab).</summary>
    Forget = 1,

    /// <summary>The native return transition is still running; dismiss after it.</summary>
    Wait = 2,

    /// <summary>Close the menu the shortcut raised: the player is back in gameplay.</summary>
    Dismiss = 3,
}

/// <summary>
/// TABS2 (user report 2026-09-29, "the in game keybindings lost the first class look and feel"):
/// the Mods shortcut in a world opens the workspace through the game's own FullscreenMenu on the
/// MODS tab — the same path as Esc → MODS — so the top menu bar and the native inner-tab donor
/// are up before the tab strip is built. A workspace the shortcut opened over a menu it raised
/// closes back to gameplay; one opened while the player was already in the menu closes back to
/// the menu, as a MODS click does. If the menu or the MODS tab never arrives within
/// <see cref="AttachFrames"/>, the standalone window opens instead and that is logged once — but
/// never over a FullscreenMenu: a menu this route raised is dismissed first, and a menu the player
/// was already in is left alone with the open canceled.
///
/// <para>Decisions are pure (<see cref="DecidePress"/>, <see cref="DecidePending"/>,
/// <see cref="DecideAfterClose"/>); the session state is a handful of statics driven by
/// <see cref="TryRoutePress"/> and <see cref="Tick"/>. Game-free.</para>
/// </summary>
internal static class ModsShortcutRoute
{
    /// <summary>Frames a routed open waits for the menu and the MODS tab (same bound as <c>mods_ui open site=top-tab</c>).</summary>
    internal const int AttachFrames = 120;

    /// <summary>Frames a close waits for the native return transition before dismissing anyway.</summary>
    internal const int DismissWaitFrames = 30;

    /// <summary>Frames a fallback waits for a menu it dismissed to go down before giving up.</summary>
    internal const int FallbackDismissFrames = 30;

    /// <summary>
    /// Frames after the press during which another HUD menu may still be current (the request
    /// switches away from it on a later frame). After this, or once the FullscreenMenu was seen,
    /// another HUD menu cancels the open.
    /// </summary>
    internal const int OtherMenuGraceFrames = 10;

    internal const string LogPrefix = "Mods workspace: shortcut ";
    internal const string WhyNoMenu = "no-fullscreen-menu";
    internal const string WhyTabNotAttached = "tab-not-attached";
    internal const string WhyMenuRefused = "menu-request-refused";
    internal const string WhyNativeRefused = "native-open-refused:";
    internal const string WhyImitationRefused = "imitation-open-refused:";
    internal const string WhyMenuClosed = "menu-closed";
    internal const string WhyOtherMenu = "other-menu";
    internal const string WhyPlayersMenu = "players-menu-kept:";
    internal const string WhyMenuStayedUp = "menu-stayed-up";

    private static IModsShortcutHost? _host;
    private static int _frames;
    private static int _elapsed;
    private static bool _sawMenuUp;
    private static string _reason = ModsMenu.ReasonShortcut;
    private static bool _requestedMenu;
    private static bool _raisedMenu;
    private static bool _standalone;
    private static int _dismissWait;
    private static int _awaitDown;
    private static int _sinceOpen = -1;
    private static int _settle = -1;
    private static int _settleReselects;
    private static bool _settlePointer;
    private static int _settleReselectsTotal;
    private static string _awaitDownWhy = "";
    private static bool _fallbackLogged;
    private static string _lastFallback = "";
    private static string _lastRoute = "";
    private static string _lastCancel = "";

    /// <summary>A routed open is waiting (for the menu and tab, or for a dismissed menu before the fallback).</summary>
    internal static bool Pending => _frames > 0 || _awaitDown > 0;
    internal static int FramesLeft => _frames;

    /// <summary>The open workspace sits on a FullscreenMenu this route raised from gameplay.</summary>
    internal static bool RaisedMenu => _raisedMenu;

    /// <summary>
    /// F10SETTLE (user, 2026-10-03: "how come F10 brings up the spell book and then the second press
    /// brings up MODs?"): frames after a routed open over a menu this route raised during which the
    /// menu is still finishing its own open. Live, the game's FullscreenMenu selected its remembered
    /// tab (Spellbook) a few frames after the workspace opened on MODS, and the native route read that
    /// as the player leaving MODS and closed the workspace. About 0.75 s at 60 fps.
    /// </summary>
    internal const int MenuSettleFrames = 45;

    /// <summary>How many times one settle window may put MODS back over the menu's own selection.</summary>
    internal const int MaxSettleReselects = 2;

    /// <summary>
    /// The native route may answer a native tab change by selecting MODS again instead of closing:
    /// the workspace opened over a menu this route raised, the menu is inside its settle window, the
    /// window has re-selections left, and the player has not pressed the mouse since the open. A click
    /// on a vanilla tab, or any change after the window, closes Mods as before.
    /// </summary>
    internal static bool SettleReselectAllowed =>
        DecideSettleReselect(_raisedMenu, _settle, _settleReselects, _settlePointer);

    /// <summary>Pure form of <see cref="SettleReselectAllowed"/>.</summary>
    internal static bool DecideSettleReselect(bool raisedMenu, int framesSinceOpen, int reselects, bool pointerSeen) =>
        raisedMenu && framesSinceOpen >= 0 && framesSinceOpen <= MenuSettleFrames
        && reselects < MaxSettleReselects && !pointerSeen;

    /// <summary>
    /// The native route put MODS back over the menu's own tab selection during the settle window.
    /// Logged; the outer tabs are repainted again on the usual frames after it.
    /// </summary>
    internal static void NoteSettleReselect(int? nativeState)
    {
        _settleReselects++;
        _settleReselectsTotal++;
        _sinceOpen = 0;
        var host = _host;
        if (host != null)
            SafeLog(host, LogPrefix + "menu settle re-selected MODS over native state=" + (nativeState?.ToString() ?? "null")
                + " frame=" + _settle + " count=" + _settleReselects);
    }

    /// <summary>
    /// The open workspace is the standalone window. NativeModsEntry must not raise the menu
    /// under it (and then close it after 120 frames) as it does for an ordinary in-world open.
    /// </summary>
    internal static bool SuppressMenuRequest => _standalone;

    internal static string LastFallback => _lastFallback;
    internal static string LastRoute => _lastRoute;
    internal static string LastCancel => _lastCancel;

    internal static void Install(IModsShortcutHost? host)
    {
        Reset();
        _host = host;
    }

    /// <summary>Drop every flag and the host. Tests and the end of <see cref="ReleaseForUnload"/>.</summary>
    internal static void Reset()
    {
        _host = null;
        ForgetSession();
        _reason = ModsMenu.ReasonShortcut;
        _fallbackLogged = false;
        _lastFallback = "";
        _lastRoute = "";
        _lastCancel = "";
    }

    /// <summary>
    /// Core unload (every reason): a FullscreenMenu this route raised is dismissed before the flags
    /// go, so a swap mid-route cannot strand the player in the Esc menu with no one left to close it.
    /// A menu the player opened themselves is left alone.
    /// </summary>
    internal static void ReleaseForUnload()
    {
        var host = _host;
        bool ours = _raisedMenu || _requestedMenu;
        if (host != null && ours)
        {
            bool up = false;
            try { up = host.MenuUp; } catch { up = false; }
            if (up)
            {
                bool dismissed = false;
                try { dismissed = host.DismissMenu(); } catch { dismissed = false; }
                SafeLog(host, LogPrefix + "core-unload dismissed raised menu=" + (dismissed ? "true" : "false"));
            }
        }
        Reset();
    }

    /// <summary>World ended / server context cleared: forget the session, keep the host.</summary>
    internal static void ForgetSession()
    {
        _frames = 0;
        _elapsed = 0;
        _sawMenuUp = false;
        _requestedMenu = false;
        _raisedMenu = false;
        _standalone = false;
        _dismissWait = 0;
        _awaitDown = 0;
        _awaitDownWhy = "";
        _sinceOpen = -1;
        _settle = -1;
        _settleReselects = 0;
        _settlePointer = false;
    }

    internal static ShortcutPress DecidePress(bool workspaceOpen, bool pending, bool inWorld)
    {
        if (workspaceOpen) return ShortcutPress.Close;
        if (pending) return ShortcutPress.CancelPending;
        return inWorld ? ShortcutPress.ThroughMenu : ShortcutPress.Standalone;
    }

    internal static ShortcutPendingStep DecidePending(
        bool workspaceOpen, bool menuUp, bool nativeRouteActive, bool imitationTabAttached, int framesLeft)
        => DecidePending(workspaceOpen, menuUp, otherMenuUp: false, sawMenuUp: false, elapsed: 0,
            nativeRouteActive, imitationTabAttached, framesLeft, menuReady: menuUp);

    /// <summary>
    /// <paramref name="menuReady"/>: the tab bar is active (<see cref="IModsShortcutHost.MenuReady"/>);
    /// MODS is selected only then, as a player's click can only happen then.
    /// </summary>
    internal static ShortcutPendingStep DecidePending(
        bool workspaceOpen, bool menuUp, bool otherMenuUp, bool sawMenuUp, int elapsed,
        bool nativeRouteActive, bool imitationTabAttached, int framesLeft, bool menuReady = true,
        bool nativeRoutePending = false)
    {
        if (workspaceOpen) return ShortcutPendingStep.Adopt;
        if (sawMenuUp && !menuUp && !otherMenuUp) return ShortcutPendingStep.CancelMenuClosed;
        if (otherMenuUp && (sawMenuUp || elapsed >= OtherMenuGraceFrames)) return ShortcutPendingStep.CancelOtherMenu;
        bool ready = menuUp && menuReady;
        if (ready && nativeRouteActive) return ShortcutPendingStep.OpenNative;
        // Live 2026-09-29 (probe I): the routed open took the imitation route in the frames before
        // the native route's retry attached, and the retry never runs while the workspace is open.
        // A player's click lands after that settles, so wait for the native route while its retry
        // is armed; the imitation route is taken only when the retry gives up or the budget ends.
        if (ready && imitationTabAttached && (!nativeRoutePending || framesLeft <= 0))
            return ShortcutPendingStep.OpenImitation;
        return framesLeft <= 0 ? ShortcutPendingStep.Fallback : ShortcutPendingStep.Wait;
    }

    /// <summary>Frames after a routed open on which the unselected native tabs are repainted.</summary>
    internal static bool RepaintsOnFrame(int framesSinceOpen) =>
        framesSinceOpen == RepaintFirstFrame || framesSinceOpen == RepaintLastFrame;

    internal const int RepaintFirstFrame = 2;
    internal const int RepaintLastFrame = 10;

    /// <summary>
    /// A close by another native tab (the player navigated to Spellbook, say) or by a lifecycle
    /// edge leaves the menu alone; every other close — the shortcut again, Close Mods — returns
    /// to gameplay. Escape dismisses the menu itself (<see cref="ModsMenu.NotifyHiddenByEscape"/>),
    /// which reads here as "menu already down".
    /// </summary>
    internal static ShortcutAfterClose DecideAfterClose(
        bool workspaceOpen, bool menuUp, string? closeReason, bool nativeReturnPending, int waited)
    {
        if (workspaceOpen) return ShortcutAfterClose.Keep;
        if (!menuUp) return ShortcutAfterClose.Forget;
        if (IsNavigationClose(closeReason)) return ShortcutAfterClose.Forget;
        if (nativeReturnPending && waited < DismissWaitFrames) return ShortcutAfterClose.Wait;
        return ShortcutAfterClose.Dismiss;
    }

    internal static bool IsNavigationClose(string? reason)
    {
        if (string.IsNullOrEmpty(reason)) return false;
        return string.Equals(reason, ModsMenu.ReasonNativeTab, StringComparison.Ordinal)
            || reason!.StartsWith("lifecycle:", StringComparison.Ordinal);
    }

    /// <summary>
    /// Called by <see cref="ModsMenu.Toggle(string)"/> while the workspace is closed. True when this
    /// route took the press (a routed open started, opened at once, fell back, or was canceled);
    /// false means "open the standalone window as before" (no world, or no host).
    /// </summary>
    internal static bool TryRoutePress(string reason)
    {
        var host = _host;
        bool inWorld = false;
        try { inWorld = host != null && host.InWorld; } catch { inWorld = false; }
        switch (DecidePress(workspaceOpen: false, Pending, inWorld))
        {
            case ShortcutPress.CancelPending:
                CancelPending(host, "pressed-again");
                return true;
            case ShortcutPress.ThroughMenu:
                break;
            default:
                return false;
        }

        _reason = string.IsNullOrEmpty(reason) ? ModsMenu.ReasonShortcut : reason;
        // A re-press inside the return window of a workspace this route opened over a menu it
        // raised: that menu is still ours, so the next close must still return to gameplay.
        bool carried = _raisedMenu;
        _raisedMenu = false;
        _requestedMenu = carried;
        _dismissWait = 0;
        _elapsed = 0;
        _frames = AttachFrames;
        bool menuUp = SafeRead(() => host!.MenuUp);
        _sawMenuUp = menuUp;
        if (!menuUp)
        {
            bool requested = false;
            try { requested = host!.RequestMenu(); } catch { requested = false; }
            if (!requested)
            {
                Fallback(host!, WhyMenuRefused);
                return true;
            }
            _requestedMenu = true;
        }

        // Already in the menu with the MODS tab attached: this opens on the same frame.
        StepPending(host!);
        return true;
    }

    /// <summary>
    /// The standalone window on purpose (<c>mods_ui open site=standalone</c>): any routed open is
    /// canceled and, in a world, the in-world menu request is suppressed for this window.
    /// </summary>
    internal static void OpenStandalone(string reason)
    {
        var host = _host;
        if (Pending) CancelPending(host, "standalone");
        bool inWorld = false;
        try { inWorld = host != null && host.InWorld; } catch { inWorld = false; }
        _standalone = inWorld;
        _lastRoute = "standalone";
        ModsMenu.Open(string.IsNullOrEmpty(reason) ? ModsMenu.ReasonModsUi : reason);
    }

    /// <summary>Per-frame body, from NativeModsEntry's tick. Cheap: flag reads unless a session is live.</summary>
    internal static void Tick()
    {
        var host = _host;
        if (host == null) return;
        try
        {
            if (_frames > 0)
            {
                StepPending(host);
                return;
            }
            if (_awaitDown > 0)
            {
                StepAwaitDown(host);
                return;
            }
            if (_settle >= 0)
            {
                _settle++;
                if (SafeRead(() => host.PrimaryPointerHeld)) _settlePointer = true;
                if (_settle > MenuSettleFrames || !host.WorkspaceOpen) EndSettle();
            }
            if (_sinceOpen >= 0)
            {
                _sinceOpen++;
                if (!host.WorkspaceOpen) _sinceOpen = -1;
                else if (RepaintsOnFrame(_sinceOpen)) host.RepaintUnselectedOuterTabs();
                if (_sinceOpen >= RepaintLastFrame) _sinceOpen = -1;
            }
            if (_standalone && !host.WorkspaceOpen) _standalone = false;
            if (_raisedMenu) StepAfterClose(host);
        }
        catch (Exception e)
        {
            ForgetSession();
            SafeLog(host, LogPrefix + "route reset after error: " + e.Message);
        }
    }

    internal static object Describe() => new
    {
        pending = Pending,
        framesLeft = _frames,
        raisedMenu = _raisedMenu,
        settle = new { frame = _settle, reselects = _settleReselects, pointerSeen = _settlePointer, reselectsTotal = _settleReselectsTotal, allowed = SettleReselectAllowed },
        standalone = _standalone,
        lastRoute = _lastRoute,
        lastFallback = _lastFallback,
        lastCancel = _lastCancel,
    };

    private static void StepPending(IModsShortcutHost host)
    {
        bool menuUp = host.MenuUp;
        bool otherMenu = !menuUp && host.OtherMenuUp;
        if (menuUp) _sawMenuUp = true;
        bool ready = menuUp && host.MenuReady;
        var step = DecidePending(
            host.WorkspaceOpen, menuUp, otherMenu, _sawMenuUp, _elapsed,
            host.NativeRouteActive, host.ImitationTabAttached, _frames, ready, host.NativeRoutePending);
        string why;
        switch (step)
        {
            case ShortcutPendingStep.Adopt:
                Opened(host, "adopted");
                return;
            case ShortcutPendingStep.CancelMenuClosed:
                CancelPending(host, WhyMenuClosed);
                return;
            case ShortcutPendingStep.CancelOtherMenu:
                CancelPending(host, WhyOtherMenu);
                return;
            case ShortcutPendingStep.OpenNative:
                if (host.OpenNative(out why)) Opened(host, "native");
                else Fallback(host, WhyNativeRefused + why);
                return;
            case ShortcutPendingStep.OpenImitation:
                if (host.OpenImitation(_reason, out why)) Opened(host, "imitation");
                else Fallback(host, WhyImitationRefused + why);
                return;
            case ShortcutPendingStep.Fallback:
                Fallback(host, menuUp ? WhyTabNotAttached : WhyNoMenu);
                return;
            default:
                _frames--;
                _elapsed++;
                if (_frames <= 0) StepPendingAtBudgetEnd(host);
                return;
        }
    }

    /// <summary>The budget ran out: the imitation route if it is there (no more waiting for native), else the fallback.</summary>
    private static void StepPendingAtBudgetEnd(IModsShortcutHost host)
    {
        bool menuUp = host.MenuUp;
        bool ready = menuUp && host.MenuReady;
        var step = DecidePending(false, menuUp, false, false, 0,
            host.NativeRouteActive, host.ImitationTabAttached, 0, ready, host.NativeRoutePending);
        string why;
        if (step == ShortcutPendingStep.OpenNative)
        {
            if (host.OpenNative(out why)) Opened(host, "native");
            else Fallback(host, WhyNativeRefused + why);
        }
        else if (step == ShortcutPendingStep.OpenImitation)
        {
            if (host.OpenImitation(_reason, out why)) Opened(host, "imitation");
            else Fallback(host, WhyImitationRefused + why);
        }
        else Fallback(host, menuUp ? WhyTabNotAttached : WhyNoMenu);
    }

    private static void Opened(IModsShortcutHost host, string route)
    {
        _frames = 0;
        _elapsed = 0;
        _raisedMenu = _requestedMenu;
        _requestedMenu = false;
        _dismissWait = 0;
        _lastRoute = route;
        _sinceOpen = 0;
        // F10SETTLE: a menu this route raised is still finishing its own open.
        _settle = _raisedMenu ? 0 : -1;
        _settleReselects = 0;
        _settlePointer = false;
        SafeLog(host, LogPrefix + "opened through menu route=" + route
            + " raisedMenu=" + (_raisedMenu ? "true" : "false") + " reason=" + _reason);
    }

    /// <summary>
    /// The standalone window, but never over a FullscreenMenu. A menu this route requested is
    /// dismissed and the window opens once it is down; a menu the player was already in is kept
    /// and the open is canceled (they can click MODS).
    /// </summary>
    private static void Fallback(IModsShortcutHost host, string why)
    {
        _frames = 0;
        _elapsed = 0;
        _raisedMenu = false;
        _dismissWait = 0;
        bool menuUp = SafeRead(() => host.MenuUp);
        if (menuUp)
        {
            if (!_requestedMenu)
            {
                CancelPending(host, WhyPlayersMenu + why);
                return;
            }
            _requestedMenu = false;
            try { host.DismissMenu(); } catch { /* checked on the next frames */ }
            if (SafeRead(() => host.MenuUp))
            {
                _awaitDown = FallbackDismissFrames;
                _awaitDownWhy = why;
                return;
            }
        }
        else if (_requestedMenu)
        {
            // Requested but never came up: undo the request in case it lands later.
            _requestedMenu = false;
            try { host.DismissMenu(); } catch { /* */ }
        }
        OpenFallbackWindow(host, why);
    }

    private static void StepAwaitDown(IModsShortcutHost host)
    {
        if (!host.MenuUp)
        {
            string why = _awaitDownWhy;
            _awaitDown = 0;
            _awaitDownWhy = "";
            OpenFallbackWindow(host, why);
            return;
        }
        if (--_awaitDown <= 0)
        {
            _awaitDownWhy = "";
            CancelPending(host, WhyMenuStayedUp);
        }
    }

    private static void OpenFallbackWindow(IModsShortcutHost host, string why)
    {
        _lastFallback = why;
        _lastRoute = "standalone";
        _standalone = true;
        if (!_fallbackLogged)
        {
            _fallbackLogged = true;
            SafeLog(host, LogPrefix + "fell back to standalone window why=" + why + " reason=" + _reason);
        }
        host.OpenStandalone(_reason);
    }

    private static void CancelPending(IModsShortcutHost? host, string why)
    {
        bool dismiss = _requestedMenu && _frames > 0;
        _frames = 0;
        _elapsed = 0;
        _awaitDown = 0;
        _awaitDownWhy = "";
        _requestedMenu = false;
        _lastCancel = why;
        if (host == null) return;
        if (dismiss)
        {
            // Unconditional: a request that has not landed yet must not land later with no owner.
            try { host.DismissMenu(); } catch { /* */ }
        }
        SafeLog(host, LogPrefix + "open canceled why=" + why);
    }

    private static void StepAfterClose(IModsShortcutHost host)
    {
        switch (DecideAfterClose(host.WorkspaceOpen, host.MenuUp, host.LastCloseReason, host.NativeReturnPending, _dismissWait))
        {
            case ShortcutAfterClose.Keep:
                _dismissWait = 0;
                return;
            case ShortcutAfterClose.Forget:
                _raisedMenu = false;
                _dismissWait = 0;
                return;
            case ShortcutAfterClose.Wait:
                _dismissWait++;
                return;
            default:
                _raisedMenu = false;
                _dismissWait = 0;
                bool dismissed = false;
                try { dismissed = host.DismissMenu(); } catch { dismissed = false; }
                SafeLog(host, LogPrefix + "closed to gameplay dismissed=" + (dismissed ? "true" : "false")
                    + " closeReason=" + (host.LastCloseReason ?? ""));
                return;
        }
    }

    private static void EndSettle()
    {
        _settle = -1;
        _settleReselects = 0;
        _settlePointer = false;
    }

    private static bool SafeRead(Func<bool> read)
    {
        try { return read(); }
        catch { return false; }
    }

    private static void SafeLog(IModsShortcutHost host, string line)
    {
        try { host.Log(line); }
        catch { /* a logger that throws must not pin UI */ }
    }
}
