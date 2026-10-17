using System;
using System.Collections.Generic;
using Progenitor.Ecs;

namespace Progenitor.Inventory;

/// <summary>
/// What <see cref="InventoryStamps"/> reads from the game. The game half
/// (<c>UnityInventoryStampSource</c>) answers with raw chunk change versions; tests answer with a
/// fake world.
/// </summary>
internal interface IInventoryStampSource
{
    /// <summary>The world-load epoch now.</summary>
    int CurrentEpoch { get; }

    /// <summary>Start a batch: bind to the world. False when there is nothing to read (every stamp is then fresh).</summary>
    bool BeginBatch();

    /// <summary>
    /// The store's global system version and an identity of the store itself (a new world is a new
    /// store with its own clock). False when unreadable.
    /// </summary>
    bool TryReadClock(out uint clock, out long store);

    /// <summary>The slow path: the entity that holds the slots of <paramref name="container"/>.</summary>
    bool TryResolve(InventoryRef container, out int inventoryIndex, out int inventoryVersion);

    /// <summary>The <c>InventoryBuffer</c> change version on the chunk of that entity.</summary>
    bool TryReadSlotsVersion(int index, int version, out ChunkVersionRead read);

    /// <summary>The <c>InventoryInstanceElement</c> change version on the chunk of an owner with external inventories.</summary>
    bool TryReadInstancesVersion(int index, int version, out ChunkVersionRead read);

    /// <summary>
    /// Complete in-flight jobs that write <c>InventoryBuffer</c> (or <c>InventoryInstanceElement</c>
    /// when <paramref name="instances"/>), through the entity named, which a raw read has just
    /// confirmed alive with that buffer. Once per batch. False (or a throw) when the completion did
    /// not happen; the batch's answers are then fresh.
    /// </summary>
    bool CompleteWriters(int index, int version, bool instances);

    /// <summary>A hash of every field of every slot row (<see cref="InventoryStamps.RowHash"/>). False when unreadable.</summary>
    bool TryContentHash(int index, int version, out ulong hash);
}

/// <summary>
/// The logic behind <c>ctx.Inventory.ChangeStamp</c>, <c>ChangeStamps</c>, <c>Clock</c> and
/// <c>AnyChangedSince</c>. Game-free and unit-tested; main thread only.
///
/// <para><b>The stamp.</b> A 64-bit mix of the world epoch, the entity that holds the slots, its
/// chunk, its archetype and the chunk's <c>InventoryBuffer</c> change version extended to 64 bits;
/// for an owner whose inventories are separate entities (a player, a servant, a station) also the
/// owner chunk's <c>InventoryInstanceElement</c> version; and the audit override below. Equal stamps
/// mean the slots did not change. Unequal stamps mean they may have: the version is per chunk, so a
/// write to any inventory sharing the chunk, a read-write buffer access that wrote nothing, or a
/// move to another chunk all change it. Any link that cannot be read yields a fresh value, never
/// the previous one, so a failure reads as a change.</para>
///
/// <para><b>Identity cache.</b> The owner-to-inventory resolution (several game calls) is done once
/// per reference and kept while the cheap raw reads agree with it: the inventory entity is alive and
/// still has <c>InventoryBuffer</c>, and for an external owner its <c>InventoryInstanceElement</c>
/// chunk and version have not moved. Anything else re-resolves. Entries unused for
/// <see cref="EvictAfterFrames"/> frames are dropped.</para>
///
/// <para><b>Clock and <see cref="AnyChangedSince"/>.</b> <see cref="Clock"/> is the store's global
/// system version extended to 64 bits (<see cref="ChunkVersionMath"/>), sampled every frame so the
/// extension never misses a wrap, rebased two eras ahead when the store changes, and carried across
/// Core reloads by the runtime. <c>AnyChangedSince(refs, since)</c> is true when any version read is
/// at or after <c>since</c> (a write in the same system step as the <see cref="Clock"/> read carries
/// the same version), when a reference's resolved identity changed at or after it, when the audit
/// flagged it, when a read fails, or when <c>since</c> is ahead of the clock.</para>
///
/// <para><b>Audit.</b> While a stamp user other than the watch hub has called lately (<see cref="HasUser"/>),
/// each frame <see cref="AuditPerFrame"/> recently used references are re-read
/// with a raw-row content hash. A hash that changed while the stamp did not is a miss (a write path
/// that did not bump the version): it is counted in <see cref="Misses"/>, logged once, and folded
/// into that reference's stamp so the consumer still sees the change.</para>
/// </summary>
internal sealed class InventoryStamps
{
    /// <summary>References re-hashed per frame by the audit.</summary>
    internal const int AuditPerFrame = 4;

    /// <summary>An entry not stamped for this many frames is dropped.</summary>
    internal const int EvictAfterFrames = 3600;

    /// <summary>Entries kept at most; past that the cache starts over (every stamp then reads as changed once).</summary>
    internal const int MaxEntries = 8192;

    private const ulong FreshTag = 0x46524553485354UL; // "FRESHST"

    private readonly IInventoryStampSource _source;
    private readonly Action<string>? _logError;
    private readonly Dictionary<InventoryRef, Entry> _entries = new();
    private readonly List<Entry> _auditOrder = new();
    private readonly ulong _freshSeed;
    private ulong _freshCounter;
    private int _auditCursor;
    private long _frame;
    private int _entriesEpoch = int.MinValue;
    private long _lastUserFrame = long.MinValue;
    private long _lastActiveFrame = long.MinValue;
    private bool _resumePending;

    // Clock state: the 64-bit value, the 32-bit sample it was last advanced to, the store it reads.
    private long _clock64;
    private uint _clock32;
    private long _clockStore;
    private bool _clockSeen;

    // Per batch.
    private bool _batchBound;
    private bool _slotsCompleted;
    private bool _instancesCompleted;
    private bool _batchFailed;
    private bool _missLogged;

    internal InventoryStamps(IInventoryStampSource source, Action<string>? logError = null, ulong? freshSeed = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _logError = logError;
        // A fresh stamp must never equal one a previous Core generation handed out, so the
        // sequence starts at a random point.
        _freshSeed = freshSeed ?? (ulong)Random.Shared.NextInt64() ^ ((ulong)Environment.TickCount64 << 17);
    }

    // ---- counters (dev bridge, tests) ----------------------------------------------------------

    /// <summary>Audit checks where the content hash changed but the stamp did not.</summary>
    internal long Misses { get; private set; }

    /// <summary>Audit checks performed.</summary>
    internal long Audits { get; private set; }

    /// <summary>Stamps handed out as fresh values because a read failed.</summary>
    internal long FreshStamps { get; private set; }

    /// <summary>Slow-path resolutions.</summary>
    internal long Resolves { get; private set; }

    /// <summary>Batches whose job completion failed (their answers were fresh).</summary>
    internal long CompletionFailures { get; private set; }

    /// <summary>
    /// Whether a stamp user other than the watch hub (a plugin or the dev bridge) called within
    /// the last <see cref="EvictAfterFrames"/> frames; the audit runs only then.
    /// </summary>
    internal bool HasUser => _lastUserFrame != long.MinValue && _frame - _lastUserFrame <= EvictAfterFrames;

    /// <summary>Cached references.</summary>
    internal int Entries => _entries.Count;

    // ---- clock ---------------------------------------------------------------------------------

    /// <summary>The 64-bit clock now (read fresh; the last value when unreadable, 0 before the first read).</summary>
    internal long Clock
    {
        get
        {
            _lastActiveFrame = _frame;
            SampleClock();
            return _clock64;
        }
    }

    /// <summary>The clock state to carry across a Core reload.</summary>
    internal (long Clock64, uint Clock32, long Store, bool Seen) ClockState => (_clock64, _clock32, _clockStore, _clockSeen);

    /// <summary>Continue a clock a previous Core generation left (same process).</summary>
    internal void SeedClock(long clock64, uint clock32, long store)
    {
        _clock64 = clock64;
        _clock32 = clock32;
        _clockStore = store;
        _clockSeen = true;
        // The extension must stay congruent with the sample; repair a state that is not.
        if ((uint)_clock64 != _clock32) _clock64 = ChunkVersionMath.Advance(_clock64, _clock32);
        // Time passed since that generation last sampled: the first sample here is a resume.
        _resumePending = true;
    }

    /// <summary>The clock state as one process-slot string (<c>clock64,clock32,store</c>); empty before the first read.</summary>
    internal string EncodeClock() =>
        _clockSeen
            ? _clock64.ToString(System.Globalization.CultureInfo.InvariantCulture) + ","
              + _clock32.ToString(System.Globalization.CultureInfo.InvariantCulture) + ","
              + _clockStore.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : "";

    /// <summary>Seed from <see cref="EncodeClock"/>'s string; false (nothing seeded) when it does not parse.</summary>
    internal bool TrySeedClock(string? encoded)
    {
        var parts = (encoded ?? "").Split(',');
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        if (parts.Length != 3
            || !long.TryParse(parts[0], System.Globalization.NumberStyles.Integer, ci, out long c64)
            || !uint.TryParse(parts[1], System.Globalization.NumberStyles.None, ci, out uint c32)
            || !long.TryParse(parts[2], System.Globalization.NumberStyles.Integer, ci, out long store)
            || c64 < 0)
            return false;
        SeedClock(c64, c32, store);
        return true;
    }

    private void SampleClock()
    {
        if (!_batchBound && !_source.BeginBatch()) return;
        _batchBound = true;
        if (!_source.TryReadClock(out uint sample, out long store)) return;
        if (!_clockSeen)
        {
            _clock64 = sample;
            _clockSeen = true;
        }
        else if (_resumePending || store != _clockStore)
        {
            // A new store, or the first sample after the idle window or a Core reload: the clock may
            // have run any distance (past a 32-bit wrap) unsampled, so jump two eras and forget every
            // cached reference. Everything then reads as changed once, a false positive.
            _clock64 = ChunkVersionMath.Rebase(_clock64, sample);
            Clear();
            Resumes++;
        }
        else
        {
            _clock64 = ChunkVersionMath.Advance(_clock64, sample);
        }

        _clock32 = sample;
        _clockStore = store;
        _resumePending = false;
    }

    /// <summary>
    /// Whether anything (a plugin, the dev bridge, a watch with the stamp setting on) stamped or read
    /// the clock within the last <see cref="EvictAfterFrames"/> frames. Only then does the frame tick
    /// read anything: with no stamp user the service does no raw read at all.
    /// </summary>
    internal bool Active => _lastActiveFrame != long.MinValue && _frame - _lastActiveFrame <= EvictAfterFrames;

    /// <summary>Times the clock resumed after the idle window, a reload or a store change (tests, bridge).</summary>
    internal long Resumes { get; private set; }

    // ---- stamps --------------------------------------------------------------------------------

    /// <summary>One stamp. Never 0.</summary>
    internal long Stamp(InventoryRef container, bool user = true)
    {
        Begin(user);
        try { return StampOne(container); }
        finally { End(); }
    }

    /// <summary>Stamps of <paramref name="containers"/> into <paramref name="into"/>, one batch.</summary>
    internal void Stamps(IReadOnlyList<InventoryRef> containers, Span<long> into, bool user = true)
    {
        if (containers == null) throw new ArgumentNullException(nameof(containers));
        if (into.Length < containers.Count)
            throw new ArgumentException("the span holds " + into.Length + " stamps; " + containers.Count + " are needed", nameof(into));
        Begin(user);
        try
        {
            for (int i = 0; i < containers.Count; i++) into[i] = StampOne(containers[i]);
        }
        finally { End(); }
    }

    /// <summary>True when any of <paramref name="containers"/> may have changed at or after <paramref name="since"/>.</summary>
    internal bool AnyChangedSince(IReadOnlyList<InventoryRef> containers, long since, bool user = true)
    {
        if (containers == null) throw new ArgumentNullException(nameof(containers));
        Begin(user);
        try
        {
            bool any = !_clockSeen || since > _clock64;
            // Every reference is refreshed even after the answer is known, so references seen for
            // the first time are all cached by one call and the next call can say false.
            for (int i = 0; i < containers.Count; i++)
            {
                var entry = Refresh(containers[i]);
                if (entry == null
                    || entry.IdentityAt >= since || entry.SlotsV64 >= since || entry.OverrideAt >= since
                    || (entry.External && entry.InstancesV64 >= since))
                    any = true;
            }

            return any;
        }
        finally { End(); }
    }

    /// <summary>
    /// One frame: advance the clock (so the 64-bit extension sees every wrap), drop entries nobody
    /// stamped lately, and audit a few.
    /// </summary>
    internal void Tick()
    {
        _frame++;
        if (!Active)
        {
            // Idle: no raw read at all. The first idle frame after use forgets the cache, and the
            // next stamp call resumes the clock with a rebase.
            if (_lastActiveFrame != long.MinValue && !_resumePending)
            {
                Clear();
                _resumePending = true;
            }
            return;
        }

        Begin(activity: false);
        try
        {
            if (!_clockSeen) return;
            Evict();
            if (HasUser) Audit();
        }
        finally { End(); }
    }

    /// <summary>Forget every reference (Core unload, tests).</summary>
    internal void Clear()
    {
        _entries.Clear();
        _auditOrder.Clear();
        _auditCursor = 0;
    }

    private void Begin(bool user = false, bool activity = true)
    {
        _batchBound = false;
        _slotsCompleted = false;
        _instancesCompleted = false;
        _batchFailed = false;
        if (user) _lastUserFrame = _frame;
        if (activity) _lastActiveFrame = _frame;
        SampleClock();
        int epoch = _source.CurrentEpoch;
        if (epoch != _entriesEpoch)
        {
            Clear();
            _entriesEpoch = epoch;
        }
    }

    private void End() => _batchBound = false;

    private long StampOne(InventoryRef container)
    {
        var entry = Refresh(container);
        return entry == null ? Fresh() : Compose(entry);
    }

    private long Fresh()
    {
        FreshStamps++;
        return NonZero(Mix(Mix(FreshTag, _freshSeed), ++_freshCounter));
    }

    private long Compose(Entry e)
    {
        ulong h = Mix(0x494E5653544D50UL, (ulong)(uint)_entriesEpoch); // "INVSTMP"
        h = Mix(h, ((ulong)(uint)e.InventoryIndex << 32) | (uint)e.InventoryVersion);
        h = Mix(h, (ulong)(uint)e.Slots.Chunk);
        h = Mix(h, (ulong)e.Slots.Archetype);
        h = Mix(h, (ulong)e.SlotsV64);
        if (e.External)
        {
            h = Mix(h, (ulong)(uint)e.Instances.Chunk);
            h = Mix(h, (ulong)e.Instances.Archetype);
            h = Mix(h, (ulong)e.InstancesV64);
        }

        h = Mix(h, (ulong)e.Override);
        return NonZero(h);
    }

    /// <summary>
    /// Bring the reference's entry up to date with the raw reads, re-resolving when they disagree
    /// with the cached identity. Null when anything is unreadable (the entry is dropped).
    /// </summary>
    private Entry? Refresh(InventoryRef container)
    {
        if (container.IsNone || container.IsStaleIn(_source.CurrentEpoch) || !_clockSeen || !_batchBound) return null;

        if (_entries.Count >= MaxEntries && !_entries.ContainsKey(container)) Clear();
        if (!_entries.TryGetValue(container, out var entry))
        {
            entry = new Entry(container) { IdentityAt = _clock64 };
            _entries[container] = entry;
            _auditOrder.Add(entry);
        }

        entry.LastFrame = _frame;
        if (!TryRefresh(entry))
        {
            // A failed batch (job completion did not happen) says nothing about this reference:
            // answer fresh, keep what is cached. Any other failure means the cache is wrong.
            if (!_batchFailed) Drop(entry);
            return null;
        }

        return entry;
    }

    private bool TryRefresh(Entry e)
    {
        bool resolve = !e.Resolved;
        ChunkVersionRead instances = default;
        if (!resolve && e.External)
        {
            if (!ReadConfirmed(e.Ref.OwnerIndex, e.Ref.OwnerVersion, instances: true, out instances))
            {
                if (_batchFailed) return false;
                resolve = true;
            }
            else if (!instances.Equals(e.Instances))
            {
                resolve = true;
            }
        }

        if (resolve)
        {
            Resolves++;
            if (!_source.TryResolve(e.Ref, out int index, out int version)) return false;
            bool external = index != e.Ref.OwnerIndex || version != e.Ref.OwnerVersion;
            if (external && !ReadConfirmed(e.Ref.OwnerIndex, e.Ref.OwnerVersion, instances: true, out instances)) return false;

            if (!e.Resolved || index != e.InventoryIndex || version != e.InventoryVersion || external != e.External
                || (external && !instances.SamePlace(e.Instances)))
                e.IdentityAt = _clock64;
            e.InventoryIndex = index;
            e.InventoryVersion = version;
            e.External = external;
            e.Resolved = true;
        }

        if (e.External)
        {
            e.InstancesV64 = KeepOrExtend(e.HasInstances, e.Instances, e.InstancesV64, instances);
            e.Instances = instances;
            e.HasInstances = true;
        }

        if (!ReadConfirmed(e.InventoryIndex, e.InventoryVersion, instances: false, out var slots))
        {
            // The cached inventory entity is gone or lost its slots: one more try from the owner.
            if (resolve || _batchFailed) return false;
            e.Resolved = false;
            return TryRefresh(e);
        }

        if (e.HasSlots && !slots.SamePlace(e.Slots)) e.IdentityAt = _clock64;
        e.SlotsV64 = KeepOrExtend(e.HasSlots, e.Slots, e.SlotsV64, slots);
        e.Slots = slots;
        e.HasSlots = true;
        return true;
    }

    /// <summary>
    /// The 64-bit version of a fresh read: the previous one while the 32-bit version, chunk and
    /// archetype are unchanged (so a clock that ran 2^31 past an old, untouched version does not
    /// flip it), else the read extended at the clock now.
    /// </summary>
    private long KeepOrExtend(bool had, ChunkVersionRead previous, long previous64, ChunkVersionRead read) =>
        had && read.Equals(previous) ? previous64 : ChunkVersionMath.Extend(_clock64, read.Version);

    /// <summary>
    /// A raw version read of an entity, with the batch's job completion done on that entity the
    /// first time: read (which confirms the entity is alive and has the buffer), complete the
    /// in-flight writers of that type through it, then read again so the version is the one after
    /// those writers. A completion that fails or throws marks the whole batch failed: everything it
    /// still answers is fresh, never a version a writer may still move past.
    /// </summary>
    private bool ReadConfirmed(int index, int version, bool instances, out ChunkVersionRead read)
    {
        read = default;
        if (_batchFailed || !_batchBound) return false;
        if (!RawRead(index, version, instances, out read)) return false;
        if (instances ? _instancesCompleted : _slotsCompleted) return true;

        bool completed;
        try { completed = _source.CompleteWriters(index, version, instances); }
        catch { completed = false; }
        if (!completed)
        {
            _batchFailed = true;
            _batchBound = false;
            CompletionFailures++;
            return false;
        }

        if (instances) _instancesCompleted = true;
        else _slotsCompleted = true;
        return RawRead(index, version, instances, out read);
    }

    private bool RawRead(int index, int version, bool instances, out ChunkVersionRead read) =>
        instances
            ? _source.TryReadInstancesVersion(index, version, out read)
            : _source.TryReadSlotsVersion(index, version, out read);

    private void Drop(Entry e)
    {
        _entries.Remove(e.Ref);
        e.Dropped = true;
    }

    // ---- upkeep --------------------------------------------------------------------------------

    private void Evict()
    {
        bool any = false;
        foreach (var e in _auditOrder)
        {
            if (e.Dropped) { any = true; continue; }
            if (_frame - e.LastFrame <= EvictAfterFrames) continue;
            Drop(e);
            any = true;
        }

        if (!any) return;
        _auditOrder.RemoveAll(static e => e.Dropped);
        if (_auditCursor >= _auditOrder.Count) _auditCursor = 0;
    }

    private void Audit()
    {
        int n = Math.Min(AuditPerFrame, _auditOrder.Count);
        for (int k = 0; k < n; k++)
        {
            if (_auditCursor >= _auditOrder.Count) _auditCursor = 0;
            var e = _auditOrder[_auditCursor++];
            if (e.Dropped) continue;
            long lastFrame = e.LastFrame;
            AuditOne(e);
            e.LastFrame = lastFrame; // an audit is not a use
        }
    }

    private void AuditOne(Entry e)
    {
        // Refresh through the dictionary path so a failure drops the entry the same way.
        if (!_entries.TryGetValue(e.Ref, out var current) || !ReferenceEquals(current, e)) return;
        if (!TryRefresh(e))
        {
            if (!_batchFailed) Drop(e);
            return;
        }

        if (!_source.TryContentHash(e.InventoryIndex, e.InventoryVersion, out ulong hash))
        {
            e.Audited = false;
            return;
        }

        Audits++;
        long stamp = Compose(e);
        if (e.Audited && e.AuditStamp == stamp && e.AuditHash != hash)
        {
            Misses++;
            e.Override++;
            e.OverrideAt = _clock64;
            stamp = Compose(e);
            if (!_missLogged)
            {
                _missLogged = true;
                _logError?.Invoke("Inventory: CHANGE STAMP MISS. The slots of " + e.Ref + " (inventory entity "
                    + e.InventoryIndex + ":" + e.InventoryVersion + ", chunk " + e.Slots.Chunk + ") changed while their"
                    + " InventoryBuffer change version did not: some write path does not mark the chunk changed. The"
                    + " audit folded the change into the stamp, so consumers still see it; the stamp alone is not"
                    + " reliable for that path. Report this with the server log. Further misses are counted, not logged.");
            }
        }

        e.AuditStamp = stamp;
        e.AuditHash = hash;
        e.Audited = true;
    }

    // ---- hashing -------------------------------------------------------------------------------

    /// <summary>
    /// Content hash of <paramref name="rows"/> <c>InventoryBuffer</c> rows laid out at the metadata
    /// stride: every field (item entity, its sync flag, item type, amount, stack override), never the
    /// padding between them. Also the row count, so a resize changes it.
    /// </summary>
    internal static ulong RowHash(ReadOnlySpan<byte> raw, int rows)
    {
        if (rows < 0 || (long)rows * Layout.InventoryBufferStride > raw.Length)
            throw new ArgumentOutOfRangeException(nameof(rows));
        ulong h = Mix(0x524F574841534855UL, (ulong)rows); // "ROWHASHU"
        for (int i = 0; i < rows; i++)
        {
            var row = raw.Slice(i * Layout.InventoryBufferStride, Layout.InventoryBufferStride);
            var entity = row.Slice(Layout.InventoryBufferItemEntityOffset + Layout.NetworkedEntityEntityOffset, Layout.EntityStride);
            h = Mix(h, BitConverter.ToUInt64(entity));
            h = Mix(h, row[Layout.InventoryBufferItemEntityOffset + Layout.NetworkedEntityWaitingForSyncOffset]);
            h = Mix(h, BitConverter.ToUInt32(row.Slice(Layout.InventoryBufferItemTypeOffset, sizeof(int))));
            h = Mix(h, BitConverter.ToUInt32(row.Slice(Layout.InventoryBufferAmountOffset, sizeof(int))));
            h = Mix(h, BitConverter.ToUInt32(row.Slice(Layout.InventoryBufferMaxAmountOverrideOffset, sizeof(int))));
        }

        return h;
    }

    private static ulong Mix(ulong h, ulong v)
    {
        // splitmix64 over the running value: cheap, and every input bit reaches every output bit.
        ulong z = h ^ (v + 0x9E3779B97F4A7C15UL + (h << 6) + (h >> 2));
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    private static long NonZero(ulong h) => h == 0 ? 1 : (long)h;

    private sealed class Entry
    {
        internal Entry(InventoryRef r) => Ref = r;

        internal InventoryRef Ref { get; }
        internal bool Resolved;
        internal bool Dropped;
        internal int InventoryIndex;
        internal int InventoryVersion;
        internal bool External;
        internal bool HasSlots;
        internal bool HasInstances;
        internal ChunkVersionRead Slots;
        internal long SlotsV64;
        internal ChunkVersionRead Instances;
        internal long InstancesV64;
        internal long IdentityAt;
        internal long Override;
        internal long OverrideAt = long.MinValue;
        internal long LastFrame;
        internal bool Audited;
        internal long AuditStamp;
        internal ulong AuditHash;
    }
}
