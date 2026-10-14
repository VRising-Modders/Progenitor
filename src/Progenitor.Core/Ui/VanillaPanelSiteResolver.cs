#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Progenitor.Ecs;
using Progenitor.Hooks;
using ProjectM.Network;
using ProjectM.UI;
using TMPro;
using Unity.Entities;
using UnityEngine;
using UnityEngine.UI;

namespace Progenitor.Ui;

/// <summary>
/// The Unity half of the P6 panel sites (<see cref="VanillaUiSite.ContainerPanel"/> and the four
/// after it): how each window is found, what entity it shows, where a container goes for each
/// <see cref="VanillaPanelAnchor"/>, and how a button and a row are cloned. Every decision that can
/// be made without Unity is made in <see cref="VanillaPanelPlacement"/>,
/// <see cref="VanillaPanelTracker"/> and <see cref="VanillaAttachPlan"/>; this file reads, builds
/// and reports.
///
/// <para><b>The seam.</b> <c>SystemHooks.After&lt;XMenuMapper&gt;</c> with
/// <c>HookOptions.OnlyWhenSystemRuns = false</c>. Every one of these mappers carries
/// <c>[RequireMatchingQueriesForUpdate]</c>, so with the default option the hook would fire only on
/// frames the mapper's own queries match: a site that reads a second mapper (the station site reads
/// the refinement mapper too; the inventory sites read all five) would miss opens, and a close would
/// never be seen, leaving an edge column on screen. With the option off the hook fires every frame
/// the client world updates its UI group, and the site reads every mapper it needs from one shared
/// per-frame scan (<see cref="EnsureScan"/>). Never Harmony on a mapper, never a mapper's Burst
/// <c>LambdaJob_*</c>, never a method of the window (<c>FakeTooltip.SetTooltip</c> takes a dozen
/// <c>Nullable</c> structs by value and is not a seam).</para>
///
/// <para><b>The address.</b> Each mapper keeps the window it drives in a private field
/// (<c>_ContainerSubMenu</c>, <c>_WorkstationSubMenu</c>, <c>_RefinementStationSubMenu</c>,
/// <c>_CastleHeartSubMenu</c>, <c>_InventorySubMenu</c>) and the entity it was opened on in
/// <c>_Target</c>; the interop exposes both as properties. Open is "the submenu's GameObject is
/// active in the hierarchy" (a <c>HUDMenu</c> is shown and hidden by <c>OnEnable</c> /
/// <c>OnDisable</c>) and, for a window with a target, "the target is not <c>Entity.Null</c>".</para>
///
/// <para><b>Absent versus degraded.</b> Only an exception from the resolver (a member the game
/// removed) marks a site absent for the generation. A missing donor or anchor group is logged once
/// and withholds only what needs it, for that frame or that handle.</para>
///
/// <para><b>What is not known offline, and is logged instead.</b> The prefab hierarchy under each
/// window, which layout groups govern it and the rects are serialized data the game's metadata does
/// not carry. The first time each site resolves, one line names the panel, its parent, the grid,
/// the donors and the inline group with their rects.</para>
/// </summary>
internal sealed partial class UnityUiRuntime
{
    private const int NetworkIdBytes = 12;

    /// <summary>Spacing inside a captioned button between its frame and its caption, in canvas units.</summary>
    private const float CaptionPadding = 14f;

    /// <summary>Smallest caption size; below it the caption ends in an ellipsis rather than overflowing.</summary>
    private const float CaptionMinSize = 10f;

    /// <summary>One view per site, reused every frame (no per-frame allocation).</summary>
    private static readonly Dictionary<VanillaUiSite, PanelView> PanelViews = new();

    private static readonly HashSet<VanillaUiSite> PanelHierarchyLogged = new();

    /// <summary>Reused by the fit; the fit runs on the main thread only.</summary>
    private static readonly List<UiObstacle> FitScratch = new();

    /// <summary>Everything the builders need from one resolved window. Mutated in place each frame.</summary>
    private sealed class PanelView
    {
        internal bool Live;
        internal GameObject? Panel;
        internal int PanelId;
        internal RectTransform? Grid;
        internal InventorySubMenu? Inventory;
        internal GameObject? Obstacle;
        internal Transform? RenameGroup;
        internal GameObject? RenameButton;
        internal TextMeshProUGUI? CaptionDonor;
        internal TextMeshProUGUI? BodyDonor;
        internal FakeTooltip? Tooltip;
        internal RectTransform? RootCanvas;
        internal int RootCanvasFor;

        internal void Reset()
        {
            Live = false;
            Panel = null;
            PanelId = 0;
            Grid = null;
            Inventory = null;
            Obstacle = null;
            RenameGroup = null;
            RenameButton = null;
            CaptionDonor = null;
            BodyDonor = null;
            Tooltip = null;
        }
    }

    // ------------------------------------------------------------------ the shared per-frame scan

    /// <summary>One mapper's window this frame.</summary>
    private struct WindowScan
    {
        internal Component? Sub;
        internal bool SubShown;
        internal InventorySubMenu? Inventory;
        internal bool InventoryShown;
        internal Entity Target;
    }

    private static int _scanFrame = -1;
    private static World? _scanWorld;
    private static ContainerMenuMapper? _containerMapper;
    private static WorkstationMenuMapper? _workstationMapper;
    private static RefinementstationMenuMapper? _refinementMapper;
    private static CastleHeartMenuMapper? _heartMapper;
    private static InventoryMenuMapper? _inventoryMapper;
    private static WindowScan _container, _workstation, _refinement, _heart, _character;

    /// <summary>
    /// Read every mapper once per frame, however many sites and handles ask. Mapper references are
    /// re-fetched only when the world changes.
    /// </summary>
    private static void EnsureScan(World world)
    {
        int frame = Time.frameCount;
        if (frame == _scanFrame && ReferenceEquals(world, _scanWorld)) return;
        if (!ReferenceEquals(world, _scanWorld))
        {
            _scanWorld = world;
            _containerMapper = Mapper<ContainerMenuMapper>(world);
            _workstationMapper = Mapper<WorkstationMenuMapper>(world);
            _refinementMapper = Mapper<RefinementstationMenuMapper>(world);
            _heartMapper = Mapper<CastleHeartMenuMapper>(world);
            _inventoryMapper = Mapper<InventoryMenuMapper>(world);
        }

        _scanFrame = frame;
        _container = default;
        _workstation = default;
        _refinement = default;
        _heart = default;
        _character = default;

        if (_containerMapper != null)
            _container = Window(_containerMapper._ContainerSubMenu, _containerMapper._InventorySubMenu, _containerMapper._Target);
        if (_workstationMapper != null)
            _workstation = Window(_workstationMapper._WorkstationSubMenu, _workstationMapper._InventorySubMenu, _workstationMapper._Target);
        if (_refinementMapper != null)
            _refinement = Window(_refinementMapper._RefinementStationSubMenu, _refinementMapper._InventorySubMenu, _refinementMapper._Target);
        if (_heartMapper != null)
            _heart = Window(_heartMapper._CastleHeartSubMenu, _heartMapper._InventorySubMenu, _heartMapper._Target);
        if (_inventoryMapper != null)
            _character = Window(null, _inventoryMapper._InventorySubMenu, Entity.Null);
    }

    private static WindowScan Window(Component? sub, InventorySubMenu? inv, Entity target) => new()
    {
        Sub = sub,
        SubShown = Shown(sub),
        Inventory = inv,
        InventoryShown = Shown(inv),
        Target = target,
    };

    internal static IDisposable? WatchPanelSite(VanillaUiSite site, Action<VanillaSiteSample> onFrame)
    {
        if (!PanelViews.ContainsKey(site)) PanelViews[site] = new PanelView();
        Action<SystemHookContext> frame = ctx =>
        {
            VanillaSiteSample sample;
            if (VanillaSiteHealth.IsAbsent(site))
            {
                sample = VanillaSiteSample.Absent;
            }
            else
            {
                try { sample = SamplePanel(site, ctx?.World); }
                catch (Exception e)
                {
                    // The resolver itself failed: a member the game removed or renamed. One line,
                    // then the site stays absent for this generation (no per-frame retry).
                    if (PanelViews.TryGetValue(site, out var v)) v.Reset();
                    if (VanillaSiteHealth.MarkAbsent(site, e.GetType().Name))
                        Plugin.Logger?.LogWarning(VanillaSiteHealth.Line(site, e.GetType().Name + ": " + e.Message));
                    sample = VanillaSiteSample.Absent;
                }
            }

            onFrame(sample);
        };

        // Any one mapper of the UI group will do as the frame source, because the hook fires on
        // every Update call; the site's own mapper is used for readability of the pin line.
        return site switch
        {
            VanillaUiSite.ContainerPanel => SystemHooks.After<ContainerMenuMapper>(frame, VanillaUiSites.WatchOptions(site)),
            VanillaUiSite.StationPanel => SystemHooks.After<WorkstationMenuMapper>(frame, VanillaUiSites.WatchOptions(site)),
            VanillaUiSite.CastleHeartPanel => SystemHooks.After<CastleHeartMenuMapper>(frame, VanillaUiSites.WatchOptions(site)),
            VanillaUiSite.InventoryPanel => SystemHooks.After<InventoryMenuMapper>(frame, VanillaUiSites.WatchOptions(site)),
            VanillaUiSite.InventoryTooltip => SystemHooks.After<InventoryMenuMapper>(frame, VanillaUiSites.WatchOptions(site)),
            _ => null,
        };
    }

    /// <summary>Core Uninstall: drop every reference this half holds.</summary>
    internal static void ClearPanelViews()
    {
        PanelViews.Clear();
        PanelHierarchyLogged.Clear();
        GridCaches.Clear();
        _hovered = null;
        _scanFrame = -1;
        _scanWorld = null;
        _containerMapper = null;
        _workstationMapper = null;
        _refinementMapper = null;
        _heartMapper = null;
        _inventoryMapper = null;
        _container = _workstation = _refinement = _heart = _character = default;
        FitScratch.Clear();
    }

    // ------------------------------------------------------------------ resolve

    private static VanillaSiteSample SamplePanel(VanillaUiSite site, World? world)
    {
        if (!PanelViews.TryGetValue(site, out var view)) return VanillaSiteSample.Absent;
        if (world == null || !world.IsCreated) return Miss(view);
        EnsureScan(world);
        return site switch
        {
            VanillaUiSite.ContainerPanel => SampleContainer(view, world),
            VanillaUiSite.StationPanel => SampleStation(view, world),
            VanillaUiSite.CastleHeartPanel => SampleCastleHeart(view, world),
            VanillaUiSite.InventoryPanel => SampleInventory(view, world),
            VanillaUiSite.InventoryTooltip => SampleTooltip(view, world),
            _ => Miss(view),
        };
    }

    private static VanillaSiteSample Miss(PanelView view)
    {
        view.Reset();
        return VanillaSiteSample.Absent;
    }

    private static T? Mapper<T>(World world) where T : SystemBase
    {
        var m = world.GetExistingSystemManaged<T>();
        return m == null || m.Pointer == IntPtr.Zero ? null : m;
    }

    private static bool Shown(Component? c)
    {
        try { return Alive(c) && Alive(c!.gameObject) && c.gameObject.activeInHierarchy; }
        catch { return false; }
    }

    /// <summary>Fill the parts every panel view shares from its inventory half.</summary>
    private static void FromInventory(PanelView view, InventorySubMenu? inv)
    {
        view.Inventory = inv;
        view.CaptionDonor = CaptionDonorOf(inv);
        view.BodyDonor = Alive(inv) ? TmpOf(inv!.FakeTooltip != null ? inv.FakeTooltip.Description : null) : null;
    }

    private static VanillaSiteSample SampleContainer(PanelView view, World world)
    {
        if (!_container.SubShown || _container.Target == Entity.Null) return Miss(view);
        var sub = _container.Sub!.TryCast<ContainerSubMenu>();
        if (sub == null) return Miss(view);

        view.Reset();
        view.Panel = sub.gameObject;
        view.Grid = RectOf(sub.EquipmentItemsParent);
        view.RenameGroup = Alive(sub.OpenTagEditButtonParent) ? sub.OpenTagEditButtonParent.transform : null;
        view.RenameButton = Alive(sub.OpenTagEditButton) ? sub.OpenTagEditButton.gameObject : null;
        FromInventory(view, _container.Inventory);
        view.Obstacle = _container.InventoryShown ? _container.Inventory!.gameObject : null;
        return Finish(VanillaUiSite.ContainerPanel, view, world, VanillaPanelKind.Container, _container.Target);
    }

    private static VanillaSiteSample SampleStation(PanelView view, World world)
    {
        if (_workstation.SubShown && _workstation.Target != Entity.Null)
        {
            var sub = _workstation.Sub!.TryCast<WorkstationSubMenu>();
            if (sub != null)
            {
                view.Reset();
                view.Panel = sub.gameObject;
                view.Grid = RectOf(sub.RecipesParent);
                FromInventory(view, _workstation.Inventory);
                view.Obstacle = _workstation.InventoryShown ? _workstation.Inventory!.gameObject : null;
                return Finish(VanillaUiSite.StationPanel, view, world, VanillaPanelKind.Workstation, _workstation.Target);
            }
        }

        if (_refinement.SubShown && _refinement.Target != Entity.Null)
        {
            var sub = _refinement.Sub!.TryCast<RefinementstationSubMenu>();
            if (sub != null)
            {
                view.Reset();
                view.Panel = sub.gameObject;
                view.Grid = RectOf(sub.RecipesParent);
                FromInventory(view, _refinement.Inventory);
                view.Obstacle = _refinement.InventoryShown ? _refinement.Inventory!.gameObject : null;
                return Finish(VanillaUiSite.StationPanel, view, world, VanillaPanelKind.RefinementStation, _refinement.Target);
            }
        }

        return Miss(view);
    }

    private static VanillaSiteSample SampleCastleHeart(PanelView view, World world)
    {
        if (!_heart.SubShown || _heart.Target == Entity.Null) return Miss(view);
        view.Reset();
        view.Panel = _heart.Sub!.gameObject;
        FromInventory(view, _heart.Inventory);
        view.Obstacle = _heart.InventoryShown ? _heart.Inventory!.gameObject : null;
        return Finish(VanillaUiSite.CastleHeartPanel, view, world, VanillaPanelKind.CastleHeart, _heart.Target);
    }

    /// <summary>The inventory a window shows right now; the game shows one window at a time.</summary>
    private static bool ActiveInventory(out WindowScan window, out VanillaPanelKind kind)
    {
        if (_container.InventoryShown) { window = _container; kind = VanillaPanelKind.Container; return true; }
        if (_workstation.InventoryShown) { window = _workstation; kind = VanillaPanelKind.Workstation; return true; }
        if (_refinement.InventoryShown) { window = _refinement; kind = VanillaPanelKind.RefinementStation; return true; }
        if (_heart.InventoryShown) { window = _heart; kind = VanillaPanelKind.CastleHeart; return true; }
        if (_character.InventoryShown) { window = _character; kind = VanillaPanelKind.Character; return true; }
        window = default;
        kind = VanillaPanelKind.None;
        return false;
    }

    private static VanillaSiteSample SampleInventory(PanelView view, World world)
    {
        if (!ActiveInventory(out var window, out var kind)) return Miss(view);
        view.Reset();
        view.Panel = window.Inventory!.gameObject;
        FromInventory(view, window.Inventory);
        return Finish(VanillaUiSite.InventoryPanel, view, world, kind, kind == VanillaPanelKind.Character ? Entity.Null : window.Target);
    }

    private static VanillaSiteSample SampleTooltip(PanelView view, World world)
    {
        if (!ActiveInventory(out var window, out var kind)) return Miss(view);
        var inv = window.Inventory!;

        FakeTooltip? tooltip = null;
        ItemGridSelectionEntry? hovered = null;
        bool inContainerHalf = false;
        if (Shown(inv.FakeTooltip))
        {
            hovered = HoveredIn(inv.EquipmentItemsParent) ?? HoveredIn(inv.ActionSlotsParent);
            if (hovered != null) tooltip = inv.FakeTooltip;
        }

        if (hovered == null && kind == VanillaPanelKind.Container && window.SubShown)
        {
            var sub = window.Sub!.TryCast<ContainerSubMenu>();
            if (sub != null && Shown(sub.FakeTooltip))
            {
                hovered = HoveredIn(sub.EquipmentItemsParent);
                if (hovered != null)
                {
                    tooltip = sub.FakeTooltip;
                    inContainerHalf = true;
                }
            }
        }

        if (hovered == null || tooltip == null) return Miss(view);

        int guid = 0;
        int amount = 0;
        try { guid = hovered.EntryId.GuidHash; } catch { /* */ }
        try { amount = hovered.Amount; } catch { /* */ }
        if (guid == 0) return Miss(view);

        view.Reset();
        view.Panel = tooltip.gameObject;
        view.Inventory = inv;
        view.Tooltip = tooltip;
        view.BodyDonor = TmpOf(tooltip.Description);

        // A slot in the player's own bag belongs to no container: the target names the window and
        // the item. A slot in the container half names that container too.
        return Finish(VanillaUiSite.InventoryTooltip, view, world, kind, inContainerHalf ? window.Target : Entity.Null, guid, amount);
    }

    // ---- hovered slot: cached per grid, rebuilt only when the grid's child count changes

    private sealed class GridCache
    {
        internal int ChildCount = -1;
        internal readonly List<ItemGridSelectionEntry> Entries = new();
    }

    private static readonly Dictionary<IntPtr, GridCache> GridCaches = new();
    private static ItemGridSelectionEntry? _hovered;

    private static ItemGridSelectionEntry? HoveredIn(Component? grid)
    {
        if (!Alive(grid)) return null;

        // The common frame: the slot hovered last frame is still hovered.
        try
        {
            if (Alive(_hovered) && _hovered!.IsHovered && _hovered.transform.parent != null
                && _hovered.transform.parent.Pointer == grid!.transform.Pointer)
                return _hovered;
        }
        catch { _hovered = null; }

        var t = grid!.transform;
        if (!GridCaches.TryGetValue(t.Pointer, out var cache))
        {
            if (GridCaches.Count > 32) GridCaches.Clear();
            cache = new GridCache();
            GridCaches[t.Pointer] = cache;
        }

        int n = t.childCount;
        if (cache.ChildCount != n)
        {
            cache.ChildCount = n;
            cache.Entries.Clear();
            for (int i = 0; i < n; i++)
            {
                var child = t.GetChild(i);
                if (child == null) continue;
                var entry = child.GetComponent<ItemGridSelectionEntry>();
                if (Alive(entry)) cache.Entries.Add(entry);
            }
        }

        for (int i = 0; i < cache.Entries.Count; i++)
        {
            var entry = cache.Entries[i];
            if (!Alive(entry))
            {
                cache.ChildCount = -1;
                continue;
            }

            if (entry.IsHovered && entry.gameObject.activeInHierarchy)
            {
                _hovered = entry;
                return entry;
            }
        }

        return null;
    }

    /// <summary>
    /// Common tail: read the target's <c>NetworkId</c>, check the donors (logging a missing one once),
    /// log the hierarchy once, and produce the sample.
    /// </summary>
    private static VanillaSiteSample Finish(
        VanillaUiSite site, PanelView view, World world, VanillaPanelKind kind, Entity target,
        int itemGuid = 0, int itemAmount = 0)
    {
        ReadNetworkId(world, target, out int n0, out int n1, out int n2);
        var panelTarget = new VanillaPanelTarget(kind, target.Index, target.Version, n0, n1, n2, itemGuid, itemAmount);

        // A missing donor withholds what needs it; it never makes the site absent. The tooltip line
        // needs the body text; a panel's buttons need the bar button and a caption style.
        bool donor;
        if (site == VanillaUiSite.InventoryTooltip)
        {
            donor = Alive(view.BodyDonor) && Alive(view.Tooltip!.DescriptionFrame);
            if (!donor) Degraded(site, Alive(view.BodyDonor) ? "FakeTooltip.DescriptionFrame" : "FakeTooltip.Description");
        }
        else
        {
            donor = Alive(view.Inventory) && Alive(view.Inventory!.SelfSortButton) && Alive(view.CaptionDonor);
            if (!donor) Degraded(site, !Alive(view.Inventory) || !Alive(view.Inventory!.SelfSortButton) ? "InventorySubMenu.SelfSortButton" : "InventorySubMenu.TabButtons caption");
            if (!Alive(view.BodyDonor)) Degraded(site, "body text");
        }

        view.Live = true;
        int id = view.Panel!.GetInstanceID();
        view.PanelId = id;
        var parent = view.Panel.transform != null ? view.Panel.transform.parent : null;
        LogPanelHierarchyOnce(site, view, parent);

        return new VanillaSiteSample(
            resolved: true,
            instanceId: id,
            donorPresent: donor,
            parentLayoutGroup: false,
            parentName: parent != null ? SafeObjectName(parent.gameObject) : "",
            parentSizeFitter: false,
            target: panelTarget);
    }

    private static void Degraded(VanillaUiSite site, string what)
    {
        if (VanillaSiteHealth.LogOnce(site, what))
            Plugin.Logger?.LogWarning(VanillaSiteHealth.Line(site, what));
    }

    /// <summary>
    /// The target's <c>NetworkId</c> as three words with the padding cleared: word 0 is
    /// <c>Normal_Index</c>, word 1 <c>Normal_Generation</c> (a static-map id keeps its own second
    /// word), word 2 the type byte. Refused when the interop's size is not the 12 bytes read.
    /// </summary>
    private static unsafe void ReadNetworkId(World world, Entity entity, out int a, out int b, out int c)
    {
        a = b = c = 0;
        if (entity == Entity.Null) return;
        try
        {
            if (Marshal.SizeOf<NetworkId>() != NetworkIdBytes) return;
            var em = world.EntityManager;
            if (!em.Exists(entity)) return;
            var type = ComponentIndex.Of<NetworkId>(ComponentType.AccessMode.ReadOnly);
            if (!em.HasComponent(entity, type)) return;
            int* net = (int*)em.GetComponentDataRawRO(entity, type.TypeIndex);
            if (net == null) return;
            c = net[2] & 0xFF;
            a = net[0];
            b = c == 1 ? net[1] & 0xFF : net[1];
        }
        catch { /* a target without a readable id is still a target */ }
    }

    private static RectTransform? RectOf(Component? c)
    {
        try { return Alive(c) ? c!.GetComponent<RectTransform>() : null; }
        catch { return null; }
    }

    private static TextMeshProUGUI? TmpOf(Component? c)
    {
        try
        {
            if (!Alive(c)) return null;
            var tmp = c!.GetComponent<TextMeshProUGUI>();
            return Alive(tmp) ? tmp : c.GetComponentInChildren<TextMeshProUGUI>(true);
        }
        catch { return null; }
    }

    /// <summary>
    /// The caption style of the game's own text buttons: the visible label of the inventory's first
    /// tab button (Equipment, Crafting, ...), never the window title.
    /// </summary>
    private static TextMeshProUGUI? CaptionDonorOf(InventorySubMenu? inv)
    {
        try
        {
            if (!Alive(inv)) return null;
            var tabs = inv!.TabButtons;
            if (tabs == null) return null;
            for (int i = 0; i < tabs.Length; i++)
            {
                var b = tabs[i];
                if (!Alive(b)) continue;
                foreach (var tmp in b.GetComponentsInChildren<TextMeshProUGUI>(true))
                {
                    if (tmp != null && tmp.enabled && tmp.gameObject.activeSelf) return tmp;
                }
            }
        }
        catch { /* */ }
        return null;
    }

    private static string SafeObjectName(GameObject? go)
    {
        try { return Alive(go) ? (go!.name ?? "") : ""; }
        catch { return ""; }
    }

    // ------------------------------------------------------------------ geometry

    /// <summary>
    /// <paramref name="target"/>'s rect in <paramref name="space"/>'s local units: two corners through
    /// <c>TransformPoint</c> / <c>InverseTransformPoint</c>. Not <c>GetWorldCorners</c>: its array
    /// argument comes back as zeros through this interop unless a retained IL2CPP array is passed.
    /// </summary>
    private static UiRect RectIn(Transform? space, RectTransform? target)
    {
        try
        {
            if (space == null || !Alive(target)) return default;
            Rect local = target!.rect;
            Vector3 worldMin = target.TransformPoint(new Vector3(local.xMin, local.yMin, 0f));
            Vector3 worldMax = target.TransformPoint(new Vector3(local.xMax, local.yMax, 0f));
            Vector3 a = space.InverseTransformPoint(worldMin);
            Vector3 b = space.InverseTransformPoint(worldMax);
            return UiRect.FromMinMax(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y), Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));
        }
        catch { return default; }
    }

    private static RectTransform? Rt(GameObject? go)
    {
        try { return Alive(go) ? go!.GetComponent<RectTransform>() : null; }
        catch { return null; }
    }

    private static float GroupSpacing(Transform? t, float fallback)
    {
        try
        {
            if (t == null) return fallback;
            var g = t.GetComponent<HorizontalOrVerticalLayoutGroup>();
            if (g != null) return g.spacing;
            var grid = t.GetComponent<GridLayoutGroup>();
            if (grid != null) return grid.spacing.y;
        }
        catch { /* */ }
        return fallback;
    }

    private static Vector2 DonorButtonSize(PanelView view)
    {
        try
        {
            var rt = Rt(view.Inventory != null && Alive(view.Inventory.SelfSortButton) ? view.Inventory.SelfSortButton.gameObject : null);
            if (rt != null)
            {
                var r = rt.rect;
                if (r.width > 1f && r.height > 1f) return new Vector2(r.width, r.height);
            }
        }
        catch { /* */ }
        return new Vector2(110f, 34f);
    }

    private static float RowHeightOf(PanelView view)
    {
        try
        {
            if (Alive(view.BodyDonor)) return Mathf.Max(18f, view.BodyDonor!.fontSize * 1.35f);
        }
        catch { /* */ }
        return VanillaPanelPlacement.DefaultRowHeight;
    }

    private static RectTransform? RootCanvasOf(PanelView view)
    {
        if (view.RootCanvasFor == view.PanelId && Alive(view.RootCanvas)) return view.RootCanvas;
        view.RootCanvas = null;
        view.RootCanvasFor = view.PanelId;
        try
        {
            var canvas = view.Panel != null ? view.Panel.GetComponentInParent<Canvas>() : null;
            var root = canvas != null ? (canvas.rootCanvas != null ? canvas.rootCanvas : canvas) : null;
            view.RootCanvas = root != null ? root.GetComponent<RectTransform>() : null;
        }
        catch { /* */ }
        return view.RootCanvas;
    }

    // ------------------------------------------------------------------ build

    public IRuntimeWidget? BuildPanelContainer(VanillaUiSite site, VanillaPanelAnchor anchor, string containerName)
    {
        if (!PanelViews.TryGetValue(site, out var view) || view == null || !view.Live || !Alive(view.Panel)) return null;
        GameObject? go = null;
        try
        {
            Transform? host;
            int siblingIndex = -1;
            switch (anchor)
            {
                case VanillaPanelAnchor.LeftEdge:
                case VanillaPanelAnchor.RightEdge:
                    host = view.Panel!.transform.parent;
                    break;
                case VanillaPanelAnchor.BesideRename:
                    host = Alive(view.RenameGroup) ? view.RenameGroup : null;
                    if (host != null && Alive(view.RenameButton)) siblingIndex = view.RenameButton!.transform.GetSiblingIndex() + 1;
                    break;
                case VanillaPanelAnchor.BesideSort:
                    var sort = view.Inventory != null ? view.Inventory.SelfSortButton : null;
                    host = Alive(sort) ? sort!.transform.parent : null;
                    if (host != null) siblingIndex = sort!.transform.GetSiblingIndex() + 1;
                    break;
                case VanillaPanelAnchor.TooltipBody:
                    var frame = view.Tooltip != null ? view.Tooltip.DescriptionFrame : null;
                    host = Alive(frame) ? frame!.transform.parent : null;
                    break;
                default:
                    host = null;
                    break;
            }

            if (host == null)
            {
                // This anchor's group is missing; only this handle goes without a container (it
                // says so once itself). The site and every other anchor keep working.
                Degraded(site, "anchor " + VanillaUiSites.AnchorKey(anchor));
                return null;
            }

            go = new GameObject(containerName);
            var rt = go.AddComponent<RectTransform>();
            rt.SetParent(host, worldPositionStays: false);
            if (siblingIndex >= 0) rt.SetSiblingIndex(Mathf.Min(siblingIndex, host.childCount - 1));
            else rt.SetAsLastSibling();
            rt.localScale = Vector3.one;

            var size = DonorButtonSize(view);
            float tooltipWidth = 0f;
            if (VanillaUiSites.IsEdge(anchor))
            {
                // Placed by us, ignored by whatever lays the window's halves out. Anchored at the
                // parent's pivot so anchoredPosition is the parent's local origin, which is the
                // space VanillaPanelPlacement computes in.
                var element = go.AddComponent<LayoutElement>();
                element.ignoreLayout = true;
                var hostRt = host.TryCast<RectTransform>();
                var pivot = hostRt != null ? hostRt.pivot : new Vector2(0.5f, 0.5f);
                rt.anchorMin = pivot;
                rt.anchorMax = pivot;
                rt.pivot = Vector2.zero;
                rt.sizeDelta = new Vector2(size.x, 0f);

                var column = go.AddComponent<VerticalLayoutGroup>();
                column.childAlignment = TextAnchor.UpperLeft;
                column.childControlWidth = true;
                column.childControlHeight = true;
                column.childForceExpandWidth = true;
                column.childForceExpandHeight = false;
                column.spacing = GroupSpacing(view.Inventory != null && Alive(view.Inventory.BottomBarButtonsParent) ? view.Inventory.BottomBarButtonsParent.transform : null, VanillaPanelPlacement.DefaultSpacing);
            }
            else if (anchor == VanillaPanelAnchor.TooltipBody)
            {
                var column = go.AddComponent<VerticalLayoutGroup>();
                column.childAlignment = TextAnchor.UpperLeft;
                column.childControlWidth = true;
                column.childControlHeight = true;
                column.childForceExpandWidth = true;
                column.childForceExpandHeight = false;
                column.spacing = 0f;

                // The line never widens the tooltip: it wraps inside the description's width.
                try
                {
                    var descRt = view.BodyDonor != null ? view.BodyDonor.rectTransform : null;
                    if (descRt != null) tooltipWidth = descRt.rect.width;
                }
                catch { /* */ }
                var element = go.AddComponent<LayoutElement>();
                if (tooltipWidth > 1f) element.preferredWidth = tooltipWidth;
                element.flexibleWidth = 0f;
            }
            else
            {
                // Inline: laid out by the game's own group like one more of its buttons.
                var row = go.AddComponent<HorizontalLayoutGroup>();
                row.childAlignment = TextAnchor.MiddleCenter;
                row.childControlWidth = true;
                row.childControlHeight = true;
                row.childForceExpandWidth = true;
                row.childForceExpandHeight = true;
                row.spacing = GroupSpacing(host, VanillaPanelPlacement.DefaultSpacing);
                var element = go.AddComponent<LayoutElement>();
                element.preferredWidth = 0f;
                element.preferredHeight = size.y;
                element.minHeight = size.y;
                CopyFlexible(element, anchor == VanillaPanelAnchor.BesideSort
                    ? (view.Inventory != null && Alive(view.Inventory.SelfSortButton) ? view.Inventory.SelfSortButton.gameObject : null)
                    : view.RenameButton);
            }

            bool hostLaid = false;
            try { hostLaid = host.GetComponent<VerticalLayoutGroup>() != null; } catch { /* */ }
            return new PanelContainerWidget(go, site, anchor, hostLaid);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: attach " + VanillaUiSites.Key(site) + " container build threw: " + e.Message);
            TryDestroy(go, immediate: true);
            return null;
        }
    }

    /// <summary>Give our inline container the donor button's flexible width, so the vanilla group treats it like a button.</summary>
    private static void CopyFlexible(LayoutElement ours, GameObject? donor)
    {
        try
        {
            if (!Alive(donor)) return;
            var theirs = donor!.GetComponent<LayoutElement>();
            if (theirs == null) return;
            ours.flexibleWidth = theirs.flexibleWidth;
            ours.flexibleHeight = theirs.flexibleHeight;
        }
        catch { /* */ }
    }

    public IRuntimeWidget? ClonePanelButton(VanillaUiSite site, IRuntimeWidget container, string caption)
    {
        if (container is not PanelContainerWidget host || !host.Alive) return null;
        if (host.Anchor == VanillaPanelAnchor.TooltipBody) return null;
        if (!PanelViews.TryGetValue(site, out var view) || view == null || !view.Live) return null;
        var donor = view.Inventory != null ? view.Inventory.SelfSortButton : null;
        if (!Alive(donor) || !Alive(view.CaptionDonor)) return null;

        GameObject? clone = null;
        try
        {
            clone = CloneButton(donor!.gameObject, host.GameObject!);
            if (clone == null) return null;
            clone.name = host.Name + "-button";

            // Buttons stack before rows in a column; in an inline group there are only buttons.
            clone.transform.SetSiblingIndex(host.Buttons.Count);
            var size = DonorButtonSize(view);
            var rt = clone.GetComponent<RectTransform>();
            if (rt != null)
            {
                rt.anchorMin = new Vector2(0.5f, 0.5f);
                rt.anchorMax = new Vector2(0.5f, 0.5f);
                rt.pivot = new Vector2(0.5f, 0.5f);
                rt.sizeDelta = size;
                rt.localScale = Vector3.one;
            }

            // The icon (and any label the donor brought) goes before the caption is added, so the
            // caption is never one of the donor's own possibly hidden labels.
            HideButtonIcon(clone, rt);
            var label = AddCaption(clone, view.CaptionDonor!, caption, size);
            if (label == null || !ButtonOperable(clone))
            {
                TryDestroyImmediate(clone);
                return null;
            }

            var stun = clone.GetComponent<SimpleStunButton>();
            if (stun == null)
            {
                TryDestroyImmediate(clone);
                return null;
            }

            var widget = new PanelButtonWidget(clone, label, stun.onClick, size);
            var element = clone.GetComponent<LayoutElement>() ?? clone.AddComponent<LayoutElement>();
            element.ignoreLayout = false;
            element.preferredHeight = size.y;
            element.minHeight = size.y;
            widget.Element = element;
            widget.Refit();

            clone.SetActive(true);
            host.Buttons.Add(widget);
            return widget;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: attach " + VanillaUiSites.Key(site) + " button clone threw: " + e.Message);
            TryDestroyImmediate(clone);
            return null;
        }
    }

    /// <summary>
    /// The donor is an icon button. Keep the frame: the button's target graphic and any graphic
    /// that covers (nearly) the whole button, which is its art for each state. Everything smaller is
    /// the icon, and goes, even when a state transition drives it: its object is deactivated, so a
    /// transition that only recolors it has nothing to show. The donor's labels go too.
    /// </summary>
    private static void HideButtonIcon(GameObject clone, RectTransform? buttonRt)
    {
        try
        {
            var stun = clone.GetComponent<SimpleStunButton>();
            IntPtr target = stun != null && stun.targetGraphic != null ? stun.targetGraphic.Pointer : IntPtr.Zero;
            var whole = buttonRt != null ? buttonRt.rect : new Rect(0, 0, 0, 0);
            foreach (var g in clone.GetComponentsInChildren<Graphic>(true))
            {
                if (g == null || g.Pointer == target) continue;
                bool isText = g.TryCast<TMP_Text>() != null;
                if (!isText && whole.width > 1f && whole.height > 1f)
                {
                    var r = g.rectTransform.rect;
                    if (r.width >= whole.width * 0.8f && r.height >= whole.height * 0.8f) continue;
                }

                try
                {
                    if (g.gameObject.Pointer != clone.Pointer) g.gameObject.SetActive(false);
                    else g.enabled = false;
                }
                catch { /* */ }
            }
        }
        catch { /* an icon left on is a cosmetic miss, not a broken button */ }
    }

    /// <summary>
    /// The caption: a clone of the game's own button label (font, material, color, size), stretched
    /// over the button with the game's padding. The button is sized to fit it (see
    /// <see cref="PanelButtonWidget.Refit"/>); when a group squeezes it, it auto-sizes down to
    /// <see cref="CaptionMinSize"/> and then ends in an ellipsis, never past the frame.
    /// </summary>
    private static TextMeshProUGUI? AddCaption(GameObject clone, TextMeshProUGUI donor, string caption, Vector2 size)
    {
        var go = UnityEngine.Object.Instantiate(donor.gameObject);
        go.name = "caption";
        go.transform.SetParent(clone.transform, false);
        foreach (var localized in go.GetComponentsInChildren<LocalizedText>(true))
        {
            try { UnityEngine.Object.DestroyImmediate(localized); } catch { /* */ }
        }

        for (int i = go.transform.childCount - 1; i >= 0; i--)
        {
            try { UnityEngine.Object.DestroyImmediate(go.transform.GetChild(i).gameObject); } catch { /* */ }
        }

        foreach (var behaviour in go.GetComponents<MonoBehaviour>())
        {
            if (behaviour == null || behaviour.TryCast<TextMeshProUGUI>() != null) continue;
            try { behaviour.enabled = false; } catch { /* */ }
        }

        var tmp = go.GetComponent<TextMeshProUGUI>();
        if (tmp == null) return null;
        var rt = tmp.rectTransform;
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.offsetMin = new Vector2(CaptionPadding, 0f);
        rt.offsetMax = new Vector2(-CaptionPadding, 0f);
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.enableWordWrapping = false;
        tmp.overflowMode = TextOverflowModes.Ellipsis;
        float baseSize = Mathf.Min(donor.fontSize, size.y * 0.6f);
        tmp.enableAutoSizing = true;
        tmp.fontSizeMax = Mathf.Max(CaptionMinSize, baseSize);
        tmp.fontSizeMin = CaptionMinSize;
        tmp.fontSize = tmp.fontSizeMax;
        tmp.raycastTarget = false;
        tmp.text = caption ?? "";
        tmp.enabled = true;
        go.SetActive(true);
        return tmp;
    }

    // ------------------------------------------------------------------ rows

    private static IRuntimeWidget? ClonePanelRow(VanillaUiSite site, IRuntimeWidget container, string text)
    {
        if (container is not PanelContainerWidget host || !host.Alive) return null;

        // An inline group is a row of buttons; a text row inside it would read as a broken button.
        if (host.Anchor == VanillaPanelAnchor.BesideRename || host.Anchor == VanillaPanelAnchor.BesideSort) return null;
        if (!PanelViews.TryGetValue(site, out var view) || view == null || !view.Live) return null;
        var donor = view.BodyDonor;
        if (!Alive(donor)) return null;

        GameObject? clone = null;
        try
        {
            // The game's body text (the tooltip description style): its font, size, color and
            // spacing, unchanged. A row wraps inside the column or the tooltip's width.
            clone = UnityEngine.Object.Instantiate(donor!.gameObject);
            clone.name = host.Name + "-row";
            clone.transform.SetParent(host.Transform, worldPositionStays: false);
            foreach (var localized in clone.GetComponentsInChildren<LocalizedText>(true))
            {
                try { UnityEngine.Object.DestroyImmediate(localized); } catch { /* */ }
            }

            var tmp = clone.GetComponent<TextMeshProUGUI>();
            if (tmp == null)
            {
                TryDestroyImmediate(clone);
                return null;
            }

            try { tmp.raycastTarget = false; } catch { /* */ }
            try { tmp.text = text ?? ""; } catch { /* */ }
            tmp.enableWordWrapping = true;
            tmp.alignment = host.Anchor == VanillaPanelAnchor.TooltipBody ? tmp.alignment : TextAlignmentOptions.Left;

            foreach (var behaviour in clone.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (behaviour == null || behaviour.TryCast<TextMeshProUGUI>() != null) continue;
                if (behaviour.TryCast<LayoutElement>() != null) continue;
                try { behaviour.enabled = false; } catch { /* */ }
            }

            clone.SetActive(true);
            return new PanelRowWidget(clone, tmp);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: attach " + VanillaUiSites.Key(site) + " row clone threw: " + e.Message);
            TryDestroyImmediate(clone);
            return null;
        }
    }

    // ------------------------------------------------------------------ fit

    public VanillaFitResult FitAttachContainer(
        VanillaUiSite site, VanillaPanelAnchor anchor, IRuntimeWidget container, int rows, int buttons)
    {
        if (!VanillaUiSites.IsPanel(site)) return VanillaFitResult.Unknown;
        if (container is not PanelContainerWidget host || !host.Alive) return new VanillaFitResult(VanillaFit.Blocked, "gone");
        if (!PanelViews.TryGetValue(site, out var view) || view == null || !view.Live || !Alive(view.Panel))
            return VanillaFitResult.Unknown;

        if (anchor == VanillaPanelAnchor.TooltipBody)
        {
            var verdict = VanillaPanelPlacement.CheckTooltip(host.HostLaidOut);
            if (verdict.Fit == VanillaFit.Fits) ClampTooltip(view);
            return verdict;
        }

        var panelRt = Rt(view.Panel);
        if (VanillaUiSites.IsEdge(anchor))
        {
            var space = view.Panel!.transform.parent;
            var panelRect = RectIn(space, panelRt);
            float? gridTop = null;
            if (view.Grid != null)
            {
                var g = RectIn(space, view.Grid);
                if (!g.IsEmpty) gridTop = g.YMax;
            }

            var size = DonorButtonSize(view);
            float width = Mathf.Max(size.x, host.WidestButton());
            float spacing = GroupSpacing(host.Transform, VanillaPanelPlacement.DefaultSpacing);
            var column = VanillaPanelPlacement.ColumnSize(buttons, rows < 0 ? 0 : rows, width, size.y, RowHeightOf(view), spacing);

            FitScratch.Clear();
            if (Alive(view.Obstacle) && view.Obstacle!.activeInHierarchy)
                FitScratch.Add(new UiObstacle(SafeObjectName(view.Obstacle), RectIn(space, Rt(view.Obstacle))));

            var bounds = RectIn(space, RootCanvasOf(view));
            var result = VanillaPanelPlacement.PlaceEdge(anchor, panelRect, gridTop, column.Width, column.Height, bounds, FitScratch);
            FitScratch.Clear();

            var rt = host.Rect;
            if (rt != null && !result.Rect.IsEmpty)
            {
                var pos = new Vector2(result.Rect.X, result.Rect.Y);
                var sz = new Vector2(result.Rect.Width, result.Rect.Height);
                if ((rt.anchoredPosition - pos).sqrMagnitude > 0.01f) rt.anchoredPosition = pos;
                if ((rt.sizeDelta - sz).sqrMagnitude > 0.01f) rt.sizeDelta = sz;
            }

            return result;
        }

        // Inline: tell the group how wide we are, make it lay out now, then check what it did.
        // Measuring before the group has laid us out reads a default rect and would hide the
        // buttons for good (review of P6, finding 2).
        var group = host.Transform != null ? host.Transform.parent : null;
        float groupSpacing = GroupSpacing(group, VanillaPanelPlacement.DefaultSpacing);
        try
        {
            var element = host.GameObject!.GetComponent<LayoutElement>();
            float want = host.InlineWidth(groupSpacing);
            if (element != null && Mathf.Abs(element.preferredWidth - want) > 0.5f) element.preferredWidth = want;
            var groupRt = group != null ? group.TryCast<RectTransform>() : null;
            if (groupRt != null) LayoutRebuilder.ForceRebuildLayoutImmediate(groupRt);
        }
        catch { /* */ }

        var panelSpace = view.Panel!.transform;
        var panelLocal = RectIn(panelSpace, panelRt);
        var ours = RectIn(panelSpace, host.Rect);
        if (ours.IsEmpty || panelLocal.IsEmpty) return new VanillaFitResult(VanillaFit.Unknown, "pending");

        FitScratch.Clear();
        if (group != null)
        {
            for (int i = 0; i < group.childCount; i++)
            {
                var child = group.GetChild(i);
                if (child == null || child.Pointer == host.Transform!.Pointer || !child.gameObject.activeInHierarchy) continue;
                FitScratch.Add(new UiObstacle(SafeObjectName(child.gameObject), RectIn(panelSpace, child.TryCast<RectTransform>())));
            }
        }

        var inline = VanillaPanelPlacement.CheckInline(ours, panelLocal, FitScratch);
        FitScratch.Clear();
        return inline;
    }

    /// <summary>
    /// The game placed the tooltip before our line existed. After the line grows it, keep it inside
    /// the tooltip's own clamp rectangle (<c>FakeTooltip.ClampWithinRect</c>) by moving it up or
    /// left by the overflow. The game positions the tooltip again on its next show, so nothing has to
    /// be put back.
    /// </summary>
    private static void ClampTooltip(PanelView view)
    {
        try
        {
            var tooltip = view.Tooltip;
            if (!Alive(tooltip)) return;
            var rt = tooltip!.GetComponent<RectTransform>();
            var clamp = tooltip.ClampWithinRect;
            if (rt == null || !Alive(clamp) || rt.parent == null) return;
            LayoutRebuilder.ForceRebuildLayoutImmediate(rt);
            var space = rt.parent;
            var r = RectIn(space, rt);
            var c = RectIn(space, clamp);
            if (r.IsEmpty || c.IsEmpty) return;
            float dy = r.Y < c.Y ? c.Y - r.Y : 0f;
            float dx = r.XMax > c.XMax ? c.XMax - r.XMax : 0f;
            if (Mathf.Abs(dx) < 0.5f && Mathf.Abs(dy) < 0.5f) return;
            rt.anchoredPosition += new Vector2(dx, dy);
        }
        catch { /* a tooltip left where the game put it is a cosmetic miss */ }
    }

    // ------------------------------------------------------------------ diagnostics

    /// <summary>
    /// One line per site and process naming what is actually there: the hierarchy, the layout
    /// components and the rects are serialized data the offline research cannot read. This is the
    /// line the first live acceptance run reads.
    /// </summary>
    private static void LogPanelHierarchyOnce(VanillaUiSite site, PanelView view, Transform? parent)
    {
        if (!PanelHierarchyLogged.Add(site)) return;
        try
        {
            var space = parent;
            string Describe(string name, Component? c)
            {
                if (!Alive(c)) return " " + name + "=<none>";
                var rt = c!.GetComponent<RectTransform>();
                return " " + name + "=" + SafeObjectName(c.gameObject) + RectIn(space, rt)
                    + (c.GetComponent<LayoutGroup>() != null ? "[group]" : "");
            }

            Plugin.Logger?.LogInfo(
                "Ui: site " + VanillaUiSites.Key(site)
                + " panel=" + SafeObjectName(view.Panel) + RectIn(space, Rt(view.Panel))
                + " parent=" + (parent != null ? SafeObjectName(parent.gameObject) : "<none>")
                + " parentGroup=" + (parent != null && parent.GetComponent<LayoutGroup>() != null)
                + Describe("grid", view.Grid)
                + Describe("sort", view.Inventory != null ? view.Inventory.SelfSortButton : null)
                + Describe("bar", view.Inventory != null ? view.Inventory.BottomBarButtonsParent : null)
                + Describe("renameGroup", view.RenameGroup)
                + Describe("caption", view.CaptionDonor)
                + Describe("body", view.BodyDonor)
                + (view.Tooltip != null ? Describe("tooltipFrame", view.Tooltip.DescriptionFrame != null ? view.Tooltip.DescriptionFrame.transform : null) : "")
                + Describe("obstacle", view.Obstacle != null ? view.Obstacle.transform : null));
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: site " + VanillaUiSites.Key(site) + " hierarchy dump threw: " + e.Message);
        }
    }

    // ------------------------------------------------------------------ widgets

    /// <summary>A P6 container. Destroyed through the module's destroy helper with the verdict kept.</summary>
    private sealed class PanelContainerWidget : IRuntimeWidget, IReleaseVerdict
    {
        private GameObject? _go;
        private UiDestroyVerdict _verdict = UiDestroyVerdict.Failed;

        internal PanelContainerWidget(GameObject go, VanillaUiSite site, VanillaPanelAnchor anchor, bool hostLaidOut)
        {
            _go = go;
            Site = site;
            Anchor = anchor;
            HostLaidOut = hostLaidOut;
            Name = SafeObjectName(go);
        }

        internal VanillaUiSite Site { get; }

        internal VanillaPanelAnchor Anchor { get; }

        /// <summary>The vanilla parent has a vertical layout group (the tooltip rule).</summary>
        internal bool HostLaidOut { get; }

        internal string Name { get; }

        internal List<PanelButtonWidget> Buttons { get; } = new();

        internal GameObject? GameObject => UnityUiRuntime.Alive(_go) ? _go : null;

        internal Transform? Transform
        {
            get { try { return UnityUiRuntime.Alive(_go) ? _go!.transform : null; } catch { return null; } }
        }

        internal RectTransform? Rect
        {
            get { try { return UnityUiRuntime.Alive(_go) ? _go!.GetComponent<RectTransform>() : null; } catch { return null; } }
        }

        internal float WidestButton()
        {
            float w = 0f;
            for (int i = 0; i < Buttons.Count; i++)
                if (Buttons[i].Alive && Buttons[i].Visible) w = Mathf.Max(w, Buttons[i].PreferredWidth);
            return w;
        }

        internal float InlineWidth(float spacing)
        {
            float w = 0f;
            int n = 0;
            for (int i = 0; i < Buttons.Count; i++)
            {
                if (!Buttons[i].Alive || !Buttons[i].Visible) continue;
                w += Buttons[i].PreferredWidth;
                n++;
            }

            return n == 0 ? 0f : w + (n - 1) * Mathf.Max(0f, spacing);
        }

        public UiDestroyVerdict LastRelease => _verdict;

        public string Text { get => ""; set { } }

        public bool Visible
        {
            get { try { return UnityUiRuntime.Alive(_go) && _go!.activeSelf; } catch { return false; } }
            set { try { if (UnityUiRuntime.Alive(_go) && _go!.activeSelf != value) _go.SetActive(value); } catch { /* */ } }
        }

        public bool BoolValue { get => false; set { } }

        public float FloatValue { get => 0f; set { } }

        public string StringValue { get => ""; set { } }

        public bool Alive => UnityUiRuntime.Alive(_go);

        public object? ClickEvent => null;

        public object? FloatEvent => null;

        public object? BoolEvent => null;

        public object? TextEvent => null;

        public object? PointerEnterEvent => null;

        public object? PointerExitEvent => null;

        public void Release()
        {
            var go = _go;
            if (go == null) return;
            if (!UnityUiRuntime.Alive(go))
            {
                _go = null;
                _verdict = UiDestroyVerdict.Destroyed;
                return;
            }

            _verdict = DestroyWithVerdict(go, immediate: true);
            if (_verdict == UiDestroyVerdict.Destroyed)
            {
                _go = null;
                Buttons.Clear();
            }
        }
    }

    /// <summary>One cloned game button inside a P6 container, sized to its caption.</summary>
    private sealed class PanelButtonWidget : IRuntimeWidget, IReleaseVerdict
    {
        private GameObject? _go;
        private TextMeshProUGUI? _caption;
        private readonly object? _click;
        private readonly Vector2 _donorSize;
        private UiDestroyVerdict _verdict = UiDestroyVerdict.Failed;

        internal PanelButtonWidget(GameObject go, TextMeshProUGUI caption, object? click, Vector2 donorSize)
        {
            _go = go;
            _caption = caption;
            _click = click;
            _donorSize = donorSize;
            PreferredWidth = donorSize.x;
        }

        internal LayoutElement? Element { get; set; }

        /// <summary>Donor width, or the caption at full size plus the padding, whichever is wider.</summary>
        internal float PreferredWidth { get; private set; }

        internal void Refit()
        {
            try
            {
                if (_caption == null) return;
                var pref = _caption.GetPreferredValues(_caption.text ?? "", 9999f, _donorSize.y);
                PreferredWidth = Mathf.Max(_donorSize.x, pref.x + 2f * CaptionPadding);
                if (Element != null) Element.preferredWidth = PreferredWidth;
            }
            catch { PreferredWidth = _donorSize.x; }
        }

        public UiDestroyVerdict LastRelease => _verdict;

        public string Text
        {
            get { try { return Alive && _caption != null ? (_caption.text ?? "") : ""; } catch { return ""; } }
            set
            {
                try
                {
                    if (!Alive || _caption == null) return;
                    _caption.text = value ?? "";
                    Refit();
                }
                catch { /* */ }
            }
        }

        public bool Visible
        {
            get { try { return UnityUiRuntime.Alive(_go) && _go!.activeSelf; } catch { return false; } }
            set { try { if (UnityUiRuntime.Alive(_go) && _go!.activeSelf != value) _go.SetActive(value); } catch { /* */ } }
        }

        public bool BoolValue { get => false; set { } }

        public float FloatValue { get => 0f; set { } }

        public string StringValue { get => Text; set => Text = value; }

        public bool Alive => UnityUiRuntime.Alive(_go);

        public object? ClickEvent => Alive ? _click : null;

        public object? FloatEvent => null;

        public object? BoolEvent => null;

        public object? TextEvent => null;

        public object? PointerEnterEvent => null;

        public object? PointerExitEvent => null;

        public void Release()
        {
            var go = _go;
            if (go == null || !UnityUiRuntime.Alive(go))
            {
                _go = null;
                _caption = null;
                _verdict = UiDestroyVerdict.Destroyed;
                return;
            }

            _verdict = DestroyWithVerdict(go, immediate: true);
            if (_verdict != UiDestroyVerdict.Destroyed) return;
            _go = null;
            _caption = null;
        }
    }

    /// <summary>One cloned text row inside a P6 container (a column line or the tooltip line).</summary>
    private sealed class PanelRowWidget : IRuntimeWidget, IReleaseVerdict
    {
        private GameObject? _go;
        private TextMeshProUGUI? _tmp;
        private UiDestroyVerdict _verdict = UiDestroyVerdict.Failed;

        internal PanelRowWidget(GameObject go, TextMeshProUGUI tmp)
        {
            _go = go;
            _tmp = tmp;
        }

        public UiDestroyVerdict LastRelease => _verdict;

        public string Text
        {
            get { try { return Alive && _tmp != null ? (_tmp.text ?? "") : ""; } catch { return ""; } }
            set { try { if (Alive && _tmp != null) _tmp.text = value ?? ""; } catch { /* */ } }
        }

        public bool Visible
        {
            get { try { return UnityUiRuntime.Alive(_go) && _go!.activeSelf; } catch { return false; } }
            set { try { if (UnityUiRuntime.Alive(_go)) _go!.SetActive(value); } catch { /* */ } }
        }

        public bool BoolValue { get => false; set { } }

        public float FloatValue { get => 0f; set { } }

        public string StringValue { get => Text; set => Text = value; }

        public bool Alive => UnityUiRuntime.Alive(_go) && UnityUiRuntime.Alive(_tmp);

        public object? ClickEvent => null;

        public object? FloatEvent => null;

        public object? BoolEvent => null;

        public object? TextEvent => null;

        public object? PointerEnterEvent => null;

        public object? PointerExitEvent => null;

        public void Release()
        {
            var go = _go;
            if (go == null || !UnityUiRuntime.Alive(go))
            {
                _go = null;
                _tmp = null;
                _verdict = UiDestroyVerdict.Destroyed;
                return;
            }

            _verdict = DestroyWithVerdict(go, immediate: true);
            if (_verdict != UiDestroyVerdict.Destroyed) return;
            _go = null;
            _tmp = null;
        }
    }
}
#endif
