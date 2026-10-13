using Unity.Entities;

namespace Progenitor;

/// <summary>
/// Finds the game's ECS worlds by name. V Rising creates a world named "Server" in the server
/// process and "Client_0" in the client process. Nothing here cares whether the server is
/// dedicated or the bundled one a client launched for a private game.
/// </summary>
public static class Worlds
{
    public const string ServerName = "Server";
    public const string ClientName = "Client_0";

    /// <summary>
    /// The first created world named <paramref name="name"/>, or null.
    ///
    /// <para>Called every frame from several Core paths, so it is written to keep IL2CPP
    /// allocations down: <c>List.Count</c> and <c>World.IsCreated</c> return value types through
    /// <c>il2cpp_runtime_invoke</c>, which boxes each result on the IL2CPP heap. The count is read
    /// once, and the name (a backing-field read, managed copy only) is compared before
    /// <c>IsCreated</c>, so only a world with the right name pays for the created check.</para>
    /// </summary>
    public static World? Find(string name)
    {
        var all = World.s_AllWorlds;
        if (all == null) return null;
        int count = all.Count;
        for (int i = 0; i < count; i++)
        {
            var world = all[i];
            if (world != null && world.Name == name && world.IsCreated) return world;
        }
        return null;
    }

    public static World? Server => Find(ServerName);
    public static World? Client => Find(ClientName);
}
