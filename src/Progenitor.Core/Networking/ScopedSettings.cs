using System;
using System.Collections.Generic;
using System.IO;
using Progenitor.Events;
using Progenitor.Install;
using ProjectM;
using ProjectM.Network;
using Unity.Entities;

namespace Progenitor.Networking;

/// <summary>Explicit settings publication and authenticated per-player edits. All state belongs to this Core generation.</summary>
internal static partial class ScopedSettings
{
    internal const int EventId = 0x50725374; // PrSt
    private static readonly ScopedSettingsClient Client = new(bytes => CustomEvents.SendToServer(EventId, bytes))
    { Warning = message => Plugin.Logger?.LogWarning(message) };
    internal static IReadOnlyList<ScopedSettingRecord> ClientRows => Client.Rows;
    internal static bool ClientReady => Client.Ready;
    internal static bool ClientCanEditServerWide => Client.CanEditServerWide;
    internal static string ClientServerName => Client.ServerName;
    internal static int ClientRevision => Client.Revision;
    internal static event Action<string, string>? ClientValueChanged
    {
        add => Client.ValueChanged += value;
        remove => Client.ValueChanged -= value;
    }
    private static readonly Dictionary<int, Peer> Peers = new();
    private static readonly List<int> StalePeers = new();
    private static double _nextPoll;
    private static int _sending; // peers with queued packets; per-frame work happens only while nonzero
    private static IDisposable? _disconnect, _world;
    private static bool _installed;

    private sealed class Peer
    {
        internal Entity Entity;
        internal ulong Account;
        internal int Revision = -1;
        internal bool Admin;
        internal bool Presentation; // declared Capability.ScopedSettingsPresentation: sent version 2 rows
        internal Guid Generation;
        internal Guid PreviousGeneration; // writes sent before the client received the newer snapshot
        internal double SendStarted;
        internal Queue<byte[]> Outgoing = new();
        internal Dictionary<string, string> Values = new(StringComparer.Ordinal);
    }
    private static double Now() => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;

    internal static string? ReadClient(string id) => Client.Read(id);
    internal static bool HasClient(string id, string type) => Client.Has(id, type);

    internal static IDisposable Request(string id, string value, string? expected, Action<ServerSettingResult> done) =>
        Client.Request(id, value, expected, done, Now());

    internal static unsafe void Install()
    {
        if (_installed) return;
        if (Plugin.IsServerProcess) CustomEvents.OnReceiveFromClient(EventId, OnRequest);
        else CustomEvents.OnReceive(EventId, OnResponse);
        _disconnect = EventBus.On<PlayerDisconnected>(ev => { if (Peers.Remove(ev.User.Index)) CountSending(); });
        _world = EventBus.On<WorldReady>(_ => { Peers.Clear(); _sending = 0; ClearClient(); });
        Plugin.Frame += Tick;
        if (!Plugin.IsServerProcess)
            Progenitor.Ui.ScopedSettingsRows.ServerMods = guid =>
            {
                // The server's feature table lists every mod it runs (cleared on disconnect).
                var table = ModCompatNet.PeerTables.Get(ModCompatNet.ServerPeer);
                if (table == null || !RemoteInstallEvents.ClientConnected()) return null;
                if (table.Mods.ContainsKey(guid)) return true;
                return table.More ? null : false;
            };
        if (!Plugin.IsServerProcess) ModCompatNet.PeerTables.Changed += OnServerTable;
        _installed = true;
    }

    internal static unsafe void Uninstall()
    {
        Plugin.Frame -= Tick;
        _disconnect?.Dispose(); _disconnect = null; _world?.Dispose(); _world = null;
        CustomEvents.RemoveHandler(EventId, OnResponse); CustomEvents.RemoveClientHandler(EventId, OnRequest);
        foreach (var row in new List<Registration>(Entries.Values)) row.Dispose();
        Stores.Clear(); Peers.Clear(); _sending = 0; ClearClient(); Client.ClearSubscribers(); ResetPresentation();
        Progenitor.Ui.ScopedSettingsRows.ServerMods = null;
        ModCompatNet.PeerTables.Changed -= OnServerTable;
        _installed = false;
    }

    internal static void OnHandshakeOk(World? world, Entity entity, Handshake.Capability capabilities)
    {
        if (!_installed || world == null || !world.IsCreated || (capabilities & Handshake.Capability.ScopedSettings) == 0 || !world.EntityManager.HasComponent<User>(entity)) return;
        var user = world.EntityManager.GetComponentData<User>(entity);
        if (!user.IsConnected || user.PlatformId == 0) return;
        Peers[user.Index] = new Peer
        {
            Entity = entity, Account = user.PlatformId,
            Presentation = (capabilities & Handshake.Capability.ScopedSettingsPresentation) != 0,
        };
        // Sent on the next frame, after the existing handshake/config/catalog sequence.
        _nextPoll = 0;
    }

    /// <summary>The server's feature table (its mod list) changed: re-evaluate rules that ask <c>ServerHasMod</c>.</summary>
    private static void OnServerTable(int peer)
    {
        if (peer == ModCompatNet.ServerPeer) Progenitor.Ui.ModsMenu.MarkScopedValuesDirty();
    }

    private static void ClearClient() => Client.Clear("Disconnected from the server.");

    private static void Tick()
    {
        if (!_installed) return;
        double now = Now();
        if (!Plugin.IsServerProcess)
        {
            // Clears once on the disconnect edge; allocation-free while connected.
            Client.Tick(RemoteInstallEvents.ClientConnected(), now);
            return;
        }
        bool poll = now >= _nextPoll;
        if (!poll && _sending == 0) return;
        if (poll)
        {
            _nextPoll = now + 1;
            Stores.Tick(); // coalesced personal writes, also while no server world exists
        }
        var world = Worlds.Server;
        if (world == null || !world.IsCreated) { if (Peers.Count > 0) { Peers.Clear(); _sending = 0; } return; }
        if (poll) Poll(world, now);
        if (_sending > 0) SendQueued(world);
    }

    /// <summary>Once a second: editability, store writes, peer validation, snapshots and deltas.</summary>
    private static void Poll(World world, double now)
    {
        foreach (var row in Entries.Values)
        {
            bool editable = row.Schema.Personal || ServerSettings.IsEditable(row.Schema.Id);
            if (editable != row.Schema.Editable) { row.Schema.Editable = editable; _schemaRevision++; }
        }
        if (Peers.Count > 0)
        {
            try { CheckPresentation(now); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Scoped settings presentation: " + e.Message); }
        }
        StalePeers.Clear();
        foreach (var pair in Peers)
        {
            var peer = pair.Value;
            if (!PeerValid(world, peer, out var user)) { StalePeers.Add(pair.Key); continue; }
            try
            {
                if (peer.Outgoing.Count > 0 && now - peer.SendStarted > 15)
                {
                    peer.Outgoing.Clear(); peer.Revision = -1;
                }
                if (peer.Outgoing.Count == 0)
                {
                    bool admin = ServerAdminList.AuthorizeShared(user.PlatformId, user.IsAdmin) != RemoteAuthorizedBy.None;
                    if (peer.Revision != _schemaRevision || peer.Admin != admin) Snapshot(peer, admin);
                    else Deltas(peer);
                }
            }
            catch (Exception e) { Plugin.Logger?.LogWarning("Scoped settings: " + e.Message); }
        }
        foreach (int index in StalePeers) Peers.Remove(index);
        StalePeers.Clear();
        CountSending();
    }

    /// <summary>Per frame while packets wait: up to four per peer, so no frame sends a whole catalog.</summary>
    private static void SendQueued(World world)
    {
        var bootstrap = world.GetExistingSystemManaged<ServerBootstrapSystem>();
        if (bootstrap == null) return;
        foreach (var peer in Peers.Values)
        {
            if (peer.Outgoing.Count == 0) continue;
            try
            {
                // A peer that left since the last poll drops its packets; the poll removes it.
                if (!PeerValid(world, peer, out var user)) { peer.Outgoing.Clear(); continue; }
                for (int i = 0; i < 4 && peer.Outgoing.Count > 0; i++)
                {
                    if (!CustomEvents.SendToUser(bootstrap, user.Index, EventId, peer.Outgoing.Peek())) break;
                    peer.Outgoing.Dequeue();
                }
            }
            catch (Exception e) { peer.Outgoing.Clear(); Plugin.Logger?.LogWarning("Scoped settings: " + e.Message); }
        }
        CountSending();
    }

    private static bool PeerValid(World world, Peer peer, out User user)
    {
        user = default;
        if (!world.EntityManager.Exists(peer.Entity) || !world.EntityManager.HasComponent<User>(peer.Entity)) return false;
        user = world.EntityManager.GetComponentData<User>(peer.Entity);
        return user.IsConnected && user.PlatformId == peer.Account && Handshake.UserAnswerStatus(user.Index) == "ok";
    }

    private static void CountSending()
    {
        int sending = 0;
        foreach (var peer in Peers.Values) if (peer.Outgoing.Count > 0) sending++;
        _sending = sending;
    }

    private static void Snapshot(Peer peer, bool admin)
    {
        var generation = Guid.NewGuid();
        var packets = new Queue<byte[]>();
        string name = ServerIdentity.HostName(Path.GetDirectoryName(BepInEx.Paths.BepInExRootPath));
        var rowPackets = new List<byte[]>();
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in Entries.Values)
        {
            try
            {
                var row = ForPeer(entry, peer.Account, peer.Presentation, Now());
                rowPackets.Add(RowPacket(generation, row, peer.Presentation));
                values[row.Id] = row.Value;
            }
            catch (Exception e) { Plugin.Logger?.LogWarning("Setting omitted from catalog " + entry.Schema.Id + ": " + e.Message); }
        }
        packets.Enqueue(ScopedSettingsWire.Encode(new ScopedSettingsPacket { Kind = 1, Generation = generation, Count = rowPackets.Count, CanEdit = admin, ServerName = name }));
        foreach (var packet in rowPackets) packets.Enqueue(packet);
        packets.Enqueue(ScopedSettingsWire.Encode(new ScopedSettingsPacket { Kind = 3, Generation = generation, Count = rowPackets.Count }));
        peer.PreviousGeneration = peer.Generation; peer.Generation = generation; peer.Revision = _schemaRevision; peer.Admin = admin; peer.Values = values; peer.Outgoing = packets; peer.SendStarted = Now();
    }

    private static void Deltas(Peer peer)
    {
        peer.SendStarted = Now();
        foreach (var entry in Entries.Values)
        {
            if (!peer.Values.ContainsKey(entry.Schema.Id))
            {
                try
                {
                    entry.Schema.Value = SentValue(entry, peer.Account);
                    ScopedSettingsWire.Encode(new ScopedSettingsPacket { Kind = 2, Generation = peer.Generation, Row = entry.Schema });
                    peer.Revision = -1;
                }
                catch { /* A malformed row does not hide the rest of the catalog. Retry after its value changes. */ }
                continue;
            }
            try
            {
                string value = SentValue(entry, peer.Account);
                if (peer.Values.TryGetValue(entry.Schema.Id, out var old) && value == old) continue;
                peer.Outgoing.Enqueue(ScopedSettingsWire.Encode(new ScopedSettingsPacket { Kind = 4, Generation = peer.Generation, Id = entry.Schema.Id, Value = value, Personal = entry.Schema.Personal }));
                peer.Values[entry.Schema.Id] = value;
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("Setting delta omitted " + entry.Schema.Id + ": " + e.Message);
                peer.Revision = -1; // Next snapshot excludes this malformed row, keeping other rows available.
            }
        }
    }

    private static unsafe void OnRequest(int _, Entity entity, User supplied, byte* payload, int length)
    {
        if (payload == null || !ScopedSettingsWire.TryDecode(new ReadOnlySpan<byte>(payload, Math.Max(0, length)), false, out var request)) return;
        var world = Worlds.Server;
        if (world == null || !world.IsCreated || !world.EntityManager.HasComponent<User>(entity)) return;
        var user = world.EntityManager.GetComponentData<User>(entity);
        if (!user.IsConnected || !Peers.TryGetValue(user.Index, out var peer) || peer.Account != user.PlatformId || peer.Entity != entity) return;
        var actor = new RemoteInstallActor(user.CharacterName.ToString(), user.PlatformId, user.IsAdmin, ServerAdminList.AuthorizeShared(user.PlatformId, user.IsAdmin));
        // A write sent before the client received the newest snapshot carries the previous generation.
        // Apply still checks that the row exists with the same scope and type, and the expected value.
        bool current = request.Generation != Guid.Empty &&
            (request.Generation == peer.Generation || request.Generation == peer.PreviousGeneration);
        var result = Apply(request, current && Handshake.UserAnswerStatus(user.Index) == "ok", actor);
        var bootstrap = world.GetExistingSystemManaged<ServerBootstrapSystem>();
        if (bootstrap != null) CustomEvents.SendToUser(bootstrap, user.Index, EventId, ScopedSettingsWire.Encode(new ScopedSettingsPacket
        {
            Kind = 6, Generation = request.Generation, RequestId = request.RequestId, Id = request.Id,
            Success = result.Success, Value = result.Value, Error = result.Error, Personal = request.Personal,
        }));
        _nextPoll = 0;
    }

    private static unsafe void OnResponse(int _, byte* payload, int length)
    {
        if (payload == null || !ScopedSettingsWire.TryDecode(new ReadOnlySpan<byte>(payload, Math.Max(0, length)), true, out var packet)) return;
        Client.Accept(packet, Now());
    }
}
