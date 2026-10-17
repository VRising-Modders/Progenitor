#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using Progenitor.Ecs;
using ProjectM;
using ProjectM.UI;
using Stunlock.Core;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace Progenitor.Ui;

/// <summary>
/// HUB1: the game side of <see cref="UiBuildingIcons"/>. Read only: it loads no asset and requests no streaming.
///
/// <para><b>Blueprint icons.</b> A blueprint's icon asset id is <c>ManagedBlueprintData.Icon</c> (an <c>AssetGuid</c>;
/// <c>ManagedDataRegistry.TryGet</c> over that reference type, as the build-menu search reads its names). The build
/// menu resolves a blueprint's sprite through its UI asset loader, which finds the loaded section holding the asset and
/// reads the section's <c>ManagedAssetReferences.AssetLookup</c> (<c>Dictionary&lt;AssetGuid, Object&gt;</c>, an
/// instantiation the game uses). This reads the same dictionaries on every loaded section, the way the model loader
/// reads them, with <c>ContainsKey</c> and the indexer only; no generic over a game value type is added.</para>
///
/// <para><b>Build-menu cells.</b> <c>BuildMenu_StructureEntry</c>: each drawn cell's building, loading flag and sprite.
/// A cell's <c>ImageFadeIn.SetSprite</c> ignores a null sprite, so a cell whose sprite has not arrived keeps the one
/// it showed before; cells only name icons for blueprints without an icon asset id
/// (<see cref="UiBuildingIcons.AcceptsCell"/>).</para>
/// </summary>
internal static class UnityBuildingIcons
{
    private static readonly Dictionary<int, AssetGuid> IconKeys = new();
    private static readonly HashSet<int> NoIconKey = new();

    /// <summary>A world change or Core unload: forget the blueprint icon ids read so far.</summary>
    internal static void Clear()
    {
        IconKeys.Clear();
        NoIconKey.Clear();
    }

    /// <summary>One scan: resolve the waiting buildings by their blueprint icons, then read the build menu's cells.</summary>
    internal static void Scan(World world)
    {
        ManagedDataRegistry? registry = null;
        try { registry = world.GetExistingSystemManaged<GameDataSystem>()?.ManagedDataRegistry; }
        catch { registry = null; }

        var waiting = UiBuildingIcons.WaitingNow();
        var keyed = new List<(int Prefab, AssetGuid Key)>(waiting.Length);
        foreach (int prefab in waiting)
            if (TryKey(registry, prefab, out var key)) keyed.Add((prefab, key));
        if (keyed.Count > 0) ResolveFromLoadedAssets(world, keyed);

        HarvestBuildMenuCells(registry);
    }

    /// <summary>
    /// <paramref name="prefab"/>'s blueprint icon id, telling the cache whether it has one. A building without
    /// <c>ManagedBlueprintData</c> is asked again on a later scan (game data may come up after the first one).
    /// </summary>
    private static bool TryKey(ManagedDataRegistry? registry, int prefab, out AssetGuid key)
    {
        if (IconKeys.TryGetValue(prefab, out key)) return true;
        key = default;
        if (NoIconKey.Contains(prefab)) return false;
        if (registry == null) return false;
        ManagedBlueprintData? data = null;
        try
        {
            if (!registry.TryGet<ManagedBlueprintData>(new PrefabGUID(prefab), out data)) data = null;
        }
        catch { data = null; }

        if (data == null)
        {
            UiBuildingIcons.NoteKey(prefab, hasBlueprintKey: false);
            return false;
        }

        AssetGuid icon;
        try { icon = data.Icon; }
        catch { return false; }
        if ((icon._a | icon._b | icon._c | icon._d) == 0)
        {
            NoIconKey.Add(prefab);
            UiBuildingIcons.NoteKey(prefab, hasBlueprintKey: false);
            return false;
        }

        IconKeys[prefab] = icon;
        UiBuildingIcons.NoteKey(prefab, hasBlueprintKey: true);
        key = icon;
        return true;
    }

    /// <summary>Look each waiting blueprint's icon id up in the loaded asset sections; a sprite found is its icon.</summary>
    private static void ResolveFromLoadedAssets(World world, List<(int Prefab, AssetGuid Key)> keyed)
    {
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
                int left = keyed.Count;
                var found = new bool[keyed.Count];
                foreach (var e in entities)
                {
                    if (left == 0) break;
                    Il2CppSystem.Object? boxed;
                    try { boxed = em.GetComponentObject<Il2CppSystem.Object>(e, type); }
                    catch { continue; }
                    var lookup = boxed?.TryCast<ManagedAssetReferences>()?.AssetLookup;
                    if (lookup == null) continue;
                    for (int i = 0; i < keyed.Count; i++)
                    {
                        if (found[i]) continue;
                        try
                        {
                            if (!lookup.ContainsKey(keyed[i].Key)) continue;
                            var sprite = lookup[keyed[i].Key]?.TryCast<Sprite>();
                            if (sprite == null || !UnityUiRuntime.Alive(sprite)) continue;
                            UiBuildingIcons.Note(keyed[i].Prefab, sprite, BuildingIconSource.Blueprint);
                            found[i] = true;
                            left--;
                        }
                        catch { /* one unreadable entry does not stop the scan */ }
                    }
                }
            }
            finally { entities.Dispose(); }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: building icons: loaded-asset scan threw: " + e.Message);
        }
        finally { query.Dispose(); }
    }

    /// <summary>
    /// Read every build-menu cell the game has drawn (found with the non-generic <c>Resources.FindObjectsOfTypeAll</c>):
    /// its building, loading flag and sprite, kept only for a blueprint without an icon id of its own.
    /// </summary>
    private static void HarvestBuildMenuCells(ManagedDataRegistry? registry)
    {
        var type = Il2CppType.Of<BuildMenu_StructureEntry>();
        if (type == null) return;
        var all = Resources.FindObjectsOfTypeAll(type);
        if (all == null) return;
        for (int i = 0; i < all.Length; i++)
        {
            try
            {
                var entry = all[i] == null ? null : all[i].TryCast<BuildMenu_StructureEntry>();
                if (entry == null || !UnityUiRuntime.Alive(entry)) continue;
                int guid = entry.PrefabGuid.GuidHash;
                if (guid == 0) continue;
                if (!UiBuildingIcons.KeyKnown(guid)) TryKey(registry, guid, out _);
                if (!UiBuildingIcons.AcceptsCell(guid, entry.IsLoading)) continue;
                var image = entry.Icon != null ? entry.Icon.CurrentImage : null;
                var sprite = image != null ? image.sprite : null;
                if (sprite != null && UnityUiRuntime.Alive(sprite)) UiBuildingIcons.Note(guid, sprite, BuildingIconSource.BuildMenu);
            }
            catch { /* one unreadable cell does not stop the scan */ }
        }
    }
}
#endif
