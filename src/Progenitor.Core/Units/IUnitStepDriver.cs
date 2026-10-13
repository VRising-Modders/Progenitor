namespace Progenitor.Units;

/// <summary>What starting one step told the sequencer.</summary>
internal enum UnitStepResult
{
    /// <summary>The step is under way; the sequencer waits for a notify or a timer.</summary>
    Running = 0,

    /// <summary>The step is finished on this tick.</summary>
    Done = 1,

    /// <summary>The write was refused but the sequence continues (Animate only).</summary>
    Skipped = 2,

    /// <summary>The write was refused and the sequence stops.</summary>
    Failed = 3,
}

/// <summary>
/// The seam <see cref="UnitSequencer"/> drives. <see cref="UnitHandle"/> implements it over
/// the live runtime; tests implement it with a few counters. Everything the sequencer
/// decides — ordering, completion, what a failure does, Stop semantics — lives above this
/// line and therefore has no game dependency.
/// </summary>
internal interface IUnitStepDriver
{
    /// <summary>False once the unit is disposed or was never enabled; the sequence ends Failed.</summary>
    bool Alive { get; }

    /// <summary>Post <c>GenericEnemyState.Idle</c>.</summary>
    UnitStepResult Idle();

    /// <summary>Start walking to a position. <see cref="UnitStepResult.Running"/> until Arrived / Unreachable.</summary>
    UnitStepResult GoTo(UnitVec3 position);

    /// <summary>Start following an entity. Done as soon as the write sticks.</summary>
    UnitStepResult Follow(UnitId target);

    /// <summary>
    /// The sequencer armed a wait for the <see cref="GoTo"/> it just asked for, and this is
    /// the stamp it will match the arrival against (R56 finding 1). The driver carries it back
    /// on the raise; a move the driver starts on its own carries
    /// <see cref="UnitMoveTicket.None"/> and so cannot complete a sequenced step.
    /// </summary>
    void MoveArmed(UnitMoveTicket ticket);

    /// <summary>Turn toward an entity.</summary>
    UnitStepResult Face(UnitId target);

    /// <summary>Turn toward a position.</summary>
    UnitStepResult Face(UnitVec3 position);

    /// <summary>
    /// Play a clip. <see cref="UnitStepResult.Skipped"/> when the write is refused: the
    /// sequencer still holds the step for its duration.
    /// </summary>
    UnitStepResult Animate(string animationName, float seconds);

    /// <summary>One Info line per step start and one per sequence end.</summary>
    void Log(string line);

    /// <summary>
    /// Where the unit is now, when the driver can say. The wait-forever reminder is the only
    /// caller: a reminder is a progress report, and the difference between "still walking" and
    /// "wedged" is whether the unit moved between two of them (R58 finding 8). A driver that
    /// cannot answer gets a reminder with no diagnosis attached rather than a guessed one, so
    /// the default is <c>false</c> and nothing below this line has to implement it.
    /// <para>
    /// <b>False means "I could not read it", not "here is the last one I saw"</b> (job FUNIT6,
    /// R59 finding 5). The real handle answers this straight off the runtime rather than off
    /// its cached <c>Position</c>: a unit killed mid-walk is still <see cref="Alive"/> to the
    /// sequencer (that check consults no runtime), so a cached answer froze and the reminder
    /// diagnosed a stall on a unit that no longer existed.
    /// </para>
    /// </summary>
    bool TryGetPosition(out UnitVec3 position)
    {
        position = default;
        return false;
    }

    /// <summary>Publish <see cref="UnitStepCompleted"/>.</summary>
    void StepCompleted(UnitStep step, int index, int count, UnitStepOutcome outcome, string reason);

    /// <summary>Publish <see cref="UnitSequenceCompleted"/>. Not called for a silent teardown stop.</summary>
    void SequenceCompleted(UnitSequenceOutcome outcome, int stepsCompleted, int count, string reason);
}
