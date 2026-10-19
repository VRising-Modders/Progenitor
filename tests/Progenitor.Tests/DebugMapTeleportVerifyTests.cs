using System;
using System.Collections.Generic;
using Progenitor.Players;
using Xunit;
using static Progenitor.Debugging.DebugMapPolicy;

namespace Progenitor.Tests;

/// <summary>
/// The 2026-10-19 castle teleport bug, game-free: inside castle territory the map teleport selects the base gameplay
/// floor instead of the roof (<see cref="PickFloor"/>), and a teleport is reported only when a read-back found the
/// character there (<see cref="PlayerTeleportPolicy"/>).
/// </summary>
public class DebugMapTeleportVerifyTests
{
    // Match the native level selection and per-cell clamping, rather than raycast semantics.
    private static Func<float, float?> Layers(List<float> asked, params float[] layers) => probeY =>
    {
        asked.Add(probeY);
        int level = (int)Math.Clamp((probeY + 51f) / 5f, 0f, 20f);
        int baseLevel = (int)((layers[0] + 51f) / 5f);
        return layers[Math.Clamp(level - baseLevel, 0, layers.Length - 1)];
    };

    [Theory]
    [InlineData(5f, 10f, 15.5f)]
    [InlineData(25f, 30f, 35.5f)]
    [InlineData(-10f, -5f, 0.5f)]
    public void Castle_base_read_avoids_roof_on_elevated_and_low_terrain(float ground, float upper, float roof)
    {
        var asked = new List<float>();
        var reader = Layers(asked, ground, upper, roof);
        Assert.Equal(roof, reader(roof - 1f)); // the old one-meter descent returns the roof again
        asked.Clear();
        var floor = PickFloor(reader, true);
        Assert.True(floor.Found);
        Assert.Equal(ground, floor.Y);
        Assert.Equal(roof, floor.Top);
        Assert.Equal(new[] { ProbeHeight, BaseLayerProbeHeight }, asked);
    }

    [Fact]
    public void Outside_castles_preserves_top_layer()
    {
        var asked = new List<float>();
        var floor = PickFloor(Layers(asked, 5f, 10f, 15.5f), false);
        Assert.Equal(15.5f, floor.Y);
        Assert.Single(asked);
    }

    [Fact]
    public void Single_raised_floor_remains_at_its_real_height()
    {
        var floor = PickFloor(Layers(new List<float>(), 25f), true);
        Assert.True(floor.Found);
        Assert.Equal(25f, floor.Y);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    [InlineData(-41f)]
    public void Invalid_base_is_refused_without_roof_fallback(float? missing)
    {
        Assert.False(PickFloor(y => y == ProbeHeight ? 15.5f : missing, true).Found);
        Assert.False(PickFloor(_ => missing, false).Found);
    }

    [Fact]
    public void The_search_carries_the_top_layer_into_the_readback()
    {
        var spot = FindNearestValid(-1465.2f, -720.6f, DefaultRadius, DefaultStep, (px, pz) =>
        {
            var f = PickFloor(Layers(new List<float>(), 5f, 10f, 15.5f), preferGround: true);
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
