#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using ProjectM;
using ProjectM.CastleBuilding;
using ProjectM.Shared;
using Progenitor.Castles;
using Progenitor.Ecs;
using Progenitor.Inventory;
using Unity.Entities;
using Unity.Transforms;

namespace Progenitor.Materials;

/// <summary>
/// The running server behind <see cref="MaterialService"/>: players, plots, container ids and
/// inventories through Core's existing readers (<c>ctx.Inventory</c>'s engine, the container id
/// book, the castle handles). Main thread only.
/// </summary>
internal sealed class UnityMaterialWorld : IMaterialWorld, IMaterialLedger<InventoryRef>
{
    private const long CastleRefreshMs = 10_000;

    private IReadOnlyList<ICastleHandle> _castles = Array.Empty<ICastleHandle>();
    private long _castlesStamp;
    private int _castlesEpoch = int.MinValue;

    /// <summary>
    /// The castle heart the game itself resolved for the current seam call (its standing-position
    /// rule), or <see cref="Entity.Null"/> to resolve from the character's position instead.
    /// </summary>
    internal Entity HeartHint { get; set; }

    public int Epoch => Progenitor.WorldState.WorldEpoch.Current;

    public IMaterialLedger<InventoryRef> Ledger => this;

    private static EntityManager Em => Worlds.Server!.EntityManager;

    public bool TryContext(CastleId character, MaterialOperation operation, out MaterialContext context)
    {
        context = default;
        var world = Worlds.Server;
        if (world == null || !world.IsCreated || !InventoryRuntime.Installed) return false;
        var em = world.EntityManager;
        var ch = new Entity { Index = character.Index, Version = character.Version };
        if (!ch.Exists(em) || !ch.Has<PlayerCharacter>(em)) return false;
        Entity user;
        try { user = em.GetComponentData<PlayerCharacter>(ch).UserEntity; }
        catch { return false; }
        if (user == Entity.Null || !user.Exists(em)) return false;

        int territory = StandingTerritory(em, ch);
        var station = ContainerId.None;
        if (operation == MaterialOperation.Craft)
        {
            var s = StationOf(em, ch);
            if (s != Entity.Null) station = InventoryRuntime.IdOf(s, out _);
        }
        context = new MaterialContext(CastleId.From(user), character, Epoch, operation, territory, station);
        return true;
    }

    public bool TryResolve(ContainerId id, out InventoryRef inventory)
    {
        inventory = InventoryRef.None;
        if (id.IsNone || !InventoryRuntime.TryFind(id, out var owner, out _)) return false;
        inventory = InventoryRef.For(owner, 0);
        return !inventory.IsNone;
    }

    public ContainerId IdOf(InventoryRef inventory) =>
        inventory.IsNone ? ContainerId.None : InventoryRuntime.IdOf(inventory.Owner, out _);

    public void VanillaCounted(in MaterialContext context, ICollection<InventoryRef> into)
    {
        var ch = new Entity { Index = context.Character.Index, Version = context.Character.Version };
        into.Add(InventoryRef.For(ch, 0));
        var station = StationOf(Em, ch);
        if (station != Entity.Null && !station.Has<CastleSharedInventory>(Em)) into.Add(InventoryRef.For(station, 0));
    }

    public InventoryRef PoolFront(in MaterialContext context)
    {
        if (context.Operation != MaterialOperation.Craft) return InventoryRef.None;
        var ch = new Entity { Index = context.Character.Index, Version = context.Character.Version };
        var station = StationOf(Em, ch);
        return station != Entity.Null && station.Has<CastleSharedInventory>(Em) ? InventoryRef.For(station, 0) : InventoryRef.None;
    }

    /// <summary>
    /// Not read in this spike: reading the heart's aggregate rows needs a buffer accessor this module
    /// does not have yet. Items a provider leaves to the game therefore show bag-only counts in the
    /// client view; the server still checks and pays them by the game's own rules.
    /// </summary>
    public void VanillaShared(in MaterialContext context, IDictionary<int, long> into) { }

    public void Items(InventoryRef inventory, ICollection<int> into)
    {
        var snap = InventoryRuntime.Engine.Read(inventory);
        if (!snap.Ok) return;
        foreach (var slot in snap.Slots)
            if (!slot.IsEmpty && !slot.HasItemEntity && slot.Amount > 0) into.Add(slot.Item);
    }

    public int Count(InventoryRef source, int item)
    {
        var snap = InventoryRuntime.Engine.Read(source);
        if (!snap.Ok) return 0;
        long n = 0;
        foreach (var slot in snap.Slots)
            if (slot.Item == item && !slot.HasItemEntity && slot.Amount > 0) n += slot.Amount;
        return n > int.MaxValue ? int.MaxValue : (int)n;
    }

    public int Remove(InventoryRef source, int item, int amount)
    {
        var r = InventoryRuntime.Engine.TryRemove(source, item, amount);
        return r.Done;
    }

    public int Add(InventoryRef source, int item, int amount)
    {
        var r = InventoryRuntime.Engine.TryAdd(source, item, amount);
        return r.Done;
    }

    /// <summary>
    /// The workstation the character is working at: its interaction target when that target is a castle
    /// workstation. Unverified live that this is the station the craft event names; the payment never
    /// relies on it beyond ordering the pool and leaving the station out of it.
    /// </summary>
    internal static Entity StationOf(EntityManager em, Entity character)
    {
        if (!character.TryRead(em, out Interactor interactor)) return Entity.Null;
        var target = interactor.Target;
        if (target == Entity.Null || !target.Exists(em) || !target.Has<CastleWorkstation>(em)) return Entity.Null;
        return target;
    }

    private int StandingTerritory(EntityManager em, Entity character)
    {
        RefreshCastles();
        var hint = HeartHint;
        if (hint != Entity.Null)
        {
            var key = CastleId.From(hint);
            for (int i = 0; i < _castles.Count; i++)
                if (_castles[i].Heart.Equals(key)) return _castles[i].Territory.Index;
        }
        if (!character.TryRead(em, out Translation t)) return -1;
        CastleVec3 pos = t.Value;
        for (int i = 0; i < _castles.Count; i++)
            if (_castles[i].IsInside(pos)) return _castles[i].Territory.Index;
        return -1;
    }

    private void RefreshCastles()
    {
        long now = Environment.TickCount64;
        int epoch = Epoch;
        if (epoch == _castlesEpoch && now - _castlesStamp < CastleRefreshMs) return;
        _castles = Castles.Castles.All;
        _castlesEpoch = epoch;
        _castlesStamp = now;
    }
}
#endif
