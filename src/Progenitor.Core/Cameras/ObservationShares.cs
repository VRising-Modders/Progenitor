using System;
using System.Collections.Generic;

namespace Progenitor.Cameras;

/// <summary>What an <see cref="ObservationShareRules.Decide"/> call says about a new holder.</summary>
internal enum ObservationJoin
{
    /// <summary>No lease for the user and nothing else owns the streaming origin: start one.</summary>
    Start = 0,
    /// <summary>A lease at the same point is running: hold it too.</summary>
    Join = 1,
    /// <summary>A lease at another point is running, or something outside Progenitor owns the origin.</summary>
    Refuse = 2,
}

/// <summary>
/// The rules for sharing one server streaming-origin lease per user between several holders (a plugin's
/// <c>ctx.Camera.Observe</c> and Core's container preview), game-free. The game has one streaming origin per user
/// (<c>User.ProxySyncPosition</c>), so a user can be shown one remote spot at a time: holders at the same point share
/// the lease, a holder at another point is refused.
/// </summary>
internal static class ObservationShareRules
{
    /// <summary>Two lease points closer than this, in meters (3D), are the same spot.</summary>
    internal const float SamePointMeters = 0.5f;

    /// <summary>True when the two points are the same spot (<see cref="SamePointMeters"/>). Any non-finite value is not.</summary>
    internal static bool SamePoint(float ax, float ay, float az, float bx, float by, float bz)
    {
        float dx = ax - bx, dy = ay - by, dz = az - bz;
        float d2 = dx * dx + dy * dy + dz * dz;
        return float.IsFinite(d2) && d2 <= SamePointMeters * SamePointMeters;
    }

    /// <summary>
    /// Whether a new holder starts a lease, joins the running one, or is refused. <paramref name="originOwnedElsewhere"/>
    /// is "the user's streaming origin is non-zero and no Progenitor lease wrote it".
    /// </summary>
    internal static ObservationJoin Decide(bool leaseRunning, bool samePoint, bool originOwnedElsewhere)
    {
        if (leaseRunning) return samePoint ? ObservationJoin.Join : ObservationJoin.Refuse;
        return originOwnedElsewhere ? ObservationJoin.Refuse : ObservationJoin.Start;
    }

    /// <summary>
    /// The reasons that end a lease for every holder at once, in the fixed order of
    /// <see cref="CameraObservationPolicy.Decide"/>, without the per-holder expiry. <see cref="CameraObservationEndReason.None"/>
    /// keeps it running.
    /// </summary>
    internal static CameraObservationEndReason Safety(in CameraObservationSample s)
    {
        if (!s.UserConnected) return CameraObservationEndReason.Disconnected;
        if (!s.OriginOurs) return CameraObservationEndReason.Superseded;
        if (!s.CharacterRead) return CameraObservationEndReason.CharacterGone;
        if (!float.IsFinite(s.MovedMeters)) return CameraObservationEndReason.Failed;
        if (s.MovedMeters > CameraObservationPolicy.LeaveDistanceMeters) return CameraObservationEndReason.LeftArea;
        return CameraObservationEndReason.None;
    }
}

/// <summary>
/// The holders of one shared lease and their own deadlines, game-free. Every holder keeps its own
/// <see cref="CameraObservationPolicy"/> (its own Renew / Hold and expiry); the lease runs while at least one
/// holder does and ends for all of them on a <see cref="ObservationShareRules.Safety"/> reason.
/// </summary>
internal sealed class ObservationShareBook<THolder> where THolder : class
{
    private readonly List<(THolder Holder, CameraObservationPolicy Policy)> _rows = new();

    internal int Count => _rows.Count;

    internal bool Contains(THolder holder) => IndexOf(holder) >= 0;

    /// <summary>Add a holder. Refused (false) when it already holds this lease.</summary>
    internal bool Add(THolder holder, CameraObservationPolicy policy)
    {
        if (holder == null) throw new ArgumentNullException(nameof(holder));
        if (policy == null) throw new ArgumentNullException(nameof(policy));
        if (IndexOf(holder) >= 0) return false;
        _rows.Add((holder, policy));
        return true;
    }

    /// <summary>Remove a holder; false when it held nothing.</summary>
    internal bool Remove(THolder holder)
    {
        int i = IndexOf(holder);
        if (i < 0) return false;
        _rows.RemoveAt(i);
        return true;
    }

    /// <summary>Remove and return every holder whose own deadline has passed, oldest first.</summary>
    internal List<THolder> TakeExpired(double now)
    {
        var expired = new List<THolder>();
        for (int i = 0; i < _rows.Count; i++)
            if (_rows[i].Policy.Expired(now)) expired.Add(_rows[i].Holder);
        foreach (var holder in expired) Remove(holder);
        return expired;
    }

    /// <summary>Remove and return every holder (a lease-wide end).</summary>
    internal List<THolder> TakeAll()
    {
        var all = new List<THolder>(_rows.Count);
        foreach (var row in _rows) all.Add(row.Holder);
        _rows.Clear();
        return all;
    }

    /// <summary>The earliest holder deadline (when the next holder would expire); +infinity with no holders.</summary>
    internal double NextDeadline
    {
        get
        {
            double next = double.PositiveInfinity;
            foreach (var row in _rows) next = Math.Min(next, row.Policy.Deadline);
            return next;
        }
    }

    /// <summary>The latest holder deadline (how long the lease runs if nobody renews); -infinity with no holders.</summary>
    internal double LastDeadline
    {
        get
        {
            double last = double.NegativeInfinity;
            foreach (var row in _rows) last = Math.Max(last, row.Policy.Deadline);
            return last;
        }
    }

    private int IndexOf(THolder holder)
    {
        for (int i = 0; i < _rows.Count; i++)
            if (ReferenceEquals(_rows[i].Holder, holder)) return i;
        return -1;
    }
}
