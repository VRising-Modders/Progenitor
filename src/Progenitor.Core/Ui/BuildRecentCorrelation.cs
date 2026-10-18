using System;
using System.Collections.Generic;
using System.Numerics;

namespace Progenitor.Ui;

/// <summary>Why <see cref="BuildRecentCorrelation.Check"/> did or did not confirm a structure.</summary>
internal enum BuildRecentConfirm
{
    Confirmed,
    InvalidTarget,
    AlreadyConfirmed,
    NoPendingForUser,
    GuidMismatch,
    RotationMismatch,
    PositionMismatch,
    ExistedBefore,
    Ambiguous,
}

/// <summary>Fail-closed correlation; no game objects or persisted entity identities.</summary>
internal sealed class BuildRecentCorrelation
{
    internal const int Capacity = 128;
    internal const long LifetimeMs = 2000;
    private sealed record Pending(ulong Source, bool Delayed, ulong User, int Guid, Vector3 Position,
        byte Rotation, long At, HashSet<ulong> Existing);
    private readonly List<Pending> _pending = new();
    private readonly Dictionary<(ulong, bool), long> _sources = new();
    private readonly Dictionary<ulong, long> _targets = new();
    private long _blockedUntil;

    internal int PendingCount => _pending.Count;

    /// <summary>
    /// Diagnostics only: the offset from the closest pending request (same user, blueprint and rotation) to the last
    /// structure refused as <see cref="BuildRecentConfirm.PositionMismatch"/>. Never used to match.
    /// </summary>
    internal Vector3? LastPositionMiss { get; private set; }

    internal void Clear() { _pending.Clear(); _sources.Clear(); _targets.Clear(); _blockedUntil = 0; }
    internal void Prune(long now)
    {
        _pending.RemoveAll(p => now - p.At > LifetimeMs);
        foreach (var key in new List<(ulong, bool)>(_sources.Keys))
            if (now - _sources[key] > 30000) _sources.Remove(key);
        foreach (var key in new List<ulong>(_targets.Keys))
            if (now - _targets[key] > 30000) _targets.Remove(key);
    }

    /// <summary>Whether a pending request of <paramref name="user"/> exists (a cheap pre-check before reading a structure).</summary>
    internal bool HasPendingFor(ulong user)
    {
        foreach (var p in _pending) if (p.User == user) return true;
        return false;
    }

    internal bool Add(ulong source, bool delayed, ulong user, int guid, Vector3 position, byte rotation,
        long now, HashSet<ulong> existing)
    {
        Prune(now);
        if (source == 0 || user == 0 || guid == 0 || rotation > 3 || !Finite(position)
            || now < _blockedUntil || _sources.ContainsKey((source, delayed)) || _sources.Count >= 4096) return false;
        _sources.Add((source, delayed), now);
        // The pre-cast completion revalidates the same intent; it replaces its earlier request.
        if (delayed) _pending.RemoveAll(p => !p.Delayed && p.User == user && p.Guid == guid
            && p.Rotation == rotation && Same(p.Position, position));
        if (_pending.Count >= Capacity) { _pending.Clear(); _blockedUntil = now + LifetimeMs; return false; }
        _pending.Add(new(source, delayed, user, guid, position, rotation, now, existing));
        return true;
    }

    internal bool Confirm(ulong target, ulong user, int guid, Vector3 position, byte rotation, long now) =>
        Check(target, user, guid, position, rotation, now) == BuildRecentConfirm.Confirmed;

    /// <summary>
    /// Confirms <paramref name="target"/> as the result of exactly one pending request, or says why not. Every field
    /// must match exactly (position within 0.001 world units); the reason is the first gate no request passed.
    /// </summary>
    internal BuildRecentConfirm Check(ulong target, ulong user, int guid, Vector3 position, byte rotation, long now)
    {
        Prune(now);
        if (target == 0 || !Finite(position) || _targets.Count >= 4096) return BuildRecentConfirm.InvalidTarget;
        if (_targets.ContainsKey(target)) return BuildRecentConfirm.AlreadyConfirmed;
        bool anyUser = false, anyGuid = false, anyRotation = false, anyPosition = false;
        Vector3? closest = null;
        int matches = 0;
        foreach (var p in _pending)
        {
            if (p.User != user) continue;
            anyUser = true;
            if (p.Guid != guid) continue;
            anyGuid = true;
            if (p.Rotation != rotation) continue;
            anyRotation = true;
            if (!Same(p.Position, position))
            {
                var d = position - p.Position;
                if (closest == null || d.LengthSquared() < closest.Value.LengthSquared()) closest = d;
                continue;
            }
            anyPosition = true;
            if (!p.Existing.Contains(target)) matches++;
        }
        if (!anyUser) return BuildRecentConfirm.NoPendingForUser;
        if (!anyGuid) return BuildRecentConfirm.GuidMismatch;
        if (!anyRotation) return BuildRecentConfirm.RotationMismatch;
        if (!anyPosition) { LastPositionMiss = closest; return BuildRecentConfirm.PositionMismatch; }
        if (matches == 0) return BuildRecentConfirm.ExistedBefore;
        _targets.Add(target, now);
        _pending.RemoveAll(p => p.User == user && p.Guid == guid && p.Rotation == rotation && Same(p.Position, position));
        return matches == 1 ? BuildRecentConfirm.Confirmed : BuildRecentConfirm.Ambiguous;
    }

    private static bool Same(Vector3 a, Vector3 b) => Vector3.DistanceSquared(a, b) <= 0.000001f;
    private static bool Finite(Vector3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);
}

/// <summary>Named, monotonically increasing per-stage counters for the Recent pipeline's diagnostics. Thread-safe.</summary>
internal sealed class BuildRecentStats
{
    private readonly object _gate = new();
    private readonly Dictionary<string, long> _counts = new(StringComparer.Ordinal);

    internal void Add(string name, long by = 1)
    {
        lock (_gate) _counts[name] = (_counts.TryGetValue(name, out long n) ? n : 0) + by;
    }

    internal long Get(string name)
    {
        lock (_gate) return _counts.TryGetValue(name, out long n) ? n : 0;
    }

    internal void Clear() { lock (_gate) _counts.Clear(); }

    /// <summary>A copy of every counter, the given names first with 0 when unset, so a reader always sees each stage.</summary>
    internal Dictionary<string, object?> Snapshot(IEnumerable<string> names)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        lock (_gate)
        {
            foreach (string n in names) result[n] = _counts.TryGetValue(n, out long v) ? v : 0L;
            foreach (var pair in _counts) result[pair.Key] = pair.Value;
        }
        return result;
    }

    /// <summary>The counter name of a refusal reason, such as <c>confirmPositionMismatch</c>.</summary>
    internal static string NameOf(BuildRecentConfirm reason) => "confirm" + reason;
}
