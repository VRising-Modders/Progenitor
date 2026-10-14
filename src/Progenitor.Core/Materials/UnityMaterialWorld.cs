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
        var s = StationFor(em, ch, operation, territory);
        if (s != Entity.Null) station = InventoryRuntime.IdOf(s, out _);
        context = new MaterialContext(CastleId.From(user), character, Epoch, operation, territory, station);
        return true;
    }

    /// <summary>
    /// The structure an operation runs at: the workstation for Craft and Repair (the character's
    /// interaction target when it is a castle workstation), the castle heart for Upgrade (the heart the
    /// game passed to this call, else the heart whose window the character has open, else the heart of
    /// the plot it stands on), nothing for Build.
    /// </summary>
    private Entity StationFor(EntityManager em, Entity character, MaterialOperation operation, int territory)
    {
        switch (operation)
        {
            case MaterialOperation.Craft:
            case MaterialOperation.Repair:
                return StationOf(em, character);
            case MaterialOperation.Upgrade:
                var hint = HeartHint;
                if (hint != Entity.Null && hint.Exists(em) && hint.Has<CastleHeart>(em)) return hint;
                var target = HeartTargetOf(em, character);
                if (target != Entity.Null) return target;
                var castle = CastleAt(territory);
                return castle == null ? Entity.Null : castle.Heart.ToEntity();
            default:
                return Entity.Null;
        }
    }

    private Entity StationFor(in MaterialContext context) =>
        StationFor(Em, new Entity { Index = context.Character.Index, Version = context.Character.Version },
            context.Operation, context.StandingTerritory);

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
        var em = Em;
        var ch = new Entity { Index = context.Character.Index, Version = context.Character.Version };
        into.Add(InventoryRef.For(ch, 0));
        var station = StationOf(em, ch);
        if (station != Entity.Null && !station.Has<CastleSharedInventory>(em)) into.Add(InventoryRef.For(station, 0));
        if (context.Operation is MaterialOperation.Repair or MaterialOperation.Upgrade)
        {
            // Repair and Upgrade run with any window open (a chest, the castle heart): the game's own
            // pass without shared storage already counts and pays from an interaction inventory that is
            // not shared storage, so the pool leaves it out, whatever kind it is.
            var target = InteractionTarget(em, ch);
            if (target != Entity.Null && !target.Has<CastleSharedInventory>(em)) into.Add(InventoryRef.For(target, 0));
        }
    }

    public InventoryRef StationInventory(in MaterialContext context)
    {
        if (context.Operation == MaterialOperation.Build) return InventoryRef.None;
        var station = StationFor(in context);
        return station == Entity.Null ? InventoryRef.None : InventoryRef.For(station, 0);
    }

    public InventoryRef PoolFront(in MaterialContext context)
    {
        // A workstation that is itself shared storage: the game reaches it only through its shared
        // branch, so it pays first, ahead of the provider's order (Craft, and a repair at that station).
        if (context.Operation is not (MaterialOperation.Craft or MaterialOperation.Repair)) return InventoryRef.None;
        var em = Em;
        var station = StationOf(em, new Entity { Index = context.Character.Index, Version = context.Character.Version });
        return station != Entity.Null && station.Has<CastleSharedInventory>(em) ? InventoryRef.For(station, 0) : InventoryRef.None;
    }

    /// <summary>
    /// Item types in the game's own shared storage at the standing plot: every container on that
    /// castle that carries <c>CastleSharedInventory</c>, read through the inventory engine (discovery
    /// only; the game's own aggregate on the heart is never read or written).
    /// </summary>
    public void VanillaSharedItems(in MaterialContext context, ICollection<int> into)
    {
        var castle = CastleAt(context.StandingTerritory);
        if (castle == null) return;
        var em = Em;
        foreach (var list in new[] { castle.Containers(), castle.Stations() })
            foreach (var row in list)
            {
                var e = row.Entity.ToEntity();
                if (!e.Exists(em) || !e.Has<CastleSharedInventory>(em)) continue;
                Items(row.Main, into);
            }
    }

    /// <summary>Packed network id of the castle heart at <paramref name="territory"/>; 0 for none or unreadable.</summary>
    internal ulong HeartNet(int territory)
    {
        var castle = CastleAt(territory);
        return castle == null ? 0 : MaterialNet.Of(Em, castle.Heart.ToEntity());
    }

    /// <summary>Packed network id of the workstation the character is working at; 0 for none.</summary>
    internal ulong StationNet(CastleId character)
    {
        var station = StationOf(Em, new Entity { Index = character.Index, Version = character.Version });
        return station == Entity.Null ? 0 : MaterialNet.Of(Em, station);
    }

    /// <summary>Packed network id of the castle heart whose window the character has open; 0 for none.</summary>
    internal ulong HeartTargetNet(CastleId character)
    {
        var heart = HeartTargetOf(Em, new Entity { Index = character.Index, Version = character.Version });
        return heart == Entity.Null ? 0 : MaterialNet.Of(Em, heart);
    }

    private ICastleHandle? CastleAt(int territory)
    {
        if (territory < 0) return null;
        RefreshCastles();
        for (int i = 0; i < _castles.Count; i++)
            if (_castles[i].Territory.Index == territory && _castles[i].Enabled) return _castles[i];
        return null;
    }

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
    /// <summary>The character's interaction target, whatever it is, or <see cref="Entity.Null"/>.</summary>
    internal static Entity InteractionTarget(EntityManager em, Entity character)
    {
        if (!character.TryRead(em, out Interactor interactor)) return Entity.Null;
        var target = interactor.Target;
        return target == Entity.Null || !target.Exists(em) ? Entity.Null : target;
    }

    /// <summary>The castle heart whose window the character has open (its interaction target), or <see cref="Entity.Null"/>.</summary>
    internal static Entity HeartTargetOf(EntityManager em, Entity character)
    {
        var target = InteractionTarget(em, character);
        return target != Entity.Null && target.Has<CastleHeart>(em) ? target : Entity.Null;
    }

    internal static Entity StationOf(EntityManager em, Entity character)
    {
        if (!character.TryRead(em, out Interactor interactor)) return Entity.Null;
        var target = interactor.Target;
        if (target == Entity.Null || !target.Exists(em) || !target.Has<WorkstationRecipesBuffer>(em)) return Entity.Null;
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
