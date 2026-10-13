using System;
using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>
/// KEYS7: what the back/forward history needs from the game's full-screen menu (the Esc / K / J / M / P
/// menu with the Emotes, Vampire Powers, Spellbook, Map, V Blood, Social and System tabs). Game-free, so
/// the history is tested with a fake; the client uses <c>FullscreenMenuProbe</c>. Every member is read
/// on the main thread once a frame and must never throw.
/// </summary>
internal interface IGameMenuProbe
{
    /// <summary>The game's full-screen menu is the HUD's current menu.</summary>
    bool MenuOpen { get; }

    /// <summary>Its current top tab (a <c>FullscreenMenuState</c> name), or empty.</summary>
    string CurrentTab { get; }

    /// <summary>Whether <paramref name="tab"/> is a tab the menu shows now (a hidden tab is skipped).</summary>
    bool IsTabAvailable(string tab);

    /// <summary>Select <paramref name="tab"/> the way a click on its tab does. False when it cannot now.</summary>
    bool SelectTab(string tab);

    /// <summary>Ask the game to open its full-screen menu (it lands on a tab on a later frame).</summary>
    bool OpenMenu();

    /// <summary>The adapter for the current page's inner tabs, or null (none known, or inner tabs are off).</summary>
    IMenuInnerTabs? InnerTabs { get; }

    /// <summary>KEYS7: the inner-tab adapter a page has (whether or not it is shown or enabled), or null.</summary>
    IMenuInnerTabs? InnerTabsFor(string tab);
}

/// <summary>
/// KEYS7: one page's inner tabs (the school or sub-tab bar under a top tab), when the page has a readable
/// current index and a select the game's own buttons use. Optional per page.
/// </summary>
internal interface IMenuInnerTabs
{
    /// <summary>The adapter's name for the readback (<c>SpellbookSchools</c>).</summary>
    string Name { get; }

    /// <summary>The current inner tab, or -1 when it cannot be read.</summary>
    int Current { get; }

    /// <summary>How many inner tabs the page has now, or -1.</summary>
    int Count { get; }

    /// <summary>Select inner tab <paramref name="index"/>. False when it cannot.</summary>
    bool Select(int index);
}

/// <summary>No game menu (the title screen, the server, tests that do not set one).</summary>
internal sealed class NullGameMenuProbe : IGameMenuProbe
{
    internal static readonly NullGameMenuProbe Instance = new();

    public bool MenuOpen => false;

    public string CurrentTab => "";

    public bool IsTabAvailable(string tab) => false;

    public bool SelectTab(string tab) => false;

    public bool OpenMenu() => false;

    public IMenuInnerTabs? InnerTabs => null;

    public IMenuInnerTabs? InnerTabsFor(string tab) => null;
}

/// <summary>
/// KEYS7 (requested 2026-09-30: "switch between Vampire Powers tabs, go to Mods, press Back, go to
/// Social"): the game's full-screen menu as part of the one back/forward history the Mods workspace keeps
/// (<see cref="ModsMenu"/>'s navigation). The probe is installed by the client's keybind host; the inner
/// tabs are off unless <c>[Ui] MenuHistoryInnerTabs</c> is on (their native calls are not yet live-checked).
/// </summary>
internal static class GameMenuHistory
{
    private static IGameMenuProbe _probe = NullGameMenuProbe.Instance;

    internal static IGameMenuProbe Probe
    {
        get => _probe;
        set => _probe = value ?? NullGameMenuProbe.Instance;
    }

    /// <summary>
    /// KEYS7: <c>[Ui] MenuHistoryInnerTabs</c> (default true since the live check 2026-09-30). On, the history also records and restores the
    /// inner tab of a game menu page that has an adapter (<see cref="IMenuInnerTabs"/>; today the Spellbook
    /// schools).
    /// </summary>
    internal static bool InnerTabsEnabled { get; set; }

    internal const string ConfigSection = "Ui";

    internal const string InnerTabsConfigKey = "MenuHistoryInnerTabs";

    internal const string InnerTabsConfigDescription =
        "Back and forward in the game's menus also remember and restore the tab inside a page (today the Spellbook "
        + "school tabs). On by default (checked in game 2026-09-30); turn it off to keep only the top tabs "
        + "(it reads SpellbookMenuMapperSystem._SelectedTabIndex and invokes the page's OnTabClicked).";

    internal static bool SafeMenuOpen()
    {
        try { return Probe.MenuOpen; }
        catch { return false; }
    }

    /// <summary>The top tab that is the game's world map (a <c>FullscreenMenuState</c> name).</summary>
    internal const string MapTab = "Map";

    /// <summary>
    /// MAPTP1: the full-screen menu is open on its Map tab (the M key opens it there). Never throws; false
    /// on the title screen, the server and with no probe.
    /// </summary>
    internal static bool SafeMapOpen()
    {
        try
        {
            var probe = Probe;
            return probe.MenuOpen && string.Equals(probe.CurrentTab, MapTab, StringComparison.Ordinal);
        }
        catch { return false; }
    }

    /// <summary>The game step the menu is on now, or null when it is closed or its tab is unknown.</summary>
    internal static NavStep? Current()
    {
        try
        {
            var probe = Probe;
            if (!probe.MenuOpen) return null;
            string tab = probe.CurrentTab ?? "";
            if (tab.Length == 0) return null;
            int inner = -1;
            if (InnerTabsEnabled)
            {
                try { inner = probe.InnerTabs?.Current ?? -1; }
                catch { inner = -1; }
            }

            return NavStep.GameTab(tab, inner);
        }
        catch { return null; }
    }

    /// <summary><c>mods_ui menu_tab_state</c>: the game menu as the history sees it.</summary>
    internal static Dictionary<string, object?> Describe()
    {
        var d = new Dictionary<string, object?>(StringComparer.Ordinal);
        try
        {
            var probe = Probe;
            d["probe"] = probe.GetType().Name;
            d["menuOpen"] = probe.MenuOpen;
            d["tab"] = probe.CurrentTab ?? "";
            IMenuInnerTabs? inner = null;
            try { inner = probe.InnerTabs; }
            catch { inner = null; }
            d["innerTabsEnabled"] = InnerTabsEnabled;
            d["inner"] = inner == null ? null : new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["adapter"] = inner.Name,
                ["current"] = SafeInt(() => inner.Current),
                ["count"] = SafeInt(() => inner.Count),
            };
            var tabs = new Dictionary<string, object?>(StringComparer.Ordinal);
            var pages = new Dictionary<string, object?>(StringComparer.Ordinal);
            string current = probe.CurrentTab ?? "";
            foreach (var name in TopTabs)
            {
                tabs[name] = probe.IsTabAvailable(name);
                IMenuInnerTabs? adapter = null;
                try { adapter = probe.InnerTabsFor(name); }
                catch { adapter = null; }
                if (adapter == null)
                {
                    pages[name] = "no adapter";
                    continue;
                }

                bool shown = probe.MenuOpen && string.Equals(current, name, StringComparison.OrdinalIgnoreCase);
                pages[name] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["adapter"] = adapter.Name,
                    ["enabled"] = InnerTabsEnabled,
                    // Read only while the page is shown (its values mean nothing on another tab).
                    ["current"] = shown ? SafeInt(() => adapter.Current) : null,
                    ["count"] = shown ? SafeInt(() => adapter.Count) : null,
                };
            }

            d["available"] = tabs;
            d["pages"] = pages;
            d["step"] = Current()?.Readback();
        }
        catch (Exception e) { d["error"] = e.Message; }
        return d;
    }

    private static int SafeInt(Func<int> read)
    {
        try { return read(); }
        catch { return -1; }
    }

    /// <summary>The game's top tabs in its order (client <c>FullscreenMenuState</c> 0-6).</summary>
    internal static readonly string[] TopTabs = { "Emotes", "VampirePowers", "SpellBook", "Map", "VBlood", "Social", "System" };

    internal static void ResetForTests()
    {
        _probe = NullGameMenuProbe.Instance;
        InnerTabsEnabled = false;
    }
}
