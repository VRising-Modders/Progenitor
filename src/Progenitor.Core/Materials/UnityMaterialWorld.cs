#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using ProjectM;
using ProjectM.CastleBuilding;
using ProjectM.Network;
using ProjectM.Shared;
using ProjectM.Tiles;
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
    /// the plot it stands on), nothing for Build and CharacterCraft (the Character window's Crafting tab
    /// has no workstation).
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
        if (context.Operation is MaterialOperation.Repair or MaterialOperation.Upgrade or MaterialOperation.CharacterCraft)
        {
            // Repair, Upgrade and character crafting run with any window open (a chest, the castle heart):
            // the game's own pass without shared storage already counts and pays from an interaction
            // inventory that is not shared storage, so the pool leaves it out, whatever kind it is.
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
    /// The game's own shared storage at the standing plot: every container on that castle that carries
    /// <c>CastleSharedInventory</c> (discovery only; the game's own aggregate on the heart is never read
    /// or written), listed by <see cref="ListShared"/> and kept per plot for <see cref="SharedListRefreshMs"/>;
    /// the caller reads the contents fresh.
    /// </summary>
    public void VanillaSharedInventories(in MaterialContext context, ICollection<InventoryRef> into)
    {
        int territory = context.StandingTerritory;
        if (territory < 0) return;
        long now = Environment.TickCount64;
        int epoch = Epoch;
        if (!_shared.TryGetValue(territory, out var cached) || cached.Epoch != epoch || now - cached.Stamp >= SharedListRefreshMs)
        {
            var list = new List<InventoryRef>();
            var castle = CastleAt(territory);
            if (castle != null) ListShared(castle.Heart.ToEntity(), list);
            cached = (epoch, now, list);
            _shared[territory] = cached;
        }
        foreach (var inv in cached.List) into.Add(inv);
    }

    /// <summary>
    /// How long a plot's shared-storage list is kept (the contents are always read fresh): one refresh,
    /// so the operations and players of one refresh share one listing.
    /// </summary>
    internal const long SharedListRefreshMs = 1_000;

    /// <summary>
    /// The placed shared-storage containers of one castle: a query of only the tile models that carry
    /// <c>CastleSharedInventory</c> and a heart connection (a few hundred server-wide, not every castle
    /// building), kept when connected to <paramref name="heart"/> (the connection read through the
    /// existing guarded raw reader) and holding an inventory. Hearts are left out, as the castle's
    /// container rows leave them out. Sorted by entity, so an unchanged castle lists the same order.
    /// </summary>
    private static void ListShared(Entity heart, List<InventoryRef> into)
    {
        if (heart == Entity.Null) return;
        var em = Em;
        var rows = Query.Entities(em, includeDisabled: true, includeSystems: false,
            ComponentIndex.Of<TileModel>(ComponentType.AccessMode.ReadOnly),
            ComponentIndex.Of<CastleSharedInventory>(ComponentType.AccessMode.ReadOnly),
            ComponentIndex.Of<CastleHeartConnection>(ComponentType.AccessMode.ReadOnly));
        var found = new List<Entity>();
        foreach (var e in rows)
        {
            try
            {
                if (e.Has<CastleHeart>(em) || !UnityInventoryBackend.HasInventory(em, e)) continue;
                if (Servants.ServantRuntime.HeartOf(em, e) != heart) continue;
                found.Add(e);
            }
            catch { /* unreadable entity: not listed */ }
        }
        found.Sort((a, b) => a.Index != b.Index ? a.Index.CompareTo(b.Index) : a.Version.CompareTo(b.Version));
        foreach (var e in found) into.Add(InventoryRef.For(e, 0));
    }

    private readonly Dictionary<int, (int Epoch, long Stamp, List<InventoryRef> List)> _shared = new();

    private InventorySlot[] _slots = new InventorySlot[64];

    /// <summary>One slot-buffer read through the inventory engine, summed per item (slots without an item entity).</summary>
    public bool ReadStock(InventoryRef inventory, Dictionary<int, long> into)
    {
        var engine = InventoryRuntime.Engine;
        int n = engine.ReadInto(inventory, _slots);
        if (n < 0) return false;
        if (n > _slots.Length)
        {
            _slots = new InventorySlot[Math.Max(n, _slots.Length * 2)];
            n = engine.ReadInto(inventory, _slots);
            if (n < 0) return false;
            n = Math.Min(n, _slots.Length);
        }
        for (int i = 0; i < n; i++)
        {
            var slot = _slots[i];
            if (slot.IsEmpty || slot.HasItemEntity || slot.Amount <= 0) continue;
            into[slot.Item] = (into.TryGetValue(slot.Item, out long had) ? had : 0) + slot.Amount;
        }
        return true;
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

    /// <summary>
    /// How <paramref name="user"/> stands to the castle of <paramref name="heart"/> (owner, the owner's
    /// clan, or neither), read now from the castle and the user's clan; <see cref="CastleRelation.None"/>
    /// when either is unreadable. The team check is not consulted: only ownership counts here.
    /// </summary>
    internal CastleRelation OwnershipOf(CastleId user, ulong heartNet, CastleId character)
    {
        try
        {
            var em = Em;
            var heart = HeartTargetOf(em, new Entity { Index = character.Index, Version = character.Version });
            if (heart == Entity.Null || MaterialNet.Of(em, heart) != heartNet) return CastleRelation.None;
            var key = CastleId.From(heart);
            ICastleHandle? castle = null;
            RefreshCastles();
            for (int i = 0; i < _castles.Count; i++)
                if (_castles[i].Heart.Equals(key)) { castle = _castles[i]; break; }
            if (castle == null) return CastleRelation.None;
            castle = castle.Refresh();
            if (!castle.Enabled) return CastleRelation.None;
            var userEntity = new Entity { Index = user.Index, Version = user.Version };
            if (!userEntity.Exists(em) || !userEntity.Has<User>(em)) return CastleRelation.None;
            var clan = CastleId.From(em.GetComponentData<User>(userEntity).ClanEntity._Entity); // existing typed accessor
            return Progenitor.Clans.ClanLogic.Relation(user, clan, castle.Owner, teamAllied: null);
        }
        catch { return CastleRelation.None; }
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
        // During a view refresh, one position test per character: the refresh builds a context for every
        // operation (Build, Craft, Upgrade, CharacterCraft) of the same player, twice when it checks reuse.
        // A seam call always reads the position fresh.
        if (!InRefresh) _standing.Clear();
        else if (_standing.TryGetValue(character, out int known)) return known;
        int found = -1;
        if (character.TryRead(em, out Translation t))
        {
            CastleVec3 pos = t.Value;
            for (int i = 0; i < _castles.Count; i++)
                if (_castles[i].IsInside(pos)) { found = _castles[i].Territory.Index; break; }
        }
        if (InRefresh) _standing[character] = found;
        return found;
    }

    private readonly Dictionary<Entity, int> _standing = new();
    private bool _inRefresh;

    /// <summary>
    /// Set by the per-player view refresh for its duration (main thread, one frame): standing plots are
    /// then resolved once per character. Cleared (and the memo dropped) when the refresh ends.
    /// </summary>
    internal bool InRefresh
    {
        get => _inRefresh;
        set { _inRefresh = value; _standing.Clear(); }
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
