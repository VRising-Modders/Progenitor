using System;
using System.Globalization;
using ProjectM;
using ProjectM.Network;
using ProjectM.Scripting;
using ProjectM.Shared;
using Progenitor.Ecs;
using Stunlock.Core;
using Unity.Entities;

namespace Progenitor.Items;

/// <summary>
/// Server item grant and equip. <see cref="Grant"/> adds through
/// <c>ServerGameManager.TryAddInventoryItem</c>. <see cref="Equip"/> sends
/// <c>EquipItemEvent</c> for a bag slot, the same event as clicking the item.
/// Neither call runs on the client. <c>ServerBootstrapSystem</c> is not involved.
///
/// A player character does not carry <c>InventoryBuffer</c>. The bag is the
/// <c>InventoryInstanceElement.ExternalInventoryEntity</c> that element points at.
/// </summary>
public static class Items
{
    /// <summary>
    /// Add <paramref name="guid"/> to <paramref name="character"/>'s inventory, then optionally
    /// equip it and clear durability loss. <paramref name="options"/> null uses the defaults:
    /// stack onto an existing copy, accept a partial add, do not equip, leave durability alone.
    /// </summary>
    public static ItemGrantResult Grant(World world, Entity character, PrefabGUID guid, int amount, ItemGrantOptions? options = null)
    {
        RequireServer(world);
        options ??= new ItemGrantOptions();
        var em = world.EntityManager;
        if (!em.Exists(character))
            return Refuse("missing", "character does not exist", amount);

        var presence = Scan(em, character, guid, out var equippedItem);
        var step = ItemGrantPolicy.Before(amount, presence, options);
        switch (step)
        {
            case ItemGrantStep.RefuseAmount:
                return Refuse("refused", "amount must be from 1 to " + ItemGrantPolicy.MaxAmount.ToString(CultureInfo.InvariantCulture), amount);
            case ItemGrantStep.RefusePresent:
                return Refuse("refused", "already has this item in the bag or equipped", amount);
            case ItemGrantStep.AlreadyEquipped:
                bool clearedWorn = options.Durability == ItemDurability.Indestructible
                    && ClearDurability(em, equippedItem);
                return new ItemGrantResult
                {
                    Ok = true,
                    Action = "already-equipped",
                    Requested = amount,
                    Added = 0,
                    DidNotFit = amount,
                    ItemEntity = Format(equippedItem),
                    Equipped = true,
                    DurabilityCleared = clearedWorn,
                    Reason = "already wearing this prefab; no second copy was added",
                };
            case ItemGrantStep.EquipExisting:
                return EquipExisting(em, character, presence.InventorySlot, amount, options);
        }

        AddItemResponse response;
        try
        {
            response = Manager(world).TryAddInventoryItem(character, guid, amount);
        }
        catch (Exception e)
        {
            return Refuse("refused", "TryAddInventoryItem threw: " + e.Message, amount);
        }

        int added = amount - response.RemainingAmount;
        if (added < 0) added = 0;
        if (response.Result == AddItemResult.Failed_InventoryDidntExist
            || response.Result == AddItemResult.Failed_ItemDoesntExist
            || response.Result == AddItemResult.Failed_TriedToEquipItemWithoutEquippable
            || response.Result == AddItemResult.Failed_InventoryFull)
        {
            return Refuse("refused", FailureText(response.Result), amount, response.Result.ToString());
        }

        if (!ItemGrantPolicy.AcceptAdd(options.WhenFull, amount, added))
        {
            return new ItemGrantResult
            {
                Ok = false,
                Action = "refused",
                Reason = "only " + added.ToString(CultureInfo.InvariantCulture) + " of " + amount.ToString(CultureInfo.InvariantCulture) + " fit, and whenFull is requireComplete",
                Requested = amount,
                Added = added,
                DidNotFit = amount - added,
                Slot = response.Slot,
                ItemEntity = Format(response.NewEntity),
                Result = response.Result.ToString(),
            };
        }

        var item = response.NewEntity;
        bool durability = options.Durability == ItemDurability.Indestructible && ClearDurability(em, item);
        bool equipped = response.Result == AddItemResult.Success_Equipped;
        if (options.Equip && !equipped && response.Slot >= 0)
        {
            var equip = Equip(world, character, response.Slot);
            equipped = equip.Ok && equip.Equipped;
        }

        return new ItemGrantResult
        {
            Ok = true,
            Action = equipped ? "added-and-equipped" : "added",
            Requested = amount,
            Added = added,
            DidNotFit = amount - added,
            Slot = response.Slot,
            ItemEntity = Format(item),
            Equipped = equipped,
            DurabilityCleared = durability,
            Result = response.Result.ToString(),
            Reason = response.Result == AddItemResult.Success_Partial
                ? "partial add"
                : null,
        };
    }

    /// <summary>
    /// Wear whatever is in bag <paramref name="slot"/>. Fails when the slot is missing or empty.
    /// Does not create an item.
    /// </summary>
    public static ItemGrantResult Equip(World world, Entity character, int slot)
    {
        RequireServer(world);
        var em = world.EntityManager;
        if (!em.Exists(character))
            return Refuse("refused", "character does not exist", 0);
        if (!TrySlot(em, character, slot, out var item, out var amount) || !ItemGrantPolicy.SlotCanEquip(true, amount))
            return Refuse("refused", "inventory slot " + slot.ToString(CultureInfo.InvariantCulture) + " is empty or missing", 0);

        SendEquip(em, character, slot);
        return new ItemGrantResult
        {
            Ok = true,
            Action = "equipped",
            Requested = 0,
            Slot = slot,
            ItemEntity = Format(item),
            Equipped = true,
        };
    }

    private static ItemGrantResult EquipExisting(EntityManager em, Entity character, int slot, int amount, ItemGrantOptions options)
    {
        if (!ItemGrantPolicy.SlotCanEquip(slot >= 0, 1))
            return Refuse("refused", "the existing copy has no bag slot to equip", amount);
        if (!TrySlot(em, character, slot, out var item, out var held) || !ItemGrantPolicy.SlotCanEquip(true, held))
            return Refuse("refused", "the existing copy's bag slot is empty", amount);
        bool cleared = options.Durability == ItemDurability.Indestructible && ClearDurability(em, item);
        SendEquip(em, character, slot);
        return new ItemGrantResult
        {
            Ok = true,
            Action = "equipped-existing",
            Requested = amount,
            Added = 0,
            DidNotFit = amount,
            Slot = slot,
            ItemEntity = Format(item),
            Equipped = true,
            DurabilityCleared = cleared,
            Reason = "already in the bag; equipped that copy and did not add another",
        };
    }

    private static void SendEquip(EntityManager em, Entity character, int slot)
    {
        Entity user = Entity.Null;
        if (em.HasComponent<PlayerCharacter>(character))
            user = em.GetComponentData<PlayerCharacter>(character).UserEntity;
        var ev = em.CreateEntity(ComponentType.ReadWrite<EquipItemEvent>(), ComponentType.ReadWrite<FromCharacter>());
        em.SetComponentData(ev, new EquipItemEvent { SlotIndex = slot, IsCosmetic = false });
        em.SetComponentData(ev, new FromCharacter { User = user, Character = character });
    }

    private static bool ClearDurability(EntityManager em, Entity item)
    {
        if (item == Entity.Null || !em.Exists(item) || !em.HasComponent<Durability>(item))
            return false;
        var durability = em.GetComponentData<Durability>(item);
        durability.LossType = DurabilityLossType.None;
        durability.TakeDamageDurabilityLossFactor = 0f;
        durability.DestroyItemWhenBroken = false;
        if (durability.MaxDurability > 0f)
            durability.Value = durability.MaxDurability;
        em.SetComponentData(item, durability);
        return true;
    }

    /// <summary>
    /// <c>InventoryInstanceElement</c> element stride. Interop <c>FieldOffset</c> puts
    /// <c>StartItems</c> at byte 40 and that blob reference is 8 bytes. Live inspect of a
    /// player reported element size 48.
    /// </summary>
    internal const int InstanceStride = 48;

    /// <summary>
    /// <c>ExternalInventoryEntity</c> (<c>NetworkedEntity._Entity</c>) inside one instance
    /// element. Interop offset 16. The following int is the entity version.
    /// </summary>
    internal const int InstanceEntityOffset = 16;

    /// <summary>
    /// A character has one bag instance. Anything longer than this is not walked: a huge
    /// <c>GetBufferLength</c> must not be treated as a real inventory list.
    /// </summary>
    internal const int MaxInventoryInstances = 8;

    private static ItemPresence Scan(EntityManager em, Entity character, PrefabGUID guid, out Entity equippedItem)
    {
        equippedItem = Entity.Null;
        bool inBag = false;
        int bagSlot = -1;
        var bags = new Entity[MaxInventoryInstances];
        int bagCount = CopyBags(em, character, bags);
        for (int b = 0; b < bagCount && !inBag; b++)
        {
            if (!TryBuffer(em, bags[b], out var buffer))
                continue;
            for (int i = 0; i < buffer.Length; i++)
            {
                var row = buffer[i];
                if (row.Amount <= 0 || row.ItemType != guid)
                    continue;
                inBag = true;
                bagSlot = i;
                break;
            }
        }

        bool equipped = false;
        if (Has<Equipment>(em, character))
        {
            var equipment = em.GetComponentData<Equipment>(character);
            NoteEquipped(equipment.ArmorHeadgearSlot, guid, ref equipped, ref equippedItem);
            NoteEquipped(equipment.ArmorChestSlot, guid, ref equipped, ref equippedItem);
            NoteEquipped(equipment.ArmorGlovesSlot, guid, ref equipped, ref equippedItem);
            NoteEquipped(equipment.ArmorLegsSlot, guid, ref equipped, ref equippedItem);
            NoteEquipped(equipment.ArmorFootgearSlot, guid, ref equipped, ref equippedItem);
            NoteEquipped(equipment.CloakSlot, guid, ref equipped, ref equippedItem);
            NoteEquipped(equipment.WeaponSlot, guid, ref equipped, ref equippedItem);
            NoteEquipped(equipment.GrimoireSlot, guid, ref equipped, ref equippedItem);
        }

        return new ItemPresence(inBag, bagSlot, equipped);
    }

    private static void NoteEquipped(EquipmentSlot slot, PrefabGUID guid, ref bool equipped, ref Entity equippedItem)
    {
        if (slot.SlotId != guid)
            return;
        equipped = true;
        if (equippedItem != Entity.Null)
            return;
        try
        {
            var item = slot.SlotEntity.GetEntityOnServer();
            if (item != Entity.Null)
                equippedItem = item;
        }
        catch
        {
            // Prefab match is enough to refuse a second copy. Durability needs the entity.
        }
    }

    private static bool TrySlot(EntityManager em, Entity character, int slot, out Entity item, out int amount)
    {
        item = Entity.Null;
        amount = 0;
        if (slot < 0)
            return false;
        var bags = new Entity[MaxInventoryInstances];
        if (CopyBags(em, character, bags) <= 0 || !TryBuffer(em, bags[0], out var buffer))
            return false;
        if (slot >= buffer.Length)
            return false;
        var row = buffer[slot];
        item = row.ItemEntity.GetEntityOnServer();
        amount = row.Amount;
        return true;
    }

    /// <summary>
    /// True when <paramref name="owner"/> (a chest, a station, any piece) may hold items: a
    /// readable inventory has a row with a positive amount, or it carries an inventory that could
    /// not be read (fails closed). False only for an entity with no inventory at all, or whose
    /// every inventory read back empty. Server or client world; main thread.
    /// </summary>
    internal static bool MayHoldItems(EntityManager em, Entity owner)
    {
        try
        {
            bool hasInventory = Has<InventoryBuffer>(em, owner) || Has<InventoryInstanceElement>(em, owner);
            if (!hasInventory)
                return false;
            var bags = new Entity[MaxInventoryInstances];
            int count = CopyBags(em, owner, bags);
            if (count <= 0)
                return true;
            for (int b = 0; b < count; b++)
            {
                if (!TryBuffer(em, bags[b], out var buffer))
                    return true;
                for (int i = 0; i < buffer.Length; i++)
                    if (buffer[i].Amount > 0)
                        return true;
            }
            return false;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// The entities that actually hold <c>InventoryBuffer</c>. A container that is its own
    /// inventory is returned as itself. A player is not: the bag is
    /// <c>InventoryInstanceElement.ExternalInventoryEntity</c>, read with
    /// <c>GetBufferRawRO</c> (not <c>GetBuffer&lt;InventoryInstanceElement&gt;</c>).
    /// </summary>
    private static unsafe int CopyBags(EntityManager em, Entity owner, Entity[] into)
    {
        if (Has<InventoryBuffer>(em, owner))
        {
            if (into.Length == 0)
                return 0;
            into[0] = owner;
            return 1;
        }

        if (!Has<InventoryInstanceElement>(em, owner))
            return 0;

        var typeIndex = ComponentIndex.IndexOf<InventoryInstanceElement>();
        int length;
        try { length = em.GetBufferLength(owner, typeIndex); }
        catch { return 0; }
        if (length <= 0)
            return 0;
        if (length > MaxInventoryInstances)
            length = MaxInventoryInstances;

        byte* raw = (byte*)em.GetBufferRawRO(owner, typeIndex);
        if (raw == null)
            return 0;

        int count = 0;
        for (int i = 0; i < length && count < into.Length; i++)
        {
            byte* row = raw + (i * InstanceStride);
            int index = *(int*)(row + InstanceEntityOffset);
            int version = *(int*)(row + InstanceEntityOffset + 4);
            if (index == 0 && version == 0)
                continue;
            var bag = new Entity { Index = index, Version = version };
            if (!em.Exists(bag) || !Has<InventoryBuffer>(em, bag))
                continue;
            into[count++] = bag;
        }
        return count;
    }

    private static bool TryBuffer(EntityManager em, Entity bag, out DynamicBuffer<InventoryBuffer> buffer)
    {
        buffer = default;
        try
        {
            if (!Has<InventoryBuffer>(em, bag))
                return false;
            buffer = em.GetBuffer<InventoryBuffer>(bag);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool Has<T>(EntityManager em, Entity entity)
    {
        try
        {
            return em.Exists(entity)
                && em.HasComponent(entity, ComponentIndex.Of<T>(ComponentType.AccessMode.ReadOnly));
        }
        catch
        {
            return false;
        }
    }

    private static ServerGameManager Manager(World world)
    {
        var mapper = world.GetExistingSystemManaged<ServerScriptMapper>()
            ?? throw new InvalidOperationException("ServerScriptMapper is not in this world");
        return mapper.GetServerGameManager();
    }

    private static void RequireServer(World world)
    {
        if (!Progenitor.Plugin.IsServerProcess)
            throw new InvalidOperationException("item grant runs on the server side only");
        if (world == null || !world.IsCreated)
            throw new InvalidOperationException("the game world does not exist yet");
    }

    private static string FailureText(AddItemResult result) => result switch
    {
        AddItemResult.Failed_InventoryDidntExist =>
            "no inventory (no InventoryBuffer). Pass a character, a chest, or a station.",
        AddItemResult.Failed_ItemDoesntExist =>
            "prefab is not in the server item table (GameDataSystem.ItemHashLookupMap). Vanilla items are registered at boot; a cloned prefab has to be registered first.",
        AddItemResult.Failed_InventoryFull =>
            "inventory is full; nothing was added. Free a slot, lower amount, or pass whenFull allowPartial only helps when some of the stack fits.",
        AddItemResult.Failed_TriedToEquipItemWithoutEquippable =>
            "item cannot be equipped, and the add was routed to an equipment slot.",
        _ => result.ToString(),
    };

    private static ItemGrantResult Refuse(string action, string reason, int requested, string result = "") =>
        new()
        {
            Ok = false,
            Action = action,
            Reason = reason,
            Requested = requested,
            DidNotFit = requested,
            Result = result,
        };

    private static string? Format(Entity entity)
    {
        if (entity == Entity.Null) return null;
        return entity.Index.ToString(CultureInfo.InvariantCulture) + ":" + entity.Version.ToString(CultureInfo.InvariantCulture);
    }
}
