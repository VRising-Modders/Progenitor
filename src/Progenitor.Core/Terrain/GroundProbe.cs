using System;
using ProjectM;
using ProjectM.Pathfinding;
using ProjectM.Tiles;
using Progenitor.Ecs;
using Unity.Entities;
using Unity.Mathematics;

namespace Progenitor.Terrain;

/// <summary>
/// MAPTP1: ground queries against the game's tile world, opened once for a batch of probes (a nearest-
/// valid-spot search makes thousands). Main thread, server world (the client has a tile world too, but
/// only for the streamed-in chunks).
///
/// <para><b>Height</b> (<see cref="TryGetHeight"/>) is
/// <c>TileWorldSingleton.GetTileWorld().TryGetGameplayHeight(in float3, out float, bilinear: true)</c>
/// (<c>TileWorld</c>) — the call the Dev bridge's <c>teleport</c> op has snapped its
/// destinations to the floor with since it shipped. The probe height selects a five-meter height band (with the game
/// offset), clamped to the cell's available layers; it is not a downward raycast. A miss (no tile data at that X/Z: off the world, an unloaded chunk) is
/// <c>false</c>; so is a NaN or a height below <see cref="MinHeight"/>, the same floor Dev applies.</para>
///
/// <para><b>Walkable tile</b> (<see cref="TryIsWalkable"/>) is the game's own
/// <c>ProjectM.Pathfinding.PathfindingUtility.TryFindValidTile(float3, float, in PathfindingTerrainData,
/// ref TileWorld, out TileCoordinate)</c> with a ground humanoid's terrain preferences:
/// valid when it answers with the probed tile itself. That catches walls, rocks and deep water, which a
/// height alone does not. <b>It is not live-verified</b> — a by-ref struct call through interop that no
/// Progenitor feature has made — so callers keep it behind a setting that defaults off.</para>
/// </summary>
public sealed class GroundProbe : IDisposable
{
    /// <summary>Lowest accepted gameplay height. Below it is the void under the map (Dev's teleport uses -40 too).</summary>
    public const float MinHeight = -40f;

    /// <summary>Agent radius the walkable check asks for, meters (a player's map collision is about this).</summary>
    public const float AgentRadius = 0.5f;

    /// <summary>Ground humanoids whose <c>Pathfinder.TerrainData</c> stands in for a player's (players have none).</summary>
    private static readonly string[] AgentPrefabs = { "CHAR_Bandit_Thug", "CHAR_Farmlands_Villager_Male" };

    private TileWorld _tiles;
    private PathfindingTerrainData _terrain;
    private bool _hasTerrain;
    private bool _open;

    private GroundProbe(TileWorld tiles)
    {
        _tiles = tiles;
        _open = true;
    }

    /// <summary>Name of the prefab the walkable check borrowed terrain preferences from, or empty.</summary>
    public string TerrainSource { get; private set; } = "";

    /// <summary>
    /// Open the tile world of <paramref name="world"/>. Null with <paramref name="why"/> set when there is
    /// none (no world, no <c>TileWorldSingleton</c> yet).
    /// </summary>
    public static GroundProbe? TryOpen(World? world, out string why)
    {
        why = "";
        try
        {
            if (world == null || !world.IsCreated)
            {
                why = "no game world";
                return null;
            }

            var em = world.EntityManager;
            var query = em.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<TileWorldSingleton>() },
                Options = EntityQueryOptions.IncludeSystems | EntityQueryOptions.IncludeDisabledEntities,
            });
            try
            {
                if (query.CalculateEntityCount() != 1)
                {
                    why = "the tile world is not loaded";
                    return null;
                }

                var tiles = em.GetComponentData<TileWorldSingleton>(query.GetSingletonEntity()).GetTileWorld();
                return new GroundProbe(tiles);
            }
            finally
            {
                query.Dispose();
            }
        }
        catch (Exception e)
        {
            why = "tile world read threw: " + e.Message;
            return null;
        }
    }

    /// <summary>
    /// The gameplay floor at (<paramref name="x"/>, <paramref name="z"/>) in the height band selected by
    /// <paramref name="probeY"/>, clamped to the available cell layers. False for no tile data, NaN, or below <see cref="MinHeight"/>.
    /// </summary>
    public bool TryGetHeight(float x, float probeY, float z, out float height)
    {
        height = float.NaN;
        if (!_open) return false;
        try
        {
            var sample = new float3(x, probeY, z);
            if (!_tiles.TryGetGameplayHeight(ref sample, out float floor, true)) return false;
            if (float.IsNaN(floor) || float.IsInfinity(floor) || floor < MinHeight) return false;
            height = floor;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// <b>Not live-verified; keep it behind a default-off setting.</b> Whether the game's pathfinder
    /// counts the tile at (<paramref name="x"/>, <paramref name="y"/>, <paramref name="z"/>) as standable
    /// for a ground humanoid: <c>PathfindingUtility.TryFindValidTile</c> answers with that same tile.
    /// False with <paramref name="walkable"/> unset when the check itself cannot run (no agent prefab, the
    /// call threw).
    /// </summary>
    public bool TryIsWalkable(World world, float x, float y, float z, out bool walkable)
    {
        walkable = false;
        if (!_open) return false;
        if (!_hasTerrain && !TryBorrowTerrain(world)) return false;
        try
        {
            var pos = new float3(x, y, z);
            var own = TileCoordinate.FromWorldPos(pos);
            if (!PathfindingUtility.TryFindValidTile(pos, AgentRadius, ref _terrain, ref _tiles, out var found))
            {
                walkable = false;
                return true;
            }

            walkable = found.X == own.X && found.Z == own.Z;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The probe holds no native memory of its own; this only stops further use.</summary>
    public void Dispose() => _open = false;

    private unsafe bool TryBorrowTerrain(World world)
    {
        try
        {
            var em = world.EntityManager;
            var prefabs = world.GetExistingSystemManaged<PrefabCollectionSystem>();
            if (prefabs == null) return false;
            var type = ComponentIndex.Of<Pathfinder>(ComponentType.AccessMode.ReadOnly);
            foreach (var name in AgentPrefabs)
            {
                if (!prefabs._PrefabLookupMap.TryGetPrefabGuidWithName(name, out var guid, true)) continue;
                if (!prefabs._PrefabGuidToEntityMap.TryGetValue(guid, out var prefab) || prefab == Entity.Null) continue;
                if (!em.HasComponent(prefab, type)) continue;
                // Pathfinder.TerrainData is at offset 0 (client metadata); the Dev bridge's bot_path reads it
                // the same way.
                byte* raw = (byte*)em.GetComponentDataRawRO(prefab, type.TypeIndex);
                if (raw == null) continue;
                _terrain = *(PathfindingTerrainData*)raw;
                _hasTerrain = true;
                TerrainSource = name;
                return true;
            }
        }
        catch
        {
            // falls through: no terrain preferences, the walkable check cannot run
        }

        return false;
    }
}
