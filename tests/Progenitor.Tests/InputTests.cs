using System;
using System.Collections.Generic;
using Progenitor.Inputs;
using Progenitor.Units;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Game-free coverage of <c>ctx.Input</c>: ownership and preemption, every end reason, the
/// per-frame write (aim fields, targets, hover, buttons), the user yield, and the outgoing command
/// read-back and patch (edges once per pass, release edges, taps, zero walk, reconnect re-seed)
/// against a fake runtime. What only a client can show (the writes landing, the server applying
/// them) is the acceptance procedure in docs/input.md.
/// </summary>
public class InputTests
{
    private static readonly InputEntityRef Controller = new(10, 1);
    private static readonly InputEntityRef Character = new(20, 1);
    private static readonly InputEntityRef Dracula = new(30, 2);

    private sealed class FakeRuntime : IInputRuntime
    {
        public long WorldToken { get; set; } = 3;
        public double Now { get; set; } = 100;
        public int Frame { get; set; } = 1;
        public bool IsMainThread { get; set; } = true;
        public bool Focused { get; set; } = true;
        public bool HavePlayer = true;
        public InputEntityRef ControllerRef = Controller, CharacterRef = Character;
        public readonly Dictionary<InputEntityRef, UnitVec3> Positions = new();
        public readonly Dictionary<InputEntityRef, InputNetworkId> NetworkIds = new();
        public UserActivity Activity;
        public readonly List<string> Logs = new(), Warnings = new();

        public bool TryGetLocalPlayer(out InputEntityRef controller, out InputEntityRef character)
        {
            controller = ControllerRef;
            character = CharacterRef;
            return HavePlayer;
        }

        public bool TryGetPosition(InputEntityRef entity, out UnitVec3 position) => Positions.TryGetValue(entity, out position);

        public bool TryGetNetworkId(InputEntityRef entity, out InputNetworkId id) => NetworkIds.TryGetValue(entity, out id);

        public UserActivity SampleActivity() => Activity;

        public void Log(string message) => Logs.Add(message);

        public void Warn(string message) => Warnings.Add(message);
    }

    private readonly FakeRuntime _rt = new();
    private readonly InputDirector _director;

    public InputTests()
    {
        _director = new InputDirector(_rt);
        _rt.Positions[Character] = new UnitVec3(700, 10, -2800);
        _rt.Positions[Dracula] = new UnitVec3(706, 12, -2792);
        _rt.NetworkIds[Dracula] = new InputNetworkId(77, 5, 0);
    }

    private InputLease Acquire(InputChannels channels, UserPolicy user = UserPolicy.Yield, bool preempt = false, string holder = "a")
    {
        var result = _director.TryAcquire(holder, new InputLeaseOptions(channels, user, preempt), out var lease);
        Assert.Equal(InputAcquireResult.Acquired, result);
        return lease!;
    }

    private void NextFrame(double seconds = 1.0 / 60)
    {
        _rt.Frame++;
        _rt.Now += seconds;
    }

    /// <summary>One frame: prepare, then a command pass over <paramref name="cmds"/> (appended at the given frames).</summary>
    private InputCommandView[] Pass(params int[] clientFrames)
    {
        _director.Prepare();
        var cmds = new InputCommandView[clientFrames.Length];
        for (int i = 0; i < cmds.Length; i++) cmds[i].ClientFrame = clientFrames[i];
        int newest = int.MinValue;
        foreach (var c in cmds) newest = Math.Max(newest, c.ClientFrame);
        if (cmds.Length == 0) return cmds;
        int threshold = _director.CommandThreshold(Controller, newest);
        int best = threshold, count = 0;
        for (int i = 0; i < cmds.Length; i++)
        {
            if (cmds[i].ClientFrame <= threshold) continue;
            _director.PatchCommand(ref cmds[i], first: count == 0);
            best = Math.Max(best, cmds[i].ClientFrame);
            count++;
        }

        _director.CommandsDone(best, count);
        return cmds;
    }

    // ---- ownership -------------------------------------------------------------------------------

    [Fact]
    public void No_local_player_refuses_and_holds_nothing()
    {
        _rt.HavePlayer = false;
        Assert.Equal(InputAcquireResult.NoLocalPlayer, _director.TryAcquire("a", new InputLeaseOptions(InputChannels.Aim), out var lease));
        Assert.Null(lease);
        _rt.WorldToken = 0;
        _rt.HavePlayer = true;
        Assert.Equal(InputAcquireResult.NoLocalPlayer, _director.TryAcquire("a", new InputLeaseOptions(InputChannels.Aim), out _));
        Assert.Equal(InputChannels.None, _director.HeldChannels);
    }

    [Theory]
    [InlineData(InputChannels.None)]
    [InlineData((InputChannels)16)]
    public void Channels_must_name_a_real_channel(InputChannels channels)
    {
        Assert.Throws<ArgumentException>(() => _director.TryAcquire("a", new InputLeaseOptions(channels), out _));
    }

    [Fact]
    public void Disjoint_channels_have_separate_owners()
    {
        var aim = Acquire(InputChannels.Aim | InputChannels.Hover, holder: "camera");
        var move = Acquire(InputChannels.Move, holder: "walker");
        Assert.True(aim.Active);
        Assert.True(move.Active);
        Assert.Equal("camera", _director.HolderOf(InputChannels.Hover));
        Assert.Equal("walker", _director.HolderOf(InputChannels.Move));
        Assert.Null(_director.HolderOf(InputChannels.Buttons));
    }

    [Fact]
    public void Overlap_is_busy_and_acquires_nothing_not_even_the_free_channels()
    {
        var first = Acquire(InputChannels.Aim, holder: "first");
        Assert.Equal(InputAcquireResult.Busy, _director.TryAcquire("second", new InputLeaseOptions(InputChannels.Aim | InputChannels.Move), out var second));
        Assert.Null(second);
        Assert.True(first.Active);
        Assert.Null(_director.HolderOf(InputChannels.Move));
    }

    [Fact]
    public void Preempt_ends_every_overlapping_lease_once_before_returning()
    {
        var aim = Acquire(InputChannels.Aim, holder: "aim");
        var move = Acquire(InputChannels.Move, holder: "move");
        var buttons = Acquire(InputChannels.Buttons, holder: "buttons");
        var ended = new List<(string, InputLeaseEnd)>();
        aim.Ended += r => ended.Add(("aim", r));
        move.Ended += r => ended.Add(("move", r));
        buttons.Ended += r => ended.Add(("buttons", r));

        var bot = Acquire(InputChannels.Aim | InputChannels.Move, preempt: true, holder: "bot");

        Assert.Equal(new[] { ("aim", InputLeaseEnd.Preempted), ("move", InputLeaseEnd.Preempted) }, ended);
        Assert.False(aim.Active);
        Assert.Equal(InputLeaseEnd.Preempted, move.EndReason);
        Assert.True(buttons.Active);
        Assert.True(bot.Active);
        Assert.Equal("bot", _director.HolderOf(InputChannels.Aim));
    }

    [Fact]
    public void Mutating_an_unheld_channel_throws_and_an_ended_lease_ignores_calls()
    {
        var lease = Acquire(InputChannels.Aim);
        Assert.Throws<InvalidOperationException>(() => lease.Move(1, 0));
        Assert.Throws<InvalidOperationException>(() => lease.Press(SyncedButtons.Primary));
        lease.Dispose();
        lease.AimAt(new UnitVec3(1, 2, 3));
        lease.Move(1, 0); // no-op after end, even for an unheld channel
        Assert.False(lease.HasAim);
    }

    [Fact]
    public void Off_the_main_thread_throws()
    {
        var lease = Acquire(InputChannels.Aim);
        _rt.IsMainThread = false;
        Assert.Throws<InvalidOperationException>(() => lease.AimAt(new UnitVec3(1, 2, 3)));
        Assert.Throws<InvalidOperationException>(() => _director.TryAcquire("b", new InputLeaseOptions(InputChannels.Move), out _));
    }

    // ---- end reasons -----------------------------------------------------------------------------

    [Fact]
    public void Dispose_ends_released_once_and_frees_the_channel()
    {
        var lease = Acquire(InputChannels.Aim);
        int ended = 0;
        lease.Ended += r => { ended++; Assert.Equal(InputLeaseEnd.Released, r); };
        lease.Dispose();
        lease.Dispose();
        Assert.Equal(1, ended);
        Assert.Equal(InputChannels.None, _director.HeldChannels);
        Acquire(InputChannels.Aim, holder: "next");
    }

    [Fact]
    public void World_change_disconnect_and_character_change_end_leases()
    {
        var a = Acquire(InputChannels.Aim);
        _rt.WorldToken = 5;
        _director.Validate();
        Assert.Equal(InputLeaseEnd.WorldChanged, a.EndReason);

        var b = Acquire(InputChannels.Aim);
        _rt.WorldToken = 0;
        _director.Validate();
        Assert.Equal(InputLeaseEnd.Disconnected, b.EndReason);

        _rt.WorldToken = 7;
        var c = Acquire(InputChannels.Aim);
        _rt.CharacterRef = new InputEntityRef(21, 1);
        _director.Validate();
        Assert.Equal(InputLeaseEnd.CharacterChanged, c.EndReason);

        _rt.CharacterRef = Character;
        var d = Acquire(InputChannels.Aim);
        _rt.HavePlayer = false;
        _director.Validate();
        Assert.Equal(InputLeaseEnd.CharacterChanged, d.EndReason);
    }

    [Fact]
    public void Release_all_ends_every_lease_with_the_reason()
    {
        var a = Acquire(InputChannels.Aim, holder: "a");
        var b = Acquire(InputChannels.Move, UserPolicy.Override, holder: "b");
        Assert.Equal(2, _director.ReleaseAll(InputLeaseEnd.UserPanic));
        Assert.Equal(InputLeaseEnd.UserPanic, a.EndReason);
        Assert.Equal(InputLeaseEnd.UserPanic, b.EndReason);
    }

    [Fact]
    public void An_ended_handler_that_throws_is_logged_and_the_lease_still_ends()
    {
        var a = Acquire(InputChannels.Aim);
        a.Ended += _ => throw new InvalidOperationException("boom");
        a.Dispose();
        Assert.False(a.Active);
        Assert.Contains(_rt.Warnings, w => w.Contains("boom"));
    }

    [Fact]
    public void Layout_refusal_ends_leases_and_refuses_new_ones()
    {
        var a = Acquire(InputChannels.Aim);
        _director.RefuseLayout("EntityInput moved");
        Assert.Equal(InputLeaseEnd.LayoutRefused, a.EndReason);
        Assert.Equal(InputAcquireResult.LayoutRefused, _director.TryAcquire("b", new InputLeaseOptions(InputChannels.Aim), out _));
        Assert.False(_director.Status().LayoutOk);
        Assert.False(_director.HasWork);
    }

    [Fact]
    public void A_fault_ends_leases_disables_and_stops_the_hooks()
    {
        var a = Acquire(InputChannels.Buttons);
        a.Press(SyncedButtons.Primary);
        Pass(1);
        _director.Fault("write threw");
        Assert.Equal(InputLeaseEnd.Faulted, a.EndReason);
        Assert.False(_director.HasWork);
        Assert.Equal(InputAcquireResult.Faulted, _director.TryAcquire("b", new InputLeaseOptions(InputChannels.Aim), out _));
        Assert.True(_director.Status().Faulted);
    }

    // ---- the frame write -------------------------------------------------------------------------

    [Fact]
    public void Aim_fields_are_the_floor_point_a_flat_unit_direction_and_the_flat_distance()
    {
        Assert.True(InputDirector.AimFields(new UnitVec3(0, 10, 0), new UnitVec3(3, 50, 4), out var plane, out var dir, out float len));
        Assert.Equal(new UnitVec3(3, 10, 4), plane);
        Assert.Equal(5f, len, 4);
        Assert.Equal(0.6f, dir.X, 4);
        Assert.Equal(0f, dir.Y);
        Assert.Equal(0.8f, dir.Z, 4);
        Assert.False(InputDirector.AimFields(new UnitVec3(1, 0, 1), new UnitVec3(1, 9, 1), out _, out _, out _));
    }

    [Fact]
    public void Aim_at_a_point_writes_aim_and_rotation()
    {
        var lease = Acquire(InputChannels.Aim);
        lease.AimAt(new UnitVec3(703, 0, -2796));
        var w = _director.Prepare();
        Assert.True(w.WriteAim);
        Assert.Equal(new UnitVec3(703, 10, -2796), w.AimPosition);
        Assert.Equal(w.AimPosition, w.AimPlane);
        Assert.Equal(5f, w.AimLength, 4);
        Assert.True(w.WriteRotation);
        Assert.Equal(w.AimDirection, w.Rotation);
        Assert.False(w.WriteMove);
        Assert.False(w.WriteHover);
        Assert.Equal(0, w.Pressed);
    }

    [Fact]
    public void Aim_at_a_unit_follows_it_and_keeps_the_last_point_when_it_is_gone()
    {
        var lease = Acquire(InputChannels.Aim);
        lease.AimAtTarget(Dracula);
        var w = _director.Prepare();
        Assert.Equal(new UnitVec3(706, 10, -2792), w.AimPosition);

        NextFrame();
        _rt.Positions[Dracula] = new UnitVec3(710, 12, -2800);
        Assert.Equal(new UnitVec3(710, 10, -2800), _director.Prepare().AimPosition);

        NextFrame();
        _rt.Positions.Remove(Dracula);
        w = _director.Prepare();
        Assert.True(lease.TargetLost);
        Assert.True(w.WriteAim);
        Assert.Equal(new UnitVec3(710, 10, -2800), w.AimPosition);
    }

    [Fact]
    public void Aim_at_a_unit_never_found_writes_no_aim()
    {
        var lease = Acquire(InputChannels.Aim);
        lease.AimAtTarget(new InputEntityRef(99, 1));
        Assert.False(_director.Prepare().WriteAim);
        Assert.True(lease.TargetLost);
    }

    [Fact]
    public void A_lease_holding_aim_and_move_with_no_aim_faces_the_walk()
    {
        var lease = Acquire(InputChannels.Aim | InputChannels.Move);
        lease.Move(0, 2); // normalized to (0, 1)
        var w = _director.Prepare();
        Assert.True(w.WriteMove);
        Assert.Equal(0f, w.MoveX);
        Assert.Equal(1f, w.MoveZ);
        Assert.True(w.WriteRotation);
        Assert.Equal(new UnitVec3(0, 0, 1), w.Rotation);
        Assert.False(w.WriteAim);
    }

    [Fact]
    public void A_move_only_lease_leaves_facing_to_the_player()
    {
        var lease = Acquire(InputChannels.Move);
        lease.Move(0.5f, 0);
        var w = _director.Prepare();
        Assert.Equal(0.5f, w.MoveX); // a partial stick is kept
        Assert.False(w.WriteRotation);
    }

    [Fact]
    public void Move_refuses_non_finite_values()
    {
        var lease = Acquire(InputChannels.Move | InputChannels.Buttons);
        Assert.Throws<ArgumentOutOfRangeException>(() => lease.Move(float.NaN, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => lease.Press(SyncedButtons.Primary, 0f));
    }

    [Fact]
    public void Hover_writes_the_units_network_id_and_clears_when_it_is_gone()
    {
        var lease = Acquire(InputChannels.Hover);
        lease.HoverTargetSet(Dracula);
        var w = _director.Prepare();
        Assert.True(w.WriteHover);
        Assert.Equal(Dracula, w.HoverEntity);
        Assert.Equal(new InputNetworkId(77, 5, 0), w.HoverNetworkId);

        NextFrame();
        _rt.NetworkIds.Remove(Dracula);
        w = _director.Prepare();
        Assert.True(w.WriteHover);
        Assert.True(w.HoverEntity.IsNull);
        Assert.True(w.HoverNetworkId.IsZero);
        Assert.True(lease.TargetLost);

        lease.ClearHover();
        Assert.False(_director.Prepare().WriteHover);
    }

    [Fact]
    public void Timed_presses_expire_per_bit()
    {
        var lease = Acquire(InputChannels.Buttons);
        lease.Press(SyncedButtons.Primary, 0.5f);
        lease.Press(SyncedButtons.OffensiveSpell);
        Assert.Equal((ushort)(SyncedButtons.Primary | SyncedButtons.OffensiveSpell), _director.Prepare().Pressed);
        NextFrame(0.6);
        Assert.Equal((ushort)SyncedButtons.OffensiveSpell, _director.Prepare().Pressed);
        lease.Release(SyncedButtons.OffensiveSpell);
        Assert.Equal(0, _director.Prepare().Pressed);
    }

    [Fact]
    public void On_frame_runs_once_per_frame_and_a_throwing_callback_is_logged_once()
    {
        var lease = Acquire(InputChannels.Move);
        int calls = 0;
        lease.OnFrame = l => { calls++; l.Move(1, 0); };
        _director.Prepare();
        _director.Prepare();
        Assert.Equal(1, calls);
        NextFrame();
        Assert.Equal(1f, _director.Prepare().MoveX);
        Assert.Equal(2, calls);

        lease.OnFrame = _ => throw new InvalidOperationException("steer");
        NextFrame();
        _director.Prepare();
        NextFrame();
        _director.Prepare();
        Assert.Single(_rt.Warnings, w => w.Contains("steer"));
        Assert.True(lease.Active);
    }

    // ---- the user yield --------------------------------------------------------------------------

    [Fact]
    public void Yield_pauses_only_the_channel_the_user_is_using_and_resumes_after_idle()
    {
        var lease = Acquire(InputChannels.All);
        lease.AimAt(new UnitVec3(703, 0, -2796));
        lease.Move(1, 0);
        lease.Press(SyncedButtons.Primary);

        _rt.Activity = UserActivity.Pointer;
        var w = _director.Prepare();
        Assert.False(w.WriteAim);
        Assert.True(w.WriteMove);
        Assert.Equal((ushort)SyncedButtons.Primary, w.Pressed);
        Assert.Equal(InputChannels.Aim | InputChannels.Hover, lease.SuspendedChannels);
        Assert.True(lease.Suspended);
        Assert.True(lease.Active);

        _rt.Activity = UserActivity.None;
        NextFrame(1.0);
        Assert.False(_director.Prepare().WriteAim);
        NextFrame(0.6);
        Assert.True(_director.Prepare().WriteAim);
        Assert.False(lease.Suspended);

        _rt.Activity = UserActivity.MoveKeys | UserActivity.ButtonKeys;
        NextFrame();
        w = _director.Prepare();
        Assert.False(w.WriteMove);
        Assert.Equal(0, w.Pressed);
        Assert.True(w.WriteAim);
    }

    [Fact]
    public void Override_keeps_writing_while_the_user_is_active_and_is_logged()
    {
        var lease = Acquire(InputChannels.Aim | InputChannels.Move, UserPolicy.Override);
        Assert.Contains(_rt.Warnings, w => w.Contains("Override"));
        lease.AimAt(new UnitVec3(703, 0, -2796));
        _rt.Activity = UserActivity.Pointer | UserActivity.MoveKeys;
        var w = _director.Prepare();
        Assert.True(w.WriteAim);
        Assert.True(w.WriteMove);
        Assert.False(lease.Suspended);
    }

    // ---- outgoing commands -----------------------------------------------------------------------

    [Fact]
    public void The_first_pass_starts_at_the_newest_command_and_never_rewrites_history()
    {
        var lease = Acquire(InputChannels.Move);
        lease.Move(1, 0);
        var cmds = Pass(40, 41, 42);
        Assert.False(cmds[0].Patched);
        Assert.False(cmds[1].Patched);
        Assert.True(cmds[2].Patched);
        Assert.Equal(1f, cmds[2].MoveX);
        Assert.Equal(1f, cmds[2].RawMoveX);
        Assert.Equal(42, _director.LastSent.ClientFrame);
    }

    [Fact]
    public void Edges_go_on_the_first_new_command_only_and_release_sends_up()
    {
        var lease = Acquire(InputChannels.Buttons);
        lease.Press(SyncedButtons.OffensiveSpell);
        var cmds = Pass(10);
        Assert.Equal((ushort)SyncedButtons.OffensiveSpell, cmds[0].Down);
        Assert.Equal((ushort)SyncedButtons.OffensiveSpell, cmds[0].Pressed);

        NextFrame();
        cmds = Pass(10, 11, 12);
        Assert.Equal(0, cmds[1].Down);
        Assert.Equal(0, cmds[2].Down);
        Assert.Equal((ushort)SyncedButtons.OffensiveSpell, cmds[2].Pressed);

        lease.Dispose();
        Assert.True(_director.HasWork); // the up edge has not gone out yet
        NextFrame();
        cmds = Pass(12, 13, 14);
        Assert.Equal((ushort)SyncedButtons.OffensiveSpell, cmds[1].Up);
        Assert.Equal(0, cmds[1].Pressed);
        Assert.Equal(0, cmds[2].Up);
        Assert.False(_director.HasWork);
    }

    [Fact]
    public void Edges_wait_for_a_frame_that_appends_a_command()
    {
        var lease = Acquire(InputChannels.Buttons);
        Pass(1);
        lease.Press(SyncedButtons.Primary);
        NextFrame();
        Pass(); // unfocused frame: nothing appended
        NextFrame();
        var cmds = Pass(1, 2);
        Assert.Equal((ushort)SyncedButtons.Primary, cmds[1].Down);
    }

    [Fact]
    public void A_tap_is_down_on_one_command_and_up_on_the_next_frame()
    {
        var lease = Acquire(InputChannels.Buttons);
        Pass(4); // seeds the command stream
        lease.Tap(SyncedButtons.Interact);
        NextFrame();
        var cmds = Pass(4, 5, 6);
        Assert.False(cmds[0].Patched);
        Assert.Equal((ushort)SyncedButtons.Interact, cmds[1].Down);
        Assert.Equal(0, cmds[2].Down);
        Assert.Equal((ushort)SyncedButtons.Interact, cmds[2].Pressed);
        NextFrame();
        cmds = Pass(7);
        Assert.Equal((ushort)SyncedButtons.Interact, cmds[0].Up);
        Assert.Equal(0, cmds[0].Pressed);
        Assert.Equal(0, lease.Taps);
    }

    [Fact]
    public void Yielding_buttons_release_them_and_resuming_presses_again()
    {
        var lease = Acquire(InputChannels.Buttons);
        lease.Press(SyncedButtons.Primary);
        Pass(1);
        _rt.Activity = UserActivity.ButtonKeys;
        NextFrame();
        var cmds = Pass(2);
        Assert.Equal((ushort)SyncedButtons.Primary, cmds[0].Up);
        _rt.Activity = UserActivity.None;
        NextFrame(2.0);
        cmds = Pass(3);
        Assert.Equal((ushort)SyncedButtons.Primary, cmds[0].Down);
    }

    [Fact]
    public void Ending_a_move_lease_zeroes_the_next_commands_walk_once()
    {
        var lease = Acquire(InputChannels.Move);
        lease.Move(0, 1);
        Pass(1);
        lease.Dispose();
        NextFrame();
        var cmds = Pass(2, 3);
        Assert.Equal(0f, cmds[0].MoveZ);
        Assert.Equal(0f, cmds[1].MoveZ);
        Assert.False(_director.HasWork);
        NextFrame();
        var later = new InputCommandView { ClientFrame = 4, MoveZ = 0.7f };
        _director.Prepare();
        _director.CommandThreshold(Controller, 4);
        _director.PatchCommand(ref later, first: true);
        Assert.Equal(0.7f, later.MoveZ); // the player's own walk is left alone afterwards
        Assert.False(later.Patched);
    }

    [Fact]
    public void Read_back_says_whether_the_game_had_already_copied_the_intent()
    {
        var lease = Acquire(InputChannels.Aim | InputChannels.Move);
        lease.AimAt(new UnitVec3(703, 0, -2796));
        lease.Move(1, 0);
        var w = _director.Prepare();

        var cmd = new InputCommandView { ClientFrame = 9, MoveX = 1, AimPosition = w.AimPosition };
        _director.CommandThreshold(Controller, 9);
        _director.PatchCommand(ref cmd, first: true);
        Assert.True(_director.LastSent.SourceMatched);

        var stale = new InputCommandView { ClientFrame = 10, AimPosition = new UnitVec3(0, 0, 0) };
        _director.PatchCommand(ref stale, first: false);
        Assert.False(_director.LastSent.SourceMatched);
        Assert.Equal(w.AimPosition, stale.AimPosition);
        Assert.Equal(1f, stale.MoveX);
        Assert.Equal(1f, lease.LastSent.MoveX);
    }

    [Fact]
    public void A_restarted_connection_re_seeds_from_its_newest_command()
    {
        var lease = Acquire(InputChannels.Move);
        lease.Move(1, 0);
        Pass(5000);
        NextFrame();
        var cmds = Pass(3, 4);
        Assert.False(cmds[0].Patched);
        Assert.True(cmds[1].Patched);
    }

    [Fact]
    public void Reacquiring_after_an_idle_gap_starts_from_the_newest_command_again()
    {
        var first = Acquire(InputChannels.Buttons);
        first.Press(SyncedButtons.Primary);
        Pass(100);
        first.Dispose();
        NextFrame();
        Pass(101); // the release edge goes out; the director is idle after this
        Assert.False(_director.HasWork);

        // Ten seconds later, in the same world, the buffer holds commands the game already sent.
        _rt.Now += 10;
        var second = Acquire(InputChannels.Buttons);
        second.Tap(SyncedButtons.Primary);
        NextFrame();
        var cmds = Pass(700, 701, 702);
        Assert.False(cmds[0].Patched);
        Assert.False(cmds[1].Patched);
        Assert.Equal((ushort)SyncedButtons.Primary, cmds[2].Down);
    }

    [Fact]
    public void An_ended_handler_cannot_take_back_a_preempted_channel()
    {
        var victim = Acquire(InputChannels.Aim, holder: "victim");
        InputAcquireResult? retry = null;
        victim.Ended += why => retry = _director.TryAcquire("victim", new InputLeaseOptions(InputChannels.Aim, preempt: true), out _);
        var bot = Acquire(InputChannels.Aim, preempt: true, holder: "bot");
        Assert.Equal(InputAcquireResult.Busy, retry);
        Assert.Single(_director.Leases);
        Assert.Equal("bot", _director.HolderOf(InputChannels.Aim));
        Assert.True(bot.Active);
    }

    [Fact]
    public void The_release_key_holds_off_new_leases_for_a_moment()
    {
        var lease = Acquire(InputChannels.Move);
        InputAcquireResult? retry = null;
        lease.Ended += why => retry = _director.TryAcquire("again", new InputLeaseOptions(InputChannels.Move), out _);
        _director.ReleaseAll(InputLeaseEnd.UserPanic);
        Assert.Equal(InputAcquireResult.Busy, retry);
        Assert.Equal(InputAcquireResult.Busy, _director.TryAcquire("later", new InputLeaseOptions(InputChannels.Move), out _));
        _rt.Now += InputDirector.PanicHoldSeconds + 0.1;
        Acquire(InputChannels.Move, holder: "later");
    }

    [Fact]
    public void A_tap_asked_for_while_the_buttons_are_paused_is_dropped()
    {
        var lease = Acquire(InputChannels.Buttons);
        Pass(1);
        _rt.Activity = UserActivity.ButtonKeys;
        lease.Tap(SyncedButtons.Interact);
        NextFrame();
        Assert.Equal(0, Pass(2)[0].Down);
        _rt.Activity = UserActivity.None;
        NextFrame(2.0);
        var cmds = Pass(3);
        Assert.Equal(0, cmds[0].Down);
        Assert.Equal(0, cmds[0].Pressed);
    }

    [Fact]
    public void Status_reports_leases_flow_and_suspension()
    {
        var lease = Acquire(InputChannels.Aim);
        Assert.False(_director.Status().CommandsFlowing);
        Pass(1);
        var status = _director.Status();
        Assert.True(status.CommandsFlowing);
        Assert.Equal(1, status.Leases);
        Assert.Equal(InputChannels.Aim, status.Held);
        Assert.True(status.HasLocalPlayer);
        _rt.Now += 2;
        Assert.False(_director.Status().CommandsFlowing);
        lease.Dispose();
    }
}
