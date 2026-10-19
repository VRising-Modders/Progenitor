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
    /// bag through <c>ctx.Inventory.Of(character)</c>, or any other inventory). The inventory add runs
    /// first (<c>ctx.Inventory.TryAdd</c>, partial allowed); the pile then shrinks by exactly what the
    /// inventory read back as added, or is emptied and removed with the game's replicated destroy.
    /// Refused (nothing touched): a stale or empty handle, amount below 1, a pile that is gone, being
    /// removed or empty, a pile with its own item entity, an item that does not stack or that the server
    /// item table does not know. Only stackable items are taken: no proven path moves a unique item's
    /// own entity into a bag yet.
    /// </summary>
    public GroundTakeResult TakeInto(GroundPileId pile, InventoryRef into, int amount)
    {
        Require(nameof(TakeInto));
        return GroundPiles.TakeInto(pile, into, amount);
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
