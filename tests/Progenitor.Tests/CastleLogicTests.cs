using System;
using System.Collections.Generic;
using Progenitor.Castles;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Game-free coverage of castle-read ordering, kind classification, NESW bit packing,
/// floor-type names (including treasury), and the debug-bridge limit clamp.
/// Linked through <c>CastleLogic.cs</c>.
/// </summary>
public class CastleLogicTests
{
    [Fact]
    public void CompareTiles_orders_y_then_x_then_height()
    {
        Assert.True(CastleLogic.CompareTiles(y: 1, x: 9, height: 0, otherY: 2, otherX: 0, otherHeight: 0) < 0);
        Assert.True(CastleLogic.CompareTiles(y: 2, x: 0, height: 0, otherY: 1, otherX: 9, otherHeight: 0) > 0);
        Assert.True(CastleLogic.CompareTiles(y: 1, x: 1, height: 0, otherY: 1, otherX: 2, otherHeight: 0) < 0);
        Assert.True(CastleLogic.CompareTiles(y: 1, x: 1, height: 0, otherY: 1, otherX: 1, otherHeight: 3) < 0);
        Assert.Equal(0, CastleLogic.CompareTiles(4, 5, 6, 4, 5, 6));
    }

    [Fact]
    public void Tile_sort_is_row_major()
    {
        var tiles = new List<(int y, int x, int h)>
        {
            (1, 2, 0),
            (0, 9, 1),
            (0, 1, 0),
            (0, 1, 2),
            (1, 0, 0),
        };
        tiles.Sort((a, b) => CastleLogic.CompareTiles(a.y, a.x, a.h, b.y, b.x, b.h));
        Assert.Equal(new[] { (0, 1, 0), (0, 1, 2), (0, 9, 1), (1, 0, 0), (1, 2, 0) }, tiles);
    }

    [Fact]
    public void CompareNetworkIds_orders_index_then_generation()
    {
        Assert.True(CastleLogic.CompareNetworkIds(1, 9, 2, 0) < 0);
        Assert.True(CastleLogic.CompareNetworkIds(2, 0, 1, 9) > 0);
        Assert.True(CastleLogic.CompareNetworkIds(5, 1, 5, 2) < 0);
        Assert.Equal(0, CastleLogic.CompareNetworkIds(7, 3, 7, 3));
        Assert.True(CastleLogic.CompareNetworkIds(int.MaxValue, 0, 0, 0) > 0);
    }

    [Fact]
    public void Building_sort_puts_missing_ids_last()
    {
        var ids = new List<(int index, int gen)>
        {
            (int.MaxValue, 0),
            (2, 1),
            (2, 0),
            (0, 0),
        };
        ids.Sort((a, b) => CastleLogic.CompareNetworkIds(a.index, a.gen, b.index, b.gen));
        Assert.Equal(new[] { (0, 0), (2, 0), (2, 1), (int.MaxValue, 0) }, ids);
    }

    [Theory]
    [InlineData(true, true, true, CastleLogic.BuildingKind.Heart)]
    [InlineData(true, false, false, CastleLogic.BuildingKind.Heart)]
    [InlineData(false, true, true, CastleLogic.BuildingKind.Container)]
    [InlineData(false, true, false, CastleLogic.BuildingKind.Container)]
    [InlineData(false, false, true, CastleLogic.BuildingKind.Station)]
    [InlineData(false, false, false, CastleLogic.BuildingKind.Decor)]
    public void ClassifyKind_priority_is_heart_container_station_decor(
        bool heart, bool container, bool station, CastleLogic.BuildingKind expected)
    {
        Assert.Equal(expected, CastleLogic.ClassifyKind(heart, container, station));
    }

    [Theory]
    [InlineData(CastleLogic.BuildingKind.Heart, "heart")]
    [InlineData(CastleLogic.BuildingKind.Container, "container")]
    [InlineData(CastleLogic.BuildingKind.Station, "station")]
    [InlineData(CastleLogic.BuildingKind.Decor, "decor")]
    public void KindName_is_stable_lowercase(CastleLogic.BuildingKind kind, string name)
    {
        Assert.Equal(name, CastleLogic.KindName(kind));
    }

    [Fact]
    public void PackCardinals_uses_nesw_bits()
    {
        Assert.Equal(0, CastleLogic.PackCardinals(false, false, false, false));
        Assert.Equal(CastleLogic.WallNorth, CastleLogic.PackCardinals(true, false, false, false));
        Assert.Equal(CastleLogic.WallEast, CastleLogic.PackCardinals(false, true, false, false));
        Assert.Equal(CastleLogic.WallSouth, CastleLogic.PackCardinals(false, false, true, false));
        Assert.Equal(CastleLogic.WallWest, CastleLogic.PackCardinals(false, false, false, true));
        Assert.Equal(
            CastleLogic.WallNorth | CastleLogic.WallEast | CastleLogic.WallSouth | CastleLogic.WallWest,
            CastleLogic.PackCardinals(true, true, true, true));
    }

    [Fact]
    public void Cardinal_bits_round_trip()
    {
        int flags = CastleLogic.PackCardinals(north: true, east: false, south: true, west: true);
        Assert.True(CastleLogic.HasNorth(flags));
        Assert.False(CastleLogic.HasEast(flags));
        Assert.True(CastleLogic.HasSouth(flags));
        Assert.True(CastleLogic.HasWest(flags));
        Assert.False(CastleLogic.HasNorth(0));
        Assert.False(CastleLogic.HasEast(0));
        Assert.False(CastleLogic.HasSouth(0));
        Assert.False(CastleLogic.HasWest(0));
    }

    [Fact]
    public void Door_flags_share_the_wall_bit_layout()
    {
        int doors = CastleLogic.PackCardinals(north: false, east: true, south: false, west: false);
        Assert.Equal(CastleLogic.WallEast, doors);
        Assert.True(CastleLogic.HasEast(doors));
        Assert.False(CastleLogic.HasNorth(doors));
    }

    [Theory]
    [InlineData(0, "None")]
    [InlineData(1, "Hallway")]
    [InlineData(4, "Forge")]
    [InlineData(13, "UniversalFloor")]
    [InlineData(14, "Treasury")]
    [InlineData(99, "Unknown:99")]
    public void FloorTypeName_covers_treasury_and_unknown(byte type, string name)
    {
        Assert.Equal(name, CastleLogic.FloorTypeName(type));
    }

    [Fact]
    public void IsTreasury_is_only_floor_type_14()
    {
        Assert.True(CastleLogic.IsTreasury(CastleLogic.FloorTypeTreasury));
        Assert.True(CastleLogic.IsTreasury(14));
        Assert.False(CastleLogic.IsTreasury(0));
        Assert.False(CastleLogic.IsTreasury(13));
        Assert.False(CastleLogic.IsTreasury(15));
    }

    [Theory]
    [InlineData(0, "None")]
    [InlineData(1, "Clockwise_90")]
    [InlineData(2, "Clockwise_180")]
    [InlineData(3, "Clockwise_270")]
    [InlineData(9, "Unknown:9")]
    public void RotationName_matches_TileRotation(byte rotation, string name)
    {
        Assert.Equal(name, CastleLogic.RotationName(rotation));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-4, 1)]
    [InlineData(1, 1)]
    [InlineData(200, 200)]
    [InlineData(2000, 2000)]
    [InlineData(2001, 2000)]
    [InlineData(int.MaxValue, 2000)]
    public void ClampLimit_stays_between_one_and_max(int input, int expected)
    {
        Assert.Equal(expected, CastleLogic.ClampLimit(input));
    }

    [Fact]
    public void TileInBounds_is_min_inclusive_max_exclusive()
    {
        Assert.True(CastleLogic.TileInBounds(0, 0, 0, 0, 10, 10));
        Assert.True(CastleLogic.TileInBounds(9, 9, 0, 0, 10, 10));
        Assert.False(CastleLogic.TileInBounds(10, 0, 0, 0, 10, 10));
        Assert.False(CastleLogic.TileInBounds(0, 10, 0, 0, 10, 10));
        Assert.False(CastleLogic.TileInBounds(-1, 0, 0, 0, 10, 10));
    }
}
