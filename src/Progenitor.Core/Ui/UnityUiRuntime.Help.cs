#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Progenitor.Ui;

/// <summary>
/// P7b: the Unity half of the help widgets (<see cref="IUiHelpRuntime"/>): the term marker drawn after a control's text,
/// and the tip card's placement beside the panel it explains.
///
/// <para><b>Marker.</b> A circle-i drawn once per Core generation into a texture (<see cref="UiTermMarkerArt.Alpha"/>, as
/// the Locate marker is), shown as an <c>Image</c> child of the text it follows, tinted the muted gold of the widgets'
/// glyph buttons, never a raycast target (the hover belongs to the control). It is a child of the text, so it is clipped,
/// scaled, hidden and destroyed with it. One shared frame tick re-places every live marker after its text's last
/// character (<c>TMP_Text.textBounds</c>, local units), so a caption rewritten in place keeps its marker at its end; it
/// reads only the cached objects (no scene search) and stops when no marker is left.</para>
/// </summary>
internal sealed partial class UnityUiRuntime : IUiHelpRuntime
{
    internal const string TermMarkerName = UiOwnership.Prefix + "term-marker";

    private static Sprite? _termSprite;
    private static Texture2D? _termTexture;
    private static readonly List<TermMarkerView> TermMarkers = new();
    private static IDisposable? _termTick;
    private static bool _termTeardown;

    private sealed class TermMarkerView : IUiTermMarkerView
    {
        internal TermMarkerView(TextMeshProUGUI text, GameObject marker, RectTransform rt, bool reserveRoom, Image image)
        {
            Text = text;
            Marker = marker;
            Rt = rt;
            ReserveRoom = reserveRoom;
            Image = image;
        }

        internal readonly Image Image;

        public Func<bool>? OnPlate { get; set; }

        /// <summary>The plate state last painted (null before the first paint).</summary>
        internal bool? PaintedOnPlate;

        internal readonly TextMeshProUGUI Text;
        internal readonly GameObject Marker;
        internal readonly RectTransform Rt;

        /// <summary>The marker keeps its room in the text's right margin (see <see cref="IUiHelpRuntime.AttachTermMarker"/>).</summary>
        internal readonly bool ReserveRoom;

        /// <summary>The text's margin without the marker's room (re-read when the kit repaints the text).</summary>
        internal Vector4 BaseMargin;

        /// <summary>The margin this view last wrote, or null when it has not written one.</summary>
        internal Vector4? Written;

        private bool _shown = true;
        internal bool Disposed;

        public bool Shown
        {
            get => _shown;
            set
            {
                if (_shown == value) return;
                _shown = value;
                PlaceTermMarker(this);
            }
        }

        public void Dispose()
        {
            if (Disposed) return;
            Disposed = true;
            TermMarkers.Remove(this);
            // Give the caption its full width back.
            try { if (Written is { } w && Alive(Text) && Text.margin == w) Text.margin = BaseMargin; } catch { /* */ }
            if (Alive(Marker)) TryDestroyImmediate(Marker);
        }
    }

    public IUiTermMarkerView? AttachTermMarker(IRuntimeWidget widget, string? cellKey, bool reserveRoom = true)
    {
        try
        {
            TextMeshProUGUI? text = null;
            if (cellKey != null && widget is UnityCellRow row) text = row.CellText(cellKey);
            else
            {
                var go = NavObject(widget);
                if (go != null) text = go.GetComponentInChildren<TextMeshProUGUI>(true);
            }

            if (text == null || !Alive(text)) return null;
            var sprite = TermSprite();
            if (sprite == null) return null;

            var old = text.transform.Find(TermMarkerName);
            if (old != null) TryDestroyImmediate(old.gameObject);
            var marker = new GameObject(TermMarkerName);
            marker.transform.SetParent(text.transform, false);
            var rt = marker.GetComponent<RectTransform>() ?? marker.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            var image = TryAdd<Image>(marker);
            if (image == null)
            {
                TryDestroyImmediate(marker);
                return null;
            }

            try { image.sprite = sprite; } catch { /* */ }
            try { image.type = Image.Type.Simple; } catch { /* */ }
            try { image.preserveAspect = true; } catch { /* */ }
            try { image.raycastTarget = false; } catch { /* */ }
            try { image.color = ToColor(UiTermMarkerArt.Normal); }
            catch { /* */ }

            var view = new TermMarkerView(text, marker, rt, reserveRoom, image);
            try { view.BaseMargin = text.margin; } catch { view.BaseMargin = Vector4.zero; }
            PlaceTermMarker(view);
            TermMarkers.Add(view);
            if (!_termTeardown)
            {
                _termTeardown = true;
                UiWidgetLifetime.Register("help-markers", TeardownTermMarkers);
            }

            _termTick ??= EveryFrame(TickTermMarkers);
            return view;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: term marker could not be drawn: " + e.Message);
            return null;
        }
    }

    private static Sprite? TermSprite()
    {
        if (_termSprite != null && Alive(_termSprite)) return _termSprite;
        _termSprite = HudSprite("term-marker", UiTermMarkerArt.TextureSize, UiTermMarkerArt.TextureSize, UiTermMarkerArt.Alpha, out _termTexture);
        return _termSprite;
    }

    private static void TickTermMarkers()
    {
        for (int i = TermMarkers.Count - 1; i >= 0; i--)
        {
            var view = TermMarkers[i];
            if (view.Disposed || !Alive(view.Marker) || !Alive(view.Text))
            {
                TermMarkers.RemoveAt(i);
                continue;
            }

            // The marker itself may be inactive (it did not fit last frame); its text's state decides.
            if (view.Shown && view.Text.gameObject.activeInHierarchy) PlaceTermMarker(view);
        }

        if (TermMarkers.Count == 0)
        {
            var tick = _termTick;
            _termTick = null;
            try { tick?.Dispose(); }
            catch { /* */ }
        }
    }

    /// <summary>
    /// Keep the marker's room in the text's right margin (when it reserves one and no drawn sort mark owns that margin),
    /// size the marker from the text's font size, and put it after the text's last character; hide it when it is not
    /// shown or does not fit inside the text's rect. It is never placed on the text.
    /// </summary>
    private static void PlaceTermMarker(TermMarkerView view)
    {
        try
        {
            if (view.Disposed || !Alive(view.Text) || !Alive(view.Marker)) return;
            var tmp = view.Text;
            float size = UiTermMarkerArt.SizeFor(tmp.fontSize);

            // A drawn mark beside the same text (the data table's sort arrow) owns the margin and keeps this marker's
            // room itself (UnityUiRuntime.Marks.cs, TermMarkerRoom).
            bool owned = view.ReserveRoom && tmp.transform.Find(DrawnMarkName) == null;
            var current = tmp.margin;
            // The kit repainted the text (a cell's paint writes its own margin): that is the new base.
            if (view.Written is not { } written || current != written) view.BaseMargin = current;
            if (owned)
            {
                var want = view.BaseMargin;
                want.z = UiTermMarkerArt.RightMargin(want.z, tmp.fontSize, view.Shown);
                if (current != want)
                {
                    tmp.margin = want;
                    try { tmp.ForceMeshUpdate(); } catch { /* the bounds follow next frame */ }
                }

                view.Written = want;
            }
            else
            {
                if (view.Written is { } mine && current == mine) tmp.margin = view.BaseMargin;
                view.Written = null;
            }

            // P7b: dark on a selected chip's gold plate, the muted gold elsewhere; follows the selection.
            bool plate = false;
            try { plate = view.OnPlate?.Invoke() ?? false; }
            catch { plate = false; }
            if (view.PaintedOnPlate != plate)
            {
                view.PaintedOnPlate = plate;
                try { view.Image.color = ToColor(UiTermMarkerArt.ColorOn(plate)); } catch { /* */ }
            }

            bool visible = false;
            if (view.Shown)
            {
                Rect rect = tmp.rectTransform.rect;
                Bounds bounds = tmp.textBounds;
                bool known = bounds.size.x > 0f && !float.IsNaN(bounds.max.x);
                float x = UiTermMarkerArt.CenterAfter(known ? bounds.max.x : float.NaN, rect.xMax, size);
                if (!float.IsNaN(x))
                {
                    visible = true;
                    var at = new Vector2(x - rect.center.x, bounds.center.y - rect.center.y);
                    var rt = view.Rt;
                    if (Math.Abs(rt.sizeDelta.x - size) > 0.1f) rt.sizeDelta = new Vector2(size, size);
                    if ((rt.anchoredPosition - at).sqrMagnitude > 0.01f) rt.anchoredPosition = at;
                }
            }

            if (view.Marker.activeSelf != visible) view.Marker.SetActive(visible);
        }
        catch { /* the marker stays where it was */ }
    }

    private static void TeardownTermMarkers()
    {
        foreach (var view in TermMarkers.ToArray())
        {
            try { view.Dispose(); }
            catch { /* */ }
        }

        TermMarkers.Clear();
        var tick = _termTick;
        _termTick = null;
        try { tick?.Dispose(); }
        catch { /* */ }
        FreeHudAsset(_termSprite);
        FreeHudAsset(_termTexture);
        _termSprite = null;
        _termTexture = null;
        _termTeardown = false;
    }

    /// <summary>
    /// A caller's sprite handle as a <c>Sprite</c>. C# <c>as</c> answers only for a wrapper already typed Sprite; a
    /// handle typed as a base class (a <c>UnityEngine.Object</c> from an asset lookup) needs the IL2CPP cast.
    /// </summary>
    internal static Sprite? AsSprite(object? handle)
    {
        if (handle == null) return null;
        if (handle is Sprite sprite) return sprite;
        try { return (handle as Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase)?.TryCast<Sprite>(); }
        catch { return null; }
    }

    // ---- Tip card placement ------------------------------------------------------------------------

    public void TintTipCard(IRuntimeWidget? title, IRuntimeCellRow? buttons, string goldCell)
    {
        var gold = ToColor(MenuPalette.ChipOnBorder);
        try
        {
            var go = title == null ? null : NavObject(title);
            var tmp = go == null ? null : go.GetComponent<TextMeshProUGUI>() ?? go.GetComponentInChildren<TextMeshProUGUI>(true);
            if (tmp != null && tmp.color != gold) tmp.color = gold;
        }
        catch { /* the dialog's gray stays */ }

        try
        {
            var caption = buttons is UnityCellRow row ? row.CellText(goldCell) : null;
            if (caption != null && caption.color != gold) caption.color = gold;
        }
        catch { /* the button's light caption stays */ }
    }

    public void PassWheel(IRuntimePanel card)
    {
        if (card is UnityPanel host && host.Alive) host.WheelPassThrough = true;
    }

    /// <summary>P7b: the panel lets the wheel through to the panel beneath it (the tip card).</summary>
    private sealed partial class UnityPanel
    {
        internal bool WheelPassThrough;
    }

    public ScreenRect TextRect(IRuntimeWidget widget)
    {
        try
        {
            var go = NavObject(widget);
            if (go == null || !go.activeInHierarchy) return ScreenRect.Unknown;
            var tmp = go.GetComponentInChildren<TextMeshProUGUI>(false);
            if (tmp == null || string.IsNullOrEmpty(tmp.text)) return ScreenRect.Unknown;
            Bounds bounds = tmp.textBounds;
            if (!(bounds.size.x > 0f) || !(bounds.size.y > 0f)) return ScreenRect.Unknown;
            var rt = tmp.rectTransform;
            var min = RectTransformUtility.WorldToScreenPoint(null, rt.TransformPoint(bounds.min));
            var max = RectTransformUtility.WorldToScreenPoint(null, rt.TransformPoint(bounds.max));
            return ScreenRect.FromCorners(Math.Min(min.x, max.x), Math.Min(min.y, max.y), Math.Max(min.x, max.x), Math.Max(min.y, max.y));
        }
        catch { return ScreenRect.Unknown; }
    }

    public bool PlaceTipCard(IRuntimePanel card, ScreenRect anchor, IReadOnlyList<ScreenRect> controls, IReadOnlyList<ScreenRect>? texts = null)
    {
        if (card is not UnityPanel host || !host.Alive) return false;
        try
        {
            var rt = host.GameObject.GetComponent<RectTransform>();
            // Not `rt.parent as RectTransform`: C# `as` tests the managed wrapper (a Transform), never the IL2CPP class.
            var parentTransform = rt == null ? null : rt.parent;
            var parent = parentTransform == null ? null : parentTransform.GetComponent<RectTransform>();
            if (rt == null || parent == null) return false;
            Rect pr = parent.rect;
            if (pr.width <= 0f || pr.height <= 0f) return false;
            Vector2 size = rt.rect.size;
            if (size.x <= 0f || size.y <= 0f) return false;

            bool hasAnchor = false;
            Vector2 a = default, b = default;
            if (anchor.IsKnown && ToCanvas(parent, anchor.MinX, anchor.MinY, out a) && ToCanvas(parent, anchor.MaxX, anchor.MaxY, out b))
                hasAnchor = true;
            // The anchor panel's controls, in the same canvas units, so the card covers none of them (UiTipLayout).
            var canvasControls = new List<ScreenRect>(controls?.Count ?? 0);
            if (controls != null)
                foreach (var c in controls)
                    if (c.IsKnown && ToCanvas(parent, c.MinX, c.MinY, out var cmin) && ToCanvas(parent, c.MaxX, c.MaxY, out var cmax))
                        canvasControls.Add(ScreenRect.FromCorners(cmin.x, cmin.y, cmax.x, cmax.y));
            var canvasTexts = new List<ScreenRect>(texts?.Count ?? 0);
            if (texts != null)
                foreach (var c in texts)
                    if (c.IsKnown && ToCanvas(parent, c.MinX, c.MinY, out var tmin) && ToCanvas(parent, c.MaxX, c.MaxY, out var tmax))
                        canvasTexts.Add(ScreenRect.FromCorners(tmin.x, tmin.y, tmax.x, tmax.y));
            var place = UiTipLayout.Place(hasAnchor, a.x, a.y, b.x, b.y, size.x, size.y, pr.width, pr.height, canvasControls, canvasTexts);

            // The card's bottom-left in the parent's bottom-left units -> its pivot -> its anchored position.
            float pivotX = pr.xMin + place.X + size.x * rt.pivot.x;
            float pivotY = pr.yMin + place.Y + size.y * rt.pivot.y;
            float refX = pr.xMin + pr.width * rt.anchorMin.x;
            float refY = pr.yMin + pr.height * rt.anchorMin.y;
            var want = new Vector2(pivotX - refX, pivotY - refY);
            if ((rt.anchoredPosition - want).sqrMagnitude > 0.25f) rt.anchoredPosition = want;
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogDiag("Ui: tip card placement threw: " + e.Message);
            return false;
        }
    }
}
#endif
