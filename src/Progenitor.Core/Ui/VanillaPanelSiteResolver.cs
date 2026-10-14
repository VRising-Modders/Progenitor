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
/// never be seen, leaving an edge column on screen. With the option off the hook fires whenever the
/// mapper updates, and the site reads every mapper it needs from one shared per-frame scan
/// (<see cref="EnsureScan"/>). The mappers do not all update every frame, so every site hooks all
/// five and has a frame watchdog for its close (<see cref="WatchPanelSite"/>). Never Harmony on a mapper, never a mapper's Burst
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
        internal string RenameSource = "";
        internal GameObject? RenameButton;
        internal TextMeshProUGUI? CaptionDonor;
        internal TextMeshProUGUI? BodyDonor;
        internal FakeTooltip? Tooltip;
        internal RectTransform? RootCanvas;
        internal int RootCanvasFor;

        /// <summary>The bar button every P6 button is cloned from (<see cref="VanillaPanelAnchors.PickButtonDonor"/>).</summary>
        internal GameObject? ButtonDonor;
        internal string DonorSource = "";

        /// <summary>The control <see cref="VanillaPanelAnchor.BesideSort"/> goes beside.</summary>
        internal GameObject? Sort;
        internal string SortSource = "";

        /// <summary>The inventory's bottom bar (<c>BottomBarButtonsParent</c>).</summary>
        internal Transform? Bar;

        internal void Reset()
        {
            Live = false;
            Panel = null;
            PanelId = 0;
            Grid = null;
            Inventory = null;
            Obstacle = null;
            RenameGroup = null;
            RenameSource = "";
            RenameButton = null;
            CaptionDonor = null;
            BodyDonor = null;
            Tooltip = null;
            ButtonDonor = null;
            DonorSource = "";
            Sort = null;
            SortSource = "";
            Bar = null;
        }
    }

    // ------------------------------------------------------------------ anchors on this build

    /// <summary>
    /// A <c>Transform</c> as <see cref="VanillaPanelAnchors"/> sees it. Built only when an
    /// inventory's donor and sort control are (re)resolved, never per frame.
    /// </summary>
    private sealed class TransformNode : IAnchorNode
    {
        internal TransformNode(Transform t) => Transform = t;

        internal Transform Transform { get; }

        internal static TransformNode? Of(Component? c)
        {
            try { return Alive(c) && c!.transform != null ? new TransformNode(c.transform) : null; }
            catch { return null; }
        }

        public string Name => SafeObjectName(Transform != null ? Transform.gameObject : null);

        public bool Active
        {
            get { try { return Alive(Transform) && Transform.gameObject.activeInHierarchy; } catch { return false; } }
        }

        public bool IsButton
        {
            get { try { return Alive(Transform) && Transform.GetComponent<SimpleStunButton>() != null; } catch { return false; } }
        }

        /// <summary>The clone gate's own test (<c>QualifiedButtonTargets</c>), so a donor that passes here clones.</summary>
        public bool CloneSafe
        {
            get { try { return Alive(Transform) && QualifiedButtonTargets(Transform.gameObject); } catch { return false; } }
        }

        public float Width => Size().x;

        public float Height => Size().y;

        public AnchorLayout Layout => LayoutOf(Transform);

        public IAnchorNode? Parent
        {
            get { try { return Alive(Transform) && Transform.parent != null ? new TransformNode(Transform.parent) : null; } catch { return null; } }
        }

        public int ChildCount
        {
            get { try { return Alive(Transform) ? Transform.childCount : 0; } catch { return 0; } }
        }

        public IAnchorNode? Child(int index)
        {
            try
            {
                var c = Alive(Transform) ? Transform.GetChild(index) : null;
                return c != null ? new TransformNode(c) : null;
            }
            catch { return null; }
        }

        private Vector2 Size()
        {
            try
            {
                var rt = Alive(Transform) ? Transform.TryCast<RectTransform>() : null;
                return rt != null ? rt.rect.size : Vector2.zero;
            }
            catch { return Vector2.zero; }
        }
    }

    private static AnchorLayout LayoutOf(Transform? t)
    {
        try
        {
            if (!Alive(t)) return AnchorLayout.None;
            var grid = t!.GetComponent<GridLayoutGroup>();
            if (grid != null && grid.enabled) return AnchorLayout.Grid;
            var h = t.GetComponent<HorizontalLayoutGroup>();
            if (h != null && h.enabled)
                return h.childControlWidth ? AnchorLayout.HorizontalControlled : AnchorLayout.HorizontalFree;
            var v = t.GetComponent<VerticalLayoutGroup>();
            if (v != null && v.enabled) return AnchorLayout.Vertical;
        }
        catch { /* */ }
        return AnchorLayout.None;
    }

    private static GameObject? GoOf(IAnchorNode? node)
    {
        try { return node is TransformNode tn && Alive(tn.Transform) ? tn.Transform.gameObject : null; }
        catch { return null; }
    }

    /// <summary>One inventory window's donor and sort control, re-resolved only when one of them dies.</summary>
    private sealed class InventoryAnchorCache
    {
        internal GameObject? Donor;
        internal string DonorSource = "";
        internal GameObject? Sort;
        internal string SortSource = "";
        internal int Frame;
    }

    private static readonly Dictionary<int, InventoryAnchorCache> InventoryAnchors = new();

    /// <summary>Each container window's rename group and the rule that found it.</summary>
    private static readonly Dictionary<int, (Transform? Group, string Source, int Frame)> RenameGroups = new();

    /// <summary>
    /// The last word on each site and anchor: <c>ok ...</c> naming the host, the mode and the donor,
    /// or <c>missing ...</c> naming what was not found. Read by <c>ui_read panels</c>.
    /// </summary>
    private static readonly Dictionary<(VanillaUiSite, VanillaPanelAnchor), string> AnchorStatus = new();

    private static InventoryAnchorCache AnchorsOf(InventorySubMenu inv)
    {
        int id = inv.GetInstanceID();
        if (InventoryAnchors.TryGetValue(id, out var cached) && !Stale(cached.Donor, cached.Frame) && !Stale(cached.Sort, cached.Frame))
            return cached;
        if (InventoryAnchors.Count > 8) InventoryAnchors.Clear();

        var bar = TransformNode.Of(inv.BottomBarButtonsParent);
        var named = new IAnchorNode?[]
        {
            TransformNode.Of(inv.SelfSortButton), TransformNode.Of(inv.AllSortButton), TransformNode.Of(inv.SmartMergeButton),
            TransformNode.Of(inv.SmartTakeButton), TransformNode.Of(inv.DropAllButton),
        };
        var donor = VanillaPanelAnchors.PickButtonDonor(named, bar);
        var sort = VanillaPanelAnchors.SortControl(named[0], named[1], bar);
        var entry = new InventoryAnchorCache
        {
            Donor = GoOf(donor.Node),
            DonorSource = donor.Source,
            Sort = GoOf(sort.Node),
            SortSource = sort.Source,
            Frame = Time.frameCount,
        };
        InventoryAnchors[id] = entry;
        return entry;
    }

    /// <summary>Frames between two lookups of a piece that was not found (a found one is re-read only when it dies).</summary>
    private const int MissingRetryFrames = 60;

    /// <summary>A cached piece needs looking up again: it died, or it was missing and the retry interval passed.</summary>
    private static bool Stale(UnityEngine.Object? found, int frame) =>
        found is null ? Time.frameCount - frame >= MissingRetryFrames : !Alive(found);

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

    /// <summary>
    /// One site's watch: when it last sampled, on which world, and whether that sample was open.
    /// The five mapper hooks and the frame watchdog share it, so a site samples once a frame.
    /// </summary>
    private sealed class SiteWatch
    {
        internal int Frame = -1;
        internal World? World;
        internal bool Open;
    }

    /// <summary>
    /// Frames a site may go without a mapper hook while it last read open before the watchdog
    /// samples it on its own. Two, so the ordinary one-frame gap between the frame tick and the UI
    /// group never triggers it.
    /// </summary>
    private const int WatchdogFrames = 2;

    /// <summary>
    /// Watch one panel site. Live 1.1.13.0 showed the window mappers do not all update on every
    /// frame: the inventory site, hooked on <c>InventoryMenuMapper</c> alone, never saw the bag
    /// beside an open chest (it first resolved when the character menu opened), and a window that
    /// closes together with the mapper that drives it may leave no frame in which its own mapper
    /// sees it closed. So every panel site hooks <b>all five</b> window mappers and samples on the
    /// first of them to run each frame; and while the site last read open, a frame watchdog
    /// (<c>Plugin.Frame</c>) samples it on the last world when no mapper has run for
    /// <see cref="WatchdogFrames"/> frames. A window that went inactive or was destroyed then reads
    /// closed on the next frame either way, and the tracker raises exactly one Closed per Opened.
    /// </summary>
    internal static IDisposable? WatchPanelSite(VanillaUiSite site, Action<VanillaSiteSample> onFrame)
    {
        if (!PanelViews.ContainsKey(site)) PanelViews[site] = new PanelView();
        var watch = new SiteWatch();

        void Sample(World? world)
        {
            int now = Time.frameCount;
            if (now == watch.Frame) return;
            watch.Frame = now;
            if (world != null) watch.World = world;

            VanillaSiteSample sample;
            if (VanillaSiteHealth.IsAbsent(site))
            {
                sample = VanillaSiteSample.Absent;
            }
            else
            {
                try { sample = SamplePanel(site, world); }
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

            watch.Open = sample.Resolved;
            onFrame(sample);
        }

        Action<SystemHookContext> frame = ctx => Sample(ctx?.World);
        Action watchdog = () =>
        {
            if (!watch.Open || Time.frameCount - watch.Frame < WatchdogFrames) return;
            var world = watch.World;
            Sample(world != null && world.IsCreated ? world : null);
        };

        var leases = new List<IDisposable>();
        try
        {
            // The site's own mapper first, for the pin line's readability; the rest because any one
            // of them may be the only one updating this frame.
            switch (site)
            {
                case VanillaUiSite.ContainerPanel:
                case VanillaUiSite.StationPanel:
                case VanillaUiSite.CastleHeartPanel:
                case VanillaUiSite.InventoryPanel:
                case VanillaUiSite.InventoryTooltip:
                    break;
                default:
                    return null;
            }

            Add(leases, SystemHooks.After<ContainerMenuMapper>(frame, VanillaUiSites.WatchOptions(site)));
            Add(leases, SystemHooks.After<WorkstationMenuMapper>(frame, VanillaUiSites.WatchOptions(site)));
            Add(leases, SystemHooks.After<RefinementstationMenuMapper>(frame, VanillaUiSites.WatchOptions(site)));
            Add(leases, SystemHooks.After<CastleHeartMenuMapper>(frame, VanillaUiSites.WatchOptions(site)));
            Add(leases, SystemHooks.After<InventoryMenuMapper>(frame, VanillaUiSites.WatchOptions(site)));
            Plugin.Frame += watchdog;
            leases.Add(new FrameLease(watchdog));
        }
        catch
        {
            foreach (var l in leases)
            {
                try { l.Dispose(); } catch { /* */ }
            }

            throw;
        }

        return new LeaseSet(leases);
    }

    private static void Add(List<IDisposable> leases, IDisposable? lease)
    {
        if (lease != null) leases.Add(lease);
    }

    private sealed class FrameLease : IDisposable
    {
        private Action? _tick;

        internal FrameLease(Action tick) => _tick = tick;

        public void Dispose()
        {
            var tick = _tick;
            _tick = null;
            if (tick != null) Plugin.Frame -= tick;
        }
    }

    /// <summary>One site watch: the five mapper hooks and the watchdog, released together.</summary>
    private sealed class LeaseSet : IDisposable
    {
        private List<IDisposable>? _leases;

        internal LeaseSet(List<IDisposable> leases) => _leases = leases;

        public void Dispose()
        {
            var leases = _leases;
            _leases = null;
            if (leases == null) return;
            foreach (var l in leases)
            {
                try { l.Dispose(); }
                catch (Exception e) { Plugin.Logger?.LogWarning("Ui: attach watch release threw: " + e.Message); }
            }
        }
    }

    /// <summary>Core Uninstall: drop every reference this half holds.</summary>
    internal static void ClearPanelViews()
    {
        PanelViews.Clear();
        PanelHierarchyLogged.Clear();
        GridCaches.Clear();
        InventoryAnchors.Clear();
        RenameGroups.Clear();
        ButtonFailures.Clear();
        AnchorStatusKey.Clear();
        PlacementSaid.Clear();
        AnchorStatus.Clear();
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
        if (!Alive(inv)) return;
        try { view.Bar = Alive(inv!.BottomBarButtonsParent) ? inv.BottomBarButtonsParent.transform : null; }
        catch { view.Bar = null; }
        var anchors = AnchorsOf(inv!);
        view.ButtonDonor = Alive(anchors.Donor) ? anchors.Donor : null;
        view.DonorSource = anchors.DonorSource;
        view.Sort = Alive(anchors.Sort) ? anchors.Sort : null;
        view.SortSource = anchors.SortSource;
    }

    private static VanillaSiteSample SampleContainer(PanelView view, World world)
    {
        if (!_container.SubShown || _container.Target == Entity.Null) return Miss(view);
        var sub = _container.Sub!.TryCast<ContainerSubMenu>();
        if (sub == null) return Miss(view);

        view.Reset();
        view.Panel = sub.gameObject;
        view.Grid = RectOf(sub.EquipmentItemsParent);
        view.RenameButton = Alive(sub.OpenTagEditButton) ? sub.OpenTagEditButton.gameObject : null;

        // OpenTagEditButtonParent is empty in the shipped prefab (1.1.13.0); the rename button's
        // own parent is the group then (VanillaPanelAnchors.RenameGroup), cached per window.
        int subId = sub.GetInstanceID();
        if (!RenameGroups.TryGetValue(subId, out var rename) || Stale(rename.Group, rename.Frame))
        {
            if (RenameGroups.Count > 8) RenameGroups.Clear();
            var pick = VanillaPanelAnchors.RenameGroup(
                TransformNode.Of(sub.OpenTagEditButtonParent), TransformNode.Of(sub.OpenTagEditButton));
            var group = GoOf(pick.Node);
            rename = (group != null ? group.transform : null, pick.Source, Time.frameCount);
            RenameGroups[subId] = rename;
        }

        view.RenameGroup = Alive(rename.Group) ? rename.Group : null;
        view.RenameSource = rename.Source;
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
            // The button donor is the first live bar button (VanillaPanelAnchors.PickButtonDonor):
            // SelfSortButton is empty on 1.1.13.0 and was, alone, why no P6 button ever appeared.
            donor = Alive(view.ButtonDonor) && Alive(view.CaptionDonor);
            if (!donor)
            {
                Degraded(site, !Alive(view.ButtonDonor)
                    ? "button donor: InventorySubMenu." + string.Join("/", VanillaPanelAnchors.ButtonDonorFields) + " and the BottomBarButtonsParent buttons are all missing"
                    : "button caption: InventorySubMenu.TabButtons has no visible label");
            }

            if (!Alive(view.BodyDonor)) Degraded(site, "row text: InventorySubMenu.FakeTooltip.Description");
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
    private static void ReadNetworkId(World world, Entity entity, out int a, out int b, out int c) =>
        ReadNetworkId(world.EntityManager, entity, out a, out b, out c);

    /// <summary>
    /// <see cref="ReadNetworkId(World, Entity, out int, out int, out int)"/> on an entity manager;
    /// also what the world pick reports in <see cref="UiWorldPickResult"/>. All zero when unreadable.
    /// </summary>
    internal static unsafe void ReadNetworkId(EntityManager em, Entity entity, out int a, out int b, out int c)
    {
        a = b = c = 0;
        if (entity == Entity.Null) return;
        try
        {
            if (Marshal.SizeOf<NetworkId>() != NetworkIdBytes) return;
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
            var rt = Rt(view.ButtonDonor);
            if (rt != null)
            {
                var r = rt.rect;
                if (r.width > 1f && r.height > 1f) return new Vector2(r.width, r.height);
            }
        }
        catch { /* */ }
        return new Vector2(116f, 36f);
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
            GameObject? reference = null;
            string missing = "";
            int siblingIndex = -1;
            switch (anchor)
            {
                case VanillaPanelAnchor.LeftEdge:
                case VanillaPanelAnchor.RightEdge:
                    host = view.Panel!.transform.parent;
                    if (host == null) missing = "the window's parent";
                    break;
                case VanillaPanelAnchor.BesideRename:
                    host = Alive(view.RenameGroup) ? view.RenameGroup : null;
                    reference = Alive(view.RenameButton) ? view.RenameButton : null;
                    if (host == null) missing = "ContainerSubMenu.OpenTagEditButtonParent and OpenTagEditButton";
                    break;
                case VanillaPanelAnchor.BesideSort:
                    reference = Alive(view.Sort) ? view.Sort : null;
                    host = reference != null ? reference.transform.parent : null;
                    if (host == null) missing = "InventorySubMenu.SelfSortButton, AllSortButton and a sort button on BottomBarButtonsParent";
                    break;
                case VanillaPanelAnchor.TooltipBody:
                    var frame = view.Tooltip != null ? view.Tooltip.DescriptionFrame : null;
                    host = Alive(frame) ? frame!.transform.parent : null;
                    if (host == null) missing = "FakeTooltip.DescriptionFrame";
                    break;
                default:
                    host = null;
                    missing = "an anchor this site does not have";
                    break;
            }

            if (host == null)
            {
                // This anchor's group is missing; only this handle goes without a container (it
                // says so once itself). The site and every other anchor keep working.
                SetAnchorStatus(site, anchor, "missing: " + missing);
                Degraded(site, "anchor " + VanillaUiSites.AnchorKey(anchor) + ": " + missing);
                return null;
            }

            // BesideSort never joins the bar (VanillaPanelAnchors.PlanInline with besideSort): a
            // grid recenters its row, so a fifth cell would move all four game buttons.
            var mode = VanillaUiSites.IsEdge(anchor) || anchor == VanillaPanelAnchor.TooltipBody
                ? InlineMode.None
                : PlanInline(host, anchor == VanillaPanelAnchor.BesideSort);
            if (mode == InlineMode.GridCell || mode == InlineMode.Group)
                siblingIndex = reference != null && reference.transform.parent != null && reference.transform.parent.Pointer == host.Pointer
                    ? reference.transform.GetSiblingIndex() + 1
                    : -1;

            // A placed container is a child of the group's parent, not of the group: no layout or
            // gamepad selection group built over the group's children can see it (the bar's
            // ButtonSelectionGroup keeps its own button list over its LayoutGroup parent).
            var group = host;
            var parentT = mode == InlineMode.Positioned && group.parent != null ? group.parent : group;

            go = new GameObject(containerName);
            var rt = go.AddComponent<RectTransform>();
            rt.SetParent(parentT, worldPositionStays: false);
            if (siblingIndex >= 0) rt.SetSiblingIndex(Mathf.Min(siblingIndex, parentT.childCount - 1));
            else rt.SetAsLastSibling();
            rt.localScale = Vector3.one;

            var size = ButtonSizeFor(view, anchor, mode, group, reference);

            // The size is said in the line but is not part of what counts as a change: the rename
            // button's measured height can read 0 before its first layout and 24 after.
            SetAnchorStatus(site, anchor, "ok host=" + SafeObjectName(parentT.gameObject)
                + (mode == InlineMode.Positioned ? " group=" + SafeObjectName(group.gameObject) : "")
                + " mode=" + (VanillaUiSites.IsEdge(anchor) ? "column" : anchor == VanillaPanelAnchor.TooltipBody ? "tooltip" : VanillaPanelAnchors.ModeWord(mode))
                + (anchor == VanillaPanelAnchor.BesideSort ? " sort=" + view.SortSource : "")
                + (anchor == VanillaPanelAnchor.BesideRename ? " via=" + view.RenameSource : "")
                + (anchor == VanillaPanelAnchor.TooltipBody ? "" : " donor=" + view.DonorSource),
                anchor == VanillaPanelAnchor.TooltipBody ? "" : " button=" + Mathf.RoundToInt(size.x) + "x" + Mathf.RoundToInt(size.y));
            host = parentT;
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
                column.spacing = GroupSpacing(view.Bar, VanillaPanelPlacement.DefaultSpacing);
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
                // Inline. A grid cell or a group member is laid out by the game's own group like one
                // more of its buttons; a positioned row is ignored by the group and placed by the fit
                // right after the group's last visible member (VanillaPanelAnchors.PlanInline).
                var row = go.AddComponent<HorizontalLayoutGroup>();
                row.childAlignment = TextAnchor.MiddleCenter;
                row.childControlWidth = true;
                row.childControlHeight = true;
                row.childForceExpandWidth = true;
                row.childForceExpandHeight = true;
                row.spacing = InlineSpacing(group, mode);
                var element = go.AddComponent<LayoutElement>();
                if (mode == InlineMode.Positioned)
                {
                    element.ignoreLayout = true;
                    var hostRt = host.TryCast<RectTransform>();
                    var pivot = hostRt != null ? hostRt.pivot : new Vector2(0.5f, 0.5f);
                    rt.anchorMin = pivot;
                    rt.anchorMax = pivot;
                    rt.pivot = Vector2.zero;
                    rt.sizeDelta = new Vector2(0f, size.y);
                }
                else
                {
                    element.preferredWidth = 0f;
                    element.preferredHeight = size.y;
                    element.minHeight = size.y;
                    if (mode == InlineMode.Group) CopyFlexible(element, reference);
                }
            }

            bool hostLaid = false;
            try { hostLaid = host.GetComponent<VerticalLayoutGroup>() != null; } catch { /* */ }
            return new PanelContainerWidget(go, site, anchor, hostLaid, mode, size, group);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: attach " + VanillaUiSites.Key(site) + " container build threw: " + e.Message);
            TryDestroy(go, immediate: true);
            return null;
        }
    }

    private static InlineMode PlanInline(Transform host, bool besideSort)
    {
        try { return VanillaPanelAnchors.PlanInline(new TransformNode(host), besideSort); }
        catch { return InlineMode.Positioned; }
    }

    private static GridLayoutGroup? GridOf(Transform? t)
    {
        try { return Alive(t) ? t!.GetComponent<GridLayoutGroup>() : null; }
        catch { return null; }
    }

    /// <summary>
    /// The size of one button in this container: the bar button's own size for a column and a
    /// group member; the grid's cell for a grid cell (the grid sizes it anyway, and this keeps the
    /// caption's arithmetic on the same numbers); for a positioned row the reference control's row
    /// height (the rename button's 24 units) at the bar button's proportions.
    /// </summary>
    private static Vector2 ButtonSizeFor(PanelView view, VanillaPanelAnchor anchor, InlineMode mode, Transform host, GameObject? reference)
    {
        var donor = DonorButtonSize(view);
        if (VanillaUiSites.IsEdge(anchor) || anchor == VanillaPanelAnchor.TooltipBody) return donor;
        switch (mode)
        {
            case InlineMode.GridCell:
                try
                {
                    var grid = GridOf(host);
                    if (grid != null && grid.cellSize.x > 1f && grid.cellSize.y > 1f) return grid.cellSize;
                }
                catch { /* */ }
                return donor;
            case InlineMode.Positioned:
                float refHeight = 0f;
                try
                {
                    var rrt = Rt(reference);
                    if (rrt != null)
                    {
                        // Before the window's first layout pass the rect can read 0; the authored
                        // size (24 for the rename button, which no group sizes) is the same answer
                        // the laid-out rect gives, so the row height never flips between builds.
                        refHeight = rrt.rect.height;
                        if (refHeight <= 1f) refHeight = rrt.sizeDelta.y;
                    }
                }
                catch { /* */ }
                float h = VanillaPanelAnchors.PositionedHeight(refHeight, donor.y);
                return new Vector2(
                    anchor == VanillaPanelAnchor.BesideSort ? VanillaPanelAnchors.CompactWidth(h) : VanillaPanelAnchors.ScaledWidth(donor.x, donor.y, h),
                    h);
            default:
                return donor;
        }
    }

    /// <summary>Spacing between our own buttons inside an inline container, and (positioned) from the group.</summary>
    private static float InlineSpacing(Transform? host, InlineMode mode)
    {
        if (mode == InlineMode.GridCell)
        {
            try
            {
                var grid = GridOf(host);
                if (grid != null) return grid.spacing.x;
            }
            catch { /* */ }
            return VanillaPanelPlacement.DefaultSpacing;
        }

        if (mode == InlineMode.Positioned) return VanillaPanelAnchors.PositionedSpacing(GroupSpacing(host, 0f));
        return GroupSpacing(host, VanillaPanelPlacement.DefaultSpacing);
    }

    /// <summary>Why the last button clone on a site failed (cleared by a success). Read by <c>ui_read panels</c>.</summary>
    private static readonly Dictionary<VanillaUiSite, string> ButtonFailures = new();

    /// <summary>A button clone failed: record why, warn once per site and reason, and return null.</summary>
    private static IRuntimeWidget? ButtonFailed(VanillaUiSite site, string why)
    {
        ButtonFailures[site] = why;
        if (VanillaSiteHealth.LogOnce(site, "button: " + why))
            Plugin.Logger?.LogWarning("Ui: attach " + VanillaUiSites.Key(site) + " button not built: " + why + ".");
        return null;
    }

    /// <summary>Record the last word on an anchor; a change is logged once (found or missing, with what).</summary>
    /// <param name="detail">Said in the line and in <c>ui_read</c>, but not part of what counts as a change.</param>
    private static void SetAnchorStatus(VanillaUiSite site, VanillaPanelAnchor anchor, string status, string detail = "")
    {
        bool changed = !AnchorStatusKey.TryGetValue((site, anchor), out var old) || old != status;
        AnchorStatusKey[(site, anchor)] = status;
        AnchorStatus[(site, anchor)] = status + detail;
        if (changed && status.StartsWith("ok", StringComparison.Ordinal))
            Plugin.Logger?.LogInfo("Ui: site " + VanillaUiSites.Key(site) + " anchor=" + VanillaUiSites.AnchorKey(anchor) + " " + status + detail);
    }

    private static readonly Dictionary<(VanillaUiSite, VanillaPanelAnchor), string> AnchorStatusKey = new();

    /// <summary>The last placement geometry line said per site and anchor.</summary>
    private static readonly Dictionary<(VanillaUiSite, VanillaPanelAnchor), string> PlacementSaid = new();

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

    public IRuntimeWidget? ClonePanelButton(VanillaUiSite site, IRuntimeWidget container, string caption) =>
        ClonePanelButton(site, container, caption, VanillaButtonIcon.Default);

    public IRuntimeWidget? ClonePanelButton(VanillaUiSite site, IRuntimeWidget container, string caption, VanillaButtonIcon icon)
    {
        if (container is not PanelContainerWidget host || !host.Alive) return null;
        if (host.Anchor == VanillaPanelAnchor.TooltipBody) return null;
        if (!PanelViews.TryGetValue(site, out var view) || view == null || !view.Live) return null;
        var donor = view.ButtonDonor;
        if (!Alive(donor)) return ButtonFailed(site, "no button donor");
        if (!Alive(view.CaptionDonor)) return ButtonFailed(site, "no caption donor");

        GameObject? clone = null;
        try
        {
            clone = CloneButton(donor!, host.GameObject!);
            if (clone == null) return ButtonFailed(site, "donor " + SafeObjectName(donor) + " refused by the clone gate (a state target outside its own subtree, or not a plain SimpleStunButton)");
            clone.name = host.Name + "-button";

            // Buttons stack before rows in a column; in an inline group there are only buttons.
            clone.transform.SetSiblingIndex(host.Buttons.Count);
            var size = host.ButtonSize;
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
                return ButtonFailed(site, label == null ? "caption clone had no TextMeshProUGUI" : "clone not operable (no visible, raycastable graphic)");
            }

            var stun = clone.GetComponent<SimpleStunButton>();
            if (stun == null)
            {
                TryDestroyImmediate(clone);
                return ButtonFailed(site, "clone has no SimpleStunButton on its root");
            }

            // BesideSort may fall back to a square icon face (PlaceBesideRow); its icon is built now,
            // hidden, so the switch is a SetActive and never a clone.
            GameObject? face = host.Anchor == VanillaPanelAnchor.BesideSort ? AddIconFace(clone, label, icon, size) : null;
            var widget = new PanelButtonWidget(clone, label, stun.onClick, size, face, this);
            var element = clone.GetComponent<LayoutElement>() ?? clone.AddComponent<LayoutElement>();
            element.ignoreLayout = false;
            element.preferredHeight = size.y;
            element.minHeight = size.y;
            widget.Element = element;
            widget.Refit();

            clone.SetActive(true);
            host.Buttons.Add(widget);
            ButtonFailures.Remove(site);
            return widget;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: attach " + VanillaUiSites.Key(site) + " button clone threw: " + e.Message);
            TryDestroyImmediate(clone);
            return null;
        }
    }

    /// <summary>Icon face size as a share of the button's height (24 on the 36-unit bar).</summary>
    private const float IconFaceShare = 0.67f;

    /// <summary>
    /// The icon an icon-mode button shows: the item's own game icon (<c>ManagedItemData.Icon</c>) for
    /// <see cref="VanillaButtonIcon.Item"/>, else the framework's drawn mark, tinted with the caption's
    /// color. Built hidden; the caption's object is the one that hides when it shows.
    /// </summary>
    private static GameObject? AddIconFace(GameObject clone, TextMeshProUGUI caption, VanillaButtonIcon icon, Vector2 size)
    {
        GameObject? go = null;
        try
        {
            Sprite? sprite = null;
            bool itemIcon = false;
            if (icon.ItemGuid != 0)
            {
                sprite = ItemIconSprite(icon.ItemGuid);
                itemIcon = sprite != null;
            }

            if (sprite == null)
            {
                if (!UiDrawnMark.TryParse(icon.Mark, out var mark) || !mark.IsIcon) mark = UiDrawnMark.ChestMark;
                sprite = MarkSprite(mark);
            }

            if (sprite == null) return null;
            go = new GameObject("icon");
            var rt = go.AddComponent<RectTransform>();
            rt.SetParent(clone.transform, false);
            float side = Mathf.Round(size.y * IconFaceShare);
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(side, side);
            rt.anchoredPosition = Vector2.zero;
            var layout = go.AddComponent<LayoutElement>();
            layout.ignoreLayout = true;
            var image = go.AddComponent<Image>();
            image.sprite = sprite;
            image.preserveAspect = true;
            image.raycastTarget = false;
            image.color = itemIcon ? Color.white : caption.color;
            go.SetActive(false);
            return go;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: attach icon face not built: " + e.Message);
            TryDestroyImmediate(go);
            return null;
        }
    }

    /// <summary>An item's own inventory icon from the client's game data, or null.</summary>
    private static Sprite? ItemIconSprite(int guid)
    {
        try
        {
            var world = Worlds.Client;
            if (world == null || !world.IsCreated) return null;
            var registry = world.GetExistingSystemManaged<ProjectM.GameDataSystem>()?.ManagedDataRegistry;
            if (registry == null) return null;
            if (registry.TryGet<ProjectM.ManagedItemData>(new Stunlock.Core.PrefabGUID(guid), out var data) && data != null)
                return data.Icon;
        }
        catch { /* the drawn mark stands in */ }
        return null;
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

            var size = host.ButtonSize;
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

        // Nothing built (or every button hidden) is nothing on screen: it fits trivially, like an
        // empty edge column, instead of reading as an unmeasurable "pending" rect.
        if (host.InlineWidth(0f) <= 0f) return new VanillaFitResult(VanillaFit.Fits, "empty");

        var group = host.Group;
        var panelSpace = view.Panel!.transform;
        var panelLocal = RectIn(panelSpace, panelRt);
        if (panelLocal.IsEmpty || group == null) return new VanillaFitResult(VanillaFit.Unknown, "pending");

        if (host.Mode != InlineMode.Positioned)
        {
            // Joined: tell the group how wide we are, make it lay out now, then check what it did.
            // Measuring before the group has laid us out reads a default rect and would hide the
            // buttons for good. A grid cell is sized by the grid (the width ask is ignored there).
            float groupSpacing = GroupSpacing(group, VanillaPanelPlacement.DefaultSpacing);
            try
            {
                var element = host.GameObject!.GetComponent<LayoutElement>();
                float want = host.InlineWidth(groupSpacing);
                if (element != null && host.Mode == InlineMode.Group && Mathf.Abs(element.preferredWidth - want) > 0.5f)
                    element.preferredWidth = want;
                var groupRt = group.TryCast<RectTransform>();
                if (groupRt != null) LayoutRebuilder.ForceRebuildLayoutImmediate(groupRt);
            }
            catch { /* */ }
        }

        // The frame is the window widened to the strip the group lives on (the container's title
        // strip sits above its window rect).
        FitScratchRects.Clear();
        try
        {
            var a = group.parent;
            for (int depth = 0; a != null && depth < 8 && a.Pointer != panelSpace.Pointer; depth++, a = a.parent)
                FitScratchRects.Add(RectIn(panelSpace, a.TryCast<RectTransform>()));
        }
        catch { /* the window rect alone is the frame */ }
        var frame = VanillaPanelPlacement.InlineFrame(panelLocal, FitScratchRects);
        FitScratchRects.Clear();

        // Obstacles: the group's own members; for a placed container also the group's siblings
        // (the inventory's item area above the bar), since it sits beside the group, not in it.
        FitScratch.Clear();
        var oursPtr = host.Transform != null ? host.Transform.Pointer : IntPtr.Zero;
        UiRect lastMember = default;
        AddMembers(group, panelSpace, oursPtr, ref lastMember);
        if (host.Mode == InlineMode.Positioned && group.parent != null)
        {
            var parent = group.parent;
            for (int i = 0; i < parent.childCount; i++)
            {
                var sib = parent.GetChild(i);
                if (sib == null || sib.Pointer == group.Pointer || sib.Pointer == oursPtr || !sib.gameObject.activeInHierarchy) continue;
                FitScratch.Add(new UiObstacle(SafeObjectName(sib.gameObject), RectIn(panelSpace, sib.TryCast<RectTransform>())));
            }
        }

        VanillaFitResult inlineVerdict;
        if (host.Mode == InlineMode.Positioned)
        {
            if (lastMember.IsEmpty) lastMember = RectIn(panelSpace, group.TryCast<RectTransform>());

            // Judge only a window at rest: it slides in when it opens, and a slot measured mid-slide
            // is the wrong slot (live: 88 wide sliding, 58 at rest). Until the frame and the bar have
            // measured the same three frames running, nothing is shown and nothing is logged.
            if (!host.Settle.Observe(frame, lastMember))
            {
                FitScratch.Clear();
                return VanillaFitResult.Moving;
            }

            var reference = host.Anchor == VanillaPanelAnchor.BesideSort && Alive(view.Sort)
                ? RectIn(panelSpace, Rt(view.Sort))
                : default;
            float rowSpacing = GroupSpacing(host.Transform, VanillaPanelPlacement.DefaultSpacing);
            float width = host.TextRowWidth(rowSpacing);
            float gap = VanillaPanelAnchors.PositionedSpacing(GroupSpacing(group, 0f));
            int capable = host.IconCapableCount(out int visibleButtons);
            bool allowIcons = host.Anchor == VanillaPanelAnchor.BesideSort && visibleButtons > 0 && capable == visibleButtons;
            inlineVerdict = VanillaPanelPlacement.PlaceBesideRow(
                lastMember, reference, allowAbove: host.Anchor == VanillaPanelAnchor.BesideSort, allowIcons, gap,
                width, host.MinInlineWidth(rowSpacing), visibleButtons, host.ButtonSize.y, frame, FitScratch, out var slot);
            host.Slot = slot;
            if (inlineVerdict.Fit == VanillaFit.Fits)
            {
                if (host.IconRow != inlineVerdict.Icons) host.SetIconRow(inlineVerdict.Icons);
                Place(host, panelSpace, inlineVerdict.Rect);
            }

            // The geometry the verdict was made on, said when it changes (a refused slot is asked
            // again every 30 frames; the line repeats only if something moved).
            string geometry = "Ui: attach " + VanillaUiSites.Key(host.Site) + " anchor=" + VanillaUiSites.AnchorKey(host.Anchor)
                + " slot=" + (slot == BesideSlot.After ? "after" : slot == BesideSlot.Above ? "above" : "none")
                + " face=" + (inlineVerdict.Fit == VanillaFit.Fits ? (inlineVerdict.Icons ? "icon" : "text") : "none")
                + " last=" + lastMember + " frame=" + frame + " row=" + Mathf.RoundToInt(width) + "/min" + Mathf.RoundToInt(host.MinInlineWidth(rowSpacing))
                + " placed=" + inlineVerdict.Rect + " reason=" + inlineVerdict.Reason;
            if (!PlacementSaid.TryGetValue((host.Site, host.Anchor), out var said) || said != geometry)
            {
                PlacementSaid[(host.Site, host.Anchor)] = geometry;
                Plugin.Logger?.LogInfo(geometry);
            }
        }
        else
        {
            var ours = RectIn(panelSpace, host.Rect);
            inlineVerdict = ours.IsEmpty
                ? new VanillaFitResult(VanillaFit.Unknown, "pending")
                : VanillaPanelPlacement.CheckInline(ours, frame, FitScratch);
        }

        FitScratch.Clear();
        return inlineVerdict;
    }

    private static readonly List<UiRect> FitScratchRects = new();

    /// <summary>The group's visible members as obstacles (ours skipped); <paramref name="last"/> is the last of them.</summary>
    private static void AddMembers(Transform group, Transform space, IntPtr ours, ref UiRect last)
    {
        for (int i = 0; i < group.childCount; i++)
        {
            var child = group.GetChild(i);
            if (child == null || child.Pointer == ours || !child.gameObject.activeInHierarchy) continue;
            var r = RectIn(space, child.TryCast<RectTransform>());
            FitScratch.Add(new UiObstacle(SafeObjectName(child.gameObject), r));
            if (!r.IsEmpty) last = r;
        }
    }

    /// <summary>
    /// Put a placed container at <paramref name="rect"/> (in <paramref name="space"/>'s units). The
    /// container is anchored at its parent's pivot with a bottom-left pivot, so its anchored
    /// position is a point in the parent's local units: the rect is carried over by two corners.
    /// </summary>
    private static void Place(PanelContainerWidget host, Transform space, UiRect rect)
    {
        try
        {
            var rt = host.Rect;
            var parent = rt != null ? rt.parent : null;
            if (rt == null || parent == null) return;
            Vector3 a = parent.InverseTransformPoint(space.TransformPoint(new Vector3(rect.X, rect.Y, 0f)));
            Vector3 b = parent.InverseTransformPoint(space.TransformPoint(new Vector3(rect.XMax, rect.YMax, 0f)));
            var pos = new Vector2(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y));
            var size = new Vector2(Mathf.Abs(b.x - a.x), Mathf.Abs(b.y - a.y));
            if ((rt.anchoredPosition - pos).sqrMagnitude > 0.01f) rt.anchoredPosition = pos;
            if ((rt.sizeDelta - size).sqrMagnitude > 0.01f) rt.sizeDelta = size;
            LayoutRebuilder.ForceRebuildLayoutImmediate(rt);
        }
        catch { /* left where it was; the next refit places it again */ }
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
    /// Every tracked vanilla attachment for <c>ui_read panels</c> (<c>attached</c>): the handle's
    /// state (<see cref="VanillaAttachReadback.Describe"/>), the last word on its anchor
    /// (<c>anchorStatus</c>: <c>ok host=... mode=... donor=...</c> or <c>missing: ...</c>), and the
    /// screen rects of the container and of each built button while they are shown. Main thread.
    /// </summary>
    internal static List<object?> DescribeAttachments()
    {
        var list = new List<object?>();
        foreach (var handle in VanillaAttachRegistry.Snapshot())
        {
            Dictionary<string, object?> d;
            VanillaAttachReadback rb;
            try
            {
                rb = handle.Readback();
                d = rb.Describe();
            }
            catch (Exception e)
            {
                list.Add(new Dictionary<string, object?> { ["error"] = e.Message });
                continue;
            }

            if (VanillaUiSites.IsPanel(rb.Site))
            {
                d["anchorStatus"] = VanillaSiteHealth.IsAbsent(rb.Site) ? "absent"
                    : AnchorStatus.TryGetValue((rb.Site, rb.Anchor), out var s) ? s
                    : "not resolved yet (the window has not been open with this anchor attached)";
                if (PanelViews.TryGetValue(rb.Site, out var view) && view.Live)
                    d["donor"] = view.DonorSource.Length > 0 ? view.DonorSource : "<none>";
                d["buttonFailure"] = ButtonFailures.TryGetValue(rb.Site, out var bf) ? bf : "";
            }

            object? rect = null;
            try
            {
                if (rb.Container is PanelContainerWidget pc && pc.Alive && pc.GameObject!.activeInHierarchy && pc.Rect != null)
                    rect = RectDict(ScreenRectOf(pc.Rect));
            }
            catch { /* */ }
            d["rect"] = rect;
            if (rb.Container is PanelContainerWidget placed && placed.Mode == InlineMode.Positioned)
            {
                d["slot"] = placed.Slot == BesideSlot.After ? "after" : placed.Slot == BesideSlot.Above ? "above" : "none";
                d["face"] = placed.IconRow ? "icon" : "text";
            }

            if (d["buttons"] is List<object?> buttons)
            {
                for (int i = 0; i < buttons.Count && i < rb.Buttons.Length; i++)
                {
                    if (buttons[i] is not Dictionary<string, object?> b) continue;
                    object? brect = null;
                    try
                    {
                        if (rb.Buttons[i] is PanelButtonWidget pb && pb.Alive && pb.Rect != null && pb.Rect.gameObject.activeInHierarchy)
                            brect = RectDict(ScreenRectOf(pb.Rect));
                    }
                    catch { /* */ }
                    b["rect"] = brect;
                }
            }

            list.Add(d);
        }

        return list;
    }

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
                + Describe("sort", view.Sort != null ? view.Sort.transform : null) + (view.Sort != null ? "{" + view.SortSource + "}" : "")
                + Describe("donor", view.ButtonDonor != null ? view.ButtonDonor.transform : null) + (view.ButtonDonor != null ? "{" + view.DonorSource + "}" : "")
                + Describe("bar", view.Bar) + (view.Bar != null ? "{" + LayoutOf(view.Bar) + "}" : "")
                + Describe("renameGroup", view.RenameGroup) + (view.RenameGroup != null ? "{" + view.RenameSource + "}" : "")
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

        internal PanelContainerWidget(
            GameObject go, VanillaUiSite site, VanillaPanelAnchor anchor, bool hostLaidOut, InlineMode mode, Vector2 buttonSize,
            Transform group)
        {
            _go = go;
            _group = group;
            Site = site;
            Anchor = anchor;
            HostLaidOut = hostLaidOut;
            Mode = mode;
            ButtonSize = buttonSize;
            Name = SafeObjectName(go);
        }

        internal VanillaUiSite Site { get; }

        internal VanillaPanelAnchor Anchor { get; }

        /// <summary>How an inline container joined its group (<see cref="InlineMode.None"/> for columns and the tooltip).</summary>
        internal InlineMode Mode { get; }

        /// <summary>One button's size in this container (its minimum width before the caption widens it).</summary>
        internal Vector2 ButtonSize { get; }

        /// <summary>Where a placed container went on its last fit (<see cref="BesideSlot.None"/> otherwise).</summary>
        internal BesideSlot Slot { get; set; }

        /// <summary>Whether the window this placed container measures has come to rest.</summary>
        internal PlacementSettle Settle { get; } = new();

        /// <summary>The row is shown as icon buttons (the captions did not fit the slot).</summary>
        internal bool IconRow { get; private set; }

        /// <summary>Switch every button between its text and icon face.</summary>
        internal void SetIconRow(bool icons)
        {
            IconRow = icons;
            for (int i = 0; i < Buttons.Count; i++)
                if (Buttons[i].Alive) Buttons[i].SetIconMode(icons);
        }

        /// <summary>The visible buttons with an icon face (an icon row needs every one of them).</summary>
        internal int IconCapableCount(out int visible)
        {
            visible = 0;
            int capable = 0;
            for (int i = 0; i < Buttons.Count; i++)
            {
                if (!Buttons[i].Alive || !Buttons[i].Visible) continue;
                visible++;
                if (Buttons[i].HasIcon) capable++;
            }

            return capable;
        }

        /// <summary>The row's width as text at full size.</summary>
        internal float TextRowWidth(float spacing)
        {
            float w = 0f;
            int n = 0;
            for (int i = 0; i < Buttons.Count; i++)
            {
                if (!Buttons[i].Alive || !Buttons[i].Visible) continue;
                w += Buttons[i].TextWidth;
                n++;
            }

            return n == 0 ? 0f : w + (n - 1) * Mathf.Max(0f, spacing);
        }

        private readonly Transform _group;

        /// <summary>
        /// The vanilla group this container goes beside or into: its parent for a joined container,
        /// its sibling for a placed one (<see cref="InlineMode.Positioned"/>).
        /// </summary>
        internal Transform? Group
        {
            get { try { return UnityUiRuntime.Alive(_group) ? _group : null; } catch { return null; } }
        }

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

        /// <summary>
        /// The narrowest this row may be squeezed to: each button at its caption's
        /// <see cref="VanillaPanelAnchors.MinCaptionScale"/> of full size plus the padding, never
        /// below the bar button's 36 units.
        /// </summary>
        internal float MinInlineWidth(float spacing)
        {
            float w = 0f;
            int n = 0;
            for (int i = 0; i < Buttons.Count; i++)
            {
                if (!Buttons[i].Alive || !Buttons[i].Visible) continue;
                w += VanillaPanelAnchors.MinButtonWidth(Buttons[i].CaptionWidth, CaptionPadding, ButtonSize.y);
                n++;
            }

            return n == 0 ? 0f : w + (n - 1) * Mathf.Max(0f, spacing);
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
        private GameObject? _icon;
        private UiHoverWatch? _hover;
        private bool _hovered;
        private readonly UnityUiRuntime? _runtime;
        private readonly object? _click;
        private readonly Vector2 _donorSize;
        private UiDestroyVerdict _verdict = UiDestroyVerdict.Failed;
        private float _textWidth;

        internal PanelButtonWidget(GameObject go, TextMeshProUGUI caption, object? click, Vector2 donorSize,
            GameObject? icon = null, UnityUiRuntime? runtime = null)
        {
            _go = go;
            _caption = caption;
            _click = click;
            _donorSize = donorSize;
            _icon = icon;
            _runtime = runtime;
            _textWidth = donorSize.x;
            if (icon != null && runtime != null)
            {
                try { _hover = UiHoverWatch.Create(go, OnHover, null); }
                catch (Exception e) { Plugin.Logger?.LogWarning("Ui: attach icon button tooltip unavailable: " + e.Message); }
            }
        }

        /// <summary>
        /// Shown as a square icon button (its caption hidden, the caption as its tooltip) because the
        /// caption does not fit the slot (<see cref="VanillaPanelPlacement.PlaceBesideRow"/>).
        /// </summary>
        internal bool IconMode { get; private set; }

        /// <summary>The width this button asks for as text: donor width or caption plus padding.</summary>
        internal float TextWidth => _textWidth;

        /// <summary>An icon face was built (a <see cref="VanillaPanelAnchor.BesideSort"/> button).</summary>
        internal bool HasIcon => UnityUiRuntime.Alive(_icon);

        internal void SetIconMode(bool icon)
        {
            if (_icon == null) icon = false;
            if (icon == IconMode) return;
            IconMode = icon;
            try
            {
                if (_caption != null) _caption.gameObject.SetActive(!icon);
                if (UnityUiRuntime.Alive(_icon)) _icon!.SetActive(icon);
                if (Element != null)
                {
                    Element.preferredWidth = PreferredWidth;
                    Element.minWidth = icon ? _donorSize.y : -1f;
                }
            }
            catch { /* the face it had stays */ }

            if (!icon && _hovered) HideTip();
        }

        private void OnHover(bool inside)
        {
            _hovered = inside;
            if (!inside || !IconMode)
            {
                HideTip();
                return;
            }

            try
            {
                var rt = Rect;
                if (rt == null || _runtime == null) return;
                string caption = _caption != null ? (_caption.text ?? "") : "";
                _runtime.ShowTooltip(ScreenRectOf(rt), caption, "", () => _hovered && IconMode && Alive);
            }
            catch { /* no tooltip is a cosmetic miss */ }
        }

        private void HideTip()
        {
            try { _runtime?.HideTooltip(); }
            catch { /* */ }
        }

        internal LayoutElement? Element { get; set; }

        internal RectTransform? Rect
        {
            get { try { return UnityUiRuntime.Alive(_go) ? _go!.GetComponent<RectTransform>() : null; } catch { return null; } }
        }

        /// <summary>
        /// Text: donor width, or the caption at full size plus the padding, whichever is wider. Icon:
        /// square, the button's height.
        /// </summary>
        internal float PreferredWidth => IconMode ? _donorSize.y : _textWidth;

        /// <summary>The caption's own width at full size (no padding), measured by the last refit.</summary>
        internal float CaptionWidth { get; private set; }

        internal void Refit()
        {
            try
            {
                if (_caption == null) return;
                var pref = _caption.GetPreferredValues(_caption.text ?? "", 9999f, _donorSize.y);
                CaptionWidth = pref.x;
                _textWidth = Mathf.Max(_donorSize.x, pref.x + 2f * CaptionPadding);
                if (Element != null) Element.preferredWidth = PreferredWidth;
            }
            catch { _textWidth = _donorSize.x; }
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
            if (_hover != null)
            {
                try { _hover.Dispose(); }
                catch { /* */ }
                _hover = null;
            }

            if (_hovered)
            {
                _hovered = false;
                HideTip();
            }

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
