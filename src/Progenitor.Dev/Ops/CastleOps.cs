using System;
using System.Collections.Generic;
using System.Text.Json;
using Progenitor.Castles;
using Unity.Entities;

namespace Progenitor.Dev.Ops;

/// <summary>
/// Debug-bridge <c>castles</c> op: JSON snapshots of <see cref="CastleReader"/> plots, tiles,
/// or buildings. The bridge already marshals this onto the main thread. Entity ids are
/// <c>index:version</c> strings because Unity <c>Entity</c> does not serialize.
/// </summary>
public static class CastleOps
{
    /// <summary>
    /// <paramref name="args"/>: <c>what</c> = <c>plots</c> | <c>tiles</c> | <c>buildings</c>
    /// (default plots); <c>plot</c> optional territory index (required in practice for tiles
    /// and buildings — omitted walks every plot up to <c>limit</c>); <c>limit</c> row cap
    /// (default 200, hard 2000).
    /// </summary>
    public static object Run(World world, JsonElement args)
    {
        try
        {
            if (world == null || !world.IsCreated)
                throw new InvalidOperationException("the game world does not exist yet (not in a game)");

            string what = (GetString(args, "what") ?? "plots").Trim().ToLowerInvariant();
            int? plotIndex = GetOptionalInt(args, "plot");
            int limit = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("limit", out _)
                ? CastleLogic.ClampLimit(GetInt(args, "limit", CastleLogic.DefaultLimit))
                : CastleLogic.DefaultLimit;

            var plots = CastleReader.Plots(world);
            IReadOnlyList<CastlePlot> selected = SelectPlots(plots, plotIndex);

            return what switch
            {
                "plots" => PlotsJson(selected, plots.Count, limit, plotIndex),
                "tiles" => TilesJson(world, selected, plots.Count, limit, plotIndex),
                "buildings" => BuildingsJson(world, selected, plots.Count, limit, plotIndex),
                "index" => new { what = "index", index = CastleInventoryIndexStatus.Describe() },
                _ => throw new ArgumentException("args.what must be 'plots', 'tiles', 'buildings' or 'index'"),
            };
        }
        catch (Exception e) when (e is not ArgumentException and not InvalidOperationException)
        {
            Plugin.Logger?.LogError($"castles op threw: {e}");
            throw;
        }
    }

    private static IReadOnlyList<CastlePlot> SelectPlots(IReadOnlyList<CastlePlot> plots, int? plotIndex)
    {
        if (plotIndex == null)
            return plots;
        var match = new List<CastlePlot>(1);
        for (int i = 0; i < plots.Count; i++)
        {
            if (plots[i].TerritoryIndex == plotIndex.Value)
                match.Add(plots[i]);
        }
        return match;
    }

    private static object PlotsJson(IReadOnlyList<CastlePlot> selected, int total, int limit, int? plotIndex)
    {
        int n = Math.Min(selected.Count, limit);
        var rows = new List<object>(n);
        for (int i = 0; i < n; i++)
            rows.Add(PlotObject(selected[i]));
        return new
        {
            what = "plots",
            plot = plotIndex,
            total,
            count = rows.Count,
            truncated = selected.Count > limit,
            blocksSkipped = true,
            plots = rows,
        };
    }

    private static object TilesJson(World world, IReadOnlyList<CastlePlot> selected, int plotTotal, int limit, int? plotIndex)
    {
        var rows = new List<object>(Math.Min(limit, 256));
        bool truncated = false;
        for (int p = 0; p < selected.Count; p++)
        {
            var tiles = CastleReader.Tiles(world, selected[p]);
            for (int i = 0; i < tiles.Count; i++)
            {
                if (rows.Count >= limit)
                {
                    truncated = true;
                    break;
                }
                rows.Add(TileObject(selected[p].TerritoryIndex, tiles[i]));
            }
            if (truncated) break;
        }

        return new
        {
            what = "tiles",
            plot = plotIndex,
            plots = plotTotal,
            count = rows.Count,
            truncated,
            tiles = rows,
        };
    }

    private static object BuildingsJson(World world, IReadOnlyList<CastlePlot> selected, int plotTotal, int limit, int? plotIndex)
    {
        var rows = new List<object>(Math.Min(limit, 256));
        bool truncated = false;
        for (int p = 0; p < selected.Count; p++)
        {
            var buildings = CastleReader.Buildings(world, selected[p]);
            for (int i = 0; i < buildings.Count; i++)
            {
                if (rows.Count >= limit)
                {
                    truncated = true;
                    break;
                }
                rows.Add(BuildingObject(selected[p].TerritoryIndex, buildings[i]));
            }
            if (truncated) break;
        }

        return new
        {
            what = "buildings",
            plot = plotIndex,
            plots = plotTotal,
            count = rows.Count,
            truncated,
            buildings = rows,
        };
    }

    private static object PlotObject(CastlePlot plot) => new
    {
        territoryIndex = plot.TerritoryIndex,
        territory = Fmt(plot.TerritoryEntity),
        heart = Fmt(plot.HeartEntity),
        bounds = new { minX = plot.BoundsMinX, minY = plot.BoundsMinY, maxX = plot.BoundsMaxX, maxY = plot.BoundsMaxY },
        minHeight = plot.MinHeightLevel,
        maxHeight = plot.MaxHeightLevel,
        blocksSkipped = plot.BlocksSkipped,
        blockCount = plot.BlockCoordinates.Count,
        owner = new
        {
            entity = Fmt(plot.OwnerUserEntity),
            platformId = plot.OwnerPlatformId.ToString(),
            name = plot.OwnerName,
        },
        clan = new { entity = Fmt(plot.ClanEntity), name = plot.ClanName },
        level = plot.Level,
        raided = plot.Raided,
        decaying = plot.Decaying,
    };

    private static object TileObject(int territoryIndex, CastleTile tile) => new
    {
        plot = territoryIndex,
        entity = Fmt(tile.FloorEntity),
        x = tile.X,
        y = tile.Y,
        height = tile.Height,
        floorPresent = tile.FloorPresent,
        floorType = tile.FloorType,
        floorTypeName = tile.FloorTypeName,
        treasury = CastleLogic.IsTreasury(tile.FloorType),
        impassable = tile.Impassable,
        room = Fmt(tile.RoomEntity),
        roomId = tile.RoomId,
        walls = new
        {
            flags = tile.WallFlags,
            north = CastleLogic.HasNorth(tile.WallFlags),
            east = CastleLogic.HasEast(tile.WallFlags),
            south = CastleLogic.HasSouth(tile.WallFlags),
            west = CastleLogic.HasWest(tile.WallFlags),
        },
        doors = new
        {
            flags = tile.DoorFlags,
            north = CastleLogic.HasNorth(tile.DoorFlags),
            east = CastleLogic.HasEast(tile.DoorFlags),
            south = CastleLogic.HasSouth(tile.DoorFlags),
            west = CastleLogic.HasWest(tile.DoorFlags),
        },
    };

    private static object BuildingObject(int territoryIndex, CastleBuilding b) => new
    {
        plot = territoryIndex,
        entity = Fmt(b.Entity),
        prefab = b.PrefabGuid._Value,
        prefabName = b.PrefabName,
        origin = new { x = b.OriginX, y = b.OriginY, height = b.OriginHeight },
        rotation = b.Rotation,
        rotationName = b.RotationName,
        footprint = new { minX = b.FootprintMinX, minY = b.FootprintMinY, maxX = b.FootprintMaxX, maxY = b.FootprintMaxY },
        interactable = b.Interactable,
        kind = CastleLogic.KindName(b.Kind),
        networkId = new { index = b.NetworkIndex, generation = b.NetworkGeneration },
    };

    private static string Fmt(Entity entity) =>
        entity == Entity.Null || entity.Index == 0 ? "" : $"{entity.Index}:{entity.Version}";

    private static string? GetString(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var el))
            return null;
        if (el.ValueKind == JsonValueKind.Null) return null;
        try { return el.GetString(); }
        catch { return el.ValueKind == JsonValueKind.Number ? el.GetRawText() : null; }
    }

    private static int GetInt(JsonElement args, string name, int fallback)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var el))
            return fallback;
        try
        {
            if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out int n))
                return n;
            if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out n))
                return n;
        }
        catch { }
        return fallback;
    }

    private static int? GetOptionalInt(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var el))
            return null;
        if (el.ValueKind == JsonValueKind.Null) return null;
        try
        {
            if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out int n))
                return n;
            if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out n))
                return n;
        }
        catch { }
        return null;
    }
}
