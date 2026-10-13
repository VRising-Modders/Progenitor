using System;
using System.Collections.Generic;
using Progenitor.Cameras;
using Progenitor.Units;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Game-free coverage of <c>ctx.Camera</c>: value validation, the degree/yaw/pitch arithmetic,
/// orbit-limit widening, and the director's owner and shot state machine against a fake runtime.
/// What only a client can show (the writes landing, input staying off, the timing) is listed in
/// docs/modules.md, "Camera".
/// </summary>
[Collection("Camera")]
public class CameraTests : IDisposable
{
    public CameraTests() => CameraDirector.ResetForTests();

    public void Dispose() => CameraDirector.ResetForTests();

    // ---- validation --------------------------------------------------------------------------

    [Theory]
    [InlineData(float.NaN, 0f, 0f)]
    [InlineData(0f, float.PositiveInfinity, 0f)]
    [InlineData(0f, 0f, float.NegativeInfinity)]
    [InlineData(100000f, 0f, 0f)]
    [InlineData(0f, -100000f, 0f)]
    public void Positions_that_are_not_finite_or_reach_the_game_guard_are_refused(float x, float y, float z)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FreeShotPlan.Create(new UnitVec3(x, y, z), 0f, 0f));
        Assert.Throws<ArgumentOutOfRangeException>(() => OrbitShotPlan.ForPoint(new UnitVec3(x, y, z), 10f, 0f, 0f));
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(100000f)]
    public void Angles_that_are_not_finite_or_reach_the_game_guard_are_refused(float angle)
    {
        var at = new UnitVec3(1, 2, 3);
        Assert.Throws<ArgumentOutOfRangeException>(() => FreeShotPlan.Create(at, angle, 0f));
        Assert.Throws<ArgumentOutOfRangeException>(() => FreeShotPlan.Create(at, 0f, angle));
        Assert.Throws<ArgumentOutOfRangeException>(() => OrbitShotPlan.ForPoint(at, 10f, angle, 0f));
        Assert.Throws<ArgumentOutOfRangeException>(() => OrbitShotPlan.ForPoint(at, 10f, 0f, angle));
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.05f)]
    [InlineData(-5f)]
    [InlineData(float.NaN)]
    [InlineData(100000f)]
    public void Orbit_distance_must_be_finite_positive_and_below_the_guard(float distance)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => OrbitShotPlan.ForPoint(new UnitVec3(0, 0, 0), distance, 0f, 0f));
        Assert.Throws<ArgumentOutOfRangeException>(() => OrbitShotPlan.ForEntity(new CameraEntityRef(5, 1), distance, 0f, 0f));
    }

    [Fact]
    public void Orbit_of_a_null_entity_is_refused()
    {
        Assert.Throws<ArgumentException>(() => OrbitShotPlan.ForEntity(default, 10f, 0f, 0f));
    }

    [Fact]
    public void Valid_shots_keep_their_values_wrap_yaw_and_clamp_pitch()
    {
        var free = FreeShotPlan.Create(new UnitVec3(10, 20, 30), 190f, 120f);
        Assert.Equal(new UnitVec3(10, 20, 30), free.Position);
        Assert.Equal(-170f, free.YawDegrees, 3);
        Assert.Equal(CameraMath.PitchLimit, free.PitchDegrees);

        var orbit = OrbitShotPlan.ForEntity(new CameraEntityRef(42, 3), 25f, 45f, -100f);
        Assert.True(orbit.OrbitsEntity);
        Assert.Equal(25f, orbit.Distance);
        Assert.Equal(45f, orbit.YawDegrees);
        Assert.Equal(-CameraMath.PitchLimit, orbit.PitchDegrees);

        var point = OrbitShotPlan.ForPoint(new UnitVec3(1, 2, 3), 8f, 0f, 30f);
        Assert.False(point.OrbitsEntity);
        Assert.Equal(new UnitVec3(1, 2, 3), point.Point);
    }

    // ---- shot math -----------------------------------------------------------------------------

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(180f, 180f)]
    [InlineData(-180f, 180f)]
    [InlineData(190f, -170f)]
    [InlineData(540f, 180f)]
    [InlineData(-450f, -90f)]
    [InlineData(720.5f, 0.5f)]
    public void WrapYaw_lands_in_minus_180_exclusive_to_180(float input, float expected)
    {
        Assert.Equal(expected, CameraMath.WrapYaw(input), 3);
    }

    [Theory]
    [InlineData(350f, 10f, 370f)]
    [InlineData(10f, 350f, -10f)]
    [InlineData(720f, 0f, 720f)]
    [InlineData(-725f, 0f, -720f)]
    [InlineData(0f, 90f, 90f)]
    public void NearestYaw_takes_the_short_way_from_the_current_yaw(float current, float target, float expected)
    {
        Assert.Equal(expected, CameraMath.NearestYaw(current, target), 3);
    }

    [Fact]
    public void NearestYaw_with_an_unreadable_current_yaw_returns_the_wrapped_target()
    {
        Assert.Equal(-90f, CameraMath.NearestYaw(float.NaN, 270f), 3);
        Assert.Equal(-90f, CameraMath.NearestYaw(1e9f, 270f), 3);
    }

    [Theory]
    [InlineData(95f, 89f)]
    [InlineData(-95f, -89f)]
    [InlineData(30f, 30f)]
    public void ClampPitch_holds_inside_89_degrees(float input, float expected)
    {
        Assert.Equal(expected, CameraMath.ClampPitch(input));
    }

    [Fact]
    public void LookAngles_follow_the_game_convention_yaw_0_is_plus_z_and_positive_pitch_looks_down()
    {
        var origin = new UnitVec3(0, 0, 0);

        var ahead = CameraMath.LookAngles(origin, new UnitVec3(0, 0, 10));
        Assert.Equal(0f, ahead.Yaw, 3);
        Assert.Equal(0f, ahead.Pitch, 3);

        var right = CameraMath.LookAngles(origin, new UnitVec3(10, 0, 0));
        Assert.Equal(90f, right.Yaw, 3);

        var behind = CameraMath.LookAngles(origin, new UnitVec3(0, 0, -10));
        Assert.Equal(180f, Math.Abs(behind.Yaw), 3);

        var downward = CameraMath.LookAngles(new UnitVec3(0, 10, 0), new UnitVec3(0, 0, 10));
        Assert.Equal(45f, downward.Pitch, 3);

        var upward = CameraMath.LookAngles(origin, new UnitVec3(0, 10, 10));
        Assert.Equal(-45f, upward.Pitch, 3);

        var straightDown = CameraMath.LookAngles(new UnitVec3(0, 10, 0), origin);
        Assert.Equal(CameraMath.PitchLimit, straightDown.Pitch);
        Assert.Equal(0f, straightDown.Yaw);

        Assert.Throws<ArgumentException>(() => CameraMath.LookAngles(origin, origin));
    }

    [Fact]
    public void Widen_admits_the_shot_and_leaves_contained_limits_alone()
    {
        var vanilla = new OrbitLimits(-10f, 60f, 2f, 12f);

        Assert.Equal(vanilla, CameraMath.Widen(vanilla, 8f, 30f));

        var far = CameraMath.Widen(vanilla, 40f, 30f);
        Assert.Equal(new OrbitLimits(-10f, 60f, 2f, 40f), far);

        var close = CameraMath.Widen(vanilla, 0.5f, 30f);
        Assert.Equal(new OrbitLimits(-10f, 60f, 0.5f, 12f), close);

        var low = CameraMath.Widen(vanilla, 8f, -40f);
        Assert.Equal(new OrbitLimits(-40f, 60f, 2f, 12f), low);

        var high = CameraMath.Widen(vanilla, 8f, 85f);
        Assert.Equal(new OrbitLimits(-10f, 85f, 2f, 12f), high);
    }

    [Fact]
    public void CameraMode_values_are_the_game_CameraType_values()
    {
        Assert.Equal(0, (int)CameraMode.None);
        Assert.Equal(1, (int)CameraMode.TopDown);
        Assert.Equal(2, (int)CameraMode.Orbit);
        Assert.Equal(3, (int)CameraMode.Free);
        Assert.Equal(4, (int)CameraMode.Static);
    }

    // ---- focus math --------------------------------------------------------------------------------

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(90f, 30f)]
    [InlineData(-135f, 60f)]
    [InlineData(45f, -20f)]
    public void Forward_is_unit_length_and_the_inverse_of_LookAngles(float yaw, float pitch)
    {
        var f = CameraMath.Forward(yaw, pitch);
        Assert.Equal(1.0, Math.Sqrt((f.X * f.X) + (f.Y * f.Y) + (f.Z * f.Z)), 5);

        var back = CameraMath.LookAngles(new UnitVec3(0, 0, 0), f);
        Assert.Equal(yaw, back.Yaw, 3);
        Assert.Equal(pitch, back.Pitch, 3);
    }

    [Fact]
    public void Forward_follows_the_game_convention()
    {
        AssertNear(new UnitVec3(0, 0, 1), CameraMath.Forward(0f, 0f));
        AssertNear(new UnitVec3(1, 0, 0), CameraMath.Forward(90f, 0f));
        AssertNear(new UnitVec3(0, -1, 0), CameraMath.Forward(0f, 90f)); // positive pitch looks down
    }

    [Fact]
    public void A_downward_ray_meets_the_plane_where_it_crosses_it()
    {
        // From y = 10, 45 degrees down along +Z: crosses y = 0 ten meters ahead.
        Assert.True(CameraMath.TryRayPlaneY(new UnitVec3(0, 10, 0), CameraMath.Forward(0f, 45f), 0f, 100f, out var hit));
        AssertNear(new UnitVec3(0, 0, 10), hit);

        // A non-unit direction gives the same point.
        Assert.True(CameraMath.TryRayPlaneY(new UnitVec3(1, 10, 2), new UnitVec3(0, -2, 2), 4f, 100f, out hit));
        AssertNear(new UnitVec3(1, 4, 8), hit);

        // Starting on the plane hits at the origin.
        Assert.True(CameraMath.TryRayPlaneY(new UnitVec3(3, 5, 3), new UnitVec3(0, -1, 0), 5f, 100f, out hit));
        AssertNear(new UnitVec3(3, 5, 3), hit);
    }

    [Fact]
    public void Rays_that_are_level_point_away_or_cross_too_far_miss_the_plane()
    {
        var origin = new UnitVec3(0, 10, 0);
        Assert.False(CameraMath.TryRayPlaneY(origin, CameraMath.Forward(0f, 0f), 0f, 1000f, out _));   // level
        Assert.False(CameraMath.TryRayPlaneY(origin, CameraMath.Forward(0f, -30f), 0f, 1000f, out _)); // upward, plane below
        Assert.False(CameraMath.TryRayPlaneY(origin, CameraMath.Forward(0f, 30f), 20f, 1000f, out _)); // downward, plane above
        Assert.False(CameraMath.TryRayPlaneY(origin, new UnitVec3(0, 0, 0), 0f, 1000f, out _));        // no direction

        // 45 degrees down from 10 m crosses at 14.14 m along the ray: inside 15, outside 14.
        Assert.True(CameraMath.TryRayPlaneY(origin, CameraMath.Forward(0f, 45f), 0f, 15f, out _));
        Assert.False(CameraMath.TryRayPlaneY(origin, CameraMath.Forward(0f, 45f), 0f, 14f, out _));
    }

    [Fact]
    public void FreeFocus_uses_the_players_floor_not_sea_level()
    {
        // The measured case in miniature: a camera 5 m above a floor at y = 15, 30 degrees down.
        // The game's own target (plane y = 0) sits 40 m along the ray, under the floor; the focus
        // sits on the floor 10 m along it.
        var camera = new UnitVec3(720, 20, -2825);
        var focus = CameraMath.FreeFocus(camera, 0f, 30f, 15f);

        Assert.Equal(15f, focus.Y, 3);
        Assert.Equal(10.0, Distance(camera, focus), 3);

        Assert.True(CameraMath.TryRayPlaneY(camera, CameraMath.Forward(0f, 30f), 0f, 1000f, out var seaLevel));
        Assert.Equal(40.0, Distance(camera, seaLevel), 3);
    }

    [Theory]
    [InlineData(0f, null)]       // level view: the ray never meets the floor
    [InlineData(-20f, 15f)]      // looking up: the floor is behind the ray
    [InlineData(0.5f, 15f)]      // near-level: the floor is ~570 m away
    [InlineData(4.9f, 15f)]      // just under NearLevelPitch
    [InlineData(30f, 25f)]       // floor plane above the camera: behind the downward ray
    [InlineData(30f, null)]      // no local player to take the floor height from
    public void FreeFocus_falls_back_to_15_m_along_the_ray(float pitch, float? floorY)
    {
        var camera = new UnitVec3(720, 20, -2825);
        var focus = CameraMath.FreeFocus(camera, 90f, pitch, floorY);

        Assert.Equal(CameraMath.FallbackFocusDistance, Distance(camera, focus), 3);
        var along = CameraMath.Forward(90f, pitch);
        Assert.Equal(camera.X + (along.X * 15f), focus.X, 3);
        Assert.Equal(camera.Y + (along.Y * 15f), focus.Y, 3);
    }

    [Fact]
    public void A_steep_ray_keeps_a_far_floor_hit_and_clamps_along_the_ray_only_past_1000_m()
    {
        // A crane 300 m above the floor at 30 degrees down meets it 600 m out: kept, on the floor.
        var crane = new UnitVec3(0, 315, 0);
        var far = CameraMath.FreeFocus(crane, 0f, 30f, 15f);
        Assert.Equal(15f, far.Y, 3);
        Assert.Equal(600.0, Distance(crane, far), 2);

        // 10 degrees down from 20 m above: 115 m, beyond the old 100 m cap, still the floor hit.
        var low = new UnitVec3(0, 35, 0);
        var hit = CameraMath.FreeFocus(low, 0f, 10f, 15f);
        Assert.Equal(15f, hit.Y, 3);
        Assert.True(Distance(low, hit) > 100.0);

        // 900 m up at 30 degrees: 1800 m to the floor, clamped to 1000 m along the ray (not 15 m).
        var sky = new UnitVec3(0, 915, 0);
        var clamped = CameraMath.FreeFocus(sky, 0f, 30f, 15f);
        Assert.Equal(CameraMath.MaxFocusDistance, Distance(sky, clamped), 1);
        Assert.Equal(915f - 500f, clamped.Y, 1);
    }

    [Theory]
    [InlineData(15f)]     // camera on the floor plane
    [InlineData(15.5f)]   // half a meter above it, looking 45 degrees down: 0.7 m to the floor
    public void The_derived_focus_never_collapses_onto_the_camera(float cameraY)
    {
        var camera = new UnitVec3(0, cameraY, 0);
        var focus = CameraMath.FreeFocus(camera, 0f, 45f, 15f);
        Assert.Equal(CameraMath.MinFocusDistance, Distance(camera, focus), 3);
    }

    [Fact]
    public void A_named_focus_wins_over_the_derived_one_and_is_validated()
    {
        var named = new UnitVec3(1, 2, 3);
        var plan = FreeShotPlan.Create(new UnitVec3(0, 20, 0), 0f, 30f, named);
        Assert.Equal(named, plan.Focus);
        Assert.Equal(named, plan.ResolveFocus(new UnitVec3(50, 50, 50), 90f, 10f, 0f));

        var derived = FreeShotPlan.Create(new UnitVec3(0, 20, 0), 0f, 30f);
        Assert.Null(derived.Focus);
        AssertNear(CameraMath.FreeFocus(new UnitVec3(0, 20, 0), 0f, 30f, 15f), derived.ResolveFocus(new UnitVec3(0, 20, 0), 0f, 30f, 15f));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            FreeShotPlan.Create(new UnitVec3(0, 20, 0), 0f, 30f, new UnitVec3(float.NaN, 0, 0)));
    }

    // ---- director: focus -----------------------------------------------------------------------

    [Fact]
    public void The_owners_shot_is_focused_with_its_own_plan_and_nothing_else_is()
    {
        var rt = new FakeRuntime();
        var a = new CameraDirector(rt, "a");
        var b = new CameraDirector(rt, "b");

        Assert.False(CameraDirector.FocusOwner()); // no owner
        a.Free(FreeShotPlan.Create(new UnitVec3(0, 20, 0), 0f, 30f, new UnitVec3(0, 15, 5)), instant: false);
        Assert.True(CameraDirector.FocusOwner());
        Assert.Equal(new UnitVec3(0, 15, 5), rt.FreeFocuses[^1].Focus);

        b.Orbit(OrbitShotPlan.ForPoint(new UnitVec3(3, 4, 5), 10f, 0f, 20f), instant: false);
        Assert.False(a.Focus()); // superseded
        Assert.True(CameraDirector.FocusOwner());
        Assert.Equal(new UnitVec3(3, 4, 5), rt.OrbitFocuses[^1].Point);

        b.Reset();
        int calls = rt.FreeFocuses.Count + rt.OrbitFocuses.Count;
        Assert.False(CameraDirector.FocusOwner()); // the hook may still fire once before it is removed
        Assert.Equal(calls, rt.FreeFocuses.Count + rt.OrbitFocuses.Count);
    }

    [Fact]
    public void No_focus_is_written_across_a_client_world_change()
    {
        var rt = new FakeRuntime();
        var d = new CameraDirector(rt, "a");
        d.Free(FreeAt(1), instant: false);

        rt.World = 8;
        Assert.False(CameraDirector.FocusOwner());
        Assert.Empty(rt.FreeFocuses);
    }

    [Fact]
    public void A_focus_that_cannot_be_written_is_logged_once_after_the_camera_is_up_and_never_stops_the_shot()
    {
        var rt = new FakeRuntime { WriteResult = CameraWriteResult.NotReady, FocusSucceeds = false };
        var d = new CameraDirector(rt, "a");
        d.Free(FreeAt(1), instant: false);

        Assert.True(CameraDirector.TickOwner());
        Assert.False(CameraDirector.FocusOwner()); // camera not up: expected, not logged
        Assert.Empty(rt.Logs);

        rt.WriteResult = CameraWriteResult.Written;
        Assert.True(CameraDirector.TickOwner());
        Assert.False(CameraDirector.FocusOwner());
        Assert.False(CameraDirector.FocusOwner());
        Assert.Single(rt.Logs);
        Assert.True(d.IsShotActive);
        Assert.True(CameraDirector.TickOwner());
    }

    private static double Distance(UnitVec3 a, UnitVec3 b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
        return Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
    }

    private static void AssertNear(UnitVec3 expected, UnitVec3 actual)
    {
        Assert.Equal(expected.X, actual.X, 4);
        Assert.Equal(expected.Y, actual.Y, 4);
        Assert.Equal(expected.Z, actual.Z, 4);
    }

    // ---- director: shots ------------------------------------------------------------------------

    [Fact]
    public void FreeShot_requests_free_once_waits_for_the_camera_then_writes_every_frame()
    {
        var rt = new FakeRuntime { WriteResult = CameraWriteResult.NotReady };
        var d = new CameraDirector(rt, "a");

        d.Free(FreeAt(1), instant: true);
        Assert.Equal(new[] { CameraMode.Free }, rt.Requests);
        Assert.Equal(1, rt.Arms);
        Assert.True(d.Owns);
        Assert.True(d.IsShotActive);

        Assert.True(CameraDirector.TickOwner()); // camera not up yet
        Assert.True(rt.FreeWrites[^1].Instant);

        rt.WriteResult = CameraWriteResult.Written;
        Assert.True(CameraDirector.TickOwner());
        Assert.True(rt.FreeWrites[^1].Instant); // the cut lands on the first write that succeeds
        Assert.True(CameraDirector.TickOwner());
        Assert.False(rt.FreeWrites[^1].Instant); // and only there

        d.Free(FreeAt(2), instant: false); // retarget: no new request
        Assert.True(CameraDirector.TickOwner());
        Assert.Single(rt.Requests);
        Assert.Equal(2f, rt.FreeWrites[^1].Plan.Position.X);
        Assert.False(rt.FreeWrites[^1].Instant);
    }

    [Fact]
    public void Switching_shot_kind_requests_the_new_mode_with_the_orbit_anchor()
    {
        var rt = new FakeRuntime();
        var d = new CameraDirector(rt, "a");

        d.Free(FreeAt(1), instant: false);
        var orbit = OrbitShotPlan.ForEntity(new CameraEntityRef(7, 2), 15f, 90f, 20f);
        d.Orbit(orbit, instant: true);

        Assert.Equal(new[] { CameraMode.Free, CameraMode.Orbit }, rt.Requests);
        Assert.Equal(CameraMode.Orbit, d.RequestedMode);

        Assert.True(CameraDirector.TickOwner());
        Assert.Empty(rt.FreeWrites);
        Assert.Single(rt.OrbitWrites);
        Assert.True(rt.OrbitWrites[0].Instant);
        Assert.Equal(15f, rt.OrbitWrites[0].Plan.Distance);

        d.Orbit(OrbitShotPlan.ForPoint(new UnitVec3(1, 2, 3), 9f, 0f, 10f), instant: false);
        Assert.Equal(2, rt.Requests.Count); // same kind: retarget without a new request
    }

    [Fact]
    public void SetMode_then_a_shot_of_that_mode_does_not_request_again()
    {
        var rt = new FakeRuntime();
        var d = new CameraDirector(rt, "a");

        d.SetMode(CameraMode.Static);
        d.Free(FreeAt(1), instant: false);
        Assert.Equal(new[] { CameraMode.Static }, rt.Requests);

        d.SetMode(CameraMode.Orbit);
        Assert.False(d.IsShotActive);
        d.Orbit(OrbitShotPlan.ForPoint(new UnitVec3(0, 0, 0), 10f, 0f, 0f), instant: false);
        Assert.Equal(new[] { CameraMode.Static, CameraMode.Orbit }, rt.Requests);
    }

    [Fact]
    public void SetMode_refuses_None_and_values_outside_the_game_enum()
    {
        var d = new CameraDirector(new FakeRuntime(), "a");
        Assert.Throws<ArgumentOutOfRangeException>(() => d.SetMode(CameraMode.None));
        Assert.Throws<ArgumentOutOfRangeException>(() => d.SetMode((CameraMode)9));
    }

    [Fact]
    public void SetMode_TopDown_leaves_the_camera_unowned_and_unload_then_requests_nothing()
    {
        var rt = new FakeRuntime();
        var d = new CameraDirector(rt, "a");

        d.SetMode(CameraMode.TopDown);
        Assert.False(d.Owns);
        d.Release();
        Assert.Equal(new[] { CameraMode.TopDown }, rt.Requests);
    }

    // ---- director: reset and unload -------------------------------------------------------------

    [Fact]
    public void Reset_returns_to_top_down_and_stops_ticking()
    {
        var rt = new FakeRuntime();
        var d = new CameraDirector(rt, "a");
        d.Free(FreeAt(1), instant: false);

        d.Reset();

        Assert.Equal(CameraMode.TopDown, rt.Requests[^1]);
        Assert.False(d.Owns);
        Assert.False(d.IsShotActive);
        Assert.Null(CameraDirector.Owner);
        Assert.False(CameraDirector.TickOwner());
    }

    [Fact]
    public void Unload_of_the_owner_after_a_mode_change_resets_to_top_down()
    {
        var rt = new FakeRuntime();
        var d = new CameraDirector(rt, "a");
        d.SetMode(CameraMode.Free); // no shot, but the player would be stranded in free mode

        d.Release();

        Assert.Equal(new[] { CameraMode.Free, CameraMode.TopDown }, rt.Requests);
        Assert.Null(CameraDirector.Owner);
        Assert.Throws<InvalidOperationException>(() => d.Free(FreeAt(1), instant: false));
        d.Release(); // idempotent
        Assert.Equal(2, rt.Requests.Count);
    }

    [Fact]
    public void Core_uninstall_releases_the_owner()
    {
        var rt = new FakeRuntime();
        var d = new CameraDirector(rt, "a");
        d.Orbit(OrbitShotPlan.ForPoint(new UnitVec3(0, 0, 0), 10f, 0f, 0f), instant: false);

        CameraDirector.ReleaseAll();

        Assert.Equal(CameraMode.TopDown, rt.Requests[^1]);
        Assert.Null(CameraDirector.Owner);
        Assert.False(d.IsShotActive);
    }

    [Fact]
    public void Unload_without_a_client_world_clears_state_and_does_not_throw()
    {
        var rt = new FakeRuntime();
        var d = new CameraDirector(rt, "a");
        d.Free(FreeAt(1), instant: false);
        rt.World = 0;
        rt.RequestSucceeds = false;

        d.Release();

        Assert.Null(CameraDirector.Owner);
        Assert.Contains(rt.Logs, l => l.Contains("could not request TopDown"));
    }

    // ---- director: ownership ---------------------------------------------------------------------

    [Fact]
    public void The_last_caller_owns_the_camera_and_only_the_owner_resets_on_unload()
    {
        var rt = new FakeRuntime();
        var a = new CameraDirector(rt, "a");
        var b = new CameraDirector(rt, "b");

        a.Free(FreeAt(1), instant: false);
        b.Orbit(OrbitShotPlan.ForPoint(new UnitVec3(0, 0, 0), 10f, 0f, 0f), instant: false);

        Assert.False(a.IsShotActive);
        Assert.True(b.Owns);

        a.Release();
        Assert.Equal(new[] { CameraMode.Free, CameraMode.Orbit }, rt.Requests); // a no longer owns: no reset

        Assert.True(CameraDirector.TickOwner());
        Assert.Single(rt.OrbitWrites);
        Assert.Empty(rt.FreeWrites);

        b.Release();
        Assert.Equal(CameraMode.TopDown, rt.Requests[^1]);
    }

    [Fact]
    public void Reset_from_a_non_owner_still_returns_to_top_down_and_clears_the_owner()
    {
        var rt = new FakeRuntime();
        var a = new CameraDirector(rt, "a");
        var b = new CameraDirector(rt, "b");
        a.Free(FreeAt(1), instant: false);

        b.Reset();

        Assert.Equal(CameraMode.TopDown, rt.Requests[^1]);
        Assert.False(a.IsShotActive);
        Assert.Null(CameraDirector.Owner);
        a.Release();
        Assert.Equal(2, rt.Requests.Count);
    }

    // ---- director: world, retry, failure -----------------------------------------------------------

    [Fact]
    public void A_client_world_change_drops_the_shot_without_a_request()
    {
        var rt = new FakeRuntime();
        var d = new CameraDirector(rt, "a");
        d.Orbit(OrbitShotPlan.ForEntity(new CameraEntityRef(5, 1), 10f, 0f, 0f), instant: false);

        rt.World = 99;
        Assert.False(CameraDirector.TickOwner());

        Assert.Single(rt.Requests);
        Assert.Empty(rt.OrbitWrites);
        Assert.Null(CameraDirector.Owner);
        d.Release();
        Assert.Single(rt.Requests); // nothing left to undo in the new world
    }

    [Fact]
    public void A_mode_that_never_appears_is_requested_again_then_the_shot_stops_once()
    {
        var rt = new FakeRuntime { WriteResult = CameraWriteResult.NotReady };
        var d = new CameraDirector(rt, "a");
        d.Free(FreeAt(1), instant: false);

        int frames = 0;
        while (CameraDirector.TickOwner()) Assert.True(++frames < 10_000);

        Assert.Equal(CameraDirector.RetryFrames * (CameraDirector.MaxRetries + 1) - 1, frames);
        Assert.Equal(1 + CameraDirector.MaxRetries, rt.Requests.Count);
        Assert.All(rt.Requests, m => Assert.Equal(CameraMode.Free, m));
        Assert.Single(rt.Logs, l => l.Contains("never came up"));
        Assert.False(d.IsShotActive);
        Assert.False(CameraDirector.TickOwner());

        // It still owns the mode it asked for, so unload restores top-down.
        Assert.True(d.Owns);
        d.Release();
        Assert.Equal(CameraMode.TopDown, rt.Requests[^1]);
    }

    [Fact]
    public void A_shot_requests_again_when_the_game_switched_the_mode_away()
    {
        var rt = new FakeRuntime();
        var d = new CameraDirector(rt, "a");
        d.Free(FreeAt(1), instant: false);
        Assert.True(CameraDirector.TickOwner()); // the write lands, so the request is no longer pending

        rt.Current = CameraMode.TopDown; // something else switched the camera
        d.Free(FreeAt(2), instant: false);

        Assert.Equal(new[] { CameraMode.Free, CameraMode.Free }, rt.Requests);
    }

    [Fact]
    public void A_retarget_while_the_request_is_still_pending_does_not_request_again()
    {
        var rt = new FakeRuntime { FollowRequests = false }; // the game still reports TopDown
        var d = new CameraDirector(rt, "a");
        d.Free(FreeAt(1), instant: false);
        d.Free(FreeAt(2), instant: false);

        Assert.Single(rt.Requests);
    }

    [Fact]
    public void A_refused_request_leaves_the_previous_owner_its_shot_and_its_reset_duty()
    {
        var rt = new FakeRuntime();
        var a = new CameraDirector(rt, "a");
        var b = new CameraDirector(rt, "b");
        a.Free(FreeAt(1), instant: false);

        rt.RequestSucceeds = false;
        Assert.Throws<InvalidOperationException>(() => b.Orbit(OrbitShotPlan.ForPoint(new UnitVec3(0, 0, 0), 10f, 0f, 0f), instant: false));
        Assert.Throws<InvalidOperationException>(() => b.SetMode(CameraMode.Static));

        Assert.True(a.Owns);
        Assert.True(a.IsShotActive);
        Assert.False(b.IsShotActive);
        b.Release();
        Assert.Single(rt.Requests); // b never owned the camera, so it has nothing to reset

        rt.RequestSucceeds = true;
        a.Release();
        Assert.Equal(new[] { CameraMode.Free, CameraMode.TopDown }, rt.Requests);
    }

    [Fact]
    public void A_throwing_request_takes_ownership_without_a_shot_so_unload_still_resets()
    {
        var rt = new FakeRuntime();
        var a = new CameraDirector(rt, "a");
        var b = new CameraDirector(rt, "b");
        a.Free(FreeAt(1), instant: false);

        rt.RequestThrows = true;
        var e = Assert.Throws<InvalidOperationException>(() => b.Orbit(OrbitShotPlan.ForPoint(new UnitVec3(0, 0, 0), 10f, 0f, 0f), instant: false));
        Assert.IsType<InvalidOperationException>(e.InnerException);

        Assert.True(b.Owns);
        Assert.False(b.IsShotActive);
        Assert.False(a.IsShotActive);
        Assert.False(CameraDirector.TickOwner());

        rt.RequestThrows = false;
        a.Release();
        Assert.Single(rt.Requests); // a was superseded
        b.Release();
        Assert.Equal(new[] { CameraMode.Free, CameraMode.TopDown }, rt.Requests);
    }

    [Fact]
    public void A_throwing_request_on_unload_is_logged_not_thrown()
    {
        var rt = new FakeRuntime();
        var d = new CameraDirector(rt, "a");
        d.SetMode(CameraMode.Free);
        rt.RequestThrows = true;

        d.Release();

        Assert.Null(CameraDirector.Owner);
        Assert.Contains(rt.Logs, l => l.Contains("threw"));
    }

    [Fact]
    public void Reset_after_release_throws()
    {
        var d = new CameraDirector(new FakeRuntime(), "a");
        d.Release();
        Assert.Throws<InvalidOperationException>(() => d.Reset());
    }

    [Fact]
    public void A_failed_write_stops_the_shot_and_resets_to_top_down()
    {
        var rt = new FakeRuntime { WriteResult = CameraWriteResult.Failed };
        var d = new CameraDirector(rt, "a");
        d.Free(FreeAt(1), instant: false);

        Assert.False(CameraDirector.TickOwner());

        Assert.Equal(new[] { CameraMode.Free, CameraMode.TopDown }, rt.Requests);
        Assert.False(d.IsShotActive);
        Assert.Null(CameraDirector.Owner);
    }

    [Fact]
    public void Shots_need_a_client_world_and_reset_without_one_is_a_quiet_no_op()
    {
        var rt = new FakeRuntime { World = 0 };
        var d = new CameraDirector(rt, "a");

        Assert.Throws<InvalidOperationException>(() => d.Free(FreeAt(1), instant: false));
        Assert.Throws<InvalidOperationException>(() => d.SetMode(CameraMode.Free));
        d.Reset();

        Assert.Empty(rt.Requests);
        Assert.Null(CameraDirector.Owner);
    }

    [Fact]
    public void A_refused_request_throws_and_leaves_the_camera_unowned()
    {
        var rt = new FakeRuntime { RequestSucceeds = false };
        var d = new CameraDirector(rt, "a");

        Assert.Throws<InvalidOperationException>(() => d.Free(FreeAt(1), instant: false));
        Assert.Null(CameraDirector.Owner);
        Assert.False(d.IsShotActive);
        Assert.False(CameraDirector.TickOwner());
    }

    private static FreeShotPlan FreeAt(float x) => FreeShotPlan.Create(new UnitVec3(x, 10, 0), 0f, 30f);

    private sealed class FakeRuntime : ICameraRuntime
    {
        public long World { get; set; } = 7;
        public bool RequestSucceeds { get; set; } = true;
        public bool RequestThrows { get; set; }
        public bool FollowRequests { get; set; } = true;
        public CameraWriteResult WriteResult { get; set; } = CameraWriteResult.Written;
        public CameraMode Current { get; set; } = CameraMode.TopDown;
        public List<CameraMode> Requests { get; } = new();
        public List<(FreeShotPlan Plan, bool Instant)> FreeWrites { get; } = new();
        public List<(OrbitShotPlan Plan, bool Instant)> OrbitWrites { get; } = new();
        public List<string> Logs { get; } = new();
        public int Arms { get; private set; }

        public long WorldToken => World;

        public CameraMode ReadMode() => Current;

        public bool Request(CameraMode mode)
        {
            if (RequestThrows) throw new InvalidOperationException("boom");
            if (!RequestSucceeds) return false;
            Requests.Add(mode);
            if (FollowRequests) Current = mode; // the game's type once LocalUserSystem has run
            return true;
        }

        public CameraWriteResult WriteFree(in FreeShotPlan plan, bool instant)
        {
            FreeWrites.Add((plan, instant));
            return WriteResult;
        }

        public CameraWriteResult WriteOrbit(in OrbitShotPlan plan, bool instant)
        {
            OrbitWrites.Add((plan, instant));
            return WriteResult;
        }

        public bool FocusSucceeds { get; set; } = true;
        public List<FreeShotPlan> FreeFocuses { get; } = new();
        public List<OrbitShotPlan> OrbitFocuses { get; } = new();

        public bool FocusFree(in FreeShotPlan plan)
        {
            FreeFocuses.Add(plan);
            return FocusSucceeds;
        }

        public bool FocusOrbit(in OrbitShotPlan plan)
        {
            OrbitFocuses.Add(plan);
            return FocusSucceeds;
        }

        public void Arm() => Arms++;

        public void Log(string line) => Logs.Add(line);
    }
}
