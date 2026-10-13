using System;

namespace Progenitor.Units;

/// <summary>
/// World-space position used by unit handles. Game-free so tests do not need Unity;
/// the live runtime converts to <c>float3</c> / <c>Translation.Value</c>.
/// </summary>
public readonly struct UnitVec3 : IEquatable<UnitVec3>
{
    public float X { get; }
    public float Y { get; }
    public float Z { get; }

    public UnitVec3(float x, float y, float z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    public bool Equals(UnitVec3 other) =>
        X.Equals(other.X) && Y.Equals(other.Y) && Z.Equals(other.Z);

    public override bool Equals(object? obj) => obj is UnitVec3 other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(X, Y, Z);

    public override string ToString() =>
        "(" + X.ToString("0.##") + ", " + Y.ToString("0.##") + ", " + Z.ToString("0.##") + ")";

    public static bool operator ==(UnitVec3 left, UnitVec3 right) => left.Equals(right);

    public static bool operator !=(UnitVec3 left, UnitVec3 right) => !left.Equals(right);

    /// <summary>3D Euclidean distance, matching ApproachPosition.Tick (includes Y).</summary>
    public static float Distance(UnitVec3 a, UnitVec3 b)
    {
        float dx = a.X - b.X;
        float dy = a.Y - b.Y;
        float dz = a.Z - b.Z;
        return MathF.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
    }

#if !PROGENITOR_TESTS
    public static implicit operator Unity.Mathematics.float3(UnitVec3 v) => new(v.X, v.Y, v.Z);

    public static implicit operator UnitVec3(Unity.Mathematics.float3 v) => new(v.x, v.y, v.z);
#endif
}
