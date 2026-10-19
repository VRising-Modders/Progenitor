using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Progenitor.Inventory;

/// <summary>Which way items went for one <see cref="PlayerInventoryMove"/>, seen from the container.</summary>
public enum PlayerInventoryMoveDirection
{
    /// <summary>The container gained units of the item.</summary>
    In = 0,

    /// <summary>The container lost units of the item.</summary>
    Out = 1,

    /// <summary>The container's total of the item did not change, but its slots did (a split, a sort, a drag between two of its slots).</summary>
    Within = 2,
}

/// <summary>The game request behind a <see cref="PlayerInventoryMove"/>: which client item event the server handled.</summary>
public enum PlayerInventoryMoveKind
{
    /// <summary><c>MoveItemBetweenInventoriesEvent</c>: a drag of one slot, or a shift-click.</summary>
    Move = 0,

    /// <summary><c>MoveAllItemsBetweenInventoriesEvent</c> or its V2 (take all, deposit all, optionally one item type).</summary>
    MoveAll = 1,

    /// <summary><c>SmartMergeItemsBetweenInventoriesEvent</c>: add to the stacks the target already holds.</summary>
    SmartMerge = 2,

    /// <summary><c>SplitItemEvent</c> or <c>SplitItemEventV2</c>: split a stack, within one inventory or into another.</summary>
    Split = 3,

    /// <summary><c>SortSingleInventoryEvent</c>: the sort button or key on one inventory.</summary>
    Sort = 4,

    /// <summary><c>SortAllInventoriesEvent</c>: the game's sort-all request.</summary>
    SortAll = 5,

    /// <summary><c>DropInventoryItemEvent</c>: a slot dragged out of the window onto the ground.</summary>
    Drop = 6,

    /// <summary><c>EquipItemFromInventoryEvent</c>: an item equipped straight from the inventory.</summary>
    Equip = 7,
}

/// <summary>Where the units of a <see cref="PlayerInventoryMove"/> came from (In) or went to (Out), from the request.</summary>
public enum PlayerInventoryCounterpart
{
    /// <summary>The request named only this container (a sort, a split inside it, a drag between two of its slots).</summary>
    None = 0,

    /// <summary>The acting player's own inventory (the bag).</summary>
    OwnInventory = 1,

    /// <summary>Another non-player inventory: see <see cref="PlayerInventoryMove.CounterpartInventory"/> and <see cref="PlayerInventoryMove.CounterpartContainer"/>.</summary>
    OtherInventory = 2,

    /// <summary>The ground (<see cref="PlayerInventoryMoveKind.Drop"/>).</summary>
    Ground = 3,

    /// <summary>The player's equipment (<see cref="PlayerInventoryMoveKind.Equip"/>).</summary>
    Equipment = 4,

    /// <summary>The request named an inventory the server could not resolve, or several requests of one update named this container with different other sides.</summary>
    Unknown = 5,

    /// <summary>Another player's inventory (the game refuses this unless that player's character is the container).</summary>
    OtherPlayer = 6,
}

/// <summary>
/// One change a player made by hand to a container's inventory: the server handled one of the
/// player's item requests (a drag, shift-click, take all, split, sort, smart merge, drop or equip
/// from the window) and, around that one system update, this container's total of
/// <see cref="Item"/> changed by <see cref="Amount"/> (In or Out), or only its slots changed
/// (Within). Amounts are read back from the inventory, never taken from the request, so a request
/// the game refused raises nothing and a partial move reports what really moved.
/// Raised by <c>ctx.Inventory.OnPlayerMove</c> on the main thread, from the frame tick after the move.
/// </summary>
public sealed class PlayerInventoryMove
{
    /// <summary>Make one. Public so tests and samples can describe one.</summary>
    public PlayerInventoryMove(
        long sequence,
        DateTime utcTime,
        int userIndex,
        int userVersion,
        int characterIndex,
        int characterVersion,
        PlayerInventoryMoveKind kind,
        InventoryRef inventory,
        ContainerId container,
        int item,
        int amount,
        PlayerInventoryMoveDirection direction,
        int countBefore,
        int countAfter,
        PlayerInventoryCounterpart counterpart,
        InventoryRef counterpartInventory,
        ContainerId counterpartContainer,
        bool ambiguous,
        int requests,
        bool incomplete = false)
    {
        Sequence = sequence;
        UtcTime = utcTime;
        UserIndex = userIndex;
        UserVersion = userVersion;
        CharacterIndex = characterIndex;
        CharacterVersion = characterVersion;
        Kind = kind;
        Inventory = inventory;
        Container = container;
        Item = item;
        Amount = amount;
        Direction = direction;
        CountBefore = countBefore;
        CountAfter = countAfter;
        Counterpart = counterpart;
        CounterpartInventory = counterpartInventory;
        CounterpartContainer = counterpartContainer;
        Ambiguous = ambiguous || incomplete;
        Requests = requests;
        Incomplete = incomplete;
    }

    /// <summary>Grows by one per raised move in this Core generation (gaps mean moves dropped by the queue cap).</summary>
    public long Sequence { get; }

    /// <summary>When the server handled the request.</summary>
    public DateTime UtcTime { get; }

    /// <summary>The acting user's <c>Entity.Index</c> (<c>FromCharacter.User</c>).</summary>
    public int UserIndex { get; }

    /// <summary>The acting user's <c>Entity.Version</c>.</summary>
    public int UserVersion { get; }

    /// <summary>The acting character's <c>Entity.Index</c> (<c>FromCharacter.Character</c>).</summary>
    public int CharacterIndex { get; }

    /// <summary>The acting character's <c>Entity.Version</c>.</summary>
    public int CharacterVersion { get; }

    /// <summary>Which request the server handled.</summary>
    public PlayerInventoryMoveKind Kind { get; }

    /// <summary>The container's inventory: owner (the chest, station, heart, servant) and instance (1 is a refinement station's output).</summary>
    public InventoryRef Inventory { get; }

    /// <summary>The owner's stable id; <see cref="ContainerId.None"/> for an inventory that is not a placed castle model (a servant, a world container).</summary>
    public ContainerId Container { get; }

    /// <summary>True when the owner is a placed castle container, station or heart (it has a <see cref="ContainerId"/>).</summary>
    public bool IsCastleContainer => !Container.IsNone;

    /// <summary>Item <c>PrefabGUID</c> hash.</summary>
    public int Item { get; }

    /// <summary>Units moved: gained (In), lost (Out), or moved between this container's slots (Within). Always above 0.</summary>
    public int Amount { get; }

    /// <summary>In, Out or Within, seen from the container.</summary>
    public PlayerInventoryMoveDirection Direction { get; }

    /// <summary>The container's total of <see cref="Item"/> just before the system update.</summary>
    public int CountBefore { get; }

    /// <summary>The container's total of <see cref="Item"/> just after it.</summary>
    public int CountAfter { get; }

    /// <summary>The other side the request named.</summary>
    public PlayerInventoryCounterpart Counterpart { get; }

    /// <summary>The other inventory when <see cref="Counterpart"/> is <see cref="PlayerInventoryCounterpart.OtherInventory"/>; <see cref="InventoryRef.None"/> otherwise.</summary>
    public InventoryRef CounterpartInventory { get; }

    /// <summary>That other inventory's stable id, when it is a placed castle model.</summary>
    public ContainerId CounterpartContainer { get; }

    /// <summary>
    /// True when requests of more than one user touched this inventory in the same system update:
    /// the amounts are still exact for the container, but which user moved what is not known.
    /// <see cref="UserIndex"/> is then the first such request's user.
    /// </summary>
    public bool Ambiguous { get; }

    /// <summary>How many requests of that update named this inventory (two fast clicks in one frame are one move with 2).</summary>
    public int Requests { get; }

    /// <summary>
    /// True when the server could not observe every request of that update: more than 64 requests
    /// (the rest were not read), a request naming an inventory that did not resolve, or the
    /// per-update inventory cap. The amounts are still the container's real change, but an unseen
    /// request (possibly another player's) may have made it, so <see cref="Ambiguous"/> is also true.
    /// </summary>
    public bool Incomplete { get; }

#if !PROGENITOR_TESTS
    /// <summary>The acting user entity. Only meaningful in the world load the move happened in.</summary>
    public Unity.Entities.Entity User => new() { Index = UserIndex, Version = UserVersion };

    /// <summary>The acting character entity.</summary>
    public Unity.Entities.Entity Character => new() { Index = CharacterIndex, Version = CharacterVersion };
#endif

    /// <summary>One log line, for a move log.</summary>
    public override string ToString()
    {
        var c = CultureInfo.InvariantCulture;
        var sb = new StringBuilder(160);
        sb.Append('#').Append(Sequence.ToString(c)).Append(' ')
            .Append("user=").Append(UserIndex.ToString(c)).Append(':').Append(UserVersion.ToString(c)).Append(' ')
            .Append(Kind).Append(' ').Append(Direction).Append(' ')
            .Append("item=").Append(Item.ToString(c)).Append(" x").Append(Amount.ToString(c)).Append(' ')
            .Append("container=").Append(Container.IsNone ? Inventory.ToString() : Container.ToString())
            .Append(Inventory.Instance == 0 ? "" : "#" + Inventory.Instance.ToString(c)).Append(' ')
            .Append("count=").Append(CountBefore.ToString(c)).Append("->").Append(CountAfter.ToString(c)).Append(' ')
            .Append("other=").Append(Counterpart);
        if (Counterpart == PlayerInventoryCounterpart.OtherInventory)
            sb.Append('(').Append(CounterpartContainer.IsNone ? CounterpartInventory.ToString() : CounterpartContainer.ToString()).Append(')');
        if (Requests > 1) sb.Append(" requests=").Append(Requests.ToString(c));
        if (Ambiguous) sb.Append(" ambiguous");
        if (Incomplete) sb.Append(" incomplete");
        return sb.ToString();
    }
}

// ---- game-free logic behind ctx.Inventory.OnPlayerMove ------------------------------------------

/// <summary>One inventory row as the move observer reads it: item hash, amount, item entity (0 for none).</summary>
internal readonly struct MoveSlotRow
{
    internal MoveSlotRow(int item, int amount, long itemEntity)
    {
        Item = item;
        Amount = amount;
        ItemEntity = itemEntity;
    }

    internal int Item { get; }
    internal int Amount { get; }

    /// <summary><c>(Index &lt;&lt; 32) | Version</c> of the slot's item entity, 0 when the item has none.</summary>
    internal long ItemEntity { get; }

    internal bool IsEmpty => Item == 0 || Amount <= 0;
}

/// <summary>What one inventory's update did to one item.</summary>
internal readonly struct ItemMoveDelta
{
    internal ItemMoveDelta(int item, int @in, int @out, int within, int before, int after, int outWithEntity = 0)
    {
        OutWithEntity = outWithEntity;
        Item = item;
        In = @in;
        Out = @out;
        Within = within;
        Before = before;
        After = after;
    }

    internal int Item { get; }
    internal int In { get; }
    internal int Out { get; }
    internal int Within { get; }
    internal int Before { get; }
    internal int After { get; }

    /// <summary>Units of <see cref="Out"/> that left in rows with their own item entity (gear, jewels).</summary>
    internal int OutWithEntity { get; }
}

/// <summary>
/// The before/after comparison of one inventory. Game-free.
///
/// <para><b>Rules.</b> Per item type: rows without an item entity are interchangeable, so their
/// total's change is the moved amount (a gain is In, a loss Out). Rows with an item entity (gear,
/// anything with its own state) are matched by entity: an entity that left is Out by its amount,
/// one that arrived is In, one on both sides contributes its amount change. So swapping one sword
/// for another of the same prefab is one Out and one In, not nothing. When the item had neither
/// In nor Out but its per-slot amounts changed, the units that arrived at slots are Within.</para>
/// </summary>
internal sealed class PlayerMoveDiffer
{
    private readonly Dictionary<int, Acc> _items = new();
    private readonly Dictionary<long, int> _entityBefore = new();
    private readonly List<int> _order = new();

    private sealed class Acc
    {
        internal int PlainBefore, PlainAfter, EntityIn, EntityOut, Before, After, Within;
    }

    private readonly Stack<Acc> _pool = new();

    /// <summary>Compare <paramref name="before"/> with <paramref name="after"/>; one delta per item that changed, in first-seen order.</summary>
    internal void Diff(IReadOnlyList<MoveSlotRow> before, IReadOnlyList<MoveSlotRow> after, List<ItemMoveDelta> into)
    {
        into.Clear();
        foreach (var a in _items.Values) _pool.Push(a);
        _items.Clear();
        _entityBefore.Clear();
        _order.Clear();

        for (int i = 0; i < before.Count; i++)
        {
            var r = before[i];
            if (r.IsEmpty) continue;
            var acc = Get(r.Item);
            acc.Before += r.Amount;
            if (r.ItemEntity == 0) acc.PlainBefore += r.Amount;
            else _entityBefore[r.ItemEntity] = _entityBefore.TryGetValue(r.ItemEntity, out int prev) ? prev + r.Amount : r.Amount;
        }

        for (int i = 0; i < after.Count; i++)
        {
            var r = after[i];
            if (r.IsEmpty) continue;
            var acc = Get(r.Item);
            acc.After += r.Amount;
            if (r.ItemEntity == 0)
            {
                acc.PlainAfter += r.Amount;
                continue;
            }

            if (_entityBefore.TryGetValue(r.ItemEntity, out int had))
            {
                // On both sides: its amount change is interchangeable units.
                _entityBefore.Remove(r.ItemEntity);
                acc.PlainBefore += had;
                acc.PlainAfter += r.Amount;
            }
            else
            {
                acc.EntityIn += r.Amount;
            }
        }

        // Entities that left. Their item type is the one their row carried.
        if (_entityBefore.Count > 0)
        {
            for (int i = 0; i < before.Count; i++)
            {
                var r = before[i];
                if (r.IsEmpty || r.ItemEntity == 0 || !_entityBefore.Remove(r.ItemEntity, out int amount)) continue;
                Get(r.Item).EntityOut += amount;
            }
        }

        // Slot-level arrivals, for Within.
        int slots = Math.Max(before.Count, after.Count);
        for (int s = 0; s < slots; s++)
        {
            var b = s < before.Count ? before[s] : default;
            var a = s < after.Count ? after[s] : default;
            if (!a.IsEmpty && (b.IsEmpty || b.Item != a.Item))
                Get(a.Item).Within += a.Amount;
            else if (!a.IsEmpty && b.Item == a.Item && a.Amount > b.Amount)
                Get(a.Item).Within += a.Amount - b.Amount;
            else if (!a.IsEmpty && b.Item == a.Item && a.Amount == b.Amount && a.ItemEntity != b.ItemEntity)
                Get(a.Item).Within += a.Amount;
        }

        foreach (int item in _order)
        {
            var acc = _items[item];
            int plain = acc.PlainAfter - acc.PlainBefore;
            int @in = acc.EntityIn + Math.Max(0, plain);
            int @out = acc.EntityOut + Math.Max(0, -plain);
            int within = @in == 0 && @out == 0 ? acc.Within : 0;
            if (@in == 0 && @out == 0 && within == 0) continue;
            into.Add(new ItemMoveDelta(item, @in, @out, within, acc.Before, acc.After, acc.EntityOut));
        }
    }

    private Acc Get(int item)
    {
        if (_items.TryGetValue(item, out var acc)) return acc;
        acc = _pool.Count > 0 ? _pool.Pop() : new Acc();
        acc.PlainBefore = acc.PlainAfter = acc.EntityIn = acc.EntityOut = acc.Before = acc.After = acc.Within = 0;
        _items[item] = acc;
        _order.Add(item);
        return acc;
    }
}

/// <summary>What one side of a request named, once the server resolved it.</summary>
internal enum MoveSideRole
{
    /// <summary>The event has no such side (a sort or a v1 split names one inventory).</summary>
    None = 0,

    /// <summary>The acting player's own character or bag.</summary>
    OwnPlayer = 1,

    /// <summary>A non-player inventory owner (a chest, station, heart, servant): its inventories are observed.</summary>
    Inventory = 2,

    /// <summary>The id did not resolve.</summary>
    Unknown = 3,

    /// <summary>The ground (drop).</summary>
    Ground = 4,

    /// <summary>The player's equipment (equip from inventory).</summary>
    Equipment = 5,

    /// <summary>Another player's character or bag.</summary>
    OtherPlayer = 6,
}

/// <summary>A move the batch found, before the delivery tick turns it into a <see cref="PlayerInventoryMove"/>.</summary>
internal readonly struct PendingPlayerMove
{
    internal PendingPlayerMove(long user, long character, PlayerInventoryMoveKind kind, InventoryRef inventory, int item, int amount,
        PlayerInventoryMoveDirection direction, int before, int after, PlayerInventoryCounterpart counterpart, InventoryRef counterpartInventory,
        bool ambiguous, int requests, DateTime utc, bool incomplete = false)
    {
        Incomplete = incomplete;
        User = user;
        Character = character;
        Kind = kind;
        Inventory = inventory;
        Item = item;
        Amount = amount;
        Direction = direction;
        Before = before;
        After = after;
        Counterpart = counterpart;
        CounterpartInventory = counterpartInventory;
        Ambiguous = ambiguous;
        Requests = requests;
        Utc = utc;
    }

    internal long User { get; }
    internal long Character { get; }
    internal PlayerInventoryMoveKind Kind { get; }
    internal InventoryRef Inventory { get; }
    internal int Item { get; }
    internal int Amount { get; }
    internal PlayerInventoryMoveDirection Direction { get; }
    internal int Before { get; }
    internal int After { get; }
    internal PlayerInventoryCounterpart Counterpart { get; }
    internal InventoryRef CounterpartInventory { get; }
    internal bool Ambiguous { get; }
    internal bool Incomplete { get; }
    internal int Requests { get; }
    internal DateTime Utc { get; }
}

/// <summary>
/// One system update's requests and the inventories they name, read before and after the update.
/// Game-free: the runtime adds the resolved requests and the rows it read, then asks for the moves.
///
/// <para><b>Attribution.</b> The diff is per inventory, over the whole update, because the game
/// handles every request of the update in one call. Requests that name the same inventory are
/// one move record per item with <see cref="PendingPlayerMove.Requests"/> above 1; when they come
/// from more than one user the record is <see cref="PendingPlayerMove.Ambiguous"/> and carries the
/// first request's user. Requests that name different inventories are attributed exactly.</para>
/// </summary>
internal sealed class PlayerMoveBatch
{
    /// <summary>Requests read per system update; the rest of a burst is counted and not observed.</summary>
    internal const int MaxRequests = 64;

    /// <summary>Inventories observed per system update.</summary>
    internal const int MaxInventories = 32;

    private readonly List<Request> _requests = new();
    private readonly List<Inv> _inventories = new();
    private readonly Dictionary<long, int> _byKey = new();
    private readonly PlayerMoveDiffer _differ = new();
    private readonly List<ItemMoveDelta> _deltas = new();
    private readonly List<int> _touching = new();

    private sealed class Request
    {
        internal long User, Character;
        internal MoveSideRole RoleA, RoleB;
        internal readonly List<int> SideA = new(2), SideB = new(2);
        internal bool Changed;
    }

    private sealed class Inv
    {
        internal long Key;
        internal InventoryRef Ref;
        internal readonly List<MoveSlotRow> Before = new();
        internal readonly List<MoveSlotRow> After = new();
        internal bool HasBefore, HasAfter;
    }

    private readonly Stack<Request> _requestPool = new();
    private readonly Stack<Inv> _invPool = new();

    /// <summary>The request kind of this update.</summary>
    internal PlayerInventoryMoveKind Kind { get; private set; }

    /// <summary>True between <see cref="Begin"/> and <see cref="Clear"/>.</summary>
    internal bool Open { get; private set; }

    /// <summary>Opaque owner of the open batch (the runtime's per-system watcher).</summary>
    internal object? Owner { get; private set; }

    internal int RequestCount => _requests.Count;
    internal int InventoryCount => _inventories.Count;

    /// <summary>Requests that changed none of their observed inventories in the last <see cref="Emit"/>.</summary>
    internal int Unchanged { get; private set; }

    /// <summary>Inventories whose rows could not be read before or after.</summary>
    internal int Unread { get; private set; }

    /// <summary>True when this update had requests the batch could not observe (cap, an unresolved side): every move it emits is marked incomplete and ambiguous.</summary>
    internal bool Incomplete { get; private set; }

    /// <summary>Record that a request of this update was not (fully) observed.</summary>
    internal void MarkIncomplete() => Incomplete = true;

    /// <summary>Start a batch (clears any earlier one that never got its after-read).</summary>
    internal void Begin(object owner, PlayerInventoryMoveKind kind)
    {
        Clear();
        Owner = owner;
        Kind = kind;
        Open = true;
    }

    /// <summary>Forget everything.</summary>
    internal void Clear()
    {
        foreach (var r in _requests) _requestPool.Push(r);
        foreach (var i in _inventories) _invPool.Push(i);
        _requests.Clear();
        _inventories.Clear();
        _byKey.Clear();
        Owner = null;
        Open = false;
        Incomplete = false;
    }

    /// <summary>The inventory's index in this batch (added once per key); -1 when the batch is full.</summary>
    internal int AddInventory(long key, InventoryRef inventory)
    {
        if (_byKey.TryGetValue(key, out int at)) return at;
        if (_inventories.Count >= MaxInventories)
        {
            Incomplete = true;
            return -1;
        }
        var inv = _invPool.Count > 0 ? _invPool.Pop() : new Inv();
        inv.Key = key;
        inv.Ref = inventory;
        inv.Before.Clear();
        inv.After.Clear();
        inv.HasBefore = inv.HasAfter = false;
        _byKey[key] = _inventories.Count;
        _inventories.Add(inv);
        return _inventories.Count - 1;
    }

    /// <summary>The batch key of inventory <paramref name="index"/> (the entity that holds the slots).</summary>
    internal long KeyOf(int index) => _inventories[index].Key;

    /// <summary>
    /// Add one request with its two sides (inventory indices from <see cref="AddInventory"/>). False
    /// when the batch already holds <see cref="MaxRequests"/>.
    /// </summary>
    internal bool AddRequest(long user, long character, MoveSideRole roleA, IReadOnlyList<int>? sideA, MoveSideRole roleB, IReadOnlyList<int>? sideB)
    {
        if (_requests.Count >= MaxRequests)
        {
            Incomplete = true;
            return false;
        }

        if (roleA == MoveSideRole.Unknown || roleB == MoveSideRole.Unknown) Incomplete = true;
        var r = _requestPool.Count > 0 ? _requestPool.Pop() : new Request();
        r.User = user;
        r.Character = character;
        r.RoleA = roleA;
        r.RoleB = roleB;
        r.SideA.Clear();
        r.SideB.Clear();
        r.Changed = false;
        if (sideA != null) foreach (int i in sideA) if (i >= 0) r.SideA.Add(i);
        if (sideB != null) foreach (int i in sideB) if (i >= 0) r.SideB.Add(i);
        _requests.Add(r);
        return true;
    }

    /// <summary>The rows read before (<paramref name="after"/> false) or after the update; null when the read failed.</summary>
    internal void SetRows(int index, bool after, IReadOnlyList<MoveSlotRow>? rows)
    {
        var inv = _inventories[index];
        var into = after ? inv.After : inv.Before;
        into.Clear();
        if (rows != null) for (int i = 0; i < rows.Count; i++) into.Add(rows[i]);
        if (after) inv.HasAfter = rows != null;
        else inv.HasBefore = rows != null;
    }

    /// <summary>Compare every observed inventory and append the moves, then <see cref="Clear"/>.</summary>
    internal void Emit(DateTime utc, List<PendingPlayerMove> into)
    {
        Unchanged = 0;
        Unread = 0;
        for (int i = 0; i < _inventories.Count; i++)
        {
            var inv = _inventories[i];
            _touching.Clear();
            for (int r = 0; r < _requests.Count; r++)
                if (_requests[r].SideA.Contains(i) || _requests[r].SideB.Contains(i)) _touching.Add(r);
            if (_touching.Count == 0) continue;
            if (!inv.HasBefore || !inv.HasAfter)
            {
                Unread++;
                continue;
            }

            _differ.Diff(inv.Before, inv.After, _deltas);
            if (_deltas.Count == 0) continue;

            var first = _requests[_touching[0]];
            bool ambiguous = false;
            foreach (int r in _touching)
            {
                _requests[r].Changed = true;
                if (_requests[r].User != first.User) ambiguous = true;
            }

            Counterpart(first, i, out var counterpart, out var other);
            // Requests of one update that name different other sides: which of them moved is not known.
            for (int t = 1; t < _touching.Count; t++)
            {
                Counterpart(_requests[_touching[t]], i, out var c2, out var o2);
                if (c2 == counterpart && o2 == other) continue;
                counterpart = PlayerInventoryCounterpart.Unknown;
                other = InventoryRef.None;
                break;
            }

            foreach (var d in _deltas)
            {
                if (d.Out > 0) into.Add(Make(first, inv, d, PlayerInventoryMoveDirection.Out, d.Out, counterpart, other, ambiguous, utc));
                if (d.In > 0) into.Add(Make(first, inv, d, PlayerInventoryMoveDirection.In, d.In, counterpart, other, ambiguous, utc));
                if (d.Within > 0) into.Add(Make(first, inv, d, PlayerInventoryMoveDirection.Within, d.Within, PlayerInventoryCounterpart.None, InventoryRef.None, ambiguous, utc));
            }
        }

        foreach (var r in _requests)
            if (!r.Changed) Unchanged++;
        Clear();
    }

    private PendingPlayerMove Make(Request first, Inv inv, ItemMoveDelta d, PlayerInventoryMoveDirection direction, int amount,
        PlayerInventoryCounterpart counterpart, InventoryRef other, bool ambiguous, DateTime utc) =>
        new(first.User, first.Character, Kind, inv.Ref, d.Item, amount, direction, d.Before, d.After, counterpart, other, ambiguous || Incomplete, _touching.Count, utc, Incomplete);

    /// <summary>The request's other side, seen from inventory <paramref name="index"/>.</summary>
    private void Counterpart(Request r, int index, out PlayerInventoryCounterpart counterpart, out InventoryRef other)
    {
        bool inA = r.SideA.Contains(index), inB = r.SideB.Contains(index);
        other = InventoryRef.None;
        if (inA && inB)
        {
            counterpart = PlayerInventoryCounterpart.None;
            return;
        }

        var role = inA ? r.RoleB : r.RoleA;
        var side = inA ? r.SideB : r.SideA;
        switch (role)
        {
            case MoveSideRole.None:
                counterpart = PlayerInventoryCounterpart.None;
                return;
            case MoveSideRole.OwnPlayer:
                counterpart = PlayerInventoryCounterpart.OwnInventory;
                return;
            case MoveSideRole.OtherPlayer:
                counterpart = PlayerInventoryCounterpart.OtherPlayer;
                return;
            case MoveSideRole.Ground:
                counterpart = PlayerInventoryCounterpart.Ground;
                return;
            case MoveSideRole.Equipment:
                counterpart = PlayerInventoryCounterpart.Equipment;
                return;
            case MoveSideRole.Inventory:
                if (side.Count == 0)
                {
                    counterpart = PlayerInventoryCounterpart.Unknown;
                    return;
                }

                // A station side names all its instances: prefer the one whose content changed.
                int pick = side[0];
                foreach (int s in side)
                {
                    var o = _inventories[s];
                    if (o.HasBefore && o.HasAfter && !SameRows(o.Before, o.After)) { pick = s; break; }
                }

                counterpart = PlayerInventoryCounterpart.OtherInventory;
                other = _inventories[pick].Ref;
                return;
            default:
                counterpart = PlayerInventoryCounterpart.Unknown;
                return;
        }
    }

    private static bool SameRows(List<MoveSlotRow> a, List<MoveSlotRow> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (a[i].Item != b[i].Item || a[i].Amount != b[i].Amount || a[i].ItemEntity != b[i].ItemEntity) return false;
        return true;
    }
}

/// <summary>
/// Subscribers, the delivery queue and the recent-moves ring behind <c>ctx.Inventory.OnPlayerMove</c>
/// and the Dev bridge <c>inventory_moves</c> op. Game-free; main thread only.
/// </summary>
internal sealed class PlayerMoveHub
{
    /// <summary>Moves waiting for the next delivery tick; past this the oldest are dropped and counted.</summary>
    internal const int MaxPending = 1024;

    /// <summary>Recent moves kept for the bridge readback.</summary>
    internal const int RingSize = 256;

    private readonly List<Subscription> _subs = new();
    private readonly Queue<PendingPlayerMove> _pending = new();
    private readonly PlayerInventoryMove?[] _ring = new PlayerInventoryMove?[RingSize];
    private readonly Action<string>? _warn;
    private int _ringNext, _ringCount;
    private long _sequence;
    private int _recorders;

    internal PlayerMoveHub(Action<string>? warn = null) => _warn = warn;

    /// <summary>Called with true when the first listener (subscriber or recorder) arrives and false when the last leaves.</summary>
    internal Action<bool>? ActiveChanged { get; set; }

    /// <summary>True while anything listens: the observer's hooks are installed only then.</summary>
    internal bool Active => _subs.Count > 0 || _recorders > 0;

    internal int Subscribers => _subs.Count;
    internal bool Recording => _recorders > 0;
    internal int PendingCount => _pending.Count;
    internal long Raised => _sequence;

    /// <summary>Moves dropped because the queue was full.</summary>
    internal long Dropped { get; private set; }

    /// <summary>Handler calls that threw.</summary>
    internal long HandlerErrors { get; private set; }

    internal IDisposable Subscribe(Action<PlayerInventoryMove> handler)
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        bool was = Active;
        var s = new Subscription(this, handler);
        _subs.Add(s);
        if (!was) ActiveChanged?.Invoke(true);
        return s;
    }

    /// <summary>Turn the bridge recorder on or off (it listens like a subscriber with no handler).</summary>
    internal void SetRecording(bool on)
    {
        bool was = Active;
        _recorders = on ? 1 : 0;
        if (was == Active) return;
        if (!Active) _pending.Clear();
        ActiveChanged?.Invoke(Active);
    }

    private void Remove(Subscription s)
    {
        bool was = Active;
        if (!_subs.Remove(s)) return;
        if (was && !Active)
        {
            _pending.Clear();
            ActiveChanged?.Invoke(false);
        }
    }

    /// <summary>Queue moves for the next delivery.</summary>
    internal void Enqueue(List<PendingPlayerMove> moves)
    {
        if (!Active) return;
        foreach (var m in moves)
        {
            if (_pending.Count >= MaxPending)
            {
                _pending.Dequeue();
                Dropped++;
            }

            _pending.Enqueue(m);
        }
    }

    /// <summary>
    /// Deliver the queued moves: build each <see cref="PlayerInventoryMove"/> (the container ids
    /// resolved through <paramref name="idOf"/>), keep it in the ring and call every subscriber.
    /// A throwing handler is logged (first few) and the others still run.
    /// </summary>
    internal int Drain(Func<InventoryRef, ContainerId> idOf, Action<PlayerInventoryMove>? log = null)
    {
        int n = 0;
        if (_pending.Count == 0) return 0;
        // One snapshot per drain: a handler may dispose its own subscription (checked through Active below).
        var subs = _subs.ToArray();
        while (_pending.Count > 0)
        {
            var p = _pending.Dequeue();
            var move = new PlayerInventoryMove(++_sequence, p.Utc,
                (int)(p.User >> 32), (int)p.User, (int)(p.Character >> 32), (int)p.Character,
                p.Kind, p.Inventory, Safe(idOf, p.Inventory), p.Item, p.Amount, p.Direction, p.Before, p.After,
                p.Counterpart, p.CounterpartInventory,
                p.Counterpart == PlayerInventoryCounterpart.OtherInventory ? Safe(idOf, p.CounterpartInventory) : ContainerId.None,
                p.Ambiguous, p.Requests, p.Incomplete);
            _ring[_ringNext] = move;
            _ringNext = (_ringNext + 1) % RingSize;
            if (_ringCount < RingSize) _ringCount++;
            n++;
            log?.Invoke(move);
            foreach (var s in subs)
            {
                if (!s.Active) continue;
                try { s.Handler(move); }
                catch (Exception e)
                {
                    if (HandlerErrors++ < 8) _warn?.Invoke("Inventory: a player-move handler threw: " + e.Message);
                }
            }
        }

        return n;
    }

    private static ContainerId Safe(Func<InventoryRef, ContainerId> idOf, InventoryRef r)
    {
        if (r.IsNone) return ContainerId.None;
        try { return idOf(r); }
        catch { return ContainerId.None; }
    }

    /// <summary>The last <paramref name="count"/> moves, oldest first.</summary>
    internal List<PlayerInventoryMove> Recent(int count)
    {
        count = Math.Max(0, Math.Min(count, _ringCount));
        var list = new List<PlayerInventoryMove>(count);
        int start = (_ringNext - count + RingSize) % RingSize;
        for (int i = 0; i < count; i++)
        {
            var m = _ring[(start + i) % RingSize];
            if (m != null) list.Add(m);
        }

        return list;
    }

    /// <summary>Forget the ring (the bridge's <c>clear</c>).</summary>
    internal void ClearRecent()
    {
        Array.Clear(_ring, 0, _ring.Length);
        _ringNext = _ringCount = 0;
    }

    /// <summary>Drop every subscriber, the recorder and the queue (Core unload).</summary>
    internal void Reset()
    {
        bool was = Active;
        foreach (var s in _subs) s.Active = false;
        _subs.Clear();
        _recorders = 0;
        _pending.Clear();
        if (was) ActiveChanged?.Invoke(false);
    }

    private sealed class Subscription : IDisposable
    {
        private readonly PlayerMoveHub _hub;

        internal Subscription(PlayerMoveHub hub, Action<PlayerInventoryMove> handler)
        {
            _hub = hub;
            Handler = handler;
        }

        internal Action<PlayerInventoryMove> Handler { get; }
        internal bool Active { get; set; } = true;

        public void Dispose()
        {
            if (!Active) return;
            Active = false;
            _hub.Remove(this);
        }
    }
}
