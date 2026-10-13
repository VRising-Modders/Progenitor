#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using Progenitor.Ecs;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace Progenitor.Models;

/// <summary>
/// Live <see cref="IModelStreamingRuntime"/> over the client world's entity-scene sections.
///
/// <para>Section entities carry <c>SceneSectionData</c> (<c>Unity.Entities</c>, client
/// metadata: <c>Hash128 SceneGUID</c> at 0x0, <c>int SubSectionIndex</c> at 0x10).
/// Loading is the standard request: <c>RequestSceneLoaded</c> added with
/// the non-generic <c>AddComponent(Entity, ComponentType)</c>, flags 0; loaded is
/// <c>Unity.Scenes.IsSectionLoaded</c>. Loaded hybrid-model sections register their assets in
/// <c>ManagedAssetReferences.AssetLookup</c> (<c>Dictionary&lt;AssetGuid, Object&gt;</c>),
/// read through the closed
/// <c>GetComponentObject&lt;Il2CppSystem.Object&gt;(Entity, ComponentType)</c> the Content module
/// already uses. All live 2026-09-28 on scene <c>ddfc6a4db4009e2448b8f8b6f0965b8f</c> section 373.</para>
/// </summary>
internal sealed unsafe class UnityModelStreamingRuntime : IModelStreamingRuntime
{
    internal static readonly UnityModelStreamingRuntime Instance = new();

    private const string HybridModelsRoot = "HybridModels";

    private readonly Dictionary<ModelSection, Entity> _sections = new();
    private ulong _indexedWorld;

    public bool HasClientWorld
    {
        get
        {
            var world = Worlds.Client;
            return world != null && world.IsCreated;
        }
    }

    public SectionProbe Probe(ModelSection section)
    {
        if (!TryFindSection(section, out var em, out var entity)) return SectionProbe.Missing;
        return new SectionProbe(
            found: true,
            requested: em.HasComponent(entity, ComponentIndex.Of<RequestSceneLoaded>(ComponentType.AccessMode.ReadOnly)),
            loaded: em.HasComponent(entity, ComponentIndex.Of<Unity.Scenes.IsSectionLoaded>(ComponentType.AccessMode.ReadOnly)));
    }

    public bool Request(ModelSection section)
    {
        if (!TryFindSection(section, out var em, out var entity)) return false;
        try
        {
            var type = ComponentIndex.Of<RequestSceneLoaded>(ComponentType.AccessMode.ReadWrite);
            if (em.HasComponent(entity, type)) return true;
            // LoadFlags 0: the default streaming request, as the game's own handler issues it.
            return em.AddComponent(entity, type);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Models: request of section " + section + " threw: " + e.Message);
            return false;
        }
    }

    public bool Unrequest(ModelSection section)
    {
        if (!TryFindSection(section, out var em, out var entity)) return false;
        try
        {
            var type = ComponentIndex.Of<RequestSceneLoaded>(ComponentType.AccessMode.ReadWrite);
            return em.HasComponent(entity, type) && em.RemoveComponent(entity, type);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Models: unrequest of section " + section + " threw: " + e.Message);
            return false;
        }
    }

    public object? FindPrefab(ModelSection section, string prefabName)
        => FindInManagedAssets(prefabName) ?? FindLoadedPrefabAsset(prefabName);

    public bool GameUsesPrefab(string prefabName)
    {
        try
        {
            var root = GameObject.Find(HybridModelsRoot);
            if (root == null) return false;
            string clone = prefabName + "(Clone)";
            var t = root.transform;
            for (int i = 0; i < t.childCount; i++)
                if (string.Equals(t.GetChild(i).name, clone, StringComparison.Ordinal)) return true;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Models: GameUsesPrefab threw: " + e.Message);
            // Unknown: keep the request rather than risk unloading a model a unit is drawing with.
            return true;
        }
        return false;
    }

    private static GameObject? FindInManagedAssets(string prefabName)
    {
        var world = Worlds.Client;
        if (world == null || !world.IsCreated) return null;
        var em = world.EntityManager;
        var type = ComponentIndex.Of<ManagedAssetReferences>(ComponentType.AccessMode.ReadOnly);
        var query = em.CreateEntityQuery(new EntityQueryDesc
        {
            All = new[] { type },
            Options = EntityQueryOptions.IncludeDisabled | EntityQueryOptions.IncludePrefab,
        });
        try
        {
            var entities = query.ToEntityArray(Allocator.TempJob);
            try
            {
                foreach (var e in entities)
                {
                    Il2CppSystem.Object? boxed;
                    try { boxed = em.GetComponentObject<Il2CppSystem.Object>(e, type); }
                    catch { continue; }
                    var refs = boxed?.TryCast<ManagedAssetReferences>();
                    if (refs?.AssetLookup == null) continue;
                    foreach (var pair in refs.AssetLookup)
                    {
                        var obj = pair.Value;
                        if (obj == null) continue;
                        string name;
                        try { name = obj.name; } catch { continue; }
                        if (!string.Equals(name, prefabName, StringComparison.Ordinal)) continue;
                        var go = obj.TryCast<GameObject>();
                        if (go != null) return go;
                    }
                }
            }
            finally { entities.Dispose(); }
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogWarning("Models: ManagedAssetReferences scan threw: " + ex.Message);
        }
        finally { query.Dispose(); }
        return null;
    }

    /// <summary>Fallback: a root GameObject asset (not a scene object) with this name.</summary>
    private static GameObject? FindLoadedPrefabAsset(string prefabName)
    {
        try
        {
            var all = Resources.FindObjectsOfTypeAll(Il2CppType.Of<GameObject>());
            for (int i = 0; i < all.Length; i++)
            {
                var go = all[i]?.TryCast<GameObject>();
                if (go == null) continue;
                if (!string.Equals(go.name, prefabName, StringComparison.Ordinal)) continue;
                if (go.transform.parent != null || go.scene.IsValid()) continue;
                return go;
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Models: FindObjectsOfTypeAll threw: " + e.Message);
        }
        return null;
    }

    private bool TryFindSection(ModelSection section, out EntityManager em, out Entity entity)
    {
        em = default;
        entity = Entity.Null;
        var world = Worlds.Client;
        if (world == null || !world.IsCreated) return false;
        em = world.EntityManager;
        if (_indexedWorld != world.SequenceNumber || !_sections.TryGetValue(section, out entity) || !em.Exists(entity))
        {
            Reindex(world);
            if (!_sections.TryGetValue(section, out entity)) return false;
        }
        return em.Exists(entity);
    }

    /// <summary>One pass over every section entity (about 1 200 on this build) keyed by scene and index.</summary>
    private void Reindex(World world)
    {
        _sections.Clear();
        _indexedWorld = world.SequenceNumber;
        var em = world.EntityManager;
        var type = ComponentIndex.Of<SceneSectionData>(ComponentType.AccessMode.ReadOnly);
        var query = em.CreateEntityQuery(new EntityQueryDesc
        {
            All = new[] { type },
            Options = EntityQueryOptions.IncludeDisabled | EntityQueryOptions.IncludePrefab,
        });
        try
        {
            var entities = query.ToEntityArray(Allocator.TempJob);
            try
            {
                foreach (var e in entities)
                {
                    byte* data = (byte*)em.GetComponentDataRawRO(e, type.TypeIndex);
                    if (data == null) continue;
                    uint* words = (uint*)data;
                    int index = *(int*)(data + 0x10);
                    var key = new ModelSection(ModelSection.SceneGuidFromHashWords(words[0], words[1], words[2], words[3]), index);
                    _sections[key] = e;
                }
            }
            finally { entities.Dispose(); }
        }
        finally { query.Dispose(); }
    }
}
#endif
