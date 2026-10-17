using System;
using System.Collections.Generic;
using Progenitor.Events;
using Progenitor.Ui;
using Progenitor.WorldState;
#if !PROGENITOR_TESTS
using ProjectM;
using ProjectM.Network;
using Unity.Entities;
#endif

namespace Progenitor.Networking;

/// <summary>One bounded request to recover server-owned state after a client Core generation changes.</summary>
internal sealed class HandshakeRefreshGate
{
    internal const double CooldownSeconds = 5;
    // Let the existing five-second post-ok snapshot throttle expire before asking for a reply.
    internal const double DispatchDelaySeconds = HandshakeReAnswer.MinSecondsBetween + 0.1;
    internal const int MaxPeers = 256;
    private readonly Dictionary<int, Entry> _peers = new();
    private long _serial;
    private sealed class Entry
    {
        internal double Last;
        internal ulong PlatformId;
        internal long Pending;
    }

    internal bool TryAdmit(int userIndex, ulong platformId, bool handshakeOk, ReadOnlySpan<byte> payload,
        double now, out long ticket)
    {
        ticket = 0;
        if (userIndex < 0 || platformId == 0 || !handshakeOk || payload.Length != 1 || payload[0] != 1
            || double.IsNaN(now) || double.IsInfinity(now)) return false;
        if (_peers.TryGetValue(userIndex, out var entry))
        {
            if (entry.PlatformId != platformId) return false; // Disconnect must retire the previous occupant.
            if (entry.Pending != 0 || now < entry.Last || now - entry.Last < CooldownSeconds) return false;
        }
        else
        {
            if (_peers.Count >= MaxPeers) return false;
            _peers.Add(userIndex, entry = new Entry { PlatformId = platformId });
        }
        entry.Last = now;
        ticket = entry.Pending = ++_serial;
        return true;
    }

    internal bool Finish(int userIndex, ulong platformId, long ticket)
    {
        if (!_peers.TryGetValue(userIndex, out var entry) || entry.PlatformId != platformId
            || ticket == 0 || entry.Pending != ticket) return false;
        entry.Pending = 0;
        return true;
    }
    internal void Forget(int userIndex) => _peers.Remove(userIndex);
    internal void Clear() => _peers.Clear();
}

public static partial class Handshake
{
    /// <summary>
    /// Client-to-server refresh, version byte 1. Requires an updated Core on both sides;
    /// no old-peer fallback changes the client's authoritative config locks.
    /// </summary>
    public const int RefreshEventId = 0x50726846; // PrhF
    private static readonly HandshakeRefreshGate RefreshRequests = new();
    private static readonly object ClientContextOwner = new();
    private static IDisposable? _worldLoadedSub;
#if !PROGENITOR_TESTS
    private static bool _heldClientConnection;

    /// <summary>
    /// JOINFLOW4: the client is connected or connecting (Loading, Running, Reconnecting), as the connection watch last
    /// saw it. With a handshake result other than ok, a join is between its connect and its answer.
    /// </summary>
    internal static bool ClientConnectionHeld => _heldClientConnection;

    /// <summary>
    /// How many connections this Core generation has seen the client take (connecting or connected after not), so a
    /// reader can tell a new join from the one it saw leave even when the client world survives the leave.
    /// </summary>
    internal static int ClientConnectionSerial { get; private set; }
    private static bool _onDisconnectScreen;
#endif

    /// <summary>Last handshake result seen on the client, for UI or diagnostics.</summary>
    public static string? LastClientResult { get; private set; }

    /// <summary>Mods workspace close reason for <see cref="ClientWorldEpoch.EndedSource"/>.</summary>
    internal const string WorkspaceCloseWorldEnded = "world-ended";

    /// <summary>
    /// Mods workspace close reason for connection-ended and handshake epoch change.
    /// </summary>
    internal const string WorkspaceCloseServerContextCleared = "server-context-cleared";

    /// <summary>
    /// Owned notification the Mods workspace subscribes to. Raised on
    /// <see cref="ClientWorldEpoch.EndedSource"/>, connection-ended, and handshake epoch
    /// change. Argument is <see cref="WorkspaceCloseWorldEnded"/> or
    /// <see cref="WorkspaceCloseServerContextCleared"/>. Not raised on a Core-only refresh
    /// of a surviving world.
    /// </summary>
    internal static event Action<string>? ServerContextCleared;

    /// <summary>
    /// The client's connection to its server ended: <c>ClientState</c> Error/Idle after a held
    /// connection (kick, walk to the menu, timeout — <c>Client_0</c> may survive), or
    /// <c>Client_0</c> ended. Unlike <see cref="ServerContextCleared"/> it is <b>not</b> raised
    /// on a handshake epoch change, which happens inside the next join after its first packets
    /// may already have arrived. May be raised twice for one leave (Error, then the world
    /// ending); handlers must be idempotent.
    /// </summary>
    internal static event Action? ClientConnectionEnded;

    /// <summary>
    /// Handshake-generation token for this client's server context. Advanced on connection end
    /// and on a new join request; not on <see cref="ClientWorldEpoch.CreatedSource"/> of a
    /// surviving world (a Core-only refresh).
    /// </summary>
    internal static int ClientEpoch { get; private set; }

    /// <summary>Handshake epoch of the last <see cref="InvalidateClientServerContext"/>.</summary>
    internal static int ClearedEpoch { get; private set; }

    /// <summary>Handshake epoch of the last successful restore, or 0.</summary>
    internal static int AcceptedEpoch { get; private set; }

    /// <summary>
    /// Epoch a late ConfigSync / handshake packet is stamped with: the accepted join while
    /// <see cref="LastClientResult"/> is still ok, otherwise the last accepted epoch so a
    /// packet after clear compares older than <see cref="ClearedEpoch"/>.
    /// </summary>
    internal static int IncomingPacketEpoch =>
        string.Equals(LastClientResult, "ok", StringComparison.Ordinal) ? ClientEpoch : AcceptedEpoch;

    /// <summary>
    /// Subscribe to <see cref="WorldLoaded"/> <see cref="ClientWorldEpoch.EndedSource"/> and to
    /// the client-side connection-ended signal
    /// (<c>ClientBootstrapSystem.OnClientDisconnected</c> writes <c>ClientState.Error</c> and
    /// does not destroy <c>Client_0</c> — FMAN17). <see cref="WorldReady"/> only fires when a
    /// new <c>Client_0</c> is created, which is too late for kick-to-menu / walk-to-menu.
    /// </summary>
    internal static void InstallClientServerContextWatch()
    {
#if !PROGENITOR_TESTS
        if (Plugin.IsServerProcess) return;
#endif
        if (_worldLoadedSub != null) return;
        _worldLoadedSub = EventBus.On<WorldLoaded>(OnClientWorldLoaded, priority: 900, owner: ClientContextOwner);
#if !PROGENITOR_TESTS
        _heldClientConnection = false;
        _onDisconnectScreen = false;
        Plugin.Frame += OnClientConnectionWatch;
#endif
    }

    internal static void UninstallClientServerContextWatch()
    {
#if !PROGENITOR_TESTS
        try { Plugin.Frame -= OnClientConnectionWatch; } catch { /* teardown */ }
        _heldClientConnection = false;
        _onDisconnectScreen = false;
#endif
        try { _worldLoadedSub?.Dispose(); } catch { /* teardown */ }
        _worldLoadedSub = null;
        try { EventBus.RemoveAll(ClientContextOwner); } catch { /* teardown */ }
        ModMenuInventory.ClearServerContext("uninstall");
    }

    /// <summary>
    /// Drop this client's server inventory and unlock synced settings. Does not store or
    /// compare raw entity ids; the epoch token is the identity. False when already cleared.
    /// </summary>
    internal static bool InvalidateClientServerContext(string reason, int epoch = 0)
    {
        LastClientResult = null;
        // REMOTEINST1: nothing the old server said about remote installs applies to the next one.
        Progenitor.Install.RemoteInstallClient.Shared.Reset();
        if (ClientEpoch > ClearedEpoch) ClearedEpoch = ClientEpoch;
        ForgetServerRequest();
        bool cleared = ModMenuInventory.ClearServerContext(reason, epoch);
        ConfigSync.RestoreLocal();
        return cleared;
    }

    internal static void RestoreClientServerContext(string reason, int epoch = 0)
    {
        if (!AcceptsIncomingServerPacket(IncomingPacketEpoch)) return;
        if (!ModMenuInventory.RestoreServerContext(reason, epoch) && !ModMenuInventory.ServerContextLive)
            return;
        AcceptedEpoch = IncomingPacketEpoch;
    }

    /// <summary>
    /// True when a ConfigSync / handshake packet's epoch is still this join. A packet older
    /// than <see cref="ClearedEpoch"/> is ignored and logged.
    /// </summary>
    internal static bool AcceptsIncomingServerPacket(int epoch)
    {
        if (epoch >= ClearedEpoch) return true;
        ModMenuInventory.LogStalePacket(epoch, ClientEpoch);
        return false;
    }

    /// <summary>
    /// Kick / walk-to-menu / timeout that may leave <c>Client_0</c> alive: no
    /// <see cref="WorldLoaded"/> Ended. Advances the handshake epoch and clears.
    /// </summary>
    internal static void NotifyClientConnectionEnded(int worldEpoch = 0)
    {
        EndClientJoin("disconnect", WorkspaceCloseServerContextCleared, worldEpoch);
        RaiseClientConnectionEnded();
    }

    /// <summary>
    /// The client reached the main menu: <c>ClientState.Idle</c> after the disconnect
    /// screen, or <c>Client_0</c> ended. Distinct from
    /// <see cref="NotifyClientConnectionEnded"/>, which fires at <c>Error</c> — the
    /// disconnect screen the deny panel is meant to occupy. Disposes that panel.
    /// </summary>
    internal static void NotifyClientReturnedToMenu()
    {
        try { ModManifestDeny.OnJoinEnded(); }
        catch (Exception e)
        {
            try { Plugin.Logger?.LogWarning("Handshake: deny panel menu dismiss: " + e.Message); }
            catch { /* a logger that throws must not pin the leave */ }
        }

        // CLIENTPOLICY3: the disconnect notice is shown again here, on the menu, not on the in-world banner.
        var handlers = ClientReturnedToMenu;
        if (handlers == null) return;
        foreach (Action handler in handlers.GetInvocationList())
        {
            try { handler(); }
            catch (Exception e)
            {
                try { Plugin.Logger?.LogWarning("Handshake: returned-to-menu notification: " + e.Message); }
                catch { /* a logger that throws must not pin the leave */ }
            }
        }
    }

    /// <summary>
    /// CLIENTPOLICY3: the client left the server's world for the main menu (<c>Client_0</c> ended, or Idle after the
    /// disconnect screen). Raised after <see cref="ClientConnectionEnded"/>; handlers must be idempotent.
    /// </summary>
    internal static event Action? ClientReturnedToMenu;

    /// <summary>
    /// A new join's handshake request (not a Core-only refresh on an ok connection).
    /// </summary>
    internal static void NotifyHandshakeEpochChanged(int worldEpoch = 0) =>
        EndClientJoin("epoch", WorkspaceCloseServerContextCleared, worldEpoch);

    internal static int AdvanceClientEpoch()
    {
        if (ClientEpoch == int.MaxValue) ClientEpoch = 0;
        return ++ClientEpoch;
    }

    private static void OnClientWorldLoaded(WorldLoaded ev)
    {
        if (ev == null) return;
        // Ended is a real leave. Created of a surviving Client_0 (Core-only refresh after
        // Forget+Note, or kick/walk that never destroyed the world) must not clear: the
        // handshake epoch is the identity, not the Created note.
        if (!string.Equals(ev.Source, ClientWorldEpoch.EndedSource, StringComparison.Ordinal))
            return;
        EndClientJoin("disconnect", WorkspaceCloseWorldEnded, ev.Epoch);
        RaiseClientConnectionEnded();
        NotifyClientReturnedToMenu();
    }

    private static void RaiseClientConnectionEnded()
    {
        var handlers = ClientConnectionEnded;
        if (handlers == null) return;
        foreach (Action handler in handlers.GetInvocationList())
        {
            try { handler(); }
            catch (Exception e)
            {
                try { Plugin.Logger?.LogWarning("Handshake: connection-ended notification: " + e.Message); }
                catch { /* a logger that throws must not pin the leave */ }
            }
        }
    }

    private static void EndClientJoin(string inventoryReason, string workspaceReason, int worldEpoch)
    {
        // Per-server mod pause ends on a real leave only; a new join's request (epoch) reconciles
        // the paused set itself, and a Core-only refresh never reaches here.
        if (string.Equals(inventoryReason, "disconnect", StringComparison.Ordinal))
        {
            try { ServerModPause.OnConnectionEnded(); }
            catch (Exception e)
            {
                try { Plugin.Logger?.LogWarning("Handshake: mod pause resume on leave threw: " + e.Message); }
                catch { /* a logger that throws must not pin the leave */ }
            }
        }
        // Review 2026-10-07: the loopback alias never outlives its reconnect (a new join or a leave ends it).
        ConnectHostAlias.OnJoinBoundary(newJoin: string.Equals(inventoryReason, "epoch", StringComparison.Ordinal));
        AdvanceClientEpoch();
        InvalidateClientServerContext(inventoryReason, worldEpoch);
        RaiseServerContextCleared(workspaceReason);
    }

    private static void RaiseServerContextCleared(string reason)
    {
        var handlers = ServerContextCleared;
        if (handlers == null) return;
        foreach (Action<string> handler in handlers.GetInvocationList())
        {
            try { handler(reason); }
            catch (Exception e)
            {
                try { Plugin.Logger?.LogWarning("Handshake: Mods workspace close notification: " + e.Message); }
                catch { /* a logger that throws must not pin the leave */ }
            }
        }
    }

#if !PROGENITOR_TESTS
    /// <summary>
    /// Client-side connection-ended seam. <c>ClientBootstrapSystem.OnClientDisconnected</c>
    /// (FMAN17) sets <c>ClientState.Error</c> and does not destroy <c>Client_0</c>, so
    /// <see cref="WorldLoaded"/> Ended never fires for a kick or a walk back to the menu.
    /// Idle is the walk-to-menu / Back-from-error landing. Server-side Handshake still uses
    /// <c>User.IsConnected</c> and <see cref="PlayerDisconnected"/>.
    /// </summary>
    private static void OnClientConnectionWatch()
    {
        // Per-server mod pause: report resumed mods that loaded late. Before any world check, so
        // it also runs on the main menu after a leave.
        try { ServerModPause.Tick(); }
        catch { /* evidence only */ }
        try
        {
            // Every frame: Worlds.Client returns only a created world (no second boxed IsCreated).
            var world = Worlds.Client;
            if (world == null) return;
            var bootstrap = world.GetExistingSystemManaged<ClientBootstrapSystem>();
            if (bootstrap == null) return;
            var state = bootstrap.ClientState;
            // Reconnecting is still this connection: Running -> Reconnecting -> Error must end it.
            bool held = state == ClientState.Running
                || state == ClientState.Loading
                || state == ClientState.PreWarmingShaders
                || state == ClientState.Reconnecting;
            bool ended = state == ClientState.Error || state == ClientState.Idle;
            // Per-server mod pause: a join that reaches Running with no handshake request is a
            // server without Progenitor once the grace period passes.
            ServerModPause.OnClientFrame(state == ClientState.Running);
            if (_heldClientConnection && ended)
                NotifyClientConnectionEnded();
            if (held && !_heldClientConnection) ClientConnectionSerial++;
            _heldClientConnection = held;
            // Error is the disconnect screen the deny panel occupies. Idle after that
            // is the main menu: dismiss, do not leave the overlay over the next world.
            if (_onDisconnectScreen && state == ClientState.Idle)
                NotifyClientReturnedToMenu();
            _onDisconnectScreen = state == ClientState.Error;
        }
        catch
        {
            /* a ClientState read must not pin server evidence */
        }
    }
#endif

#if !PROGENITOR_TESTS
    private static readonly object RefreshOwner = new();

    // Called by Runtime after Bootstrap has loaded all consumer plugins into the new Core.
    internal static void RequestRefreshAfterCoreSwap()
    {
        if (Plugin.IsServerProcess || !_installed) return;
        // Per-server mod pause: a request the previous generation deferred and never answered (its
        // queued reply was dropped with it) is answered now, with this generation's rows.
        try
        {
            // Only on the same live connection: the disconnect screen keeps Client_0 alive, so a
            // created world alone is not a connection.
            bool live = false;
            var client = Worlds.Client;
            if (client != null && client.IsCreated)
            {
                var bootstrap = client.GetExistingSystemManaged<ClientBootstrapSystem>();
                var state = bootstrap == null ? ClientState.Idle : bootstrap.ClientState;
                live = state == ClientState.Running || state == ClientState.Loading || state == ClientState.PreWarmingShaders;
            }
            if (ServerModPause.TakeCarriedRequest(live, out var carried, out bool truncated))
            {
                RecordServerRequest(carried, truncated);
                ServerModPause.OnRequest(carried, truncated,
                    () => SendReply(carried, "answering the request the previous Core generation deferred"));
            }
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("Handshake: carried request reply threw: " + e.Message); }
        try
        {
            bool sent = CustomEvents.SendToServer(RefreshEventId, new byte[] { 1 });
            Plugin.Logger?.LogInfo("Handshake: client Core refresh requested after plugins reloaded; sent=" + sent);
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("Handshake refresh request: " + e.Message); }
    }

    private static unsafe void OnRefreshRequest(int _, Entity userEntity, User user, byte* payload, int length)
    {
        if (payload == null || length != 1 || userEntity == Entity.Null) return;
        try
        {
            var world = Worlds.Server;
            if (world == null || !world.IsCreated || !world.EntityManager.HasComponent<User>(userEntity)) return;
            var current = world.EntityManager.GetComponentData<User>(userEntity);
            if (!current.IsConnected) return;
            int index = current.Index;
            ulong platform = current.PlatformId;
            // REASK1: also while a soft re-ask waits on this client: the request it missed during its own swap is resent.
            if (!RefreshRequests.TryAdmit(index, platform,
                    HandshakeReask.RefreshAllowed(AnswerWatch.GetStatus(index) == HandshakeAnswerWatch.Status.Ok, SoftReaskPending(index)),
                    new ReadOnlySpan<byte>(payload, length), LiveClock(), out long ticket)) return;
            Dev.MainThread.PostDelayed(HandshakeRefreshGate.DispatchDelaySeconds, () =>
            {
                if (!RefreshRequests.Finish(index, platform, ticket)) return;
                try
                {
                    // The captured Entity includes its version. A reconnect or world teardown
                    // cannot send the old user's scheduled request to a later occupant.
                    if (!world.IsCreated || Worlds.Server?.Pointer != world.Pointer
                        || !world.EntityManager.HasComponent<User>(userEntity)) return;
                    var connected = world.EntityManager.GetComponentData<User>(userEntity);
                    if (!connected.IsConnected || connected.Index != index || connected.PlatformId != platform
                        || !HandshakeReask.RefreshAllowed(AnswerWatch.GetStatus(index) == HandshakeAnswerWatch.Status.Ok, SoftReaskPending(index))) return;
                    var packed = PackRequestNow(out string requestStamp);
                    // Mod compatibility C1/C2: the client's new generation must learn again that this server reads tables.
                    var refreshRows = new List<ModRecord>(packed.Rows);
                    Compare.MergeCapabilityBits(refreshRows, ModCompatCaps.RequestBitsNow());
                    Compare.MergeCapabilityBits(refreshRows, Capability.ItemDefinitionPriority | Capability.ServerLogs);
                    var list = new ModList();
                    foreach (var record in refreshRows) list.Add(record);
                    bool sent = CustomEvents.SendToUser(world, userEntity, RequestEventId, list);
                    if (sent) RequirementWatch.NoteSent(index, requestStamp);
                    Plugin.Logger?.LogInfo("Handshake: refreshed server request for client Core, user=" + index + "; sent=" + sent);
                }
                catch (Exception e) { Plugin.Logger?.LogWarning("Handshake refresh dispatch: " + e.Message); }
            }, RefreshOwner);
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("Handshake refresh receive: " + e.Message); }
    }

    private static void UninstallRefresh()
    {
        CustomEvents.RemoveClientHandler(RefreshEventId);
        Dev.MainThread.DropOwnedBy(RefreshOwner, typeof(Handshake).Assembly);
        RefreshRequests.Clear();
        UninstallClientServerContextWatch();
    }
#endif
}
