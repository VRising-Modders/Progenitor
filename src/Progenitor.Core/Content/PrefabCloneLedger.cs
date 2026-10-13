using System;
using System.Collections.Generic;

namespace Progenitor.Content;

/// <summary>
/// What this process cloned into, and appended to <c>ItemKeyLookup</c> of, <b>one</b> world:
/// cloned GUIDs, appended GUIDs with their row index, and the last applied <c>CloneOf</c> hash
/// and <see cref="PrefabKind"/> per clone. Game-free (hashes only) so the suite drives the
/// same instance <see cref="PrefabApplicator"/> uses.
///
/// <para><b>World-scoped.</b> Every row describes an entity or a key-table row of the world it
/// was recorded in. A Core swap keeps the world, so the rows cross it through Bootstrap
/// <c>ContentMemory</c>. A new client world does not: the next join builds a fresh
/// <c>ItemKeyLookup</c> and fresh prefab entities, and <see cref="ForgetWorld"/> must run before
/// anything applies there. Before that reset existed, a client's own item recorded at row 1083
/// in the first join failed every later join with
/// <c>key index mismatch saved=1083 now=-1</c> and was dropped (live client logs 2026-09-20 and
/// 2026-09-27).</para>
/// </summary>
internal sealed class PrefabCloneLedger
{
    private readonly object _gate = new();
    private readonly HashSet<int> _cloned = new();
    /// <summary>
    /// GUIDs this process actually appended to ItemKeyLookup (or confirmed as ours on a
    /// successful apply of an already-present row from a prior session). Unregister tombstones
    /// only these; a vanilla row that already contained the hash is never recorded.
    /// </summary>
    private readonly HashSet<int> _appended = new();
    /// <summary>ItemKeyLookup list index for each appended GUID. −1 is never stored.</summary>
    private readonly Dictionary<int, int> _appendedIndex = new();
    /// <summary>
    /// Last resolved <c>CloneOf</c> hash per cloned GUID. Soft unapply keeps the row so a
    /// hot-reload of the same id can detect a source change.
    /// </summary>
    private readonly Dictionary<int, int> _appliedSource = new();
    /// <summary>Last applied <see cref="PrefabKind"/> per cloned GUID.</summary>
    private readonly Dictionary<int, PrefabKind> _appliedKind = new();

    public bool WasCloned(int guidHash)
    {
        lock (_gate) return _cloned.Contains(guidHash);
    }

    public bool WasAppended(int guidHash)
    {
        lock (_gate) return _appended.Contains(guidHash);
    }

    public int AppendedCount
    {
        get { lock (_gate) return _appended.Count; }
    }

    /// <summary>Rows of every kind; zero right after <see cref="ForgetWorld"/>.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
                return _cloned.Count + _appended.Count + _appendedIndex.Count + _appliedSource.Count + _appliedKind.Count;
        }
    }

    public void RememberClone(int guidHash)
    {
        lock (_gate) _cloned.Add(guidHash);
    }

    public void RememberIdentity(int guidHash, int sourceHash, PrefabKind kind)
    {
        lock (_gate)
        {
            _appliedSource[guidHash] = sourceHash;
            _appliedKind[guidHash] = kind;
        }
    }

    public bool TryGetIdentity(int guidHash, out int sourceHash, out PrefabKind kind, out bool hasKind)
    {
        lock (_gate)
        {
            bool hasSource = _appliedSource.TryGetValue(guidHash, out sourceHash);
            hasKind = _appliedKind.TryGetValue(guidHash, out kind);
            if (hasSource || hasKind)
            {
                if (!hasKind) kind = PrefabKind.Item;
                if (!hasSource) sourceHash = 0;
                return true;
            }
        }
        sourceHash = 0;
        kind = PrefabKind.Item;
        hasKind = false;
        return false;
    }

    public void RememberAppend(int guidHash, int index)
    {
        lock (_gate)
        {
            _appended.Add(guidHash);
            if (index >= 0) _appendedIndex[guidHash] = index;
        }
    }

    public bool TryGetAppendedIndex(int guidHash, out int index)
    {
        lock (_gate) return _appendedIndex.TryGetValue(guidHash, out index);
    }

    public void ForgetClone(int guidHash)
    {
        lock (_gate)
        {
            _cloned.Remove(guidHash);
            _appliedSource.Remove(guidHash);
            _appliedKind.Remove(guidHash);
        }
    }

    /// <summary>
    /// The world these rows describe is gone (a new client world). Drops every row and returns
    /// how many GUIDs were cloned or appended, for the log line. Never call it for a Core swap of
    /// a surviving world: the rows are what lets the next generation reclaim its own clones.
    /// </summary>
    public int ForgetWorld()
    {
        lock (_gate)
        {
            var owned = new HashSet<int>(_cloned);
            owned.UnionWith(_appended);
            _cloned.Clear();
            _appended.Clear();
            _appendedIndex.Clear();
            _appliedSource.Clear();
            _appliedKind.Clear();
            return owned.Count;
        }
    }

    public void ExportSources(out int[] cloneGuids, out int[] sourceHashes)
    {
        lock (_gate)
        {
            int n = _appliedSource.Count;
            if (n == 0)
            {
                cloneGuids = Array.Empty<int>();
                sourceHashes = Array.Empty<int>();
                return;
            }
            cloneGuids = new int[n];
            sourceHashes = new int[n];
            int i = 0;
            foreach (var kv in _appliedSource)
            {
                cloneGuids[i] = kv.Key;
                sourceHashes[i] = kv.Value;
                i++;
            }
        }
    }

    /// <summary>Already-validated parallel arrays (see <c>PrefabApplyBookkeeping.CopyCloneSources</c>).</summary>
    public void ImportSources(int[] guids, int[] sources)
    {
        lock (_gate)
        {
            for (int i = 0; i < guids.Length && i < sources.Length; i++)
            {
                if (sources[i] == 0) continue;
                _appliedSource[guids[i]] = sources[i];
            }
        }
    }

    public void ExportClonesAndKeys(out int[] cloned, out int[] appendedGuids, out int[] appendedIndices)
    {
        lock (_gate)
        {
            cloned = ToHashArray(_cloned);
            int n = _appended.Count;
            appendedGuids = new int[n];
            appendedIndices = new int[n];
            int i = 0;
            foreach (var hash in _appended)
            {
                appendedGuids[i] = hash;
                appendedIndices[i] = _appendedIndex.TryGetValue(hash, out var idx) ? idx : -1;
                i++;
            }
        }
    }

    public void ImportClonesAndKeys(int[]? cloned, int[]? appendedGuids, int[]? appendedIndices)
    {
        lock (_gate)
        {
            if (cloned != null)
            {
                for (int i = 0; i < cloned.Length; i++)
                    _cloned.Add(cloned[i]);
            }
            if (appendedGuids == null) return;
            for (int i = 0; i < appendedGuids.Length; i++)
            {
                int hash = appendedGuids[i];
                _appended.Add(hash);
                int idx = appendedIndices != null && i < appendedIndices.Length ? appendedIndices[i] : -1;
                if (idx >= 0) _appendedIndex[hash] = idx;
            }
        }
    }

    /// <summary>
    /// Same rule as Bootstrap <c>ContentMemory.KeyRowStillHolds</c>: a saved slot is still
    /// ours only when the ECS table reports that index for the GUID.
    /// </summary>
    public static bool KeyRowStillHolds(int savedIndex, int currentIndex) =>
        savedIndex >= 0 && currentIndex == savedIndex;

    private static int[] ToHashArray(HashSet<int> set)
    {
        if (set.Count == 0) return Array.Empty<int>();
        var copy = new int[set.Count];
        set.CopyTo(copy);
        return copy;
    }
}
