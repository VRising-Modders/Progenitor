using System;
using ProjectM;
using Stunlock.Core;
using Unity.Entities;

namespace Progenitor.Ecs;

/// <summary>
/// Resolves prefab GUID ↔ entity ↔ spawnable name through <see cref="PrefabCollectionSystem"/>.
/// Each call is one the prefab spike already exercised; they are wrapped so a missing world or
/// system is a <c>false</c> rather than an exception into a hook.
/// </summary>
public static class PrefabLookup
{
    /// <summary>
    /// Prefab entity for <paramref name="guid"/>, if the collection has registered it.
    /// Generic-instantiation-safe: <c>PrefabCollectionSystem._PrefabGuidToEntityMap.TryGetValue</c>
    /// is the specific <c>NativeParallelHashMap&lt;PrefabGUID, Entity&gt;</c> instantiation the
    /// game uses and the prefab spike proved works through the interop. Not a general license
    /// to call <c>TryGetValue</c> on other native maps.
    /// </summary>
    public static bool TryGetEntity(World world, PrefabGUID guid, out Entity entity)
    {
        entity = default;
        if (world == null || !world.IsCreated)
            return false;
        try
        {
            var prefabs = world.GetExistingSystemManaged<PrefabCollectionSystem>();
            if (prefabs == null)
                return false;
            return prefabs._PrefabGuidToEntityMap.TryGetValue(guid, out entity);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"PrefabLookup.TryGetEntity({guid.GuidHash}) failed: {e}");
            entity = default;
            return false;
        }
    }

    /// <summary>
    /// Spawnable / asset name for <paramref name="guid"/>.
    /// Generic-instantiation-safe: <c>PrefabLookupMap.TryGetName</c> is a non-generic instance
    /// method (GUID in, string out) used throughout the prefab spike. It does not instantiate
    /// an IL2CPP generic over a game value type.
    /// </summary>
    public static bool TryGetName(World world, PrefabGUID guid, out string name)
    {
        name = "";
        if (world == null || !world.IsCreated)
            return false;
        try
        {
            var prefabs = world.GetExistingSystemManaged<PrefabCollectionSystem>();
            if (prefabs == null)
                return false;
            if (!prefabs._PrefabLookupMap.TryGetName(guid, out var found) || string.IsNullOrEmpty(found))
                return false;
            name = found;
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"PrefabLookup.TryGetName({guid.GuidHash}) failed: {e}");
            name = "";
            return false;
        }
    }

    /// <summary>
    /// GUID registered under the spawnable name, if any.
    /// Generic-instantiation-safe: reads <c>_SpawnableNameToPrefabGuidDictionary</c>, whose
    /// closed type is <c>Dictionary&lt;string, PrefabGUID&gt;</c> — a class-key map the game
    /// itself stores on the system, so the IL2CPP instantiation exists. The key is a string
    /// (not a ValueType-derived wrapper), which avoids the PrefabLookupKey marshal trap.
    /// </summary>
    public static bool TryGetGuidByName(World world, string spawnableName, out PrefabGUID guid)
    {
        guid = default;
        if (world == null || !world.IsCreated || string.IsNullOrEmpty(spawnableName))
            return false;
        try
        {
            var prefabs = world.GetExistingSystemManaged<PrefabCollectionSystem>();
            if (prefabs == null)
                return false;
            var map = prefabs._SpawnableNameToPrefabGuidDictionary;
            if (map == null)
                return false;
            return map.TryGetValue(spawnableName, out guid);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"PrefabLookup.TryGetGuidByName({spawnableName}) failed: {e}");
            guid = default;
            return false;
        }
    }
}
