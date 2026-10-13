using System;

namespace Progenitor.Castles;

/// <summary>
/// World-space position used by the castle API. Game-free so the test assembly does not need
/// Unity; the live runtime converts to <c>float3</c> / <c>Translation.Value</c>. Y is carried
/// but ignored by the territory tests: no world-Y to <c>TilePosition.HeightLevel</c> mapping
/// is verified (see <see cref="CastleGeometry"/>).
/// </summary>
public readonly struct CastleVec3 : IEquatable<CastleVec3>
{
    /// <summary>World X.</summary>
    public float X { get; }

    /// <summary>World Y (height in meters, not a tile height level).</summary>
    public float Y { get; }

    /// <summary>World Z. The castle tile grid's second axis.</summary>
    public float Z { get; }

    /// <summary>Construct from world coordinates.</summary>
    public CastleVec3(float x, float y, float z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    /// <inheritdoc />
    public bool Equals(CastleVec3 other) =>
        X.Equals(other.X) && Y.Equals(other.Y) && Z.Equals(other.Z);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is CastleVec3 other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(X, Y, Z);

    /// <inheritdoc />
    public override string ToString() =>
        "(" + X.ToString("0.##") + ", " + Y.ToString("0.##") + ", " + Z.ToString("0.##") + ")";

    /// <summary>Value equality.</summary>
    public static bool operator ==(CastleVec3 left, CastleVec3 right) => left.Equals(right);

    /// <summary>Value inequality.</summary>
    public static bool operator !=(CastleVec3 left, CastleVec3 right) => !left.Equals(right);

#if !PROGENITOR_TESTS
    /// <summary>Implicit conversion to the game's <c>float3</c>.</summary>
    public static implicit operator Unity.Mathematics.float3(CastleVec3 v) => new(v.X, v.Y, v.Z);

    /// <summary>Implicit conversion from the game's <c>float3</c>.</summary>
    public static implicit operator CastleVec3(Unity.Mathematics.float3 v) => new(v.x, v.y, v.z);
#endif
}
