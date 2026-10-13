using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;

namespace Progenitor.Ecs;

/// <summary>
/// Builds entity queries without the generic <c>WithAll&lt;T&gt;</c> helpers, which are IL2CPP
/// generic instantiations and can hard-crash. Matches the spike pattern:
/// <c>EntityQueryBuilder(Allocator.Temp).AddAll(componentType).WithOptions(...)</c> then
/// <c>em.CreateEntityQuery(ref builder)</c>, then dispose the builder.
///
/// Default queries skip disabled entities (units out of player range and offline
/// characters live on those). <see cref="Build"/> therefore includes them unless the caller opts
/// out. An offline player's <c>User</c> entity is <em>not</em> among them — a disconnect never
/// disables it — so "is this player online" is <c>User.IsConnected</c>, never this option.
/// </summary>
public static class Query
{
    /// <summary>
    /// Creates an <see cref="EntityQuery"/> requiring every type in <paramref name="all"/>.
    /// The caller owns the query and must <see cref="EntityQuery.Dispose"/> it.
    /// Generic-instantiation-safe: <see cref="EntityQueryBuilder.AddAll"/> takes a
    /// <see cref="ComponentType"/> value; <see cref="EntityManager.CreateEntityQuery(ref EntityQueryBuilder)"/>
    /// is non-generic. An empty <paramref name="all"/> matches every entity under the given options.
    /// </summary>
    public static EntityQuery Build(EntityManager em, bool includeDisabled = true, bool includeSystems = false, params ComponentType[] all)
    {
        var options = EntityQueryOptions.Default;
        if (includeDisabled) options |= EntityQueryOptions.IncludeDisabledEntities;
        if (includeSystems) options |= EntityQueryOptions.IncludeSystems;

        var builder = new EntityQueryBuilder(Allocator.Temp);
        try
        {
            if (all != null)
            {
                for (int i = 0; i < all.Length; i++)
                    builder = builder.AddAll(all[i]);
            }
            builder = builder.WithOptions(options);
            return em.CreateEntityQuery(ref builder);
        }
        finally
        {
            builder.Dispose();
        }
    }

    /// <summary>
    /// Runs <see cref="Build"/>, copies matching entities into a managed list, and disposes the
    /// native array and the query. Generic-instantiation-safe: <c>ToEntityArray</c> is
    /// <c>NativeArray&lt;Entity&gt;</c>, an instantiation the game uses everywhere (the chat hook
    /// already depends on it). Returns an empty list on failure after logging.
    /// </summary>
    public static List<Entity> Entities(EntityManager em, bool includeDisabled = true, bool includeSystems = false, params ComponentType[] all)
    {
        var options = EntityQueryOptions.Default;
        if (includeDisabled) options |= EntityQueryOptions.IncludeDisabledEntities;
        if (includeSystems) options |= EntityQueryOptions.IncludeSystems;
        return EntitiesWithOptions(em, options, all);
    }

    /// <summary>
    /// <see cref="Entities(EntityManager, bool, bool, ComponentType[])"/> with explicit
    /// <see cref="EntityQueryOptions"/>, for the reads that must see prefab entities
    /// (<c>EntityQueryOptions.IncludePrefab</c>), such as every faction prefab. Same
    /// non-generic builder path; the caller must not write to a prefab it did not create.
    /// </summary>
    public static List<Entity> EntitiesWithOptions(EntityManager em, EntityQueryOptions options, params ComponentType[] all)
    {
        var result = new List<Entity>();
        EntityQuery query = default;
        bool created = false;
        try
        {
            var builder = new EntityQueryBuilder(Allocator.Temp);
            try
            {
                if (all != null)
                {
                    for (int i = 0; i < all.Length; i++)
                        builder = builder.AddAll(all[i]);
                }
                builder = builder.WithOptions(options);
                query = em.CreateEntityQuery(ref builder);
            }
            finally
            {
                builder.Dispose();
            }
            created = true;
            var array = query.ToEntityArray(Allocator.Temp);
            try
            {
                result.Capacity = array.Length;
                for (int i = 0; i < array.Length; i++)
                    result.Add(array[i]);
            }
            finally
            {
                array.Dispose();
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"Query.Entities failed: {e}");
        }
        finally
        {
            if (created)
                query.Dispose();
        }
        return result;
    }
}
