namespace Progenitor.Units;

/// <summary>
/// The step sequencer on one unit handle (<see cref="IUnitHandle.Behaviour"/>).
/// <para>
/// This is <b>behavior access</b>, not behavior authoring. Progenitor never compiles or
/// edits a behavior-tree blob: the unit runs a vanilla tree (its prefab's, or one selected
/// at spawn through <c>behaviourTree:</c>), the sequencer forces
/// <c>GenericEnemyState.Idle</c> so that tree neither wanders nor fights, and every step
/// below is a module-owned write driven from the same main-thread tick the rest of Units
/// polls on.
/// </para>
/// <para>
/// Completion rules: <see cref="UnitStepKind.GoTo"/> ends on <see cref="UnitArrived"/> and
/// fails on <see cref="UnitUnreachable"/>; <see cref="UnitStepKind.Wait"/> and
/// <see cref="UnitStepKind.Animate"/> end on a module timer;
/// <see cref="UnitStepKind.Idle"/>, <see cref="UnitStepKind.Follow"/>, the two Face kinds
/// and <see cref="UnitStepKind.Invoke"/> end on the tick that started them. A failed step
/// stops the sequence (<see cref="UnitSequenceOutcome.Failed"/>); a refused
/// <see cref="UnitStepKind.Animate"/> is the one exception and is reported
/// <see cref="UnitStepOutcome.Skipped"/>.
/// </para>
/// </summary>
public interface IUnitBehaviour
{
    /// <summary>True between the first step starting and the sequence ending.</summary>
    bool IsPlaying { get; }

    /// <summary>Zero-based index of the running step, or -1 when nothing is playing.</summary>
    int StepIndex { get; }

    /// <summary>Length of the sequence handed to the last <see cref="Play"/>, or 0.</summary>
    int StepCount { get; }

    /// <summary>
    /// Start a sequence. A sequence already playing is stopped first (one
    /// <see cref="UnitSequenceCompleted"/> with <see cref="UnitSequenceOutcome.Stopped"/>),
    /// then this one begins with its first step on the calling tick. An empty or null array
    /// only stops what was playing. No-op on a disabled or disposed handle.
    /// Main-thread only, like the rest of <see cref="Units"/>.
    /// </summary>
    void Play(params UnitStep[] steps);

    /// <summary>
    /// Stop the running sequence and publish <see cref="UnitSequenceCompleted"/> with
    /// <see cref="UnitSequenceOutcome.Stopped"/>. No-op when nothing is playing. The unit
    /// keeps whatever steering the last step left it in; call
    /// <see cref="IUnitHandle.GoTo"/> / <see cref="IUnitHandle.Follow"/> to change it.
    /// </summary>
    void Stop();
}
