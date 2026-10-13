using System;
using System.Collections.Generic;

namespace Progenitor.Config;

/// <summary>
/// Refcounts a shared resource by key. Each <see cref="Acquire"/> returns a distinct
/// <see cref="IDisposable"/>; the create callback runs on the first lease for a key and the
/// destroy callback runs when the last lease is disposed (or when <see cref="Drain"/> tears
/// everything down). A second acquire of the same key never returns the first lease, so
/// disposing one caller cannot stop the watch for another.
/// </summary>
internal sealed class LeaseTable<TKey, TResource>
    where TKey : notnull
    where TResource : class
{
    private readonly object _gate = new();
    private readonly Dictionary<TKey, Entry> _entries;

    public LeaseTable() : this(null) { }

    public LeaseTable(IEqualityComparer<TKey>? comparer)
    {
        _entries = comparer == null
            ? new Dictionary<TKey, Entry>()
            : new Dictionary<TKey, Entry>(comparer);
    }

    /// <summary>How many live leases currently hold <paramref name="key"/>. Zero if none.</summary>
    public int RefCount(TKey key)
    {
        if (key == null) throw new ArgumentNullException(nameof(key));
        lock (_gate)
            return _entries.TryGetValue(key, out var entry) && !entry.Stopped ? entry.Count : 0;
    }

    /// <summary>How many keys currently have at least one live lease.</summary>
    public int KeyCount
    {
        get { lock (_gate) return _entries.Count; }
    }

    /// <summary>
    /// Take a lease on <paramref name="key"/>. The first caller for a key runs
    /// <paramref name="create"/>; the last dispose (or <see cref="Drain"/>) runs
    /// <paramref name="destroy"/>. If <paramref name="create"/> throws, the table is unchanged.
    /// </summary>
    public IDisposable Acquire(TKey key, Func<TResource> create, Action<TResource> destroy)
    {
        if (key == null) throw new ArgumentNullException(nameof(key));
        if (create == null) throw new ArgumentNullException(nameof(create));
        if (destroy == null) throw new ArgumentNullException(nameof(destroy));

        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var existing) && !existing.Stopped)
            {
                existing.Count++;
                return new Lease(this, key, existing);
            }

            var resource = create();
            if (resource == null)
                throw new InvalidOperationException("LeaseTable create callback returned null.");

            var entry = new Entry { Resource = resource, Destroy = destroy, Count = 1 };
            _entries[key] = entry;
            return new Lease(this, key, entry);
        }
    }

    /// <summary>
    /// Drop every key, mark remaining leases as no-ops, and run each key's destroy callback
    /// outside the lock. Used by process-lifetime Uninstall.
    /// </summary>
    public void Drain()
    {
        Entry[] snapshot;
        lock (_gate)
        {
            snapshot = new Entry[_entries.Count];
            _entries.Values.CopyTo(snapshot, 0);
            _entries.Clear();
            for (int i = 0; i < snapshot.Length; i++)
                snapshot[i].Stopped = true;
        }
        for (int i = 0; i < snapshot.Length; i++)
        {
            try { snapshot[i].Destroy(snapshot[i].Resource); }
            catch { /* caller logs inside destroy; do not skip the rest */ }
        }
    }

    private void Release(TKey key, Entry expected)
    {
        TResource? doomed = null;
        Action<TResource>? destroy = null;
        lock (_gate)
        {
            if (expected.Stopped) return;
            if (!_entries.TryGetValue(key, out var current) || !ReferenceEquals(current, expected))
                return;
            expected.Count--;
            if (expected.Count > 0) return;
            _entries.Remove(key);
            expected.Stopped = true;
            doomed = expected.Resource;
            destroy = expected.Destroy;
        }
        if (doomed != null && destroy != null)
            destroy(doomed);
    }

    private sealed class Entry
    {
        public TResource Resource = null!;
        public Action<TResource> Destroy = null!;
        public int Count;
        public bool Stopped;
    }

    private sealed class Lease : IDisposable
    {
        private LeaseTable<TKey, TResource>? _table;
        private readonly TKey _key;
        private readonly Entry _entry;

        public Lease(LeaseTable<TKey, TResource> table, TKey key, Entry entry)
        {
            _table = table;
            _key = key;
            _entry = entry;
        }

        public void Dispose()
        {
            var table = System.Threading.Interlocked.Exchange(ref _table, null);
            table?.Release(_key, _entry);
        }
    }
}
