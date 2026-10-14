using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Progenitor.Castles;
using Progenitor.Events;
using Xunit;
using CastlesApi = Progenitor.Castles.Castles;

namespace Progenitor.Tests;

/// <summary>
/// Game-free coverage of the Castles handle API: territory math pinned to the live readings,
/// building enumeration and grouping, IsInside with and without the buildable-tile mask, the
/// read-only placement check, the polled change detector, and the disabled-on-client contract.
///
/// <para>The fixture is the real castle read off the test server on 2026-09-04 (save
/// <c>test1</c>): territory 13, heart <c>327390:1</c>, owner TestPlayer <c>326690:1</c>, bounds
/// 2639,2899..2831,3011, height levels 10-12, one <c>TM_BloodFountain_CastleHeart</c> at tile
/// 2785,2985 and one <c>TM_Castle_Floor_Outdoor_Plain01</c> at 2775,2965. Using the live numbers
/// means a change to the geometry constants breaks a test rather than a live read.</para>
///
/// <para>The <b>entity ids</b> in the fixture are the ones read at that moment and nothing more:
/// a save reload later in the same session re-assigned every one of them (heart 327390:1 became
/// 325974:1) while the territory index, bounds, tile coordinates and <c>NetworkId</c> stayed
/// identical. That is why <c>Castles.ForTerritory</c> is the reload-safe lookup and why
/// <c>CastleWatch</c> reports a reload as one destroy plus one place.</para>
/// </summary>
[Collection("PluginLogger")]
public class CastlesTests : IDisposable
{
    // Live readings, 2026-09-04 test server (internal research notes).
    private const int TerritoryIndex = 13;
    private const int MinX = 2639, MinY = 2899, MaxX = 2831, MaxY = 3011;
    private static readonly CastleId Heart = new(327390, 1);
    private static readonly CastleId TerritoryEntity = new(2032, 1);
    private static readonly CastleId TestPlayer = new(326690, 1);
    private static readonly CastleId Character = new(324767, 1);

    private readonly TestLogSource _log = new();
    private readonly object _owner = new Owner("example.castles.test");
    private readonly FakeCastleRuntime _server = new() { IsServer = true };

    public CastlesTests()
    {
        Plugin.Logger = _log;
        Progenitor.WorldState.WorldEpoch.ResetForTests();
        CastlesApi.ResetForTests();
        _server.Castles.Add(LiveCastle());
        CastlesApi.SetRuntimeForTests(_server);
    }

    public void Dispose()
    {
        EventBus.RemoveAll(_owner);
        CastlesApi.ResetForTests();
        Progenitor.WorldState.WorldEpoch.ResetForTests();
        Plugin.Logger = null;
    }

    // ---- geometry ---------------------------------------------------------------------------

    [Theory]
    // (world x, world z) -> (tile x, tile y), from the two live tile-models.
    [InlineData(-1807.5f, -1707.5f, 2785, 2985)] // TM_BloodFountain_CastleHeart 327390:1
    [InlineData(-1812.5f, -1717.5f, 2775, 2965)] // TM_Castle_Floor_Outdoor_Plain01 327377:1
    public void WorldToTile_matches_the_live_tile_models(float worldX, float worldZ, int tileX, int tileY)
    {
        Assert.True(CastleGeometry.TryWorldToTile(new CastleVec3(worldX, 5f, worldZ), out int x, out int y));
        Assert.Equal(tileX, x);
        Assert.Equal(tileY, y);

        // And back: the tile's world anchor is exactly what Translation carried. These are
        // exact binary floats (multiples of 0.5), so this is an equality, not a tolerance.
        Assert.Equal(worldX, CastleGeometry.TileToWorld(tileX));
        Assert.Equal(worldZ, CastleGeometry.TileToWorld(tileY));
    }

    [Fact]
    public void WorldToTile_refuses_nonfinite_positions()
    {
        Assert.False(CastleGeometry.TryWorldToTile(new CastleVec3(float.NaN, 0f, 0f), out _, out _));
        Assert.False(CastleGeometry.TryWorldToTile(new CastleVec3(0f, 0f, float.PositiveInfinity), out _, out _));
    }

    [Fact]
    public void LocalTileIndex_is_row_major_over_the_bounds_and_minus_one_outside()
    {
        Assert.Equal(0, CastleGeometry.LocalTileIndex(MinX, MinY, MinX, MinY, MaxX, MaxY));
        Assert.Equal(1, CastleGeometry.LocalTileIndex(MinX + 1, MinY, MinX, MinY, MaxX, MaxY));
        Assert.Equal(MaxX - MinX, CastleGeometry.LocalTileIndex(MinX, MinY + 1, MinX, MinY, MaxX, MaxY));
        Assert.Equal(-1, CastleGeometry.LocalTileIndex(MaxX, MinY, MinX, MinY, MaxX, MaxY));
        Assert.Equal(-1, CastleGeometry.LocalTileIndex(MinX - 1, MinY, MinX, MinY, MaxX, MaxY));

        // 192 x 112 on the live plot; this is the length CastleTerritoryTiles must have.
        Assert.Equal(192 * 112, CastleGeometry.BoundsTileCount(MinX, MinY, MaxX, MaxY));
    }

    [Fact]
    public void LocalToWorldBounds_translates_at_rotation_none_and_keeps_the_box_at_90_degrees()
    {
        CastleGeometry.LocalToWorldBounds(2775, 2965, 0, -6, -6, 6, 6,
            out int minX, out int minY, out int maxX, out int maxY);
        // Exactly the live footprint of TM_Castle_Floor_Outdoor_Plain01.
        Assert.Equal(2769, minX);
        Assert.Equal(2959, minY);
        Assert.Equal(2781, maxX);
        Assert.Equal(2971, maxY);

        // A 90° step swaps the axes of the box: 4×2 local becomes 2×4 world.
        CastleGeometry.LocalToWorldBounds(0, 0, 1, 0, 0, 4, 2, out minX, out minY, out maxX, out maxY);
        Assert.Equal(2, maxX - minX);
        Assert.Equal(4, maxY - minY);

        // 180° keeps the extent and mirrors the box through the pivot. Derived by hand, not
        // read off the implementation: the local box [0,4) x [0,2) occupies tiles x ∈ {0,1,2,3}
        // and y ∈ {0,1}. Under (x,y) -> (-x,-y) those become x ∈ {0,-1,-2,-3} and y ∈ {0,-1},
        // i.e. the half-open boxes [-3, 1) and [-1, 1) — so around pivot (10, 20) the result is
        // X [7, 11) and Y [19, 21). [-4, 0) would be the same box shifted one tile too far
        // negative, which is what treating the exclusive max as inclusive produces (R30 f8).
        CastleGeometry.LocalToWorldBounds(10, 20, 2, 0, 0, 4, 2, out minX, out minY, out maxX, out maxY);
        Assert.Equal(4, maxX - minX);
        Assert.Equal(2, maxY - minY);
        Assert.Equal(10 - 3, minX);
        Assert.Equal(20 - 1, minY);
        Assert.Equal(10 + 1, maxX);
        Assert.Equal(20 + 1, maxY);
    }

    /// <summary>
    /// All four rotation steps of an asymmetric footprint, each expectation derived by hand
    /// from the tile set rather than from the code under test.
    ///
    /// <para>The footprint is the local box <c>[0,2) x [0,3)</c> — 2 tiles wide, 3 deep, so the
    /// two axes cannot be confused — occupying tiles <c>x ∈ {0,1}</c>, <c>y ∈ {0,1,2}</c>.
    /// Mapping that tile set through each rotation and re-reading off the half-open box:</para>
    /// <list type="table">
    /// <item><description>0, identity: x {0,1} -> [0,2); y {0,1,2} -> [0,3)</description></item>
    /// <item><description>1, (x,y) -> (y,-x): x images {0,1,2} -> [0,3); y images {0,-1} -> [-1,1)</description></item>
    /// <item><description>2, (x,y) -> (-x,-y): x images {0,-1} -> [-1,1); y images {0,-1,-2} -> [-2,1)</description></item>
    /// <item><description>3, (x,y) -> (-y,x): x images {0,-1,-2} -> [-2,1); y images {0,1} -> [0,2)</description></item>
    /// </list>
    /// <para>Then translated to the pivot (100, 200). A rotation that treats the exclusive max
    /// as inclusive fails every non-zero row here by exactly one tile on the negated axis.</para>
    /// </summary>
    [Theory]
    [InlineData(0, 100, 200, 102, 203)]
    [InlineData(1, 100, 199, 103, 201)]
    [InlineData(2, 99, 198, 101, 201)]
    [InlineData(3, 98, 200, 101, 202)]
    public void LocalToWorldBounds_rotates_an_asymmetric_footprint_half_open(
        byte rotation, int expectedMinX, int expectedMinY, int expectedMaxX, int expectedMaxY)
    {
        CastleGeometry.LocalToWorldBounds(100, 200, rotation, 0, 0, 2, 3,
            out int minX, out int minY, out int maxX, out int maxY);

        Assert.Equal(expectedMinX, minX);
        Assert.Equal(expectedMinY, minY);
        Assert.Equal(expectedMaxX, maxX);
        Assert.Equal(expectedMaxY, maxY);

        // The tile count is a rotation invariant: 2 x 3 = 6 tiles, whichever way it is turned.
        Assert.Equal(6, (maxX - minX) * (maxY - minY));
    }

    /// <summary>
    /// R31 finding 30. A local box with <c>max &lt;= min</c> on either axis holds no tiles, so
    /// its rotation holds none either. The rotation cases negate an axis and re-normalize with
    /// <c>Min</c>/<c>Max</c>, which used to turn an inverted box into a real one of positive
    /// width — local X <c>[5,2)</c> under rotation 2 became <c>[-4,-1)</c>, three tiles wide,
    /// and <c>BoundsOverlap</c> would then happily intersect it with real construction.
    ///
    /// <para>The table covers inverted-X, inverted-Y, both-inverted and zero-width boxes at
    /// every rotation step including one past the mask (7 &amp; 3 == 3). Each row asserts the
    /// box is still empty <i>and</i> that <see cref="CastleGeometry.BoundsOverlap"/> refuses it
    /// against a box that certainly covers the pivot.</para>
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    public void LocalToWorldBounds_keeps_an_empty_local_box_empty_under_every_rotation(byte rotation)
    {
        var boxes = new[]
        {
            (MinX: 5, MinY: 0, MaxX: 2, MaxY: 4),   // inverted on X
            (MinX: 0, MinY: 5, MaxX: 4, MaxY: 2),   // inverted on Y
            (MinX: 5, MinY: 5, MaxX: 2, MaxY: 2),   // inverted on both
            (MinX: 3, MinY: 3, MaxX: 3, MaxY: 3),   // zero-width on both
            (MinX: 0, MinY: 3, MaxX: 2, MaxY: 3),   // zero-width on Y only
        };

        foreach (var box in boxes)
        {
            CastleGeometry.LocalToWorldBounds(100, 200, rotation, box.MinX, box.MinY, box.MaxX, box.MaxY,
                out int minX, out int minY, out int maxX, out int maxY);

            Assert.True(maxX <= minX || maxY <= minY,
                "rotation " + rotation + " turned local " + box + " into ["
                + minX + "," + maxX + ") x [" + minY + "," + maxY + ")");

            Assert.False(CastleGeometry.BoundsOverlap(minX, minY, maxX, maxY, 90, 190, 110, 210));
        }
    }

    [Fact]
    public void LocalToWorldBounds_composes_two_90_degree_steps_into_one_180()
    {
        // Independent of the per-case expectations above: rotating the box twice by 90° about
        // the origin must land on the 180° box. A one-tile error in a single negated axis does
        // not survive this (the second step negates the other axis), so this fails for the
        // exclusive/inclusive mix-up as well.
        CastleGeometry.LocalToWorldBounds(0, 0, 1, 0, 0, 2, 3,
            out int oneMinX, out int oneMinY, out int oneMaxX, out int oneMaxY);
        CastleGeometry.LocalToWorldBounds(0, 0, 1, oneMinX, oneMinY, oneMaxX, oneMaxY,
            out int twiceMinX, out int twiceMinY, out int twiceMaxX, out int twiceMaxY);

        CastleGeometry.LocalToWorldBounds(0, 0, 2, 0, 0, 2, 3,
            out int halfMinX, out int halfMinY, out int halfMaxX, out int halfMaxY);

        Assert.Equal(halfMinX, twiceMinX);
        Assert.Equal(halfMinY, twiceMinY);
        Assert.Equal(halfMaxX, twiceMaxX);
        Assert.Equal(halfMaxY, twiceMaxY);
    }

    /// <summary>
    /// The mask index is the game's, recovered from the native body of
    /// <c>BoundsMinMax.GetLocalIndexFromBoundsTile</c> (R30 finding 17). This walks a small
    /// bounds exhaustively and checks the index against the ordering that body implements —
    /// enumerate rows, and within a row enumerate columns — computed here by counting, not by
    /// repeating the production expression.
    /// </summary>
    [Fact]
    public void LocalTileIndex_enumerates_rows_before_columns_over_the_whole_bounds()
    {
        const int minX = -3, minY = 7, maxX = 2, maxY = 11;   // 5 wide, 4 deep; negative min

        int counted = 0;
        for (int y = minY; y < maxY; y++)
        {
            for (int x = minX; x < maxX; x++)
            {
                Assert.Equal(counted, CastleGeometry.LocalTileIndex(x, y, minX, minY, maxX, maxY));
                counted++;
            }
        }

        // Every tile in the bounds got exactly one index, and they filled [0, count).
        Assert.Equal(CastleGeometry.BoundsTileCount(minX, minY, maxX, maxY), counted);

        // A column-major mask would be a transpose of this one. On a non-square bounds the two
        // orders disagree on every tile off the diagonal, which is what makes the live 192 x 112
        // plot able to settle the question; here 5 x 4 shows the same thing.
        Assert.NotEqual(
            CastleGeometry.LocalTileIndex(minX + 1, minY, minX, minY, maxX, maxY),
            CastleGeometry.LocalTileIndex(minX, minY + 1, minX, minY, maxX, maxY));
    }

    // ---- reads ------------------------------------------------------------------------------

    [Fact]
    public void All_returns_one_handle_per_castle_with_the_live_identity()
    {
        var all = CastlesApi.All;
        var castle = Assert.Single(all);

        Assert.True(castle.Enabled);
        Assert.Equal(Heart, castle.Heart);
        Assert.Equal(TerritoryIndex, castle.Territory.Index);
        Assert.Equal(TerritoryEntity, castle.Territory.Entity);
        Assert.Equal(MinX, castle.Territory.MinX);
        Assert.Equal(MaxY, castle.Territory.MaxY);
        Assert.Equal(192, castle.Territory.Width);
        Assert.Equal(112, castle.Territory.Depth);
        Assert.Equal((byte)10, castle.Territory.MinHeightLevel);
        Assert.Equal((byte)12, castle.Territory.MaxHeightLevel);
        Assert.Equal("TestPlayer", castle.Owner.Name);
        Assert.Equal(76561198000000076UL, castle.Owner.PlatformId);
        Assert.False(castle.Owner.HasClan);
        Assert.Equal((byte)1, castle.Level);
        Assert.False(castle.Raided);
        Assert.True(castle.Decaying);
    }

    [Fact]
    public void ForUser_matches_the_owner_and_returns_a_disabled_handle_for_a_stranger()
    {
        var mine = CastlesApi.ForUser(TestPlayer);
        Assert.True(mine.Enabled);
        Assert.Equal(TerritoryIndex, mine.Territory.Index);

        var stranger = CastlesApi.ForUser(new CastleId(999, 1));
        Assert.False(stranger.Enabled);
        Assert.Equal(CastleId.None, stranger.Heart);
        Assert.Empty(stranger.Buildings());
        Assert.False(stranger.IsInside(new CastleVec3(-1807.5f, 5f, -1707.5f)));
    }

    [Fact]
    public void ForUser_matches_a_clanmate_through_the_clan_entity()
    {
        var clan = new CastleId(4242, 1);
        _server.Castles.Clear();
        _server.Castles.Add(LiveCastle(clan: clan, clanName: "Nightfall"));
        var clanmate = new CastleId(777, 1);
        _server.Clans[clanmate] = clan;

        var castle = CastlesApi.ForUser(clanmate);
        Assert.True(castle.Enabled);
        Assert.Equal("Nightfall", castle.Owner.ClanName);
        Assert.True(castle.Owner.HasClan);
    }

    [Fact]
    public void Buildings_are_enumerated_and_grouped_by_prefab_highest_count_first()
    {
        var castle = CastlesApi.ForUser(TestPlayer);
        var buildings = castle.Buildings();
        Assert.Equal(2, buildings.Count);
        Assert.Equal("TM_BloodFountain_CastleHeart", buildings[0].PrefabName);
        Assert.Equal(-485210554, buildings[0].PrefabGuid);
        Assert.Equal("heart", buildings[0].Kind);
        Assert.Equal(2785, buildings[0].TileX);
        Assert.Equal(2985, buildings[0].TileY);
        Assert.Equal(11, buildings[0].HeightLevel);
        Assert.Equal("TM_Castle_Floor_Outdoor_Plain01", buildings[1].PrefabName);
        Assert.Equal("decor", buildings[1].Kind);

        _server.Buildings[Heart].Add(Floor(2765, 2965, netIndex: 17802));
        var rows = CastlesApi.ForUser(TestPlayer).BuildingsByPrefab();
        Assert.Equal(2, rows.Count);
        Assert.Equal("TM_Castle_Floor_Outdoor_Plain01", rows[0].Key);
        Assert.Equal(2, rows[0].Value);
        Assert.Equal("TM_BloodFountain_CastleHeart", rows[1].Key);
        Assert.Equal(1, rows[1].Value);
        Assert.Equal("TM_Castle_Floor_Outdoor_Plain01×2, TM_BloodFountain_CastleHeart×1", CastleCounts.Format(rows));
    }

    [Fact]
    public void Buildings_with_an_unnamed_prefab_group_under_the_guid()
    {
        _server.Buildings[Heart].Clear();
        _server.Buildings[Heart].Add(new CastleBuildingInfo { PrefabGuid = 12345, TileX = 2700, TileY = 2900 });
        var rows = CastlesApi.ForUser(TestPlayer).BuildingsByPrefab();
        Assert.Equal("12345", Assert.Single(rows).Key);
    }

    // ---- IsInside ---------------------------------------------------------------------------

    [Fact]
    public void IsInside_uses_the_bounds_when_the_tile_mask_is_not_readable()
    {
        var castle = CastlesApi.ForUser(TestPlayer);
        Assert.False(castle.IsTilePrecise);

        Assert.True(castle.IsInside(new CastleVec3(-1807.5f, 5f, -1707.5f)));   // the heart tile
        // TestPlayer's live position while this job ran: tile 2705,3672 — well past MaxY 3011.
        Assert.False(castle.IsInside(new CastleVec3(-1847.0658f, 5f, -1363.5184f)));
    }

    [Fact]
    public void IsInside_uses_the_buildable_tile_mask_when_it_is_readable()
    {
        int count = CastleGeometry.BoundsTileCount(MinX, MinY, MaxX, MaxY);
        var mask = new bool[count];
        int heartIndex = CastleGeometry.LocalTileIndex(2785, 2985, MinX, MinY, MaxX, MaxY);
        mask[heartIndex] = true;
        _server.BuildableTiles[TerritoryEntity] = mask;

        var castle = CastlesApi.ForUser(TestPlayer);
        Assert.True(castle.IsTilePrecise);
        Assert.True(castle.IsInside(new CastleVec3(-1807.5f, 5f, -1707.5f)));
        // Inside the AABB but not marked buildable.
        Assert.False(castle.IsInside(new CastleVec3(CastleGeometry.TileToWorld(2700), 5f, CastleGeometry.TileToWorld(2950))));
    }

    [Fact]
    public void IsInside_a_mask_of_the_wrong_length_is_refused_and_falls_back_to_bounds()
    {
        _server.BuildableTiles[TerritoryEntity] = new bool[7];
        var castle = CastlesApi.ForUser(TestPlayer);
        Assert.False(castle.IsTilePrecise);
        Assert.True(castle.IsInside(new CastleVec3(-1807.5f, 5f, -1707.5f)));
    }

    [Fact]
    public void IsInside_entity_prefers_TilePosition_and_falls_back_to_Translation()
    {
        var castle = CastlesApi.ForUser(TestPlayer);

        // A tile-model: its own tile and height level answer directly.
        _server.TilePositions[Heart] = (2785, 2985, 11);
        Assert.True(castle.IsInside(Heart));

        // Same tile, height level outside the territory band 10-12.
        _server.TilePositions[Heart] = (2785, 2985, 40);
        Assert.False(castle.IsInside(Heart));

        // A character has no TilePosition; Translation is used.
        _server.Positions[Character] = new CastleVec3(-1807.5f, 5f, -1707.5f);
        Assert.True(castle.IsInside(Character));
        _server.Positions[Character] = new CastleVec3(-1847.0658f, 5f, -1363.5184f);
        Assert.False(castle.IsInside(Character));

        Assert.False(castle.IsInside(CastleId.None));
    }

    [Fact]
    public void At_finds_the_castle_that_contains_a_position_and_nothing_outside()
    {
        Assert.Equal(TerritoryIndex, CastlesApi.At(new CastleVec3(-1807.5f, 5f, -1707.5f)).Territory.Index);
        Assert.False(CastlesApi.At(new CastleVec3(-1847.0658f, 5f, -1363.5184f)).Enabled);
    }

    // ---- CanPlace ---------------------------------------------------------------------------

    [Fact]
    public void CanPlace_is_never_complete_and_always_names_what_it_did_not_check()
    {
        var castle = CastlesApi.ForUser(TestPlayer);
        var check = castle.CanPlace(1356453147, new CastleVec3(CastleGeometry.TileToWorld(2700), 5f, CastleGeometry.TileToWorld(2950)));
        Assert.False(check.Complete);
        Assert.NotEmpty(check.NotChecked);
        Assert.Contains(check.NotChecked, s => s.Contains("build cost", StringComparison.Ordinal));
    }

    [Fact]
    public void CanPlace_allows_a_free_tile_inside_the_territory()
    {
        var castle = CastlesApi.ForUser(TestPlayer);
        var check = castle.CanPlace(1356453147, new CastleVec3(CastleGeometry.TileToWorld(2700), 5f, CastleGeometry.TileToWorld(2950)));
        Assert.True(check.Allowed);
        Assert.Equal(CastleBlockReason.None, check.Reason);
        Assert.True(check.InsideTerritory);
        Assert.True(check.TileFree);
        Assert.Equal(TerritoryIndex, check.TerritoryIndex);
    }

    /// <summary>
    /// Occupancy names the model in the way. The heart and the floor in the fixture are both on
    /// level 11, so this pins the territory to that single level: on a multi-level territory the
    /// same tile is free on levels 10 and 12, which is what
    /// <see cref="CanPlace_keys_occupancy_by_tile_and_height_level_on_a_multi_level_territory"/>
    /// covers (R30 finding 16).
    /// </summary>
    [Fact]
    public void CanPlace_reports_the_model_that_occupies_the_tile()
    {
        _server.Castles[0] = LiveCastle(minHeightLevel: 11, maxHeightLevel: 11);

        var castle = CastlesApi.ForUser(TestPlayer);
        var check = castle.CanPlace(1356453147, new CastleVec3(-1807.5f, 5f, -1707.5f));
        Assert.False(check.Allowed);
        Assert.Equal(CastleBlockReason.TileOccupied, check.Reason);
        Assert.Equal(11, check.HeightLevel);
        Assert.False(check.HeightAmbiguous);
        Assert.Equal("TM_BloodFountain_CastleHeart", check.BlockingPrefabName);
        Assert.Equal(Heart, check.BlockingEntity);
    }

    [Fact]
    public void CanPlace_on_the_multi_level_plot_finds_a_free_level_over_the_occupied_one()
    {
        // Same tile as above, but the live 10-12 band: the heart occupies level 11 only, so the
        // module reports the placement allowed on a level nothing stands on and says the level
        // was its own choice. That is a lower bound on the game's answer, not the game's answer.
        var check = CastlesApi.ForUser(TestPlayer).CanPlace(1356453147, new CastleVec3(-1807.5f, 5f, -1707.5f));

        Assert.True(check.Allowed);
        Assert.True(check.HeightAmbiguous);
        Assert.NotEqual(11, check.HeightLevel);
        Assert.InRange(check.HeightLevel, 10, 12);
    }

    [Fact]
    public void CanPlace_refuses_a_position_outside_the_territory()
    {
        var castle = CastlesApi.ForUser(TestPlayer);
        var check = castle.CanPlace(1356453147, new CastleVec3(-1847.0658f, 5f, -1363.5184f));
        Assert.False(check.Allowed);
        Assert.Equal(CastleBlockReason.OutsideTerritory, check.Reason);
        Assert.False(check.InsideTerritory);
    }

    [Fact]
    public void CanPlace_uses_the_learned_prefab_footprint_when_there_is_one()
    {
        // Single-level territory so the verdict is about the footprint and nothing else; the
        // height band is the subject of the multi-level tests.
        _server.Castles[0] = LiveCastle(minHeightLevel: 11, maxHeightLevel: 11);

        // The runtime learned (-6,-6)..(6,6) for the floor prefab from a placed instance, so a
        // pivot two tiles clear of the heart footprint still overlaps it.
        _server.Footprints[1356453147] = (-6, -6, 6, 6);
        var castle = CastlesApi.ForUser(TestPlayer);
        var check = castle.CanPlace(1356453147, new CastleVec3(CastleGeometry.TileToWorld(2802), 5f, CastleGeometry.TileToWorld(2985)));
        Assert.True(check.FootprintResolved);
        Assert.Equal(CastleBlockReason.TileOccupied, check.Reason);

        // Without the learned footprint the same pivot is a single free tile.
        _server.Footprints.Clear();
        var single = CastlesApi.ForUser(TestPlayer)
            .CanPlace(1356453147, new CastleVec3(CastleGeometry.TileToWorld(2802), 5f, CastleGeometry.TileToWorld(2985)));
        Assert.False(single.FootprintResolved);
        Assert.True(single.Allowed);
    }

    [Fact]
    public void CanPlace_refuses_a_tile_the_mask_says_is_not_buildable()
    {
        _server.BuildableTiles[TerritoryEntity] = new bool[CastleGeometry.BoundsTileCount(MinX, MinY, MaxX, MaxY)];
        var check = CastlesApi.ForUser(TestPlayer)
            .CanPlace(1356453147, new CastleVec3(CastleGeometry.TileToWorld(2700), 5f, CastleGeometry.TileToWorld(2950)));
        Assert.Equal(CastleBlockReason.OutsideBuildableTiles, check.Reason);
        Assert.True(check.BuildableTilesRead);
        Assert.False(check.InsideBuildableTiles);
    }

    // ---- change detector --------------------------------------------------------------------

    [Fact]
    public void ShouldPoll_fires_on_the_first_call_then_once_per_interval()
    {
        Assert.True(CastleWatch.ShouldPoll(1000, long.MinValue));
        Assert.False(CastleWatch.ShouldPoll(1500, 1000));
        Assert.True(CastleWatch.ShouldPoll(2000, 1000));
        Assert.True(CastleWatch.ShouldPoll(500, 1000)); // clock went backwards
    }

    [Fact]
    public void Diff_reports_new_hearts_as_placed_and_missing_ones_as_destroyed()
    {
        var a = LiveCastle();
        var b = LiveCastle(heart: new CastleId(400, 1), territoryIndex: 14);
        var placed = new List<CastleSnapshot>();
        var destroyed = new List<CastleSnapshot>();

        CastleWatch.Diff(new[] { a }, new[] { a, b }, placed, destroyed);
        Assert.Equal(new CastleId(400, 1), Assert.Single(placed).Heart);
        Assert.Empty(destroyed);

        CastleWatch.Diff(new[] { a, b }, new[] { b }, placed, destroyed);
        Assert.Empty(placed);
        Assert.Equal(Heart, Assert.Single(destroyed).Heart);

        // A recycled entity index is a destroy plus a place, not silent continuity.
        var recycled = LiveCastle(heart: new CastleId(327390, 2));
        CastleWatch.Diff(new[] { a }, new[] { recycled }, placed, destroyed);
        Assert.Single(placed);
        Assert.Single(destroyed);
    }

    [Fact]
    public void Poll_publishes_placed_then_destroyed_and_marks_the_first_scan()
    {
        var placed = new List<CastleHeartPlaced>();
        var destroyed = new List<CastleHeartDestroyed>();
        using var a = EventBus.On<CastleHeartPlaced>(placed.Add, owner: _owner);
        using var b = EventBus.On<CastleHeartDestroyed>(destroyed.Add, owner: _owner);

        CastlesApi.TickForTests();
        var first = Assert.Single(placed);
        Assert.True(first.InitialScan);
        Assert.Equal(TerritoryIndex, first.Castle.Territory.Index);
        Assert.Empty(destroyed);

        placed.Clear();
        _server.Castles.Add(LiveCastle(heart: new CastleId(400, 1), territoryIndex: 14));
        CastlesApi.TickForTests();
        var second = Assert.Single(placed);
        Assert.False(second.InitialScan);
        Assert.Equal(14, second.Castle.Territory.Index);

        placed.Clear();
        _server.Castles.RemoveAt(0);
        CastlesApi.TickForTests();
        Assert.Empty(placed);
        Assert.Equal(Heart, Assert.Single(destroyed).Heart);
        Assert.Equal(TerritoryIndex, destroyed[0].TerritoryIndex);
        Assert.Equal("TestPlayer", destroyed[0].Owner.Name);
    }

    [Fact]
    public void Poll_does_nothing_while_nobody_is_subscribed()
    {
        CastlesApi.TickForTests();
        Assert.Equal(0, _server.SnapshotCalls);

        using var sub = EventBus.On<CastleHeartPlaced>(_ => { }, owner: _owner);
        CastlesApi.TickForTests();
        Assert.True(_server.SnapshotCalls > 0);
    }

    // ---- client / skip ----------------------------------------------------------------------

    [Fact]
    public void Client_returns_empty_lists_and_disabled_handles_with_one_log_line()
    {
        CastlesApi.SetRuntimeForTests(new FakeCastleRuntime { IsServer = false });

        Assert.Empty(CastlesApi.All);
        var castle = CastlesApi.ForUser(TestPlayer);
        Assert.False(castle.Enabled);
        Assert.Empty(castle.Buildings());
        Assert.Empty(castle.Tiles());
        Assert.False(castle.IsTilePrecise);
        Assert.False(castle.IsInside(new CastleVec3(-1807.5f, 5f, -1707.5f)));

        var check = castle.CanPlace(1356453147, new CastleVec3(-1807.5f, 5f, -1707.5f));
        Assert.False(check.Allowed);
        Assert.Equal(CastleBlockReason.NoCastle, check.Reason);
        Assert.False(CastlesApi.IsAvailable);
        Assert.False(CastlesApi.At(new CastleVec3(0f, 0f, 0f)).Enabled);
        Assert.Same(castle, castle.Refresh());
    }

    [Fact]
    public void Skipped_module_reads_nothing()
    {
        CastlesApi.SetSkippedForTests(true);
        Assert.Empty(CastlesApi.All);
        Assert.False(CastlesApi.ForUser(TestPlayer).Enabled);
        Assert.False(CastlesApi.IsAvailable);
        Assert.Equal(0, _server.SnapshotCalls);
    }

    [Fact]
    public void Refresh_re_reads_the_castle_and_disables_when_the_heart_is_gone()
    {
        var castle = CastlesApi.ForUser(TestPlayer);
        _server.Castles[0] = LiveCastle(level: 3);
        Assert.Equal((byte)1, castle.Level);
        Assert.Equal((byte)3, castle.Refresh().Level);

        _server.Castles.Clear();
        Assert.False(castle.Refresh().Enabled);
    }

    /// <summary>
    /// R30 finding 10, repaired for R31 finding 2. A handle holds raw <c>Entity.Index</c> /
    /// <c>Version</c> pairs, and a save reload re-assigns them on the same durable castle — the
    /// research recorded heart <c>327390:1</c> coming back as <c>325974:1</c> with territory
    /// index 13, bounds and <c>NetworkId</c> unchanged. A retained handle must not answer from
    /// those ids after the reload; it reports disabled with a reason instead.
    ///
    /// <para>The signal is the liveness of the handle's own two entities at their exact
    /// <c>index:version</c>, <b>not</b> <c>World.SequenceNumber</c>: V Rising loads a save
    /// inside the existing world, so the sequence number cannot move across this event while
    /// the entity versions do. The simulated reload below therefore does what the real one
    /// does — the old ids stop existing — and nothing bumps a world counter.</para>
    /// </summary>
    [Fact]
    public void A_handle_retained_across_a_world_reload_goes_stale_instead_of_reading_reused_ids()
    {
        var castle = CastlesApi.ForUser(TestPlayer);
        Assert.True(castle.Enabled);
        Assert.False(castle.IsStale);
        Assert.Equal("", castle.DisabledReason);
        Assert.NotEmpty(castle.Buildings());

        // The reload: the heart that used to be 327390:1 is now a different entity and the old
        // id is no longer live — while the territory index, the stable key, is unchanged.
        var reloadedHeart = new CastleId(325974, 1);
        _server.Castles.Clear();
        _server.Castles.Add(LiveCastle(heart: reloadedHeart));

        Assert.True(castle.IsStale);
        Assert.False(castle.Enabled);
        Assert.Contains(TerritoryIndex.ToString(), castle.DisabledReason);
        Assert.Contains("stale", castle.DisabledReason, StringComparison.OrdinalIgnoreCase);

        // No live read re-enters the world with the old ids.
        Assert.Empty(castle.Buildings());
        Assert.Empty(castle.Tiles());
        Assert.False(castle.IsTilePrecise);
        Assert.False(castle.IsInside(new CastleVec3(-1807.5f, 5f, -1707.5f)));

        var check = castle.CanPlace(1356453147, new CastleVec3(-1807.5f, 5f, -1707.5f));
        Assert.Equal(CastleBlockReason.NoCastle, check.Reason);
        Assert.Equal(castle.DisabledReason, check.Detail);
        Assert.Equal(TerritoryIndex, check.TerritoryIndex);

        // Refresh resolves through the territory index, so it finds the castle under its new
        // heart id. Keying on the heart (the old behavior) would have found nothing.
        var refreshed = castle.Refresh();
        Assert.True(refreshed.Enabled);
        Assert.False(refreshed.IsStale);
        Assert.Equal(reloadedHeart, refreshed.Heart);
        Assert.NotEqual(castle.Heart, refreshed.Heart);
        Assert.Equal(TerritoryIndex, refreshed.Territory.Index);
        Assert.NotEmpty(refreshed.Buildings());
    }

    /// <summary>
    /// R32 finding 1, job FEPOCH — the case the liveness check <b>cannot</b> see. The research
    /// reload table shows every id at version 1 on both sides of the reload
    /// (heart <c>327390:1 → 325974:1</c>, owner <c>326690:1 → 326205:1</c>, character
    /// <c>324767:1 → 327864:1</c>), which is a load that rebuilds entities from index 0 at
    /// version 1 rather than an <c>EntityManager</c> bumping a recycled index's version. Under
    /// that reading the reloaded world can hand index <c>327390</c>, still at version 1, to a
    /// completely different castle's heart — so <c>Exists(327390:1)</c> answers <b>true</b>, the
    /// liveness check says "live", and the old handle would happily read somebody else's plot
    /// with <c>Enabled == true</c>. The world-load epoch is what refuses it.
    /// </summary>
    [Fact]
    public void A_reload_that_re_issues_the_same_index_and_version_is_still_stale_by_epoch()
    {
        var castle = CastlesApi.ForUser(TestPlayer);
        Assert.True(castle.Enabled);
        Assert.NotEmpty(castle.Buildings());

        // The reload the liveness check is blind to: the *same* 327390:1 and 2032:1 exist
        // afterwards, they just belong to a different castle now (territory 99, not 13).
        Progenitor.WorldState.WorldEpoch.Advance("LoadPersistenceSystemV2.CopyDataFromPrefabs");
        _server.Castles.Clear();
        _server.Castles.Add(LiveCastle(territoryIndex: 99));

        // Proof that the liveness check alone would have said "fine".
        Assert.True(_server.TryIsEntityLive(Heart, out bool heartLive));
        Assert.True(heartLive);

        Assert.True(castle.IsStale);
        Assert.False(castle.Enabled);

        // The reason names both epochs, so an admin can see how far behind the handle is.
        string reason = castle.DisabledReason;
        Assert.Contains("epoch 1", reason);
        Assert.Contains("epoch 2", reason);
        Assert.Contains(TerritoryIndex.ToString(), reason);

        // And no live read re-enters the world with the re-issued ids.
        Assert.Empty(castle.Buildings());
        Assert.Empty(castle.Tiles());
        Assert.False(castle.IsTilePrecise);
    }

    [Fact]
    public void A_handle_taken_after_the_reload_stamps_the_new_epoch_and_is_usable()
    {
        Progenitor.WorldState.WorldEpoch.Advance("LoadPersistenceSystemV2.CopyDataFromPrefabs");

        var castle = CastlesApi.ForUser(TestPlayer);

        Assert.True(castle.Enabled);
        Assert.False(castle.IsStale);
        Assert.Equal("", castle.DisabledReason);
        Assert.NotEmpty(castle.Buildings());
    }

    [Fact]
    public void The_liveness_check_still_catches_a_demolition_inside_one_epoch()
    {
        var castle = CastlesApi.ForUser(TestPlayer);
        Assert.False(castle.IsStale);

        // No load: the epoch does not move. The heart was simply destroyed.
        _server.Castles.Clear();

        Assert.True(castle.IsStale);
        Assert.Equal(1, Progenitor.WorldState.WorldEpoch.Current);
        Assert.Contains("no longer live", castle.DisabledReason);
    }

    [Fact]
    public void A_handle_is_not_stale_while_its_own_entities_are_still_live()
    {
        var castle = CastlesApi.ForUser(TestPlayer);

        // Same entities: the ids mean what they meant, so nothing is refused.
        _server.Castles[0] = LiveCastle(level: 4);
        Assert.False(castle.IsStale);
        Assert.True(castle.Enabled);
        Assert.NotEmpty(castle.Buildings());
        Assert.True(_server.LivenessChecks > 0);
    }

    /// <summary>
    /// R31 finding 15. The old stamp treated an unreadable signal as "cannot be checked" and
    /// kept reading, so a handle taken while the signal was unavailable was never stale again.
    /// Effects settled the same question the other way in R28 finding 26 — an unreadable world
    /// signal is a <i>changed</i> world signal — and this module now matches it: unreadable is
    /// stale, on a fresh handle and on a retained one, and it says so once in the log.
    /// </summary>
    [Fact]
    public void An_unreadable_liveness_signal_fails_closed_and_logs_once()
    {
        CastleHandle.ResetUnreadableLogForTests();

        // Retained handle: it was fine, then the signal goes away.
        var castle = CastlesApi.ForUser(TestPlayer);
        Assert.False(castle.IsStale);

        _server.CanCheckLiveness = false;
        Assert.True(castle.IsStale);
        Assert.False(castle.Enabled);
        Assert.Contains(CastleHandle.UnreadableDetail, castle.DisabledReason);
        Assert.Contains("stale", castle.DisabledReason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(castle.Buildings());
        Assert.Empty(castle.Tiles());

        // And a handle *taken* while the signal is unreadable is stale from the start — the
        // case the old stamp could not express, because it recorded 0 and compared 0 to 0.
        var fresh = CastlesApi.ForUser(TestPlayer);
        Assert.True(fresh.IsStale);
        Assert.False(fresh.Enabled);

        int warned = 0;
        foreach (var line in _log.Warnings)
            if (line.Contains("entity-liveness check", StringComparison.Ordinal)) warned++;
        Assert.Equal(1, warned);

        // The signal comes back and so does the handle: this is a refusal, not a latch.
        _server.CanCheckLiveness = true;
        Assert.False(CastlesApi.ForUser(TestPlayer).IsStale);
        Assert.False(castle.IsStale);
    }

    /// <summary>
    /// R30 finding 16. Occupancy is keyed by (tile, height level): a model on one level does
    /// not occupy the same tile on another, which is how floors and the furniture standing on
    /// them coexist. The live territory spans levels 10-12, and the old code passed -1 for the
    /// whole band, which skipped the level filter and let one floor refuse every level.
    /// </summary>
    [Fact]
    public void CanPlace_keys_occupancy_by_tile_and_height_level_on_a_multi_level_territory()
    {
        var castle = CastlesApi.ForUser(TestPlayer);
        Assert.NotEqual(castle.Territory.MinHeightLevel, castle.Territory.MaxHeightLevel);

        // One tile, two models, on levels 10 and 11. Level 12 is free.
        const int tileX = 2700, tileY = 2950;
        _server.Buildings[Heart] = new List<CastleBuildingInfo>
        {
            OneTileModel(tileX, tileY, heightLevel: 10, netIndex: 40_001),
            OneTileModel(tileX, tileY, heightLevel: 11, netIndex: 40_002),
        };

        var position = new CastleVec3(CastleGeometry.TileToWorld(tileX), 5f, CastleGeometry.TileToWorld(tileY));
        var check = CastlesApi.ForUser(TestPlayer).CanPlace(1356453147, position);

        Assert.True(check.Allowed);
        Assert.Equal(12, check.HeightLevel);
        Assert.True(check.HeightAmbiguous);

        // Fill the last level and the same call is refused, naming the model that took it.
        _server.Buildings[Heart].Add(OneTileModel(tileX, tileY, heightLevel: 12, netIndex: 40_003));
        var full = CastlesApi.ForUser(TestPlayer).CanPlace(1356453147, position);
        Assert.False(full.Allowed);
        Assert.Equal(CastleBlockReason.TileOccupied, full.Reason);
        Assert.True(full.HeightAmbiguous);

        // A neighboring tile is free on every level, and the lowest one answers.
        var free = CastlesApi.ForUser(TestPlayer).CanPlace(
            1356453147,
            new CastleVec3(CastleGeometry.TileToWorld(tileX + 1), 5f, CastleGeometry.TileToWorld(tileY)));
        Assert.True(free.Allowed);
        Assert.Equal(10, free.HeightLevel);
    }

    [Fact]
    public void CanPlace_on_a_single_level_territory_names_that_level_and_is_not_ambiguous()
    {
        _server.Castles[0] = LiveCastle(minHeightLevel: 11, maxHeightLevel: 11);

        var check = CastlesApi.ForUser(TestPlayer).CanPlace(
            1356453147,
            new CastleVec3(CastleGeometry.TileToWorld(2700), 5f, CastleGeometry.TileToWorld(2950)));

        Assert.True(check.Allowed);
        Assert.Equal(11, check.HeightLevel);
        Assert.False(check.HeightAmbiguous);
    }

    private static CastleBuildingInfo OneTileModel(int tileX, int tileY, int heightLevel, int netIndex) => new()
    {
        Entity = new CastleId(netIndex, 1),
        PrefabGuid = 1356453147,
        PrefabName = "TM_Castle_Floor_Outdoor_Plain01",
        TileX = tileX,
        TileY = tileY,
        HeightLevel = heightLevel,
        RotationName = "None",
        FootprintMinX = tileX,
        FootprintMinY = tileY,
        FootprintMaxX = tileX + 1,
        FootprintMaxY = tileY + 1,
        Kind = "decor",
        NetworkIndex = netIndex,
        NetworkGeneration = 1,
    };

    // ---- territory buffer layout (job FCAST2) -----------------------------------------------

    /// <summary>
    /// The element layouts are the dump's:
    /// <code>
    /// 540068  public struct CastleTerritoryBlocks : IBufferElementData { public int2 BlockCoordinate; }        // 0x0
    /// 540076  public struct CastleTerritoryTiles  : IBufferElementData { public bool InsideBuildableTerritory; } // 0x0
    /// </code>
    /// <c>int2</c> is two 4-byte ints (x at 0x0, y at 0x4) so the blocks stride is 8; a
    /// single-field <c>bool</c> struct has no padding so the tiles stride is 1. Neither number
    /// may come from <c>sizeof</c> or <c>TypeManager</c> — that is the EV17 rule.
    ///
    /// <para><b>Three independently obtained numbers (R31 finding 4 of the test-quality
    /// table).</b> The previous version of this test probed the constants <i>through</i>
    /// <c>DecodeBlockCoordinates</c>, which places element <c>i</c> at
    /// <c>ElementOffset(i, length, BlockElementStride)</c> — so the measured stride was
    /// <c>BlockElementStride</c> by construction, for any value of it, and changing the
    /// constant moved both sides together. It is now:</para>
    /// <list type="number">
    /// <item><description><b>the dump</b> — the two struct declarations are read out of the
    /// server metadata's type listing (or, when that file is not on the machine, out of the layout
    /// block the research notes record), and the stride and field offsets are
    /// computed from the <i>field types</i> with a size/alignment table that knows nothing
    /// about this module;</description></item>
    /// <item><description><b>the decoders</b> — sentinel probing, which reports where the walks
    /// actually read;</description></item>
    /// <item><description><b>the constants</b> — the declared values.</description></item>
    /// </list>
    /// <para>All three must agree. Editing <c>BlockElementStride</c> to 12 now fails against the
    /// dump; editing the dump's <c>int2</c> to something else fails against the constants.</para>
    ///
    /// <para><b>Source (1) lives outside this repository, so this test SKIPS when it is absent</b>
    /// (R48 finding 7). Both the metadata and the research notes live outside the repository; a
    /// <c>git archive</c> extraction, a fresh clone or CI has neither, and the test used to
    /// <c>Assert.Fail</c> there — which made <c>dotnet test</c> red from every clean extraction and
    /// poisoned the mutation matrices that jobs run in one (a mutation scored "killed" by "the
    /// suite failed" could not tell its own kill from this). The assertion is "the module's
    /// constants match the dump", and without the dump that question is unanswerable, not false —
    /// so <see cref="DumpBackedFactAttribute"/> skips it with a reason. When either source IS
    /// present the assertion is as hard as it ever was: nothing below is conditional.</para>
    /// </summary>
    /// <summary>
    /// FDOC7 (R49 finding 4). <c>[DumpBackedFact]</c>'s decision is taken in an attribute
    /// constructor, which xunit v2 runs at <em>discovery</em>: a throw there is not a red test but
    /// a lost test class. The environments the skip was written for — a published test bundle, a
    /// dll copied to an artifacts directory — are exactly the ones with no <c>Progenitor.sln</c>
    /// above the binary, which is what the resolution walks for. So the resolution has to answer
    /// "neither source is here", not throw. Driven from a directory with no solution above it,
    /// since the machine running this one has both.
    /// </summary>
    [Fact]
    public void Dump_source_resolution_answers_rather_than_throwing_with_no_solution_above_it()
    {
        string dir = Path.Combine(Path.GetTempPath(), "progenitor-no-sln-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var resolved = CastleDumpSources.Resolve(dir);

            Assert.Contains("DirectoryNotFoundException", resolved.Error, StringComparison.Ordinal);
            Assert.Equal("", resolved.Dump);
            Assert.Equal("", resolved.Research);

            // The shape Available reads, and the reason it hands to Skip: a name of the failure,
            // not a pair of paths nobody looked at.
            Assert.False(resolved.Error.Length == 0
                && (File.Exists(resolved.Dump) || File.Exists(resolved.Research)));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); }
            catch { /* best-effort temp cleanup */ }
        }
    }

    [DumpBackedFact]
    public void Territory_buffer_strides_and_offsets_are_the_ones_the_decoders_walk()
    {
        // (1) The dump. Nothing below reads a CastleTerritoryBuffers constant to get here.
        var blocks = DumpLayout.Of(ReadDumpStruct("CastleTerritoryBlocks"));
        var tiles = DumpLayout.Of(ReadDumpStruct("CastleTerritoryTiles"));

        var blockCoordinate = blocks.Field("BlockCoordinate");
        Assert.Equal("int2", blockCoordinate.Type);
        var tileFlag = tiles.Field("InsideBuildableTerritory");
        Assert.Equal("bool", tileFlag.Type);

        int dumpBlockStride = blocks.Stride;
        int dumpBlockX = blockCoordinate.Offset;
        int dumpBlockY = blockCoordinate.Offset + DumpLayout.SizeOf("int"); // the int2's second component
        int dumpTileStride = tiles.Stride;
        int dumpTileFlag = tileFlag.Offset;

        // (2) The decoders, measured. Blocks: element 1's x field is the first place a sentinel
        // lands in coords[2]; element 0's y field is the first place it lands in coords[1].
        int walkedBlockStride = OffsetWhereBlockSentinelLands(coordinateSlot: 2);
        int walkedBlockY = OffsetWhereBlockSentinelLands(coordinateSlot: 1);
        int walkedBlockX = OffsetWhereBlockSentinelLands(coordinateSlot: 0);
        int walkedTileStride = ByteThatSetsTile(1) - ByteThatSetsTile(0);
        int walkedTileFlag = ByteThatSetsTile(0);

        // (3) The constants. Dump == walk == constant, on every number.
        Assert.Equal(dumpBlockStride, walkedBlockStride);
        Assert.Equal(CastleTerritoryBuffers.BlockElementStride, dumpBlockStride);
        Assert.Equal(dumpBlockX, walkedBlockX);
        Assert.Equal(CastleTerritoryBuffers.BlockCoordinateXOffset, dumpBlockX);
        Assert.Equal(dumpBlockY, walkedBlockY);
        Assert.Equal(CastleTerritoryBuffers.BlockCoordinateYOffset, dumpBlockY);
        Assert.Equal(dumpTileStride, walkedTileStride);
        Assert.Equal(CastleTerritoryBuffers.TileElementStride, dumpTileStride);
        Assert.Equal(dumpTileFlag, walkedTileFlag);
        Assert.Equal(CastleTerritoryBuffers.TileFlagOffset, dumpTileFlag);

        // ElementOffset is what both walks use to place element i (it is no longer a helper
        // nothing calls). Expected offsets are accumulated from the *dump's* stride, not from
        // the constant, and not multiplied.
        int running = 0;
        for (int i = 0; i < 5; i++)
        {
            Assert.Equal(running, CastleTerritoryBuffers.ElementOffset(i, 5, dumpBlockStride));
            running += dumpBlockStride;
        }

        Assert.Equal(-1, CastleTerritoryBuffers.ElementOffset(5, 5, dumpBlockStride));
        Assert.Equal(-1, CastleTerritoryBuffers.ElementOffset(-1, 5, dumpBlockStride));
    }

    /// <summary>
    /// The layout reader is only worth trusting if it can produce a number other than the one
    /// this module happens to declare. A synthetic struct with a <c>bool</c> followed by an
    /// <c>int</c> must come back padded — offset 4 for the int, stride 8 — which neither of the
    /// two real structs would ever give.
    /// </summary>
    [Fact]
    public void The_dump_layout_reader_computes_padding_and_stride_from_field_types()
    {
        var layout = DumpLayout.Of(
            "public struct Synthetic : IBufferElementData\n{\n"
            + "\tpublic bool Flag; // 0x0\n"
            + "\tpublic int Value; // 0x4\n"
            + "}");

        Assert.Equal(0, layout.Field("Flag").Offset);
        Assert.Equal(4, layout.Field("Value").Offset);
        Assert.Equal(8, layout.Stride);

        // And a packed one is not padded: three bytes in a row, stride 3.
        var packed = DumpLayout.Of(
            "public struct Packed { public bool A; public byte B; public sbyte C; }");
        Assert.Equal(2, packed.Field("C").Offset);
        Assert.Equal(3, packed.Stride);
    }

    /// <summary>
    /// Byte offset at which a sentinel int written into an otherwise zero blocks buffer is
    /// decoded into <paramref name="coordinateSlot"/> of the interleaved x/y output. Probes
    /// every offset in a 64-byte buffer; -1 when none does.
    /// </summary>
    private static int OffsetWhereBlockSentinelLands(int coordinateSlot)
    {
        int sentinel = 0x0BADF00D;
        for (int offset = 0; offset + 4 <= 64; offset++)
        {
            var bytes = new byte[64];
            bytes[offset] = (byte)sentinel;
            bytes[offset + 1] = (byte)(sentinel >> 8);
            bytes[offset + 2] = (byte)(sentinel >> 16);
            bytes[offset + 3] = (byte)(sentinel >> 24);

            var coords = CastleTerritoryBuffers.DecodeBlockCoordinates(bytes, 4, maxBlocks: 8);
            if (coords != null && coordinateSlot < coords.Length && coords[coordinateSlot] == sentinel)
                return offset;
        }

        return -1;
    }

    /// <summary>Byte index that turns tile <paramref name="tile"/> on in a decoded mask; -1 when none does.</summary>
    private static int ByteThatSetsTile(int tile)
    {
        for (int at = 0; at < 16; at++)
        {
            var bytes = new byte[16];
            bytes[at] = 1;

            var mask = CastleTerritoryBuffers.DecodeTileMask(bytes, 16, 16);
            if (mask != null && mask[tile])
                return at;
        }

        return -1;
    }

    // ---- the dump as an independent source of the buffer layouts -----------------------------

    /// <summary>
    /// The declaration of one <c>IBufferElementData</c> struct, read from the server metadata the
    /// constants are cited from (<see cref="GameMetadataFiles"/>), or — when the metadata is not on
    /// this machine — from the layout block the research notes record
    /// (<c>castle-module.md</c> under <c>PROGENITOR_LOCAL_EVIDENCE</c>). Fails the
    /// test when a source IS present but does not carry the struct, rather than passing silently:
    /// this test's whole value is that one side of the comparison does not come from the module.
    /// The "neither source exists at all" case never reaches here — <c>[DumpBackedFact]</c> has
    /// already skipped the test (R48 finding 7).
    /// </summary>
    private static string ReadDumpStruct(string structName)
    {
        string dump = CastleDumpSources.Dump;
        string research = CastleDumpSources.Research;
        string opener = "public struct " + structName;

        if (File.Exists(dump))
        {
            var text = new System.Text.StringBuilder();
            int depth = 0;
            bool started = false;
            foreach (var line in File.ReadLines(dump))
            {
                if (!started)
                {
                    if (!line.TrimStart().StartsWith(opener, StringComparison.Ordinal)) continue;
                    started = true;
                }

                text.Append(line).Append('\n');
                foreach (char c in line)
                {
                    if (c == '{') depth++;
                    else if (c == '}') depth--;
                }

                if (started && depth == 0 && text.ToString().Contains('{')) break;
            }

            if (started) return text.ToString();
        }

        if (File.Exists(research))
        {
            foreach (var line in File.ReadLines(research))
            {
                if (line.TrimStart().StartsWith(opener, StringComparison.Ordinal) && line.Contains('{'))
                    return line;
            }
        }

        Assert.Fail("no source for the " + structName + " layout: neither " + dump + " nor a "
            + opener + " line in " + research + ". This test compares the module's constants "
            + "against the dump; without the dump there is nothing to compare against.");
        return "";
    }

    internal static string RepoRootForDumpSources() => FindRepoRootFrom(AppContext.BaseDirectory);

    private static string FindRepoRoot() => FindRepoRootFrom(AppContext.BaseDirectory);

    /// <summary>
    /// Walk up from <paramref name="start"/> to the directory holding <c>Progenitor.sln</c>.
    /// Takes the starting directory so <see cref="CastleDumpSources"/>' failure path can be
    /// tested from a directory that has no solution above it (job FDOC7, R49 finding 4);
    /// production callers pass <see cref="AppContext.BaseDirectory"/>.
    /// </summary>
    internal static string FindRepoRootFrom(string start)
    {
        var dir = new DirectoryInfo(start);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Progenitor.sln"))) return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Progenitor.sln not found from " + start);
    }

    /// <summary>
    /// A struct layout computed from a metadata declaration: field offsets by packing the
    /// declared field <i>types</i>, and the element stride as the end of the last field rounded
    /// up to the struct's largest field alignment. The size/alignment table below is the C# /
    /// Unity.Mathematics one and knows nothing about <c>CastleTerritoryBuffers</c>, which is
    /// what makes it an independent second opinion. A <c>// 0x..</c> offset comment, when the
    /// dump carries one, is cross-checked against the computed offset.
    /// </summary>
    private sealed class DumpLayout
    {
        private static readonly Dictionary<string, (int Size, int Align)> Types =
            new(StringComparer.Ordinal)
            {
                ["bool"] = (1, 1), ["byte"] = (1, 1), ["sbyte"] = (1, 1),
                ["short"] = (2, 2), ["ushort"] = (2, 2), ["char"] = (2, 2),
                ["int"] = (4, 4), ["uint"] = (4, 4), ["float"] = (4, 4),
                ["long"] = (8, 8), ["ulong"] = (8, 8), ["double"] = (8, 8),
                ["int2"] = (8, 4), ["uint2"] = (8, 4), ["float2"] = (8, 4),
                ["int3"] = (12, 4), ["float3"] = (12, 4),
                ["int4"] = (16, 4), ["float4"] = (16, 4),
                ["Entity"] = (8, 4),
            };

        private readonly List<(string Type, string Name, int Offset, int Size)> _fields = new();

        private DumpLayout()
        {
        }

        /// <summary>Element stride: last field end, rounded up to the largest field alignment.</summary>
        public int Stride { get; private set; }

        /// <summary>Size of a primitive by name, from the same table — never from the module.</summary>
        public static int SizeOf(string type) => Types[type].Size;

        public (string Type, string Name, int Offset, int Size) Field(string name)
        {
            foreach (var field in _fields)
                if (string.Equals(field.Name, name, StringComparison.Ordinal))
                    return field;

            throw new InvalidOperationException("no field named " + name + " in the parsed layout");
        }

        public static DumpLayout Of(string declaration)
        {
            int open = declaration.IndexOf('{');
            int close = declaration.LastIndexOf('}');
            if (open < 0 || close <= open)
                throw new InvalidOperationException("not a struct declaration: " + declaration);

            string body = declaration.Substring(open + 1, close - open - 1);
            var layout = new DumpLayout();
            int at = 0, maxAlign = 1;

            foreach (Match m in Regex.Matches(
                body,
                // The trailing group stops at the end of a `// 0x..` comment (or immediately when
                // there is none) so that a one-line declaration's later fields still match.
                @"public\s+(?<type>[A-Za-z0-9_]+)\s+(?<name>[A-Za-z0-9_]+)\s*;(?<trail>[ \t]*(?://[^\r\n]*)?)"))
            {
                string type = m.Groups["type"].Value;
                if (!Types.TryGetValue(type, out var size))
                    throw new InvalidOperationException(
                        "unknown field type '" + type + "' — add it to the table with its real "
                        + "size and alignment rather than guessing.");

                if (size.Align > maxAlign) maxAlign = size.Align;
                at = AlignUp(at, size.Align);

                var stated = Regex.Match(m.Groups["trail"].Value, @"//\s*0x(?<hex>[0-9A-Fa-f]+)");
                if (stated.Success)
                {
                    int declared = Convert.ToInt32(stated.Groups["hex"].Value, 16);
                    if (declared != at)
                        throw new InvalidOperationException(
                            "the dump says " + m.Groups["name"].Value + " is at 0x"
                            + declared.ToString("x") + " but packing its declared types puts it at 0x"
                            + at.ToString("x") + "; the size table is wrong for one of them.");
                }

                layout._fields.Add((type, m.Groups["name"].Value, at, size.Size));
                at += size.Size;
            }

            if (layout._fields.Count == 0)
                throw new InvalidOperationException("no fields parsed from: " + declaration);

            layout.Stride = AlignUp(at, maxAlign);
            return layout;
        }

        private static int AlignUp(int value, int alignment) =>
            alignment <= 1 ? value : ((value + alignment - 1) / alignment) * alignment;
    }

    /// <summary>
    /// R30 finding 7. The blocks length must be bounded by a number that does not come from
    /// the length: a caller sizes its span as <c>length * stride</c>, so a guard that compares
    /// the length against that span can never fire, and a wrong <c>GetBufferLength</c> walks
    /// off the allocation. The bound is the block count the territory's own <c>WorldBounds</c>
    /// allow — computed here by counting the 10 x 10 blocks the AABB touches, independently of
    /// the production expression.
    /// </summary>
    [Fact]
    public void Block_length_is_bounded_by_the_territory_bounds_not_by_its_own_span()
    {
        // Count the distinct 10x10 blocks the live AABB covers, by walking its tiles.
        var seen = new HashSet<(int, int)>();
        for (int y = MinY; y < MaxY; y++)
            for (int x = MinX; x < MaxX; x++)
                seen.Add((x / CastleTerritoryBuffers.BlockTileSize, y / CastleTerritoryBuffers.BlockTileSize));

        int expectedMax = seen.Count;
        Assert.Equal(21 * 13, expectedMax);   // 192 x 112 tiles starting mid-block
        Assert.Equal(expectedMax, CastleTerritoryBuffers.MaxBlocksForBounds(MinX, MinY, MaxX, MaxY));

        // The tautology this replaces: a span that *does* back the claimed length is still
        // refused, because the length is larger than the territory can hold. 40 elements are
        // physically present in these bytes; a 1-block territory may not read them.
        var bytes = new byte[40 * CastleTerritoryBuffers.BlockElementStride];
        int oneBlock = CastleTerritoryBuffers.MaxBlocksForBounds(0, 0, 10, 10);
        Assert.Equal(1, oneBlock);
        Assert.False(CastleTerritoryBuffers.BlockLengthOk(40, oneBlock));
        Assert.Null(CastleTerritoryBuffers.DecodeBlockCoordinates(bytes, 40, oneBlock));

        // Same bytes, a territory big enough for them: read.
        int bigEnough = CastleTerritoryBuffers.MaxBlocksForBounds(0, 0, 400, 10);
        Assert.True(bigEnough >= 40);
        Assert.NotNull(CastleTerritoryBuffers.DecodeBlockCoordinates(bytes, 40, bigEnough));

        // Bounds that did not read give no bound at all, and no bound means no walk — not
        // "an empty buffer".
        Assert.Equal(0, CastleTerritoryBuffers.MaxBlocksForBounds(0, 0, 0, 0));
        Assert.False(CastleTerritoryBuffers.BlockLengthOk(0, 0));
        Assert.Null(CastleTerritoryBuffers.DecodeBlockCoordinates(bytes, 0, 0));

        // Negative tile coordinates floor toward minus infinity, so a block is never split.
        Assert.Equal(2, CastleTerritoryBuffers.MaxBlocksForBounds(-5, 0, 5, 10));

        // And the magnitude ceiling still caps a bounds big enough to exceed it.
        Assert.Equal(CastleTerritoryBuffers.MaxTerritoryBlocks,
            CastleTerritoryBuffers.MaxBlocksForBounds(0, 0, 100_000, 100_000));
    }

    [Fact]
    public void DecodeBlockCoordinates_walks_int2_elements_little_endian()
    {
        // Two int2 elements: (2770, 2970) and (-3, 4). Little-endian, stride 8. The first pair
        // is the live heart's TileBounds min, re-read raw off the server (D20A0000 9A0B0000).
        var bytes = new byte[]
        {
            0xD2, 0x0A, 0x00, 0x00,  0x9A, 0x0B, 0x00, 0x00,
            0xFD, 0xFF, 0xFF, 0xFF,  0x04, 0x00, 0x00, 0x00,
        };

        int maxBlocks = CastleTerritoryBuffers.MaxBlocksForBounds(MinX, MinY, MaxX, MaxY);

        var coords = CastleTerritoryBuffers.DecodeBlockCoordinates(bytes, 2, maxBlocks);
        Assert.NotNull(coords);
        Assert.Equal(new[] { 2770, 2970, -3, 4 }, coords!);

        // A length past the bytes actually mapped, or past the territory's own bound, is refused.
        Assert.Null(CastleTerritoryBuffers.DecodeBlockCoordinates(bytes, 3, maxBlocks));
        Assert.Null(CastleTerritoryBuffers.DecodeBlockCoordinates(bytes, -1, maxBlocks));
        Assert.Null(CastleTerritoryBuffers.DecodeBlockCoordinates(bytes, maxBlocks + 1, maxBlocks));
        Assert.False(CastleTerritoryBuffers.BlockLengthOk(-1, maxBlocks));
        Assert.False(CastleTerritoryBuffers.BlockLengthOk(maxBlocks + 1, maxBlocks));
        Assert.True(CastleTerritoryBuffers.BlockLengthOk(0, maxBlocks));
        Assert.Empty(CastleTerritoryBuffers.DecodeBlockCoordinates(Array.Empty<byte>(), 0, maxBlocks)!);
    }

    [Fact]
    public void DecodeTileMask_is_one_byte_per_tile_and_only_at_the_computed_length()
    {
        int expected = CastleGeometry.BoundsTileCount(MinX, MinY, MaxX, MaxY);
        Assert.Equal(21_504, expected);   // 192 x 112, live territory 13

        var bytes = new byte[expected];
        int heartIndex = CastleGeometry.LocalTileIndex(2785, 2985, MinX, MinY, MaxX, MaxY);
        bytes[heartIndex] = 1;
        bytes[3] = 0x02;   // any non-zero byte is "buildable", not just 1

        var mask = CastleTerritoryBuffers.DecodeTileMask(bytes, expected, expected);
        Assert.NotNull(mask);
        Assert.Equal(expected, mask!.Length);
        Assert.True(mask[heartIndex]);
        Assert.True(mask[3]);
        Assert.False(mask[0]);

        // The length check is an equality against the caller's own tile count: a garbage length
        // cannot pass it, and the handle then answers from the bounding box.
        Assert.False(CastleTerritoryBuffers.TileMaskLengthOk(expected - 1, expected));
        Assert.False(CastleTerritoryBuffers.TileMaskLengthOk(expected, 0));
        Assert.True(CastleTerritoryBuffers.TileMaskLengthOk(expected, expected));
        Assert.Null(CastleTerritoryBuffers.DecodeTileMask(bytes, 7, expected));
        Assert.Null(CastleTerritoryBuffers.DecodeTileMask(new byte[4], expected, expected));
    }

    /// <summary>
    /// The buffer log lines have to carry the numbers an operator needs to tell which side is
    /// wrong — the length the game reported and the bound the module computed. Rather than
    /// comparing the formatter's output to the literal it was handed (R30 test-quality), every
    /// number here is computed from the fixture geometry, and each is checked to actually move
    /// the message: a formatter that drops one, or prints the same number twice, fails.
    /// </summary>
    [Fact]
    public void Territory_buffer_log_lines_carry_both_numbers_and_move_with_them()
    {
        int expectedTiles = CastleGeometry.BoundsTileCount(MinX, MinY, MaxX, MaxY);
        int reportedLength = expectedTiles - 1;

        string fallback = CastleTerritoryBuffers.TileMaskFallbackLog(
            TerritoryEntity.ToString(), reportedLength, expectedTiles);

        Assert.Contains(TerritoryEntity.ToString(), fallback);
        Assert.Contains(reportedLength.ToString(), fallback);
        Assert.Contains(expectedTiles.ToString(), fallback);
        Assert.Contains("AABB", fallback);

        // Each number is really read from its own argument: change one, the line changes.
        Assert.NotEqual(fallback, CastleTerritoryBuffers.TileMaskFallbackLog(
            TerritoryEntity.ToString(), reportedLength + 1, expectedTiles));
        Assert.NotEqual(fallback, CastleTerritoryBuffers.TileMaskFallbackLog(
            TerritoryEntity.ToString(), reportedLength, expectedTiles + 1));

        // The success line is distinguishable from the fallback line — a consumer reading the
        // log must not have to guess which answer IsInside gave.
        int buildable = expectedTiles / 3;
        string read = CastleTerritoryBuffers.TileMaskReadLog(
            TerritoryEntity.ToString(), expectedTiles, buildable);
        Assert.Contains(expectedTiles.ToString(), read);
        Assert.Contains(buildable.ToString(), read);
        Assert.DoesNotContain("AABB", read);
        Assert.NotEqual(fallback, read);

        // The blocks refusal names the bound that refused it, not just the length (finding 7).
        int maxBlocks = CastleTerritoryBuffers.MaxBlocksForBounds(MinX, MinY, MaxX, MaxY);
        string refused = CastleTerritoryBuffers.BlockLengthRefusedLog(
            TerritoryEntity.ToString(), maxBlocks + 5, maxBlocks);
        Assert.Contains((maxBlocks + 5).ToString(), refused);
        Assert.Contains(maxBlocks.ToString(), refused);
        Assert.DoesNotContain(CastleTerritoryBuffers.MaxTerritoryBlocks.ToString(), refused);
    }

    // ---- fixtures ---------------------------------------------------------------------------

    private CastleSnapshot LiveCastle(
        CastleId heart = default,
        int territoryIndex = TerritoryIndex,
        byte level = 1,
        CastleId clan = default,
        string clanName = "",
        byte minHeightLevel = 10,
        byte maxHeightLevel = 12)
    {
        var h = heart.IsNone ? Heart : heart;
        var snapshot = new CastleSnapshot
        {
            Heart = h,
            Territory = new CastleTerritoryInfo
            {
                Index = territoryIndex,
                Entity = TerritoryEntity,
                MinX = MinX,
                MinY = MinY,
                MaxX = MaxX,
                MaxY = MaxY,
                MinHeightLevel = minHeightLevel,
                MaxHeightLevel = maxHeightLevel,
                BlockCount = 0,
                BlocksRead = false,
            },
            Owner = new CastleOwnerInfo
            {
                User = TestPlayer,
                PlatformId = 76561198000000076UL,
                Name = "TestPlayer",
                Clan = clan,
                ClanName = clanName,
            },
            Level = level,
            FloorCount = 2,
            Raided = false,
            Decaying = true,
        };

        _server.Buildings[h] = new List<CastleBuildingInfo>
        {
            new()
            {
                Entity = h,
                PrefabGuid = -485210554,
                PrefabName = "TM_BloodFountain_CastleHeart",
                TileX = 2785,
                TileY = 2985,
                HeightLevel = 11,
                RotationName = "None",
                FootprintMinX = 2770,
                FootprintMinY = 2970,
                FootprintMaxX = 2800,
                FootprintMaxY = 3000,
                Interactable = true,
                Kind = "heart",
                NetworkIndex = 15732,
                NetworkGeneration = 1,
            },
            Floor(2775, 2965, netIndex: 17801),
        };

        return snapshot;
    }

    private static CastleBuildingInfo Floor(int tileX, int tileY, int netIndex) => new()
    {
        Entity = new CastleId(327377, 1),
        PrefabGuid = 1356453147,
        PrefabName = "TM_Castle_Floor_Outdoor_Plain01",
        TileX = tileX,
        TileY = tileY,
        HeightLevel = 11,
        RotationName = "None",
        FootprintMinX = tileX - 6,
        FootprintMinY = tileY - 6,
        FootprintMaxX = tileX + 6,
        FootprintMaxY = tileY + 6,
        Kind = "decor",
        NetworkIndex = netIndex,
        NetworkGeneration = 1,
    };

    private sealed class Owner
    {
        public Owner(string guid) => GUID = guid;

        public string GUID { get; }
    }

    /// <summary>In-memory <see cref="ICastleRuntime"/>. No game, no Unity.</summary>
    internal sealed class FakeCastleRuntime : ICastleRuntime
    {
        public bool IsServer { get; set; }

        /// <summary>
        /// False makes <see cref="TryIsEntityLive"/> answer "cannot be checked", which is what
        /// a missing server world or a throwing <c>Entity.Exists</c> looks like to a handle.
        /// </summary>
        public bool CanCheckLiveness { get; set; } = true;

        /// <summary>
        /// Entities that are live on top of the ones the current <see cref="Castles"/> rows
        /// name. A save reload is simulated by replacing the rows: the ids the old rows carried
        /// stop being live, exactly as the re-assigned <c>Entity.Version</c> makes them.
        /// </summary>
        public HashSet<CastleId> AlsoLive { get; } = new();

        /// <summary>How many liveness questions handles have asked; 0 proves nothing asked.</summary>
        public int LivenessChecks { get; private set; }

        public bool TryIsEntityLive(CastleId entity, out bool live)
        {
            LivenessChecks++;
            live = false;
            if (!CanCheckLiveness) return false;

            if (AlsoLive.Contains(entity))
            {
                live = true;
                return true;
            }

            for (int i = 0; i < Castles.Count; i++)
            {
                if (Castles[i].Heart == entity || Castles[i].Territory.Entity == entity)
                {
                    live = true;
                    return true;
                }
            }

            return true;
        }

        public List<CastleSnapshot> Castles { get; } = new();

        public Dictionary<CastleId, List<CastleBuildingInfo>> Buildings { get; } = new();

        public Dictionary<CastleId, List<CastleTileInfo>> TileRows { get; } = new();

        public Dictionary<CastleId, bool[]> BuildableTiles { get; } = new();

        public Dictionary<CastleId, (int X, int Y, int Height)> TilePositions { get; } = new();

        public Dictionary<CastleId, CastleVec3> Positions { get; } = new();

        public Dictionary<CastleId, CastleId> Clans { get; } = new();

        public Dictionary<int, (int MinX, int MinY, int MaxX, int MaxY)> Footprints { get; } = new();

        public int SnapshotCalls { get; private set; }

        IReadOnlyList<CastleSnapshot> ICastleRuntime.Snapshot()
        {
            SnapshotCalls++;
            return Castles.ToArray();
        }

        IReadOnlyList<CastleBuildingInfo> ICastleRuntime.Buildings(CastleId heart) =>
            Buildings.TryGetValue(heart, out var rows) ? rows.ToArray() : Array.Empty<CastleBuildingInfo>();

        IReadOnlyList<CastleTileInfo> ICastleRuntime.Tiles(CastleId heart) =>
            TileRows.TryGetValue(heart, out var rows) ? rows.ToArray() : Array.Empty<CastleTileInfo>();

        public bool TryGetBuildableTiles(CastleId territory, int expectedTileCount, out bool[] tiles)
        {
            if (BuildableTiles.TryGetValue(territory, out var mask) && mask.Length == expectedTileCount)
            {
                tiles = mask;
                return true;
            }

            tiles = Array.Empty<bool>();
            return false;
        }

        public bool TryGetTilePosition(CastleId entity, out int tileX, out int tileY, out int heightLevel)
        {
            if (TilePositions.TryGetValue(entity, out var row))
            {
                tileX = row.X;
                tileY = row.Y;
                heightLevel = row.Height;
                return true;
            }

            tileX = 0;
            tileY = 0;
            heightLevel = -1;
            return false;
        }

        public bool TryGetPosition(CastleId entity, out CastleVec3 position) =>
            Positions.TryGetValue(entity, out position);

        public bool TryGetPrefabFootprint(int prefabGuid, out int localMinX, out int localMinY, out int localMaxX, out int localMaxY)
        {
            if (Footprints.TryGetValue(prefabGuid, out var box))
            {
                localMinX = box.MinX;
                localMinY = box.MinY;
                localMaxX = box.MaxX;
                localMaxY = box.MaxY;
                return true;
            }

            localMinX = 0;
            localMinY = 0;
            localMaxX = 1;
            localMaxY = 1;
            return false;
        }

        public bool TryGetClan(CastleId user, out CastleId clan) =>
            Clans.TryGetValue(user, out clan);

        public Dictionary<CastleId, List<CastleInventoryRow>> InventoryRows { get; } = new();

        public Dictionary<CastleId, string> HeartNames { get; } = new();

        public Dictionary<CastleId, CastleHeartFuel> Fuel { get; } = new();

        IReadOnlyList<CastleInventoryRow> ICastleRuntime.Inventories(CastleId heart) =>
            InventoryRows.TryGetValue(heart, out var rows) ? rows.ToArray() : Array.Empty<CastleInventoryRow>();

        public readonly Dictionary<CastleId, long> Revisions = new();

        public long StructureRevision(CastleId heart) => Revisions.TryGetValue(heart, out var r) ? r : 0;

        public string HeartName(CastleId heart) => HeartNames.TryGetValue(heart, out var n) ? n : "";

        public CastleHeartFuel HeartFuel(CastleId heart) =>
            Fuel.TryGetValue(heart, out var f) ? f : CastleHeartFuel.Fail("no fuel row in the fake");
    }
}

/// <summary>
/// Where the two external sources of the Il2Cpp buffer layouts live, and whether this machine has
/// either of them (job FDOC6, R48 finding 7).
///
/// <para>Both sit <em>outside</em> this repository — the server metadata's type listing
/// (<see cref="GameMetadataFiles"/>) and <c>castle-module.md</c> in the research notes
/// (<c>PROGENITOR_LOCAL_EVIDENCE</c>); neither is committed here. A
/// <c>git archive</c> extraction, a fresh clone and CI therefore all have neither, and a test
/// whose one independent source is missing has an unanswerable question in front of it rather
/// than a false one.</para>
///
/// <para>Resolution is cached for the run: the paths cannot change while the suite executes, and
/// the attribute below is constructed once per decorated test at discovery.</para>
///
/// <para><b>Nothing here throws</b> (job FDOC7, R49 finding 4). Resolution starts at
/// <c>AppContext.BaseDirectory</c> and walks up for <c>Progenitor.sln</c>, which is absent from a
/// published test bundle or a dll copied to an artifacts directory — the same kind of environment
/// the skip exists for. Because <see cref="Resolved"/> is read from the attribute's constructor,
/// and xunit v2 constructs attributes during <em>discovery</em>, a throw there is not a red test:
/// it drops the declaring class or reports against the assembly, and a CI log shows only "fewer
/// tests ran". A failed resolution therefore becomes another missing source, with the failure
/// text in the skip reason.</para>
/// </summary>
internal static class CastleDumpSources
{
    private static readonly Lazy<(string Dump, string Research, string Error)> Resolved = new(() =>
        Resolve(AppContext.BaseDirectory));

    /// <summary>
    /// Locate both sources from <paramref name="baseDirectory"/>. Never throws: an unresolvable
    /// repo root comes back as two empty paths plus the failure text, which
    /// <see cref="Available"/> reads as "neither source is here".
    /// </summary>
    internal static (string Dump, string Research, string Error) Resolve(string baseDirectory)
    {
        try
        {
            // The metadata sits beside the main repo (<workspace>) unless PROGENITOR_METADATA_DIR
            // says otherwise; the research notes are found only through PROGENITOR_LOCAL_EVIDENCE.
            string root = CastlesTests.FindRepoRootFrom(baseDirectory);
            string notes = Environment.GetEnvironmentVariable("PROGENITOR_LOCAL_EVIDENCE") ?? "";
            return (
                GameMetadataFiles.TypeListing(root, "server"),
                notes.Length == 0 ? "" : Path.Combine(notes, "castle-module.md"),
                "");
        }
        catch (Exception e)
        {
            return ("", "", e.GetType().Name + ": " + e.Message);
        }
    }

    /// <summary>Path the server dump would be at, whether or not it exists.</summary>
    internal static string Dump => Resolved.Value.Dump;

    /// <summary>Path the research doc would be at, whether or not it exists.</summary>
    internal static string Research => Resolved.Value.Research;

    /// <summary>Why the two paths could not be worked out, or "" when they were.</summary>
    internal static string ResolveError => Resolved.Value.Error;

    /// <summary>True when at least one of the two is on this machine.</summary>
    internal static bool Available =>
        ResolveError.Length == 0 && (File.Exists(Dump) || File.Exists(Research));

    /// <summary>The skip reason, naming both paths that were looked for.</summary>
    internal static string MissingReason =>
        ResolveError.Length > 0
            ? "needs an external source of the Il2Cpp layout, and this machine could not be asked "
              + "where they would be: " + ResolveError + ". Both sources live outside this "
              + "repository by design; run this test from a working copy beside "
              + @"<workspace>\_tools, or set PROGENITOR_METADATA_DIR or PROGENITOR_LOCAL_EVIDENCE."
            : "needs an external source of the Il2Cpp layout, and neither is on this machine: no "
              + Dump + " and no " + Research + ". Both live outside this repository by design, so a "
              + "git archive extraction, a fresh clone and CI all skip this test; run it from a working "
              + @"copy beside <workspace>\_tools, or set PROGENITOR_METADATA_DIR or PROGENITOR_LOCAL_EVIDENCE.";
}

/// <summary>
/// A <see cref="FactAttribute"/> that skips its test, with the reason, when neither external
/// layout source is present (job FDOC6, R48 finding 7).
///
/// <para><b>Why a custom attribute and not a bare <c>return</c>.</b> R40 already ruled on that in
/// <c>DeployPackageTests</c>: a test that returns early on a missing precondition reports green
/// while asserting nothing. xunit 2.9 has no dynamic <c>Assert.Skip</c> (that is v3) and this repo
/// takes no <c>SkippableFact</c> dependency, so the mechanism available in v2 is a
/// <c>FactAttribute</c> subclass that sets <see cref="FactAttribute.Skip"/> at construction. The
/// test then reports as <b>skipped with a reason</b> in every runner and in CI, which is the
/// honest third answer between green and red.</para>
///
/// <para>The check runs at discovery, once per decorated test. It is a file-existence check only;
/// a source that exists but does not carry the struct is still a hard failure inside the test.</para>
///
/// <para><b>The constructor cannot throw</b> (job FDOC7, R49 finding 4). xunit v2 builds
/// attributes while discovering tests, so an exception here is not one red test — it is a
/// discovery error that drops this class, or reports against the assembly, and reads in CI as
/// "fewer tests ran". <see cref="CastleDumpSources.Resolve"/> already answers instead of throwing;
/// the catch below is the second belt, because a future source of the answer must not be able to
/// make an unrelated test class disappear.</para>
/// </summary>
internal sealed class DumpBackedFactAttribute : FactAttribute
{
    public DumpBackedFactAttribute()
    {
        try
        {
            if (!CastleDumpSources.Available) Skip = CastleDumpSources.MissingReason;
        }
        catch (Exception e)
        {
            Skip = "could not decide whether the external Il2Cpp layout sources are present: " + e;
        }
    }
}
