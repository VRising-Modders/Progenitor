using System;
using System.Collections.Generic;

namespace Progenitor.Castles;

/// <summary>
/// Which placed models with an inventory belong to which castle heart, kept between reads.
///
/// <para>Before, <c>ICastleHandle.Containers()</c> and <c>Stations()</c> each listed every castle
/// building on the server (every tile model with a heart connection) and read each one's heart
/// connection, to keep the few with an inventory on one plot. On a save whose biggest plot has 1,267
/// buildings, one plot cost about 100 ms per Containers() + Stations() pair. Now one server-wide scan lists only the
/// models that carry an inventory, groups them by heart, and is reused until the stamp moves (the
/// number of inventory-bearing castle models changed: one was built or destroyed), the world epoch
/// moves, or <see cref="MaxAgeMs"/> passes. A build and a destroy between two reads leave the count
/// unchanged; that change is found by the age rescan, at most <see cref="MaxAgeMs"/> late. The rows
/// themselves (names, slot counts, station kind) are still read live on every call; only the
/// membership is kept.</para>
///
/// <para><see cref="RevisionOf"/> changes when a heart's membership changes: a container or station
/// on that plot was built or destroyed (or the world was loaded). Values are seeded from the clock
/// when the index is created, so they keep growing across a Core swap or a module reinstall within
/// one process. They are valid within one process only and must not be persisted.</para>
///
/// <para>Main thread only, like every ECS read.</para>
/// </summary>
internal sealed class CastleInventoryIndex
{
    /// <summary>
    /// Longest a membership is trusted without a rescan, even when the stamp says nothing changed:
    /// a safety net for a stamp that misses a change. A rescan that finds the same members does not
    /// move any revision.
    /// </summary>
    internal const long MaxAgeMs = 10_000;

    private readonly Func<long?> _stamp;
    private readonly Func<IEnumerable<(CastleId Model, CastleId Heart)>> _scan;
    private readonly Func<long> _nowMs;

    private Dictionary<CastleId, CastleId[]> _byHeart = new();
    private readonly Dictionary<CastleId, long> _revisions = new();
    private long _lastRevision = DateTime.UtcNow.Ticks;
    private bool _scanned;
    private long? _scannedStamp;
    private long _scannedAtMs;
    private int _epoch;

    /// <summary>How many server-wide scans ran.</summary>
    internal int Scans { get; private set; }

    /// <summary>Reads answered from the kept membership, without a scan.</summary>
    internal long Hits { get; private set; }

    /// <summary>Scans caused by a stamp move, by <see cref="MaxAgeMs"/>, by an unreadable stamp, and by a world load or the first read.</summary>
    internal int StampScans { get; private set; }
    internal int AgeScans { get; private set; }
    internal int UnreadableScans { get; private set; }
    internal int FirstScans { get; private set; }

    /// <summary>The index the live runtime uses, for the development bridge's status read.</summary>
    internal static CastleInventoryIndex? Live { get; set; }

    /// <param name="stamp">A cheap value that moves when the inventory-bearing castle models change; null when unreadable (then every read rescans).</param>
    /// <param name="scan">Every inventory-bearing castle model and its heart.</param>
    /// <param name="nowMs">Monotonic milliseconds.</param>
    internal CastleInventoryIndex(Func<long?> stamp, Func<IEnumerable<(CastleId Model, CastleId Heart)>> scan, Func<long>? nowMs = null)
    {
        _stamp = stamp ?? throw new ArgumentNullException(nameof(stamp));
        _scan = scan ?? throw new ArgumentNullException(nameof(scan));
        _nowMs = nowMs ?? (() => Environment.TickCount64);
    }

    /// <summary>The inventory-bearing models connected to <paramref name="heart"/>, sorted by index then version.</summary>
    internal IReadOnlyList<CastleId> Of(CastleId heart, int epoch)
    {
        Validate(epoch);
        return _byHeart.TryGetValue(heart, out var models) ? models : Array.Empty<CastleId>();
    }

    /// <summary>
    /// The membership revision of <paramref name="heart"/>: changes when a container or station is
    /// built or destroyed on that plot. Never 0 once read.
    /// </summary>
    internal long RevisionOf(CastleId heart, int epoch)
    {
        Validate(epoch);
        if (_revisions.TryGetValue(heart, out long revision)) return revision;
        // A heart with nothing on it yet: give it a revision now, so its first container moves it.
        revision = ++_lastRevision;
        _revisions[heart] = revision;
        return revision;
    }

    /// <summary>Forget everything (a world teardown); revisions handed out later are still new.</summary>
    internal void Clear()
    {
        _byHeart = new Dictionary<CastleId, CastleId[]>();
        _revisions.Clear();
        _scanned = false;
        _scannedStamp = null;
    }

    private void Validate(int epoch)
    {
        if (_scanned && epoch != _epoch) Clear();
        long now = _nowMs();
        long? stamp;
        try { stamp = _stamp(); }
        catch { stamp = null; }
        if (_scanned && stamp.HasValue && _scannedStamp == stamp && now - _scannedAtMs < MaxAgeMs)
        {
            Hits++;
            return;
        }
        if (!_scanned) FirstScans++;
        else if (!stamp.HasValue) UnreadableScans++;
        else if (_scannedStamp != stamp) StampScans++;
        else AgeScans++;

        Dictionary<CastleId, List<CastleId>> found = new();
        foreach (var (model, heart) in _scan())
        {
            if (heart.IsNone || model.IsNone) continue;
            if (!found.TryGetValue(heart, out var list)) found[heart] = list = new List<CastleId>();
            list.Add(model);
        }

        var next = new Dictionary<CastleId, CastleId[]>(found.Count);
        foreach (var pair in found)
        {
            var models = pair.Value.ToArray();
            Array.Sort(models, Compare);
            next[pair.Key] = models;
        }

        // A heart whose membership changed, appeared or emptied gets a new revision.
        foreach (var pair in next)
            if (!_byHeart.TryGetValue(pair.Key, out var old) || !Same(old, pair.Value))
                Bump(pair.Key);
        foreach (var pair in _byHeart)
            if (!next.ContainsKey(pair.Key))
                Bump(pair.Key);

        _byHeart = next;
        _scanned = true;
        _scannedStamp = stamp;
        _scannedAtMs = now;
        _epoch = epoch;
        Scans++;
    }

    /// <summary>Counters for a status read.</summary>
    internal IReadOnlyDictionary<string, object?> Describe() => new Dictionary<string, object?>
    {
        ["scans"] = Scans,
        ["hits"] = Hits,
        ["stampScans"] = StampScans,
        ["ageScans"] = AgeScans,
        ["unreadableScans"] = UnreadableScans,
        ["firstScans"] = FirstScans,
        ["hearts"] = _byHeart.Count,
        ["stamp"] = _scannedStamp,
        ["maxAgeMs"] = MaxAgeMs,
    };

    private void Bump(CastleId heart)
    {
        // Only hearts someone has asked about carry a revision; the rest get one on first read.
        if (_revisions.ContainsKey(heart)) _revisions[heart] = ++_lastRevision;
    }

    private static int Compare(CastleId a, CastleId b) =>
        a.Index != b.Index ? a.Index.CompareTo(b.Index) : a.Version.CompareTo(b.Version);

    private static bool Same(CastleId[] a, CastleId[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (!a[i].Equals(b[i])) return false;
        return true;
    }
}

/// <summary>
/// Development bridge read of the castle inventory index (the <c>castles</c> op, <c>what: "index"</c>):
/// scans, reads answered without a scan, and why each scan ran. Not a plugin API.
/// </summary>
public static class CastleInventoryIndexStatus
{
    /// <summary>The counters, or a note when no server read has used the index yet.</summary>
    public static IReadOnlyDictionary<string, object?> Describe() =>
        CastleInventoryIndex.Live?.Describe()
        ?? new Dictionary<string, object?> { ["note"] = "no Containers()/Stations()/StructureRevision read yet in this Core generation" };
}
