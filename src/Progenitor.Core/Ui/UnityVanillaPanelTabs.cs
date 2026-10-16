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

        internal NativePanelTabs(UnityUiRuntime runtime, VanillaUiSite site, string[] captions, Action<int> changed)
        { _runtime = runtime; _site = site; _captions = captions; _changed = changed; }

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
                var anchor = _routing != null && _routing.StationTabButton != null
                    ? _routing.StationTabButton.GetComponent<RectTransform>() : view.Grid;
                _bonusAnchor = bench != null ? RectOf(bench.BonusesParent) : refiner != null ? RectOf(refiner.BonusesParent) : null;
                anchor.GetWorldCorners(_corners);
                _top = _root.InverseTransformPoint(_corners[1]).y;
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
                float tabHeight = tabDonor != null ? tabDonor.GetComponent<RectTransform>().rect.height : 49f;
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
            }
            if (!_flow && _bonusAnchor != null && _root != null && _strip != null)
            {
                _bonusAnchor.GetWorldCorners(_corners);
                _top = _root.InverseTransformPoint(_corners[0]).y - 20;
                var stripRect = _strip.GetComponent<RectTransform>();
                stripRect.anchoredPosition = new Vector2(_root.rect.center.x, _top);
            }
            foreach (var item in _nativeTabs) item.Hide();
            ApplySelection();
            PlacePanel();
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
            if (_panel == null || _root == null || !_panel.Alive) return;
            float height = Math.Min(680, _root.rect.height - 220);
            // Keep custom content clear of the lower HUD. Measure after native layout settles,
            // converting screen clearance back to the tab row's canvas units.
            var stripRect = _strip!.GetComponent<RectTransform>();
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
                rect.anchoredPosition = new Vector2(_root.rect.center.x, _top - _strip!.GetComponent<RectTransform>().rect.height - 6);
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
        }

        public void Dispose() => Release();

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
