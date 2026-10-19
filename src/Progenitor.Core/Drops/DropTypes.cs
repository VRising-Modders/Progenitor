using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Progenitor.Inventory;

namespace Progenitor.Drops;

/// <summary>
/// A session handle for one item pile on the ground (an entity with the game's <c>ItemPickup</c>):
/// the entity's index and version plus the world-load epoch it was listed in. A handle from before
/// a save reload is refused as stale, so a reused entity slot in a later world never reads as the
/// same pile. Valid only while this process runs; never persist it.
/// </summary>
public readonly struct GroundPileId : IEquatable<GroundPileId>
{
    /// <summary>Construct from raw parts. Plugins normally take ids from <c>ctx.Drops.Near</c>.</summary>
    public GroundPileId(int index, int version, int epoch)
    {
        Index = index;
        Version = version;
        Epoch = epoch;
    }

    /// <summary>The pile's <c>Entity.Index</c>.</summary>
    public int Index { get; }

    /// <summary>The pile's <c>Entity.Version</c>.</summary>
    public int Version { get; }

    /// <summary>World-load epoch the pile was listed in.</summary>
    public int Epoch { get; }

    /// <summary>True for the default (no pile) value.</summary>
    public bool IsNone => Index == 0 && Version == 0;

    /// <summary>The default (no pile) value.</summary>
    public static GroundPileId None => default;

    /// <summary>True when this handle was taken in a different world-load epoch.</summary>
    public bool IsStaleIn(int currentEpoch) => !IsNone && Epoch != currentEpoch;

    /// <summary><c>(Index &lt;&lt; 32) | Version</c>: a dictionary key within one epoch. Not an identity across loads.</summary>
    public long Key => ((long)Index << 32) | (uint)Version;

    /// <summary>Same entity in the same epoch.</summary>
    public bool Equals(GroundPileId other) => Index == other.Index && Version == other.Version && Epoch == other.Epoch;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is GroundPileId other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Index, Version, Epoch);

    /// <summary>Value equality.</summary>
    public static bool operator ==(GroundPileId left, GroundPileId right) => left.Equals(right);

    /// <summary>Value inequality.</summary>
    public static bool operator !=(GroundPileId left, GroundPileId right) => !left.Equals(right);

    /// <summary><c>index:version@epoch</c>, or <c>none</c>.</summary>
    public override string ToString() =>
        IsNone
            ? "none"
            : Index.ToString(CultureInfo.InvariantCulture) + ":" + Version.ToString(CultureInfo.InvariantCulture)
              + "@" + Epoch.ToString(CultureInfo.InvariantCulture);

#if !PROGENITOR_TESTS
    /// <summary>The pile entity. Only meaningful in the epoch the handle was taken in.</summary>
    public Unity.Entities.Entity Entity => new() { Index = Index, Version = Version };

    /// <summary>A handle to <paramref name="pile"/> in the current epoch.</summary>
    public static GroundPileId For(Unity.Entities.Entity pile) =>
        new(pile.Index, pile.Version, Progenitor.WorldState.WorldEpoch.Current);
#endif
}

/// <summary>
/// One item pile on the ground as <c>ctx.Drops.Near</c> read it: item, amount, where it lies, and
/// whether it can be taken by <c>ctx.Drops.TakeInto</c>. A copy: read it again
/// (<c>ctx.Drops.TryGet</c>) before relying on the amount later.
/// </summary>
public readonly struct GroundPile
{
    /// <summary>Make one. Public so tests and samples can describe one.</summary>
    public GroundPile(GroundPileId id, int item, int amount, float x, float y, float z, float distanceSquared,
        bool disabled, int maxStack, int itemEntityIndex = 0, int itemEntityVersion = 0)
    {
        Id = id;
        Item = item;
        Amount = amount;
        X = x;
        Y = y;
        Z = z;
        DistanceSquared = distanceSquared;
        Disabled = disabled;
        MaxStack = maxStack;
        ItemEntityIndex = itemEntityIndex;
        ItemEntityVersion = itemEntityVersion;
    }

    /// <summary>The pile's handle.</summary>
    public GroundPileId Id { get; }

    /// <summary>Item <c>PrefabGUID</c> hash (<c>ItemPickup.ItemId</c>).</summary>
    public int Item { get; }

    /// <summary>Units in the pile (<c>ItemPickup.ItemAmount</c>), above 0 for a listed pile.</summary>
    public int Amount { get; }

    /// <summary>World position (<c>Translation</c>, or <c>LocalToWorld</c> when the pile has no <c>Translation</c>).</summary>
    public float X { get; }

    /// <summary>World position, height.</summary>
    public float Y { get; }

    /// <summary>World position.</summary>
    public float Z { get; }

    /// <summary>Squared distance from the point the listing was asked about.</summary>
    public float DistanceSquared { get; }

    /// <summary>Distance from the point the listing was asked about.</summary>
    public float Distance => MathF.Sqrt(DistanceSquared);

    /// <summary>The pile carries <c>Disabled</c> (an area nobody is near; the game keeps it asleep).</summary>
    public bool Disabled { get; }

    /// <summary>The item's stack size from the server item table; 0 when the table does not know the item.</summary>
    public int MaxStack { get; }

    /// <summary>The pile's own item entity <c>Entity.Index</c> (0 when it has none).</summary>
    public int ItemEntityIndex { get; }

    /// <summary>The pile's own item entity <c>Entity.Version</c>.</summary>
    public int ItemEntityVersion { get; }

    /// <summary>
    /// True when the pile names an item entity of its own (gear with rolled stats, a jewel): the
    /// game's throw component on the pile (<c>DropItemThrowSystem.DropItem.ItemEntity</c>) names a live entity.
    /// </summary>
    public bool HasItemEntity => ItemEntityIndex != 0 || ItemEntityVersion != 0;

    /// <summary>The item stacks (stack size above 1 in the server item table).</summary>
    public bool IsStackable => MaxStack > 1;

    /// <summary>
    /// The pile may carry state a plain copy would lose: it names its own item entity, or the item
    /// does not stack (or is not in the item table). <c>TakeInto</c> refuses these piles.
    /// </summary>
    public bool IsUnique => HasItemEntity || !IsStackable;

    /// <summary>What <c>TakeInto</c> accepts: a stackable item with no item entity of its own and units left.</summary>
    public bool CanTake => !IsUnique && Amount > 0;

#if !PROGENITOR_TESTS
    /// <summary>The pile entity (only meaningful in the epoch of <see cref="Id"/>).</summary>
    public Unity.Entities.Entity Entity => Id.Entity;

    /// <summary>The pile's own item entity, <c>Entity.Null</c> when it has none.</summary>
    public Unity.Entities.Entity ItemEntity => new() { Index = ItemEntityIndex, Version = ItemEntityVersion };
#endif

    /// <summary>One log line.</summary>
    public override string ToString()
    {
        var c = CultureInfo.InvariantCulture;
        return Id + " item=" + Item.ToString(c) + " x" + Amount.ToString(c)
            + " at " + X.ToString("0.0", c) + "," + Y.ToString("0.0", c) + "," + Z.ToString("0.0", c)
            + " d=" + Distance.ToString("0.0", c)
            + (Disabled ? " asleep" : "")
            + (HasItemEntity ? " item-entity" : "")
            + (IsStackable ? "" : " not-stackable");
    }
}

/// <summary>Result of <c>ctx.Drops.TakeInto</c>. Every count is read back from the world after the write.</summary>
public sealed class GroundTakeResult
{
    /// <summary>
    /// <c>Complete</c> (all requested), <c>Partial</c> (some), <c>Nothing</c> (the inventory took none;
    /// the pile is untouched), <c>Refused</c> (nothing touched), <c>Inconsistent</c> (the inventory
    /// could not be read back after the add; the pile is untouched and should be left alone).
    /// </summary>
    public InventoryOutcome Outcome { get; init; }

    /// <summary>True for <c>Complete</c>, <c>Partial</c> and <c>Nothing</c>.</summary>
    public bool Ok => Outcome is InventoryOutcome.Complete or InventoryOutcome.Partial or InventoryOutcome.Nothing;

    /// <summary>The pile.</summary>
    public GroundPileId Pile { get; init; }

    /// <summary>Item <c>PrefabGUID</c> hash.</summary>
    public int Item { get; init; }

    /// <summary>Amount asked for.</summary>
    public int Requested { get; init; }

    /// <summary>Units that went into the inventory (and came off the pile).</summary>
    public int Done { get; init; }

    /// <summary>The pile's amount before.</summary>
    public int PileBefore { get; init; }

    /// <summary>The pile's amount after (0 when it was emptied).</summary>
    public int PileAfter { get; init; }

    /// <summary>The pile was emptied: its amount is 0 and its replicated removal has started.</summary>
    public bool PileRemoved { get; init; }

    /// <summary>Why it was not <c>Complete</c>; empty when it was.</summary>
    public string Reason { get; init; } = "";

    /// <summary>The inventory add behind it (<c>ctx.Inventory.TryAdd</c>); null when refused before the add.</summary>
    public InventoryResult? Add { get; init; }

    /// <summary>One line for logs and chat.</summary>
    public override string ToString() =>
        Outcome + " pile=" + Pile + " item=" + Item + " requested=" + Requested + " done=" + Done
        + " pile " + PileBefore + "->" + PileAfter + (PileRemoved ? " removed" : "")
        + (Reason.Length > 0 ? " (" + Reason + ")" : "");

    internal static GroundTakeResult Refuse(GroundPileId pile, int item, int requested, int pileAmount, string reason) =>
        new()
        {
            Outcome = InventoryOutcome.Refused, Pile = pile, Item = item, Requested = requested,
            PileBefore = pileAmount, PileAfter = pileAmount, Reason = reason,
        };
}

/// <summary>Who the game lets open a player's death container, by which of its prefabs it is.</summary>
public enum DeathContainerAccess
{
    /// <summary>Not one of the three player death container prefabs.</summary>
    None = 0,

    /// <summary><c>PlayerDeathContainerOnlySelfPrefabGUID</c>.</summary>
    OnlySelf = 1,

    /// <summary><c>PlayerDeathContainerClanMembersPrefabGUID</c>.</summary>
    ClanMembers = 2,

    /// <summary><c>PlayerDeathContainerAnyonePrefabGUID</c>.</summary>
    Anyone = 3,
}

/// <summary>
/// The drop-related prefab GUIDs from the game's prefab table (<c>RootPrefabCollection</c>), read
/// at the field offsets the server and client metadata agree on. 0 for a field that read 0.
/// </summary>
public readonly struct DropPrefabs
{
    /// <summary>Make one. Public so tests can describe one.</summary>
    public DropPrefabs(int dropItem, int deathContainer, int deathAnyone, int deathClanMembers, int deathOnlySelf, int inventoryDropContainer)
    {
        DropItem = dropItem;
        DeathContainer = deathContainer;
        PlayerDeathContainerAnyone = deathAnyone;
        PlayerDeathContainerClanMembers = deathClanMembers;
        PlayerDeathContainerOnlySelf = deathOnlySelf;
        PlayerInventoryDropContainer = inventoryDropContainer;
    }

    /// <summary><c>DropItemPrefabGUID</c>: the item pile the game spawns.</summary>
    public int DropItem { get; }

    /// <summary><c>DeathContainerPrefabGUID</c>: the game's generic death container (not a player's).</summary>
    public int DeathContainer { get; }

    /// <summary><c>PlayerDeathContainerAnyonePrefabGUID</c>.</summary>
    public int PlayerDeathContainerAnyone { get; }

    /// <summary><c>PlayerDeathContainerClanMembersPrefabGUID</c>.</summary>
    public int PlayerDeathContainerClanMembers { get; }

    /// <summary><c>PlayerDeathContainerOnlySelfPrefabGUID</c>.</summary>
    public int PlayerDeathContainerOnlySelf { get; }

    /// <summary><c>PlayerInventoryDropContainerPrefabGUID</c>: the bag a whole-inventory drop leaves.</summary>
    public int PlayerInventoryDropContainer { get; }

    /// <summary>True when every player death container and the inventory-drop container read non-zero.</summary>
    public bool IsComplete =>
        PlayerDeathContainerAnyone != 0 && PlayerDeathContainerClanMembers != 0 && PlayerDeathContainerOnlySelf != 0
        && PlayerInventoryDropContainer != 0;

    /// <summary>Which death container <paramref name="prefab"/> is; <see cref="DeathContainerAccess.None"/> for anything else (and for 0).</summary>
    public DeathContainerAccess AccessOf(int prefab)
    {
        if (prefab == 0) return DeathContainerAccess.None;
        if (prefab == PlayerDeathContainerAnyone) return DeathContainerAccess.Anyone;
        if (prefab == PlayerDeathContainerClanMembers) return DeathContainerAccess.ClanMembers;
        if (prefab == PlayerDeathContainerOnlySelf) return DeathContainerAccess.OnlySelf;
        return DeathContainerAccess.None;
    }

    /// <summary>True for one of the three player death container prefabs.</summary>
    public bool IsPlayerDeathContainer(int prefab) => AccessOf(prefab) != DeathContainerAccess.None;

    /// <summary>True for the whole-inventory drop container prefab.</summary>
    public bool IsPlayerInventoryDrop(int prefab) => prefab != 0 && prefab == PlayerInventoryDropContainer;

    /// <summary>One log line.</summary>
    public override string ToString() =>
        "dropItem=" + DropItem + " death=" + DeathContainer + " playerDeath(anyone=" + PlayerDeathContainerAnyone
        + ", clan=" + PlayerDeathContainerClanMembers + ", self=" + PlayerDeathContainerOnlySelf + ") inventoryDrop=" + PlayerInventoryDropContainer;
}

/// <summary>Which client request dropped a <see cref="PlayerGroundDrop"/> from the player's bag.</summary>
public enum GroundDropSource
{
    /// <summary><c>DropItemAtSlotEvent</c> (handled by <c>DropItemSystem</c>): the inventory menu's drop key on a slot.</summary>
    DropKey = 0,

    /// <summary><c>DropInventoryItemEvent</c> naming the player's own inventory (handled by <c>DropInventoryItemSystem</c>): a slot dragged onto the ground.</summary>
    DragOut = 1,
}

/// <summary>
/// One item a player dropped from their own bag onto the ground: the server handled the player's
/// drop request and, around that one system update, the bag's total of <see cref="Item"/> fell by
/// <see cref="Amount"/>. Amounts are read back from the bag, never taken from the request, so a
/// refused drop raises nothing. Raised by <c>ctx.Inventory.OnGroundDrop</c> on the main thread,
/// from the frame tick after the drop.
/// </summary>
public sealed class PlayerGroundDrop
{
    /// <summary>Make one. Public so tests and samples can describe one.</summary>
    public PlayerGroundDrop(long sequence, DateTime utcTime, int userIndex, int userVersion, int characterIndex, int characterVersion,
        GroundDropSource source, int item, int amount, int countBefore, int countAfter, bool withItemEntity,
        bool hasPosition, float x, float y, float z, int requests)
    {
        Sequence = sequence;
        UtcTime = utcTime;
        UserIndex = userIndex;
        UserVersion = userVersion;
        CharacterIndex = characterIndex;
        CharacterVersion = characterVersion;
        Source = source;
        Item = item;
        Amount = amount;
        CountBefore = countBefore;
        CountAfter = countAfter;
        WithItemEntity = withItemEntity;
        HasPosition = hasPosition;
        X = x;
        Y = y;
        Z = z;
        Requests = requests;
    }

    /// <summary>Grows by one per raised drop in this Core generation (gaps mean drops lost to the queue cap).</summary>
    public long Sequence { get; }

    /// <summary>When the server handled the request.</summary>
    public DateTime UtcTime { get; }

    /// <summary>The dropping user's <c>Entity.Index</c> (<c>FromCharacter.User</c>).</summary>
    public int UserIndex { get; }

    /// <summary>The dropping user's <c>Entity.Version</c>.</summary>
    public int UserVersion { get; }

    /// <summary>The dropping character's <c>Entity.Index</c> (<c>FromCharacter.Character</c>).</summary>
    public int CharacterIndex { get; }

    /// <summary>The dropping character's <c>Entity.Version</c>.</summary>
    public int CharacterVersion { get; }

    /// <summary>Which request.</summary>
    public GroundDropSource Source { get; }

    /// <summary>Item <c>PrefabGUID</c> hash.</summary>
    public int Item { get; }

    /// <summary>Units that left the bag. Always above 0.</summary>
    public int Amount { get; }

    /// <summary>The bag's total of <see cref="Item"/> just before the system update.</summary>
    public int CountBefore { get; }

    /// <summary>The bag's total just after it.</summary>
    public int CountAfter { get; }

    /// <summary>Some of the units left in a slot with its own item entity (gear, a jewel): the pile will carry that entity.</summary>
    public bool WithItemEntity { get; }

    /// <summary>True when the character's position was read right after the update.</summary>
    public bool HasPosition { get; }

    /// <summary>
    /// The character's <c>Translation</c> right after the update: where the game drops the item from
    /// (the pile lands nearby, thrown along the character's facing).
    /// </summary>
    public float X { get; }

    /// <summary>Height of <see cref="X"/>'s position.</summary>
    public float Y { get; }

    /// <summary>See <see cref="X"/>.</summary>
    public float Z { get; }

    /// <summary>How many drop requests of this player that update covered (two quick drops in one frame are one record per item with 2).</summary>
    public int Requests { get; }

#if !PROGENITOR_TESTS
    /// <summary>The dropping user entity. Only meaningful in the world load the drop happened in.</summary>
    public Unity.Entities.Entity User => new() { Index = UserIndex, Version = UserVersion };

    /// <summary>The dropping character entity.</summary>
    public Unity.Entities.Entity Character => new() { Index = CharacterIndex, Version = CharacterVersion };
#endif

    /// <summary>One log line.</summary>
    public override string ToString()
    {
        var c = CultureInfo.InvariantCulture;
        var sb = new StringBuilder(128);
        sb.Append('#').Append(Sequence.ToString(c)).Append(" user=").Append(UserIndex.ToString(c)).Append(':').Append(UserVersion.ToString(c))
            .Append(' ').Append(Source).Append(" item=").Append(Item.ToString(c)).Append(" x").Append(Amount.ToString(c))
            .Append(" bag=").Append(CountBefore.ToString(c)).Append("->").Append(CountAfter.ToString(c));
        if (HasPosition) sb.Append(" at ").Append(X.ToString("0.0", c)).Append(',').Append(Y.ToString("0.0", c)).Append(',').Append(Z.ToString("0.0", c));
        if (WithItemEntity) sb.Append(" item-entity");
        if (Requests > 1) sb.Append(" requests=").Append(Requests.ToString(c));
        return sb.ToString();
    }
}

/// <summary>
/// The game's whole-inventory drop setting (<c>DropEntireInventoryEvent.Setting</c>,
/// <c>DropEntireInventorySetting</c>): what the drop leaves in place. Values are the game's.
/// </summary>
public enum DropAllSetting
{
    /// <summary>Everything.</summary>
    All = 0,

    /// <summary>Everything except anything equipped.</summary>
    ExceptAnyEquipped = 1,

    /// <summary>Everything except equipped items on the action bar.</summary>
    ExceptEquippedInActionBar = 2,

    /// <summary>Everything except weapons on the action bar.</summary>
    ExceptAnyWeaponInActionBar = 3,

    /// <summary>Everything except weapons on the action bar and anything equipped.</summary>
    ExceptAnyWeaponInActionBarOrAnyEquipped = 4,

    /// <summary>Everything except the action bar.</summary>
    ExceptAnythingInActionBar = 5,

    /// <summary>Everything except the action bar and anything equipped.</summary>
    ExceptAnythingInActionBarOrEquipped = 6,

    /// <summary>The request carried a value outside the game's list.</summary>
    Unknown = -1,
}

/// <summary>One item type that left a bag: item, units, and whether some had their own item entity.</summary>
public readonly struct DroppedStack
{
    /// <summary>Make one.</summary>
    public DroppedStack(int item, int amount, bool withItemEntity)
    {
        Item = item;
        Amount = amount;
        WithItemEntity = withItemEntity;
    }

    /// <summary>Item <c>PrefabGUID</c> hash.</summary>
    public int Item { get; }

    /// <summary>Units.</summary>
    public int Amount { get; }

    /// <summary>Some units were in slots with their own item entity.</summary>
    public bool WithItemEntity { get; }

    /// <inheritdoc />
    public override string ToString() => Item.ToString(CultureInfo.InvariantCulture) + "x" + Amount.ToString(CultureInfo.InvariantCulture) + (WithItemEntity ? "*" : "");
}

/// <summary>
/// A player dropped their whole inventory (<c>DropEntireInventoryEvent</c>, handled by
/// <c>DropItemSystem</c>): the bag lost <see cref="Items"/> in that system update, and the
/// container the game left for it (<see cref="DropPrefabs.PlayerInventoryDropContainer"/>), when
/// found. Raised by <c>ctx.Inventory.OnDropAll</c> on the main thread once the container is found
/// or the search gave up.
/// </summary>
public sealed class PlayerDropAll
{
    /// <summary>Make one. Public so tests and samples can describe one.</summary>
    public PlayerDropAll(long sequence, DateTime utcTime, int userIndex, int userVersion, int characterIndex, int characterVersion,
        DropAllSetting setting, IReadOnlyList<DroppedStack> items, bool hasPosition, float x, float y, float z,
        int containerIndex, int containerVersion, int epoch, bool contentsMatch, bool ambiguous, string reason)
    {
        Sequence = sequence;
        UtcTime = utcTime;
        UserIndex = userIndex;
        UserVersion = userVersion;
        CharacterIndex = characterIndex;
        CharacterVersion = characterVersion;
        Setting = setting;
        Items = items ?? Array.Empty<DroppedStack>();
        HasPosition = hasPosition;
        X = x;
        Y = y;
        Z = z;
        ContainerIndex = containerIndex;
        ContainerVersion = containerVersion;
        Epoch = epoch;
        ContentsMatch = contentsMatch;
        Ambiguous = ambiguous;
        Reason = reason ?? "";
    }

    /// <summary>Grows by one per raised drop-all in this Core generation.</summary>
    public long Sequence { get; }

    /// <summary>When the server handled the request.</summary>
    public DateTime UtcTime { get; }

    /// <summary>The user's <c>Entity.Index</c> (<c>FromCharacter.User</c>).</summary>
    public int UserIndex { get; }

    /// <summary>The user's <c>Entity.Version</c>.</summary>
    public int UserVersion { get; }

    /// <summary>The character's <c>Entity.Index</c>.</summary>
    public int CharacterIndex { get; }

    /// <summary>The character's <c>Entity.Version</c>.</summary>
    public int CharacterVersion { get; }

    /// <summary>The request's setting.</summary>
    public DropAllSetting Setting { get; }

    /// <summary>What left the bag, per item type. Worn equipment the setting let go is not in this list (it was never in the bag).</summary>
    public IReadOnlyList<DroppedStack> Items { get; }

    /// <summary>True when the character's position was read right after the update.</summary>
    public bool HasPosition { get; }

    /// <summary>The character's <c>Translation</c> right after the update.</summary>
    public float X { get; }

    /// <summary>Height.</summary>
    public float Y { get; }

    /// <summary>See <see cref="X"/>.</summary>
    public float Z { get; }

    /// <summary>The container's <c>Entity.Index</c>; 0 when no container was found (<see cref="Reason"/> says why).</summary>
    public int ContainerIndex { get; }

    /// <summary>The container's <c>Entity.Version</c>.</summary>
    public int ContainerVersion { get; }

    /// <summary>World-load epoch the container was found in.</summary>
    public int Epoch { get; }

    /// <summary>True when a container was found.</summary>
    public bool HasContainer => ContainerIndex != 0 || ContainerVersion != 0;

    /// <summary>The container held, per item type, at least what left the bag when it was found.</summary>
    public bool ContentsMatch { get; }

    /// <summary>
    /// The same update also carried a drop-key request of this player, so some of <see cref="Items"/>
    /// may have gone to the ground instead; or more than one new container was equally close.
    /// </summary>
    public bool Ambiguous { get; }

    /// <summary>Why <see cref="HasContainer"/> is false, or a note on the match; empty otherwise.</summary>
    public string Reason { get; }

#if !PROGENITOR_TESTS
    /// <summary>The user entity. Only meaningful in the world load the drop happened in.</summary>
    public Unity.Entities.Entity User => new() { Index = UserIndex, Version = UserVersion };

    /// <summary>The character entity.</summary>
    public Unity.Entities.Entity Character => new() { Index = CharacterIndex, Version = CharacterVersion };

    /// <summary>The dropped-inventory container, <c>Entity.Null</c> when none was found.</summary>
    public Unity.Entities.Entity Container => new() { Index = ContainerIndex, Version = ContainerVersion };

    /// <summary>The container's inventory for <c>ctx.Inventory</c> (stale after a world reload); <see cref="InventoryRef.None"/> without a container.</summary>
    public InventoryRef ContainerInventory =>
        HasContainer ? new InventoryRef(ContainerIndex, ContainerVersion, 0, Epoch) : InventoryRef.None;
#endif

    /// <summary>One log line.</summary>
    public override string ToString()
    {
        var c = CultureInfo.InvariantCulture;
        var sb = new StringBuilder(160);
        sb.Append('#').Append(Sequence.ToString(c)).Append(" user=").Append(UserIndex.ToString(c)).Append(':').Append(UserVersion.ToString(c))
            .Append(" dropall ").Append(Setting).Append(" items=").Append(Items.Count.ToString(c));
        if (HasContainer) sb.Append(" container=").Append(ContainerIndex.ToString(c)).Append(':').Append(ContainerVersion.ToString(c)).Append(ContentsMatch ? " match" : " mismatch");
        else sb.Append(" no-container");
        if (Ambiguous) sb.Append(" ambiguous");
        if (Reason.Length > 0) sb.Append(" (").Append(Reason).Append(')');
        return sb.ToString();
    }
}
