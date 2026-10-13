using System;

namespace Progenitor.Ui;

/// <summary>
/// The title screen's Collect logs tile glyph (user-approved: "Zip file"): a page with a folded top-right corner and a
/// zipper down its middle, drawn white on transparent so the Image tints it gold. Anti-aliased by 4×4 supersampling, at
/// <see cref="TextureSize"/> (crisp from 1080p to 4K at the icon row's size). Game-free.
/// </summary>
internal static class ZipGlyph
{
    internal const int TextureSize = 128;
    private const int Samples = 4;

    /// <summary>Coverage (0..1) of the glyph at pixel (<paramref name="x"/>, <paramref name="y"/>) of a <paramref name="size"/>² texture (y up).</summary>
    internal static float Alpha(int x, int y, int size)
    {
        if (size <= 0) return 0f;
        int hits = 0;
        for (int sy = 0; sy < Samples; sy++)
            for (int sx = 0; sx < Samples; sx++)
            {
                float u = (x + (sx + 0.5f) / Samples) / size;
                float v = (y + (sy + 0.5f) / Samples) / size;
                if (Inside(u, 1f - v)) hits++;
            }
        return hits / (float)(Samples * Samples);
    }

    // Page geometry in unit space, (0,0) top-left.
    private const float Left = 0.22f, Right = 0.78f, Top = 0.10f, Bottom = 0.90f;
    private const float Fold = 0.16f;
    private const float Stroke = 0.055f;

    /// <summary>True when (u, v) (0..1, v down) is on the glyph: the page outline, the fold, or the zipper.</summary>
    internal static bool Inside(float u, float v)
    {
        // Outside the page's box at all.
        if (u < Left || u > Right || v < Top || v > Bottom) return false;
        // The cut-off corner (top right): beyond the fold's diagonal is empty.
        float cornerU = Right - Fold, cornerV = Top + Fold;
        bool inCorner = u > cornerU && v < cornerV;
        float diag = (u - cornerU) - (v - Top); // > 0: beyond the diagonal from (cornerU, Top) to (Right, cornerV)
        if (inCorner && diag > 0f) return false;

        // The outline: left, bottom, right (below the fold) and top (left of the fold) edges.
        if (u < Left + Stroke || v > Bottom - Stroke) return true;
        if (u > Right - Stroke && v > cornerV) return true;
        if (v < Top + Stroke && u < cornerU) return true;
        // The fold's diagonal edge, and the folded flap's two inner edges.
        if (inCorner && diag > -Stroke * 1.4f) return true;
        if (u > cornerU - Stroke * 0.5f && u < cornerU + Stroke * 0.5f && v < cornerV) return true;
        if (v > cornerV - Stroke * 0.5f && v < cornerV + Stroke * 0.5f && u > cornerU) return true;

        // The zipper: alternating teeth left and right of the center line, from the top edge down to the pull.
        float center = (Left + Right) / 2f;
        const float tooth = 0.05f, toothWidth = 0.07f;
        const float pullTop = 0.58f, pullBottom = 0.74f, pullHalf = 0.065f;
        if (v >= Top && v < pullTop)
        {
            int row = (int)((v - Top) / tooth);
            bool leftTooth = row % 2 == 0;
            if (leftTooth && u >= center - toothWidth && u < center) return true;
            if (!leftTooth && u > center && u <= center + toothWidth) return true;
        }
        // The pull: a small box with a hole.
        if (v >= pullTop && v <= pullBottom && u >= center - pullHalf && u <= center + pullHalf)
        {
            bool hole = v > pullTop + Stroke && v < pullBottom - Stroke && u > center - pullHalf + Stroke && u < center + pullHalf - Stroke;
            return !hole;
        }
        return false;
    }
}
