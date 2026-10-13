#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using Progenitor.Ecs;
using ProjectM.UI;
using Unity.Entities;

namespace Progenitor.Ui;

/// <summary>
/// BUILDSEARCH4: open and close the game's build menu without input or window focus, the way the B key
/// does it (for the Dev bridge, so live tests never take focus).
///
/// <para><b>What the key does</b> (client <c>MenuInputSystem.HandleInput</c>, <c>ButtonInputAction</c> 0x15
/// → <c>HUDMenuManager.ToggleMenu&lt;CharacterMenu.InitData&gt;</c>, read in the game's native
/// code): with no HUD menu open it creates an event
/// entity carrying <c>OpenMenuEvent {Menu = CharacterInfo, InitDataEntity}</c> and an init entity carrying
/// <c>CharacterMenu.InitData {DefaultTab = Building}</c>, which the game's open-menu system turns into the
/// character menu on its Building tab (the build menu); with a menu open it calls
/// <c>HUDMenuManager.CloseMenu(world)</c>. Opening here creates the same two entities and writes the
/// fields raw at their dump offsets (<c>layout.txt</c>, "Build menu open/close"): no generic over a game
/// value type, no native call with a struct parameter. Closing calls the same static, which takes only the
/// world.</para>
///
/// <para>The key also checks <c>BuildMenuAvailability.IsAvailable</c> (the build-menu progression
/// requirement) before opening; this does not, so a test character without that unlock still gets the
/// menu. It is a test tool, not a gameplay path.</para>
/// </summary>
internal static unsafe class BuildMenuOpener
{
    /// <summary>
    /// The HUD menu the game has open (<c>OpenHUDMenuSystem.CurrentMenuType</c>, the field the key's toggle
    /// reads), as its int value; -1 when the system is missing.
    /// </summary>
    internal static int CurrentMenuType(World world)
    {
        try
        {
            var system = world.GetExistingSystemManaged<OpenHUDMenuSystem>();
            return system == null ? -1 : (int)system.CurrentMenuType;
        }
        catch { return -1; }
    }

    internal static Dictionary<string, object?> Open(World world)
    {
        if (world == null || !world.IsCreated) throw new InvalidOperationException("no client world (join a game first)");
        int current = CurrentMenuType(world);
        if (current == Layout.HUDMenuTypeCharacterInfo && BuildMenuSearch.IsBuildMenuShown(world))
            return new Dictionary<string, object?> { ["requested"] = "none", ["note"] = "the build menu is already open" };
        var em = world.EntityManager;
        var eventType = ComponentIndex.Of<OpenMenuEvent>(ComponentType.AccessMode.ReadWrite);
        var initType = ComponentIndex.Of<CharacterMenu.InitData>(ComponentType.AccessMode.ReadWrite);

        var init = em.CreateEntity(initType);
        byte* initData = (byte*)em.GetComponentDataRawRW(init, initType.TypeIndex);
        if (initData == null)
        {
            em.DestroyEntity(init);
            throw new InvalidOperationException("CharacterMenu.InitData could not be written");
        }

        *(int*)(initData + Layout.CharacterMenuInitDataDefaultTabOffset) = Layout.CharacterMenuTabBuilding;

        var ev = em.CreateEntity(eventType);
        byte* evData = (byte*)em.GetComponentDataRawRW(ev, eventType.TypeIndex);
        if (evData == null)
        {
            em.DestroyEntity(ev);
            em.DestroyEntity(init);
            throw new InvalidOperationException("OpenMenuEvent could not be written");
        }

        *(int*)(evData + Layout.OpenMenuEventMenuOffset) = Layout.HUDMenuTypeCharacterInfo;
        *(int*)(evData + Layout.OpenMenuEventInitDataEntityOffset) = init.Index;
        *(int*)(evData + Layout.OpenMenuEventInitDataEntityOffset + sizeof(int)) = init.Version;
        Plugin.Logger?.LogInfo("BuildSearch: build menu open requested (OpenMenuEvent " + ev.Index + ":" + ev.Version
            + ", init " + init.Index + ":" + init.Version + ").");
        return new Dictionary<string, object?>
        {
            ["requested"] = "open",
            ["menuBefore"] = current,
            ["event"] = ev.Index + ":" + ev.Version,
            ["init"] = init.Index + ":" + init.Version,
        };
    }

    internal static Dictionary<string, object?> Close(World world)
    {
        if (world == null || !world.IsCreated) throw new InvalidOperationException("no client world (join a game first)");
        int current = CurrentMenuType(world);
        // As the key: only the character menu (which hosts the build menu) is closed; any other open menu is
        // left alone.
        if (current != Layout.HUDMenuTypeCharacterInfo)
            return new Dictionary<string, object?> { ["requested"] = "none", ["menu"] = current, ["note"] = "the build menu is not open" };
        HUDMenuManager.CloseMenu(world);
        Plugin.Logger?.LogInfo("BuildSearch: HUD menu close requested.");
        return new Dictionary<string, object?> { ["requested"] = "close" };
    }
}
#endif
