using System;
using System.Text.Json;
using Unity.Entities;

namespace Progenitor.Dev.Ops;

/// <summary>
/// Debug-bridge <c>respawn</c>. Enqueues <see cref="ProjectM.Network.CharacterRespawnEvent"/>.
/// Does not call <c>ServerBootstrapSystem.RespawnCharacter</c>.
/// </summary>
public static class RespawnOps
{
    public static object Run(World world, JsonElement args)
    {
        if (!Progenitor.Plugin.IsServerProcess)
            throw new InvalidOperationException("respawn runs on the server side only");
        if (world == null || !world.IsCreated)
            throw new InvalidOperationException("the game world does not exist yet (not in a game)");
        _ = args;
        return LifeOps.Respawn(world, args);
    }
}
