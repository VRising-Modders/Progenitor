using System;
using ProjectM.CastleBuilding;
using ProjectM.Network;
using Progenitor.Ecs;
using Unity.Entities;

namespace Progenitor.Dev.Ops;

/// <summary>Server-thread, one-territory castle generation for an explicitly authorized test world.</summary>
public static class StressPlace
{
    /// <summary>Adds real blood essence to an initialized heart inventory; verify fuel on a later frame.</summary>
    public static Progenitor.Items.ItemGrantResult Fuel(World world, Entity heart, int amount)
    {
        RequireServerThread(world);
        if (!heart.Has<CastleHeart>(world.EntityManager)) throw new ArgumentException("Target is not a castle heart.");
        if (amount < 1 || amount > 1000) throw new ArgumentOutOfRangeException(nameof(amount));
        if (!PrefabLookup.TryGetGuidByName(world, "Item_BloodEssence_T01", out var guid))
            throw new InvalidOperationException("Blood essence prefab unavailable.");
        return Progenitor.Items.Items.Grant(world, heart, guid, amount);
    }

    /// <summary>
    /// Runs the game's generation event once. The caller must enforce the test-world boundary,
    /// back up the save, and verify initialization on later frames. This does not assign ownership.
    /// </summary>
    public static string Generate(World world, Entity territory, Entity adminUser)
    {
        RequireServerThread(world);
        var em = world.EntityManager;
        if (!territory.Has<CastleTerritory>(em)) return "missing-territory";
        if (!adminUser.Has<User>(em) || !em.GetComponentData<User>(adminUser).IsAdmin) return "admin-required";
        var data = em.GetComponentData<CastleTerritory>(territory);
        if (em.Exists(data.CastleHeart)) return "occupied";
        if (world.GetExistingSystemManaged<GenerateCastleSystem>() != null) return "generator-already-active";
        if (world.GetExistingSystemManaged<GenerateCastlePrefabsCollectionSystem>() == null) return "prefabs-unavailable";
        if (Query.Entities(em, true, false, ComponentIndex.Of<GenerateCastleEvent>(ComponentType.AccessMode.ReadWrite)).Count != 0 ||
            Query.Entities(em, true, false, ComponentIndex.Of<GenerateCastlesInRegionEvent>(ComponentType.AccessMode.ReadWrite)).Count != 0)
            return "pending-generation-events";

        GenerateCastleSystem? generator = null;
        Entity request = Entity.Null;
        try
        {
            // An isolated managed system is never added to an update group or left enabled.
            generator = world.CreateSystemManaged<GenerateCastleSystem>();
            request = em.CreateEntity(ComponentIndex.Of<GenerateCastleEvent>(ComponentType.AccessMode.ReadWrite), ComponentIndex.Of<FromCharacter>(ComponentType.AccessMode.ReadWrite));
            request.Write(em, new GenerateCastleEvent { TerritoryIndex = data.CastleTerritoryIndex });
            request.Write(em, new FromCharacter { User = adminUser, Character = Entity.Null });
            generator.Update();
            return "submitted-awaiting-initialization";
        }
        finally
        {
            if (request != Entity.Null && em.Exists(request)) em.DestroyEntity(request);
            if (generator != null) world.DestroySystemManaged(generator);
        }
    }

    private static void RequireServerThread(World world)
    {
        if (!MainThread.IsMainThread) throw new InvalidOperationException("Castle generation must run on the game main thread.");
        if (world == null || !world.IsCreated || !ReferenceEquals(world, Progenitor.Worlds.Server))
            throw new InvalidOperationException("Castle generation requires the current server world.");
    }
}
