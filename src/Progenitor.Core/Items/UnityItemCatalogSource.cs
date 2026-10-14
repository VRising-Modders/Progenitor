#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using ProjectM;
using Progenitor.Chat;
using Progenitor.Content;
using Progenitor.Ecs;
using Stunlock.Core;
using Unity.Entities;
using PrefabTag = Unity.Entities.Prefab;

namespace Progenitor.Items;

/// <summary>
/// The game half of the item catalog. Reads the current side's world (the server world in a
/// server process, the client world otherwise) through paths Core already uses:
///
/// <list type="bullet">
/// <item><description>Enumeration: a prefab-inclusive query over <c>ItemData</c> + <c>PrefabGUID</c>
/// + <c>Prefab</c> through <see cref="Query.EntitiesWithOptions"/> (the non-generic builder path; the
/// <c>Prefab</c> requirement keeps live item instances in inventories out).</description></item>
/// <item><description>Facts: <c>GameDataSystem.ItemHashLookupMap.TryGetValue</c>, the table
/// <c>ctx.Inventory</c> reads stack sizes from. A prefab missing from it is not listed.</description></item>
/// <item><description>Prefab name: <see cref="PrefabLookup.TryGetName"/>.</description></item>
/// <item><description>Display name (client): <c>ChatGameData.TryLocalizedItemName</c>, the resolver
/// chat item links use. Custom items fall back to their definition's <c>Named(...)</c> text on both sides.</description></item>
/// </list>
/// Main thread.
/// </summary>
internal sealed class UnityItemCatalogSource : IItemCatalogSource
{
    internal static readonly ItemCatalogCache Cache = new(new UnityItemCatalogSource());

    public object? World
    {
        get
        {
            var world = Plugin.IsServerProcess ? Worlds.Server : Worlds.Client;
            return world != null && world.IsCreated ? world : null;
        }
    }

    public string Side => Plugin.IsServerProcess ? "server" : "client";

    public int Epoch => Progenitor.WorldState.WorldEpoch.Current;

    public int ContentVersion => PrefabApplicator.ContentVersion;

    public void Collect(List<ItemCatalogRow> into)
    {
        if (World is not World world) return;
        var em = world.EntityManager;
        var gameData = world.GetExistingSystemManaged<GameDataSystem>();
        if (gameData == null) return;
        bool client = !Plugin.IsServerProcess;

        var custom = new Dictionary<int, PrefabDefinition>();
        foreach (var def in PrefabRegistry.Definitions)
        {
            if (def.Kind == PrefabKind.Item && def.Guid.GuidHash != 0)
                custom[def.Guid.GuidHash] = def;
        }

        var guidType = ComponentIndex.Of<PrefabGUID>(ComponentType.AccessMode.ReadOnly);
        var entities = Query.EntitiesWithOptions(
            em,
            EntityQueryOptions.IncludePrefab | EntityQueryOptions.IncludeDisabledEntities,
            ComponentIndex.Of<ItemData>(ComponentType.AccessMode.ReadOnly),
            guidType,
            ComponentIndex.Of<PrefabTag>(ComponentType.AccessMode.ReadOnly));

        var seen = new HashSet<int>();
        foreach (var e in entities)
        {
            try
            {
                if (!e.TryRead(em, out PrefabGUID guid) || guid.GuidHash == 0 || !seen.Add(guid.GuidHash)) continue;
                if (!gameData.ItemHashLookupMap.TryGetValue(guid, out var data)) continue;

                custom.TryGetValue(guid.GuidHash, out var def);
                bool isCustom = def != null && PrefabApplicator.WasClonedHere(guid);
                string? prefabName = PrefabLookup.TryGetName(world, guid, out var n) ? n : def?.Id;
                string? display = null;
                if (client && ChatGameData.TryLocalizedItemName(world, guid, out var localized))
                    display = localized;
                if (display == null && isCustom && !string.IsNullOrWhiteSpace(def!.LocalizedName))
                    display = def.LocalizedName;

                into.Add(new ItemCatalogRow(guid.GuidHash, prefabName, display, data.MaxAmount,
                    (long)data.ItemCategory, (int)data.ItemType, isCustom));
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogDiag("Items: catalog row " + e.Index + ":" + e.Version + " skipped: " + ex.Message);
            }
        }
    }
}

/// <summary>
/// The Dev bridge's <c>item_catalog</c> op. Public only because Core grants <c>Progenitor.Dev</c> no
/// <c>InternalsVisibleTo</c>; not a plugin API (plugins use <c>ctx.Items.Catalog()</c>). Main thread.
/// </summary>
public static class ItemCatalogBridge
{
    /// <summary>The catalog of this process's side, built when needed.</summary>
    public static IReadOnlyList<ItemCatalogEntry> Entries() => UnityItemCatalogSource.Cache.Entries();

    /// <summary>Summary of the last build.</summary>
    public static ItemCatalogInfo Info() => UnityItemCatalogSource.Cache.Info;

    /// <summary>Drop the cache so the next read rebuilds (to time a build).</summary>
    public static void Invalidate() => UnityItemCatalogSource.Cache.Invalidate();

    /// <summary>Entries whose prefab or display name contains <paramref name="filter"/>, or whose GUID equals it.</summary>
    public static List<ItemCatalogEntry> Filter(IReadOnlyList<ItemCatalogEntry> entries, string? filter) =>
        ItemCatalogCache.Filter(entries, filter);
}
#endif
