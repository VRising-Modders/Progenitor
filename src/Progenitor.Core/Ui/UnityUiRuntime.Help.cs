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
        internal TermMarkerView(TextMeshProUGUI text, GameObject marker, RectTransform rt)
        {
            Text = text;
            Marker = marker;
            Rt = rt;
        }

        internal readonly TextMeshProUGUI Text;
        internal readonly GameObject Marker;
        internal readonly RectTransform Rt;
        private bool _shown = true;
        internal bool Disposed;

        public bool Shown
        {
            get => _shown;
            set
            {
                _shown = value;
                try { if (Alive(Marker) && Marker.activeSelf != value) Marker.SetActive(value); }
                catch { /* */ }
            }
        }

        public void Dispose()
        {
            if (Disposed) return;
            Disposed = true;
            TermMarkers.Remove(this);
            if (Alive(Marker)) TryDestroyImmediate(Marker);
        }
    }

    public IUiTermMarkerView? AttachTermMarker(IRuntimeWidget widget, string? cellKey)
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
            try
            {
                var gold = ToColor(MenuPalette.TextColor(CellTone.Gold));
                image.color = new Color(gold.r * 0.85f, gold.g * 0.85f, gold.b * 0.85f, 1f);
            }
            catch { /* */ }

            var view = new TermMarkerView(text, marker, rt);
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

            if (view.Shown && view.Marker.activeInHierarchy) PlaceTermMarker(view);
        }

        if (TermMarkers.Count == 0)
        {
            var tick = _termTick;
            _termTick = null;
            try { tick?.Dispose(); }
            catch { /* */ }
        }
    }

    /// <summary>Size the marker from the text's font size and put it after the text's last character.</summary>
    private static void PlaceTermMarker(TermMarkerView view)
    {
        try
        {
            var tmp = view.Text;
            float size = UiTermMarkerArt.SizeFor(tmp.fontSize);
            Rect rect = tmp.rectTransform.rect;
            Bounds bounds = tmp.textBounds;
            bool known = bounds.size.x > 0f && !float.IsNaN(bounds.max.x);
            float x = UiTermMarkerArt.CenterX(known ? bounds.max.x : float.NaN, rect.xMin, rect.xMax, size);
            float y = known ? bounds.center.y : rect.center.y;
            var want = new Vector2(x - rect.center.x, y - rect.center.y);
            var rt = view.Rt;
            if (Math.Abs(rt.sizeDelta.x - size) > 0.1f) rt.sizeDelta = new Vector2(size, size);
            if ((rt.anchoredPosition - want).sqrMagnitude > 0.01f) rt.anchoredPosition = want;
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

    // ---- Tip card placement ------------------------------------------------------------------------

    public bool PlaceTipCard(IRuntimePanel card, ScreenRect anchor)
    {
        if (card is not UnityPanel host || !host.Alive) return false;
        try
        {
            var rt = host.GameObject.GetComponent<RectTransform>();
            var parent = rt == null ? null : rt.parent as RectTransform;
            if (rt == null || parent == null) return false;
            Rect pr = parent.rect;
            if (pr.width <= 0f || pr.height <= 0f) return false;
            Vector2 size = rt.rect.size;
            if (size.x <= 0f || size.y <= 0f) return false;

            bool hasAnchor = false;
            Vector2 a = default, b = default;
            if (anchor.IsKnown && ToCanvas(parent, anchor.MinX, anchor.MinY, out a) && ToCanvas(parent, anchor.MaxX, anchor.MaxY, out b))
                hasAnchor = true;
            var place = UiTipLayout.Place(hasAnchor, a.x, a.y, b.x, b.y, size.x, size.y, pr.width, pr.height);

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
