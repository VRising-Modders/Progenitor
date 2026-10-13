using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.Json;
using ProjectM.Pathfinding;
using ProjectM.Tiles;
using Progenitor.Ecs;
using Unity.Entities;
using Unity.Mathematics;

namespace Progenitor.Dev.Bots;

/// <summary>
/// Server op <c>bot_path</c>: the "legs" of a headless player. Asks the game's own
/// <c>PathfindingSystem</c> for a walkable route and returns world waypoints, which the client-side
/// <c>bot</c> op then walks one by one. The bot still moves only through its input; the server is
/// used as a map oracle, like a player who knows the terrain.
///
/// <para><b>Request entity</b> (research unit-movement.md 1): <c>PathfindingSystem</c> solves any
/// entity with <c>PathRequest</c> and fills its <c>PathBuffer</c> (<c>TileCoordinate</c>
/// {X,Z,HeightLevel} ints, stride 12). The request struct is built by the game's
/// <c>PathRequest.Create</c> + <c>PathfindingSolver.Approach</c> (all blittable interop structs)
/// and copied into the entity through <c>GetComponentDataRawRW</c> (84 bytes, the native size
/// measured in the unit spike; the interop TypeSize read 4 and SizeInChunk reads an implausible 483,
/// so neither is trusted as a size, only as a floor). Status @0x48 (Success 0, Failed 1, Awaiting 2);
/// Destroy flag @0x50. Terrain preferences are borrowed from a ground-humanoid prefab's
/// <c>Pathfinder</c> (players have none). <c>SearchBounds</c> is widened to cover both endpoints.</para>
///
/// <para>Actions: <c>request</c> {from:{x,y,z}, to:{x,y,z}, maxExpansions?} → {id};
/// <c>result</c> {id, cancel?} → {status, waypoints[]} (the request entity is destroyed once read or
/// canceled; unread requests are swept after 15 s and all are destroyed on unload).</para>
/// </summary>
internal static class BotPath
{
    private const int StatusOffset = 0x48;
    private const int DestroyOffset = 0x50;
    private const int TileStride = 12;
    private const int MaxTiles = 4096;

    private static readonly Dictionary<int, (Entity entity, double created)> Requests = new();
    private static int _nextId = 1;
    private static PathfindingTerrainData? _terrain;

    public static object Run(World world, JsonElement args)
    {
        if (!Progenitor.Plugin.IsServerProcess)
            throw new InvalidOperationException("bot_path runs on the server");
        string action = args.TryGetProperty("action", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString()! : "request";
        return action switch
        {
            "request" => Request(world, args),
            "result" => Result(world, args),
            _ => throw new ArgumentException("args.action must be request or result"),
        };
    }

    // Requests older than this are destroyed on the next request, whether or not anyone read them
    // (a caller that gave up, or a bridge error mid-poll, must not leak request entities).
    private const double MaxRequestAgeSeconds = 15;

    private static void Sweep(EntityManager em, double now)
    {
        var stale = new List<int>();
        foreach (var kv in Requests)
            if (now - kv.Value.created > MaxRequestAgeSeconds) stale.Add(kv.Key);
        foreach (var id in stale)
        {
            var e = Requests[id].entity;
            if (em.Exists(e)) em.DestroyEntity(e);
            Requests.Remove(id);
        }
    }

    /// <summary>Destroy every request this Dev generation made (plugin unload).</summary>
    public static void Shutdown()
    {
        var world = Progenitor.Worlds.Server;
        if (world != null && world.IsCreated)
            foreach (var kv in Requests)
                if (world.EntityManager.Exists(kv.Value.entity)) world.EntityManager.DestroyEntity(kv.Value.entity);
        Requests.Clear();
    }

    private static unsafe object Request(World world, JsonElement args)
    {
        var em = world.EntityManager;
        Sweep(em, UnityEngine.Time.realtimeSinceStartupAsDouble);
        float3 from = Vec(args, "from");
        float3 to = Vec(args, "to");
        int maxExpansions = args.TryGetProperty("maxExpansions", out var me) && me.ValueKind == JsonValueKind.Number ? me.GetInt32() : 6000;

        string? agent = args.TryGetProperty("agent", out var ag) && ag.ValueKind == JsonValueKind.String ? ag.GetString() : null;
        PathfindingTerrainData terrain;
        if (agent != null) terrain = BorrowTerrain(world, new[] { agent }) ?? throw new InvalidOperationException($"prefab '{agent}' has no Pathfinder");
        else terrain = _terrain ??= BorrowTerrain(world, AgentPrefabs) ?? throw new InvalidOperationException("no agent prefab with a Pathfinder to borrow terrain preferences from");
        var fromTile = TileCoordinate.FromWorldPos(from);
        var toTile = TileCoordinate.FromWorldPos(to);
        var solver = PathfindingSolver.Approach(toTile);
        var request = PathRequest.Create(from, 0.5f, terrain, maxExpansions, true, solver, Entity.Null);
        // Create only knows the start, so its SearchBounds need not contain the target (live: a
        // target 10 m away failed while one 14 m away in another direction succeeded). Cover both
        // endpoints plus a margin for detours.
        var createdBounds = request.SearchBounds;
        var b = createdBounds;
        bool inside = toTile.X >= b.Min.x && toTile.X <= b.Max.x && toTile.Z >= b.Min.y && toTile.Z <= b.Max.y;
        if (!inside)
        {
            const int margin = 48;
            request.SearchBounds = new ProjectM.BoundsMinMax
            {
                Min = new int2(Math.Min(b.Min.x, toTile.X - margin), Math.Min(b.Min.y, toTile.Z - margin)),
                Max = new int2(Math.Max(b.Max.x, toTile.X + margin), Math.Max(b.Max.y, toTile.Z + margin)),
            };
        }

        var reqType = ComponentIndex.Of<PathRequest>(ComponentType.AccessMode.ReadWrite);
        var bufType = ComponentIndex.Of<PathBuffer>(ComponentType.AccessMode.ReadWrite);
        int size = Marshal.SizeOf<PathRequest>();
        int sizeInChunk = TypeManager.GetTypeInfo(reqType.TypeIndex).SizeInChunk;
        if (sizeInChunk < size)
            throw new InvalidOperationException($"PathRequest SizeInChunk {sizeInChunk} < struct {size}; refusing the raw copy");

        var e = em.CreateEntity(reqType, bufType);
        byte* dst = (byte*)em.GetComponentDataRawRW(e, reqType.TypeIndex);
        if (dst == null) { em.DestroyEntity(e); throw new InvalidOperationException("GetComponentDataRawRW returned null"); }
        Buffer.MemoryCopy(&request, dst, sizeInChunk, size);
        int id = _nextId++;
        Requests[id] = (e, UnityEngine.Time.realtimeSinceStartupAsDouble);
        return new
        {
            id,
            entity = $"{e.Index}:{e.Version}",
            structSize = size,
            terrainFrom = _terrainSource,
            terrainBytes = TerrainHex(terrain),
            widened = !inside,
            status = Status(*(int*)(dst + StatusOffset)),
            fromTile = new { fromTile.X, fromTile.Z, fromTile.HeightLevel },
            toTile = new { toTile.X, toTile.Z, toTile.HeightLevel },
            createdBounds = new { min = new { createdBounds.Min.x, createdBounds.Min.y }, max = new { createdBounds.Max.x, createdBounds.Max.y } },
        };
    }

    private static unsafe object Result(World world, JsonElement args)
    {
        var em = world.EntityManager;
        int id = args.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.Number ? i.GetInt32() : throw new ArgumentException("args.id is required");
        if (!Requests.TryGetValue(id, out var req)) throw new InvalidOperationException($"no path request {id}");
        if (!em.Exists(req.entity)) { Requests.Remove(id); return new { id, status = "Gone" }; }
        var reqType = ComponentIndex.Of<PathRequest>(ComponentType.AccessMode.ReadWrite);
        byte* raw = (byte*)em.GetComponentDataRawRW(req.entity, reqType.TypeIndex);
        if (raw == null) { em.DestroyEntity(req.entity); Requests.Remove(id); return new { id, status = "Gone" }; }
        int status = *(int*)(raw + StatusOffset);
        double age = UnityEngine.Time.realtimeSinceStartupAsDouble - req.created;
        bool cancel = args.TryGetProperty("cancel", out var c) && c.ValueKind == JsonValueKind.True;
        if (status == 2 && age < 10 && !cancel) return new { id, status = "AwaitingPath", age };

        var waypoints = new List<object>();
        if (status == 0)
        {
            var bufType = ComponentIndex.Of<PathBuffer>(ComponentType.AccessMode.ReadOnly);
            int n = Math.Min(em.GetBufferLength(req.entity, bufType.TypeIndex), MaxTiles);
            byte* tiles = (byte*)em.GetBufferRawRO(req.entity, bufType.TypeIndex);
            for (int t = 0; tiles != null && t < n; t++)
            {
                var tile = *(TileCoordinate*)(tiles + t * TileStride);
                var p = TileCoordinate.ToWorldPos(tile);
                waypoints.Add(new { x = p.x, y = p.y, z = p.z });
            }
        }
        *(byte*)(raw + DestroyOffset) = 1;
        em.DestroyEntity(req.entity);
        Requests.Remove(id);
        return new { id, status = status == 2 ? "TimedOut" : Status(status), count = waypoints.Count, waypoints };
    }

    // Pathfinder.TerrainData @0 of a fixed ground-humanoid prefab. Live NPCs are streamed out or
    // disabled away from players, and "the first NPC found" differs between calls (a bat and a
    // bandit do not share a walkability mask), which made results flaky.
    private static readonly string[] AgentPrefabs = { "CHAR_Bandit_Thug", "CHAR_Farmlands_Villager_Male", "CHAR_Farmlands_Sheep" };

    private static unsafe PathfindingTerrainData? BorrowTerrain(World world, string[] names)
    {
        var em = world.EntityManager;
        var prefabs = world.GetExistingSystemManaged<ProjectM.PrefabCollectionSystem>();
        if (prefabs == null) return null;
        var type = ComponentIndex.Of<Pathfinder>(ComponentType.AccessMode.ReadOnly);
        foreach (var name in names)
        {
            try
            {
                if (!prefabs._PrefabLookupMap.TryGetPrefabGuidWithName(name, out var guid, true)) continue;
                if (!prefabs._PrefabGuidToEntityMap.TryGetValue(guid, out var prefab) || prefab == Entity.Null) continue;
                if (!em.HasComponent(prefab, type)) continue;
                byte* raw = (byte*)em.GetComponentDataRawRO(prefab, type.TypeIndex);
                if (raw == null) continue;
                _terrainSource = name;
                return *(PathfindingTerrainData*)raw;
            }
            catch (Exception e) { Plugin.Logger?.LogWarning($"bot_path: terrain from {name} failed: {e.Message}"); }
        }
        return null;
    }

    private static string _terrainSource = "";

    private static unsafe string TerrainHex(PathfindingTerrainData t)
    {
        byte* p = (byte*)&t;
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < sizeof(PathfindingTerrainData); i++) sb.Append(p[i].ToString("X2"));
        return sb.ToString();
    }

    private static string Status(int s) => s switch { 0 => "Success", 1 => "Failed", 2 => "AwaitingPath", _ => $"Unknown({s})" };

    private static float3 Vec(JsonElement args, string key)
    {
        if (!args.TryGetProperty(key, out var v) || v.ValueKind != JsonValueKind.Object)
            throw new ArgumentException($"args.{key} {{x,y,z}} is required");
        float Get(string k, float d) => v.TryGetProperty(k, out var n) && n.ValueKind == JsonValueKind.Number ? n.GetSingle() : d;
        return new float3(Get("x", 0), Get("y", 5), Get("z", 0));
    }
}
