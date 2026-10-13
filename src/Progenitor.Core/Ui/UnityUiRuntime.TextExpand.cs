#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Progenitor.Ui;

/// <summary>
/// TEXTEXPAND1: the runtime half of the expanded text editor and of wheel scrolling in text areas.
///
/// <list type="bullet">
/// <item><see cref="AddCornerLink"/>: the expand button, a small square in a text field's bottom-right
/// corner (the field's text keeps clear of it).</item>
/// <item><see cref="MakeEditorField"/>: a dialog's input cell made the large editor — multi-line,
/// word-wrapped, a vertical scroll bar in the game's <c>ScrollBar_Bar</c> / <c>ScrollBar_Handle</c> art
/// (the README reader's), and optionally read-only (focus, caret, selection and copy; no edit).</item>
/// <item><see cref="TryScrollTextField"/>: a wheel notch over a multi-line field moves its text
/// (<see cref="UiTextScroll.WheelShift"/>); at the end of the text the notch goes to the list.</item>
/// <item><see cref="TickEditorBars"/>: the bar follows the text after an edit, a caret move or the
/// wheel, and a drag of the bar moves the text. It runs only while an editor is open; an idle editor
/// costs, per frame, one liveness check of its field and bar and one counter check, and writes nothing.</item>
/// </list>
///
/// <para>The text moves inside the field's own viewport (<c>textComponent.rectTransform</c>), the same
/// transform TEXTAREA2's caret scroll writes. A scroll the player made holds
/// (<see cref="ArmedField.ViewHeld"/>): the caret placement does not pull the text back to the caret,
/// and the caret hides while its line is out of view, until the caret or the text changes.</para>
/// </summary>
internal sealed partial class UnityUiRuntime
{
    /// <summary>TEXTEXPAND1: the editor's scroll bar and what it last showed.</summary>
    internal sealed class EditorScrollBar
    {
        public TMP_InputField Field = null!;
        public GameObject? Go;
        public Scrollbar? Bar;
        public int Dirty;
        public bool Visible = true;
        public float Size = 1f;
        public float Value = 1f;
    }

    private static readonly List<EditorScrollBar> EditorBars = new();

    /// <summary>TEXTEXPAND1: the live expand buttons (children of their fields), so a press on one is not a click in the field.</summary>
    private static readonly List<GameObject> CornerButtons = new();

    /// <summary>
    /// TEXTEXPAND1: true when (<paramref name="x"/>, <paramref name="y"/>) is on an editor's scroll bar
    /// or a text area's expand button — controls inside a field's rect whose press is their own. With a
    /// dialog open only the dialog's controls count. Dead entries are pruned.
    /// </summary>
    private static bool OverFieldChrome(float x, float y, Transform? dialogRoot)
    {
        for (int i = CornerButtons.Count - 1; i >= 0; i--)
        {
            var go = CornerButtons[i];
            bool alive;
            try { alive = Alive(go); }
            catch { alive = false; }
            if (!alive)
            {
                CornerButtons.RemoveAt(i);
                continue;
            }
            if (Hit(go, x, y, dialogRoot)) return true;
        }

        for (int i = 0; i < EditorBars.Count; i++)
        {
            var b = EditorBars[i];
            if (b.Go != null && b.Visible && Hit(b.Go, x, y, dialogRoot)) return true;
        }

        return false;
    }

    private static bool Hit(GameObject go, float x, float y, Transform? dialogRoot)
    {
        try
        {
            if (!Alive(go) || !go.activeInHierarchy) return false;
            if (dialogRoot != null && !InsideDialog(go, dialogRoot)) return false;
            var rt = go.GetComponent<RectTransform>();
            if (rt == null) return false;
            var rect = ScreenRectOf(rt);
            return rect.IsKnown && rect.Contains(x, y);
        }
        catch { return false; }
    }

    // ---- the expand button ------------------------------------------------------------------------

    /// <summary>The expand glyph when the font draws it, else the ellipsis (no missing-glyph box).</summary>
    private static string ChooseGlyph(string? glyph, TMP_Text? tmp)
    {
        string want = string.IsNullOrEmpty(glyph) ? UiTextExpandLayout.Glyph : glyph!;
        if (tmp == null) return want.Length == 1 && want[0] > 0xFF ? UiTextExpandLayout.FallbackGlyph : want;
        try
        {
            var font = tmp.font;
            if (font != null && want.Length == 1 && !font.HasCharacter(want[0], true, false)) return UiTextExpandLayout.FallbackGlyph;
        }
        catch { return UiTextExpandLayout.FallbackGlyph; }
        return want;
    }

    /// <summary>
    /// RADIOCLICK2: the first of <see cref="UiOptionsRules.GlyphCandidates"/> the TMP's font (or its fallbacks) draws, so a
    /// radio circle is never a missing-glyph box or nothing; the last candidate (ASCII) when none can be checked.
    /// </summary>
    internal static string DrawableGlyph(TMP_Text? tmp, string? text)
    {
        var candidates = UiOptionsRules.GlyphCandidates(text);
        if (candidates.Count <= 1) return text ?? "";
        try
        {
            var font = tmp?.font;
            if (font == null) return candidates[0];
            for (int i = 0; i < candidates.Count; i++)
            {
                string c = candidates[i];
                if (c.Length == 1 && (c[0] <= 0x7F || font.HasCharacter(c[0], true, false))) return c;
            }
        }
        catch { return candidates[candidates.Count - 1]; }
        return candidates[candidates.Count - 1];
    }

    /// <summary>Narrow the text viewport so the text wraps before the corner button.</summary>
    private static void KeepTextClearOfCorner(TMP_InputField field)
    {
        try
        {
            var viewport = field.textViewport;
            if (viewport == null) return;
            var max = viewport.offsetMax;
            viewport.offsetMax = new Vector2(max.x - UiTextExpandLayout.ButtonSize, max.y);
        }
        catch { /* the button sits over the text's last column */ }
    }

    // ---- the editor --------------------------------------------------------------------------------

    /// <summary>
    /// TEXTEXPAND1: make a dialog's input cell (<paramref name="control"/>, already armed) the large
    /// editor: multi-line, wrapped from the top left, a vertical bar at its right in the game's art,
    /// and <paramref name="readOnly"/> when the value may only be read.
    /// </summary>
    private static void MakeEditorField(GameObject control, TMP_InputField field, bool readOnly)
    {
        if (control == null || field == null) return;
        try { field.lineType = TMP_InputField.LineType.MultiLineNewline; } catch { /* */ }
        try
        {
            var tmp = field.textComponent;
            if (tmp != null)
            {
                tmp.enableWordWrapping = true;
                tmp.verticalAlignment = VerticalAlignmentOptions.Top;
                tmp.horizontalAlignment = HorizontalAlignmentOptions.Left;
            }
        }
        catch { /* */ }

        try
        {
            var viewport = field.textViewport;
            if (viewport != null)
            {
                var max = viewport.offsetMax;
                viewport.offsetMax = new Vector2(max.x - UiTextExpandLayout.ScrollBarGutter, max.y);
            }
        }
        catch { /* */ }

        var armed = FindArmed(field);
        if (armed != null) armed.ReadOnly = readOnly;

        GameObject? bar = null;
        Scrollbar? scrollbar = null;
        try
        {
            bar = new GameObject("editor-scroll-bar");
            bar.transform.SetParent(control.transform, false);
            var brt = bar.GetComponent<RectTransform>() ?? bar.AddComponent<RectTransform>();
            brt.anchorMin = new Vector2(1f, 0f);
            brt.anchorMax = new Vector2(1f, 1f);
            brt.pivot = new Vector2(1f, 0.5f);
            brt.offsetMin = new Vector2(-(CellLayout.ScrollBarWidth + 4f), 4f);
            brt.offsetMax = new Vector2(-4f, -4f);
            var track = TryAdd<Image>(bar);
            PaintPart(track, GameArt.SettingsArtPart.ScrollbarTrack, Color.white, UnityPanel.ScrollTrack);
            var area = new GameObject("sliding-area");
            area.transform.SetParent(bar.transform, false);
            Stretch(area);
            var handle = new GameObject("handle");
            handle.transform.SetParent(area.transform, false);
            Stretch(handle);
            var handleImage = TryAdd<Image>(handle);
            PaintPart(handleImage, GameArt.SettingsArtPart.ScrollbarHandle, Color.white, UnityPanel.ScrollHandle);
            scrollbar = TryAdd<Scrollbar>(bar);
            if (scrollbar != null)
            {
                try { scrollbar.direction = Scrollbar.Direction.BottomToTop; } catch { /* */ }
                try { scrollbar.handleRect = handle.GetComponent<RectTransform>(); } catch { /* */ }
                try { scrollbar.targetGraphic = handleImage; } catch { /* */ }
                try { scrollbar.size = 1f; } catch { /* */ }
                try { scrollbar.value = 1f; } catch { /* */ }
            }
            try { bar.SetActive(false); } catch { /* shown once the text overflows */ }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: editor scroll bar threw: " + e.Message);
            TryDestroyImmediate(bar);
            bar = null;
            scrollbar = null;
        }

        var entry = new EditorScrollBar { Field = field, Go = bar, Bar = scrollbar, Dirty = 3, Visible = false };
        if (armed != null) armed.Bar = entry;
        EditorBars.Add(entry);
    }

    /// <summary>The bar follows the text for the next frames (after an edit, a caret move or a scroll).</summary>
    private static void MarkEditorBarDirty(ArmedField? armed)
    {
        if (armed?.Bar is { } bar) bar.Dirty = 3;
    }

    /// <summary>One pump frame for the open editors' bars: a drag moves the text; a dirty bar re-reads the text.</summary>
    private static void TickEditorBars(bool dragging)
    {
        for (int i = EditorBars.Count - 1; i >= 0; i--)
        {
            var b = EditorBars[i];
            bool alive;
            try { alive = Alive(b.Field) && (b.Go == null || Alive(b.Go)); }
            catch { alive = false; }
            if (!alive)
            {
                EditorBars.RemoveAt(i);
                continue;
            }

            if (dragging && b.Visible && b.Bar != null)
            {
                float v;
                try { v = b.Bar.value; }
                catch { continue; }
                if (Math.Abs(v - b.Value) > 0.002f)
                {
                    if (TextExtent(b.Field, out float above, out float below, out _))
                    {
                        float shift = UiTextScroll.ShiftForBarValue(v, above, below);
                        // The drag owns the bar's value: the text follows it, the bar is not re-written.
                        if (Math.Abs(shift) > 0.5f) MoveText(b.Field, shift, syncBar: false);
                    }
                    b.Value = v;
                    continue;
                }
            }

            if (b.Dirty <= 0) continue;
            b.Dirty--;
            SyncEditorBar(b);
        }
    }

    private static void SyncEditorBar(EditorScrollBar b)
    {
        if (!TextExtent(b.Field, out float above, out float below, out float viewH)) return;
        var (visible, size, value) = UiTextScroll.Bar(above, below, viewH);
        if (visible != b.Visible && b.Go != null)
        {
            try { b.Go.SetActive(visible); } catch { /* */ }
            b.Visible = visible;
        }
        if (!visible || b.Bar == null) return;
        if (Math.Abs(size - b.Size) > 0.001f)
        {
            try { b.Bar.size = size; } catch { /* */ }
            b.Size = size;
        }
        if (Math.Abs(value - b.Value) > 0.001f)
        {
            try { b.Bar.SetValueWithoutNotify(value); }
            catch
            {
                try { b.Bar.value = value; } catch { /* */ }
            }
            b.Value = value;
        }
    }

    // ---- moving the text ---------------------------------------------------------------------------

    /// <summary>
    /// How much of the field's text is hidden above and below its viewport (px, the viewport's space),
    /// and the viewport's height. False when the field has no text component or viewport.
    /// </summary>
    private static bool TextExtent(TMP_InputField field, out float above, out float below, out float viewHeight)
    {
        above = below = viewHeight = 0f;
        try
        {
            var tmp = field.textComponent;
            var viewport = field.textViewport;
            if (tmp == null || viewport == null) return false;
            var b = tmp.textBounds;
            var trt = tmp.rectTransform;
            Vector3 top = viewport.InverseTransformPoint(trt.TransformPoint(new Vector3(0f, b.max.y, 0f)));
            Vector3 bottom = viewport.InverseTransformPoint(trt.TransformPoint(new Vector3(0f, b.min.y, 0f)));
            Rect v = viewport.rect;
            viewHeight = v.height;
            if (b.size.y < 0.5f) return true;
            above = Math.Max(0f, top.y - v.yMax);
            below = Math.Max(0f, v.yMin - bottom.y);
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Move the field's text up by <paramref name="shift"/> (down when negative) and hold the view there.
    /// With <paramref name="syncBar"/> the editor's bar is brought to the new position at once (TEXTUNDO1
    /// polish: it used to follow only on the next frames, so a readback taken in the same call — the
    /// bridge's <c>field_scroll</c> — read the bar of the previous position); the next frames re-check it.
    /// </summary>
    private static void MoveText(TMP_InputField field, float shift, bool syncBar = true)
    {
        try
        {
            var trt = field.textComponent.rectTransform;
            var pos = trt.anchoredPosition;
            trt.anchoredPosition = new Vector2(pos.x, pos.y + shift);
            TextFieldScrollWrites++;
        }
        catch { return; }
        var armed = FindArmed(field);
        if (armed == null) return;
        armed.ViewHeld = true;
        MarkEditorBarDirty(armed);
        if (syncBar && armed.Bar is { } bar)
        {
            try { SyncEditorBar(bar); }
            catch { /* the dirty frames retry */ }
        }
    }

    /// <summary>One wheel event on <paramref name="field"/>: the shift applied (0 when the text could not move that way).</summary>
    private static float ScrollTextFieldBy(TMP_InputField field, float deltaY)
    {
        if (!TextExtent(field, out float above, out float below, out _)) return 0f;
        float shift = UiTextScroll.WheelShift(deltaY, above, below);
        if (Math.Abs(shift) < 0.5f) return 0f;
        MoveText(field, shift);
        return shift;
    }

    /// <summary>
    /// TEXTEXPAND1: a wheel notch over a multi-line field (a text area, the expanded editor) moves its
    /// text while it can move that way. With a dialog open only the dialog's fields take the wheel.
    /// </summary>
    private static bool TryScrollTextField(PointerSample pointer, float deltaY, UnityPanel? hit)
    {
        if (pointer.Kind != PointerKind.Inside || _armedFields.Count == 0 || hit == null) return false;
        // Only a field of the panel under the pointer, and only where that panel's list shows it (a
        // text area scrolled out of the list's viewport is clipped there, not under the pointer).
        Transform? panelRoot;
        try { panelRoot = hit.GameObject != null ? hit.GameObject.transform : null; }
        catch { panelRoot = null; }
        if (panelRoot == null) return false;
        try
        {
            var listViewport = hit.Scroll != null ? hit.Scroll.viewport : null;
            if (listViewport != null)
            {
                var view = ScreenRectOf(listViewport);
                if (view.IsKnown && !view.Contains(pointer.X, pointer.Y)) return false;
            }
        }
        catch { /* no list viewport: the panel's own rect already holds the pointer */ }
        var dialogRoot = OpenDialogRoot();
        for (int i = _armedFields.Count - 1; i >= 0; i--)
        {
            var field = _armedFields[i]?.Field;
            if (field == null) continue;
            bool usable;
            try { usable = Alive(field) && field.gameObject.activeInHierarchy && IsMultiline(field); }
            catch { usable = false; }
            if (!usable) continue;
            if (dialogRoot != null && !InsideDialog(field.gameObject, dialogRoot)) continue;
            if (!InsideDialog(field.gameObject, panelRoot)) continue;
            RectTransform? viewport;
            try { viewport = field.textViewport; }
            catch { continue; }
            if (viewport == null) continue;
            ScreenRect rect;
            try { rect = ScreenRectOf(viewport); }
            catch { continue; }
            if (!rect.IsKnown || !rect.Contains(pointer.X, pointer.Y)) continue;
            return ScrollTextFieldBy(field, deltaY) != 0f;
        }

        return false;
    }

    // ---- readback ----------------------------------------------------------------------------------

    /// <summary>
    /// TEXTEXPAND1 readback: what part of a multi-line field's text is in view — TMP's laid-out lines,
    /// the first and last in view, the characters they hold, the hidden heights above and below, how
    /// far down the text the view is, the bar, and whether the view is held.
    /// </summary>
    internal static Dictionary<string, object?> DescribeTextView(TMP_InputField? field)
    {
        var d = new Dictionary<string, object?>();
        if (field == null) return d;
        TextExtent(field, out float above, out float below, out float viewH);
        d["viewHeight"] = (float)Math.Round(viewH, 1);
        d["textAbove"] = (float)Math.Round(above, 1);
        d["textBelow"] = (float)Math.Round(below, 1);
        d["fraction"] = (float)Math.Round(UiTextScroll.Fraction(above, below), 4);
        d["atTop"] = above < 0.5f;
        d["atEnd"] = below < 0.5f;
        try
        {
            var tmp = field.textComponent;
            var viewport = field.textViewport;
            var info = tmp != null ? tmp.textInfo : null;
            if (tmp != null && viewport != null && info != null)
            {
                int lineCount = info.lineCount;
                var lines = info.lineInfo;
                d["lineCount"] = lineCount;
                if (lines != null && lineCount > 0 && lineCount <= lines.Length)
                {
                    var trt = tmp.rectTransform;
                    var tops = new List<float>(lineCount);
                    var bottoms = new List<float>(lineCount);
                    for (int i = 0; i < lineCount; i++)
                    {
                        var line = lines[i];
                        tops.Add(viewport.InverseTransformPoint(trt.TransformPoint(new Vector3(0f, line.ascender, 0f))).y);
                        bottoms.Add(viewport.InverseTransformPoint(trt.TransformPoint(new Vector3(0f, line.descender, 0f))).y);
                    }

                    Rect v = viewport.rect;
                    var (first, last) = UiTextScroll.VisibleLines(tops, bottoms, v.yMax, v.yMin);
                    d["firstVisibleLine"] = first;
                    d["lastVisibleLine"] = last;
                    d["visibleLines"] = first < 0 ? 0 : last - first + 1;
                    if (first >= 0)
                    {
                        d["firstVisibleChar"] = lines[first].firstCharacterIndex;
                        d["lastVisibleChar"] = lines[last].lastCharacterIndex;
                    }
                }
            }
        }
        catch (Exception e) { d["linesWhy"] = "read-threw:" + e.Message; }

        var armed = FindArmed(field);
        if (armed != null)
        {
            d["viewHeld"] = armed.ViewHeld;
            d["caretOutOfView"] = armed.CaretOutOfView;
            d["readOnly"] = armed.ReadOnly;
            if (armed.Bar is { } bar)
            {
                // TEXTUNDO1 polish: bring the bar to the text first (as the next frame would), so the
                // readback describes this view and not the one before the last scroll or edit.
                try { SyncEditorBar(bar); } catch { /* */ }
                float value = bar.Value;
                try { if (bar.Bar != null) value = bar.Bar.value; } catch { /* */ }
                var (_, _, expected) = UiTextScroll.Bar(above, below, viewH);
                d["bar"] = new Dictionary<string, object?>
                {
                    ["visible"] = bar.Visible,
                    ["size"] = (float)Math.Round(bar.Size, 4),
                    ["value"] = (float)Math.Round(value, 4),
                    // The mapping: value = 1 − fraction (bottom-to-top: 1 at the top of the text, 0 at its end).
                    ["expectedValue"] = (float)Math.Round(expected, 4),
                    ["direction"] = UiTextScroll.BarDirection,
                    ["agrees"] = !bar.Visible || Math.Abs(value - expected) <= 0.01f,
                    ["art"] = "ScrollBar_Bar / ScrollBar_Handle (kit colors when not borrowed)",
                };
            }
        }

        return d;
    }

    /// <summary>
    /// TEXTEXPAND1 bridge <c>field_scroll</c>: <paramref name="notches"/> wheel notches on the focused
    /// field (positive toward the end, as a wheel turned toward the player), or straight to
    /// <paramref name="to"/> (<c>top</c> / <c>end</c>), through the wheel's own path.
    /// </summary>
    internal static Dictionary<string, object?> TestScroll(int notches, string? to)
    {
        var field = CaretFocusField() ?? OpenEditorField();
        if (field == null) return TestError("no focused multi-line field and no open editor; run mods_ui field_focus or field_expand first");
        if (!IsMultiline(field)) return TestError("the focused field is a single line; field_scroll scrolls a text area or the editor");
        string target = (to ?? "").Trim().ToLowerInvariant();
        int steps = target is "top" or "end" ? 10_000 : Math.Max(-10_000, Math.Min(10_000, notches));
        float delta = target == "top" ? 120f : target == "end" ? -120f : steps > 0 ? -120f : 120f;
        int moved = 0;
        float total = 0f;
        for (int i = 0; i < Math.Abs(steps); i++)
        {
            float shift = ScrollTextFieldBy(field, delta);
            if (shift == 0f) break;
            moved++;
            total += shift;
        }

        return new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["notchesApplied"] = moved,
            ["pixels"] = (float)Math.Round(total, 1),
            ["handler"] = "UnityUiRuntime.ScrollTextFieldBy (the wheel's path)",
            ["view"] = DescribeTextView(field),
        };
    }

    /// <summary>The open expanded editor's field (the dialog's input cell), or null.</summary>
    internal static TMP_InputField? OpenEditorField()
    {
        var root = OpenDialogRoot();
        if (root == null) return null;
        for (int i = _armedFields.Count - 1; i >= 0; i--)
        {
            var armed = _armedFields[i];
            var f = armed?.Field;
            if (f == null || armed!.Bar == null) continue;
            try { if (f.transform.IsChildOf(root)) return f; }
            catch { /* */ }
        }

        return null;
    }
}
#endif
