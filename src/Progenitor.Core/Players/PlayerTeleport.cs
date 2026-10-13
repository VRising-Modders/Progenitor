using System;
using ProjectM;
using Progenitor.Units;
using Unity.Entities;
using Unity.Mathematics;

namespace Progenitor.Players;

/// <summary>
/// MAPTP1: move a character the way the game's own server teleports do. Server, main thread.
///
/// <para>The game's helper first: <c>ProjectM.TeleportUtilityServer.Teleport(EntityManager, Entity,
/// float3, Nullable&lt;quaternion&gt;)</c> (<c>ProjectM.Shared</c>) — a public static with
/// no admin gate and no network event to forge, the path the Dev bridge's <c>teleport</c> op has used
/// live. If it throws (a game update moving it), the guarded position writes
/// <see cref="UnitSpawnPlacement.TryWriteTranslation"/> and <see cref="UnitSpawnPlacement.TryWriteLastTranslation"/>
/// (the client interpolates from <c>LastTranslation</c>) run instead. <c>SpawnTransform</c>, the AI leash
/// home, is never touched. The Dev op keeps its own richer fallback and one-second read-back.</para>
///
/// <para>Mounted, shapeshifted or in combat makes no difference to the call; whether a mount follows its
/// rider is the game helper's business and has not been checked live.</para>
/// </summary>
public static class PlayerTeleport
{
    /// <summary>The <see cref="Teleport"/> method token for the game helper.</summary>
    public const string GameMethod = "TeleportUtilityServer.Teleport";

    /// <summary>The <see cref="Teleport"/> method token for the fallback writes.</summary>
    public const string WriteMethod = "Translation+LastTranslation";

    /// <summary>
    /// Move <paramref name="character"/> to <paramref name="destination"/>. Returns the method that ran
    /// (<see cref="GameMethod"/> or <see cref="WriteMethod"/>), or null with <paramref name="why"/> set when
    /// neither could.
    /// </summary>
    public static string? Teleport(EntityManager em, Entity character, float3 destination, out string why)
    {
        why = "";
        if (character == Entity.Null || !em.Exists(character))
        {
            why = "no live character";
            return null;
        }

        string helperError;
        try
        {
            TeleportUtilityServer.Teleport(em, character, destination);
            return GameMethod;
        }
        catch (Exception e)
        {
            helperError = e.Message;
        }

        if (UnitSpawnPlacement.TryWriteTranslation(em, character, destination, "PlayerTeleport"))
        {
            UnitSpawnPlacement.TryWriteLastTranslation(em, character, destination, "PlayerTeleport");
            Plugin.Logger?.LogWarning("PlayerTeleport: the game helper threw (" + helperError + "); wrote the position instead.");
            return WriteMethod;
        }

        why = "the game helper threw (" + helperError + ") and the position could not be written";
        return null;
    }
}
