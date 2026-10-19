using System;
using System.Collections.Generic;
using ProjectM;
using Progenitor.Ai;
using Progenitor.Ecs;
using Progenitor.Inventory;
using Unity.Collections;
using Unity.Entities;
using Unity.Transforms;

namespace Progenitor.Drops;

/// <summary>
/// The server reads and the one write behind <c>ctx.Drops</c>: item piles on the ground (entities
/// with <c>ItemPickup</c>), listed by distance, and a take that moves units into an inventory and
/// shrinks or removes the pile.
///
/// <para><b>Reads.</b> <c>ItemPickup.ItemId</c> and <c>ItemAmount</c> raw at the layout constants;
/// the position from <c>Translation</c>, else <c>LocalToWorld</c>; <c>Disabled</c> and
/// <c>DestroyTag</c> by presence; the pile's own item entity from
/// <c>DropItemThrowSystem.DropItem.ItemEntity</c> when the pile carries that component and it names a
/// live entity. The stack size comes from the server item table.</para>
///
/// <para><b>The take.</b> <c>ctx.Inventory.TryAdd</c> first; the pile shrinks only by what the
/// inventory read back as added. A pile left with units gets its new <c>ItemAmount</c> through the
/// component overlay (<c>GetComponentDataRawRW</c>, the amount field alone). An emptied pile gets
/// amount 0 first, so nothing can take it twice, then the replicated removal
/// (<see cref="BuffSweep.DestroyUnitReplicated"/>, the game's own destroy). Everything on the main
/// thread in one call, so no other mod call and no game system runs in between.</para>
/// </summary>
internal static unsafe class GroundPiles
{
    private static readonly WorldBoundQuery PileQuery = new("Drops.ItemPickup");

    internal static void Release() => PileQuery.Release();

    /// <summary>Every pile within <paramref name="radius"/> of the point, nearest first, at most <paramref name="max"/>.</summary>
    internal static int Near(float x, float y, float z, float radius, int max, List<GroundPile> into)
    {
        into.Clear();
        var world = Worlds.Server;
        if (world == null || !world.IsCreated) return 0;
        var em = world.EntityManager;
        float r = GroundPileSelect.ClampRadius(radius);
        float r2 = r * r;
        int cap = GroundPileSelect.ClampMax(max);
        int epoch = Progenitor.WorldState.WorldEpoch.Current;
        if (!PileQuery.TryGet(world, out var query))
        {
            // Asleep piles too: an area nobody is near keeps its loot disabled.
            query = Query.Build(em, includeDisabled: true, includeSystems: false,
                ComponentIndex.Of<ItemPickup>(ComponentType.AccessMode.ReadOnly));
            PileQuery.Store(world, query);
        }

        var array = query.ToEntityArray(Allocator.Temp);
        try
        {
            for (int i = 0; i < array.Length; i++)
            {
                var e = array[i];
                // Position first: most piles are far away, and that read is the cheapest filter.
                if (!TryPosition(em, e, out float px, out float py, out float pz)) continue;
                if (!GroundPileSelect.Within(x, y, z, r2, px, py, pz, out float d2)) continue;
                if (TryRead(em, e, epoch, px, py, pz, d2, out var pile, out _)) into.Add(pile);
            }
        }
        finally
        {
            array.Dispose();
        }

        GroundPileSelect.NearestFirst(into, cap);
        return into.Count;
    }

    /// <summary>The pile as it is now; false with a reason when it is stale, gone, being removed or empty.</summary>
    internal static bool TryGet(GroundPileId id, out GroundPile pile, out string reason)
    {
        pile = default;
        int epoch = Progenitor.WorldState.WorldEpoch.Current;
        if (id.IsNone) { reason = GroundTakeRules.NoPile; return false; }
        if (id.IsStaleIn(epoch)) { reason = GroundTakeRules.StalePile; return false; }
        var world = Worlds.Server;
        if (world == null || !world.IsCreated) { reason = "no server world"; return false; }
        var em = world.EntityManager;
        var e = id.Entity;
        if (!TryPosition(em, e, out float x, out float y, out float z)) { reason = GroundTakeRules.Gone; return false; }
        return TryRead(em, e, epoch, x, y, z, 0, out pile, out reason);
    }

    private static bool TryRead(EntityManager em, Entity e, int epoch, float x, float y, float z, float d2, out GroundPile pile, out string reason)
    {
        pile = default;
        if (!e.Exists(em)) { reason = GroundTakeRules.Gone; return false; }
        if (e.Has<DestroyTag>(em)) { reason = GroundTakeRules.Removing; return false; }
        if (!TryItem(em, e, out int item, out int amount)) { reason = GroundTakeRules.Gone; return false; }
        if (item == 0 || amount <= 0) { reason = GroundTakeRules.Empty; return false; }
        int maxStack = InventoryRuntime.TryItemInfo(item, out var info) ? info.MaxAmount : 0;
        var own = ItemEntityOf(em, e);
        pile = new GroundPile(new GroundPileId(e.Index, e.Version, epoch), item, amount, x, y, z, d2,
            e.Has<Disabled>(em), maxStack, own.Index, own.Version);
        reason = "";
        return true;
    }

    /// <summary><c>ItemPickup.ItemId</c> and <c>ItemAmount</c>, raw at the layout constants.</summary>
    private static bool TryItem(EntityManager em, Entity e, out int item, out int amount)
    {
        item = amount = 0;
        byte* p = AiRuntime.RO<ItemPickup>(em, e, Math.Max(Layout.ItemPickupItemIdOffset, Layout.ItemPickupItemAmountOffset) + sizeof(int));
        if (p == null) return false;
        item = *(int*)(p + Layout.ItemPickupItemIdOffset);
        amount = *(int*)(p + Layout.ItemPickupItemAmountOffset);
        return true;
    }

    /// <summary>The live item entity the pile's throw component names, or <c>Entity.Null</c>.</summary>
    private static Entity ItemEntityOf(EntityManager em, Entity e)
    {
        byte* p = AiRuntime.RO<DropItemThrowSystem.DropItem>(em, e, Layout.DropItemThrowDropItemItemEntityOffset + Layout.EntityStride);
        if (p == null) return Entity.Null;
        var named = AiRuntime.ReadEntity(p + Layout.DropItemThrowDropItemItemEntityOffset);
        return named != Entity.Null && named.Exists(em) ? named : Entity.Null;
    }

    /// <summary><c>Translation</c>, else <c>LocalToWorld</c>.</summary>
    internal static bool TryPosition(EntityManager em, Entity e, out float x, out float y, out float z)
    {
        x = y = z = 0;
        if (e.TryRead(em, out Translation t))
        {
            x = t.Value.x;
            y = t.Value.y;
            z = t.Value.z;
            return true;
        }

        if (e.TryRead(em, out LocalToWorld l))
        {
            var pos = l.Position;
            x = pos.x;
            y = pos.y;
            z = pos.z;
            return true;
        }

        return false;
    }

    /// <summary>Move up to <paramref name="amount"/> from the pile into <paramref name="into"/>.</summary>
    internal static GroundTakeResult TakeInto(GroundPileId id, InventoryRef into, int amount)
    {
        int epoch = Progenitor.WorldState.WorldEpoch.Current;
        var world = Worlds.Server;
        if (world == null || !world.IsCreated) return GroundTakeResult.Refuse(id, 0, amount, 0, "no server world");
        var em = world.EntityManager;
        var e = id.IsNone ? Entity.Null : id.Entity;
        bool stale = id.IsStaleIn(epoch);
        bool exists = !id.IsNone && !stale && e.Exists(em);
        int item = 0, have = 0;
        bool removing = false, own = false, known = false;
        int maxStack = 0;
        if (exists)
        {
            removing = e.Has<DestroyTag>(em);
            if (!TryItem(em, e, out item, out have)) exists = false;
            else
            {
                own = ItemEntityOf(em, e) != Entity.Null;
                known = InventoryRuntime.TryItemInfo(item, out var info);
                maxStack = known ? info.MaxAmount : 0;
            }
        }

        string? refusal = GroundTakeRules.Refusal(id.IsNone, stale, into.IsNone, amount, exists, removing, have, own, known, maxStack);
        if (refusal != null) return GroundTakeResult.Refuse(id, item, amount, have, refusal);

        int ask = GroundTakeRules.Ask(amount, have);
        var add = InventoryRuntime.Engine.TryAdd(into, item, ask);
        if (add.Outcome == InventoryOutcome.Inconsistent)
        {
            // The inventory could not be read back: the units may be in it already. The pile is not touched.
            return new GroundTakeResult
            {
                Outcome = InventoryOutcome.Inconsistent, Pile = id, Item = item, Requested = amount, PileBefore = have, PileAfter = have,
                Reason = "the inventory could not be read back after the add (" + add.Reason + "); the pile was not changed", Add = add,
            };
        }

        if (add.Outcome == InventoryOutcome.Refused)
            return new GroundTakeResult
            {
                Outcome = InventoryOutcome.Refused, Pile = id, Item = item, Requested = amount, PileBefore = have, PileAfter = have,
                Reason = add.Reason, Add = add,
            };

        var (left, remove, done) = GroundTakeRules.After(have, ask, add.Done);
        if (done <= 0)
            return new GroundTakeResult
            {
                Outcome = InventoryOutcome.Nothing, Pile = id, Item = item, Requested = amount, PileBefore = have, PileAfter = have,
                Reason = add.Reason.Length > 0 ? add.Reason : "the inventory took none", Add = add,
            };

        string reason = done < amount ? (done < have ? add.Reason : "the pile held " + have) : "";
        try
        {
            byte* p = AiRuntime.RW<ItemPickup>(em, e, Layout.ItemPickupItemAmountOffset + sizeof(int));
            *(int*)(p + Layout.ItemPickupItemAmountOffset) = left;
        }
        catch (Exception ex)
        {
            // The units are in the inventory and the pile still shows them: say so loudly, once per call.
            Plugin.Logger?.LogError("Drops: TakeInto added " + done + " of item " + item + " but could not shrink pile " + id + ": " + ex.Message);
            return new GroundTakeResult
            {
                Outcome = InventoryOutcome.Inconsistent, Pile = id, Item = item, Requested = amount, Done = done, PileBefore = have, PileAfter = have,
                Reason = "added to the inventory but the pile could not be written: " + ex.Message, Add = add,
            };
        }

        if (remove)
        {
            try { BuffSweep.DestroyUnitReplicated(em, e, DestroyDebugReason.None, "Drops: pile taken"); }
            catch (Exception ex) { Plugin.Logger?.LogWarning("Drops: pile " + id + " is empty but its removal threw: " + ex.Message); }
        }

        return new GroundTakeResult
        {
            Outcome = GroundTakeRules.Outcome(amount, done), Pile = id, Item = item, Requested = amount, Done = done,
            PileBefore = have, PileAfter = left, PileRemoved = remove, Reason = reason, Add = add,
        };
    }
}

/// <summary>
/// <c>RootPrefabCollection</c>'s drop prefabs, read raw at the layout constants of this side's
/// metadata from the singleton entity, cached per world and world epoch. Never written.
/// </summary>
internal static unsafe class DropPrefabsReader
{
    private static World? _world;
    private static int _epoch = int.MinValue;
    private static DropPrefabs _cached;
    private static bool _have;
    private static long _retryAt;

    internal static void Clear()
    {
        _world = null;
        _epoch = int.MinValue;
        _cached = default;
        _have = false;
        _retryAt = 0;
    }

    internal static bool TryRead(World? world, out DropPrefabs prefabs, out string reason)
    {
        prefabs = default;
        if (world == null || !world.IsCreated) { reason = "no world"; return false; }
        int epoch = Progenitor.WorldState.WorldEpoch.Current;
        if (_have && ReferenceEquals(world, _world) && epoch == _epoch)
        {
            prefabs = _cached;
            reason = "";
            return true;
        }

        long now = Environment.TickCount64;
        if (ReferenceEquals(world, _world) && epoch == _epoch && now < _retryAt)
        {
            reason = "the prefab table was not readable a moment ago; retried at most once a second";
            return false;
        }

        _world = world;
        _epoch = epoch;
        _have = false;
        _retryAt = now + 1_000;
        try
        {
            var em = world.EntityManager;
            var roots = Query.Entities(em, includeDisabled: true, includeSystems: true,
                ComponentIndex.Of<RootPrefabCollection>(ComponentType.AccessMode.ReadOnly));
            if (roots.Count == 0) { reason = "no RootPrefabCollection entity in this world yet"; return false; }
            bool server = Plugin.IsServerProcess && ReferenceEquals(world, Worlds.Server);
            int dropItem = server ? Layout.RootPrefabDropItemOffset : Layout.ClientRootPrefabDropItemOffset;
            int death = server ? Layout.RootPrefabDeathContainerOffset : Layout.ClientRootPrefabDeathContainerOffset;
            int anyone = server ? Layout.RootPrefabPlayerDeathContainerAnyoneOffset : Layout.ClientRootPrefabPlayerDeathContainerAnyoneOffset;
            int clan = server ? Layout.RootPrefabPlayerDeathContainerClanMembersOffset : Layout.ClientRootPrefabPlayerDeathContainerClanMembersOffset;
            int self = server ? Layout.RootPrefabPlayerDeathContainerOnlySelfOffset : Layout.ClientRootPrefabPlayerDeathContainerOnlySelfOffset;
            int drop = server ? Layout.RootPrefabPlayerInventoryDropContainerOffset : Layout.ClientRootPrefabPlayerInventoryDropContainerOffset;
            int need = Math.Max(Math.Max(Math.Max(dropItem, death), Math.Max(anyone, clan)), Math.Max(self, drop)) + sizeof(int);
            byte* p = AiRuntime.RO<RootPrefabCollection>(em, roots[0], need);
            if (p == null) { reason = "RootPrefabCollection is not readable (absent, or smaller than " + need + " bytes)"; return false; }
            var read = new DropPrefabs(*(int*)(p + dropItem), *(int*)(p + death), *(int*)(p + anyone), *(int*)(p + clan),
                *(int*)(p + self), *(int*)(p + drop));
            if (!read.IsComplete) { reason = "the prefab table is not filled in yet (" + read + ")"; return false; }
            _cached = read;
            _have = true;
            prefabs = read;
            reason = "";
            return true;
        }
        catch (Exception e)
        {
            reason = "reading RootPrefabCollection threw: " + e.Message;
            return false;
        }
    }
}
