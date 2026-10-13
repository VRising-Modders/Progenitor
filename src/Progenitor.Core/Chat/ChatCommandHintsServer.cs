#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using Progenitor.Commands;
using Progenitor.Install;
using Progenitor.Networking;
using ProjectM;
using ProjectM.Network;
using Unity.Entities;

namespace Progenitor.Chat;

/// <summary>
/// Chat command hints, server side: answers a player's request on <c>'PrCh'</c> with the chat commands that player can
/// run (<see cref="CommandHintCatalog"/>), in pages, to that player only. A hello goes after an ok handshake to every
/// client that declared <see cref="Handshake.Capability.CommandHints"/>, and again to those clients a second after the
/// command list stops changing (a plugin loaded or unloaded, routing settings applied). Main thread.
/// </summary>
internal static class ChatCommandHintsServer
{
    private const double SettleSeconds = 1;

    private static bool _installed;
    private static IDisposable? _disconnected;
    private static readonly HashSet<int> Capable = new();
    private static readonly ChatCommandHintsRateLimit Limit = new();
    private static int _announced;
    private static int _seen;
    private static double _seenAt;

    private static double Now() => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;

    internal static unsafe void Install()
    {
        if (_installed || !Plugin.IsServerProcess) return;
        _installed = true;
        _announced = _seen = CommandRegistry.Generation;
        try { CustomEvents.OnReceiveFromClient(CustomEventPolicy.ChatCommandHintsEventId, OnRequest); }
        catch (Exception e) { Plugin.Logger?.LogWarning("ChatCommandHints: the request channel could not be registered: " + e.Message); }
        try
        {
            _disconnected = Events.EventBus.On<Events.PlayerDisconnected>(ev =>
            {
                Capable.Remove(ev.User.Index);
                Limit.Forget(ev.User.Index);
            }, priority: 897);
        }
        catch { /* a reused index is re-added by its own handshake */ }
        try { Plugin.Frame += Tick; }
        catch { /* clients then learn of a change on their next connection */ }
    }

    internal static unsafe void Uninstall()
    {
        if (!_installed) return;
        _installed = false;
        try { Plugin.Frame -= Tick; } catch { /* teardown */ }
        try { CustomEvents.RemoveClientHandler(CustomEventPolicy.ChatCommandHintsEventId); } catch { /* teardown */ }
        _disconnected?.Dispose();
        _disconnected = null;
        Capable.Clear();
        Limit.Clear();
    }

    /// <summary>After an ok handshake: tell a client that declared the capability that this server has the channel.</summary>
    internal static void OnHandshakeOk(World? world, Entity userEntity, Handshake.Capability clientCaps)
    {
        if (!_installed || world == null || (clientCaps & Handshake.Capability.CommandHints) == 0) return;
        try
        {
            if (!world.IsCreated || !world.EntityManager.HasComponent<User>(userEntity)) return;
            var user = world.EntityManager.GetComponentData<User>(userEntity);
            if (!user.IsConnected) return;
            Capable.Add(user.Index);
            var bootstrap = world.GetExistingSystemManaged<ServerBootstrapSystem>();
            if (bootstrap != null)
                CustomEvents.SendToUser(bootstrap, user.Index, CustomEventPolicy.ChatCommandHintsEventId,
                    new ReadOnlySpan<byte>(ChatCommandHintsWire.EncodeHello(CommandRegistry.Generation)));
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("ChatCommandHints: hello threw: " + e.Message); }
    }

    private static void Tick()
    {
        int generation = CommandRegistry.Generation;
        if (generation == _announced) return;
        double now = Now();
        if (generation != _seen)
        {
            _seen = generation;
            _seenAt = now;
            return;
        }

        if (now - _seenAt < SettleSeconds) return;
        _announced = generation;
        if (Capable.Count == 0) return;
        try
        {
            var world = Worlds.Server;
            if (world == null || !world.IsCreated) return;
            var bootstrap = world.GetExistingSystemManaged<ServerBootstrapSystem>();
            if (bootstrap == null) return;
            byte[] hello = ChatCommandHintsWire.EncodeHello(generation);
            foreach (int index in new List<int>(Capable))
                CustomEvents.SendToUser(bootstrap, index, CustomEventPolicy.ChatCommandHintsEventId, new ReadOnlySpan<byte>(hello));
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("ChatCommandHints: change notice threw: " + e.Message); }
    }

    private static unsafe void OnRequest(int _, Entity userEntity, User user, byte* payload, int length)
    {
        if (payload == null || length <= 0) return;
        if (!ChatCommandHintsWire.TryDecodeRequest(new ReadOnlySpan<byte>(payload, length), out int nonce)) return;
        try
        {
            // Re-read the User the event resolved; nothing about privilege travels in the request.
            var world = Worlds.Server;
            if (world == null || !world.IsCreated || !world.EntityManager.HasComponent<User>(userEntity)) return;
            var current = world.EntityManager.GetComponentData<User>(userEntity);
            if (!current.IsConnected || Handshake.UserAnswerStatus(current.Index) != "ok") return;
            if (!Limit.Allow(current.Index, Now())) return;

            // The same admin tests the dispatcher applies: adminOnly follows CommandContext.IsAdmin, and a
            // command gated by the admin-list rule (AdminListGateAttribute, e.g. .debug) follows AuthorizeShared.
            bool admin = ServerAdminList.IsProgenitorAdmin(current.PlatformId, current.IsAdmin);
            bool listedAdmin = ServerAdminList.AuthorizeShared(current.PlatformId, current.IsAdmin) != RemoteAuthorizedBy.None;
            int stamp = CommandRegistry.Generation;
            var catalog = CommandHintCatalog.Build(admin, listedAdmin);
            var pages = ChatCommandHintsWire.EncodePages(nonce, stamp, catalog);
            var bootstrap = world.GetExistingSystemManaged<ServerBootstrapSystem>();
            if (bootstrap == null) return;
            foreach (var page in pages)
                CustomEvents.SendToUser(bootstrap, current.Index, CustomEventPolicy.ChatCommandHintsEventId, new ReadOnlySpan<byte>(page));
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("ChatCommandHints: request threw: " + e.Message); }
    }
}
#endif
