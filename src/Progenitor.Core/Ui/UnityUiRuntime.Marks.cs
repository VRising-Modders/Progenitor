#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Progenitor.Ui;

/// <summary>
/// P7b: the Unity half of the drawn marks (<see cref="IWidgetMarks"/>): the data table's sort arrows with their order
/// digit and its pending spinner, beside a cell's text.
///
/// <para>Each mark is a coverage texture drawn once per Core generation from <see cref="UiDrawnMarkArt.Alpha"/> (the
/// term marker's and the Locate marker's technique), one sprite per mark name, made the first time it is asked for. A
/// slot is an <c>Image</c> child of the cell's text, tinted, never a raycast target, so it is clipped, scaled, hidden and
/// destroyed with the text. One shared frame tick re-places every shown slot from the text's bounds (cached objects
/// only, no scene search) and stops when no slot is left. A mark after right-justified text widens the text's right
/// margin by its own width, so the caption moves left rather than under the mark. The <c>drawn-marks</c> teardown
/// destroys every slot and frees the textures at the Core unload.</para>
/// </summary>
internal sealed partial class UnityUiRuntime : IWidgetMarks
{
    internal const string DrawnMarkName = UiOwnership.Prefix + "drawn-mark";

    private static readonly Dictionary<string, (Sprite Sprite, Texture2D? Texture)> MarkSprites = new(StringComparer.Ordinal);
    private static readonly List<MarkView> MarkViews = new();
    private static IDisposable? _markTick;
    private static bool _markTeardown;

    private sealed class MarkView : IUiMarkView
    {
        internal MarkView(TextMeshProUGUI text, GameObject go, RectTransform rt, Image image, UiMarkSide side)
        {
            Text = text;
            Go = go;
            Rt = rt;
            Image = image;
            Side = side;
            try { BaseMargin = text.margin; } catch { BaseMargin = new Vector4(2f, 0f, 2f, 0f); }
        }

        internal readonly TextMeshProUGUI Text;
        internal readonly GameObject Go;
        internal readonly RectTransform Rt;
        internal readonly Image Image;
        internal readonly UiMarkSide Side;
        internal readonly Vector4 BaseMargin;
        internal UiDrawnMark Mark = UiDrawnMark.None;
        internal bool Disposed;

        /// <summary>The help widgets' term marker after the same text, once found (looked up again only every 30 frames).</summary>
        internal Transform? TermMarker;
        internal int TermLookFrame = int.MinValue;

        public void Set(UiDrawnMark mark, PaletteColor color)
        {
            if (Disposed) return;
            try
            {
                if (!mark.Equals(Mark))
                {
                    Mark = mark;
                    var sprite = mark.IsNone ? null : MarkSprite(mark);
                    Image.sprite = sprite;
                    if (Go.activeSelf != (sprite != null)) Go.SetActive(sprite != null);
                }

                var c = ToColor(color);
                if (Image.color != c) Image.color = c;
                Place(this);
            }
            catch { /* the mark keeps its last state */ }

            _markTick ??= StartMarkTick();
        }

        public void Dispose()
        {
            if (Disposed) return;
            Disposed = true;
            MarkViews.Remove(this);
            try { if (Alive(Text) && Text.margin != BaseMargin) Text.margin = BaseMargin; } catch { /* */ }
            if (Alive(Go)) TryDestroyImmediate(Go);
        }
    }

    public IUiMarkView? AttachMark(IRuntimeCellRow row, string cellKey, UiMarkSide side)
    {
        try
        {
            if (row is not UnityCellRow cells || cellKey == null) return null;
            var text = cells.CellText(cellKey);
            if (text == null || !Alive(text)) return null;
            var go = new GameObject(DrawnMarkName);
            go.transform.SetParent(text.transform, false);
            var rt = go.GetComponent<RectTransform>() ?? go.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            var image = TryAdd<Image>(go);
            if (image == null)
            {
                TryDestroyImmediate(go);
                return null;
            }

            try { image.type = Image.Type.Simple; } catch { /* */ }
            try { image.preserveAspect = true; } catch { /* */ }
            try { image.raycastTarget = false; } catch { /* */ }
            try { image.maskable = true; } catch { /* */ }
            go.SetActive(false);
            var view = new MarkView(text, go, rt, image, side);
            MarkViews.Add(view);
            if (!_markTeardown)
            {
                _markTeardown = true;
                UiWidgetLifetime.Register("drawn-marks", TeardownMarks);
            }

            return view;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: a drawn mark could not be attached: " + e.Message);
            return null;
        }
    }

    private static Sprite? MarkSprite(UiDrawnMark mark)
    {
        string name = mark.Name;
        if (MarkSprites.TryGetValue(name, out var made) && made.Sprite != null && Alive(made.Sprite)) return made.Sprite;
        var (w, h) = UiDrawnMarkArt.TextureSize(mark);
        if (w <= 0 || h <= 0) return null;
        var sprite = HudSprite("mark-" + name, w, h, (x, y) => UiDrawnMarkArt.Alpha(mark, x, y), out var texture);
        if (sprite == null) return null;
        MarkSprites[name] = (sprite, texture);
        return sprite;
    }

    private IDisposable? StartMarkTickInstance() => EveryFrame(TickMarks);

    private static IDisposable? StartMarkTick()
    {
        try { return (Ui.CurrentRuntime() as UnityUiRuntime)?.StartMarkTickInstance(); }
        catch { return null; }
    }

    private static void TickMarks()
    {
        bool any = false;
        for (int i = MarkViews.Count - 1; i >= 0; i--)
        {
            var view = MarkViews[i];
            if (view.Disposed || !Alive(view.Go) || !Alive(view.Text))
            {
                view.Disposed = true;
                MarkViews.RemoveAt(i);
                continue;
            }

            // Every slot keeps being placed while its text is on screen: a term marker beside it can appear or hide (its
            // margin follows). A closed or hidden panel's slots are skipped.
            any = true;
            bool onScreen;
            try { onScreen = view.Text.gameObject.activeInHierarchy; }
            catch { onScreen = false; }
            if (onScreen) Place(view);
        }

        if (!any)
        {
            var tick = _markTick;
            _markTick = null;
            try { tick?.Dispose(); }
            catch { /* */ }
        }
    }

    /// <summary>Size the mark from the text's font size and put it beside the text; keep its room after right-justified text.</summary>
    private static void Place(MarkView view)
    {
        try
        {
            var tmp = view.Text;
            bool rightJustified = (tmp.horizontalAlignment & HorizontalAlignmentOptions.Right) != 0;
            // A term marker (the help widgets' circle-i) after the same text comes first: caption, term marker, mark.
            float term = view.Side == UiMarkSide.After ? TermMarkerRoom(view) : 0f;
            var margin = view.BaseMargin;
            if (view.Mark.IsNone)
            {
                if (rightJustified) margin.z += term;
                if (tmp.margin != margin) tmp.margin = margin;
                return;
            }

            float height = UiDrawnMarkArt.Height(view.Mark, tmp.fontSize);
            float width = UiDrawnMarkArt.Width(view.Mark, tmp.fontSize);
            if (view.Side == UiMarkSide.After && rightJustified) margin.z += term + width + UiDrawnMarkArt.Gap;
            if (tmp.margin != margin) tmp.margin = margin;

            Rect rect = tmp.rectTransform.rect;
            Bounds bounds = tmp.textBounds;
            bool known = bounds.size.x > 0f && !float.IsNaN(bounds.max.x);
            float x = UiDrawnMarkArt.CenterX(view.Side, known ? bounds.min.x : float.NaN, known ? bounds.max.x + term : float.NaN,
                rect.xMin, rect.xMax, width);
            float y = known ? bounds.center.y : rect.center.y;
            var want = new Vector2(x - rect.center.x, y - rect.center.y);
            var rt = view.Rt;
            if (Math.Abs(rt.sizeDelta.x - width) > 0.1f || Math.Abs(rt.sizeDelta.y - height) > 0.1f) rt.sizeDelta = new Vector2(width, height);
            if ((rt.anchoredPosition - want).sqrMagnitude > 0.01f) rt.anchoredPosition = want;
        }
        catch { /* the mark stays where it was */ }
    }

    /// <summary>The room a shown term marker takes after <paramref name="tmp"/>'s text (its side and gap), else 0.</summary>
    private static float TermMarkerRoom(MarkView view)
    {
        try
        {
            var tmp = view.Text;
            var marker = view.TermMarker;
            if (marker == null || !Alive(marker))
            {
                // A marker can be attached after the mark (a term defined later); look again now and then, not every frame.
                int frame = Time.frameCount;
                if (frame - view.TermLookFrame < 30 && view.TermLookFrame != int.MinValue) return 0f;
                view.TermLookFrame = frame;
                marker = tmp.transform.Find(TermMarkerName);
                view.TermMarker = marker;
                if (marker == null) return 0f;
            }

            if (!marker.gameObject.activeSelf) return 0f;
            return UiTermMarkerArt.SizeFor(tmp.fontSize) + UiTermMarkerArt.Gap;
        }
        catch { return 0f; }
    }

    private static void TeardownMarks()
    {
        foreach (var view in MarkViews.ToArray())
        {
            try { view.Dispose(); }
            catch { /* */ }
        }

        MarkViews.Clear();
        var tick = _markTick;
        _markTick = null;
        try { tick?.Dispose(); }
        catch { /* */ }
        foreach (var made in MarkSprites.Values)
        {
            FreeHudAsset(made.Sprite);
            FreeHudAsset(made.Texture);
        }

        MarkSprites.Clear();
        _markTeardown = false;
    }
}
#endif
