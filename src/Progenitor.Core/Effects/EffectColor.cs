using System;

namespace Progenitor.Effects;

/// <summary>
/// RGBA color used by effect handles. Kept as a game-free struct so unit tests do not
/// reference Unity; on the client it converts to <c>UnityEngine.Color</c>.
/// </summary>
public readonly struct EffectColor : IEquatable<EffectColor>
{
    public float R { get; }
    public float G { get; }
    public float B { get; }
    public float A { get; }

    public EffectColor(float r, float g, float b, float a = 1f)
    {
        R = r;
        G = g;
        B = b;
        A = a;
    }

    public bool Equals(EffectColor other) =>
        R.Equals(other.R) && G.Equals(other.G) && B.Equals(other.B) && A.Equals(other.A);

    public override bool Equals(object? obj) => obj is EffectColor other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(R, G, B, A);

    public override string ToString() =>
        "(" + R.ToString("0.##") + "," + G.ToString("0.##") + "," + B.ToString("0.##") + "," + A.ToString("0.##") + ")";

    public static bool operator ==(EffectColor left, EffectColor right) => left.Equals(right);

    public static bool operator !=(EffectColor left, EffectColor right) => !left.Equals(right);

    /// <summary>
    /// Recolor <paramref name="source"/> with <paramref name="tint"/>'s hue while keeping
    /// the source's HDR intensity (<c>max(rgb)</c>). So a copied GameRed material can be
    /// tinted blue and back without dimming the emission. Alpha is multiplied.
    /// </summary>
    public static EffectColor TintPreserveIntensity(EffectColor source, EffectColor tint)
    {
        float srcI = Max3(source.R, source.G, source.B);
        float tintI = Max3(tint.R, tint.G, tint.B);
        float a = source.A * tint.A;
        if (tintI <= 1e-8f)
            return new EffectColor(0f, 0f, 0f, a);
        float scale = srcI / tintI;
        return new EffectColor(tint.R * scale, tint.G * scale, tint.B * scale, a);
    }

    /// <summary>Largest of three channels. Used by <see cref="TintPreserveIntensity"/> and tests.</summary>
    public static float Max3(float a, float b, float c)
    {
        float m = a > b ? a : b;
        return m > c ? m : c;
    }

#if !PROGENITOR_TESTS
    public static implicit operator UnityEngine.Color(EffectColor c) => new(c.R, c.G, c.B, c.A);

    public static implicit operator EffectColor(UnityEngine.Color c) => new(c.r, c.g, c.b, c.a);
#endif
}

/// <summary>Screen-space size or anchored position. Game-free so tests do not need Unity.</summary>
public readonly struct EffectVec2 : IEquatable<EffectVec2>
{
    public float X { get; }
    public float Y { get; }

    public EffectVec2(float x, float y)
    {
        X = x;
        Y = y;
    }

    public bool Equals(EffectVec2 other) => X.Equals(other.X) && Y.Equals(other.Y);

    public override bool Equals(object? obj) => obj is EffectVec2 other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(X, Y);

    public override string ToString() => "(" + X.ToString("0.##") + "," + Y.ToString("0.##") + ")";

    public static bool operator ==(EffectVec2 left, EffectVec2 right) => left.Equals(right);

    public static bool operator !=(EffectVec2 left, EffectVec2 right) => !left.Equals(right);

#if !PROGENITOR_TESTS
    public static implicit operator UnityEngine.Vector2(EffectVec2 v) => new(v.X, v.Y);

    public static implicit operator EffectVec2(UnityEngine.Vector2 v) => new(v.x, v.y);
#endif
}

/// <summary>World-space position. Game-free so tests do not need Unity.</summary>
public readonly struct EffectVec3 : IEquatable<EffectVec3>
{
    public float X { get; }
    public float Y { get; }
    public float Z { get; }

    public EffectVec3(float x, float y, float z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    public bool Equals(EffectVec3 other) => X.Equals(other.X) && Y.Equals(other.Y) && Z.Equals(other.Z);

    public override bool Equals(object? obj) => obj is EffectVec3 other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(X, Y, Z);

    public override string ToString() =>
        "(" + X.ToString("0.##") + "," + Y.ToString("0.##") + "," + Z.ToString("0.##") + ")";

    public static bool operator ==(EffectVec3 left, EffectVec3 right) => left.Equals(right);

    public static bool operator !=(EffectVec3 left, EffectVec3 right) => !left.Equals(right);

#if !PROGENITOR_TESTS
    public static implicit operator UnityEngine.Vector3(EffectVec3 v) => new(v.X, v.Y, v.Z);

    public static implicit operator EffectVec3(UnityEngine.Vector3 v) => new(v.x, v.y, v.z);
#endif
}
