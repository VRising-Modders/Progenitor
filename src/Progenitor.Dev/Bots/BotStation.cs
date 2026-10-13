using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.Json;
using ProjectM;
using ProjectM.Network;
using Progenitor.Dev.Ops;
using Progenitor.Ecs;
using Stunlock.Core;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace Progenitor.Dev.Bots;

/// <summary>
/// Server op <c>bot_station</c>: a refining station (sawmill, furnace, tannery, loom) as a player sees
/// it at the station window: status, current recipe, what sits in its input and output inventories,
/// and the network ids of both inventories, so the client's <c>bot_event moveall</c> can load the input
/// and collect the output (collecting needs the output inventory's own id: addressing the station
/// takes from its input).
///
/// <para>Raw reads, layout-checked: <c>Refinementstation</c>:
/// <c>NetworkedEntity InputInventoryEntity @0x08</c> and <c>OutputInventoryEntity @0x14</c> (Entity at +0),
/// <c>PrefabGUID CurrentRecipeGuid @0x20</c>, <c>RefinementStatus Status @0x24</c> (0 no input, 1 processing,
/// 2 output full, 3 not powered, 4 damaged, 5 no owner), <c>bool IsWorking @0x44</c>. Inventories are read
/// with the typed <c>InventoryBuffer</c> accessor; <c>NetworkId</c> is copied raw (12 bytes) as BotEvents does.</para>
/// <para>Args: <c>x</c>, <c>z</c> (the station's position, e.g. from <c>castles</c>), optional <c>radius</c>
/// (default 4 m).</para>
/// </summary>
internal static class BotStation
{
    private static readonly string[] StatusNames = { "no input", "processing", "output full", "not powered", "damaged", "no owner" };

    public static unsafe object Run(World world, JsonElement args)
    {
        if (!Progenitor.Plugin.IsServerProcess) throw new InvalidOperationException("bot_station runs on the server");
        CheckLayout();
        var em = world.EntityManager;
        float x = Num(args, "x"), z = Num(args, "z");
        float radius = args.TryGetProperty("radius", out var r) && r.ValueKind == JsonValueKind.Number ? r.GetSingle() : 4f;
        var prefabs = world.GetExistingSystemManaged<PrefabCollectionSystem>();
        var stationType = ComponentIndex.Of<Refinementstation>(ComponentType.AccessMode.ReadOnly);
        var stations = Query.Entities(em, includeDisabled: false, includeSystems: false,
            stationType, ComponentIndex.Of<Translation>(ComponentType.AccessMode.ReadOnly));
        Entity best = Entity.Null;
        float bestD = radius;
        foreach (var e in stations)
        {
            if (!e.TryRead<Translation>(em, out var tr)) continue;
            float d = math.distance(tr.Value.xz, new float2(x, z));
            if (d <= bestD) { bestD = d; best = e; }
        }
        if (best == Entity.Null) return new { found = false, x, z, radius };

        byte* raw = (byte*)em.GetComponentDataRawRO(best, stationType.TypeIndex);
        if (raw == null) throw new InvalidOperationException("Refinementstation read returned null");
        var input = new Entity { Index = *(int*)(raw + 0x08), Version = *(int*)(raw + 0x0C) };
        var output = new Entity { Index = *(int*)(raw + 0x14), Version = *(int*)(raw + 0x18) };
        int recipe = *(int*)(raw + 0x20);
        int status = *(int*)(raw + 0x24);
        bool working = *(raw + 0x44) != 0;
        best.TryRead<PrefabGUID>(em, out var guid);
        return new
        {
            found = true,
            entity = $"{best.Index}:{best.Version}",
            name = prefabs != null ? EntityOps.TryPrefabName(prefabs, guid) : null,
            distance = MathF.Round(bestD, 2),
            status = status >= 0 && status < StatusNames.Length ? StatusNames[status] : status.ToString(),
            working,
            recipe = recipe == 0 || prefabs == null ? null : EntityOps.TryPrefabName(prefabs, new PrefabGUID(recipe)),
            input = Inventory(em, input, prefabs),
            output = Inventory(em, output, prefabs),
        };
    }

    private static unsafe object? Inventory(EntityManager em, Entity inv, PrefabCollectionSystem? prefabs)
    {
        if (!em.Exists(inv)) return null;
        int[]? net = null;
        var netType = ComponentIndex.Of<NetworkId>(ComponentType.AccessMode.ReadOnly);
        if (em.HasComponent(inv, netType) && Marshal.SizeOf<NetworkId>() == 12)
        {
            int* src = (int*)em.GetComponentDataRawRO(inv, netType.TypeIndex);
            if (src != null) net = new[] { src[0], src[1], src[2] };
        }
        var items = new List<object>();
        int slots = 0;
        if (em.HasComponent(inv, ComponentIndex.Of<InventoryBuffer>(ComponentType.AccessMode.ReadOnly)))
        {
            var buffer = em.GetBuffer<InventoryBuffer>(inv);
            slots = buffer.Length;
            var totals = new Dictionary<int, int>();
            for (int i = 0; i < buffer.Length; i++)
            {
                var row = buffer[i];
                if (row.ItemType.GuidHash == 0 || row.Amount <= 0) continue;
                totals.TryGetValue(row.ItemType.GuidHash, out int n);
                totals[row.ItemType.GuidHash] = n + row.Amount;
            }
            foreach (var kv in totals)
                items.Add(new { item = prefabs != null ? EntityOps.TryPrefabName(prefabs, new PrefabGUID(kv.Key)) : null, guid = kv.Key, amount = kv.Value });
        }
        return new { entity = $"{inv.Index}:{inv.Version}", networkId = net, slots, items };
    }

    private static float Num(JsonElement args, string key) =>
        args.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetSingle() : throw new ArgumentException($"args.{key} is required (number)");

    private static bool s_layoutChecked;

    private static void CheckLayout()
    {
        if (s_layoutChecked) return;
        void Expect(string field, int offset)
        {
            int actual = (int)Marshal.OffsetOf<Refinementstation>(field);
            if (actual != offset) throw new InvalidOperationException($"Refinementstation.{field} is at 0x{actual:X}, expected 0x{offset:X}; refusing raw reads");
        }
        Expect(nameof(Refinementstation.InputInventoryEntity), 0x08);
        Expect(nameof(Refinementstation.OutputInventoryEntity), 0x14);
        Expect(nameof(Refinementstation.CurrentRecipeGuid), 0x20);
        Expect(nameof(Refinementstation.Status), 0x24);
        Expect(nameof(Refinementstation.IsWorking), 0x44);
        s_layoutChecked = true;
    }
}
