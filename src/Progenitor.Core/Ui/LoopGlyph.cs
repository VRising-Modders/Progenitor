using System;

namespace Progenitor.Ui;

/// <summary>
/// The Mods list's <b>Works across versions</b> icon (C5, user choice 2026-10-10, option A of the marker mockup): two arcs
/// of a circle, each ending in an arrowhead, chasing each other clockwise. Drawn white on transparent so the Image tints
/// it (<see cref="MenuPalette.Muted"/>, the second line's color), anti-aliased by 4×4 supersampling. The texture is the
/// glyph's own bounding box (<see cref="TextureWidth"/> × <see cref="TextureHeight"/>), so the drawn size is the glyph's
/// size. Game-free; the runtime makes one texture from <see cref="Pixels"/> per Core generation and destroys it on
/// Core Uninstall.
/// </summary>
internal static class LoopGlyph
{
    /// <summary>The texture's size. 6:5, the glyph's own aspect: crisp from 1080p to 4K at the list's 13 × 11 px.</summary>
    internal const int TextureWidth = 96;

    internal const int TextureHeight = 80;

    private const int Samples = 4;

    // The mockup's geometry (c5_marker_mock.py, option A), in a unit square with v down: center, radius, stroke, the two
    // arcs (degrees clockwise from 3 o'clock) and the arrowheads at each arc's clockwise end.
    private const float Center = 0.5f;
    private const float Radius = 0.33f;
    private const float Stroke = 0.10f;
    private const float Sweep = 125f;
    private static readonly float[] ArcStarts = { 200f, 20f };
    private const float HeadLength = 0.17f;
    private const float HeadHalf = 0.15f;

    // The glyph's bounding box inside that square: the texture maps exactly onto it.
    private const float BoxLeft = 54f / 512f, BoxTop = 87f / 512f, BoxWidth = 404f / 512f, BoxHeight = 338f / 512f;

    private static float[]? _pixels;

    /// <summary>
    /// Coverage (0..1) of every texture pixel, row by row from the bottom (Unity's <c>SetPixel</c> order: index
    /// <c>y * TextureWidth + x</c>, y up). Computed once and kept: the same array every call.
    /// </summary>
    internal static float[] Pixels()
    {
        var cached = _pixels;
        if (cached != null) return cached;
        var pixels = new float[TextureWidth * TextureHeight];
        for (int y = 0; y < TextureHeight; y++)
            for (int x = 0; x < TextureWidth; x++)
                pixels[y * TextureWidth + x] = Alpha(x, y);
        _pixels = pixels;
        return pixels;
    }

    /// <summary>Coverage (0..1) of the glyph at texture pixel (<paramref name="x"/>, <paramref name="y"/>), y up.</summary>
    internal static float Alpha(int x, int y)
    {
        if (x < 0 || y < 0 || x >= TextureWidth || y >= TextureHeight) return 0f;
        int hits = 0;
        for (int sy = 0; sy < Samples; sy++)
            for (int sx = 0; sx < Samples; sx++)
            {
                float u = (x + (sx + 0.5f) / Samples) / TextureWidth;
                float v = (y + (sy + 0.5f) / Samples) / TextureHeight;
                if (Inside(u, 1f - v)) hits++;
            }

        return hits / (float)(Samples * Samples);
    }

    /// <summary>True when (u, v) (0..1 over the texture, v down) is on the glyph: an arc's stroke or an arrowhead.</summary>
    internal static bool Inside(float u, float v)
    {
        float x = BoxLeft + u * BoxWidth;
        float y = BoxTop + v * BoxHeight;
        float dx = x - Center, dy = y - Center;
        float distance = (float)Math.Sqrt(dx * dx + dy * dy);
        // The stroke lies inside the circle's edge, as the mockup's arc drew it.
        if (distance <= Radius && distance >= Radius - Stroke)
        {
            float angle = (float)(Math.Atan2(dy, dx) * 180.0 / Math.PI);
            if (angle < 0f) angle += 360f;
            foreach (float start in ArcStarts)
            {
                float into = angle - start;
                if (into < 0f) into += 360f;
                if (into <= Sweep) return true;
            }
        }

        foreach (float start in ArcStarts)
            if (InHead(start + Sweep, x, y)) return true;
        return false;
    }

    /// <summary>The arrowhead at <paramref name="degrees"/> on the circle: its tip one head length along the clockwise tangent.</summary>
    private static bool InHead(float degrees, float x, float y)
    {
        double a = degrees * Math.PI / 180.0;
        float px = Center + Radius * (float)Math.Cos(a), py = Center + Radius * (float)Math.Sin(a);
        float tx = -(float)Math.Sin(a), ty = (float)Math.Cos(a);
        float nx = (float)Math.Cos(a), ny = (float)Math.Sin(a);
        float ax = px + tx * HeadLength, ay = py + ty * HeadLength;
        float bx = px + nx * HeadHalf, by = py + ny * HeadHalf;
        float cx = px - nx * HeadHalf, cy = py - ny * HeadHalf;
        float d1 = Side(x, y, ax, ay, bx, by), d2 = Side(x, y, bx, by, cx, cy), d3 = Side(x, y, cx, cy, ax, ay);
        bool negative = d1 < 0f || d2 < 0f || d3 < 0f;
        bool positive = d1 > 0f || d2 > 0f || d3 > 0f;
        return !(negative && positive);
    }

    private static float Side(float x, float y, float ax, float ay, float bx, float by) =>
        (x - bx) * (ay - by) - (ax - bx) * (y - by);
}
