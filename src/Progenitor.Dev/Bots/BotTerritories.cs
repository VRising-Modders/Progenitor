using System;
using System.Collections.Generic;
using System.Text.Json;
using ProjectM.CastleBuilding;
using ProjectM.Terrain;
using Progenitor.Castles;
using Progenitor.Ecs;
using Unity.Entities;

namespace Progenitor.Dev.Bots;

/// <summary>
/// Server op <c>bot_territories</c>: lists castle territories nearest a world point, with whether
/// each is claimed and a suggested castle-heart position, so a headless bot can pick a plot
/// before sending <c>BuildTileModelEvent</c> for <c>TM_BloodFountain_CastleHeart</c>
/// (-485210554). Outside a territory (<c>BlockReason.RequiresTerritoryLocation</c>) or in a
/// claimed one (<c>RequiresUnclaimedTerritory</c>) that event is refused silently.
///
/// <para><b>Args:</b> <c>x</c>, <c>z</c> (world point, required); <c>limit</c> (default 5);
/// <c>onlyFree</c> (default true); <c>spot</c> = <c>"centre"</c> (default: heart nearest the
/// territory's buildable centroid) or <c>"near"</c> (heart nearest the point).</para>
///
/// <para><b>Reads (server metadata), all raw through non-generic
/// <c>GetComponentDataRawRO</c> / <c>GetBufferLength</c> / <c>GetBufferRawRO</c> with metadata
/// offsets; no new IL2CPP generic instantiation:</b></para>
/// <list type="bullet">
/// <item><description><c>CastleTerritory</c>: <c>[0x0] MapZoneId ZoneId;
/// [0x4] Entity CastleHeart; [0xC] BoundsMinMax WorldBounds</c> (int2 Min, int2 Max, tiles);
/// <c>[0x1C] bool IsGlobalDebugTerritory; [0x1D] byte MinHeightLevel; [0x1E] byte MaxHeightLevel;
/// [0x20] int CastleTerritoryIndex</c>. Bool-bearing, so read raw rather than as a C# struct.</description></item>
/// <item><description><c>CastleTerritoryTiles</c>: one <c>bool InsideBuildableTerritory</c> per tile,
/// stride 1, row-major over <c>WorldBounds</c> (native, see <see cref="CastleGeometry.LocalTileIndex"/>),
/// decoded by <see cref="CastleTerritoryBuffers.DecodeTileMask"/>.</description></item>
/// <item><description><c>CastleTerritoryBlocks</c>: <c>int2 BlockCoordinate</c>, stride 8; fallback
/// mask when the tile mask is refused. A block is taken as tiles <c>[b*10, b*10+10)</c>
/// (<b>inferred</b>: the native corner arithmetic (FCAST3) is <c>b*10+1 … b*10+10</c>,
/// one tile off from this, so a blocks-derived mask may be one tile wrong at the plot edge;
/// <c>maskSource</c> in the output says which mask was used).</description></item>
/// <item><description><c>TerritoryWorldRegion</c>: <c>[0x0] WorldRegionType Region</c> (int enum).</description></item>
/// </list>
///
/// <para><b>Claimed:</b> <c>CastleTerritory.CastleHeart</c> is a live entity, <i>or</i> a
/// <c>CastleHeart</c> found by <see cref="CastleReader.Plots"/> points at this territory
/// (<c>CastleHeart.CastleTerritoryEntity</c>). Either one marks it claimed; the two sources are
/// reported separately. This is the condition vanilla tests for
/// <c>RequiresUnclaimedTerritory</c>, not a native check.</para>
///
/// <para><b>Heart spot:</b> pivot tile <c>p ≡ 5 (mod 10)</c> on both axes (world X/Z ≡ 2.5 mod 5,
/// live for heart and floor), footprint <c>[p-15, p+15)</c> = 30 x 30 tiles = 3 x 3 build cells
/// (live <c>TileBounds</c> of the test-save heart). Every footprint tile must be inside
/// <c>WorldBounds</c> and buildable in the mask; among those the pivot closest to the chosen
/// target wins. World position = <see cref="CastleGeometry.TileToWorld(int)"/> of the pivot.
/// No <c>y</c>: there is no verified mapping from territory height levels or terrain to world Y
/// (<see cref="CastleGeometry"/> remarks); the bot should send its own ground Y and let the
/// server snap it. Height-band, roads, map-zone restrictions and existing objects are not checked.</para>
///
/// <para><b>Status:</b> source-checked and built; <b>not verified live</b>.</para>
/// </summary>
internal static class BotTerritories
{
    private const int TerritoryHeartOffset = 0x4;
    private const int TerritoryBoundsOffset = 0xC;
    private const int TerritoryDebugOffset = 0x1C;
    private const int TerritoryMinHeightOffset = 0x1D;
    private const int TerritoryMaxHeightOffset = 0x1E;
    private const int TerritoryIndexOffset = 0x20;

    /// <summary>Heart footprint half-extent in tiles (30 x 30 tiles, live).</summary>
    private const int HeartHalfTiles = 15;
    private const int CellTiles = 10;
    private const int PivotResidue = 5;

    private sealed class Row
    {
        public Entity Entity;
        public int Index;
        public bool Debug;
        public Entity HeartField;
        public bool HeartFieldLive;
        public CastlePlot? Plot;
        public int MinX, MinY, MaxX, MaxY;
        public byte MinH, MaxH;
        public string Region = "";
        public bool[]? Mask;
        public string MaskSource = "bounds";
        public int Buildable;
        public double CentroidX, CentroidY; // tiles
        public double CentreDist, EdgeDist; // world units
        public bool PointBuildable;
        public bool Claimed => HeartFieldLive || Plot != null;
    }

    public static object Run(World world, JsonElement args)
    {
        if (!Progenitor.Plugin.IsServerProcess)
            throw new InvalidOperationException("bot_territories runs on the server");

        float px = Num(args, "x") ?? throw new ArgumentException("args.x is required");
        float pz = Num(args, "z") ?? throw new ArgumentException("args.z is required");
        int limit = Math.Max(1, (int)(Num(args, "limit") ?? 5));
        bool onlyFree = !(args.TryGetProperty("onlyFree", out var of) && of.ValueKind == JsonValueKind.False);
        string spot = args.TryGetProperty("spot", out var sp) && sp.ValueKind == JsonValueKind.String ? sp.GetString()! : "centre";
        if (spot != "centre" && spot != "near")
            throw new ArgumentException("args.spot must be center or near");

        var em = world.EntityManager;
        // Keyed by (Index, Version): plain ints, so no reliance on the interop Entity's Equals/GetHashCode.
        var plotsByTerritory = new Dictionary<(int, int), CastlePlot>();
        foreach (var plot in CastleReader.Plots(world))
            if (plot.TerritoryEntity != Entity.Null)
                plotsByTerritory[(plot.TerritoryEntity.Index, plot.TerritoryEntity.Version)] = plot;

        var territories = Query.Entities(em, includeDisabled: true, includeSystems: false,
            ComponentIndex.Of<CastleTerritory>(ComponentType.AccessMode.ReadOnly));

        int pointTileX = CastleGeometry.WorldToTile(px);
        int pointTileY = CastleGeometry.WorldToTile(pz);
        var rows = new List<Row>(territories.Count);
        int total = 0, claimed = 0, skippedDebug = 0, unreadable = 0;
        foreach (var e in territories)
        {
            Row? row;
            try { row = ReadTerritory(em, e); }
            catch (Exception ex)
            {
                Plugin.Logger?.LogWarning($"bot_territories: {e.Index}:{e.Version} threw: {ex.Message}");
                row = null;
            }
            if (row == null) { unreadable++; continue; }
            if (row.Debug) { skippedDebug++; continue; }
            total++;
            plotsByTerritory.TryGetValue((e.Index, e.Version), out row.Plot);
            if (row.Claimed) claimed++;
            if (onlyFree && row.Claimed) continue;

            Measure(row, px, pz, pointTileX, pointTileY);
            rows.Add(row);
        }

        rows.Sort((a, b) =>
        {
            int c = a.EdgeDist.CompareTo(b.EdgeDist);
            return c != 0 ? c : a.CentreDist.CompareTo(b.CentreDist);
        });

        var results = new List<object>();
        for (int i = 0; i < rows.Count && results.Count < limit; i++)
            results.Add(Describe(rows[i], spot, pointTileX, pointTileY));

        return new
        {
            point = new { x = px, z = pz, tileX = pointTileX, tileY = pointTileY },
            territories = total,
            claimed,
            free = total - claimed,
            skippedDebug,
            unreadable,
            onlyFree,
            spot,
            results,
        };
    }

    // ---- stress_populate discovery: the same reads, one territory per call -------------------

    /// <summary>The territory's world region (<c>TerritoryWorldRegion</c>, the raw read <see cref="ReadTerritory"/> makes); empty when absent.</summary>
    internal static unsafe string RegionOf(EntityManager em, Entity territory)
    {
        if (territory == Entity.Null || !em.Exists(territory) || !territory.Has<TerritoryWorldRegion>(em)) return "";
        int* r = (int*)em.GetComponentDataRawRO(territory, ComponentIndex.IndexOf<TerritoryWorldRegion>());
        return r == null ? "" : ((WorldRegionType)(*r)).ToString();
    }

    /// <summary>Every castle territory entity (disabled included), for <see cref="ReadForStress"/>.</summary>
    internal static List<Entity> TerritoryEntities(EntityManager em) =>
        Query.Entities(em, includeDisabled: true, includeSystems: false,
            ComponentIndex.Of<CastleTerritory>(ComponentType.AccessMode.ReadOnly));

    /// <summary>The castle hearts by territory entity (index, version), as <see cref="Run"/> keys them.</summary>
    internal static Dictionary<(int, int), CastlePlot> PlotsByTerritory(World world)
    {
        var map = new Dictionary<(int, int), CastlePlot>();
        foreach (var plot in CastleReader.Plots(world))
            if (plot.TerritoryEntity != Entity.Null)
                map[(plot.TerritoryEntity.Index, plot.TerritoryEntity.Version)] = plot;
        return map;
    }

    /// <summary>
    /// One territory for the stress planner: bounds, buildable mask, claimed, region and the heart
    /// pivot nearest the buildable centroid (the <c>spot:"centre"</c> rule). Null when unreadable.
    /// A debug territory comes back with region <c>Debug</c> so the planner refuses it.
    /// </summary>
    internal static Ops.StressTerritory? ReadForStress(EntityManager em, Entity e, Dictionary<(int, int), CastlePlot> plots)
    {
        var row = ReadTerritory(em, e);
        if (row == null) return null;
        if (row.Debug) return new Ops.StressTerritory { Index = row.Index, Region = "Debug" };
        plots.TryGetValue((e.Index, e.Version), out row.Plot);
        Measure(row, 0, 0, 0, 0);
        var t = new Ops.StressTerritory
        {
            Index = row.Index,
            Claimed = row.Claimed,
            ClaimedBy = row.Plot?.OwnerName,
            Region = row.Region,
            MinX = row.MinX, MinY = row.MinY, MaxX = row.MaxX, MaxY = row.MaxY,
            Mask = row.Mask,
        };
        if (!row.Claimed && FindHeartPivot(row, row.CentroidX, row.CentroidY, out int hx, out int hy, out _))
        {
            t.HasPivot = true;
            t.PivotX = hx;
            t.PivotY = hy;
        }
        return t;
    }

    private static unsafe Row? ReadTerritory(EntityManager em, Entity e)
    {
        var typeIndex = ComponentIndex.IndexOf<CastleTerritory>();
        byte* p = (byte*)em.GetComponentDataRawRO(e, typeIndex);
        if (p == null) return null;

        var row = new Row { Entity = e };
        if (p[TerritoryDebugOffset] != 0) { row.Debug = true; return row; }

        row.HeartField = *(Entity*)(p + TerritoryHeartOffset);
        row.HeartFieldLive = row.HeartField != Entity.Null && row.HeartField.Index > 0
            && em.Exists(row.HeartField) && row.HeartField.Has<CastleHeart>(em);
        int* b = (int*)(p + TerritoryBoundsOffset);
        row.MinX = b[0]; row.MinY = b[1]; row.MaxX = b[2]; row.MaxY = b[3];
        row.MinH = p[TerritoryMinHeightOffset];
        row.MaxH = p[TerritoryMaxHeightOffset];
        row.Index = *(int*)(p + TerritoryIndexOffset);

        if (e.Has<TerritoryWorldRegion>(em))
        {
            int* r = (int*)em.GetComponentDataRawRO(e, ComponentIndex.IndexOf<TerritoryWorldRegion>());
            if (r != null) row.Region = ((WorldRegionType)(*r)).ToString();
        }

        int expected = CastleGeometry.BoundsTileCount(row.MinX, row.MinY, row.MaxX, row.MaxY);
        if (expected <= 0) return row;
        row.Mask = ReadTileMask(em, e, expected);
        if (row.Mask != null) row.MaskSource = "tiles";
        else
        {
            row.Mask = MaskFromBlocks(em, row, expected);
            if (row.Mask != null) row.MaskSource = "blocks";
        }
        return row;
    }

    private static unsafe bool[]? ReadTileMask(EntityManager em, Entity e, int expected)
    {
        if (!e.Has<CastleTerritoryTiles>(em)) return null;
        var typeIndex = ComponentIndex.IndexOf<CastleTerritoryTiles>();
        int length = em.GetBufferLength(e, typeIndex);
        if (!CastleTerritoryBuffers.TileMaskLengthOk(length, expected)) return null;
        void* raw = em.GetBufferRawRO(e, typeIndex);
        if (raw == null) return null;
        var span = new ReadOnlySpan<byte>(raw, length * CastleTerritoryBuffers.TileElementStride);
        return CastleTerritoryBuffers.DecodeTileMask(span, length, expected);
    }

    private static unsafe bool[]? MaskFromBlocks(EntityManager em, Row row, int expected)
    {
        if (!row.Entity.Has<CastleTerritoryBlocks>(em)) return null;
        int maxBlocks = CastleTerritoryBuffers.MaxBlocksForBounds(row.MinX, row.MinY, row.MaxX, row.MaxY);
        var typeIndex = ComponentIndex.IndexOf<CastleTerritoryBlocks>();
        int length = em.GetBufferLength(row.Entity, typeIndex);
        if (!CastleTerritoryBuffers.BlockLengthOk(length, maxBlocks) || length == 0) return null;
        void* raw = em.GetBufferRawRO(row.Entity, typeIndex);
        if (raw == null) return null;
        var span = new ReadOnlySpan<byte>(raw, length * CastleTerritoryBuffers.BlockElementStride);
        var coords = CastleTerritoryBuffers.DecodeBlockCoordinates(span, length, maxBlocks);
        if (coords == null) return null;

        var mask = new bool[expected];
        for (int i = 0; i < length; i++)
        {
            int bx = coords[i * 2] * CellTiles, by = coords[(i * 2) + 1] * CellTiles;
            for (int y = by; y < by + CellTiles; y++)
                for (int x = bx; x < bx + CellTiles; x++)
                {
                    int at = CastleGeometry.LocalTileIndex(x, y, row.MinX, row.MinY, row.MaxX, row.MaxY);
                    if (at >= 0) mask[at] = true;
                }
        }
        return mask;
    }

    private static void Measure(Row row, float px, float pz, int ptx, int pty)
    {
        int w = row.MaxX - row.MinX, h = row.MaxY - row.MinY;
        double sx = 0, sy = 0;
        int n = 0;
        if (row.Mask != null)
        {
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    if (row.Mask[(y * w) + x]) { sx += x; sy += y; n++; }
        }
        if (n == 0)
        {
            n = row.Mask == null ? Math.Max(0, w) * Math.Max(0, h) : 0;
            sx = n * (w - 1) / 2.0;
            sy = n * (h - 1) / 2.0;
        }
        row.Buildable = n;
        row.CentroidX = row.MinX + (n > 0 ? sx / n : (w - 1) / 2.0);
        row.CentroidY = row.MinY + (n > 0 ? sy / n : (h - 1) / 2.0);

        double cx = TileCentreWorld(row.CentroidX), cz = TileCentreWorld(row.CentroidY);
        row.CentreDist = Math.Sqrt(((cx - px) * (cx - px)) + ((cz - pz) * (cz - pz)));

        double minWx = CastleGeometry.TileToWorld(row.MinX), maxWx = CastleGeometry.TileToWorld(row.MaxX);
        double minWz = CastleGeometry.TileToWorld(row.MinY), maxWz = CastleGeometry.TileToWorld(row.MaxY);
        double dx = px < minWx ? minWx - px : px > maxWx ? px - maxWx : 0;
        double dz = pz < minWz ? minWz - pz : pz > maxWz ? pz - maxWz : 0;
        row.EdgeDist = Math.Sqrt((dx * dx) + (dz * dz));

        int local = CastleGeometry.LocalTileIndex(ptx, pty, row.MinX, row.MinY, row.MaxX, row.MaxY);
        row.PointBuildable = local >= 0 && (row.Mask == null || row.Mask[local]);
    }

    private static object Describe(Row row, string spot, int ptx, int pty)
    {
        object? heart = null;
        int heartSpots = 0;
        if (!row.Claimed)
        {
            double tx = spot == "near" ? ptx : row.CentroidX;
            double ty = spot == "near" ? pty : row.CentroidY;
            if (FindHeartPivot(row, tx, ty, out int hx, out int hy, out heartSpots))
            {
                heart = new
                {
                    x = CastleGeometry.TileToWorld(hx),
                    z = CastleGeometry.TileToWorld(hy),
                    tileX = hx,
                    tileY = hy,
                    footprint = new { minX = hx - HeartHalfTiles, minY = hy - HeartHalfTiles, maxX = hx + HeartHalfTiles, maxY = hy + HeartHalfTiles },
                };
            }
        }

        var plot = row.Plot;
        return new
        {
            index = row.Index,
            entity = $"{row.Entity.Index}:{row.Entity.Version}",
            claimed = row.Claimed,
            claim = row.Claimed
                ? new
                {
                    territoryHeart = row.HeartFieldLive ? $"{row.HeartField.Index}:{row.HeartField.Version}" : null,
                    plotHeart = plot != null ? $"{plot.HeartEntity.Index}:{plot.HeartEntity.Version}" : null,
                    owner = plot?.OwnerName,
                    ownerPlatformId = plot?.OwnerPlatformId.ToString(),
                }
                : null,
            region = row.Region,
            centre = new { x = Round(TileCentreWorld(row.CentroidX)), z = Round(TileCentreWorld(row.CentroidY)) },
            aabb = new
            {
                minX = CastleGeometry.TileToWorld(row.MinX),
                minZ = CastleGeometry.TileToWorld(row.MinY),
                maxX = CastleGeometry.TileToWorld(row.MaxX),
                maxZ = CastleGeometry.TileToWorld(row.MaxY),
                tiles = new { minX = row.MinX, minY = row.MinY, maxX = row.MaxX, maxY = row.MaxY },
            },
            heightLevels = new { min = row.MinH, max = row.MaxH },
            maskSource = row.MaskSource,
            buildableTiles = row.Buildable,
            areaWorld = row.Buildable * CastleGeometry.WorldUnitsPerTile * CastleGeometry.WorldUnitsPerTile,
            distance = Round(row.CentreDist),
            edgeDistance = Round(row.EdgeDist),
            pointInside = row.PointBuildable,
            heartSpots,
            heart,
        };
    }

    /// <summary>
    /// Pivot tile (≡ 5 mod 10 on both axes) whose 30 x 30 footprint is entirely inside the bounds
    /// and buildable, closest to (<paramref name="tx"/>, <paramref name="ty"/>). Uses a 2D prefix
    /// sum over the mask so each candidate is O(1).
    /// </summary>
    private static bool FindHeartPivot(Row row, double tx, double ty, out int bestX, out int bestY, out int count)
    {
        bestX = bestY = 0;
        count = 0;
        int w = row.MaxX - row.MinX, h = row.MaxY - row.MinY;
        if (w < HeartHalfTiles * 2 || h < HeartHalfTiles * 2) return false;

        int[,]? sum = null;
        if (row.Mask != null)
        {
            sum = new int[h + 1, w + 1];
            for (int y = 0; y < h; y++)
            {
                int line = 0;
                for (int x = 0; x < w; x++)
                {
                    if (row.Mask[(y * w) + x]) line++;
                    sum[y + 1, x + 1] = sum[y, x + 1] + line;
                }
            }
        }

        int full = HeartHalfTiles * 2 * HeartHalfTiles * 2;
        double best = double.MaxValue;
        int startX = FirstPivot(row.MinX + HeartHalfTiles), startY = FirstPivot(row.MinY + HeartHalfTiles);
        for (int py = startY; py + HeartHalfTiles <= row.MaxY; py += CellTiles)
        {
            for (int px = startX; px + HeartHalfTiles <= row.MaxX; px += CellTiles)
            {
                if (sum != null)
                {
                    int x0 = px - HeartHalfTiles - row.MinX, y0 = py - HeartHalfTiles - row.MinY;
                    int x1 = x0 + (HeartHalfTiles * 2), y1 = y0 + (HeartHalfTiles * 2);
                    int filled = sum[y1, x1] - sum[y0, x1] - sum[y1, x0] + sum[y0, x0];
                    if (filled != full) continue;
                }

                count++;
                double d = ((px - tx) * (px - tx)) + ((py - ty) * (py - ty));
                if (d < best) { best = d; bestX = px; bestY = py; }
            }
        }
        return count > 0;
    }

    /// <summary>Smallest tile ≥ <paramref name="t"/> with <c>t ≡ 5 (mod 10)</c>.</summary>
    private static int FirstPivot(int t)
    {
        int r = ((t % CellTiles) + CellTiles) % CellTiles;
        return t + ((PivotResidue - r + CellTiles) % CellTiles);
    }

    private static double TileCentreWorld(double tile) =>
        ((tile - CastleGeometry.TileOrigin) * CastleGeometry.WorldUnitsPerTile) + (CastleGeometry.WorldUnitsPerTile / 2);

    private static double Round(double v) => Math.Round(v, 2);

    private static float? Num(JsonElement args, string key) =>
        args.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetSingle() : null;
}
