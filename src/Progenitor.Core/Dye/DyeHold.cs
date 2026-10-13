using Progenitor.Ui;

namespace Progenitor.Dye;

/// <summary>
/// Dye all's press-and-hold: Core's shared <see cref="HoldMachine"/> (phases, cancels, the one-shot
/// fire and the hold clamp live there, and the debug tools' map teleport uses the same machine), plus the
/// one rule that is Dye all's own — which presses start a charge.
///
/// <list type="bullet">
/// <item>A primary-button press on a color swatch starts a charge (nothing else does: not the
/// hide/show cell, not an empty cell, not a right click, not while the feature is off).</item>
/// <item>Everything after the press is <see cref="HoldMachine"/>: release, leave and panel-closed
/// cancels (<see cref="HoldCancel.Closed"/>), the fire on the filling tick, one hold at a time.</item>
/// </list>
/// </summary>
internal sealed class DyeHoldMachine : HoldMachine
{
    /// <summary>The swatch the live hold is on, or -1 (the machine's <see cref="HoldMachine.Target"/>).</summary>
    internal int Swatch => Target;

    /// <summary>
    /// A pointer went down on swatch <paramref name="swatch"/>. Starts a charge when the feature is
    /// on, the press is the primary button, the swatch is a color and no hold is live. Returns whether
    /// a charge started.
    /// </summary>
    internal bool Press(int swatch, bool primary, bool isColor, bool enabled, double holdSeconds, double now) =>
        Press(swatch, enabled && primary && isColor, holdSeconds, now);
}

/// <summary>
/// Game-free: when Dye all runs its scene search for <c>TransmogContainer</c>s. The game has more than one: the
/// character menu's and the interaction (chest, station) menu's inventory sub-menus each carry a picker, and the
/// second exists only once such a menu was opened. Every container found is kept, and the search runs again (at most
/// every two seconds) whenever none of the known ones is active, never because one is merely known. Searching only
/// while none was known kept the interaction menu's (inactive) picker and never watched the character menu's
/// (2026-10-08).
/// </summary>
internal static class DyePickerSearch
{
    /// <summary>
    /// <paramref name="knownActive"/> is how many known containers are active in the hierarchy; <paramref name="nextFind"/>
    /// is the earliest time of the next search.
    /// </summary>
    internal static bool ShouldFind(int knownActive, double now, double nextFind) => knownActive <= 0 && now >= nextFind;
}

/// <summary>What Dye all does on one quarter-second scan tick (<see cref="DyeScanGate.Decide"/>).</summary>
internal enum DyeScanStep
{
    /// <summary>Switched off or standing down: no search, no listing, and every swatch watch is released.</summary>
    Release = 0,

    /// <summary>No HUD menu open (or no client world: the title screen, loading): no search, no listing; watches are kept.</summary>
    Pause = 1,

    /// <summary>A HUD menu is open: find the color picker if it is not known and list its cells.</summary>
    Scan = 2,
}

/// <summary>
/// Game-free: when Dye all looks for the equipment color picker. The scene search and the cell listing run only
/// while the game has a HUD menu open (<c>OpenHUDMenuSystem.CurrentMenuType</c> is not <c>None</c>; the picker is
/// in the inventory sub-menu the character menu and the interaction menus share), never on the title screen,
/// while loading, or with the feature off.
/// </summary>
internal static class DyeScanGate
{
    /// <summary>The menu type could not be read (the system is missing).</summary>
    internal const int MenuUnknown = -1;

    /// <summary><c>HUDMenuType.None</c>.</summary>
    internal const int MenuNone = 0;

    /// <summary>
    /// <paramref name="menuType"/> is the open HUD menu as an int, <see cref="MenuUnknown"/> when it cannot be read.
    /// An unreadable menu type in a client world scans (the feature keeps working, at the old cost) rather than
    /// going dark; the caller logs that once.
    /// </summary>
    internal static DyeScanStep Decide(bool enabled, bool standingDown, bool hasClientWorld, int menuType)
    {
        if (!enabled || standingDown) return DyeScanStep.Release;
        if (!hasClientWorld) return DyeScanStep.Pause;
        if (menuType == MenuNone) return DyeScanStep.Pause;
        return DyeScanStep.Scan;
    }
}
