#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using Progenitor.Ecs;
using ProjectM;
using ProjectM.Network;
using Stunlock.Core;
using Unity.Entities;
using Unity.Transforms;

namespace Progenitor.Chat;

/// <summary>
/// CHAT8: what location and item links need from the client world: the local character's position and an item's name.
/// Read-only, through the access paths Core already uses elsewhere (the <c>LocalCharacter</c> query of the input
/// runtime, <c>Translation</c>, <c>GameDataSystem.ManagedDataRegistry</c> as the build-menu search reads it, and
/// <see cref="PrefabLookup"/>). No new generic instantiation over a game type.
/// </summary>
internal static class ChatGameData
{
    private static readonly Dictionary<int, string> ItemNames = new();
    private static World? _namesWorld;

    /// <summary>An item's localized name (or its prefab name), or null when the id is not an item.</summary>
    internal static string? ItemName(int prefabId)
    {
        var world = Worlds.Client;
        if (world == null || !world.IsCreated) return null;
        if (!ReferenceEquals(world, _namesWorld))
        {
            ItemNames.Clear();
            _namesWorld = world;
        }

        if (ItemNames.TryGetValue(prefabId, out var cached)) return cached.Length == 0 ? null : cached;
        string name = "";
        try
        {
            var guid = new PrefabGUID(prefabId);
            if (TryLocalizedItemName(world, guid, out var s))
                name = s;
            if (name.Length == 0 && PrefabLookup.TryGetName(world, guid, out var prefab) && prefab.StartsWith("Item_", StringComparison.Ordinal))
                name = prefab;
        }
        catch { name = ""; }

        ItemNames[prefabId] = name;
        return name.Length == 0 ? null : name;
    }

    /// <summary>
    /// An item's localized name: <c>ManagedItemData.Name</c> from <c>GameDataSystem.ManagedDataRegistry</c>,
    /// resolved through the game's string table (<see cref="Localization.Localization.TryGetGameString"/>).
    /// Uncached. Always false on the server, which has no string table. Shared with the item catalog.
    /// </summary>
    internal static bool TryLocalizedItemName(World world, PrefabGUID guid, out string name)
    {
        name = "";
        if (world == null || !world.IsCreated) return false;
        try
        {
            var registry = world.GetExistingSystemManaged<GameDataSystem>()?.ManagedDataRegistry;
            if (registry != null && registry.TryGet<ManagedItemData>(guid, out var data) && data != null
                && Localization.Localization.TryGetGameString(data.Name, out var s) && !string.IsNullOrWhiteSpace(s))
            {
                name = s.Trim();
                return true;
            }
        }
        catch { name = ""; }
        return false;
    }

    /// <summary>An item's prefab id from its spawnable prefab name (<c>Item_Weapon_Sword_T01_Bone</c>), or 0.</summary>
    internal static int ItemIdByPrefabName(string name)
    {
        var world = Worlds.Client;
        if (world == null || !world.IsCreated || string.IsNullOrWhiteSpace(name)) return 0;
        return PrefabLookup.TryGetGuidByName(world, name.Trim(), out var guid) && ItemName(guid.GuidHash) != null ? guid.GuidHash : 0;
    }

    /// <summary>The local character's position on the ground plane (x east, z north).</summary>
    internal static bool TryLocalPosition(out float x, out float z)
    {
        x = z = 0f;
        try
        {
            var world = Worlds.Client;
            if (world == null || !world.IsCreated) return false;
            var em = world.EntityManager;
            foreach (var e in Query.Entities(em, includeDisabled: false, includeSystems: false,
                         ComponentIndex.Of<LocalCharacter>(ComponentType.AccessMode.ReadOnly)))
            {
                Unity.Mathematics.float3 t;
                try { t = em.GetComponentData<Translation>(e).Value; }
                catch { continue; }
                x = t.x;
                z = t.z;
                return true;
            }
        }
        catch { /* unknown position: no location link */ }

        return false;
    }
}
#endif
