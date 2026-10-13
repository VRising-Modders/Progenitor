using System;
using Progenitor.Cameras;
using Progenitor.Inputs;
using Progenitor.Units;
using Xunit;

namespace Progenitor.Tests;

/// <summary>Photo mode lock-on: the hold-to-target state machine and the orbit camera. Game-free; not run in the game.</summary>
public class PhotoLockTests
{
    private static readonly InputEntityRef Wolf = new(10, 1);
    private static readonly InputEntityRef Boss = new(20, 3);

    [Fact]
    public void Hold_previews_the_unit_under_the_cursor_and_release_locks_it()
    {
        var s = new PhotoLockState();
        Assert.Equal(PhotoLockEvent.TargetingStarted, s.KeyDown(0, null));
        Assert.True(s.Targeting);
        Assert.Null(s.Preview);
        s.Hover(Wolf);
        Assert.Equal(Wolf, s.Preview);
        s.Hover(Boss);
        Assert.Equal(Boss, s.Preview);
        Assert.Equal(PhotoLockEvent.Locked, s.KeyUp(1.0, Boss));
        Assert.Equal(Boss, s.Locked);
        Assert.False(s.Targeting);
        Assert.Null(s.Preview); // nothing is shown while the key is up
    }

    [Fact]
    public void Releasing_over_nothing_cancels()
    {
        var s = new PhotoLockState();
        s.KeyDown(0, Wolf);
        Assert.Equal(PhotoLockEvent.Cancelled, s.KeyUp(1.0, null));
        Assert.Null(s.Locked);
    }

    [Fact]
    public void A_tap_while_locked_unlocks_but_a_long_hold_over_nothing_keeps_the_lock()
    {
        var s = new PhotoLockState();
        s.LockDirect(Wolf);
        s.KeyDown(5.0, null);
        Assert.Equal(PhotoLockEvent.Cancelled, s.KeyUp(5.0 + PhotoLockState.TapSeconds + 0.1, null));
        Assert.Equal(Wolf, s.Locked);

        s.KeyDown(10.0, null);
        Assert.Equal(PhotoLockEvent.Unlocked, s.KeyUp(10.1, null));
        Assert.Null(s.Locked);
    }

    [Fact]
    public void A_tap_over_the_locked_unit_unlocks_and_a_quick_move_onto_a_new_unit_relocks()
    {
        var s = new PhotoLockState();
        s.LockDirect(Wolf);
        s.KeyDown(0, Wolf);
        Assert.Equal(PhotoLockEvent.Unlocked, s.KeyUp(0.1, Wolf));

        s.LockDirect(Wolf);
        s.KeyDown(1.0, Wolf);
        s.Hover(Boss);
        Assert.Equal(PhotoLockEvent.Locked, s.KeyUp(1.1, Boss));
        Assert.Equal(Boss, s.Locked);
    }

    [Fact]
    public void A_held_key_over_another_unit_switches_the_lock()
    {
        var s = new PhotoLockState();
        s.LockDirect(Wolf);
        s.KeyDown(0, null);
        Assert.Equal(PhotoLockEvent.Locked, s.KeyUp(1.0, Boss));
        Assert.Equal(Boss, s.Locked);
    }

    [Fact]
    public void A_lost_target_unlocks_once_and_reset_forgets_everything_silently()
    {
        var s = new PhotoLockState();
        s.LockDirect(Wolf);
        Assert.Equal(PhotoLockEvent.TargetLost, s.Lost());
        Assert.Null(s.Locked);
        Assert.Equal(PhotoLockEvent.None, s.Lost());

        s.LockDirect(Boss);
        s.KeyDown(0, Wolf);
        s.Reset();
        Assert.Null(s.Locked);
        Assert.False(s.Targeting);
        Assert.Null(s.Preview);
        Assert.Equal(PhotoLockEvent.None, s.KeyUp(1, Wolf));
        Assert.Equal(PhotoLockEvent.None, s.UnlockDirect());
    }

    [Fact]
    public void A_null_entity_is_nothing_under_the_cursor()
    {
        var s = new PhotoLockState();
        s.KeyDown(0, new InputEntityRef(0, 0));
        Assert.Null(s.Preview);
        Assert.Equal(PhotoLockEvent.Cancelled, s.KeyUp(1, new InputEntityRef(0, 0)));
    }

    // ---- orbit ----

    private static readonly UnitVec3 Focus = new(10, 2, 10);

    private static float Dist(PhotoPose p) => PhotoOrbit.Distance(new UnitVec3(p.X, p.Y, p.Z), Focus);

    private static void AssertFacesFocus(PhotoPose p)
    {
        var (yaw, pitch) = PhotoOrbit.Angles(new UnitVec3(p.X, p.Y, p.Z), Focus);
        Assert.Equal(yaw, p.Yaw, 2);
        Assert.Equal(pitch, p.Pitch, 2);
    }

    [Fact]
    public void Flying_while_locked_keeps_facing_the_target()
    {
        var pose = new PhotoPose(10, 2, 0, 0, 0);
        var moved = PhotoOrbit.Step(pose, Focus, new PhotoFlightInput { Right = 1 }, 10, 1, 0.1f, 0);
        Assert.NotEqual(pose.X, moved.X);
        AssertFacesFocus(moved);
    }

    [Fact]
    public void Mouse_look_orbits_at_constant_distance()
    {
        var pose = new PhotoPose(10, 2, 0, 0, 0);
        float before = Dist(pose);
        var orbited = PhotoOrbit.Step(pose, Focus, new PhotoFlightInput { Looking = true, LookX = 300 }, 10, 1, 0.016f, 0);
        Assert.Equal(before, Dist(orbited), 2);
        Assert.NotEqual(pose.X, orbited.X, 1);
        AssertFacesFocus(orbited);
    }

    [Fact]
    public void The_wheel_changes_distance_along_the_view_within_bounds()
    {
        var pose = new PhotoPose(10, 2, 0, 0, 0);
        var closer = PhotoOrbit.Step(pose, Focus, default, 10, 1, 0.016f, 1);
        Assert.Equal(10f / PhotoOrbit.DistanceStep, Dist(closer), 2);
        AssertFacesFocus(closer);
        var far = PhotoOrbit.Step(pose, Focus, default, 10, 1, 0.016f, -100);
        Assert.Equal(PhotoOrbit.MaxDistance, Dist(far), 1);
        var near = PhotoOrbit.Step(pose, Focus, default, 10, 1, 0.016f, 100);
        Assert.Equal(PhotoOrbit.MinDistance, Dist(near), 1);
    }

    [Fact]
    public void Angles_match_the_flight_forward_convention()
    {
        var from = new UnitVec3(0, 5, 0);
        var to = new UnitVec3(3, 1, 4);
        var (yaw, pitch) = PhotoOrbit.Angles(from, to);
        var f = PhotoFlight.Forward(yaw, pitch);
        float d = PhotoOrbit.Distance(from, to);
        Assert.Equal(3f / d, f.X, 3);
        Assert.Equal(-4f / d, f.Y, 3);
        Assert.Equal(4f / d, f.Z, 3);
        Assert.Equal(PhotoOrbit.AimHeight, PhotoOrbit.FocusOf(new UnitVec3(0, 0, 0)).Y);
    }
}
