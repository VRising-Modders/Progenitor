#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using Progenitor.Ecs;
using ProjectM;
using Stunlock.Core;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace Progenitor.Ui;

/// <summary>
/// HUB1: the game side of <see cref="UiBuildingIcons"/>. Read only: it loads no asset and requests no streaming.
///
/// <para>A blueprint's icon asset id is <c>ManagedBlueprintData.Icon</c> (an <c>AssetGuid</c>;
/// <c>ManagedDataRegistry.TryGet</c> over that reference type, as the build-menu search reads its names). The build
/// menu resolves a blueprint's sprite through its UI asset loader, which finds the loaded section holding the asset and
/// reads the section's <c>ManagedAssetReferences.AssetLookup</c> (<c>Dictionary&lt;AssetGuid, Object&gt;</c>, an
/// instantiation the game uses). This reads the same dictionaries on the loaded sections, the way the model loader
/// reads them, with <c>ContainsKey</c> and the indexer only; no generic over a game value type is added.</para>
///
/// <para>The build menu's cells are not read: a cell's icon setter ignores a null sprite, so a cell whose sprite has not
/// arrived keeps the one it showed before while it already names the new building.</para>
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

    /// <summary>
    /// One bounded scan on <paramref name="frame"/>: look the waiting buildings' blueprint icon ids up in the loaded asset
    /// sections, resuming at <see cref="UiBuildingIcons.ScanCursor"/>. Returns whether it went over every section.
    /// </summary>
    internal static bool Scan(World world, int frame)
    {
        ManagedDataRegistry? registry = null;
        try { registry = world.GetExistingSystemManaged<GameDataSystem>()?.ManagedDataRegistry; }
        catch { registry = null; }

        var waiting = UiBuildingIcons.WaitingNow(frame);
        var keyed = new List<(int Prefab, AssetGuid Key)>(waiting.Length);
        foreach (int prefab in waiting)
            if (TryKey(registry, prefab, out var key)) keyed.Add((prefab, key));
        return keyed.Count == 0 || ResolveFromLoadedAssets(world, keyed);
    }

    /// <summary>
    /// <paramref name="prefab"/>'s blueprint icon id. A blueprint whose icon id is empty has none (remembered); a building
    /// without <c>ManagedBlueprintData</c> is asked again on a later scan (game data may come up after the first one).
    /// </summary>
    private static bool TryKey(ManagedDataRegistry? registry, int prefab, out AssetGuid key)
    {
        if (IconKeys.TryGetValue(prefab, out key)) return true;
        key = default;
        if (NoIconKey.Contains(prefab) || registry == null) return false;
        ManagedBlueprintData? data;
        try
        {
            if (!registry.TryGet<ManagedBlueprintData>(new PrefabGUID(prefab), out data)) data = null;
        }
        catch { data = null; }

        if (data == null) return false;
        AssetGuid icon;
        try { icon = data.Icon; }
        catch { return false; }
        if ((icon._a | icon._b | icon._c | icon._d) == 0)
        {
            NoIconKey.Add(prefab);
            return false;
        }

        IconKeys[prefab] = icon;
        key = icon;
        return true;
    }

    /// <summary>
    /// Look each waiting blueprint's icon id up in the loaded asset sections, at most
    /// <see cref="UiBuildingIcons.MaxLookupsPerScan"/> lookups, from the cursor on. Returns whether the pass reached the
    /// end of the list (or found every icon).
    /// </summary>
    private static bool ResolveFromLoadedAssets(World world, List<(int Prefab, AssetGuid Key)> keyed)
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
                int count = entities.Length;
                int at = UiBuildingIcons.ScanCursor;
                if (at < 0 || at >= count) at = 0;
                int budget = UiBuildingIcons.MaxLookupsPerScan;
                int left = keyed.Count;
                var found = new bool[keyed.Count];
                while (at < count && left > 0 && budget > 0)
                {
                    var e = entities[at++];
                    budget -= 2;
                    Il2CppSystem.Object? boxed;
                    try { boxed = em.GetComponentObject<Il2CppSystem.Object>(e, type); }
                    catch { continue; }
                    var lookup = boxed?.TryCast<ManagedAssetReferences>()?.AssetLookup;
                    if (lookup == null) continue;
                    for (int i = 0; i < keyed.Count; i++)
                    {
                        if (found[i]) continue;
                        budget--;
                        try
                        {
                            if (!lookup.ContainsKey(keyed[i].Key)) continue;
                            budget -= 2;
                            var sprite = lookup[keyed[i].Key]?.TryCast<Sprite>();
                            if (sprite == null || !UnityUiRuntime.Alive(sprite)) continue;
                            UiBuildingIcons.Note(keyed[i].Prefab, sprite);
                            found[i] = true;
                            left--;
                        }
                        catch { /* one unreadable entry does not stop the scan */ }
                    }
                }

                bool fullPass = at >= count || left == 0;
                UiBuildingIcons.ScanCursor = at >= count ? 0 : at;
                return fullPass;
            }
            finally { entities.Dispose(); }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: building icons: loaded-asset scan threw: " + e.Message);
            UiBuildingIcons.ScanCursor = 0;
            return true;
        }
        finally { query.Dispose(); }
    }
}
#endif
