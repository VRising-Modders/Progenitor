#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Progenitor.Plugins;
using ProjectM;
using ProjectM.Network;
using Stunlock.Core;
using Unity.Entities;

namespace Progenitor.Networking;

/// <summary>
/// Mod compatibility C1 and C2, live half (docs/mod-compatibility.md, sections 1 and 3): the <c>'PrFt'</c> feature
/// tables and the <c>'PrMs'</c> versioned message envelope on the reliable events channel. The decisions are game-free
/// (<see cref="FeatureTableWire"/>, <see cref="PeerFeatureBook"/>, <see cref="FeatureDeltaSchedule"/>,
/// <see cref="ModMessageRouter"/>, <see cref="ModMessageGate"/>); this type only sends, receives and keeps the per-peer
/// bookkeeping. Main thread. Both ids are Core's and registered through the existing owned-id path, so a peer without a
/// handler drops the packet in Core rather than handing it to the game.
/// </summary>
internal static unsafe class ModCompatNet
{
    /// <summary>The client's key for its server in <see cref="PeerTables"/>.</summary>
    internal const int ServerPeer = 0;

    internal static readonly FeatureOfferBook Offers = new();
    internal static readonly PeerFeatureBook PeerTables = new();
    internal static readonly ModMessageRouter Router = new();
    internal static readonly RawSendWarnings RawWarnings = new();
    private static readonly FeatureDeltaSchedule Schedule = new();

    // Server: users that were sent this server's full table (handshake ok and ModFeatures), and their user entities.
    private static readonly HashSet<int> TablePeers = new();
    private static readonly Dictionary<int, Entity> UserEntities = new();

    private static bool _installed;
    private static bool _server;
    private static IDisposable? _connected;
    private static IDisposable? _disconnected;

    /// <summary>Client: the server's table arrived, changed or went away (join, peer hot reload, leave).</summary>
    internal static event Action? ClientChanged;

    /// <summary>Server: one user's table arrived, changed or went away.</summary>
    internal static event Action<Entity>? ServerChanged;

    private static double Now() => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;

    internal static void Install()
    {
        if (_installed) return;
        _installed = true;
        _server = Plugin.IsServerProcess;
        Schedule.Reset();
        try
        {
            if (_server)
            {
                CustomEvents.OnReceiveFromClient(FeatureTableWire.EventId, OnServerTable);
                CustomEvents.OnReceiveFromClient(ModMessageEnvelope.EventId, OnServerMessage);
                _connected = Events.EventBus.On<Events.PlayerConnected>(ev => ForgetUser(ev.UserIndex), priority: 896);
                _disconnected = Events.EventBus.On<Events.PlayerDisconnected>(ev => ForgetUser(ev.User.Index), priority: 896);
            }
            else
            {
                CustomEvents.OnReceive(FeatureTableWire.EventId, OnClientTable);
                CustomEvents.OnReceive(ModMessageEnvelope.EventId, OnClientMessage);
                Handshake.ServerContextCleared += OnClientContextCleared;
            }
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("ModCompat: registering the feature and message channels threw: " + e.Message); }

        try
        {
            MessageSchema.RegisterScalar(typeof(PrefabGUID), o => ((PrefabGUID)o).GuidHash, v => new PrefabGUID(unchecked((int)v)));
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("ModCompat: PrefabGUID message fields unavailable: " + e.Message); }

        // Compat C3: the join evaluation reads this server's own declarations from the registry.
        VersionMatching.DeclarationSource = id => ModRegistry.TryGet(id, out var entry) ? entry.Compat : null;
        Offers.Changed += OnLocalChanged;
        ModRegistry.Changed += OnLocalChanged;
        PeerTables.Changed += OnPeerChanged;
        // The baseline is what peers were last told. A new generation's peers get full tables at their re-handshake,
        // each of which adds its ids (NoteFullSent), so a mod that unloads before the first delta still reaches them as gone.
        try { Schedule.Baseline(BuildLocalTable()); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Features: the initial baseline threw: " + e.Message); }
        Plugin.Frame += Tick;
    }

    internal static void Uninstall()
    {
        if (!_installed) return;
        _installed = false;
        try { Plugin.Frame -= Tick; } catch { /* teardown */ }
        Offers.Changed -= OnLocalChanged;
        ModRegistry.Changed -= OnLocalChanged;
        PeerTables.Changed -= OnPeerChanged;
        VersionMatching.DeclarationSource = null;
        try
        {
            if (_server)
            {
                CustomEvents.RemoveClientHandler(FeatureTableWire.EventId);
                CustomEvents.RemoveClientHandler(ModMessageEnvelope.EventId);
            }
            else
            {
                CustomEvents.RemoveHandler(FeatureTableWire.EventId);
                CustomEvents.RemoveHandler(ModMessageEnvelope.EventId);
                Handshake.ServerContextCleared -= OnClientContextCleared;
            }
        }
        catch { /* teardown */ }

        _connected?.Dispose();
        _disconnected?.Dispose();
        _connected = _disconnected = null;
        MessageSchema.UnregisterScalar(typeof(PrefabGUID));
        ClientChanged = null;
        ServerChanged = null;
        Offers.Clear();
        PeerTables.Clear();
        Router.Clear();
        ModMessageRecordBridge.StopForTeardown();
        RawWarnings.Clear();
        Schedule.Reset();
        TablePeers.Clear();
        UserEntities.Clear();
        ModCompatCaps.Reset();
    }

    // ---- this process's table ---------------------------------------------------------------------------------------

    internal static FeatureTable BuildLocalTable()
    {
        // Compat C3: every mod with its effective peer range (declared, else from its rule, else the same minor).
        var loaded = new List<(string, string, string?)>();
        foreach (var e in ModRegistry.All())
            if (e.Running) loaded.Add((e.Id, e.Version, e.Compat?.EffectiveRange is { Length: > 0 } r ? r : CompatDeclaration.DefaultRange(e.Version)));
        int proto;
        try { proto = Math.Max(0, BootstrapLink.ReadBootstrapProtocolOrAbsent()); }
        catch { proto = 0; }
        return FeatureTableBuilder.Build(loaded, Offers, Plugin.Version, Progenitor.Install.SelfUpdatesLive.RunningCoreSha(), proto);
    }

    private static void OnLocalChanged(string _)
    {
        if (_installed) Schedule.MarkDirty(Now());
    }

    private static List<byte[]> FullPackets(out int kept, out bool more)
    {
        var table = BuildLocalTable();
        byte[] body = FeatureTableWire.FullBody(table, out kept);
        more = kept < table.Mods.Count || table.More;
        Schedule.NoteFullSent(table);
        return FeatureTableWire.Pack(FeatureTableWire.KindFull, body);
    }

    private static bool _moreLogged;

    private static void NoteMore(bool more, int kept)
    {
        if (!more || _moreLogged) return;
        _moreLogged = true;
        Plugin.Logger?.LogWarning("Features: this table lists " + kept + " mod(s); the rest are left out (the limit is "
                                  + ModFeatureLimits.MaxModsPerTable + " mods and " + ModFeatureLimits.MaxTableBytes
                                  + " bytes). Peers treat a missing mod's features as absent.");
    }

    /// <summary>Client: right after a handshake answer. Sends the full table when the server's request declared ModFeatures.</summary>
    internal static void AfterClientAnswer()
    {
        if (!_installed || _server) return;
        if ((ModCompatCaps.ServerCaps & Handshake.Capability.ModFeatures) == 0) return;
        var packets = FullPackets(out int kept, out bool more);
        NoteMore(more, kept);
        bool ok = true;
        foreach (var p in packets) ok &= CustomEvents.SendToServer(FeatureTableWire.EventId, new ReadOnlySpan<byte>(p));
        Plugin.Logger?.LogInfo("Features: sent this client's table (" + kept + " mod(s), " + packets.Count + " packet(s)) to the server; sent=" + ok);
    }

    /// <summary>Server: after an ok handshake. Sends this server's full table to a client that declared ModFeatures.</summary>
    internal static void OnHandshakeOk(World? world, Entity userEntity, Handshake.Capability clientCaps)
    {
        // Requests and subscriptions: what the server held for this user's previous client state is reset.
        try
        {
            if (_installed && _server && world != null && world.IsCreated && world.EntityManager.HasComponent<User>(userEntity))
                ExchangeNet.OnHandshakeOk(userEntity, world.EntityManager.GetComponentData<User>(userEntity).Index);
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("Exchange: the handshake reset threw: " + e.Message); }
        if (!_installed || !_server || world == null || (clientCaps & Handshake.Capability.ModFeatures) == 0) return;
        try
        {
            if (!world.IsCreated || !world.EntityManager.HasComponent<User>(userEntity)) return;
            var user = world.EntityManager.GetComponentData<User>(userEntity);
            if (!user.IsConnected) return;
            var bootstrap = world.GetExistingSystemManaged<ServerBootstrapSystem>();
            if (bootstrap == null) return;
            var packets = FullPackets(out int kept, out bool more);
            NoteMore(more, kept);
            bool ok = true;
            foreach (var p in packets) ok &= CustomEvents.SendToUser(bootstrap, user.Index, FeatureTableWire.EventId, new ReadOnlySpan<byte>(p));
            TablePeers.Add(user.Index);
            UserEntities[user.Index] = userEntity;
            // The table the client sent right after its answer was held until now (its handshake had not passed).
            if (PeerTables.PromotePending(user.Index)) LogTable("Features: " + Label(user) + "'s table", PeerTables.Get(user.Index));
            Plugin.Logger?.LogInfo("Features: sent this server's table (" + kept + " mod(s)) to " + Label(user) + "; sent=" + ok);
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("Features: sending the table threw: " + e.Message); }
    }

    /// <summary>Deltas: at most one per second, after a second of collecting, to every peer that has the full table.</summary>
    private static void Tick()
    {
        if (!_installed) return;
        double now = Now();
        if (!Schedule.Due(now)) return;
        try
        {
            var table = BuildLocalTable();
            if (!Schedule.Diff(table, out var changed, out var gone))
            {
                Schedule.Commit(table, now);
                return;
            }

            // A delta that would be cut (rows over the byte cap, or more than 256 removals) becomes a full table, so
            // nothing is left behind; either way every peer now has this table.
            byte[] body = FeatureTableWire.DeltaBody(table, changed, gone, out int kept);
            byte kind = FeatureTableWire.KindDelta;
            if (!FeatureDeltaSchedule.DeltaIsWhole(changed.Count, kept, gone.Count))
            {
                body = FeatureTableWire.FullBody(table, out kept);
                NoteMore(kept < table.Mods.Count, kept);
                kind = FeatureTableWire.KindFull;
            }

            Schedule.Commit(table, now);
            var packets = FeatureTableWire.Pack(kind, body);
            int peers = 0;
            if (_server)
            {
                var world = Worlds.Server;
                var bootstrap = world == null || !world.IsCreated ? null : world.GetExistingSystemManaged<ServerBootstrapSystem>();
                if (bootstrap != null)
                    foreach (int index in new List<int>(TablePeers))
                    {
                        if (Handshake.UserAnswerStatus(index) != "ok") continue;
                        foreach (var p in packets) CustomEvents.SendToUser(bootstrap, index, FeatureTableWire.EventId, new ReadOnlySpan<byte>(p));
                        peers++;
                    }
            }
            else if (ClientMayExchange(Handshake.Capability.ModFeatures))
            {
                foreach (var p in packets) CustomEvents.SendToServer(FeatureTableWire.EventId, new ReadOnlySpan<byte>(p));
                peers = 1;
            }

            if (peers > 0)
                Plugin.Logger?.LogInfo(kind == FeatureTableWire.KindDelta
                    ? "Features: sent a delta (" + changed.Count + " changed, " + gone.Count + " gone) to " + peers + " peer(s)"
                    : "Features: sent the full table instead of a delta that would not fit (" + changed.Count + " changed, " + gone.Count + " gone) to " + peers + " peer(s)");
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("Features: the delta threw: " + e.Message); }
    }

    private static bool ClientMayExchange(Handshake.Capability bit) =>
        !_server && string.Equals(Handshake.LastClientResult, "ok", StringComparison.Ordinal) && (ModCompatCaps.ServerCaps & bit) != 0;

    // ---- peers' tables ----------------------------------------------------------------------------------------------

    private static void ForgetUser(int index)
    {
        TablePeers.Remove(index);
        PeerTables.Forget(index);
        UserEntities.Remove(index);
    }

    private static void OnClientContextCleared(string reason) => PeerTables.Forget(ServerPeer);

    private static void OnPeerChanged(int peer)
    {
        if (_server)
        {
            if (!UserEntities.TryGetValue(peer, out var entity)) return;
            var handlers = ServerChanged;
            if (handlers == null) return;
            foreach (Action<Entity> h in handlers.GetInvocationList())
            {
                try { h(entity); }
                catch (Exception e) { Plugin.Logger?.LogWarning("Features: a Peers.Changed handler threw: " + e.Message); }
            }
        }
        else
        {
            var handlers = ClientChanged;
            if (handlers == null) return;
            foreach (Action h in handlers.GetInvocationList())
            {
                try { h(); }
                catch (Exception e) { Plugin.Logger?.LogWarning("Features: a Peer.Changed handler threw: " + e.Message); }
            }
        }
    }

    private static void OnServerTable(int _, Entity userEntity, User user, byte* payload, int length)
    {
        if (ReceiveGate.HandlerMustRefuse(userEntity == Entity.Null, user.Index))
        {
            Plugin.Logger?.LogWarning(ReceiveGate.HandlerRefusedLog("Features", ReceiveGate.Fourcc(FeatureTableWire.EventId)));
            return;
        }

        if (payload == null || length <= 0) return;
        // A user whose handshake has not passed cannot change its table or fire Peers.Changed: the full table it sends
        // right after its answer is held and applied at its ok (OnHandshakeOk); its deltas are dropped.
        bool accepted = Handshake.UserAnswerStatus(user.Index) == "ok";
        if (accepted) UserEntities[user.Index] = userEntity;
        var outcome = PeerTables.Receive(user.Index, new ReadOnlySpan<byte>(payload, length), out string? log, accepted);
        if (log != null)
        {
            var line = Router.Once("table|" + user.Index + "|" + log, log + " (from " + Label(user) + ")");
            if (line != null) Plugin.Logger?.LogWarning(line);
        }

        if (outcome == PeerFeatureBook.Outcome.Full) LogTable("Features: " + Label(user) + "'s table", PeerTables.Get(user.Index));
        // Compat C3: a join held for this table is judged now.
        if (outcome == PeerFeatureBook.Outcome.Full || outcome == PeerFeatureBook.Outcome.Held)
        {
            try { Handshake.OnClientFeatureTable(user.Index, PeerTables.FullCount(user.Index)); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Features: releasing the join hold threw: " + e.Message); }
        }
    }

    /// <summary>
    /// Compat C3: the client's effective ranges from its table (held or applied), or null when it has none. Every row of a
    /// C3 table carries one; a row without one gets the default at the evaluation.
    /// </summary>
    internal static Dictionary<string, string>? ClientRangesOf(int userIndex)
    {
        var t = PeerTables.TableOrPending(userIndex);
        if (t == null) return null;
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in t.Mods)
            if (!string.IsNullOrEmpty(kv.Value.Peers)) map[kv.Key] = kv.Value.Peers!;
        return map;
    }

    /// <summary>Compat C3: how many full tables this user has sent (the join hold waits for one after the answer).</summary>
    internal static int TablesSeen(int userIndex) => PeerTables.FullCount(userIndex);

    /// <summary>
    /// Mod compatibility C4: the user's Core version and sha256 from its table (held or applied), and whether a table
    /// arrived at all (a client without one cannot show which Progenitor build it runs).
    /// </summary>
    internal static (string Core, string Sha, bool Table) ClientCoreOf(int userIndex)
    {
        var t = PeerTables.TableOrPending(userIndex);
        return t == null ? ("", "", false) : (t.Core, t.CoreSha, true);
    }

    private static void OnClientTable(int _, byte* payload, int length)
    {
        if (payload == null || length <= 0) return;
        var outcome = PeerTables.Receive(ServerPeer, new ReadOnlySpan<byte>(payload, length), out string? log);
        if (log != null)
        {
            var line = Router.Once("table|server|" + log, log + " (from the server)");
            if (line != null) Plugin.Logger?.LogWarning(line);
        }

        if (outcome == PeerFeatureBook.Outcome.Full) LogTable("Features: the server's table", PeerTables.Get(ServerPeer));
    }

    /// <summary>One line per full table (bounded: a table holds at most 256 mods of 32 features). Deltas are not logged per packet.</summary>
    private static void LogTable(string what, FeatureTable? t)
    {
        if (t == null) return;
        int features = 0;
        foreach (var m in t.Mods.Values) features += m.Features.Count;
        Plugin.Logger?.LogInfo(what + ": " + t.Mods.Count + " mod(s), " + features + " feature(s), Progenitor " + (t.Core.Length > 0 ? t.Core : "?")
                               + (t.More ? ", more left out" : ""));
    }

    private static string Label(User user)
    {
        try { return Handshake.Compare.PlayerLabel(user.CharacterName.ToString(), user.PlatformId); }
        catch { return "user " + user.Index.ToString(CultureInfo.InvariantCulture); }
    }

    /// <summary>The peer's version of <paramref name="modGuid"/> from its table, or null when the table does not list it.</summary>
    internal static string? PeerModVersion(int peer, string modGuid)
    {
        var t = PeerTables.Get(peer);
        return t != null && t.Mods.TryGetValue(modGuid, out var row) ? row.Version : null;
    }

    /// <summary>Client fallback for <see cref="IPeerView.ModVersion"/>: the server's handshake request rows.</summary>
    internal static string? ServerRowVersion(string modGuid)
    {
        foreach (var r in Handshake.CopyServerAsked())
            if (string.Equals(r.Guid, modGuid, StringComparison.OrdinalIgnoreCase)) return r.Version;
        return null;
    }

    /// <summary>Server: the user index of a user entity, or -1.</summary>
    internal static int UserIndexOf(Entity user)
    {
        try
        {
            var world = Worlds.Server;
            if (world == null || !world.IsCreated || !world.EntityManager.HasComponent<User>(user)) return -1;
            return world.EntityManager.GetComponentData<User>(user).Index;
        }
        catch { return -1; }
    }

    // ---- versioned messages -----------------------------------------------------------------------------------------

    private static void OnServerMessage(int _, Entity userEntity, User user, byte* payload, int length)
    {
        if (ReceiveGate.HandlerMustRefuse(userEntity == Entity.Null, user.Index))
        {
            Plugin.Logger?.LogWarning(ReceiveGate.HandlerRefusedLog("Messages", ReceiveGate.Fourcc(ModMessageEnvelope.EventId)));
            return;
        }

        if (payload == null || length <= 0) return;
        string label = Label(user);
        if (Handshake.UserAnswerStatus(user.Index) != "ok")
        {
            var line = Router.Once("notok|" + user.Index, "Messages: dropped a message from " + label + ": its handshake has not passed");
            if (line != null) Plugin.Logger?.LogWarning(line);
            return;
        }

        int index = user.Index;
        if (Router.TryRoute(new ReadOnlySpan<byte>(payload, length), label, g => PeerModVersion(index, g), fromServer: false, userIndex: index,
                out var routed, out string? log))
            Run(routed!, userEntity, label);
        else if (log != null) Plugin.Logger?.LogWarning(log);
    }

    private static void OnClientMessage(int _, byte* payload, int length)
    {
        if (payload == null || length <= 0) return;
        if (Router.TryRoute(new ReadOnlySpan<byte>(payload, length), "the server", g => PeerModVersion(ServerPeer, g), fromServer: true, userIndex: -1,
                out var routed, out string? log))
            Run(routed!, Entity.Null, "the server");
        else if (log != null) Plugin.Logger?.LogWarning(log);
    }

    private static void Run(ModMessageRouter.Routed routed, Entity from, string label)
    {
        if (ModMessageRecordBridge.Recording)
        {
            try
            {
                ModMessageRecordBridge.Offer(routed.ModGuid, routed.Info.SenderModVersion ?? "", routed.Info.Name, routed.Info.SenderVersion, routed.MinReader,
                    routed.Payload, routed.Info.FromServer ? ModMessageFixtureFormat.ToClient : ModMessageFixtureFormat.ToServer);
            }
            catch { /* a recording never costs a delivery */ }
        }

        try { routed.Dispatch(from, routed.Payload, routed.Info); }
        catch (Exception e)
        {
            var inner = e is TargetInvocationException t && t.InnerException != null ? t.InnerException : e;
            var line = Router.Once("threw|" + routed.ModGuid + "|" + routed.Info.Name,
                "Messages: " + routed.ModGuid + " " + routed.Info.Name + " v" + routed.Info.SenderVersion + " from " + label
                + (inner is FormatException ? " could not be decoded: " : " handler threw: ") + inner.Message);
            if (line != null) Plugin.Logger?.LogError(line);
        }
    }

    private static SendResult Refuse(string modGuid, string? refusal)
    {
        if (refusal == null) return SendResult.Invalid;
        var line = Router.Once("refuse|" + modGuid + "|" + refusal, refusal);
        if (line != null) Plugin.Logger?.LogError(line);
        return SendResult.Invalid;
    }

    private static bool Prepare(string modGuid, string name, int version, int minReader, byte[] payload, out byte[] packet, out SendResult result)
    {
        packet = Array.Empty<byte>();
        result = SendResult.Sent;
        string? refusal = Router.Claim(modGuid, name, out uint modHash, out uint msgHash);
        if (refusal != null)
        {
            result = Refuse(modGuid, refusal);
            return false;
        }

        if (!ModMessageEnvelope.IsValidVersion(version) || minReader < 1 || minReader > version)
        {
            result = Refuse(modGuid, "Messages: " + modGuid + " " + name + ": version " + version + " / min reader " + minReader
                                     + " is not valid (1 <= min reader <= version <= 65535).");
            return false;
        }

        if (payload.Length > ModMessageEnvelope.MaxPayloadBytes)
        {
            result = SendResult.TooLarge;
            return false;
        }

        packet = ModMessageEnvelope.Pack(modHash, msgHash, version, minReader, payload);
        return true;
    }

    internal static SendResult SendToServer(string modGuid, string name, int version, int minReader, byte[] payload, IReadOnlyList<FieldShape>? schema = null)
    {
        if (!_installed || _server) return SendResult.NotConnected;
        if (!Prepare(modGuid, name, version, minReader, payload, out var packet, out var result)) return result;
        var table = PeerTables.Get(ServerPeer);
        var state = new MessagePeerState(
            connected: Worlds.Client != null && string.Equals(Handshake.LastClientResult, "ok", StringComparison.Ordinal),
            hasMessagesBit: (ModCompatCaps.ServerCaps & Handshake.Capability.ModMessages) != 0,
            tableKnown: table != null,
            listsMod: table != null && table.Mods.ContainsKey(modGuid));
        result = ModMessageGate.Decide(state, payload.Length);
        if (result != SendResult.Sent) return result;
        if (!CustomEvents.SendToServer(ModMessageEnvelope.EventId, new ReadOnlySpan<byte>(packet))) return SendResult.TransportFailed;
        NoteSent(modGuid, name, version, minReader, payload, ModMessageFixtureFormat.ToServer, schema);
        return SendResult.Sent;
    }

    /// <summary>A send went out: the developer details' record, and the Dev bridge's recording when one runs. Never throws.</summary>
    private static void NoteSent(string modGuid, string name, int version, int minReader, byte[] payload, string direction, IReadOnlyList<FieldShape>? schema)
    {
        try
        {
            Router.NoteSent(modGuid, name, version, minReader);
            if (ModMessageRecordBridge.Recording)
                ModMessageRecordBridge.Offer(modGuid, ModRegistry.TryGet(modGuid, out var entry) ? entry.Version : "", name, version, minReader, payload, direction, schema);
        }
        catch { /* bookkeeping never costs a send */ }
    }

    internal static SendResult SendToUser(string modGuid, string name, int version, int minReader, byte[] payload, Entity userEntity, IReadOnlyList<FieldShape>? schema = null)
    {
        if (!_installed || !_server) return SendResult.NotConnected;
        if (!Prepare(modGuid, name, version, minReader, payload, out var packet, out var result)) return result;
        var world = Worlds.Server;
        if (world == null || !world.IsCreated || !world.EntityManager.HasComponent<User>(userEntity)) return SendResult.NotConnected;
        var user = world.EntityManager.GetComponentData<User>(userEntity);
        var sent = SendToIndex(world, user, modGuid, payload.Length, packet);
        if (sent == SendResult.Sent) NoteSent(modGuid, name, version, minReader, payload, ModMessageFixtureFormat.ToClient, schema);
        return sent;
    }

    internal static int SendToAll(string modGuid, string name, int version, int minReader, byte[] payload, IReadOnlyList<FieldShape>? schema = null)
    {
        if (!_installed || !_server) return 0;
        if (!Prepare(modGuid, name, version, minReader, payload, out var packet, out _)) return 0;
        var world = Worlds.Server;
        if (world == null || !world.IsCreated) return 0;
        int sent = 0;
        foreach (var kv in new List<KeyValuePair<int, Entity>>(UserEntities))
        {
            if (!world.EntityManager.HasComponent<User>(kv.Value)) continue;
            var user = world.EntityManager.GetComponentData<User>(kv.Value);
            if (user.Index != kv.Key) continue;
            if (SendToIndex(world, user, modGuid, payload.Length, packet) == SendResult.Sent) sent++;
        }

        if (sent > 0) NoteSent(modGuid, name, version, minReader, payload, ModMessageFixtureFormat.ToClient, schema);
        return sent;
    }

    private static SendResult SendToIndex(World world, User user, string modGuid, int payloadBytes, byte[] packet)
    {
        var table = PeerTables.Get(user.Index);
        var state = new MessagePeerState(
            connected: user.IsConnected && Handshake.UserAnswerStatus(user.Index) == "ok",
            hasMessagesBit: (Handshake.CapsOf(user.Index) & Handshake.Capability.ModMessages) != 0,
            tableKnown: table != null,
            listsMod: table != null && table.Mods.ContainsKey(modGuid));
        var result = ModMessageGate.Decide(state, payloadBytes);
        if (result != SendResult.Sent) return result;
        var bootstrap = world.GetExistingSystemManaged<ServerBootstrapSystem>();
        if (bootstrap == null) return SendResult.NotConnected;
        return CustomEvents.SendToUser(bootstrap, user.Index, ModMessageEnvelope.EventId, new ReadOnlySpan<byte>(packet))
            ? SendResult.Sent
            : SendResult.TransportFailed;
    }

    // ---- the raw ctx.Net warning ------------------------------------------------------------------------------------

    /// <summary>
    /// A plugin sent a raw <c>ctx.Net</c> message. One line per mod and id per Core generation when the peer runs the same
    /// mod and the two may differ in version (see <see cref="RawSendWarnings.Check"/>). Never blocks the send.
    /// </summary>
    /// <returns>True when this mod and id are settled for the generation (the line was logged now or before).</returns>
    internal static bool NoteRawSend(string modGuid, string localVersion, bool ruleAllowsOtherVersions, int eventId, int userIndex)
    {
        if (!_installed || string.IsNullOrEmpty(modGuid)) return false;
        try
        {
            string? peerVersion = null;
            if (!_server) peerVersion = PeerModVersion(ServerPeer, modGuid);
            else if (userIndex >= 0) peerVersion = PeerModVersion(userIndex, modGuid);
            else
                foreach (int peer in PeerTables.Peers())
                    if ((peerVersion = PeerModVersion(peer, modGuid)) != null) break;
            var line = RawWarnings.Check(modGuid, eventId, localVersion, peerVersion, ruleAllowsOtherVersions);
            if (line != null) Plugin.Logger?.LogWarning(line);
            return line != null || RawWarnings.Warned(modGuid, eventId);
        }
        catch { return true; /* a warning never costs a send */ }
    }
}
#endif
