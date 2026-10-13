#if !PROGENITOR_TESTS
using System;
using ProjectM.UI;
using UnityEngine.EventSystems;

namespace Progenitor.Ui;

/// <summary>
/// KEYS7: the game's full-screen menu for the back/forward history (<see cref="IGameMenuProbe"/>). Reads only
/// what <see cref="NativeModsEntry"/>'s tick already read this frame (the HUD's <c>FullscreenMenu</c> and its
/// <c>CurrentView.State</c>), and selects a top tab the way a click does: the tab entry's
/// <c>FullscreenMenu_OuterTabEntry.OnPointerClick</c> and its <c>EventTrigger</c>, the path the dev bridge's
/// <c>mods_ui native_tab</c> drives live, so the highlight and the sound are the game's own. Opening the menu
/// is <c>HUDMenuManager.GoToMenu(FullscreenMenu)</c>, the call the Mods shortcut route already makes. No game
/// method is patched or hooked.
///
/// <para>The inner tabs (<see cref="SpellbookSchoolTabs"/>) are off unless <c>[Ui] MenuHistoryInnerTabs</c> is
/// on: their calls are new here and not yet checked live.</para>
/// </summary>
internal sealed class FullscreenMenuProbe : IGameMenuProbe
{
    public bool MenuOpen
    {
        get
        {
            try { return NativeModsEntry.GameMenuFound != null; }
            catch { return false; }
        }
    }

    public string CurrentTab
    {
        get
        {
            try
            {
                if (NativeModsEntry.GameMenuFound == null || NativeModsEntry.GameMenuView is not int view) return "";
                // Only the game's own seven tabs; the owned MODS tab is the workspace's step.
                return view >= 0 && view < GameMenuHistory.TopTabs.Length ? ((FullscreenMenuState)view).ToString() : "";
            }
            catch { return ""; }
        }
    }

    public bool IsTabAvailable(string tab)
    {
        var entry = FindTab(tab);
        try { return entry != null && entry.gameObject.activeInHierarchy; }
        catch { return false; }
    }

    public bool SelectTab(string tab)
    {
        var entry = FindTab(tab);
        if (entry == null) return false;
        try
        {
            var es = EventSystem.current;
            if (es == null) return false;
            var ev = new PointerEventData(es) { button = PointerEventData.InputButton.Left };
            entry.OnPointerClick(ev);
            entry.GetComponent<EventTrigger>()?.OnPointerClick(ev);
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: menu history tab select threw: " + e.Message);
            return false;
        }
    }

    public bool OpenMenu() => NativeModsEntry.TryRequestFullscreenMenu();

    public IMenuInnerTabs? InnerTabs => GameMenuHistory.InnerTabsEnabled && MenuOpen ? InnerTabsFor(CurrentTab) : null;

    /// <summary>KEYS7: the page adapters (Spellbook schools only; see menu-navigation.md for the pages without one).</summary>
    public IMenuInnerTabs? InnerTabsFor(string tab) =>
        string.Equals(tab, "SpellBook", StringComparison.OrdinalIgnoreCase) ? SpellbookSchoolTabs.Instance : null;

    private static FullscreenMenu_OuterTabEntry? FindTab(string tab)
    {
        try
        {
            var menu = NativeModsEntry.GameMenuFound;
            var group = menu?.OuterTabGroup;
            if (group == null) return null;
            var tabs = group.GetComponentsInChildren<FullscreenMenu_OuterTabEntry>(true);
            if (tabs == null) return null;
            for (int i = 0; i < tabs.Length; i++)
            {
                var t = tabs[i];
                if (t == null) continue;
                if (string.Equals(t.State.ToString(), tab, StringComparison.OrdinalIgnoreCase)) return t;
            }
        }
        catch { /* not found */ }
        return null;
    }
}

/// <summary>
/// KEYS7 (off by default, <c>[Ui] MenuHistoryInnerTabs</c>; live 2026-09-30: the full-screen menu mapper's shared
/// inner-bar fields stayed 0 while the player clicked the Spellbook schools, so that adapter was dropped): the
/// Spellbook page's school tabs. The selected school is <c>SpellbookMenuMapperSystem._SelectedTabIndex</c> (int,
/// 0x70), which the page's own tab handler <c>SpellbookMenuMapperSystem.OnTabClicked(int itemIndex)</c>
/// sets. That handler is what a click on a school tab runs: <c>Initialize</c> stores it
/// as the view's <c>FullscreenMenuView.OnTabClicked</c> (<c>Action&lt;int&gt;</c>, 0x30), and a select invokes that
/// delegate on the current view. <c>RefreshTabs</c> redraws the tab highlight from the index. The
/// tab count is not read (it is a list over a game struct). Not live-checked.
/// </summary>
internal sealed class SpellbookSchoolTabs : IMenuInnerTabs
{
    internal static readonly SpellbookSchoolTabs Instance = new();

    public string Name => "SpellbookSchools";

    private static SpellbookMenuMapperSystem? Mapper()
    {
        try
        {
            var world = Worlds.Client;
            if (world == null || !world.IsCreated) return null;
            return world.GetExistingSystemManaged<SpellbookMenuMapperSystem>();
        }
        catch { return null; }
    }

    public int Current
    {
        get
        {
            try { return Mapper()?._SelectedTabIndex ?? -1; }
            catch { return -1; }
        }
    }

    public int Count => -1;

    public bool Select(int index)
    {
        if (index < 0) return false;
        try
        {
            var view = NativeModsEntry.GameMenuFound?.CurrentView?.Instance;
            var handler = view?.OnTabClicked;
            if (handler == null) return false;
            handler.Invoke(index);
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: menu history Spellbook school select threw: " + e.Message);
            return false;
        }
    }
}
#endif
