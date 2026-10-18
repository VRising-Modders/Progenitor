#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
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
/// the refinement mapper too; the inventory sites read all eight) would miss opens, and a close would
/// never be seen, leaving an edge column on screen. With the option off the hook fires whenever the
/// mapper updates, and the site reads every mapper it needs from one shared per-frame scan
/// (<see cref="EnsureScan"/>). The mappers do not all update every frame, so every site hooks all
/// eight and has a frame watchdog for its close (<see cref="WatchPanelSite"/>). Never Harmony on a mapper, never a mapper's Burst
/// <c>LambdaJob_*</c>, never a method of the window (<c>FakeTooltip.SetTooltip</c> takes a dozen
/// <c>Nullable</c> structs by value and is not a seam).</para>
///
/// <para><b>The address.</b> Each mapper keeps the window it drives in a private field
/// (<c>_ContainerSubMenu</c>, <c>_WorkstationSubMenu</c>, <c>_RefinementStationSubMenu</c>, <c>_PrisonSubMenu</c>,
/// <c>_SalvageStationSubMenu</c>, <c>_UnitSpawnerstationSubMenu</c>,
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

        /// <summary>What <see cref="VanillaPanelAnchor.BesideRename"/> lacks on this window when it has no rename group (<see cref="VanillaStationWindows.RenameMissing"/>).</summary>
        internal string RenameMissing = "";
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

        /// <summary>
        /// The window's own icon-button row <see cref="VanillaPanelAnchor.BesideTitleButtons"/> goes beside (the castle
        /// heart's <c>ForfeitButtonsParent</c>), the game button that is its reference, and the field that found it.
        /// </summary>
        internal Transform? TitleButtons;
        internal GameObject? TitleButtonsRef;
        internal string TitleButtonsSource = "";

        /// <summary>What <see cref="VanillaPanelAnchor.BesideTitleButtons"/> lacks on this window when it has no row.</summary>
        internal string TitleButtonsMissing = "";

        /// <summary>The row's own button the mod's buttons are cloned from when it is clone-safe (else the bar donor).</summary>
        internal GameObject? TitleButtonsDonor;

        /// <summary>The game icon's share of its button's height on that row (24 of 42 on 1.1.13.0), and its color.</summary>
        internal float TitleIconShare;
        internal Color TitleIconColor = Color.white;

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
            RenameMissing = "";
            CaptionDonor = null;
            BodyDonor = null;
            Tooltip = null;
            ButtonDonor = null;
            DonorSource = "";
            Sort = null;
            SortSource = "";
            Bar = null;
            TitleButtons = null;
            TitleButtonsRef = null;
            TitleButtonsSource = "";
            TitleButtonsMissing = "";
            TitleButtonsDonor = null;
            TitleIconShare = 0f;
            TitleIconColor = Color.white;
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

        /// <summary>Two nodes over the same transform are the same node (<see cref="Parent"/> makes a new wrapper each time).</summary>
        public override bool Equals(object? obj)
        {
            try { return obj is TransformNode o && Alive(Transform) && Alive(o.Transform) && o.Transform.Pointer == Transform.Pointer; }
            catch { return false; }
        }

        public override int GetHashCode()
        {
            try { return Alive(Transform) ? Transform.Pointer.GetHashCode() : 0; }
            catch { return 0; }
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

    /// <summary>Each castle heart window's icon row, its reference button, the rule that found it and its donor.</summary>
    private static readonly Dictionary<int, TitleButtonsCache> TitleButtonRows = new();

    private sealed class TitleButtonsCache
    {
        internal Transform? Group;
        internal GameObject? Reference;
        internal GameObject? Donor;
        internal string Source = "";
        internal float IconShare;
        internal Color IconColor = Color.white;
        internal int Frame;
    }

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
    private static WindowScan _container, _workstation, _refinement, _heart, _character, _prison, _salvage, _unit;

    /// <summary>
    /// The station windows in <see cref="VanillaStationWindows.Covered"/> order, refreshed by every
    /// scan; <see cref="StationStates"/> is its game-free mirror for <see cref="VanillaStationWindows.Pick"/>.
    /// </summary>
    private static readonly WindowScan[] StationScans = new WindowScan[VanillaStationWindows.Covered.Length];
    private static readonly StationWindowState[] StationStates = new StationWindowState[VanillaStationWindows.Covered.Length];

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
            Guarded(VanillaPanelKind.PrisonCell, "fetch", () => PrisonReader.Fetch(world));
            Guarded(VanillaPanelKind.SalvageStation, "fetch", () => SalvageReader.Fetch(world));
            Guarded(VanillaPanelKind.UnitStation, "fetch", () => UnitReader.Fetch(world));
        }

        _scanFrame = frame;
        _container = default;
        _workstation = default;
        _refinement = default;
        _heart = default;
        _character = default;
        _prison = default;
        _salvage = default;
        _unit = default;

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
        // The three later station windows are each read in their own non-inlined reader: a member the
        // game renames on one of them fails only that reader (one line, then that window is dropped for
        // the generation), never this scan and with it every other panel site.
        Guarded(VanillaPanelKind.PrisonCell, "read", () => _prison = PrisonReader.Read());
        Guarded(VanillaPanelKind.SalvageStation, "read", () => _salvage = SalvageReader.Read());
        Guarded(VanillaPanelKind.UnitStation, "read", () => _unit = UnitReader.Read());

        // Same order as VanillaStationWindows.Covered (a test pins that order).
        StationScans[0] = _workstation;
        StationScans[1] = _refinement;
        StationScans[2] = _prison;
        StationScans[3] = _salvage;
        StationScans[4] = _unit;
        for (int i = 0; i < StationScans.Length; i++)
        {
            var kind = VanillaStationWindows.Covered[i].Kind;
            if (VanillaStationWindows.IsFailed(kind)) StationScans[i] = default;
            StationStates[i] = new StationWindowState(kind, StationScans[i].SubShown, StationScans[i].Target.Index, StationScans[i].Target.Version);
        }

        if (VanillaStationWindows.IsFailed(VanillaPanelKind.PrisonCell)) _prison = default;
        if (VanillaStationWindows.IsFailed(VanillaPanelKind.SalvageStation)) _salvage = default;
        if (VanillaStationWindows.IsFailed(VanillaPanelKind.UnitStation)) _unit = default;
    }

    /// <summary>
    /// The chest, station or castle heart window shown now, through the same once-a-frame mapper scan the panel sites use
    /// (<c>ctx.Inventory.LastWindow</c> remembers it). <paramref name="interactBuff"/> is the container window's
    /// interaction (<c>ContainerMenuMapper._InteractBuff</c>; <see cref="Entity.Null"/> for a remote preview's window) and
    /// is left null for the other windows, which only open through an interaction. False when none of them is shown.
    /// </summary>
    internal static bool TryOpenInteractWindow(World world, out VanillaPanelKind kind, out Entity target, out Entity interactBuff)
    {
        kind = VanillaPanelKind.None;
        target = Entity.Null;
        interactBuff = Entity.Null;
        EnsureScan(world);
        if (_container.SubShown && _container.Target != Entity.Null)
        {
            kind = VanillaPanelKind.Container;
            target = _container.Target;
            try { interactBuff = _containerMapper == null ? Entity.Null : _containerMapper._InteractBuff; }
            catch { interactBuff = Entity.Null; }
            return true;
        }
        if (_heart.SubShown && _heart.Target != Entity.Null)
        {
            kind = VanillaPanelKind.CastleHeart;
            target = _heart.Target;
            return true;
        }
        for (int i = 0; i < StationScans.Length; i++)
        {
            if (!StationScans[i].SubShown || StationScans[i].Target == Entity.Null) continue;
            kind = VanillaStationWindows.Covered[i].Kind;
            target = StationScans[i].Target;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Run one station window's reader. A throw (a type or member the game removed: the JIT raises it
    /// when the reader is first compiled) drops that window for the generation with one warning; the
    /// scan and every other site go on.
    /// </summary>
    private static void Guarded(VanillaPanelKind kind, string what, Action read)
    {
        if (VanillaStationWindows.IsFailed(kind)) return;
        try { read(); }
        catch (Exception e)
        {
            if (VanillaStationWindows.MarkFailed(kind))
                Plugin.Logger?.LogWarning("Ui: StationPanel window " + kind + " unavailable (" + what + "): "
                    + e.GetType().Name + ": " + e.Message + "; the other windows and sites are unaffected");
        }
    }

    // Each reader is its own class so that its static field's type, its member reads and its hook's
    // generic instantiation are only loaded when that reader runs, inside Guarded's try/catch.

    private static class PrisonReader
    {
        internal static PrisonMenuMapper? M;

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Fetch(World world) => M = Mapper<PrisonMenuMapper>(world);

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static WindowScan Read() => M == null ? default : Window(M._PrisonSubMenu, M._InventorySubMenu, M._Target);

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static IDisposable Hook(Action<SystemHookContext> frame, HookOptions options) => SystemHooks.After<PrisonMenuMapper>(frame, options);

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static RectTransform? Grid(Component sub)
        {
            var s = sub.TryCast<PrisonSubMenu>();
            return s == null ? null : RectOf(s.RecipesParent);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Clear() => M = null;
    }

    private static class SalvageReader
    {
        internal static SalvagestationMenuMapper? M;

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Fetch(World world) => M = Mapper<SalvagestationMenuMapper>(world);

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static WindowScan Read() => M == null ? default : Window(M._SalvageStationSubMenu, M._InventorySubMenu, M._Target);

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static IDisposable Hook(Action<SystemHookContext> frame, HookOptions options) => SystemHooks.After<SalvagestationMenuMapper>(frame, options);

        /// <summary>No recipe grid: the input grid is the window's item grid.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static RectTransform? Grid(Component sub)
        {
            var s = sub.TryCast<SalvagestationSubMenu>();
            return s == null ? null : RectOf(s.ItemInputsParent);
        }

        /// <summary>The devourer window's rename button (<c>OpenTagEditButton</c>), or null.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static Component? Rename(Component sub)
        {
            var s = sub.TryCast<SalvagestationSubMenu>();
            return s == null ? null : s.OpenTagEditButton;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Clear() => M = null;
    }

    private static class UnitReader
    {
        internal static UnitSpawnerstationMenuMapper? M;

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Fetch(World world) => M = Mapper<UnitSpawnerstationMenuMapper>(world);

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static WindowScan Read() => M == null ? default : Window(M._UnitSpawnerstationSubMenu, M._InventorySubMenu, M._Target);

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static IDisposable Hook(Action<SystemHookContext> frame, HookOptions options) => SystemHooks.After<UnitSpawnerstationMenuMapper>(frame, options);

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static RectTransform? Grid(Component sub)
        {
            var s = sub.TryCast<UnitSpawnerstationSubMenu>();
            return s == null ? null : RectOf(s.RecipesParent);
        }

        /// <summary>The unit station window's rename button (<c>OpenTagEditButton</c>), or null.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static Component? Rename(Component sub)
        {
            var s = sub.TryCast<UnitSpawnerstationSubMenu>();
            return s == null ? null : s.OpenTagEditButton;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Clear() => M = null;
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
    /// The eight mapper hooks and the frame watchdog share it, so a site samples once a frame.
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
    /// sees it closed. So every panel site hooks <b>all eight</b> window mappers and samples on the
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

        Action<SystemHookContext> frame = site == VanillaUiSite.RecipeTooltip
            ? ctx =>
            {
                Sample(ctx?.World);
                try { SyncRequirementLines(); }
                catch { /* a container Unity just destroyed is dropped on the next sync */ }
            }
            : ctx => Sample(ctx?.World);
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
                case VanillaUiSite.RecipeTooltip:
                    break;
                default:
                    return null;
            }

            Add(leases, SystemHooks.After<ContainerMenuMapper>(frame, VanillaUiSites.WatchOptions(site)));
            Add(leases, SystemHooks.After<WorkstationMenuMapper>(frame, VanillaUiSites.WatchOptions(site)));
            Add(leases, SystemHooks.After<RefinementstationMenuMapper>(frame, VanillaUiSites.WatchOptions(site)));
            Add(leases, SystemHooks.After<CastleHeartMenuMapper>(frame, VanillaUiSites.WatchOptions(site)));
            Add(leases, SystemHooks.After<InventoryMenuMapper>(frame, VanillaUiSites.WatchOptions(site)));
            // Each later station mapper is hooked on its own: a failed registration drops that window.
            Guarded(VanillaPanelKind.PrisonCell, "hook", () => Add(leases, PrisonReader.Hook(frame, VanillaUiSites.WatchOptions(site))));
            Guarded(VanillaPanelKind.SalvageStation, "hook", () => Add(leases, SalvageReader.Hook(frame, VanillaUiSites.WatchOptions(site))));
            Guarded(VanillaPanelKind.UnitStation, "hook", () => Add(leases, UnitReader.Hook(frame, VanillaUiSites.WatchOptions(site))));
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

    /// <summary>One site watch: the eight mapper hooks and the watchdog, released together.</summary>
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
        TitleButtonRows.Clear();
        StackedContainers.Clear();
        StackVersions.Clear();
        StackScratch.Clear();
        ButtonFailures.Clear();
        AnchorStatusKey.Clear();
        PlacementSaid.Clear();
        AnchorStatus.Clear();
        _hovered = null;
        RecipeGridCaches.Clear();
        ClearRowCaches();
        _hoveredRecipe = null;
        RecipeHover.Clear();
        _scanFrame = -1;
        _scanWorld = null;
        _containerMapper = null;
        _workstationMapper = null;
        _refinementMapper = null;
        _heartMapper = null;
        _inventoryMapper = null;
        try { PrisonReader.Clear(); } catch { /* the reader never loaded */ }
        try { SalvageReader.Clear(); } catch { /* */ }
        try { UnitReader.Clear(); } catch { /* */ }
        VanillaStationWindows.ResetFailures();
        _container = _workstation = _refinement = _heart = _character = _prison = _salvage = _unit = default;
        Array.Clear(StationScans, 0, StationScans.Length);
        Array.Clear(StationStates, 0, StationStates.Length);
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
            VanillaUiSite.RecipeTooltip => SampleRecipeTooltip(view, world),
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
        FillRename(view, sub, VanillaPanelKind.Container, sub.OpenTagEditButton, sub.OpenTagEditButtonParent);
        FromInventory(view, _container.Inventory);
        view.Obstacle = _container.InventoryShown ? _container.Inventory!.gameObject : null;
        return Finish(VanillaUiSite.ContainerPanel, view, world, VanillaPanelKind.Container, _container.Target);
    }

    /// <summary>
    /// The rename button and the group <see cref="VanillaPanelAnchor.BesideRename"/> is placed beside, for a window of
    /// <paramref name="kind"/>. <paramref name="parentField"/> (<c>OpenTagEditButtonParent</c>, where the window has
    /// one) is empty in the shipped prefabs (1.1.13.0); the rename button's own parent is the group then
    /// (<see cref="VanillaPanelAnchors.RenameGroup"/>), cached per window. A window without a rename button keeps none,
    /// and the anchor says why (<see cref="VanillaStationWindows.RenameMissing"/>).
    /// </summary>
    private static void FillRename(PanelView view, Component sub, VanillaPanelKind kind, Component? button, Component? parentField)
    {
        view.RenameMissing = VanillaStationWindows.RenameMissing(kind);
        if (!VanillaStationWindows.HasRename(kind)) return;
        view.RenameButton = Alive(button) ? button!.gameObject : null;
        int subId = sub.GetInstanceID();
        if (!RenameGroups.TryGetValue(subId, out var rename) || Stale(rename.Group, rename.Frame))
        {
            if (RenameGroups.Count > 8) RenameGroups.Clear();
            var pick = VanillaPanelAnchors.RenameGroup(TransformNode.Of(parentField), TransformNode.Of(button));
            var group = GoOf(pick.Node);
            rename = (group != null ? group.transform : null, pick.Source, Time.frameCount);
            RenameGroups[subId] = rename;
        }

        view.RenameGroup = Alive(rename.Group) ? rename.Group : null;
        view.RenameSource = rename.Source;
    }

    private static VanillaSiteSample SampleStation(PanelView view, World world)
    {
        // Which window is open is decided game-free (VanillaStationWindows.Pick); this half only
        // reads the chosen window's grid. The target is always that mapper's _Target: the station.
        if (!VanillaStationWindows.TryResolve(StationStates, out int pick, out var picked)) return Miss(view);
        var window = StationScans[pick];
        var kind = picked.Kind;
        RectTransform? grid;
        Component? renameButton = null, renameParent = null;
        switch (kind)
        {
            case VanillaPanelKind.Workstation:
            {
                var sub = window.Sub!.TryCast<WorkstationSubMenu>();
                if (sub == null) return Miss(view);
                grid = RectOf(sub.RecipesParent);
                renameButton = sub.OpenTagEditButton;
                break;
            }
            case VanillaPanelKind.RefinementStation:
            {
                var sub = window.Sub!.TryCast<RefinementstationSubMenu>();
                if (sub == null) return Miss(view);
                grid = RectOf(sub.RecipesParent);
                renameButton = sub.OpenTagEditButton;
                renameParent = sub.OpenTagEditButtonParent;
                break;
            }
            case VanillaPanelKind.PrisonCell:
            case VanillaPanelKind.SalvageStation:
            case VanillaPanelKind.UnitStation:
            {
                RectTransform? found = null;
                bool ok = false;
                Guarded(kind, "grid", () =>
                {
                    found = kind == VanillaPanelKind.PrisonCell ? PrisonReader.Grid(window.Sub!)
                        : kind == VanillaPanelKind.SalvageStation ? SalvageReader.Grid(window.Sub!)
                        : UnitReader.Grid(window.Sub!);
                    ok = true;
                });
                if (!ok) return Miss(view);
                grid = found;

                // The prison cell has no rename button. A missing pencil only withholds BesideRename.
                if (kind != VanillaPanelKind.PrisonCell)
                {
                    try { renameButton = kind == VanillaPanelKind.SalvageStation ? SalvageReader.Rename(window.Sub!) : UnitReader.Rename(window.Sub!); }
                    catch { renameButton = null; /* BesideRename says what is missing; the window stays */ }
                }
                break;
            }
            default:
                return Miss(view);
        }

        view.Reset();
        view.Panel = window.Sub!.gameObject;
        view.Grid = grid;
        FillRename(view, window.Sub!, kind, renameButton, renameParent);
        FromInventory(view, window.Inventory);
        view.Obstacle = window.InventoryShown ? window.Inventory!.gameObject : null;
        return Finish(VanillaUiSite.StationPanel, view, world, kind, window.Target);
    }

    private static VanillaSiteSample SampleCastleHeart(PanelView view, World world)
    {
        if (!_heart.SubShown || _heart.Target == Entity.Null) return Miss(view);
        view.Reset();
        view.Panel = _heart.Sub!.gameObject;
        FillTitleButtons(view, _heart.Sub!);
        FromInventory(view, _heart.Inventory);
        view.Obstacle = _heart.InventoryShown ? _heart.Inventory!.gameObject : null;
        return Finish(VanillaUiSite.CastleHeartPanel, view, world, VanillaPanelKind.CastleHeart, _heart.Target);
    }

    /// <summary>What <see cref="VanillaPanelAnchor.BesideTitleButtons"/> names when the heart window's icon row is not found.</summary>
    private const string TitleButtonsFields = "CastleHeartSubMenu.AllyWindow.LevelPanel.AbandonButton and ExposeButton";

    /// <summary>
    /// The castle heart window's top-right icon row (<see cref="VanillaPanelAnchors.TitleButtonsRow"/>), cached per
    /// window and read again only when a piece dies (or, missing, after <see cref="MissingRetryFrames"/>). The row's
    /// reference button is also the donor of the mod's buttons there when it is clone-safe, so they are the game's own
    /// art and size; the game icon's share of the button and its color are measured once, for the mod's icon faces.
    /// </summary>
    private static void FillTitleButtons(PanelView view, Component sub)
    {
        view.TitleButtonsMissing = TitleButtonsFields;
        int id;
        try { id = sub.GetInstanceID(); }
        catch { return; }
        if (!TitleButtonRows.TryGetValue(id, out var row) || Stale(row.Group, row.Frame) || (row.Group != null && !Alive(row.Reference)))
        {
            if (TitleButtonRows.Count > 8) TitleButtonRows.Clear();
            row = new TitleButtonsCache { Frame = Time.frameCount };
            try
            {
                HeartReader.TitleButtons(sub, out var abandon, out var expose);
                var pick = VanillaPanelAnchors.TitleButtonsRow(new IAnchorNode?[] { TransformNode.Of(abandon), TransformNode.Of(expose) });
                var group = GoOf(pick.Group.Node);
                var reference = GoOf(pick.Reference.Node);
                if (group != null && reference != null)
                {
                    row.Group = group.transform;
                    row.Reference = reference;
                    row.Source = pick.Group.Source;
                    row.Donor = pick.Reference.Node != null && pick.Reference.Node.CloneSafe ? reference : null;
                    MeasureTitleIcon(reference, out row.IconShare, out row.IconColor);
                }
            }
            catch (Exception e)
            {
                // A member the game renamed: only this anchor goes; the window and its other anchors stay.
                if (VanillaSiteHealth.LogOnce(VanillaUiSite.CastleHeartPanel, "title buttons threw"))
                    Plugin.Logger?.LogWarning("Ui: site CastleHeartPanel icon row not read (" + TitleButtonsFields + "): " + e.GetType().Name + ": " + e.Message);
            }

            TitleButtonRows[id] = row;
        }

        if (!Alive(row.Group) || !Alive(row.Reference)) return;
        view.TitleButtons = row.Group;
        view.TitleButtonsRef = row.Reference;
        view.TitleButtonsSource = row.Source;
        view.TitleButtonsDonor = Alive(row.Donor) ? row.Donor : null;
        view.TitleIconShare = row.IconShare;
        view.TitleIconColor = row.IconColor;
        view.TitleButtonsMissing = "";
    }

    /// <summary>The game icon on <paramref name="button"/> (its largest graphic smaller than the button): its share of the button's height and its color.</summary>
    private static void MeasureTitleIcon(GameObject button, out float share, out Color color)
    {
        share = 0f;
        color = Color.white;
        try
        {
            var brt = button.GetComponent<RectTransform>();
            float h = brt != null ? (brt.rect.height > 1f ? brt.rect.height : brt.sizeDelta.y) : 0f;
            if (h <= 1f) return;
            var stun = button.GetComponent<SimpleStunButton>();
            IntPtr target = stun != null && stun.targetGraphic != null ? stun.targetGraphic.Pointer : IntPtr.Zero;
            float best = 0f;
            foreach (var g in button.GetComponentsInChildren<Graphic>(true))
            {
                if (g == null || g.Pointer == target || g.gameObject.Pointer == button.Pointer || g.TryCast<TMP_Text>() != null) continue;
                var r = g.rectTransform.rect;
                float side = Mathf.Max(r.width > 1f ? r.width : g.rectTransform.sizeDelta.x, r.height > 1f ? r.height : g.rectTransform.sizeDelta.y);
                if (side <= best || side >= h * 0.8f) continue;
                best = side;
                color = g.color;
            }

            if (best > 1f) share = best / h;
        }
        catch { share = 0f; }
    }

    /// <summary>The castle heart window's reads that a game update could rename, in their own non-inlined method.</summary>
    private static class HeartReader
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void TitleButtons(Component sub, out Component? abandon, out Component? expose)
        {
            abandon = null;
            expose = null;
            var heart = sub.TryCast<CastleHeartSubMenu>();
            var level = heart != null && heart.AllyWindow != null ? heart.AllyWindow.LevelPanel : null;
            if (level == null) return;
            abandon = level.AbandonButton;
            expose = level.ExposeButton;
        }
    }

    /// <summary>The inventory a window shows right now; the game shows one window at a time.</summary>
    private static bool ActiveInventory(out WindowScan window, out VanillaPanelKind kind)
    {
        if (_container.InventoryShown) { window = _container; kind = VanillaPanelKind.Container; return true; }
        if (_workstation.InventoryShown) { window = _workstation; kind = VanillaPanelKind.Workstation; return true; }
        if (_refinement.InventoryShown) { window = _refinement; kind = VanillaPanelKind.RefinementStation; return true; }
        if (_prison.InventoryShown) { window = _prison; kind = VanillaPanelKind.PrisonCell; return true; }
        if (_salvage.InventoryShown) { window = _salvage; kind = VanillaPanelKind.SalvageStation; return true; }
        if (_unit.InventoryShown) { window = _unit; kind = VanillaPanelKind.UnitStation; return true; }
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

    // ---- the recipe tooltip

    /// <summary>
    /// <see cref="VanillaUiSite.RecipeTooltip"/>: a window's own <c>FakeTooltip</c> shown while one of
    /// its recipe entries is hovered. The windows, in the order they are asked (the game shows one at
    /// a time): the workstation (<c>WorkstationSubMenu.RecipesParent</c>), the refinement station
    /// (<c>RefinementstationSubMenu.RecipesParent</c>) and the character menu's crafting tab
    /// (<c>InventorySubMenu.RecipesParent</c>, no station). Only field reads: the entry's
    /// <c>IsHovered</c> and <c>EntryId</c>, never the entry's <c>Requirements</c> list or the window's
    /// <c>GridSelectionGroup</c> (interop generics over game value types). The product and the
    /// requirements come from the recipe prefab's buffers (<see cref="ReadRecipe"/>), once per hover.
    /// </summary>
    private static VanillaSiteSample SampleRecipeTooltip(PanelView view, World world)
    {
        FakeTooltip? tooltip = null;
        GridSelectionEntry? hovered = null;
        int recipe = 0;
        VanillaPanelKind kind = VanillaPanelKind.None;
        Entity station = Entity.Null;

        if (_workstation.SubShown && _workstation.Target != Entity.Null)
        {
            var sub = _workstation.Sub!.TryCast<WorkstationSubMenu>();
            if (sub != null && Shown(sub.FakeTooltip))
            {
                hovered = HoveredRecipeIn(sub.RecipesParent);
                if (hovered != null)
                {
                    tooltip = sub.FakeTooltip;
                    kind = VanillaPanelKind.Workstation;
                    station = _workstation.Target;
                }
            }
        }

        if (hovered == null && _refinement.SubShown && _refinement.Target != Entity.Null)
        {
            var sub = _refinement.Sub!.TryCast<RefinementstationSubMenu>();
            if (sub != null && Shown(sub.FakeTooltip))
            {
                hovered = HoveredRecipeIn(sub.RecipesParent);
                if (hovered != null)
                {
                    tooltip = sub.FakeTooltip;
                    kind = VanillaPanelKind.RefinementStation;
                    station = _refinement.Target;
                }
            }
        }

        // The crafting tab belongs to the inventory half (InventorySubMenu.RecipesParent), so it is
        // asked of whichever inventory is shown; its recipes are the character's own, never a station's.
        if (hovered == null && ActiveInventory(out var shown, out _))
        {
            var inv = shown.Inventory!;
            if (Shown(inv.FakeTooltip) && Shown(inv.RecipesParent))
            {
                hovered = HoveredRecipeIn(inv.RecipesParent);
                if (hovered != null)
                {
                    tooltip = inv.FakeTooltip;
                    kind = VanillaPanelKind.Character;
                }
            }
        }

        if (hovered == null || tooltip == null) return Miss(view);
        recipe = RecipeIdOf(hovered);
        if (recipe == 0) return Miss(view);

        long entryKey = hovered.Pointer.ToInt64();
        int worldKey = RuntimeHelpers.GetHashCode(world);
        if (!RecipeHover.Holds(entryKey, recipe, worldKey))
        {
            ReadRecipe(world, recipe, out int product, out int amount, out var requirements);
            RecipeHover.Store(entryKey, recipe, worldKey, product, amount, requirements);
        }

        view.Reset();
        view.Panel = tooltip.gameObject;
        view.Tooltip = tooltip;
        view.BodyDonor = TmpOf(tooltip.Description);

        ReadNetworkId(world, station, out int n0, out int n1, out int n2);
        VanillaRecipeTooltip.Subject(kind, recipe, RecipeHover.ProductGuid, RecipeHover.ProductAmount, out int item, out int itemAmount);
        var target = VanillaPanelTarget.ForRecipe(
            kind, station.Index, station.Version, n0, n1, n2,
            recipe, item, itemAmount, RecipeHover.ProductGuid, RecipeHover.ProductAmount, RecipeHover.Requirements);
        return Finish(VanillaUiSite.RecipeTooltip, view, target);
    }

    /// <summary>The one hover the recipe site last read the buffers for.</summary>
    private static readonly VanillaRecipeHoverMemo RecipeHover = new();

    /// <summary>
    /// The refinement window's recipe tooltip, for the material counts
    /// (<c>MaterialOperation.RefineView</c>): its <c>FakeTooltip</c> while shown over a hovered recipe row
    /// (the same hover rule as <see cref="VanillaUiSite.RecipeTooltip"/>: the entry's <c>IsHovered</c>),
    /// the station the window shows, a key for the hovered entry, and the recipe's requirement rows (read
    /// once per hover through <see cref="ReadRecipe"/>). False when the window, its tooltip or a hovered
    /// recipe is missing. Field reads only; uses this frame's shared window scan.
    /// </summary>
    internal static bool TryRefinementRecipeTooltip(World world, out FakeTooltip? tooltip, out Entity station, out long entryKey,
        out int recipe, out VanillaRecipeRequirement[]? requirements)
    {
        tooltip = null;
        station = Entity.Null;
        entryKey = 0;
        recipe = 0;
        requirements = null;
        try
        {
            EnsureScan(world);
            if (!_refinement.SubShown || _refinement.Target == Entity.Null) return false;
            var sub = _refinement.Sub!.TryCast<RefinementstationSubMenu>();
            if (sub == null || !Shown(sub.FakeTooltip)) return false;
            var hovered = HoveredRecipeIn(sub.RecipesParent);
            if (hovered == null) return false;
            recipe = RecipeIdOf(hovered);
            if (recipe == 0) return false;
            entryKey = hovered.Pointer.ToInt64();
            int worldKey = RuntimeHelpers.GetHashCode(world);
            if (!RecipeHover.Holds(entryKey, recipe, worldKey))
            {
                ReadRecipe(world, recipe, out int product, out int amount, out var rows);
                RecipeHover.Store(entryKey, recipe, worldKey, product, amount, rows);
            }
            requirements = RecipeHover.Requirements;
            tooltip = sub.FakeTooltip;
            station = _refinement.Target;
            return true;
        }
        catch
        {
            tooltip = null;
            return false;
        }
    }

    /// <summary>The recipe an entry shows: <c>EntryId</c> of a workstation or crafting-tab entry, or of a refinement row.</summary>
    private static int RecipeIdOf(GridSelectionEntry entry)
    {
        try
        {
            var w = entry.TryCast<WorkstationRecipeGridSelectionEntry>();
            if (w != null) return w.EntryId.GuidHash;
            var r = entry.TryCast<RefinementstationRecipeEntry>();
            if (r != null) return r.EntryId.GuidHash;
        }
        catch { /* an entry Unity destroyed this frame reads as no recipe */ }
        return 0;
    }

    /// <summary>
    /// The product (first output row with an item) and the requirement rows of
    /// <paramref name="recipeGuid"/>'s prefab, through the raw buffer reader the station API already
    /// uses (<c>AiRuntime.Buffer</c>, strides from <c>RecipeBufferLayout</c>). All zero / null when
    /// the prefab or a buffer is missing.
    /// </summary>
    private static unsafe void ReadRecipe(World world, int recipeGuid, out int product, out int amount, out VanillaRecipeRequirement[]? requirements)
    {
        product = 0;
        amount = 0;
        requirements = null;
        try
        {
            if (!PrefabLookup.TryGetEntity(world, new Stunlock.Core.PrefabGUID(recipeGuid), out var prefab)) return;
            var em = world.EntityManager;
            var rows = new List<VanillaRecipeRequirement>();

            byte* outRaw = Progenitor.Ai.AiRuntime.Buffer<ProjectM.RecipeOutputBuffer>(em, prefab, Progenitor.Content.RecipeBufferLayout.OutputStride, false, out int outLength);
            for (int i = 0; outRaw != null && i < outLength && i < Progenitor.Content.RecipeBufferLayout.MaxElements; i++)
            {
                Progenitor.Content.RecipeBufferLayout.TryReadOutput(outRaw, i, out int g, out int a);
                rows.Add(new VanillaRecipeRequirement(g, a));
            }

            VanillaRecipeTooltip.TryProduct(rows, out product, out amount);

            rows.Clear();
            byte* req = Progenitor.Ai.AiRuntime.Buffer<ProjectM.RecipeRequirementBuffer>(em, prefab, Progenitor.Content.RecipeBufferLayout.RequirementStride, false, out int reqLength);
            for (int i = 0; req != null && i < reqLength && i < Progenitor.Content.RecipeBufferLayout.MaxElements; i++)
            {
                Progenitor.Content.RecipeBufferLayout.TryReadRequirement(req, i, out int g, out int a);
                rows.Add(new VanillaRecipeRequirement(g, a));
            }

            requirements = VanillaRecipeTooltip.Requirements(rows);
        }
        catch (Exception e)
        {
            // The target still names the recipe; only its product and requirements are missing.
            Degraded(VanillaUiSite.RecipeTooltip, "recipe buffers: " + e.GetType().Name);
        }
    }

    /// <summary>The requirement block of a recipe tooltip (<c>FakeTooltip.RecipeRequiredItems</c>), or null.</summary>
    private static Transform? RecipeBlockOf(FakeTooltip? tooltip)
    {
        try
        {
            if (!Alive(tooltip)) return null;
            var block = tooltip!.RecipeRequiredItems;
            return Alive(block) ? block.transform : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// A tooltip container's place for <c>ui_read panels</c>: <c>host</c> (its parent), <c>index</c> (its sibling index),
    /// <c>after</c> and <c>before</c> (the game's entries on either side, Progenitor's containers skipped) and, for
    /// <see cref="VanillaPanelAnchor.TooltipHeader"/>, <c>inset</c> (the measured title inset, -1 before the first fit).
    /// </summary>
    private static void DescribeTooltipSlot(Dictionary<string, object?> d, PanelContainerWidget tip)
    {
        try
        {
            var t = tip.Transform;
            var parent = t != null ? t.parent : null;
            if (t == null || parent == null) return;
            int index = t.GetSiblingIndex();
            VanillaRecipeTooltip.Neighbors(index, parent.childCount, i =>
            {
                var c = parent.GetChild(i);
                return c != null ? SafeObjectName(c.gameObject) : "";
            }, out string after, out string before);
            var slot = new Dictionary<string, object?>
            {
                ["host"] = SafeObjectName(parent.gameObject),
                ["index"] = index,
                ["after"] = after,
                ["before"] = before,
            };
            if (tip.Anchor == VanillaPanelAnchor.TooltipHeader) slot["inset"] = Mathf.Round(tip.HeaderInset * 10f) / 10f;
            d["tooltipSlot"] = slot;
        }
        catch { /* */ }
    }

    /// <summary>The tooltip's title band (<c>FakeTooltip.TooltipHeader</c>, the icon and name), or null.</summary>
    private static Transform? HeaderOf(FakeTooltip? tooltip)
    {
        try
        {
            if (!Alive(tooltip)) return null;
            var header = tooltip!.TooltipHeader;
            return Alive(header) ? header.transform : null;
        }
        catch { return null; }
    }

    /// <summary>The title text of the tooltip's title band (<c>TooltipHeader.Header</c>), or null.</summary>
    private static TextMeshProUGUI? HeaderTitleOf(FakeTooltip? tooltip)
    {
        try
        {
            if (!Alive(tooltip)) return null;
            var header = tooltip!.TooltipHeader;
            if (!Alive(header) || !Alive(header.Header)) return null;
            var title = header.Header.Text;
            return Alive(title) ? title : null;
        }
        catch { return null; }
    }

    /// <summary>The title band's own left padding (its layout group's), the header rows' first inset; 0 when unreadable.</summary>
    private static float HeaderPadding(FakeTooltip? tooltip)
    {
        try
        {
            var header = HeaderOf(tooltip);
            var group = header != null ? header.GetComponent<LayoutGroup>() : null;
            return group != null && group.padding != null ? Mathf.Max(0, group.padding.left) : 0f;
        }
        catch { return 0f; }
    }

    /// <summary>Retained for <c>GetWorldCorners</c>: a managed array would be copied in and never read back.</summary>
    private static Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Vector3>? _headerCorners;

    /// <summary>Give <paramref name="column"/> the same left and right padding (written back through the setter, which marks the group dirty).</summary>
    private static void SetSidePadding(VerticalLayoutGroup column, int px)
    {
        var pad = column.padding;
        if (pad == null) return;
        pad.left = px;
        pad.right = px;
        column.padding = pad;
    }

    /// <summary>
    /// Start a header container's rows where the title's text starts, as the build menu's breadcrumb does: the
    /// distance from the container's left edge to the title's, in world space, over the container's scale
    /// (<see cref="VanillaRecipeTooltip.TryHeaderInset"/>). Written as the container's left and right padding,
    /// only when it moved by more than half a unit, so a steady tooltip is never laid out again.
    /// </summary>
    private static void AlignHeader(PanelContainerWidget host, PanelView view)
    {
        try
        {
            var title = HeaderTitleOf(view.Tooltip);
            var rt = host.Rect;
            if (title == null || rt == null || !Alive(host.GameObject)) return;
            var column = host.GameObject!.GetComponent<VerticalLayoutGroup>();
            if (column == null) return;
            _headerCorners ??= new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Vector3>(4);
            title.rectTransform.GetWorldCorners(_headerCorners);
            float titleLeft = _headerCorners[0].x;
            rt.GetWorldCorners(_headerCorners);
            float rowLeft = _headerCorners[0].x;
            if (!VanillaRecipeTooltip.TryHeaderInset(titleLeft, rowLeft, rt.lossyScale.x, out float inset)) return;
            host.HeaderInset = inset;
            var pad = column.padding;
            if (pad != null && Mathf.Abs(pad.left - inset) <= 0.5f) return;
            SetSidePadding(column, Mathf.RoundToInt(inset));
            LayoutRebuilder.MarkLayoutForRebuild(rt);
        }
        catch { /* the rows keep their last inset */ }
    }

    /// <summary>
    /// The transform of requirement line <paramref name="line"/>'s own row
    /// (<c>TooltipRecipeRequiredItems.RequiredItemsList[line]</c>, a <c>WorkstationRequirementEntry</c>), or null.
    /// </summary>
    private static Transform? RequirementRowOf(FakeTooltip? tooltip, int line)
    {
        try
        {
            if (line < 0 || !Alive(tooltip)) return null;
            var block = tooltip!.RecipeRequiredItems;
            if (!Alive(block)) return null;
            var list = block.RequiredItemsList;
            if (list == null || line >= list.Count) return null;
            var entry = list[line];
            return Alive(entry) ? entry.transform : null;
        }
        catch { return null; }
    }

    /// <summary>The live per-line containers (<see cref="VanillaPanelAnchor.TooltipRequirementLine"/>).</summary>
    private static readonly List<PanelContainerWidget> LineContainers = new();

    private static void TrackLineContainer(PanelContainerWidget widget)
    {
        LineContainers.RemoveAll(w => !w.Alive);
        LineContainers.Add(widget);
    }

    /// <summary>
    /// Show each per-line container exactly while the game shows its line. Run after every window mapper's
    /// update (the recipe tooltip site's watch hooks all of them), so the one that just rewrote the tooltip
    /// is followed by a sync in the same frame, before the canvas draws: a recipe with fewer lines never
    /// shows a stale container for a frame.
    /// </summary>
    private static void SyncRequirementLines()
    {
        if (LineContainers.Count == 0) return;
        LineMatchScratch.Clear();
        for (int i = LineContainers.Count - 1; i >= 0; i--)
        {
            var w = LineContainers[i];
            if (!w.Alive) { LineContainers.RemoveAt(i); continue; }
            w.SyncLine(LinesMatchFor(w.Block));
        }
        LineMatchScratch.Clear();
    }

    private static readonly Dictionary<IntPtr, bool> LineMatchScratch = new();

    /// <summary>
    /// Whether the game's shown requirement lines are the hovered recipe's requirements one for one: as many
    /// shown line rows as <see cref="VanillaPanelTarget.Requirements"/> has entries. The game leaves out a
    /// requirement it has no item data for, which would shift every later line; then no per-line container is
    /// shown for that recipe, so rows never sit under the wrong item. Read once per requirement list per sync.
    /// </summary>
    private static bool LinesMatchFor(TooltipRecipeRequiredItems? block)
    {
        try
        {
            if (!Alive(block)) return false;
            if (LineMatchScratch.TryGetValue(block!.Pointer, out bool known)) return known;
            int shown = 0;
            var list = block.RequiredItemsList;
            int n = list == null ? 0 : list.Count;
            for (int i = 0; i < n; i++)
            {
                var entry = list![i];
                if (Alive(entry) && entry.gameObject.activeSelf) shown++;
            }
            bool match = VanillaUiSites.RequirementLinesMatch(shown, RecipeHover.Requirements?.Length ?? 0);
            LineMatchScratch[block.Pointer] = match;
            return match;
        }
        catch { return false; }
    }

    /// <summary>The slot at the end of <paramref name="column"/> but before its footer containers, or -1 for the end.</summary>
    private static int TooltipSlotBeforeFooters(Transform column)
    {
        try
        {
            return VanillaRecipeTooltip.SlotBeforeFooters(column.childCount, i =>
            {
                var c = column.GetChild(i);
                return c != null ? SafeObjectName(c.gameObject) : "";
            });
        }
        catch { return -1; }
    }

    /// <summary>The sibling index right after <paramref name="reference"/> and any Progenitor container already there.</summary>
    private static int TooltipSlotAfter(Transform reference)
    {
        try
        {
            var parent = reference.parent;
            if (parent == null) return -1;
            return VanillaRecipeTooltip.SlotAfter(reference.GetSiblingIndex(), parent.childCount, i =>
            {
                var c = parent.GetChild(i);
                return c != null ? SafeObjectName(c.gameObject) : "";
            });
        }
        catch { return -1; }
    }

    // ---- hovered recipe entry: cached per grid, rebuilt only when the grid's child count changes

    private sealed class RecipeGridCache
    {
        internal int ChildCount = -1;
        internal readonly List<GridSelectionEntry> Entries = new();
    }

    private static readonly Dictionary<IntPtr, RecipeGridCache> RecipeGridCaches = new();
    private static GridSelectionEntry? _hoveredRecipe;

    /// <summary>
    /// The hovered recipe entry among <paramref name="grid"/>'s children: a
    /// <c>WorkstationRecipeGridSelectionEntry</c> or a <c>RefinementstationRecipeEntry</c> (both
    /// <c>GridSelectionEntry</c>, whose <c>IsHovered</c> is a plain field read).
    /// </summary>
    private static GridSelectionEntry? HoveredRecipeIn(Component? grid)
    {
        if (!Alive(grid)) return null;

        try
        {
            if (Alive(_hoveredRecipe) && _hoveredRecipe!.IsHovered && _hoveredRecipe.transform.parent != null
                && _hoveredRecipe.transform.parent.Pointer == grid!.transform.Pointer
                && _hoveredRecipe.gameObject.activeInHierarchy)
                return _hoveredRecipe;
        }
        catch { _hoveredRecipe = null; }

        var t = grid!.transform;
        if (!RecipeGridCaches.TryGetValue(t.Pointer, out var cache))
        {
            if (RecipeGridCaches.Count > 16) RecipeGridCaches.Clear();
            cache = new RecipeGridCache();
            RecipeGridCaches[t.Pointer] = cache;
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
                var entry = child.GetComponent<GridSelectionEntry>();
                if (Alive(entry)) cache.Entries.Add(entry);
            }
        }

        // The frame this runs on most is "tooltip shown over something that is not a recipe" (a station
        // bonus, an output slot): every entry is read and none is hovered. So the per-entry cost is the
        // one field read (IsHovered, pointer arithmetic on the entry's own object), and the Unity
        // liveness check is paid only by the hovered entry. A hovered state can change on any pointer
        // move (OnPointerEnter/Exit, OnDisable) and nothing cheaper than this read observes it, so a
        // "none hovered" answer is not cached across frames.
        for (int i = 0; i < cache.Entries.Count; i++)
        {
            var entry = cache.Entries[i];
            bool hovered;
            try { hovered = entry.IsHovered; }
            catch
            {
                cache.ChildCount = -1;
                continue;
            }

            if (!hovered) continue;
            if (!Alive(entry))
            {
                cache.ChildCount = -1;
                continue;
            }

            if (entry.gameObject.activeInHierarchy)
            {
                _hoveredRecipe = entry;
                return entry;
            }
        }

        return null;
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
        // A remote container preview's window: the previewed chest, its network id and its stable id.
        if (kind == VanillaPanelKind.Container) panelTarget = VanillaPanelPreview.Apply(panelTarget, Progenitor.Inventory.ContainerPreviewClient.PreviewWindow(world), emptyIsWindow: site != VanillaUiSite.InventoryTooltip);
        if (site == VanillaUiSite.ContainerPanel) { LastContainerPanelTarget = panelTarget; LastContainerPanelFrame = Time.frameCount; }
        return Finish(site, view, panelTarget);
    }

    /// <summary>The container panel site's most recent target (the Dev bridge's container preview readback), and its frame.</summary>
    internal static VanillaPanelTarget LastContainerPanelTarget { get; private set; }

    internal static int LastContainerPanelFrame { get; private set; } = -1;

    /// <summary>The common tail for a target that is already built (the recipe tooltip's).</summary>
    private static VanillaSiteSample Finish(VanillaUiSite site, PanelView view, in VanillaPanelTarget panelTarget)
    {
        // A missing donor withholds what needs it; it never makes the site absent. The tooltip line
        // needs the body text; a panel's buttons need the bar button and a caption style. The recipe
        // tooltip's requirement anchor needs the requirement block too, but only that anchor: its
        // container build says so (BuildPanelContainer), and TooltipBody keeps working.
        bool donor;
        if (VanillaUiSites.IsTooltip(site))
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

    /// <summary>A rect's laid-out size, else its authored size (zero when unknown).</summary>
    private static Vector2 SizeOf(GameObject? go)
    {
        try
        {
            var rt = Rt(go);
            if (rt == null) return Vector2.zero;
            var r = rt.rect;
            return new Vector2(r.width > 1f ? r.width : rt.sizeDelta.x, r.height > 1f ? r.height : rt.sizeDelta.y);
        }
        catch { return Vector2.zero; }
    }

    /// <summary>The field the buttons on <paramref name="anchor"/> are cloned from: the heart row's own button there, else the bar donor.</summary>
    private static string DonorSourceFor(PanelView view, VanillaPanelAnchor anchor) =>
        anchor == VanillaPanelAnchor.BesideTitleButtons && Alive(view.TitleButtonsDonor)
            ? "CastleHeartLevelPanel." + SafeObjectName(view.TitleButtonsDonor)
            : view.DonorSource;

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

    public IRuntimeWidget? BuildPanelContainer(VanillaUiSite site, VanillaPanelAnchor anchor, string containerName) =>
        BuildPanelContainer(site, anchor, containerName, -1);

    public IRuntimeWidget? BuildPanelContainer(VanillaUiSite site, VanillaPanelAnchor anchor, string containerName, int line)
    {
        if (!PanelViews.TryGetValue(site, out var view) || view == null || !view.Live || !Alive(view.Panel)) return null;
        GameObject? go = null;
        try
        {
            Transform? host;
            GameObject? reference = null;
            GameObject? lineRow = null;
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
                    if (host == null) missing = view.RenameMissing.Length > 0 ? view.RenameMissing : "the window's rename button";
                    break;
                case VanillaPanelAnchor.BesideSort:
                    reference = Alive(view.Sort) ? view.Sort : null;
                    host = reference != null ? reference.transform.parent : null;
                    if (host == null) missing = "InventorySubMenu.SelfSortButton, AllSortButton and a sort button on BottomBarButtonsParent";
                    break;
                case VanillaPanelAnchor.BesideTitleButtons:
                    // The window's own icon row (the castle heart's top-right buttons): placed left of the game's first
                    // button, never joined (VanillaPanelAnchors.PlanTitleButtons).
                    host = Alive(view.TitleButtons) ? view.TitleButtons : null;
                    reference = Alive(view.TitleButtonsRef) ? view.TitleButtonsRef : null;
                    if (host == null || reference == null)
                    {
                        host = null;
                        missing = view.TitleButtonsMissing.Length > 0 ? view.TitleButtonsMissing : "the window's icon-button row";
                    }
                    break;
                case VanillaPanelAnchor.TooltipBody:
                    var frame = view.Tooltip != null ? view.Tooltip.DescriptionFrame : null;
                    host = Alive(frame) ? frame!.transform.parent : null;
                    if (host == null) missing = "FakeTooltip.DescriptionFrame";
                    // The item tooltip's line is the column's last; the recipe tooltip's rows go right
                    // under the product's description, above the requirement list (both are
                    // InformationEntries children on 1.1.13.0).
                    if (host != null && site == VanillaUiSite.RecipeTooltip) siblingIndex = TooltipSlotAfter(frame!.transform);
                    else if (host != null) siblingIndex = TooltipSlotBeforeFooters(host);
                    break;
                case VanillaPanelAnchor.TooltipFooter:
                    // The text column's very last row (InformationEntries on 1.1.13.0): after the game's last entry
                    // (InputContexts) and after every Progenitor container already there, so footers stack in build
                    // order and an item tooltip's body rows, built later, still go above them.
                    var footFrame = view.Tooltip != null ? view.Tooltip.DescriptionFrame : null;
                    host = Alive(footFrame) ? footFrame!.transform.parent : null;
                    if (host == null) missing = "FakeTooltip.DescriptionFrame";
                    break;
                case VanillaPanelAnchor.TooltipRequirements:
                    var block = RecipeBlockOf(view.Tooltip);
                    host = block != null ? block.parent : null;
                    if (host == null) missing = "FakeTooltip.RecipeRequiredItems";
                    else siblingIndex = TooltipSlotAfter(block!);
                    break;
                case VanillaPanelAnchor.TooltipHeader:
                    // The tooltip's own next row after its title band (TooltipHeader), in the tooltip root's
                    // vertical group: the place the build menu's breadcrumb line takes in the same FakeTooltip
                    // shape. Above Tooltip_ReforgeCost and InformationEntries, so above the description.
                    var headerT = HeaderOf(view.Tooltip);
                    host = headerT != null ? headerT.parent : null;
                    if (host == null) missing = "FakeTooltip.TooltipHeader";
                    else siblingIndex = TooltipSlotAfter(headerT!);
                    break;
                case VanillaPanelAnchor.TooltipRequirementLine:
                    // Inside the requirement list (a vertical group of the game's pooled line rows),
                    // right after this line's own row and any Progenitor container already there. The
                    // game's rewrite of the lines only writes each row's texts and active state, so the
                    // container keeps its place.
                    var lineT = RequirementRowOf(view.Tooltip, line);
                    host = lineT != null ? lineT.parent : null;
                    if (host == null) missing = "requirement line " + line + " (FakeTooltip.RecipeRequiredItems.RequiredItemsList)";
                    else
                    {
                        siblingIndex = TooltipSlotAfter(lineT!);
                        lineRow = lineT!.gameObject;
                    }
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
            var mode = VanillaUiSites.IsEdge(anchor) || VanillaUiSites.IsTooltipAnchor(anchor)
                ? InlineMode.None
                : anchor == VanillaPanelAnchor.BesideTitleButtons
                    ? InlineMode.Positioned
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

            // A placed title-row container goes after anything the window draws over its title row with a raycast
            // target (the devourer's and the workstation's MotionRoot/Blocker), or no click or hover ever reaches it.
            string coverNote = "";
            if (mode == InlineMode.Positioned && VanillaUiSites.IsTitleRow(anchor) && Alive(view.Panel))
            {
                var above = TitleRowHost(parentT, view.Panel!.transform, reference, group, out int aboveIndex, out coverNote);
                if (above != null)
                {
                    parentT = above;
                    siblingIndex = aboveIndex;
                }
            }

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
                + " mode=" + (VanillaUiSites.IsEdge(anchor) ? "column" : VanillaUiSites.IsTooltipAnchor(anchor) ? "tooltip" : VanillaPanelAnchors.ModeWord(mode, VanillaUiSites.PlacesBefore(anchor)))
                + (anchor == VanillaPanelAnchor.BesideSort ? " sort=" + view.SortSource : "")
                + (anchor == VanillaPanelAnchor.BesideRename ? " via=" + view.RenameSource : "")
                + (anchor == VanillaPanelAnchor.BesideTitleButtons ? " via=" + view.TitleButtonsSource + " ref=" + SafeObjectName(reference) : "")
                + (coverNote.Length > 0 ? " above=" + coverNote : "")
                + (VanillaUiSites.IsTooltipAnchor(anchor) ? "" : " donor=" + DonorSourceFor(view, anchor)),
                VanillaUiSites.IsTooltipAnchor(anchor) ? "" : " button=" + Mathf.RoundToInt(size.x) + "x" + Mathf.RoundToInt(size.y));
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
            else if (VanillaUiSites.IsTooltipAnchor(anchor))
            {
                var column = go.AddComponent<VerticalLayoutGroup>();
                column.childAlignment = TextAnchor.UpperLeft;
                column.childControlWidth = true;
                column.childControlHeight = true;
                column.childForceExpandWidth = true;
                column.childForceExpandHeight = false;
                column.spacing = 0f;

                var element = go.AddComponent<LayoutElement>();
                if (anchor == VanillaPanelAnchor.TooltipHeader)
                {
                    // A row of the tooltip root, as wide as the title band (the root's own width: it fills it
                    // and never asks for more), its rows inset to where the title's text starts. The first
                    // guess is the title band's own left padding, so the first frame is already in place; the
                    // fit measures the title and corrects it (AlignHeader).
                    int inset = Mathf.RoundToInt(HeaderPadding(view.Tooltip));
                    SetSidePadding(column, inset);
                    element.flexibleWidth = 1f;
                }
                else
                {
                    // The line never widens the tooltip: it wraps inside the description's width.
                    try
                    {
                        var descRt = view.BodyDonor != null ? view.BodyDonor.rectTransform : null;
                        if (descRt != null) tooltipWidth = descRt.rect.width;
                    }
                    catch { /* */ }
                    if (tooltipWidth > 1f) element.preferredWidth = tooltipWidth;
                    element.flexibleWidth = 0f;
                }
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
            var widget = new PanelContainerWidget(go, site, anchor, hostLaid, mode, size, group);
            if (VanillaUiSites.Stacks(anchor)) StackedContainers.Add(widget);
            if (lineRow != null)
            {
                widget.GateOn(lineRow, view.Tooltip != null ? view.Tooltip.RecipeRequiredItems : null);
                TrackLineContainer(widget);
            }
            return widget;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: attach " + VanillaUiSites.Key(site) + " container build threw: " + e.Message);
            TryDestroy(go, immediate: true);
            return null;
        }
    }

    /// <summary>
    /// The parent and sibling index that put a placed title-row container above every later sibling that covers the
    /// rename button with a raycast target (<see cref="VanillaPanelAnchors.AboveCovers"/>), walking from
    /// <paramref name="groupParent"/> out to <paramref name="panel"/>. Null when nothing covers the row: the container
    /// stays the last child of the group's parent. <paramref name="note"/> names what it went above (<c>MotionRoot/Blocker</c>).
    /// </summary>
    private static Transform? TitleRowHost(Transform groupParent, Transform panel, GameObject? reference, Transform group, out int index, out string note)
    {
        index = -1;
        note = "";
        try
        {
            var refRt = reference != null ? reference.GetComponent<RectTransform>() : group.TryCast<RectTransform>();
            var target = RectIn(panel, refRt);
            if (target.IsEmpty) target = RectIn(panel, group.TryCast<RectTransform>());
            if (target.IsEmpty) return null;

            var levels = new List<CoverLevel>();
            var parents = new List<Transform>();
            var names = new List<string>();
            Transform chain = group;
            var level = groupParent;
            for (int depth = 0; level != null && depth < 12; depth++)
            {
                // The chain child at this level: the group itself at the first level, else the ancestor below.
                while (chain != null && chain.parent != null && chain.parent.Pointer != level.Pointer) chain = chain.parent;
                if (chain == null || chain.parent == null) break;
                int chainIndex = chain.GetSiblingIndex();
                List<int>? covers = null;
                for (int i = chainIndex + 1; i < level.childCount; i++)
                {
                    var sib = level.GetChild(i);
                    if (sib == null || !sib.gameObject.activeInHierarchy || SafeObjectName(sib.gameObject).StartsWith(UiOwnership.Prefix, StringComparison.OrdinalIgnoreCase)) continue;
                    string? hit = CoveringGraphic(sib, panel, target);
                    if (hit == null) continue;
                    (covers ??= new List<int>()).Add(i);
                    names.Add(SafeObjectName(level.gameObject) + "/" + hit);
                }

                levels.Add(new CoverLevel(chainIndex, covers, SafeObjectName(level.gameObject)));
                parents.Add(level);
                if (level.Pointer == panel.Pointer) break;
                chain = level;
                level = level.parent;
            }

            var pick = VanillaPanelAnchors.AboveCovers(levels);
            if (pick.Level < 0) return null;
            index = pick.SiblingIndex;
            note = names.Count > 0 ? string.Join(",", names) : SafeObjectName(parents[pick.Level].gameObject);
            return parents[pick.Level];
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: attach title-row cover check threw: " + e.Message);
            return null;
        }
    }

    /// <summary>
    /// Move a placed title-row container above a cover the window turned on after it was built (<see cref="TitleRowHost"/>).
    /// True when it moved (the caller places it again in its new parent's units). One line per move.
    /// </summary>
    private static bool ReparentAboveCovers(PanelContainerWidget host, PanelView view, Transform group)
    {
        try
        {
            var rt = host.Rect;
            if (rt == null || group.parent == null || !Alive(view.Panel)) return false;
            var parent = TitleRowHost(group.parent, view.Panel!.transform, view.RenameButton, group, out int index, out string note) ?? group.parent;
            if (rt.parent != null && rt.parent.Pointer == parent.Pointer)
            {
                // Same parent: only the order may need fixing (a cover moved after us).
                if (index >= 0 && rt.GetSiblingIndex() < index - 1) rt.SetSiblingIndex(Mathf.Min(index - 1, parent.childCount - 1));
                else return false;
            }
            else
            {
                rt.SetParent(parent, worldPositionStays: false);
                if (index >= 0) rt.SetSiblingIndex(Mathf.Min(index, parent.childCount - 1));
                else rt.SetAsLastSibling();
                var prt = parent.TryCast<RectTransform>();
                var pivot = prt != null ? prt.pivot : new Vector2(0.5f, 0.5f);
                rt.anchorMin = pivot;
                rt.anchorMax = pivot;
                rt.pivot = Vector2.zero;
                rt.localScale = Vector3.one;
            }

            Plugin.Logger?.LogInfo("Ui: attach " + VanillaUiSites.Key(host.Site) + " anchor=" + VanillaUiSites.AnchorKey(host.Anchor)
                + " moved above " + (note.Length > 0 ? note : "nothing (cover gone)") + " parent=" + SafeObjectName(parent.gameObject));
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: attach title-row reparent threw: " + e.Message);
            return false;
        }
    }

    /// <summary>The path below <paramref name="sib"/> of an active raycast-target graphic that overlaps <paramref name="target"/>, or null.</summary>
    private static string? CoveringGraphic(Transform sib, Transform panel, UiRect target)
    {
        foreach (var g in sib.GetComponentsInChildren<Graphic>(false))
        {
            try
            {
                if (g == null || !g.enabled || !g.raycastTarget || !g.gameObject.activeInHierarchy) continue;
                if (!RaycastsReach(g.transform, sib)) continue;
                if (!RectIn(panel, g.rectTransform).Overlaps(target)) continue;
                return g.transform == sib ? SafeObjectName(sib.gameObject) : SafeObjectName(sib.gameObject) + "/" + SafeObjectName(g.gameObject);
            }
            catch { /* an unreadable graphic is not a cover */ }
        }

        return null;
    }

    /// <summary>No <c>CanvasGroup</c> from <paramref name="t"/> up to <paramref name="top"/> turns raycasts off.</summary>
    private static bool RaycastsReach(Transform t, Transform top)
    {
        for (var a = t; a != null; a = a.parent)
        {
            var cg = a.GetComponent<CanvasGroup>();
            if (cg != null && cg.enabled && !cg.blocksRaycasts) return false;
            if (a.Pointer == top.Pointer) break;
        }

        return true;
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
        if (anchor == VanillaPanelAnchor.BesideTitleButtons)
        {
            // The row's own buttons' size (42 x 42 on 1.1.13.0): the reference's height, not capped by the bar donor's 36.
            float rh = 0f;
            try
            {
                var rrt = Rt(reference);
                if (rrt != null)
                {
                    rh = rrt.rect.height;
                    if (rh <= 1f) rh = rrt.sizeDelta.y;
                }
            }
            catch { /* */ }
            float th = rh > 1f ? Math.Max(VanillaPanelAnchors.MinPositionedHeight, rh) : donor.y;
            var own = Alive(view.TitleButtonsDonor) ? SizeOf(view.TitleButtonsDonor) : Vector2.zero;
            return own.x > 1f && own.y > 1f
                ? new Vector2(VanillaPanelAnchors.ScaledWidth(own.x, own.y, th), th)
                : new Vector2(VanillaPanelAnchors.ScaledWidth(donor.x, donor.y, th), th);
        }

        if (VanillaUiSites.IsEdge(anchor) || VanillaUiSites.IsTooltipAnchor(anchor)) return donor;
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
    private static readonly Dictionary<string, string> PlacementSaid = new(StringComparer.Ordinal);

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
        ClonePanelButton(site, container, caption, VanillaButtonIcon.Unchosen);

    public IRuntimeWidget? ClonePanelButton(VanillaUiSite site, IRuntimeWidget container, string caption, VanillaButtonIcon icon)
    {
        if (container is not PanelContainerWidget host || !host.Alive) return null;
        if (VanillaUiSites.IsTooltipAnchor(host.Anchor)) return null;
        if (!PanelViews.TryGetValue(site, out var view) || view == null || !view.Live) return null;
        var donor = view.ButtonDonor;
        // On the heart's icon row the row's own button is the donor (its art and size), the bar button the fallback.
        var own = host.Anchor == VanillaPanelAnchor.BesideTitleButtons && Alive(view.TitleButtonsDonor) ? view.TitleButtonsDonor : null;
        if (!Alive(donor) && own == null) return ButtonFailed(site, "no button donor");
        if (!Alive(view.CaptionDonor)) return ButtonFailed(site, "no caption donor");

        GameObject? clone = null;
        try
        {
            if (own != null) clone = CloneButton(own, host.GameObject!);
            if (clone == null && Alive(donor)) clone = CloneButton(donor!, host.GameObject!);
            else if (clone == null) donor = own;
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
            // hidden, so the switch is a SetActive and never a clone. On a title-row anchor a button
            // given an icon is that face for good (VanillaUiSites.IconAlways), a square the row's height.
            string faceSource = "";
            bool ownRow = host.Anchor == VanillaPanelAnchor.BesideTitleButtons;
            float share = ownRow && view.TitleIconShare > 0.2f && view.TitleIconShare < 0.9f ? view.TitleIconShare : ownRow ? TitleIconFaceShare : IconFaceShare;
            Color? tint = ownRow ? view.TitleIconColor : null;
            GameObject? face = VanillaUiSites.HasIconFace(host.Anchor, icon) ? AddIconFace(site, clone, label, icon, size, out faceSource, share, tint) : null;
            bool always = face != null && VanillaUiSites.IconAlways(host.Anchor, icon);
            var widget = new PanelButtonWidget(clone, label, stun.onClick, size, face, this) { Site = site, IconAlways = always, FaceSource = faceSource };
            var element = clone.GetComponent<LayoutElement>() ?? clone.AddComponent<LayoutElement>();
            element.ignoreLayout = false;
            element.preferredHeight = size.y;
            element.minHeight = size.y;
            widget.Element = element;
            widget.Refit();
            if (always) widget.SetIconMode(true);

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

    public void SetPanelButtonTooltip(IRuntimeWidget button, string title, string body, bool set)
    {
        if (button is PanelButtonWidget pb) pb.SetTooltip(set ? new VanillaButtonTooltip(title, body) : null);
    }

    /// <summary>Icon face size as a share of the button's height (24 on the 36-unit bar).</summary>
    private const float IconFaceShare = 0.67f;

    /// <summary>
    /// The icon face's share on <see cref="VanillaPanelAnchor.BesideTitleButtons"/> when the game's own icon could not be
    /// measured: the heart row's 24-unit icons on its 42-unit buttons.
    /// </summary>
    private const float TitleIconFaceShare = 24f / 42f;

    /// <summary>
    /// The icon an icon-mode button shows: the named loaded game sprite for <see cref="VanillaButtonIcon.Sprite"/>,
    /// the item's own game icon (<c>ManagedItemData.Icon</c>) for <see cref="VanillaButtonIcon.Item"/>, else the
    /// framework's drawn mark, tinted with the caption's color. Built hidden; the caption's object is the one that
    /// hides when it shows. <paramref name="source"/> says which it was (<c>sprite:</c>, <c>item:</c> or <c>mark:</c>
    /// and the name), for the readback.
    /// </summary>
    private static GameObject? AddIconFace(VanillaUiSite site, GameObject clone, TextMeshProUGUI caption, VanillaButtonIcon icon, Vector2 size, out string source,
        float share = IconFaceShare, Color? markTint = null)
    {
        GameObject? go = null;
        source = "";
        try
        {
            Sprite? sprite = null;
            bool gameIcon = false;
            if (icon.SpriteName.Length > 0)
            {
                sprite = NamedSprite(site, icon.SpriteName);
                gameIcon = sprite != null;
                if (gameIcon) source = "sprite:" + (sprite!.name ?? icon.SpriteName);
            }

            if (sprite == null && icon.ItemGuid != 0)
            {
                sprite = ItemIconSprite(icon.ItemGuid);
                gameIcon = sprite != null;
                if (gameIcon) source = "item:" + icon.ItemGuid.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            if (sprite == null)
            {
                if (!UiDrawnMark.TryParse(icon.Mark, out var mark) || !mark.IsIcon) mark = UiDrawnMark.ChestMark;
                sprite = MarkSprite(mark);
                source = "mark:" + mark.Name;
            }

            if (sprite == null) return null;
            go = new GameObject("icon");
            var rt = go.AddComponent<RectTransform>();
            rt.SetParent(clone.transform, false);
            float side = Mathf.Round(size.y * share);
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(side, side);
            rt.anchoredPosition = Vector2.zero;
            var layout = go.AddComponent<LayoutElement>();
            layout.ignoreLayout = true;
            var image = go.AddComponent<Image>();
            image.sprite = sprite;
            image.preserveAspect = true;
            image.raycastTarget = false;
            // A drawn mark takes the caption's color, or on a row of the game's own icons (the heart's) their color.
            image.color = gameIcon ? Color.white : markTint ?? caption.color;
            go.SetActive(false);
            return go;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: attach icon face not built: " + e.Message);
            TryDestroyImmediate(go);
            source = "";
            return null;
        }
    }

    /// <summary>
    /// A loaded game sprite by name (<see cref="VanillaButtonIcon.Sprite"/>): the cached one while alive, else one
    /// sweep of the sprites in memory (an exact-case name wins over another case; the framework's own sprites are
    /// skipped). Loads nothing. A miss is said once per name and not swept again for
    /// <see cref="VanillaButtonSprites.RetryFrames"/> frames.
    /// </summary>
    private static Sprite? NamedSprite(VanillaUiSite site, string name)
    {
        var cached = VanillaButtonSprites.Get(name, o => o is Sprite s && Alive(s));
        if (cached is Sprite hit) return hit;
        int frame = Time.frameCount;
        // One sweep looks for every wanted name due a search; the others asked this frame then read the cache or
        // their fresh miss instead of sweeping again.
        var names = VanillaButtonSprites.SweepFor(name, frame);
        if (names.Count == 0) return null;
        var exact = new Sprite?[names.Count];
        var loose = new Sprite?[names.Count];
        int left = names.Count;
        try
        {
            var all = Resources.FindObjectsOfTypeAll(Il2CppType.Of<Sprite>());
            for (int i = 0; all != null && i < all.Length && left > 0; i++)
            {
                Sprite? s;
                string? n;
                try
                {
                    s = all[i] == null ? null : all[i].TryCast<Sprite>();
                    n = s != null ? s.name : null;
                }
                catch { continue; }
                if (s == null || n == null || UiOwnership.IsOwned(n)) continue;
                for (int k = 0; k < names.Count; k++)
                {
                    if (exact[k] != null) continue;
                    int m = VanillaButtonSprites.Match(names[k], n);
                    if (m == 2) { exact[k] = s; left--; }
                    else if (m == 1 && loose[k] == null) loose[k] = s;
                }
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: attach sprite '" + name + "' lookup threw: " + e.Message);
        }

        for (int k = 1; k < names.Count; k++) VanillaButtonSprites.Note(names[k], exact[k] ?? loose[k], frame);
        var found = exact[0] ?? loose[0];
        VanillaButtonSprites.Note(name, found, frame);
        if (found == null && VanillaSiteHealth.LogOnce(site, "sprite missing: " + name))
            Plugin.Logger?.LogInfo("Ui: attach " + VanillaUiSites.Key(site) + " icon sprite '" + name + "' is not loaded; the button shows its drawn mark (looked for again in "
                + VanillaButtonSprites.RetryFrames + " frames).");
        return found;
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
        if (VanillaUiSites.IsInline(host.Anchor)) return null;
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
            tmp.alignment = VanillaUiSites.IsTooltipAnchor(host.Anchor) ? tmp.alignment : TextAlignmentOptions.Left;

            foreach (var behaviour in clone.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (behaviour == null || behaviour.TryCast<TextMeshProUGUI>() != null) continue;
                if (behaviour.TryCast<LayoutElement>() != null) continue;
                try { behaviour.enabled = false; } catch { /* */ }
            }

            clone.SetActive(true);
            var made = new PanelRowWidget(clone, tmp) { Site = site };
            host.Rows.Add(made);
            return made;
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

        if (VanillaUiSites.IsTooltipAnchor(anchor))
        {
            if (anchor == VanillaPanelAnchor.TooltipHeader) AlignHeader(host, view);
            else if (anchor == VanillaPanelAnchor.TooltipFooter) AlignToLineBefore(host, view);
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
            // Columns on one edge stack in attach order: each under the ones attached before it.
            var earlierColumns = EarlierStacked(host, space);
            var result = VanillaPanelPlacement.PlaceEdge(anchor, panelRect, gridTop, column.Width, column.Height, bounds, FitScratch,
                earlierColumns, VanillaPanelPlacement.DefaultSpacing);
            FitScratch.Clear();
            NoteStack(host, earlierColumns.Count, result);

            var rt = host.Rect;
            if (rt != null && !result.Rect.IsEmpty)
            {
                var pos = new Vector2(result.Rect.X, result.Rect.Y);
                var sz = new Vector2(result.Rect.Width, result.Rect.Height);
                if ((rt.anchoredPosition - pos).sqrMagnitude > 0.01f) rt.anchoredPosition = pos;
                if ((rt.sizeDelta - sz).sqrMagnitude > 0.01f) rt.sizeDelta = sz;
            }

            NotePlaced(host, result.Fit == VanillaFit.Blocked ? default : result.Rect);
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
        // The window rect is the window's moving content when it fills the window (MotionRoot, which the open slide
        // moves inside the static root), so the frame moves with the group and a slide keeps its shape.
        FitScratchRects.Clear();
        var windowLocal = panelLocal;
        try
        {
            var a = group.parent;
            UiRect moving = default;
            for (int depth = 0; a != null && depth < 8 && a.Pointer != panelSpace.Pointer; depth++, a = a.parent)
            {
                var r = RectIn(panelSpace, a.TryCast<RectTransform>());
                FitScratchRects.Add(r);
                if (a.parent != null && a.parent.Pointer == panelSpace.Pointer) moving = r;
            }

            windowLocal = VanillaPanelPlacement.WindowRect(panelLocal, moving);
        }
        catch { /* the window rect alone is the frame */ }
        var frame = VanillaPanelPlacement.InlineFrame(windowLocal, FitScratchRects);
        FitScratchRects.Clear();

        // Obstacles: the group's own members; for a placed container also the group's siblings
        // (the inventory's item area above the bar), since it sits beside the group, not in it.
        FitScratch.Clear();
        var oursPtr = host.Transform != null ? host.Transform.Pointer : IntPtr.Zero;
        UiRect lastMember = default, firstMember = default;
        AddMembers(group, panelSpace, oursPtr, ref lastMember, ref firstMember);
        if (host.Mode == InlineMode.Positioned && group.parent != null)
        {
            var parent = group.parent;
            for (int i = 0; i < parent.childCount; i++)
            {
                var sib = parent.GetChild(i);
                if (sib == null || sib.Pointer == group.Pointer || sib.Pointer == oursPtr || !sib.gameObject.activeInHierarchy) continue;
                // Another plugin's container on this anchor is not an obstacle: the stack keeps them apart.
                if (IsStackPeer(host, sib)) continue;
                FitScratch.Add(new UiObstacle(SafeObjectName(sib.gameObject), RectIn(panelSpace, sib.TryCast<RectTransform>())));
            }
        }

        VanillaFitResult inlineVerdict;
        if (host.Mode == InlineMode.Positioned)
        {
            bool before = VanillaUiSites.PlacesBefore(host.Anchor);
            if (lastMember.IsEmpty) lastMember = RectIn(panelSpace, group.TryCast<RectTransform>());
            if (firstMember.IsEmpty) firstMember = lastMember;
            var member = before ? firstMember : lastMember;

            // Judge only a window at rest: it slides in when it opens, and a slot measured mid-slide
            // is the wrong slot (live: 88 wide sliding, 58 at rest). Until the frame and the bar have
            // measured the same three frames running, nothing is shown and nothing is logged.
            if (!host.Settle.Observe(frame, member))
            {
                FitScratch.Clear();
                NotePlaced(host, default);
                return VanillaFitResult.Moving;
            }

            // Title-row containers from several plugins line up in attach order: beyond the ones placed before them.
            var earlierRow = VanillaUiSites.Stacks(host.Anchor) ? EarlierStacked(host, panelSpace) : null;
            var stackRef = VanillaPanelPlacement.StackReference(member, earlierRow, before);

            var reference = host.Anchor == VanillaPanelAnchor.BesideSort && Alive(view.Sort)
                ? RectIn(panelSpace, Rt(view.Sort))
                : default;
            float rowSpacing = GroupSpacing(host.Transform, VanillaPanelPlacement.DefaultSpacing);
            float width = host.TextRowWidth(rowSpacing);
            float gap = VanillaPanelAnchors.PositionedSpacing(GroupSpacing(group, 0f));
            int capable = host.IconCapableCount(out int visibleButtons);
            bool allowIcons = host.Anchor == VanillaPanelAnchor.BesideSort && visibleButtons > 0 && capable == visibleButtons;
            BesideSlot slot;
            inlineVerdict = before
                ? VanillaPanelPlacement.PlaceBeforeRow(stackRef, gap, width, host.ButtonSize.y, frame, FitScratch, out slot)
                : VanillaPanelPlacement.PlaceBesideRow(
                    stackRef, reference, allowAbove: host.Anchor == VanillaPanelAnchor.BesideSort, allowIcons, gap,
                    width, host.MinInlineWidth(rowSpacing), visibleButtons, host.ButtonSize.y, frame, FitScratch, out slot);
            host.Slot = slot;
            NoteStack(host, earlierRow?.Count ?? 0, inlineVerdict);
            if (inlineVerdict.Fit == VanillaFit.Fits)
            {
                if (host.IconRow != inlineVerdict.Icons) host.SetIconRow(inlineVerdict.Icons);
                Place(host, panelSpace, inlineVerdict.Rect);
            }

            // The geometry the verdict was made on, said when it changes (a refused slot is asked
            // again every 30 frames; the line repeats only if something moved).
            string slotWord = SlotWord(slot);
            string faceWord = inlineVerdict.Fit == VanillaFit.Fits ? host.FaceWord() : "none";
            int stacked = earlierRow?.Count ?? 0;
            string geometry = "Ui: attach " + VanillaUiSites.Key(host.Site) + " anchor=" + VanillaUiSites.AnchorKey(host.Anchor)
                + " slot=" + slotWord
                + " face=" + faceWord
                + (before ? " first=" + firstMember : " last=" + lastMember)
                + (stacked > 0 ? " stacked=" + stacked + " beside=" + stackRef : "")
                + " frame=" + frame + " row=" + Mathf.RoundToInt(width) + "/min" + Mathf.RoundToInt(host.MinInlineWidth(rowSpacing))
                + " placed=" + inlineVerdict.Rect + " reason=" + inlineVerdict.Reason;

            // Said, and counted as a placement, when the placement's shape changes; a window sliding as one piece
            // keeps its shape, so one open is one line.
            string shape = PlacementSettle.ShapeKey(slotWord, faceWord, frame, member, inlineVerdict.Rect) + "|" + inlineVerdict.Reason
                + "|" + stacked;
            if (host.LastShape != shape)
            {
                host.LastShape = shape;
                if (inlineVerdict.Fit == VanillaFit.Fits)
                {
                    host.Placements++;

                    // A new placement checks again that nothing the window turned on since covers the title row.
                    if (VanillaUiSites.IsTitleRow(host.Anchor) && ReparentAboveCovers(host, view, group))
                        Place(host, panelSpace, inlineVerdict.Rect);
                }
            }

            // Keyed by container, so two plugins' containers on one anchor each say their own placement.
            if (!PlacementSaid.TryGetValue(host.Name, out var said) || said != shape)
            {
                PlacementSaid[host.Name] = shape;
                Plugin.Logger?.LogInfo(geometry);
            }

            NotePlaced(host, inlineVerdict.Fit == VanillaFit.Fits ? inlineVerdict.Rect : default);
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

    /// <summary>
    /// The group's visible members as obstacles (ours skipped); <paramref name="last"/> is the last of them and
    /// <paramref name="first"/> the leftmost (the reference of a row placed before them).
    /// </summary>
    private static void AddMembers(Transform group, Transform space, IntPtr ours, ref UiRect last, ref UiRect first)
    {
        for (int i = 0; i < group.childCount; i++)
        {
            var child = group.GetChild(i);
            if (child == null || child.Pointer == ours || !child.gameObject.activeInHierarchy) continue;
            var r = RectIn(space, child.TryCast<RectTransform>());
            FitScratch.Add(new UiObstacle(SafeObjectName(child.gameObject), r));
            if (r.IsEmpty) continue;
            last = r;
            if (first.IsEmpty || r.X < first.X) first = r;
        }
    }

    /// <summary>The <c>slot=</c> word (geometry line and readback).</summary>
    private static string SlotWord(BesideSlot slot) => slot switch
    {
        BesideSlot.After => "after",
        BesideSlot.Above => "above",
        BesideSlot.Before => "before",
        _ => "none",
    };

    // ---- stacking: containers of one site and anchor line up in attach order

    /// <summary>Every live container on a stacking anchor (<see cref="VanillaUiSites.Stacks"/>), in build order.</summary>
    private static readonly List<PanelContainerWidget> StackedContainers = new();

    /// <summary>Bumped when a stacked container moves, shows, hides or goes (<see cref="AttachStackVersion"/>).</summary>
    private static readonly Dictionary<(VanillaUiSite, VanillaPanelAnchor), int> StackVersions = new();

    private static readonly List<UiRect> StackScratch = new();

    public void SetAttachOrder(IRuntimeWidget container, long order)
    {
        if (container is PanelContainerWidget pc) pc.Order = order;
    }

    public int AttachStackVersion(VanillaUiSite site, VanillaPanelAnchor anchor) =>
        StackVersions.TryGetValue((site, anchor), out int v) ? v : 0;

    private static void BumpStack(VanillaUiSite site, VanillaPanelAnchor anchor)
    {
        StackVersions.TryGetValue((site, anchor), out int v);
        StackVersions[(site, anchor)] = unchecked(v + 1);
    }

    /// <summary>
    /// The rects, in <paramref name="space"/>, of the live, shown containers of <paramref name="host"/>'s site and
    /// anchor attached before it (lower <see cref="PanelContainerWidget.Order"/>). Dead ones are dropped from the list.
    /// The list is reused: read it before the next call.
    /// </summary>
    private static List<UiRect> EarlierStacked(PanelContainerWidget host, Transform space)
    {
        StackScratch.Clear();
        for (int i = StackedContainers.Count - 1; i >= 0; i--)
            if (StackedContainers[i] == null || !StackedContainers[i].Alive) StackedContainers.RemoveAt(i);
        if (host.Order <= 0 || !VanillaUiSites.Stacks(host.Anchor)) return StackScratch;
        for (int i = 0; i < StackedContainers.Count; i++)
        {
            var c = StackedContainers[i];
            if (ReferenceEquals(c, host) || c.Site != host.Site || c.Anchor != host.Anchor) continue;
            if (c.Order <= 0 || c.Order >= host.Order || !c.StackShown) continue;
            var r = RectIn(space, c.Rect);
            if (!r.IsEmpty) StackScratch.Add(r);
        }

        return StackScratch;
    }

    /// <summary>Whether <paramref name="t"/> is another live container on <paramref name="host"/>'s site and anchor.</summary>
    private static bool IsStackPeer(PanelContainerWidget host, Transform t)
    {
        if (!VanillaUiSites.Stacks(host.Anchor)) return false;
        for (int i = 0; i < StackedContainers.Count; i++)
        {
            var c = StackedContainers[i];
            if (ReferenceEquals(c, host) || c.Site != host.Site || c.Anchor != host.Anchor) continue;
            var ct = c.Transform;
            if (ct != null && ct.Pointer == t.Pointer) return true;
        }

        return false;
    }

    /// <summary>
    /// Record where a stacked container was placed (empty while it is hidden or settling) and bump the anchor's version
    /// when that changed, so the containers stacked behind it follow on the next frame.
    /// </summary>
    private static void NotePlaced(PanelContainerWidget host, UiRect placed)
    {
        if (!VanillaUiSites.Stacks(host.Anchor)) return;
        string key = placed.ToString();
        if (host.StackKey == key) return;
        host.StackKey = key;
        BumpStack(host.Site, host.Anchor);
    }

    /// <summary>One line when a container is stacked behind others, or no longer is (once per change of the count).</summary>
    private static void NoteStack(PanelContainerWidget host, int earlier, in VanillaFitResult result)
    {
        host.StackedBehind = earlier;
        if (host.StackSaid == earlier) return;
        bool first = host.StackSaid < 0;
        host.StackSaid = earlier;
        if (first && earlier == 0) return;
        Plugin.Logger?.LogInfo("Ui: attach " + VanillaUiSites.Key(host.Site) + " anchor=" + VanillaUiSites.AnchorKey(host.Anchor)
            + " container " + host.Name + " stacked behind " + earlier + " attached before it (order " + host.Order + ")"
            + " fit=" + (result.Fit == VanillaFit.Fits ? "ok" : result.Fit == VanillaFit.Blocked ? "blocked" : "unknown") + " placed=" + result.Rect);
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
    internal static List<object?> DescribeAttachments() => DescribeAttachmentsCore();

    /// <summary>
    /// A cloned button's interactable state for the readback: the button's own <c>interactable</c>, and every
    /// <c>CanvasGroup</c> above it that turns interaction or raycasts off (by name).
    /// </summary>
    private static Dictionary<string, object?> InteractableOf(GameObject? go)
    {
        var d = new Dictionary<string, object?>();
        if (go == null) return d;
        try
        {
            var stun = go.GetComponent<SimpleStunButton>();
            d["button"] = stun != null && stun.interactable;
            d["active"] = go.activeInHierarchy;
            var off = new List<object?>();
            for (var t = go.transform; t != null; t = t.parent)
            {
                var cg = t.GetComponent<CanvasGroup>();
                if (cg == null || !cg.enabled) continue;
                if (!cg.interactable || !cg.blocksRaycasts)
                    off.Add(SafeObjectName(t.gameObject) + (!cg.interactable ? " interactable=false" : "") + (!cg.blocksRaycasts ? " blocksRaycasts=false" : ""));
                if (cg.ignoreParentGroups) break;
            }

            d["groupsOff"] = off;
            d["ok"] = stun != null && stun.interactable && go.activeInHierarchy && off.Count == 0;
        }
        catch (Exception e) { d["error"] = e.Message; }
        return d;
    }

    /// <summary>
    /// What the pointer would hit at the center of <paramref name="rt"/>: the topmost raycast result (the graphic that
    /// takes a click there), whether it is this button or inside it, and the first few results under it. Read on
    /// demand for the readback, never per frame.
    /// </summary>
    private static Dictionary<string, object?> RaycastAtCenter(RectTransform? rt, GameObject? self)
    {
        var d = new Dictionary<string, object?>();
        if (rt == null || self == null || !self.activeInHierarchy) { d["why"] = "not shown"; return d; }
        try
        {
            var canvas = rt.GetComponentInParent<Canvas>();
            var root = canvas != null ? canvas.rootCanvas : null;
            Camera? cam = root != null && root.renderMode != RenderMode.ScreenSpaceOverlay ? root.worldCamera : null;
            var center = rt.TransformPoint(rt.rect.center);
            var sp = RectTransformUtility.WorldToScreenPoint(cam, center);
            d["x"] = Math.Round(sp.x, 1);
            d["y"] = Math.Round(sp.y, 1);
            var es = UnityEngine.EventSystems.EventSystem.current;
            if (es == null) { d["why"] = "no EventSystem"; return d; }
            var ev = new UnityEngine.EventSystems.PointerEventData(es) { position = sp };
            var results = new Il2CppSystem.Collections.Generic.List<UnityEngine.EventSystems.RaycastResult>();
            es.RaycastAll(ev, results);
            var under = new List<object?>();
            GameObject? top = null;
            for (int i = 0; results != null && i < results.Count && i < 4; i++)
            {
                GameObject? g = null;
                try { g = results[i].gameObject; } catch { }
                if (i == 0) top = g;
                under.Add(g == null ? null : PathOf(g.transform));
            }

            d["topmost"] = top == null ? null : PathOf(top.transform);
            d["hitsSelf"] = top != null && (top.Pointer == self.Pointer || top.transform.IsChildOf(self.transform));
            d["under"] = under;
        }
        catch (Exception e) { d["why"] = e.GetType().Name + ": " + e.Message; }
        return d;
    }

    /// <summary>The last four names of <paramref name="t"/>'s path, outermost first.</summary>
    private static string PathOf(Transform t)
    {
        var parts = new List<string>();
        for (var a = t; a != null && parts.Count < 4; a = a.parent) parts.Insert(0, SafeObjectName(a.gameObject));
        return string.Join("/", parts);
    }

    private static List<object?> DescribeAttachmentsCore()
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
                {
                    d["donor"] = view.DonorSource.Length > 0 ? view.DonorSource : "<none>";
                    // The open window's rename button: where BesideRename goes, or why it cannot.
                    d["rename"] = Alive(view.RenameGroup) ? "ok via=" + view.RenameSource
                        : view.RenameMissing.Length > 0 ? "missing: " + view.RenameMissing : "";
                    // The castle heart's icon row: where BesideTitleButtons goes, or why it cannot.
                    if (rb.Site == VanillaUiSite.CastleHeartPanel)
                        d["titleButtons"] = Alive(view.TitleButtons)
                            ? "ok via=" + view.TitleButtonsSource + " ref=" + SafeObjectName(view.TitleButtonsRef)
                                + " donor=" + (Alive(view.TitleButtonsDonor) ? SafeObjectName(view.TitleButtonsDonor) : "bar:" + view.DonorSource)
                                + " iconShare=" + Math.Round(view.TitleIconShare, 3)
                            : "missing: " + (view.TitleButtonsMissing.Length > 0 ? view.TitleButtonsMissing : TitleButtonsFields);
                }

                d["siteAnchors"] = VanillaUiSites.AnchorKeys(rb.Site);
                bool indented = false;
                foreach (int level in rb.RowIndents) indented |= level > 0;
                if (indented) d["indentStep"] = Mathf.Round(IndentStepReadback(rb.Site) * 10f) / 10f;
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
            if (rb.Container is PanelContainerWidget tip && VanillaUiSites.IsTooltipAnchor(tip.Anchor))
            {
                DescribeTooltipSlot(d, tip);
                if (tip.Anchor == VanillaPanelAnchor.TooltipFooter && d["tooltipSlot"] is Dictionary<string, object?> footSlot)
                {
                    footSlot["inset"] = tip.LineInset;
                    footSlot["alignedTo"] = tip.LineBefore;
                }
            }
            if (rb.Container is PanelContainerWidget stacked && VanillaUiSites.Stacks(stacked.Anchor))
            {
                // Where this container is in its anchor's stack: how many attached before it are shown, and its order.
                d["stack"] = new Dictionary<string, object?>
                {
                    ["order"] = stacked.Order,
                    ["behind"] = stacked.StackedBehind,
                    ["shown"] = stacked.StackShown,
                };
            }

            if (rb.Container is PanelContainerWidget placed && placed.Mode == InlineMode.Positioned)
            {
                d["slot"] = SlotWord(placed.Slot);
                d["face"] = placed.FaceWord();
                d["placements"] = placed.Placements;
                d["parent"] = placed.Transform != null && placed.Transform.parent != null ? SafeObjectName(placed.Transform.parent.gameObject) : "";
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
                    if (i < rb.Buttons.Length && rb.Buttons[i] is PanelButtonWidget tipped && tipped.Alive)
                    {
                        b["face"] = tipped.IconMode ? "icon" : "text";
                        b["iconAlways"] = tipped.IconAlways;
                        b["icon"] = tipped.FaceSource;
                        b["interactable"] = InteractableOf(tipped.GameObject);
                        b["raycast"] = RaycastAtCenter(tipped.Rect, tipped.GameObject);
                        var (tipTitle, tipBody, tipSource) = tipped.TooltipContent;
                        b["tooltip"] = new Dictionary<string, object?>
                        {
                            ["text"] = tipped.IconMode ? tipped.Text : "",
                            ["source"] = tipSource,
                            ["style"] = VanillaButtonTooltip.Style,
                            ["title"] = tipTitle,
                            ["body"] = tipBody,
                            ["placement"] = "below",
                            ["side"] = tipped.TooltipUp ? UnityUiRuntime.TooltipPlacedSide : "",
                            ["boxBody"] = UnityUiRuntime.TooltipBody,
                            ["watch"] = tipped.TooltipWatch,
                            ["hovered"] = tipped.Hovered,
                            ["hoverEvents"] = tipped.HoverEvents,
                            ["shown"] = tipped.TooltipUp,
                            ["boxTitle"] = UnityUiRuntime.TooltipTitle,
                        };
                    }
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
                + (site == VanillaUiSite.CastleHeartPanel ? Describe("titleButtons", view.TitleButtons) + (view.TitleButtons != null ? "{" + view.TitleButtonsSource + " " + LayoutOf(view.TitleButtons) + "}" : "") : "")
                + Describe("caption", view.CaptionDonor)
                + Describe("body", view.BodyDonor)
                + (view.Tooltip != null ? Describe("tooltipFrame", view.Tooltip.DescriptionFrame != null ? view.Tooltip.DescriptionFrame.transform : null) : "")
                + (site == VanillaUiSite.RecipeTooltip ? Describe("requirements", RecipeBlockOf(view.Tooltip)) + Describe("header", HeaderOf(view.Tooltip)) : "")
                + Describe("obstacle", view.Obstacle != null ? view.Obstacle.transform : null));
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: site " + VanillaUiSites.Key(site) + " hierarchy dump threw: " + e.Message);
        }
    }

    // ------------------------------------------------------------------ widgets

    /// <summary>A P6 container. Destroyed through the module's destroy helper with the verdict kept.</summary>
    private sealed class PanelContainerWidget : IRuntimeWidget, IReleaseVerdict, IRequirementLineGate
    {
        private GameObject? _go;
        private UiDestroyVerdict _verdict = UiDestroyVerdict.Failed;

        // A per-line container: shown while the attachment wants it (_wanted), the game shows the line and
        // the game's lines are the hovered recipe's requirements one for one (_linesMatch).
        private GameObject? _lineRow;
        private bool _gated;
        private bool _wanted = true;
        private bool _linesMatch;

        /// <summary>The requirement list this per-line container sits in, or null.</summary>
        internal TooltipRecipeRequiredItems? Block { get; private set; }

        /// <summary>Gate this container on the game's requirement line row <paramref name="lineRow"/>.</summary>
        internal void GateOn(GameObject lineRow, TooltipRecipeRequiredItems? block)
        {
            _wanted = Alive && _go!.activeSelf;
            _lineRow = lineRow;
            Block = block;
            _gated = true;
            SyncLine(LinesMatchFor(block));
        }

        public bool GameLineShown
        {
            get { try { return !_gated || (_linesMatch && UnityUiRuntime.Alive(_lineRow) && _lineRow!.activeSelf); } catch { return false; } }
        }

        /// <summary>Apply the gate: active exactly when wanted, the lines match the recipe and the game shows this line.</summary>
        internal void SyncLine(bool linesMatch)
        {
            if (!_gated) return;
            _linesMatch = linesMatch;
            Apply();
        }

        private void Apply()
        {
            try
            {
                bool show = VanillaUiSites.LineShown(_wanted, GameLineShown);
                if (UnityUiRuntime.Alive(_go) && _go!.activeSelf != show) _go.SetActive(show);
            }
            catch { /* */ }
        }

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

        /// <summary>The shape of the last placement (<see cref="PlacementSettle.ShapeKey"/>).</summary>
        internal string LastShape { get; set; } = "";

        /// <summary>How many different placements this container got (a window open is one; readback <c>placements</c>).</summary>
        internal int Placements { get; set; }

        /// <summary>The attach order of the handle that owns it (<see cref="SetAttachOrder"/>); 0 when not given (no stacking).</summary>
        internal long Order { get; set; }

        /// <summary>The last placed rect, as a key (empty while hidden or settling), for <see cref="NotePlaced"/>.</summary>
        internal string StackKey { get; set; } = "";

        /// <summary>How many containers attached before it it was last stacked behind; and the count last said (-1: none yet).</summary>
        internal int StackedBehind { get; set; }

        internal int StackSaid { get; set; } = -1;

        /// <summary>Shown now, so a container attached later stacks behind it.</summary>
        internal bool StackShown
        {
            get { try { return UnityUiRuntime.Alive(_go) && _go!.activeSelf; } catch { return false; } }
        }

        /// <summary>What the visible buttons show: <c>icon</c> (all), <c>text</c> (none) or <c>mixed</c>.</summary>
        internal string FaceWord()
        {
            int visible = 0, icons = 0;
            for (int i = 0; i < Buttons.Count; i++)
            {
                if (!Buttons[i].Alive || !Buttons[i].Visible) continue;
                visible++;
                if (Buttons[i].IconMode) icons++;
            }

            return VanillaPanelPlacement.FaceWord(visible, icons);
        }

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

        /// <summary>A <see cref="VanillaPanelAnchor.TooltipHeader"/> container's measured title inset, or -1 before the first measure.</summary>
        internal float HeaderInset { get; set; } = -1f;

        /// <summary>The rows cloned into this container, in build order (dead ones are skipped where read).</summary>
        internal List<IRuntimeWidget> Rows { get; } = new();

        /// <summary>A footer container's measured inset (its left padding) and the game line it follows, for the readback.</summary>
        internal int LineInset { get; set; } = -1;

        internal string LineBefore { get; set; } = "";

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
                w += Buttons[i].IconAlways ? ButtonSize.y : VanillaPanelAnchors.MinButtonWidth(Buttons[i].CaptionWidth, CaptionPadding, ButtonSize.y);
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
            // A gated container reports the attachment's wish, not the line gate: the handle compares
            // this with what it wants and must not fight the gate every frame.
            get { try { return UnityUiRuntime.Alive(_go) && (_gated ? _wanted : _go!.activeSelf); } catch { return false; } }
            set
            {
                if (_gated) { _wanted = value; Apply(); return; }
                try
                {
                    if (UnityUiRuntime.Alive(_go) && _go!.activeSelf != value)
                    {
                        _go.SetActive(value);
                        // A stacked container that shows or hides moves the ones stacked behind it.
                        if (VanillaUiSites.Stacks(Anchor)) BumpStack(Site, Anchor);
                    }
                }
                catch { /* */ }
            }
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

            if (VanillaUiSites.Stacks(Anchor))
            {
                StackedContainers.Remove(this);
                BumpStack(Site, Anchor);
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
        /// Shown as a square icon button (its caption hidden, the caption as its tooltip): the caption
        /// does not fit the slot (<see cref="VanillaPanelPlacement.PlaceBesideRow"/>), or the button is
        /// <see cref="IconAlways"/>.
        /// </summary>
        internal bool IconMode { get; private set; }

        /// <summary>
        /// The width this button asks for as text: donor width or caption plus padding; the square for a button that
        /// is always its icon (<see cref="IconAlways"/>).
        /// </summary>
        internal float TextWidth => IconAlways ? _donorSize.y : _textWidth;

        /// <summary>An icon face was built (a <see cref="VanillaPanelAnchor.BesideSort"/> button, or <see cref="IconAlways"/>).</summary>
        internal bool HasIcon => UnityUiRuntime.Alive(_icon);

        /// <summary>
        /// Always the square icon face, never text (<see cref="VanillaUiSites.IconAlways"/>: a title-row anchor and a
        /// chosen icon). A row switch to text leaves it as it is.
        /// </summary>
        internal bool IconAlways { get; init; }

        /// <summary>What the icon face shows (<c>sprite:</c>, <c>item:</c> or <c>mark:</c> and a name), or empty without one.</summary>
        internal string FaceSource { get; init; } = "";

        internal void SetIconMode(bool icon)
        {
            if (_icon == null) icon = false;
            else if (IconAlways) icon = true;
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

        /// <summary>Pointer-enter events this button received (diagnostics and <c>ui_read</c>).</summary>
        internal int HoverEvents { get; private set; }

        internal bool Hovered => _hovered;

        /// <summary>The pointer watch that drives the tooltip was attached.</summary>
        internal bool TooltipWatch => _hover != null;

        /// <summary>The tooltip box is up and is this button's.</summary>
        internal bool TooltipUp => _hovered && IconMode && UnityUiRuntime.TooltipShown;

        /// <summary>The site this button is on, for its one-time lines.</summary>
        internal VanillaUiSite Site { get; set; }

        private VanillaButtonTooltip? _tip;

        /// <summary>The plugin's <c>Tooltip(text, title)</c>, or null: the caption is the tooltip. A shown box is redrawn.</summary>
        internal void SetTooltip(VanillaButtonTooltip? tip)
        {
            _tip = tip;
            if (_hovered && IconMode) OnHover(true);
        }

        /// <summary>What the icon face's tooltip shows now (<see cref="VanillaButtonTooltip.Resolve"/>).</summary>
        internal (string Title, string Body, string Source) TooltipContent =>
            VanillaButtonTooltip.Resolve(_caption != null ? (_caption.text ?? "") : "", _tip);

        private void OnHover(bool inside)
        {
            _hovered = inside;
            if (inside) HoverEvents++;
            if (!inside || !IconMode)
            {
                HideTip();
                return;
            }

            string why = "";
            try
            {
                var rt = Rect;
                if (rt == null || _runtime == null) why = "no rect or runtime";
                else
                {
                    var (title, body, _) = TooltipContent;
                    var anchor = ScreenRectOf(rt);
                    if (!anchor.IsKnown) why = "the button's screen rect is unknown";
                    else
                    {
                        // A game window has no mod panel: the box must not need one to stay up. Under the button (over it
                        // when there is no room below), never beside it, so it never covers the window's title.
                        _runtime.ShowGameControlTooltip(anchor, title, body, () => _hovered && IconMode && Alive && ActiveInHierarchy, below: true);
                        if (!UnityUiRuntime.TooltipShown) why = "the tooltip box did not come up";
                    }
                }
            }
            catch (Exception e) { why = e.GetType().Name + ": " + e.Message; }

            if (why.Length > 0)
            {
                if (VanillaSiteHealth.LogOnce(Site, "icon tooltip: " + why))
                    Plugin.Logger?.LogWarning("Ui: attach " + VanillaUiSites.Key(Site) + " icon tooltip not shown: " + why + ".");
            }
            else if (VanillaSiteHealth.LogOnce(Site, "icon tooltip shown"))
            {
                Plugin.Logger?.LogInfo("Ui: attach " + VanillaUiSites.Key(Site) + " icon tooltip shown on hover ('" + UnityUiRuntime.TooltipTitle + "').");
            }
        }

        private bool ActiveInHierarchy
        {
            get { try { return UnityUiRuntime.Alive(_go) && _go!.activeInHierarchy; } catch { return false; } }
        }

        private void HideTip()
        {
            try { _runtime?.HideTooltip(); }
            catch { /* */ }
        }

        internal LayoutElement? Element { get; set; }

        internal GameObject? GameObject => UnityUiRuntime.Alive(_go) ? _go : null;

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

        private LabelTone _tone = LabelTone.Normal;
        private Color? _donorColor;

        /// <summary>
        /// Give the row its tone: the donor's own color (read on the first tone change) for
        /// <see cref="LabelTone.Normal"/>, else the framework's label color. A repeat is no write.
        /// </summary>
        internal void SetTone(LabelTone tone)
        {
            if (tone == _tone || !Alive || _tmp == null) return;
            try
            {
                _donorColor ??= _tmp.color;
                _tmp.color = tone == LabelTone.Normal ? _donorColor.Value : ToColor(MenuPalette.LabelColor(tone));
                _tone = tone;
            }
            catch { /* a row Unity is destroying keeps its old color for its last frame */ }
        }

        /// <summary>The site whose donor this row was cloned from (its indent step is measured there).</summary>
        internal VanillaUiSite Site { get; init; }

        internal float FontSize
        {
            get { try { return Alive && _tmp != null ? _tmp.fontSize : 0f; } catch { return 0f; } }
        }

        internal Transform? RowTransform => UnityUiRuntime.Alive(_go) ? _go!.transform : null;

        private Vector4? _baseMargin;
        private float _indentPx;

        /// <summary>Where the row's text starts, in world x, without its indent; NaN when it cannot be read.</summary>
        internal float ContentLeftWorld(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Vector3> corners)
        {
            try
            {
                if (!Alive || _tmp == null || !_tmp.gameObject.activeInHierarchy) return float.NaN;
                _tmp.rectTransform.GetWorldCorners(corners);
                float margin = _tmp.margin.x - _indentPx;
                return corners[0].x + margin * _tmp.rectTransform.lossyScale.x;
            }
            catch { return float.NaN; }
        }

        /// <summary>The row's text inset by <paramref name="px"/> on the left (the donor's own margin kept); a repeat is no write.</summary>
        internal void SetIndent(float px)
        {
            if (!Alive || _tmp == null || Mathf.Abs(px - _indentPx) < 0.25f) return;
            try
            {
                _baseMargin ??= _tmp.margin;
                var m = _baseMargin.Value;
                _tmp.margin = new Vector4(m.x + px, m.y, m.z, m.w);
                _indentPx = px;
            }
            catch { /* */ }
        }
    }

    public void StyleAttachRow(IRuntimeWidget row, LabelTone tone)
    {
        if (row is PanelRowWidget panelRow) panelRow.SetTone(tone);
        else if (row is KeyHintRowWidget hint) hint.SetTone(tone);
    }
}
#endif
