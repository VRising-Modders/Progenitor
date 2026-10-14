#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Progenitor.Ui;

/// <summary>
/// P7 code area: the runtime half. The area is TEXTEXPAND1's editor field (a cell row's input cell with
/// <see cref="CellSpec.EditorLines"/> on the settings value box, the game's scroll bar art, the wheel) and this
/// partial decorates it:
///
/// <list type="bullet">
/// <item><b>Gutter</b>: the field's text viewport is narrowed from the left by the gutter's width
/// (<see cref="UiCodeAreaModel.GutterWidth"/>) and the gutter sits in the room that frees, as a sibling of the
/// viewport with its own clip: a red marker column, the line numbers (muted, right-aligned) and a thin separator.
/// Its labels copy the text component's vertical placement every frame, so they scroll with the text, and the
/// caret and selection math stays the field's own (the text component itself is not moved or re-margined).</item>
/// <item><b>Colors</b>: an overlay label under the text component (same font, size, spacing, margins and
/// alignment, rich text with the caller's color tags, escaped) draws the text; the field's own glyphs are made
/// transparent (the kit's caret is a separate graphic and stays). A later repaint of the field's color (graying)
/// is picked up as the overlay's base color and hidden again.</item>
/// <item><b>Errors</b>: a red dot in the marker column, a faint red band across the line (under the text
/// component, so it scrolls and clips with it) and, while the pointer is over an error line's gutter, the
/// kit's tooltip (<see cref="IWidgetRuntime.ShowTooltip"/>) with the message.</item>
/// <item><b>Wrap off</b>: one text line is one gutter line. A line wider than the viewport is clipped by the
/// viewport's mask (the kit scrolls multi-line fields vertically only).</item>
/// </list>
///
/// <para>One frame subscription serves every live area; it is dropped when the last area goes and on Core
/// unload (<see cref="UiWidgetLifetime"/>). An idle area costs one liveness check, one color read and one
/// offset compare per frame; the pointer is read only while some area shows errors.</para>
/// </summary>
internal sealed partial class UnityUiRuntime : ICodeAreaRuntime
{
    private static readonly List<CodeAreaView> CodeAreas = new();
    private static bool _codeAreaTicking;

    public ICodeAreaView? AttachCodeArea(IRuntimeCellRow row, string cellKey, object? owner)
    {
        if (row is not UnityCellRow unityRow || !unityRow.Alive) return null;
        TMP_InputField? field;
        try { field = FindIn<TMP_InputField>(unityRow.GameObject); }
        catch { field = null; }
        if (field == null) return null;
        CodeAreaView? view = null;
        try { view = CodeAreaView.Build(field); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: code area build threw: " + e.Message); }
        if (view == null) return null;
        CodeAreas.Add(view);
        EnsureCodeAreaTick();
        return view;
    }

    private static void EnsureCodeAreaTick()
    {
        if (_codeAreaTicking) return;
        Plugin.Frame += TickCodeAreas;
        _codeAreaTicking = true;
        UiWidgetLifetime.Register("code-area", StopCodeAreas);
    }

    private static void StopCodeAreaTick()
    {
        if (!_codeAreaTicking) return;
        Plugin.Frame -= TickCodeAreas;
        _codeAreaTicking = false;
    }

    /// <summary>Core unload: drop the frame subscription and every area's decorations.</summary>
    private static void StopCodeAreas()
    {
        StopCodeAreaTick();
        var snapshot = CodeAreas.ToArray();
        CodeAreas.Clear();
        foreach (var view in snapshot)
        {
            try { view.Dispose(); } catch { /* the row's own release destroys what is left */ }
        }
    }

    private static void TickCodeAreas()
    {
        bool sampled = false, inside = false;
        float px = 0f, py = 0f;
        for (int i = CodeAreas.Count - 1; i >= 0; i--)
        {
            var view = CodeAreas[i];
            if (!view.Alive)
            {
                view.Forget();
                CodeAreas.RemoveAt(i);
                continue;
            }

            try
            {
                view.Tick();
                if (view.WantsPointer)
                {
                    if (!sampled)
                    {
                        sampled = true;
                        try { inside = TrySamplePointerScreen(out px, out py); }
                        catch { inside = false; }
                    }
                    view.Hover(inside, px, py);
                }
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("Ui: code area tick threw (area dropped): " + e.Message);
                try { view.Dispose(); } catch { /* */ }
                CodeAreas.RemoveAt(i);
            }
        }

        if (CodeAreas.Count == 0) StopCodeAreaTick();
    }

    /// <summary>One decorated field.</summary>
    private sealed class CodeAreaView : ICodeAreaView
    {
        private readonly TMP_InputField _field;
        private readonly TextMeshProUGUI _tmp;
        private readonly RectTransform _viewport;
        private readonly float _baseViewportLeft;
        private readonly bool _wrapBefore;
        private readonly bool _ctrlBefore;
        private readonly GameObject _gutter;
        private readonly RectTransform _gutterRt;
        private readonly TextMeshProUGUI _numbers;
        private readonly TextMeshProUGUI _markers;
        private readonly RectTransform? _separator;
        private readonly TextMeshProUGUI _overlay;
        private readonly List<Image> _bands = new();
        private readonly string _glyph;
        private readonly float _digitAdvance;
        private Color _ink;
        private float _gutterWidth = -1f;
        private int _lineCount = -1;
        private IReadOnlyList<UiLineError> _errors = Array.Empty<UiLineError>();
        private readonly List<(int Line, float Top, float Bottom, string Message)> _errorRows = new();
        private bool _layoutDirty = true;
        private Vector4 _lastTextY = new(float.NaN, 0f, 0f, 0f);
        private int _hoverLine;
        private bool _disposed;
        private int _overlayLines = -1;

        private CodeAreaView(
            TMP_InputField field, TextMeshProUGUI tmp, RectTransform viewport, float baseLeft, bool wrapBefore, bool ctrlBefore,
            GameObject gutter, TextMeshProUGUI numbers, TextMeshProUGUI markers, RectTransform? separator,
            TextMeshProUGUI overlay, string glyph, float digitAdvance, Color ink)
        {
            _field = field;
            _tmp = tmp;
            _viewport = viewport;
            _baseViewportLeft = baseLeft;
            _wrapBefore = wrapBefore;
            _ctrlBefore = ctrlBefore;
            _gutter = gutter;
            _gutterRt = gutter.GetComponent<RectTransform>();
            _numbers = numbers;
            _markers = markers;
            _separator = separator;
            _overlay = overlay;
            _glyph = glyph;
            _digitAdvance = digitAdvance;
            _ink = ink;
        }

        internal static CodeAreaView? Build(TMP_InputField field)
        {
            TextMeshProUGUI? tmp = null;
            try { tmp = field.textComponent != null ? field.textComponent.TryCast<TextMeshProUGUI>() : null; }
            catch { tmp = null; }
            var viewport = field.textViewport;
            if (tmp == null || viewport == null) return null;
            var parent = viewport.parent;
            if (parent == null) return null;

            bool wrapBefore = true, ctrlBefore = true;
            try { wrapBefore = tmp.enableWordWrapping; } catch { /* */ }
            try { ctrlBefore = tmp.parseCtrlCharacters; } catch { /* */ }
            // Code: one text line per numbered line, and a typed backslash-n stays two characters.
            try { tmp.enableWordWrapping = false; } catch { /* */ }
            try { tmp.parseCtrlCharacters = false; } catch { /* */ }
            try { tmp.richText = false; } catch { /* */ }
            try { field.richText = false; } catch { /* */ }

            Color ink;
            try { ink = tmp.color; } catch { ink = ToColor(MenuPalette.Text); }
            if (ink.a < 0.01f) ink = ToColor(MenuPalette.Text);

            float baseLeft;
            try { baseLeft = viewport.offsetMin.x; } catch { baseLeft = InputPadX; }

            GameObject? gutter = null;
            GameObject? overlayGo = null;
            try
            {
                // The gutter: a clipped strip left of the text viewport, as tall as it.
                gutter = new GameObject("code-gutter");
                gutter.transform.SetParent(parent, false);
                var grt = gutter.GetComponent<RectTransform>() ?? gutter.AddComponent<RectTransform>();
                var vMin = viewport.anchorMin;
                var vMax = viewport.anchorMax;
                grt.anchorMin = new Vector2(vMin.x, vMin.y);
                grt.anchorMax = new Vector2(vMin.x, vMax.y);
                grt.pivot = new Vector2(0f, 0.5f);
                TryAdd<RectMask2D>(gutter);

                var markers = MakeGutterLabel(gutter, "code-markers", tmp, HorizontalAlignmentOptions.Center);
                var numbers = MakeGutterLabel(gutter, "code-numbers", tmp, HorizontalAlignmentOptions.Right);
                if (markers == null || numbers == null)
                {
                    TryDestroyImmediate(gutter);
                    return null;
                }
                try { markers.color = ToColor(MenuPalette.Red); } catch { /* */ }
                try { numbers.color = ToColor(MenuPalette.Muted); } catch { /* */ }

                RectTransform? separator = null;
                try
                {
                    var sep = new GameObject("code-separator");
                    sep.transform.SetParent(gutter.transform, false);
                    var line = TryAdd<Image>(sep);
                    if (line != null)
                    {
                        try { line.raycastTarget = false; } catch { /* */ }
                        try { line.maskable = true; } catch { /* */ }
                        var m = MenuPalette.Muted;
                        try { line.color = new Color(m.R, m.G, m.B, 0.35f); } catch { /* */ }
                        separator = sep.GetComponent<RectTransform>();
                    }
                }
                catch { separator = null; }

                // The overlay: the same text in the caller's colors, laid out exactly as the field's own.
                overlayGo = new GameObject("code-colors");
                overlayGo.transform.SetParent(tmp.transform, false);
                Stretch(overlayGo);
                var overlay = TryAdd<TextMeshProUGUI>(overlayGo);
                if (overlay == null)
                {
                    TryDestroyImmediate(overlayGo);
                    TryDestroyImmediate(gutter);
                    return null;
                }
                CopyTextLook(tmp, overlay);
                try { overlay.richText = true; } catch { /* */ }
                try { overlay.parseCtrlCharacters = false; } catch { /* */ }
                try { overlay.enableWordWrapping = false; } catch { /* */ }
                try { overlay.color = ink; } catch { /* */ }
                try { overlay.text = ""; } catch { /* */ }

                string glyph = ChooseMarkerGlyph(markers);
                float advance = UiCodeAreaModel.FallbackDigitAdvance;
                try
                {
                    var size = numbers.GetPreferredValues("0000000000");
                    if (size.x > 1f) advance = size.x / 10f;
                }
                catch { /* the fallback width */ }

                // Hide the field's own glyphs; the caret and the selection are separate graphics.
                try { tmp.color = new Color(ink.r, ink.g, ink.b, 0f); } catch { /* */ }

                return new CodeAreaView(field, tmp, viewport, baseLeft, wrapBefore, ctrlBefore, gutter, numbers, markers, separator,
                    overlay, glyph, advance, ink);
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("Ui: code area decoration failed: " + e.Message);
                TryDestroyImmediate(overlayGo);
                TryDestroyImmediate(gutter);
                try { tmp.color = ink; } catch { /* */ }
                try { tmp.enableWordWrapping = wrapBefore; } catch { /* */ }
                return null;
            }
        }

        private static TextMeshProUGUI? MakeGutterLabel(GameObject gutter, string name, TextMeshProUGUI like, HorizontalAlignmentOptions align)
        {
            var go = new GameObject(name);
            go.transform.SetParent(gutter.transform, false);
            Stretch(go);
            var label = TryAdd<TextMeshProUGUI>(go);
            if (label == null) return null;
            CopyTextLook(like, label);
            try { label.richText = false; } catch { /* */ }
            try { label.parseCtrlCharacters = false; } catch { /* */ }
            try { label.enableWordWrapping = false; } catch { /* */ }
            try { label.horizontalAlignment = align; } catch { /* */ }
            try
            {
                // The text's own top and bottom margins, so the numbers sit on the text's lines.
                var m = like.margin;
                label.margin = new Vector4(0f, m.y, 0f, m.w);
            }
            catch { /* */ }
            return label;
        }

        /// <summary>The field's font, size, spacing, style, margins and alignment, so the layouts agree line for line.</summary>
        private static void CopyTextLook(TextMeshProUGUI from, TextMeshProUGUI to)
        {
            try { to.font = from.font; } catch { /* */ }
            try { to.fontSharedMaterial = from.fontSharedMaterial; } catch { /* */ }
            try { to.fontSize = from.fontSize; } catch { /* */ }
            try { to.enableAutoSizing = false; } catch { /* */ }
            try { to.fontStyle = from.fontStyle; } catch { /* */ }
            try { to.characterSpacing = from.characterSpacing; } catch { /* */ }
            try { to.wordSpacing = from.wordSpacing; } catch { /* */ }
            try { to.lineSpacing = from.lineSpacing; } catch { /* */ }
            try { to.paragraphSpacing = from.paragraphSpacing; } catch { /* */ }
            try { to.margin = from.margin; } catch { /* */ }
            try { to.horizontalAlignment = from.horizontalAlignment; } catch { /* */ }
            try { to.verticalAlignment = from.verticalAlignment; } catch { /* */ }
            try { to.overflowMode = TextOverflowModes.Overflow; } catch { /* */ }
            try { to.raycastTarget = false; } catch { /* */ }
            try { to.maskable = true; } catch { /* */ }
        }

        private static string ChooseMarkerGlyph(TextMeshProUGUI label)
        {
            var glyphs = UiCodeAreaModel.MarkerGlyphs;
            try
            {
                var font = label.font;
                if (font == null) return glyphs[glyphs.Length - 1];
                foreach (var g in glyphs)
                    if (g.Length == 1 && (g[0] <= 0x7F || font.HasCharacter(g[0], true, false))) return g;
            }
            catch { /* */ }
            return glyphs[glyphs.Length - 1];
        }

        public bool Alive
        {
            get
            {
                if (_disposed) return false;
                try { return UnityUiRuntime.Alive(_field) && UnityUiRuntime.Alive(_tmp) && UnityUiRuntime.Alive(_gutter) && UnityUiRuntime.Alive(_overlay); }
                catch { return false; }
            }
        }

        internal bool WantsPointer
        {
            get
            {
                if (_errorRows.Count == 0 && _hoverLine == 0) return false;
                try { return _gutter.activeInHierarchy || _hoverLine != 0; }
                catch { return false; }
            }
        }

        public void Render(CodeAreaFrame frame)
        {
            if (_disposed || frame == null) return;
            try { if (_overlay.text != frame.Markup) _overlay.text = frame.Markup; } catch { /* */ }
            if (frame.LineCount != _lineCount)
            {
                _lineCount = frame.LineCount;
                try { _numbers.text = UiCodeAreaModel.GutterText(_lineCount); } catch { /* */ }
                PlaceGutter(UiCodeAreaModel.GutterWidth(_lineCount, _digitAdvance));
            }
            _errors = frame.Errors;
            try { _markers.text = UiCodeAreaModel.MarkerText(_lineCount, _errors, _glyph); } catch { /* */ }
            _layoutDirty = true;
        }

        /// <summary>Size the gutter and narrow the text viewport by its width.</summary>
        private void PlaceGutter(float width)
        {
            if (Math.Abs(width - _gutterWidth) < 0.5f) return;
            _gutterWidth = width;
            try
            {
                var vMin = _viewport.offsetMin;
                var vMax = _viewport.offsetMax;
                _viewport.offsetMin = new Vector2(_baseViewportLeft + width, vMin.y);
                _gutterRt.offsetMin = new Vector2(_baseViewportLeft, vMin.y);
                _gutterRt.offsetMax = new Vector2(_baseViewportLeft + width, vMax.y);
            }
            catch { /* */ }
            SetColumn(_markers.rectTransform, 0f, UiCodeAreaModel.MarkerWidth);
            SetColumn(_numbers.rectTransform, UiCodeAreaModel.MarkerWidth, width - UiCodeAreaModel.NumberGap - UiCodeAreaModel.TextGap);
            if (_separator != null)
            {
                float x = UiCodeAreaModel.SeparatorX(width);
                try
                {
                    _separator.anchorMin = new Vector2(0f, 0f);
                    _separator.anchorMax = new Vector2(0f, 1f);
                    _separator.pivot = new Vector2(0.5f, 0.5f);
                    _separator.offsetMin = new Vector2(x - 0.5f, 2f);
                    _separator.offsetMax = new Vector2(x + 0.5f, -2f);
                }
                catch { /* */ }
            }
            _lastTextY = new Vector4(float.NaN, 0f, 0f, 0f);
            MarkEditorBarDirty(FindArmed(_field));
        }

        private static void SetColumn(RectTransform rt, float left, float right)
        {
            try
            {
                var a = rt.anchorMin;
                var b = rt.anchorMax;
                rt.anchorMin = new Vector2(0f, a.y);
                rt.anchorMax = new Vector2(0f, b.y);
                var oMin = rt.offsetMin;
                var oMax = rt.offsetMax;
                rt.offsetMin = new Vector2(left, oMin.y);
                rt.offsetMax = new Vector2(Math.Max(left + 1f, right), oMax.y);
            }
            catch { /* */ }
        }

        /// <summary>Per frame: keep the field's glyphs hidden and wrap off, follow the text's scroll, lay out the bands.</summary>
        internal void Tick()
        {
            if (_disposed) return;
            Color c = _tmp.color;
            if (c.a > 0.001f)
            {
                // Something repainted the field's text (graying): that is the overlay's base color now.
                _ink = c;
                try { _overlay.color = c; } catch { /* */ }
                try { _tmp.color = new Color(c.r, c.g, c.b, 0f); } catch { /* */ }
            }
            if (_tmp.enableWordWrapping)
            {
                try { _tmp.enableWordWrapping = false; } catch { /* */ }
                _layoutDirty = true;
            }

            var trt = _tmp.rectTransform;
            var aMin = trt.anchorMin;
            var aMax = trt.anchorMax;
            var oMin = trt.offsetMin;
            var oMax = trt.offsetMax;
            var now = new Vector4(aMin.y, aMax.y, oMin.y, oMax.y);
            if (now != _lastTextY)
            {
                _lastTextY = now;
                FollowText(_numbers.rectTransform, now);
                FollowText(_markers.rectTransform, now);
            }

            if (_layoutDirty)
            {
                _layoutDirty = false;
                LayOutBands();
            }
        }

        private static void FollowText(RectTransform rt, Vector4 y)
        {
            var aMin = rt.anchorMin;
            var aMax = rt.anchorMax;
            var oMin = rt.offsetMin;
            var oMax = rt.offsetMax;
            rt.anchorMin = new Vector2(aMin.x, y.x);
            rt.anchorMax = new Vector2(aMax.x, y.y);
            rt.offsetMin = new Vector2(oMin.x, y.z);
            rt.offsetMax = new Vector2(oMax.x, y.w);
        }

        /// <summary>Place a faint band behind each error line, from the overlay's laid-out lines (the model's pitch past them).</summary>
        private void LayOutBands()
        {
            _errorRows.Clear();
            float firstTop, pitch = UiTextExpandLayout.LinePitch;
            var trt = _tmp.rectTransform;
            Rect rect = trt.rect;
            // Each laid-out line's ascender, descender and baseline (the text's own space), read once.
            var asc = new List<float>();
            var desc = new List<float>();
            var bases = new List<float>();
            float firstHeight = 0f;
            try
            {
                _overlay.ForceMeshUpdate(false, false);
                var info = _overlay.textInfo;
                int count = info != null ? info.lineCount : 0;
                var lineInfo = count > 0 ? info!.lineInfo : null;
                if (lineInfo != null && count <= lineInfo.Length)
                {
                    for (int i = 0; i < count; i++)
                    {
                        var li = lineInfo[i];
                        asc.Add(li.ascender);
                        desc.Add(li.descender);
                        bases.Add(li.baseline);
                        if (i == 0) firstHeight = li.lineHeight;
                    }
                }
            }
            catch { asc.Clear(); desc.Clear(); bases.Clear(); }
            int laid = asc.Count;
            _overlayLines = laid;

            Vector4 margin;
            try { margin = _tmp.margin; } catch { margin = Vector4.zero; }
            if (laid > 0)
            {
                firstTop = asc[0];
                if (laid >= 2) pitch = Math.Max(1f, bases[0] - bases[1]);
                else if (firstHeight > 1f) pitch = firstHeight;
            }
            else firstTop = rect.yMax - margin.y;

            int used = 0;
            foreach (var e in _errors)
            {
                float top, bottom;
                if (e.Line <= laid)
                {
                    top = asc[e.Line - 1];
                    bottom = desc[e.Line - 1];
                    if (top - bottom < 1f) bottom = top - pitch;
                }
                else
                {
                    top = UiCodeAreaModel.LineTop(e.Line, firstTop, pitch);
                    bottom = top - pitch;
                }
                _errorRows.Add((e.Line, top, bottom, e.Message));
                var band = BandAt(used++);
                if (band == null) continue;
                try
                {
                    var rt = band.rectTransform;
                    rt.anchorMin = Vector2.zero;
                    rt.anchorMax = Vector2.zero;
                    rt.pivot = new Vector2(0.5f, 0.5f);
                    rt.anchoredPosition = new Vector2(rect.center.x - rect.xMin, (top + bottom) * 0.5f - rect.yMin);
                    rt.sizeDelta = new Vector2(rect.width + 4000f, Math.Max(1f, top - bottom));
                    if (!band.gameObject.activeSelf) band.gameObject.SetActive(true);
                }
                catch { /* */ }
            }

            for (int i = used; i < _bands.Count; i++)
            {
                try { if (_bands[i].gameObject.activeSelf) _bands[i].gameObject.SetActive(false); } catch { /* */ }
            }
        }

        private Image? BandAt(int index)
        {
            while (_bands.Count <= index)
            {
                var go = new GameObject("code-error-band");
                go.transform.SetParent(_tmp.transform, false);
                // Behind the overlay (the text component's first child is drawn first).
                try { go.transform.SetAsFirstSibling(); } catch { /* */ }
                var image = TryAdd<Image>(go);
                if (image == null)
                {
                    TryDestroyImmediate(go);
                    return null;
                }
                try { image.raycastTarget = false; } catch { /* */ }
                try { image.maskable = true; } catch { /* */ }
                var r = MenuPalette.Red;
                try { image.color = new Color(r.R, r.G, r.B, UiCodeAreaModel.BandAlpha); } catch { /* */ }
                _bands.Add(image);
            }

            return _bands[index];
        }

        /// <summary>The pointer over an error line's gutter shows its message in the kit's tooltip.</summary>
        internal void Hover(bool inside, float x, float y)
        {
            int line = 0;
            string message = "";
            ScreenRect anchor = ScreenRect.Unknown;
            if (inside && _errorRows.Count > 0)
            {
                try
                {
                    if (_gutter.activeInHierarchy)
                    {
                        var gutter = ScreenRectOf(_gutterRt);
                        if (gutter.IsKnown && gutter.Contains(x, y))
                        {
                            var trt = _tmp.rectTransform;
                            foreach (var row in _errorRows)
                            {
                                Vector2 top = RectTransformUtility.WorldToScreenPoint(null, trt.TransformPoint(new Vector3(0f, row.Top, 0f)));
                                Vector2 bottom = RectTransformUtility.WorldToScreenPoint(null, trt.TransformPoint(new Vector3(0f, row.Bottom, 0f)));
                                var band = ScreenRect.FromCorners(gutter.MinX, bottom.y, gutter.MaxX, top.y);
                                if (!band.Contains(x, y)) continue;
                                line = row.Line;
                                message = row.Message;
                                anchor = band;
                                break;
                            }
                        }
                    }
                }
                catch { line = 0; }
            }

            if (line == _hoverLine) return;
            var widgets = WidgetRuntime.Current;
            if (line != 0)
            {
                try { widgets?.ShowTooltip(anchor, UiCodeAreaModel.TooltipTitle(line), message); }
                catch (Exception e) { Plugin.Logger?.LogWarning("Ui: code area tooltip threw: " + e.Message); }
            }
            else
            {
                try { widgets?.HideTooltip(); } catch { /* */ }
            }
            _hoverLine = line;
        }

        public Dictionary<string, object?> Describe()
        {
            var d = new Dictionary<string, object?>
            {
                ["gutterWidth"] = (float)Math.Round(_gutterWidth, 1),
                ["digitAdvance"] = (float)Math.Round(_digitAdvance, 2),
                ["lineCount"] = _lineCount,
                ["overlayLines"] = _overlayLines,
                ["markerGlyph"] = _glyph,
                ["hoverLine"] = _hoverLine,
                ["bands"] = CountActiveBands(),
                ["art"] = "settings value box (SimpleBox_Normal / SimpleBox_Hover), ScrollBar_Bar / ScrollBar_Handle",
            };
            try { d["wrap"] = _tmp.enableWordWrapping; } catch { /* */ }
            try { d["inkHidden"] = _tmp.color.a < 0.01f; } catch { /* */ }
            try { d["overlayChars"] = (_overlay.text ?? "").Length; } catch { /* */ }
            try { d["viewportLeft"] = (float)Math.Round(_viewport.offsetMin.x, 1); } catch { /* */ }
            try { d["font"] = _overlay.font != null ? _overlay.font.name : ""; } catch { /* */ }
            try
            {
                var gutter = ScreenRectOf(_gutterRt);
                if (gutter.IsKnown)
                    d["gutterRect"] = new Dictionary<string, object?>
                    {
                        ["x"] = (float)Math.Round(gutter.MinX, 1), ["y"] = (float)Math.Round(gutter.MinY, 1),
                        ["w"] = (float)Math.Round(gutter.MaxX - gutter.MinX, 1), ["h"] = (float)Math.Round(gutter.MaxY - gutter.MinY, 1),
                    };
            }
            catch { /* */ }
            var rows = new List<Dictionary<string, object?>>(_errorRows.Count);
            foreach (var r in _errorRows)
                rows.Add(new Dictionary<string, object?> { ["line"] = r.Line, ["top"] = (float)Math.Round(r.Top, 1), ["bottom"] = (float)Math.Round(r.Bottom, 1) });
            d["errorRows"] = rows;
            d["textView"] = DescribeTextView(_field);
            return d;
        }

        private int CountActiveBands()
        {
            int n = 0;
            foreach (var b in _bands)
            {
                try { if (b != null && b.gameObject.activeSelf) n++; } catch { /* */ }
            }
            return n;
        }

        /// <summary>The tick found the field gone (the row was released): nothing to restore, only the tooltip.</summary>
        internal void Forget()
        {
            if (_disposed) return;
            _disposed = true;
            if (_hoverLine != 0)
            {
                try { WidgetRuntime.Current?.HideTooltip(); } catch { /* */ }
                _hoverLine = 0;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            bool fieldAlive = false;
            try { fieldAlive = UnityUiRuntime.Alive(_field) && UnityUiRuntime.Alive(_tmp); } catch { /* */ }
            Forget();
            CodeAreas.Remove(this);
            foreach (var b in _bands)
            {
                try { if (b != null) TryDestroyImmediate(b.gameObject); } catch { /* */ }
            }
            _bands.Clear();
            try { TryDestroyImmediate(_overlay != null ? _overlay.gameObject : null); } catch { /* */ }
            TryDestroyImmediate(_gutter);
            if (!fieldAlive) return;
            // The field goes back to a plain multi-line field.
            try
            {
                var vMin = _viewport.offsetMin;
                _viewport.offsetMin = new Vector2(_baseViewportLeft, vMin.y);
            }
            catch { /* */ }
            try { _tmp.color = new Color(_ink.r, _ink.g, _ink.b, _ink.a < 0.01f ? 1f : _ink.a); } catch { /* */ }
            try { _tmp.enableWordWrapping = _wrapBefore; } catch { /* */ }
            try { _tmp.parseCtrlCharacters = _ctrlBefore; } catch { /* */ }
        }
    }
}
#endif
