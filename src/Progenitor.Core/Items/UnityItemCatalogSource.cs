#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using ProjectM;
using ProjectM.Network;
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
/// <item><description>Filter: <c>GameDataSystem.ItemHashLookupMap.TryGetValue</c>, the table
/// <c>ctx.Inventory</c> reads stack sizes from. A prefab missing from it is not listed, except a
/// custom item applied in this process.</description></item>
/// <item><description>Facts: the prefab's own <c>ItemData</c> (<c>GetComponentData&lt;ItemData&gt;</c>);
/// the table row only when that read fails (<see cref="ItemCatalogFacts.TryPick"/>).</description></item>
/// <item><description>Prefab name: <c>PrefabCollectionSystem._PrefabLookupMap.TryGetName</c>, the call
/// <see cref="PrefabLookup.TryGetName"/> makes, with the system resolved once per build and no per-item log.</description></item>
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

    public int ContentVersion => ItemCatalogVersion.Current;

    public int Frame
    {
        get
        {
            if (Plugin.TryDispatchFrame(out int dispatched)) return dispatched;
            try { return UnityEngine.Time.frameCount; }
            catch { return 0; }
        }
    }

    /// <summary>
    /// The server is ready once it has a world. A client is ready once its local character exists:
    /// before that the join may still be streaming prefabs in, so a build is served but not cached.
    /// </summary>
    public bool Ready
    {
        get
        {
            if (Plugin.IsServerProcess) return true;
            if (World is not World world) return false;
            try
            {
                return Query.Entities(world.EntityManager, includeDisabled: false, includeSystems: false,
                    ComponentIndex.Of<LocalCharacter>(ComponentType.AccessMode.ReadOnly)).Count > 0;
            }
            catch
            {
                return false;
            }
        }
    }

    public void Warn(string message) => Plugin.Logger?.LogWarning(message);

    public void Collect(List<ItemCatalogRow> into)
    {
        if (World is not World world) return;
        var em = world.EntityManager;
        // Systems and the managed registry are resolved once per build, not per item.
        var gameData = world.GetExistingSystemManaged<GameDataSystem>();
        if (gameData == null) return;
        var prefabs = world.GetExistingSystemManaged<PrefabCollectionSystem>();
        bool client = !Plugin.IsServerProcess;
        ManagedDataRegistry? registry = null;
        if (client)
        {
            try { registry = gameData.ManagedDataRegistry; }
            catch { registry = null; }
        }
        int nameMisses = 0, entityMisses = 0, rowFailures = 0;

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

                custom.TryGetValue(guid.GuidHash, out var def);
                bool isCustom = def != null && PrefabApplicator.WasClonedHere(guid);

                bool inTable = gameData.ItemHashLookupMap.TryGetValue(guid, out var row);
                var tableFacts = inTable ? new ItemCatalogFacts(row.MaxAmount, (long)row.ItemCategory, (int)row.ItemType) : default;

                // The prefab's own ItemData (the typed read Core's edit and status paths use): the
                // table row can lag behind an ordered edit pass on the client.
                bool entityRead = false;
                ItemCatalogFacts entityFacts = default;
                try
                {
                    var data = em.GetComponentData<ItemData>(e);
                    entityFacts = new ItemCatalogFacts(data.MaxAmount, (long)data.ItemCategory, (int)data.ItemType);
                    entityRead = true;
                }
                catch { entityMisses++; }

                if (!ItemCatalogFacts.TryPick(inTable, tableFacts, entityRead, entityFacts, isCustom, out var facts)) continue;

                string? prefabName = null;
                if (prefabs != null)
                {
                    try
                    {
                        if (prefabs._PrefabLookupMap.TryGetName(guid, out var n) && !string.IsNullOrEmpty(n)) prefabName = n;
                    }
                    catch { /* counted below; no per-item error log */ }
                }
                if (prefabName == null)
                {
                    nameMisses++;
                    prefabName = def?.Id;
                }

                string? display = null;
                if (client && ChatGameData.TryLocalizedItemName(registry, guid, out var localized))
                    display = localized;
                if (display == null && isCustom && !string.IsNullOrWhiteSpace(def!.LocalizedName))
                    display = def.LocalizedName;

                into.Add(new ItemCatalogRow(guid.GuidHash, prefabName, display, facts.MaxAmount,
                    facts.ItemCategory, facts.ItemType, isCustom));
            }
            catch
            {
                rowFailures++;
            }
        }

        if (nameMisses + entityMisses + rowFailures > 0)
            Plugin.Logger?.LogDiag("Items: catalog build: " + into.Count + " rows; " + nameMisses + " without a prefab name, "
                + entityMisses + " ItemData reads failed, " + rowFailures + " rows skipped.");
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
