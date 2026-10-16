using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>
/// One station-type window the game can open, as <see cref="VanillaUiSite.StationPanel"/> sees it.
/// Game-free: the mapper and submenu are names, read out of the game's metadata, so the
/// classification can be tested without Unity.
/// </summary>
internal readonly struct VanillaStationWindow
{
    internal VanillaStationWindow(VanillaPanelKind kind, string mapper, string subMenu, string examples, bool routingTabs, bool hostsTabs, string note)
    {
        Kind = kind;
        Mapper = mapper;
        SubMenu = subMenu;
        Examples = examples;
        RoutingTabs = routingTabs;
        HostsTabs = hostsTabs;
        Note = note;
    }

    /// <summary>The <see cref="VanillaPanelTarget.Kind"/> the site reports for it; <c>None</c> for a window that is not part of the site.</summary>
    internal VanillaPanelKind Kind { get; }

    /// <summary>The managed mapper that keeps the window (<c>_XSubMenu</c>) and its station (<c>_Target</c>).</summary>
    internal string Mapper { get; }

    /// <summary>The window's <c>HUDMenu</c> type.</summary>
    internal string SubMenu { get; }

    /// <summary>Prefab examples, for the docs and the log line.</summary>
    internal string Examples { get; }

    /// <summary>
    /// The window carries the game's station/redistribution tab row (<c>InventoryRoutingParent</c>,
    /// with <c>StationTabButton</c> and <c>RoutingTabButton</c>), which the tab strip replaces.
    /// </summary>
    internal bool RoutingTabs { get; }

    /// <summary>Whether <c>Ui.AttachPanelTabs</c> draws its strip on this window.</summary>
    internal bool HostsTabs { get; }

    /// <summary>Why the window is in or out, or what differs from a workstation.</summary>
    internal string Note { get; }

    internal bool Covered => Kind != VanillaPanelKind.None;
}

/// <summary>What the shared per-frame scan read from one station mapper (game-free mirror).</summary>
internal readonly struct StationWindowState
{
    internal StationWindowState(VanillaPanelKind kind, bool shown, int targetIndex, int targetVersion)
    {
        Kind = kind;
        Shown = shown;
        TargetIndex = targetIndex;
        TargetVersion = targetVersion;
    }

    internal VanillaPanelKind Kind { get; }

    /// <summary>The submenu's GameObject is active in the hierarchy.</summary>
    internal bool Shown { get; }

    /// <summary>The mapper's <c>_Target</c>: the station entity the window was opened on.</summary>
    internal int TargetIndex { get; }

    internal int TargetVersion { get; }

    /// <summary>The mapper's <c>_Target</c> is not <c>Entity.Null</c> (index 0, version 0).</summary>
    internal bool HasTarget => TargetIndex != 0 || TargetVersion != 0;
}

/// <summary>
/// Which game windows count as <see cref="VanillaUiSite.StationPanel"/>, which of them host the
/// <c>AttachPanelTabs</c> strip, and the windows that were read and deliberately left out.
///
/// <para><b>Rule.</b> A window is a station panel when its mapper keeps the window in a
/// <c>_XSubMenu</c> field and the station entity in <c>_Target</c>, and the window is laid out like a
/// crafting or refining station (bonuses, then a recipe or input grid, then outputs). The target is
/// always the mapper's <c>_Target</c>: the station entity the player interacted with, the same
/// semantics the workstation and refinement windows have had since the site was added.</para>
///
/// <para><b>Tabs.</b> The tab strip takes the place of the game's own station/redistribution tab
/// row, so only windows with that row host it. A covered window without it (the unit stations)
/// still reports open, close and target to <c>AttachToVanilla</c>, but <c>AttachPanelTabs</c>
/// treats it as closed: there is no row to replace and the strip would cover the recipe grid.</para>
/// </summary>
internal static class VanillaStationWindows
{
    /// <summary>
    /// The covered windows, in the order the site checks them. The game shows one station window at
    /// a time, so the order only settles a frame in which a close and an open overlap.
    /// </summary>
    internal static readonly VanillaStationWindow[] Covered =
    {
        new(VanillaPanelKind.Workstation, "WorkstationMenuMapper", "WorkstationSubMenu",
            "workbench, tailor, alchemy table, anvil and the other recipe stations",
            routingTabs: true, hostsTabs: true, note: "the original station window"),
        new(VanillaPanelKind.RefinementStation, "RefinementstationMenuMapper", "RefinementstationSubMenu",
            "furnace, sawmill, grinder, blood press, paper press and the other converters",
            routingTabs: true, hostsTabs: true, note: "the original refinement window"),
        new(VanillaPanelKind.PrisonCell, "PrisonMenuMapper", "PrisonSubMenu",
            "TM_SpecialStation_PrisonCell and its variants",
            routingTabs: true, hostsTabs: true,
            note: "station/redistribution tab row like a workstation; the prisoner block, recipe grid and the cell's slots are hidden under a custom tab"),
        new(VanillaPanelKind.SalvageStation, "SalvagestationMenuMapper", "SalvagestationSubMenu",
            "devourer (salvage station; by prefab name)",
            routingTabs: true, hostsTabs: true,
            note: "station/redistribution tab row; no recipe grid, the input grid is the anchor"),
        new(VanillaPanelKind.UnitStation, "UnitSpawnerstationMenuMapper", "UnitSpawnerstationSubMenu",
            "TM_UnitStation_Tomb, TM_UnitStation_VerminNest, TM_UnitStation_NetherGate (by prefab name)",
            routingTabs: false, hostsTabs: false,
            note: "edge attachments only: the window has no station/redistribution tab row to replace"),
    };

    /// <summary>Station-like windows read from the game's metadata and left out of the site, with the reason.</summary>
    internal static readonly VanillaStationWindow[] NotCovered =
    {
        new(VanillaPanelKind.None, "ContainerMenuMapper", "ContainerSubMenu", "braziers (BurnContainerUI), chests",
            false, false, "already ContainerPanel: a brazier is a container window with a burn toggle"),
        new(VanillaPanelKind.None, "ServantCoffinstationMenuMapper", "ServantCoffinstationSubMenu", "TM_SpecialStation_ServantCoffin_Base",
            false, false, "servant layout (portrait, perks, action box), no recipe grid or tab row"),
        new(VanillaPanelKind.None, "ResearchstationMenuMapper", "ResearchstationSubMenu", "research tables",
            false, false, "research tree layout, no grid or tab row"),
        new(VanillaPanelKind.None, "SpellSchoolPassiveStationMenuMapper", "SpellSchoolPassiveStationSubMenu", "spell school passive station",
            false, false, "passive tree layout, no grid or tab row"),
        new(VanillaPanelKind.None, "ForgeMenuMapper", "ForgeSubMenu", "forge (craft, upgrade, repair weapons)",
            false, false, "three own grids, own status text pair; layout not read"),
        new(VanillaPanelKind.None, "FusionForgeMenuMapper", "FusionForgeSubMenu", "fusion forge (legendary weapons)",
            false, false, "fuse layout, no recipe grid or tab row"),
        new(VanillaPanelKind.None, "JewelCraftingMenuMapper", "JewelCraftingSubMenu", "jewelcrafting table",
            false, false, "own status text pair, no tab row; layout not read"),
        new(VanillaPanelKind.None, "BloodMixerMenuMapper", "BloodMixerSubMenu", "blood mixer",
            false, false, "blood quality layout, no recipe grid or tab row"),
        new(VanillaPanelKind.None, "LightningRodMenuMapper", "LightningRodSubMenu", "lightning rod",
            false, false, "charges layout, no tab row"),
        new(VanillaPanelKind.None, "StablesMenuMapper", "StablesSubMenu", "TM_SpecialStation_StablePen",
            false, false, "mount layout, no recipe grid or tab row"),
        new(VanillaPanelKind.None, "ArenaStationMenuMapper", "ArenaStationSubMenu", "TM_SpecialStation_ArenaStation",
            false, false, "no inventory half, own target field (_TargetArenaStation)"),
        new(VanillaPanelKind.None, "InventoryRouteStationMenuMapper", "InventoryRouteStationSubMenu", "TM_SpecialStation_InventoryRouting",
            false, false, "no inventory half, own target field (_TargetWorkstation)"),
        new(VanillaPanelKind.None, "MusicPlayerStationMenuMapper", "MusicPlayerStationMenu", "TM_SpecialStation_MusicPlayer",
            false, false, "full menu, no inventory half"),
        new(VanillaPanelKind.None, "RespecStationMenuMapper", "RespecStationMenu", "respec station",
            false, false, "full menu, no inventory half"),
        new(VanillaPanelKind.None, "TraderMenuMapper", "TraderSubMenu", "traders",
            false, false, "an NPC, not a castle station"),
    };

    private static readonly HashSet<VanillaPanelKind> Failed = new();

    /// <summary>
    /// Drop one window for the generation: its reader threw (a type or member the game renamed).
    /// True the first time, so the caller logs once. The other windows and sites are unaffected.
    /// </summary>
    internal static bool MarkFailed(VanillaPanelKind kind)
    {
        lock (Failed) return Failed.Add(kind);
    }

    internal static bool IsFailed(VanillaPanelKind kind)
    {
        lock (Failed) return Failed.Count != 0 && Failed.Contains(kind);
    }

    /// <summary>Core Uninstall (a new generation reads every window again).</summary>
    internal static void ResetFailures()
    {
        lock (Failed) Failed.Clear();
    }

    /// <summary>The kind a covered window reports, by its submenu type name; <c>None</c> for anything else.</summary>
    internal static VanillaPanelKind KindOfSubMenu(string? subMenu)
    {
        if (string.IsNullOrEmpty(subMenu)) return VanillaPanelKind.None;
        foreach (var w in Covered)
            if (string.Equals(w.SubMenu, subMenu, System.StringComparison.Ordinal)) return w.Kind;
        return VanillaPanelKind.None;
    }

    /// <summary>The catalog entry of a covered kind, or false.</summary>
    internal static bool TryGet(VanillaPanelKind kind, out VanillaStationWindow window)
    {
        foreach (var w in Covered)
        {
            if (w.Kind != kind) continue;
            window = w;
            return true;
        }

        window = default;
        return false;
    }

    /// <summary>A kind <see cref="VanillaUiSite.StationPanel"/> reports.</summary>
    internal static bool IsStation(VanillaPanelKind kind) => TryGet(kind, out _);

    /// <summary>
    /// Whether <c>Ui.AttachPanelTabs</c> on <paramref name="site"/> draws on a window of
    /// <paramref name="kind"/>. Containers always do; a station only when it has the tab row.
    /// </summary>
    internal static bool HostsTabs(VanillaUiSite site, VanillaPanelKind kind) => site switch
    {
        VanillaUiSite.ContainerPanel => kind == VanillaPanelKind.Container,
        VanillaUiSite.StationPanel => TryGet(kind, out var w) && w.HostsTabs,
        _ => false,
    };

    /// <summary>
    /// The station window the site shows this frame: the first of <paramref name="states"/> whose
    /// window is shown and whose mapper has a target, skipping a window dropped by
    /// <see cref="MarkFailed"/>. -1 when none is open. A shown window without a
    /// target (the frame the game is still filling it, or the frame after the station died) is closed.
    /// </summary>
    internal static int Pick(StationWindowState[] states)
    {
        if (states == null) return -1;
        for (int i = 0; i < states.Length; i++)
        {
            var s = states[i];
            if (s.Shown && s.HasTarget && IsStation(s.Kind) && !IsFailed(s.Kind)) return i;
        }

        return -1;
    }

    /// <summary>
    /// <see cref="Pick"/> and the target the site reports for it: the window's kind and the picked
    /// mapper's <c>_Target</c> (the station entity). The <c>NetworkId</c> is read from the world by
    /// the Unity half and is zero here.
    /// </summary>
    internal static bool TryResolve(StationWindowState[] states, out int index, out VanillaPanelTarget target)
    {
        index = Pick(states);
        if (index < 0)
        {
            target = default;
            return false;
        }

        var s = states[index];
        target = new VanillaPanelTarget(s.Kind, s.TargetIndex, s.TargetVersion);
        return true;
    }
}
