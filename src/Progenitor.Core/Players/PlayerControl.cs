using System;
using ProjectM;
using ProjectM.Network;
using Stunlock.Network;
using Unity.Entities;

namespace Progenitor.Players;

/// <summary>
/// Server-side control over connected players. Disconnect goes through the server's network layer
/// with a reason code and a free-text message. The message reaches the client's disconnect-reason
/// extra data (<c>ClientBootstrapSystem.DisconnectReasonExtraData</c>), but the game's disconnect
/// screen never shows it: it prints only the reason code's own text (live 2026-10-05: Kicked →
/// "You were kicked from the server"; IncorrectVersion → the raw name; Banned → the extra data read
/// as a ban-expiry date; Unknown → a Steam connection-end code). Kicked reads best. To tell a
/// player why, send a chat system message before the disconnect, or show it in Progenitor's own UI
/// on a client that runs Progenitor.
/// </summary>
public static class PlayerControl
{
    /// <summary>
    /// Called from Core Install so SkipCoreModules can log a skip. Also installs
    /// <see cref="PlayerDirectory"/> (connect/disconnect/world invalidation). Disconnect itself
    /// is a one-shot call; skip is a documented no-op for the bisect list.
    /// </summary>
    internal static void Install()
    {
        if (HarmonySlotHandoff.ShouldSkipModule(HarmonySlotHandoff.PlayersModule))
        {
            Plugin.Logger?.LogInfo(HarmonySlotHandoff.SkipModuleLog(HarmonySlotHandoff.PlayersModule));
            PlayerDirectory.Install();
            return;
        }

        PlayerDirectory.Install();
    }

    /// <summary>Drop directory bus subscriptions on Core unload / swap.</summary>
    internal static void Uninstall()
    {
        try { PlayerDirectory.Uninstall(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("PlayerDirectory.Uninstall: " + e.Message); }
    }

    /// <summary>Players module pins, including the directory cache revision and hook holds.</summary>
    internal static string DescribePins()
    {
        if (HarmonySlotHandoff.ShouldSkipModule(HarmonySlotHandoff.PlayersModule))
            return HarmonySlotHandoff.FormatModulePinsSkipped(HarmonySlotHandoff.PlayersModule);

        var holds = new System.Collections.Generic.List<string>();
        PlayerDirectory.AppendPinHolds(holds);
        return HarmonySlotHandoff.FormatModulePins(HarmonySlotHandoff.PlayersModule, holds);
    }

    /// <summary>Finds the connection for a user entity in the approved-user table.</summary>
    public static bool TryGetConnection(ServerBootstrapSystem bootstrap, Entity userEntity, out NetConnectionId connection)
    {
        connection = null!;
        var clients = bootstrap._ApprovedUsersLookup;
        if (clients == null) return false;
        for (int i = 0; i < clients.Length; i++)
        {
            var client = clients[i];
            if (client == null || !client.Exists || client.UserEntity != userEntity) continue;
            connection = client.NetConnectionId;
            return connection != null;
        }
        return false;
    }

    /// <summary>
    /// Disconnects a user with a reason code and a message. The game's disconnect screen shows only the
    /// reason code's text, not the message (see the type remarks). Returns false if the user is not connected.
    /// </summary>
    public static bool Disconnect(World server, Entity userEntity, string message, ConnectionStatusChangeReason reason = ConnectionStatusChangeReason.Kicked)
    {
        try
        {
            var bootstrap = server.GetExistingSystemManaged<ServerBootstrapSystem>();
            if (bootstrap == null) return false;
            if (!TryGetConnection(bootstrap, userEntity, out var connection)) return false;
            var layer = bootstrap._NetServer;
            if (layer == null) return false;
            layer.Disconnect(connection, reason, message);
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"PlayerControl.Disconnect failed: {e}");
            return false;
        }
    }
}
