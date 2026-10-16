using System;
using System.Collections.Generic;
using System.Globalization;

namespace Progenitor.Inventory;

/// <summary>
/// One inventory, as a session handle: the owner entity (a chest, a station, a castle heart, a
/// player character, a servant) plus which of its inventories (instance 0 is the main inventory
/// and a refinement station's input, instance 1 a refinement station's output), stamped with the
/// world-load epoch it was taken in.
///
/// <para><b>Session-scoped.</b> Entity ids are re-assigned when a save loads, so a reference
/// taken before a reload is <see cref="IsStaleIn"/> afterwards and every inventory call refuses
/// it. Persist a <see cref="ContainerId"/> instead and resolve it again. Value type; building one
/// allocates nothing.</para>
/// </summary>
public readonly struct InventoryRef : IEquatable<InventoryRef>
{
    /// <summary>Owner <c>Entity.Index</c>.</summary>
    public int OwnerIndex { get; }

    /// <summary>Owner <c>Entity.Version</c>.</summary>
    public int OwnerVersion { get; }

    /// <summary>
    /// Inventory instance on the owner: 0 main (or a refinement station's input), 1 a
    /// refinement station's output. Matches the owner's <c>InventoryInstanceElement</c> rows.
    /// </summary>
    public int Instance { get; }

    /// <summary>World-load epoch this reference was taken in.</summary>
    public int Epoch { get; }

    /// <summary>Construct from raw parts. Plugins normally use <c>ctx.Inventory.Of</c>.</summary>
    public InventoryRef(int ownerIndex, int ownerVersion, int instance, int epoch)
    {
        OwnerIndex = ownerIndex;
        OwnerVersion = ownerVersion;
        Instance = instance;
        Epoch = epoch;
    }

    /// <summary>True for the default (no inventory) value.</summary>
    public bool IsNone => OwnerIndex == 0 && OwnerVersion == 0;

    /// <summary>The default (no inventory) value.</summary>
    public static InventoryRef None => default;

    /// <summary>True when this reference was taken in a different world-load epoch.</summary>
    public bool IsStaleIn(int currentEpoch) => !IsNone && Epoch != currentEpoch;

    /// <summary>Same inventory: owner and instance. The epoch is part of equality too.</summary>
    public bool Equals(InventoryRef other) =>
        OwnerIndex == other.OwnerIndex && OwnerVersion == other.OwnerVersion
        && Instance == other.Instance && Epoch == other.Epoch;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is InventoryRef other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(OwnerIndex, OwnerVersion, Instance, Epoch);

    /// <summary><c>index:version</c>, plus <c>#1</c> for the output instance.</summary>
    public override string ToString() =>
        IsNone
            ? "none"
            : OwnerIndex.ToString(CultureInfo.InvariantCulture) + ":" + OwnerVersion.ToString(CultureInfo.InvariantCulture)
              + (Instance == 0 ? "" : "#" + Instance.ToString(CultureInfo.InvariantCulture));

    /// <summary>Value equality.</summary>
    public static bool operator ==(InventoryRef left, InventoryRef right) => left.Equals(right);

    /// <summary>Value inequality.</summary>
    public static bool operator !=(InventoryRef left, InventoryRef right) => !left.Equals(right);

#if !PROGENITOR_TESTS
    /// <summary>The owner entity. Only meaningful in the epoch the reference was taken in.</summary>
    public Unity.Entities.Entity Owner => new() { Index = OwnerIndex, Version = OwnerVersion };

    /// <summary>A reference to <paramref name="owner"/>'s inventory <paramref name="instance"/> in the current epoch.</summary>
    public static InventoryRef For(Unity.Entities.Entity owner, int instance = 0) =>
        new(owner.Index, owner.Version, instance, Progenitor.WorldState.WorldEpoch.Current);
#endif
}

/// <summary>
/// One inventory slot. A <c>readonly struct</c> so <c>ReadInto</c> fills a caller-owned array
/// without allocating per slot.
/// </summary>
public readonly struct InventorySlot
{
    /// <summary>Slot index in the inventory buffer.</summary>
    public int Slot { get; }

    /// <summary>Item <c>PrefabGUID</c> hash; 0 for an empty slot.</summary>
    public int Item { get; }

    /// <summary>Amount in the slot; 0 for an empty slot.</summary>
    public int Amount { get; }

    /// <summary>
    /// Effective stack size of the slot's item: the slot's <c>MaxAmountOverride</c> when it is
    /// above 0, else the item's <c>ItemData.MaxAmount</c>. 0 for an empty slot.
    /// </summary>
    public int MaxStack { get; }

    /// <summary>The slot's raw <c>MaxAmountOverride</c> (0 = none). It also applies to an item added into the empty slot.</summary>
    public int StackOverride { get; }

    /// <summary>True when the slot's item has its own item entity (gear and other items with per-copy state).</summary>
    public bool HasItemEntity { get; }

    /// <summary>Construct a slot row.</summary>
    public InventorySlot(int slot, int item, int amount, int maxStack, int stackOverride, bool hasItemEntity)
    {
        Slot = slot;
        Item = item;
        Amount = amount;
        MaxStack = maxStack;
        StackOverride = stackOverride;
        HasItemEntity = hasItemEntity;
    }

    /// <summary>True when the slot holds nothing. The game treats item 0 as empty.</summary>
    public bool IsEmpty => Item == 0;

    /// <inheritdoc />
    public override string ToString() =>
        IsEmpty ? "#" + Slot + " empty" : "#" + Slot + " " + Item + " x" + Amount + "/" + MaxStack;
}

/// <summary>
/// Which <b>source</b> slots an item-based move (<c>Transfer</c>, <c>TransferAll</c>) may draw
/// from. The default value allows every slot. A filter is slot numbers only: it applies the same
/// way to whatever inventory is the source, so <see cref="SkipActionBar"/> means "slots 0–7" in a
/// chest too. Use it with a player's bag, where those slots are the action bar. The target side
/// is never filtered: where the game puts arriving items is the game's add rule. Value type;
/// building one allocates nothing.
/// </summary>
public readonly struct InventorySlotFilter : IEquatable<InventorySlotFilter>
{
    /// <summary>
    /// Slots on a player's action bar: bag slots 0 to 7, the keys <c>ActionBar1</c> to
    /// <c>ActionBar8</c>. The game's own server add for a player starts stackables, jewels and
    /// tech items at slot 8 and only falls back to slots 0–7 afterwards.
    /// </summary>
    public const int ActionBarSlots = 8;

    private readonly int _first;
    private readonly int _count;
    private readonly bool _only;

    private InventorySlotFilter(int first, int count, bool only)
    {
        _first = first;
        _count = count;
        _only = only;
    }

    /// <summary>Every slot (the default value).</summary>
    public static InventorySlotFilter All => default;

    /// <summary>Every slot except a player's action bar (slots 0 to <see cref="ActionBarSlots"/> - 1).</summary>
    public static InventorySlotFilter SkipActionBar => new(0, ActionBarSlots, false);

    /// <summary>
    /// Every slot except <paramref name="count"/> slots starting at <paramref name="first"/>.
    /// A negative start or count, or a count of 0, skips nothing.
    /// </summary>
    public static InventorySlotFilter SkipSlots(int first, int count) =>
        first < 0 || count <= 0 ? default : new(first, count, false);

    /// <summary>Only <paramref name="slot"/> (what <c>TransferSlot</c> uses).</summary>
    internal static InventorySlotFilter Only(int slot) => new(slot, 1, true);

    /// <summary>True when the filter allows every slot.</summary>
    public bool IsAll => _count <= 0 && !_only;

    /// <summary>First slot of the skipped (or, for a single-slot filter, the allowed) range.</summary>
    public int First => _first;

    /// <summary>How many slots the range covers; 0 for <see cref="All"/>.</summary>
    public int Count => _count;

    /// <summary>True when an item-based move may take from <paramref name="slot"/>.</summary>
    public bool Allows(int slot)
    {
        bool inRange = _count > 0 && slot >= _first && slot - _first < _count;
        return _only ? inRange : !inRange;
    }

    /// <inheritdoc />
    public bool Equals(InventorySlotFilter other) => _first == other._first && _count == other._count && _only == other._only;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is InventorySlotFilter other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(_first, _count, _only);

    /// <summary><c>all</c>, <c>skip 0..7</c> or <c>only 12</c>.</summary>
    public override string ToString() =>
        IsAll ? "all"
        : _only ? "only " + _first.ToString(CultureInfo.InvariantCulture)
        : "skip " + _first.ToString(CultureInfo.InvariantCulture) + ".." + ((long)_first + _count - 1).ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// The rule a container applies to what it accepts, from the game's <c>RestrictedInventory</c>
/// component on the inventory entity. Both 0 means anything goes.
/// </summary>
public readonly struct InventoryRestriction
{
    /// <summary><c>RestrictedInventory.RestrictedItemType</c>: the only item accepted, 0 = any.</summary>
    public int Item { get; }

    /// <summary><c>RestrictedInventory.RestrictedItemCategory</c> flags: one must match, 0 = any.</summary>
    public long Category { get; }

    /// <summary>Construct a restriction.</summary>
    public InventoryRestriction(int item, long category)
    {
        Item = item;
        Category = category;
    }

    /// <summary>True when the container accepts every item.</summary>
    public bool IsOpen => Item == 0 && Category == 0;

    /// <summary>No restriction.</summary>
    public static InventoryRestriction None => default;

    /// <inheritdoc />
    public override string ToString() =>
        IsOpen ? "any" : "item=" + Item + " category=0x" + Category.ToString("X", CultureInfo.InvariantCulture);
}

/// <summary>A copied view of one inventory, from <c>ctx.Inventory.Read</c>.</summary>
public sealed class InventorySnapshot
{
    /// <summary>Construct a snapshot.</summary>
    public InventorySnapshot(InventoryRef container, InventorySlot[] slots, InventoryRestriction restriction, string reason = "")
    {
        Container = container;
        Slots = slots ?? Array.Empty<InventorySlot>();
        Restriction = restriction;
        Reason = reason ?? "";
        int free = 0;
        for (int i = 0; i < Slots.Count; i++)
            if (Slots[i].IsEmpty) free++;
        FreeSlots = free;
    }

    /// <summary>The inventory read.</summary>
    public InventoryRef Container { get; }

    /// <summary>Every slot in buffer order, empty ones included.</summary>
    public IReadOnlyList<InventorySlot> Slots { get; }

    /// <summary>What the container accepts.</summary>
    public InventoryRestriction Restriction { get; }

    /// <summary>Slot count.</summary>
    public int SlotCount => Slots.Count;

    /// <summary>Empty slots.</summary>
    public int FreeSlots { get; }

    /// <summary>Why the snapshot is empty when it could not be read; empty otherwise.</summary>
    public string Reason { get; }

    /// <summary>True when the inventory was read.</summary>
    public bool Ok => Reason.Length == 0;

    /// <summary>Total of <paramref name="item"/> across the slots.</summary>
    public int Count(int item) => InventoryMath.Count(Slots, item);
}

/// <summary>A read-only copy of the native inventory admission rule, independent of free space.</summary>
public sealed class InventoryAcceptance
{
    /// <summary>Make a description. A failed read must carry a nonempty reason.</summary>
    public InventoryAcceptance(InventoryRestriction restriction, string reason = "")
    {
        Restriction = restriction;
        Reason = reason ?? "";
    }

    /// <summary>False when the inventory could not be read; never treat that as unrestricted.</summary>
    public bool Ok => Reason.Length == 0;
    /// <summary>The game's item GUID and category mask. Meaningful only when <see cref="Ok"/>.</summary>
    public InventoryRestriction Restriction { get; }
    /// <summary>True only for a successfully read, unrestricted inventory.</summary>
    public bool IsOpen => Ok && Restriction.IsOpen;
    /// <summary>Why admission is unknown; empty on success.</summary>
    public string Reason { get; }
    /// <summary>Test native restrictions using an item's actual ItemData category flags, not UI categories. Does not test capacity.</summary>
    public bool Allows(int item, long itemCategory) =>
        Ok && item != 0 && InventoryMath.IsAllowed(Restriction, item, itemCategory);
}

/// <summary>How an inventory write ended.</summary>
public enum InventoryOutcome
{
    /// <summary>Everything requested happened.</summary>
    Complete,

    /// <summary>Some of it happened; <see cref="InventoryResult.Remainder"/> did not.</summary>
    Partial,

    /// <summary>Nothing happened and nothing was wrong: nothing present, nothing fit.</summary>
    Nothing,

    /// <summary>Refused before touching the world: bad arguments, client side, stale reference, unknown item.</summary>
    Refused,

    /// <summary>
    /// The read-back did not add up even after the compensating step. Logged as an error with
    /// both containers' counts. Should not happen; report it.
    /// </summary>
    Inconsistent,
}

/// <summary>
/// Result of <c>TryAdd</c>, <c>TryRemove</c> and <c>Transfer</c>. Every count is read back from
/// the world after the write, never inferred from what the game call returned.
/// </summary>
public sealed class InventoryResult
{
    /// <summary>True for <see cref="InventoryOutcome.Complete"/>, <see cref="InventoryOutcome.Partial"/> and <see cref="InventoryOutcome.Nothing"/>.</summary>
    public bool Ok => Outcome is InventoryOutcome.Complete or InventoryOutcome.Partial or InventoryOutcome.Nothing;

    /// <summary>How it ended.</summary>
    public InventoryOutcome Outcome { get; init; }

    /// <summary>Item <c>PrefabGUID</c> hash.</summary>
    public int Item { get; init; }

    /// <summary>Amount asked for.</summary>
    public int Requested { get; init; }

    /// <summary>Amount added, removed or moved.</summary>
    public int Done { get; init; }

    /// <summary><see cref="Requested"/> minus <see cref="Done"/>.</summary>
    public int Remainder => Math.Max(0, Requested - Done);

    /// <summary>Why it was not <see cref="InventoryOutcome.Complete"/>; empty when it was.</summary>
    public string Reason { get; init; } = "";

    /// <summary>Count of the item in the source before (<c>TryRemove</c>, <c>Transfer</c>).</summary>
    public int SourceBefore { get; init; }

    /// <summary>Count of the item in the source after.</summary>
    public int SourceAfter { get; init; }

    /// <summary>Count of the item in the target before (<c>TryAdd</c>, <c>Transfer</c>).</summary>
    public int TargetBefore { get; init; }

    /// <summary>Count of the item in the target after.</summary>
    public int TargetAfter { get; init; }

    /// <summary>
    /// <c>Transfer</c> only: the amount the compensating step put back after a game move did not
    /// conserve the total (normally 0).
    /// </summary>
    public int Compensated { get; init; }

    /// <summary>Game move or add steps the call made.</summary>
    public int Steps { get; init; }

    /// <summary>
    /// <c>Transfer</c> only: units of <see cref="Done"/> that the game equipped onto the target's
    /// owner (a player or servant with a free matching slot) instead of putting them in the
    /// target inventory. They are counted as moved; <see cref="TargetAfter"/> does not include them.
    /// </summary>
    public int Equipped { get; init; }

    internal InventoryResult WithEquipped(int equipped) =>
        new()
        {
            Outcome = Outcome, Item = Item, Requested = Requested, Done = Done,
            Reason = Reason.Length == 0 ? equipped + " equipped by the target's owner" : Reason + "; " + equipped + " equipped by the target's owner",
            SourceBefore = SourceBefore, SourceAfter = SourceAfter, TargetBefore = TargetBefore, TargetAfter = TargetAfter,
            Compensated = Compensated, Steps = Steps, Equipped = equipped,
        };

    /// <summary>One line for logs and chat.</summary>
    public override string ToString() =>
        Outcome + " item=" + Item + " requested=" + Requested + " done=" + Done
        + (Reason.Length > 0 ? " (" + Reason + ")" : "");

    internal static InventoryResult Refuse(int item, int requested, string reason) =>
        new() { Outcome = InventoryOutcome.Refused, Item = item, Requested = requested, Reason = reason };
}

/// <summary>Result of <c>TransferAll</c>: one row per item type it tried.</summary>
public sealed class InventoryTransferAllResult
{
    /// <summary>False when the call was refused or any row was <see cref="InventoryOutcome.Inconsistent"/>.</summary>
    public bool Ok { get; init; }

    /// <summary>Units moved across every item type.</summary>
    public int Moved { get; init; }

    /// <summary>Units of matching items still in the source afterwards.</summary>
    public int Left { get; init; }

    /// <summary>One result per item type, in the order the source held them.</summary>
    public IReadOnlyList<InventoryResult> PerItem { get; init; } = Array.Empty<InventoryResult>();

    /// <summary>Why the call was refused; empty otherwise.</summary>
    public string Reason { get; init; } = "";

    /// <inheritdoc />
    public override string ToString() =>
        (Ok ? "ok" : "failed") + " moved=" + Moved + " left=" + Left + " items=" + PerItem.Count
        + (Reason.Length > 0 ? " (" + Reason + ")" : "");
}

/// <summary>What a watch saw.</summary>
public enum InventoryChangeKind
{
    /// <summary>The contents changed (item or amount in any slot, or the slot count).</summary>
    Changed,

    /// <summary>The inventory is gone: its owner was destroyed or the world was reloaded. The watch stops.</summary>
    Gone,
}

/// <summary>One change event from <c>ctx.Inventory.Watch</c>.</summary>
public readonly struct InventoryChange
{
    /// <summary>Construct an event.</summary>
    public InventoryChange(InventoryRef container, InventoryChangeKind kind, ulong fingerprint, ulong previousFingerprint)
    {
        Container = container;
        Kind = kind;
        Fingerprint = fingerprint;
        PreviousFingerprint = previousFingerprint;
    }

    /// <summary>The watched inventory.</summary>
    public InventoryRef Container { get; }

    /// <summary>Changed or gone.</summary>
    public InventoryChangeKind Kind { get; }

    /// <summary>Content fingerprint now (<see cref="InventoryMath.Fingerprint"/>); 0 when gone.</summary>
    public ulong Fingerprint { get; }

    /// <summary>Content fingerprint at the previous check.</summary>
    public ulong PreviousFingerprint { get; }

    /// <inheritdoc />
    public override string ToString() => Kind + " " + Container;
}

/// <summary>
/// A live watch from <c>ctx.Inventory.Watch</c>. Tracked on the plugin context, so unload
/// stops it; dispose to stop it earlier.
/// </summary>
public interface IInventoryWatch : IDisposable
{
    /// <summary>The watched inventory.</summary>
    InventoryRef Container { get; }

    /// <summary>False once disposed or after a <see cref="InventoryChangeKind.Gone"/> event.</summary>
    bool Active { get; }

    /// <summary>Check on the next frame instead of waiting for the interval.</summary>
    void Poke();
}
