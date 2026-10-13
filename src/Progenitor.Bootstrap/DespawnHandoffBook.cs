using System.Collections.Generic;

namespace Progenitor.Bootstrap;

/// <summary>
/// Unit despawns a Core generation started but could not finish, held for the next generation.
/// Game-free and process-lifetime.
///
/// <para><b>Why it exists.</b> Core uninstall (a Core swap included) disposes every live
/// <c>ctx.Units</c> handle. Each dispose sweeps the unit's buffs and posts the rest of the despawn
/// as Core-owned delayed work, which <c>CoreLoader.DropCoreWork</c> then drops, so the unit stayed
/// in the world without its buffs (live, 2026-09-28). Core hands those units here from
/// <c>Units.Uninstall</c> and the next generation takes them and despawns them again.</para>
///
/// <para><b>Identity.</b> A row is an entity's <c>index</c>, <c>version</c> and <c>PrefabGUID</c>
/// hash. A raw id is only meaningful in the world it came from, so each hand-off is stamped with
/// the server world's <c>World.SequenceNumber</c> and with <see cref="WorldLoads"/>, a count of
/// save loads Bootstrap sees at <c>LoadPersistenceSystemV2.DeserializeLoadFile</c>. A load rebuilds
/// entities inside the same <c>World</c>, so the sequence number alone cannot see it. A row whose
/// stamp does not match both at <see cref="Take"/> is dropped, never returned. The taker still
/// checks existence and the <c>PrefabGUID</c> before acting.</para>
/// </summary>
internal sealed class DespawnHandoffBook
{
    /// <summary>Rows kept at most; a hand-off beyond it is dropped and counted.</summary>
    public const int MaxRows = 4096;

    /// <summary>Ints per row in the arrays exchanged with Core: index, version, PrefabGUID hash.</summary>
    public const int Stride = 3;

    private readonly struct Row
    {
        internal Row(int index, int version, int prefab, ulong worldSequence, int worldLoads)
        {
            Index = index;
            Version = version;
            Prefab = prefab;
            WorldSequence = worldSequence;
            WorldLoads = worldLoads;
        }

        internal int Index { get; }
        internal int Version { get; }
        internal int Prefab { get; }
        internal ulong WorldSequence { get; }
        internal int WorldLoads { get; }
    }

    private readonly object _gate = new();
    private readonly List<Row> _rows = new();
    private int _worldLoads;

    /// <summary>Save loads seen so far in this process.</summary>
    public int WorldLoads
    {
        get { lock (_gate) return _worldLoads; }
    }

    /// <summary>Rows waiting for a taker.</summary>
    public int Count
    {
        get { lock (_gate) return _rows.Count; }
    }

    /// <summary>A save load started: every row recorded before it names another entity now.</summary>
    public void NoteWorldLoad()
    {
        lock (_gate) _worldLoads++;
    }

    /// <summary>
    /// Record <paramref name="rows"/> (<see cref="Stride"/> ints each) from the world with
    /// <paramref name="worldSequence"/>. A null id (index and version 0) and a row already held
    /// are skipped; rows beyond <see cref="MaxRows"/> are dropped. Returns the rows accepted.
    /// </summary>
    public int HandOff(int[]? rows, ulong worldSequence)
    {
        if (rows == null) return 0;
        int accepted = 0;
        lock (_gate)
        {
            for (int i = 0; i + Stride <= rows.Length; i += Stride)
            {
                int index = rows[i], version = rows[i + 1], prefab = rows[i + 2];
                if (index == 0 && version == 0) continue;
                if (Holds(index, version, worldSequence)) continue;
                if (_rows.Count >= MaxRows) break;
                _rows.Add(new Row(index, version, prefab, worldSequence, _worldLoads));
                accepted++;
            }
        }

        return accepted;
    }

    /// <summary>
    /// Remove and return every row recorded in the world with <paramref name="worldSequence"/>
    /// and no save load since (<see cref="Stride"/> ints each). All other rows are dropped too
    /// and counted in <paramref name="stale"/>: after a take the book is empty.
    /// </summary>
    public int[] Take(ulong worldSequence, out int stale)
    {
        lock (_gate)
        {
            var result = new List<int>(_rows.Count * Stride);
            stale = 0;
            foreach (var row in _rows)
            {
                if (row.WorldSequence != worldSequence || row.WorldLoads != _worldLoads)
                {
                    stale++;
                    continue;
                }

                result.Add(row.Index);
                result.Add(row.Version);
                result.Add(row.Prefab);
            }

            _rows.Clear();
            return result.ToArray();
        }
    }

    private bool Holds(int index, int version, ulong worldSequence)
    {
        foreach (var row in _rows)
        {
            if (row.Index == index && row.Version == version && row.WorldSequence == worldSequence
                && row.WorldLoads == _worldLoads)
                return true;
        }

        return false;
    }
}
