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
    internal static IReadOnlyList<ScopedSettingRecord> ClientRows => _clientRows;
    internal static bool ClientReady { get; private set; }
    internal static bool ClientCanEditServerWide { get; private set; }
    internal static string ClientServerName { get; private set; } = "";
    internal static int ClientRevision { get; private set; }
    internal static event Action<string, string>? ClientValueChanged;
    private static List<ScopedSettingRecord> _clientRows = new();
    private static readonly Dictionary<string, ScopedSettingRecord> ClientById = new(StringComparer.Ordinal);
    private static readonly Dictionary<Guid, Pending> PendingRequests = new();
    private static readonly Dictionary<int, Peer> Peers = new();
    private static readonly ScopedSettingsSnapshot SnapshotReceiver = new();
    private static Guid _clientGeneration;
    private static double _nextPoll;
    private static IDisposable? _disconnect, _world;
    private static bool _installed;

    private sealed class Peer
    {
        internal Entity Entity;
        internal ulong Account;
        internal int Revision = -1;
        internal bool Admin;
        internal Guid Generation;
        internal double SendStarted;
        internal Queue<byte[]> Outgoing = new();
        internal Dictionary<string, string> Values = new(StringComparer.Ordinal);
    }
    private sealed class Pending : IDisposable
    {
        internal Guid Id;
        internal string RowId = "";
        internal double Deadline;
        internal Action<ServerSettingResult>? Done;
        public void Dispose() { PendingRequests.Remove(Id); Done = null; }
    }
    private static double Now() => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;

    internal static string? ReadClient(string id) => ClientReady && ClientById.TryGetValue(id, out var row) ? row.Value : null;
    internal static bool HasClient(string id, string type) => ClientReady && ClientById.TryGetValue(id, out var row) && row.Personal && row.Type == type;

    internal static IDisposable Request(string id, string value, string? expected, Action<ServerSettingResult> done)
    {
        if (done == null) throw new ArgumentNullException(nameof(done));
        var pending = new Pending { Id = Guid.NewGuid(), RowId = id, Deadline = Now() + 8, Done = done };
        string error = !ClientReady || !ClientById.TryGetValue(id, out var row) ? "This server setting is unavailable." : "";
        if (error.Length == 0)
        {
            row = ClientById[id];
            if (!row.Editable || !row.Personal && !ClientCanEditServerWide) error = "Only server admins can change this setting.";
            if (PendingRequests.Count >= 32) error = "Too many pending setting changes.";
            foreach (var request in PendingRequests.Values) if (request.RowId == id) error = "A change to this setting is already pending.";
        }
        if (error.Length > 0) { done(new(false, ReadClient(id) ?? "", error)); return pending; }
        row = ClientById[id];
        PendingRequests.Add(pending.Id, pending);
        try
        {
            byte[] bytes = ScopedSettingsWire.Encode(new ScopedSettingsPacket
            {
                Kind = 5, Generation = _clientGeneration, RequestId = pending.Id, Id = id, Personal = row.Personal,
                Type = row.Type, Expected = expected ?? row.Value, Value = value,
            });
            if (!CustomEvents.SendToServer(EventId, bytes)) Complete(pending.Id, new(false, row.Value, "The connection did not accept the change."));
        }
        catch (Exception e) { Complete(pending.Id, new(false, row.Value, e.Message)); }
        return pending;
    }

    private static void Complete(Guid id, ServerSettingResult result)
    {
        if (!PendingRequests.TryGetValue(id, out var pending)) return;
        var done = pending.Done; pending.Dispose();
        try { done?.Invoke(result); } catch (Exception e) { Plugin.Logger?.LogWarning("Settings completion: " + e.Message); }
    }

    internal static unsafe void Install()
    {
        if (_installed) return;
        if (Plugin.IsServerProcess) CustomEvents.OnReceiveFromClient(EventId, OnRequest);
        else CustomEvents.OnReceive(EventId, OnResponse);
        _disconnect = EventBus.On<PlayerDisconnected>(ev => Peers.Remove(ev.User.Index));
        _world = EventBus.On<WorldReady>(_ => { Peers.Clear(); ClearClient(); });
        Plugin.Frame += Tick;
        _installed = true;
    }

    internal static unsafe void Uninstall()
    {
        Plugin.Frame -= Tick;
        _disconnect?.Dispose(); _disconnect = null; _world?.Dispose(); _world = null;
        CustomEvents.RemoveHandler(EventId, OnResponse); CustomEvents.RemoveClientHandler(EventId, OnRequest);
        foreach (var row in new List<Registration>(Entries.Values)) row.Dispose();
        foreach (var store in Stores.Values) { try { store.Flush(); } catch (Exception e) { Plugin.Logger?.LogWarning(e.Message); } }
        Stores.Clear(); Peers.Clear(); ClearClient(); ClientValueChanged = null; _installed = false;
    }

    internal static void OnHandshakeOk(World? world, Entity entity, Handshake.Capability capabilities)
    {
        if (!_installed || world == null || !world.IsCreated || (capabilities & Handshake.Capability.ScopedSettings) == 0 || !world.EntityManager.HasComponent<User>(entity)) return;
        var user = world.EntityManager.GetComponentData<User>(entity);
        if (!user.IsConnected || user.PlatformId == 0) return;
        Peers[user.Index] = new Peer { Entity = entity, Account = user.PlatformId };
        // Sent on the next frame, after the existing handshake/config/catalog sequence.
        _nextPoll = 0;
    }

    private static void ClearClient()
    {
        if (ClientReady || _clientRows.Count > 0) ClientRevision++;
        ClientReady = false; ClientCanEditServerWide = false; ClientServerName = "";
        _clientRows = new(); ClientById.Clear(); SnapshotReceiver.Reset();
        _clientGeneration = Guid.Empty;
        foreach (var id in new List<Guid>(PendingRequests.Keys)) Complete(id, new(false, "", "Disconnected from the server."));
    }

    private static void Tick()
    {
        if (!_installed) return;
        double now = Now();
        if (!Plugin.IsServerProcess)
        {
            if (!RemoteInstallEvents.ClientConnected()) { ClearClient(); return; }
            foreach (var pending in new List<Pending>(PendingRequests.Values))
                if (now >= pending.Deadline) Complete(pending.Id, new(false, ReadClient(pending.RowId) ?? "", "The server did not answer in time."));
            return;
        }
        var world = Worlds.Server;
        if (world == null || !world.IsCreated) { Peers.Clear(); return; }
        bool poll = now >= _nextPoll;
        if (poll)
        {
            _nextPoll = now + 1;
            foreach (var row in Entries.Values)
            {
                bool editable = row.Schema.Personal || ServerSettings.IsEditable(row.Schema.Id);
                if (editable != row.Schema.Editable) { row.Schema.Editable = editable; _schemaRevision++; }
            }
        }
        foreach (var pair in new List<KeyValuePair<int, Peer>>(Peers))
        {
            var peer = pair.Value;
            if (!world.EntityManager.Exists(peer.Entity) || !world.EntityManager.HasComponent<User>(peer.Entity)) { Peers.Remove(pair.Key); continue; }
            var user = world.EntityManager.GetComponentData<User>(peer.Entity);
            if (!user.IsConnected || user.PlatformId != peer.Account || Handshake.UserAnswerStatus(user.Index) != "ok") { Peers.Remove(pair.Key); continue; }
            try
            {
                if (peer.Outgoing.Count > 0 && now - peer.SendStarted > 15)
                {
                    peer.Outgoing.Clear(); peer.Revision = -1;
                }
                if (poll && peer.Outgoing.Count == 0)
                {
                    bool admin = ServerAdminList.AuthorizeShared(user.PlatformId, user.IsAdmin) != RemoteAuthorizedBy.None;
                    if (peer.Revision != _schemaRevision || peer.Admin != admin) Snapshot(peer, admin);
                    else Deltas(peer);
                }
                var bootstrap = world.GetExistingSystemManaged<ServerBootstrapSystem>();
                if (bootstrap == null) continue;
                // Spread catalogs over frames; no packet exceeds the custom-event limit.
                for (int i = 0; i < 4 && peer.Outgoing.Count > 0; i++)
                {
                    if (!CustomEvents.SendToUser(bootstrap, user.Index, EventId, peer.Outgoing.Peek())) break;
                    peer.Outgoing.Dequeue();
                }
            }
            catch (Exception e) { Plugin.Logger?.LogWarning("Scoped settings: " + e.Message); }
        }
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
                var row = entry.Schema;
                row.Value = entry.Read(peer.Account);
                row.Editable = row.Personal || ServerSettings.IsEditable(row.Id);
                rowPackets.Add(ScopedSettingsWire.Encode(new ScopedSettingsPacket { Kind = 2, Generation = generation, Row = row }));
                values[row.Id] = row.Value;
            }
            catch (Exception e) { Plugin.Logger?.LogWarning("Setting omitted from catalog " + entry.Schema.Id + ": " + e.Message); }
        }
        packets.Enqueue(ScopedSettingsWire.Encode(new ScopedSettingsPacket { Kind = 1, Generation = generation, Count = rowPackets.Count, CanEdit = admin, ServerName = name }));
        foreach (var packet in rowPackets) packets.Enqueue(packet);
        packets.Enqueue(ScopedSettingsWire.Encode(new ScopedSettingsPacket { Kind = 3, Generation = generation, Count = rowPackets.Count }));
        peer.Generation = generation; peer.Revision = _schemaRevision; peer.Admin = admin; peer.Values = values; peer.Outgoing = packets; peer.SendStarted = Now();
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
                    entry.Schema.Value = entry.Read(peer.Account);
                    ScopedSettingsWire.Encode(new ScopedSettingsPacket { Kind = 2, Generation = peer.Generation, Row = entry.Schema });
                    peer.Revision = -1;
                }
                catch { /* A malformed row does not hide the rest of the catalog. Retry after its value changes. */ }
                continue;
            }
            try
            {
                string value = entry.Read(peer.Account);
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
        var result = Apply(request, request.Generation == peer.Generation && Handshake.UserAnswerStatus(user.Index) == "ok", actor);
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
        Accept(packet);
    }

    private static void Accept(ScopedSettingsPacket packet)
    {
        if (packet.Kind <= 3)
        {
            if (!SnapshotReceiver.Accept(packet, Now())) return;
            _clientRows = new(SnapshotReceiver.Rows); ClientById.Clear();
            foreach (var row in _clientRows) ClientById.Add(row.Id, row);
            ClientCanEditServerWide = SnapshotReceiver.CanEdit; ClientServerName = SnapshotReceiver.ServerName;
            _clientGeneration = SnapshotReceiver.Generation;
            ClientReady = true; ClientRevision++;
            foreach (var row in _clientRows) NotifyValue(row.Id, row.Value);
            return;
        }
        switch (packet.Kind)
        {
            case 4:
                if (packet.Generation == _clientGeneration) SetClientValue(packet.Id, packet.Value);
                break;
            case 6:
                if (packet.Generation != _clientGeneration || !PendingRequests.TryGetValue(packet.RequestId, out var pending) || pending.RowId != packet.Id) return;
                if (packet.Success || packet.Error.StartsWith("This setting changed on the server.", StringComparison.Ordinal)) SetClientValue(packet.Id, packet.Value);
                Complete(packet.RequestId, new(packet.Success, packet.Value, packet.Error));
                break;
        }
    }

    private static void SetClientValue(string id, string value)
    {
        if (!ClientById.TryGetValue(id, out var row) || row.Value == value) return;
        row.Value = value; NotifyValue(id, value);
    }
    private static void NotifyValue(string id, string value)
    {
        if (ClientValueChanged == null) return;
        foreach (Action<string, string> callback in ClientValueChanged.GetInvocationList())
            try { callback(id, value); } catch (Exception e) { Plugin.Logger?.LogWarning("Player settings subscriber: " + e.Message); }
    }
}
