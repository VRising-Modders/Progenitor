using System;

namespace Progenitor.Buffs;

/// <summary>
/// ECS entity identity without a Unity.Entities dependency, so the lifecycle logic compiles and
/// is tested without the game. Same shape and the same <c>index:version</c> text as
/// <see cref="Progenitor.Units.UnitId"/>; a separate type because the two modules have no
/// dependency on each other and neither owns the other's handles.
///
/// <para><b>Never persist one.</b> Entity indices are re-issued across a save reload, which is why
/// an application also stamps <see cref="Progenitor.WorldState.WorldEpoch"/> and stops touching
/// the world when that epoch moves.</para>
/// </summary>
public readonly struct BuffEntityId : IEquatable<BuffEntityId>
{
    public int Index { get; }
    public int Version { get; }

    public BuffEntityId(int index, int version)
    {
        Index = index;
        Version = version;
    }

    /// <summary>True when this is the default (no entity) value.</summary>
    public bool IsNone => Index == 0 && Version == 0;

    public static BuffEntityId None => default;

    public bool Equals(BuffEntityId other) => Index == other.Index && Version == other.Version;

    public override bool Equals(object? obj) => obj is BuffEntityId other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Index, Version);

    /// <summary>The <c>index:version</c> form the Dev bridge and the unit logs already use.</summary>
    public override string ToString() => Index + ":" + Version;

    public static bool operator ==(BuffEntityId left, BuffEntityId right) => left.Equals(right);

    public static bool operator !=(BuffEntityId left, BuffEntityId right) => !left.Equals(right);

#if !PROGENITOR_TESTS
    public static BuffEntityId From(Unity.Entities.Entity entity) =>
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
