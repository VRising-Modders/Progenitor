using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.Json;
using ProjectM;
using ProjectM.Network;
using ProjectM.Shared;
using Progenitor.Dev.Ops;
using Progenitor.Ecs;
using Stunlock.Core;
using Unity.Entities;

namespace Progenitor.Dev.Bots;

/// <summary>
/// Server op <c>bot_repair</c>: worn gear durability, and the player's repair of it. Each death drains
/// 12.5% of max durability and a broken piece adds nothing to gear level.
/// <c>action</c> <c>status</c> (default) lists the worn slots; <c>repair</c> queues
/// <c>RepairEquippedItemEvent { EquipmentType @0x0 }</c> (the character screen's repair; bag materials,
/// no station) with the bot's <c>FromCharacter</c>, as <c>bot_use</c> does, for <c>slots</c> (names, default:
/// every damaged one).
///
/// <para>Raw reads, layout-checked: <c>Equipment</c> slots (chest @0x2C, weapon
/// @0x48, grimoire @0x64, boots @0x80, legs @0x9C, gloves @0xD4), <c>EquipmentSlot { NetworkedEntity
/// SlotEntity @0x0 (Entity at +0); bool IsBroken @0x1A }</c>, and on the item <c>ProjectM.Shared.Durability
/// { float Value @0x0; float MaxDurability @0x4; bool IsBroken @0x20 }</c>. EquipmentType values from the
/// ProjectM enum.</para>
/// </summary>
internal static class BotRepair
{
    private static readonly (string name, int offset, int type)[] Slots =
    {
        ("chest", 0x2C, 1), ("weapon", 0x48, 2), ("grimoire", 0x64, 3), ("boots", 0x80, 4), ("legs", 0x9C, 5), ("gloves", 0xD4, 9),
    };

    public static unsafe object Run(World world, JsonElement args)
    {
        if (!Progenitor.Plugin.IsServerProcess) throw new InvalidOperationException("bot_repair runs on the server");
        CheckLayout();
        var em = world.EntityManager;
        var character = BotCast.ResolveCharacter(em, args, out string name);
        var prefabs = world.GetExistingSystemManaged<PrefabCollectionSystem>();
        var eqType = ComponentIndex.Of<Equipment>(ComponentType.AccessMode.ReadOnly);
        if (!em.HasComponent(character, eqType)) throw new InvalidOperationException("character has no Equipment");
        byte* eq = (byte*)em.GetComponentDataRawRO(character, eqType.TypeIndex);
        if (eq == null) throw new InvalidOperationException("Equipment read returned null");
        var durType = ComponentIndex.Of<Durability>(ComponentType.AccessMode.ReadOnly);

        var rows = new List<(string slot, int type, float value, float max, bool broken, string? item)>();
        foreach (var (slot, off, type) in Slots)
        {
            var item = new Entity { Index = *(int*)(eq + off), Version = *(int*)(eq + off + 4) };
            if (item == Entity.Null || !em.Exists(item)) continue;
            bool broken = eq[off + 0x1A] != 0;
            float value = 0, max = 0;
            if (em.HasComponent(item, durType))
            {
                byte* d = (byte*)em.GetComponentDataRawRO(item, durType.TypeIndex);
                if (d != null) { value = *(float*)d; max = *(float*)(d + 4); broken |= d[0x20] != 0; }
            }
            item.TryRead<PrefabGUID>(em, out var guid);
            rows.Add((slot, type, value, max, broken, prefabs != null ? EntityOps.TryPrefabName(prefabs, guid) : null));
        }

        string action = args.TryGetProperty("action", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString()!.ToLowerInvariant() : "status";
        var sent = new List<string>();
        if (action == "repair")
        {
            HashSet<string>? only = null;
            if (args.TryGetProperty("slots", out var s) && s.ValueKind == JsonValueKind.Array)
            {
                only = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var e in s.EnumerateArray()) if (e.GetString() is { } n) only.Add(n);
            }
            var pc = em.GetComponentData<PlayerCharacter>(character);
            var evType = ComponentIndex.Of<RepairEquippedItemEvent>(ComponentType.AccessMode.ReadWrite);
            foreach (var r in rows)
            {
                if (r.max <= 0 || r.value >= r.max) continue;
                if (only != null && !only.Contains(r.slot)) continue;
                var ev = em.CreateEntity(evType, ComponentIndex.Of<FromCharacter>(ComponentType.AccessMode.ReadWrite));
                try
                {
                    int* raw = (int*)em.GetComponentDataRawRW(ev, evType.TypeIndex);
                    if (raw == null) throw new InvalidOperationException("GetComponentDataRawRW returned null");
                    raw[0] = r.type;
                    em.SetComponentData(ev, new FromCharacter { User = pc.UserEntity, Character = character });
                }
                catch { em.DestroyEntity(ev); throw; }
                sent.Add(r.slot);
            }
        }
        var list = new List<object>();
        foreach (var r in rows) list.Add(new { r.slot, r.item, durability = MathF.Round(r.value, 1), max = MathF.Round(r.max, 1), r.broken });
        return new { name, action, slots = list, repairQueued = sent };
    }

    private static bool s_layoutChecked;

    private static void CheckLayout()
    {
        if (s_layoutChecked) return;
        void Expect<T>(string field, int offset)
        {
            int actual = (int)Marshal.OffsetOf<T>(field);
            if (actual != offset) throw new InvalidOperationException($"{typeof(T).Name}.{field} is at 0x{actual:X}, expected 0x{offset:X}; refusing raw reads");
        }
        Expect<Equipment>(nameof(Equipment.ArmorChestSlot), 0x2C);
        Expect<Equipment>(nameof(Equipment.WeaponSlot), 0x48);
        Expect<Equipment>(nameof(Equipment.GrimoireSlot), 0x64);
        Expect<Equipment>(nameof(Equipment.ArmorFootgearSlot), 0x80);
        Expect<Equipment>(nameof(Equipment.ArmorLegsSlot), 0x9C);
        Expect<Equipment>(nameof(Equipment.ArmorGlovesSlot), 0xD4);
        Expect<EquipmentSlot>(nameof(EquipmentSlot.IsBroken), 0x1A);
        Expect<Durability>(nameof(Durability.Value), 0x0);
        Expect<Durability>(nameof(Durability.MaxDurability), 0x4);
        Expect<Durability>(nameof(Durability.IsBroken), 0x20);
        Expect<RepairEquippedItemEvent>(nameof(RepairEquippedItemEvent.EquipmentType), 0x0);
        s_layoutChecked = true;
    }
}
