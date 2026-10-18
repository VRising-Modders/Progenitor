#if !PROGENITOR_TESTS
using System;
using System.Runtime.CompilerServices;
using Progenitor.Ai;
using Progenitor.Ecs;
using Progenitor.Hooks;
using ProjectM;
using ProjectM.UI;
using Unity.Entities;
using UnityEngine;

namespace Progenitor.Inventory;

/// <summary>
/// STATIONPREVIEW1: the game windows a preview can open, one place per window: the container window and the crafting,
/// refining and salvage station windows. Each opens the same way (an init entity carrying the menu's
/// <c>InitData {Target, InteractBuff}</c>, written raw at layout offsets, and an <c>OpenMenuEvent</c>, the game's own
/// open as <c>InteractSystemHUD</c> builds it), and each window's mapper names its target and interaction. The station
/// readers are each in their own non-inlined method, so a member a game update renames fails only that window.
/// </summary>
internal static unsafe class ContainerPreviewWindows
{
    /// <summary>What one preview window shows now.</summary>
    internal struct Read
    {
        /// <summary>The game's open HUD menu (any).</summary>
        public int Menu;
        /// <summary>The window's mapper exists.</summary>
        public bool Present;
        /// <summary>The mapper's target and interaction.</summary>
        public Entity Target, Interact;
        /// <summary>The window's own half (the container or station sub menu) and the character inventory half.</summary>
        public Component? Sub;
        public InventorySubMenu? Bag;
    }

    /// <summary>The window <paramref name="menuType"/> reads now (its mapper; <see cref="Read.Menu"/> is whatever is open).</summary>
    internal static Read ReadWindow(World world, int menuType)
    {
        var r = new Read { Menu = Progenitor.Ui.BuildMenuOpener.CurrentMenuType(world) };
        try
        {
            switch (menuType)
            {
                case ContainerPreviewMenus.WorkstationMenu: Workstation(world, ref r); break;
                case ContainerPreviewMenus.RefinementstationMenu: Refinement(world, ref r); break;
                case ContainerPreviewMenus.SalvagestationMenu: Salvage(world, ref r); break;
                default: Container(world, ref r); break;
            }
        }
        catch { r.Present = false; r.Target = Entity.Null; r.Interact = Entity.Null; r.Sub = null; r.Bag = null; }
        return r;
    }

    private static void Container(World world, ref Read r)
    {
        var m = world.GetExistingSystemManaged<ContainerMenuMapper>();
        if (m == null) return;
        r.Present = true; r.Target = m._Target; r.Interact = m._InteractBuff; r.Sub = m._ContainerSubMenu; r.Bag = m._InventorySubMenu;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Workstation(World world, ref Read r)
    {
        var m = world.GetExistingSystemManaged<WorkstationMenuMapper>();
        if (m == null) return;
        r.Present = true; r.Target = m._Target; r.Interact = m._InteractBuff; r.Sub = m._WorkstationSubMenu; r.Bag = m._InventorySubMenu;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Refinement(World world, ref Read r)
    {
        var m = world.GetExistingSystemManaged<RefinementstationMenuMapper>();
        if (m == null) return;
        r.Present = true; r.Target = m._Target; r.Interact = m._InteractBuff; r.Sub = m._RefinementStationSubMenu; r.Bag = m._InventorySubMenu;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Salvage(World world, ref Read r)
    {
        var m = world.GetExistingSystemManaged<SalvagestationMenuMapper>();
        if (m == null) return;
        r.Present = true; r.Target = m._Target; r.Interact = m._InteractBuff; r.Sub = m._SalvageStationSubMenu; r.Bag = m._InventorySubMenu;
    }

    /// <summary>
    /// The game's own open for window <paramref name="menuType"/> on <paramref name="target"/> with no interaction behind it:
    /// the init entity and the open event, raw at the layout offsets (the container preview's N1 recipe for each window).
    /// </summary>
    internal static void WriteOpen(World world, Entity target, int menuType)
    {
        var em = world.EntityManager;
        var (initType, targetOffset, interactOffset, menu) = InitFor(menuType);
        var eventType = ComponentIndex.Of<OpenMenuEvent>(ComponentType.AccessMode.ReadWrite);
        var init = em.CreateEntity(initType);
        byte* initData = (byte*)em.GetComponentDataRawRW(init, initType.TypeIndex);
        if (initData == null)
        {
            em.DestroyEntity(init);
            throw new InvalidOperationException("the window's init data could not be written");
        }
        AiRuntime.WriteEntity(initData + targetOffset, target);
        AiRuntime.WriteEntity(initData + interactOffset, Entity.Null);

        var ev = em.CreateEntity(eventType);
        byte* evData = (byte*)em.GetComponentDataRawRW(ev, eventType.TypeIndex);
        if (evData == null)
        {
            em.DestroyEntity(ev);
            em.DestroyEntity(init);
            throw new InvalidOperationException("OpenMenuEvent could not be written");
        }
        // Menu is a byte enum followed by padding up to InitDataEntity (layout.txt, "Build menu open/close").
        *(int*)(evData + Layout.OpenMenuEventMenuOffset) = menu;
        AiRuntime.WriteEntity(evData + Layout.OpenMenuEventInitDataEntityOffset, init);
        Plugin.Logger?.LogInfo("ContainerPreview: window " + menu + " open requested for " + target.Index + ":" + target.Version
            + " (OpenMenuEvent " + ev.Index + ":" + ev.Version + ", init " + init.Index + ":" + init.Version + ").");
    }

    private static (ComponentType Type, int Target, int Interact, int Menu) InitFor(int menuType) => menuType switch
    {
        ContainerPreviewMenus.WorkstationMenu => (WorkstationInit(), Layout.WorkstationMenuInitDataTargetOffset,
            Layout.WorkstationMenuInitDataInteractBuffOffset, Layout.HUDMenuTypeWorkstationMenu),
        ContainerPreviewMenus.RefinementstationMenu => (RefinementInit(), Layout.RefinementstationMenuInitDataTargetOffset,
            Layout.RefinementstationMenuInitDataInteractBuffOffset, Layout.HUDMenuTypeRefinementstationMenu),
        ContainerPreviewMenus.SalvagestationMenu => (SalvageInit(), Layout.SalvagestationMenuInitDataTargetOffset,
            Layout.SalvagestationMenuInitDataInteractBuffOffset, Layout.HUDMenuTypeSalvagestationMenu),
        _ => (ComponentIndex.Of<ContainerMenu.InitData>(ComponentType.AccessMode.ReadWrite), Layout.ContainerMenuInitDataTargetContainerOffset,
            Layout.ContainerMenuInitDataInteractBuffOffset, Layout.HUDMenuTypeContainerMenu),
    };

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ComponentType WorkstationInit() => ComponentIndex.Of<WorkstationMenu.InitData>(ComponentType.AccessMode.ReadWrite);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ComponentType RefinementInit() => ComponentIndex.Of<RefinementstationMenu.InitData>(ComponentType.AccessMode.ReadWrite);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ComponentType SalvageInit() => ComponentIndex.Of<SalvagestationMenu.InitData>(ComponentType.AccessMode.ReadWrite);

    /// <summary>
    /// The N3 guard on window <paramref name="menuType"/>'s mapper: skip it while the target or one of its inventories is
    /// away. A station window's sub menu mapper (<c>WorkstationSubMenuMapper</c> and its two siblings, the systems that
    /// read the station's recipes, queue and inventories) gets the same guard; the container window keeps its one guard,
    /// as qualified live. One handle disposes both.
    /// </summary>
    internal static IDisposable HookMapper(int menuType, Action<SystemHookContext> hook) => menuType switch
    {
        ContainerPreviewMenus.WorkstationMenu => new Both(WorkstationHook(hook), WorkstationSubHook(hook)),
        ContainerPreviewMenus.RefinementstationMenu => new Both(RefinementHook(hook), RefinementSubHook(hook)),
        ContainerPreviewMenus.SalvagestationMenu => new Both(SalvageHook(hook), SalvageSubHook(hook)),
        _ => SystemHooks.Before<ContainerMenuMapper>(hook),
    };

    private sealed class Both : IDisposable
    {
        private IDisposable? _a, _b;
        internal Both(IDisposable a, IDisposable b) { _a = a; _b = b; }

        public void Dispose()
        {
            var a = _a; _a = null;
            var b = _b; _b = null;
            try { a?.Dispose(); } finally { b?.Dispose(); }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IDisposable WorkstationHook(Action<SystemHookContext> hook) => SystemHooks.Before<WorkstationMenuMapper>(hook);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IDisposable RefinementHook(Action<SystemHookContext> hook) => SystemHooks.Before<RefinementstationMenuMapper>(hook);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IDisposable SalvageHook(Action<SystemHookContext> hook) => SystemHooks.Before<SalvagestationMenuMapper>(hook);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IDisposable WorkstationSubHook(Action<SystemHookContext> hook) => SystemHooks.Before<WorkstationSubMenuMapper>(hook);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IDisposable RefinementSubHook(Action<SystemHookContext> hook) => SystemHooks.Before<RefinementstationSubMenuMapper>(hook);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IDisposable SalvageSubHook(Action<SystemHookContext> hook) => SystemHooks.Before<SalvagestationSubMenuMapper>(hook);
}
#endif
