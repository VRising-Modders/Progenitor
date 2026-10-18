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
        // Each tab's caption, in _buttons order; kept centered by KeepCaptions.
        readonly List<TextMeshProUGUI> _labels = new();
        Dictionary<string, object?>? _captionDonor;
        int _captionFixes;
        // Each tab's frame art (and the objects between it and the tab), refitted to the tab by AdoptArt.
        readonly List<TabArt> _art = new();
        bool _artDriverLogged;
        // The strip's baseline trim (drawn after the tabs), for the caption's visible band.
        readonly List<RectTransform> _trimRects = new();
        // Each trim with the donor row image and row it copies, re-placed until the row is laid out.
        readonly List<(RectTransform Trim, RectTransform Donor, RectTransform Row)> _trims = new();
        int _trimMoves;
        // The donor tab's own caption, read live for the readback.
        TextMeshProUGUI? _donorCaption;
        List<object?>? _artDonor;
        int _artFixes;
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
                _donorCaption = donorCaption;
                _height = new VanillaTabStripHeight(_tabDonorRect != null ? _tabDonorRect.rect.height : float.NaN,
                    donorCaption != null ? donorCaption.fontSize * 1.25f : 0f, Time.frameCount);
                float tabHeight = _tabHeight = _height.Height;
                stripRect.sizeDelta = new Vector2(stripRect.sizeDelta.x, tabHeight);
                if (layout != null) { layout.preferredHeight = tabHeight; layout.minHeight = tabHeight; }
                if (tabDonor != null)
                {
                    // These two sibling images are the inventory tab row's native silver baseline.
                    var donorParent = tabDonor.transform.parent;
                    foreach (var graphic in donorParent.GetComponentsInChildren<Image>(true))
                    {
                        if (graphic.transform.parent != donorParent || graphic.sprite == null
                            || (graphic.name != "TabGradient" && graphic.name != "TabDivider")) continue;
                        var trim = UnityEngine.Object.Instantiate(graphic.gameObject, _strip.transform, false);
                        var trimRect = trim.GetComponent<RectTransform>();
                        var donorRect = graphic.GetComponent<RectTransform>();
                        trimRect.anchorMin = new Vector2(0, 0); trimRect.anchorMax = new Vector2(1, 0);
                        trimRect.pivot = new Vector2(.5f, 0);
                        _trims.Add((trimRect, donorRect, donorParent.GetComponent<RectTransform>()));
                        PlaceTrim(trimRect, donorRect, donorParent.GetComponent<RectTransform>());
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
                    // Pivoted at its center: a donor image that preserves its sprite's aspect places
                    // its art by the pivot, so a corner pivot drew it against one edge of the tab.
                    rt.anchorMin = rt.anchorMax = new Vector2(0, 1);
                    rt.pivot = new Vector2(.5f, .5f);
                    rt.anchoredPosition = new Vector2(width * i + (width - 3) * .5f, -tabHeight * .5f);
                    rt.sizeDelta = new Vector2(width - 3, tabHeight);
                    rt.localScale = Vector3.one;
                    // The inventory tab supplies the actual character-window font and size. The
                    // caption is centered in our bounded cell without enlarging short captions.
                    if (i == 0) _captionDonor = DescribeDonorCaption(label, button);
                    AdoptCaption(label, button);
                    _labels.Add(label);
                    if (i == 0) _artDonor = DescribeImages(button, label, null, rt);
                    var art = AdoptArt(button, label, _tabDonorRect);
                    _art.Add(art);
                    if (art.Refused.Count > 0 && !_artDriverLogged)
                    {
                        _artDriverLogged = true;
                        Plugin.Logger?.LogWarning("Ui: panel tab art left as the donor drew it, driven by " + string.Join(", ", art.Refused));
                    }
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
                foreach (var trim in trims)
                {
                    trim.SetAsLastSibling();
                    var trimRect = trim.GetComponent<RectTransform>();
                    if (trimRect != null) _trimRects.Add(trimRect);
                }
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
            KeepCaptions();
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
            // The baseline trims copy the donor row's geometry: read it again with the height, so a
            // row read before the game laid it out does not leave the trim over the tabs' bottom edge.
            PlaceTrims();
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
                if (rt == null) continue;
                rt.sizeDelta = new Vector2(rt.sizeDelta.x, height);
                rt.anchoredPosition = new Vector2(rt.anchoredPosition.x, -height * .5f);
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
            List<object?>? tabs = null;
            bool? centered = null;
            if (shown) { centered = CaptionsCentered(out var list); tabs = list; }
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
                ["captionsCentered"] = centered,
                ["tabCaptions"] = tabs,
                ["captionDonor"] = _captionDonor,
                ["captionFixes"] = _captionFixes,
                ["captionBand"] = CaptionBandDict(),
                ["donorCaption"] = DonorCaptionNow(),
                ["artDonor"] = _artDonor,
                ["artFixes"] = _artFixes,
                ["stripArt"] = shown ? StripArt() : null,
            };
        }

        /// <summary>
        /// Make the donor's caption a direct child of its tab and center it there
        /// (<see cref="VanillaTabCaption"/>): the donor's label can sit under a parent sized for the
        /// character window's tab and carry its own pivot, margins and wrapping, each of which moves
        /// the text off center in a tab of another size.
        /// </summary>
        static void AdoptCaption(TextMeshProUGUI label, GameObject tab)
        {
            var t = label.transform;
            if (t.parent == null || t.parent.Pointer != tab.transform.Pointer) t.SetParent(tab.transform, false);
            t.SetAsLastSibling();
            label.enableAutoSizing = false;
            try { label.enableWordWrapping = false; } catch { /* */ }
            PlaceCaption(label, tab.GetComponent<RectTransform>(), 0f, 0f);
        }

        /// <summary>
        /// Stretch the caption over its tab, <see cref="VanillaTabCaption.InsetFor"/> in from each
        /// side, pivoted at its center, unrotated, unscaled, centered both ways and without margins.
        /// Writes only what drifted; true when anything did.
        /// </summary>
        static bool PlaceCaption(TextMeshProUGUI label, RectTransform? tab, float bandBottom, float bandTop)
        {
            if (tab == null) return false;
            var c = label.rectTransform;
            float inset = VanillaTabCaption.InsetFor(tab.rect.width);
            bool fixedAny = false;
            if (Moved(c.anchorMin, Vector2.zero)) { c.anchorMin = Vector2.zero; fixedAny = true; }
            if (Moved(c.anchorMax, Vector2.one)) { c.anchorMax = Vector2.one; fixedAny = true; }
            if (Moved(c.pivot, new Vector2(.5f, .5f))) { c.pivot = new Vector2(.5f, .5f); fixedAny = true; }
            if (Moved(c.offsetMin, new Vector2(inset, bandBottom))) { c.offsetMin = new Vector2(inset, bandBottom); fixedAny = true; }
            if (Moved(c.offsetMax, new Vector2(-inset, -bandTop))) { c.offsetMax = new Vector2(-inset, -bandTop); fixedAny = true; }
            var p = c.localPosition;
            if (VanillaTabCaption.Drifted(p.z, 0f)) { c.localPosition = new Vector3(p.x, p.y, 0f); fixedAny = true; }
            if (c.localRotation != Quaternion.identity) { c.localRotation = Quaternion.identity; fixedAny = true; }
            var s = c.localScale;
            if (VanillaTabCaption.Drifted(s.x, 1f) || VanillaTabCaption.Drifted(s.y, 1f) || VanillaTabCaption.Drifted(s.z, 1f))
            { c.localScale = Vector3.one; fixedAny = true; }
            if (label.alignment != TextAlignmentOptions.Center) { label.alignment = TextAlignmentOptions.Center; fixedAny = true; }
            var m = label.margin;
            if (m.x != 0f || m.y != 0f || m.z != 0f || m.w != 0f) { label.margin = Vector4.zero; fixedAny = true; }
            return fixedAny;

            static bool Moved(Vector2 now, Vector2 wanted)
                => VanillaTabCaption.Drifted(now.x, wanted.x) || VanillaTabCaption.Drifted(now.y, wanted.y);
        }

        /// <summary>
        /// The caption's vertical band in the tab, as insets (bottom, top) in canvas units: the part
        /// of the tab the strip's baseline trim, drawn after the tabs, leaves visible
        /// (<see cref="VanillaTabCaption.CoveredInsets"/>). Zero when no trim overlaps a tab edge.
        /// </summary>
        (float Bottom, float Top) CaptionBand()
        {
            if (_trimRects.Count == 0 || !(_tabHeight > 1f)) return (0f, 0f);
            var bands = new List<(float Bottom, float Top)>();
            foreach (var trim in _trimRects)
            {
                if (!Alive(trim) || !trim.gameObject.activeSelf) continue;
                var image = trim.GetComponent<Image>();
                if (image == null || !image.enabled || image.color.a <= .01f) continue;
                // Anchored at the strip's bottom edge, pivot at its bottom: the tabs span 0 to the tab height.
                float bottom = trim.anchoredPosition.y - trim.pivot.y * trim.rect.height;
                bands.Add((bottom, bottom + trim.rect.height));
            }

            return VanillaTabCaption.CoveredInsets(0f, _tabHeight, bands);
        }

        /// <summary>Put back any caption something moved since the last frame (counted in <c>captionFixes</c>).</summary>
        void KeepCaptions()
        {
            var (bandBottom, bandTop) = CaptionBand();
            int n = Math.Min(_labels.Count, _buttons.Count);
            for (int i = 0; i < n; i++)
            {
                var label = _labels[i];
                var go = _buttons[i].go;
                if (!Alive(label) || !Alive(go)) continue;
                try { if (PlaceCaption(label, go.GetComponent<RectTransform>(), bandBottom, bandTop)) _captionFixes++; }
                catch { /* a caption that cannot be read is left as it is */ }
                if (i >= _art.Count) continue;
                foreach (var fit in _art[i].Fits)
                {
                    try { if (Alive(fit.Rect) && ApplyFit(fit)) _artFixes++; }
                    catch { /* art that cannot be read is left as it is */ }
                }
            }
        }

        /// <summary>
        /// The readback: per tab its caption, whether it is selected, the screen rects of the tab,
        /// the caption rect and the drawn text, and the offsets of their centers from the tab's
        /// center in pixels. True when every caption is <see cref="VanillaTabCaption.Centered"/>.
        /// </summary>
        bool CaptionsCentered(out List<object?> tabs)
        {
            tabs = new List<object?>();
            bool all = true;
            int n = Math.Min(_labels.Count, _buttons.Count);
            for (int i = 0; i < n; i++)
            {
                var label = _labels[i];
                var go = _buttons[i].go;
                if (!Alive(label) || !Alive(go)) { all = false; continue; }
                var tabRect = go.GetComponent<RectTransform>();
                if (tabRect == null) { all = false; continue; }
                var tab = ScreenRectOf(tabRect);
                var caption = ScreenRectOf(label.rectTransform);
                var text = TextRectOf(label);
                var art = DescribeImages(go, label, i < _art.Count ? _art[i] : null, tabRect, out var drawnBox);
                // What the player sees of the art: without the baseline trim drawn over its edge.
                var covers = new List<ScreenRect>();
                foreach (var trim in _trimRects)
                {
                    var trimImage = Alive(trim) ? trim.GetComponent<Image>() : null;
                    if (trimImage != null && Draws(trimImage)) covers.Add(DrawnRect(trimImage, ScreenRectOf(trim)));
                }
                var artBox = VanillaTabCaption.Uncovered(drawnBox, covers);
                // Judged against the art the player sees, never the tab's rect alone.
                bool centered = VanillaTabCaption.Centered(artBox, caption, text);
                all &= centered;
                var (rx, ry) = VanillaTabCaption.Offset(artBox, caption);
                var (tx, ty) = VanillaTabCaption.Offset(artBox, text);
                var (tabTx, _) = VanillaTabCaption.Offset(tab, text);
                var (artDx, artDy) = VanillaTabCaption.Offset(tab, artBox);
                var parent = label.transform.parent;
                tabs.Add(new Dictionary<string, object?>
                {
                    ["index"] = i,
                    ["text"] = label.text,
                    ["selected"] = i == _selected,
                    ["centered"] = centered,
                    ["tab"] = RectDict(tab),
                    ["artBox"] = RectDict(artBox),
                    ["artDrawn"] = RectDict(drawnBox),
                    ["artDx"] = Round1(artDx),
                    ["artDy"] = Round1(artDy),
                    ["tabTextDx"] = Round1(tabTx),
                    ["art"] = art,
                    ["artDrivers"] = i < _art.Count ? _art[i].Drivers : null,
                    ["caption"] = RectDict(caption),
                    ["glyphs"] = RectDict(text),
                    ["captionDx"] = Round1(rx),
                    ["captionDy"] = Round1(ry),
                    ["textDx"] = Round1(tx),
                    ["textDy"] = Round1(ty),
                    ["parentIsTab"] = parent != null && parent.Pointer == go.transform.Pointer,
                    ["font"] = label.font != null ? label.font.name : null,
                    ["fontSize"] = Math.Round(label.fontSize, 1),
                });
            }

            return all;

            static object? Round1(float v) => float.IsNaN(v) ? null : Math.Round(v, 1);
        }

        /// <summary>The drawn text's screen rect (the TMP mesh bounds), Unknown before anything is drawn.</summary>
        static ScreenRect TextRectOf(TextMeshProUGUI label)
        {
            try
            {
                label.ForceMeshUpdate();
                var bounds = label.textBounds;
                if (bounds.size.x <= .01f || bounds.size.y <= .01f) return ScreenRect.Unknown;
                var tr = label.rectTransform;
                Vector2 a = RectTransformUtility.WorldToScreenPoint(null, tr.TransformPoint(bounds.min));
                Vector2 b = RectTransformUtility.WorldToScreenPoint(null, tr.TransformPoint(bounds.max));
                return ScreenRect.FromCorners(a.x, a.y, b.x, b.y);
            }
            catch { return ScreenRect.Unknown; }
        }

        /// <summary>One refitted rect of a tab's art: kept at these anchors with zero offsets.</summary>
        readonly struct ArtFit
        {
            internal ArtFit(RectTransform rect, Vector2 min, Vector2 max, bool frame)
            { Rect = rect; Min = min; Max = max; Frame = frame; }

            internal RectTransform Rect { get; }
            internal Vector2 Min { get; }
            internal Vector2 Max { get; }

            /// <summary>A frame image (<see cref="VanillaTabCaption.IsFrameArt"/>), not an object between one and the tab.</summary>
            internal bool Frame { get; }
        }

        /// <summary>What <see cref="AdoptArt"/> did to one tab's art.</summary>
        sealed class TabArt
        {
            internal readonly List<ArtFit> Fits = new();

            /// <summary>Every animator or layout driver on a frame image's chain: "Type on path", with " (off)" when disabled.</summary>
            internal readonly List<string> Drivers = new();

            /// <summary>The enabled drivers that kept a frame image as the donor drew it.</summary>
            internal readonly List<string> Refused = new();

            internal bool IsFrame(IntPtr pointer) => Fits.Exists(f => f.Frame && f.Rect.Pointer == pointer);
        }

        /// <summary>
        /// Refit the tab's frame art to the tab (<see cref="VanillaTabCaption.IsFrameArt"/>, judged on
        /// the button's state graphics against the largest image that draws). The donor tab's frame
        /// and state images keep the donor's own anchors, position and size, so in a tab of another
        /// size they draw off the tab's center while the caption is centered on the tab. Each frame
        /// image is anchored where it sat in the donor tab, in proportion
        /// (<see cref="VanillaTabCaption.Proportional"/>), so its insets, spill and height scale with
        /// the tab; it fills the tab when the donor could not be measured. Objects between a frame
        /// image and the tab (or the frame image it sits in) fill their parent. A frame image whose
        /// chain has an enabled animator or layout driver is left alone and reported. Other images
        /// (icons, badges) keep their place.
        /// </summary>
        static TabArt AdoptArt(GameObject tab, TextMeshProUGUI label, RectTransform? donor)
        {
            var art = new TabArt();
            try
            {
                var root = tab.transform;
                var targets = GraphicTargets(tab);
                var images = tab.GetComponentsInChildren<Image>(true);
                if (images == null) return art;
                bool donorOk = donor != null && Alive(donor) && donor.rect.width >= 1f && donor.rect.height >= 1f;
                var tabRect = tab.GetComponent<RectTransform>();
                var pieces = new List<(Image Image, Rect Box, bool Measured)>();
                float maxW = 0f, maxH = 0f;
                foreach (var image in images)
                {
                    if (image == null || image.transform.Pointer == root.Pointer || image.transform.IsChildOf(label.transform)) continue;
                    bool measured = false;
                    Rect box = default;
                    if (donorOk)
                    {
                        var twin = donor!.transform.Find(PathUnder(image.transform, root));
                        var twinRect = twin != null ? twin.GetComponent<RectTransform>() : null;
                        if (twinRect != null) { box = BoxIn(twinRect, donor); measured = true; }
                    }
                    if (!measured) box = BoxIn(image.rectTransform, tabRect);
                    pieces.Add((image, box, measured));
                    if (!WouldDraw(image, root)) continue;
                    maxW = Math.Max(maxW, box.width);
                    maxH = Math.Max(maxH, box.height);
                }

                var frames = new List<(Image Image, Rect Box, bool Measured)>();
                foreach (var piece in pieces)
                    if (VanillaTabCaption.IsFrameArt(piece.Box.width, piece.Box.height, maxW, maxH,
                            targets.Contains(piece.Image.Pointer), targets.Count > 0))
                        frames.Add(piece);

                foreach (var (image, box, measured) in frames)
                {
                    string? driver = Drivers(image.transform, root, art.Drivers);
                    if (driver != null) { if (!art.Refused.Contains(driver)) art.Refused.Add(driver); continue; }
                    // The reference box: the frame image this one sits in, else the donor tab.
                    Transform? reference = null;
                    Rect refBox = donorOk ? donor!.rect : default;
                    bool refMeasured = donorOk;
                    for (var t = image.transform.parent; t != null && t.Pointer != root.Pointer; t = t.parent)
                    {
                        var at = t;
                        int index = frames.FindIndex(f => f.Image.transform.Pointer == at.Pointer);
                        if (index < 0) continue;
                        reference = t;
                        refBox = frames[index].Box;
                        refMeasured = frames[index].Measured;
                        break;
                    }

                    for (var t = image.transform.parent; t != null && t.Pointer != root.Pointer
                        && (reference == null || t.Pointer != reference.Pointer); t = t.parent)
                    {
                        var link = t.GetComponent<RectTransform>();
                        if (link != null && !art.Fits.Exists(f => f.Rect.Pointer == link.Pointer))
                            art.Fits.Add(new ArtFit(link, Vector2.zero, Vector2.one, false));
                    }

                    var share = measured && refMeasured
                        ? VanillaTabCaption.Proportional(refBox.xMin, refBox.yMin, refBox.width, refBox.height,
                            box.xMin, box.yMin, box.xMax, box.yMax)
                        : null;
                    var min = share.HasValue ? new Vector2(share.Value.MinX, share.Value.MinY) : Vector2.zero;
                    var max = share.HasValue ? new Vector2(share.Value.MaxX, share.Value.MaxY) : Vector2.one;
                    var own = image.rectTransform;
                    art.Fits.RemoveAll(f => f.Rect.Pointer == own.Pointer);
                    art.Fits.Add(new ArtFit(own, min, max, true));
                }

                foreach (var fit in art.Fits) ApplyFit(fit);
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("Ui: panel tab art could not be fitted: " + e.Message);
            }

            return art;
        }

        /// <summary>The native objects of every state graphic the tab's buttons name (target graphic and target settings).</summary>
        static HashSet<IntPtr> GraphicTargets(GameObject tab)
        {
            var set = new HashSet<IntPtr>();
            try
            {
                foreach (var button in tab.GetComponentsInChildren<SimpleStunButton>(true))
                {
                    if (button == null) continue;
                    if (button.targetGraphic != null) set.Add(button.targetGraphic.Pointer);
                    var settings = button.TargetSettings;
                    if (settings == null) continue;
                    foreach (var target in settings)
                    {
                        if (target == null || target.GraphicTargets == null) continue;
                        foreach (var graphic in target.GraphicTargets)
                            if (graphic != null) set.Add(graphic.Pointer);
                    }
                }
            }
            catch { /* no named targets: the both-ways size rule decides */ }

            return set;
        }

        /// <summary>A rect's box in <paramref name="space"/>'s local units (y up), from its world corners.</summary>
        static Rect BoxIn(RectTransform r, RectTransform? space)
        {
            var corners = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Vector3>(4);
            r.GetWorldCorners(corners);
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            for (int k = 0; k < 4; k++)
            {
                Vector3 p = space != null ? space.InverseTransformPoint(corners[k]) : corners[k];
                minX = Math.Min(minX, p.x); maxX = Math.Max(maxX, p.x);
                minY = Math.Min(minY, p.y); maxY = Math.Max(maxY, p.y);
            }

            return Rect.MinMaxRect(minX, minY, maxX, maxY);
        }

        /// <summary>The image would draw once its tab is shown: enabled, not clear, and active up to (not including) the tab, which is still inactive while it is built.</summary>
        static bool WouldDraw(Image image, Transform tab)
        {
            if (!image.enabled || image.color.a <= .01f) return false;
            for (var t = image.transform; t != null && t.Pointer != tab.Pointer; t = t.parent)
                if (!t.gameObject.activeSelf) return false;
            return true;
        }

        /// <summary>
        /// Record every animator or layout driver (<see cref="VanillaTabCaption.IsLayoutDriver"/>) on
        /// the chain from <paramref name="from"/> up to and including the tab; return the first
        /// enabled one, or null.
        /// </summary>
        static string? Drivers(Transform from, Transform tab, List<string> report)
        {
            string? enabled = null;
            for (var t = from; t != null; t = t.parent)
            {
                foreach (var behaviour in t.GetComponents<Behaviour>())
                {
                    if (behaviour == null) continue;
                    string type;
                    try { type = behaviour.GetIl2CppType().Name; } catch { continue; }
                    if (!VanillaTabCaption.IsLayoutDriver(type)) continue;
                    bool on = behaviour.enabled;
                    string where = type + " on " + PathUnder(t, tab);
                    string entry = where + (on ? "" : " (off)");
                    if (!report.Contains(entry)) report.Add(entry);
                    if (on && enabled == null) enabled = where;
                }
                if (t.Pointer == tab.Pointer) break;
            }

            return enabled;
        }

        /// <summary>Keep a fitted rect at its anchors: zero offsets, centered pivot, unrotated, unit scale (a mirror flip is kept). True when anything moved.</summary>
        static bool ApplyFit(ArtFit fit)
        {
            var r = fit.Rect;
            bool moved = false;
            if (Moved(r.anchorMin, fit.Min)) { r.anchorMin = fit.Min; moved = true; }
            if (Moved(r.anchorMax, fit.Max)) { r.anchorMax = fit.Max; moved = true; }
            if (Moved(r.pivot, new Vector2(.5f, .5f))) { r.pivot = new Vector2(.5f, .5f); moved = true; }
            if (Moved(r.offsetMin, Vector2.zero)) { r.offsetMin = Vector2.zero; moved = true; }
            if (Moved(r.offsetMax, Vector2.zero)) { r.offsetMax = Vector2.zero; moved = true; }
            if (r.localRotation != Quaternion.identity) { r.localRotation = Quaternion.identity; moved = true; }
            var s = r.localScale;
            var unit = new Vector3(s.x < 0f ? -1f : 1f, s.y < 0f ? -1f : 1f, 1f);
            if (VanillaTabCaption.Drifted(s.x, unit.x) || VanillaTabCaption.Drifted(s.y, unit.y) || VanillaTabCaption.Drifted(s.z, 1f))
            { r.localScale = unit; moved = true; }
            return moved;

            static bool Moved(Vector2 now, Vector2 wanted)
                => VanillaTabCaption.Drifted(now.x, wanted.x) || VanillaTabCaption.Drifted(now.y, wanted.y);
        }

        static List<object?> DescribeImages(GameObject tab, TextMeshProUGUI label, TabArt? art, RectTransform tabRect)
            => DescribeImages(tab, label, art, tabRect, out _);

        /// <summary>
        /// Every image under a tab (the tab's own included), with its path, whether it draws, its
        /// sprite (name, type, border, packing padding, pivot), its rect's anchors, pivot, position
        /// and size, its screen rect and its center's offset from the tab's center. <paramref name="artBox"/>
        /// is the visible art box (<see cref="VanillaTabCaption.ArtBox"/>).
        /// </summary>
        static List<object?> DescribeImages(GameObject tab, TextMeshProUGUI label, TabArt? art,
            RectTransform tabRect, out ScreenRect artBox)
        {
            var list = new List<object?>();
            var boxes = new List<(ScreenRect Rect, bool Visible, bool Root, bool Frame)>();
            artBox = ScreenRect.Unknown;
            try
            {
                var tabScreen = ScreenRectOf(tabRect);
                var images = tab.GetComponentsInChildren<Image>(true);
                if (images == null) return list;
                foreach (var image in images)
                {
                    if (image == null || image.transform.IsChildOf(label.transform)) continue;
                    var d = new Dictionary<string, object?>();
                    try
                    {
                        var r = image.rectTransform;
                        var screen = ScreenRectOf(r);
                        var drawn = DrawnRect(image, screen);
                        bool visible = Draws(image);
                        bool isRoot = image.transform.Pointer == tab.transform.Pointer;
                        bool frame = art != null && art.IsFrame(r.Pointer);
                        boxes.Add((drawn, visible, isRoot, frame));
                        var (dx, dy) = VanillaTabCaption.Offset(tabScreen, drawn);
                        d["path"] = PathUnder(image.transform, tab.transform);
                        d["visible"] = visible;
                        d["frame"] = frame;
                        d["fitted"] = art != null && art.Fits.Exists(f => f.Rect.Pointer == r.Pointer);
                        d["rect"] = RectDict(screen);
                        d["drawn"] = RectDict(drawn);
                        d["dx"] = float.IsNaN(dx) ? null : Math.Round(dx, 1);
                        d["dy"] = float.IsNaN(dy) ? null : Math.Round(dy, 1);
                        d["anchorMin"] = Pair(r.anchorMin);
                        d["anchorMax"] = Pair(r.anchorMax);
                        d["pivot"] = Pair(r.pivot);
                        d["anchoredPosition"] = Pair(r.anchoredPosition);
                        d["sizeDelta"] = Pair(r.sizeDelta);
                        d["type"] = image.type.ToString();
                        d["preserveAspect"] = image.preserveAspect;
                        var sprite = image.sprite;
                        d["sprite"] = sprite != null ? sprite.name : null;
                        if (sprite != null)
                        {
                            var b = sprite.border;
                            d["border"] = new object[] { Math.Round(b.x, 1), Math.Round(b.y, 1), Math.Round(b.z, 1), Math.Round(b.w, 1) };
                            var pad = UnityEngine.Sprites.DataUtility.GetPadding(sprite);
                            d["padding"] = new object[] { Math.Round(pad.x, 1), Math.Round(pad.y, 1), Math.Round(pad.z, 1), Math.Round(pad.w, 1) };
                            var sr = sprite.rect;
                            d["spriteSize"] = Pair(new Vector2(sr.width, sr.height));
                            d["spritePivot"] = sr.width > 0f && sr.height > 0f
                                ? Pair(new Vector2(sprite.pivot.x / sr.width, sprite.pivot.y / sr.height)) : null;
                        }
                    }
                    catch (Exception e) { d["error"] = e.Message; }
                    list.Add(d);
                }
            }
            catch (Exception e) { list.Add(new Dictionary<string, object?> { ["error"] = e.Message }); }

            artBox = VanillaTabCaption.ArtBox(boxes);
            return list;

            static object[] Pair(Vector2 v) => new object[] { Math.Round(v.x, 1), Math.Round(v.y, 1) };
        }

        /// <summary>
        /// The part of an image's screen rect its sprite draws (<see cref="VanillaTabCaption.Drawn"/>):
        /// minus the sprite's packing padding, scaled with the sprite for a simple or filled image and
        /// by the image's pixels per unit times its multiplier for a sliced one, as the image builds
        /// its mesh. A tiled image covers its whole rect.
        /// </summary>
        static ScreenRect DrawnRect(Image image, ScreenRect screen)
        {
            var sprite = image.sprite;
            if (sprite == null || !screen.IsKnown) return screen;
            try
            {
                if (image.preserveAspect && (image.type == Image.Type.Simple || image.type == Image.Type.Filled))
                {
                    var whole = sprite.rect;
                    var pivot = image.rectTransform.pivot;
                    screen = VanillaTabCaption.PreserveAspect(screen, whole.width, whole.height, pivot.x, pivot.y);
                }
                var pad = UnityEngine.Sprites.DataUtility.GetPadding(sprite);
                if (pad.x == 0f && pad.y == 0f && pad.z == 0f && pad.w == 0f) return screen;
                var rect = image.rectTransform.rect;
                float w = screen.MaxX - screen.MinX, h = screen.MaxY - screen.MinY;
                if (rect.width <= .01f || rect.height <= .01f || w <= .01f || h <= .01f) return screen;
                float sx, sy;
                // A tiled image repeats the sprite's drawn region across the whole rect.
                if (image.type == Image.Type.Tiled) return screen;
                if (image.type == Image.Type.Simple || image.type == Image.Type.Filled)
                {
                    var sr = sprite.rect;
                    if (sr.width <= .01f || sr.height <= .01f) return screen;
                    sx = w / sr.width;
                    sy = h / sr.height;
                }
                else
                {
                    // As the sliced mesh is built: padding over pixels per unit times the image's multiplier.
                    float ppu = image.pixelsPerUnit * image.pixelsPerUnitMultiplier;
                    if (ppu <= .0001f) return screen;
                    sx = w / rect.width / ppu;
                    sy = h / rect.height / ppu;
                }

                return VanillaTabCaption.Drawn(screen, pad.x * sx, pad.y * sy, pad.z * sx, pad.w * sy);
            }
            catch { return screen; }
        }

        /// <summary>The strip's own images (the baseline trim), with screen rects, for comparing tab art with what surrounds it.</summary>
        List<object?>? StripArt()
        {
            if (_strip == null) return null;
            var list = new List<object?>();
            try
            {
                for (int i = 0; i < _strip.transform.childCount; i++)
                {
                    var child = _strip.transform.GetChild(i);
                    var image = child != null ? child.GetComponent<Image>() : null;
                    if (image == null) continue;
                    list.Add(new Dictionary<string, object?>
                    {
                        ["name"] = child!.name,
                        ["visible"] = Draws(image),
                        ["sprite"] = image.sprite != null ? image.sprite.name : null,
                        ["type"] = image.type.ToString(),
                        ["rect"] = RectDict(ScreenRectOf(image.rectTransform)),
                    });
                }
            }
            catch (Exception e) { list.Add(new Dictionary<string, object?> { ["error"] = e.Message }); }

            return list;
        }

        /// <summary>The image draws something: active, enabled, and neither its color nor its renderer's alpha (a button state tint) is clear.</summary>
        static bool Draws(Image image)
        {
            if (!image.isActiveAndEnabled || image.color.a <= .01f) return false;
            try { if (image.canvasRenderer != null && image.canvasRenderer.GetAlpha() <= .01f) return false; }
            catch { /* the color alone decides */ }
            return true;
        }

        /// <summary>The object's path from (not including) <paramref name="root"/>; "(tab)" for the root itself.</summary>
        static string PathUnder(Transform t, Transform root)
        {
            if (t.Pointer == root.Pointer) return "(tab)";
            var path = t.name;
            for (var p = t.parent; p != null && p.Pointer != root.Pointer; p = p.parent) path = p.name + "/" + path;
            return path;
        }

        /// <summary>Re-place every baseline trim from its donor row image (counted in <c>trimMoves</c>).</summary>
        void PlaceTrims()
        {
            foreach (var (trim, donor, row) in _trims)
            {
                try { if (Alive(trim) && Alive(donor) && Alive(row) && PlaceTrim(trim, donor, row)) _trimMoves++; }
                catch { /* a trim that cannot be read stays where it is */ }
            }
        }

        /// <summary>
        /// One baseline trim: at the strip's bottom edge, raised by the donor image's own height
        /// offset, as wide as the strip plus the donor's overhang past its row, and as tall as the
        /// donor image. Writes only what changed; true when it moved.
        /// </summary>
        static bool PlaceTrim(RectTransform trim, RectTransform donor, RectTransform row)
        {
            var position = new Vector2(0, donor.anchoredPosition.y);
            var size = new Vector2(donor.rect.width - row.rect.width, donor.rect.height);
            bool moved = false;
            if (VanillaTabCaption.Drifted(trim.anchoredPosition.x, position.x) || VanillaTabCaption.Drifted(trim.anchoredPosition.y, position.y))
            { trim.anchoredPosition = position; moved = true; }
            if (VanillaTabCaption.Drifted(trim.sizeDelta.x, size.x) || VanillaTabCaption.Drifted(trim.sizeDelta.y, size.y))
            { trim.sizeDelta = size; moved = true; }
            return moved;
        }

        /// <summary>The caption band (<see cref="CaptionBand"/>) and the baseline trims it came from, for the readback.</summary>
        Dictionary<string, object?> CaptionBandDict()
        {
            var (bottom, top) = CaptionBand();
            var trims = new List<object?>();
            foreach (var trim in _trimRects)
            {
                if (!Alive(trim)) continue;
                trims.Add(new Dictionary<string, object?>
                {
                    ["name"] = trim.name,
                    ["bottom"] = Math.Round(trim.anchoredPosition.y - trim.pivot.y * trim.rect.height, 1),
                    ["height"] = Math.Round(trim.rect.height, 1),
                });
            }

            return new Dictionary<string, object?>
            {
                ["bottom"] = Math.Round(bottom, 1),
                ["top"] = Math.Round(top, 1),
                ["tabHeight"] = Math.Round(_tabHeight, 1),
                ["trims"] = trims,
                ["trimMoves"] = _trimMoves,
            };
        }

        /// <summary>
        /// The donor tab's own caption as the game lays it out now (the live donor, not our copy): its
        /// rect's center against the donor tab's center as shares of the donor tab's size (x right,
        /// y up), for comparing our caption with the game's. Null while the donor has no usable size.
        /// </summary>
        Dictionary<string, object?>? DonorCaptionNow()
        {
            if (!Alive(_donorCaption) || !Alive(_tabDonorRect)) return null;
            try
            {
                var donor = _tabDonorRect!;
                var r = donor.rect;
                if (r.width < 1f || r.height < 1f) return null;
                var box = BoxIn(_donorCaption!.rectTransform, donor);
                return new Dictionary<string, object?>
                {
                    ["donorTab"] = new object[] { Math.Round(r.width, 1), Math.Round(r.height, 1) },
                    ["caption"] = new object[] { Math.Round(box.width, 1), Math.Round(box.height, 1) },
                    ["shareDx"] = Math.Round((box.center.x - r.center.x) / r.width, 3),
                    ["shareDy"] = Math.Round((box.center.y - r.center.y) / r.height, 3),
                    ["alignment"] = _donorCaption.alignment.ToString(),
                };
            }
            catch (Exception e) { return new Dictionary<string, object?> { ["error"] = e.Message }; }
        }

        /// <summary>What the donor tab's caption brought, read before <see cref="AdoptCaption"/> changes it.</summary>
        static Dictionary<string, object?> DescribeDonorCaption(TextMeshProUGUI label, GameObject tab)
        {
            var d = new Dictionary<string, object?>();
            try
            {
                var rt = label.rectTransform;
                var parent = label.transform.parent;
                var m = label.margin;
                d["parent"] = parent == null ? null : parent.Pointer == tab.transform.Pointer ? "(tab)" : parent.name;
                d["anchorMin"] = Pair(rt.anchorMin);
                d["anchorMax"] = Pair(rt.anchorMax);
                d["pivot"] = Pair(rt.pivot);
                d["anchoredPosition"] = Pair(rt.anchoredPosition);
                d["sizeDelta"] = Pair(rt.sizeDelta);
                d["margin"] = new object[] { Math.Round(m.x, 1), Math.Round(m.y, 1), Math.Round(m.z, 1), Math.Round(m.w, 1) };
                d["alignment"] = label.alignment.ToString();
                d["wordWrapping"] = label.enableWordWrapping;
                var transitions = new List<object?>();
                var native = tab.GetComponent<SimpleStunButton>();
                var targets = native != null ? native.TargetSettings : null;
                if (targets != null)
                    foreach (var target in targets)
                        if (target != null && target.Enabled) transitions.Add(target.Transition.ToString());
                d["transitions"] = transitions;
            }
            catch (Exception e) { d["error"] = e.Message; }

            return d;

            static object[] Pair(Vector2 v) => new object[] { Math.Round(v.x, 1), Math.Round(v.y, 1) };
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
            _labels.Clear(); _captionDonor = null; _captionFixes = 0;
            _art.Clear(); _artDonor = null; _artFixes = 0; _artDriverLogged = false;
            _trimRects.Clear(); _donorCaption = null; _trims.Clear(); _trimMoves = 0;
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
