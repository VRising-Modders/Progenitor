using System;
using System.Collections.Generic;
using System.Numerics;

namespace Progenitor.Ui;

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

    internal void Clear() { _pending.Clear(); _sources.Clear(); _targets.Clear(); _blockedUntil = 0; }
    internal void Prune(long now)
    {
        _pending.RemoveAll(p => now - p.At > LifetimeMs);
        foreach (var key in new List<(ulong, bool)>(_sources.Keys))
            if (now - _sources[key] > 30000) _sources.Remove(key);
        foreach (var key in new List<ulong>(_targets.Keys))
            if (now - _targets[key] > 30000) _targets.Remove(key);
    }

    internal void Add(ulong source, bool delayed, ulong user, int guid, Vector3 position, byte rotation,
        long now, HashSet<ulong> existing)
    {
        Prune(now);
        if (source == 0 || user == 0 || guid == 0 || rotation > 3 || !Finite(position)
            || now < _blockedUntil || _sources.ContainsKey((source, delayed)) || _sources.Count >= 4096) return;
        _sources.Add((source, delayed), now);
        // The pre-cast completion revalidates the same intent; it replaces its earlier request.
        if (delayed) _pending.RemoveAll(p => !p.Delayed && p.User == user && p.Guid == guid
            && p.Rotation == rotation && Same(p.Position, position));
        if (_pending.Count >= Capacity) { _pending.Clear(); _blockedUntil = now + LifetimeMs; return; }
        _pending.Add(new(source, delayed, user, guid, position, rotation, now, existing));
    }

    internal bool Confirm(ulong target, ulong user, int guid, Vector3 position, byte rotation, long now)
    {
        Prune(now);
        if (target == 0 || !Finite(position) || _targets.ContainsKey(target) || _targets.Count >= 4096) return false;
        int matches = 0;
        foreach (var p in _pending)
            if (p.User == user && p.Guid == guid && p.Rotation == rotation && Same(p.Position, position)
                && !p.Existing.Contains(target)) matches++;
        if (matches == 0) return false;
        _targets.Add(target, now);
        _pending.RemoveAll(p => p.User == user && p.Guid == guid && p.Rotation == rotation && Same(p.Position, position));
        return matches == 1;
    }

    private static bool Same(Vector3 a, Vector3 b) => Vector3.DistanceSquared(a, b) <= 0.000001f;
    private static bool Finite(Vector3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);
}
