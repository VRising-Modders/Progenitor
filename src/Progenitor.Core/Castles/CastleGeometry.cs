using System;

namespace Progenitor.Castles;

/// <summary>
/// World-space to castle-tile math, game-free so the live reader and the tests cannot drift.
///
/// <para><b>Where the constants come from (live, test server 2026-09-04, save <c>test1</c>).</b>
/// Two placed tile-models on TestPlayer's plot were read through the debug bridge and give the
/// scale and the origin exactly, with no fitting:</para>
/// <list type="bullet">
/// <item><description>heart <c>327390:1</c> <c>TM_BloodFountain_CastleHeart</c>:
/// <c>TilePosition.Tile</c> = (2785, 2985), <c>Translation.Value</c> = (-1807.5, 5, -1707.5)</description></item>
/// <item><description>floor <c>327377:1</c> <c>TM_Castle_Floor_Outdoor_Plain01</c>:
/// <c>TilePosition.Tile</c> = (2775, 2965), <c>Translation.Value</c> = (-1812.5, 5, -1717.5)</description></item>
/// </list>
/// <para>Δtile (10, 20) over Δworld (5, 10) gives <see cref="WorldUnitsPerTile"/> = 0.5, and
/// <c>tile − world / 0.5</c> is 6400 on all four coordinates, giving <see cref="TileOrigin"/>.
/// So <c>world = (tile − 6400) × 0.5</c> exactly: a tile's world anchor is the value
/// <c>Translation</c> carries, and <c>TilePosition.Tile</c> is the model's pivot, while
/// <c>TileBounds.Value</c> is its footprint AABB around that pivot.</para>
///
/// <para><b>Inferred, not verified:</b> the rounding of <see cref="WorldToTile(float)"/> is
/// <c>floor</c>. Both live anchors land on exact integers, so they cannot separate floor from
/// round; floor matches how <c>ProjectM.Tiles.SpaceConversion.WorldToTile</c> is shaped
/// (both overloads in the server metadata) and is the
/// convention every grid in that class uses. A half-tile error only shows up for a position
/// exactly on a tile boundary.</para>
///
/// <para><b>No world-Y to height-level mapping.</b> <c>TilePosition.HeightLevel</c> was 11 on
/// both live models while <c>Translation.Value.y</c> was 5 and <c>CompressedHeight</c> 27500.
/// One sample cannot fix that relation, so nothing here converts world Y to a height level:
/// position tests are X/Z only, and height comes from an entity's own
/// <c>TilePosition</c> when there is one.</para>
/// </summary>
public static class CastleGeometry
{
    /// <summary>World units covered by one castle tile. Live-derived (see the type remarks).</summary>
    public const float WorldUnitsPerTile = 0.5f;

    /// <summary>Tile index of world coordinate 0. Live-derived (see the type remarks).</summary>
    public const int TileOrigin = 6400;

    /// <summary>
    /// World coordinate to tile index on one axis: <c>floor(world / 0.5) + 6400</c>.
    /// NaN and infinities return <see cref="int.MinValue"/> so a garbage position can never
    /// look like a tile inside somebody's territory.
    /// </summary>
    public static int WorldToTile(float world)
    {
        if (float.IsNaN(world) || float.IsInfinity(world))
            return int.MinValue;

        double tile = Math.Floor(world / WorldUnitsPerTile) + TileOrigin;
        if (tile < int.MinValue) return int.MinValue;
        if (tile > int.MaxValue) return int.MaxValue;
        return (int)tile;
    }

    /// <summary>
    /// World X/Z to a tile pair. Returns false when either axis was not representable
    /// (NaN, infinity, or off the int grid).
    /// </summary>
    public static bool TryWorldToTile(CastleVec3 position, out int tileX, out int tileY)
    {
        tileX = WorldToTile(position.X);
        tileY = WorldToTile(position.Z);
        return tileX != int.MinValue && tileX != int.MaxValue
            && tileY != int.MinValue && tileY != int.MaxValue;
    }

    /// <summary>Tile index back to its world anchor on one axis: <c>(tile − 6400) × 0.5</c>.</summary>
    public static float TileToWorld(int tile) => (tile - TileOrigin) * WorldUnitsPerTile;

    /// <summary>
    /// Tile pair back to a world position. <paramref name="worldY"/> is passed through
    /// untouched — there is no verified height-level to world-Y conversion.
    /// </summary>
    public static CastleVec3 TileToWorld(int tileX, int tileY, float worldY = 0f) =>
        new(TileToWorld(tileX), worldY, TileToWorld(tileY));

    /// <summary>
    /// Inclusive-min, exclusive-max AABB test, matching
    /// <c>BoundsMinMax.GetTiles(includeMaxBoundary: false)</c>. Delegates to
    /// <see cref="CastleLogic.TileInBounds"/> so the reader and this share one rule.
    /// </summary>
    public static bool BoundsContains(int x, int y, int minX, int minY, int maxX, int maxY) =>
        CastleLogic.TileInBounds(x, y, minX, minY, maxX, maxY);

    /// <summary>Tile count of an AABB, the length a <c>CastleTerritoryTiles</c> buffer must have.</summary>
    public static int BoundsTileCount(int minX, int minY, int maxX, int maxY)
    {
        long w = (long)maxX - minX;
        long h = (long)maxY - minY;
        if (w <= 0 || h <= 0) return 0;
        long total = w * h;
        return total > int.MaxValue ? 0 : (int)total;
    }

    /// <summary>
    /// Row-major index of a tile inside an AABB, or -1 when the tile is outside:
    /// <c>(y - minY) * (maxX - minX) + (x - minX)</c>.
    ///
    /// <para><b>Native, not inferred (R30 finding 17, settled).</b> The game's metadata proves only that
    /// <c>BoundsMinMax.GetLocalIndexFromBoundsTile</c> exists; the body was read from the server
    /// <c>GameAssembly.dll</c> native code. The native method:</para>
    /// <list type="bullet">
    /// <item><description>tests <c>Min.x &lt;= x</c>, <c>y &gt;= Min.y</c>,
    /// <c>x &lt; Max.x</c>, <c>y &lt; Max.y</c> — inclusive min, exclusive max, which is what
    /// <see cref="BoundsContains"/> does;</description></item>
    /// <item><description>computes <c>x - Min.x</c>, <c>y - Min.y</c>
    /// and <c>Max.x - Min.x</c> (the width);</description></item>
    /// <item><description>returns <c>(y - Min.y) * width + (x - Min.x)</c> — row-major, stride =
    /// width, exactly the expression below. Column-major and a <c>width + 1</c> variant are
    /// both ruled out.</description></item>
    /// </list>
    /// <para>And that same expression is what indexes the <c>CastleTerritoryTiles</c> buffer.
    /// <c>GetPlacementResult.IsArenaStationOverlappingOutsideBlock</c> hands each candidate tile,
    /// <c>territoryBounds</c> and the buffer to
    /// <c>CastleTerritoryExtensions.IsTileInTerritory</c>, whose
    /// three-argument overload (the game's native
    /// <c>ProjectM.CastleBuilding.CastleTerritoryExtensions.IsTileInTerritory</c>)
    /// computes <c>(x - Min.x) + (y - Min.y) * (Max.x - Min.x)</c>, bounds-checks
    /// it against the buffer's own <c>Length</c>, and returns the flag at that element.
    /// So the buffer order and this index are the same fact, not two matched
    /// guesses; column-major is ruled out.</para>
    /// </summary>
    public static int LocalTileIndex(int x, int y, int minX, int minY, int maxX, int maxY)
    {
        if (!BoundsContains(x, y, minX, minY, maxX, maxY)) return -1;
        long width = (long)maxX - minX;
        long index = ((long)(y - minY) * width) + (x - minX);
        return index < 0 || index > int.MaxValue ? -1 : (int)index;
    }

    /// <summary>
    /// True when <paramref name="heightLevel"/> is within a territory's inclusive
    /// <c>MinHeightLevel</c> / <c>MaxHeightLevel</c> band. A negative level (no
    /// <c>TilePosition</c> on the caller's entity) is treated as unknown and accepted.
    /// </summary>
    public static bool HeightInRange(int heightLevel, byte minHeightLevel, byte maxHeightLevel)
    {
        if (heightLevel < 0) return true;
        return heightLevel >= minHeightLevel && heightLevel <= maxHeightLevel;
    }

    /// <summary>
    /// Local footprint bounds rotated by a <c>TileRotation</c> step and offset to the pivot
    /// tile, the shape of <c>TileBounds.LocalToWorldBounds(int2 tile, TileRotation, BoundsMinMax)</c>
    /// (server metadata).
    ///
    /// <para><b>Inferred:</b> the rotation itself (90° steps of the local box about the pivot)
    /// is not live-verified — every model on the test save is <c>TileRotation.None</c>. With
    /// <paramref name="rotation"/> 0 this is a plain translate and is exactly what the live
    /// footprints showed.</para>
    ///
    /// <para><b>Half-open on every axis, including the negated ones (R30 finding 8).</b> Boxes
    /// here are inclusive-min / exclusive-max, the convention <see cref="BoundsContains"/>,
    /// <see cref="BoundsOverlap"/> and the native <see cref="LocalTileIndex"/> all use. The
    /// tiles of <c>[min, max)</c> on one axis are <c>min … max-1</c>; negating them gives
    /// <c>-(max-1) … -min</c>, whose half-open form is <c>[1 - max, 1 - min)</c> — not
    /// <c>[-max, -min)</c>, which is the same box shifted one tile the wrong way. Worked
    /// through on the local box <c>[0,4)</c>: tiles <c>{0,1,2,3}</c>, images
    /// <c>{0,-1,-2,-3}</c>, box <c>[-3, 1)</c> = <c>[1-4, 1-0)</c>.</para>
    ///
    /// <para><b>An empty local box stays empty (R31 finding 30).</b> A box with
    /// <c>max &lt;= min</c> on either axis holds no tiles, so its rotation holds no tiles
    /// either. The rotation cases below negate one or both axes and then re-normalize with
    /// <c>Math.Min</c> / <c>Math.Max</c>, which would turn an inverted box into a real one of
    /// positive width (local X <c>[5,2)</c> under rotation 2 becomes <c>[-4,-1)</c>, width 3).
    /// An empty box is therefore translated to the pivot and returned unrotated: the caller
    /// still sees <c>max &lt;= min</c> and <see cref="BoundsOverlap"/> still refuses it. The
    /// live path never reaches this (<c>CastleHandle.CanPlace</c> clamps a degenerate footprint
    /// to 1x1 first), but this method is public and other callers are not clamped.</para>
    /// </summary>
    public static void LocalToWorldBounds(
        int tileX, int tileY, byte rotation,
        int localMinX, int localMinY, int localMaxX, int localMaxY,
        out int minX, out int minY, out int maxX, out int maxY)
    {
        int aMinX = localMinX, aMinY = localMinY, aMaxX = localMaxX, aMaxY = localMaxY;
        bool empty = localMaxX <= localMinX || localMaxY <= localMinY;
        switch (empty ? 0 : rotation & 3)
        {
            case 1: // Clockwise_90: (x, y) -> (y, -x). X takes y's box unchanged; Y is negated.
                Set(aMinY, 1 - aMaxX, aMaxY, 1 - aMinX, out aMinX, out aMinY, out aMaxX, out aMaxY);
                break;
            case 2: // Clockwise_180: (x, y) -> (-x, -y). Both axes negated.
                Set(1 - aMaxX, 1 - aMaxY, 1 - aMinX, 1 - aMinY, out aMinX, out aMinY, out aMaxX, out aMaxY);
                break;
            case 3: // Clockwise_270: (x, y) -> (-y, x). X is negated y; Y takes x's box unchanged.
                Set(1 - aMaxY, aMinX, 1 - aMinY, aMaxX, out aMinX, out aMinY, out aMaxX, out aMaxY);
                break;
        }

        minX = aMinX + tileX;
        minY = aMinY + tileY;
        maxX = aMaxX + tileX;
        maxY = aMaxY + tileY;
    }

    /// <summary>
    /// True when two inclusive-min / exclusive-max tile AABBs share at least one tile.
    /// Empty boxes (max &lt;= min on either axis) never overlap.
    /// </summary>
    public static bool BoundsOverlap(
        int aMinX, int aMinY, int aMaxX, int aMaxY,
        int bMinX, int bMinY, int bMaxX, int bMaxY)
    {
        if (aMaxX <= aMinX || aMaxY <= aMinY) return false;
        if (bMaxX <= bMinX || bMaxY <= bMinY) return false;
        return aMinX < bMaxX && bMinX < aMaxX && aMinY < bMaxY && bMinY < aMaxY;
    }

    private static void Set(int a, int b, int c, int d, out int minX, out int minY, out int maxX, out int maxY)
    {
        minX = Math.Min(a, c);
        minY = Math.Min(b, d);
        maxX = Math.Max(a, c);
        maxY = Math.Max(b, d);
    }
}
