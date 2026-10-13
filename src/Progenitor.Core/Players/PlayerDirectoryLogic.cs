using System;
using System.Collections.Generic;

namespace Progenitor.Players;

/// <summary>
/// Game-free player-directory rules shared by the live scanner and the unit tests: name
/// matching, account lookup, online filter, freshness, and rename detection. No Unity /
/// ProjectM types. A stable identity is <see cref="PlayerAccountId"/> — never a raw entity
/// handle.
/// </summary>
public static class PlayerDirectoryLogic
{
    /// <summary>Whole-name compare used by every directory lookup.</summary>
    public static readonly StringComparison NameComparison = StringComparison.OrdinalIgnoreCase;

    /// <summary>Invalidate reason: a player joined.</summary>
    public const string ReasonConnect = "connect";

    /// <summary>Invalidate reason: a player left.</summary>
    public const string ReasonDisconnect = "disconnect";

    /// <summary>Invalidate reason: an account kept its id but changed display name.</summary>
    public const string ReasonRename = "rename";

    /// <summary>Invalidate reason: world load / client world create or end.</summary>
    public const string ReasonWorld = "world";

    /// <summary>Invalidate reason: Core reload / explicit clear.</summary>
    public const string ReasonReload = "reload";

    /// <summary>True when <paramref name="platformId"/> is a usable account identity.</summary>
    public static bool IsValidAccount(ulong platformId) => platformId != 0;

    /// <summary>Whole-name, case-insensitive equality. Null or empty never matches.</summary>
    public static bool NamesMatch(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        return string.Equals(a, b, NameComparison);
    }

    /// <summary>
    /// Copy online rows into <paramref name="destination"/> (cleared first). Offline discovery
    /// does not imply an operable character — callers that need a live session use this filter.
    /// </summary>
    public static void CollectOnline(IReadOnlyList<PlayerDirectoryRow> rows, List<PlayerDirectoryRow> destination)
    {
        if (destination == null) throw new ArgumentNullException(nameof(destination));
        destination.Clear();
        if (rows == null || rows.Count == 0) return;
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (!row.IsOnline) continue;
            if (!IsValidAccount(row.AccountId)) continue;
            destination.Add(row);
        }
    }

    /// <summary>
    /// Find rows whose display name matches <paramref name="name"/>. Duplicate names yield
    /// <see cref="PlayerLookupStatus.Ambiguous"/> with every hit index — never an arbitrary
    /// single match. Invalid / empty names are <see cref="PlayerLookupStatus.NotFound"/>.
    /// </summary>
    public static PlayerLookupStatus MatchByName(
        IReadOnlyList<PlayerDirectoryRow> rows,
        string? name,
        bool onlineOnly,
        List<int> hitIndexes)
    {
        if (hitIndexes == null) throw new ArgumentNullException(nameof(hitIndexes));
        hitIndexes.Clear();
        if (string.IsNullOrWhiteSpace(name) || rows == null || rows.Count == 0)
            return PlayerLookupStatus.NotFound;

        string needle = name.Trim();
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (!IsValidAccount(row.AccountId)) continue;
            if (onlineOnly && !row.IsOnline) continue;
            if (!NamesMatch(row.DisplayName, needle)) continue;
            hitIndexes.Add(i);
        }

        if (hitIndexes.Count == 0) return PlayerLookupStatus.NotFound;
        if (hitIndexes.Count == 1) return PlayerLookupStatus.Exact;
        return PlayerLookupStatus.Ambiguous;
    }

    /// <summary>
    /// Index of the row with <paramref name="accountId"/>, or -1. Account id 0 never matches.
    /// When <paramref name="onlineOnly"/> is set, an offline row is treated as missing.
    /// </summary>
    public static int FindByAccount(IReadOnlyList<PlayerDirectoryRow> rows, ulong accountId, bool onlineOnly)
    {
        if (!IsValidAccount(accountId) || rows == null) return -1;
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row.AccountId != accountId) continue;
            if (onlineOnly && !row.IsOnline) return -1;
            return i;
        }
        return -1;
    }

    /// <summary>
    /// True when a retained stamp still matches the live directory: same world epoch and same
    /// cache revision. Either moving means session entity refs must not be used for mutation.
    /// </summary>
    public static bool IsFresh(int stampedEpoch, int stampedRevision, int currentEpoch, int currentRevision)
        => stampedEpoch != 0
           && stampedRevision != 0
           && stampedEpoch == currentEpoch
           && stampedRevision == currentRevision;

    /// <summary>
    /// True when some account id appears in both lists with a different display name.
    /// Used to bump the directory revision on rename even without a dedicated rename event.
    /// </summary>
    public static bool DetectedRename(IReadOnlyList<PlayerDirectoryRow> previous, IReadOnlyList<PlayerDirectoryRow> next)
    {
        if (previous == null || next == null || previous.Count == 0 || next.Count == 0)
            return false;

        for (int i = 0; i < next.Count; i++)
        {
            var row = next[i];
            if (!IsValidAccount(row.AccountId)) continue;
            for (int j = 0; j < previous.Count; j++)
            {
                var old = previous[j];
                if (old.AccountId != row.AccountId) continue;
                if (!NamesMatch(old.DisplayName, row.DisplayName)
                    && !string.IsNullOrEmpty(old.DisplayName)
                    && !string.IsNullOrEmpty(row.DisplayName))
                    return true;
                break;
            }
        }
        return false;
    }

    /// <summary>
    /// Build a <see cref="PlayerNameLookup"/> from match status and hit indexes into
    /// <paramref name="rows"/>. Ambiguous fills <see cref="PlayerNameLookup.Candidates"/> and
    /// leaves <see cref="PlayerNameLookup.Player"/> null.
    /// </summary>
    public static PlayerNameLookup ToNameLookup(
        PlayerLookupStatus status,
        IReadOnlyList<PlayerDirectoryRow> rows,
        List<int> hitIndexes,
        int worldEpoch,
        int directoryRevision,
        bool isFresh)
    {
        if (status == PlayerLookupStatus.NotFound || hitIndexes == null || hitIndexes.Count == 0)
            return PlayerNameLookup.NotFound(isFresh);

        if (status == PlayerLookupStatus.Exact)
        {
            var info = rows[hitIndexes[0]].ToInfo(worldEpoch, directoryRevision);
            return new PlayerNameLookup(PlayerLookupStatus.Exact, info, Array.Empty<PlayerInfo>(), isFresh);
        }

        var candidates = new List<PlayerInfo>(hitIndexes.Count);
        for (int i = 0; i < hitIndexes.Count; i++)
            candidates.Add(rows[hitIndexes[i]].ToInfo(worldEpoch, directoryRevision));
        return new PlayerNameLookup(PlayerLookupStatus.Ambiguous, null, candidates, isFresh);
    }
}
