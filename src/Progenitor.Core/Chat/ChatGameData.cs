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
    // A miss is retried (ChatNameCache): history is restored when the chat window appears, which can be before the item
    // data and the string table are loaded, and a miss cached for the world's life left those lines' tokens raw.
    private static readonly ChatNameCache ItemNames = new(LookUpItemName);
    private static readonly ChatNameCache ItemDescriptions = new(LookUpItemDescription);
    private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
    private static World? _namesWorld;

    private static double Now() => Clock.Elapsed.TotalSeconds;

    /// <summary>Rises when an item name that was unknown becomes known (or the client world changes): redraw the lines.</summary>
    internal static int ItemNamesVersion => ItemNames.Version;

    /// <summary>Once a frame from the chat view: retry the item names that were unknown. True when one resolved.</summary>
    internal static bool PollItemNames()
    {
        SyncWorld();
        return ItemNames.Poll(Now());
    }

    /// <summary>Both caches belong to one client world; a new one starts them empty.</summary>
    private static void SyncWorld()
    {
        var world = Worlds.Client;
        if (world == null || ReferenceEquals(world, _namesWorld)) return;
        _namesWorld = world;
        ItemNames.Clear();
        ItemDescriptions.Clear();
    }

    /// <summary>An item's localized name (or its prefab name), or null when the id is not an item (or not yet known).</summary>
    internal static string? ItemName(int prefabId)
    {
        SyncWorld();
        return ItemNames.Get(prefabId, Now());
    }

    private static string? LookUpItemName(int prefabId)
    {
        var world = Worlds.Client;
        if (world == null || !world.IsCreated) return null;
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
            return TryLocalizedItemName(world.GetExistingSystemManaged<GameDataSystem>()?.ManagedDataRegistry, guid, out name);
        }
        catch { name = ""; }
        return false;
    }

    /// <summary>
    /// <see cref="TryLocalizedItemName(World, PrefabGUID, out string)"/> with the registry already resolved,
    /// for callers that name many items in one pass (the item catalog build).
    /// </summary>
    internal static bool TryLocalizedItemName(ManagedDataRegistry? registry, PrefabGUID guid, out string name)
    {
        name = "";
        if (registry == null) return false;
        try
        {
            if (registry.TryGet<ManagedItemData>(guid, out var data) && data != null
                && Localization.Localization.TryGetGameString(data.Name, out var s) && !string.IsNullOrWhiteSpace(s))
            {
                name = s.Trim();
                return true;
            }
        }
        catch { name = ""; }
        return false;
    }

    /// <summary>
    /// An item's localized description (<c>ManagedItemData.Description</c> through the game's string table), as the
    /// game's own text (rich text kept), or null when it has none (or it is not yet known). Cached per client world.
    /// </summary>
    internal static string? ItemDescription(int prefabId)
    {
        SyncWorld();
        return ItemDescriptions.Get(prefabId, Now());
    }

    private static string? LookUpItemDescription(int prefabId)
    {
        var world = Worlds.Client;
        if (world == null || !world.IsCreated) return null;
        try
        {
            var registry = world.GetExistingSystemManaged<GameDataSystem>()?.ManagedDataRegistry;
            if (registry != null && registry.TryGet<ManagedItemData>(new PrefabGUID(prefabId), out var data) && data != null
                && Localization.Localization.TryGetGameString(data.Description.Key, out var s) && !string.IsNullOrWhiteSpace(s))
                return s.Trim();
        }
        catch { /* unknown for now: retried */ }

        return null;
    }

    private static readonly Dictionary<int, string> MissionNames = new();
    private static World? _missionNamesWorld;

    /// <summary>
    /// A servant mission's localized name (<c>ManagedMissionData.Name</c> from
    /// <c>GameDataSystem.ManagedDataRegistry</c>, resolved through the game's string table, the
    /// route the game's own hunt notices take), or null when the id is not a mission. Client only;
    /// cached per client world. <c>ManagedMissionData</c> is a managed class, so the registry
    /// lookup instantiates no generic over a game value type.
    /// </summary>
    internal static string? MissionName(int missionGuid)
    {
        var world = Worlds.Client;
        if (world == null || !world.IsCreated || missionGuid == 0) return null;
        if (!ReferenceEquals(world, _missionNamesWorld))
        {
            MissionNames.Clear();
            _missionNamesWorld = world;
        }

        if (MissionNames.TryGetValue(missionGuid, out var cached)) return cached.Length == 0 ? null : cached;
        string name = "";
        try
        {
            var registry = world.GetExistingSystemManaged<GameDataSystem>()?.ManagedDataRegistry;
            if (registry != null && registry.TryGet<ManagedMissionData>(new PrefabGUID(missionGuid), out var data) && data != null
                && Localization.Localization.TryGetGameString(data.Name, out var s) && !string.IsNullOrWhiteSpace(s))
                name = s.Trim();
        }
        catch { name = ""; }

        // An empty answer before the game data is loaded is not cached, so a later ask can succeed.
        if (name.Length > 0) MissionNames[missionGuid] = name;
        return name.Length == 0 ? null : name;
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
