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
/// live entity; the pile's own inventory (<c>InventoryBuffer</c> on the pile, or its
/// <c>InventoryInstanceElement</c> row 0) through <c>ctx.Inventory</c>'s reader. The stack size and whether the item has an instance entity (<c>ItemData.Entity</c>
/// set, the field the game's inventory add checks) come from the server item table row.</para>
///
/// <para><b>The take.</b> A pile with its own inventory: <c>ctx.Inventory.Transfer</c> out of it
/// (the game's slot move, which carries the item entity along), then <c>ItemAmount</c> set to what
/// the pile's inventory still holds of the item. This is what the game's pickup
/// (<c>ItemPickupSystem</c>) does: <c>TryGetInventory</c> on the pile, <c>TryMoveItem</c> per slot,
/// <c>GetItemAmount</c>, then <c>SetComponentData&lt;ItemPickup&gt;</c>. A pile without one (stackable,
/// not unique items only): <c>ctx.Inventory.TryAdd</c> first, and the pile shrinks only by what the
/// inventory read back as added. <c>ItemAmount</c> is written through the component overlay
/// (<c>GetComponentDataRawRW</c>, the amount field alone). An emptied pile gets amount 0 first, so
/// nothing can take it twice, then the replicated removal (<see cref="BuffSweep.DestroyUnitReplicated"/>,
/// the game's own destroy). Everything on the main thread in one call, so no other mod call and no
/// game system runs in between.</para>
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
        bool known = InventoryRuntime.TryItemInfo(item, out var info);
        int maxStack = known ? info.MaxAmount : 0;
        var own = ItemEntityOf(em, e);
        var contents = ReadContents(e, item);
        pile = new GroundPile(new GroundPileId(e.Index, e.Version, epoch), item, amount, x, y, z, d2,
            e.Has<Disabled>(em), maxStack, own.Index, own.Version, InFlight(em, e), known && info.HasInstanceEntity,
            contents.HasInventory, contents.SlotHasItemEntity);
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

    /// <summary>
    /// The game's throw still runs on the pile: it carries <c>DropItemThrowSystem.DropItem</c> (with its
    /// own <c>Amount</c>, which a take would not update) or <c>SpellMovement</c>.
    /// </summary>
    private static bool InFlight(EntityManager em, Entity e) =>
        e.Has<DropItemThrowSystem.DropItem>(em) || e.Has<SpellMovement>(em);

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
        bool removing = false, known = false, flying = false;
        var route = GroundTakeRoute.None;
        var contents = default(PileContents);
        if (exists)
        {
            removing = e.Has<DestroyTag>(em);
            if (!TryItem(em, e, out item, out have)) exists = false;
            else
            {
                flying = InFlight(em, e);
                known = InventoryRuntime.TryItemInfo(item, out var info);
                contents = ReadContents(e, item);
                bool unique = GroundTakeRules.IsUnique(ItemEntityOf(em, e) != Entity.Null, contents.SlotHasItemEntity, known && info.HasInstanceEntity);
                route = GroundTakeRules.Route(contents.HasInventory, unique, known ? info.MaxAmount : 0);
            }
        }

        string? refusal = GroundTakeRules.Refusal(id.IsNone, stale, into.IsNone, amount, exists, removing, have, flying, known, route);
        if (refusal != null) return GroundTakeResult.Refuse(id, item, amount, have, refusal);
        return route == GroundTakeRoute.PileInventory
            ? TakeFromPileInventory(em, e, id, into, item, amount, have, contents.Units)
            : TakeAsCopy(em, e, id, into, item, amount, have);
    }

    /// <summary>
    /// The game's pickup, done for a mod: the game's slot move out of the pile's own inventory
    /// (<c>ctx.Inventory.Transfer</c>, which moves the slot with its item entity), then
    /// <c>ItemPickup.ItemAmount</c> set to what the pile's inventory still holds of the item, and the
    /// replicated removal once nothing is left.
    /// </summary>
    private static GroundTakeResult TakeFromPileInventory(EntityManager em, Entity e, GroundPileId id, InventoryRef into, int item,
        int amount, int have, int units)
    {
        const GroundTakeRoute route = GroundTakeRoute.PileInventory;
        if (units <= 0)
            return Refused(id, item, amount, have, GroundTakeRules.NotInPileInventory, route);
        var source = InventoryRef.For(e);
        int ask = GroundTakeRules.Ask(amount, units);
        var move = InventoryRuntime.Engine.Transfer(source, into, item, ask);
        if (move.Outcome == InventoryOutcome.Inconsistent)
            return new GroundTakeResult
            {
                Route = route, Outcome = InventoryOutcome.Inconsistent, Pile = id, Item = item, Requested = amount, Done = move.Done,
                PileBefore = have, PileAfter = have, Add = move,
                Reason = "the move out of the pile's inventory could not be read back (" + move.Reason + "); the pile was not changed",
            };

        if (move.Outcome == InventoryOutcome.Refused)
            return new GroundTakeResult
            {
                Route = route, Outcome = InventoryOutcome.Refused, Pile = id, Item = item, Requested = amount,
                PileBefore = have, PileAfter = have, Reason = move.Reason, Add = move,
            };

        int done = Math.Max(0, move.Done);
        if (done <= 0)
            return new GroundTakeResult
            {
                Route = route, Outcome = InventoryOutcome.Nothing, Pile = id, Item = item, Requested = amount,
                PileBefore = have, PileAfter = have, Reason = move.Reason.Length > 0 ? move.Reason : "the inventory took none", Add = move,
            };

        // What the pile's inventory still holds becomes ItemAmount, as the game's pickup writes it.
        int left = InventoryRuntime.Engine.TryCount(source, item, out int n, out _) ? n : Math.Max(0, units - done);
        var (newAmount, remove) = GroundTakeRules.AfterMove(done, left);
        string reason = done < amount ? (done < units ? move.Reason : "the pile held " + units) : "";
        if (!TryWriteAmount(em, e, id, item, newAmount, done, out string writeError))
            return new GroundTakeResult
            {
                Route = route, Outcome = InventoryOutcome.Inconsistent, Pile = id, Item = item, Requested = amount, Done = done,
                PileBefore = have, PileAfter = have, Reason = "moved into the inventory but the pile could not be written: " + writeError, Add = move,
            };

        if (remove) Destroy(em, e, id);
        return new GroundTakeResult
        {
            Route = route, Outcome = GroundTakeRules.Outcome(amount, done), Pile = id, Item = item, Requested = amount, Done = done,
            PileBefore = have, PileAfter = newAmount, PileRemoved = remove, Reason = reason, Add = move,
        };
    }

    /// <summary>A pile with no inventory of its own (stackable, not unique): a server add, then the pile shrinks by what was added.</summary>
    private static GroundTakeResult TakeAsCopy(EntityManager em, Entity e, GroundPileId id, InventoryRef into, int item, int amount, int have)
    {
        const GroundTakeRoute route = GroundTakeRoute.Copy;
        int ask = GroundTakeRules.Ask(amount, have);
        var add = InventoryRuntime.Engine.TryAdd(into, item, ask);
        if (add.Outcome == InventoryOutcome.Inconsistent)
        {
            // The inventory could not be read back: the units may be in it already. The pile is not touched.
            return new GroundTakeResult
            {
                Route = route, Outcome = InventoryOutcome.Inconsistent, Pile = id, Item = item, Requested = amount, PileBefore = have, PileAfter = have,
                Reason = "the inventory could not be read back after the add (" + add.Reason + "); the pile was not changed", Add = add,
            };
        }

        if (add.Outcome == InventoryOutcome.Refused)
            return new GroundTakeResult
            {
                Route = route, Outcome = InventoryOutcome.Refused, Pile = id, Item = item, Requested = amount, PileBefore = have, PileAfter = have,
                Reason = add.Reason, Add = add,
            };

        var (left, remove, done) = GroundTakeRules.After(have, ask, add.Done);
        if (done <= 0)
            return new GroundTakeResult
            {
                Route = route, Outcome = InventoryOutcome.Nothing, Pile = id, Item = item, Requested = amount, PileBefore = have, PileAfter = have,
                Reason = add.Reason.Length > 0 ? add.Reason : "the inventory took none", Add = add,
            };

        string reason = done < amount ? (done < have ? add.Reason : "the pile held " + have) : "";
        if (!TryWriteAmount(em, e, id, item, left, done, out string writeError))
            return new GroundTakeResult
            {
                Route = route, Outcome = InventoryOutcome.Inconsistent, Pile = id, Item = item, Requested = amount, Done = done, PileBefore = have, PileAfter = have,
                Reason = "added to the inventory but the pile could not be written: " + writeError, Add = add,
            };

        if (remove) Destroy(em, e, id);
        return new GroundTakeResult
        {
            Route = route, Outcome = GroundTakeRules.Outcome(amount, done), Pile = id, Item = item, Requested = amount, Done = done,
            PileBefore = have, PileAfter = left, PileRemoved = remove, Reason = reason, Add = add,
        };
    }

    private static GroundTakeResult Refused(GroundPileId id, int item, int amount, int have, string reason, GroundTakeRoute route) =>
        new()
        {
            Route = route, Outcome = InventoryOutcome.Refused, Pile = id, Item = item, Requested = amount,
            PileBefore = have, PileAfter = have, Reason = reason,
        };

    /// <summary><c>ItemPickup.ItemAmount</c> through the component overlay, the amount field alone. Logs loudly on failure.</summary>
    private static bool TryWriteAmount(EntityManager em, Entity e, GroundPileId id, int item, int value, int done, out string error)
    {
        try
        {
            byte* p = AiRuntime.RW<ItemPickup>(em, e, Layout.ItemPickupItemAmountOffset + sizeof(int));
            *(int*)(p + Layout.ItemPickupItemAmountOffset) = value;
            error = "";
            return true;
        }
        catch (Exception ex)
        {
            // The units are in the inventory and the pile still shows them: say so loudly, once per call.
            Plugin.Logger?.LogError("Drops: TakeInto moved " + done + " of item " + item + " but could not write pile " + id + ": " + ex.Message);
            error = ex.Message;
            return false;
        }
    }

    /// <summary>The game's replicated destroy; a throw is logged, not raised (the pile already reads empty).</summary>
    private static void Destroy(EntityManager em, Entity e, GroundPileId id)
    {
        try { BuffSweep.DestroyUnitReplicated(em, e, DestroyDebugReason.None, "Drops: pile taken"); }
        catch (Exception ex) { Plugin.Logger?.LogWarning("Drops: pile " + id + " is empty but its removal threw: " + ex.Message); }
    }

    /// <summary>
    /// Remove an emptied pile with the game's replicated destroy. True when it was removed now or was
    /// already gone or being removed; false with a reason for a stale handle, an entity that is not a
    /// pile, or a pile that still holds units.
    /// </summary>
    internal static bool Remove(GroundPileId id, out string reason)
    {
        int epoch = Progenitor.WorldState.WorldEpoch.Current;
        var world = Worlds.Server;
        if (world == null || !world.IsCreated) { reason = "no server world"; return false; }
        var em = world.EntityManager;
        var e = id.IsNone ? Entity.Null : id.Entity;
        bool stale = id.IsStaleIn(epoch);
        bool exists = !id.IsNone && !stale && e.Exists(em);
        bool isPile = false, removing = false;
        int amount = 0, units = 0;
        if (exists)
        {
            removing = e.Has<DestroyTag>(em);
            isPile = TryItem(em, e, out int item, out amount);
            if (isPile) units = ReadContents(e, 0).AllUnits;
        }

        var (refusal, done) = GroundTakeRules.RemoveCheck(id.IsNone, stale, exists, isPile, removing, amount, units);
        if (refusal != null) { reason = refusal; return false; }
        if (done) { reason = exists ? GroundTakeRules.Removing : GroundTakeRules.Gone; return true; }
        try
        {
            BuffSweep.DestroyUnitReplicated(em, e, DestroyDebugReason.None, "Drops: pile removed");
            reason = "";
            return true;
        }
        catch (Exception ex)
        {
            reason = "the replicated removal threw: " + ex.Message;
            return false;
        }
    }

    /// <summary>What the pile's own inventory holds (read through <c>ctx.Inventory</c>'s reader).</summary>
    private readonly struct PileContents
    {
        internal PileContents(bool hasInventory, int units, int allUnits, bool slotHasItemEntity)
        {
            HasInventory = hasInventory;
            Units = units;
            AllUnits = allUnits;
            SlotHasItemEntity = slotHasItemEntity;
        }

        /// <summary>The pile entity has an inventory of its own.</summary>
        internal bool HasInventory { get; }

        /// <summary>Units of the pile's item in it.</summary>
        internal int Units { get; }

        /// <summary>Units of any item in it.</summary>
        internal int AllUnits { get; }

        /// <summary>A slot holding the pile's item names an item entity.</summary>
        internal bool SlotHasItemEntity { get; }
    }

    private static InventorySlot[] _slots = new InventorySlot[8];

    /// <summary>The pile's own inventory: present, units of <paramref name="item"/>, units in all, item entity. Main thread.</summary>
    private static PileContents ReadContents(Entity e, int item)
    {
        var engine = InventoryRuntime.Engine;
        var source = InventoryRef.For(e);
        int n = engine.ReadInto(source, _slots);
        if (n < 0) return default;
        if (n > _slots.Length)
        {
            _slots = new InventorySlot[Math.Max(n, _slots.Length * 2)];
            n = engine.ReadInto(source, _slots);
            if (n < 0) return default;
            n = Math.Min(n, _slots.Length);
        }

        int units = 0, all = 0;
        bool entity = false;
        for (int i = 0; i < n; i++)
        {
            var s = _slots[i];
            if (s.IsEmpty || s.Amount <= 0) continue;
            all += s.Amount;
            if (s.Item != item) continue;
            units += s.Amount;
            entity |= s.HasItemEntity;
        }

        return new PileContents(true, units, all, entity);
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
