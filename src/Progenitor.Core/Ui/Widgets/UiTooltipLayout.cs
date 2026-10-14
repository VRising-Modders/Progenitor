using System;

namespace Progenitor.Ui;

/// <summary>
/// P7: where the tooltip box goes and how big it is, in the tooltip canvas's units (origin bottom-left, the game's
/// design space). Game-free and tested (<c>UiWidgetLayoutTests</c>).
///
/// <para>Rules: the box is the dropdown description box's look (the game's dark frame, <see cref="Pad"/> inside), at
/// most <see cref="MaxWidth"/> wide, its body wrapped to that. It sits <see cref="Gap"/> right of the anchor, top edges
/// aligned; when that would leave the canvas it goes to the anchor's left; when neither side fits it goes under the
/// anchor. It is always clamped <see cref="Margin"/> inside the canvas, so it never draws off screen at any resolution.</para>
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
    /// <paramref name="canvasWidth"/> by <paramref name="canvasHeight"/>.
    /// </summary>
    internal static (float X, float Y) Place(
        float minX, float minY, float maxX, float maxY, float width, float height, float canvasWidth, float canvasHeight)
    {
        float x = maxX + Gap;
        float top = maxY;
        if (x + width > canvasWidth - Margin)
        {
            float left = minX - Gap - width;
            if (left >= Margin) x = left;
            else
            {
                // Neither side: under the anchor, starting at its left edge.
                x = minX;
                top = minY - Gap;
            }
        }

        float y = top - height;
        x = Clamp(x, Margin, canvasWidth - Margin - width);
        y = Clamp(y, Margin, canvasHeight - Margin - height);
        return (x, y);
    }

    private static float Clamp(float v, float lo, float hi) => hi < lo ? lo : v < lo ? lo : v > hi ? hi : v;

    private static float Finite(float v) => float.IsNaN(v) || float.IsInfinity(v) || v < 0f ? 0f : v;
}
