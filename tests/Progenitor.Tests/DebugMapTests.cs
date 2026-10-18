using System;
using System.Runtime.InteropServices;
using Progenitor.Debugging;
using Progenitor.Ui;
using Xunit;
using static Progenitor.Debugging.DebugMapPolicy;

namespace Progenitor.Tests;

/// <summary>
/// MAPTP1: the debug buff's map powers, game-free (<see cref="DebugMapPolicy"/>): the nearest-valid
/// search, the admin+buff gate and pushed state, the client's hold-to-teleport rules, the wire and the
/// lines. The coordinate math and the reveal leases are <c>WorldMapMathTests</c>; the map keybind scope is
/// <c>KeybindMapScopeTests</c>.
/// </summary>
public class DebugMapTests
{
    // ---- search ----------------------------------------------------------------------------------

    private static Func<float, float, Probe> Everywhere(float y = 7f) => (_, _) => Probe.Ok(y);

    [Fact]
    public void A_valid_point_is_used_as_is()
    {
        var spot = FindNearestValid(100f, -50f, DefaultRadius, DefaultStep, Everywhere(12f));
        Assert.True(spot.Found);
        Assert.Equal(100f, spot.X);
        Assert.Equal(-50f, spot.Z);
        Assert.Equal(12f, spot.Y);
        Assert.Equal(0f, spot.Distance);
        Assert.Equal(1, spot.Probes);
    }

    [Fact]
    public void Inside_a_wall_the_search_finds_the_nearest_spot_outside_it()
    {
        // A round "rock" of radius 9 m around the point: the nearest valid ground is just past 9 m.
        const float cx = 500f, cz = 300f;
        Probe Probe(float x, float z) =>
            Distance(cx, cz, x, z) < 9.0 ? DebugMapPolicy.Probe.No("not walkable") : DebugMapPolicy.Probe.Ok(3f);
        var spot = FindNearestValid(cx, cz, DefaultRadius, DefaultStep, Probe);
        Assert.True(spot.Found);
        Assert.InRange(spot.Distance, 9f, 9f + DefaultStep + 0.01f);
        Assert.Equal("not walkable", spot.Why);
        Assert.True(Distance(cx, cz, spot.X, spot.Z) >= 9.0);
    }

    [Fact]
    public void Water_on_one_side_sends_you_to_the_nearer_shore()
    {
        // Water everywhere west of x = 1015; the point is at x = 1000, so the shore is ~15 m east.
        Probe Probe(float x, float z) => x < 1015f ? DebugMapPolicy.Probe.No("no ground") : DebugMapPolicy.Probe.Ok(0f);
        var spot = FindNearestValid(1000f, 0f, DefaultRadius, DefaultStep, Probe);
        Assert.True(spot.Found);
        Assert.True(spot.X >= 1015f);
        Assert.InRange(spot.Distance, 15f, 15f + DefaultStep + 0.01f);
    }

    [Fact]
    public void Nothing_valid_within_the_radius_is_a_refusal_and_the_probe_count_is_bounded()
    {
        int calls = 0;
        var spot = FindNearestValid(0f, 0f, 20f, 2f, (_, _) => { calls++; return Probe.No("no ground"); });
        Assert.False(spot.Found);
        Assert.Equal(calls, spot.Probes);
        // 1 + rings 1..10, each max(8, ceil(2*pi*r/2)) points.
        int expected = 1;
        for (int ring = 1; ring <= 10; ring++) expected += Math.Max(8, (int)Math.Ceiling(2 * Math.PI * ring * 2 / 2));
        Assert.Equal(expected, calls);
        Assert.Equal("no ground", spot.Why);

        var exact = FindNearestValid(0f, 0f, 0f, 2f, (_, _) => Probe.No("x"));
        Assert.Equal(1, exact.Probes);
        Assert.True(FindNearestValid(0f, 0f, 250f, 0.5f, (_, _) => Probe.No("x")).Probes <= MaxProbes);
    }

    [Fact]
    public void A_step_too_fine_for_the_budget_is_widened_and_the_whole_radius_is_still_searched()
    {
        Assert.Equal(DefaultStep, StepFor(20f, DefaultStep));
        Assert.True(PlannedProbes(250f, 0.5f) > MaxProbes);
        float widened = StepFor(250f, 0.5f);
        Assert.True(widened > 0.5f);
        Assert.True(PlannedProbes(250f, widened) <= MaxProbes);

        // Ground only from 240 m out: still found at the configured 250 m radius.
        var spot = FindNearestValid(0f, 0f, 250f, 0.5f,
            (x, z) => Distance(0f, 0f, x, z) >= 240.0 ? Probe.Ok(1f) : Probe.No("no ground"));
        Assert.True(spot.Found);
        Assert.InRange(spot.Distance, 240f, 240f + spot.Step + 0.01f);
        Assert.Equal(widened, spot.Step);
        Assert.True(spot.Probes <= MaxProbes);
    }

    [Fact]
    public void Points_off_the_world_are_never_probed_and_a_throwing_probe_is_invalid()
    {
        int calls = 0;
        var spot = FindNearestValid(WorldHalfExtent, 0f, 10f, 2f, (x, _) =>
        {
            calls++;
            Assert.True(x <= WorldHalfExtent);
            return Probe.No("no ground");
        });
        Assert.False(spot.Found);
        Assert.True(calls < spot.Probes + 1);

        var threw = FindNearestValid(0f, 0f, 0f, 2f, (_, _) => throw new InvalidOperationException("boom"));
        Assert.False(threw.Found);
        Assert.Contains("boom", threw.Why);
    }

    [Theory]
    [InlineData(float.NaN, 0f, false)]
    [InlineData(0f, float.PositiveInfinity, false)]
    [InlineData(3201f, 0f, false)]
    [InlineData(-3200f, 3200f, true)]
    public void Only_finite_points_on_the_chunk_grid_are_usable(float x, float z, bool usable) =>
        Assert.Equal(usable, IsUsablePoint(x, z));

    [Fact]
    public void Settings_are_clamped()
    {
        Assert.Equal(0f, ClampRadius(-5f));
        Assert.Equal(250f, ClampRadius(1e6f));
        Assert.Equal(DefaultRadius, ClampRadius(float.NaN));
        Assert.Equal(0.5f, ClampStep(0f));
        Assert.Equal(10f, ClampStep(99f));
    }

    // ---- who may ---------------------------------------------------------------------------------

    [Fact]
    public void The_server_gate_wants_an_admin_with_a_character_wearing_the_buff()
    {
        Assert.Equal(Result.NotAdmin, Gate(isAdmin: false, hasCharacter: true, holdsBuff: true));
        Assert.Equal(Result.NoCharacter, Gate(true, false, false));
        Assert.Equal(Result.NoBuff, Gate(true, true, false));
        Assert.Null(Gate(true, true, true));
        // A non-admin is refused first, so the reply never says which other check failed.
        Assert.Equal(Result.NotAdmin, Gate(false, false, false));
    }

    [Fact]
    public void Wearing_the_buff_reveals_the_map_and_only_an_admin_wearer_may_teleport()
    {
        Assert.Equal(new DebugMapState(true, true), StateFor(hasCharacter: true, holdsBuff: true, isAdmin: true));
        Assert.Equal(new DebugMapState(true, false), StateFor(true, true, false));
        Assert.Equal(DebugMapState.None, StateFor(true, false, true));
        Assert.Equal(DebugMapState.None, StateFor(false, true, true));
    }

    // ---- the client's hold -----------------------------------------------------------------------

    private static readonly DebugMapState Armed = new(true, true);

    [Fact]
    public void A_full_hold_over_the_map_fires_once()
    {
        var hold = new MapTeleportHold();
        Assert.True(hold.Press(Armed, pointerOnMap: true, holdSeconds: 1.0, now: 0.0));
        var half = hold.Frame(0.5, mapOpen: true, pointerOnMap: true, Armed);
        Assert.Equal(HoldPhase.Charging, half.Phase);
        Assert.Equal(0.5f, half.Progress, 3);
        Assert.True(hold.Frame(1.0, true, true, Armed).Fire);
        Assert.False(hold.Frame(1.5, true, true, Armed).Fire);
        hold.Release();
        Assert.Equal(HoldPhase.Idle, hold.Frame(1.6, true, true, Armed).Phase);
    }

    [Fact]
    public void No_power_or_a_press_off_the_map_starts_nothing()
    {
        var hold = new MapTeleportHold();
        Assert.False(hold.Press(new DebugMapState(true, false), true, 1.0, 0.0));
        Assert.False(hold.Press(DebugMapState.None, true, 1.0, 0.0));
        Assert.False(hold.Press(Armed, pointerOnMap: false, 1.0, 0.0));
        Assert.Equal(HoldPhase.Idle, hold.Phase);
    }

    [Theory]
    [InlineData("release", HoldCancel.Released)]
    [InlineData("leave", HoldCancel.Left)]
    [InlineData("close", HoldCancel.Closed)]
    [InlineData("revoke", HoldCancel.Disabled)]
    public void Releasing_leaving_the_map_closing_it_or_losing_the_buff_cancels_with_nothing_sent(string how, HoldCancel expected)
    {
        var hold = new MapTeleportHold();
        hold.Press(Armed, true, 1.0, 0.0);
        hold.Frame(0.3, true, true, Armed);

        HoldStep step = how switch
        {
            "release" => Release(hold),
            "leave" => hold.Frame(0.5, mapOpen: true, pointerOnMap: false, Armed),
            "close" => hold.Frame(0.5, mapOpen: false, pointerOnMap: false, Armed),
            _ => hold.Frame(0.5, true, true, new DebugMapState(true, false)),
        };

        Assert.Equal(HoldPhase.Idle, step.Phase);
        Assert.Equal(expected, step.Cancelled);
        Assert.False(step.Fire);
        // Holding on past the hold time afterwards fires nothing.
        Assert.False(hold.Frame(2.0, true, true, Armed).Fire);
    }

    private static HoldStep Release(MapTeleportHold hold)
    {
        hold.Release();
        return hold.Frame(0.5, true, true, Armed);
    }

    // ---- one teleport per gesture (2026-10-18) ---------------------------------------------------

    private static readonly KeyChord GameChord = new(UiKey.MouseLeft, KeyModifiers.Ctrl | KeyModifiers.Shift);

    [Fact]
    public void Only_a_game_authenticated_admin_wearer_is_marked_as_teleported_by_the_game()
    {
        // On the admin list but not authenticated (no adminauth): Progenitor teleports them.
        Assert.Equal(new DebugMapState(true, true, false), StateFor(true, true, isAdmin: true, gameAuthenticated: false));
        // Authenticated: the game's own Ctrl+Shift+left click teleports them.
        Assert.Equal(new DebugMapState(true, true, true), StateFor(true, true, isAdmin: true, gameAuthenticated: true));
        // The flag never stands without the teleport power.
        Assert.Equal(new DebugMapState(true, false, false), StateFor(true, true, isAdmin: false, gameAuthenticated: true));
        Assert.Equal(DebugMapState.None, StateFor(true, false, true, true));
    }

    [Fact]
    public void The_hold_is_skipped_only_when_the_game_teleports_on_the_same_click()
    {
        var listed = new DebugMapState(true, true, false);
        var authenticated = new DebugMapState(true, true, true);

        Assert.True(SkipHold(authenticated, GameChord));
        // The game checks only Ctrl and Shift, so Alt with them is the game's gesture too.
        Assert.True(SkipHold(authenticated, new KeyChord(UiKey.MouseLeft, KeyModifiers.Ctrl | KeyModifiers.Shift | KeyModifiers.Alt)));
        // Any other key: the game does nothing on it, so Progenitor's hold teleports.
        Assert.False(SkipHold(authenticated, new KeyChord(UiKey.MouseMiddle)));
        Assert.False(SkipHold(authenticated, new KeyChord(UiKey.MouseLeft, KeyModifiers.Shift)));
        Assert.False(SkipHold(authenticated, new KeyChord(UiKey.MouseRight, KeyModifiers.Ctrl | KeyModifiers.Shift)));
        Assert.False(SkipHold(authenticated, KeyChord.None));
        // A listed admin who has not authenticated: the game refuses its own teleport, so the hold is theirs.
        Assert.False(SkipHold(listed, GameChord));
        Assert.False(SkipHold(DebugMapState.None, GameChord));
        Assert.True(IsGameTeleportChord(DefaultTeleportKey));
    }

    [Fact]
    public void The_state_message_carries_the_game_flag_in_its_old_reserved_byte()
    {
        var msg = StateMsg.For(new DebugMapState(true, true, true));
        Assert.Equal(1, msg.GameTeleports);
        Assert.Equal(new DebugMapState(true, true, true), msg.State);
        Assert.Equal(0, StateMsg.For(new DebugMapState(true, true, false)).GameTeleports);
        Assert.Equal(6, (int)Marshal.OffsetOf<StateMsg>(nameof(StateMsg.GameTeleports)));

        // A server from before the flag sends 0 there: the client always holds, as before.
        var old = new StateMsg { Magic = Magic, Reveal = 1, CanTeleport = 1 };
        Assert.False(old.State.GameTeleports);
        Assert.False(SkipHold(old.State, GameChord));
        // A flag without the power reads as off.
        Assert.False(new StateMsg { Magic = Magic, Reveal = 1, CanTeleport = 0, GameTeleports = 1 }.State.GameTeleports);
    }

    // ---- wire and lines --------------------------------------------------------------------------

    [Fact]
    public void Messages_have_distinct_sizes_and_ids_in_Cores_reserved_range()
    {
        Assert.Equal(8, Marshal.SizeOf<StateMsg>());
        Assert.Equal(16, Marshal.SizeOf<RequestMsg>());
        Assert.Equal(36, Marshal.SizeOf<ReplyMsg>());
        Assert.Equal(4, Marshal.SizeOf<QueryMsg>());
        var ids = new[] { StateEventId, RequestEventId, ReplyEventId, QueryEventId };
        Assert.Equal(4, new System.Collections.Generic.HashSet<int>(ids).Count);
        foreach (int id in ids)
        {
            // Core owns the debug tools now, so the ids are in its reserved 'Pr' range, where no
            // plugin may register or send them ('PrD*', free before this).
            Assert.True(Progenitor.Networking.CustomEventPolicy.IsReserved(id));
            Assert.Equal(0x5072, (id >> 16) & 0xFFFF);
            Assert.Equal(0x44, (id >> 8) & 0xFF); // 'D'
        }
    }

    [Fact]
    public void The_notice_says_teleported_or_how_far_it_moved_or_why_not()
    {
        Assert.Equal("Teleported", Notice(Result.Teleported, 0f, 100f));
        Assert.Equal("Teleported (moved 12 m to the nearest valid spot)", Notice(Result.Moved, 11.6f, 100f));
        Assert.Contains("admins only", Notice(Result.NotAdmin, 0f, 100f));
        Assert.Contains(".debug on", Notice(Result.NoBuff, 0f, 100f));
        Assert.Contains("within 100 m", Notice(Result.NoValidSpot, 0f, 100f));
    }

    [Fact]
    public void The_readback_names_the_requested_point_the_chosen_point_and_the_distance()
    {
        var spot = new Spot(true, 110f, 4.5f, -20f, 10f, 37, "not walkable");
        string line = Readback("Ash", Result.Moved, 100f, -20f, spot, "TeleportUtilityServer.Teleport", walkCheck: true);
        Assert.Contains("requested (100.0, -20.0)", line);
        Assert.Contains("chosen (110.0, 4.5, -20.0)", line);
        Assert.Contains("moved 10.0 m", line);
        Assert.Contains("probes=37", line);
        Assert.Contains("walkCheck=on", line);
        Assert.Contains("result=Moved", line);
        Assert.Contains("first=not walkable", line);

        Assert.Contains("no spot", Readback("Ash", Result.NoValidSpot, 1f, 2f, new Spot(false, 1f, float.NaN, 2f, 0f, 9, ""), "", false));
    }

    [Fact]
    public void Status_lines_report_both_powers_and_whether_the_client_was_told()
    {
        var on = StatusLines(new DebugMapState(true, true), pushed: true, 100f, walkCheck: false);
        Assert.Equal(2, on.Count);
        Assert.StartsWith("  map revealed: on", on[0]);
        Assert.DoesNotContain("not yet sent", on[0]);
        Assert.StartsWith("  map teleport: on", on[1]);
        Assert.Contains("walkable-tile check off", on[1]);

        var off = StatusLines(DebugMapState.None, pushed: false, 100f, false);
        Assert.StartsWith("  map revealed: OFF", off[0]);
        Assert.Contains("not yet sent", off[0]);
        Assert.StartsWith("  map teleport: OFF", off[1]);
    }
}
