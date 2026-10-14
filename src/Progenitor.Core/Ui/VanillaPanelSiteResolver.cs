#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
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
/// <para><b>The seam.</b> <c>SystemHooks.After&lt;XMenuMapper&gt;</c> on the window's own managed
/// mapper, the same seam the map card uses: the managed <c>SystemBase.Update</c>, which runs every
/// frame whether or not the mapper's queries match, so a close is seen on the frame it happens.
/// Never Harmony on a mapper, never a mapper's Burst <c>LambdaJob_*</c>, never a method of the
/// window (<c>FakeTooltip.SetTooltip</c> takes a dozen <c>Nullable</c> structs by value and is not
/// a seam).</para>
///
/// <para><b>The address.</b> Each mapper keeps the window it drives in a private field
/// (<c>_ContainerSubMenu</c>, <c>_WorkstationSubMenu</c>, <c>_RefinementStationSubMenu</c>,
/// <c>_CastleHeartSubMenu</c>, <c>_InventorySubMenu</c>) and the entity it was opened on in
/// <c>_Target</c>; the interop exposes both as properties. Open is "the submenu's GameObject is
/// active in the hierarchy" (a <c>HUDMenu</c> is shown and hidden by <c>OnEnable</c> /
/// <c>OnDisable</c>) and, for a window with a target, "the target is not <c>Entity.Null</c>".</para>
///
/// <para><b>What is not known offline, and is logged instead.</b> The prefab hierarchy under each
/// window, which layout groups govern it and the rects are serialized data the game's metadata does
/// not carry. The first time each site resolves, one line names the panel, its parent, the grid,
/// the donors and the inline group with their rects, so the first live run answers them from the
/// log.</para>
/// </summary>
internal sealed partial class UnityUiRuntime
{
    /// <summary>What the most recent frame resolved for each panel site.</summary>
    private static readonly Dictionary<VanillaUiSite, PanelView> PanelViews = new();

    private static readonly HashSet<VanillaUiSite> PanelHierarchyLogged = new();

    /// <summary>Everything the builders need from one resolved window. References only; never cached across a miss.</summary>
    private sealed class PanelView
    {
        internal GameObject? Panel;
        internal RectTransform? Grid;
        internal InventorySubMenu? Inventory;
        internal List<GameObject> Obstacles = new();
        internal Transform? RenameGroup;
        internal GameObject? RenameButton;
        internal TextMeshProUGUI? FontDonor;
        internal FakeTooltip? Tooltip;
    }

    internal static IDisposable? WatchPanelSite(VanillaUiSite site, Action<VanillaSiteSample> onFrame)
    {
        Action<SystemHookContext> frame = ctx =>
        {
            VanillaSiteSample sample;
            if (VanillaSiteHealth.IsMissing(site))
            {
                sample = VanillaSiteSample.Absent;
            }
            else
            {
                try { sample = SamplePanel(site, ctx?.World); }
                catch (Exception e)
                {
                    // A member the game removed or renamed surfaces here (the interop property
                    // throws, or the method that names it fails to compile). One line, then the
                    // site stays absent for this generation: no per-frame retry, no spam.
                    PanelViews.Remove(site);
                    if (VanillaSiteHealth.ReportMissing(site, e.GetType().Name))
                        Plugin.Logger?.LogWarning(VanillaSiteHealth.Line(site, e.GetType().Name + ": " + e.Message));
                    sample = VanillaSiteSample.Absent;
                }
            }

            onFrame(sample);
        };

        return site switch
        {
            VanillaUiSite.ContainerPanel => SystemHooks.After<ContainerMenuMapper>(frame),
            VanillaUiSite.StationPanel => SystemHooks.After<WorkstationMenuMapper>(frame),
            VanillaUiSite.CastleHeartPanel => SystemHooks.After<CastleHeartMenuMapper>(frame),
            VanillaUiSite.InventoryPanel => SystemHooks.After<InventoryMenuMapper>(frame),
            VanillaUiSite.InventoryTooltip => SystemHooks.After<InventoryMenuMapper>(frame),
            _ => null,
        };
    }

    /// <summary>Core Uninstall: drop every reference this half holds.</summary>
    internal static void ClearPanelViews()
    {
        PanelViews.Clear();
        PanelHierarchyLogged.Clear();
    }

    // ------------------------------------------------------------------ resolve

    private static VanillaSiteSample SamplePanel(VanillaUiSite site, World? world)
    {
        if (world == null || !world.IsCreated) return Miss(site);
        return site switch
        {
            VanillaUiSite.ContainerPanel => SampleContainer(world),
            VanillaUiSite.StationPanel => SampleStation(world),
            VanillaUiSite.CastleHeartPanel => SampleCastleHeart(world),
            VanillaUiSite.InventoryPanel => SampleInventory(world),
            VanillaUiSite.InventoryTooltip => SampleTooltip(world),
            _ => Miss(site),
        };
    }

    private static VanillaSiteSample Miss(VanillaUiSite site)
    {
        PanelViews.Remove(site);
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

    private static VanillaSiteSample SampleContainer(World world)
    {
        const VanillaUiSite site = VanillaUiSite.ContainerPanel;
        var m = Mapper<ContainerMenuMapper>(world);
        if (m == null) return Miss(site);
        var sub = m._ContainerSubMenu;
        if (!Shown(sub)) return Miss(site);
        var target = m._Target;
        if (target == Entity.Null) return Miss(site);

        var inv = m._InventorySubMenu;
        var view = new PanelView
        {
            Panel = sub.gameObject,
            Grid = RectOf(sub.EquipmentItemsParent),
            Inventory = inv,
            RenameGroup = Alive(sub.OpenTagEditButtonParent) ? sub.OpenTagEditButtonParent.transform : null,
            RenameButton = Alive(sub.OpenTagEditButton) ? sub.OpenTagEditButton.gameObject : null,
            FontDonor = TmpOf(sub.HeaderText),
        };
        if (Shown(inv)) view.Obstacles.Add(inv.gameObject);
        return Finish(site, view, world, VanillaPanelKind.Container, target);
    }

    private static VanillaSiteSample SampleStation(World world)
    {
        const VanillaUiSite site = VanillaUiSite.StationPanel;
        var wm = Mapper<WorkstationMenuMapper>(world);
        if (wm != null)
        {
            var sub = wm._WorkstationSubMenu;
            var target = wm._Target;
            if (Shown(sub) && target != Entity.Null)
            {
                var inv = wm._InventorySubMenu;
                var view = new PanelView
                {
                    Panel = sub.gameObject,
                    Grid = RectOf(sub.RecipesParent),
                    Inventory = inv,
                    FontDonor = TmpOf(sub.WorkstationName),
                };
                if (Shown(inv)) view.Obstacles.Add(inv.gameObject);
                return Finish(site, view, world, VanillaPanelKind.Workstation, target);
            }
        }

        var rm = Mapper<RefinementstationMenuMapper>(world);
        if (rm != null)
        {
            var sub = rm._RefinementStationSubMenu;
            var target = rm._Target;
            if (Shown(sub) && target != Entity.Null)
            {
                var inv = rm._InventorySubMenu;
                var view = new PanelView
                {
                    Panel = sub.gameObject,
                    Grid = RectOf(sub.RecipesParent),
                    Inventory = inv,
                    FontDonor = TmpOf(sub.StationName),
                };
                if (Shown(inv)) view.Obstacles.Add(inv.gameObject);
                return Finish(site, view, world, VanillaPanelKind.RefinementStation, target);
            }
        }

        return Miss(site);
    }

    private static VanillaSiteSample SampleCastleHeart(World world)
    {
        const VanillaUiSite site = VanillaUiSite.CastleHeartPanel;
        var m = Mapper<CastleHeartMenuMapper>(world);
        if (m == null) return Miss(site);
        var sub = m._CastleHeartSubMenu;
        if (!Shown(sub)) return Miss(site);
        var target = m._Target;
        if (target == Entity.Null) return Miss(site);

        var inv = m._InventorySubMenu;
        var view = new PanelView
        {
            Panel = sub.gameObject,
            Grid = null,
            Inventory = inv,
            FontDonor = Alive(inv) ? TmpOf(inv.HeaderText) : null,
        };
        if (Shown(inv)) view.Obstacles.Add(inv.gameObject);
        return Finish(site, view, world, VanillaPanelKind.CastleHeart, target);
    }

    /// <summary>
    /// The inventory a window is showing right now, its kind and the window's target. The game
    /// shows one window at a time, so the first active inventory wins; the character menu's has no
    /// target.
    /// </summary>
    private static bool ActiveInventory(
        World world, out InventorySubMenu? inv, out VanillaPanelKind kind, out Entity target, out GameObject? companion)
    {
        inv = null;
        kind = VanillaPanelKind.None;
        target = Entity.Null;
        companion = null;

        var cm = Mapper<ContainerMenuMapper>(world);
        if (cm != null && Shown(cm._InventorySubMenu))
        {
            inv = cm._InventorySubMenu;
            kind = VanillaPanelKind.Container;
            target = cm._Target;
            companion = Shown(cm._ContainerSubMenu) ? cm._ContainerSubMenu.gameObject : null;
            return true;
        }

        var wm = Mapper<WorkstationMenuMapper>(world);
        if (wm != null && Shown(wm._InventorySubMenu))
        {
            inv = wm._InventorySubMenu;
            kind = VanillaPanelKind.Workstation;
            target = wm._Target;
            companion = Shown(wm._WorkstationSubMenu) ? wm._WorkstationSubMenu.gameObject : null;
            return true;
        }

        var rm = Mapper<RefinementstationMenuMapper>(world);
        if (rm != null && Shown(rm._InventorySubMenu))
        {
            inv = rm._InventorySubMenu;
            kind = VanillaPanelKind.RefinementStation;
            target = rm._Target;
            companion = Shown(rm._RefinementStationSubMenu) ? rm._RefinementStationSubMenu.gameObject : null;
            return true;
        }

        var hm = Mapper<CastleHeartMenuMapper>(world);
        if (hm != null && Shown(hm._InventorySubMenu))
        {
            inv = hm._InventorySubMenu;
            kind = VanillaPanelKind.CastleHeart;
            target = hm._Target;
            companion = Shown(hm._CastleHeartSubMenu) ? hm._CastleHeartSubMenu.gameObject : null;
            return true;
        }

        var im = Mapper<InventoryMenuMapper>(world);
        if (im != null && Shown(im._InventorySubMenu))
        {
            inv = im._InventorySubMenu;
            kind = VanillaPanelKind.Character;
            return true;
        }

        return false;
    }

    private static VanillaSiteSample SampleInventory(World world)
    {
        const VanillaUiSite site = VanillaUiSite.InventoryPanel;
        if (!ActiveInventory(world, out var inv, out var kind, out var target, out _)) return Miss(site);
        var view = new PanelView
        {
            Panel = inv!.gameObject,
            Inventory = inv,
            FontDonor = TmpOf(inv.HeaderText),
        };
        return Finish(site, view, world, kind, target);
    }

    private static VanillaSiteSample SampleTooltip(World world)
    {
        const VanillaUiSite site = VanillaUiSite.InventoryTooltip;
        if (!ActiveInventory(world, out var inv, out var kind, out var target, out var companion)) return Miss(site);

        // The player's own slots first: their tooltip is the inventory's FakeTooltip.
        FakeTooltip? tooltip = null;
        ItemGridSelectionEntry? hovered = null;
        bool fromCompanion = false;
        if (Shown(inv!.FakeTooltip))
        {
            hovered = HoveredIn(inv.EquipmentItemsParent) ?? HoveredIn(inv.ActionSlotsParent);
            if (hovered != null) tooltip = inv.FakeTooltip;
        }

        // Then a container's slots, whose tooltip is the container half's own.
        if (hovered == null && kind == VanillaPanelKind.Container && companion != null)
        {
            var sub = companion.GetComponent<ContainerSubMenu>();
            if (Alive(sub) && Shown(sub.FakeTooltip))
            {
                hovered = HoveredIn(sub.EquipmentItemsParent);
                if (hovered != null)
                {
                    tooltip = sub.FakeTooltip;
                    fromCompanion = true;
                }
            }
        }

        if (hovered == null || tooltip == null) return Miss(site);

        int guid = 0;
        int amount = 0;
        try { guid = hovered.EntryId.GuidHash; } catch { /* */ }
        try { amount = hovered.Amount; } catch { /* */ }
        if (guid == 0) return Miss(site);

        var view = new PanelView
        {
            Panel = tooltip.gameObject,
            Inventory = inv,
            Tooltip = tooltip,
            FontDonor = TmpOf(tooltip.Description),
        };

        // A slot in the player's own bag belongs to no container: the target names the window and
        // the item. A slot in the container half names that container too.
        var owner = fromCompanion ? target : Entity.Null;
        return Finish(site, view, world, kind, owner, guid, amount);
    }

    private static ItemGridSelectionEntry? HoveredIn(Component? grid)
    {
        if (!Alive(grid)) return null;
        var t = grid!.transform;
        int n = t.childCount;
        for (int i = 0; i < n; i++)
        {
            var child = t.GetChild(i);
            if (child == null || !child.gameObject.activeInHierarchy) continue;
            var entry = child.GetComponent<ItemGridSelectionEntry>();
            if (!Alive(entry)) continue;
            if (entry.IsHovered) return entry;
        }

        return null;
    }

    /// <summary>
    /// Common tail: read the target's <c>NetworkId</c>, check the donors, remember the view for the
    /// builders, log the hierarchy once, and produce the sample.
    /// </summary>
    private static VanillaSiteSample Finish(
        VanillaUiSite site, PanelView view, World world, VanillaPanelKind kind, Entity target,
        int itemGuid = 0, int itemAmount = 0)
    {
        ReadNetworkId(world, target, out int n0, out int n1, out int n2);
        var panelTarget = new VanillaPanelTarget(kind, target.Index, target.Version, n0, n1, n2, itemGuid, itemAmount);

        // The donors this site's builders need. "Open but a donor is gone" is the after-an-update
        // shape: the events still fire (the plugin learns the window opened), the container is
        // not built (the plan detaches on a missing donor), and one line says which piece is gone.
        string? missing = null;
        if (site == VanillaUiSite.InventoryTooltip)
        {
            if (!Alive(view.FontDonor)) missing = "FakeTooltip.Description";
            else if (!Alive(view.Tooltip!.DescriptionFrame)) missing = "FakeTooltip.DescriptionFrame";
        }
        else
        {
            if (!Alive(view.Inventory) || !Alive(view.Inventory!.SelfSortButton)) missing = "InventorySubMenu.SelfSortButton";
            else if (!Alive(view.FontDonor)) missing = "HeaderText";
        }

        if (missing != null && VanillaSiteHealth.ReportMissing(site, missing))
            Plugin.Logger?.LogWarning(VanillaSiteHealth.Line(site, missing));

        PanelViews[site] = view;
        var parent = view.Panel != null && view.Panel.transform != null ? view.Panel.transform.parent : null;
        LogPanelHierarchyOnce(site, view, parent);

        return new VanillaSiteSample(
            resolved: true,
            instanceId: view.Panel!.GetInstanceID(),
            donorPresent: missing == null,
            parentLayoutGroup: false,
            parentName: parent != null ? SafeObjectName(parent.gameObject) : "",
            parentSizeFitter: false,
            target: panelTarget);
    }

    private static unsafe void ReadNetworkId(World world, Entity entity, out int a, out int b, out int c)
    {
        a = b = c = 0;
        if (entity == Entity.Null) return;
        try
        {
            var em = world.EntityManager;
            if (!em.Exists(entity)) return;
            var type = ComponentIndex.Of<NetworkId>(ComponentType.AccessMode.ReadOnly);
            if (!em.HasComponent(entity, type)) return;
            int* net = (int*)em.GetComponentDataRawRO(entity, type.TypeIndex);
            if (net == null) return;
            a = net[0];
            b = net[1];
            c = net[2];
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
            var rt = Rt(view.Inventory?.SelfSortButton?.gameObject);
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
            var tmp = view.FontDonor;
            if (Alive(tmp)) return Mathf.Max(18f, tmp!.fontSize * 1.35f);
        }
        catch { /* */ }
        return VanillaPanelPlacement.DefaultRowHeight;
    }

    // ------------------------------------------------------------------ build

    public IRuntimeWidget? BuildPanelContainer(VanillaUiSite site, VanillaPanelAnchor anchor, string containerName)
    {
        if (!PanelViews.TryGetValue(site, out var view) || view == null || !Alive(view.Panel)) return null;
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
                    host = view.RenameGroup;
                    if (Alive(view.RenameButton)) siblingIndex = view.RenameButton!.transform.GetSiblingIndex() + 1;
                    break;
                case VanillaPanelAnchor.BesideSort:
                    var sort = view.Inventory != null ? view.Inventory.SelfSortButton : null;
                    host = Alive(sort) ? sort!.transform.parent : null;
                    if (host != null) siblingIndex = sort!.transform.GetSiblingIndex() + 1;
                    break;
                case VanillaPanelAnchor.TooltipBody:
                    var frame = view.Tooltip != null ? view.Tooltip.DescriptionFrame : null;
                    host = Alive(frame) ? frame!.transform.parent : null;
                    if (host != null) siblingIndex = frame!.transform.GetSiblingIndex() + 1;
                    break;
                default:
                    host = null;
                    break;
            }

            if (host == null)
            {
                if (VanillaSiteHealth.ReportMissing(site, "anchor " + VanillaUiSites.AnchorKey(anchor)))
                    Plugin.Logger?.LogWarning(VanillaSiteHealth.Line(site, "anchor " + VanillaUiSites.AnchorKey(anchor)));
                return null;
            }

            go = new GameObject(containerName);
            var rt = go.AddComponent<RectTransform>();
            rt.SetParent(host, worldPositionStays: false);
            if (siblingIndex >= 0) rt.SetSiblingIndex(Mathf.Min(siblingIndex, host.childCount - 1));
            rt.localScale = Vector3.one;

            var size = DonorButtonSize(view);
            if (VanillaUiSites.IsEdge(anchor))
            {
                // Placed by us, ignored by whatever lays the window's halves out. Anchored at the
                // parent's pivot so anchoredPosition is the parent's local origin, which is the
                // space VanillaPanelPlacement computes in.
                var element = go.AddComponent<LayoutElement>();
                element.ignoreLayout = true;
                var pivot = host is RectTransform hostRt ? hostRt.pivot : new Vector2(0.5f, 0.5f);
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
                column.spacing = GroupSpacing(view.Inventory?.BottomBarButtonsParent?.transform, VanillaPanelPlacement.DefaultSpacing);
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
                var fit = go.AddComponent<ContentSizeFitter>();
                fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
                fit.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            }
            else
            {
                // Inline: laid out by the game's own group like one more of its buttons. Our row of
                // buttons asks for n donor widths plus the group's spacing.
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
                CopyFlexible(element, anchor == VanillaPanelAnchor.BesideSort ? view.Inventory?.SelfSortButton?.gameObject : view.RenameButton);
            }

            return new PanelContainerWidget(go, site, anchor);
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
        if (!PanelViews.TryGetValue(site, out var view) || view == null) return null;
        var donor = view.Inventory != null ? view.Inventory.SelfSortButton : null;
        if (!Alive(donor)) return null;

        GameObject? clone = null;
        try
        {
            clone = CloneButton(donor!.gameObject, host.GameObject!);
            if (clone == null) return null;
            clone.name = host.Name + "-button";

            // Buttons stack before rows in a column; in an inline group there are only buttons.
            clone.transform.SetSiblingIndex(host.ButtonCount);
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

            var element = clone.GetComponent<LayoutElement>() ?? clone.AddComponent<LayoutElement>();
            element.ignoreLayout = false;
            element.preferredWidth = size.x;
            element.preferredHeight = size.y;
            element.minHeight = size.y;

            HideButtonIcon(clone);
            var label = CaptionFor(clone, view, caption, size);
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

            clone.SetActive(true);
            host.ButtonCount++;
            return new PanelButtonWidget(clone, label, stun.onClick);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: attach " + VanillaUiSites.Key(site) + " button clone threw: " + e.Message);
            TryDestroyImmediate(clone);
            return null;
        }
    }

    /// <summary>
    /// The donor is an icon button. Keep every graphic the button's state transitions drive (its
    /// frame, its hover and press art) and switch the rest off: that is the icon.
    /// </summary>
    private static void HideButtonIcon(GameObject clone)
    {
        try
        {
            var stun = clone.GetComponent<SimpleStunButton>();
            if (stun == null) return;
            var keep = new HashSet<IntPtr>();
            if (stun.targetGraphic != null) keep.Add(stun.targetGraphic.Pointer);
            var targets = stun.TargetSettings;
            if (targets != null)
            {
                foreach (var t in targets)
                {
                    if (t == null || t.GraphicTargets == null) continue;
                    foreach (var g in t.GraphicTargets)
                        if (g != null) keep.Add(g.Pointer);
                }
            }

            foreach (var g in clone.GetComponentsInChildren<Graphic>(true))
            {
                if (g == null || keep.Contains(g.Pointer)) continue;
                if (g.TryCast<TextMeshProUGUI>() != null) continue;
                try { g.enabled = false; } catch { /* */ }
            }
        }
        catch { /* an icon left on is a cosmetic miss, not a broken button */ }
    }

    /// <summary>
    /// The caption: the donor's own TMP when it brought one, otherwise a clone of the window's own
    /// text (its font, material and color), stretched over the button and auto-sized down to fit.
    /// </summary>
    private static TextMeshProUGUI? CaptionFor(GameObject clone, PanelView view, string caption, Vector2 size)
    {
        var tmp = PrimaryLabel(clone, out _);
        if (tmp == null)
        {
            var donor = view.FontDonor;
            if (!Alive(donor)) return null;
            var go = UnityEngine.Object.Instantiate(donor!.gameObject);
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

            tmp = go.GetComponent<TextMeshProUGUI>();
            if (tmp == null) return null;
            go.SetActive(true);
        }

        var rt = tmp.rectTransform;
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = new Vector2(6f, 2f);
        rt.offsetMax = new Vector2(-6f, -2f);
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.enableWordWrapping = false;
        tmp.enableAutoSizing = true;
        tmp.fontSizeMin = 10f;
        tmp.fontSizeMax = Mathf.Max(12f, Mathf.Min(tmp.fontSize, size.y * 0.5f));
        tmp.raycastTarget = false;
        tmp.text = caption ?? "";
        tmp.enabled = true;
        return tmp;
    }

    // ------------------------------------------------------------------ rows

    private static IRuntimeWidget? ClonePanelRow(VanillaUiSite site, IRuntimeWidget container, string text)
    {
        if (container is not PanelContainerWidget host || !host.Alive) return null;

        // An inline group is a row of buttons; a text row inside it would read as a broken button.
        if (host.Anchor == VanillaPanelAnchor.BesideRename || host.Anchor == VanillaPanelAnchor.BesideSort) return null;
        if (!PanelViews.TryGetValue(site, out var view) || view == null) return null;
        var donor = view.FontDonor;
        if (!Alive(donor)) return null;

        GameObject? clone = null;
        try
        {
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
            if (host.Anchor != VanillaPanelAnchor.TooltipBody)
            {
                // In a column the row takes the caption style at a body size: left aligned,
                // wrapping inside the column's width.
                tmp.alignment = TextAlignmentOptions.Left;
                tmp.enableWordWrapping = true;
                tmp.fontSize = Mathf.Min(tmp.fontSize, RowHeightOf(view) / 1.35f);
            }

            foreach (var behaviour in clone.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (behaviour == null || behaviour.TryCast<TextMeshProUGUI>() != null) continue;
                if (behaviour.TryCast<LayoutElement>() != null || behaviour.TryCast<ContentSizeFitter>() != null) continue;
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
        if (!PanelViews.TryGetValue(site, out var view) || view == null || !Alive(view.Panel))
            return new VanillaFitResult(VanillaFit.Blocked, "panel");

        if (anchor == VanillaPanelAnchor.TooltipBody)
        {
            var parent = host.Transform != null ? host.Transform.parent : null;
            bool laid = false;
            try { laid = parent != null && parent.GetComponent<VerticalLayoutGroup>() != null; } catch { /* */ }
            return VanillaPanelPlacement.CheckTooltip(laid);
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
            float spacing = GroupSpacing(host.Transform, VanillaPanelPlacement.DefaultSpacing);
            int liveRows = rows < 0 ? 0 : rows;
            var column = VanillaPanelPlacement.ColumnSize(buttons, liveRows, size.x, size.y, RowHeightOf(view), spacing);

            var obstacles = new List<UiObstacle>();
            for (int i = 0; i < view.Obstacles.Count; i++)
            {
                var o = view.Obstacles[i];
                if (!Alive(o) || !o.activeInHierarchy) continue;
                obstacles.Add(new UiObstacle(SafeObjectName(o), RectIn(space, Rt(o))));
            }

            var canvas = view.Panel.GetComponentInParent<Canvas>();
            var root = canvas != null ? (canvas.rootCanvas != null ? canvas.rootCanvas : canvas) : null;
            var bounds = root != null ? RectIn(space, root.GetComponent<RectTransform>()) : default;

            var result = VanillaPanelPlacement.PlaceEdge(anchor, panelRect, gridTop, column.Width, column.Height, bounds, obstacles);
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

        // Inline: tell the group how wide we are, then check what it did with us.
        var donorSize = DonorButtonSize(view);
        var group = host.Transform != null ? host.Transform.parent : null;
        float groupSpacing = GroupSpacing(group, VanillaPanelPlacement.DefaultSpacing);
        try
        {
            var element = host.GameObject!.GetComponent<LayoutElement>();
            float want = VanillaPanelPlacement.InlineWidth(buttons, donorSize.x, groupSpacing);
            if (element != null && Mathf.Abs(element.preferredWidth - want) > 0.5f) element.preferredWidth = want;
        }
        catch { /* */ }

        var panelSpace = view.Panel!.transform;
        var panelLocal = RectIn(panelSpace, panelRt);
        var ours = RectIn(panelSpace, host.Rect);
        var members = new List<UiObstacle>();
        if (group != null)
        {
            for (int i = 0; i < group.childCount; i++)
            {
                var child = group.GetChild(i);
                if (child == null || child.Pointer == host.Transform!.Pointer || !child.gameObject.activeInHierarchy) continue;
                members.Add(new UiObstacle(SafeObjectName(child.gameObject), RectIn(panelSpace, child.GetComponent<RectTransform>())));
            }
        }

        return VanillaPanelPlacement.CheckInline(ours, panelLocal, members);
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
                + Describe("font", view.FontDonor)
                + (view.Tooltip != null ? Describe("tooltipFrame", view.Tooltip.DescriptionFrame != null ? view.Tooltip.DescriptionFrame.transform : null) : "")
                + " obstacles=" + view.Obstacles.Count);
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

        internal PanelContainerWidget(GameObject go, VanillaUiSite site, VanillaPanelAnchor anchor)
        {
            _go = go;
            Site = site;
            Anchor = anchor;
            Name = SafeObjectName(go);
        }

        internal VanillaUiSite Site { get; }

        internal VanillaPanelAnchor Anchor { get; }

        internal string Name { get; }

        internal int ButtonCount { get; set; }

        internal GameObject? GameObject => Alive(_go) ? _go : null;

        internal Transform? Transform
        {
            get { try { return Alive(_go) ? _go!.transform : null; } catch { return null; } }
        }

        internal RectTransform? Rect
        {
            get { try { return Alive(_go) ? _go!.GetComponent<RectTransform>() : null; } catch { return null; } }
        }

        public UiDestroyVerdict LastRelease => _verdict;

        public string Text { get => ""; set { } }

        public bool Visible
        {
            get { try { return Alive(_go) && _go!.activeSelf; } catch { return false; } }
            set { try { if (Alive(_go) && _go!.activeSelf != value) _go.SetActive(value); } catch { /* */ } }
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
            if (_verdict == UiDestroyVerdict.Destroyed) _go = null;
        }
    }

    /// <summary>One cloned game button inside a P6 container.</summary>
    private sealed class PanelButtonWidget : IRuntimeWidget, IReleaseVerdict
    {
        private GameObject? _go;
        private TextMeshProUGUI? _caption;
        private readonly object? _click;
        private UiDestroyVerdict _verdict = UiDestroyVerdict.Failed;

        internal PanelButtonWidget(GameObject go, TextMeshProUGUI caption, object? click)
        {
            _go = go;
            _caption = caption;
            _click = click;
        }

        public UiDestroyVerdict LastRelease => _verdict;

        public string Text
        {
            get { try { return Alive && _caption != null ? (_caption.text ?? "") : ""; } catch { return ""; } }
            set { try { if (Alive && _caption != null) _caption.text = value ?? ""; } catch { /* */ } }
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
