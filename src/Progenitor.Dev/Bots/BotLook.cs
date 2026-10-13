// EXPERIMENTAL, EARLY DEVELOPMENT, development-only: part of the headless bot experiment (tools/bots/README.md), not a
// supported framework feature. Ships only in the Dev bridge, never in the Release package.
using System;
using System.Collections.Generic;
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
/// Client op <c>bot_look</c>: the "eyes" of a headless player. A read-only snapshot of what this
/// client's world holds around its own character: itself (position, health) and nearby units,
/// resource nodes, breakable objects, structures and players (anything with <c>Health</c>, i.e.
/// anything that can be hit), nearest first. Only what the server replicated to this
/// client is visible, the same knowledge a human player's client has.
///
/// <para>Args: <c>radius</c> (meters, default 30, max 120), <c>limit</c> (default 40, max 200),
/// <c>kind</c> (optional filter: unit, resource, breakable, object, structure, player), <c>name</c> (optional
/// case-insensitive substring of the prefab name).</para>
/// </summary>
internal static class BotLook
{
    public static object Run(World world, JsonElement args)
    {
        if (Progenitor.Plugin.IsServerProcess)
            throw new InvalidOperationException("bot_look runs on a client");
        var em = world.EntityManager;
        float radius = Math.Clamp(NumOr(args, "radius", 30f), 1f, 120f);
        int limit = (int)Math.Clamp(NumOr(args, "limit", 40f), 1f, 200f);
        string? kind = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String
            ? k.GetString()?.Trim().ToLowerInvariant() : null;

        string? nameFilter = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("name", out var nf) && nf.ValueKind == JsonValueKind.String
            ? nf.GetString() : null;
        var locals = Query.Entities(em, includeDisabled: false, includeSystems: false,
            ComponentIndex.Of<LocalCharacter>(ComponentType.AccessMode.ReadOnly));
        if (locals.Count == 0)
            return new { self = (object?)null, note = "no local character (not in the world yet, or no character)" };
        var me = locals[0];
        me.TryRead<Translation>(em, out var myTr);
        float3 pos = myTr.Value;
        var prefabs = world.GetExistingSystemManaged<PrefabCollectionSystem>();

        var found = new List<(float d, object row)>();
        // Three sources: anything with Health (units, players), anything with EntityCategory (wild
        // resource nodes), and static breakables (urns, coffins) that only carry
        // SpawnPhysicsObjectOnDeath on the client. Deduplicated.
        var entities = new List<Entity>();
        var seen = new HashSet<Entity>();
        foreach (var marker in new[]
        {
            ComponentIndex.Of<Health>(ComponentType.AccessMode.ReadOnly),
            ComponentIndex.Of<EntityCategory>(ComponentType.AccessMode.ReadOnly),
            ComponentIndex.Of<SpawnPhysicsObjectOnDeath>(ComponentType.AccessMode.ReadOnly),
        })
        {
            var batch = Query.Entities(em, includeDisabled: false, includeSystems: false,
                ComponentIndex.Of<PrefabGUID>(ComponentType.AccessMode.ReadOnly),
                ComponentIndex.Of<Translation>(ComponentType.AccessMode.ReadOnly),
                marker);
            for (int b = 0; b < batch.Count; b++)
                if (seen.Add(batch[b])) entities.Add(batch[b]);
        }
        for (int i = 0; i < entities.Count; i++)
        {
            var e = entities[i];
            if (e == me) continue;
            if (!e.TryRead<Translation>(em, out var tr)) continue;
            float d = math.distance(tr.Value.xz, pos.xz);
            if (d > radius) continue;
            bool isPlayer = em.HasComponent(e, ComponentIndex.Of<PlayerCharacter>(ComponentType.AccessMode.ReadOnly));
            string category = isPlayer ? "player" : Category(em, e);
            if (kind != null && category != kind) continue;
            if (category is "spell" or "projectile" or "terrain" or "other") continue;
            e.TryRead<PrefabGUID>(em, out var guid);
            var (hp, maxHp, dead) = ReadHealth(em, e);
            if (dead) continue;
            string? prefabName = prefabs != null ? EntityOps.TryPrefabName(prefabs, guid) : null;
            if (nameFilter != null && (prefabName == null || prefabName.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) < 0)) continue;
            found.Add((d, new
            {
                entity = $"{e.Index}:{e.Version}",
                name = prefabName,
                kind = category,
                x = tr.Value.x, y = tr.Value.y, z = tr.Value.z,
                distance = MathF.Round(d, 2),
                health = hp,
                maxHealth = maxHp,
                level = ReadLevel(em, e),
            }));
        }
        found.Sort((a, b) => a.d.CompareTo(b.d));
        var rows = new List<object>(Math.Min(limit, found.Count));
        for (int i = 0; i < found.Count && i < limit; i++) rows.Add(found[i].row);

        var (myHp, myMax, myDead) = ReadHealth(em, me);
        return new
        {
            self = new { entity = $"{me.Index}:{me.Version}", x = pos.x, y = pos.y, z = pos.z, health = myHp, maxHealth = myMax, dead = myDead },
            radius,
            total = found.Count,
            nearby = rows,
        };
    }

    private static string Category(EntityManager em, Entity e)
    {
        // Breakables (urns, crates) carry Health but no EntityCategory.
        if (!e.TryRead<EntityCategory>(em, out var c))
            return em.HasComponent(e, ComponentIndex.Of<SpawnPhysicsObjectOnDeath>(ComponentType.AccessMode.ReadOnly)) ? "breakable" : "object";
        return (MainEntityCategory)c.MainCategoryInt.Value switch
        {
            MainEntityCategory.Unit => "unit",
            MainEntityCategory.Resource => "resource",
            MainEntityCategory.Structure => "structure",
            MainEntityCategory.Terrain => "terrain",
            MainEntityCategory.Spell => "spell",
            MainEntityCategory.Projectile => "projectile",
            _ => "other",
        };
    }

    // Health carries a bool: typed GetComponentData (CLAUDE.md), not a raw read.
    private static (float? hp, float? max, bool dead) ReadHealth(EntityManager em, Entity e)
    {
        try
        {
            if (!em.HasComponent(e, ComponentIndex.Of<Health>(ComponentType.AccessMode.ReadOnly))) return (null, null, false);
            var h = em.GetComponentData<Health>(e);
            return (MathF.Round(h.Value, 1), MathF.Round(h.MaxHealth.Value, 1), h.IsDead);
        }
        catch { return (null, null, false); }
    }

    private static float NumOr(JsonElement a, string k, float d) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetSingle() : d;

    /// <summary>
    /// Unit level as the game shows it: <c>UnitLevel { ModifiableInt Level @0x0 (int _Value @0x0);
    /// bool HideLevel @0x4 }</c>. Read raw (the struct holds a bool); null when
    /// the entity has no level (resources, structures).
    /// </summary>
    private static unsafe int? ReadLevel(EntityManager em, Entity e)
    {
        var type = ComponentIndex.Of<UnitLevel>(ComponentType.AccessMode.ReadOnly);
        if (!em.HasComponent(e, type)) return null;
        byte* raw = (byte*)em.GetComponentDataRawRO(e, type.TypeIndex);
        return raw == null ? null : *(int*)raw;
    }
}
