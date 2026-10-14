using System;
using System.Collections.Generic;
using ProjectM;
using ProjectM.Network;
using Progenitor.Ecs;
using Progenitor.Events;
using Progenitor.Players;
using Unity.Entities;
using Unity.Transforms;

namespace Progenitor.Castles;

/// <summary>
/// Server publisher of <see cref="PlayerReady"/> and <see cref="PlayerTerritoryChanged"/>.
///
/// <para><b>Cost.</b> PlayerReady: only users whose session has not reached ready are read, four
/// times a second, one <c>User</c> read each; after that nothing. PlayerTerritoryChanged: only
/// while something is subscribed, once a second, one position read per connected player and an
/// <c>IsInside</c> test against each castle (bounds first, then the cached tile mask); the castle
/// list is re-read every ten seconds or after a world load.</para>
/// </summary>
internal static class CastlePresenceRuntime
{
    private const long ReadyPollMs = 250;
    private const long TerritoryPollMs = 1_000;
    private const long CastleRefreshMs = 10_000;

    private static readonly PlayerReadyTracker Ready = new();
    private static readonly PlayerTerritoryTracker Territories = new();
    private static readonly HashSet<long> Present = new();
    private static IReadOnlyList<ICastleHandle> _castles = Array.Empty<ICastleHandle>();
    private static long _nextReady, _nextTerritory, _castlesStamp;
    private static int _castlesEpoch = int.MinValue;
    private static IDisposable? _connected, _disconnected;
    private static bool _hooked;

    internal static void Install()
    {
        if (!Plugin.IsServerProcess) return;
        _connected = EventBus.On<PlayerConnected>(e => Ready.Connected(Key(e.UserEntity)), priority: 1900);
        _disconnected = EventBus.On<PlayerDisconnected>(e => Ready.Disconnected(Key(e.UserEntity)), priority: 1900);
        MarkConnectedAsReady();
        Plugin.Frame += Tick;
        _hooked = true;
    }

    internal static void Uninstall()
    {
        if (_hooked) Plugin.Frame -= Tick;
        _hooked = false;
        _connected?.Dispose();
        _disconnected?.Dispose();
        _connected = _disconnected = null;
        Ready.Clear();
        Territories.Clear();
        Present.Clear();
        _castles = Array.Empty<ICastleHandle>();
        _castlesEpoch = int.MinValue;
    }

    private static long Key(Entity e) => ((long)e.Index << 32) | (uint)e.Version;

    private static Entity FromKey(long key) => new() { Index = (int)(key >> 32), Version = (int)(key & 0xFFFFFFFF) };

    /// <summary>A Core installed into a running server: players already in the world are not announced again.</summary>
    private static void MarkConnectedAsReady()
    {
        var world = Worlds.Server;
        if (world == null || !world.IsCreated) return;
        try
        {
            var em = world.EntityManager;
            var users = Query.Entities(em, true, false, ComponentIndex.Of<User>(ComponentType.AccessMode.ReadOnly));
            for (int i = 0; i < users.Count; i++)
            {
                var user = em.GetComponentData<User>(users[i]);
                if (!user.IsConnected) continue;
                // Someone still loading in when Core installs is still waited on.
                var character = Entity.Null;
                try { character = user.LocalCharacter._Entity; } catch { }
                bool inWorld = character != Entity.Null && character.Exists(em) && character.Has<PlayerCharacter>(em)
                    && user.LastFullyAckedFrame > 0;
                if (inWorld) Ready.AlreadyReady(Key(users[i]));
                else Ready.Connected(Key(users[i]));
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Castles: presence startup scan threw: " + e.Message);
        }
    }

    private static void Tick()
    {
        long now = Environment.TickCount64;
        try
        {
            if (now >= _nextReady)
            {
                _nextReady = now + ReadyPollMs;
                if (Ready.Pending > 0) PollReady();
            }

            if (now >= _nextTerritory)
            {
                _nextTerritory = now + TerritoryPollMs;
                if (EventBus.SubscriberCount<PlayerTerritoryChanged>() > 0) PollTerritories(now);
                else if (Territories.Count > 0) Territories.Clear();
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Castles: presence tick threw: " + e.Message);
        }
    }

    private static void PollReady()
    {
        var world = Worlds.Server;
        if (world == null || !world.IsCreated) return;
        var em = world.EntityManager;
        Ready.CopyPending(PendingScratch);
        foreach (var key in PendingScratch)
        {
            var userEntity = FromKey(key);
            if (!userEntity.Exists(em) || !userEntity.Has<User>(em)) { Ready.Disconnected(key); continue; }
            User user;
            try { user = em.GetComponentData<User>(userEntity); }
            catch { continue; }
            var character = Entity.Null;
            try { character = user.LocalCharacter._Entity; } catch { }
            bool live = character != Entity.Null && character.Exists(em) && character.Has<PlayerCharacter>(em);
            if (!Ready.Observe(key, live, user.LastFullyAckedFrame)) continue;
            string name;
            try { name = user.CharacterName.ToString() ?? ""; } catch { name = ""; }
            Plugin.Logger?.LogInfo("Castles: PlayerReady " + userEntity.Index + ":" + userEntity.Version);
            EventBus.Publish(new PlayerReady(CastleId.From(userEntity), CastleId.From(character), user.PlatformId, name));
        }
    }

    private static readonly List<long> PendingScratch = new();

    private static void PollTerritories(long now)
    {
        var world = Worlds.Server;
        if (world == null || !world.IsCreated) return;
        int epoch = Progenitor.WorldState.WorldEpoch.Current;
        if (epoch != _castlesEpoch || now - _castlesStamp > CastleRefreshMs)
        {
            if (epoch != _castlesEpoch) Territories.Clear();
            _castles = Castles.All;
            _castlesEpoch = epoch;
            _castlesStamp = now;
        }

        var em = world.EntityManager;
        Present.Clear();
        foreach (var p in PlayerDirectory.Online(world))
        {
            var s = p.Session;
            if (!s.HasCharacter) continue;
            long key = ((long)s.UserIndex << 32) | (uint)s.UserVersion;
            Present.Add(key);
            var character = new Entity { Index = s.CharacterIndex, Version = s.CharacterVersion };
            if (!character.TryRead(em, out Translation t)) continue;
            CastleVec3 pos = t.Value;
            ICastleHandle? here = null;
            for (int i = 0; i < _castles.Count; i++)
                if (_castles[i].IsInside(pos)) { here = _castles[i]; break; }
            int territory = here?.Territory.Index ?? -1;
            if (!Territories.Observe(key, territory, out int from, out int to, out bool initial)) continue;
            EventBus.Publish(new PlayerTerritoryChanged(
                new CastleId(s.UserIndex, s.UserVersion), new CastleId(s.CharacterIndex, s.CharacterVersion),
                from, to, here ?? CastleHandle.Disabled(), initial));
        }

        Territories.Retain(Present);
    }
}
