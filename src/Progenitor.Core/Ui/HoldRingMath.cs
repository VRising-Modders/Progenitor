using System;

namespace Progenitor.Ui;

/// <summary>
/// Game-free numbers behind <see cref="HoldRing"/>: the drawn ring texture's pixels and the
/// completion pulse. Shared by every hold-to-act surface (Core's Dye all, the debug tools' map teleport).
/// </summary>
public static class HoldRingMath
{
    /// <summary>Side of the drawn ring texture, pixels.</summary>
    public const int TextureSize = 128;

    /// <summary>Inner radius of the drawn ring as a fraction of the outer radius.</summary>
    public const float InnerFraction = 0.78f;

    /// <summary>How long the completion pulse lasts, seconds.</summary>
    public const double PulseSeconds = 0.3;

    /// <summary>How far the ring grows over the pulse (1 + this at the end).</summary>
    public const float PulseGrowth = 0.35f;

    /// <summary>
    /// Alpha of drawn-ring pixel (<paramref name="x"/>, <paramref name="y"/>) in a
    /// <paramref name="size"/>-square texture: 1 inside the band between the inner and outer radius,
    /// a one-pixel ramp at both edges, 0 elsewhere.
    /// </summary>
    public static float Alpha(int x, int y, int size, float innerFraction = InnerFraction)
    {
        if (size <= 0) return 0f;
        float half = size / 2f;
        float dx = x + 0.5f - half;
        float dy = y + 0.5f - half;
        float r = (float)Math.Sqrt(dx * dx + dy * dy);
        float outer = half - 1f;
        float inner = outer * innerFraction;
        return Math.Min(Clamp01(outer - r + 0.5f), Clamp01(r - inner + 0.5f));
    }

    /// <summary>The pulse at <paramref name="t"/> seconds after the fire: (scale, alpha). Done past the end.</summary>
    public static (float Scale, float Alpha, bool Done) Pulse(double t)
    {
        if (t <= 0) return (1f, 1f, false);
        if (t >= PulseSeconds) return (1f + PulseGrowth, 0f, true);
        float k = (float)(t / PulseSeconds);
        return (1f + PulseGrowth * k, 1f - k, false);
    }

    private static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;
}
