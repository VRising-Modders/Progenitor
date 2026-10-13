using System;

namespace Progenitor.Castles;

/// <summary>
/// Game-free castle-read helpers: row-major tile order, NetworkId building order, kind
/// classification, NESW wall/door bit packing, and floor-type names. The ECS reader and the
/// unit tests share this so live output and tests cannot drift. No Unity / ProjectM types.
/// </summary>
public static class CastleLogic
{
    /// <summary>North wall or door bit. Matches <c>CastleFloor.WallNorth</c>.</summary>
    public const int WallNorth = 1;

    /// <summary>East wall or door bit. Matches <c>CastleFloor.WallEast</c>.</summary>
    public const int WallEast = 2;

    /// <summary>South wall or door bit. Matches <c>CastleFloor.WallSouth</c>.</summary>
    public const int WallSouth = 4;

    /// <summary>West wall or door bit. Matches <c>CastleFloor.WallWest</c>.</summary>
    public const int WallWest = 8;

    /// <summary>
    /// <c>CastleFloorTypes.Treasury</c> is 14 on this game build. Kept as a number so tests
    /// do not reference the interop enum.
    /// </summary>
    public const byte FloorTypeTreasury = Layout.CastleFloorTypesTreasury;

    /// <summary>Default row cap for the debug-bridge <c>castles</c> op.</summary>
    public const int DefaultLimit = 200;

    /// <summary>Hard cap for the debug-bridge <c>castles</c> op so a huge plot cannot stall the sim.</summary>
    public const int MaxLimit = 2_000;

    /// <summary>
    /// Placed-building kind the module exposes. Heart wins over container, container over
    /// station, everything else is decor (floors, walls, doors, and furniture without an
    /// inventory or workstation).
    /// </summary>
    public enum BuildingKind
    {
        Decor = 0,
        Container = 1,
        Station = 2,
        Heart = 3,
    }

    /// <summary>
    /// Row-major tile order: <c>Tile.y</c> (world Z), then <c>Tile.x</c>, then height.
    /// That is the order Satisvampory and any overlay need so two reads of the same plot
    /// produce the same sequence.
    /// </summary>
    public static int CompareTiles(int y, int x, int height, int otherY, int otherX, int otherHeight)
    {
        int c = y.CompareTo(otherY);
        if (c != 0) return c;
        c = x.CompareTo(otherX);
        if (c != 0) return c;
        return height.CompareTo(otherHeight);
    }

    /// <summary>
    /// Building order: <c>NetworkId.Normal_Index</c> then <c>Normal_Generation</c>.
    /// Missing ids should be sorted with <see cref="int.MaxValue"/> so they do not interleave
    /// with a real index 0.
    /// </summary>
    public static int CompareNetworkIds(int index, int generation, int otherIndex, int otherGeneration)
    {
        int c = index.CompareTo(otherIndex);
        if (c != 0) return c;
        return generation.CompareTo(otherGeneration);
    }

    /// <summary>
    /// Kind from presence flags. Heart first so the castle heart (which also has an inventory)
    /// is not reported as a container; then container; then station (workstation or refinement);
    /// otherwise decor.
    /// </summary>
    public static BuildingKind ClassifyKind(bool isHeart, bool isContainer, bool isStation)
    {
        if (isHeart) return BuildingKind.Heart;
        if (isContainer) return BuildingKind.Container;
        if (isStation) return BuildingKind.Station;
        return BuildingKind.Decor;
    }

    /// <summary>Stable lowercase name for JSON and logs.</summary>
    public static string KindName(BuildingKind kind) => kind switch
    {
        BuildingKind.Heart => "heart",
        BuildingKind.Container => "container",
        BuildingKind.Station => "station",
        _ => "decor",
    };

    /// <summary>Pack four cardinal presence flags into the NESW bitfield.</summary>
    public static int PackCardinals(bool north, bool east, bool south, bool west)
    {
        int flags = 0;
        if (north) flags |= WallNorth;
        if (east) flags |= WallEast;
        if (south) flags |= WallSouth;
        if (west) flags |= WallWest;
        return flags;
    }

    /// <summary>True when the north bit is set.</summary>
    public static bool HasNorth(int flags) => (flags & WallNorth) != 0;

    /// <summary>True when the east bit is set.</summary>
    public static bool HasEast(int flags) => (flags & WallEast) != 0;

    /// <summary>True when the south bit is set.</summary>
    public static bool HasSouth(int flags) => (flags & WallSouth) != 0;

    /// <summary>True when the west bit is set.</summary>
    public static bool HasWest(int flags) => (flags & WallWest) != 0;

    /// <summary>
    /// <c>CastleFloorTypes</c> name for a raw byte. Treasury is called out because the
    /// consumer treats it as a special floor, not a room-type enum (there is no room-type enum).
    /// Unknown values stay numeric so a game patch adding a floor type is still greppable.
    /// </summary>
    public static string FloorTypeName(byte floorType) => floorType switch
    {
        0 => "None",
        1 => "Hallway",
        2 => "Lumbermill",
        3 => "Tailor",
        4 => "Forge",
        5 => "CryptCastle",
        6 => "Library",
        7 => "VerminNest",
        8 => "AlchemyLab",
        9 => "Bestiary",
        10 => "Prison",
        11 => "ThroneHall",
        12 => "Temple",
        13 => "UniversalFloor",
        FloorTypeTreasury => "Treasury",
        _ => "Unknown:" + floorType.ToString(),
    };

    /// <summary>True when <paramref name="floorType"/> is the treasury floor.</summary>
    public static bool IsTreasury(byte floorType) => floorType == FloorTypeTreasury;

    /// <summary>
    /// <c>TileRotation</c> name. The game has no separate rotation enum for tiles;
    /// this is the encoding on <c>TilePosition.TileRotation</c>.
    /// </summary>
    public static string RotationName(byte rotation) => rotation switch
    {
        0 => "None",
        1 => "Clockwise_90",
        2 => "Clockwise_180",
        3 => "Clockwise_270",
        _ => "Unknown:" + rotation.ToString(),
    };

    /// <summary>
    /// Pin a debug-bridge <c>limit</c> to [1, <see cref="MaxLimit"/>]. Zero and negative
    /// become 1 so a missing-or-garbage arg cannot request an empty page by accident.
    /// </summary>
    public static int ClampLimit(int limit)
    {
        if (limit < 1) return 1;
        if (limit > MaxLimit) return MaxLimit;
        return limit;
    }

    /// <summary>
    /// Inclusive-min, exclusive-max AABB test matching <c>BoundsMinMax.GetTiles(includeMaxBoundary: false)</c>.
    /// Used when a floor has no <c>CastleHeartConnection</c> so we still keep tiles inside the plot.
    /// </summary>
    public static bool TileInBounds(int x, int y, int minX, int minY, int maxX, int maxY) =>
        x >= minX && x < maxX && y >= minY && y < maxY;
}
