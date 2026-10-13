using System;
using System.Runtime.InteropServices;
using System.Text.Json;
using ProjectM;
using ProjectM.Network;
using Progenitor.Ecs;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace Progenitor.Dev.Bots;

/// <summary>
/// Server op <c>bot_exit_graveyard</c>: leave the starting graveyard the way a player does. The
/// graveyard copy has no walking exit (its archway gate is closed scenery); Interact on the
/// <c>TM_SelectStartGraveyardGate</c> in that archway opens the map, the player picks one of the two
/// Farbane exits (<c>StartGraveyardExitWaypoint</c>), and the client sends
/// <c>TeleportEvents_ToServer.TeleportToWaypointEvent { NetworkId FromWaypoint @0x0; NetworkId
/// TargetChunkWaypoint @0xC }</c> (event id -82029052). The server refuses it beyond 15 m of the gate (native handler).
/// This op queues that event with the
/// bot's <c>FromCharacter</c>, as <c>bot_use</c> does for <c>UseItemEvent</c>: the nearest gate as the source
/// and the exit nearest (<c>x</c>, <c>z</c>) as the target. Args: <c>user</c>, <c>x</c>, <c>z</c> (where the
/// bot is heading). Refuses when no gate is within 15 m; success shows as the bot's new position.
/// </summary>
internal static class BotGraveyardExit
{
    private const float GateRadius = 15f;

    public static unsafe object Run(World world, JsonElement args)
    {
        if (!Progenitor.Plugin.IsServerProcess) throw new InvalidOperationException("bot_exit_graveyard runs on the server");
        var em = world.EntityManager;
        var character = BotCast.ResolveCharacter(em, args, out string name);
        if (!character.TryRead<Translation>(em, out var me)) throw new InvalidOperationException("bot has no Translation");
        float2 goal = new(Num(args, "x", me.Value.x), Num(args, "z", me.Value.z));

        // Gate and exits both carry StartGraveyardExitWaypoint (live): the gate is the Interactable one,
        // and it is disabled while no player is in range (DisableWhenNoPlayersInRange).
        var markType = ComponentIndex.Of<StartGraveyardExitWaypoint>(ComponentType.AccessMode.ReadOnly);
        var trType = ComponentIndex.Of<Translation>(ComponentType.AccessMode.ReadOnly);
        var interType = ComponentIndex.Of<Interactable>(ComponentType.AccessMode.ReadOnly);
        var all = Query.Entities(em, includeDisabled: true, includeSystems: false, markType, trType);
        var gates = new System.Collections.Generic.List<Entity>();
        var exits = new System.Collections.Generic.List<Entity>();
        foreach (var e in all) (em.HasComponent(e, interType) ? gates : exits).Add(e);
        var (gate, gateD) = Nearest(em, gates, me.Value, null);
        if (gate == Entity.Null) throw new InvalidOperationException("no start-graveyard gate in this world");
        if (gateD > GateRadius) return new { name, sent = false, reason = $"the gate is {gateD:F1} m away (the server allows {GateRadius} m)", gate = Pos(em, gate) };
        var (exit, _) = Nearest(em, exits, default, goal);
        if (exit == Entity.Null) throw new InvalidOperationException("no graveyard exit waypoint in this world");

        var from = NetId(em, gate);
        var to = NetId(em, exit);
        if ((int)Marshal.OffsetOf<TeleportEvents_ToServer.TeleportToWaypointEvent>(nameof(TeleportEvents_ToServer.TeleportToWaypointEvent.FromWaypoint)) != 0x0
            || (int)Marshal.OffsetOf<TeleportEvents_ToServer.TeleportToWaypointEvent>(nameof(TeleportEvents_ToServer.TeleportToWaypointEvent.TargetChunkWaypoint)) != 0xC)
            throw new InvalidOperationException("TeleportToWaypointEvent layout differs from the dump; refusing the raw write");
        var pc = em.GetComponentData<PlayerCharacter>(character);
        var type = ComponentIndex.Of<TeleportEvents_ToServer.TeleportToWaypointEvent>(ComponentType.AccessMode.ReadWrite);
        var ev = em.CreateEntity(type, ComponentIndex.Of<FromCharacter>(ComponentType.AccessMode.ReadWrite));
        try
        {
            int* raw = (int*)em.GetComponentDataRawRW(ev, type.TypeIndex);
            if (raw == null) throw new InvalidOperationException("GetComponentDataRawRW returned null");
            raw[0] = from.i0; raw[1] = from.i1; raw[2] = from.i2;
            raw[3] = to.i0; raw[4] = to.i1; raw[5] = to.i2;
            em.SetComponentData(ev, new FromCharacter { User = pc.UserEntity, Character = character });
        }
        catch
        {
            em.DestroyEntity(ev);
            throw;
        }
        return new { name, sent = true, gate = Pos(em, gate), gateDistance = MathF.Round(gateD, 2), exit = Pos(em, exit), @event = $"{ev.Index}:{ev.Version}", note = "queued; check the bot's position" };
    }

    private static (Entity e, float d) Nearest(EntityManager em, System.Collections.Generic.List<Entity> list, float3 from3, float2? from2)
    {
        Entity best = Entity.Null; float bestD = float.MaxValue;
        foreach (var e in list)
        {
            if (!e.TryRead<Translation>(em, out var t)) continue;
            float d = from2 is { } p ? math.distance(t.Value.xz, p) : math.distance(t.Value, from3);
            if (d < bestD) { bestD = d; best = e; }
        }
        return (best, bestD);
    }

    private static unsafe (int i0, int i1, int i2) NetId(EntityManager em, Entity e)
    {
        var netType = ComponentIndex.Of<NetworkId>(ComponentType.AccessMode.ReadOnly);
        if (!em.HasComponent(e, netType) || Marshal.SizeOf<NetworkId>() != 12) throw new InvalidOperationException($"{e.Index}:{e.Version} has no 12-byte NetworkId");
        int* src = (int*)em.GetComponentDataRawRO(e, netType.TypeIndex);
        if (src == null) throw new InvalidOperationException("NetworkId read returned null");
        return (src[0], src[1], src[2]);
    }

    private static object? Pos(EntityManager em, Entity e) =>
        e.TryRead<Translation>(em, out var t) ? new { entity = $"{e.Index}:{e.Version}", x = t.Value.x, y = t.Value.y, z = t.Value.z } : null;

    private static float Num(JsonElement args, string key, float fallback) =>
        args.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetSingle() : fallback;
}
