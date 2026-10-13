using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using ProjectM;
using ProjectM.Network;
using ProjectM.Scripting;
using Progenitor.Ecs;
using Progenitor.WorldState;
using Stunlock.Core;
using Unity.Collections;
using Unity.Entities;

namespace Progenitor.Content;

internal enum ItemQuarantineRestoreStatus { Ready, Restored, Pending, Unavailable, Ambiguous }
internal sealed record ItemQuarantineRestoreOutcome(ItemQuarantineRestoreStatus Status, bool Attempted, int Added, string Reason);

/// <summary>Conservative inventory adapter. Only property-free stacks in a uniquely identified
/// player bag are supported. Incarnation tokens are session-local: after load or Core replacement,
/// records remain unavailable rather than being redirected to a recreated character. Raw entity
/// handles never enter the journal. Unique gear and unqualified container identities are preserved.</summary>
internal static class ItemQuarantineAdapter
{
    private sealed record Incarnation(World World, int Epoch, Entity Character, Entity Bag, ulong Account);
    private sealed record Snapshot(int Schema, int Prefab, int Amount, int MaxAmountOverride, string Incarnation, int SourceGuid);
    private static readonly Dictionary<string, Incarnation> Incarnations = new(StringComparer.Ordinal);

    internal static void Reset() => Incarnations.Clear();

    internal static bool TryCapture(World world, Entity inventory, int slot, InventoryBuffer row,
        out string snapshotJson, out ItemQuarantineDestination destination, out string reason)
        => TryCapture(world, inventory, slot, row, 0, out snapshotJson, out destination, out reason);

    internal static bool TryCapture(World world, Entity inventory, int slot, InventoryBuffer row, int sourceGuid,
        out string snapshotJson, out ItemQuarantineDestination destination, out string reason)
    {
        snapshotJson = "";
        destination = new("unsupported", "", "", "", "", slot);
        reason = "";
        try
        {
            if (!Plugin.IsServerProcess || world == null || !world.IsCreated) return Refuse("Server world unavailable.", out reason);
            var em = world.EntityManager;
            if (sourceGuid == 0) return Refuse("Original item donor identity is unknown; item preserved.", out reason);
            if (!TryBuffer(em, inventory, out var buffer) || slot < 0 || slot >= buffer.Length)
                return Refuse("Inventory slot unavailable.", out reason);
            var current = buffer[slot];
            if (!Same(current, row) || row.Amount <= 0 || row.Amount > 4095 || row.ItemType.GuidHash == 0)
                return Refuse("Inventory changed before capture or stack is invalid.", out reason);
            if (row.ItemEntity.GetEntityOnServer() != Entity.Null)
                return Refuse("Unique item properties have no qualified serialization/restoration contract; item preserved.", out reason);
            if (row.MaxAmountOverride != 0)
                return Refuse("Stack capacity override cannot be preserved through the qualified inventory helper; item preserved.", out reason);
            if (!TryStackMetadata(world, row.ItemType, out var metadata) || metadata.MaxAmount < row.Amount)
                return Refuse("Item prefab is not a verified plain stack with sufficient capacity; item preserved.", out reason);

            Entity character = Entity.Null;
            ulong account = 0;
            var query = Query.Build(em, includeDisabled: true, all: new[] { ComponentIndex.Of<PlayerCharacter>(ComponentType.AccessMode.ReadOnly) });
            try
            {
                var players = query.ToEntityArray(Allocator.Temp);
                try
                {
                    if (players.Length > 100000) return Refuse("Player identity query exceeds bounds.", out reason);
                    for (int i = 0; i < players.Length; i++)
                    {
                        var candidate = players[i];
                        if (!TrySingleBag(em, candidate, out var bag) || bag != inventory) continue;
                        if (character != Entity.Null) return Refuse("Bag has ambiguous ownership.", out reason);
                        var userEntity = em.GetComponentData<PlayerCharacter>(candidate).UserEntity;
                        if (!Has<User>(em, userEntity)) return Refuse("Bag owner has no verifiable user.", out reason);
                        var user = em.GetComponentData<User>(userEntity);
                        if (user.PlatformId == 0 || user.LocalCharacter.GetEntityOnServer() != candidate)
                            return Refuse("Player account/character relationship is not verified.", out reason);
                        character = candidate;
                        account = user.PlatformId;
                    }
                }
                finally { players.Dispose(); }
            }
            finally { query.Dispose(); }
            if (character == Entity.Null) return Refuse("Ordinary container persistent identity is unqualified; item preserved.", out reason);
            string token = "";
            foreach (var entry in Incarnations)
                if (ReferenceEquals(entry.Value.World, world) && entry.Value.Epoch == WorldEpoch.Current
                    && entry.Value.Character == character && entry.Value.Bag == inventory && entry.Value.Account == account)
                { token = entry.Key; break; }
            if (token.Length == 0)
            {
                token = System.Guid.NewGuid().ToString("N");
                Incarnations.Add(token, new(world, WorldEpoch.Current, character, inventory, account));
            }
            snapshotJson = JsonSerializer.Serialize(new Snapshot(2, row.ItemType.GuidHash, row.Amount, row.MaxAmountOverride, token, sourceGuid));
            destination = new("player-bag", account.ToString(CultureInfo.InvariantCulture), "", "", token, slot);
            return true;
        }
        catch (Exception e) { return Refuse("Capture refused: " + e.Message, out reason); }
    }

    internal static ItemQuarantineRestoreOutcome ProbeRestore(World world, ItemQuarantineRecord record)
        => Prepare(world, record, out _, out _);

    /// <summary>Caller must durably claim Restoring before this call. Rechecks preflight; Attempted
    /// becomes true before the native helper. A thrown or partial add is ambiguous and never retried.</summary>
    internal static ItemQuarantineRestoreOutcome TryRestore(World world, ItemQuarantineRecord record)
    {
        var preflight = Prepare(world, record, out var identity, out var snapshot);
        if (preflight.Status != ItemQuarantineRestoreStatus.Ready) return preflight;
        if (record.State != ItemQuarantineState.Restoring)
            return new(ItemQuarantineRestoreStatus.Unavailable, false, 0, "Durable Restoring claim is required.");
        try
        {
            var mapper = world.GetExistingSystemManaged<ServerScriptMapper>();
            if (mapper == null) return new(ItemQuarantineRestoreStatus.Pending, false, 0, "Inventory manager unavailable.");
            var manager = mapper.GetServerGameManager();
            // Target the verified inventory itself, so an inventory route cannot select another owner.
            var response = manager.TryAddInventoryItem(identity!.Bag, new PrefabGUID(snapshot!.Prefab), snapshot.Amount);
            int observed = Count(world.EntityManager, identity.Bag, snapshot.Prefab);
            if (response.RemainingAmount == 0 && observed == snapshot.Amount)
                return new(ItemQuarantineRestoreStatus.Restored, true, observed, "Stack added and quantity read back in the running world.");
            return new(ItemQuarantineRestoreStatus.Ambiguous, true, Math.Max(0, observed), "Native add did not restore the complete stack; automatic retry blocked.");
        }
        catch (Exception e) { return new(ItemQuarantineRestoreStatus.Ambiguous, true, 0, "Native restoration outcome uncertain: " + e.Message); }
    }

    private static ItemQuarantineRestoreOutcome Prepare(World world, ItemQuarantineRecord record,
        out Incarnation? identity, out Snapshot? snapshot)
    {
        identity = null; snapshot = null;
        ItemQuarantineRestoreOutcome Result(ItemQuarantineRestoreStatus status, string reason) => new(status, false, 0, reason);
        try
        {
            if (!Plugin.IsServerProcess || world == null || !world.IsCreated)
                return Result(ItemQuarantineRestoreStatus.Pending, "Server world unavailable.");
            snapshot = JsonSerializer.Deserialize<Snapshot>(record.SnapshotJson);
            if (snapshot == null || snapshot.Schema != 2 || snapshot.SourceGuid == 0 || snapshot.Prefab != record.Guid || snapshot.Amount <= 0
                || snapshot.Amount > 4095 || snapshot.MaxAmountOverride != 0 || record.Destination.Kind != "player-bag"
                || snapshot.Incarnation != record.Destination.OwnerId)
                return Result(ItemQuarantineRestoreStatus.Unavailable, "Unsupported or inconsistent snapshot.");
            PrefabDefinition? definition = null;
            foreach (var candidate in PrefabRegistry.Definitions)
                if (candidate.Guid.GuidHash == record.Guid)
                {
                    if (definition != null) return Result(ItemQuarantineRestoreStatus.Unavailable, "Item definition is ambiguous.");
                    definition = candidate;
                }
            if (definition == null) return Result(ItemQuarantineRestoreStatus.Pending, "Original item definition is not registered.");
            if (definition.Kind != PrefabKind.Item || definition.Source.GuidHash != snapshot.SourceGuid)
                return Result(ItemQuarantineRestoreStatus.Unavailable, "Registered item donor or kind changed; snapshot retained.");
            if (!TryStackMetadata(world, definition.Guid, out var metadata))
                return Result(ItemQuarantineRestoreStatus.Unavailable, "Registered item is no longer a verified plain stack.");
            if (metadata.MaxAmount < snapshot.Amount || definition.MaxAmount > 0 && definition.MaxAmount < snapshot.Amount)
                return Result(ItemQuarantineRestoreStatus.Pending, "Registered stack capacity is smaller than the retained stack.");
            if (!Incarnations.TryGetValue(snapshot.Incarnation, out identity) || !ReferenceEquals(identity.World, world)
                || identity.Epoch != WorldEpoch.Current)
                return Result(ItemQuarantineRestoreStatus.Unavailable, "Player incarnation cannot be verified after world load or Core replacement; snapshot retained.");
            var em = world.EntityManager;
            if (!Has<PlayerCharacter>(em, identity.Character) || !TrySingleBag(em, identity.Character, out var bag) || bag != identity.Bag)
                return Result(ItemQuarantineRestoreStatus.Unavailable, "Original character or bag no longer exists.");
            var userEntity = em.GetComponentData<PlayerCharacter>(identity.Character).UserEntity;
            if (!Has<User>(em, userEntity)) return Result(ItemQuarantineRestoreStatus.Unavailable, "Original owner unavailable.");
            var user = em.GetComponentData<User>(userEntity);
            if (user.PlatformId != identity.Account || user.LocalCharacter.GetEntityOnServer() != identity.Character
                || record.Destination.PlayerId != identity.Account.ToString(CultureInfo.InvariantCulture))
                return Result(ItemQuarantineRestoreStatus.Unavailable, "Original account/character ownership changed.");
            if (!TryBuffer(em, identity.Bag, out var rows)) return Result(ItemQuarantineRestoreStatus.Pending, "Original bag unavailable.");
            bool free = false;
            for (int i = 0; i < rows.Length; i++)
            {
                var row = rows[i];
                if (row.Amount > 0 && row.ItemType.GuidHash == snapshot.Prefab)
                    return Result(ItemQuarantineRestoreStatus.Pending, "Existing same-prefab stack prevents unambiguous restoration; snapshot retained.");
                if (row.Amount == 0 && row.ItemEntity.GetEntityOnServer() == Entity.Null) free = true;
            }
            return free ? Result(ItemQuarantineRestoreStatus.Ready, "Original bag and owner verified.")
                : Result(ItemQuarantineRestoreStatus.Pending, "Original bag is full; snapshot retained.");
        }
        catch (Exception e) { return Result(ItemQuarantineRestoreStatus.Unavailable, "Restoration preflight refused: " + e.Message); }
    }

    private static int Count(EntityManager em, Entity bag, int guid)
    {
        if (!TryBuffer(em, bag, out var rows)) throw new InvalidOperationException("Restored bag readback unavailable.");
        int count = 0;
        for (int i = 0; i < rows.Length; i++)
            if (rows[i].ItemType.GuidHash == guid && rows[i].Amount > 0)
            {
                if (rows[i].ItemEntity.GetEntityOnServer() != Entity.Null || rows[i].MaxAmountOverride != 0)
                    throw new InvalidOperationException("Restored item properties differ from plain stack snapshot.");
                count = checked(count + rows[i].Amount);
            }
        return count;
    }

    private static bool TryStackMetadata(World world, PrefabGUID guid, out ItemData metadata)
    {
        metadata = default;
        if (!PrefabLookup.TryGetEntity(world, guid, out var prefab) || !Has<ItemData>(world.EntityManager, prefab)) return false;
        metadata = world.EntityManager.GetComponentData<ItemData>(prefab);
        if (metadata.ItemType != ItemType.Stackable || metadata.ItemTypeGUID != guid || metadata.MaxAmount <= 0) return false;
        // The helper consumes the server lookup, not just the prefab component. Both must agree.
        var gameData = world.GetExistingSystemManaged<GameDataSystem>();
        return gameData != null && gameData.ItemHashLookupMap.TryGetValue(guid, out var lookup)
            && lookup.Entity == prefab && lookup.ItemType == ItemType.Stackable && lookup.ItemTypeGUID == guid
            && lookup.MaxAmount == metadata.MaxAmount;
    }

    private static bool Same(InventoryBuffer a, InventoryBuffer b) => a.ItemType == b.ItemType && a.Amount == b.Amount
        && a.MaxAmountOverride == b.MaxAmountOverride && a.ItemEntity.GetEntityOnServer() == b.ItemEntity.GetEntityOnServer();
    private static bool Refuse(string message, out string reason) { reason = message; return false; }
    private static bool Has<T>(EntityManager em, Entity entity) => em.Exists(entity)
        && em.HasComponent(entity, ComponentIndex.Of<T>(ComponentType.AccessMode.ReadOnly));
    private static bool TryBuffer(EntityManager em, Entity entity, out DynamicBuffer<InventoryBuffer> rows)
    {
        rows = default;
        if (!Has<InventoryBuffer>(em, entity)) return false;
        rows = em.GetBuffer<InventoryBuffer>(entity);
        return rows.Length > 0 && rows.Length <= 1024;
    }

    // Same qualified 48-byte InventoryInstanceElement stride and +16 external entity offset
    // as Items.CopyBags. Reject multiple inventories instead of guessing which bag to restore.
    private static unsafe bool TrySingleBag(EntityManager em, Entity character, out Entity bag)
    {
        bag = Entity.Null;
        if (!Has<InventoryInstanceElement>(em, character)) return false;
        var index = ComponentIndex.IndexOf<InventoryInstanceElement>();
        int length = em.GetBufferLength(character, index);
        if (length != 1) return false;
        byte* raw = (byte*)em.GetBufferRawRO(character, index);
        if (raw == null) return false;
        bag = new Entity { Index = *(int*)(raw + 16), Version = *(int*)(raw + 20) };
        return Has<InventoryBuffer>(em, bag);
    }
}
