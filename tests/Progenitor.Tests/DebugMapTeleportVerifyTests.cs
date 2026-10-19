using System;
using System.Collections.Generic;
using Progenitor.Players;
using Progenitor.Terrain;
using Xunit;
using static Progenitor.Debugging.DebugMapPolicy;

namespace Progenitor.Tests;

/// <summary>
/// The 2026-10-19 castle teleport bugs, game-free: inside castle territory the map teleport walks down the game's
/// height levels to the ground floor instead of the roof (<see cref="PickFloor"/>, <see cref="GroundProbeLevels"/>), a
/// teleport is reported only when a read-back found the character there, and the position write is never used to force
/// a player onto a roof the game refused (<see cref="PlayerTeleportPolicy"/>, <see cref="WriteAllowed"/>).
/// </summary>
public class DebugMapTeleportVerifyTests
{
    /// <summary>
    /// A stand-in for the game's height read, modeled on its native body: the probe Y becomes a level
    /// (<see cref="GroundProbeLevels.LevelOf"/>), the cell's layer index is <c>level - baseLevel</c> clamped to 0 and to
    /// the last layer, and the answer is that layer's stored height (null for an empty layer). Records every probe
    /// height it was asked.
    /// </summary>
    private static Func<float, float?> Cell(List<float> asked, int baseLevel, params float?[] layers) => probeY =>
    {
        asked.Add(probeY);
        int index = GroundProbeLevels.LevelOf(probeY) - baseLevel;
        index = Math.Max(0, Math.Min(layers.Length - 1, index));
        return layers[index];
    };

    /// <summary>The user's castle on 2026-10-19: floor 5.07 on level 11, the roof 15.5 on level 13.</summary>
    private static Func<float, float?> LiveCastle(List<float> asked, float? level12 = null) =>
        Cell(asked, 11, 5.07f, level12, 15.5f);

    private static readonly GroundBand LiveBand = new(11, 11);

    // ---- the level formula ------------------------------------------------------------------------

    [Fact]
    public void The_height_read_quantizes_the_probe_into_five_meter_levels()
    {
        // (y + 1 + 50) / 5, truncated, clamped to 0..20.
        Assert.Equal(11, GroundProbeLevels.LevelOf(5.07f));
        Assert.Equal(11, GroundProbeLevels.LevelOf(5f)); // a castle floor at y 5 is TilePosition.HeightLevel 11
        Assert.Equal(13, GroundProbeLevels.LevelOf(15.5f));
        Assert.Equal(10, GroundProbeLevels.LevelOf(0f));
        Assert.Equal(11, GroundProbeLevels.LevelOf(4f)); // band edge: (4 + 51) / 5 = 11
        Assert.Equal(10, GroundProbeLevels.LevelOf(3.99f));
        Assert.Equal(20, GroundProbeLevels.LevelOf(200f));
        Assert.Equal(20, GroundProbeLevels.LevelOf(float.NaN));
        Assert.Equal(0, GroundProbeLevels.LevelOf(-60f));
    }

    [Fact]
    public void One_meter_under_the_roof_is_still_the_roof_level()
    {
        // The #163 walk probed 1 m under the floor it found: 14.5 is level 13, the roof's own level.
        Assert.Equal(GroundProbeLevels.LevelOf(15.5f), GroundProbeLevels.LevelOf(15.5f - FloorLayerGap));
        var asked = new List<float>();
        Assert.Equal(15.5f, LiveCastle(asked)(14.5f));
    }

    [Fact]
    public void Every_level_has_a_probe_height_that_reads_exactly_that_level()
    {
        for (int level = GroundProbeLevels.MinLevel; level <= GroundProbeLevels.MaxLevel; level++)
            Assert.Equal(level, GroundProbeLevels.LevelOf(GroundProbeLevels.ProbeYFor(level)));
        Assert.Equal(6.5f, GroundProbeLevels.ProbeYFor(11)); // middle of [4, 9)
    }

    // ---- the floor --------------------------------------------------------------------------------

    [Fact]
    public void Inside_a_castle_the_roof_ten_meters_over_the_floor_is_passed_for_the_ground_floor()
    {
        // The live case after #163: chosen 15.5, floor 5.07. The level 12 layer is empty here.
        var asked = new List<float>();
        var floor = PickFloor(LiveCastle(asked), preferGround: true, LiveBand);
        Assert.True(floor.Found);
        Assert.Equal(5.07f, floor.Y);
        Assert.Equal(15.5f, floor.Top);
        Assert.Equal(2, floor.Layers);
        Assert.Equal(ProbeHeight, asked[0]);
        // Then whole levels: 13 (the roof again), 12 (empty), 11 (the floor), and the band stops the walk.
        Assert.Equal(new[] { 13, 12, 11 }, asked.GetRange(1, asked.Count - 1).ConvertAll(GroundProbeLevels.LevelOf));
        Assert.Equal(asked.Count, floor.Reads);
    }

    [Theory]
    [InlineData(5.07f)]  // the level 12 layer repeats the floor under it
    [InlineData(15.5f)]  // the level 12 layer repeats the roof (answers from above the level asked: skipped)
    [InlineData(-50f)]   // a zero stored height (GroundProbe turns it into a miss; here a real low value, far below)
    public void Whatever_the_level_between_answers_the_walk_ends_on_the_floor(float level12)
    {
        var floor = PickFloor(LiveCastle(new List<float>(), level12), preferGround: true, LiveBand);
        Assert.Equal(5.07f, floor.Y);
    }

    [Fact]
    public void Without_the_band_the_walk_takes_the_lowest_floor_within_the_drop()
    {
        var floor = PickFloor(LiveCastle(new List<float>()), preferGround: true);
        Assert.Equal(5.07f, floor.Y);
    }

    [Fact]
    public void The_band_keeps_the_walk_from_going_under_the_ground_floor()
    {
        // Terrain 3 m under the castle floor, on level 10: with the band the walk stops at the floor.
        var cell = Cell(new List<float>(), 10, 2f, 5.07f, null, 15.5f);
        Assert.Equal(5.07f, PickFloor(cell, preferGround: true, LiveBand).Y);
        // Without it the lowest wins, which is why the castle path passes the band.
        Assert.Equal(2f, PickFloor(cell, preferGround: true).Y);
    }

    [Fact]
    public void Outside_a_castle_the_top_layer_is_used_as_before_with_one_read()
    {
        var asked = new List<float>();
        var floor = PickFloor(LiveCastle(asked), preferGround: false);
        Assert.Equal(15.5f, floor.Y);
        Assert.Equal(1, floor.Layers);
        Assert.Equal(1, floor.Reads);
        Assert.Single(asked);
    }

    [Fact]
    public void A_single_floor_in_a_castle_stays_on_that_floor()
    {
        var floor = PickFloor(Cell(new List<float>(), 11, 5f), preferGround: true, LiveBand);
        Assert.Equal(5f, floor.Y);
        Assert.Equal(1, floor.Layers);
        Assert.Equal(1, floor.Reads); // the top is already on the ground band
    }

    [Fact]
    public void Several_stacked_levels_end_on_the_lowest()
    {
        var floor = PickFloor(Cell(new List<float>(), 11, 5f, 10f, 15.5f, 20f), preferGround: true, LiveBand);
        Assert.Equal(5f, floor.Y);
        Assert.Equal(20f, floor.Top);
        Assert.Equal(4, floor.Layers);
    }

    [Fact]
    public void A_slab_just_under_the_floor_is_not_a_layer()
    {
        // Terrain 20 cm under the castle floor slab, on the level below, is the same floor.
        var floor = PickFloor(Cell(new List<float>(), 10, 3.9f, 4.1f, 15.5f), preferGround: true);
        Assert.Equal(4.1f, floor.Y);
    }

    [Fact]
    public void A_read_that_ignores_the_probe_height_ends_on_the_top_layer_after_a_bounded_walk()
    {
        int calls = 0;
        var floor = PickFloor(_ => { calls++; return 15.5f; }, preferGround: true, LiveBand);
        Assert.Equal(15.5f, floor.Y);
        // The top read plus one read per level from 13 down to the level 30 m under the top (7).
        int levels = GroundProbeLevels.LevelOf(15.5f) - GroundProbeLevels.LevelOf(15.5f - MaxFloorDrop) + 1;
        Assert.Equal(1 + levels, calls);
        Assert.Equal(calls, floor.Reads);
    }

    [Fact]
    public void The_walk_is_bounded_in_depth_and_in_layers()
    {
        // A floor far under the top (a cave, the void) is not taken.
        var deep = PickFloor(Cell(new List<float>(), 3, -20f, null, null, null, null, null, null, null, null, null, 15.5f),
            preferGround: true);
        Assert.Equal(15.5f, deep.Y);

        // A floor on every level from 0 to 20: the walk keeps at most MaxFloorLayers.
        var every = new float?[21];
        for (int i = 0; i < every.Length; i++) every[i] = i * 5f - 50f + 1f;
        var floor = PickFloor(Cell(new List<float>(), 0, every), preferGround: true);
        Assert.Equal(MaxFloorLayers, floor.Layers);
    }

    [Fact]
    public void No_ground_at_all_is_not_found()
    {
        Assert.False(PickFloor(_ => null, preferGround: true).Found);
        Assert.False(PickFloor(_ => float.NaN, preferGround: false).Found);
        Assert.False(PickFloor(_ => throw new InvalidOperationException("read threw"), preferGround: true).Found);
    }

    [Fact]
    public void A_miss_on_one_level_does_not_end_the_walk()
    {
        var floor = PickFloor(Cell(new List<float>(), 11, 5f, null, null, 20.5f), preferGround: true, LiveBand);
        Assert.Equal(5f, floor.Y);
    }

    // ---- the fallback rules -----------------------------------------------------------------------

    [Fact]
    public void A_castle_roof_is_never_a_write_target_and_the_ground_floor_is()
    {
        var walk = WalkFloors(LiveCastle(new List<float>()));
        Assert.Equal(new[] { 15.5f, 5.07f }, walk.Floors);
        Assert.False(WriteAllowed(15.5f, walk, castle: true, LiveBand));
        Assert.True(WriteAllowed(5.07f, walk, castle: true, LiveBand));
        // Without the band nothing in a castle is a known floor.
        Assert.False(WriteAllowed(5.07f, walk, castle: true, ground: null));
    }

    [Fact]
    public void The_live_roof_request_now_refuses_rather_than_forcing_the_roof()
    {
        // The #163 log: castle=yes, chosen 15.5, the walk found nothing lower. The game's helper refused it.
        var walk = new Walk(new[] { 15.5f }, 1);
        var candidates = Candidates(15.5f, walk, castle: true, LiveBand);
        Assert.Single(candidates);
        Assert.False(candidates[0].WriteAllowed);
        var step = PlayerTeleportPolicy.AfterHelperAt(PlayerTeleportPolicy.Arrival.NotArrived, 0, Allowed(candidates));
        Assert.Equal(PlayerTeleportPolicy.Next.Refused, step.Next);
        Assert.Equal(-1, step.Index);
        Assert.Contains("left where they were", PlayerTeleportPolicy.NoKnownFloorWhy(1));
    }

    [Fact]
    public void A_refused_floor_tries_the_next_lower_floor_with_the_game_teleport_first()
    {
        // Chosen on an upper floor (say the band stop failed): roof 15.5 chosen, ground floor under it.
        var walk = WalkFloors(LiveCastle(new List<float>()));
        var candidates = Candidates(15.5f, walk, castle: true, LiveBand);
        Assert.Equal(2, candidates.Count);
        Assert.Equal(5.07f, candidates[1].Y);
        var allowed = Allowed(candidates);

        var first = PlayerTeleportPolicy.AfterHelperAt(PlayerTeleportPolicy.Arrival.NotArrived, 0, allowed);
        Assert.Equal(PlayerTeleportPolicy.Next.HelperOnNext, first.Next);
        Assert.Equal(1, first.Index);

        // The game takes the lower floor: done there, no write.
        var arrived = PlayerTeleportPolicy.AfterHelperAt(PlayerTeleportPolicy.Arrival.Arrived, 1, allowed);
        Assert.Equal(PlayerTeleportPolicy.Next.Done, arrived.Next);
        Assert.Equal(1, arrived.Index);

        // The game refuses that too: the write goes to the first known floor, the ground floor, never the roof.
        var write = PlayerTeleportPolicy.AfterHelperAt(PlayerTeleportPolicy.Arrival.NotArrived, 1, allowed);
        Assert.Equal(PlayerTeleportPolicy.Next.WritePosition, write.Next);
        Assert.Equal(1, write.Index);
    }

    [Fact]
    public void Outside_a_castle_only_the_lowest_floor_may_be_written()
    {
        var ground = WalkFloors(Cell(new List<float>(), 10, 2.6f));
        Assert.True(WriteAllowed(2.6f, ground, castle: false, ground: null));

        // A bridge or a ruin's roof over the ground: the top is not a write target, the ground under it is.
        var stacked = WalkFloors(Cell(new List<float>(), 10, 2.6f, null, 12f));
        var candidates = Candidates(12f, stacked, castle: false, ground: null);
        Assert.Equal(new[] { false, true }, Allowed(candidates));
    }

    [Fact]
    public void An_unreadable_position_or_a_throwing_helper_never_forces_an_unknown_floor()
    {
        Assert.Equal(PlayerTeleportPolicy.Next.Refused,
            PlayerTeleportPolicy.AfterHelperAt(PlayerTeleportPolicy.Arrival.Unknown, 0, new[] { true, true }).Next);
        Assert.Equal(PlayerTeleportPolicy.Next.Refused, PlayerTeleportPolicy.AfterHelperThrew(new[] { false }).Next);
        var threw = PlayerTeleportPolicy.AfterHelperThrew(new[] { false, true });
        Assert.Equal(PlayerTeleportPolicy.Next.WritePosition, threw.Next);
        Assert.Equal(1, threw.Index);
    }

    [Fact]
    public void The_method_token_says_what_ran()
    {
        const string G = "TeleportUtilityServer.Teleport", W = "Translation+LastTranslation+PlayerLastValidPosition";
        Assert.Equal(G, PlayerTeleportPolicy.MethodToken(G, 1, true, W, false));
        Assert.Equal(G + " x2", PlayerTeleportPolicy.MethodToken(G, 2, true, W, false));
        Assert.Equal(G + " x2 refused", PlayerTeleportPolicy.MethodToken(G, 2, false, W, false));
        Assert.Equal(PlayerTeleportPolicy.HelperThenWrite(G, W), PlayerTeleportPolicy.MethodToken(G, 1, false, W, true));
        Assert.Equal(W, PlayerTeleportPolicy.MethodToken(G, 0, false, W, true));
    }

    private static bool[] Allowed(IReadOnlyList<Candidate> candidates)
    {
        var a = new bool[candidates.Count];
        for (int i = 0; i < a.Length; i++) a[i] = candidates[i].WriteAllowed;
        return a;
    }

    [Fact]
    public void The_search_carries_the_floors_into_the_readback()
    {
        var spot = FindNearestValid(-1473.2f, -729.2f, DefaultRadius, DefaultStep, (px, pz) =>
        {
            var f = PickFloor(LiveCastle(new List<float>()), preferGround: true, LiveBand);
            return Probe.Ok(f.Y, f.Top);
        });
        Assert.Equal(5.07f, spot.Y);
        Assert.Equal(15.5f, spot.Top);
        Assert.Equal(1, spot.Probes);
        var candidates = Candidates(spot.Y, WalkFloors(LiveCastle(new List<float>())), castle: true, LiveBand);
        string line = Readback("Ash", Result.Teleported, -1473.2f, -729.2f, spot, "TeleportUtilityServer.Teleport", false,
            castle: true, verify: "arrived", floors: candidates, reads: 13);
        Assert.Contains("chosen (-1473.2, 5.1, -729.2)", line);
        Assert.Contains(" castle=yes", line);
        Assert.Contains(" top=15.5", line);
        Assert.Contains(" floors=5.1*", line);
        Assert.Contains(" probes=1 reads=13", line);
        Assert.Contains(" verify=arrived", line);
        Assert.DoesNotContain("landed=", line);

        string lower = Readback("Ash", Result.Teleported, 0f, 0f, spot, "TeleportUtilityServer.Teleport" + " x2", false,
            castle: true, verify: "arrived", floors: Candidates(15.5f, WalkFloors(LiveCastle(new List<float>())), true, LiveBand),
            landed: 5.07f);
        Assert.Contains(" floors=15.5>5.1*", lower);
        Assert.Contains(" landed=5.1", lower);
    }

    // ---- the read-back ------------------------------------------------------------------------------

    [Fact]
    public void A_helper_that_left_the_player_in_place_did_not_arrive()
    {
        // The live case: the player stayed at (-303.88, 15, -581.21) after a castle request.
        var a = PlayerTeleportPolicy.Check(-303.88f, 15f, -581.21f, -1465.2f, 15.5f, -720.6f, -303.88f, 15f, -581.21f);
        Assert.Equal(PlayerTeleportPolicy.Arrival.NotArrived, a);
        Assert.Equal(PlayerTeleportPolicy.Next.WritePosition, PlayerTeleportPolicy.AfterHelper(a, fallbackAllowed: true));
        Assert.Equal(PlayerTeleportPolicy.Next.Refused, PlayerTeleportPolicy.AfterHelper(a, fallbackAllowed: false));
    }

    [Fact]
    public void Landing_at_the_target_arrives_and_needs_no_write()
    {
        var a = PlayerTeleportPolicy.Check(-303.9f, 15f, -581.2f, -1465.2f, 5f, -720.6f, -1465.0f, 5.02f, -720.4f);
        Assert.Equal(PlayerTeleportPolicy.Arrival.Arrived, a);
        Assert.Equal(PlayerTeleportPolicy.Next.Done, PlayerTeleportPolicy.AfterHelper(a, true));
    }

    [Fact]
    public void Landing_then_walking_on_still_counts_but_a_short_hop_does_not_get_the_allowance()
    {
        // Long trip, landed and ran 6 m in the read-back second.
        Assert.Equal(PlayerTeleportPolicy.Arrival.Arrived,
            PlayerTeleportPolicy.Check(0f, 0f, 0f, 500f, 5f, 0f, 506f, 5f, 0f));
        // The same 6 m off after a 10 m hop could be a player who never moved: not arrived.
        Assert.Equal(PlayerTeleportPolicy.Arrival.NotArrived,
            PlayerTeleportPolicy.Check(0f, 0f, 0f, 10f, 0f, 0f, 4f, 0f, 0f));
        // Right X/Z but on the roof 10 m up: not arrived.
        Assert.Equal(PlayerTeleportPolicy.Arrival.NotArrived,
            PlayerTeleportPolicy.Check(0f, 0f, 0f, 500f, 5f, 0f, 500f, 15.5f, 0f));
    }

    [Fact]
    public void An_unreadable_position_is_unknown_and_never_success()
    {
        var a = PlayerTeleportPolicy.Check(0f, 0f, 0f, 100f, 0f, 0f, null, null, null);
        Assert.Equal(PlayerTeleportPolicy.Arrival.Unknown, a);
        Assert.Equal(PlayerTeleportPolicy.Arrival.Unknown, PlayerTeleportPolicy.Check(0f, 0f, 0f, 1f, 1f, 1f, float.NaN, 0f, 0f));
        Assert.NotEqual(PlayerTeleportPolicy.Next.Done, PlayerTeleportPolicy.AfterHelper(a, false));
        Assert.Equal(PlayerTeleportPolicy.Next.Refused, PlayerTeleportPolicy.AfterWrite(a));
    }

    [Fact]
    public void After_the_write_only_an_arrival_is_success()
    {
        Assert.Equal(PlayerTeleportPolicy.Next.Done, PlayerTeleportPolicy.AfterWrite(PlayerTeleportPolicy.Arrival.Arrived));
        Assert.Equal(PlayerTeleportPolicy.Next.Refused, PlayerTeleportPolicy.AfterWrite(PlayerTeleportPolicy.Arrival.NotArrived));
        Assert.Equal("TeleportUtilityServer.Teleport refused; Translation+LastTranslation+PlayerLastValidPosition",
            PlayerTeleportPolicy.HelperThenWrite("TeleportUtilityServer.Teleport", "Translation+LastTranslation+PlayerLastValidPosition"));
    }

    // ---- the honest result -------------------------------------------------------------------------

    [Fact]
    public void A_refused_spot_is_never_called_a_teleport()
    {
        Assert.False(IsTeleported(Result.Refused));
        Assert.False(IsTeleported(Result.TeleportFailed));
        Assert.True(IsTeleported(Result.Teleported));
        Assert.True(IsTeleported(Result.Moved));
        string notice = Notice(Result.Refused, 0f, 100f);
        Assert.StartsWith("Couldn't teleport there", notice);
        Assert.DoesNotContain("Teleported", notice);
        Assert.Equal(9, (int)Result.Refused); // on the wire
    }
}
