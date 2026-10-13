namespace Progenitor.Players;

/// <summary>
/// Game-free refusal text for <see cref="PlayerRespawn"/>. The direct
/// <c>RespawnCharacter</c> call aborts the dedicated server; the strings live here so
/// tests can pin them without the game assemblies.
/// </summary>
public static class PlayerRespawnPolicy
{
    /// <summary>Returned by <see cref="PlayerRespawn.Respawn"/> instead of calling the game.</summary>
    public const string UnsafeDirectCallRefusal =
        "respawn refused: ServerBootstrapSystem.RespawnCharacter allocates Allocator.Temp and aborts the dedicated server, including when the call is posted to after ServerBootstrapSystem. A NativeArray(Allocator.Temp) aborts on the debug-bridge drain and is legal on that hook; RespawnCharacter is not. Use the in-game respawn (Space, then a waygate).";
}
