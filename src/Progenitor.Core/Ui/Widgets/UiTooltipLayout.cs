using System;

namespace Progenitor.Ui;

/// <summary>
/// P7: where the tooltip box goes and how big it is, in the tooltip canvas's units (origin bottom-left, the game's
/// design space). Game-free and tested (<c>UiWidgetLayoutTests</c>, <c>UiTermsTests</c>).
///
/// <para>Rules: the box is the dropdown description box's look (the game's dark frame, <see cref="Pad"/> inside), at
/// most <see cref="MaxWidth"/> wide, its body wrapped to that. It stays inside the area it is given (P7b: the panel the
/// hovered control is on, so a tooltip on the Mods workspace's page never flips over the Mods list beside it), else the
/// canvas, always <see cref="Margin"/> inside. A wide anchor (a list row, at least half the area's width) gets the box
/// under it, else over it, so the box never covers the row it describes and never jumps sideways out of its panel. A
/// narrow anchor gets the box <see cref="Gap"/> to its right, top edges aligned; to its left when the right side has no
/// room; under or over it when neither side has. Only when nothing fits does the box take the roomier side, clamped.
/// An area too small for the box (a small floating window) is not used on that axis: the canvas is.</para>
/// </summary>
internal static class UiTooltipLayout
{
    internal const float MaxWidth = 340f;
    internal const float MinWidth = 120f;
    internal const float Pad = 12f;
    internal const float Gap = 8f;
    internal const float Margin = 8f;
    internal const float TitleFontSize = 17f;
    internal const float BodyFontSize = 15f;
    internal const float TitleGap = 4f;

    /// <summary>An anchor at least this share of its area's width is a wide one (a list row): under or over it, never beside.</summary>
    internal const float WideShare = 0.5f;

    /// <summary>Which rule placed the box (tests).</summary>
    internal enum Side
    {
        Right,
        Left,
        Below,
        Above,
        Clamped,
    }

    /// <summary>The box size for measured text: widths and heights are the text's own preferred sizes.</summary>
    internal static (float Width, float Height) Size(float titleWidth, float titleHeight, float bodyWidth, float bodyHeight)
    {
        float inner = Math.Max(Finite(titleWidth), Finite(bodyWidth));
        float width = Math.Min(MaxWidth, Math.Max(MinWidth, inner + 2f * Pad));
        float h = Finite(titleHeight) + Finite(bodyHeight);
        if (titleHeight > 0f && bodyHeight > 0f) h += TitleGap;
        return (width, Math.Max(h + 2f * Pad, 2f * Pad + BodyFontSize));
    }

    /// <summary>
    /// The box's bottom-left corner for an anchor rect (<paramref name="minX"/>..<paramref name="maxY"/>) on a canvas
    /// <paramref name="canvasWidth"/> by <paramref name="canvasHeight"/>, with no area of its own (the whole canvas).
    /// </summary>
    internal static (float X, float Y) Place(
        float minX, float minY, float maxX, float maxY, float width, float height, float canvasWidth, float canvasHeight)
    {
        var p = PlaceIn(minX, minY, maxX, maxY, width, height, canvasWidth, canvasHeight, float.NaN, float.NaN, float.NaN, float.NaN);
        return (p.X, p.Y);
    }

    /// <summary>
    /// As <see cref="Place"/>, kept inside the area <paramref name="areaMinX"/>..<paramref name="areaMaxY"/> (the hovered
    /// control's panel; NaN for none) on each axis where the box fits in it.
    /// <paramref name="beside"/> false keeps it off the anchor's sides: under it, else over it (a title-row button's box
    /// never covers the title beside it).
    /// </summary>
    internal static (float X, float Y, Side Side) PlaceIn(
        float minX, float minY, float maxX, float maxY, float width, float height, float canvasWidth, float canvasHeight,
        float areaMinX, float areaMinY, float areaMaxX, float areaMaxY, bool beside = true)
    {
        width = Finite(width);
        height = Finite(height);
        float l = Margin, r = canvasWidth - Margin, b = Margin, t = canvasHeight - Margin;
        if (!float.IsNaN(areaMinX) && !float.IsNaN(areaMaxX) && !float.IsNaN(areaMinY) && !float.IsNaN(areaMaxY))
        {
            float al = Math.Max(l, areaMinX + Margin), ar = Math.Min(r, areaMaxX - Margin);
            float ab = Math.Max(b, areaMinY + Margin), at = Math.Min(t, areaMaxY - Margin);
            if (ar - al >= width) { l = al; r = ar; }
            if (at - ab >= height) { b = ab; t = at; }
        }

        float xMax = r - width, yMax = t - height;
        bool wide = maxX - minX >= WideShare * (r - l);
        if (!wide && beside)
        {
            // Beside the anchor, top edges aligned: it never covers the anchor, so only the vertical clamp applies.
            float right = maxX + Gap;
            if (right <= xMax) return (right, Clamp(maxY - height, b, yMax), Side.Right);
            float left = minX - Gap - width;
            if (left >= l) return (left, Clamp(maxY - height, b, yMax), Side.Left);
        }

        // Under it, else over it, starting at its left edge; only when neither fits does the box take the roomier side.
        float x = Clamp(minX, l, xMax);
        float below = minY - Gap - height;
        if (below >= b) return (x, below, Side.Below);
        float above = maxY + Gap;
        if (above <= yMax) return (x, above, Side.Above);
        bool roomBelow = minY - b >= t - maxY;
        return (x, Clamp(roomBelow ? below : above, b, yMax), Side.Clamped);
    }

    /// <summary>
    /// The area a tooltip stays in: the smallest of <paramref name="panels"/> (screen rects of the shown panels) that holds
    /// the anchor's center, so a control on the Mods workspace's page keeps its tooltip on that page, not over the list
    /// beside it. Unknown when none holds it (the canvas is used).
    /// </summary>
    internal static ScreenRect AreaFor(ScreenRect anchor, System.Collections.Generic.IEnumerable<ScreenRect> panels)
    {
        if (!anchor.IsKnown || panels == null) return ScreenRect.Unknown;
        float cx = (anchor.MinX + anchor.MaxX) / 2f, cy = (anchor.MinY + anchor.MaxY) / 2f;
        var best = ScreenRect.Unknown;
        float bestArea = float.MaxValue;
        foreach (var r in panels)
        {
            if (!r.IsKnown || cx < r.MinX || cx > r.MaxX || cy < r.MinY || cy > r.MaxY) continue;
            float area = (r.MaxX - r.MinX) * (r.MaxY - r.MinY);
            if (area < bestArea)
            {
                bestArea = area;
                best = r;
            }
        }

        return best;
    }

    private static float Clamp(float v, float lo, float hi) => hi < lo ? lo : v < lo ? lo : v > hi ? hi : v;

    private static float Finite(float v) => float.IsNaN(v) || float.IsInfinity(v) || v < 0f ? 0f : v;
}
