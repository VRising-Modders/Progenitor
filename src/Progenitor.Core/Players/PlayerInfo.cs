using System;
using System.Collections.Generic;

namespace Progenitor.Players;

/// <summary>
/// One directory snapshot row: stable account identity, display name, online state, and
/// session-scoped entity refs. Built by <see cref="PlayerDirectoryLogic"/> from adapter rows
/// or by the live scanner. Epoch and revision stamp freshness — a retained
/// <see cref="PlayerInfo"/> must not drive mutation after either moves.
/// </summary>
public sealed class PlayerInfo
{
    /// <summary>Stable account identity (<c>User.PlatformId</c>). Not an entity handle.</summary>
    public PlayerAccountId Account { get; init; }

    /// <summary><c>User.CharacterName</c> at snapshot time, empty when unreadable.</summary>
    public string DisplayName { get; init; } = "";

    /// <summary><c>User.IsConnected</c> at snapshot time.</summary>
    public bool IsOnline { get; init; }

    /// <summary>Session entity refs. Meaningless when <see cref="WorldEpoch"/> is stale.</summary>
    public PlayerSessionRef Session { get; init; }

    /// <summary><see cref="Progenitor.WorldState.WorldEpoch.Current"/> when this row was stamped.</summary>
    public int WorldEpoch { get; init; }

    /// <summary>Directory cache revision when this row was stamped.</summary>
    public int DirectoryRevision { get; init; }

    /// <summary>Convenience: <see cref="PlayerAccountId.Value"/>.</summary>
    public ulong PlatformId => Account.Value;

    /// <inheritdoc />
    public override string ToString() =>
        (DisplayName.Length == 0 ? Account.ToString() : DisplayName)
        + (IsOnline ? " online" : " offline")
        + " @" + Account;
}

/// <summary>Raw row the game-free matcher consumes. No Unity / ProjectM types.</summary>
public readonly struct PlayerDirectoryRow
{
    /// <summary><c>User.PlatformId</c>; 0 is invalid and skipped by lookups.</summary>
    public readonly ulong AccountId;

    /// <summary><c>User.CharacterName</c>.</summary>
    public readonly string DisplayName;

    /// <summary><c>User.IsConnected</c>.</summary>
    public readonly bool IsOnline;

    /// <summary>Connected-user slot (<c>User.Index</c>) when online, else -1.</summary>
    public readonly int ConnectedUserIndex;

    /// <summary>User entity index.</summary>
    public readonly int UserIndex;

    /// <summary>User entity version.</summary>
    public readonly int UserVersion;

    /// <summary>Character entity index, or 0.</summary>
    public readonly int CharacterIndex;

    /// <summary>Character entity version, or 0.</summary>
    public readonly int CharacterVersion;

    /// <summary>Construct.</summary>
    public PlayerDirectoryRow(
        ulong accountId,
        string displayName,
        bool isOnline,
        int connectedUserIndex,
        int userIndex,
        int userVersion,
        int characterIndex,
        int characterVersion)
    {
        AccountId = accountId;
        DisplayName = displayName ?? "";
        IsOnline = isOnline;
        ConnectedUserIndex = connectedUserIndex;
        UserIndex = userIndex;
        UserVersion = userVersion;
        CharacterIndex = characterIndex;
        CharacterVersion = characterVersion;
    }

    /// <summary>Stamp into a public <see cref="PlayerInfo"/>.</summary>
    public PlayerInfo ToInfo(int worldEpoch, int directoryRevision) => new()
    {
        Account = PlayerAccountId.From(AccountId),
        DisplayName = DisplayName,
        IsOnline = IsOnline,
        Session = new PlayerSessionRef(UserIndex, UserVersion, CharacterIndex, CharacterVersion, ConnectedUserIndex),
        WorldEpoch = worldEpoch,
        DirectoryRevision = directoryRevision,
    };
}

/// <summary>Outcome of a name lookup. Ambiguous never picks an arbitrary match.</summary>
public enum PlayerLookupStatus
{
    /// <summary>No row matched.</summary>
    NotFound = 0,

    /// <summary>Exactly one row matched.</summary>
    Exact = 1,

    /// <summary>Two or more rows matched; see <see cref="PlayerNameLookup.Candidates"/>.</summary>
    Ambiguous = 2,
}

/// <summary>
/// Name-lookup result. When <see cref="Status"/> is <see cref="PlayerLookupStatus.Ambiguous"/>,
/// <see cref="Candidates"/> holds every match and <see cref="Player"/> is null. Callers must
/// not treat an ambiguous result as a single target.
/// </summary>
public sealed class PlayerNameLookup
{
    /// <summary>Construct.</summary>
    public PlayerNameLookup(PlayerLookupStatus status, PlayerInfo? player, IReadOnlyList<PlayerInfo> candidates, bool isFresh)
    {
        Status = status;
        Player = player;
        Candidates = candidates ?? Array.Empty<PlayerInfo>();
        IsFresh = isFresh;
    }

    /// <summary>Whether the name resolved uniquely, ambiguously, or not at all.</summary>
    public PlayerLookupStatus Status { get; }

    /// <summary>The single match when <see cref="Status"/> is <see cref="PlayerLookupStatus.Exact"/>.</summary>
    public PlayerInfo? Player { get; }

    /// <summary>Every match when ambiguous; empty otherwise (exact puts the one match in <see cref="Player"/>).</summary>
    public IReadOnlyList<PlayerInfo> Candidates { get; }

    /// <summary>False when the directory revision or world epoch moved since this result was stamped.</summary>
    public bool IsFresh { get; }

    /// <summary>Empty not-found result.</summary>
    public static PlayerNameLookup NotFound(bool isFresh) =>
        new(PlayerLookupStatus.NotFound, null, Array.Empty<PlayerInfo>(), isFresh);
}
