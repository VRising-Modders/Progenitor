using System;
using System.Collections.Generic;
using Progenitor.Inventory;

namespace Progenitor.Drops;

// ---- game-free logic behind ctx.Drops and ctx.Inventory.OnGroundDrop / OnDropAll ---------------

/// <summary>
/// The rules <c>ctx.Drops.TakeInto</c> applies before and after its inventory add. Game-free.
/// </summary>
internal static class GroundTakeRules
{
    internal const string NoPile = "no pile (GroundPileId.None)";
    internal const string StalePile = "the pile handle is from an earlier world load";
    internal const string BadAmount = "amount must be at least 1";
    internal const string Gone = "the pile is gone";
    internal const string Removing = "the pile is being removed";
    internal const string Empty = "the pile is empty";
    internal const string InFlight =
        "the pile is in flight (the game's throw is still running, and the throw holds its own amount); try again once it has landed";
    internal const string ItemEntity =
        "the pile carries its own item entity (gear with rolled stats, a jewel); TakeInto refuses it rather than copy the item without its state";
    internal const string NotStackable =
        "the item does not stack (stack size 1); TakeInto refuses it, because such an item can carry state a copy would lose";
    internal const string UnknownItem = "the item is not in the server item table";
    internal const string NoTarget = "no target inventory (InventoryRef.None)";

    /// <summary>
    /// Why a take must not start, or null when it may. Checked in this order: arguments, the
    /// handle's epoch, the pile's presence and state, then what it holds.
    /// </summary>
    internal static string? Refusal(bool pileNone, bool stale, bool targetNone, int requested, bool exists, bool removing,
        int amount, bool inFlight, bool hasItemEntity, bool itemKnown, int maxStack)
    {
        if (pileNone) return NoPile;
        if (targetNone) return NoTarget;
        if (requested < 1) return BadAmount;
        if (stale) return StalePile;
        if (!exists) return Gone;
        if (removing) return Removing;
        if (amount <= 0) return Empty;
        if (inFlight) return InFlight;
        if (hasItemEntity) return ItemEntity;
        if (!itemKnown) return UnknownItem;
        if (maxStack <= 1) return NotStackable;
        return null;
    }

    /// <summary>How many to ask the inventory for: never more than the pile holds.</summary>
    internal static int Ask(int requested, int pileAmount) => Math.Max(0, Math.Min(requested, pileAmount));

    /// <summary>
    /// The pile after the inventory took <paramref name="added"/> (a read-back, clamped to what was
    /// asked): the amount to write and whether the pile is now empty and must be removed.
    /// </summary>
    internal static (int NewAmount, bool Remove, int Done) After(int pileAmount, int asked, int added)
    {
        int done = Math.Max(0, Math.Min(added, Math.Min(asked, pileAmount)));
        int left = pileAmount - done;
        return (left, left <= 0 && done > 0, done);
    }

    /// <summary>The outcome for a take that reached the add.</summary>
    internal static InventoryOutcome Outcome(int requested, int done) =>
        done <= 0 ? InventoryOutcome.Nothing : done >= requested ? InventoryOutcome.Complete : InventoryOutcome.Partial;
}

/// <summary>Distance filter and nearest-first selection for <c>ctx.Drops.Near</c>. Game-free.</summary>
internal static class GroundPileSelect
{
    /// <summary>The largest radius <c>Near</c> accepts, metres.</summary>
    internal const float MaxRadius = 500f;

    /// <summary>The most piles one <c>Near</c> call returns.</summary>
    internal const int MaxResults = 4096;

    /// <summary>The radius clamped to 0..<see cref="MaxRadius"/>; NaN reads as 0.</summary>
    internal static float ClampRadius(float radius) =>
        float.IsNaN(radius) || radius <= 0 ? 0 : Math.Min(radius, MaxRadius);

    /// <summary>The result cap clamped to 1..<see cref="MaxResults"/>.</summary>
    internal static int ClampMax(int max) => Math.Max(1, Math.Min(max, MaxResults));

    /// <summary>True when (x,y,z) lies within the radius of the centre; <paramref name="distanceSquared"/> is the 3-D squared distance.</summary>
    internal static bool Within(float cx, float cy, float cz, float radiusSquared, float x, float y, float z, out float distanceSquared)
    {
        float dx = x - cx, dy = y - cy, dz = z - cz;
        distanceSquared = dx * dx + dy * dy + dz * dz;
        return distanceSquared <= radiusSquared;
    }

    /// <summary>Order nearest first (ties by entity key, so the order is stable) and keep at most <paramref name="max"/>.</summary>
    internal static void NearestFirst(List<GroundPile> piles, int max)
    {
        piles.Sort(static (a, b) =>
        {
            int c = a.DistanceSquared.CompareTo(b.DistanceSquared);
            return c != 0 ? c : a.Id.Key.CompareTo(b.Id.Key);
        });
        if (piles.Count > max) piles.RemoveRange(max, piles.Count - max);
    }
}

/// <summary>What kind of drop request a <see cref="BagDropBatch"/> row came from.</summary>
internal enum BagDropRequestKind
{
    /// <summary><c>DropItemAtSlotEvent</c>.</summary>
    DropKey = 0,

    /// <summary><c>DropInventoryItemEvent</c> naming the player's own inventory.</summary>
    DragOut = 1,

    /// <summary><c>DropEntireInventoryEvent</c>.</summary>
    DropAll = 2,
}

/// <summary>A ground drop the batch found, before the delivery tick numbers it.</summary>
internal readonly struct PendingGroundDrop
{
    internal PendingGroundDrop(long user, long character, GroundDropSource source, int item, int amount, int before, int after,
        bool withItemEntity, bool hasPosition, float x, float y, float z, int requests, DateTime utc)
    {
        User = user;
        Character = character;
        Source = source;
        Item = item;
        Amount = amount;
        Before = before;
        After = after;
        WithItemEntity = withItemEntity;
        HasPosition = hasPosition;
        X = x;
        Y = y;
        Z = z;
        Requests = requests;
        Utc = utc;
    }

    internal long User { get; }
    internal long Character { get; }
    internal GroundDropSource Source { get; }
    internal int Item { get; }
    internal int Amount { get; }
    internal int Before { get; }
    internal int After { get; }
    internal bool WithItemEntity { get; }
    internal bool HasPosition { get; }
    internal float X { get; }
    internal float Y { get; }
    internal float Z { get; }
    internal int Requests { get; }
    internal DateTime Utc { get; }

    internal PlayerGroundDrop ToDrop(long sequence) =>
        new(sequence, Utc, (int)(User >> 32), (int)User, (int)(Character >> 32), (int)Character, Source, Item, Amount, Before, After,
            WithItemEntity, HasPosition, X, Y, Z, Requests);
}

/// <summary>A whole-inventory drop the batch found: the bag's loss, waiting for its container.</summary>
internal sealed class PendingDropAll
{
    internal long User, Character;
    internal int SettingRaw;
    internal readonly List<DroppedStack> Items = new();
    internal bool HasPosition, Ambiguous;
    internal float X, Y, Z;
    internal DateTime Utc;

    /// <summary>A search offered a container near enough that did not hold what left the bag.</summary>
    internal bool SawMismatch { get; set; }

    /// <summary>The container book had no baseline when this drop happened: its container cannot be found.</summary>
    internal bool Unbooked { get; set; }

    /// <summary>Frame tick count after which the search gives up.</summary>
    internal long Deadline { get; set; }

    /// <summary>Total units that left the bag.</summary>
    internal int Units
    {
        get
        {
            int n = 0;
            foreach (var s in Items) n += s.Amount;
            return n;
        }
    }

    /// <summary>The request's raw <c>DropEntireInventorySetting</c>, mapped through the game's values (layout constants).</summary>
    internal static DropAllSetting Setting(int raw)
    {
        if (raw == Layout.DropEntireInventorySettingAll) return DropAllSetting.All;
        if (raw == Layout.DropEntireInventorySettingExceptAnyEquipped) return DropAllSetting.ExceptAnyEquipped;
        if (raw == Layout.DropEntireInventorySettingExceptEquippedInActionBar) return DropAllSetting.ExceptEquippedInActionBar;
        if (raw == Layout.DropEntireInventorySettingExceptAnyWeaponInActionBar) return DropAllSetting.ExceptAnyWeaponInActionBar;
        if (raw == Layout.DropEntireInventorySettingExceptAnyWeaponInActionBarOrAnyEquipped) return DropAllSetting.ExceptAnyWeaponInActionBarOrAnyEquipped;
        if (raw == Layout.DropEntireInventorySettingExceptAnythingInActionBar) return DropAllSetting.ExceptAnythingInActionBar;
        if (raw == Layout.DropEntireInventorySettingExceptAnythingInActionBarOrEquipped) return DropAllSetting.ExceptAnythingInActionBarOrEquipped;
        return DropAllSetting.Unknown;
    }

    internal PlayerDropAll ToDrop(long sequence, int containerIndex, int containerVersion, int epoch, bool contentsMatch, bool ambiguous, string reason) =>
        new(sequence, Utc, (int)(User >> 32), (int)User, (int)(Character >> 32), (int)Character, Setting(SettingRaw),
            Items.ToArray(), HasPosition, X, Y, Z, containerIndex, containerVersion, epoch, contentsMatch, Ambiguous || ambiguous, reason);
}

/// <summary>
/// One system update's drop requests, grouped by character, with each character's bag rows
/// before and after the update. Game-free: the runtime adds the requests and the rows it read, then
/// asks for the drops.
///
/// <para><b>Attribution.</b> A drop request only ever empties the sender's own bag (the drop key
/// names a slot of <c>FromCharacter</c>'s inventory; a drag-out that names another player's
/// inventory is not added), so the diff of one character's bag belongs to that character's
/// requests. When the update also carried that character's whole-inventory drop, the whole loss is
/// that drop's (marked ambiguous when a drop-key request came with it).</para>
/// </summary>
internal sealed class BagDropBatch
{
    /// <summary>Requests read per system update; the rest are counted and not observed.</summary>
    internal const int MaxRequests = 64;

    /// <summary>Characters observed per system update.</summary>
    internal const int MaxBags = 32;

    private sealed class Bag
    {
        internal long User, Character, Key;
        internal int DropKey, DragOut, DropAll, Setting;
        internal readonly List<MoveSlotRow> Before = new(), After = new();
        internal bool HasBefore, HasAfter, HasPosition;
        internal float X, Y, Z;
    }

    private readonly List<Bag> _bags = new();
    private readonly Stack<Bag> _pool = new();
    private readonly PlayerMoveDiffer _differ = new();
    private readonly List<ItemMoveDelta> _deltas = new();
    private int _requests;

    /// <summary>True between <see cref="Begin"/> and <see cref="Clear"/>.</summary>
    internal bool Open { get; private set; }

    /// <summary>Opaque owner (the runtime's per-system watcher).</summary>
    internal object? Owner { get; private set; }

    internal int BagCount => _bags.Count;
    internal int RequestCount => _requests;

    /// <summary>Requests past the caps in this update (not observed).</summary>
    internal int Truncated { get; private set; }

    /// <summary>Bags that did not change in the last <see cref="Emit"/> (the game refused, or nothing to drop).</summary>
    internal int Unchanged { get; private set; }

    /// <summary>Bags whose rows could not be read before or after in the last <see cref="Emit"/>.</summary>
    internal int Unread { get; private set; }

    /// <summary>True when the update carried a whole-inventory drop request.</summary>
    internal bool HasDropAll { get; private set; }

    internal void Begin(object owner)
    {
        Clear();
        Owner = owner;
        Open = true;
    }

    internal void Clear()
    {
        foreach (var b in _bags) _pool.Push(b);
        _bags.Clear();
        _requests = 0;
        Truncated = 0;
        HasDropAll = false;
        Owner = null;
        Open = false;
    }

    /// <summary>Add one request; the index of its character's bag row, or -1 past a cap.</summary>
    internal int AddRequest(long user, long character, BagDropRequestKind kind, int setting = 0)
    {
        if (_requests >= MaxRequests)
        {
            Truncated++;
            return -1;
        }

        int at = -1;
        for (int i = 0; i < _bags.Count; i++)
            if (_bags[i].Character == character) { at = i; break; }
        if (at < 0)
        {
            if (_bags.Count >= MaxBags)
            {
                Truncated++;
                return -1;
            }

            var b = _pool.Count > 0 ? _pool.Pop() : new Bag();
            b.User = user;
            b.Character = character;
            b.Key = 0;
            b.DropKey = b.DragOut = b.DropAll = b.Setting = 0;
            b.Before.Clear();
            b.After.Clear();
            b.HasBefore = b.HasAfter = b.HasPosition = false;
            b.X = b.Y = b.Z = 0;
            _bags.Add(b);
            at = _bags.Count - 1;
        }

        _requests++;
        var bag = _bags[at];
        switch (kind)
        {
            case BagDropRequestKind.DropKey: bag.DropKey++; break;
            case BagDropRequestKind.DragOut: bag.DragOut++; break;
            default:
                bag.DropAll++;
                bag.Setting = setting;
                HasDropAll = true;
                break;
        }

        return at;
    }

    internal long CharacterOf(int index) => _bags[index].Character;

    /// <summary>The key of the entity that holds this character's bag slots (0 when unresolved).</summary>
    internal long KeyOf(int index) => _bags[index].Key;

    internal void SetKey(int index, long key) => _bags[index].Key = key;

    /// <summary>The rows read before (<paramref name="after"/> false) or after the update; null when the read failed.</summary>
    internal void SetRows(int index, bool after, IReadOnlyList<MoveSlotRow>? rows)
    {
        var b = _bags[index];
        var into = after ? b.After : b.Before;
        into.Clear();
        if (rows != null) for (int i = 0; i < rows.Count; i++) into.Add(rows[i]);
        if (after) b.HasAfter = rows != null;
        else b.HasBefore = rows != null;
    }

    internal void SetPosition(int index, float x, float y, float z)
    {
        var b = _bags[index];
        b.X = x;
        b.Y = y;
        b.Z = z;
        b.HasPosition = true;
    }

    /// <summary>
    /// Compare every bag; append ground drops and whole-inventory drops, then <see cref="Clear"/>.
    /// Only losses count: a drop never adds to the bag, and slots that only moved are not drops.
    /// </summary>
    internal void Emit(DateTime utc, List<PendingGroundDrop> ground, List<PendingDropAll> all)
    {
        Unchanged = 0;
        Unread = 0;
        foreach (var b in _bags)
        {
            if (!b.HasBefore || !b.HasAfter)
            {
                Unread++;
                continue;
            }

            _differ.Diff(b.Before, b.After, _deltas);
            bool lost = false;
            foreach (var d in _deltas)
                if (d.Out > 0) { lost = true; break; }
            if (!lost)
            {
                Unchanged++;
                continue;
            }

            if (b.DropAll > 0)
            {
                var p = new PendingDropAll
                {
                    User = b.User, Character = b.Character, SettingRaw = b.Setting, HasPosition = b.HasPosition,
                    X = b.X, Y = b.Y, Z = b.Z, Utc = utc, Ambiguous = b.DropKey > 0 || b.DragOut > 0,
                };
                foreach (var d in _deltas)
                    if (d.Out > 0) p.Items.Add(new DroppedStack(d.Item, d.Out, d.OutWithEntity > 0));
                all.Add(p);
                continue;
            }

            var source = b.DragOut > 0 && b.DropKey == 0 ? GroundDropSource.DragOut : GroundDropSource.DropKey;
            int requests = b.DropKey + b.DragOut;
            foreach (var d in _deltas)
            {
                if (d.Out <= 0) continue;
                ground.Add(new PendingGroundDrop(b.User, b.Character, source, d.Item, d.Out, d.Before, d.After,
                    d.OutWithEntity > 0, b.HasPosition, b.X, b.Y, b.Z, requests, utc));
            }
        }

        Clear();
    }
}

/// <summary>A dropped-inventory container that appeared after a whole-inventory drop. Game-free.</summary>
internal readonly struct DropContainerCandidate
{
    internal DropContainerCandidate(long key, float x, float y, float z)
    {
        Key = key;
        X = x;
        Y = y;
        Z = z;
    }

    internal long Key { get; }
    internal float X { get; }
    internal float Y { get; }
    internal float Z { get; }
}

/// <summary>
/// Pairs pending whole-inventory drops with the new containers near them. Game-free. Pairs are
/// taken nearest first across all requests, each container at most once, only within
/// <see cref="MaxDistance"/> of where the player stood, and only when <c>accept</c> says the
/// container holds what left that player's bag. A request with no acceptable container stays
/// unmatched (-1) and keeps waiting.
/// </summary>
internal static class DropContainerMatcher
{
    /// <summary>A container further than this from the player's position is not theirs, metres.</summary>
    internal const float MaxDistance = 8f;

    /// <summary>
    /// For each request, the index into <paramref name="candidates"/> of its container, or -1.
    /// <paramref name="accept"/>(request, candidate) is asked at most once per pair, nearest pairs
    /// first. <paramref name="ambiguous"/>[i] is true when another unassigned candidate that would
    /// also be accepted lay within 0.5 m of the chosen distance. A request without a position is never matched.
    /// </summary>
    internal static int[] Match(IReadOnlyList<PendingDropAll> requests, IReadOnlyList<DropContainerCandidate> candidates,
        Func<int, int, bool> accept, out bool[] ambiguous)
    {
        var result = new int[requests.Count];
        ambiguous = new bool[requests.Count];
        for (int i = 0; i < result.Length; i++) result[i] = -1;
        if (requests.Count == 0 || candidates.Count == 0) return result;

        var pairs = new List<(float D, int R, int C)>();
        float max2 = MaxDistance * MaxDistance;
        for (int r = 0; r < requests.Count; r++)
        {
            var q = requests[r];
            if (!q.HasPosition) continue;
            for (int c = 0; c < candidates.Count; c++)
            {
                var k = candidates[c];
                float dx = k.X - q.X, dy = k.Y - q.Y, dz = k.Z - q.Z;
                float d2 = dx * dx + dy * dy + dz * dz;
                if (d2 <= max2) pairs.Add((d2, r, c));
            }
        }

        pairs.Sort(static (a, b) =>
        {
            int x = a.D.CompareTo(b.D);
            if (x != 0) return x;
            x = a.R.CompareTo(b.R);
            return x != 0 ? x : a.C.CompareTo(b.C);
        });
        var asked = new Dictionary<(int, int), bool>();
        bool Accepts(int r, int c)
        {
            if (asked.TryGetValue((r, c), out bool known)) return known;
            bool ok;
            try { ok = accept(r, c); }
            catch { ok = false; }
            asked[(r, c)] = ok;
            return ok;
        }

        var taken = new bool[candidates.Count];
        foreach (var (_, r, c) in pairs)
        {
            if (result[r] >= 0 || taken[c] || !Accepts(r, c)) continue;
            result[r] = c;
            taken[c] = true;
        }

        // A second free, acceptable candidate about as close means the pick was a coin toss.
        foreach (var (d, r, c) in pairs)
        {
            if (result[r] < 0 || result[r] == c || taken[c]) continue;
            var chosen = candidates[result[r]];
            var q = requests[r];
            float dx = chosen.X - q.X, dy = chosen.Y - q.Y, dz = chosen.Z - q.Z;
            float chosenD = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
            if (MathF.Sqrt(d) - chosenD <= 0.5f && Accepts(r, c)) ambiguous[r] = true;
        }

        return result;
    }

    /// <summary>
    /// True when the container holds, per item type, at least what left the bag. Containers may
    /// hold more (worn equipment the setting let go never was in the bag).
    /// </summary>
    internal static bool ContentsCover(IReadOnlyList<DroppedStack> lost, IReadOnlyList<InventorySlot> container)
    {
        if (lost.Count == 0) return false;
        var have = new Dictionary<int, int>();
        foreach (var s in container)
            if (s.Item != 0 && s.Amount > 0) have[s.Item] = (have.TryGetValue(s.Item, out int n) ? n : 0) + s.Amount;
        var want = new Dictionary<int, int>();
        foreach (var s in lost) want[s.Item] = (want.TryGetValue(s.Item, out int n) ? n : 0) + s.Amount;
        foreach (var kv in want)
            if (!have.TryGetValue(kv.Key, out int got) || got < kv.Value) return false;
        return true;
    }
}

/// <summary>
/// Which dropped-inventory containers are already accounted for, so a search only offers new
/// ones. Game-free. A baseline is taken once per world load, outside any game update, while a
/// drop-all listener exists; a matched container joins the book at once; containers no waiting
/// drop accepted join it once nothing waits any more, so a later drop is never offered them.
/// </summary>
internal sealed class DropContainerBook
{
    private readonly HashSet<long> _known = new();
    private readonly HashSet<long> _seen = new();

    /// <summary>The world epoch of the baseline; int.MinValue before the first.</summary>
    internal int Epoch { get; private set; } = int.MinValue;

    internal bool HasBaselineFor(int epoch) => Epoch == epoch;

    internal int Count => _known.Count;

    /// <summary>Start over for <paramref name="epoch"/> with every container that exists now.</summary>
    internal void Baseline(int epoch, IEnumerable<long> existing)
    {
        _known.Clear();
        _seen.Clear();
        foreach (long k in existing) _known.Add(k);
        Epoch = epoch;
    }

    /// <summary>Forget everything (unload, last listener gone).</summary>
    internal void Clear()
    {
        _known.Clear();
        _seen.Clear();
        Epoch = int.MinValue;
    }

    /// <summary>The candidates of one search pass that are not in the book; each is remembered as seen.</summary>
    internal void New(IReadOnlyList<DropContainerCandidate> scanned, List<DropContainerCandidate> into)
    {
        into.Clear();
        foreach (var c in scanned)
        {
            if (_known.Contains(c.Key)) continue;
            _seen.Add(c.Key);
            into.Add(c);
        }
    }

    /// <summary>A container was handed to a drop.</summary>
    internal void Matched(long key)
    {
        _known.Add(key);
        _seen.Remove(key);
    }

    /// <summary>Nothing waits any more: every new container seen meanwhile is accounted for.</summary>
    internal void Settle()
    {
        foreach (long k in _seen) _known.Add(k);
        _seen.Clear();
    }

    internal bool Contains(long key) => _known.Contains(key);
}

/// <summary>
/// Subscribers and a delivery queue for one drop event type. Game-free; main thread only. Hooks
/// are installed only while something listens (<see cref="ActiveChanged"/>).
/// </summary>
internal sealed class DropEventHub<T> where T : class
{
    /// <summary>Records waiting for delivery; past this the oldest are dropped and counted.</summary>
    internal const int MaxPending = 1024;

    private readonly List<Sub> _subs = new();
    private readonly Queue<Func<long, T>> _pending = new();
    private readonly Action<string>? _warn;
    private readonly string _name;
    private long _sequence;

    internal DropEventHub(string name, Action<string>? warn = null)
    {
        _name = name;
        _warn = warn;
    }

    /// <summary>Called with true when the first subscriber arrives and false when the last leaves.</summary>
    internal Action<bool>? ActiveChanged { get; set; }

    internal bool Active => _subs.Count > 0;
    internal int Subscribers => _subs.Count;
    internal int PendingCount => _pending.Count;
    internal long Raised => _sequence;
    internal long Dropped { get; private set; }
    internal long HandlerErrors { get; private set; }

    internal IDisposable Subscribe(Action<T> handler)
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        bool was = Active;
        var s = new Sub(this, handler);
        _subs.Add(s);
        if (!was) ActiveChanged?.Invoke(true);
        return s;
    }

    /// <summary>Queue a record; <paramref name="make"/> gets the sequence number at delivery.</summary>
    internal void Enqueue(Func<long, T> make)
    {
        if (!Active) return;
        if (_pending.Count >= MaxPending)
        {
            _pending.Dequeue();
            Dropped++;
        }

        _pending.Enqueue(make);
    }

    /// <summary>Deliver every queued record to every subscriber; a throwing handler is logged and the others still run.</summary>
    internal int Drain()
    {
        if (_pending.Count == 0) return 0;
        var subs = _subs.ToArray();
        int n = 0;
        while (_pending.Count > 0)
        {
            var record = _pending.Dequeue()(++_sequence);
            n++;
            foreach (var s in subs)
            {
                if (!s.Live) continue;
                try { s.Handler(record); }
                catch (Exception e)
                {
                    if (HandlerErrors++ < 8) _warn?.Invoke("Drops: a " + _name + " handler threw: " + e.Message);
                }
            }
        }

        return n;
    }

    /// <summary>Drop every subscriber and the queue (Core unload).</summary>
    internal void Reset()
    {
        bool was = Active;
        foreach (var s in _subs) s.Live = false;
        _subs.Clear();
        _pending.Clear();
        if (was) ActiveChanged?.Invoke(false);
    }

    private void Remove(Sub s)
    {
        bool was = Active;
        if (!_subs.Remove(s)) return;
        if (was && !Active)
        {
            _pending.Clear();
            ActiveChanged?.Invoke(false);
        }
    }

    private sealed class Sub : IDisposable
    {
        private readonly DropEventHub<T> _hub;

        internal Sub(DropEventHub<T> hub, Action<T> handler)
        {
            _hub = hub;
            Handler = handler;
        }

        internal Action<T> Handler { get; }
        internal bool Live { get; set; } = true;

        public void Dispose()
        {
            if (!Live) return;
            Live = false;
            _hub.Remove(this);
        }
    }
}
