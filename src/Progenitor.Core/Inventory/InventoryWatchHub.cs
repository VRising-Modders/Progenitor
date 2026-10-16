using System;
using System.Collections.Generic;

namespace Progenitor.Inventory;

/// <summary>
/// The change detector behind <c>ctx.Inventory.Watch</c>: a fingerprint poll.
///
/// <para><b>Why a poll.</b> The game's own inventory utilities create
/// <c>InventoryChangedEvent</c> entities on their add, remove and move paths, but which system
/// consumes and destroys those entities, and whether every path that writes an inventory buffer
/// creates one, is not established. A missed event would leave a watcher asleep forever, so the
/// watch compares a content fingerprint (<see cref="InventoryMath.Fingerprint"/>) instead.</para>
///
/// <para><b>Cost.</b> Per watch and per interval (default 500 ms, minimum 100 ms): one
/// inventory buffer read and one pass over its slots (a chest has tens of slots). Nothing is
/// allocated per check. With no watches the frame hook costs one count compare. Writes made
/// through <c>ctx.Inventory</c> poke the watches on the containers they touched, so those are
/// reported on the next frame without waiting for the interval.</para>
///
/// <para><b>Change stamps.</b> Given a stamp function (<see cref="InventoryStamps"/> on the
/// server), a due check whose stamp equals the one taken at the last fingerprint skips the
/// fingerprint: equal stamps mean the slots did not change. Every
/// <see cref="FingerprintEvery"/>th due check fingerprints anyway, so even a write path the stamp
/// missed is reported within that many intervals. A poke always fingerprints.</para>
///
/// <para>Main thread only.</para>
/// </summary>
internal sealed class InventoryWatchHub
{
    /// <summary>A due check fingerprints at least this often, whatever the stamp says.</summary>
    internal const int FingerprintEvery = 8;

    /// <summary>Default check interval.</summary>
    internal const int DefaultIntervalMs = 500;

    /// <summary>Smallest accepted interval.</summary>
    internal const int MinIntervalMs = 100;

    /// <summary>Most watches the server keeps at once (all plugins together).</summary>
    internal const int MaxWatches = 1024;

    private readonly IInventoryBackend _backend;
    private readonly Action<string>? _logWarning;
    private readonly List<Watch> _watches = new();
    private readonly Func<InventoryRef, long>? _stamp;

    internal InventoryWatchHub(IInventoryBackend backend, Action<string>? logWarning = null, Func<InventoryRef, long>? stamp = null)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _logWarning = logWarning;
        _stamp = stamp;
    }

    /// <summary>Active watch count.</summary>
    internal int Count => _watches.Count;

    /// <summary>Checks performed (diagnostics and tests).</summary>
    internal long Checks { get; private set; }

    /// <summary>Due checks answered by an unchanged stamp, without a fingerprint.</summary>
    internal long Skipped { get; private set; }

    /// <summary>
    /// Start watching. The first fingerprint is taken now, so the first event is a real change.
    /// A container that cannot be read now still gets a watch; its first check reports
    /// <see cref="InventoryChangeKind.Gone"/>.
    /// </summary>
    internal IInventoryWatch Add(InventoryRef container, Action<InventoryChange> handler, TimeSpan? interval, long nowMs)
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        _watches.RemoveAll(static w => !w.Active);
        if (_watches.Count >= MaxWatches)
            throw new InvalidOperationException("ctx.Inventory.Watch: " + MaxWatches + " watches are active on this server; dispose some first.");
        int ms = interval.HasValue ? (int)Math.Min(int.MaxValue, Math.Max(MinIntervalMs, interval.Value.TotalMilliseconds)) : DefaultIntervalMs;
        var w = new Watch(this, container, handler, ms)
        {
            NextMs = nowMs + ms,
            Fingerprint = TryFingerprint(container, out ulong fp) ? fp : 0,
        };
        _watches.Add(w);
        return w;
    }

    /// <summary>Make every watch on <paramref name="container"/> check on the next tick.</summary>
    internal void Poke(InventoryRef container)
    {
        for (int i = 0; i < _watches.Count; i++)
        {
            var w = _watches[i];
            if (w.Container.OwnerIndex == container.OwnerIndex && w.Container.OwnerVersion == container.OwnerVersion
                && w.Container.Instance == container.Instance)
                w.Poke();
        }
    }

    /// <summary>One frame: check every due watch and raise its events.</summary>
    internal void Tick(long nowMs)
    {
        if (_watches.Count == 0) return;
        // Iterate over a snapshot count; handlers may add or dispose watches.
        for (int i = 0; i < _watches.Count; i++)
        {
            var w = _watches[i];
            if (!w.Active || nowMs < w.NextMs) continue;
            w.NextMs = nowMs + w.IntervalMs;
            Checks++;

            long stamp = StampOf(w.Container);
            if (stamp != 0 && stamp == w.Stamp && w.SkippedInRow < FingerprintEvery - 1)
            {
                w.SkippedInRow++;
                Skipped++;
                continue;
            }

            w.SkippedInRow = 0;
            w.Stamp = stamp;
            InventoryChange? change = null;
            if (!TryFingerprint(w.Container, out ulong fp))
            {
                change = new InventoryChange(w.Container, InventoryChangeKind.Gone, 0, w.Fingerprint);
                w.Active = false;
            }
            else if (fp != w.Fingerprint)
            {
                change = new InventoryChange(w.Container, InventoryChangeKind.Changed, fp, w.Fingerprint);
                w.Fingerprint = fp;
            }

            if (change.HasValue)
            {
                try { w.Handler(change.Value); }
                catch (Exception e) { _logWarning?.Invoke("Inventory: watch handler for " + w.Container + " threw: " + e.Message); }
            }
        }

        _watches.RemoveAll(static w => !w.Active);
    }

    /// <summary>Drop every watch (Core unload).</summary>
    internal void Clear()
    {
        for (int i = 0; i < _watches.Count; i++) _watches[i].Active = false;
        _watches.Clear();
    }

    /// <summary>The container's change stamp, or 0 (no stamp: fingerprint) without a stamp function or on a throw.</summary>
    private long StampOf(InventoryRef container)
    {
        if (_stamp == null) return 0;
        try { return _stamp(container); }
        catch { return 0; }
    }

    private bool TryFingerprint(InventoryRef container, out ulong fingerprint)
    {
        fingerprint = 0;
        if (!_backend.IsServer || container.IsNone || container.IsStaleIn(_backend.CurrentEpoch)) return false;
        return _backend.TryFingerprint(container, out fingerprint);
    }

    private sealed class Watch : IInventoryWatch
    {
        private readonly InventoryWatchHub _hub;

        internal Watch(InventoryWatchHub hub, InventoryRef container, Action<InventoryChange> handler, int intervalMs)
        {
            _hub = hub;
            Container = container;
            Handler = handler;
            IntervalMs = intervalMs;
        }

        public InventoryRef Container { get; }
        internal Action<InventoryChange> Handler { get; }
        internal int IntervalMs { get; }
        internal long NextMs { get; set; }
        internal ulong Fingerprint { get; set; }

        /// <summary>The stamp taken with the last fingerprint; 0 forces the next due check to fingerprint.</summary>
        internal long Stamp { get; set; }

        /// <summary>Due checks skipped since the last fingerprint.</summary>
        internal int SkippedInRow { get; set; }

        public bool Active { get; internal set; } = true;

        public void Poke()
        {
            if (!Active) return;
            NextMs = long.MinValue;
            Stamp = 0;
        }

        public void Dispose() => Active = false;
    }
}
