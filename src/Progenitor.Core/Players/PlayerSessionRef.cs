using System;

namespace Progenitor.Players;

/// <summary>
/// Session-scoped entity refs for one directory row. Valid only while
/// <see cref="PlayerInfo.WorldEpoch"/> is current — after a world load every index:version
/// pair may name something else. Never persist these; persist <see cref="PlayerAccountId"/>.
/// </summary>
public readonly struct PlayerSessionRef : IEquatable<PlayerSessionRef>
{
    /// <summary><c>User</c> entity <c>Index</c>.</summary>
    public int UserIndex { get; }

    /// <summary><c>User</c> entity <c>Version</c>.</summary>
    public int UserVersion { get; }

    /// <summary><c>User.LocalCharacter</c> index, or 0 when none / unreadable.</summary>
    public int CharacterIndex { get; }

    /// <summary><c>User.LocalCharacter</c> version, or 0 when none / unreadable.</summary>
    public int CharacterVersion { get; }

    /// <summary>
    /// <c>User.Index</c> / connected-user slot when online and in range for chat send
    /// (0–127), otherwise -1. Not the ECS entity index.
    /// </summary>
    public int ConnectedUserIndex { get; }

    /// <summary>Construct.</summary>
    public PlayerSessionRef(int userIndex, int userVersion, int characterIndex, int characterVersion, int connectedUserIndex)
    {
        UserIndex = userIndex;
        UserVersion = userVersion;
        CharacterIndex = characterIndex;
        CharacterVersion = characterVersion;
        ConnectedUserIndex = connectedUserIndex;
    }

    /// <summary>True when a character entity pair was recorded.</summary>
    public bool HasCharacter => CharacterIndex != 0 || CharacterVersion != 0;

    /// <summary>Empty session (no user entity).</summary>
    public static PlayerSessionRef None => default;

    /// <inheritdoc />
    public bool Equals(PlayerSessionRef other) =>
        UserIndex == other.UserIndex && UserVersion == other.UserVersion
        && CharacterIndex == other.CharacterIndex && CharacterVersion == other.CharacterVersion
        && ConnectedUserIndex == other.ConnectedUserIndex;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is PlayerSessionRef other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() =>
        HashCode.Combine(UserIndex, UserVersion, CharacterIndex, CharacterVersion, ConnectedUserIndex);

    /// <summary><c>user=index:version char=index:version slot=n</c>.</summary>
    public override string ToString() =>
        "user=" + UserIndex + ":" + UserVersion
        + " char=" + CharacterIndex + ":" + CharacterVersion
        + " slot=" + ConnectedUserIndex;
}
