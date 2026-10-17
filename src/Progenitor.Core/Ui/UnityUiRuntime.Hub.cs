#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using ProjectM.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Progenitor.Ui;

/// <summary>
/// HUB1: the Unity half of the hub controls: compact dropdowns sharing an inline row, an action button at the end of a
/// field's row, centered button rows, key hints on buttons, the game's window tabs as a cell skin, the height rule of a
/// floating window with tabs, a panel's room for a table that fills it, and castle building icons (<see cref="UnityBuildingIcons"/>).
/// Every object here is a child of its panel or button, so it goes with it.
/// </summary>
internal sealed partial class UnityUiRuntime
{
    internal const string CompactRowKind = "compact-choices";
    internal const string KeyHintName = "key-hint";

    /// <summary>HUB1 (<see cref="IUiRuntime.PanelDrawOrder"/>): the panel's cached canvas sort order (WINDOWS1's row layers).</summary>
    public int PanelDrawOrder(IRuntimePanel panel) => panel is UnityPanel host && host.Alive ? host.CanvasOrder : 0;

    // ---- the height rule ------------------------------------------------------------------------------------------

    public void SetHeightRule(IRuntimePanel panel, bool keep, float minContentHeight)
    {
        if (panel is not UnityPanel host || !host.Alive) return;
        host.SetHeightRule(keep, minContentHeight);
    }

    // ---- a panel's room ---------------------------------------------------------------------------------------------

    public bool PanelRoom(IUiPanel panel, out float maxViewport, out float content)
    {
        maxViewport = 0f;
        content = 0f;
        if (panel is not UiPanelHandle handle || handle.RuntimePanel is not UnityPanel host || !host.Alive) return false;
        return host.TryRoom(out maxViewport, out content);
    }

    // ---- compact dropdowns ------------------------------------------------------------------------------------------

    public IRuntimeWidget? AddCompactChoice(
        IRuntimePanel panel, string text, IReadOnlyList<UiChoiceOption> options, string value, float labelWidth, float barWidth)
    {
        if (panel is not UnityPanel host || !host.Alive) return null;
        try
        {
            var row = host.OpenCompactRow(out bool fresh);
            float itemWidth = UiCompactChoiceLayout.ItemWidth(labelWidth, barWidth);
            var item = new GameObject("compact-choice");
            item.transform.SetParent(row.transform, false);
            PlaceTop(item, 0f, 0f, itemWidth, UiCompactChoiceLayout.LineHeight);
            GameObject? label = null;
            if (labelWidth > 0f)
            {
                label = MakeLabel(item, text ?? "", Vector2.zero, UiCompactChoiceLayout.LabelFontSize, left: true);
                var tmp = label != null ? label.GetComponent<TextMeshProUGUI>() : null;
                if (tmp != null)
                {
                    try { tmp.enableWordWrapping = false; } catch { /* */ }
                    try { tmp.overflowMode = TextOverflowModes.Ellipsis; } catch { /* */ }
                    try { tmp.verticalAlignment = VerticalAlignmentOptions.Middle; } catch { /* */ }
                }
            }

            var columns = new RowColumns(labelWidth, -itemWidth * 0.5f + labelWidth * 0.5f, barWidth, itemWidth * 0.5f - barWidth * 0.5f);
            var state = new UiChoiceState(options, value);
            var control = BuildChoiceControl(item, columns, state, out var source, out var valueClick, out var prev, out var next);
            if (control != null)
            {
                try
                {
                    var rt = control.GetComponent<RectTransform>();
                    if (rt != null) rt.sizeDelta = new Vector2(barWidth, UiCompactChoiceLayout.BarHeight);
                }
                catch { /* the bar keeps the row's height */ }

                // The game's dropdown bar (DropdownButton_Normal, its arrow at the right), retried until the art arrives.
                var bar = control;
                if (!SkinChoice(bar)) RetrySkinLater(bar, () => SkinChoice(bar));
            }

            host.AddCompactItem(row, item, label, labelWidth, barWidth);
            if (fresh) host.AddRow(row, null, hasControl: false, kind: CompactRowKind, source: source);
            else host.Relayout();
            var widget = new UnityChoiceWidget(host, item, valueClick);
            widget.AddPart(UiControlParts.Previous, prev);
            widget.AddPart(UiControlParts.Next, next);
            if (control != null) widget.BuildList(control, options);
            return widget;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: AddCompactChoice threw: " + e.Message);
            return null;
        }
    }

    // ---- an action button at the end of a field's row ---------------------------------------------------------------

    public IRuntimeWidget? JoinFieldRowButton(IRuntimePanel panel, IRuntimeWidget field, string text)
    {
        if (panel is not UnityPanel host || !host.Alive || field is not UnityWidget fieldWidget) return null;
        try
        {
            var rowGo = fieldWidget.GameObject;
            if (rowGo == null || !host.IsFieldRow(rowGo)) return null;
            var go = CloneOrCreateButton(rowGo, text ?? "", out var caption, out _, false, false);
            if (go == null) return null;
            UseGridButtonCaption(caption);
            if (!host.AttachInlineTo(rowGo, go))
            {
                TryDestroyImmediate(go);
                return null;
            }

            host.Relayout();
            EnsurePointerEvents(go, out var enter, out var exit);
            var widget = new UnityWidget(host, go, FindClickEvent(go), null, null, null, enter, exit);
            SetGameButton(widget);
            return widget;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: JoinFieldRowButton threw: " + e.Message);
            return null;
        }
    }

    // ---- centered button rows ---------------------------------------------------------------------------------------

    public bool CenterButtonRow(IRuntimePanel panel, IRuntimeWidget button)
    {
        if (panel is not UnityPanel host || !host.Alive || button is not UnityWidget widget || widget.GameObject == null) return false;
        try { return host.CenterPageRowOf(widget.GameObject); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: CenterButtonRow threw: " + e.Message);
            return false;
        }
    }

    // ---- key hints ----------------------------------------------------------------------------------------------------

    public IRuntimeKeyHint? AttachKeyHint(IRuntimeWidget button, string text)
    {
        if (button is not UnityWidget widget || widget.GameObject == null || !Alive(widget.GameObject)) return null;
        try
        {
            var face = ButtonFace(widget.GameObject);
            if (face == null) return null;
            var hint = new UnityKeyHint(widget.Owner, face);
            hint.SetText(text);
            return hint;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: AttachKeyHint threw: " + e.Message);
            return null;
        }
    }

    /// <summary>The button's own object: the widget's, or the first button under it (a grid button row's button).</summary>
    private static GameObject? ButtonFace(GameObject go)
    {
        try
        {
            if (go.GetComponent<SimpleStunButton>() != null || go.GetComponent<Button>() != null) return go;
            var stun = go.GetComponentInChildren<SimpleStunButton>(true);
            if (stun != null) return stun.gameObject;
            var plain = go.GetComponentInChildren<Button>(true);
            return plain != null ? plain.gameObject : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// A key cap at a button's right end: the kit's key cap (the game's key-cap art, <see cref="KeybindArtDonors.TryKeyCap"/>,
    /// as the world-pick prompt draws its keys), the key's words in it. The button's caption keeps clear of it: the panel
    /// widens a compact button by the cap's room and keeps the caption's right margin on every layout pass.
    /// </summary>
    private sealed class UnityKeyHint : IRuntimeKeyHint
    {
        private readonly UnityPanel? _host;
        private readonly GameObject _button;
        private GameObject? _cap;
        private bool _disposed;

        internal UnityKeyHint(UnityPanel? host, GameObject button)
        {
            _host = host;
            _button = button;
        }

        public void SetText(string text)
        {
            if (_disposed || !Alive(_button)) return;
            if (_cap != null) TryDestroyImmediate(_cap);
            _cap = null;
            float room = 0f;
            if (!string.IsNullOrEmpty(text))
            {
                var cap = BandCap(_button, text, out float width);
                if (cap != null)
                {
                    cap.name = KeyHintName;
                    float w = Math.Max(UiKeyHint.CapWidth(text), width);
                    try
                    {
                        var rt = cap.GetComponent<RectTransform>();
                        rt.anchorMin = new Vector2(1f, 0.5f);
                        rt.anchorMax = new Vector2(1f, 0.5f);
                        rt.pivot = new Vector2(1f, 0.5f);
                        rt.anchoredPosition = new Vector2(-UiKeyHint.RightInset, 0f);
                        rt.sizeDelta = new Vector2(w, UiKeyHint.CapHeight);
                    }
                    catch { /* the cap keeps its band placement */ }

                    _cap = cap;
                    room = w + UiKeyHint.Gap + UiKeyHint.RightInset;
                }
            }

            ApplyCaptionRoom(_button, room);
            if (_host != null)
            {
                _host.NoteHintRoom(_button, room);
                try { _host.Relayout(); } catch { /* the next pass places it */ }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_cap != null) TryDestroyImmediate(_cap);
            _cap = null;
            if (Alive(_button)) ApplyCaptionRoom(_button, 0f);
            _host?.NoteHintRoom(_button, 0f);
        }
    }

    /// <summary>Keep a button's caption clear of its key cap: its right margin is the cap's room (0 restores it).</summary>
    private static void ApplyCaptionRoom(GameObject button, float room)
    {
        try
        {
            var tmp = button.GetComponentInChildren<TextMeshProUGUI>(true);
            if (tmp == null || tmp.transform.parent != null && tmp.transform.parent.name == KeyHintName) return;
            var m = tmp.margin;
            tmp.margin = new Vector4(m.x, m.y, room, m.w);
        }
        catch { /* the caption keeps its margin */ }
    }

    // ---- the game's window tabs as a cell -----------------------------------------------------------------------------

    /// <summary>
    /// A <see cref="CellSkin.GameTab"/> cell's object: a clone of the game's own window tab (the Mods workspace strip's
    /// donor ladder: the full-screen menu's inner tab, a hidden template of it, the resident prefab), its gameplay
    /// callbacks and decorations removed; the kit button when no donor is reachable (then drawn as the chip).
    /// </summary>
    private static GameObject? BuildGameTabCell(GameObject parent, string text, out GameObject? caption)
    {
        caption = null;
        GameObject? go = null;
        try { go = CloneWorkspaceTab(parent); }
        catch { go = null; }
        if (go == null) return null;
        caption = SetButtonText(go, text ?? "");
        // Like the workspace strip: the caption is stretched over the tab and takes no pointer, so a press lands on the tab.
        PrepareStripButtonCaption(caption);
        return go;
    }

    /// <summary>
    /// Paint a game tab cell: the clone's own state machine (selected while the cell's tone is <see cref="CellTone.ChipOn"/>,
    /// its hover lighter than selected), the caption at the cell's size, centered, clear of a badge. False when the cell
    /// holds no clone (the chip look then).
    /// </summary>
    private static bool PaintGameTabCell(GameObject? go, TextMeshProUGUI? tmp, CellSpec spec)
    {
        if (go == null) return false;
        SimpleStunButton? native = null;
        try { native = go.GetComponent<SimpleStunButton>(); }
        catch { native = null; }
        if (native == null) return false;
        bool selected = spec.Tone == CellTone.ChipOn;
        try
        {
            native.ForceSelect = false;
            // Hover lighter than selected (WorkspaceTabHover), as on the workspace strip; a tab row's tab changes between
            // selected and not, so the clone's own highlighted colors are kept and dimmed only while it is not selected.
            SetGameTabHover(native, dim: WorkspaceTabHover.DimsHover(selected));
            // FullscreenMenu_InnerTabEntry.Refresh marks the selected tab with ForceHighlight.
            native.ForceHighlight = selected;
        }
        catch { /* the clone keeps its last state */ }
        if (tmp != null)
        {
            try { tmp.text = UiRichText.Literal(spec.Text ?? ""); } catch { /* */ }
            try { tmp.enableAutoSizing = false; } catch { /* */ }
            try { tmp.fontSize = spec.FontSize; } catch { /* */ }
            try { tmp.enableWordWrapping = false; } catch { /* */ }
            try { tmp.horizontalAlignment = HorizontalAlignmentOptions.Center; } catch { /* */ }
            try { tmp.verticalAlignment = VerticalAlignmentOptions.Middle; } catch { /* */ }
            float half = CellLayout.ButtonPad * .5f;
            float right = spec.CaptionInsetRight > 0f ? spec.CaptionInsetRight : half;
            try { tmp.margin = new Vector4(half, 0f, right, 0f); } catch { /* */ }
            try { tmp.raycastTarget = false; } catch { /* */ }
        }

        return true;
    }

    /// <summary>Each game tab clone's own highlighted colors per tint target, by the clone's instance id.</summary>
    private static readonly Dictionary<int, Color[]> GameTabHighlights = new();

    /// <summary>
    /// A game tab clone's hover: its tint targets' highlighted colors moved part of the way to their normal colors
    /// (<see cref="WorkspaceTabHover.HoverColor"/>) while <paramref name="dim"/>, else the clone's own (the selected look).
    /// </summary>
    private static void SetGameTabHover(SimpleStunButton button, bool dim)
    {
        var targets = button.TargetSettings;
        int n = targets == null ? 0 : targets.Length;
        if (n == 0) return;
        int id = button.GetInstanceID();
        if (!GameTabHighlights.TryGetValue(id, out var own) || own.Length != n)
        {
            if (GameTabHighlights.Count > 512) GameTabHighlights.Clear();
            own = new Color[n];
            for (int i = 0; i < n; i++)
            {
                try { own[i] = targets![i] != null ? targets[i].Colors.highlightedColor : Color.clear; }
                catch { own[i] = Color.clear; }
            }

            GameTabHighlights[id] = own;
        }

        for (int i = 0; i < n; i++)
        {
            try
            {
                var target = targets![i];
                if (target == null || !target.Enabled || (int)target.Transition != 1 /* ColorTint */) continue;
                var colors = target.Colors;
                var n0 = colors.normalColor;
                var h0 = own[i];
                if (dim)
                {
                    var h = WorkspaceTabHover.HoverColor((n0.r, n0.g, n0.b, n0.a), (h0.r, h0.g, h0.b, h0.a));
                    colors.highlightedColor = new Color(h.R, h.G, h.B, h.A);
                }
                else colors.highlightedColor = h0;
                target.Colors = colors;
            }
            catch { /* one target keeps its colors */ }
        }
    }

    // ---- castle building icons ------------------------------------------------------------------------------------------

    private static Unity.Entities.World? _buildingIconWorld;

    public object? BuildingIcon(int prefab)
    {
        if (prefab == 0) return null;
        try
        {
            var world = Worlds.Client;
            if (world == null || !world.IsCreated) return null;
            if (!ReferenceEquals(world, _buildingIconWorld))
            {
                UiBuildingIcons.Clear();
                UnityBuildingIcons.Clear();
                _buildingIconWorld = world;
            }

            var icon = UiBuildingIcons.Get(prefab, o => o is Sprite s && Alive(s));
            if (icon != null) return icon;
            UiBuildingIcons.Want(prefab);
            int frame;
            try { frame = Time.frameCount; }
            catch { frame = 0; }
            if (!UiBuildingIcons.MayScan(frame)) return null;
            int before = UiBuildingIcons.Version;
            try { UnityBuildingIcons.Scan(world); }
            finally { UiBuildingIcons.ScanDone(UiBuildingIcons.Version != before); }
            return UiBuildingIcons.Get(prefab, o => o is Sprite s && Alive(s));
        }
        catch { return null; }
    }

    // ---- the panel side ---------------------------------------------------------------------------------------------

    private sealed partial class UnityPanel
    {
        private bool _keepHeight;
        private float _minContentHeight;
        private float _roomViewport = -1f;
        private float _roomContent = -1f;

        /// <summary>HUB1: a floating panel that keeps its tallest height while it stays open (a window with tabs).</summary>
        private bool KeepsHeightRule => _keepHeight && !_menuPane && !_dialog && _embeddedHeight <= 0;

        internal void SetHeightRule(bool keep, float minContentHeight)
        {
            _keepHeight = keep;
            _minContentHeight = _menuPane || _dialog ? 0f : UiTabsLayout.MinHeight(minContentHeight);
            try { Relayout(); } catch { /* the next pass applies it */ }
        }

        /// <summary>HUB1: the room this pass left (for a table that fills it): the tallest viewport and the content height.</summary>
        private void NoteRoom(float viewH, float screen, float footerBand, float topChrome, float content)
        {
            _roomContent = content;
            if (_menuPane || _embeddedHeight > 0 || DialogSized || _sidebar || _strip)
            {
                _roomViewport = viewH;
                return;
            }

            float cap = PanelLayout.PanelHeightFor(1_000_000f, screen);
            if (WideGameWindow && !Docked) cap = Math.Min(cap, screen * .70f);
            if (InWindowRow) cap = Math.Min(cap, WindowMaxHeight(screen));
            if (Docked) cap = Math.Min(cap, DockMaxHeight(screen));
            float tallest = PanelLayout.ViewportHeightFor(cap) - footerBand - (IsStandardWindow && topChrome > 0f ? topChrome : 0f);
            _roomViewport = Math.Max(viewH, Math.Max(0f, tallest));
        }

        internal bool TryRoom(out float maxViewport, out float content)
        {
            maxViewport = _roomViewport;
            content = _roomContent;
            return _roomViewport > 0f && _roomContent >= 0f;
        }

        // ---- compact dropdown rows ----

        private sealed class CompactItem
        {
            internal CompactItem(GameObject go, GameObject? label, float labelWidth, float barWidth)
            {
                Go = go;
                Label = label;
                LabelWidth = labelWidth;
                BarWidth = barWidth;
            }

            internal GameObject Go { get; }

            internal GameObject? Label { get; }

            internal float LabelWidth { get; }

            internal float BarWidth { get; }
        }

        private readonly Dictionary<GameObject, List<CompactItem>> _compactRows = new();
        private GameObject? _openCompactRow;

        /// <summary>The compact row the next compact dropdown joins: the inline block's open one, else a new row.</summary>
        internal GameObject OpenCompactRow(out bool fresh)
        {
            if (_inline && _openCompactRow != null && UnityUiRuntime.Alive(_openCompactRow))
            {
                fresh = false;
                return _openCompactRow;
            }

            var row = new GameObject("compact-choice-row");
            row.transform.SetParent(RowParent.transform, false);
            PlaceTop(row, 0f, ContentRowWidth, UiCompactChoiceLayout.LineHeight);
            _compactRows[row] = new List<CompactItem>();
            _openCompactRow = _inline ? row : null;
            fresh = true;
            return row;
        }

        /// <summary>BeginInline and EndInline: the next compact dropdown starts a row of its own.</summary>
        internal void CloseCompactRow() => _openCompactRow = null;

        internal void AddCompactItem(GameObject row, GameObject item, GameObject? label, float labelWidth, float barWidth)
        {
            if (!_compactRows.TryGetValue(row, out var list))
            {
                list = new List<CompactItem>();
                _compactRows[row] = list;
            }

            list.Add(new CompactItem(item, label, labelWidth, barWidth));
        }

        /// <summary>A compact row's shown items, in order.</summary>
        private List<CompactItem> ShownCompactItems(GameObject row)
        {
            var shown = new List<CompactItem>();
            if (!_compactRows.TryGetValue(row, out var list)) return shown;
            list.RemoveAll(i => !UnityUiRuntime.Alive(i.Go));
            foreach (var i in list)
            {
                try { if (i.Go.activeSelf) shown.Add(i); }
                catch { /* */ }
            }

            return shown;
        }

        /// <summary>HUB1: a compact row is as tall as its lines (<see cref="UiCompactChoiceLayout.RowHeight"/>).</summary>
        private float CompactRowHeight(in PanelRow row, float measured)
        {
            if (row.Go == null || !_compactRows.ContainsKey(row.Go)) return measured;
            var items = ShownCompactItems(row.Go);
            var places = UiCompactChoiceLayout.Place(RowWidthOf(row.Go), Sizes(items));
            return UiCompactChoiceLayout.RowHeight(UiCompactChoiceLayout.Lines(places));
        }

        private static List<(float Label, float Bar)> Sizes(List<CompactItem> items)
        {
            var sizes = new List<(float, float)>(items.Count);
            foreach (var i in items) sizes.Add((i.LabelWidth, i.BarWidth));
            return sizes;
        }

        private float RowWidthOf(GameObject row)
        {
            try
            {
                var rt = row.GetComponent<RectTransform>();
                if (rt != null && rt.sizeDelta.x > 1f) return rt.sizeDelta.x;
            }
            catch { /* */ }
            return ContentRowWidth;
        }

        /// <summary>HUB1: place a compact row's items left to right (<see cref="UiCompactChoiceLayout.Place"/>), after the row is placed.</summary>
        private void PlaceCompactChoices(in PanelRow row)
        {
            if (row.Go == null || !_compactRows.ContainsKey(row.Go)) return;
            var items = ShownCompactItems(row.Go);
            float width = RowWidthOf(row.Go);
            var places = UiCompactChoiceLayout.Place(width, Sizes(items));
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                float itemWidth = UiCompactChoiceLayout.ItemWidth(item.LabelWidth, item.BarWidth);
                float x = -width * 0.5f + places[i].Left + itemWidth * 0.5f;
                PlaceTop(item.Go, x, UiCompactChoiceLayout.LineTop(places[i].Line), itemWidth, UiCompactChoiceLayout.LineHeight);
                if (item.Label == null) continue;
                try
                {
                    var rt = item.Label.GetComponent<RectTransform>();
                    if (rt == null) continue;
                    rt.anchorMin = new Vector2(0.5f, 0.5f);
                    rt.anchorMax = new Vector2(0.5f, 0.5f);
                    rt.pivot = new Vector2(0.5f, 0.5f);
                    rt.anchoredPosition = new Vector2(-itemWidth * 0.5f + item.LabelWidth * 0.5f, 0f);
                    rt.sizeDelta = new Vector2(item.LabelWidth, UiCompactChoiceLayout.LineHeight);
                }
                catch { /* the caption keeps its place */ }
            }
        }

        // ---- an action button on a field's row ----

        /// <summary>Whether <paramref name="go"/> is one of this panel's field rows (an input row that can take a button).</summary>
        internal bool IsFieldRow(GameObject go)
        {
            foreach (var row in _rows)
                if (row.Go == go) return string.Equals(row.Kind, "input", StringComparison.Ordinal) && row.Control != null;
            return false;
        }

        /// <summary>Join <paramref name="go"/> (a button) to <paramref name="hostRow"/>'s inline buttons, after its field.</summary>
        internal bool AttachInlineTo(GameObject hostRow, GameObject go)
        {
            if (hostRow == null || go == null) return false;
            try { go.transform.SetParent(hostRow.transform, false); }
            catch { return false; }
            if (!_inlineButtons.TryGetValue(hostRow, out var list))
            {
                list = new List<GameObject>();
                _inlineButtons[hostRow] = list;
            }

            list.Add(go);
            return true;
        }

        // ---- centered button rows and key-hint room ----

        private readonly HashSet<GameObject> _centeredPageRows = new();
        private readonly Dictionary<GameObject, float> _hintRoom = new();

        /// <summary>Center the page button row holding <paramref name="button"/>; false when it is in none.</summary>
        internal bool CenterPageRowOf(GameObject button)
        {
            foreach (var pair in _pageButtons)
            {
                foreach (var b in pair.Value)
                {
                    if (b.Button != button) continue;
                    _centeredPageRows.Add(pair.Key);
                    Relayout();
                    return true;
                }
            }

            return false;
        }

        private bool IsCenteredPageRow(GameObject row) => _centeredPageRows.Contains(row);

        internal void NoteHintRoom(GameObject button, float room)
        {
            if (room > 0f) _hintRoom[button] = room;
            else _hintRoom.Remove(button);
        }

        /// <summary>The key-hint room a button keeps at its right (0 for none).</summary>
        private float HintRoomOf(GameObject button) => _hintRoom.TryGetValue(button, out var room) ? room : 0f;

        /// <summary>After a pass refitted a button's caption: keep its key cap's room.</summary>
        private void KeepHintRoom(GameObject button)
        {
            float room = HintRoomOf(button);
            if (room > 0f) ApplyCaptionRoom(button, room);
        }
    }
}
#endif
