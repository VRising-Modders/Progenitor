using System;
using System.Collections.Generic;
using Progenitor.Inventory;
using Progenitor.Plugins;

namespace Progenitor.Drops;

/// <summary>
/// <see cref="PluginContext.Drops"/>: item piles on the ground (list them near a point, take from
/// one into an inventory) and the game's drop prefabs. Pile calls are server only and main thread
/// only; on the client they throw <see cref="InvalidOperationException"/>. <see cref="TryPrefabs"/>
/// works on both sides. <b>Experimental</b>: source-checked and game-free tested, not live-verified.
/// The bag-drop events are on <c>ctx.Inventory</c> (<c>OnGroundDrop</c>, <c>OnDropAll</c>).
/// </summary>
public sealed class PluginDrops
{
    private readonly PluginContext _ctx;

    internal PluginDrops(PluginContext ctx) => _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));

    /// <summary>
    /// Every item pile within <paramref name="radius"/> metres (3-D distance, at most 500) of the
    /// point, nearest first, at most <paramref name="max"/> (1 to 4096). Piles being removed or empty
    /// are left out; asleep piles (<see cref="GroundPile.Disabled"/>) are listed.
    /// </summary>
    public IReadOnlyList<GroundPile> Near(float x, float y, float z, float radius, int max = 64)
    {
        Require(nameof(Near));
        var list = new List<GroundPile>();
        GroundPiles.Near(x, y, z, radius, max, list);
        return list;
    }

    /// <summary><see cref="Near"/> into a caller-owned list (cleared first), for a poll that should not allocate a list per call. Returns the count.</summary>
    public int NearInto(float x, float y, float z, float radius, List<GroundPile> into, int max = 64)
    {
        Require(nameof(NearInto));
        if (into == null) throw new ArgumentNullException(nameof(into));
        return GroundPiles.Near(x, y, z, radius, max, into);
    }

    /// <summary>The pile as it is now (amount, place, item entity); false with a reason when stale, gone, being removed or empty.</summary>
    public bool TryGet(GroundPileId pile, out GroundPile current, out string reason)
    {
        Require(nameof(TryGet));
        return GroundPiles.TryGet(pile, out current, out reason);
    }

    /// <summary>
    /// Move up to <paramref name="amount"/> units from the pile into <paramref name="into"/> (a player's
    /// bag through <c>ctx.Inventory.Of(character)</c>, or any other inventory). Partial takes allowed.
    ///
    /// <para><b>A pile with its own inventory</b> (<see cref="GroundPile.HasOwnInventory"/>; the
    /// game's item pile holds the real item there): the game's slot move out of that inventory
    /// (<c>ctx.Inventory.Transfer</c>), so gear, jewels and books arrive as the very copy that dropped,
    /// item entity and rolled stats included. <c>ItemAmount</c> then becomes what the pile's inventory
    /// still holds, as the game's own pickup writes it. Nothing moved (bag full): <c>Nothing</c>, pile
    /// untouched. Part moved: <c>Partial</c>, the pile stays with the rest.</para>
    ///
    /// <para><b>A pile without one</b>: a stackable item with no per-copy state is added fresh
    /// (<c>ctx.Inventory.TryAdd</c>) and the pile shrinks by what the inventory read back as added.
    /// Anything else is refused (<see cref="GroundTakeRoute.None"/>).</para>
    ///
    /// <para>An emptied pile is removed with the game's replicated destroy. Refused (nothing
    /// touched): a stale or empty handle, amount below 1, a pile that is gone, being removed, empty or
    /// in flight, an item the server item table does not know, and a pile with no route.
    /// <see cref="GroundTakeResult.Route"/> says which route ran.</para>
    /// </summary>
    public GroundTakeResult TakeInto(GroundPileId pile, InventoryRef into, int amount)
    {
        Require(nameof(TakeInto));
        return GroundPiles.TakeInto(pile, into, amount);
    }

    /// <summary>
    /// Remove a pile that holds nothing, with the game's replicated destroy (the removal reaches
    /// every client). True when the pile was removed now, or was already gone or being removed (the
    /// call is idempotent). False with a reason for a stale handle (from an earlier world load), an
    /// entity that is not an item pile, or a pile that still holds units in <c>ItemAmount</c> or its
    /// own inventory: take them first with <see cref="TakeInto"/>. <c>TakeInto</c> already removes
    /// the piles it empties; this is for a pile emptied another way.
    /// </summary>
    public bool Remove(GroundPileId pile, out string reason)
    {
        Require(nameof(Remove));
        return GroundPiles.Remove(pile, out reason);
    }

    /// <summary>
    /// The drop prefabs from the game's prefab table (<c>RootPrefabCollection</c>): the item pile,
    /// the three player death containers and the whole-inventory drop container. Both sides, main
    /// thread. False with a reason until the table exists and is filled in.
    /// </summary>
    public bool TryPrefabs(out DropPrefabs prefabs, out string reason)
    {
        _ctx.EnsureAttached();
        if (!Dev.MainThread.IsMainThread)
            throw new InvalidOperationException("ctx.Drops.TryPrefabs must run on the game main thread.");
        var world = Plugin.IsServerProcess ? Worlds.Server : Worlds.Client;
        return DropPrefabsReader.TryRead(world, out prefabs, out reason);
    }

    private void Require(string op)
    {
        _ctx.EnsureAttached();
        if (!Plugin.IsServerProcess || !InventoryRuntime.Installed)
            throw new InvalidOperationException("ctx.Drops." + op + " runs on the server side only; ground items are server-authoritative.");
        if (!Dev.MainThread.IsMainThread)
            throw new InvalidOperationException("ctx.Drops." + op + " must run on the game main thread (Plugin.Frame, a command, an event, or MainThread.Post).");
    }
}
