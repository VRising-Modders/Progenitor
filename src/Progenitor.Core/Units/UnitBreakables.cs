using System;
using ProjectM;
using ProjectM.Shared;
using ProjectM.Tiles;
using Progenitor.Ecs;
using Stunlock.Core;
using Unity.Entities;
using Unity.Transforms;

namespace Progenitor.Units;

/// <summary>
/// Runtime half of <see cref="PluginUnits.ClearBreakables"/>. The breakable props of a dungeon or
/// boss arena (banners, braziers, vases, pillars) are tile models spawned by the area's spawn chains:
/// they carry <c>SpawnChainChild</c> (found live 2026-09-25 in Dracula's throne hall, where the chains
/// are <c>DraculaSpawnChain</c> entities). Destroyed through <c>DestroyUtility</c>, the game's destroy
/// path, which replicates to clients. The chains are left alone, so the game can put the props back;
/// some do so within seconds (Dracula's arena pillars came back in about 20 s). Server only.
/// </summary>
internal static class UnitBreakables
{
    internal static int Clear(World? world, UnitVec3 center, float radius, Func<int, bool>? keep, out string why)
    {
        if (world == null || !world.IsCreated)
        {
            why = "the server world is not ready";
            return 0;
        }
        var em = world.EntityManager;
        var query = em.CreateEntityQuery(
            ComponentIndex.Of<SpawnChainChild>(ComponentType.AccessMode.ReadOnly),
            ComponentIndex.Of<TileModel>(ComponentType.AccessMode.ReadOnly),
            ComponentIndex.Of<Translation>(ComponentType.AccessMode.ReadOnly),
            ComponentIndex.Of<PrefabGUID>(ComponentType.AccessMode.ReadOnly));
        var props = query.ToEntityArray(Unity.Collections.Allocator.Temp);
        int count = 0;
        try
        {
            foreach (var prop in props)
            {
                var p = em.GetComponentData<Translation>(prop).Value;
                if (!UnitBreakablesPolicy.InArea(new UnitVec3(p.x, p.y, p.z), center, radius)) continue;
                if (em.HasComponent<UnitStats>(prop)) continue;
                if (keep != null && keep(em.GetComponentData<PrefabGUID>(prop).GuidHash)) continue;
                DestroyUtility.Destroy(em, prop, DestroyDebugReason.None);
                count++;
            }
        }
        finally
        {
            props.Dispose();
            query.Dispose();
        }
        why = "";
        return count;
    }
}
