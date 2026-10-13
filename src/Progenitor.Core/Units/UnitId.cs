using System;

namespace Progenitor.Units;

/// <summary>
/// ECS entity identity without taking a Unity.Entities dependency in tests.
/// Live code maps this to <c>Entity.Index</c> / <c>Entity.Version</c>.
/// </summary>
public readonly struct UnitId : IEquatable<UnitId>
{
    public int Index { get; }
    public int Version { get; }

    public UnitId(int index, int version)
    {
        Index = index;
        Version = version;
    }

    /// <summary>True when this is the default (no entity) value.</summary>
    public bool IsNone => Index == 0 && Version == 0;

    public static UnitId None => default;

    public bool Equals(UnitId other) => Index == other.Index && Version == other.Version;

    public override bool Equals(object? obj) => obj is UnitId other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Index, Version);

    /// <summary>Same <c>index:version</c> form the spawn op and unit spike logs use.</summary>
    public override string ToString() => Index + ":" + Version;

    public static bool operator ==(UnitId left, UnitId right) => left.Equals(right);

    public static bool operator !=(UnitId left, UnitId right) => !left.Equals(right);

#if !PROGENITOR_TESTS
    public static UnitId From(Unity.Entities.Entity entity) =>
        new(entity.Index, entity.Version);

    public Unity.Entities.Entity ToEntity()
    {
        var e = new Unity.Entities.Entity();
        e.Index = Index;
        e.Version = Version;
        return e;
    }
#endif
}
