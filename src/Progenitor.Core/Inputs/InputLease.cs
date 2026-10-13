using System;
using Progenitor.Units;

namespace Progenitor.Inputs;

/// <summary>
/// Ownership of some of the local player's input channels (<see cref="Channels"/>), from
/// <c>ctx.Input.TryAcquire</c>. The lease records intent; Core writes it into the game's input
/// every frame until the lease ends. Intent persists: <see cref="Move(float, float)"/> keeps
/// walking and <see cref="AimAt(UnitVec3)"/> keeps aiming until changed.
///
/// <para>Call members on the main thread (hooks, ticks, commands, <see cref="OnFrame"/>). After
/// the lease has ended every mutator is a no-op, because a lease can end between two calls
/// (preemption, disconnect, the user's release key). Calling a mutator for a channel the lease
/// does not hold throws.</para>
///
/// <para>On the frame a lease ends Core sends a button-up for every button it held and, if it
/// held <see cref="InputChannels.Move"/>, a zero walk, so the server never sees a stuck walk or
/// button. Aim is not restored: the game refills it from the mouse the next frame.</para>
/// </summary>
public sealed partial class InputLease : IDisposable
{
    /// <summary>Seconds without user activity before a <see cref="UserPolicy.Yield"/> channel resumes.</summary>
    public const double YieldIdleSeconds = 1.5;

    private readonly InputDirector _director;

    // Aim intent.
    internal bool HasAim;
    internal bool AimIsTarget;
    internal UnitVec3 AimPoint;
    internal InputEntityRef AimTarget;
    internal bool AimResolved; // AimPoint holds a usable point (a target resolved at least once)

    // Hover intent.
    internal bool HasHover;
    internal InputEntityRef HoverTarget;

    // Move intent.
    internal float MoveX, MoveZ;

    // Buttons intent: held bits with a per-bit expiry, and taps not yet sent.
    internal ushort Held;
    internal readonly double[] HoldUntil = new double[16];
    internal ushort Taps;
    internal ushort ComposedTaps; // taps in this frame's write, cleared once a command carried them

    internal InputLease(InputDirector director, string holder, InputLeaseOptions options, long worldToken,
        InputEntityRef controller, InputEntityRef character)
    {
        _director = director;
        Holder = holder;
        Channels = options.Channels;
        User = options.User;
        Reason = options.Reason;
        WorldToken = worldToken;
        Controller = controller;
        Character = character;
    }

    /// <summary>The owning plugin's GUID (or the framework holder's name).</summary>
    public string Holder { get; }

    public InputChannels Channels { get; }

    public UserPolicy User { get; }

    public string? Reason { get; }

    internal long WorldToken { get; }

    internal InputEntityRef Controller { get; }

    internal InputEntityRef Character { get; }

    /// <summary>False once the lease has ended.</summary>
    public bool Active => EndReason == null;

    /// <summary>Why the lease ended; null while active.</summary>
    public InputLeaseEnd? EndReason { get; private set; }

    /// <summary>Raised once when the lease ends, on the main thread. Exceptions are logged.</summary>
    public event Action<InputLeaseEnd>? Ended;

    /// <summary>
    /// Called once per frame before Core writes the input, with fresh client positions: the place to
    /// steer (update <see cref="Move(float, float)"/>, retarget aim). Exceptions are logged once and
    /// the lease continues.
    /// </summary>
    public Action<InputLease>? OnFrame { get; set; }

    /// <summary>Held channels paused this frame because the user is using them (<see cref="UserPolicy.Yield"/> only).</summary>
    public InputChannels SuspendedChannels { get; internal set; }

    /// <summary>True when any held channel is paused for the user.</summary>
    public bool Suspended => SuspendedChannels != InputChannels.None;

    /// <summary><see cref="AimAt(UnitVec3)"/>'s unit (or the hovered unit) could not be found; the last aim point is kept and hover is cleared.</summary>
    public bool TargetLost { get; internal set; }

    /// <summary>The newest outgoing command Core checked (shared by every lease).</summary>
    public InputReadback LastSent => _director.LastSent;

    internal bool CallbackFailed;

    // ---- aim --------------------------------------------------------------------------------------

    /// <summary>
    /// Aim at a world point. The aim point is (x, character y, z), the floor point the game builds
    /// from the cursor; the direction is flat and the length is the flat distance.
    /// </summary>
    public void AimAt(UnitVec3 point)
    {
        if (!Mutable(InputChannels.Aim)) return;
        RequireFinite(point, nameof(point));
        HasAim = true;
        AimIsTarget = false;
        AimPoint = point;
        AimResolved = true;
        TargetLost = false;
    }

    internal void AimAtTarget(InputEntityRef target)
    {
        if (!Mutable(InputChannels.Aim)) return;
        HasAim = true;
        AimIsTarget = true;
        AimTarget = target;
        AimResolved = false;
        TargetLost = false;
    }

    /// <summary>Hand aim back to the mouse, keeping the lease.</summary>
    public void ClearAim()
    {
        if (!Mutable(InputChannels.Aim)) return;
        HasAim = false;
        AimIsTarget = false;
    }

    // ---- hover ------------------------------------------------------------------------------------

    internal void HoverTargetSet(InputEntityRef target)
    {
        if (!Mutable(InputChannels.Hover)) return;
        HasHover = true;
        HoverTarget = target;
    }

    /// <summary>Hand the hovered unit back to the mouse, keeping the lease.</summary>
    public void ClearHover()
    {
        if (!Mutable(InputChannels.Hover)) return;
        HasHover = false;
    }

    // ---- move -------------------------------------------------------------------------------------

    /// <summary>
    /// Walk in world direction (x, z) until changed. A vector longer than 1 is normalized; shorter
    /// is kept (a partial stick); zero stands still.
    /// </summary>
    public void Move(float x, float z)
    {
        if (!Mutable(InputChannels.Move)) return;
        if (!float.IsFinite(x) || !float.IsFinite(z)) throw new ArgumentOutOfRangeException(nameof(x), "Move direction must be finite.");
        float len = MathF.Sqrt((x * x) + (z * z));
        if (len > 1f)
        {
            x /= len;
            z /= len;
        }

        MoveX = x;
        MoveZ = z;
    }

    /// <summary>Stand still (zero walk). The lease keeps the channel.</summary>
    public void Stop()
    {
        if (!Mutable(InputChannels.Move)) return;
        MoveX = 0f;
        MoveZ = 0f;
    }

    // ---- buttons ----------------------------------------------------------------------------------

    /// <summary>Hold buttons, for <paramref name="seconds"/> or until <see cref="Release"/>. Each bit keeps its own expiry.</summary>
    public void Press(SyncedButtons buttons, float? seconds = null)
    {
        if (!Mutable(InputChannels.Buttons)) return;
        if (seconds is { } s && (!float.IsFinite(s) || s <= 0f))
            throw new ArgumentOutOfRangeException(nameof(seconds), "Hold time must be positive and finite.");
        ushort mask = (ushort)buttons;
        double until = seconds is { } secs ? _director.Now + secs : double.MaxValue;
        Held |= mask;
        for (int bit = 0; bit < 16; bit++)
            if ((mask & (1 << bit)) != 0) HoldUntil[bit] = until;
    }

    /// <summary>Let go of buttons.</summary>
    public void Release(SyncedButtons buttons)
    {
        if (!Mutable(InputChannels.Buttons)) return;
        Held &= (ushort)~(ushort)buttons;
    }

    /// <summary>
    /// Press on the next outgoing command and release on the next frame's. A tap asked for while
    /// the buttons are paused for the user (<see cref="UserPolicy.Yield"/>) is dropped.
    /// </summary>
    public void Tap(SyncedButtons buttons)
    {
        if (!Mutable(InputChannels.Buttons)) return;
        Taps |= (ushort)buttons;
    }

    /// <summary>End the lease (<see cref="InputLeaseEnd.Released"/>). Idempotent.</summary>
    public void Dispose() => _director.End(this, InputLeaseEnd.Released);

    public override string ToString() =>
        Holder + " [" + Channels + ", " + User + (Reason == null ? "" : ", " + Reason) + "]"
        + (Active ? "" : " ended " + EndReason);

    // ---- internals --------------------------------------------------------------------------------

    /// <summary>Ends the lease; returns false when it had already ended.</summary>
    internal bool MarkEnded(InputLeaseEnd reason, Action<string> warn)
    {
        if (EndReason != null) return false;
        EndReason = reason;
        OnFrame = null;
        var handlers = Ended;
        Ended = null;
        if (handlers != null)
        {
            try { handlers(reason); }
            catch (Exception e) { warn("Input: " + Holder + "'s Ended handler threw: " + e.Message); }
        }

        return true;
    }

    private bool Mutable(InputChannels channel)
    {
        _director.RequireMainThread();
        if (!Active) return false;
        if ((Channels & channel) == 0)
            throw new InvalidOperationException("This input lease does not hold the " + channel + " channel (it holds " + Channels + ").");
        return true;
    }

    private static void RequireFinite(UnitVec3 v, string name)
    {
        if (!float.IsFinite(v.X) || !float.IsFinite(v.Y) || !float.IsFinite(v.Z))
            throw new ArgumentOutOfRangeException(name, "Position must be finite.");
    }
}
