using System;

namespace Progenitor.Castles;

/// <summary>
/// Element layout of the two territory <c>IBufferElementData</c> buffers, and the decoding of a
/// raw buffer's bytes. Game-free on purpose: <see cref="UnityCastleRuntime"/> and
/// <c>CastleReader</c> obtain the pointer and the length through the non-generic
/// <c>EntityManager.GetBufferRawRO(entity, typeIndex)</c> / <c>GetBufferLength(entity, typeIndex)</c>
/// pair that <c>Effects</c> already uses live (<c>BuffSequence</c>, <c>BlackboardElement</c>);
/// everything that interprets the bytes lives here, so it is covered by the game-free tests.
///
/// <para><b>EV17 rule.</b> The stride and the field offsets below are the <b>game metadata's</b>, never
/// <c>sizeof</c> and never <c>TypeManager.GetTypeInfo(...).ElementSize</c>: the Il2CppInterop
/// wrapper structs do not carry the native layout, and reading the size back from the interop is
/// what made the first EV17 pass regress. Server metadata:</para>
/// <code>
/// [ComponentInWorld(new[] { 4, 2 })]
/// public struct CastleTerritoryBlocks : IBufferElementData   // TypeDefIndex: 19465
/// {
///     public int2 BlockCoordinate;          // 0x0   -> x at 0x0, y at 0x4, 8 bytes
/// }
///
/// [ComponentInWorld(new[] { 4, 2 })]
/// public struct CastleTerritoryTiles : IBufferElementData    // TypeDefIndex: 19466
/// {
///     public bool InsideBuildableTerritory;  // 0x0  -> 1 byte, no padding (single-field struct)
/// }
/// </code>
///
/// <para><c>CastleTerritoryTiles</c> is one flag per tile over the territory's
/// <c>CastleTerritory.WorldBounds</c>, row-major. That ordering is <b>native, not
/// inferred</b>: see <see cref="CastleGeometry.LocalTileIndex"/> for the recovered body of
/// <c>BoundsMinMax.GetLocalIndexFromBoundsTile</c>.</para>
/// </summary>
public static class CastleTerritoryBuffers
{
    /// <summary><c>CastleTerritoryBlocks</c> element size: one <c>int2</c> (server metadata).</summary>
    public const int BlockElementStride = Layout.CastleTerritoryBlocksStride;

    /// <summary><c>BlockCoordinate.x</c> byte offset inside a blocks element (server metadata).</summary>
    public const int BlockCoordinateXOffset = Layout.CastleTerritoryBlocksBlockCoordinateOffset;

    /// <summary><c>BlockCoordinate.y</c> byte offset: the second int of the <c>int2</c>.</summary>
    public const int BlockCoordinateYOffset = 4;

    /// <summary><c>CastleTerritoryTiles</c> element size: one <c>bool</c> (server metadata).</summary>
    public const int TileElementStride = Layout.CastleTerritoryTilesStride;

    /// <summary><c>InsideBuildableTerritory</c> byte offset inside a tiles element (server metadata).</summary>
    public const int TileFlagOffset = Layout.CastleTerritoryTilesInsideBuildableOffset;

    /// <summary>
    /// Absolute ceiling on a <c>CastleTerritoryBlocks</c> length, used only when a territory's
    /// own bounds give a larger number. A territory is a handful of blocks; anything past this
    /// is <c>GetBufferLength</c> reading garbage through the interop (the premise of R27
    /// finding 17), not a real buffer. This is a magnitude check and is <b>not</b> on its own a
    /// bound on the read — see <see cref="MaxBlocksForBounds"/>.
    /// </summary>
    public const int MaxTerritoryBlocks = 65_536;

    /// <summary>
    /// Tiles per castle block on each axis. Native, not inferred: the block loop in
    /// <c>GetPlacementResult.IsArenaStationOverlappingOutsideBlock</c> divides tile coordinates
    /// by 10 and rebuilds each block's tile corners as <c>block*10 + 1</c> / <c>block*10 + 10</c>
    /// in the game's native <c>ProjectM.CastleBuilding.Placement.GetPlacementResult.IsArenaStationOverlappingOutsideBlock</c>
    /// (the <c>/ 10</c> block bounds and the <c>* 10 + 1</c> / <c>* 10 + 10</c>
    /// tile corners). Matches <c>BlockReason.RequireGridAlignment10x10</c> in the game's metadata.
    /// </summary>
    public const int BlockTileSize = 10;

    /// <summary>
    /// The largest number of <c>CastleTerritoryBlocks</c> elements a territory with these
    /// <c>WorldBounds</c> can hold: the count of 10x10 blocks its tile AABB touches, capped at
    /// <see cref="MaxTerritoryBlocks"/>. Returns 0 for an empty or unreadable AABB, which means
    /// "do not read the buffer" rather than "the buffer is empty".
    ///
    /// <para>This exists because a bound computed from the same <c>GetBufferLength</c> the bound
    /// is checking is not a bound at all (R30 finding 7): the caller sizes its span from that
    /// number, so only a value derived from something else — here, the territory's own bounds,
    /// which come from <c>CastleTerritory.WorldBounds</c> — can stop a wrong length from walking
    /// past the allocation. 273 (21 x 13 blocks) on the live 192 x 112 plot, territory 13.</para>
    /// </summary>
    public static int MaxBlocksForBounds(int minX, int minY, int maxX, int maxY)
    {
        if (maxX <= minX || maxY <= minY) return 0;

        long blocksX = BlockOf(maxX - 1) - BlockOf(minX) + 1;
        long blocksY = BlockOf(maxY - 1) - BlockOf(minY) + 1;
        if (blocksX <= 0 || blocksY <= 0) return 0;

        long total = blocksX * blocksY;
        return total > MaxTerritoryBlocks ? MaxTerritoryBlocks : (int)total;
    }

    /// <summary>Block index of a tile coordinate: floor division, so negative tiles round down.</summary>
    private static long BlockOf(int tile)
    {
        long q = tile / BlockTileSize;
        if (tile < 0 && q * BlockTileSize != tile) q--;
        return q;
    }

    /// <summary>
    /// True when a <c>CastleTerritoryBlocks</c> length is inside the band its own territory's
    /// bounds allow. <paramref name="maxBlocks"/> must come from
    /// <see cref="MaxBlocksForBounds"/>; 0 refuses every length, including 0, because a
    /// territory whose bounds could not be read cannot bound the walk.
    /// </summary>
    public static bool BlockLengthOk(int length, int maxBlocks) =>
        maxBlocks > 0 && length >= 0 && length <= maxBlocks;

    /// <summary>
    /// The only length a <c>CastleTerritoryTiles</c> buffer may have: exactly the tile count the
    /// caller computed itself from <c>WorldBounds</c> (<see cref="CastleGeometry.BoundsTileCount"/>).
    /// A garbage length cannot pass this, so a bad read degrades to the AABB instead of answering
    /// with noise. 21 504 (192 x 112) on the live plot, territory 13.
    /// </summary>
    public static bool TileMaskLengthOk(int length, int expectedTileCount) =>
        expectedTileCount > 0 && length == expectedTileCount;

    /// <summary>Byte offset of element <paramref name="index"/>, or -1 when it is out of range.</summary>
    public static int ElementOffset(int index, int length, int stride)
    {
        if (index < 0 || length < 0 || stride <= 0 || index >= length) return -1;
        long at = (long)index * stride;
        return at + stride > int.MaxValue ? -1 : (int)at;
    }

    /// <summary>
    /// Decode a <c>CastleTerritoryTiles</c> buffer into one bool per tile. Returns null when the
    /// length fails <see cref="TileMaskLengthOk"/> or the byte span is shorter than the buffer
    /// claims — both mean "fall back to the bounding box", never "no tiles are buildable".
    /// </summary>
    public static bool[]? DecodeTileMask(ReadOnlySpan<byte> bytes, int length, int expectedTileCount)
    {
        if (!TileMaskLengthOk(length, expectedTileCount)) return null;
        if ((long)length * TileElementStride > bytes.Length) return null;

        var mask = new bool[length];
        for (int i = 0; i < length; i++)
        {
            int at = ElementOffset(i, length, TileElementStride);
            if (at < 0 || at + TileElementStride > bytes.Length) return null;
            mask[i] = bytes[at + TileFlagOffset] != 0;
        }

        return mask;
    }

    /// <summary>
    /// Decode a <c>CastleTerritoryBlocks</c> buffer into interleaved <c>x, y</c> pairs
    /// (<c>2 * length</c> ints). Returns null when the length exceeds
    /// <paramref name="maxBlocks"/> (which the caller derives from the territory's bounds, not
    /// from the length — R30 finding 7) or the byte span is shorter than the buffer claims.
    /// </summary>
    public static int[]? DecodeBlockCoordinates(ReadOnlySpan<byte> bytes, int length, int maxBlocks)
    {
        if (!BlockLengthOk(length, maxBlocks)) return null;
        if ((long)length * BlockElementStride > bytes.Length) return null;
        if (length == 0) return Array.Empty<int>();

        var coords = new int[length * 2];
        for (int i = 0; i < length; i++)
        {
            int at = ElementOffset(i, length, BlockElementStride);
            if (at < 0 || at + BlockElementStride > bytes.Length) return null;
            coords[i * 2] = ReadInt32(bytes, at + BlockCoordinateXOffset);
            coords[(i * 2) + 1] = ReadInt32(bytes, at + BlockCoordinateYOffset);
        }

        return coords;
    }

    private static int ReadInt32(ReadOnlySpan<byte> bytes, int at) =>
        bytes[at] | (bytes[at + 1] << 8) | (bytes[at + 2] << 16) | (bytes[at + 3] << 24);

    /// <summary>
    /// The one line a territory logs when its tile mask is refused. Info, not a warning: on a
    /// territory whose buffer never passes the check this would otherwise fire on every
    /// <c>.castle</c> call, and the degraded answer (the AABB) is a documented mode, not a fault.
    /// </summary>
    public static string TileMaskFallbackLog(string territory, int length, int expectedTileCount) =>
        "Castles: CastleTerritoryTiles on " + territory + " has length " + length
        + ", expected " + expectedTileCount + " from WorldBounds (stride " + TileElementStride
        + ", game metadata); IsInside / CanPlace answer from the territory AABB here. "
        + "Logged once per territory.";

    /// <summary>The line a territory logs when the mask was read and believed.</summary>
    public static string TileMaskReadLog(string territory, int length, int buildableCount) =>
        "Castles: CastleTerritoryTiles on " + territory + " read: " + length
        + " tiles, " + buildableCount + " buildable. IsInside / CanPlace are tile-precise here.";

    /// <summary>
    /// The line a territory logs when its <c>CastleTerritoryBlocks</c> length is outside the
    /// band its own bounds allow. Names both numbers, so the log says which one is suspect.
    /// </summary>
    public static string BlockLengthRefusedLog(string territory, int length, int maxBlocks) =>
        "CastleReader: CastleTerritoryBlocks length " + length + " on " + territory
        + " is outside [0, " + maxBlocks + "], the block count this territory's own WorldBounds "
        + "allow (" + BlockTileSize + " tiles per block); treating it as unread. "
        + "Further reports of this length are silent.";
}
