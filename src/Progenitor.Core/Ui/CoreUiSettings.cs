using BepInEx.Configuration;

namespace Progenitor.Ui;

/// <summary>
/// Settings organization stage 3: the entries of Core's own <c>progenitor.ui.cfg</c> that the
/// Configuration tab draws (<c>[Settings] EditOtherModsSettings</c>, <c>[Ui] MenuHistoryInnerTabs</c>). Set by the
/// client's <c>UiKeybindHost</c> when it binds them; null before that, on a server and in tests that do not set
/// them (the Configuration tab then leaves the row out). Game-free.
/// </summary>
internal static class CoreUiSettings
{
    internal const string EditOtherModsSection = "Settings";
    internal const string EditOtherModsKey = "EditOtherModsSettings";

    internal const string EditOtherModsDescription =
        "Allow the Progenitor settings panel to EDIT other BepInEx mods' settings, not just show them. Off by default: "
        + "a mod that syncs its own settings from a server is invisible to Progenitor's config sync, so an edit here could "
        + "write a server-owned value into that mod's cfg file. Applies at once: an open mod page is drawn again.";

    /// <summary><c>[Settings] EditOtherModsSettings</c> (default off; live since settings organization stage 3).</summary>
    internal static ConfigEntry<bool>? EditOtherModsSettings { get; set; }

    /// <summary><c>[Ui] MenuHistoryInnerTabs</c>: the game menu's inner tabs in back and forward.</summary>
    internal static ConfigEntry<bool>? MenuHistoryInnerTabs { get; set; }

    /// <summary>BROWSERFILTER1: <c>[ServerBrowser] ProgenitorOnly</c>'s section, key and words.</summary>
    internal const string ServerBrowserSection = "ServerBrowser";

    internal const string ServerBrowserProgenitorOnlyKey = "ProgenitorOnly";

    internal const string ServerBrowserProgenitorOnlyDescription =
        "Join Game shows only servers running Progenitor (the Show Only › Progenitor Servers checkbox, which writes this). "
        + "A server is listed as running Progenitor when it publishes the \"progenitor\" server-list tag. Default off. "
        + "Personal to this PC.";

    /// <summary>BROWSERFILTER1: <c>[ServerBrowser] ProgenitorOnly</c> (default off), the Join Game browser's Progenitor filter.</summary>
    internal static ConfigEntry<bool>? ServerBrowserProgenitorOnly { get; set; }

    /// <summary>TITLEVIDEO1: <c>[TitleScreen] Video</c> (default on), the title screen's moving background (<see cref="TitleVideoPolicy"/>).</summary>
    internal static ConfigEntry<bool>? TitleScreenVideo { get; set; }

    /// <summary>Three homes: <c>[Ui] ClosedDrawers</c>, the settings drawers the player closed (<see cref="SettingsDrawers"/>).</summary>
    internal static ConfigEntry<string>? ClosedDrawers { get; set; }

    /// <summary>P7b help (<see cref="UiHelpSettings"/>): <c>[Ui] HiddenTermMarkers</c>, the hidden term markers.</summary>
    internal static ConfigEntry<string>? HiddenTermMarkers { get; set; }

    /// <summary>P7b help: <c>[Ui] TermMarkerHovers</c>, hovers counted toward a marker's auto-hide.</summary>
    internal static ConfigEntry<string>? TermMarkerHovers { get; set; }

    /// <summary>P7b help: <c>[Ui] TermMarkerAutoHide</c>, hovers before a marker hides itself (default 3, 0 never).</summary>
    internal static ConfigEntry<int>? TermMarkerAutoHide { get; set; }

    /// <summary>P7b help: <c>[Ui] SeenTips</c>, the tip cards the player dismissed.</summary>
    internal static ConfigEntry<string>? SeenTips { get; set; }

    /// <summary>
    /// <c>EditOtherModsSettings</c> changed (or was bound): the catalog's foreign rows follow it at once, and an open
    /// mod page or Keybinds tab is drawn again, so a foreign row becomes editable (or read-only) without a Core reload.
    /// </summary>
    internal static void ApplyForeignEditPolicy(bool allow, string reason)
    {
        bool was = SettingsCatalog.AllowForeignEdits;
        SettingsCatalog.AllowForeignEdits = allow;
        if (was == allow) return;
        SettingsCatalog.BumpKeyVersion();
        Plugin.Logger?.LogInfo("Ui: [Settings] EditOtherModsSettings = " + allow + " (" + reason + "); other mods' settings are "
            + (allow ? "editable" : "read-only") + " now.");
        try { ModsMenu.NoteForeignEditPolicyChanged(); }
        catch (System.Exception e) { Plugin.Logger?.LogWarning("Ui: redrawing after EditOtherModsSettings threw: " + e.Message); }
    }

    internal static void Reset()
    {
        EditOtherModsSettings = null;
        MenuHistoryInnerTabs = null;
        ClosedDrawers = null;
        HiddenTermMarkers = null;
        TermMarkerHovers = null;
        TermMarkerAutoHide = null;
        SeenTips = null;
        ServerBrowserProgenitorOnly = null;
        TitleScreenVideo = null;
    }
}
