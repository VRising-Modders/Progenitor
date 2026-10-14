using System;
using System.Collections.Generic;
using System.Text;
using ProjectM;
using ProjectM.Network;
using Stunlock.Core;
using Unity.Collections;
using Unity.Entities;
using PrefabTag = Unity.Entities.Prefab;

namespace Progenitor.Content;

/// <summary>
/// Writes a cloned item or buff into every lookup the game consults. Same steps on the server
/// world and the client world; the headless server has an empty managed-data registry and skips
/// that item step. Buff clones skip <c>ItemKeyLookup</c> / <c>ItemHashLookupMap</c> /
/// <c>ManagedItemData</c> and keep the source's <c>ShowOnBuffBar</c> / client
/// <c>ShowOnBuffBarData</c> (HUD icon).
///
/// Order: append the ItemKeyLookup row first (inventory slots travel as an index into it). If
/// that append fails, no prefab map is touched and the report contains "item key table full".
/// Then clone + Prefab tag + PrefabGUID + patched ItemData, then rewrite the
/// <c>NetSnapshot</c> blob PrefabGUID (spawn packets read that, not the ECS component),
/// then the collection's three maps and spawnable-name dictionary, then ItemHashLookupMap,
/// then client managed data via runtime_invoke. Buff clones on the client then load
/// <see cref="PrefabDefinition.IconPath"/> onto <c>ShowOnBuffBarData.Entries[].Icon</c>
/// and write <c>Named</c>/<c>Described</c> onto <c>Entries[].Header</c> /
/// <c>Description.Key</c> (server skips; the path is not a wire field). A later-step failure rolls back everything
/// this call inserted (maps, the clone entity, a key row that was new this call). Soft
/// unapply is separate: it never removes maps or destroys entities, so held inventories
/// keep resolving.
///
/// A GUID already in <c>_PrefabGuidToEntityMap</c> is updated in place only when this process
/// cloned it (this generation, or an earlier one whose GUID was snapshot-carried — including
/// a clone retired with the entity kept) <em>and</em> the new definition has the same
/// <see cref="PrefabKind"/> and resolved <c>CloneOf</c>. A kind change (Item↔Buff) is
/// refused. A source change with no live <c>PrefabGUID</c> instances destroys the old clone
/// and <c>InsertNew</c>s from the new source (GUID kept, key row not compacted). Live
/// instances refuse that definition until restart. A spawnable name that already maps to a
/// GUID this process did not clone is refused even when that GUID equals the definition's
/// own hash (vanilla name reuse). On <c>!ok &amp;&amp; keyWasNew</c> the just-appended
/// ItemKeyLookup row is rolled back so join-time count+hash cannot see a hole.
/// </summary>
internal static class PrefabApplicator
{
    /// <summary>
    /// Clone / key-row bookkeeping for the current world. World-scoped: a Core swap carries it
    /// through Bootstrap ContentMemory, a new client world drops it (<see cref="ForgetClientWorld"/>).
    /// </summary>
    private static readonly PrefabCloneLedger Ledger = new();

    private static readonly object LiveQueryGate = new();
    // World-bound: the client world these were built in is destroyed on every leave, and
    // disposing a query after that is a native crash (live 2026-10-02 Core swap at the title).
    private static readonly Ecs.WorldBoundQuery BuffLiveQuery = new("PrefabApplicator buff live-query");
    private static readonly Ecs.WorldBoundQuery ItemLiveQuery = new("PrefabApplicator item live-query");

    public static bool TryApply(World world, PrefabDefinition def, out string report)
    {
        var sb = new StringBuilder();
        try
        {
            if (world == null || !world.IsCreated)
            {
                report = "world missing";
                return false;
            }

            var em = world.EntityManager;
            var prefabs = world.GetExistingSystemManaged<PrefabCollectionSystem>();
            var gameData = world.GetExistingSystemManaged<GameDataSystem>();
            if (prefabs == null || gameData == null)
            {
                report = "PrefabCollectionSystem or GameDataSystem missing in world " + world.Name;
                return false;
            }

            if (!TryResolveSource(prefabs, def, out var source, out var sourceWhy))
            {
                report = sourceWhy;
                return false;
            }
            def.Source = source;
            var target = def.Guid;

            bool haveExisting = prefabs._PrefabGuidToEntityMap.TryGetValue(target, out var existing)
                                && em.Exists(existing);
            if (PrefabApplyBookkeeping.MustRefuseExistingGuid(haveExisting, WasClonedHere(target)))
            {
                report = $"guid {target.GuidHash} already in _PrefabGuidToEntityMap and was not cloned by this process";
                return false;
            }

            if (PrefabApplyBookkeeping.ShouldAppendItemKey(def.Kind)
                && TryGetAppendedIndex(target.GuidHash, out int savedIndex)
                && savedIndex >= 0)
            {
                int now = ItemKeyTable.IndexOf(em, target);
                if (!KeyRowStillHolds(savedIndex, now))
                {
                    report = $"key index mismatch guid={target.GuidHash} saved={savedIndex} now={now}";
                    return false;
                }
            }

            if (!TryClaimSpawnableName(prefabs, def, target, out var spawnWhy))
            {
                report = spawnWhy;
                return false;
            }

            bool retarget = false;
            int wasSourceHash = 0;
            if (haveExisting)
            {
                if (!TryDecideSameId(
                    world, em, prefabs, def, source, target, existing,
                    out var sameIdAction, out wasSourceHash, out var sameIdWhy))
                {
                    report = sameIdWhy;
                    return false;
                }
                if (sameIdAction == PrefabApplyBookkeeping.SameIdApplyAction.RefuseKindChange
                    || sameIdAction == PrefabApplyBookkeeping.SameIdApplyAction.RefuseSourceChange
                    || sameIdAction == PrefabApplyBookkeeping.SameIdApplyAction.RefuseExistingMismatch)
                {
                    report = sameIdWhy;
                    return false;
                }
                retarget = sameIdAction == PrefabApplyBookkeeping.SameIdApplyAction.RetargetClone;
            }

            bool keyWasNew = false;
            int keyIndex = -1;
            if (PrefabApplyBookkeeping.ShouldAppendItemKey(def.Kind))
            {
                // ItemKeyLookup first: a GUID in ItemHashLookupMap without a key-table index serializes
                // as a wrong inventory slot. Already-present (tombstone / prior apply) is success.
                bool addedKey = ItemKeyTable.TryAppend(em, target, out keyIndex, out var keyWhy);
                if (!addedKey)
                {
                    report = KeyTableFailReport(keyWhy);
                    return false;
                }
                keyWasNew = !IsAlreadyPresent(keyWhy);
                sb.Append($"itemKey={addedKey} index={keyIndex} ({keyWhy}); ");
            }
            else
            {
                sb.Append("itemKey=skipped (buff); ");
            }

            bool ok;
            if (retarget)
            {
                // Instantiate the new clone first; unmap/destroy the old one only after
                // InsertNew succeeds so a failed re-clone cannot leave the GUID unmapped.
                sb.Append(PrefabApplyBookkeeping.FormatRetargeted(wasSourceHash, source.GuidHash));
                sb.Append("; ");
                ok = InsertNew(
                    world, em, prefabs, gameData, def, source, target, keyWasNew, sb,
                    replaceManaged: true, replaceExisting: existing, keepOwnership: true, out report);
            }
            else if (haveExisting)
                ok = UpdateExisting(world, em, gameData, def, source, target, existing, sb, out report);
            else
                ok = InsertNew(
                    world, em, prefabs, gameData, def, source, target, keyWasNew, sb,
                    replaceManaged: false, replaceExisting: Entity.Null, keepOwnership: false, out report);

            if (!ok && PrefabApplyBookkeeping.ShouldRollbackKey(ok, keyWasNew))
                RollbackKey(em, def, target);
            if (ok)
            {
                RememberAppliedIdentity(target, source, def.Kind);
                if (PrefabApplyBookkeeping.ShouldAppendItemKey(def.Kind))
                    RememberAppend(target, keyIndex);
            }
            return ok;
        }
        catch (Exception ex)
        {
            report = sb + " EXCEPTION " + ex;
            return false;
        }
    }

    /// <summary>
    /// Soft unapply. Inventories hold this GUID as <c>ItemType</c>; game systems then look it up
    /// in <c>_PrefabGuidToEntityMap</c>, <c>_PrefabDataLookup</c>, <c>_ConversionStateLookup</c>,
    /// <c>_SpawnableNameToPrefabGuidDictionary</c>, and <c>ItemHashLookupMap</c> (tooltips,
    /// inventory validation, save). Removing those entries or destroying the clone makes the
    /// GUID unresolvable and can throw or crash. ItemKeyLookup stays too: slots travel as an
    /// index into that table, which never shrinks (a middle compact remaps every later slot and
    /// desyncs join-time count+hash). This method only runs the definition's undo list
    /// (icon lease and any other recorded side-effect; localization keys stay with the
    /// owner registration) and leaves the prefab retired-but-resolvable. A later
    /// <see cref="TryApply"/> of the same id updates the surviving clone in place.
    /// <paramref name="entityKept"/> is true when the clone still resolves; the caller
    /// records that GUID as ours so a Core-swap snapshot can re-claim it (buffs never
    /// tombstone, so this is the only ownership hand-off they have).
    /// </summary>
    public static bool TryUnapply(World? world, PrefabDefinition def, out string report, out bool entityKept)
    {
        entityKept = false;
        var sb = new StringBuilder();
        try
        {
            def.RunUndo();
            sb.Append("undo ran; ");

            if (world == null || !world.IsCreated)
            {
                report = sb + "world missing (entity kept if any)";
                return false;
            }

            var em = world.EntityManager;
            var prefabs = world.GetExistingSystemManaged<PrefabCollectionSystem>();
            var target = def.Guid;

            Entity clone = Entity.Null;
            bool haveClone = prefabs != null
                && prefabs._PrefabGuidToEntityMap.TryGetValue(target, out clone)
                && clone != Entity.Null
                && em.Exists(clone);

            // Do not Remove from any GUID map and do not DestroyEntity. The clone stays so
            // held stacks still resolve. Registry bookkeeping (Entries / tombstone / PrfU)
            // is the caller's job. Clone ownership stays too: a re-register of this id
            // (same Core generation or the next, via the snapshot) must update in place.
            entityKept = haveClone;
            if (haveClone)
                sb.Append($"retired (entity kept) {clone.Index}:{clone.Version}; ");
            else
                sb.Append("retired (no clone entity); ");

            sb.Append("maps=kept; itemMap=kept; managed=kept; ");
            sb.Append("itemKey=kept (table never shrinks; inventory indices; do not compact)");

            report = sb.ToString();
            return true;
        }
        catch (Exception ex)
        {
            report = sb + " EXCEPTION " + ex;
            return false;
        }
    }

    public static string Describe(World world, PrefabGUID guid)
    {
        var sb = new StringBuilder();
        sb.Append($"guid {guid.GuidHash} in world {(world == null ? "<null>" : world.Name)}: ");
        if (world == null || !world.IsCreated)
        {
            sb.Append("world missing");
            return sb.ToString();
        }

        try
        {
            var em = world.EntityManager;
            var prefabs = world.GetExistingSystemManaged<PrefabCollectionSystem>();
            var gameData = world.GetExistingSystemManaged<GameDataSystem>();
            Entity entity = default;
            bool inEntity = false;

            if (prefabs == null)
            {
                sb.Append("PrefabCollectionSystem missing; ");
            }
            else
            {
                inEntity = prefabs._PrefabGuidToEntityMap.TryGetValue(guid, out entity);
                sb.Append(inEntity
                    ? $"entityMap={entity.Index}:{entity.Version}; "
                    : "entityMap=miss; ");

                bool inAsset = prefabs._PrefabDataLookup.TryGetValue(guid, out var asset);
                sb.Append(inAsset ? $"asset='{asset.AssetName}'; " : "asset=miss; ");

                bool inState = prefabs._ConversionStateLookup.TryGetValue(guid, out var state);
                sb.Append(inState ? $"state={state}; " : "state=miss; ");

                string name = prefabs._PrefabLookupMap.TryGetName(guid, out var lookedUp) ? lookedUp : "";
                sb.Append(string.IsNullOrEmpty(name) ? "lookupName=miss; " : $"lookupName='{name}'; ");

                bool inSpawn = false;
                PrefabGUID named = default;
                var dict = prefabs._SpawnableNameToPrefabGuidDictionary;
                if (dict != null && !string.IsNullOrEmpty(name))
                    inSpawn = dict.TryGetValue(name, out named);
                sb.Append(inSpawn ? $"spawnable='{name}'->{named.GuidHash}; " : "spawnable=miss; ");
            }

            // Prefer the entity's ItemData (GetComponentData<ItemData> is known-good on both
            // sides). ItemHashLookupMap.TryGetValue can miss or return the clone-time MaxAmount
            // on the client after an ordered edit pass, which made client `status` hide the
            // live stack size the server line already printed as `itemMap max=N`.
            if (inEntity)
            {
                try
                {
                    var buffType = Ecs.ComponentIndex.Of<Buff>(ComponentType.AccessMode.ReadOnly);
                    if (em.HasComponent(entity, buffType))
                        sb.Append("kind=buff; ");
                }
                catch { }
            }

            string? itemMax = TryReadEntityItemMax(em, inEntity, entity);
            if (gameData == null)
            {
                if (itemMax != null) sb.Append(itemMax);
                sb.Append("GameDataSystem missing; ");
            }
            else
            {
                if (itemMax == null)
                {
                    bool inItems = gameData.ItemHashLookupMap.TryGetValue(guid, out var item);
                    itemMax = inItems ? $"itemMap max={item.MaxAmount}; " : "itemMap=miss; ";
                }
                sb.Append(itemMax);

                try
                {
                    var registry = gameData.ManagedDataRegistry;
                    var map = registry._DataLookupMap;
                    if (map == null || map.Count == 0)
                    {
                        sb.Append("managed=empty (server); ");
                    }
                    else
                    {
                        bool got = registry.TryGet<ManagedItemData>(guid, out var managed) && managed != null;
                        sb.Append(got ? "managed=present; " : "managed=miss; ");
                    }
                }
                catch (Exception e)
                {
                    sb.Append($"managed probe threw {e.GetType().Name}; ");
                }
            }

            sb.Append(ItemKeyTable.Describe(em, guid));
        }
        catch (Exception e)
        {
            sb.Append("EXCEPTION " + e);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Live <c>ItemData.MaxAmount</c> from the prefab entity. Status uses this so the client
    /// line matches the server (<c>itemMap max=N</c>) after both sides ran the same editors.
    /// </summary>
    private static string? TryReadEntityItemMax(EntityManager em, bool inEntity, Entity entity)
    {
        if (!inEntity) return null;
        try
        {
            var itemType = Ecs.ComponentIndex.Of<ItemData>(ComponentType.AccessMode.ReadOnly);
            if (!em.HasComponent(entity, itemType)) return null;
            var item = em.GetComponentData<ItemData>(entity);
            return $"itemMap max={item.MaxAmount}; ";
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The clone is still in the game maps (soft unapply never removes it) and this process
    /// cloned it. Re-patch <c>ItemData.MaxAmount</c> on the entity and in <c>ItemHashLookupMap</c>,
    /// then re-run managed data / localized name and description so a hot-reloaded content plugin
    /// sees its new values. The key row was already appended (or confirmed present) by the caller.
    /// </summary>
    private static bool UpdateExisting(
        World world,
        EntityManager em,
        GameDataSystem gameData,
        PrefabDefinition def,
        PrefabGUID source,
        PrefabGUID target,
        Entity existing,
        StringBuilder sb,
        out string report)
    {
        sb.Append($"updated existing clone {existing.Index}:{existing.Version}; ");
        var existingItemType = Ecs.ComponentIndex.Of<ItemData>(ComponentType.AccessMode.ReadOnly);
        var existingBuffType = Ecs.ComponentIndex.Of<Buff>(ComponentType.AccessMode.ReadOnly);
        bool existingHasItem = em.HasComponent(existing, existingItemType);
        bool existingHasBuff = em.HasComponent(existing, existingBuffType);
        if (!PrefabApplyBookkeeping.SourceMatchesKind(def.Kind, existingHasItem, existingHasBuff))
        {
            report = sb + PrefabApplyBookkeeping.SourceKindRejectedMessage(def.Kind);
            return false;
        }
        // In-place update still owns the GUID: a Core-swap snapshot after plugin unload
        // reads the clone ledger, and buffs never get a tombstone to union in.
        RememberClone(target);
        AppendNetIdentity(em, existing, target, sb);
        if (def.Kind == PrefabKind.Buff)
        {
            sb.Append("kind=buff");
            RegisterLocalizedFields(def, BuffBarText.NameKey(def.Id), PrefabWire.DescriptionKey(def.Id));
            BuffIconApply.TryApply(em, existing, def);
            BuffBarTextApply.TryApply(em, existing, def);
            report = sb.ToString();
            return true;
        }

        ItemData oldData = default;
        bool patchedEntity = false;
        bool removedItem = false;
        bool hadPrevious = false;
        ItemData previousMap = default;
        try
        {
            var itemType = Ecs.ComponentIndex.Of<ItemData>(ComponentType.AccessMode.ReadOnly);
            if (!em.HasComponent(existing, itemType))
            {
                report = sb + "clone has no ItemData";
                return false;
            }

            oldData = em.GetComponentData<ItemData>(existing);
            var itemData = oldData;
            itemData.ItemTypeGUID = target;
            // TryAddInventoryItem Instantiates ItemData.Entity from ItemHashLookupMap.
            // Instantiation of the donor leaves live items with the donor PrefabGUID.
            itemData.Entity = existing;
            if (def.MaxAmount > 0) itemData.MaxAmount = def.MaxAmount;
            else def.MaxAmount = itemData.MaxAmount;
            // Keep rules: the death drop reads ItemCategory from the lookup row, so the bits
            // go onto the entity and its row together. An option that is off takes the
            // donor's bit, so dropping it from a re-registered definition reverts the clone.
            Entity donorEntity = Entity.Null;
            if (gameData.ItemHashLookupMap.TryGetValue(source, out var donorData))
            {
                donorEntity = donorData.Entity;
                ItemKeepPolicy.ApplyCategory(ref itemData, def.Keep, in donorData);
            }
            else if (def.Keep != null)
            {
                var current = itemData;
                ItemKeepPolicy.ApplyCategory(ref itemData, def.Keep, in current);
            }
            em.SetComponentData(existing, itemData);
            patchedEntity = true;
            sb.Append($"ItemData MaxAmount={itemData.MaxAmount}; ");

            hadPrevious = gameData.ItemHashLookupMap.TryGetValue(target, out previousMap);
            // TryAdd is false when the key already exists (retired-but-resolvable / retry).
            gameData.ItemHashLookupMap.Remove(target);
            removedItem = true;
            bool addedItem = gameData.ItemHashLookupMap.TryAdd(target, itemData);
            Progenitor.Items.ItemCatalogVersion.Bump();
            sb.Append($"itemMap={addedItem}; ");

            bool hadUndo = def.HasRecordedUndo;
            bool okManaged = EnsureManagedData(gameData, def, source, target, replaceExisting: true, out var whyManaged);
            sb.Append($"managed={okManaged} ({whyManaged})");

            if (addedItem && okManaged)
            {
                ApplyArmorBaseline(world, em, def, target, existing);
                if (def.StatBonuses != null) ItemStatBonusesApply.Apply(em, existing, donorEntity, def.StatBonuses);
                ApplyKeepToClone(world, em, def, existing, donorEntity, sb);
                report = sb.ToString();
                return true;
            }

            RestoreExisting(em, gameData, existing, target, oldData, patchedEntity, removedItem, hadPrevious, previousMap);
            if (!hadUndo && def.HasRecordedUndo)
                def.RunUndo();
            report = sb + "; rolled back this call";
            return false;
        }
        catch (Exception ex)
        {
            RestoreExisting(em, gameData, existing, target, oldData, patchedEntity, removedItem, hadPrevious, previousMap);
            report = sb + " EXCEPTION " + ex;
            return false;
        }
    }

    /// <summary>
    /// The three <c>PrefabCollectionSystem</c> rows that make a runtime clone resolvable through
    /// <c>PrefabLookupMap</c> (including <c>TryGetConvertedPrefab</c>, which checks the conversion
    /// state): entity map, asset data (the source's, renamed), conversion state
    /// <c>Converted</c>. Used by item/buff clones and by ctx.Ai cast-option clones.
    /// </summary>
    internal static void AddCloneLookups(
        PrefabCollectionSystem prefabs,
        PrefabGUID source,
        PrefabGUID target,
        Entity clone,
        string assetName,
        out bool addedEntity,
        out bool addedAsset,
        out bool addedState)
    {
        addedAsset = false;
        addedEntity = prefabs._PrefabGuidToEntityMap.TryAdd(target, clone);
        if (prefabs._PrefabDataLookup.TryGetValue(source, out var assetData))
        {
            assetData.AssetName = new FixedString128Bytes(assetName);
            addedAsset = prefabs._PrefabDataLookup.TryAdd(target, assetData);
        }
        addedState = prefabs._ConversionStateLookup.TryAdd(target, AssetConversionState.Converted);
    }

    /// <summary>Undo <see cref="AddCloneLookups"/> for a clone that failed before it was used.</summary>
    internal static void RemoveCloneLookups(PrefabCollectionSystem prefabs, PrefabGUID target)
    {
        try { prefabs._PrefabGuidToEntityMap.Remove(target); } catch { }
        try { prefabs._PrefabDataLookup.Remove(target); } catch { }
        try { prefabs._ConversionStateLookup.Remove(target); } catch { }
    }

    private static bool InsertNew(
        World world,
        EntityManager em,
        PrefabCollectionSystem prefabs,
        GameDataSystem gameData,
        PrefabDefinition def,
        PrefabGUID source,
        PrefabGUID target,
        bool keyWasNew,
        StringBuilder sb,
        bool replaceManaged,
        Entity replaceExisting,
        bool keepOwnership,
        out string report)
    {
        Entity clone = Entity.Null;
        bool clonedThisCall = false;
        bool addedEntity = false;
        bool addedAsset = false;
        bool addedState = false;
        bool addedSpawn = false;
        bool addedItem = false;
        bool undoThisCall = false;
        bool hadUndo = def.HasRecordedUndo;
        bool unmappedOld = false;
        RetargetRows oldRows = default;

        try
        {
            if (!prefabs._PrefabGuidToEntityMap.TryGetValue(source, out var sourceEntity))
            {
                RollbackInsert(
                    em, prefabs, gameData, def, target, clone,
                    clonedThisCall, addedEntity, addedAsset, addedState, addedSpawn, addedItem,
                    keyWasNew, undoThisCall, keepOwnership);
                report = sb + $"source prefab {source.GuidHash} not found";
                return false;
            }
            sb.Append($"source entity {sourceEntity.Index}:{sourceEntity.Version}; ");

            var itemType = Ecs.ComponentIndex.Of<ItemData>(ComponentType.AccessMode.ReadOnly);
            var buffType = Ecs.ComponentIndex.Of<Buff>(ComponentType.AccessMode.ReadOnly);
            bool hasItem = em.HasComponent(sourceEntity, itemType);
            bool hasBuff = em.HasComponent(sourceEntity, buffType);
            if (!PrefabApplyBookkeeping.SourceMatchesKind(def.Kind, hasItem, hasBuff))
            {
                RollbackInsert(
                    em, prefabs, gameData, def, target, clone,
                    clonedThisCall, addedEntity, addedAsset, addedState, addedSpawn, addedItem,
                    keyWasNew, undoThisCall, keepOwnership);
                report = sb + PrefabApplyBookkeeping.SourceKindRejectedMessage(def.Kind);
                return false;
            }

            bool isBuff = def.Kind == PrefabKind.Buff;

            var cloneEntity = em.Instantiate(sourceEntity);
            clone = cloneEntity;
            clonedThisCall = true;
            RememberClone(target);

            var prefabType = Ecs.ComponentIndex.Of<PrefabTag>(ComponentType.AccessMode.ReadWrite);
            if (!em.HasComponent(clone, prefabType))
                em.AddComponent(clone, prefabType);
            em.SetComponentData(clone, target);
            sb.Append($"clone {clone.Index}:{clone.Version}; ");
            if (isBuff) sb.Append("kind=buff; ");
            AppendNetIdentity(em, clone, target, sb);

            if (!isBuff)
            {
                var itemData = em.GetComponentData<ItemData>(clone);
                itemData.ItemTypeGUID = target;
                // See UpdateExisting: ItemHashLookupMap.Entity must be the clone prefab.
                itemData.Entity = clone;
                if (def.MaxAmount > 0) itemData.MaxAmount = def.MaxAmount;
                else def.MaxAmount = itemData.MaxAmount;
                // A fresh clone is a copy of the donor, so its own category is the donor's.
                if (def.Keep != null)
                {
                    var donorData = itemData;
                    ItemKeepPolicy.ApplyCategory(ref itemData, def.Keep, in donorData);
                }
                em.SetComponentData(clone, itemData);
                // Own-clone baseline is applied before it enters item and prefab lookups.
                def.ArmorBaseline?.Apply(em, clone);
                if (def.StatBonuses != null) ItemStatBonusesApply.Apply(em, clone, sourceEntity, def.StatBonuses);
                if (def.Keep?.Indestructible == true)
                    ItemKeepPolicy.KeepDurability(em, clone);
            }

            // Retarget: unmap the old clone only after the new entity exists so a later
            // failure can remap the old GUID. Destroy the old entity only on success.
            if (replaceExisting != Entity.Null)
            {
                if (!TrySnapshotRetargetRows(prefabs, gameData, target, replaceExisting, out oldRows))
                {
                    RollbackInsert(
                        em, prefabs, gameData, def, target, clone,
                        clonedThisCall, addedEntity, addedAsset, addedState, addedSpawn, addedItem,
                        keyWasNew, undoThisCall, keepOwnership);
                    Plugin.Logger?.LogWarning(PrefabApplyBookkeeping.FormatRetargetRemapFailed(def.Id));
                    report = sb + "; " + PrefabApplyBookkeeping.FormatRetargetRemapFailed(def.Id);
                    return false;
                }

                PrepareRetarget(prefabs, gameData, target);
                unmappedOld = true;
            }

            AddCloneLookups(prefabs, source, target, clone, def.Id, out addedEntity, out addedAsset, out addedState);
            addedSpawn = TryWriteSpawnable(prefabs, def.Id, target);
            sb.Append($"maps: entity={addedEntity} asset={addedAsset} state={addedState} spawn={addedSpawn}; ");

            bool addedManaged;
            if (isBuff)
            {
                // HUD icon is ShowOnBuffBarData on the cloned entity (client world 4), copied
                // by Instantiate. No ItemHashLookupMap / ManagedItemData.
                addedItem = false;
                addedManaged = true;
                sb.Append("itemMap=skipped; managed=skipped (buff); ");
            }

            else
            {
                var itemData = em.GetComponentData<ItemData>(clone);
                addedItem = gameData.ItemHashLookupMap.TryAdd(target, itemData);
                Progenitor.Items.ItemCatalogVersion.Bump();
                sb.Append($"itemMap={addedItem}; ");

                addedManaged = EnsureManagedData(gameData, def, source, target, replaceExisting: replaceManaged, out var managedWhy);
                undoThisCall = !hadUndo && def.HasRecordedUndo;
                sb.Append($"managed={addedManaged} ({managedWhy}); ");
            }

            string lookedUp = prefabs._PrefabLookupMap.TryGetName(target, out var n) ? n : "<none>";
            sb.Append($"name lookup: {lookedUp}");

            bool ok = isBuff ? addedEntity : (addedEntity && addedItem && addedManaged);
            if (!ok)
            {
                RollbackInsert(
                    em, prefabs, gameData, def, target, clone,
                    clonedThisCall, addedEntity, addedAsset, addedState, addedSpawn, addedItem,
                    keyWasNew, undoThisCall, keepOwnership);
                if (unmappedOld)
                    RemapClone(prefabs, gameData, target, oldRows, def.Id);
                report = sb + "; rolled back this call";
                return false;
            }

            Entity existing = replaceExisting;
            if (unmappedOld && existing != Entity.Null && em.Exists(existing))
            {
                try { em.DestroyEntity(existing); }
                catch (Exception e)
                {
                    Plugin.Logger?.LogWarning($"PrefabApplicator: retarget DestroyEntity threw: {e.Message}");
                }
            }

            if (isBuff)
            {
                RegisterLocalizedFields(def, BuffBarText.NameKey(def.Id), PrefabWire.DescriptionKey(def.Id));
                BuffIconApply.TryApply(em, clone, def);
                BuffBarTextApply.TryApply(em, clone, def);
            }

            if (!isBuff && def.ArmorBaseline != null)
            {
                try { ApplyArmorBaselineToLive(world, em, def, target, clone); }
                catch (Exception e)
                { Plugin.Logger?.LogWarning("PrefabApplicator: armor baseline live migration for " + def.Id + " failed: " + e.Message); }
            }
            if (!isBuff && def.Keep != null)
            {
                sb.Append("; keep=").Append(def.Keep);
                RearmKeptItemsLogged(world, def);
            }
            report = sb.ToString();
            return true;
        }
        catch (Exception ex)
        {
            RollbackInsert(
                em, prefabs, gameData, def, target, clone,
                clonedThisCall, addedEntity, addedAsset, addedState, addedSpawn, addedItem,
                keyWasNew, undoThisCall || (!hadUndo && def.HasRecordedUndo), keepOwnership);
            if (unmappedOld && replaceExisting != Entity.Null)
                RemapClone(prefabs, gameData, target, oldRows, def.Id);
            report = sb + " EXCEPTION " + ex + "; rolled back this call";
            return false;
        }
    }

    private static void ApplyArmorBaseline(World world, EntityManager em, PrefabDefinition def,
        PrefabGUID target, Entity clone)
    {
        if (def.ArmorBaseline == null) return;
        def.ArmorBaseline.Apply(em, clone);
        // Registration is committed once the owned clone is ready. A malformed or
        // stale live item must not trigger the ItemData-only registration rollback.
        try { ApplyArmorBaselineToLive(world, em, def, target, clone); }
        catch (Exception e)
        { Plugin.Logger?.LogWarning("PrefabApplicator: armor baseline live migration for " + def.Id + " failed: " + e.Message); }
    }

    /// <summary>
    /// In-place update: durability rules on the surviving clone (kept values, or the donor's
    /// rules back when <c>Indestructible</c> was dropped), then the server-side instance rearm.
    /// The category bits were already written with the clone's <c>ItemData</c> and map row.
    /// </summary>
    private static void ApplyKeepToClone(World world, EntityManager em, PrefabDefinition def,
        Entity clone, Entity donor, StringBuilder sb)
    {
        try
        {
            if (def.Keep?.Indestructible == true) ItemKeepPolicy.KeepDurability(em, clone);
            else ItemKeepPolicy.RestoreDurabilityRules(em, clone, donor);
        }
        catch (Exception e)
        { Plugin.Logger?.LogWarning("PrefabApplicator: keep durability on clone " + def.Id + " failed: " + e.Message); }
        if (def.Keep == null) return;
        sb.Append("; keep=").Append(def.Keep);
        RearmKeptItemsLogged(world, def);
    }

    private static void RearmKeptItemsLogged(World world, PrefabDefinition def)
    {
        // Registration is committed; a stale or malformed live item must not roll it back.
        try { RearmKeptItems(world, new[] { def }); }
        catch (Exception e)
        { Plugin.Logger?.LogWarning("PrefabApplicator: keep rearm for " + def.Id + " failed: " + e.Message); }
    }

    /// <summary>
    /// Server only. Re-check each kept clone and its <c>ItemHashLookupMap</c> row (cheap, no
    /// scan), then, when <paramref name="scanInstances"/>, give every existing instance the
    /// policy's category bits and indestructible durability. The instance scan is the live item
    /// query (<c>PrefabGUID</c> + <c>ItemData</c>, no <c>Prefab</c>, disabled included), which on
    /// the server also holds worn items: they are instances of the clone and carry its GUID. One
    /// scan covers any number of definitions. Returns the number of entities or rows written.
    /// </summary>
    internal static int RearmKeptItems(World world, IReadOnlyList<PrefabDefinition> defs, bool scanInstances = true)
    {
        if (!Plugin.IsServerProcess || world == null || !world.IsCreated || defs == null) return 0;
        var policies = new Dictionary<int, ItemKeepPolicy>();
        var ids = new Dictionary<int, string>();
        for (int i = 0; i < defs.Count; i++)
        {
            var def = defs[i];
            if (def?.Keep == null || def.Kind != PrefabKind.Item) continue;
            policies[def.Guid.GuidHash] = def.Keep;
            ids[def.Guid.GuidHash] = def.Id;
        }
        if (policies.Count == 0) return 0;

        var em = world.EntityManager;
        int written = RecheckKeptClones(world, em, policies, ids);
        if (!scanInstances) return written;

        if (!TryGetLiveQuery(world, em, PrefabKind.Item, out var query))
            throw new InvalidOperationException("Cannot enumerate existing item instances.");
        int instances = 0;
        var items = query.ToEntityArray(Allocator.Temp);
        try
        {
            for (int i = 0; i < items.Length; i++)
            {
                Entity item = items[i];
                if (!em.Exists(item)) continue;
                if (!policies.TryGetValue(em.GetComponentData<PrefabGUID>(item).GuidHash, out var policy)) continue;
                try { if (policy.ApplyToInstance(em, item)) instances++; }
                catch (Exception e)
                { Plugin.Logger?.LogWarning("PrefabApplicator: keep rearm skipped item "
                    + item.Index + ":" + item.Version + ": " + e.Message); }
            }
        }
        finally { items.Dispose(); }

        if (instances != 0)
            Plugin.Logger?.LogInfo("PrefabApplicator: keep rules rearmed " + instances + " existing item(s).");
        return written + instances;
    }

    /// <summary>GUIDs whose clone or row was found stripped; logged once per Core generation.</summary>
    private static readonly HashSet<int> KeepStripLogged = new();

    /// <summary>
    /// The game strips <c>BloodBound</c> from Equippable item data when the server's blood-bound
    /// equipment setting is off, in its item registration pass over newly registered prefabs,
    /// which runs before Progenitor clones exist. Nothing is known to rewrite a clone later; this
    /// re-check makes sure: it re-sets the bits on the clone and its lookup row and logs once
    /// when it had to.
    /// </summary>
    private static int RecheckKeptClones(World world, EntityManager em,
        Dictionary<int, ItemKeepPolicy> policies, Dictionary<int, string> ids)
    {
        var prefabs = world.GetExistingSystemManaged<PrefabCollectionSystem>();
        var gameData = world.GetExistingSystemManaged<GameDataSystem>();
        if (prefabs == null || gameData == null) return 0;
        var itemType = Ecs.ComponentIndex.Of<ItemData>(ComponentType.AccessMode.ReadOnly);
        int written = 0;
        foreach (var pair in policies)
        {
            var target = new PrefabGUID(pair.Key);
            var policy = pair.Value;
            string id = ids[pair.Key];
            try
            {
                bool stripped = false;
                if (prefabs._PrefabGuidToEntityMap.TryGetValue(target, out var clone)
                    && clone != Entity.Null && em.Exists(clone) && em.HasComponent(clone, itemType))
                {
                    var data = em.GetComponentData<ItemData>(clone);
                    long before = (long)data.ItemCategory;
                    long after = ItemKeepRules.InstanceCategory(before, policy.BloodBound, policy.Indestructible);
                    if (after != before)
                    {
                        data.ItemCategory = (ItemCategory)after;
                        em.SetComponentData(clone, data);
                        Progenitor.Items.ItemCatalogVersion.Bump();
                        stripped = true;
                        written++;
                    }
                    if (policy.Indestructible && ItemKeepPolicy.KeepDurability(em, clone))
                    {
                        stripped = true;
                        written++;
                    }
                }
                if (gameData.ItemHashLookupMap.TryGetValue(target, out var row))
                {
                    long before = (long)row.ItemCategory;
                    long after = ItemKeepRules.InstanceCategory(before, policy.BloodBound, policy.Indestructible);
                    if (after != before)
                    {
                        row.ItemCategory = (ItemCategory)after;
                        gameData.ItemHashLookupMap.Remove(target);
                        gameData.ItemHashLookupMap.TryAdd(target, row);
                        Progenitor.Items.ItemCatalogVersion.Bump();
                        stripped = true;
                        written++;
                    }
                }
                if (stripped && KeepStripLogged.Add(pair.Key))
                    Plugin.Logger?.LogWarning("PrefabApplicator: keep rules for " + id
                        + " had been cleared from the clone or its item lookup row; re-set (" + policy + ").");
            }
            catch (Exception e)
            { Plugin.Logger?.LogWarning("PrefabApplicator: keep re-check for " + id + " failed: " + e.Message); }
        }
        return written;
    }

    private static void ApplyArmorBaselineToLive(World world, EntityManager em, PrefabDefinition def,
        PrefabGUID target, Entity clone)
    {
        var baseline = def.ArmorBaseline;
        if (baseline == null) return;
        if (!TryGetLiveQuery(world, em, PrefabKind.Item, out var query))
            throw new InvalidOperationException("Cannot enumerate existing armor item instances.");
        var items = query.ToEntityArray(Allocator.Temp);
        try
        {
            int migrated = 0;
            for (int i = 0; i < items.Length; i++)
            {
                Entity item = items[i];
                if (item == clone || !em.Exists(item)
                    || em.GetComponentData<PrefabGUID>(item).GuidHash != target.GuidHash) continue;
                try
                {
                    baseline.Apply(em, item);
                    migrated++;
                }
                catch (Exception e)
                { Plugin.Logger?.LogWarning("PrefabApplicator: armor baseline skipped live item "
                    + item.Index + ":" + item.Version + " for " + def.Id + ": " + e.Message); }
            }
            if (migrated != 0)
                Plugin.Logger?.LogInfo("PrefabApplicator: armor baseline migrated " + migrated
                    + " live item(s) for " + def.Id + "; re-equip to refresh wearer totals.");
        }
        finally { items.Dispose(); }

        // A networked clone can keep the donor PrefabGUID on the client-side
        // inventory entity even while EquipmentSlot.SlotId is our custom GUID.
        // Migrate those currently worn instances by the authoritative slot ID.
        var equipmentType = Ecs.ComponentIndex.Of<Equipment>(ComponentType.AccessMode.ReadOnly);
        var builder = new EntityQueryBuilder(Allocator.Temp);
        try
        {
            builder = builder.AddAll(equipmentType).WithOptions(EntityQueryOptions.IncludeDisabledEntities);
            var equippedQuery = em.CreateEntityQuery(ref builder);
            try
            {
                var wearers = equippedQuery.ToEntityArray(Allocator.Temp);
                try
                {
                    var seen = new HashSet<long>();
                    foreach (var wearer in wearers)
                    {
                        if (!em.Exists(wearer)) continue;
                        var equipment = em.GetComponentData<Equipment>(wearer);
                        ApplySlot(equipment.ArmorHeadgearSlot);
                        ApplySlot(equipment.ArmorChestSlot);
                        ApplySlot(equipment.ArmorGlovesSlot);
                        ApplySlot(equipment.ArmorLegsSlot);
                        ApplySlot(equipment.ArmorFootgearSlot);
                        ApplySlot(equipment.CloakSlot);
                        ApplySlot(equipment.ChestCosmeticSlot);
                        ApplySlot(equipment.GlovesCosmeticSlot);
                        ApplySlot(equipment.LegsCosmeticSlot);
                        ApplySlot(equipment.FootgearCosmeticSlot);
                        ApplySlot(equipment.CloakCosmeticSlot);
                    }
                    void ApplySlot(EquipmentSlot slot)
                    {
                        if (slot.SlotId.GuidHash != target.GuidHash) return;
                        Entity item = slot.SlotEntity._Entity;
                        if (item == clone || !em.Exists(item)) return;
                        if (em.HasComponent(item, Ecs.ComponentIndex.Of<PrefabTag>(ComponentType.AccessMode.ReadOnly))
                            || !em.HasComponent(item, Ecs.ComponentIndex.Of<ItemData>(ComponentType.AccessMode.ReadOnly)))
                            return;
                        long key = ((long)item.Index << 32) ^ (uint)item.Version;
                        if (!seen.Add(key)) return;
                        try { baseline.Apply(em, item); }
                        catch (Exception e)
                        { Plugin.Logger?.LogWarning("PrefabApplicator: armor baseline skipped equipped item "
                            + item.Index + ":" + item.Version + " for " + def.Id + ": " + e.Message); }
                    }
                }
                finally { wearers.Dispose(); }
            }
            finally { equippedQuery.Dispose(); }
        }
        finally { builder.Dispose(); }
    }

    /// <summary>
    /// Same-id apply: kind change is always refuse; source change retargets only when
    /// no live instance has this PrefabGUID; in-place update still requires
    /// <c>SourceMatchesKind</c> on the existing entity. Failures are fatal for this
    /// definition, not the batch.
    /// </summary>
    private static bool TryDecideSameId(
        World world,
        EntityManager em,
        PrefabCollectionSystem prefabs,
        PrefabDefinition def,
        PrefabGUID source,
        PrefabGUID target,
        Entity existing,
        out PrefabApplyBookkeeping.SameIdApplyAction action,
        out int wasSourceHash,
        out string why)
    {
        action = PrefabApplyBookkeeping.SameIdApplyAction.UpdateInPlace;
        wasSourceHash = 0;
        why = "";
        try
        {
            var itemType = Ecs.ComponentIndex.Of<ItemData>(ComponentType.AccessMode.ReadOnly);
            var buffType = Ecs.ComponentIndex.Of<Buff>(ComponentType.AccessMode.ReadOnly);
            bool hasItem = em.HasComponent(existing, itemType);
            bool hasBuff = em.HasComponent(existing, buffType);
            PrefabKind existingKind = PrefabApplyBookkeeping.InferKindFromComponents(hasItem, hasBuff);
            if (TryGetAppliedIdentity(target, out int rememberedSource, out var rememberedKind, out bool hasKind))
            {
                wasSourceHash = rememberedSource;
                if (hasKind) existingKind = rememberedKind;
            }

            bool existingMatches = PrefabApplyBookkeeping.SourceMatchesKind(def.Kind, hasItem, hasBuff);
            bool fingerprintMatches = true;
            if (wasSourceHash == 0
                && prefabs._PrefabGuidToEntityMap.TryGetValue(source, out var sourceEntity)
                && em.Exists(sourceEntity)
                && TryReadNetSnapshotPresence(em, existing, out bool cloneHasNet, out int cloneNetType)
                && TryReadNetSnapshotPresence(em, sourceEntity, out bool sourceHasNet, out int sourceNetType))
            {
                fingerprintMatches = PrefabApplyBookkeeping.CloneLooksLikeSource(
                    cloneHasNet, cloneNetType, sourceHasNet, sourceNetType);
            }

            bool sourceChanged = PrefabApplyBookkeeping.SourceChanged(
                wasSourceHash, source.GuidHash, fingerprintMatches);

            int liveCount = 0;
            if (sourceChanged && existingKind == def.Kind)
            {
                int counted = CountLiveInstances(world, em, target, existing, def.Kind);
                liveCount = PrefabApplyBookkeeping.LiveCountForDecision(counted);
            }

            action = PrefabApplyBookkeeping.DecideSameIdApply(
                existingKind, def.Kind, existingMatches, sourceChanged, liveCount);
            switch (action)
            {
                case PrefabApplyBookkeeping.SameIdApplyAction.RefuseKindChange:
                    why = PrefabApplyBookkeeping.FormatKindChangedRefuse(def.Id, existingKind, def.Kind);
                    return true;
                case PrefabApplyBookkeeping.SameIdApplyAction.RefuseSourceChange:
                    why = PrefabApplyBookkeeping.FormatSourceChangedRefuse(
                        def.Id, wasSourceHash, source.GuidHash, liveCount);
                    return true;
                case PrefabApplyBookkeeping.SameIdApplyAction.RefuseExistingMismatch:
                    why = PrefabApplyBookkeeping.SourceKindRejectedMessage(def.Kind);
                    return true;
                default:
                    return true;
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"PrefabApplicator: same-id decision for '{def.Id}' threw: {e.Message}");
            why = "same-id decision threw: " + e.Message;
            action = PrefabApplyBookkeeping.SameIdApplyAction.RefuseSourceChange;
            return false;
        }
    }

    private struct RetargetRows
    {
        public Entity Entity;
        public bool HadAsset;
        public ConvertedAssetData Asset;
        public bool HadState;
        public AssetConversionState State;
        public bool HadItem;
        public ItemData Item;
    }

    /// <summary>
    /// Copy entity / asset / conversion / item-hash rows before <see cref="PrepareRetarget"/>
    /// so a failed re-clone can restore all four. False when any lookup threw (do not unmap).
    /// </summary>
    private static bool TrySnapshotRetargetRows(
        PrefabCollectionSystem prefabs,
        GameDataSystem gameData,
        PrefabGUID target,
        Entity existing,
        out RetargetRows rows)
    {
        rows = new RetargetRows { Entity = existing };
        try
        {
            rows.HadAsset = prefabs._PrefabDataLookup.TryGetValue(target, out rows.Asset);
            rows.HadState = prefabs._ConversionStateLookup.TryGetValue(target, out rows.State);
            if (gameData != null)
                rows.HadItem = gameData.ItemHashLookupMap.TryGetValue(target, out rows.Item);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Unmap the old clone (entity / asset / conversion / item-hash) so <c>InsertNew</c>
    /// can TryAdd the same GUID. Does not destroy: the caller Instantiates the new clone
    /// first and destroys the old entity only on success. Spawnable name and ItemKeyLookup
    /// stay. Clone ownership stays so a later snapshot still claims the GUID.
    /// </summary>
    private static void PrepareRetarget(
        PrefabCollectionSystem prefabs,
        GameDataSystem gameData,
        PrefabGUID target)
    {
        try { prefabs._PrefabGuidToEntityMap.Remove(target); }
        catch (Exception e) { Plugin.Logger?.LogWarning($"PrefabApplicator: retarget entity map threw: {e.Message}"); }
        try { prefabs._PrefabDataLookup.Remove(target); }
        catch (Exception e) { Plugin.Logger?.LogWarning($"PrefabApplicator: retarget asset map threw: {e.Message}"); }
        try { prefabs._ConversionStateLookup.Remove(target); }
        catch (Exception e) { Plugin.Logger?.LogWarning($"PrefabApplicator: retarget conversion map threw: {e.Message}"); }
        try { gameData.ItemHashLookupMap.Remove(target); }
        catch (Exception e) { Plugin.Logger?.LogWarning($"PrefabApplicator: retarget item map threw: {e.Message}"); }
        Progenitor.Items.ItemCatalogVersion.Bump();
    }

    /// <summary>
    /// Put the old clone back in every bookkeeping map after a failed retarget InsertNew.
    /// One Warning if any row could not be restored (R26 finding 13).
    /// </summary>
    private static void RemapClone(
        PrefabCollectionSystem prefabs,
        GameDataSystem gameData,
        PrefabGUID target,
        RetargetRows rows,
        string id)
    {
        bool ok = true;
        try
        {
            if (rows.Entity != Entity.Null)
                prefabs._PrefabGuidToEntityMap.TryAdd(target, rows.Entity);
        }
        catch { ok = false; }
        try
        {
            if (rows.HadAsset)
                prefabs._PrefabDataLookup.TryAdd(target, rows.Asset);
        }
        catch { ok = false; }
        try
        {
            if (rows.HadState)
                prefabs._ConversionStateLookup.TryAdd(target, rows.State);
        }
        catch { ok = false; }
        try
        {
            if (rows.HadItem && gameData != null)
                gameData.ItemHashLookupMap.TryAdd(target, rows.Item);
            Progenitor.Items.ItemCatalogVersion.Bump();
        }
        catch { ok = false; }

        if (!ok)
            Plugin.Logger?.LogWarning(PrefabApplyBookkeeping.FormatRetargetRemapFailed(id));
    }

    /// <summary>
    /// Live entities that hold this <c>PrefabGUID</c> and are not the prefab clone
    /// (cached <c>PrefabGUID</c> + <c>Buff</c> or <c>ItemData</c>, exclude <c>Prefab</c>,
    /// include disabled). Inventory stacks are not entities and do not count.
    /// Returns <see cref="PrefabApplyBookkeeping.LiveInstanceCountUnknown"/> when the
    /// query threw (callers treat that as "instances exist" so retarget is refused).
    /// </summary>
    private static int CountLiveInstances(
        World world,
        EntityManager em,
        PrefabGUID target,
        Entity prefabClone,
        PrefabKind kind)
    {
        try
        {
            if (!TryGetLiveQuery(world, em, kind, out var query))
            {
                Plugin.Logger?.LogWarning(PrefabApplyBookkeeping.FormatLiveQueryFailedClosed(target.GuidHash));
                return PrefabApplyBookkeeping.LiveInstanceCountUnknown;
            }
            int total;
            try { total = query.CalculateEntityCount(); }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning(
                    PrefabApplyBookkeeping.FormatLiveQueryFailedClosed(target.GuidHash) + ": " + e.Message);
                return PrefabApplyBookkeeping.LiveInstanceCountUnknown;
            }
            if (total == 0) return 0;

            var array = query.ToEntityArray(Unity.Collections.Allocator.Temp);
            try
            {
                int n = 0;
                int hash = target.GuidHash;
                for (int i = 0; i < array.Length; i++)
                {
                    var e = array[i];
                    if (e == prefabClone) continue;
                    if (!em.Exists(e)) continue;
                    PrefabGUID pg = em.GetComponentData<PrefabGUID>(e);
                    if (pg.GuidHash == hash) n++;
                }
                return n;
            }
            finally
            {
                array.Dispose();
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning(
                PrefabApplyBookkeeping.FormatLiveQueryFailedClosed(target.GuidHash) + ": " + e.Message);
            return PrefabApplyBookkeeping.LiveInstanceCountUnknown;
        }
    }

    /// <summary>
    /// Drop cached live-instance queries (Core Uninstall). Each is disposed only while the world it
    /// was built in still exists; a query whose world is already destroyed is forgotten.
    /// </summary>
    internal static void UninstallQueries()
    {
        lock (LiveQueryGate)
        {
            var buff = BuffLiveQuery.Release();
            var item = ItemLiveQuery.Release();
            if (buff == Ecs.CachedQueryRelease.Forget || item == Ecs.CachedQueryRelease.Forget)
                Plugin.Logger?.LogInfo("PrefabApplicator: live-query uninstall buff=" + buff + " item=" + item
                    + " (Forget: the world it was built in is gone, so it is not disposed).");
        }
    }

    private static bool TryGetLiveQuery(World world, EntityManager em, PrefabKind kind, out EntityQuery query)
    {
        query = default;
        if (world == null || !world.IsCreated) return false;
        lock (LiveQueryGate)
        {
            bool buff = kind == PrefabKind.Buff;
            var cache = buff ? BuffLiveQuery : ItemLiveQuery;
            if (cache.TryGet(world, out query)) return true;
            if (!TryBuildLiveQuery(em, includeBuff: buff, out query)) return false;
            cache.Store(world, query);
            return true;
        }
    }

    private static bool TryBuildLiveQuery(EntityManager em, bool includeBuff, out EntityQuery query)
    {
        query = default;
        var guidType = Ecs.ComponentIndex.Of<PrefabGUID>(ComponentType.AccessMode.ReadOnly);
        var prefabType = Ecs.ComponentIndex.Of<PrefabTag>(ComponentType.AccessMode.ReadOnly);
        var extra = includeBuff
            ? Ecs.ComponentIndex.Of<Buff>(ComponentType.AccessMode.ReadOnly)
            : Ecs.ComponentIndex.Of<ItemData>(ComponentType.AccessMode.ReadOnly);
        var builder = new EntityQueryBuilder(Unity.Collections.Allocator.Temp);
        try
        {
            builder = builder.AddAll(guidType);
            builder = builder.AddAll(extra);
            builder = builder.AddNone(prefabType);
            builder = builder.WithOptions(EntityQueryOptions.IncludeDisabledEntities);
            query = em.CreateEntityQuery(ref builder);
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("PrefabApplicator: live-query build threw: " + e.Message);
            return false;
        }
        finally
        {
            builder.Dispose();
        }
    }

    /// <summary>
    /// NetSnapshot presence + schema ushort for the same-id fingerprint. False only
    /// when the read threw; missing buffer is a successful "no snapshot" result.
    /// </summary>
    private static unsafe bool TryReadNetSnapshotPresence(
        EntityManager em,
        Entity entity,
        out bool hasBuffer,
        out int snapshotType)
    {
        hasBuffer = false;
        snapshotType = 0;
        try
        {
            var netType = Ecs.ComponentIndex.Of<NetSnapshot>(ComponentType.AccessMode.ReadWrite);
            if (!em.HasComponent(entity, netType)) return true;
            hasBuffer = true;
            var typeIndex = Ecs.ComponentIndex.IndexOf<NetSnapshot>();
            void* raw = em.GetBufferRawRW(entity, typeIndex);
            int length = em.GetBufferLength(entity, typeIndex);
            if (raw == null || !PrefabApplyBookkeeping.NetSnapshotHeaderIsWritable(length))
                return true;
            snapshotType = *(ushort*)((byte*)raw + PrefabApplyBookkeeping.NetSnapshotTypeOffset);
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"PrefabApplicator: NetSnapshot fingerprint threw: {e.Message}");
            return false;
        }
    }

    private static bool TryResolveSource(
        PrefabCollectionSystem prefabs,
        PrefabDefinition def,
        out PrefabGUID source,
        out string why)
    {
        source = def.Source;
        why = "";
        if (!source.IsEmpty()) return true;
        if (string.IsNullOrEmpty(def.SourceName))
        {
            why = "no source guid or spawnable name";
            return false;
        }
        // Buffs (and most non-item prefabs) are not console spawnables, so ask the PrefabLookupMap
        // by name first (the same lookup the Dev bridge uses), then fall back to the spawnable list.
        try
        {
            PrefabGUID byName = default;
            if (prefabs._PrefabLookupMap.TryGetPrefabGuidWithName(def.SourceName, out byName, true) && byName._Value != 0)
            {
                source = byName;
                return true;
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"PrefabApplicator: TryGetPrefabGuidWithName('{def.SourceName}') threw: {e.Message}");
        }
        var map = prefabs._SpawnableNameToPrefabGuidDictionary;
        if (map == null || !map.TryGetValue(def.SourceName, out source))
        {
            why = $"source prefab '{def.SourceName}' not found by PrefabLookupMap name or spawnable name";
            source = default;
            return false;
        }
        return true;
    }

    /// <summary>
    /// Refuse an id that already exists in the spawnable dictionary mapped to a GUID this
    /// process did not clone, even when that GUID equals this definition's own hash
    /// (vanilla name reuse / hash collision). Never overwrite a vanilla (or someone else's)
    /// name → GUID entry. Re-register of a GUID we cloned is allowed (in-place update).
    /// </summary>
    private static bool TryClaimSpawnableName(
        PrefabCollectionSystem prefabs,
        PrefabDefinition def,
        PrefabGUID target,
        out string why)
    {
        why = "";
        var dict = prefabs._SpawnableNameToPrefabGuidDictionary;
        if (dict == null || string.IsNullOrEmpty(def.Id)) return true;
        if (!dict.TryGetValue(def.Id, out var mapped)) return true;
        bool cloned = WasClonedHere(mapped);
        if (PrefabApplyBookkeeping.CanClaimMappedSpawnableName(mapped.GuidHash, target.GuidHash, cloned))
            return true;
        if (mapped.GuidHash == target.GuidHash)
        {
            why = $"spawnable name '{def.Id}' already maps to {mapped.GuidHash} (not cloned by this process)";
            return false;
        }
        if (cloned)
        {
            why = $"spawnable name '{def.Id}' already maps to our guid {mapped.GuidHash}, not {target.GuidHash}";
            return false;
        }
        why = $"spawnable name '{def.Id}' already maps to {mapped.GuidHash} (not ours)";
        return false;
    }

    private static bool TryWriteSpawnable(PrefabCollectionSystem prefabs, string id, PrefabGUID target)
    {
        var dict = prefabs._SpawnableNameToPrefabGuidDictionary;
        if (dict == null || string.IsNullOrEmpty(id)) return false;
        if (dict.TryGetValue(id, out var mapped))
            return mapped.GuidHash == target.GuidHash;
        dict[id] = target;
        return true;
    }

    /// <summary>
    /// Spawn packets identify the prefab from <c>NetSnapshot.GetPrefabGUID</c> (blob offset 0),
    /// not from the ECS <c>PrefabGUID</c> component. <c>EntityManager.Instantiate</c> copies
    /// the source blob, so a clone of haste still serializes as haste until this rewrite.
    /// <c>NetworkSnapshotType</c> stays the source schema id (ushort, not a GUID).
    /// </summary>
    private static unsafe void AppendNetIdentity(EntityManager em, Entity clone, PrefabGUID target, StringBuilder sb)
    {
        try
        {
            var netType = Ecs.ComponentIndex.Of<NetSnapshot>(ComponentType.AccessMode.ReadWrite);
            if (!em.HasComponent(clone, netType))
            {
                sb.Append(PrefabApplyBookkeeping.NetNoneToken);
                sb.Append("; ");
                return;
            }

            var typeIndex = Ecs.ComponentIndex.IndexOf<NetSnapshot>();
            void* raw = em.GetBufferRawRW(clone, typeIndex);
            int length = em.GetBufferLength(clone, typeIndex);
            if (raw == null || !PrefabApplyBookkeeping.NetSnapshotHeaderIsWritable(length))
            {
                sb.Append(PrefabApplyBookkeeping.FormatNetShort(length));
                sb.Append("; ");
                return;
            }

            int stored = *(int*)((byte*)raw + PrefabApplyBookkeeping.NetSnapshotPrefabGuidOffset);
            int snapshotType = *(ushort*)((byte*)raw + PrefabApplyBookkeeping.NetSnapshotTypeOffset);
            int cloneHash = target._Value;
            if (PrefabApplyBookkeeping.ShouldRewriteNetSnapshotGuid(stored, cloneHash))
                *(int*)((byte*)raw + PrefabApplyBookkeeping.NetSnapshotPrefabGuidOffset) = cloneHash;
            sb.Append(PrefabApplyBookkeeping.FormatNetIdentity(cloneHash, snapshotType, stored));
            sb.Append("; ");
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"PrefabApplicator: NetSnapshot identity for guid {target._Value} threw: {e.Message}");
            sb.Append(PrefabApplyBookkeeping.NetErrorToken);
            sb.Append("; ");
        }
    }

    /// <summary>
    /// Clones the source item's managed data (icon, name, description) under the target GUID.
    /// The headless server has an empty registry and skips this. When
    /// <paramref name="replaceExisting"/> is set, a present entry is rebuilt so a re-register
    /// after soft unapply can change the display name and description. Returns true when the
    /// game's own getter can see the entry afterwards (or when there is nothing to insert).
    /// </summary>
    private static bool EnsureManagedData(
        GameDataSystem gameData,
        PrefabDefinition def,
        PrefabGUID source,
        PrefabGUID target,
        bool replaceExisting,
        out string why)
    {
        var registry = gameData.ManagedDataRegistry;
        var map = registry._DataLookupMap;
        if (map == null || map.Count == 0)
        {
            why = "registry empty (server)";
            return true;
        }
        if (!replaceExisting && registry.TryGet<ManagedItemData>(target, out var already) && already != null)
        {
            why = "present";
            return true;
        }
        if (!registry.TryGet<ManagedItemData>(source, out var managed) || managed == null)
        {
            why = "source has no managed item data";
            return false;
        }

        // ManagedItemData.ICloneable.Clone is the native identity thunk (returns this),
        // not a copy. Allocate an independent data object before changing any field.
        // Name and Description are value structs; the shared Sprite is only replaced.
        var cloned = new ManagedItemData
        {
            Icon = managed.Icon,
            Name = managed.Name,
            Description = managed.Description,
        };
        ApplyLocalizedFields(cloned, def);
        BuffIconApply.TryApplyItem(cloned, def);
        var key = PrefabLookupKey.Create<ManagedItemData>(target);
        ManagedDataInsert.Set(map, key, cloned);
        // TryGet takes the GUID as an int and builds the key natively, so it is a trustworthy check.
        bool ok = registry.TryGet<ManagedItemData>(target, out var check) && check != null;
        why = ok
            ? (replaceExisting ? "updated" : "inserted")
            : "inserted but getter still misses";
        return ok;
    }

    /// <summary>
    /// Record <c>Named</c> / <c>Described</c> into Localization under the definition's
    /// owner (the server-sync owner for text a <c>PrfD</c> filled, so connection end drops it). Not recorded as apply undo: soft unapply must not drop keys the clone's
    /// <c>ShowOnBuffBarData</c> still points at. Same owner+text is a no-op (see
    /// <c>Localization.Register</c>). Buffs call this before the HUD write. Items pass
    /// <paramref name="nameKey"/> = <c>id</c>; buffs pass <c>id.name</c>.
    /// </summary>
    private static void RegisterLocalizedFields(PrefabDefinition def, string nameKey, string descKey)
    {
        var localizedName = def.LocalizedName;
        if (!string.IsNullOrEmpty(localizedName) && !string.IsNullOrEmpty(nameKey))
        {
            try
            {
                Progenitor.Localization.Localization.Register(nameKey, localizedName, owner: def.LocalizationOwner(name: true));
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogError($"PrefabApplicator: name localization for '{def.Id}' failed: {e}");
            }
        }

        var localizedDescription = def.LocalizedDescription;
        if (!string.IsNullOrEmpty(localizedDescription) && !string.IsNullOrEmpty(descKey))
        {
            try
            {
                Progenitor.Localization.Localization.Register(descKey, localizedDescription, owner: def.LocalizationOwner(name: false));
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogError($"PrefabApplicator: description localization for '{def.Id}' failed: {e}");
            }
        }
    }

    /// <summary>
    /// Writes <c>Named</c> / <c>Described</c> (and a file-based <c>id.desc</c> fallback) onto the
    /// cloned managed data. Items keep the name key as <see cref="PrefabDefinition.Id"/>; buff HUD
    /// text is a separate client write onto <c>ShowOnBuffBarData</c>.
    /// </summary>
    private static void ApplyLocalizedFields(ManagedItemData cloned, PrefabDefinition def)
    {
        string descKey = PrefabWire.DescriptionKey(def.Id);
        RegisterLocalizedFields(def, def.Id, descKey);

        var localizedName = def.LocalizedName;
        if (!string.IsNullOrEmpty(localizedName))
        {
            try
            {
                Progenitor.Localization.Localization.ApplyName(cloned, def.Id);
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogError($"PrefabApplicator: name localization for '{def.Id}' failed: {e}");
            }
        }

        var localizedDescription = def.LocalizedDescription;
        if (!string.IsNullOrEmpty(localizedDescription))
        {
            try
            {
                Progenitor.Localization.Localization.ApplyDescription(cloned, descKey);
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogError($"PrefabApplicator: description localization for '{def.Id}' failed: {e}");
            }
        }
        else if (Progenitor.Localization.Localization.IsRegistered(descKey))
        {
            // No Described() on the builder, but a plugin's en.json (or an earlier Register)
            // already owns id.desc. Point the clone at that key; do not Register again.
            try
            {
                Progenitor.Localization.Localization.ApplyDescription(cloned, descKey);
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogError($"PrefabApplicator: file-based description for '{def.Id}' failed: {e}");
            }
        }
    }

    private static void RestoreExisting(
        EntityManager em,
        GameDataSystem gameData,
        Entity existing,
        PrefabGUID target,
        ItemData oldData,
        bool patchedEntity,
        bool removedItem,
        bool hadPrevious,
        ItemData previousMap)
    {
        try
        {
            if (patchedEntity && em.Exists(existing))
                em.SetComponentData(existing, oldData);
            if (removedItem)
            {
                gameData.ItemHashLookupMap.Remove(target);
                if (hadPrevious)
                    gameData.ItemHashLookupMap.TryAdd(target, previousMap);
                Progenitor.Items.ItemCatalogVersion.Bump();
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"PrefabApplicator: restore existing clone threw: {e.Message}");
        }
    }

    private static void RollbackInsert(
        EntityManager em,
        PrefabCollectionSystem prefabs,
        GameDataSystem gameData,
        PrefabDefinition def,
        PrefabGUID target,
        Entity clone,
        bool clonedThisCall,
        bool addedEntity,
        bool addedAsset,
        bool addedState,
        bool addedSpawn,
        bool addedItem,
        bool keyWasNew,
        bool undoThisCall,
        bool keepOwnership)
    {
        try
        {
            if (addedItem)
            {
                try { gameData.ItemHashLookupMap.Remove(target); }
                catch (Exception e) { Plugin.Logger?.LogWarning($"PrefabApplicator: rollback ItemHashLookupMap threw: {e.Message}"); }
                Progenitor.Items.ItemCatalogVersion.Bump();
            }
            if (addedSpawn)
            {
                try
                {
                    var dict = prefabs._SpawnableNameToPrefabGuidDictionary;
                    if (dict != null && dict.TryGetValue(def.Id, out var mapped) && mapped.GuidHash == target.GuidHash)
                        dict.Remove(def.Id);
                }
                catch (Exception e) { Plugin.Logger?.LogWarning($"PrefabApplicator: rollback spawnable name threw: {e.Message}"); }
            }
            if (addedState)
            {
                try { prefabs._ConversionStateLookup.Remove(target); }
                catch (Exception e) { Plugin.Logger?.LogWarning($"PrefabApplicator: rollback ConversionStateLookup threw: {e.Message}"); }
            }
            if (addedAsset)
            {
                try { prefabs._PrefabDataLookup.Remove(target); }
                catch (Exception e) { Plugin.Logger?.LogWarning($"PrefabApplicator: rollback PrefabDataLookup threw: {e.Message}"); }
            }
            if (addedEntity)
            {
                try { prefabs._PrefabGuidToEntityMap.Remove(target); }
                catch (Exception e) { Plugin.Logger?.LogWarning($"PrefabApplicator: rollback PrefabGuidToEntityMap threw: {e.Message}"); }
            }
            if (clonedThisCall && clone != Entity.Null)
            {
                try
                {
                    if (em.Exists(clone))
                        em.DestroyEntity(clone);
                }
                catch (Exception e) { Plugin.Logger?.LogWarning($"PrefabApplicator: rollback DestroyEntity threw: {e.Message}"); }
            }
            if (clonedThisCall && !keepOwnership)
            {
                bool stillThere = clone != Entity.Null && em.Exists(clone);
                if (!stillThere)
                    ForgetClone(target);
            }
            if (keyWasNew)
                RollbackKey(em, def, target);
            if (undoThisCall)
            {
                try { def.RunUndo(); }
                catch (Exception e) { Plugin.Logger?.LogWarning($"PrefabApplicator: rollback undo threw: {e.Message}"); }
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"PrefabApplicator: rollback '{def.Id}' threw: {e}");
        }
    }

    private static void RollbackKey(EntityManager em, PrefabDefinition def, PrefabGUID target)
    {
        try
        {
            bool ok = ItemKeyTable.TryRemove(em, target, out var why);
            // A clean pop of our own just-appended row happens on every pending retry before the
            // prefab collection is populated; InsertNew may already have popped, so "not present"
            // is also clean. Only an unclean rollback is worth a log line.
            if (!ok
                || (why.IndexOf("popped last", StringComparison.OrdinalIgnoreCase) < 0
                    && why.IndexOf("not present", StringComparison.OrdinalIgnoreCase) < 0))
                Plugin.Logger?.LogWarning($"PrefabApplicator: rolled back key row for '{def.Id}': ok={ok} ({why})");
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"PrefabApplicator: rollback key row for '{def.Id}' threw: {e.Message}");
        }
    }

    private static string KeyTableFailReport(string why)
    {
        if (!string.IsNullOrEmpty(why) && why.IndexOf("full", StringComparison.OrdinalIgnoreCase) >= 0)
            return "item key table full: " + why;
        return "item key table: " + why;
    }

    private static bool IsAlreadyPresent(string why) =>
        !string.IsNullOrEmpty(why) && why.IndexOf("already present", StringComparison.OrdinalIgnoreCase) >= 0;

    internal static bool WasClonedHere(PrefabGUID guid) => Ledger.WasCloned(guid.GuidHash);

    /// <summary>
    /// True when this process successfully applied a definition that occupies
    /// <paramref name="guid"/>'s ItemKeyLookup row (appended this session, or confirmed
    /// already-present on a successful apply after a save reload).
    /// </summary>
    internal static bool WasAppendedHere(PrefabGUID guid) => Ledger.WasAppended(guid.GuidHash);

    /// <summary>How many ItemKeyLookup GUIDs this process owns for tombstone / Describe.</summary>
    internal static int AppendedCount => Ledger.AppendedCount;

    /// <summary>
    /// Mark <paramref name="guid"/> as cloned by this process. Called on a successful
    /// insert or in-place update, and on soft unapply that kept the entity, so a later
    /// Core generation's snapshot still owns it.
    /// </summary>
    internal static void RememberClone(PrefabGUID guid)
    {
        Ledger.RememberClone(guid.GuidHash);
        Progenitor.Items.ItemCatalogVersion.Bump();
    }

    /// <summary>
    /// Record the resolved source and kind for a GUID this process just applied, or
    /// the definition being replaced, so a same-id hot-reload can detect <c>CloneOf</c>
    /// / kind change. Soft unapply does not clear this.
    /// </summary>
    internal static void RememberAppliedIdentity(PrefabGUID guid, PrefabGUID source, PrefabKind kind) =>
        Ledger.RememberIdentity(guid.GuidHash, source.GuidHash, kind);

    internal static bool TryGetAppliedIdentity(PrefabGUID guid, out int sourceHash, out PrefabKind kind)
        => TryGetAppliedIdentity(guid, out sourceHash, out kind, out _);

    internal static bool TryGetAppliedIdentity(
        PrefabGUID guid,
        out int sourceHash,
        out PrefabKind kind,
        out bool hasKind)
        => Ledger.TryGetIdentity(guid.GuidHash, out sourceHash, out kind, out hasKind);

    /// <summary>
    /// Parallel clone-guid / <c>CloneOf</c> hashes for the Core-swap reclaim snapshot.
    /// </summary>
    internal static void ExportAppliedSources(out int[] cloneGuids, out int[] sourceHashes) =>
        Ledger.ExportSources(out cloneGuids, out sourceHashes);

    /// <summary>
    /// Restore remembered <c>CloneOf</c> hashes into a fresh Core generation. Kind is
    /// inferred from the surviving entity on the next apply.
    /// </summary>
    internal static void ImportAppliedSources(int[]? cloneGuids, int[]? sourceHashes)
    {
        PrefabApplyBookkeeping.CopyCloneSources(cloneGuids, sourceHashes, out var guids, out var sources);
        Ledger.ImportSources(guids, sources);
    }

    private static void RememberAppend(PrefabGUID guid, int index) => Ledger.RememberAppend(guid.GuidHash, index);

    private static void ForgetClone(PrefabGUID guid)
    {
        Ledger.ForgetClone(guid.GuidHash);
        Progenitor.Items.ItemCatalogVersion.Bump();
    }

    /// <summary>
    /// A new client world replaced the one every clone and key row was recorded in. Returns how
    /// many GUIDs were forgotten. Client only, and only for a genuinely new world — never for a
    /// Core swap, and never at connection end while the old <c>Client_0</c> may still exist.
    /// </summary>
    internal static int ForgetClientWorld() => Ledger.ForgetWorld();

    /// <inheritdoc cref="PrefabCloneLedger.KeyRowStillHolds"/>
    internal static bool KeyRowStillHolds(int savedIndex, int currentIndex) =>
        PrefabCloneLedger.KeyRowStillHolds(savedIndex, currentIndex);

    internal static bool TryGetAppendedIndex(int guidHash, out int index) =>
        Ledger.TryGetAppendedIndex(guidHash, out index);

    /// <summary>
    /// Copy clone hashes and appended (guid, index) pairs for Bootstrap ContentMemory.
    /// Same-assembly; no private-field names.
    /// </summary>
    internal static void ExportClonesAndKeys(out int[] cloned, out int[] appendedGuids, out int[] appendedIndices) =>
        Ledger.ExportClonesAndKeys(out cloned, out appendedGuids, out appendedIndices);

    /// <summary>Restore clone / appended sets into a fresh Core generation.</summary>
    internal static void ImportClonesAndKeys(int[]? cloned, int[]? appendedGuids, int[]? appendedIndices) =>
        Ledger.ImportClonesAndKeys(cloned, appendedGuids, appendedIndices);
}
