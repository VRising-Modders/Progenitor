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
/// Creation writes UserOwner and a BuildingSequence targeting the new structure; the server
/// sequence consumer runs after spawn playback and before SpawnTag removal. A pre-placement
/// snapshot excludes existing targets: only structures still carrying SpawnTag can ever confirm, so
/// the snapshot holds just those (a short-lived handful), never every structure in the world.
/// Unsupported transformations are omitted, never inferred.
/// No wallpaper coverage. Static/compile verification is not live verification.
/// </summary>
internal static unsafe class BuildRecentObserver
{
    internal const int EventId = 0x50725262; // PrRb, Core-reserved; both directions versioned below.
    /// <summary>Most just-spawned structures (BlueprintData with SpawnTag) one placement frame snapshots.</summary>
    private const int MaxSpawning = 512;
    /// <summary>A peer's hello counts this long from when it arrived (the client sends one every 10 s).</summary>
    private const long PeerLifetimeMs = 30000;
    private const int SequenceTargetOffset = 4;
    private const int NetworkedEntityWaitingOffset = 8;
    private static readonly BuildRecentCorrelation Correlation = new();
    private static readonly List<IDisposable> Hooks = new();
    private static readonly object Gate = new();
    private static readonly Queue<(Wire Packet, Entity User, ulong Platform, long At)> Inbox = new();
    private static readonly Dictionary<ulong, Peer> Peers = new();
    private static readonly HashSet<(ulong, ulong)> Received = new();
    private static readonly Queue<(ulong, ulong)> ReceivedOrder = new();
    private static World? _server;
    private static ulong _serverEpoch, _clientEpoch, _serverNonce, _clientNonce, _serial;
    private static long _helloAt;
    private static bool _installed, _queries;
    private static int _warnings;
    private static EntityQuery _requests, _abilities, _spawning, _sequences;
    private sealed record Peer(ulong Nonce, ulong Platform, long At);

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
            if (Plugin.IsServerProcess)
            {
                CustomEvents.OnReceiveFromClient(EventId, ReceiveHello);
                Hooks.Add(SystemHooks.Before<PlaceTileModelSystem>(BeforePlacement));
                Hooks.Add(SystemHooks.Before<SpawnSequenceForEntitySystem_Server>(BeforeSequences));
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

    private static void Receive(int id, byte* p, int length)
    {
        if (!_installed || id != EventId || p == null || length != sizeof(Wire)) return;
        var packet = *(Wire*)p;
        if (packet.Version != 1 || packet.Kind != 2 || packet.ClientNonce == 0
            || packet.ServerNonce == 0 || packet.Serial == 0 || packet.Blueprint == 0) return;
        lock (Gate)
            if (packet.ClientNonce == _clientNonce && Inbox.Count < 256) Inbox.Enqueue((packet, Entity.Null, 0, 0));
    }

    private static void ReceiveHello(int id, Entity userEntity, User user, byte* p, int length)
    {
        if (!_installed || id != EventId || p == null || length != sizeof(Wire) || !user.IsConnected) return;
        var packet = *(Wire*)p;
        if (packet.Version != 1 || packet.Kind != 1 || packet.ClientNonce == 0
            || packet.ServerNonce != 0 || packet.Serial != 0 || packet.Blueprint != 0) return;
        lock (Gate)
        {
            // The placement hooks drain hellos only when they run; keep the newest ones and their arrival time,
            // so a backlog never crowds out a fresh hello and a stale one still expires.
            if (Inbox.Count >= 256) Inbox.Dequeue();
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
                CustomEvents.SendToServer(EventId, hello);
            }
            foreach (var item in Drain(2))
            {
                var p = item.Packet;
                if (p.ClientNonce != _clientNonce) continue;
                var key = (p.ServerNonce, p.Serial);
                if (!Received.Add(key)) continue;
                ReceivedOrder.Enqueue(key);
                while (ReceivedOrder.Count > 512) Received.Remove(ReceivedOrder.Dequeue());
                BuildMenuSearch.RecordBuilt(p.Blueprint);
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
        foreach (var item in Drain(1))
        {
            // A peer lives from its hello's arrival, not from when a placement hook happened to drain it.
            ulong key = Key(item.User);
            if (now - item.At > PeerLifetimeMs || (Peers.TryGetValue(key, out var seen) && seen.At > item.At)) continue;
            if (Peers.Count < 256 || Peers.ContainsKey(key))
                Peers[key] = new(item.Packet.ClientNonce, item.Platform, item.At);
        }
        foreach (var key in new List<ulong>(Peers.Keys)) if (now - Peers[key].At > PeerLifetimeMs) Peers.Remove(key);
        Correlation.Prune(now);
        return true;
    }

    private static void BeforePlacement(SystemHookContext hook)
    {
        try
        {
            if (!Server(hook) || Peers.Count == 0) return;
            var em = hook.World.EntityManager;
            if (_requests.CalculateEntityCount() + _abilities.CalculateEntityCount() == 0) return;
            if (_requests.CalculateEntityCount() > 128 || _abilities.CalculateEntityCount() > 128) { Correlation.Clear(); return; }
            // Confirm requires SpawnTag on the target, and only a newly created entity carries it, so the structures
            // that could be mistaken for this placement's result are the ones spawning right now. A burst larger than
            // the bound (a world load) skips these requests only; they are omitted, never guessed.
            if (_spawning.CalculateEntityCount() > MaxSpawning) return;
            var existing = new HashSet<ulong>();
            foreach (var entity in Entities(_spawning)) existing.Add(Key(entity));
            long now = Environment.TickCount64;
            foreach (var entity in Entities(_requests))
                {
                    byte* p = AiRuntime.RO<BuildTileModelEvent>(em, entity, 0x18);
                    byte* f = AiRuntime.RO<FromCharacter>(em, entity, 0x10);
                    if (p == null || f == null || *(int*)(p + 0x14) != 0) continue; // relocation excluded
                    Entity user = *(Entity*)f, character = *(Entity*)(f + 8);
                    if (!Owns(em, user, character)) continue;
                    Correlation.Add(Key(entity), false, Key(user), *(int*)p, Position(p + 4), p[0x10], now, existing);
                }
            foreach (var entity in Entities(_abilities))
                {
                    byte* f = AiRuntime.RO<AbilityPreCastFinishedEvent>(em, entity, 0x18);
                    if (f == null) continue;
                    Entity character = *(Entity*)f, ability = *(Entity*)(f + 8);
                    byte* p = AiRuntime.RO<PlaceTilemodelAbility>(em, ability, 0x12);
                    byte* c = AiRuntime.RO<ControlledBy>(em, character, 8);
                    if (p == null || c == null) continue;
                    Entity user = *(Entity*)c;
                    if (!Owns(em, user, character)) continue;
                    Correlation.Add(Key(entity), true, Key(user), *(int*)(p + 0xC), Position(p), p[0x10], now, existing);
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

    private static void BeforeSequences(SystemHookContext hook)
    {
        try
        {
            if (!Server(hook) || Peers.Count == 0) return;
            var em = hook.World.EntityManager;
            if (_sequences.CalculateEntityCount() > 512) { Correlation.Clear(); return; }
            foreach (var entity in Entities(_sequences))
            {
                byte* s = AiRuntime.RO<SpawnSequenceForEntity>(em, entity, 0x10);
                // Target._WaitingForSync: Target @4 + named nested field @8, not an opaque flags byte.
                if (s == null || s[SequenceTargetOffset + NetworkedEntityWaitingOffset] != 0) continue;
                Entity target = *(Entity*)(s + SequenceTargetOffset);
                if (!target.Has<SpawnTag>(em)) continue;
                byte* b = AiRuntime.RO<BlueprintData>(em, target, 0x24);
                byte* o = AiRuntime.RO<UserOwner>(em, target, 0xC);
                byte* t = AiRuntime.RO<Translation>(em, target, 12);
                byte* r = AiRuntime.RO<TilePosition>(em, target, 12);
                byte* g = AiRuntime.RO<PrefabGUID>(em, target, 4);
                if (b == null || o == null || t == null || r == null || g == null || o[8] != 0) continue;
                int sequence = *(int*)s, guid = *(int*)(b + 8);
                // Strict identity: generated/transformed pieces are outside this first observer's coverage.
                if (guid == 0 || *(int*)g != guid || sequence == 0
                    || (sequence != *(int*)(b + 0x1C) && sequence != *(int*)(b + 0x20))) continue;
                Entity user = *(Entity*)o;
                if (!Peers.TryGetValue(Key(user), out var peer) || !user.Has<User>(em)) continue;
                var data = em.GetComponentData<User>(user);
                if (!data.IsConnected || data.PlatformId != peer.Platform) continue;
                if (!Correlation.Confirm(Key(target), Key(user), guid, Position(t), r[8], Environment.TickCount64)) continue;
                var packet = new Wire { Version = 1, Kind = 2, ClientNonce = peer.Nonce,
                    ServerNonce = _serverNonce, Serial = ++_serial, Blueprint = guid };
                CustomEvents.SendToUser(hook.World, user, EventId, packet);
            }
        }
        catch (Exception e) { Correlation.Clear(); Warn(e); }
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
        _queries = false; _server = null; _serverEpoch = 0; _serial = 0;
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
