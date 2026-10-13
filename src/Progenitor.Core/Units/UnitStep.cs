using System;

namespace Progenitor.Units;

/// <summary>
/// What one <see cref="UnitStep"/> asks the unit to do. The sequencer maps each kind to a
/// module-owned write; none of them compiles or edits a behavior-tree blob (the v1 rule
/// from the research: select a vanilla tree, force Idle, drive the steps yourself).
/// </summary>
public enum UnitStepKind
{
    /// <summary>Post <c>GenericEnemyState.Idle</c> so the selected tree neither wanders nor fights. Completes immediately.</summary>
    Idle = 0,

    /// <summary>
    /// Walk to a world position. Completes on <see cref="UnitArrived"/>, fails on
    /// <see cref="UnitUnreachable"/>, and ends <see cref="UnitStepOutcome.TimedOut"/> when
    /// neither arrives inside the step's timeout.
    /// </summary>
    GoTo = 1,

    /// <summary>Follow an entity. A standing state, so the step completes as soon as the write sticks.</summary>
    Follow = 2,

    /// <summary>Turn toward an entity. Completes immediately.</summary>
    FaceEntity = 3,

    /// <summary>Turn toward a world position. Completes immediately.</summary>
    FacePosition = 4,

    /// <summary>Module timer, never a behavior-tree <c>Delay</c> node.</summary>
    Wait = 5,

    /// <summary>Play a named animation clip for a duration. Best effort; see <see cref="UnitStepOutcome.Skipped"/>.</summary>
    Animate = 6,

    /// <summary>Run a callback on the game main thread between two steps. Completes immediately.</summary>
    Invoke = 7,
}

/// <summary>How one step ended.</summary>
public enum UnitStepOutcome
{
    /// <summary>The step did what it said.</summary>
    Completed = 0,

    /// <summary>
    /// The step could not be performed but the sequence continues. Two ways in: a refused
    /// <see cref="UnitStepKind.Animate"/> (an animation is cosmetic, so a refused clip must not
    /// strand a patrol halfway), and a <see cref="UnitStep.Then"/> the plugin sweep neutralized
    /// because the assembly that wrote the callback unloaded — reason
    /// <c>callback dropped: &lt;assembly&gt; unloaded</c> (R58 finding 6). <see cref="UnitStepCompleted.Reason"/>
    /// on the event says which.
    /// </summary>
    Skipped = 1,

    /// <summary>The step failed. The sequence stops here.</summary>
    Failed = 2,

    /// <summary>
    /// The step ran out of time. Only <see cref="UnitStepKind.GoTo"/> ends this way: it is
    /// the one step that waits on an external event, and a unit that never moves raises
    /// neither <see cref="UnitArrived"/> nor <see cref="UnitUnreachable"/> (R55 finding 3).
    /// The likeliest cause is the documented gate — a unit does not tick its AI with no
    /// connected player in range (evidence §22) — so the sequence stops here the way a
    /// <see cref="Failed"/> step does, with one log line naming that gate.
    /// </summary>
    TimedOut = 3,
}

/// <summary>How a whole sequence ended.</summary>
public enum UnitSequenceOutcome
{
    /// <summary>Every step ran; the last one completed.</summary>
    Completed = 0,

    /// <summary>A step failed. <see cref="UnitSequenceCompleted.Reason"/> says which and why.</summary>
    Failed = 1,

    /// <summary>
    /// <see cref="IUnitBehaviour.Stop"/> or a replacing <see cref="IUnitBehaviour.Play"/>
    /// ended it. Despawn and Core uninstall stop silently and publish nothing: the handle is
    /// going away and a subscriber would be handed a disposed unit. The plugin sweep
    /// (<c>Units.SweepForUnload</c>) is the exception — a still-live sequence owned by the unloading assembly ends with a
    /// published <c>Stopped</c>, and anyone else's keeps playing with the unloading
    /// assembly's <see cref="Then"/> steps neutralized (FUNIT4, R57 finding 1).
    /// </summary>
    Stopped = 2,
}

/// <summary>
/// One hookable step in a unit behavior sequence. Immutable and game-free: the same
/// value drives the live runtime and the test fakes. Build them with the factory methods
/// (<see cref="Idle"/>, <see cref="GoTo"/>, …) and hand the array to
/// <see cref="IUnitBehaviour.Play"/>.
/// </summary>
public sealed class UnitStep
{
    /// <summary>Default clip length when <see cref="Animate(string, float)"/> is given no duration.</summary>
    public const float DefaultAnimationSeconds = 1f;

    /// <summary>
    /// How long a <see cref="GoTo(UnitVec3)"/> waits for Arrived / Unreachable before it gives
    /// up (R55 finding 3). Generous on purpose: a real walk across a courtyard is seconds, and
    /// the case this exists for — the unit never moves at all because no connected player is in
    /// range (evidence §22) — produces no event ever, so the sequence would otherwise wait for
    /// the rest of the session.
    /// </summary>
    public const float DefaultGoToTimeoutSeconds = 30f;

    /// <summary>
    /// How often a <see cref="GoTo(UnitVec3, float)"/> with <c>timeoutSeconds: 0</c> says it is
    /// still waiting. A wait with no deadline has no other way of showing up in a log, and
    /// after the move ticket (R56 finding 1) it can be a wedge rather than a long walk: a
    /// notification stamped for another step is discarded, so the only remaining ends are an
    /// arrival for this step, an unreachable, or a Stop (R57 finding 2).
    /// </summary>
    public const float NoDeadlineReminderSeconds = 60f;

    private UnitStep(UnitStepKind kind)
    {
        Kind = kind;
        Animation = "";
    }

    /// <summary>What this step asks for.</summary>
    public UnitStepKind Kind { get; private init; }

    /// <summary>Goal for <see cref="UnitStepKind.GoTo"/> / <see cref="UnitStepKind.FacePosition"/>.</summary>
    public UnitVec3 Position { get; private init; }

    /// <summary>Target for <see cref="UnitStepKind.Follow"/> / <see cref="UnitStepKind.FaceEntity"/>.</summary>
    public UnitId Target { get; private init; }

    /// <summary>Seconds for <see cref="UnitStepKind.Wait"/> and <see cref="UnitStepKind.Animate"/>. Never negative.</summary>
    public float Seconds { get; private init; }

    /// <summary>Clip name for <see cref="UnitStepKind.Animate"/>, otherwise empty.</summary>
    public string Animation { get; private init; }

    /// <summary>Callback for <see cref="UnitStepKind.Invoke"/>.</summary>
    internal Action? Callback { get; private init; }

    /// <summary>
    /// True for a <see cref="Then"/> whose callback was dropped because the assembly that
    /// wrote it is unloading (R57 finding 1). The step keeps its slot, so the rest of
    /// somebody else's sequence still runs and the indices in
    /// <see cref="UnitStepCompleted"/> still mean what they meant — and it reports
    /// <see cref="UnitStepOutcome.Skipped"/> with <see cref="CallbackDroppedReason"/> rather
    /// than <c>Completed</c> with an empty reason, so a subscriber that confirms its own
    /// side effect from the event can tell a no-op apart from a callback that ran
    /// (R58 finding 6).
    /// </summary>
    internal bool CallbackDropped { get; private init; }

    /// <summary>The assembly whose unload neutralized this step, for the Skipped reason.</summary>
    internal string CallbackDroppedFrom { get; private init; } = "";

    /// <summary>What a neutralized <see cref="Then"/> reports as its Skipped reason.</summary>
    internal string CallbackDroppedReason =>
        "callback dropped: " + (CallbackDroppedFrom.Length == 0 ? "its plugin" : CallbackDroppedFrom)
        + " unloaded";

    /// <summary>
    /// This step with its callback removed. Only a <see cref="Then"/> carries one, and only
    /// the plugin sweep calls this: a delegate written in an unloading plugin's
    /// assembly pins that assembly's collectible ALC, and taking the delegate out is enough
    /// to unpin it without stopping a sequence somebody else owns.
    /// </summary>
    internal UnitStep WithoutCallback(string assemblyName) =>
        Callback == null
            ? this
            : new UnitStep(UnitStepKind.Invoke)
            {
                CallbackDropped = true,
                CallbackDroppedFrom = assemblyName ?? "",
            };

    /// <summary>
    /// Seconds a <see cref="UnitStepKind.GoTo"/> may wait for Arrived / Unreachable before the
    /// step ends <see cref="UnitStepOutcome.TimedOut"/>. 0 means "wait forever", which is only
    /// safe when the caller has its own way out — and is never silent: the sequencer prints
    /// one line a minute for as long as the wait lasts (R57 finding 2).
    /// </summary>
    public float TimeoutSeconds { get; private init; }

    /// <summary>Force the selected tree to <c>Idle</c>. Completes on the same tick.</summary>
    public static UnitStep Idle() => new(UnitStepKind.Idle);

    /// <summary>
    /// Walk to a world position. Completes on Arrived, fails on Unreachable, and ends
    /// <see cref="UnitStepOutcome.TimedOut"/> after
    /// <see cref="DefaultGoToTimeoutSeconds"/> if neither ever comes.
    /// </summary>
    public static UnitStep GoTo(UnitVec3 position) =>
        GoTo(position, DefaultGoToTimeoutSeconds);

    /// <summary>
    /// Walk to a world position with an explicit deadline. <paramref name="timeoutSeconds"/>
    /// 0 (or negative / non-finite, which clamps to 0) waits forever — the pre-R55 behavior,
    /// and a wedge whenever the unit's AI is not ticking (evidence §22) or when something
    /// restarted the walk behind the sequence's back, so pass it only when something else can
    /// stop the sequence.
    /// <para>
    /// <b>0 is still accepted, and no longer silent (R57 finding 2).</b> Refusing it was the
    /// other option and was dropped: "wait forever" is a real contract for a caller whose way
    /// out is its own event, and turning it into a large cap would end sequences that were
    /// written to outlive any cap. Instead the sequencer logs one line every
    /// <see cref="NoDeadlineReminderSeconds"/> seconds while the wait lasts, saying how long
    /// it has waited and which of the two causes fits — so a wedge shows up in the log within
    /// a minute instead of at the end of the session.
    /// </para>
    /// </summary>
    public static UnitStep GoTo(UnitVec3 position, float timeoutSeconds) =>
        new(UnitStepKind.GoTo) { Position = position, TimeoutSeconds = Sanitise(timeoutSeconds) };

    /// <summary>Follow a real entity (player character or prefab instance). Completes as soon as the write sticks.</summary>
    public static UnitStep Follow(UnitId target) =>
        new(UnitStepKind.Follow) { Target = target };

    /// <summary>Turn toward an entity. Completes on the same tick.</summary>
    public static UnitStep Face(UnitId target) =>
        new(UnitStepKind.FaceEntity) { Target = target };

    /// <summary>Turn toward a world position. Completes on the same tick.</summary>
    public static UnitStep Face(UnitVec3 position) =>
        new(UnitStepKind.FacePosition) { Position = position };

    /// <summary>Module timer. A non-positive or non-finite value completes on the next tick.</summary>
    public static UnitStep Wait(float seconds) =>
        new(UnitStepKind.Wait) { Seconds = Sanitise(seconds) };

    /// <summary>Play a clip for <see cref="DefaultAnimationSeconds"/>.</summary>
    public static UnitStep Animate(string animationName) =>
        Animate(animationName, DefaultAnimationSeconds);

    /// <summary>
    /// Play a clip for <paramref name="seconds"/>. The step holds the sequence for that
    /// long whether or not the write landed, so a refused clip does not change the timing
    /// of everything after it.
    /// </summary>
    public static UnitStep Animate(string animationName, float seconds)
    {
        if (string.IsNullOrWhiteSpace(animationName))
            throw new ArgumentException("Animation clip name is required.", nameof(animationName));
        return new UnitStep(UnitStepKind.Animate)
        {
            Animation = animationName.Trim(),
            Seconds = Sanitise(seconds),
        };
    }

    /// <summary>Run <paramref name="action"/> on the game main thread between two steps.</summary>
    public static UnitStep Then(Action action)
    {
        if (action == null) throw new ArgumentNullException(nameof(action));
        return new UnitStep(UnitStepKind.Invoke) { Callback = action };
    }

    /// <summary>One short line per step for the log, e.g. <c>GoTo (1, 2, 3)</c>.</summary>
    public override string ToString() => Kind switch
    {
        UnitStepKind.GoTo => "GoTo " + Position,
        UnitStepKind.Follow => "Follow " + Target,
        UnitStepKind.FaceEntity => "Face " + Target,
        UnitStepKind.FacePosition => "Face " + Position,
        UnitStepKind.Wait => "Wait " + Seconds.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "s",
        UnitStepKind.Animate => "Animate '" + Animation + "' " + Seconds.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "s",
        UnitStepKind.Invoke => CallbackDropped ? "Then (" + CallbackDroppedReason + ")" : "Then",
        _ => "Idle",
    };

    private static float Sanitise(float seconds)
    {
        if (float.IsNaN(seconds) || float.IsInfinity(seconds) || seconds < 0f) return 0f;
        return seconds;
    }
}
