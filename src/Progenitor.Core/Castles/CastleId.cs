using System;

namespace Progenitor.Castles;

/// <summary>
/// ECS entity identity without taking a Unity.Entities dependency in the test assembly.
/// Same shape as <c>Progenitor.Units.UnitId</c>; live code maps it to <c>Entity.Index</c> /
/// <c>Entity.Version</c>, which is also the <c>index:version</c> form the debug bridge prints.
/// </summary>
public readonly struct CastleId : IEquatable<CastleId>
{
    /// <summary><c>Entity.Index</c>.</summary>
    public int Index { get; }

    /// <summary><c>Entity.Version</c>.</summary>
    public int Version { get; }

    /// <summary>Construct from a raw index / version pair.</summary>
    public CastleId(int index, int version)
    {
        Index = index;
        Version = version;
    }

    /// <summary>True when this is the default (no entity) value.</summary>
    public bool IsNone => Index == 0 && Version == 0;

    /// <summary>The default (no entity) value.</summary>
    public static CastleId None => default;

    /// <inheritdoc />
    public bool Equals(CastleId other) => Index == other.Index && Version == other.Version;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is CastleId other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Index, Version);

    /// <summary>Same <c>index:version</c> form the <c>castles</c> bridge op prints.</summary>
    public override string ToString() => Index + ":" + Version;

    /// <summary>Value equality.</summary>
    public static bool operator ==(CastleId left, CastleId right) => left.Equals(right);

    /// <summary>Value inequality.</summary>
    public static bool operator !=(CastleId left, CastleId right) => !left.Equals(right);

#if !PROGENITOR_TESTS
    /// <summary>Wrap a live entity.</summary>
    public static CastleId From(Unity.Entities.Entity entity) =>
        new(entity.Index, entity.Version);

    /// <summary>Unwrap to a live entity.</summary>
    public Unity.Entities.Entity ToEntity()
    {
        var e = new Unity.Entities.Entity();
        e.Index = Index;
        e.Version = Version;
        return e;
    }
#endif
}
