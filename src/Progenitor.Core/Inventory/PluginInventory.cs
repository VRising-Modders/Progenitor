#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using Progenitor.Plugins;
using Progenitor.Ecs;
using ProjectM;
using Unity.Entities;

namespace Progenitor.Inventory;

/// <summary>Which of an owner's inventories (<c>ctx.Inventory.Of(owner, part)</c>).</summary>
public enum InventoryPart
{
    /// <summary>Instance 0: a chest's, a heart's, a servant's or a player's main inventory, a station's input.</summary>
    Main = 0,

    /// <summary>Instance 0 of a refinement station (same as <see cref="Main"/>).</summary>
    Input = 0,

    /// <summary>
    /// Instance 1: a refinement station's output on the stations the game spawns. <c>OutputOf</c>
    /// reads the row from the station's own field and also covers a salvage station.
    /// </summary>
    Output = 1,
}

/// <summary>
/// <see cref="PluginContext.Inventory"/>: read and move items in any inventory on the server —
/// chests, station input and output, castle hearts, servants, player bags. Every write is
/// server-authoritative and read back. Main thread only. On the client every call throws
/// <see cref="InvalidOperationException"/> ("ctx.Inventory runs on the server side only"), except
/// <see cref="Preview"/>, <see cref="ReopenLast"/> and <see cref="LastWindow"/>, which are client only.
/// </summary>
public sealed partial class PluginInventory
{
    private readonly PluginContext _ctx;

    internal PluginInventory(PluginContext ctx) => _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));

    // ---- resolution ----------------------------------------------------------------------------

    /// <summary>The owner's inventory <paramref name="part"/> (main by default).</summary>
    public InventoryRef Of(Entity owner, InventoryPart part = InventoryPart.Main)
    {
        Require(nameof(Of));
        return InventoryRef.For(owner, (int)part);
    }

    /// <summary>
    /// A refinement or salvage station's input: the row holding the station component's
    /// <c>InputInventoryEntity</c> (instance 0 on the stations the game spawns). A crafting station's
    /// only inventory; instance 0 for any other model.
    /// </summary>
    public InventoryRef InputOf(Entity station)
    {
        Require(nameof(InputOf));
        return InventoryRef.For(station, StationInventoryReader.Read(Worlds.Server!.EntityManager, station).Input);
    }

    /// <summary>
    /// A refinement or salvage station's output: the row holding the station component's
    /// <c>OutputInventoryEntity</c>. On a model without a separate output this is instance 1, and
    /// calls on it report that reason.
    /// </summary>
    public InventoryRef OutputOf(Entity station)
    {
        Require(nameof(OutputOf));
        int output = StationInventoryReader.Read(Worlds.Server!.EntityManager, station).Output;
        return InventoryRef.For(station, output >= 0 ? output : (int)InventoryPart.Output);
    }

    /// <summary>How many inventories <paramref name="owner"/> has: 1 for a chest or heart, 2 for a refinement station, 0 for none.</summary>
    public int Instances(Entity owner)
    {
        Require(nameof(Instances));
        var em = Worlds.Server!.EntityManager;
        int n = 0;
        while (n < Progenitor.Items.Items.MaxInventoryInstances && UnityInventoryBackend.TryInventoryEntity(em, owner, n, out _, out _)) n++;
        return n;
    }

    // ---- reads ---------------------------------------------------------------------------------

    /// <summary>
    /// Identify servant coffins and servant equipment owners by native component presence, never
    /// furniture names. False means classification failed; inspect reason and do not assume ordinary storage.
    /// This does not follow a coffin's ConnectedServant or filter inventory slots.
    /// </summary>
    public bool TryIsServantContainer(Entity owner, out bool isServantContainer, out string reason)
    {
        Require(nameof(TryIsServantContainer));
        isServantContainer = false;
        try
        {
            var em = Worlds.Server!.EntityManager;
            if (owner == Entity.Null || !em.Exists(owner))
            {
                reason = "inventory owner does not exist";
                return false;
            }
            isServantContainer = owner.Has<ServantCoffinstation>(em) || owner.Has<ServantEquipment>(em);
            reason = "";
            return true;
        }
        catch (Exception e)
        {
            reason = "servant container classification failed: " + e.Message;
            return false;
        }
    }

    /// <summary>The native RestrictedInventory rule, copied read-only. Failure is explicit and never means unrestricted.</summary>
    public InventoryAcceptance DescribeAcceptance(InventoryRef container)
    {
        Require(nameof(DescribeAcceptance));
        return InventoryRuntime.Engine.DescribeAcceptance(container);
    }

    /// <summary>A copy of every slot. <see cref="InventorySnapshot.Ok"/> false (with a reason) when unreadable.</summary>
    public InventorySnapshot Read(InventoryRef container)
    {
        Require(nameof(Read));
        return InventoryRuntime.Engine.Read(container);
    }

    /// <summary>
    /// Copy the slots into <paramref name="buffer"/> without allocating. Returns the inventory's
    /// slot count (larger than the buffer when it did not fit), or -1 when unreadable.
    /// </summary>
    public int ReadInto(InventoryRef container, InventorySlot[] buffer)
    {
        Require(nameof(ReadInto));
        return InventoryRuntime.Engine.ReadInto(container, buffer);
    }

    /// <summary>Total of <paramref name="item"/> (a PrefabGUID hash); 0 when unreadable.</summary>
    public int Count(InventoryRef container, int item)
    {
        Require(nameof(Count));
        return InventoryRuntime.Engine.Count(container, item);
    }

    /// <summary>Total across <paramref name="containers"/>; unreadable ones count 0.</summary>
    public int CountMany(IEnumerable<InventoryRef> containers, int item)
    {
        Require(nameof(CountMany));
        return InventoryRuntime.Engine.CountMany(containers, item);
    }

    /// <summary>
    /// How many more of <paramref name="item"/> fit: stack size, slot overrides and the
    /// container's <c>RestrictedInventory</c> rule. Exact for chests, stations and hearts; an
    /// upper bound for a player bag (the game keeps some items off the action bar first).
    /// </summary>
    public int FreeSpace(InventoryRef container, int item)
    {
        Require(nameof(FreeSpace));
        return InventoryRuntime.Engine.FreeSpace(container, item);
    }

    // ---- writes --------------------------------------------------------------------------------

    /// <summary>Add up to <paramref name="amount"/>; a partial add reports the remainder.</summary>
    public InventoryResult TryAdd(InventoryRef container, int item, int amount)
    {
        Require(nameof(TryAdd));
        return InventoryRuntime.Engine.TryAdd(container, item, amount);
    }

    /// <summary>Remove up to <paramref name="amount"/> from this inventory only; never touches worn equipment.</summary>
    public InventoryResult TryRemove(InventoryRef container, int item, int amount)
    {
        Require(nameof(TryRemove));
        return InventoryRuntime.Engine.TryRemove(container, item, amount);
    }

    /// <summary>
    /// Move up to <paramref name="amount"/> with the game's own slot move, conserving the total:
    /// never duplicates or destroys. Partial when the target fills or the source runs out. Draws
    /// from every source slot, a player's action bar included; pass an
    /// <see cref="InventorySlotFilter"/> to skip slots.
    /// </summary>
    public InventoryResult Transfer(InventoryRef from, InventoryRef to, int item, int amount)
    {
        Require(nameof(Transfer));
        return InventoryRuntime.Engine.Transfer(from, to, item, amount);
    }

    /// <summary>
    /// <see cref="Transfer(InventoryRef, InventoryRef, int, int)"/> that draws only from the source
    /// slots <paramref name="sourceSlots"/> allows, for example
    /// <see cref="InventorySlotFilter.SkipActionBar"/> to leave a player's action bar (bag slots
    /// 0–7) alone. Same conservation guarantees; <see cref="InventoryResult.SourceBefore"/> and
    /// <see cref="InventoryResult.SourceAfter"/> still count the whole source.
    /// <b>Experimental</b>: game-free tested, not live-verified.
    /// </summary>
    public InventoryResult Transfer(InventoryRef from, InventoryRef to, int item, int amount, InventorySlotFilter sourceSlots)
    {
        Require(nameof(Transfer));
        return InventoryRuntime.Engine.Transfer(from, to, item, amount, sourceSlots);
    }

    /// <summary>
    /// Move up to <paramref name="amount"/> of whatever is in slot <paramref name="slotIndex"/> of
    /// <paramref name="from"/> (0-based, buffer order as <see cref="Read"/> lists it) into
    /// <paramref name="to"/>. The same transfer as <see cref="Transfer(InventoryRef, InventoryRef, int, int)"/>
    /// restricted to that one slot: the same conservation check, restore step for
    /// interchangeable items, <see cref="InventoryOutcome.Inconsistent"/> stop for anything else,
    /// and units the target's owner equips counted as moved. Refused for an amount below 1, a
    /// slot out of range and the same inventory on both sides; an empty slot is
    /// <see cref="InventoryOutcome.Nothing"/> with <see cref="InventoryResult.Item"/> 0.
    /// <b>Experimental</b>: game-free tested, not live-verified.
    /// </summary>
    public InventoryResult TransferSlot(InventoryRef from, int slotIndex, InventoryRef to, int amount)
    {
        Require(nameof(TransferSlot));
        return InventoryRuntime.Engine.TransferSlot(from, slotIndex, to, amount);
    }

    /// <summary>Move every item <paramref name="filter"/> accepts (all when null), one <see cref="Transfer(InventoryRef, InventoryRef, int, int)"/> per item type.</summary>
    public InventoryTransferAllResult TransferAll(InventoryRef from, InventoryRef to, Func<int, bool>? filter = null)
    {
        Require(nameof(TransferAll));
        return InventoryRuntime.Engine.TransferAll(from, to, filter);
    }

    /// <summary>
    /// <see cref="TransferAll(InventoryRef, InventoryRef, Func{int, bool})"/> over only the source
    /// slots <paramref name="sourceSlots"/> allows: a quick stash that leaves a player's action bar
    /// alone is <c>TransferAll(bag, chest, InventorySlotFilter.SkipActionBar)</c>. Items found
    /// only in skipped slots get no row; <see cref="InventoryTransferAllResult.Left"/> is what the
    /// allowed slots held minus what moved. Skipped slots are never drawn from, but the restore
    /// step for interchangeable items puts a shortfall back with the game's add, which can land in
    /// any slot, a skipped one included. <b>Experimental</b>: game-free tested, not live-verified.
    /// </summary>
    public InventoryTransferAllResult TransferAll(InventoryRef from, InventoryRef to, InventorySlotFilter sourceSlots, Func<int, bool>? filter = null)
    {
        Require(nameof(TransferAll));
        return InventoryRuntime.Engine.TransferAll(from, to, filter, sourceSlots);
    }

    // ---- change stamps -------------------------------------------------------------------------

    /// <summary>
    /// A cheap stamp of the inventory's slot contents. Compare two stamps for <b>equality only</b>:
    /// equal means the slots have not changed since the first was taken; unequal means they
    /// <b>may</b> have (false positives, never false negatives). The game tracks changes per storage
    /// chunk, so a write to another inventory in the same chunk, a read-write access that wrote
    /// nothing or a move to another chunk also changes it, and anything that cannot be read (a gone
    /// owner, a stale reference, another world) gives a fresh value every call. Covers the slots
    /// only: not item-entity state such as durability, and not the restriction rule. Never 0, so
    /// 0 works as "no stamp yet". Costs a few raw memory reads after the first call for a reference;
    /// meant for polling hundreds of containers every frame. Server, main thread; never call it
    /// from inside a native inventory hook (the store may be mid-change). <b>Experimental</b>:
    /// source-checked and unit-tested, not live-verified.
    /// </summary>
    public long ChangeStamp(InventoryRef container)
    {
        Require(nameof(ChangeStamp));
        return InventoryRuntime.Stamps.Stamp(container);
    }

    /// <summary>
    /// <see cref="ChangeStamp"/> for each of <paramref name="containers"/>, into
    /// <paramref name="stamps"/> (at least as long), as one batch: in-flight inventory jobs are
    /// completed once rather than per container. Throws <see cref="ArgumentException"/> when the
    /// span is too short.
    /// </summary>
    public void ChangeStamps(IReadOnlyList<InventoryRef> containers, Span<long> stamps)
    {
        Require(nameof(ChangeStamps));
        InventoryRuntime.Stamps.Stamps(containers, stamps);
    }

    /// <summary>
    /// The server's change clock now, a 64-bit count that only grows (across Core reloads too). Take
    /// it before reading containers and pass it to <see cref="AnyChangedSince"/> later. Reading it
    /// completes no jobs and reads no container.
    /// </summary>
    public long Clock
    {
        get
        {
            Require(nameof(Clock));
            return InventoryRuntime.Stamps.Clock;
        }
    }

    /// <summary>
    /// True when any of <paramref name="containers"/> may have changed at or after
    /// <paramref name="since"/> (a <see cref="Clock"/> value): the same false-positives-only rule
    /// as <see cref="ChangeStamp"/>, with the same coverage. Also true for a reference seen for the
    /// first time, one whose owner now resolves to a different inventory, one that cannot be read,
    /// and a <paramref name="since"/> this clock never handed out. Server, main thread, never from
    /// inside a native inventory hook. <b>Experimental</b>: source-checked and unit-tested, not
    /// live-verified.
    /// </summary>
    public bool AnyChangedSince(IReadOnlyList<InventoryRef> containers, long since)
    {
        Require(nameof(AnyChangedSince));
        return InventoryRuntime.Stamps.AnyChangedSince(containers, since);
    }

    // ---- watches -------------------------------------------------------------------------------

    /// <summary>
    /// Call <paramref name="handler"/> on the main thread when the contents change, or once with
    /// <see cref="InventoryChangeKind.Gone"/> when the inventory goes away. Checked every
    /// <paramref name="interval"/> (default 500 ms, minimum 100 ms) by fingerprint, and on the
    /// next frame after a <c>ctx.Inventory</c> write touches it. With the experimental server
    /// setting <c>[Inventory] WatchUsesChangeStamps</c> (default false), a check whose
    /// <see cref="ChangeStamp"/> is unchanged skips the fingerprint, except every 8th, which
    /// fingerprints anyway. Tracked: unload stops it.
    /// </summary>
    public IInventoryWatch Watch(InventoryRef container, Action<InventoryChange> handler, TimeSpan? interval = null)
    {
        Require(nameof(Watch));
        var watch = InventoryRuntime.Hub.Add(container, handler, interval, Environment.TickCount64);
        _ctx.Track(watch);
        return watch;
    }

    // ---- player moves ----------------------------------------------------------------------------

    /// <summary>
    /// Call <paramref name="handler"/> for every change a player makes by hand to a non-player
    /// inventory (a castle chest, station or heart, a servant): a drag in or out, shift-click, take
    /// all, split, sort, smart merge, drop or equip from the window. One
    /// <see cref="PlayerInventoryMove"/> per container and item: who (<c>FromCharacter</c>), which
    /// container (<see cref="PlayerInventoryMove.Container"/>, <see cref="PlayerInventoryMove.Inventory"/>),
    /// the item, the amount and the direction (In, Out, Within). Amounts are the container's real
    /// change around the game's handling of the request: a refused request raises nothing, a partial
    /// one raises what moved. Observe only: the request is never changed or held back. Called on the
    /// main thread from the frame tick after the move, never inside the game's update. Moves between
    /// a player's own inventories (a bag sort) are not raised. Tracked: unload stops it.
    /// <b>Experimental</b>: source-checked and game-free tested, not live-verified.
    /// </summary>
    public IDisposable OnPlayerMove(Action<PlayerInventoryMove> handler)
    {
        Require(nameof(OnPlayerMove));
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        var sub = PlayerMoveRuntime.Hub.Subscribe(handler);
        _ctx.Track(sub);
        return sub;
    }

    /// <summary>
    /// Raise one <see cref="Progenitor.Drops.PlayerGroundDrop"/> per item each time a player drops
    /// something from their own bag onto the ground: the inventory menu's drop key
    /// (<c>DropItemAtSlotEvent</c>) or a slot dragged onto the ground (<c>DropInventoryItemEvent</c>
    /// naming the player's own inventory). Amounts are read back from the bag around the game's
    /// update; a refused drop raises nothing. Position: the character's <c>Translation</c> right after
    /// the update. Drops from a container window are <see cref="OnPlayerMove"/>'s (<c>Kind = Drop</c>);
    /// a whole-inventory drop is <see cref="OnDropAll"/>'s. Called on the main thread from the frame
    /// tick after the drop. Tracked: unload stops it.
    /// <b>Experimental</b>: source-checked and game-free tested, not live-verified.
    /// </summary>
    public IDisposable OnGroundDrop(Action<Progenitor.Drops.PlayerGroundDrop> handler)
    {
        Require(nameof(OnGroundDrop));
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        var sub = Progenitor.Drops.DropRuntime.Ground.Subscribe(handler);
        _ctx.Track(sub);
        return sub;
    }

    /// <summary>
    /// Raise one <see cref="Progenitor.Drops.PlayerDropAll"/> each time a player drops their whole
    /// inventory (<c>DropEntireInventoryEvent</c>, handled by <c>DropItemSystem</c>): the user, the
    /// character, what left the bag, and the container the game left for it
    /// (<c>PlayerInventoryDropContainerPrefabGUID</c>) once a new one appears near the player, or
    /// with <c>HasContainer</c> false and a reason after 60 frames. Called on the main thread from
    /// the frame tick. Tracked: unload stops it.
    /// <b>Experimental</b>: source-checked and game-free tested, not live-verified.
    /// </summary>
    public IDisposable OnDropAll(Action<Progenitor.Drops.PlayerDropAll> handler)
    {
        Require(nameof(OnDropAll));
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        var sub = Progenitor.Drops.DropRuntime.All.Subscribe(handler);
        _ctx.Track(sub);
        return sub;
    }

    // ---- stable ids (P5) -------------------------------------------------------------------------

    /// <summary>
    /// The stable id of a placed container, station or castle heart. <see cref="ContainerId.None"/>
    /// for anything else (a player, a servant) or while the world's mod save is not loaded.
    /// </summary>
    public ContainerId IdOf(Entity containerOrStation)
    {
        Require(nameof(IdOf));
        return InventoryRuntime.IdOf(containerOrStation, out _);
    }

    /// <summary><see cref="IdOf(Entity)"/> with the reason when there is no id.</summary>
    public ContainerId IdOf(Entity containerOrStation, out string reason)
    {
        Require(nameof(IdOf));
        return InventoryRuntime.IdOf(containerOrStation, out reason);
    }

    /// <summary>What the id book knows about <paramref name="id"/>.</summary>
    public ContainerIdStatus Status(ContainerId id)
    {
        Require(nameof(Status));
        return InventoryRuntime.Status(id);
    }

    /// <summary>The live entity for <paramref name="id"/>; false with a reason when it is gone, unknown or not bound yet.</summary>
    public bool TryFind(ContainerId id, out Entity owner, out string reason)
    {
        Require(nameof(TryFind));
        return InventoryRuntime.TryFind(id, out owner, out reason);
    }

    // ---- network ids ---------------------------------------------------------------------------

    /// <summary>
    /// Resolve a <c>NetworkId</c> a client reported (<c>VanillaPanelTarget.NetworkIdIndex</c>,
    /// <c>NetworkIdGeneration</c>, <c>NetworkId2</c>, or the same fields of a
    /// <c>UiWorldPickResult</c>) to the server entity and the stable id of the placed container,
    /// station or castle heart it names, issuing the id as <see cref="IdOf(Entity)"/> does. The
    /// words are untrusted: an empty, static or malformed id, one no container carries, a stale
    /// generation or anything that is not a placed container, station or heart gives false with a
    /// reason (it names no server entity id). Never throws on bad input. The resolver does not
    /// check whether this user may touch the container: authorization is yours (for example
    /// <c>ctx.Castles.RelationTo</c>). <b>Experimental</b>: not live-verified.
    /// </summary>
    public bool TryResolveNetworkId(int index, int generation, int type, out ContainerId id, out Entity owner, out string reason)
    {
        Require(nameof(TryResolveNetworkId));
        return InventoryRuntime.TryResolveNetworkId(index, generation, type, out id, out owner, out reason);
    }

    /// <summary>
    /// The live container's <c>NetworkId</c> as the three words to send to a client, which finds
    /// its own entity with <c>ctx.Hud.TryFindByNetworkId</c> (to <c>Locate</c> it, for example).
    /// Session-local: never persist the words. <b>Experimental</b>: not live-verified.
    /// </summary>
    public bool TryGetNetworkId(ContainerId id, out int index, out int generation, out int type)
    {
        Require(nameof(TryGetNetworkId));
        return InventoryRuntime.TryGetNetworkId(id, out index, out generation, out type, out _);
    }

    /// <summary><see cref="TryGetNetworkId(ContainerId, out int, out int, out int)"/> with the reason on false.</summary>
    public bool TryGetNetworkId(ContainerId id, out int index, out int generation, out int type, out string reason)
    {
        Require(nameof(TryGetNetworkId));
        return InventoryRuntime.TryGetNetworkId(id, out index, out generation, out type, out reason);
    }

    private void Require(string op)
    {
        _ctx.EnsureAttached();
        if (!Plugin.IsServerProcess || !InventoryRuntime.Installed)
            throw new InvalidOperationException("ctx.Inventory." + op + " runs on the server side only; inventories are server-authoritative.");
        if (!Dev.MainThread.IsMainThread)
            throw new InvalidOperationException("ctx.Inventory." + op + " must run on the game main thread (Plugin.Frame, a command, an event, or MainThread.Post).");
    }
}
#endif
