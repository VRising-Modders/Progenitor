using System;

namespace Progenitor.Items;

/// <summary>
/// What to do when the game reports that not everything fit.
/// <see cref="AllowPartial"/> is the default: a short stack is success and a completely full
/// inventory is a failure. <see cref="RequireComplete"/> fails unless the whole request landed.
/// </summary>
public enum WhenInventoryFull
{
    /// <summary>Fail only when zero items were added. A partial stack is reported, not thrown.</summary>
    AllowPartial = 0,

    /// <summary>Fail when any of the requested amount did not fit, including a partial add.</summary>
    RequireComplete = 1,
}

/// <summary>
/// What to do when the character already has this prefab in the bag or already wearing it.
/// <see cref="Stack"/> is the default and matches <c>TryAddInventoryItem</c>: the game merges
/// into an existing stack. Equipment does not stack, so <see cref="Stack"/> on a worn piece
/// creates another copy in the bag.
/// </summary>
public enum WhenAlreadyPresent
{
    /// <summary>Add again. The game stacks stackable items. Equipment gets a second copy.</summary>
    Stack = 0,

    /// <summary>Do not add, and do not equip, when a copy is already in the bag or worn.</summary>
    Refuse = 1,

    /// <summary>
    /// Do not add another copy. If one is already worn, stop. If one is only in the bag, equip
    /// that slot.
    /// </summary>
    EquipExisting = 2,
}

/// <summary>
/// What to do to <c>Durability</c> on the item entity after it exists.
/// <see cref="Indestructible"/> is <c>LossType</c> <c>None</c>, a zero take-damage factor, and
/// <c>DestroyItemWhenBroken</c> false. Death otherwise wears armor down.
/// </summary>
public enum ItemDurability
{
    /// <summary>Leave the prefab's durability rules alone.</summary>
    Leave = 0,

    /// <summary>Stop durability loss on the granted or equipped item entity.</summary>
    Indestructible = 1,
}

/// <summary>Optional rules for <see cref="Items.Grant"/>. Omitted fields are the defaults.</summary>
public sealed class ItemGrantOptions
{
    /// <summary>Fail only when nothing fit. See <see cref="WhenInventoryFull"/>.</summary>
    public WhenInventoryFull WhenFull { get; init; } = WhenInventoryFull.AllowPartial;

    /// <summary>Add again, even if a copy exists. See <see cref="WhenAlreadyPresent"/>.</summary>
    public WhenAlreadyPresent WhenPresent { get; init; } = WhenAlreadyPresent.Stack;

    /// <summary>After a successful add, wear the new stack's inventory slot.</summary>
    public bool Equip { get; init; }

    /// <summary>Leave durability alone unless set to <see cref="ItemDurability.Indestructible"/>.</summary>
    public ItemDurability Durability { get; init; } = ItemDurability.Leave;
}

/// <summary>Where a prefab already sits, as facts the policy can see without calling the game.</summary>
public readonly struct ItemPresence
{
    /// <summary>A bag slot holds this prefab with a positive amount.</summary>
    public readonly bool InInventory;

    /// <summary>Bag slot index, or -1.</summary>
    public readonly int InventorySlot;

    /// <summary>An equipment slot's item id is this prefab.</summary>
    public readonly bool Equipped;

    public ItemPresence(bool inInventory, int inventorySlot, bool equipped)
    {
        InInventory = inInventory;
        InventorySlot = inventorySlot;
        Equipped = equipped;
    }

    /// <summary>Nothing in the bag and nothing worn.</summary>
    public static ItemPresence None => new(false, -1, false);
}

/// <summary>What <see cref="ItemGrantPolicy.Before"/> tells the server call to do.</summary>
public enum ItemGrantStep
{
    /// <summary>Call the inventory add.</summary>
    Add = 0,

    /// <summary>Do not add. Equip <see cref="ItemPresence.InventorySlot"/>.</summary>
    EquipExisting = 1,

    /// <summary>Do not add and do not equip. The piece is already worn.</summary>
    AlreadyEquipped = 2,

    /// <summary>Do not add. A copy is already in the bag or worn.</summary>
    RefusePresent = 3,

    /// <summary>Do not add. Amount is outside 1..10000.</summary>
    RefuseAmount = 4,
}

/// <summary>
/// Decisions for granting and equipping items. No game types: the suite drives every branch.
/// The live call is <see cref="Items.Grant"/>.
/// </summary>
public static class ItemGrantPolicy
{
    /// <summary>Same cap as the debug <c>give</c> op. Larger amounts stall the main thread.</summary>
    public const int MaxAmount = 10_000;

    /// <summary>
    /// Decide before <c>TryAddInventoryItem</c>. A non-positive amount is a refusal, not a
    /// delete and not a clamp up to one. <see cref="WhenAlreadyPresent.Stack"/> is the only
    /// choice that adds when a copy already exists.
    /// </summary>
    public static ItemGrantStep Before(int amount, ItemPresence presence, ItemGrantOptions? options)
    {
        if (amount <= 0 || amount > MaxAmount)
            return ItemGrantStep.RefuseAmount;

        options ??= new ItemGrantOptions();
        if (!presence.InInventory && !presence.Equipped)
            return ItemGrantStep.Add;

        return options.WhenPresent switch
        {
            WhenAlreadyPresent.Refuse => ItemGrantStep.RefusePresent,
            WhenAlreadyPresent.EquipExisting when presence.Equipped => ItemGrantStep.AlreadyEquipped,
            WhenAlreadyPresent.EquipExisting when presence.InInventory => ItemGrantStep.EquipExisting,
            _ => ItemGrantStep.Add,
        };
    }

    /// <summary>
    /// Whether an add that has already happened is success. Nothing added is always a failure.
    /// <see cref="WhenInventoryFull.RequireComplete"/> also fails a short stack. The items that
    /// fit stay in the bag; this does not remove them.
    /// </summary>
    public static bool AcceptAdd(WhenInventoryFull whenFull, int requested, int added)
    {
        if (added <= 0)
            return false;
        if (whenFull == WhenInventoryFull.RequireComplete && added < requested)
            return false;
        return true;
    }

    /// <summary>An equip targets a bag slot only when that slot exists and holds a positive stack.</summary>
    public static bool SlotCanEquip(bool slotExists, int amount) => slotExists && amount > 0;

    /// <summary>Parse <c>allowPartial</c> or <c>requireComplete</c>. Null is the default.</summary>
    public static WhenInventoryFull ParseWhenFull(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Equals("allowPartial", StringComparison.OrdinalIgnoreCase))
            return WhenInventoryFull.AllowPartial;
        if (text.Equals("requireComplete", StringComparison.OrdinalIgnoreCase))
            return WhenInventoryFull.RequireComplete;
        throw new ArgumentException("whenFull must be allowPartial or requireComplete (got '" + text + "')");
    }

    /// <summary>Parse <c>stack</c>, <c>refuse</c>, or <c>equipExisting</c>. Null is the default.</summary>
    public static WhenAlreadyPresent ParseWhenPresent(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Equals("stack", StringComparison.OrdinalIgnoreCase))
            return WhenAlreadyPresent.Stack;
        if (text.Equals("refuse", StringComparison.OrdinalIgnoreCase))
            return WhenAlreadyPresent.Refuse;
        if (text.Equals("equipExisting", StringComparison.OrdinalIgnoreCase))
            return WhenAlreadyPresent.EquipExisting;
        throw new ArgumentException("whenPresent must be stack, refuse, or equipExisting (got '" + text + "')");
    }

    /// <summary>Parse <c>leave</c> or <c>none</c>. Null is the default.</summary>
    public static ItemDurability ParseDurability(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Equals("leave", StringComparison.OrdinalIgnoreCase))
            return ItemDurability.Leave;
        if (text.Equals("none", StringComparison.OrdinalIgnoreCase) || text.Equals("indestructible", StringComparison.OrdinalIgnoreCase))
            return ItemDurability.Indestructible;
        throw new ArgumentException("durability must be leave or none (got '" + text + "')");
    }
}
