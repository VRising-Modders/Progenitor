using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;

namespace Progenitor.Systems;

/// <summary>
/// Ordered, owner-tracked callback table that <see cref="VirtualSystems"/> dispatches from a
/// single game-system hook per anchor. No Unity, BepInEx, or game types — the same class is
/// compiled into the test project so ordering, isolation, replace, and <see cref="Stop"/> can
/// be proven without the interop.
/// </summary>
public sealed class VirtualSystemDispatcher
{
    /// <summary>
    /// Time a slot every Nth invoke so <c>lastMs=</c> in Describe is not stale forever, without a
    /// QPC syscall on every sim tick. Describe() also sets a one-dispatch profiling flag.
    /// </summary>
    internal const int TimeEveryFrames = 60;

    private static readonly HashSet<string> NestedLogged = new(StringComparer.Ordinal);
    private static readonly VirtualSystemSlot[] NoSlots = Array.Empty<VirtualSystemSlot>();
    private static readonly Dictionary<string, VirtualSystemSlot[]> NoSnapshots = new(StringComparer.Ordinal);

    private readonly object _gate = new();
    private readonly Dictionary<string, List<VirtualSystemSlot>> _byAnchor = new(StringComparer.Ordinal);
    private readonly List<VirtualSystemSlot> _all = new();
    private int _nextSequence;
    private int _profiling;
    private VirtualSystemSlot[] _allSnapshot = NoSlots;
    private Dictionary<string, VirtualSystemSlot[]> _snapshots = NoSnapshots;

    [ThreadStatic]
    private static HashSet<string>? t_dispatching;

    /// <summary>Number of live slots across every anchor. Reads the published snapshot (no lock).</summary>
    public int Count => Volatile.Read(ref _allSnapshot)?.Length ?? 0;

    /// <summary>Live slots under <paramref name="anchorKey"/>, or 0 if none. Reads the published snapshot.</summary>
    public int CountFor(string anchorKey)
    {
        if (string.IsNullOrEmpty(anchorKey)) return 0;
        var snapshots = Volatile.Read(ref _snapshots);
        return snapshots != null && snapshots.TryGetValue(anchorKey, out var list) ? list.Length : 0;
    }

    /// <summary>
    /// Register a named callback under <paramref name="anchorKey"/>. Lower
    /// <paramref name="order"/> runs first (Unity execution-order convention); equal order keeps
    /// registration sequence. The same <paramref name="name"/> from the same
    /// <paramref name="owner"/> replaces the previous slot so a reload cannot stack copies.
    /// Dispose the result (or <see cref="RemoveAll"/>) to stop.
    /// </summary>
    public IDisposable Add(
        string name,
        string anchorKey,
        Action<VirtualSystemSlot> onUpdate,
        object owner,
        int order = 0,
        string? anchorDisplay = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Name is required.", nameof(name));
        if (string.IsNullOrEmpty(anchorKey))
            throw new ArgumentException("Anchor key is required.", nameof(anchorKey));
        if (onUpdate == null) throw new ArgumentNullException(nameof(onUpdate));
        if (owner == null) throw new ArgumentNullException(nameof(owner));

        var slot = new VirtualSystemSlot(
            this,
            name.Trim(),
            anchorKey,
            string.IsNullOrEmpty(anchorDisplay) ? anchorKey : anchorDisplay,
            owner,
            order,
            onUpdate);

        lock (_gate)
        {
            ReplaceSameName_NoLock(owner, slot.Name);
            slot.Sequence = ++_nextSequence;
            if (!_byAnchor.TryGetValue(anchorKey, out var list))
                _byAnchor[anchorKey] = list = new List<VirtualSystemSlot>();
            InsertSorted(list, slot);
            _all.Add(slot);
            PublishSnapshot_NoLock();
        }

        return slot;
    }

    /// <summary>Drop every slot registered with this owner. Returns how many were removed.</summary>
    public int RemoveAll(object owner)
    {
        if (owner == null) throw new ArgumentNullException(nameof(owner));
        int n = 0;
        lock (_gate)
        {
            for (int i = _all.Count - 1; i >= 0; i--)
            {
                var slot = _all[i];
                if (!ReferenceEquals(slot.Owner, owner)) continue;
                Detach_NoLock(slot);
                n++;
            }
            if (n != 0) PublishSnapshot_NoLock();
        }
        return n;
    }

    /// <summary>
    /// Run every live slot under <paramref name="anchorKey"/> in (order, registration) sequence.
    /// A throwing slot is recorded and logged; later slots still run. Nested dispatch of the same
    /// key on this thread is refused so a callback cannot stack-overflow the main thread.
    /// </summary>
    public void Dispatch(string anchorKey)
    {
        if (string.IsNullOrEmpty(anchorKey)) return;

        var dispatching = t_dispatching ??= new HashSet<string>(StringComparer.Ordinal);
        if (!dispatching.Add(anchorKey))
        {
            LogNestedOnce(anchorKey);
            return;
        }

        try
        {
            var snapshots = Volatile.Read(ref _snapshots);
            if (snapshots == null || !snapshots.TryGetValue(anchorKey, out var snapshot) || snapshot.Length == 0)
                return;

            // Describe() arms profiling for one dispatch so lastMs= is fresh when someone looks,
            // without a Stopwatch on every sim tick. Consumed here so a later Dispatch of a
            // nested different key does not time again until the next Describe.
            bool profile = Interlocked.Exchange(ref _profiling, 0) != 0;
            for (int i = 0; i < snapshot.Length; i++)
            {
                // Snapshot is authoritative for this dispatch: a Stop/RemoveAll from an
                // earlier slot does not skip later ones that were live when we published.
                Invoke(snapshot[i], profile);
            }
        }
        finally
        {
            dispatching.Remove(anchorKey);
        }
    }

    /// <summary>
    /// One-line diagnostic for the status op: count, then each slot's name, anchor, order,
    /// invocation count, last duration, and last error (if any).
    /// </summary>
    public string Describe()
    {
        // Arm timing for the next Dispatch so lastMs= is not a 60-frame-old sample when the
        // status op is about to be read. The hot path does not time unless this is set or
        // the slot has reached TimeEveryFrames.
        Volatile.Write(ref _profiling, 1);
        var slots = Volatile.Read(ref _allSnapshot) ?? NoSlots;

        var sb = new StringBuilder();
        sb.Append("count=").Append(slots.Length);
        for (int i = 0; i < slots.Length; i++)
        {
            var s = slots[i];
            sb.Append("; ").Append(s.Name)
                .Append(" [").Append(s.AnchorDisplay).Append(']')
                .Append(" order=").Append(s.Order)
                .Append(" frames=").Append(s.Frame)
                .Append(" lastMs=").Append(s.LastDuration.TotalMilliseconds.ToString("0.###", CultureInfo.InvariantCulture));
            if (!string.IsNullOrEmpty(s.LastError))
                sb.Append(" lastError=").Append(s.LastError);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Published live slots, in registration-list order (not dispatch order). The array is
    /// immutable after publish; callers must not mutate it.
    /// </summary>
    public VirtualSystemSlot[] SnapshotSlots()
        => Volatile.Read(ref _allSnapshot) ?? NoSlots;

    internal void Remove(VirtualSystemSlot slot)
    {
        if (slot == null) return;
        lock (_gate)
        {
            if (slot.Removed) return;
            Detach_NoLock(slot);
            PublishSnapshot_NoLock();
        }
    }

    /// <summary>
    /// Copy-on-write publish of per-anchor slot arrays. Callers must hold the dispatcher lock.
    /// Dispatch reads with <c>Volatile.Read</c> only — no lock, no <c>ToArray</c> per update.
    /// </summary>
    private void PublishSnapshot_NoLock()
    {
        VirtualSystemSlot[] all = _all.Count == 0 ? NoSlots : _all.ToArray();
        Dictionary<string, VirtualSystemSlot[]> snaps;
        if (_byAnchor.Count == 0)
        {
            snaps = NoSnapshots;
        }
        else
        {
            snaps = new Dictionary<string, VirtualSystemSlot[]>(_byAnchor.Count, StringComparer.Ordinal);
            foreach (var kv in _byAnchor)
                snaps[kv.Key] = kv.Value.Count == 0 ? NoSlots : kv.Value.ToArray();
        }

        Volatile.Write(ref _allSnapshot, all);
        Volatile.Write(ref _snapshots, snaps);
    }

    private void ReplaceSameName_NoLock(object owner, string name)
    {
        for (int i = _all.Count - 1; i >= 0; i--)
        {
            var existing = _all[i];
            if (existing.Removed) continue;
            if (!ReferenceEquals(existing.Owner, owner)) continue;
            if (!string.Equals(existing.Name, name, StringComparison.Ordinal)) continue;
            Detach_NoLock(existing);
        }
    }

    private void Detach_NoLock(VirtualSystemSlot slot)
    {
        if (slot.Removed) return;
        slot.Removed = true;
        _all.Remove(slot);
        if (_byAnchor.TryGetValue(slot.AnchorKey, out var list))
        {
            list.Remove(slot);
            if (list.Count == 0) _byAnchor.Remove(slot.AnchorKey);
        }
    }

    private static void InsertSorted(List<VirtualSystemSlot> list, VirtualSystemSlot slot)
    {
        int i = 0;
        while (i < list.Count)
        {
            var other = list[i];
            if (other.Order > slot.Order) break;
            if (other.Order == slot.Order && other.Sequence > slot.Sequence) break;
            i++;
        }
        list.Insert(i, slot);
    }

    private static void Invoke(VirtualSystemSlot slot, bool profile)
    {
        int nextFrame = slot.Frame + 1;
        bool time = profile || (nextFrame % TimeEveryFrames == 0);
        Stopwatch? sw = time ? Stopwatch.StartNew() : null;
        try
        {
            slot.Frame = nextFrame;
            slot.Handler(slot);
            slot.LastError = null;
        }
        catch (Exception e)
        {
            slot.LastError = e.GetType().Name + ": " + e.Message;
            Plugin.Logger?.LogError($"VirtualSystems: '{slot.Name}' threw: {e}");
        }
        finally
        {
            if (sw != null)
            {
                sw.Stop();
                slot.LastDuration = sw.Elapsed;
                slot.LastTimedFrame = slot.Frame;
            }
        }
    }

    private static void LogNestedOnce(string anchorKey)
    {
        lock (NestedLogged)
        {
            if (!NestedLogged.Add(anchorKey)) return;
        }
        Plugin.Logger?.LogWarning($"VirtualSystems: refused nested Dispatch of '{anchorKey}' (already dispatching on this thread).");
    }
}

/// <summary>
/// One named callback in <see cref="VirtualSystemDispatcher"/>. Dispose or <see cref="Stop"/>
/// unregisters it; either is safe from inside the callback (the current invoke finishes, the
/// next dispatch skips it).
/// </summary>
public sealed class VirtualSystemSlot : IDisposable
{
    private readonly VirtualSystemDispatcher _dispatcher;
    private int _disposed;

    internal VirtualSystemSlot(
        VirtualSystemDispatcher dispatcher,
        string name,
        string anchorKey,
        string anchorDisplay,
        object owner,
        int order,
        Action<VirtualSystemSlot> handler)
    {
        _dispatcher = dispatcher;
        Name = name;
        AnchorKey = anchorKey;
        AnchorDisplay = anchorDisplay;
        Owner = owner;
        Order = order;
        Handler = handler;
    }

    /// <summary>Name passed to Add. Unique per owner; a second Add of the same pair replaces this slot.</summary>
    public string Name { get; }

    /// <summary>Dispatcher grouping key (phase + system type, or the default-simulation sentinel).</summary>
    public string AnchorKey { get; }

    /// <summary>Human-readable anchor for logs and <see cref="VirtualSystemDispatcher.Describe"/>.</summary>
    public string AnchorDisplay { get; }

    /// <summary>Owning plugin (or test fixture). <see cref="VirtualSystemDispatcher.RemoveAll"/> keys on this.</summary>
    public object Owner { get; }

    /// <summary>Lower runs first. Equal values keep registration sequence.</summary>
    public int Order { get; }

    /// <summary>How many times this slot has been invoked, including a throw. 0 until the first dispatch.</summary>
    public int Frame { get; internal set; }

    /// <summary>Exception type and message from the last throw, or null after a successful invoke.</summary>
    public string? LastError { get; internal set; }

    /// <summary>Wall time of the last <em>timed</em> invoke, successful or not. Zero until the first timed frame.</summary>
    public TimeSpan LastDuration { get; internal set; }

    /// <summary>
    /// <see cref="Frame"/> of the last invoke that started a Stopwatch, or 0 if none has. Tests
    /// use this to prove the hot path does not time every dispatch.
    /// </summary>
    internal int LastTimedFrame { get; set; }

    internal Action<VirtualSystemSlot> Handler { get; }
    internal int Sequence { get; set; }
    internal bool Removed { get; set; }

    /// <summary>
    /// Unregister from inside the callback. The current invoke still finishes; this slot is
    /// skipped on the next dispatch. Same as Dispose.
    /// </summary>
    public void Stop() => Dispose();

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _dispatcher.Remove(this);
    }
}
