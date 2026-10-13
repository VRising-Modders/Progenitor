using System;
using System.Collections.Generic;
using System.Text.Json;
using ProjectM;
using ProjectM.Network;
using Progenitor.Dev.Ops;
using Progenitor.Ecs;
using Stunlock.Core;
using Unity.Entities;

namespace Progenitor.Dev.Bots;

/// <summary>
/// Server op <c>bot_inventory</c>: what a bot carries, totaled by item. Server-side because the
/// server holds the authoritative bag. Same read path as Core <c>Items</c>: the character's
/// <c>InventoryInstanceElement</c> buffer (raw, stride 48, <c>ExternalInventoryEntity</c> at 16;
/// constants and their evidence in <c>Progenitor.Core/Items/Items.cs</c>) names the bag entity,
/// whose <c>InventoryBuffer</c> is read with the typed accessor Core already uses.
///
/// <para>Args: <c>user</c> or <c>platformId</c>; optional <c>name</c> substring filter.</para>
/// </summary>
internal static class BotInventory
{
    private const int InstanceStride = 48;       // = Items.InstanceStride
    private const int InstanceEntityOffset = 16; // = Items.InstanceEntityOffset
    private const int MaxInventoryInstances = 8; // = Items.MaxInventoryInstances

    public static unsafe object Run(World world, JsonElement args)
    {
        if (!Progenitor.Plugin.IsServerProcess)
            throw new InvalidOperationException("bot_inventory runs on the server");
        var em = world.EntityManager;
        var character = BotCast.ResolveCharacter(em, args, out string name);
        string? filter = args.TryGetProperty("name", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;
        var prefabs = world.GetExistingSystemManaged<PrefabCollectionSystem>();

        var totals = new Dictionary<int, int>();
        var slotRows = new List<(int slot, int guid, int amount)>();
        int slots = 0, used = 0;
        foreach (var bag in Bags(em, character))
        {
            var buffer = em.GetBuffer<InventoryBuffer>(bag);
            for (int i = 0; i < buffer.Length; i++)
            {
                slots++;
                var row = buffer[i];
                if (row.ItemType.GuidHash == 0 || row.Amount <= 0) continue;
                used++;
                // Slot index as the equip op counts it (the main bag, in buffer order).
                slotRows.Add((slots - 1, row.ItemType.GuidHash, row.Amount));
                totals.TryGetValue(row.ItemType.GuidHash, out int n);
                totals[row.ItemType.GuidHash] = n + row.Amount;
            }
        }
        var items = new List<object>();
        foreach (var kv in totals)
        {
            string? itemName = prefabs != null ? EntityOps.TryPrefabName(prefabs, new PrefabGUID(kv.Key)) : null;
            if (filter != null && (itemName == null || itemName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)) continue;
            items.Add(new { item = itemName, guid = kv.Key, amount = kv.Value });
        }
        var slotsNamed = new List<object>();
        foreach (var r in slotRows)
            slotsNamed.Add(new { r.slot, item = prefabs != null ? EntityOps.TryPrefabName(prefabs, new PrefabGUID(r.guid)) : null, r.guid, r.amount });
        return new { name, slots, used, free = slots - used, items, occupied = slotsNamed, equipped = Equipped(em, character, prefabs), gear = GearLevel(em, character) };
    }

    /// <summary>
    /// What the character wears, by slot. Same typed <c>GetComponentData&lt;Equipment&gt;</c> read as
    /// Core <c>Items</c> (EquipmentSlot.SlotId per slot); an empty slot is omitted.
    /// </summary>
    /// <summary>
    /// Gear level as the game sums it: <c>Equipment.WeaponLevel + ArmorLevel + SpellLevel</c> (each a
    /// <c>ModifiableFloat</c>, <c>_Value @0x0</c>; summed as the game's native GetUnitStats does).
    /// Same typed Equipment read as Core Items.
    /// </summary>
    private static object? GearLevel(EntityManager em, Entity character)
    {
        if (!em.HasComponent(character, ComponentIndex.Of<Equipment>(ComponentType.AccessMode.ReadOnly))) return null;
        var e = em.GetComponentData<Equipment>(character);
        float w = e.WeaponLevel.Value, a = e.ArmorLevel.Value, sp = e.SpellLevel.Value;
        return new { weapon = w, armor = a, spell = sp, total = w + a + sp };
    }

    private static List<object> Equipped(EntityManager em, Entity character, PrefabCollectionSystem? prefabs)
    {
        var rows = new List<object>();
        if (!em.HasComponent(character, ComponentIndex.Of<Equipment>(ComponentType.AccessMode.ReadOnly))) return rows;
        var e = em.GetComponentData<Equipment>(character);
        void Add(string slot, EquipmentSlot s)
        {
            if (s.SlotId.GuidHash == 0) return;
            rows.Add(new { slot, item = prefabs != null ? EntityOps.TryPrefabName(prefabs, s.SlotId) : null, guid = s.SlotId.GuidHash });
        }
        Add("weapon", e.WeaponSlot);
        Add("headgear", e.ArmorHeadgearSlot);
        Add("chest", e.ArmorChestSlot);
        Add("gloves", e.ArmorGlovesSlot);
        Add("legs", e.ArmorLegsSlot);
        Add("boots", e.ArmorFootgearSlot);
        Add("cloak", e.CloakSlot);
        Add("grimoire", e.GrimoireSlot);
        return rows;
    }

    /// <summary>
    /// Server op <c>bot_cost</c>: what building a structure or crafting a recipe costs. <c>BlueprintRequirementBuffer</c>
    /// on the prefab entity: PrefabGUID @0x0, int Amount @0x4, stride 8; raw
    /// buffer read. Args: <c>prefab</c> (guid or name).
    /// </summary>
    public static unsafe object Cost(World world, JsonElement args)
    {
        var em = world.EntityManager;
        var prefabs = world.GetExistingSystemManaged<PrefabCollectionSystem>() ?? throw new InvalidOperationException("no PrefabCollectionSystem");
        PrefabGUID guid;
        if (args.TryGetProperty("prefab", out var p) && p.ValueKind == JsonValueKind.Number) guid = new PrefabGUID(p.GetInt32());
        else if (args.TryGetProperty("prefab", out p) && p.ValueKind == JsonValueKind.String && prefabs._PrefabLookupMap.TryGetPrefabGuidWithName(p.GetString(), out var g, true)) guid = g;
        else throw new ArgumentException("args.prefab (guid or name) is required");
        if (!prefabs._PrefabGuidToEntityMap.TryGetValue(guid, out var prefab) || prefab == Entity.Null)
            throw new InvalidOperationException($"prefab {guid.GuidHash} not found");
        // Buildings: BlueprintRequirementBuffer; recipes: RecipeRequirementBuffer.
        // Both are { PrefabGUID @0x0, int Amount @0x4 }, stride 8.
        var rows = new List<object>();
        ReadRequirements(em, prefab, ComponentIndex.Of<BlueprintRequirementBuffer>(ComponentType.AccessMode.ReadOnly), prefabs, rows);
        ReadRequirements(em, prefab, ComponentIndex.Of<RecipeRequirementBuffer>(ComponentType.AccessMode.ReadOnly), prefabs, rows);
        // RecipeData: CraftDuration f32 @0xC, AlwaysUnlocked bool @0x14 (raw: bools).
        object? recipe = null;
        var recipeType = ComponentIndex.Of<RecipeData>(ComponentType.AccessMode.ReadOnly);
        if (em.HasComponent(prefab, recipeType))
        {
            byte* r = (byte*)em.GetComponentDataRawRO(prefab, recipeType.TypeIndex);
            if (r != null) recipe = new { craftSeconds = *(float*)(r + 0xC), alwaysUnlocked = r[0x14] != 0 };
        }
        return new { prefab = EntityOps.TryPrefabName(prefabs, guid), guid = guid.GuidHash, cost = rows, recipe };
    }

    private static unsafe void ReadRequirements(EntityManager em, Entity prefab, ComponentType type, PrefabCollectionSystem prefabs, List<object> rows)
    {
        if (!em.HasComponent(prefab, type)) return;
        int n = em.GetBufferLength(prefab, type.TypeIndex);
        byte* raw = (byte*)em.GetBufferRawRO(prefab, type.TypeIndex);
        for (int i = 0; raw != null && i < n; i++)
        {
            int item = *(int*)(raw + i * 8);
            rows.Add(new { item = EntityOps.TryPrefabName(prefabs, new PrefabGUID(item)), guid = item, amount = *(int*)(raw + i * 8 + 4) });
        }
    }

    /// <summary>
    /// Server op <c>bot_use</c>: use (consume) the item in a bag slot, e.g. an eaten rat for blood.
    /// Injects the game's own <c>UseItemEvent</c> { int SlotIndex @0x0; NetworkId Target @0x4 } (server
    /// metadata; EventId -1522376918) with <c>FromCharacter</c>, the pattern Core's
    /// Items.Equip proved for EquipItemEvent (a client-sent equip was ignored live). Fields are poked
    /// through GetComponentDataRawRW (event TypeSizes read 0 through this interop); Target stays empty
    /// for self-use. Args: <c>user</c>, <c>slot</c>.
    /// </summary>
    public static unsafe object Use(World world, JsonElement args)
    {
        if (!Progenitor.Plugin.IsServerProcess) throw new InvalidOperationException("bot_use runs on the server");
        var em = world.EntityManager;
        var character = BotCast.ResolveCharacter(em, args, out string name);
        int slot = args.TryGetProperty("slot", out var sl) && sl.ValueKind == JsonValueKind.Number ? sl.GetInt32() : -1;
        if (slot < 0) throw new ArgumentException("args.slot (bag slot) is required");
        // A stale slot would queue a silent no-op; refuse it (slot numbering as bot_inventory's `occupied`).
        if (!SlotHoldsItem(em, character, slot)) throw new ArgumentException($"bag slot {slot} is empty or out of range");
        if ((int)System.Runtime.InteropServices.Marshal.OffsetOf<UseItemEvent>(nameof(UseItemEvent.SlotIndex)) != 0x0
            || (int)System.Runtime.InteropServices.Marshal.OffsetOf<UseItemEvent>(nameof(UseItemEvent.Target)) != 0x4)
            throw new InvalidOperationException("UseItemEvent layout differs from the dump; refusing the raw write");
        var pc = em.GetComponentData<PlayerCharacter>(character);
        var user = pc.UserEntity;
        var type = ComponentIndex.Of<UseItemEvent>(ComponentType.AccessMode.ReadWrite);
        var ev = em.CreateEntity(type, ComponentIndex.Of<FromCharacter>(ComponentType.AccessMode.ReadWrite));
        try
        {
            byte* raw = (byte*)em.GetComponentDataRawRW(ev, type.TypeIndex);
            if (raw == null) throw new InvalidOperationException("GetComponentDataRawRW returned null");
            *(int*)raw = slot; // Target @0x4..0x10 left zero: NetworkId.Empty, self-use
            em.SetComponentData(ev, new FromCharacter { User = user, Character = character });
        }
        catch
        {
            em.DestroyEntity(ev);
            throw;
        }
        return new { name, slot, @event = $"{ev.Index}:{ev.Version}", note = "queued; check blood/inventory after a moment" };
    }

    private static bool SlotHoldsItem(EntityManager em, Entity character, int slot)
    {
        int index = slot;
        foreach (var bag in Bags(em, character))
        {
            var buffer = em.GetBuffer<InventoryBuffer>(bag);
            if (index < buffer.Length) return buffer[index].ItemType.GuidHash != 0 && buffer[index].Amount > 0;
            index -= buffer.Length;
        }
        return false;
    }

    private static unsafe List<Entity> Bags(EntityManager em, Entity owner)
    {
        var result = new List<Entity>();
        var invType = ComponentIndex.Of<InventoryBuffer>(ComponentType.AccessMode.ReadOnly);
        if (em.HasComponent(owner, invType)) { result.Add(owner); return result; }
        var instType = ComponentIndex.Of<InventoryInstanceElement>(ComponentType.AccessMode.ReadOnly);
        if (!em.HasComponent(owner, instType)) return result;
        int length = Math.Min(em.GetBufferLength(owner, instType.TypeIndex), MaxInventoryInstances);
        byte* raw = (byte*)em.GetBufferRawRO(owner, instType.TypeIndex);
        if (raw == null) return result;
        for (int i = 0; i < length; i++)
        {
            byte* row = raw + i * InstanceStride;
            var bag = new Entity { Index = *(int*)(row + InstanceEntityOffset), Version = *(int*)(row + InstanceEntityOffset + 4) };
            if (bag == Entity.Null || !em.Exists(bag) || !em.HasComponent(bag, invType)) continue;
            result.Add(bag);
        }
        return result;
    }
}
