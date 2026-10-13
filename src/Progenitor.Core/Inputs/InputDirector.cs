using System;
using System.Collections.Generic;
using Progenitor.Units;

namespace Progenitor.Inputs;

/// <summary>
/// Game-free state machine behind <c>ctx.Input</c>: the leases, per-channel ownership, the
/// user-activity yield, what to write into <c>EntityInput</c> each frame, and how to patch and
/// read back the outgoing input commands. One per process (<see cref="Current"/>) because there is
/// one local player; the tests build their own over a fake <see cref="IInputRuntime"/>.
///
/// <para><b>Ownership.</b> Each channel has at most one lease. A request overlapping a held
/// channel returns <see cref="InputAcquireResult.Busy"/> and acquires nothing, unless it asks
/// to preempt, which ends the overlapping leases first (<see cref="InputLeaseEnd.Preempted"/>).
/// Unlike the camera, the last request does not silently win: a takeover of the character's
/// hands must be asked for.</para>
///
/// <para><b>Frame.</b> The runtime calls <see cref="Prepare"/> after <c>GameplayInputSystem</c>
/// and again before <c>CreateInputCommandsSystem</c>, and writes the returned
/// <see cref="InputWrite"/> into the controller's and the character's <c>EntityInput</c>. The
/// first call of a frame validates the leases, samples user activity and runs the lease
/// callbacks. After <c>CreateInputCommandsSystem</c> it walks the commands newer than the last
/// one checked (<see cref="CommandThreshold"/>, <see cref="PatchCommand"/>,
/// <see cref="CommandsDone"/>): it records whether the game had copied the intent
/// (<see cref="InputReadback.SourceMatched"/>), then writes the intent into the command anyway,
/// which is what the Dev bot driver proved live on 2026-09-24 for movement.</para>
///
/// <para><b>Buttons.</b> Only the pressed level goes into <c>EntityInput</c>. Press and release
/// edges are derived from what was last sent and put on the first new command of a frame only,
/// because one frame can append several commands and a repeated edge reads as a second press.
/// When a lease ends, or its buttons pause for the user, the next command carries the release
/// edge; the director stays armed (<see cref="HasWork"/>) until that command went out. A fault or
/// layout refusal disables the director at once and sends no release.</para>
///
/// <para><b>Re-entrancy.</b> <c>Ended</c> handlers run inside <see cref="End"/>; an acquire from
/// one returns <see cref="InputAcquireResult.Busy"/>, so a preempt or the user's release key
/// cannot be undone by the lease it ended. After the release key every acquire is refused for
/// <see cref="PanicHoldSeconds"/>.</para>
/// </summary>
internal sealed class InputDirector
{
    /// <summary>New commands within this many seconds count as flowing.</summary>
    internal const double FlowWindowSeconds = 1.0;

    /// <summary>Aim closer than this to the character is skipped for the frame (no direction).</summary>
    internal const float MinAimLength = 1e-3f;

    /// <summary>A newest <c>ClientFrame</c> this far below the last patched one means the connection restarted.</summary>
    internal const int ClientFrameRestartSlack = 64;

    private const float Tolerance = 1e-3f;

    private static InputDirector? s_current;

    private readonly IInputRuntime _rt;
    private readonly List<InputLease> _leases = new();

    private string? _layoutIssue;
    private string? _fault;

    // Acquires are refused while a lease is ending (its Ended handler must not grab a channel
    // mid-preempt or mid-release) and for a moment after the user's release key.
    private int _ending;
    private double _panicUntil = double.NegativeInfinity;

    /// <summary>Seconds after the user's release key during which every acquire is refused.</summary>
    internal const double PanicHoldSeconds = 2.0;
    private string? _issue;

    // User activity: when each device group was last used.
    private double _pointerAt = double.NegativeInfinity;
    private double _moveKeysAt = double.NegativeInfinity;
    private double _buttonKeysAt = double.NegativeInfinity;

    // Frame.
    private int _frame = int.MinValue;
    private InputWrite _write;

    // Outgoing commands.
    private long _commandToken;
    private InputEntityRef _commandOwner;
    private int _lastPatchedFrame = int.MinValue;
    private ushort _prevSent;
    private bool _zeroMovePending;
    private double _lastNewCommandAt = double.NegativeInfinity;

    internal InputDirector(IInputRuntime runtime) => _rt = runtime ?? throw new ArgumentNullException(nameof(runtime));

    /// <summary>The process's director; null before Core installed it on a client.</summary>
    internal static InputDirector? Current => s_current;

    internal static InputDirector Install(IInputRuntime runtime) => s_current ??= new InputDirector(runtime);

    /// <summary>Core uninstall: end every lease and drop the director.</summary>
    internal static void Uninstall()
    {
        var director = s_current;
        s_current = null;
        director?.ReleaseAll(InputLeaseEnd.CoreUnloaded);
    }

    internal double Now => _rt.Now;

    internal InputReadback LastSent { get; private set; }

    internal IReadOnlyList<InputLease> Leases => _leases;

    /// <summary>True while a lease exists or a release (button-up, zero walk) has not gone out yet.</summary>
    internal bool HasWork => !Disabled && (_leases.Count > 0 || _prevSent != 0 || _zeroMovePending);

    internal bool LayoutRefused => _layoutIssue != null;

    /// <summary>Layout refused or faulted: nothing is written any more.</summary>
    internal bool Disabled => _layoutIssue != null || _fault != null;

    /// <summary>A hook threw: end every lease and refuse new ones for this generation.</summary>
    internal void Fault(string issue)
    {
        if (_fault != null) return;
        _fault = issue;
        _issue = issue;
        _rt.Warn("Input: " + issue + "; input injection is disabled for this Core generation.");
        ReleaseAll(InputLeaseEnd.Faulted);
        _prevSent = 0;
        _zeroMovePending = false;
    }

    internal InputChannels HeldChannels
    {
        get
        {
            var held = InputChannels.None;
            for (int i = 0; i < _leases.Count; i++) held |= _leases[i].Channels;
            return held;
        }
    }

    internal void RequireMainThread()
    {
        if (!_rt.IsMainThread)
            throw new InvalidOperationException("ctx.Input is main-thread only (hooks, ticks, commands, event handlers).");
    }

    /// <summary>The holder of one channel, or null.</summary>
    internal string? HolderOf(InputChannels channel)
    {
        for (int i = 0; i < _leases.Count; i++)
            if ((_leases[i].Channels & channel) != 0) return _leases[i].Holder;
        return null;
    }

    // ---- ownership -------------------------------------------------------------------------------

    internal InputAcquireResult TryAcquire(string holder, InputLeaseOptions options, out InputLease? lease)
    {
        RequireMainThread();
        lease = null;
        if (options.Channels == InputChannels.None || (options.Channels & ~InputChannels.All) != 0)
            throw new ArgumentException("InputLeaseOptions.Channels must name one or more of Aim, Hover, Move, Buttons.", nameof(options));
        if (options.User != UserPolicy.Yield && options.User != UserPolicy.Override)
            throw new ArgumentException("InputLeaseOptions.User is not a UserPolicy value.", nameof(options));
        if (_layoutIssue != null) return InputAcquireResult.LayoutRefused;
        if (_fault != null) return InputAcquireResult.Faulted;
        if (_ending > 0 || _rt.Now < _panicUntil) return InputAcquireResult.Busy;

        long token = _rt.WorldToken;
        if (token == 0 || !_rt.TryGetLocalPlayer(out var controller, out var character))
            return InputAcquireResult.NoLocalPlayer;

        List<InputLease>? overlapping = null;
        for (int i = 0; i < _leases.Count; i++)
        {
            if ((_leases[i].Channels & options.Channels) == 0) continue;
            (overlapping ??= new List<InputLease>()).Add(_leases[i]);
        }

        if (overlapping != null)
        {
            if (!options.Preempt) return InputAcquireResult.Busy;
            foreach (var old in overlapping)
            {
                _rt.Warn("Input: " + holder + " preempted " + old + ".");
                End(old, InputLeaseEnd.Preempted);
            }
        }

        // A world change since the command state was last seen belongs to the old connection.
        SyncCommandToken(token);
        // Idle since the last lease: the command stream moved on without us, so start again from
        // its newest command instead of treating everything in the buffer as new.
        if (_leases.Count == 0 && _prevSent == 0 && !_zeroMovePending)
        {
            _commandOwner = default;
            _lastPatchedFrame = int.MinValue;
        }
        lease = new InputLease(this, holder, options, token, controller, character);
        _leases.Add(lease);
        if (options.User == UserPolicy.Override)
            _rt.Warn("Input: " + lease + " acquired with UserPolicy.Override: it keeps writing while the user is using the keyboard and mouse.");
        else
            _rt.Log("Input: " + lease + " acquired.");
        return InputAcquireResult.Acquired;
    }

    /// <summary>End one lease. Idempotent.</summary>
    internal void End(InputLease lease, InputLeaseEnd reason)
    {
        if (lease == null || !lease.Active) return;
        _leases.Remove(lease);
        if ((lease.Channels & InputChannels.Move) != 0) _zeroMovePending = true;
        _rt.Log("Input: " + lease.Holder + " lease ended (" + reason + ").");
        _ending++;
        try { lease.MarkEnded(reason, _rt.Warn); }
        finally { _ending--; }
    }

    /// <summary>End every lease; returns how many ended.</summary>
    internal int ReleaseAll(InputLeaseEnd reason)
    {
        if (reason == InputLeaseEnd.UserPanic) _panicUntil = _rt.Now + PanicHoldSeconds;
        var all = _leases.ToArray();
        foreach (var lease in all) End(lease, reason);
        return all.Length;
    }

    /// <summary>The layout check failed: end every lease and refuse new ones.</summary>
    internal void RefuseLayout(string issue)
    {
        if (_layoutIssue != null) return;
        _layoutIssue = issue;
        _issue = issue;
        _rt.Warn("Input: " + issue + "; input injection is disabled for this Core generation.");
        ReleaseAll(InputLeaseEnd.LayoutRefused);
        _prevSent = 0;
        _zeroMovePending = false;
    }

    /// <summary>Record a problem for <see cref="Status"/>.</summary>
    internal void NoteIssue(string? issue) => _issue = issue;

    /// <summary>
    /// Ends leases whose world or local player is gone. Called every frame while
    /// <see cref="HasWork"/>, and at the start of <see cref="Prepare"/>.
    /// </summary>
    internal void Validate()
    {
        long token = _rt.WorldToken;
        if (_leases.Count > 0)
        {
            if (token == 0)
            {
                ReleaseAll(InputLeaseEnd.Disconnected);
            }
            else
            {
                bool havePlayer = _rt.TryGetLocalPlayer(out var controller, out var character);
                foreach (var lease in _leases.ToArray())
                {
                    if (lease.WorldToken != token) End(lease, InputLeaseEnd.WorldChanged);
                    else if (!havePlayer || !controller.Equals(lease.Controller) || !character.Equals(lease.Character))
                        End(lease, InputLeaseEnd.CharacterChanged);
                }
            }
        }

        SyncCommandToken(token);
    }

    private void SyncCommandToken(long token)
    {
        if (token == _commandToken) return;
        // A new world is a new connection: its commands restart, and nothing we sent to the old
        // one needs releasing.
        _commandToken = token;
        _commandOwner = default;
        _lastPatchedFrame = int.MinValue;
        _prevSent = 0;
        _zeroMovePending = false;
        _lastNewCommandAt = double.NegativeInfinity;
        LastSent = default;
    }

    // ---- frame -----------------------------------------------------------------------------------

    /// <summary>
    /// What to write into <c>EntityInput</c> now. The first call of a frame validates, samples
    /// user activity and runs the lease callbacks; later calls in the frame recompute the write.
    /// </summary>
    internal InputWrite Prepare()
    {
        Validate();
        int frame = _rt.Frame;
        double now = _rt.Now;
        if (frame != _frame)
        {
            _frame = frame;
            if (_leases.Count > 0)
            {
                var activity = _rt.SampleActivity();
                if ((activity & UserActivity.Pointer) != 0) _pointerAt = now;
                if ((activity & UserActivity.MoveKeys) != 0) _moveKeysAt = now;
                if ((activity & UserActivity.ButtonKeys) != 0) _buttonKeysAt = now;
                RunCallbacks();
            }
        }

        _write = Compose(now);
        return _write;
    }

    private void RunCallbacks()
    {
        foreach (var lease in _leases.ToArray())
        {
            var callback = lease.OnFrame;
            if (callback == null || !lease.Active) continue;
            try
            {
                callback(lease);
            }
            catch (Exception e)
            {
                if (!lease.CallbackFailed)
                {
                    lease.CallbackFailed = true;
                    _rt.Warn("Input: " + lease.Holder + "'s OnFrame threw (logged once; the lease continues): " + e);
                }
            }
        }
    }

    /// <summary>Channels a Yield lease pauses at <paramref name="now"/>.</summary>
    internal InputChannels SuspendedAt(double now)
    {
        var suspended = InputChannels.None;
        if (now - _moveKeysAt < InputLease.YieldIdleSeconds) suspended |= InputChannels.Move;
        if (now - _pointerAt < InputLease.YieldIdleSeconds) suspended |= InputChannels.Aim | InputChannels.Hover;
        if (now - _buttonKeysAt < InputLease.YieldIdleSeconds) suspended |= InputChannels.Buttons;
        return suspended;
    }

    private InputWrite Compose(double now)
    {
        var w = default(InputWrite);
        var userSuspended = SuspendedAt(now);
        bool haveCharacter = false;
        UnitVec3 character = default;

        for (int i = 0; i < _leases.Count; i++)
        {
            var lease = _leases[i];
            var suspended = lease.User == UserPolicy.Yield ? userSuspended & lease.Channels : InputChannels.None;
            lease.SuspendedChannels = suspended;
            var live = lease.Channels & ~suspended;

            if ((live & InputChannels.Move) != 0)
            {
                w.WriteMove = true;
                w.MoveX = lease.MoveX;
                w.MoveZ = lease.MoveZ;
            }

            if ((live & InputChannels.Aim) != 0)
            {
                if (lease.HasAim)
                {
                    bool havePoint = true;
                    if (lease.AimIsTarget)
                    {
                        if (_rt.TryGetPosition(lease.AimTarget, out var p))
                        {
                            lease.AimPoint = p;
                            lease.AimResolved = true;
                            lease.TargetLost = false;
                        }
                        else
                        {
                            // Gone (dead, despawned): keep the last point, as a player whose target
                            // vanished keeps facing where it was.
                            lease.TargetLost = true;
                            havePoint = lease.AimResolved;
                        }
                    }

                    if (havePoint)
                    {
                        if (!haveCharacter) haveCharacter = _rt.TryGetPosition(lease.Character, out character);
                        if (haveCharacter && AimFields(character, lease.AimPoint, out var plane, out var dir, out float len))
                        {
                            w.WriteAim = true;
                            w.AimPosition = plane;
                            w.AimPlane = plane;
                            w.AimDirection = dir;
                            w.AimLength = len;
                            w.WriteRotation = true;
                            w.Rotation = dir;
                        }
                    }
                }
                else if ((live & InputChannels.Move) != 0 && (lease.MoveX != 0f || lease.MoveZ != 0f))
                {
                    // The lease owns facing but aims nowhere: face the walk, as the bot driver did.
                    float len = MathF.Sqrt((lease.MoveX * lease.MoveX) + (lease.MoveZ * lease.MoveZ));
                    w.WriteRotation = true;
                    w.Rotation = new UnitVec3(lease.MoveX / len, 0f, lease.MoveZ / len);
                }
            }

            if ((live & InputChannels.Hover) != 0 && lease.HasHover)
            {
                w.WriteHover = true;
                if (_rt.TryGetNetworkId(lease.HoverTarget, out var net))
                {
                    w.HoverEntity = lease.HoverTarget;
                    w.HoverNetworkId = net;
                }
                else
                {
                    lease.TargetLost = true;
                    w.HoverEntity = default;
                    w.HoverNetworkId = default;
                }
            }

            if ((live & InputChannels.Buttons) != 0)
            {
                for (int bit = 0; bit < 16; bit++)
                    if ((lease.Held & (1 << bit)) != 0 && now > lease.HoldUntil[bit]) lease.Held &= (ushort)~(1 << bit);
                lease.ComposedTaps = lease.Taps;
                w.Pressed |= (ushort)(lease.Held | lease.Taps);
            }
            else
            {
                // Paused for the user (or not held): a tap asked for now is dropped, not fired
                // seconds later when the pause ends.
                if ((suspended & InputChannels.Buttons) != 0) lease.Taps = 0;
                lease.ComposedTaps = 0;
            }
        }

        return w;
    }

    /// <summary>
    /// The aim fields for a floor point, as the game builds them from the cursor: both positions
    /// are (x, character y, z), the direction is flat and normalized, the length is the flat
    /// distance (read live 2026-09-25 from a cursor-driven player). False when the point is on the
    /// character.
    /// </summary>
    internal static bool AimFields(UnitVec3 character, UnitVec3 point, out UnitVec3 plane, out UnitVec3 direction, out float length)
    {
        plane = new UnitVec3(point.X, character.Y, point.Z);
        float dx = point.X - character.X, dz = point.Z - character.Z;
        length = MathF.Sqrt((dx * dx) + (dz * dz));
        if (!(length >= MinAimLength))
        {
            direction = default;
            return false;
        }

        direction = new UnitVec3(dx / length, 0f, dz / length);
        return true;
    }

    // ---- outgoing commands -----------------------------------------------------------------------

    /// <summary>
    /// Commands with a <c>ClientFrame</c> above the returned value are new. The first call for a
    /// buffer owner starts at the newest command (never rewriting sent history); a newest frame
    /// far below the last patched one is a restarted connection and re-seeds the same way.
    /// </summary>
    internal int CommandThreshold(InputEntityRef owner, int newestFrame)
    {
        if (!owner.Equals(_commandOwner)
            || (_lastPatchedFrame != int.MinValue && newestFrame < _lastPatchedFrame - ClientFrameRestartSlack))
        {
            _commandOwner = owner;
            _lastPatchedFrame = int.MinValue;
        }

        if (_lastPatchedFrame == int.MinValue) _lastPatchedFrame = newestFrame - 1;
        return _lastPatchedFrame;
    }

    /// <summary>
    /// Read back and patch one new command. <paramref name="first"/> is the first new command of
    /// this pass: only it carries press and release edges.
    /// </summary>
    internal void PatchCommand(ref InputCommandView cmd, bool first)
    {
        var w = _write;
        bool matched = true;
        bool patched = false;

        if (w.WriteMove)
        {
            matched &= Near(cmd.MoveX, w.MoveX) && Near(cmd.MoveZ, w.MoveZ);
            cmd.MoveX = w.MoveX;
            cmd.MoveZ = w.MoveZ;
            cmd.RawMoveX = w.MoveX;
            cmd.RawMoveZ = w.MoveZ;
            patched = true;
        }
        else if (_zeroMovePending)
        {
            cmd.MoveX = cmd.MoveZ = 0f;
            cmd.RawMoveX = cmd.RawMoveZ = 0f;
            patched = true;
        }

        if (w.WriteRotation)
        {
            cmd.Rotation = w.Rotation;
            patched = true;
        }

        if (w.WriteAim)
        {
            matched &= Near(cmd.AimPosition, w.AimPosition);
            cmd.AimPosition = w.AimPosition;
            cmd.AimPlane = w.AimPlane;
            cmd.AimDirection = w.AimDirection;
            cmd.AimLength = w.AimLength;
            patched = true;
        }

        if (w.WriteHover)
        {
            matched &= cmd.HoverNetworkId.Equals(w.HoverNetworkId);
            cmd.HoverEntity = w.HoverEntity;
            cmd.HoverNetworkId = w.HoverNetworkId;
            patched = true;
        }

        ushort pressed = w.Pressed;
        matched &= (cmd.Pressed & pressed) == pressed;
        if (pressed != 0)
        {
            cmd.Pressed |= pressed;
            patched = true;
        }

        if (first)
        {
            ushort down = (ushort)(pressed & ~_prevSent);
            ushort up = (ushort)(_prevSent & ~pressed);
            if ((down | up) != 0)
            {
                cmd.Down |= down;
                cmd.Up |= up;
                patched = true;
            }
        }

        cmd.Patched = patched;
        LastSent = new InputReadback
        {
            Seen = true,
            ClientFrame = cmd.ClientFrame,
            MoveX = cmd.MoveX,
            MoveZ = cmd.MoveZ,
            AimPosition = cmd.AimPosition,
            HoveredNetworkId = cmd.HoverNetworkId.A,
            HoveredEntity = cmd.HoverEntity,
            Pressed = (SyncedButtons)cmd.Pressed,
            SourceMatched = matched,
        };
    }

    /// <summary>End of a command pass: <paramref name="newCommands"/> commands up to <paramref name="bestFrame"/> were patched.</summary>
    internal void CommandsDone(int bestFrame, int newCommands)
    {
        if (newCommands <= 0) return;
        if (bestFrame > _lastPatchedFrame) _lastPatchedFrame = bestFrame;
        _prevSent = _write.Pressed;
        _zeroMovePending = false;
        _lastNewCommandAt = _rt.Now;
        // A tap is pressed on one command; the next frame's compose leaves it out, which sends the up edge.
        for (int i = 0; i < _leases.Count; i++)
        {
            var lease = _leases[i];
            lease.Taps &= (ushort)~lease.ComposedTaps;
            lease.ComposedTaps = 0;
        }
    }

    // ---- status ----------------------------------------------------------------------------------

    internal InputStatus Status()
    {
        var suspended = InputChannels.None;
        for (int i = 0; i < _leases.Count; i++) suspended |= _leases[i].SuspendedChannels;
        bool player = _rt.WorldToken != 0 && _rt.TryGetLocalPlayer(out _, out _);
        return new InputStatus
        {
            LayoutOk = _layoutIssue == null,
            Faulted = _fault != null,
            HasLocalPlayer = player,
            CommandsFlowing = _rt.Now - _lastNewCommandAt <= FlowWindowSeconds,
            Focused = _rt.Focused,
            Leases = _leases.Count,
            Held = HeldChannels,
            Suspended = suspended,
            LastSent = LastSent,
            Issue = _layoutIssue ?? _issue,
        };
    }

    private static bool Near(float a, float b) => MathF.Abs(a - b) <= Tolerance;

    private static bool Near(UnitVec3 a, UnitVec3 b) => Near(a.X, b.X) && Near(a.Y, b.Y) && Near(a.Z, b.Z);

    internal static void ResetForTests() => s_current = null;
}
