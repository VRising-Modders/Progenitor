using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>
/// Which of the game's interact panels a world pick can capture, and as which kind. Values are the
/// game's <c>ProjectM.UI.HUDMenuType</c> numbers (a byte enum; client metadata). Only menus marked
/// <c>[InteractMenu]</c> whose init data carries the target entity first are listed; the waygate map
/// and the blood altar open without a target in their init data and are not pickable.
///
/// <para>The init-data component and its target field per menu live in the client half
/// (<c>WorldPickBackend</c>); the client checks that the init entity really carries that component
/// before reading it, so a pairing that a game update changed passes the interact through instead of
/// reading the wrong bytes.</para>
/// </summary>
internal static class UiWorldPickMenus
{
    internal readonly struct Info
    {
        internal Info(int menuType, string name, UiWorldPickKind kind)
        {
            MenuType = menuType;
            Name = name;
            Kind = kind;
        }

        internal int MenuType { get; }

        internal string Name { get; }

        internal UiWorldPickKind Kind { get; }
    }

    /// <summary><c>HUDMenuType.CharacterInfo</c>: the character inventory (and its other tabs, the build menu among them).</summary>
    internal const int CharacterMenu = 1;

    internal const int ContainerMenu = 3;
    internal const int WorkstationMenu = 4;
    internal const int RefinementstationMenu = 6;
    internal const int UnitSpawnerstationMenu = 10;
    internal const int ServantCoffinMenu = 11;
    internal const int ResearchstationMenu = 17;
    internal const int CastleHeartMenu = 18;
    internal const int SalvagestationMenu = 22;
    internal const int PrisonMenu = 24;
    internal const int ServantMenu = 25;
    internal const int TraderMenu = 28;
    internal const int JewelCraftingMenu = 29;
    internal const int ForgeMenu = 30;
    internal const int LightningRodMenu = 31;
    internal const int RespecStationMenu = 34;
    internal const int CastleHeartRebuildingMenu = 35;
    internal const int MusicPlayerStationMenu = 36;
    internal const int SpellSchoolPassiveMenu = 38;
    internal const int BloodMixerMenu = 39;
    internal const int ArenaStationMenu = 40;
    internal const int StablesMenu = 42;
    internal const int FusionForgeMenu = 43;
    internal const int InventoryRouteStationMenu = 45;

    private static readonly Dictionary<int, Info> ByType = Build();

    /// <summary>Every pickable menu, in menu-type order.</summary>
    internal static IEnumerable<Info> All
    {
        get
        {
            var list = new List<Info>(ByType.Values);
            list.Sort((a, b) => a.MenuType.CompareTo(b.MenuType));
            return list;
        }
    }

    /// <summary>The entry for <paramref name="menuType"/>, or false when that panel is not pickable.</summary>
    internal static bool TryGet(int menuType, out Info info) => ByType.TryGetValue(menuType, out info);

    /// <summary>True when <paramref name="kinds"/> accepts <paramref name="kind"/>.</summary>
    internal static bool Accepts(UiWorldPickKinds kinds, UiWorldPickKind kind) => ((int)kinds & (int)kind) != 0;

    /// <summary>
    /// The kind a pick with <paramref name="kinds"/> captures <paramref name="menuType"/> as, or false
    /// when the menu is not pickable or not wanted.
    /// </summary>
    internal static bool Wants(UiWorldPickKinds kinds, int menuType, out Info info) =>
        TryGet(menuType, out info) && Accepts(kinds, info.Kind);

    /// <summary>
    /// Whether <see cref="UiWorldPickOptions.CloseGameWindows"/> closes the open HUD menu <paramref name="menuType"/>
    /// (<c>OpenHUDMenuSystem.CurrentMenuType</c>): an interact window a pick could capture (a container, a station, the
    /// castle heart and the like) or the character inventory. Nothing open (0), unreadable (-1) and every other menu (the
    /// map, the full-screen menu) stay as they are.
    /// </summary>
    internal static bool ClosesForPick(int menuType) => menuType == CharacterMenu || ByType.ContainsKey(menuType);

    private static Dictionary<int, Info> Build()
    {
        var map = new Dictionary<int, Info>();
        void Add(int type, string name, UiWorldPickKind kind) => map[type] = new Info(type, name, kind);

        Add(ContainerMenu, "ContainerMenu", UiWorldPickKind.Container);

        Add(WorkstationMenu, "WorkstationMenu", UiWorldPickKind.Station);
        Add(RefinementstationMenu, "RefinementstationMenu", UiWorldPickKind.Station);
        Add(UnitSpawnerstationMenu, "UnitSpawnerstationMenu", UiWorldPickKind.Station);
        Add(ServantCoffinMenu, "ServantCoffinMenu", UiWorldPickKind.Station);
        Add(ResearchstationMenu, "ResearchstationMenu", UiWorldPickKind.Station);
        Add(SalvagestationMenu, "SalvagestationMenu", UiWorldPickKind.Station);
        Add(JewelCraftingMenu, "JewelCraftingMenu", UiWorldPickKind.Station);
        Add(ForgeMenu, "ForgeMenu", UiWorldPickKind.Station);
        Add(LightningRodMenu, "LightningRodMenu", UiWorldPickKind.Station);
        Add(RespecStationMenu, "RespecStationMenu", UiWorldPickKind.Station);
        Add(MusicPlayerStationMenu, "MusicPlayerStationMenu", UiWorldPickKind.Station);
        Add(SpellSchoolPassiveMenu, "SpellSchoolPassiveMenu", UiWorldPickKind.Station);
        Add(BloodMixerMenu, "BloodMixerMenu", UiWorldPickKind.Station);
        Add(StablesMenu, "StablesMenu", UiWorldPickKind.Station);
        Add(FusionForgeMenu, "FusionForgeMenu", UiWorldPickKind.Station);
        Add(InventoryRouteStationMenu, "InventoryRouteStationMenu", UiWorldPickKind.Station);

        Add(CastleHeartMenu, "CastleHeartMenu", UiWorldPickKind.Other);
        Add(PrisonMenu, "PrisonMenu", UiWorldPickKind.Other);
        Add(ServantMenu, "ServantMenu", UiWorldPickKind.Other);
        Add(TraderMenu, "TraderMenu", UiWorldPickKind.Other);
        Add(CastleHeartRebuildingMenu, "CastleHeartRebuildingMenu", UiWorldPickKind.Other);
        Add(ArenaStationMenu, "ArenaStationMenu", UiWorldPickKind.Other);
        return map;
    }
}
