using System;
using System.Collections.Generic;
using ProjectM;
using ProjectM.Network;
using Progenitor.Ecs;
using Progenitor.WorldState;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace Progenitor.Content;

/// <summary>Server-side delivery hints, never item authority. Shared world reads are amortized;
/// the requesting character's bag and equipment are read directly on each promotion.
/// The periodic ECS entity snapshot is a synchronous soft-budget operation; owner reads
/// and per-user spatial candidate reads have explicit operation caps.</summary>
internal sealed class ItemDefinitionRelevance
{
    private const int OwnerBudget = 128;
    private const int SlotLimit = 256;
    private const int PromotionBudget = 4096;
    private const float CellSize = 32;
    private const double NearbySquared = 96 * 96;
    private const double VisibleSquared = 32 * 32;
    // Same observed external-inventory layout used by Items.CopyBags: stride 48, entity at 16.
    private const int InstanceStride = 48;
    private const int InstanceEntityOffset = 16;
    private readonly Entity[] _bags = new Entity[8];
    private readonly HashSet<int> _guids = new();
    private List<Entity> _owners = new();
    private int _cursor;
    private World? _world;
    private int _epoch;
    private long _nextRefresh;
    private Dictionary<(int, int), List<Location>> _locations = new();
    private Dictionary<(int, int), List<Location>> _building = new();
    private readonly record struct Location(int Guid, Entity Owner, float3 Position, bool Visible);

    internal void Clear()
    {
        _world = null; _owners.Clear(); _guids.Clear();
        _locations.Clear(); _building.Clear(); _cursor = 0; _nextRefresh = 0;
    }

    internal void Refresh(World world, IReadOnlySet<int> customGuids)
    {
        if (!ReferenceEquals(_world, world) || _epoch != WorldEpoch.Current)
        { Clear(); _world = world; _epoch = WorldEpoch.Current; }
        long now = Environment.TickCount64;
        if (_cursor >= _owners.Count)
        {
            if (now < _nextRefresh) return;
            _guids.Clear(); foreach (int guid in customGuids) _guids.Add(guid);
            _owners.Clear(); _building.Clear(); _cursor = 0;
            if (_guids.Count == 0) { _locations.Clear(); _nextRefresh = now + 2000; return; }
            SnapshotOwners(world.EntityManager);
        }
        var manager = world.EntityManager;
        int end = Math.Min(_owners.Count, _cursor + OwnerBudget);
        for (; _cursor < end; _cursor++)
        {
            Entity owner = _owners[_cursor];
            try
            {
                if (!owner.TryRead<Translation>(manager, out var position)) continue;
                Scan(manager, owner, (guid, visible) => AddLocation(guid, owner, position.Value, visible));
            }
            catch { /* An entity can disappear between the snapshot and its turn. */ }
        }
        if (_cursor >= _owners.Count)
        {
            (_locations, _building) = (_building, _locations);
            _building.Clear(); _nextRefresh = now + 2000;
        }
    }

    private void SnapshotOwners(EntityManager em)
    {
        // One union query, restricted to positioned instances. External bag entities
        // without Translation are followed through their owner instead. Default prefab
        // exclusion is retained; disabled characters/containers remain eligible.
        var builder = new EntityQueryBuilder(Allocator.Temp);
        EntityQuery query;
        try
        {
            builder = builder.AddAll(ComponentIndex.Of<Translation>(ComponentType.AccessMode.ReadOnly));
            builder = builder.AddAny(ComponentIndex.Of<InventoryInstanceElement>(ComponentType.AccessMode.ReadOnly));
            builder = builder.AddAny(ComponentIndex.Of<InventoryBuffer>(ComponentType.AccessMode.ReadOnly));
            builder = builder.AddAny(ComponentIndex.Of<Equipment>(ComponentType.AccessMode.ReadOnly));
            builder = builder.AddAny(ComponentIndex.Of<ItemPickup>(ComponentType.AccessMode.ReadOnly));
            builder = builder.WithOptions(EntityQueryOptions.IncludeDisabledEntities);
            query = em.CreateEntityQuery(ref builder);
        }
        finally { builder.Dispose(); }
        try
        {
            var entities = query.ToEntityArray(Allocator.Temp);
            try
            {
                if (_owners.Capacity < entities.Length) _owners.Capacity = entities.Length;
                for (int i = 0; i < entities.Length; i++) _owners.Add(entities[i]);
            }
            finally { entities.Dispose(); }
        }
        finally { query.Dispose(); }
    }

    internal void Promote(World world, Entity userEntity, DefinitionPriorityQueue queue)
    {
        if (!ReferenceEquals(_world, world)) return;
        var em = world.EntityManager;
        if (!em.Exists(userEntity) || !userEntity.Has<User>(em)) return;
        Entity character = em.GetComponentData<User>(userEntity).LocalCharacter._Entity;
        if (character == Entity.Null || !em.Exists(character)) return;
        Scan(em, character, (guid, _) => queue.Enqueue(guid, DefinitionPriority.InventoryOrEquipment));
        if (!character.TryRead<Translation>(em, out var position)) return;
        var center = Cell(position.Value);
        int inspected = 0;
        // Visit the character's cell first, then outward rings, so a dense distant cell
        // cannot consume the budget before the closest candidates are considered.
        for (int ring = 0; ring <= 3; ring++)
        for (int x = -ring; x <= ring; x++)
        for (int z = -ring; z <= ring; z++)
        {
            if (Math.Max(Math.Abs(x), Math.Abs(z)) != ring) continue;
            if (!_locations.TryGetValue((center.Item1 + x, center.Item2 + z), out var rows)) continue;
            foreach (var row in rows)
            {
                if (++inspected > PromotionBudget) return;
                if (!em.Exists(row.Owner)) continue;
                double distance = math.lengthsq(row.Position - position.Value);
                if (distance > NearbySquared) continue;
                queue.Enqueue(row.Guid, row.Visible && distance <= VisibleSquared
                    ? DefinitionPriority.ActiveContainerOrVisible : DefinitionPriority.Nearby, distance);
            }
        }
    }

    private static (int, int) Cell(float3 position) =>
        ((int)Math.Floor(position.x / CellSize), (int)Math.Floor(position.z / CellSize));

    private void AddLocation(int guid, Entity owner, float3 position, bool visible)
    {
        var cell = Cell(position);
        if (!_building.TryGetValue(cell, out var rows)) _building.Add(cell, rows = new());
        rows.Add(new Location(guid, owner, position, visible));
    }

    private void Scan(EntityManager em, Entity owner, Action<int, bool> note)
    {
        var seen = new HashSet<int>();
        void Item(int guid, bool visible)
        { if (_guids.Contains(guid) && seen.Add(guid)) note(guid, visible); }
        if (owner.Has<Equipment>(em))
        {
            var e = em.GetComponentData<Equipment>(owner);
            Item(e.ArmorHeadgearSlot.SlotId.GuidHash, true); Item(e.ArmorChestSlot.SlotId.GuidHash, true);
            Item(e.ArmorGlovesSlot.SlotId.GuidHash, true); Item(e.ArmorLegsSlot.SlotId.GuidHash, true);
            Item(e.ArmorFootgearSlot.SlotId.GuidHash, true); Item(e.CloakSlot.SlotId.GuidHash, true);
            Item(e.WeaponSlot.SlotId.GuidHash, true); Item(e.GrimoireSlot.SlotId.GuidHash, true);
        }
        if (owner.TryRead<ItemPickup>(em, out var drop) && drop.ItemAmount > 0) Item(drop.ItemId.GuidHash, true);
        int count = CopyBags(em, owner);
        for (int b = 0; b < count; b++)
        {
            var buffer = em.GetBuffer<InventoryBuffer>(_bags[b], true);
            int length = Math.Min(buffer.Length, SlotLimit);
            for (int i = 0; i < length; i++)
            {
                var row = buffer[i];
                if (row.Amount > 0) Item(row.ItemType.GuidHash, false);
            }
        }
    }

    private unsafe int CopyBags(EntityManager em, Entity owner)
    {
        if (owner.Has<InventoryBuffer>(em)) { _bags[0] = owner; return 1; }
        if (!owner.Has<InventoryInstanceElement>(em)) return 0;
        var index = ComponentIndex.IndexOf<InventoryInstanceElement>();
        int length = Math.Min(em.GetBufferLength(owner, index), _bags.Length);
        byte* raw = (byte*)em.GetBufferRawRO(owner, index);
        if (raw == null) return 0;
        int count = 0;
        for (int i = 0; i < length; i++)
        {
            byte* row = raw + i * InstanceStride + InstanceEntityOffset;
            var bag = new Entity { Index = *(int*)row, Version = *(int*)(row + 4) };
            if (bag != Entity.Null && em.Exists(bag) && bag.Has<InventoryBuffer>(em)) _bags[count++] = bag;
        }
        return count;
    }
}
