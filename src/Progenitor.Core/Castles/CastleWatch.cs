using System;
using System.Collections.Generic;

namespace Progenitor.Castles;

/// <summary>
/// The polled change detector behind <see cref="CastleHeartPlaced"/> /
/// <see cref="CastleHeartDestroyed"/>, kept pure so the diff is testable without the game.
///
/// <para><b>Why polling.</b> There is no cheap seam that says "a heart was placed" on its own.
/// <c>SpawnCastleHeartSystem</c> and
/// <c>CastleHeartEventSystem</c> are <c>SystemBase</c>s whose <c>Update</c> runs
/// every frame whether or not a heart changed, so hooking them through <c>SystemHooks</c> would
/// still need this diff and would run it at frame rate instead of once a second. Destruction has
/// no single system at all — a heart can go through raid, dismantle or decay. So the module
/// polls the <c>CastleHeart</c> query on a <see cref="DefaultIntervalSeconds"/> cadence, and only
/// while something is subscribed. If a per-event seam is found later, the event types do not
/// change; only who fills them.</para>
/// </summary>
public static class CastleWatch
{
    /// <summary>Poll cadence in seconds.</summary>
    public const double DefaultIntervalSeconds = 1.0;

    /// <summary>
    /// True when <paramref name="nowMs"/> is at least <paramref name="intervalSeconds"/> past
    /// <paramref name="lastMs"/>. <paramref name="lastMs"/> of <see cref="long.MinValue"/> means
    /// "never polled" and always fires. A clock that jumps backwards also fires rather than
    /// wedging until it catches up.
    /// </summary>
    public static bool ShouldPoll(long nowMs, long lastMs, double intervalSeconds = DefaultIntervalSeconds)
    {
        if (lastMs == long.MinValue) return true;
        if (nowMs < lastMs) return true;
        double interval = intervalSeconds <= 0 ? DefaultIntervalSeconds : intervalSeconds;
        return nowMs - lastMs >= (long)(interval * 1000.0);
    }

    /// <summary>
    /// Diff two snapshot lists by heart entity. A heart in <paramref name="current"/> but not in
    /// <paramref name="previous"/> is placed; the reverse is destroyed. Entity version is part of
    /// the key, so a recycled entity index reads as one destroy and one place rather than as
    /// silent continuity. Order of the outputs follows <paramref name="current"/> /
    /// <paramref name="previous"/>, which the runtime sorts by territory index.
    /// </summary>
    public static void Diff(
        IReadOnlyList<CastleSnapshot>? previous,
        IReadOnlyList<CastleSnapshot>? current,
        List<CastleSnapshot> placed,
        List<CastleSnapshot> destroyed)
    {
        if (placed == null) throw new ArgumentNullException(nameof(placed));
        if (destroyed == null) throw new ArgumentNullException(nameof(destroyed));
        placed.Clear();
        destroyed.Clear();

        var before = ToSet(previous);
        var after = ToSet(current);

        if (current != null)
        {
            for (int i = 0; i < current.Count; i++)
            {
                var row = current[i];
                if (row == null || row.Heart.IsNone) continue;
                if (!before.Contains(row.Heart))
                    placed.Add(row);
            }
        }

        if (previous != null)
        {
            for (int i = 0; i < previous.Count; i++)
            {
                var row = previous[i];
                if (row == null || row.Heart.IsNone) continue;
                if (!after.Contains(row.Heart))
                    destroyed.Add(row);
            }
        }
    }

    private static HashSet<CastleId> ToSet(IReadOnlyList<CastleSnapshot>? rows)
    {
        var set = new HashSet<CastleId>();
        if (rows == null) return set;
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row != null && !row.Heart.IsNone)
                set.Add(row.Heart);
        }

        return set;
    }
}
