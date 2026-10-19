using System;
using System.Collections.Generic;
using Progenitor.Players;
using Xunit;
using static Progenitor.Debugging.DebugMapPolicy;

namespace Progenitor.Tests;

/// <summary>
/// The 2026-10-19 castle teleport bug, game-free: inside castle territory the map teleport walks down to the ground
/// floor instead of the roof (<see cref="PickFloor"/>), and a teleport is reported only when a read-back found the
/// character there (<see cref="PlayerTeleportPolicy"/>).
/// </summary>
public class DebugMapTeleportVerifyTests
{
    /// <summary>
    /// A stand-in for the game's height read: the highest of <paramref name="layers"/> at or below the probe height,
    /// or null. Records every probe height it was asked.
    /// </summary>
    private static Func<float, float?> Layers(List<float> asked, params float[] layers) => probeY =>
    {
        asked.Add(probeY);
        float? best = null;
        foreach (float l in layers)
            if (l <= probeY && (!best.HasValue || l > best.Value)) best = l;
        return best;
    };

    // ---- the floor --------------------------------------------------------------------------------

    [Fact]
    public void Inside_a_castle_the_roof_over_the_floor_is_passed_for_the_ground_floor()
    {
        // The live case: a top layer at 15.5 over the castle floor at 5.
        var asked = new List<float>();
        var floor = PickFloor(Layers(asked, 5f, 15.5f), preferGround: true);
        Assert.True(floor.Found);
        Assert.Equal(5f, floor.Y);
        Assert.Equal(15.5f, floor.Top);
        Assert.Equal(2, floor.Layers);
        Assert.Equal(ProbeHeight, asked[0]);
        Assert.Equal(15.5f - FloorLayerGap, asked[1]);
    }

    [Fact]
    public void Outside_a_castle_the_top_layer_is_used_as_before_with_one_read()
    {
        var asked = new List<float>();
        var floor = PickFloor(Layers(asked, 5f, 15.5f), preferGround: false);
        Assert.Equal(15.5f, floor.Y);
        Assert.Equal(1, floor.Layers);
        Assert.Single(asked);
    }

    [Fact]
    public void A_single_floor_in_a_castle_stays_on_that_floor()
    {
        var floor = PickFloor(Layers(new List<float>(), 15f), preferGround: true);
        Assert.Equal(15f, floor.Y);
        Assert.Equal(1, floor.Layers);
    }

    [Fact]
    public void Several_stacked_layers_end_on_the_lowest()
    {
        var floor = PickFloor(Layers(new List<float>(), 5f, 10f, 15.5f, 20f), preferGround: true);
        Assert.Equal(5f, floor.Y);
        Assert.Equal(20f, floor.Top);
        Assert.Equal(4, floor.Layers);
    }

    [Fact]
    public void A_slab_just_under_the_floor_is_not_a_layer()
    {
        // Terrain 20 cm under the castle floor slab is within the layer gap and is never probed into.
        var floor = PickFloor(Layers(new List<float>(), 4.8f, 5f), preferGround: true);
        Assert.Equal(5f, floor.Y);
    }

    [Fact]
    public void A_read_that_ignores_the_probe_height_stops_the_walk_on_the_top_layer()
    {
        // A height read that always answers the same floor, whatever it is asked: the walk must not loop or descend.
        int calls = 0;
        var floor = PickFloor(_ => { calls++; return 15.5f; }, preferGround: true);
        Assert.Equal(15.5f, floor.Y);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void The_walk_is_bounded_in_depth_and_in_layers()
    {
        // A layer far under the top (a cave, the void) is not taken.
        var deep = PickFloor(Layers(new List<float>(), 15.5f - MaxFloorDrop - 5f, 15.5f), preferGround: true);
        Assert.Equal(15.5f, deep.Y);

        // A layer every 2 m: the walk stops after MaxFloorLayers reads under the top.
        var many = new List<float>();
        for (int i = 0; i < 20; i++) many.Add(i * 2f);
        var asked = new List<float>();
        var floor = PickFloor(Layers(asked, many.ToArray()), preferGround: true);
        Assert.Equal(MaxFloorLayers, floor.Layers);
        Assert.Equal(MaxFloorLayers, asked.Count);
    }

    [Fact]
    public void No_ground_at_all_is_not_found()
    {
        Assert.False(PickFloor(_ => null, preferGround: true).Found);
        Assert.False(PickFloor(_ => float.NaN, preferGround: false).Found);
    }

    [Fact]
    public void The_search_carries_the_top_layer_into_the_readback()
    {
        var spot = FindNearestValid(-1465.2f, -720.6f, DefaultRadius, DefaultStep, (px, pz) =>
        {
            var f = PickFloor(Layers(new List<float>(), 5f, 15.5f), preferGround: true);
            return Probe.Ok(f.Y, f.Top);
        });
        Assert.Equal(5f, spot.Y);
        Assert.Equal(15.5f, spot.Top);
        string line = Readback("Ash", Result.Teleported, -1465.2f, -720.6f, spot, "TeleportUtilityServer.Teleport", false,
            castle: true, verify: "arrived");
        Assert.Contains("chosen (-1465.2, 5.0, -720.6)", line);
        Assert.Contains(" castle=yes", line);
        Assert.Contains(" top=15.5", line);
        Assert.Contains(" verify=arrived", line);
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
