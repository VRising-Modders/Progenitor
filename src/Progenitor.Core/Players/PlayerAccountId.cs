using System;
using System.Globalization;

namespace Progenitor.Players;

/// <summary>
/// Stable player identity for the directory: <c>User.PlatformId</c> (Steam / account id).
/// Not an ECS entity handle — those recycle across world loads and must not be persisted as
/// identity. <see cref="IsNone"/> is true for 0, which the game uses when a User row has no
/// account binding.
/// </summary>
public readonly struct PlayerAccountId : IEquatable<PlayerAccountId>
{
    /// <summary><c>User.PlatformId</c>.</summary>
    public ulong Value { get; }

    /// <summary>Construct from a platform / account id.</summary>
    public PlayerAccountId(ulong value) => Value = value;

    /// <summary>True when this is the empty (0) id.</summary>
    public bool IsNone => Value == 0;

    /// <summary>The empty id.</summary>
    public static PlayerAccountId None => default;

    /// <summary>Wrap a platform id.</summary>
    public static PlayerAccountId From(ulong platformId) => new(platformId);

    /// <inheritdoc />
    public bool Equals(PlayerAccountId other) => Value == other.Value;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is PlayerAccountId other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => Value.GetHashCode();

    /// <summary>Decimal platform id, invariant culture.</summary>
    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Value equality.</summary>
    public static bool operator ==(PlayerAccountId left, PlayerAccountId right) => left.Equals(right);

    /// <summary>Value inequality.</summary>
    public static bool operator !=(PlayerAccountId left, PlayerAccountId right) => !left.Equals(right);
}
