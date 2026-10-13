using System;
using System.Collections.Generic;
using Stunlock.Core;
using Unity.Entities;

namespace Progenitor.Castles;

/// <summary>
/// One castle plot: a <c>CastleHeart</c> plus the <c>CastleTerritory</c> it points at.
/// Block coordinates come from a raw walk of <c>CastleTerritoryBlocks</c> through the
/// non-generic <c>GetBufferRawRO</c> path (see <c>CastleTerritoryBuffers</c>); they are empty
/// when the buffer is absent or its length failed the sanity check.
/// </summary>
public sealed class CastlePlot
{
    /// <summary><c>CastleTerritory.CastleTerritoryIndex</c>. The plot id mods should key on.</summary>
    public int TerritoryIndex { get; init; }

    /// <summary>Territory entity that carries bounds, decay, and (later) the block buffer.</summary>
    public Entity TerritoryEntity { get; init; }

    /// <summary>Heart entity this plot was read from.</summary>
    public Entity HeartEntity { get; init; }

    /// <summary>Tile AABB min X (<c>WorldBounds.Min.x</c>).</summary>
    public int BoundsMinX { get; init; }

    /// <summary>Tile AABB min Y / world Z (<c>WorldBounds.Min.y</c>).</summary>
    public int BoundsMinY { get; init; }

    /// <summary>Tile AABB max X (<c>WorldBounds.Max.x</c>). Exclusive per <c>GetTiles(false)</c>.</summary>
    public int BoundsMaxX { get; init; }

    /// <summary>Tile AABB max Y / world Z (<c>WorldBounds.Max.y</c>). Exclusive per <c>GetTiles(false)</c>.</summary>
    public int BoundsMaxY { get; init; }

    /// <summary><c>CastleTerritory.MinHeightLevel</c>.</summary>
    public byte MinHeightLevel { get; init; }

    /// <summary><c>CastleTerritory.MaxHeightLevel</c>.</summary>
    public byte MaxHeightLevel { get; init; }

    /// <summary>
    /// Occupied block coordinates from <c>CastleTerritoryBlocks</c>: one <c>int2</c> per
    /// element, stride 8, x at 0x0 and y at 0x4 (server metadata). Empty when the
    /// buffer was missing or its length was refused; see <see cref="BlocksSkipped"/>.
    /// </summary>
    public IReadOnlyList<CastleBlockCoord> BlockCoordinates { get; init; } = Array.Empty<CastleBlockCoord>();

    /// <summary>True when the blocks buffer was not read (missing, or a refused length).</summary>
    public bool BlocksSkipped { get; init; } = true;

    /// <summary>Owner user entity from <c>UserOwner.Owner</c>, else <c>CastleHeart.LastUserOwner</c>.</summary>
    public Entity OwnerUserEntity { get; init; }

    /// <summary><c>User.PlatformId</c> of the owner, or 0 when the user component was missing.</summary>
    public ulong OwnerPlatformId { get; init; }

    /// <summary><c>User.CharacterName</c>, empty when the user component was missing.</summary>
    public string OwnerName { get; init; } = "";

    /// <summary><c>User.ClanEntity</c> on the owner, or default if none.</summary>
    public Entity ClanEntity { get; init; }

    /// <summary><c>ClanTeam.Name</c> when the clan entity was readable.</summary>
    public string ClanName { get; init; } = "";

    /// <summary><c>CastleHeart.Level</c>.</summary>
    public byte Level { get; init; }

    /// <summary><c>CastleHeart.FloorCount</c>. 0 when the heart component was unreadable.</summary>
    public int FloorCount { get; init; }

    /// <summary>
    /// <c>CastleTerritoryBlocks</c> element count from the non-generic
    /// <c>EntityManager.GetBufferLength(entity, typeIndex)</c> path. 0 when the buffer was
    /// missing or the length failed its sanity check; see <see cref="BlockCountRead"/>.
    /// The coordinates are walked in the same read — see <see cref="BlockCoordinates"/>.
    /// </summary>
    public int BlockCount { get; init; }

    /// <summary>True when <see cref="BlockCount"/> came from a length that passed its check.</summary>
    public bool BlockCountRead { get; init; }

    /// <summary>True when <c>CastleHeart.ActiveEvent</c> is <c>Raided</c> (and/or <c>IsRaided()</c> if that invoke survives).</summary>
    public bool Raided { get; init; }

    /// <summary>True when <c>CastleTerritoryDecay.Decaying</c> is set (and/or <c>IsDecaying()</c> if that invoke survives).</summary>
    public bool Decaying { get; init; }
}

/// <summary>One occupied block coordinate. Filled only after the territory-blocks buffer probe.</summary>
public readonly struct CastleBlockCoord
{
    /// <summary>Block X.</summary>
    public int X { get; }

    /// <summary>Block Y (world Z in block space).</summary>
    public int Y { get; }

    /// <summary>Construct a block coordinate.</summary>
    public CastleBlockCoord(int x, int y)
    {
        X = x;
        Y = y;
    }
}

/// <summary>
/// One floor tile on a plot. Empty tiles (no <c>CastleFloor</c>) are omitted — walking the
/// full <c>WorldBounds</c> AABB would dump thousands of empty cells. Wall and door bits use
/// <see cref="CastleLogic.PackCardinals"/>.
/// </summary>
public sealed class CastleTile
{
    /// <summary>Floor entity this row was read from.</summary>
    public Entity FloorEntity { get; init; }

    /// <summary><c>TilePosition.Tile.x</c>.</summary>
    public int X { get; init; }

    /// <summary><c>TilePosition.Tile.y</c> (world Z).</summary>
    public int Y { get; init; }

    /// <summary><c>TilePosition.HeightLevel</c> (fallback: <c>CompressedHeight</c>).</summary>
    public int Height { get; init; }

    /// <summary>Always true for rows this reader emits; empty cells are not listed.</summary>
    public bool FloorPresent { get; init; } = true;

    /// <summary>Raw <c>CastleFloor.FloorType</c> byte, including treasury (14).</summary>
    public byte FloorType { get; init; }

    /// <summary><see cref="CastleLogic.FloorTypeName"/> of <see cref="FloorType"/>.</summary>
    public string FloorTypeName { get; init; } = "";

    /// <summary><c>CastleFloor.Impassable</c>.</summary>
    public bool Impassable { get; init; }

    /// <summary>Room entity from <c>CastleRoomConnection.RoomEntity</c>, or default.</summary>
    public Entity RoomEntity { get; init; }

    /// <summary><see cref="RoomEntity"/> index, 0 when there is no room. Stable enough to group tiles.</summary>
    public int RoomId { get; init; }

    /// <summary>NESW wall-present bits from the four <c>CastleFloor.Wall*</c> entity fields.</summary>
    public int WallFlags { get; init; }

    /// <summary>NESW door-present bits: wall entity exists and carries <c>Door</c>.</summary>
    public int DoorFlags { get; init; }
}

/// <summary>
/// One placed tile-model on a plot. Sorted by <c>NetworkId.Normal_Index</c> then
/// <c>Normal_Generation</c>. Kind is heart / container / station / decor.
/// </summary>
public sealed class CastleBuilding
{
    /// <summary>Tile-model entity.</summary>
    public Entity Entity { get; init; }

    /// <summary>Prefab GUID on the entity.</summary>
    public PrefabGUID PrefabGuid { get; init; }

    /// <summary>Spawnable name from <see cref="Progenitor.Ecs.PrefabLookup"/>, empty if unresolved.</summary>
    public string PrefabName { get; init; } = "";

    /// <summary>Origin tile X.</summary>
    public int OriginX { get; init; }

    /// <summary>Origin tile Y (world Z).</summary>
    public int OriginY { get; init; }

    /// <summary>Origin height level.</summary>
    public int OriginHeight { get; init; }

    /// <summary>Raw <c>TileRotation</c> byte.</summary>
    public byte Rotation { get; init; }

    /// <summary><see cref="CastleLogic.RotationName"/> of <see cref="Rotation"/>.</summary>
    public string RotationName { get; init; } = "";

    /// <summary>Footprint min X from <c>TileBounds.Value</c> (origin tile if bounds were missing).</summary>
    public int FootprintMinX { get; init; }

    /// <summary>Footprint min Y.</summary>
    public int FootprintMinY { get; init; }

    /// <summary>Footprint max X.</summary>
    public int FootprintMaxX { get; init; }

    /// <summary>Footprint max Y.</summary>
    public int FootprintMaxY { get; init; }

    /// <summary>True when the entity carries <c>Interactable</c>. There is no InteractTile list.</summary>
    public bool Interactable { get; init; }

    /// <summary>Heart, container, station, or decor. See <see cref="CastleLogic.ClassifyKind"/>.</summary>
    public CastleLogic.BuildingKind Kind { get; init; }

    /// <summary><c>NetworkId.Normal_Index</c>, or <see cref="int.MaxValue"/> when missing (sorts last).</summary>
    public int NetworkIndex { get; init; } = int.MaxValue;

    /// <summary><c>NetworkId.Normal_Generation</c>.</summary>
    public int NetworkGeneration { get; init; }
}
