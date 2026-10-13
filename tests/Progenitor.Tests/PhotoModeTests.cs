using System;
using System.Collections.Generic;
using System.Text.Json;
using Progenitor.Cameras;
using Progenitor.Dev.Ops;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Photo mode's game-free halves: the fly camera (<c>PhotoFlight</c>) and the server's pause
/// bookkeeping (<c>PhotoPauseLedger</c>): admins only, one freeze per user, released on resume,
/// disconnect, loss of admin and unload.
/// </summary>
public class PhotoModeTests
{
    private static PhotoFlightInput Keys(float forward = 0, float right = 0, float up = 0) =>
        new() { Forward = forward, Right = right, Up = up };

    [Fact]
    public void Forward_follows_the_game_convention_yaw_0_is_plus_z_and_positive_pitch_looks_down()
    {
        var f = PhotoFlight.Forward(0, 0);
        Assert.Equal(0, f.X, 5); Assert.Equal(0, f.Y, 5); Assert.Equal(1, f.Z, 5);
        var east = PhotoFlight.Forward(90, 0);
        Assert.Equal(1, east.X, 5); Assert.Equal(0, east.Z, 5);
        var down = PhotoFlight.Forward(0, 45);
        Assert.True(down.Y < 0);
        var r = PhotoFlight.Right(0);
        Assert.Equal(1, r.X, 5); Assert.Equal(0, r.Z, 5);
    }

    [Fact]
    public void W_moves_along_the_view_including_its_pitch()
    {
        var pose = new PhotoPose(0, 10, 0, 0, 45);
        var next = PhotoFlight.Step(pose, Keys(forward: 1), speed: 10, sensitivity: 1, dt: 0.1f);
        Assert.True(next.Z > 0.6f && next.Y < 9.4f, next.ToString());
        Assert.Equal(0, next.X, 4);
    }

    [Fact]
    public void Up_and_down_keys_move_straight_up_and_down_whatever_the_view()
    {
        var pose = new PhotoPose(1, 5, 2, 30, 60);
        var up = PhotoFlight.Step(pose, Keys(up: 1), 10, 1, 0.1f);
        Assert.Equal(6, up.Y, 4); Assert.Equal(1, up.X, 4); Assert.Equal(2, up.Z, 4);
        var down = PhotoFlight.Step(pose, Keys(up: -1), 10, 1, 0.1f);
        Assert.Equal(4, down.Y, 4);
    }

    [Fact]
    public void There_is_no_ground_or_height_limit()
    {
        var pose = new PhotoPose(0, 0.5f, 0, 0, 0);
        for (int i = 0; i < 20; i++) pose = PhotoFlight.Step(pose, Keys(up: -1), 10, 1, 0.1f);
        Assert.True(pose.Y < -15);
        for (int i = 0; i < 400; i++) pose = PhotoFlight.Step(pose, Keys(up: 1), 100, 1, 0.1f);
        Assert.True(pose.Y > 3000);
    }

    [Fact]
    public void Diagonals_are_not_faster_than_straight_lines()
    {
        var pose = new PhotoPose(0, 0, 0, 0, 0);
        var diag = PhotoFlight.Step(pose, Keys(forward: 1, right: 1, up: 1), 10, 1, 0.1f);
        float dist = MathF.Sqrt((diag.X * diag.X) + (diag.Y * diag.Y) + (diag.Z * diag.Z));
        Assert.Equal(1f, dist, 3);
    }

    [Fact]
    public void The_fast_and_slow_keys_scale_the_speed()
    {
        var pose = new PhotoPose(0, 0, 0, 0, 0);
        var normal = PhotoFlight.Step(pose, Keys(forward: 1), 10, 1, 0.1f);
        var fast = PhotoFlight.Step(pose, new PhotoFlightInput { Forward = 1, Fast = true }, 10, 1, 0.1f);
        var slow = PhotoFlight.Step(pose, new PhotoFlightInput { Forward = 1, Slow = true }, 10, 1, 0.1f);
        Assert.Equal(normal.Z * PhotoFlight.FastMultiplier, fast.Z, 3);
        Assert.Equal(normal.Z * PhotoFlight.SlowMultiplier, slow.Z, 3);
    }

    [Fact]
    public void The_mouse_turns_only_while_the_right_button_is_held_and_up_looks_up()
    {
        var pose = new PhotoPose(0, 0, 0, 0, 0);
        var ignored = PhotoFlight.Step(pose, new PhotoFlightInput { LookX = 100, LookY = 100 }, 10, 1, 0.016f);
        Assert.Equal(0, ignored.Yaw); Assert.Equal(0, ignored.Pitch);
        var look = PhotoFlight.Step(pose, new PhotoFlightInput { Looking = true, LookX = 100, LookY = 100 }, 10, 1, 0.016f);
        Assert.Equal(100 * PhotoFlight.DegreesPerPixel, look.Yaw, 3);
        Assert.Equal(-100 * PhotoFlight.DegreesPerPixel, look.Pitch, 3);
    }

    [Fact]
    public void Pitch_reaches_the_free_camera_limit_but_not_past_it()
    {
        var pose = new PhotoPose(0, 0, 0, 0, 0);
        var down = PhotoFlight.Step(pose, new PhotoFlightInput { Looking = true, LookY = -100000 }, 10, 1, 0.016f);
        Assert.Equal(CameraMath.PitchLimit, down.Pitch);
        var up = PhotoFlight.Step(pose, new PhotoFlightInput { Looking = true, LookY = 100000 }, 10, 1, 0.016f);
        Assert.Equal(-CameraMath.PitchLimit, up.Pitch);
    }

    [Fact]
    public void A_long_frame_moves_at_most_the_step_cap_and_a_zero_frame_not_at_all()
    {
        var pose = new PhotoPose(0, 0, 0, 0, 0);
        var hitch = PhotoFlight.Step(pose, Keys(forward: 1), 10, 1, 5f);
        Assert.Equal(10 * PhotoFlight.MaxStepSeconds, hitch.Z, 4);
        var none = PhotoFlight.Step(pose, Keys(forward: 1), 10, 1, 0f);
        Assert.Equal(0, none.Z);
    }

    [Fact]
    public void The_wheel_scales_speed_and_zoom_steps_scale_field_of_view_within_their_bounds()
    {
        Assert.Equal(PhotoFlight.DefaultSpeed * PhotoFlight.SpeedStep, PhotoFlight.NextSpeed(PhotoFlight.DefaultSpeed, 1), 3);
        Assert.Equal(PhotoFlight.MaxSpeed, PhotoFlight.NextSpeed(PhotoFlight.MaxSpeed, 5));
        Assert.Equal(PhotoFlight.MinSpeed, PhotoFlight.NextSpeed(PhotoFlight.MinSpeed, -5));
        Assert.Equal(50 - PhotoFlight.FovStep, PhotoFlight.NextFov(50, 1));
        Assert.Equal(PhotoFlight.MinFov, PhotoFlight.NextFov(PhotoFlight.MinFov, 3));
        Assert.Equal(PhotoFlight.MaxFov, PhotoFlight.NextFov(PhotoFlight.MaxFov, -3));
        Assert.Equal(1, PhotoFlight.Notches(120)); Assert.Equal(-1, PhotoFlight.Notches(-0.5f)); Assert.Equal(0, PhotoFlight.Notches(0));
    }

    // ---- pause ledger ----

    private sealed class FakeLease : IDisposable
    {
        public bool Disposed;
        public void Dispose() => Disposed = true;
    }

    private static (PhotoPauseLedger Ledger, List<FakeLease> Leases) Ledger(bool refuse = false)
    {
        var leases = new List<FakeLease>();
        return (new PhotoPauseLedger(() =>
        {
            if (refuse) return null;
            var l = new FakeLease();
            leases.Add(l);
            return l;
        }), leases);
    }

    [Fact]
    public void The_pause_event_id_is_PrpP_in_the_reserved_range()
    {
        Assert.Equal(0x50727050, PhotoPauseLedger.EventId);
        Assert.True(PhotoPauseLedger.IdIsReserved);
    }

    [Fact]
    public void A_non_admin_cannot_pause()
    {
        var (ledger, leases) = Ledger();
        var reply = ledger.Handle(7, isAdmin: false, PhotoPauseRequest.For(1, pause: true));
        Assert.False(reply.IsPaused);
        Assert.Equal(PhotoPauseReason.NotAdmin, reply.Reason);
        Assert.Empty(leases);
        Assert.Contains("admin", PhotoPauseLedger.Describe(reply));
    }

    [Fact]
    public void An_admin_pauses_once_and_resume_ends_it()
    {
        var (ledger, leases) = Ledger();
        var first = ledger.Handle(3, true, PhotoPauseRequest.For(1, true));
        var again = ledger.Handle(3, true, PhotoPauseRequest.For(2, true));
        Assert.True(first.IsPaused); Assert.True(again.IsPaused);
        Assert.Equal(2, again.Nonce);
        Assert.Single(leases);
        var resume = ledger.Handle(3, true, PhotoPauseRequest.For(3, false));
        Assert.False(resume.IsPaused);
        Assert.True(leases[0].Disposed);
        Assert.False(ledger.Holds(3));
    }

    [Fact]
    public void Resume_needs_no_rights_and_only_ends_the_senders_own_pause()
    {
        var (ledger, leases) = Ledger();
        ledger.Handle(1, true, PhotoPauseRequest.For(1, true));
        ledger.Handle(2, false, PhotoPauseRequest.For(1, false));
        Assert.False(leases[0].Disposed);
        Assert.True(ledger.Holds(1));
    }

    [Fact]
    public void A_refused_lease_answers_refused_and_holds_nothing()
    {
        var (ledger, _) = Ledger(refuse: true);
        var reply = ledger.Handle(1, true, PhotoPauseRequest.For(9, true));
        Assert.False(reply.IsPaused);
        Assert.Equal(PhotoPauseReason.Refused, reply.Reason);
        Assert.False(ledger.Holds(1));
    }

    [Fact]
    public void The_sweep_ends_pauses_of_users_who_left_and_unload_ends_the_rest()
    {
        var (ledger, leases) = Ledger();
        ledger.Handle(1, true, PhotoPauseRequest.For(1, true));
        ledger.Handle(2, true, PhotoPauseRequest.For(1, true));
        Assert.Equal(1, ledger.Sweep(user => user == 2));
        Assert.True(leases[0].Disposed); Assert.False(leases[1].Disposed);
        ledger.ReleaseAll();
        Assert.True(leases[1].Disposed);
        Assert.Empty(ledger.Holders);
    }

    // ---- held keys from the Photo-scope keybinds ----

    private static bool[] Held(params FlightKey[] keys)
    {
        var held = new bool[PhotoFlight.FlightKeyCount];
        foreach (var k in keys) held[(int)k] = true;
        return held;
    }

    [Fact]
    public void Held_keys_become_axes_and_opposite_keys_cancel()
    {
        var input = PhotoFlight.FromHeld(Held(FlightKey.Forward, FlightKey.Left, FlightKey.Up, FlightKey.Fast));
        Assert.Equal(1f, input.Forward);
        Assert.Equal(-1f, input.Right);
        Assert.Equal(1f, input.Up);
        Assert.True(input.Fast);
        Assert.False(input.Slow);
        Assert.False(input.Looking);

        var cancel = PhotoFlight.FromHeld(Held(FlightKey.Forward, FlightKey.Back, FlightKey.Up, FlightKey.Down, FlightKey.Left, FlightKey.Right));
        Assert.Equal(0f, cancel.Forward);
        Assert.Equal(0f, cancel.Right);
        Assert.Equal(0f, cancel.Up);
    }

    [Fact]
    public void A_short_or_missing_held_array_reads_as_nothing_held()
    {
        Assert.Equal(0f, PhotoFlight.FromHeld(null!).Forward);
        Assert.Equal(0f, PhotoFlight.FromHeld(new bool[1]).Up);
        Assert.Equal(8, Enum.GetValues(typeof(FlightKey)).Length);
        Assert.Equal(PhotoFlight.FlightKeyCount, Enum.GetValues(typeof(FlightKey)).Length);
    }

    [Fact]
    public void Photo_mode_keys_go_through_the_keybind_api_and_only_the_pointer_is_read_directly()
    {
        string src = SourcePin.ReadFile("src", "Progenitor.Core", "Cameras", "PhotoMode.cs");
        Assert.Empty(DirectKeyReadScanner.Scan(src));
        SourcePin.AssertUncommented(src, "UnityInputRuntime.TrySamplePointer(out bool right, out float dx, out float dy, out float scrollY)");
        SourcePin.AssertUncommented(src, "Keybinds.PhotoScopeOpen = () => _active;");
        SourcePin.AssertUncommented(src, "KeybindScope.Photo);");
    }

    [Fact]
    public void The_pause_reuses_world_speed_leases_and_never_writes_TimeScale_itself()
    {
        string src = SourcePin.ReadFile("src", "Progenitor.Core", "Cameras", "PhotoMode.cs");
        SourcePin.AssertUncommented(src, "TimeScaleRuntime.Take(Worlds.Server, TimeScaleOverrideKind.Freeze, 0f, owner, out string why);");
        Assert.DoesNotContain("AiRuntime.RW", src);
        Assert.DoesNotContain("TimeScaleValueOffset", src);
    }

    // ---- bridge args ----

    private static PhotoRequest Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return PhotoArgs.Parse(doc.RootElement.Clone());
    }

    [Theory]
    [InlineData("{}", "status", false)]
    [InlineData("{\"action\":\"status\"}", "status", false)]
    [InlineData("{\"action\":\"ON\"}", "on", false)]
    [InlineData("{\"action\":\"on\",\"enable\":true}", "on", true)]
    [InlineData("{\"action\":\"off\"}", "off", false)]
    public void Photo_args_parse(string json, string action, bool enable)
    {
        var r = Parse(json);
        Assert.Equal(action, r.Action);
        Assert.Equal(enable, r.Enable);
    }

    [Theory]
    [InlineData("{\"action\":\"pause\"}")]
    [InlineData("{\"action\":1}")]
    [InlineData("{\"action\":\"on\",\"enable\":\"yes\"}")]
    [InlineData("{\"action\":\"off\",\"enable\":true}")]
    [InlineData("{\"action\":\"lock\"}")]
    [InlineData("{\"action\":\"lock\",\"entity\":\"abc\"}")]
    [InlineData("{\"action\":\"lock\",\"entity\":\"0:1\"}")]
    [InlineData("{\"action\":\"unlock\",\"entity\":\"5:1\"}")]
    public void Bad_photo_args_are_refused(string json) => Assert.Throws<ArgumentException>(() => Parse(json));

    [Fact]
    public void Photo_lock_takes_a_client_entity_and_unlock_takes_nothing()
    {
        var r = Parse("{\"action\":\"lock\",\"entity\":\"123:4\"}");
        Assert.Equal("lock", r.Action);
        Assert.Equal(123, r.EntityIndex);
        Assert.Equal(4, r.EntityVersion);
        Assert.Equal("unlock", Parse("{\"action\":\"unlock\"}").Action);
    }
}
