namespace Progenitor.Ui;

/// <summary>
/// Which build-menu category and tag buttons are drawn selected. Game-free, so the Recent and search rules are
/// unit-tested (<c>BuildMenuTabLookTests</c>).
/// </summary>
/// <remarks>
/// The game draws a selected category or tag through the button's <c>ForceHighlight</c>: every frame the build
/// menu mapper refreshes each entry with <c>IsSelected = (entry index == selected index)</c>, and the entry's
/// refresh writes that bit to <c>ForceHighlight</c> and repaints only when it changed. <c>ForceSelect</c> is a
/// stronger override the entries' refresh never writes, so a value Progenitor left there would stay forever;
/// these rules never set it. A Progenitor override is written after the mapper's update in the same frame and the
/// next update writes the game's own bit again, so the game takes the buttons back by itself one frame after an
/// override stops.
/// </remarks>
internal static class BuildMenuTabLook
{
    /// <summary>
    /// The highlight a native category or tag button gets after the mapper's update, or null to leave the game's
    /// own value. While Recent is shown, Recent is the only selected category and Recent has no tags, so every
    /// native category and tag is drawn unselected. A search draws every button unselected too (and dims them).
    /// </summary>
    internal static bool? NativeOverride(bool recentShown, bool searchActive)
        => recentShown || searchActive ? false : null;

    /// <summary>The Recent button's highlight: lit while Recent is shown and no search has disabled it.</summary>
    internal static bool RecentLit(bool recentShown, bool searchActive) => recentShown && !searchActive;

    /// <summary>
    /// The game's own highlight for a native button, used to put it back at once when an override ends (the
    /// mapper's next update writes the same value).
    /// </summary>
    internal static bool NativeLit(int entryIndex, int selectedIndex) => selectedIndex >= 0 && entryIndex == selectedIndex;
}
