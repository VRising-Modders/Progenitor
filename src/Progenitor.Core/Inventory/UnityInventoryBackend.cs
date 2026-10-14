using System;
using System.Collections.Generic;
using ProjectM;
using ProjectM.CastleBuilding;
using ProjectM.Network;
using ProjectM.Scripting;
using Progenitor.Ecs;
using Stunlock.Core;
using Unity.Entities;

namespace Progenitor.Inventory;

/// <summary>
/// Game reads and writes behind the inventory engine. The only file of the module that calls
/// game inventory code; every call is server-side and on the main thread.
///
/// <para><b>Which entity holds the slots.</b> A chest, a castle heart and a player bag entity
/// carry <c>InventoryBuffer</c> themselves. A player character, a servant and a station carry
/// <c>InventoryInstanceElement</c> rows pointing at external inventory entities; a refinement
/// station spawns two of them, input (row 0) then output (row 1). The rows are read raw (stride
/// 48, entity at byte 16), the same live-verified path <c>ctx.Items</c> uses.</para>
///
/// <para><b>Game calls.</b> Add: <c>ServerGameManager.TryAddInventoryItem(target, item,
/// amount)</c>, the path <c>ctx.Items.Grant</c> already uses; the target is the player character
/// for a player's main bag (so the game's player slot rules apply) and the inventory entity
/// itself otherwise. Remove: <c>InventoryUtilitiesServer.RemoveItemGetRemainder</c> on the
/// inventory entity, never for more than it holds (past that the game falls back to worn
/// equipment). Move: <c>InventoryUtilitiesServer.TryMoveItem</c> from the source inventory
/// entity and slot into the target owner with the target instance index set, so a station's
/// input and output are never confused; relic rules stay on.</para>
/// </summary>
internal sealed unsafe class UnityInventoryBackend : IInventoryBackend
{
    private readonly Dictionary<int, InventoryItemInfo> _items = new();
    private readonly HashSet<int> _unknown = new();
    private int _itemsEpoch = int.MinValue;
    private long _itemsStamp;

    /// <summary>Item facts are re-read at least this often (data packs can change a stack size).</summary>
    private const long ItemCacheMs = 10_000;

    public bool IsServer
    {
        get
        {
            if (!Plugin.IsServerProcess) return false;
            var world = Worlds.Server;
            return world != null && world.IsCreated;
        }
    }

    public int CurrentEpoch => Progenitor.WorldState.WorldEpoch.Current;

    public bool TryRead(InventoryRef container, List<InventorySlot> into, out InventoryRestriction restriction, out string reason)
    {
        into.Clear();
        restriction = InventoryRestriction.None;
        if (!TryWorld(out var world, out reason)) return false;
        var em = world.EntityManager;
        try
        {
            if (!TryInventoryEntity(em, container.Owner, container.Instance, out var inv, out reason)) return false;
            var buffer = em.GetBuffer<InventoryBuffer>(inv);
            for (int i = 0; i < buffer.Length; i++)
            {
                var row = buffer[i];
                int item = row.ItemType.GuidHash;
                int amount = row.Amount;
                int over = row.MaxAmountOverride;
                bool hasEntity = false;
                try { hasEntity = row.ItemEntity.GetEntityOnServer() != Entity.Null; }
                catch { hasEntity = false; }
                int max = 0;
                if (item != 0)
                    max = InventoryMath.SlotCapacity(over, TryItemInfo(item, out var info) ? info.MaxAmount : 0);
                into.Add(new InventorySlot(i, item, amount, max, over, hasEntity));
            }

            if (inv.TryRead(em, out RestrictedInventory r))
                restriction = new InventoryRestriction(r.RestrictedItemType.GuidHash, (long)r.RestrictedItemCategory);
            reason = "";
            return true;
        }
        catch (Exception e)
        {
            into.Clear();
            reason = "inventory read threw: " + e.Message;
            return false;
        }
    }

    public bool TryItemInfo(int item, out InventoryItemInfo info)
    {
        info = default;
        if (item == 0) return false;
        long now = Environment.TickCount64;
        int epoch = CurrentEpoch;
        if (epoch != _itemsEpoch || now - _itemsStamp > ItemCacheMs)
        {
            _items.Clear();
            _unknown.Clear();
            _itemsEpoch = epoch;
            _itemsStamp = now;
        }

        if (_items.TryGetValue(item, out info)) return true;
        if (_unknown.Contains(item)) return false;

        var world = Worlds.Server;
        if (world == null || !world.IsCreated) return false;
        try
        {
            var guid = new PrefabGUID(item);
            var gameData = world.GetExistingSystemManaged<GameDataSystem>();
            if (gameData == null || !gameData.ItemHashLookupMap.TryGetValue(guid, out var mapped))
            {
                _unknown.Add(item);
                return false;
            }

            // The prefab's own ItemData wins for the stack size: a data-pack edit patches the
            // prefab and the map, and the map can lag (see PrefabApplicator).
            int max = mapped.MaxAmount;
            var em = world.EntityManager;
            if (PrefabLookup.TryGetEntity(world, guid, out var prefab) && prefab.Has<ItemData>(em))
            {
                var data = em.GetComponentData<ItemData>(prefab);
                if (data.MaxAmount > 0) max = data.MaxAmount;
            }

            info = new InventoryItemInfo(max, (long)mapped.ItemCategory, (int)mapped.ItemType);
            _items[item] = info;
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Inventory: item facts for " + item + " threw: " + e.Message);
            return false;
        }
    }

    public int MoveSlot(InventoryRef from, int slot, InventoryRef to, int maxAmount, out string detail)
    {
        if (!TryWorld(out var world, out detail)) return -1;
        var em = world.EntityManager;
        try
        {
            if (!TryInventoryEntity(em, from.Owner, from.Instance, out var fromInv, out detail)) return -1;
            var gameData = world.GetExistingSystemManaged<GameDataSystem>();
            if (gameData == null) { detail = "GameDataSystem is not in the server world"; return -1; }
            var response = InventoryUtilitiesServer.TryMoveItem(
                em, gameData.ItemHashLookupMap, fromInv, slot, to.Owner, -1, false,
                new Il2CppSystem.Nullable_Unboxed<int>(to.Instance), maxAmount);
            detail = response.Result.ToString();
            return (int)response.Result;
        }
        catch (Exception e)
        {
            detail = "TryMoveItem threw: " + e.Message;
            return -1;
        }
    }

    public bool Add(InventoryRef container, int item, int amount, out string detail)
    {
        if (!TryWorld(out var world, out detail)) return false;
        var em = world.EntityManager;
        try
        {
            if (!TryInventoryEntity(em, container.Owner, container.Instance, out var inv, out detail)) return false;
            var owner = container.Owner;
            var target = container.Instance == 0 && owner.Has<PlayerCharacter>(em) ? owner : inv;
            var mapper = world.GetExistingSystemManaged<ServerScriptMapper>();
            if (mapper == null) { detail = "ServerScriptMapper is not in the server world"; return false; }
            var response = mapper.GetServerGameManager().TryAddInventoryItem(target, new PrefabGUID(item), amount);
            detail = response.Result.ToString();
            return response.Result is AddItemResult.Success_Complete or AddItemResult.Success_Partial or AddItemResult.Success_Equipped;
        }
        catch (Exception e)
        {
            detail = "TryAddInventoryItem threw: " + e.Message;
            return false;
        }
    }

    public bool Remove(InventoryRef container, int item, int amount, out string detail)
    {
        if (!TryWorld(out var world, out detail)) return false;
        var em = world.EntityManager;
        try
        {
            if (!TryInventoryEntity(em, container.Owner, container.Instance, out var inv, out detail)) return false;
            InventoryUtilitiesServer.RemoveItemGetRemainder(em, inv, new PrefabGUID(item), amount, out int remainder, true,
                default(Il2CppSystem.Nullable_Unboxed<int>));
            detail = remainder == 0 ? "" : "remainder " + remainder;
            return true;
        }
        catch (Exception e)
        {
            detail = "RemoveItemGetRemainder threw: " + e.Message;
            return false;
        }
    }

    // ---- shared helpers -----------------------------------------------------------------------

    private static bool TryWorld(out World world, out string reason)
    {
        world = Worlds.Server!;
        if (!Plugin.IsServerProcess || world == null || !world.IsCreated)
        {
            reason = "no server world";
            return false;
        }

        reason = "";
        return true;
    }

    /// <summary>True when <paramref name="owner"/> has any inventory.</summary>
    internal static bool HasInventory(EntityManager em, Entity owner) =>
        owner.Has<InventoryBuffer>(em) || owner.Has<InventoryInstanceElement>(em);

    /// <summary>Slot count of the owner's inventory <paramref name="instance"/>, or 0 when unreadable.</summary>
    internal static int SlotCount(EntityManager em, Entity owner, int instance)
    {
        try
        {
            return TryInventoryEntity(em, owner, instance, out var inv, out _) ? em.GetBuffer<InventoryBuffer>(inv).Length : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// The entity that holds <paramref name="owner"/>'s inventory <paramref name="instance"/>.
    /// An owner with its own <c>InventoryBuffer</c> has only instance 0 (itself).
    /// </summary>
    internal static bool TryInventoryEntity(EntityManager em, Entity owner, int instance, out Entity inventory, out string reason)
    {
        inventory = Entity.Null;
        if (!owner.Exists(em))
        {
            reason = "the owner entity " + owner.Index + ":" + owner.Version + " no longer exists";
            return false;
        }

        if (owner.Has<InventoryBuffer>(em))
        {
            if (instance != 0)
            {
                reason = "the owner holds a single inventory; there is no instance " + instance;
                return false;
            }

            inventory = owner;
            reason = "";
            return true;
        }

        if (!owner.Has<InventoryInstanceElement>(em))
        {
            reason = "the entity has no inventory";
            return false;
        }

        var typeIndex = ComponentIndex.IndexOf<InventoryInstanceElement>();
        int length = em.GetBufferLength(owner, typeIndex);
        if (length <= 0 || length > Progenitor.Items.Items.MaxInventoryInstances)
        {
            reason = "the entity's inventory list has " + length + " rows";
            return false;
        }

        if (instance < 0 || instance >= length)
        {
            reason = "the entity has " + length + " inventor" + (length == 1 ? "y" : "ies") + "; there is no instance " + instance;
            return false;
        }

        byte* raw = (byte*)em.GetBufferRawRO(owner, typeIndex);
        if (raw == null)
        {
            reason = "the entity's inventory list could not be read";
            return false;
        }

        byte* row = raw + (instance * Progenitor.Items.Items.InstanceStride);
        int index = *(int*)(row + Progenitor.Items.Items.InstanceEntityOffset);
        int version = *(int*)(row + Progenitor.Items.Items.InstanceEntityOffset + 4);
        var bag = new Entity { Index = index, Version = version };
        if ((index == 0 && version == 0) || !bag.Exists(em) || !bag.Has<InventoryBuffer>(em))
        {
            reason = "inventory instance " + instance + " is not a live inventory entity";
            return false;
        }

        inventory = bag;
        reason = "";
        return true;
    }
}
