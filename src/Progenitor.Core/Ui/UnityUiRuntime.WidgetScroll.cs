#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Progenitor.Ui;

/// <summary>
/// P7b: the runtime half of the widgets that scroll themselves (the data table's virtualized body).
///
/// <list type="bullet">
/// <item><see cref="AttachScrollBar"/>: a vertical bar in the game's art (<c>ScrollBar_Bar</c> / <c>ScrollBar_Handle</c>,
/// the kit's track and handle when not borrowed: the README reader's and the text editor's bar) on the panel's content,
/// placed once a frame over the right inset of the widget's rows, from the first row's top to the last row's bottom. The
/// widget owns the position (<see cref="IWidgetScrollBar.Set"/>); a drag of the handle or a click on the track is reported
/// once per change (<see cref="IWidgetScrollBar.OnDragged"/>). uGUI's <c>Scrollbar</c> drag is reliable in this game (the
/// wheel is not, docs/ui.md "The wheel"), so the drag is uGUI's and nothing else writes the bar.</item>
/// <item><see cref="IWidgetCellField"/>: whether an input cell holds the keyboard (from the framework's owned focus, the
/// same answer the typed-key pump uses) and its gray hint.</item>
/// </list>
///
/// <para>Every bar is a lease: disposed with its widget, and the <c>widget-scroll-bars</c> teardown destroys any left at
/// the Core unload. A bar whose rows died (the panel closed, a page rebuilt) destroys itself on its next frame.</para>
/// </summary>
internal sealed partial class UnityUiRuntime : IWidgetCellField
{
    private static readonly List<WidgetScrollBar> WidgetBars = new();

    /// <summary>The bar's padding inside the rows' right inset (from the row's right edge) and from the rows' top and bottom.</summary>
    private const float WidgetBarPadRight = 6f;

    private const float WidgetBarPadY = 4f;

    public IWidgetScrollBar? AttachScrollBar(IRuntimeCellRow top, IRuntimeCellRow bottom, object? owner)
    {
        if (top is not UnityCellRow first || bottom is not UnityCellRow last || !first.Alive || !last.Alive) return null;
        Transform? parent;
        try { parent = first.GameObject.transform.parent; }
        catch { parent = null; }
        if (parent == null) return null;

        GameObject? bar = null;
        Scrollbar? scrollbar = null;
        try
        {
            bar = new GameObject("widget-scroll-bar");
            bar.transform.SetParent(parent, false);
            var brt = bar.GetComponent<RectTransform>() ?? bar.AddComponent<RectTransform>();
            brt.anchorMin = new Vector2(0.5f, 0.5f);
            brt.anchorMax = new Vector2(0.5f, 0.5f);
            brt.pivot = new Vector2(1f, 1f);
            brt.sizeDelta = new Vector2(UiDataTableLayout.ScrollBarWidth, 10f);
            // Exactly the page's own scroll bar (MakeScrollbar): plain images, no sprite, a faint light track and a light
            // gray handle as wide as the track.
            var track = TryAdd<Image>(bar);
            if (track != null) try { track.color = new Color(1f, 1f, 1f, 0.10f); } catch { /* */ }
            var area = new GameObject("sliding-area");
            area.transform.SetParent(bar.transform, false);
            Stretch(area);
            var handle = new GameObject("handle");
            handle.transform.SetParent(area.transform, false);
            Stretch(handle);
            var handleImage = TryAdd<Image>(handle);
            if (handleImage != null) try { handleImage.color = new Color(1f, 1f, 1f, 0.45f); } catch { /* */ }
            scrollbar = TryAdd<Scrollbar>(bar);
            if (scrollbar != null)
            {
                try { scrollbar.direction = Scrollbar.Direction.BottomToTop; } catch { /* */ }
                try { scrollbar.handleRect = handle.GetComponent<RectTransform>(); } catch { /* */ }
                try { scrollbar.targetGraphic = handleImage; } catch { /* */ }
                try { scrollbar.size = 1f; } catch { /* */ }
                try { scrollbar.SetValueWithoutNotify(1f); } catch { /* */ }
            }

            try { bar.SetActive(false); } catch { /* shown once the widget overflows */ }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: widget scroll bar threw: " + e.Message);
            TryDestroyImmediate(bar);
            return null;
        }

        var entry = new WidgetScrollBar(first, last, bar, scrollbar);
        WidgetBars.Add(entry);
        UiWidgetLifetime.Register("widget-scroll-bars", DestroyWidgetBars);
        try { entry.Tick = EveryFrame(entry.Frame); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: widget scroll bar tick: " + e.Message); }
        return entry;
    }

    /// <summary>Core unload: every widget bar goes.</summary>
    private static void DestroyWidgetBars()
    {
        foreach (var bar in WidgetBars.ToArray()) bar.Dispose();
        WidgetBars.Clear();
    }

    /// <summary>One widget's bar: placed every frame over its rows, reporting drags.</summary>
    private sealed class WidgetScrollBar : IWidgetScrollBar
    {
        private readonly UnityCellRow _top;
        private readonly UnityCellRow _bottom;
        private GameObject? _go;
        private readonly Scrollbar? _bar;
        private bool _wanted;
        private bool _active;
        private float _size = 1f;
        private float _written = 1f;
        private Vector3 _placedAt = new(float.NaN, float.NaN, 0f);
        private Vector2 _placedSize = new(float.NaN, float.NaN);
        private bool _disposed;

        internal WidgetScrollBar(UnityCellRow top, UnityCellRow bottom, GameObject go, Scrollbar? bar)
        {
            _top = top;
            _bottom = bottom;
            _go = go;
            _bar = bar;
            // Looked up once: Place runs every frame.
            try { _topRt = top.GameObject.GetComponent<RectTransform>(); } catch { _topRt = null; }
            try { _bottomRt = bottom.GameObject.GetComponent<RectTransform>(); } catch { _bottomRt = null; }
            try { _rt = go.GetComponent<RectTransform>(); } catch { _rt = null; }
        }

        private readonly RectTransform? _topRt;
        private readonly RectTransform? _bottomRt;
        private readonly RectTransform? _rt;

        internal IDisposable? Tick;

        public Action<float>? OnDragged { get; set; }

        public bool Visible => _active;

        public void Set(float size, float position)
        {
            if (_disposed) return;
            _wanted = !float.IsNaN(size) && size < 0.999f;
            if (_bar == null) return;
            float s = float.IsNaN(size) ? 1f : Math.Max(0.05f, Math.Min(1f, size));
            float v = 1f - (float.IsNaN(position) ? 0f : Math.Max(0f, Math.Min(1f, position)));
            if (Math.Abs(s - _size) > 0.0005f)
            {
                try { _bar.size = s; } catch { /* */ }
                _size = s;
            }

            if (Math.Abs(v - _written) > 0.0005f)
            {
                try { _bar.SetValueWithoutNotify(v); } catch { /* */ }
                _written = v;
            }
        }

        /// <summary>One frame: follow the rows, show or hide, and report a drag.</summary>
        internal void Frame()
        {
            if (_disposed) return;
            bool alive;
            try { alive = _go != null && Alive(_go) && _top.Alive && _bottom.Alive; }
            catch { alive = false; }
            if (!alive)
            {
                Dispose();
                return;
            }

            bool shown = false;
            try { shown = _wanted && _top.GameObject.activeInHierarchy && _bottom.GameObject.activeInHierarchy; }
            catch { shown = false; }
            if (shown) shown = Place();
            if (shown != _active)
            {
                try { _go!.SetActive(shown); } catch { /* */ }
                _active = shown;
            }

            if (!shown || _bar == null) return;
            float v;
            try { v = _bar.value; }
            catch { return; }
            if (Math.Abs(v - _written) <= 0.0005f) return;
            _written = v;
            var drag = OnDragged;
            if (drag == null) return;
            try { drag(1f - v); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: widget scroll bar drag handler threw: " + e.Message); }
        }

        /// <summary>Over the rows' right inset, from the top row's top to the bottom row's bottom, last among its siblings.</summary>
        private bool Place()
        {
            try
            {
                var go = _go!;
                var parent = go.transform.parent;
                var topRt = _topRt;
                var bottomRt = _bottomRt;
                if (parent == null || topRt == null || bottomRt == null) return false;
                var tr = topRt.rect;
                var br = bottomRt.rect;
                Vector3 topRight = parent.InverseTransformPoint(topRt.TransformPoint(new Vector3(tr.xMax, tr.yMax, 0f)));
                Vector3 bottomRight = parent.InverseTransformPoint(bottomRt.TransformPoint(new Vector3(br.xMax, br.yMin, 0f)));
                float height = topRight.y - bottomRight.y - 2f * WidgetBarPadY;
                if (height < 8f) return false;
                var at = new Vector3(topRight.x - WidgetBarPadRight, topRight.y - WidgetBarPadY, 0f);
                var size = new Vector2(UiDataTableLayout.ScrollBarWidth, height);
                var rt = _rt;
                if (rt == null) return false;
                if ((at - _placedAt).sqrMagnitude > 0.01f || float.IsNaN(_placedAt.x))
                {
                    rt.localPosition = at;
                    _placedAt = at;
                }

                if ((size - _placedSize).sqrMagnitude > 0.01f || float.IsNaN(_placedSize.x))
                {
                    rt.sizeDelta = size;
                    _placedSize = size;
                }

                var t = go.transform;
                if (t.GetSiblingIndex() != parent.childCount - 1) t.SetAsLastSibling();
                return true;
            }
            catch { return false; }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            OnDragged = null;
            try { Tick?.Dispose(); } catch { /* */ }
            Tick = null;
            WidgetBars.Remove(this);
            var go = _go;
            _go = null;
            try { if (go != null && Alive(go)) UnityEngine.Object.Destroy(go); }
            catch { /* */ }
        }
    }

    // ---- IWidgetCellField ------------------------------------------------------------------------

    public bool CellFieldFocused(IRuntimeCellRow row, string cellKey)
    {
        var field = CellField(row, cellKey);
        if (field == null) return false;
        try { return ReferenceEquals(FocusedArmedField(), field); }
        catch { return false; }
    }

    public void SetCellHint(IRuntimeCellRow row, string cellKey, string hint)
    {
        var field = CellField(row, cellKey);
        if (field == null) return;
        try
        {
            var root = field.transform.Find(MenuRowStyle.SearchHintName);
            if (root == null) return;
            var tmp = FindIn<TextMeshProUGUI>(root.gameObject);
            if (tmp != null && tmp.text != (hint ?? "")) tmp.text = hint ?? "";
        }
        catch { /* the hint keeps its text */ }
        SyncSearchHint(field);
    }

    /// <summary>Escape in a table's value box: the box gives up the keyboard the way a click elsewhere takes it.</summary>
    public void ReleaseCellField(IRuntimeCellRow row, string cellKey)
    {
        var field = CellField(row, cellKey);
        if (field != null) ReleaseWidgetField(field);
    }

    /// <summary>
    /// P7b: a widget's value box gives up the keyboard (Escape, Enter, its row leaving the slot). The keys of the burst being
    /// typed stay the box's until they are all released (<see cref="_keyBurstHold"/>): no menu hotkey, no keybind and no
    /// typed text reaches anything else from a key that was meant for the box.
    /// </summary>
    private static void ReleaseWidgetField(TMP_InputField field)
    {
        _keyBurstHold = true;
        if (ReferenceEquals(_ownedFocusField, field)) NoteOwnedFocus(null);
        try { field.DeactivateInputField(); } catch { /* */ }
        try
        {
            var system = UnityEngine.EventSystems.EventSystem.current;
            if (system != null && system.currentSelectedGameObject != null && field.gameObject != null
                && system.currentSelectedGameObject.Pointer == field.gameObject.Pointer)
                system.SetSelectedGameObject(null);
        }
        catch { /* */ }
        SyncMenuHotkeyGuard(FocusedArmedField());
    }

    /// <summary>
    /// P7b: set while the keys of a typing burst are still held after a widget's value box released the keyboard; the
    /// shown-input pump keeps the field guard up meanwhile and clears it once no typing key is held.
    /// </summary>
    private static bool _keyBurstHold;

    /// <summary>P7b: the data table's value boxes: Enter commits them (they give up the keyboard; the table commits).</summary>
    private static readonly List<TMP_InputField> EnterLeavesFields = new();

    /// <summary>True for a value box Enter leaves (a data table cell); dead entries are pruned.</summary>
    private static bool EnterLeavesField(TMP_InputField field)
    {
        for (int i = EnterLeavesFields.Count - 1; i >= 0; i--)
        {
            var f = EnterLeavesFields[i];
            if (f == null || !Alive(f))
            {
                EnterLeavesFields.RemoveAt(i);
                continue;
            }

            if (ReferenceEquals(f, field)) return true;
        }

        return false;
    }

    /// <summary>P7b: the value boxes whose whole value is selected when they take the keyboard (typing replaces it).</summary>
    private static readonly List<TMP_InputField> SelectAllFields = new();

    /// <summary>True for a value box that selects its value on focus (a data table cell); dead entries are pruned.</summary>
    private static bool SelectsAllOnFocus(TMP_InputField field)
    {
        for (int i = SelectAllFields.Count - 1; i >= 0; i--)
        {
            var f = SelectAllFields[i];
            if (f == null || !Alive(f))
            {
                SelectAllFields.RemoveAt(i);
                continue;
            }

            if (ReferenceEquals(f, field)) return true;
        }

        return false;
    }

    /// <summary>P7b: the cell rows NAV1 marks with its frame instead of the row band (a soft row's own fill covers the band).</summary>
    private static readonly List<GameObject> NavFramedRows = new();

    /// <summary>Whether <paramref name="go"/> is a row that takes NAV1's frame; dead entries are pruned.</summary>
    private static bool NavFramedRow(GameObject? go)
    {
        if (go == null) return false;
        for (int i = NavFramedRows.Count - 1; i >= 0; i--)
        {
            var g = NavFramedRows[i];
            if (g == null || !Alive(g))
            {
                NavFramedRows.RemoveAt(i);
                continue;
            }

            if (g.Pointer == go.Pointer) return true;
        }

        return false;
    }

    public void FrameNavFocus(IRuntimeCellRow row)
    {
        if (row is not UnityCellRow cells || !cells.Alive) return;
        try { if (!NavFramedRow(cells.GameObject)) NavFramedRows.Add(cells.GameObject); }
        catch { /* the band stays */ }
    }

    public void SetCellJustify(IRuntimeCellRow row, string cellKey, CellAlign align, float leadPad)
    {
        if (row is not UnityCellRow cells || cellKey == null) return;
        var tmp = cells.CellText(cellKey);
        if (tmp == null || !Alive(tmp)) return;
        try
        {
            tmp.horizontalAlignment = align switch
            {
                CellAlign.Right => HorizontalAlignmentOptions.Right,
                CellAlign.Center => HorizontalAlignmentOptions.Center,
                _ => HorizontalAlignmentOptions.Left,
            };
            if (leadPad > 0f)
            {
                var m = tmp.margin;
                tmp.margin = new Vector4(2f + leadPad, m.y, m.z, m.w);
            }
        }
        catch { /* the cell keeps its alignment */ }
    }

    /// <summary>
    /// P7b: the value box that took the keyboard and selects its whole value at the start of the next typed-key pass, after
    /// anything the same click did to its caret (a click places a caret; the first click on such a box selects instead).
    /// </summary>
    private static TMP_InputField? _selectAllPending;

    /// <summary>
    /// True while <paramref name="field"/> waits for its select-all: a pointer caret placement on this same click must not
    /// run (the first click on a table value box selects its value; a second click places the caret).
    /// </summary>
    internal static bool SelectAllPendingFor(TMP_InputField? field) =>
        field != null && _selectAllPending != null && ReferenceEquals(_selectAllPending, field);

    /// <summary>How many passes the pending select-all has run (0: not applied yet).</summary>
    private static int _selectAllPasses;

    /// <summary>Queue the select-all of a value box that just took the keyboard (from the focus path).</summary>
    private static void QueueSelectAll(TMP_InputField field)
    {
        _selectAllPending = field;
        _selectAllPasses = 0;
    }

    /// <summary>
    /// Run a pending select-all on the focused field (the typed-key pass calls it before any key). The first pass selects
    /// the whole value (the kit's own Ctrl+A on the owned caret, which typing reads); the next
    /// <see cref="SelectAllHoldPasses"/> passes push that selection to the field again while it is still whole, because
    /// the field's own pointer handling and its activation run after the click and may move its native caret and
    /// selection. A key that changes the selection (typing, an arrow) ends it at once.
    /// </summary>
    private static void ApplyPendingSelectAll(TMP_InputField? focused)
    {
        var pending = _selectAllPending;
        if (pending == null) return;
        bool same = focused != null && ReferenceEquals(focused, pending) && Alive(pending);
        UiTextEditState state = default;
        bool whole = false;
        if (same && _selectAllPasses > 0)
        {
            try
            {
                state = ReadFieldState(pending);
                whole = state.HasSelection && state.SelStart == 0 && state.SelEnd == state.Text.Length;
            }
            catch { whole = false; }
        }

        switch (UiFocusSelectAll.Step(_selectAllPasses, same, whole))
        {
            case UiSelectAllStep.Select:
                ApplyShortcut(pending, 'a');
                _selectAllPasses = 1;
                return;
            case UiSelectAllStep.Hold:
                try
                {
                    PushTmpCaret(pending, state);
                    SyncOwnedCarets();
                }
                catch { /* */ }

                _selectAllPasses++;
                return;
            default:
                _selectAllPending = null;
                return;
        }
    }

    public void SetupCellField(IRuntimeCellRow row, string cellKey, bool rightAligned, bool enterCommits, bool selectAllOnFocus)
    {
        var field = CellField(row, cellKey);
        if (field == null) return;
        if (selectAllOnFocus && !SelectsAllOnFocus(field)) SelectAllFields.Add(field);
        if (rightAligned)
        {
            // The numbers line up on their last digit, as the column's figures do (the owned caret follows the alignment).
            try { if (field.textComponent != null) field.textComponent.horizontalAlignment = HorizontalAlignmentOptions.Right; }
            catch { /* */ }
            try
            {
                var hint = field.transform.Find(MenuRowStyle.SearchHintName);
                var tmp = hint == null ? null : FindIn<TextMeshProUGUI>(hint.gameObject);
                if (tmp != null) tmp.horizontalAlignment = HorizontalAlignmentOptions.Right;
            }
            catch { /* */ }
        }

        if (enterCommits && !EnterLeavesField(field)) EnterLeavesFields.Add(field);
    }

    /// <summary>
    /// A data table's filler row: its row button (the one the hover and press tints live on) stops being interactable,
    /// so uGUI draws its normal (unhovered) fill and raises no click; no <c>CanvasGroup</c>, so nothing is dimmed.
    /// </summary>
    public void SetRowInert(IRuntimeCellRow row, bool inert)
    {
        if (row is not UnityCellRow cells || !cells.Alive) return;
        try
        {
            var button = cells.GameObject.GetComponent<Button>();
            if (button != null) button.interactable = !inert;
        }
        catch { /* the row keeps its tints; the table still ignores its clicks */ }
    }

    /// <summary>The colors header captions had before a sort turned them gold, to put back.</summary>
    private static readonly Dictionary<TextMeshProUGUI, Color> CaptionColors = new();

    public void SetCellTextColor(IRuntimeCellRow row, string cellKey, PaletteColor? color)
    {
        if (row is not UnityCellRow cells || cellKey == null) return;
        var tmp = cells.CellText(cellKey);
        if (tmp == null || !Alive(tmp)) return;
        try
        {
            foreach (var dead in new List<TextMeshProUGUI>(CaptionColors.Keys))
                if (dead == null || !Alive(dead)) CaptionColors.Remove(dead!);
            if (color is { } c)
            {
                if (!CaptionColors.ContainsKey(tmp)) CaptionColors[tmp] = tmp.color;
                UiWidgetLifetime.Register("caption-colors", CaptionColors.Clear);
                tmp.color = ToColor(c);
            }
            else if (CaptionColors.TryGetValue(tmp, out var own))
            {
                tmp.color = own;
                CaptionColors.Remove(tmp);
            }
        }
        catch { /* the caption keeps its color */ }
    }

    private static TMP_InputField? CellField(IRuntimeCellRow row, string cellKey)
    {
        if (row == null || cellKey == null) return null;
        try
        {
            var widget = row.CellWidget(cellKey);
            var go = widget == null ? null : NavObject(widget);
            return go == null ? null : FindIn<TMP_InputField>(go);
        }
        catch { return null; }
    }
}
#endif
