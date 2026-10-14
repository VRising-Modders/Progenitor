#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using Progenitor.Plugins;
using Unity.Entities;

namespace Progenitor.Inventory;

/// <summary>Which of an owner's inventories (<c>ctx.Inventory.Of(owner, part)</c>).</summary>
public enum InventoryPart
{
    /// <summary>Instance 0: a chest's, a heart's, a servant's or a player's main inventory, a station's input.</summary>
    Main = 0,

    /// <summary>Instance 0 of a refinement station (same as <see cref="Main"/>).</summary>
    Input = 0,

    /// <summary>Instance 1 of a refinement station.</summary>
    Output = 1,
}

/// <summary>
/// <see cref="PluginContext.Inventory"/>: read and move items in any inventory on the server —
/// chests, station input and output, castle hearts, servants, player bags. Every write is
/// server-authoritative and read back. Main thread only. On the client every call throws
/// <see cref="InvalidOperationException"/> ("ctx.Inventory runs on the server side only").
/// </summary>
public sealed class PluginInventory
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

    /// <summary>A refinement station's input (instance 0); a crafting station's only inventory.</summary>
    public InventoryRef InputOf(Entity station) => Of(station, InventoryPart.Input);

    /// <summary>A refinement station's output (instance 1). Calls on a station without one report that reason.</summary>
    public InventoryRef OutputOf(Entity station) => Of(station, InventoryPart.Output);

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
    /// never duplicates or destroys. Partial when the target fills or the source runs out.
    /// </summary>
    public InventoryResult Transfer(InventoryRef from, InventoryRef to, int item, int amount)
    {
        Require(nameof(Transfer));
        return InventoryRuntime.Engine.Transfer(from, to, item, amount);
    }

    /// <summary>Move every item <paramref name="filter"/> accepts (all when null), one <see cref="Transfer"/> per item type.</summary>
    public InventoryTransferAllResult TransferAll(InventoryRef from, InventoryRef to, Func<int, bool>? filter = null)
    {
        Require(nameof(TransferAll));
        return InventoryRuntime.Engine.TransferAll(from, to, filter);
    }

    // ---- watches -------------------------------------------------------------------------------

    /// <summary>
    /// Call <paramref name="handler"/> on the main thread when the contents change, or once with
    /// <see cref="InventoryChangeKind.Gone"/> when the inventory goes away. Checked every
    /// <paramref name="interval"/> (default 500 ms, minimum 100 ms) by fingerprint, and on the
    /// next frame after a <c>ctx.Inventory</c> write touches it. Tracked: unload stops it.
    /// </summary>
    public IInventoryWatch Watch(InventoryRef container, Action<InventoryChange> handler, TimeSpan? interval = null)
    {
        Require(nameof(Watch));
        var watch = InventoryRuntime.Hub.Add(container, handler, interval, Environment.TickCount64);
        _ctx.Track(watch);
        return watch;
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
    /// reason. Never throws on bad input. <b>Experimental</b>: not live-verified.
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
