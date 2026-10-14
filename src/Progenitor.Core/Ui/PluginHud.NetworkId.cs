using System;
using Progenitor.Ecs;
using Progenitor.Inventory;
using ProjectM.Network;
using Unity.Entities;

namespace Progenitor.Ui;

public sealed partial class PluginHud
{
    /// <summary>
    /// The client world's entity that carries the <c>NetworkId</c> the server sent
    /// (<c>ctx.Inventory.TryGetNetworkId</c> on the server: index, generation, type), for
    /// <see cref="Locate(Entity, string, double)"/>. One pass over the client's networked entities;
    /// call it once per request, not every frame. False with a reason on malformed words, on the
    /// server, or when no entity of the client world carries the id (destroyed, or not streamed to
    /// this client: a building far away may not be). The words are session-local: never persist
    /// them. <b>Status: experimental</b> (built; not verified live).
    /// </summary>
    public bool TryFindByNetworkId(int index, int generation, int type, out Entity entity, out string reason)
    {
        entity = Entity.Null;
        if (ServerSide("TryFindByNetworkId"))
        {
            reason = "ctx.Hud.TryFindByNetworkId is client only";
            return false;
        }

        if (!NetworkIdWords.Validate(index, generation, type, out reason)) return false;
        var world = Worlds.Client;
        if (world == null || !world.IsCreated)
        {
            reason = "no client world (not in a game)";
            return false;
        }

        try
        {
            var em = world.EntityManager;
            var all = Query.Entities(em, false, false, ComponentIndex.Of<NetworkId>(ComponentType.AccessMode.ReadOnly));
            for (int i = 0; i < all.Count; i++)
            {
                var e = all[i];
                if (!e.TryRead(em, out NetworkId net) || net.Type != NetworkIdType.Normal) continue;
                if (net.Normal_Index != index || net.Normal_Generation != generation) continue;
                entity = e;
                reason = "";
                return true;
            }

            reason = "no entity of the client world has network id " + NetworkIdWords.Format(index, generation)
                + " (destroyed, or not streamed to this client)";
            return false;
        }
        catch (Exception e)
        {
            reason = "the client network id lookup failed: " + e.Message;
            return false;
        }
    }
}
