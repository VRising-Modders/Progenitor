#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using ProjectM.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Progenitor.Ui;

internal sealed partial class UnityUiRuntime
{
    public IRuntimeVanillaTabs? CreateVanillaTabs(VanillaUiSite site, string[] captions, Action<int> selected)
        => new NativePanelTabs(this, site, captions, selected);

    /// <summary>The live panel tab hosts, for <see cref="DescribeVanillaTabs"/>. Main thread.</summary>
    private static readonly List<NativePanelTabs> LiveVanillaTabs = new();

    /// <summary>
    /// <c>ui_read panels</c> → <c>tabs</c>: each <c>Ui.AttachPanelTabs</c> strip with its window kind,
    /// screen rect, tab height (and whether it came from the donor tab), its placement and the
    /// self-check state (<c>ok</c>, <c>degenerate</c>, <c>clipped</c> or <c>covered</c>). Main thread.
    /// </summary>
    internal static List<object?> DescribeVanillaTabs()
    {
        var list = new List<object?>();
        foreach (var tabs in LiveVanillaTabs.ToArray())
        {
            try { list.Add(tabs.Describe()); }
            catch (Exception e) { list.Add(new Dictionary<string, object?> { ["error"] = e.Message }); }
        }

        return list;
    }

    private sealed class NativePanelTabs : IRuntimeVanillaTabs
    {
        readonly UnityUiRuntime _runtime;
        readonly VanillaUiSite _site;
        readonly string[] _captions;
        readonly Action<int> _changed;
        readonly List<(GameObject go, ushort slot)> _buttons = new();
        readonly List<HiddenNative> _content = new();
        readonly List<HiddenNative> _nativeTabs = new();
        GameObject? _strip;
        RectTransform? _root;
        InventoryRoutingParentUI? _routing;
        UnityPanel? _panel;
        Transform? _panelParent;
        int _instance, _selected;
        float _top;
        Transform? _layoutParent;
        bool _flow;
        RectTransform? _bonusAnchor;
        readonly Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Vector3> _corners = new(4);
        bool _redistribution;
        VanillaPanelKind _kind;
        float _anchorTop;
        RectTransform? _tabDonorRect, _stripRect;
        VanillaTabStripHeight? _height;
        float _tabHeight;
        VanillaTabStripPlacement _placement;
        readonly List<(string name, Behaviour mask, RectTransform rect)> _masks = new();
        readonly List<(string Name, ScreenRect Rect)> _maskRects = new(), _above = new();
        VanillaTabStripCheck _check = new("unchecked", "", 0f);
        int _checkFrame = -1000;
        readonly VanillaTabStripLogGate _logGate = new();

        internal NativePanelTabs(UnityUiRuntime runtime, VanillaUiSite site, string[] captions, Action<int> changed)
        { _runtime = runtime; _site = site; _captions = captions; _changed = changed; LiveVanillaTabs.Add(this); }

        public bool ShowRedistribution
        {
            get => _redistribution;
            set { if (_redistribution != value) { _redistribution = value; Release(); } }
        }

        public void Update(in VanillaSiteSample sample)
        {
            if (!sample.Resolved) { Release(); return; }
            if (_instance != sample.InstanceId || !Alive(_strip))
            {
                Release();
                if (!PanelViews.TryGetValue(_site, out var view) || !Alive(view.Panel) || view.Grid == null) return;
                _root = view.Panel!.GetComponent<RectTransform>();
                if (_root == null) return;
                _instance = sample.InstanceId;
                var chest = view.Panel.GetComponent<ContainerSubMenu>();
                var bench = view.Panel.GetComponent<WorkstationSubMenu>();
                var refiner = view.Panel.GetComponent<RefinementstationSubMenu>();
                _routing = bench != null ? bench.InventoryRoutingParent : refiner != null ? refiner.InventoryRoutingParent : null;
                // Any other station window draws the strip only in place of its own station/redistribution
                // row (VanillaStationWindows.HostsTabs); never over a window that has none.
                RectTransform? specialBonus = null;
                if (chest == null && bench == null && refiner == null && !SpecialStation(sample.Target.Kind, view.Panel, _content, out _routing, out specialBonus))
                { Release(); return; }
                var anchor = _routing != null && _routing.StationTabButton != null
                    ? _routing.StationTabButton.GetComponent<RectTransform>() : view.Grid;
                _bonusAnchor = bench != null ? RectOf(bench.BonusesParent)
                    : refiner != null ? RectOf(refiner.BonusesParent) : specialBonus;
                anchor.GetWorldCorners(_corners);
                _top = _anchorTop = _root.InverseTransformPoint(_corners[1]).y;
                _kind = sample.Target.Kind;
                _flow = chest != null;
                _layoutParent = _flow ? view.Grid.transform.parent : _root;
                if (_layoutParent == null) { Release(); return; }
                if (chest != null)
                {
                    HideTarget(_content, chest.EquipmentItemsParent);
                    HideTarget(_content, chest.ButtonsParent);
                }
                if (_routing != null)
                {
                    foreach (var t in _routing.StationActiveTransforms) HideTarget(_content, t);
                    foreach (var t in _routing.RoutingActiveTransforms) HideTarget(_content, t);
                    HideTarget(_nativeTabs, anchor.transform.parent);
                }
                if (refiner != null)
                {
                    HideTarget(_content, refiner.ItemInputsParent.transform.parent);
                    HideTarget(_content, refiner.ItemOutputsParent.transform.parent);
                    HideTarget(_content, refiner.RefinementProgress);
                    HideTarget(_content, refiner.StationStatusText);
                }
                if (bench != null)
                {
                    HideTarget(_content, bench.ItemOutputsParent.transform.parent);
                    HideTarget(_content, bench.StationStatusText);
                }
                _strip = new GameObject("Progenitor.ContainerTabs");
                _strip.transform.SetParent(_layoutParent, false);
                if (_flow) _strip.transform.SetSiblingIndex(view.Grid.transform.GetSiblingIndex());
                var stripRect = TryAdd<RectTransform>(_strip);
                if (stripRect == null) { Release(); return; }
                stripRect.anchorMin = stripRect.anchorMax = _root.pivot;
                stripRect.pivot = new Vector2(.5f, 1f);
                stripRect.anchoredPosition = new Vector2(_root.rect.center.x, _top);
                stripRect.sizeDelta = new Vector2(_root.rect.width - 40f, 42f);
                var layout = TryAdd<LayoutElement>(_strip);
                if (layout != null) { layout.preferredWidth = _root.rect.width - 40; layout.preferredHeight = 42; layout.minHeight = 42; layout.ignoreLayout = !_flow; }
                int count = _captions.Length + (_routing != null && _redistribution ? 1 : 0);
                float width = stripRect.sizeDelta.x / count;
                GameObject? tabDonor = null;
                if (view.Inventory != null && view.Inventory.TabButtons != null)
                    foreach (var tab in view.Inventory.TabButtons)
                        if (Alive(tab)) { tabDonor = tab.gameObject; break; }
                // The donor's height is read again until it is usable (RefreshTabHeight). A tab read
                // before its layout ran is zero tall; the clipped prison cell captions fit a
                // zero-height strip (inferred from screenshots; confirm with tabHeightSource).
                _tabDonorRect = tabDonor != null ? tabDonor.GetComponent<RectTransform>() : null;
                var donorCaption = tabDonor != null ? tabDonor.GetComponentInChildren<TextMeshProUGUI>(true) : null;
                _height = new VanillaTabStripHeight(_tabDonorRect != null ? _tabDonorRect.rect.height : float.NaN,
                    donorCaption != null ? donorCaption.fontSize * 1.25f : 0f, Time.frameCount);
                float tabHeight = _tabHeight = _height.Height;
                stripRect.sizeDelta = new Vector2(stripRect.sizeDelta.x, tabHeight);
                if (layout != null) { layout.preferredHeight = tabHeight; layout.minHeight = tabHeight; }
                if (tabDonor != null)
                {
                    // These two sibling images are the inventory tab row's native silver baseline.
                    var donorParent = tabDonor.transform.parent;
                    var donorWidth = donorParent.GetComponent<RectTransform>().rect.width;
                    foreach (var graphic in donorParent.GetComponentsInChildren<Image>(true))
                    {
                        if (graphic.transform.parent != donorParent || graphic.sprite == null
                            || (graphic.name != "TabGradient" && graphic.name != "TabDivider")) continue;
                        var trim = UnityEngine.Object.Instantiate(graphic.gameObject, _strip.transform, false);
                        var trimRect = trim.GetComponent<RectTransform>();
                        var donorRect = graphic.GetComponent<RectTransform>();
                        trimRect.anchorMin = new Vector2(0, 0); trimRect.anchorMax = new Vector2(1, 0);
                        trimRect.pivot = new Vector2(.5f, 0);
                        trimRect.anchoredPosition = new Vector2(0, donorRect.anchoredPosition.y);
                        trimRect.sizeDelta = new Vector2(donorRect.rect.width - donorWidth, donorRect.rect.height);
                        trim.GetComponent<Image>().raycastTarget = false;
                        trim.SetActive(true);
                    }
                }
                for (int i = 0; i < count; i++)
                {
                    var button = tabDonor != null ? CloneButton(tabDonor, _strip) : CloneWorkspaceTab(_strip);
                    if (button == null) { Release(); return; }
                    var labelGo = SetButtonText(button, i < _captions.Length ? _captions[i] : "Redistribution");
                    var label = labelGo?.GetComponent<TextMeshProUGUI>();
                    var rt = button.GetComponent<RectTransform>();
                    if (label == null || rt == null) { TryDestroy(button, false); Release(); return; }
                    button.name = "tab-" + i;
                    foreach (var graphic in button.GetComponentsInChildren<Graphic>(true)) graphic.raycastTarget = false;
                    var hit = button.GetComponent<Image>();
                    if (hit == null) { hit = button.AddComponent<Image>(); hit.color = Color.clear; }
                    hit.raycastTarget = true;
                    var captionRect = label.GetComponent<RectTransform>();
                    captionRect.anchorMin = Vector2.zero; captionRect.anchorMax = Vector2.one;
                    captionRect.offsetMin = new Vector2(6, 0); captionRect.offsetMax = new Vector2(-6, 0);
                    // The inventory tab supplies the actual character-window font and size.
                    // Center within our bounded cell without enlarging short captions.
                    label.enableAutoSizing = false;
                    label.alignment = TextAlignmentOptions.Center;
                    rt.anchorMin = rt.anchorMax = new Vector2(0, 1);
                    rt.pivot = new Vector2(0, 1);
                    rt.anchoredPosition = new Vector2(width * i, 0);
                    rt.sizeDelta = new Vector2(width - 3, tabHeight);
                    rt.localScale = Vector3.one;
                    var widget = new PanelButtonWidget(button, label, FindClickEvent(button), rt.sizeDelta);
                    ushort slot = UiSlotTable.Alloc(this);
                    int index = i;
                    UiSlotTable.BindClick(slot, () =>
                    {
                        if (index < _captions.Length) _changed(index);
                        else { _changed(0); Select(index); }
                    });
                    _buttons.Add((button, slot));
                    if (!_runtime.TryWire(slot, UiSlotKind.Click, widget)) { Release(); return; }
                    Activate(button);
                }
                var trims = new List<Transform>();
                for (int i = 0; i < _strip.transform.childCount; i++)
                {
                    var child = _strip.transform.GetChild(i);
                    if (child.name.StartsWith("TabGradient") || child.name.StartsWith("TabDivider")) trims.Add(child);
                }
                foreach (var trim in trims) trim.SetAsLastSibling();
                _stripRect = stripRect;
                CollectMasks();
                _check = new VanillaTabStripCheck("unchecked", "", 0f);
                _checkFrame = -1000;
                _logGate.Reset();
            }
            bool moved = RefreshTabHeight();
            if (!_flow && _root != null && _strip != null) moved |= PlaceStrip();
            foreach (var item in _nativeTabs) item.Hide();
            ApplySelection();
            PlacePanel();
            if (VanillaTabStripLayout.CheckDue(moved, Time.frameCount, _checkFrame)) SelfCheck();
        }

        /// <summary>
        /// Re-read the donor tab's height and resize the strip and its tabs when the usable height
        /// changed (<see cref="VanillaTabStripLayout.TabHeight"/>). True when it changed.
        /// </summary>
        bool RefreshTabHeight()
        {
            if (_strip == null || _stripRect == null || _height == null) return false;
            int frame = Time.frameCount;
            if (!_height.Due(frame)) return false;
            float measured = Alive(_tabDonorRect) ? _tabDonorRect!.rect.height : float.NaN;
            if (!_height.Read(measured, frame)) return false;
            float height = _tabHeight = _height.Height;
            _stripRect.sizeDelta = new Vector2(_stripRect.sizeDelta.x, height);
            var layout = _strip.GetComponent<LayoutElement>();
            if (layout != null) { layout.preferredHeight = height; layout.minHeight = height; }
            foreach (var (go, _) in _buttons)
            {
                if (!Alive(go)) continue;
                var rt = go.GetComponent<RectTransform>();
                if (rt != null) rt.sizeDelta = new Vector2(rt.sizeDelta.x, height);
            }

            return true;
        }

        /// <summary>
        /// Place a station strip (absolute, not in a layout): under the bonus row, never above the
        /// tightest parent mask, and drawn after every sibling. True when its top moved.
        /// </summary>
        bool PlaceStrip()
        {
            float? bonusBottom = null;
            if (Alive(_bonusAnchor))
            {
                _bonusAnchor!.GetWorldCorners(_corners);
                bonusBottom = _root!.InverseTransformPoint(_corners[0]).y;
            }

            var placement = VanillaTabStripLayout.Place(_anchorTop, bonusBottom, VisibleTop(), _tabHeight);
            bool moved = Math.Abs(placement.Top - _placement.Top) > .5f || Math.Abs(placement.Height - _placement.Height) > .5f;
            _placement = placement;
            _top = placement.Top;
            _stripRect!.anchoredPosition = new Vector2(_root!.rect.center.x, _top);
            // Drawn after every sibling of the window body, so no panel of the window covers it.
            if (_layoutParent != null && _strip!.transform.GetSiblingIndex() != _layoutParent.childCount - 1)
                _strip.transform.SetAsLastSibling();
            return moved;
        }

        /// <summary>The enabled masks (RectMask2D, Mask) on the strip's parent chain, cached once per strip.</summary>
        void CollectMasks()
        {
            _masks.Clear();
            for (var t = _strip != null ? _strip.transform.parent : null; t != null; t = t.parent)
            {
                var soft = t.GetComponent<RectMask2D>();
                if (soft != null) _masks.Add(("RectMask2D " + t.name, soft, soft.rectTransform));
                var stencil = t.GetComponent<Mask>();
                if (stencil != null) _masks.Add(("Mask " + t.name, stencil, stencil.rectTransform));
            }
        }

        /// <summary>The lowest top edge of the enabled parent masks, in the root's local units; null without a mask.</summary>
        float? VisibleTop()
        {
            float? top = null;
            foreach (var (_, mask, rect) in _masks)
            {
                if (!Alive(mask) || !mask.isActiveAndEnabled || !Alive(rect)) continue;
                rect.GetWorldCorners(_corners);
                float y = _root!.InverseTransformPoint(_corners[1]).y;
                if (top == null || y < top) top = y;
            }

            return top;
        }

        /// <summary>
        /// The runtime self-check (<see cref="VanillaTabStripLayout.Check"/>): is the strip tall enough,
        /// inside every parent mask, and not under a later sibling or an override-sorted canvas? It
        /// only reports (logged once per strip, read back by <c>ui_read panels</c>, <c>tabs</c>); it
        /// never re-sorts the strip, since a coverer can be a transient popup or list.
        /// </summary>
        void SelfCheck()
        {
            _checkFrame = Time.frameCount;
            if (_strip == null || _stripRect == null || _root == null || !_strip.activeInHierarchy) return;
            var strip = ScreenRectOf(_stripRect);
            _maskRects.Clear();
            foreach (var (name, mask, rect) in _masks)
                if (Alive(mask) && mask.isActiveAndEnabled && Alive(rect)) _maskRects.Add((name, ScreenRectOf(rect)));

            _above.Clear();
            if (!_flow && _layoutParent != null)
            {
                for (int i = _strip.transform.GetSiblingIndex() + 1; i < _layoutParent.childCount; i++)
                {
                    var sibling = _layoutParent.GetChild(i);
                    var siblingRect = sibling != null ? sibling.GetComponent<RectTransform>() : null;
                    if (siblingRect != null && Shows(sibling!)) _above.Add(("sibling " + sibling!.name, ScreenRectOf(siblingRect)));
                }
            }

            var stripCanvas = _strip.GetComponentInParent<Canvas>();
            int stripOrder = stripCanvas != null ? stripCanvas.sortingOrder : 0;
            foreach (var canvas in _root.GetComponentsInChildren<Canvas>(false))
            {
                if (canvas == null || !canvas.overrideSorting || !canvas.isActiveAndEnabled) continue;
                if (stripCanvas != null && canvas.Pointer == stripCanvas.Pointer) continue;
                if (canvas.transform.IsChildOf(_strip.transform) || _strip.transform.IsChildOf(canvas.transform)) continue;
                if (canvas.sortingOrder < stripOrder || !Shows(canvas.transform)) continue;
                var canvasRect = canvas.GetComponent<RectTransform>();
                if (canvasRect == null) continue;
                _above.Add(("canvas " + canvas.name + " order " + canvas.sortingOrder, ScreenRectOf(canvasRect)));
            }

            float minHeight = Math.Max(1f, VanillaTabStripLayout.MinTabHeight * .5f * CanvasScale(_stripRect));
            var check = _check = VanillaTabStripLayout.Check(strip, _maskRects, _above, minHeight);
            if (!_logGate.ShouldLog(check)) return;
            Plugin.Logger?.LogWarning("Ui: panel tabs on " + _kind + " " + check.State + " (" + check.By + "), visible "
                + Math.Round(check.Visible * 100) + "%, strip " + RectText(strip) + ", tab height " + Math.Round(_tabHeight, 1)
                + (_height != null && _height.FromDonor ? " (donor)" : " (fallback)"));
        }

        /// <summary>The strip's canvas scale (screen pixels per canvas unit); 1 when unreadable.</summary>
        static float CanvasScale(RectTransform rt)
        {
            var r = ScreenRectOf(rt);
            float h = rt.rect.height;
            return r.IsKnown && h > .01f ? Math.Max(.01f, (r.MaxY - r.MinY) / h) : 1f;
        }

        /// <summary>Drawn: active, and no CanvasGroup up to the window root hides it.</summary>
        bool Shows(Transform t)
        {
            if (t == null || !t.gameObject.activeInHierarchy) return false;
            for (var p = t; p != null; p = p.parent)
            {
                var group = p.GetComponent<CanvasGroup>();
                if (group != null && group.alpha <= .01f) return false;
                if (_root != null && p.Pointer == _root.transform.Pointer) break;
            }

            return true;
        }

        static string RectText(ScreenRect r) => !r.IsKnown ? "unknown"
            : Math.Round(r.MinX) + "," + Math.Round(r.MinY) + " " + Math.Round(r.MaxX - r.MinX) + "x" + Math.Round(r.MaxY - r.MinY);

        internal Dictionary<string, object?> Describe()
        {
            bool shown = Alive(_strip) && _strip!.activeInHierarchy;
            return new Dictionary<string, object?>
            {
                ["site"] = _site.ToString(),
                ["kind"] = _kind.ToString(),
                ["shown"] = shown,
                ["captions"] = _captions.Length,
                ["selected"] = _selected,
                ["layout"] = _flow ? "flow" : "placed",
                ["rect"] = shown && _stripRect != null ? RectDict(ScreenRectOf(_stripRect)) : null,
                ["tabHeight"] = Math.Round(_tabHeight, 1),
                ["tabHeightSource"] = _height != null && _height.FromDonor ? "donor" : "fallback",
                ["top"] = _flow ? null : Math.Round(_placement.Top, 1),
                ["panelTop"] = _flow ? null : Math.Round(_placement.PanelTop, 1),
                ["clamped"] = !_flow && _placement.Clamped,
                ["masks"] = _masks.Count,
                ["clip"] = _check.State,
                ["clipBy"] = _check.By,
                ["visible"] = Math.Round(_check.Visible, 3),
            };
        }

        /// <summary>
        /// The prison cell and devourer windows: their tab row, bonuses and the content a custom tab
        /// hides. Each window is read in its own non-inlined method, called inside a try/catch, so a
        /// member the game renames on one of them fails only that window's tabs (logged once).
        /// False when the window has no tab row (or failed): no strip is drawn.
        /// </summary>
        static bool SpecialStation(VanillaPanelKind kind, GameObject panel, List<HiddenNative> content,
            out InventoryRoutingParentUI? routing, out RectTransform? bonus)
        {
            routing = null;
            bonus = null;
            if (VanillaStationWindows.IsFailed(kind)) return false;
            try
            {
                if (kind == VanillaPanelKind.PrisonCell) PrisonTabs(panel, content, out routing, out bonus);
                else if (kind == VanillaPanelKind.SalvageStation) SalvageTabs(panel, content, out routing, out bonus);
            }
            catch (Exception e)
            {
                if (VanillaStationWindows.MarkFailed(kind))
                    Plugin.Logger?.LogWarning("Ui: StationPanel window " + kind + " unavailable (tabs): " + e.GetType().Name + ": " + e.Message);
                routing = null;
                bonus = null;
                return false;
            }

            return routing != null;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static void PrisonTabs(GameObject panel, List<HiddenNative> content, out InventoryRoutingParentUI? routing, out RectTransform? bonus)
        {
            var prison = panel.GetComponent<PrisonSubMenu>();
            routing = prison != null ? prison.InventoryRoutingParent : null;
            bonus = prison != null ? RectOf(prison.BonusesParent) : null;
            if (prison == null || routing == null) return;
            // The prisoner block (full or empty cell), the recipe grid, the cell's slots and the
            // status line, beside what the routing row lists as station content. Hiding a piece the
            // row already hides is harmless (a second CanvasGroup state, restored in order).
            HideTarget(content, prison.Parent_FullCell != null ? prison.Parent_FullCell.transform : null);
            HideTarget(content, prison.Parent_EmptyCell != null ? prison.Parent_EmptyCell.transform : null);
            HideTarget(content, prison.RecipesParent);
            HideTarget(content, prison.ItemOutputsParent != null ? prison.ItemOutputsParent.transform.parent : null);
            HideTarget(content, prison.StationStatusText);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static void SalvageTabs(GameObject panel, List<HiddenNative> content, out InventoryRoutingParentUI? routing, out RectTransform? bonus)
        {
            var salvage = panel.GetComponent<SalvagestationSubMenu>();
            routing = salvage != null ? salvage.InventoryRoutingParent : null;
            bonus = salvage != null ? RectOf(salvage.BonusesParent) : null;
            if (salvage == null || routing == null) return;
            HideTarget(content, salvage.ItemInputsParent != null ? salvage.ItemInputsParent.transform.parent : null);
            HideTarget(content, salvage.ItemOutputsParent != null ? salvage.ItemOutputsParent.transform.parent : null);
            HideTarget(content, salvage.RefinementProgress);
            HideTarget(content, salvage.StationStatusText);
            HideTarget(content, salvage.ToggleButton_Active);
            HideTarget(content, salvage.ToggleButton_Inactive);
            HideTarget(content, salvage.ToggleButtonText);
            HideTarget(content, salvage.OpenTagEditButton);
        }

        static void HideTarget(List<HiddenNative> list, Component? target)
        { if (target != null) list.Add(new HiddenNative(target.gameObject)); }

        public void Select(int index)
        {
            _selected = index;
            if (_panel != null) _panel.GameObject.SetActive(false);
            ApplySelection();
        }

        void ApplySelection()
        {
            bool custom = _selected > 0 && _selected < _captions.Length;
            foreach (var item in _content) { if (custom) item.Hide(); else item.Restore(); }
            if (_routing != null)
            {
                bool routing = _selected == _captions.Length;
                if (_routing._RoutingActive != routing) _routing.SetRoutingActive(routing);
            }
            for (int i = 0; i < _buttons.Count; i++)
                PaintWorkspaceTab(_buttons[i].go, i == _selected ? MenuRowTone.Selected : MenuRowTone.Catalog);
        }

        public void Mount(IRuntimePanel panel)
        {
            if (panel is not UnityPanel next || _root == null) return;
            if (_panel != next)
            {
                ParkPanel();
                _panel = next;
                _panelParent = next.GameObject.transform.parent;
                next.GameObject.transform.SetParent(_layoutParent, false);
                next.GameObject.transform.SetSiblingIndex(_strip!.transform.GetSiblingIndex() + 1);
            }
            PlacePanel();
        }

        void PlacePanel()
        {
            if (_panel == null || _root == null || _stripRect == null || !_panel.Alive) return;
            float height = Math.Min(680, _root.rect.height - 220);
            // Keep custom content clear of the lower HUD. Measure after native layout settles,
            // converting screen clearance back to the tab row's canvas units.
            var stripRect = _stripRect;
            stripRect.GetWorldCorners(_corners);
            float scale = Math.Abs(_corners[1].y - _corners[0].y) / Math.Max(1f, stripRect.rect.height);
            if (scale > .01f)
            {
                float room = (_corners[0].y - Screen.height * .15f) / scale - 6f;
                height = Math.Max(80f, Math.Min(height, room));
            }
            _panel.Embed(_root.rect.width - 40, height);
            var rect = _panel.GameObject.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(_root.rect.width - 40, height);
            var layout = _panel.GameObject.GetComponent<LayoutElement>() ?? _panel.GameObject.AddComponent<LayoutElement>();
            layout.preferredWidth = _root.rect.width - 40;
            layout.preferredHeight = height;
            layout.minHeight = height;
            layout.ignoreLayout = !_flow;
            if (!_flow)
            {
                rect.anchorMin = rect.anchorMax = _root.pivot;
                rect.pivot = new Vector2(.5f, 1);
                rect.anchoredPosition = new Vector2(_root.rect.center.x, _placement.PanelTop);
                _strip!.transform.SetAsLastSibling();
            }
        }

        void ParkPanel()
        {
            if (_panel != null && _panel.Alive)
            {
                _panel.GameObject.SetActive(false);
                if (Alive(_panelParent)) _panel.GameObject.transform.SetParent(_panelParent, false);
            }
            _panel = null;
            _panelParent = null;
        }

        void Release()
        {
            ParkPanel();
            foreach (var item in _content) item.Dispose();
            foreach (var item in _nativeTabs) item.Dispose();
            _content.Clear(); _nativeTabs.Clear();
            foreach (var (_, slot) in _buttons) { _runtime.Unwire(slot); UiSlotTable.Release(slot); }
            _buttons.Clear();
            if (_strip != null) TryDestroy(_strip, false);
            _strip = null; _root = null; _routing = null; _instance = 0;
            _tabDonorRect = null; _stripRect = null; _height = null; _bonusAnchor = null; _masks.Clear();
            _placement = default; _tabHeight = 0f; _logGate.Reset();
        }

        public void Dispose()
        {
            Release();
            LiveVanillaTabs.Remove(this);
        }

        // Keep the native content live for its mapper, but remove its drawing and hit testing.
        // Restore the exact original CanvasGroup state, including on unload or target change.
        sealed class HiddenNative : IDisposable
        {
            readonly CanvasGroup _group;
            readonly LayoutElement _layout;
            readonly bool _ownLayout, _ignoreLayout;
            readonly bool _owned;
            readonly float _alpha;
            readonly bool _interactable, _blocks;
            bool _hidden;
            internal HiddenNative(GameObject go)
            {
                _layout = go.GetComponent<LayoutElement>();
                _ownLayout = _layout == null;
                if (_ownLayout) _layout = go.AddComponent<LayoutElement>();
                _ignoreLayout = _layout.ignoreLayout;
                _group = go.GetComponent<CanvasGroup>();
                _owned = _group == null;
                if (_owned) _group = go.AddComponent<CanvasGroup>();
                _alpha = _group.alpha; _interactable = _group.interactable; _blocks = _group.blocksRaycasts;
            }
            internal void Hide()
            { if (Alive(_group)) { _layout.ignoreLayout = true; _group.alpha = 0; _group.interactable = false; _group.blocksRaycasts = false; _hidden = true; } }
            internal void Restore()
            { if (_hidden && Alive(_group)) { _layout.ignoreLayout = _ignoreLayout; _group.alpha = _alpha; _group.interactable = _interactable; _group.blocksRaycasts = _blocks; _hidden = false; } }
            public void Dispose() { Restore(); if (_ownLayout && Alive(_layout)) UnityEngine.Object.Destroy(_layout); if (_owned && Alive(_group)) UnityEngine.Object.Destroy(_group); }
        }
    }
}
#endif
