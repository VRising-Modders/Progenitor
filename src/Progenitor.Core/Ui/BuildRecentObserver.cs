using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using ProjectM;
using ProjectM.Network;
using ProjectM.Sequencer;
using Progenitor.Ai;
using Progenitor.Ecs;
using Progenitor.Hooks;
using Progenitor.Networking;
using Stunlock.Core;
using Unity.Collections;
using Unity.Entities;
using Unity.Transforms;
using Vector3 = System.Numerics.Vector3;

namespace Progenitor.Ui;

/// <summary>
/// Server-authoritative, exact-match Recent ingestion. Observes existing systems only.
/// Before the server's placement system, each authenticated placement request (or pre-cast completion) of a
/// connected peer is recorded with its user, blueprint, position and rotation. Placement writes UserOwner on the
/// new structure, which carries SpawnTag until the spawn group removes it. Before the server's spawn-reaction
/// sequence consumer (after spawn playback, before SpawnTag removal), every just-spawned structure owned by a
/// peer is matched exactly against those requests. A pre-placement snapshot of the structures already spawning
/// excludes them: only a newly created entity carries SpawnTag, so the snapshot is a short-lived handful, never
/// every structure in the world. A building sequence is not required: a piece whose blueprint has none never
/// gets a sequence entity. Unsupported transformations are omitted, never inferred. No wallpaper coverage.
/// Per-stage counters (<see cref="Diagnostics"/>) are read through the Dev bridge's <c>build_search</c> op.
/// Static/compile verification is not live verification.
/// </summary>
internal static unsafe class BuildRecentObserver
{
    internal const int EventId = 0x50725262; // PrRb, Core-reserved; both directions versioned below.
    /// <summary>Most just-spawned structures (BlueprintData with SpawnTag) one frame examines or snapshots.</summary>
    private const int MaxSpawning = 512;
    /// <summary>A peer's hello counts this long from when it arrived (the client sends one every 10 s).</summary>
    private const long PeerLifetimeMs = 30000;
    private const int SequenceTargetOffset = 4;
    private const int NetworkedEntityWaitingOffset = 8;
    private static readonly BuildRecentCorrelation Correlation = new();
    private static readonly BuildRecentStats Stats = new();
    private static readonly List<IDisposable> Hooks = new();
    private static readonly object Gate = new();
    private static readonly Queue<(Wire Packet, Entity User, ulong Platform, long At)> Inbox = new();
    private static readonly Dictionary<ulong, Peer> Peers = new();
    private static readonly HashSet<(ulong, ulong)> Received = new();
    private static readonly Queue<(ulong, ulong)> ReceivedOrder = new();
    private static World? _server;
    private static ulong _serverEpoch, _clientEpoch, _serverNonce, _clientNonce, _serial;
    private static long _helloAt, _pruneAt;
    private static bool _installed, _queries;
    private static int _warnings;
    private static EntityQuery _requests, _abilities, _spawning, _sequences;
    private sealed record Peer(ulong Nonce, ulong Platform, long At);

    // Every stage's counter name, so a reader sees each one (0 until it happens).
    private static readonly string[] ClientCounters =
        { "hellosSent", "hellosSendRefused", "packetsReceived", "packetsMalformed", "packetsWrongNonce", "packetsDuplicate", "recordsAdded" };
    private static readonly string[] ServerCounters =
    {
        "hellosReceived", "hellosMalformed", "hellosDroppedFull", "peersRegistered", "hellosStale",
        "requestsSeen", "requestsRelocation", "requestsNullComponent", "requestsNotOwned",
        "abilityEventsSeen", "abilityEventsNotPlacement", "abilityEventsNotOwned",
        "correlationAdds", "correlationAddRefused", "requestsTooMany", "spawningTooMany",
        "sequenceEntities", "sequenceTargetNoSpawnTag", "sequenceTargetWaitingForSync",
        "candidates", "rejectNullBlueprint", "rejectNullOwner", "rejectNullTranslation", "rejectNullTilePosition",
        "rejectNullPrefab", "rejectOwnerWaitingForSync", "rejectGuidMismatch", "noBuildingSequence",
        "rejectNoPeer", "rejectNotConnected",
        BuildRecentStats.NameOf(BuildRecentConfirm.InvalidTarget), BuildRecentStats.NameOf(BuildRecentConfirm.AlreadyConfirmed),
        BuildRecentStats.NameOf(BuildRecentConfirm.NoPendingForUser), BuildRecentStats.NameOf(BuildRecentConfirm.GuidMismatch),
        BuildRecentStats.NameOf(BuildRecentConfirm.RotationMismatch), BuildRecentStats.NameOf(BuildRecentConfirm.PositionMismatch),
        BuildRecentStats.NameOf(BuildRecentConfirm.ExistedBefore), BuildRecentStats.NameOf(BuildRecentConfirm.Ambiguous),
        BuildRecentStats.NameOf(BuildRecentConfirm.Confirmed), "packetsSent", "packetsSendFailed",
    };

    // Managed wire struct only: not a game/native ABI. Pack and exact length are checked on receive.
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct Wire
    {
        internal int Version, Kind;
        internal ulong ClientNonce, ServerNonce, Serial;
        internal int Blueprint;
    }

    internal static void Install()
    {
        if (_installed) return;
        try
        {
            CheckLayouts();
            _installed = true;
            Stats.Clear();
            if (Plugin.IsServerProcess)
            {
                CustomEvents.OnReceiveFromClient(EventId, ReceiveHello);
                Hooks.Add(SystemHooks.Before<PlaceTileModelSystem>(BeforePlacement));
                // Every frame, not only when a sequence is consumed: hellos are drained on their own schedule and a
                // piece whose blueprint has no building sequence spawns no sequence entity.
                Hooks.Add(SystemHooks.Before<SpawnSequenceForEntitySystem_Server>(BeforeSpawnReaction,
                    new HookOptions { OnlyWhenSystemRuns = false }));
            }
            else
            {
                CustomEvents.OnReceive(EventId, Receive);
                Handshake.ClientConnectionEnded += ResetClient;
                Hooks.Add(SystemHooks.After<ClientBootstrapSystem>(ClientTick, new HookOptions { OnlyWhenSystemRuns = false }));
            }
        }
        catch (Exception e) { Uninstall(); Warn(e); }
    }

    internal static void Uninstall()
    {
        _installed = false;
        foreach (var hook in Hooks) { try { hook.Dispose(); } catch { } }
        Hooks.Clear();
        CustomEvents.RemoveHandler(EventId, Receive);
        CustomEvents.RemoveClientHandler(EventId, ReceiveHello);
        Handshake.ClientConnectionEnded -= ResetClient;
        ResetServer();
        ResetClient();
        lock (Gate) Inbox.Clear();
    }

    /// <summary>Per-stage counters and state for the side this process is, for the Dev bridge. Main thread.</summary>
    internal static Dictionary<string, object?> Diagnostics()
    {
        bool server = Plugin.IsServerProcess;
        var r = new Dictionary<string, object?>
        {
            ["side"] = server ? "server" : "client",
            ["installed"] = _installed,
            ["counters"] = Stats.Snapshot(server ? ServerCounters : ClientCounters),
        };
        lock (Gate) r["inbox"] = Inbox.Count;
        if (server)
        {
            r["peers"] = Peers.Count;
            r["pending"] = Correlation.PendingCount;
            r["worldReady"] = _queries;
            var miss = Correlation.LastPositionMiss;
            r["lastPositionMiss"] = miss == null ? null : new[] { miss.Value.X, miss.Value.Y, miss.Value.Z };
        }
        else
        {
            lock (Gate) r["clientNonceSet"] = _clientNonce != 0;
            r["helloInMs"] = Math.Max(0, _helloAt - Environment.TickCount64);
        }
        return r;
    }

    private static void Receive(int id, byte* p, int length)
    {
        if (!_installed || id != EventId) return;
        Stats.Add("packetsReceived");
        if (p == null || length != sizeof(Wire)) { Stats.Add("packetsMalformed"); return; }
        var packet = *(Wire*)p;
        if (packet.Version != 1 || packet.Kind != 2 || packet.ClientNonce == 0
            || packet.ServerNonce == 0 || packet.Serial == 0 || packet.Blueprint == 0) { Stats.Add("packetsMalformed"); return; }
        lock (Gate)
        {
            if (packet.ClientNonce != _clientNonce) { Stats.Add("packetsWrongNonce"); return; }
            if (Inbox.Count < 256) Inbox.Enqueue((packet, Entity.Null, 0, 0));
        }
    }

    private static void ReceiveHello(int id, Entity userEntity, User user, byte* p, int length)
    {
        if (!_installed || id != EventId) return;
        Stats.Add("hellosReceived");
        if (p == null || length != sizeof(Wire) || !user.IsConnected) { Stats.Add("hellosMalformed"); return; }
        var packet = *(Wire*)p;
        if (packet.Version != 1 || packet.Kind != 1 || packet.ClientNonce == 0
            || packet.ServerNonce != 0 || packet.Serial != 0 || packet.Blueprint != 0) { Stats.Add("hellosMalformed"); return; }
        lock (Gate)
        {
            // Keep the newest hellos and their arrival time: a backlog never crowds out a fresh hello (a client that
            // reloaded Core says hello with a new nonce), and a stale one still expires.
            if (Inbox.Count >= 256) { Inbox.Dequeue(); Stats.Add("hellosDroppedFull"); }
            Inbox.Enqueue((packet, userEntity, user.PlatformId, Environment.TickCount64));
        }
    }

    private static void ClientTick(SystemHookContext hook)
    {
        if (!_installed || hook.World.Name != Worlds.ClientName) return;
        try
        {
            if (_clientEpoch != hook.World.SequenceNumber)
            {
                ResetClient();
                _clientEpoch = hook.World.SequenceNumber;
                lock (Gate) _clientNonce = Nonce();
            }
            long now = Environment.TickCount64;
            if (now >= _helloAt)
            {
                _helloAt = now + 10000;
                var hello = new Wire { Version = 1, Kind = 1, ClientNonce = _clientNonce };
                Stats.Add(CustomEvents.SendToServer(EventId, hello) ? "hellosSent" : "hellosSendRefused");
            }
            foreach (var item in Drain(2))
            {
                var p = item.Packet;
                if (p.ClientNonce != _clientNonce) { Stats.Add("packetsWrongNonce"); continue; }
                var key = (p.ServerNonce, p.Serial);
                if (!Received.Add(key)) { Stats.Add("packetsDuplicate"); continue; }
                ReceivedOrder.Enqueue(key);
                while (ReceivedOrder.Count > 512) Received.Remove(ReceivedOrder.Dequeue());
                BuildMenuSearch.RecordBuilt(p.Blueprint);
                Stats.Add("recordsAdded");
            }
            // The history file is written on a debounce, also while the build menu is closed.
            BuildMenuSearch.FlushRecent();
        }
        catch (Exception e) { Warn(e); }
    }

    private static List<(Wire Packet, Entity User, ulong Platform, long At)> Drain(int kind)
    {
        var result = new List<(Wire, Entity, ulong, long)>();
        lock (Gate)
        {
            int n = Inbox.Count;
            while (n-- > 0)
            {
                var item = Inbox.Dequeue();
                if (item.Packet.Kind == kind) result.Add(item); else Inbox.Enqueue(item);
            }
        }
        return result;
    }

    private static bool Server(SystemHookContext hook)
    {
        if (!_installed || hook.World.Name != Worlds.ServerName) return false;
        if (_serverEpoch != hook.World.SequenceNumber || !_queries)
        {
            ResetServer();
            _server = hook.World;
            _serverEpoch = hook.World.SequenceNumber;
            _serverNonce = Nonce();
            var em = hook.World.EntityManager;
            // Mark ownership before creation so partial query setup also disposes cleanly.
            _queries = true;
            _requests = Query.Build(em, false, false, C<BuildTileModelEvent>(), C<FromCharacter>());
            _abilities = Query.Build(em, false, false, C<AbilityPreCastFinishedEvent>());
            _spawning = Query.Build(em, true, false, C<BlueprintData>(), C<SpawnTag>());
            _sequences = Query.Build(em, true, false, C<SpawnSequenceForEntity>(), C<SpawnTag>());
        }
        long now = Environment.TickCount64;
        bool any;
        lock (Gate) any = Inbox.Count > 0;
        if (any)
            foreach (var item in Drain(1))
            {
                // A peer lives from its hello's arrival, not from when a hook happened to drain it.
                ulong key = Key(item.User);
                if (now - item.At > PeerLifetimeMs || (Peers.TryGetValue(key, out var seen) && seen.At > item.At)) { Stats.Add("hellosStale"); continue; }
                if (Peers.Count < 256 || Peers.ContainsKey(key))
                {
                    if (!Peers.ContainsKey(key)) Stats.Add("peersRegistered");
                    Peers[key] = new(item.Packet.ClientNonce, item.Platform, item.At);
                }
            }
        if (now >= _pruneAt)
        {
            _pruneAt = now + 1000;
            foreach (var key in new List<ulong>(Peers.Keys)) if (now - Peers[key].At > PeerLifetimeMs) Peers.Remove(key);
            Correlation.Prune(now);
        }
        return true;
    }

    private static void BeforePlacement(SystemHookContext hook)
    {
        try
        {
            if (!Server(hook) || Peers.Count == 0) return;
            var em = hook.World.EntityManager;
            int requests = _requests.CalculateEntityCount(), abilities = _abilities.CalculateEntityCount();
            if (requests + abilities == 0) return;
            if (requests > 128 || abilities > 128) { Stats.Add("requestsTooMany"); Correlation.Clear(); return; }
            // Confirmation requires SpawnTag on the structure, and only a newly created entity carries it, so the
            // structures that could be mistaken for this placement's result are the ones spawning right now. A burst
            // larger than the bound (a world load) skips these requests only; they are omitted, never guessed.
            if (_spawning.CalculateEntityCount() > MaxSpawning) { Stats.Add("spawningTooMany"); return; }
            var existing = new HashSet<ulong>();
            foreach (var entity in Entities(_spawning)) existing.Add(Key(entity));
            long now = Environment.TickCount64;
            if (requests > 0)
                foreach (var entity in Entities(_requests))
                {
                    Stats.Add("requestsSeen");
                    byte* p = AiRuntime.RO<BuildTileModelEvent>(em, entity, 0x18);
                    byte* f = AiRuntime.RO<FromCharacter>(em, entity, 0x10);
                    if (p == null || f == null) { Stats.Add("requestsNullComponent"); continue; }
                    if (*(int*)(p + 0x14) != 0) { Stats.Add("requestsRelocation"); continue; } // RebuildUniqueKey.Id
                    Entity user = *(Entity*)f, character = *(Entity*)(f + 8);
                    if (!Owns(em, user, character)) { Stats.Add("requestsNotOwned"); continue; }
                    Stats.Add(Correlation.Add(Key(entity), false, Key(user), *(int*)p, Position(p + 4), p[0x10], now, existing)
                        ? "correlationAdds" : "correlationAddRefused");
                }
            if (abilities > 0)
                foreach (var entity in Entities(_abilities))
                {
                    Stats.Add("abilityEventsSeen");
                    byte* f = AiRuntime.RO<AbilityPreCastFinishedEvent>(em, entity, 0x18);
                    if (f == null) continue;
                    Entity character = *(Entity*)f, ability = *(Entity*)(f + 8);
                    byte* p = AiRuntime.RO<PlaceTilemodelAbility>(em, ability, 0x12);
                    byte* c = AiRuntime.RO<ControlledBy>(em, character, 8);
                    if (p == null || c == null) { Stats.Add("abilityEventsNotPlacement"); continue; }
                    Entity user = *(Entity*)c;
                    if (!Owns(em, user, character)) { Stats.Add("abilityEventsNotOwned"); continue; }
                    Stats.Add(Correlation.Add(Key(entity), true, Key(user), *(int*)(p + 0xC), Position(p), p[0x10], now, existing)
                        ? "correlationAdds" : "correlationAddRefused");
                }
        }
        catch (Exception e) { Correlation.Clear(); Warn(e); }
    }

    private static bool Owns(EntityManager em, Entity user, Entity character)
    {
        if (!Peers.TryGetValue(Key(user), out var peer) || !user.Has<User>(em)) return false;
        var data = em.GetComponentData<User>(user); // existing typed accessor for bool-bearing User
        byte* player = AiRuntime.RO<PlayerCharacter>(em, character, 0x68);
        return data.IsConnected && data.PlatformId == peer.Platform && player != null && *(Entity*)(player + 0x60) == user;
    }

    private static void BeforeSpawnReaction(SystemHookContext hook)
    {
        try
        {
            if (!Server(hook) || Peers.Count == 0 || Correlation.PendingCount == 0) return;
            var em = hook.World.EntityManager;
            int spawning = _spawning.CalculateEntityCount();
            if (spawning == 0) return;
            if (spawning > MaxSpawning) { Stats.Add("spawningTooMany"); return; }
            CountSequences(em);
            long now = Environment.TickCount64;
            foreach (var target in Entities(_spawning))
            {
                Stats.Add("candidates");
                byte* b = AiRuntime.RO<BlueprintData>(em, target, 0x24);
                byte* o = AiRuntime.RO<UserOwner>(em, target, 0xC);
                byte* t = AiRuntime.RO<Translation>(em, target, 12);
                byte* r = AiRuntime.RO<TilePosition>(em, target, 12);
                byte* g = AiRuntime.RO<PrefabGUID>(em, target, 4);
                if (b == null) { Stats.Add("rejectNullBlueprint"); continue; }
                if (o == null) { Stats.Add("rejectNullOwner"); continue; }
                if (t == null) { Stats.Add("rejectNullTranslation"); continue; }
                if (r == null) { Stats.Add("rejectNullTilePosition"); continue; }
                if (g == null) { Stats.Add("rejectNullPrefab"); continue; }
                if (o[NetworkedEntityWaitingOffset] != 0) { Stats.Add("rejectOwnerWaitingForSync"); continue; }
                int guid = *(int*)(b + 8);
                // Strict identity: generated/transformed pieces (a prefab other than their blueprint) are not covered.
                if (guid == 0 || *(int*)g != guid) { Stats.Add("rejectGuidMismatch"); continue; }
                if (*(int*)(b + 0x1C) == 0 && *(int*)(b + 0x20) == 0) Stats.Add("noBuildingSequence"); // diagnostic only
                Entity user = *(Entity*)o;
                if (!Peers.TryGetValue(Key(user), out var peer) || !user.Has<User>(em)) { Stats.Add("rejectNoPeer"); continue; }
                if (!Correlation.HasPendingFor(Key(user))) { Stats.Add(BuildRecentStats.NameOf(BuildRecentConfirm.NoPendingForUser)); continue; }
                var data = em.GetComponentData<User>(user);
                if (!data.IsConnected || data.PlatformId != peer.Platform) { Stats.Add("rejectNotConnected"); continue; }
                var result = Correlation.Check(Key(target), Key(user), guid, Position(t), r[8], now);
                Stats.Add(BuildRecentStats.NameOf(result));
                if (result != BuildRecentConfirm.Confirmed) continue;
                var packet = new Wire { Version = 1, Kind = 2, ClientNonce = peer.Nonce,
                    ServerNonce = _serverNonce, Serial = ++_serial, Blueprint = guid };
                Stats.Add(CustomEvents.SendToUser(hook.World, user, EventId, packet) ? "packetsSent" : "packetsSendFailed");
            }
        }
        catch (Exception e) { Correlation.Clear(); Warn(e); }
    }

    /// <summary>Diagnostics only: how many sequence entities exist and whether their targets still carry SpawnTag.</summary>
    private static void CountSequences(EntityManager em)
    {
        int count = _sequences.CalculateEntityCount();
        if (count == 0 || count > MaxSpawning) return;
        foreach (var entity in Entities(_sequences))
        {
            Stats.Add("sequenceEntities");
            byte* s = AiRuntime.RO<SpawnSequenceForEntity>(em, entity, 0x10);
            if (s == null) continue;
            // Target._WaitingForSync: Target @4 + named nested field @8, not an opaque flags byte.
            if (s[SequenceTargetOffset + NetworkedEntityWaitingOffset] != 0) { Stats.Add("sequenceTargetWaitingForSync"); continue; }
            Entity target = *(Entity*)(s + SequenceTargetOffset);
            if (!target.Has<SpawnTag>(em)) Stats.Add("sequenceTargetNoSpawnTag");
        }
    }

    private static void ResetClient()
    {
        lock (Gate) _clientNonce = 0;
        _clientEpoch = 0; _helloAt = 0;
        Received.Clear(); ReceivedOrder.Clear();
        Drain(2);
    }

    private static void ResetServer()
    {
        if (_queries && _server != null && _server.IsCreated)
            foreach (var q in new[] { _requests, _abilities, _spawning, _sequences })
                try { q.Dispose(); } catch { }
        _queries = false; _server = null; _serverEpoch = 0; _serial = 0; _pruneAt = 0;
        _requests = _abilities = _spawning = _sequences = default;
        Peers.Clear(); Correlation.Clear();
    }

    private static ComponentType C<T>() => ComponentIndex.Of<T>(ComponentType.AccessMode.ReadOnly);
    private static List<Entity> Entities(EntityQuery query)
    {
        var result = new List<Entity>();
        var entities = query.ToEntityArray(Allocator.Temp);
        try { for (int i = 0; i < entities.Length; i++) result.Add(entities[i]); }
        finally { entities.Dispose(); }
        return result;
    }
    private static ulong Key(Entity e) => e == Entity.Null ? 0 : ((ulong)(uint)e.Index << 32) | (uint)e.Version;
    private static Vector3 Position(byte* p) => new(*(float*)p, *(float*)(p + 4), *(float*)(p + 8));
    private static ulong Nonce() { ulong n; do { n = BitConverter.ToUInt64(Guid.NewGuid().ToByteArray(), 0); } while (n == 0); return n; }
    private static void Warn(Exception e) { if (_warnings++ < 4) Plugin.Logger?.LogWarning("Recent build observation refused: " + e.Message); }

    // Field offsets from both sides' current interop metadata; no wrapper sizeof/stride assumptions or native calls.
    private static void At<T>(string field, int expected)
    {
        if ((int)Marshal.OffsetOf<T>(field) != expected) throw new InvalidOperationException(typeof(T).Name + "." + field + " layout changed");
    }
    private static void CheckLayouts()
    {
        At<BuildTileModelEvent>(nameof(BuildTileModelEvent.PrefabGuid), 0);
        At<BuildTileModelEvent>(nameof(BuildTileModelEvent.SpawnTranslation), 4);
        At<BuildTileModelEvent>(nameof(BuildTileModelEvent.SpawnTileRotation), 0x10);
        At<BuildTileModelEvent>(nameof(BuildTileModelEvent.RebuildUniqueKey), 0x14);
        At<ProjectM.CastleBuilding.Rebuilding.CastleRebuildUniqueKey>(nameof(ProjectM.CastleBuilding.Rebuilding.CastleRebuildUniqueKey.Id), 0);
        At<FromCharacter>(nameof(FromCharacter.User), 0); At<FromCharacter>(nameof(FromCharacter.Character), 8);
        At<AbilityPreCastFinishedEvent>(nameof(AbilityPreCastFinishedEvent.Character), 0);
        At<AbilityPreCastFinishedEvent>(nameof(AbilityPreCastFinishedEvent.Ability), 8);
        At<PlaceTilemodelAbility>(nameof(PlaceTilemodelAbility.SpawnTranslation), 0);
        At<PlaceTilemodelAbility>(nameof(PlaceTilemodelAbility.PrefabGuid), 0xC);
        At<PlaceTilemodelAbility>(nameof(PlaceTilemodelAbility.SpawnTileRotation), 0x10);
        At<ControlledBy>(nameof(ControlledBy.Controller), 0);
        At<PlayerCharacter>(nameof(PlayerCharacter.UserEntity), 0x60);
        At<UserOwner>(nameof(UserOwner.Owner), 0);
        At<NetworkedEntity>(nameof(NetworkedEntity._Entity), 0);
        At<NetworkedEntity>(nameof(NetworkedEntity._WaitingForSync), NetworkedEntityWaitingOffset);
        At<SpawnSequenceForEntity>(nameof(SpawnSequenceForEntity.SequenceGuid), 0);
        At<SpawnSequenceForEntity>(nameof(SpawnSequenceForEntity.Target), SequenceTargetOffset);
        At<BlueprintData>(nameof(BlueprintData.Guid), 8);
        At<BlueprintData>(nameof(BlueprintData.BuildingSequence), 0x1C);
        At<BlueprintData>(nameof(BlueprintData.PhasedBuildingSequence), 0x20);
        At<Translation>(nameof(Translation.Value), 0); At<TilePosition>(nameof(TilePosition.TileRotation), 8);
        At<PrefabGUID>(nameof(PrefabGUID._Value), 0);
    }
}
