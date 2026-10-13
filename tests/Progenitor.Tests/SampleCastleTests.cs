using System;
using System.Collections.Generic;
using Progenitor.Castles;
using Progenitor.Sample;
using Xunit;
using CastlesApi = Progenitor.Castles.Castles;

namespace Progenitor.Tests;

/// <summary>
/// The exact <c>.castle</c> reply lines, over the same live fixture
/// (<see cref="CastlesTests"/>): territory 13, heart 327390:1, owner TestPlayer 326690:1, bounds
/// 2639,2899..2831,3011, a blood-fountain heart and one outdoor floor. Pinning the strings is
/// the point — the live acceptance run in <c>docs/bridge-ops.md</c> compares against them.
/// </summary>
[Collection("PluginLogger")]
public class SampleCastleTests : IDisposable
{
    private static readonly CastleId Heart = new(327390, 1);
    private static readonly CastleId TerritoryEntity = new(2032, 1);
    private static readonly CastleId TestPlayer = new(326690, 1);

    private readonly TestLogSource _log = new();
    private readonly CastlesTests.FakeCastleRuntime _server = new() { IsServer = true };

    public SampleCastleTests()
    {
        Plugin.Logger = _log;
        CastlesApi.ResetForTests();
        _server.Castles.Add(LiveCastle());
        CastlesApi.SetRuntimeForTests(_server);
    }

    public void Dispose()
    {
        CastlesApi.ResetForTests();
        Plugin.Logger = null;
    }

    // `expected` is the int value of SampleCastle.Kind: the enum is internal, so it cannot
    // appear in a public test method's signature (CS0051).
    [Theory]
    [InlineData("", 1)]
    [InlineData("   ", 1)]
    [InlineData("summary", 1)]
    [InlineData("inside", 2)]
    [InlineData("INSIDE", 2)]
    [InlineData("in", 2)]
    public void TryParse_accepts_the_documented_arguments(string token, int expected)
    {
        Assert.True(SampleCastle.TryParse(token, out var kind));
        Assert.Equal(expected, (int)kind);
    }

    [Fact]
    public void TryParse_refuses_anything_else()
    {
        Assert.False(SampleCastle.TryParse("outside", out _));
        Assert.False(SampleCastle.TryParse("13", out _));
    }

    [Fact]
    public void Summary_prints_the_plot_line_then_the_buildings_by_prefab()
    {
        var lines = SampleCastle.Run(TestPlayer, SampleCastle.Kind.Summary);
        Assert.Equal(2, lines.Count);
        Assert.Equal(
            "Castle #13 lv1 — TestPlayer — heart 327390:1, territory 2639,2899..2831,3011 (192×112 tiles, 0 blocks), floors 2 [decaying, bounds-only]",
            lines[0]);
        Assert.Equal(
            "Buildings (2): TM_BloodFountain_CastleHeart×1, TM_Castle_Floor_Outdoor_Plain01×1",
            lines[1]);
    }

    [Fact]
    public void Summary_says_so_when_the_caller_owns_nothing()
    {
        var lines = SampleCastle.Run(new CastleId(9, 1), SampleCastle.Kind.Summary);
        Assert.Equal(SampleCastle.NoCastleReply, Assert.Single(lines));
    }

    [Fact]
    public void Inside_reports_the_tile_and_which_test_answered()
    {
        var inside = SampleCastle.Run(TestPlayer, SampleCastle.Kind.Inside, new CastleVec3(-1807.5f, 5f, -1707.5f));
        Assert.Equal(
            "Inside castle #13 — you are at tile 2785,2985, checked against the territory bounds.",
            Assert.Single(inside));

        // TestPlayer's real position while this job ran: tile 2705,3672, past MaxY 3011.
        var outside = SampleCastle.Run(TestPlayer, SampleCastle.Kind.Inside, new CastleVec3(-1847.0658f, 5f, -1363.5184f));
        Assert.Equal(
            "Outside castle #13 — you are at tile 2705,3672, territory is 2639,2899..2831,3011 (checked against the territory bounds).",
            Assert.Single(outside));
    }

    [Fact]
    public void Inside_names_the_tile_mask_when_the_territory_buffer_was_readable()
    {
        int count = CastleGeometry.BoundsTileCount(2639, 2899, 2831, 3011);
        var mask = new bool[count];
        mask[CastleGeometry.LocalTileIndex(2785, 2985, 2639, 2899, 2831, 3011)] = true;
        _server.BuildableTiles[TerritoryEntity] = mask;

        var inside = SampleCastle.Run(TestPlayer, SampleCastle.Kind.Inside, new CastleVec3(-1807.5f, 5f, -1707.5f));
        Assert.Equal(
            "Inside castle #13 — you are at tile 2785,2985, checked against the buildable-tile mask.",
            Assert.Single(inside));
    }

    [Fact]
    public void Inside_says_so_when_the_caller_owns_nothing()
    {
        var lines = SampleCastle.Run(new CastleId(9, 1), SampleCastle.Kind.Inside, new CastleVec3(0f, 0f, 0f));
        Assert.Equal(SampleCastle.NoCastleReply, Assert.Single(lines));
    }

    [Fact]
    public void Prefab_rows_are_capped_with_a_more_tail()
    {
        var rows = new List<KeyValuePair<string, int>>();
        for (int i = 0; i < 12; i++)
            rows.Add(new KeyValuePair<string, int>("P" + i.ToString("00"), 12 - i));

        string text = CastleCounts.Format(rows, SampleCastle.MaxPrefabRows);
        Assert.StartsWith("P00×12, P01×11", text, StringComparison.Ordinal);
        Assert.EndsWith(", +4 more", text, StringComparison.Ordinal);
        Assert.Equal("none", CastleCounts.Format(Array.Empty<KeyValuePair<string, int>>()));
    }

    [Fact]
    public void Client_side_castle_command_replies_with_the_no_castle_line()
    {
        CastlesApi.SetRuntimeForTests(new CastlesTests.FakeCastleRuntime { IsServer = false });
        Assert.Equal(SampleCastle.NoCastleReply, Assert.Single(SampleCastle.Run(TestPlayer, SampleCastle.Kind.Summary)));
        Assert.Equal(SampleCastle.NoCastleReply, Assert.Single(SampleCastle.Run(TestPlayer, SampleCastle.Kind.Inside)));
    }

    // ---- subcommands added by job J6 (one worked example per api.md Castles row) --------------

    [Theory]
    [InlineData("all", 3)]
    [InlineData("ALL", 3)]
    [InlineData("tiles", 5)]
    [InlineData("raw", 7)]
    public void TryParse_accepts_the_argument_free_subcommands(string token, int expected)
    {
        Assert.True(SampleCastle.TryParse(token, out var kind, out int territory, out string prefab));
        Assert.Equal(expected, (int)kind);
        Assert.Equal(0, territory);
        Assert.Equal("", prefab);
    }

    [Fact]
    public void TryParse_reads_the_territory_index_and_the_prefab_token()
    {
        Assert.True(SampleCastle.TryParse("territory 13", out var kind, out int territory, out _));
        Assert.Equal(4, (int)kind);
        Assert.Equal(13, territory);

        Assert.True(SampleCastle.TryParse("canplace TM_Castle_Floor_Outdoor_Plain01", out kind, out _, out string prefab));
        Assert.Equal(6, (int)kind);
        Assert.Equal("TM_Castle_Floor_Outdoor_Plain01", prefab);
    }

    [Fact]
    public void TryParse_refuses_a_missing_or_extra_argument()
    {
        Assert.False(SampleCastle.TryParse("territory", out _, out _, out _));
        Assert.False(SampleCastle.TryParse("territory twelve", out _, out _, out _));
        Assert.False(SampleCastle.TryParse("canplace", out _, out _, out _));
        Assert.False(SampleCastle.TryParse("all 13", out _, out _, out _));
        Assert.False(SampleCastle.TryParse("tiles now", out _, out _, out _));
    }

    [Fact]
    public void All_prints_one_row_per_plot_then_the_owned_and_standing_in_line()
    {
        var lines = SampleCastle.Run(TestPlayer, SampleCastle.Kind.All);
        Assert.Equal(3, lines.Count);
        Assert.Equal("Castles (1) via Castles.All():", lines[0]);
        Assert.Equal(
            "  #13 lv1 — TestPlayer — heart 327390:1, territory 2639,2899..2831,3011, floors 2 [decaying]",
            lines[1]);
        Assert.Equal(
            "You own 1 of these (Castles.AllForUser); you are not standing in any castle (Castles.At).",
            lines[2]);
    }

    [Fact]
    public void All_names_the_castle_the_caller_stands_in()
    {
        var lines = SampleCastle.Run(TestPlayer, SampleCastle.Kind.All, new CastleVec3(-1807.5f, 5f, -1707.5f));
        Assert.Equal(
            "You own 1 of these (Castles.AllForUser); you are standing in castle #13 (Castles.At).",
            lines[2]);
    }

    [Fact]
    public void All_caps_the_rows_with_a_more_tail()
    {
        for (int i = 0; i < 8; i++)
            _server.Castles.Add(ExtraCastle(100 + i));

        var lines = SampleCastle.Run(TestPlayer, SampleCastle.Kind.All);
        Assert.Equal("Castles (9) via Castles.All():", lines[0]);
        Assert.Equal(SampleCastle.MaxPlotRows + 3, lines.Count);
        Assert.Equal("  +3 more.", lines[lines.Count - 2]);
        Assert.StartsWith("You own 9 of these", lines[lines.Count - 1], StringComparison.Ordinal);
    }

    [Fact]
    public void All_says_so_when_the_server_holds_no_castle()
    {
        _server.Castles.Clear();
        Assert.Equal(SampleCastle.NoCastlesReply, Assert.Single(SampleCastle.Run(TestPlayer, SampleCastle.Kind.All)));
    }

    [Fact]
    public void Territory_prints_the_snapshot_then_the_refreshed_one()
    {
        var lines = SampleCastle.Run(TestPlayer, SampleCastle.Kind.Territory, default, 13);
        Assert.Equal(4, lines.Count);
        Assert.Equal("ForTerritory(13) → castle #13 [2639,2899..2831,3011] h10-12 heart 327390:1 of TestPlayer lv1", lines[0]);
        Assert.Equal("Refresh() → castle #13 [2639,2899..2831,3011] h10-12 heart 327390:1 of TestPlayer lv1", lines[1]);
        Assert.Equal("ForHeart(327390:1) → castle #13, the same plot this session only", lines[2]);
        Assert.Equal(
            "Persist the territory index, not the heart: 327390:1 is re-assigned by a save reload, 13 is not.",
            lines[3]);
    }

    [Fact]
    public void Territory_reports_the_disabled_handle_for_an_unclaimed_index()
    {
        var line = Assert.Single(SampleCastle.Run(TestPlayer, SampleCastle.Kind.Territory, default, 99));
        Assert.Equal(
            "No castle in territory 99: ForTerritory returned a disabled handle "
            + "(no castle (client side, module skipped, or the handle is disabled)).",
            line);
    }

    [Fact]
    public void Tiles_prints_the_count_the_floor_types_and_the_first_rows()
    {
        _server.TileRows[Heart] = TileRows();

        var lines = SampleCastle.Run(TestPlayer, SampleCastle.Kind.Tiles);
        Assert.Equal(2, lines.Count);
        Assert.Equal("Tiles (6) on castle #13 — floor types: Regular×4, Treasury×2", lines[0]);
        Assert.Equal(
            "First 4: 2785,2985 h11 Regular; 2786,2985 h11 Regular; 2787,2985 h11 Regular; "
            + "2788,2985 h11 Regular; +2 more",
            lines[1]);
    }

    [Fact]
    public void Tiles_says_so_when_no_floor_rows_came_back()
    {
        var line = Assert.Single(SampleCastle.Run(TestPlayer, SampleCastle.Kind.Tiles));
        Assert.Equal(
            "Tiles: none on castle #13 — the plot has no floors, or the floor rows were not readable.",
            line);
    }

    [Fact]
    public void Tiles_says_so_when_the_caller_owns_nothing()
    {
        Assert.Equal(
            SampleCastle.NoCastleReply,
            Assert.Single(SampleCastle.Run(new CastleId(9, 1), SampleCastle.Kind.Tiles)));
    }

    [Fact]
    public void CanPlace_prints_the_verdict_the_footprint_and_the_not_checked_caveat()
    {
        var lines = SampleCastle.Run(
            TestPlayer, SampleCastle.Kind.CanPlace, new CastleVec3(-1807.5f, 5f, -1707.5f), 0, 1356453147);

        Assert.Equal(SampleCastle.MaxNotCheckedRows + 4, lines.Count);
        Assert.Equal(
            "CanPlace(1356453147) at tile 2785,2985 in castle #13: allowed (partial) — "
            + "no refusal from the checks this module performs",
            lines[0]);
        Assert.Equal(
            "Footprint 2785,2985..2786,2986 h10 — footprint resolved no, tile mask read no, height ambiguous yes.",
            lines[1]);
        Assert.Equal("Complete=no — 11 vanilla rules are not checked:", lines[2]);
        for (int i = 0; i < SampleCastle.MaxNotCheckedRows; i++)
            Assert.Equal("  - " + CastlePlacement.NotChecked[i], lines[3 + i]);
        Assert.Equal("  +8 more in CastlePlacementCheck.NotChecked.", lines[lines.Count - 1]);
    }

    [Fact]
    public void CanPlace_names_the_model_that_occupies_the_footprint()
    {
        // Squeeze the territory to the heart's own height level so the band walk has nowhere
        // free to go: the reply then carries the occupancy refusal rather than "allowed".
        _server.Castles.Clear();
        _server.Castles.Add(SingleLevelCastle());

        var lines = SampleCastle.Run(
            TestPlayer, SampleCastle.Kind.CanPlace, new CastleVec3(-1807.5f, 5f, -1707.5f), 0, 1356453147);
        Assert.Equal(
            "CanPlace(1356453147) at tile 2785,2985 in castle #13: blocked: TileOccupied — "
            + "occupied by TM_BloodFountain_CastleHeart 327390:1",
            lines[0]);
        Assert.Equal(
            "Footprint 2785,2985..2786,2986 h11 — footprint resolved no, tile mask read no, height ambiguous no.",
            lines[1]);
    }

    [Fact]
    public void CanPlace_says_so_when_the_caller_owns_nothing()
    {
        Assert.Equal(
            SampleCastle.NoCastleReply,
            Assert.Single(SampleCastle.Run(new CastleId(9, 1), SampleCastle.Kind.CanPlace, default, 0, 1)));
    }

    [Fact]
    public void Unknown_prefab_names_the_token_and_what_is_accepted()
    {
        Assert.Equal(
            "Unknown prefab 'nope': pass a numeric PrefabGUID hash, or a spawnable name this server can resolve.",
            SampleCastle.UnknownPrefabReply("nope"));
    }

    [Fact]
    public void Raw_prints_the_reader_row_counts()
    {
        var rows = new List<SampleCastle.RawRow>
        {
            new(13, 17, 24, 0, true),
            new(14, 0, 1, 96, false),
        };

        var lines = SampleCastle.RawReply(rows, 5);
        Assert.Equal(4, lines.Count);
        Assert.Equal("CastleReader.Plots(): 5 plot(s); rows read for 2.", lines[0]);
        Assert.Equal("  plot #13 — 17 tile row(s), 24 building row(s), 0 block(s) (blocks skipped: yes).", lines[1]);
        Assert.Equal("  plot #14 — 0 tile row(s), 1 building row(s), 96 block(s) (blocks skipped: no).", lines[2]);
        Assert.Equal("  +3 more not read.", lines[3]);
    }

    /// <summary>
    /// The <c>PlotOf</c> round trip is appended after the rows, so the existing shape of
    /// <c>.castle raw</c> is unchanged when there is nothing to look up.
    /// </summary>
    [Fact]
    public void Raw_appends_the_PlotOf_line_when_there_is_one()
    {
        var rows = new List<SampleCastle.RawRow> { new(13, 17, 24, 0, true) };

        var without = SampleCastle.RawReply(rows, 1);
        Assert.Equal(2, without.Count);

        var with = SampleCastle.RawReply(rows, 1, SampleCastle.PlotOfLine("building 'TM_Castle_Chest01'", 13, 13));
        Assert.Equal(3, with.Count);
        Assert.Equal(without[0], with[0]);
        Assert.Equal(without[1], with[1]);
        Assert.Contains("PlotOf", with[2], StringComparison.Ordinal);
    }

    /// <summary>
    /// Equality is the pass condition: <c>PlotOf</c> is the inverse of <c>Buildings(plot)</c>, so
    /// a row read off plot #13 must come back as plot #13. The other two branches exist because
    /// null and a mismatch are both things the world can really answer.
    /// </summary>
    [Theory]
    [InlineData(13, 13, "the plot that row was read from")]
    [InlineData(13, 0, "-> null")]
    [InlineData(13, 14, "which is NOT the #13")]
    public void The_PlotOf_line_says_which_of_the_three_answers_came_back(int from, int resolved, string expected)
    {
        string line = SampleCastle.PlotOfLine("building 'TM_Castle_Chest01'", from, resolved);

        Assert.Contains("building 'TM_Castle_Chest01'", line, StringComparison.Ordinal);
        Assert.Contains(expected, line, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_plot_says_there_was_nothing_to_ask_about_rather_than_failing()
    {
        Assert.Contains("no building row to ask about", SampleCastle.NoPlotOfSubjectReply, StringComparison.Ordinal);
        // The three kinds PlotOf accepts, named where a reader of the reply will see them.
        Assert.Contains("heart, a territory entity, or any placed tile-model", SampleCastle.NoPlotOfSubjectReply, StringComparison.Ordinal);
    }

    [Fact]
    public void Raw_says_so_when_the_reader_found_no_plots()
    {
        Assert.Equal(SampleCastle.NoRawPlotsReply, Assert.Single(SampleCastle.RawReply(null, 0)));
        Assert.Equal(SampleCastle.NoRawPlotsReply, Assert.Single(SampleCastle.Run(TestPlayer, SampleCastle.Kind.Raw)));
    }

    [Fact]
    public void Client_side_subcommands_reply_with_the_disabled_lines()
    {
        CastlesApi.SetRuntimeForTests(new CastlesTests.FakeCastleRuntime { IsServer = false });
        Assert.Equal(SampleCastle.NoCastlesReply, Assert.Single(SampleCastle.Run(TestPlayer, SampleCastle.Kind.All)));
        Assert.Equal(SampleCastle.NoCastleReply, Assert.Single(SampleCastle.Run(TestPlayer, SampleCastle.Kind.Tiles)));
        Assert.StartsWith(
            "No castle in territory 13: ForTerritory returned a disabled handle",
            Assert.Single(SampleCastle.Run(TestPlayer, SampleCastle.Kind.Territory, default, 13)),
            StringComparison.Ordinal);
    }

    private static List<CastleTileInfo> TileRows()
    {
        var rows = new List<CastleTileInfo>();
        for (int i = 0; i < 4; i++)
        {
            rows.Add(new CastleTileInfo
            {
                Entity = new CastleId(400000 + i, 1),
                X = 2785 + i,
                Y = 2985,
                HeightLevel = 11,
                FloorType = 1,
                FloorTypeName = "Regular",
            });
        }

        for (int i = 0; i < 2; i++)
        {
            rows.Add(new CastleTileInfo
            {
                Entity = new CastleId(410000 + i, 1),
                X = 2785 + i,
                Y = 2986,
                HeightLevel = 11,
                FloorType = 14,
                FloorTypeName = "Treasury",
            });
        }

        return rows;
    }

    private CastleSnapshot ExtraCastle(int index) => new()
    {
        Heart = new CastleId(500000 + index, 1),
        Territory = new CastleTerritoryInfo { Index = index, Entity = new CastleId(600000 + index, 1) },
        Owner = new CastleOwnerInfo { User = TestPlayer, Name = "TestPlayer" },
    };

    private CastleSnapshot SingleLevelCastle() => new()
    {
        Heart = Heart,
        Territory = new CastleTerritoryInfo
        {
            Index = 13,
            Entity = TerritoryEntity,
            MinX = 2639,
            MinY = 2899,
            MaxX = 2831,
            MaxY = 3011,
            MinHeightLevel = 11,
            MaxHeightLevel = 11,
        },
        Owner = new CastleOwnerInfo { User = TestPlayer, Name = "TestPlayer" },
        Level = 1,
        FloorCount = 2,
        Decaying = true,
    };

    private CastleSnapshot LiveCastle()
    {
        var snapshot = new CastleSnapshot
        {
            Heart = Heart,
            Territory = new CastleTerritoryInfo
            {
                Index = 13,
                Entity = TerritoryEntity,
                MinX = 2639,
                MinY = 2899,
                MaxX = 2831,
                MaxY = 3011,
                MinHeightLevel = 10,
                MaxHeightLevel = 12,
            },
            Owner = new CastleOwnerInfo
            {
                User = TestPlayer,
                PlatformId = 76561198000000076UL,
                Name = "TestPlayer",
            },
            Level = 1,
            FloorCount = 2,
            Decaying = true,
        };

        _server.Buildings[Heart] = new List<CastleBuildingInfo>
        {
            new()
            {
                Entity = Heart,
                PrefabGuid = -485210554,
                PrefabName = "TM_BloodFountain_CastleHeart",
                TileX = 2785,
                TileY = 2985,
                HeightLevel = 11,
                FootprintMinX = 2770,
                FootprintMinY = 2970,
                FootprintMaxX = 2800,
                FootprintMaxY = 3000,
                Kind = "heart",
                NetworkIndex = 15732,
            },
            new()
            {
                Entity = new CastleId(327377, 1),
                PrefabGuid = 1356453147,
                PrefabName = "TM_Castle_Floor_Outdoor_Plain01",
                TileX = 2775,
                TileY = 2965,
                HeightLevel = 11,
                FootprintMinX = 2769,
                FootprintMinY = 2959,
                FootprintMaxX = 2781,
                FootprintMaxY = 2971,
                Kind = "decor",
                NetworkIndex = 17801,
            },
        };

        return snapshot;
    }
}
