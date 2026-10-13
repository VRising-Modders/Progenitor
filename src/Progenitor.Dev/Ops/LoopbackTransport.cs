// Same build gate as ExtraInstanceServerAuth: dev builds only (PROGENITOR_EXTRA_INSTANCE).
#if PROGENITOR_EXTRA_INSTANCE
using System;
#if !PROGENITOR_TESTS
using Il2CppInterop.Runtime;
using ProjectM;
using Stunlock.Network;
using Stunlock.Network.Lidgren;
using Stunlock.Network.Steam;
#endif

namespace Progenitor.Dev.Ops;

/// <summary>
/// A second, loopback-only Lidgren listener next to a dedicated server's Steam transport, so
/// Steam-less extra clients on the server machine can join while real players keep joining over
/// Steam by IP.
///
/// <para><b>Why.</b> <c>LanMode</c> replaces the Steam socket with Lidgren (evidence 56: Steam
/// clients can then no longer join). The game already has the two-transport shape:
/// <c>ServerBootstrapSystem.BeginSetupServer</c> wraps the Steam transport in
/// <c>CompositeServerTransportLayer(steam, new LidgrenServer("ProjectM", publicIP, bind,
/// ServerFallbackPort))</c>, but only when <c>UseDoubleTransportLayer</c> is set <b>and</b> the
/// process is a hosted child (<c>GameBootstrap._ParentProcess != null</c>; native server
/// <c>BeginSetupServer</c>). A dedicated server never takes that branch.</para>
///
/// <para><b>What.</b> Once <c>ServerBootstrapSystem._NetServer</c> exists and before anyone has
/// connected, this builds the same composite by hand: it keeps the network layer's transport
/// callbacks, creates a <c>LidgrenServer</c> bound to <c>127.0.0.1</c>, wraps both in
/// <c>CompositeServerTransportLayer</c>, restores the callbacks on the composite and points
/// <c>ServerNetworkLayer._TransportLayer</c> at it. No Harmony and no native patch. The Steam
/// socket and its connections are untouched. Refused when anyone is connected (the composite
/// tracks connections it saw open), when the transport is not Steam (LanMode) or is already a
/// composite. The listener lives for the process; a Dev reload finds the composite and stops.</para>
/// </summary>
internal static class LoopbackTransport
{
    private const string LogPrefix = "extra-auth(server): loopback transport:";

    /// <summary>Port offset from the game port when no port is configured (query port is +1).</summary>
    internal const int DefaultPortOffset = 2;

    /// <summary>Pure: the configured port when valid, else game port + <see cref="DefaultPortOffset"/>.</summary>
    internal static int ChoosePort(int configuredPort, int gamePort)
    {
        if (configuredPort is > 0 and < 65536 && configuredPort != gamePort) return configuredPort;
        int p = gamePort + DefaultPortOffset;
        return p is > 0 and < 65536 ? p : 0;
    }

#if !PROGENITOR_TESTS
    private static readonly object Gate = new();
    private static bool _subscribed;
    private static int _configuredPort;
    private static int _frames;

    /// <summary>The Lidgren port once installed in this process, else 0.</summary>
    internal static int InstalledPort { get; private set; }

    internal static void Start(int configuredPort)
    {
        lock (Gate)
        {
            if (_subscribed) return;
            _configuredPort = configuredPort;
            _frames = 0;
            Progenitor.Plugin.Frame += Tick;
            _subscribed = true;
        }
    }

    internal static void Stop()
    {
        lock (Gate)
        {
            if (!_subscribed) return;
            Progenitor.Plugin.Frame -= Tick;
            _subscribed = false;
        }
    }

    private static void Tick()
    {
        if ((++_frames % 10) != 0) return;
        try
        {
            if (TryInstall()) Stop();
        }
        catch (Exception e)
        {
            Line($"failed: {e}");
            Stop();
        }
    }

    /// <summary>True when finished (installed or refused for good); false to try again later.</summary>
    private static bool TryInstall()
    {
        var world = Progenitor.Worlds.Server;
        if (world == null || !world.IsCreated) return false;
        var boot = world.GetExistingSystemManaged<ServerBootstrapSystem>();
        if (boot == null) return false;
        var net = boot._NetServer;
        if (net == null) return false;

        var layer = net.TryCast<ServerNetworkLayer>();
        if (layer == null)
        {
            Line($"network layer is {net.Cast<Il2CppSystem.Object>().GetIl2CppType().FullName}, not ServerNetworkLayer; not installed");
            return true;
        }
        var transport = layer._TransportLayer;
        if (transport == null) return false;
        var existing = transport.TryCast<CompositeServerTransportLayer>();
        if (existing != null)
        {
            Line("the transport is already a composite (installed earlier in this process, or a hosted double transport); nothing to do");
            return true;
        }
        if (transport.TryCast<ServerSteamTransportLayer>() == null)
        {
            Line($"transport is {transport.Cast<Il2CppSystem.Object>().GetIl2CppType().FullName}, not Steam (LanMode?); extra clients can join it directly; not installed");
            return true;
        }
        if (layer.ConnectionsCount > 0)
        {
            Line($"{layer.ConnectionsCount} connection(s) already open; installing now would orphan them. Restart the server; not installed");
            return true;
        }

        var host = boot._ServerHostData;
        int gamePort = host != null ? host.ServerPort : 0;
        int port = ChoosePort(_configuredPort, gamePort);
        if (port == 0)
        {
            Line($"no usable port (configured {_configuredPort}, game port {gamePort}); not installed");
            return true;
        }

        var callbacks = transport.Callbacks;
        var lidgren = new LidgrenServer(
            "ProjectM",
            "127.0.0.1",
            Il2CppSystem.Net.IPAddress.Parse("127.0.0.1"),
            (ushort)port,
            default);
        var composite = new CompositeServerTransportLayer(transport, lidgren.Cast<IServerTransportLayer>());
        composite.Callbacks = callbacks;
        layer._TransportLayer = composite.Cast<IServerTransportLayer>();
        InstalledPort = port;
        try
        {
            Plugin.Logger?.LogWarning(
                $"{LogPrefix} Steam transport kept; Lidgren listening on 127.0.0.1:{port} for extra instances (dev only). " +
                $"Connect extra clients to 127.0.0.1:{port}.");
        }
        catch { /* logger can be gone */ }
        return true;
    }

    private static void Line(string message)
    {
        try { Plugin.Logger?.LogInfo($"{LogPrefix} {message}"); }
        catch { /* logger can be gone */ }
    }
#endif
}
#endif
