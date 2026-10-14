using System;
using System.Collections.Generic;
using ProjectM;
using ProjectM.Network;
using Progenitor.Castles;
using Progenitor.Ecs;
using Unity.Entities;

namespace Progenitor.Inventory;

/// <summary>
/// The server half of <see cref="NetworkIdResolver"/>: the candidates are every castle heart and
/// every entity with <c>InventoryOwner</c> (placed containers and stations, and also players and
/// servants, which <c>IdOf</c> then refuses with its own reason), each with its own
/// <c>NetworkId</c> component read through the typed accessor the castle reader uses.
///
/// <para>The game's own map (<c>NetworkIdSystem.Singleton._NetworkIdLookupMap</c>, a
/// <c>NativeParallelHashMap&lt;NetworkId, Entity&gt;</c>) is not called: reaching it needs a new
/// generic interop instantiation over a game value type, which this framework does not create.
/// The components it is filled from carry the same ids.</para>
/// </summary>
internal sealed class ServerNetworkIdSource : INetworkIdSource
{
    public void Scan(List<NetworkIdEntry> into)
    {
        var world = Worlds.Server;
        if (world == null || !world.IsCreated) return;
        var em = world.EntityManager;
        foreach (var plot in CastleReader.Plots(world))
            Add(em, plot.HeartEntity, into);

        var owners = Query.Entities(em, includeDisabled: true, includeSystems: false,
            ComponentIndex.Of<InventoryOwner>(ComponentType.AccessMode.ReadOnly),
            ComponentIndex.Of<NetworkId>(ComponentType.AccessMode.ReadOnly));
        for (int i = 0; i < owners.Count; i++)
            Add(em, owners[i], into);
    }

    public bool TryReadBack(int entityIndex, int entityVersion, out int index, out int generation, out int type)
    {
        index = generation = type = 0;
        var world = Worlds.Server;
        if (world == null || !world.IsCreated) return false;
        var em = world.EntityManager;
        return Read(em, new Entity { Index = entityIndex, Version = entityVersion }, out index, out generation, out type);
    }

    /// <summary>The entity's id as three words; false when it is gone or has none.</summary>
    internal static bool Read(EntityManager em, Entity e, out int index, out int generation, out int type)
    {
        index = generation = type = 0;
        try
        {
            if (e == Entity.Null || !e.Exists(em) || !e.TryRead(em, out NetworkId net)) return false;
            type = (int)net.Type;
            if (net.Type != NetworkIdType.Normal) return true;
            index = net.Normal_Index;
            generation = net.Normal_Generation;
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogDebug("Inventory: NetworkId read of " + e.Index + ":" + e.Version + " threw: " + ex.Message);
            return false;
        }
    }

    private static void Add(EntityManager em, Entity e, List<NetworkIdEntry> into)
    {
        if (Read(em, e, out int index, out int generation, out int type) && type == NetworkIdWords.TypeNormal)
            into.Add(new NetworkIdEntry(index, generation, e.Index, e.Version));
    }
}
