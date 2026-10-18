using System;

namespace Progenitor.Ui;

/// <summary>
/// The Escape order over a game window (game-free). The game's chest and station windows close themselves on
/// <c>Menu_Close</c> (Escape) in their own input handler, which the game walks at order 502, ahead of Progenitor's
/// panel mask at 599. While a Progenitor window that Escape closes is shown over such a window, the menu-hotkey guard
/// at 498 takes the close actions too, so the first Escape closes the Progenitor window and the next one the game's.
/// While a Progenitor text field has the keyboard over such a window, the guard also takes Interact, which closes a
/// chest window on its key release.
/// </summary>
internal static class GameWindowUnderPanel
{
    /// <summary>
    /// The game's open HUD menu (<c>OpenHUDMenuSystem.CurrentMenuType</c>, the world pick's cached read), 0 with none.
    /// Set by the client runtime at Core install (<c>ContainerReturnClient</c>) and reset at uninstall; tests set it.
    /// </summary>
    internal static Func<int> MenuTypeNow = NoMenu;

    internal static int NoMenu() => 0;

    /// <summary>True when <paramref name="menuType"/> (<c>OpenHUDMenuSystem.CurrentMenuType</c>) is a chest, station or other interact window.</summary>
    internal static bool IsInteractWindow(int menuType) => UiWorldPickMenus.TryGet(menuType, out _);

    /// <summary>Hold the game's close actions this frame: an interact window is open under a Progenitor window that Escape closes.</summary>
    internal static bool HoldsClose(int menuType, bool escapeClosesAProgenitorWindow) =>
        escapeClosesAProgenitorWindow && IsInteractWindow(menuType);

    /// <summary>
    /// A shown panel the next Escape hides on its own, ahead of the game's window under it: a window (a game window such
    /// as a mod's hub, or a standard window), or a floating panel that takes the input now (<see cref="UiCaptureMode.WhileShown"/>,
    /// or <see cref="UiCaptureMode.WhenHovered"/> under the pointer), the same "takes the input" rule the topmost-window
    /// close uses, so a tracker left on screen never costs the player a second Escape at a chest. Never a Mods workspace
    /// pane (the workspace lives in the game's full-screen menu, never over a chest), a panel mounted in a chest or station
    /// tab (it is part of the game's window and goes with it), or a <see cref="UiCaptureMode.Never"/> panel (it registers
    /// no input context, so the game's window keeps its Escape, as documented for that mode).
    /// </summary>
    internal static bool PanelTakesEscape(bool workspacePane, bool mountedInTab, UiCaptureMode mode, bool isWindow, bool takesInputNow) =>
        !workspacePane && !mountedInTab && mode != UiCaptureMode.Never && (isWindow || takesInputNow);

    /// <summary>
    /// The Escape sweep with a chest or station window open under panels that take Escape
    /// (<see cref="PanelTakesEscape"/>): the panels mounted in that window's tabs stay, because the window stays (the
    /// guard holds its close). With no such panel shown, the sweep and the game's window close together, as before.
    /// </summary>
    internal static bool SweepKeepsMounted(bool interactWindowOpen, bool anyPanelTakesEscape, bool mountedInTab) =>
        interactWindowOpen && anyPanelTakesEscape && mountedInTab;

    /// <summary>Hold Interact this frame: an interact window is open and a Progenitor text field has the keyboard.</summary>
    internal static bool HoldsInteract(int menuType, bool progenitorFieldFocused) =>
        progenitorFieldFocused && IsInteractWindow(menuType);
}
