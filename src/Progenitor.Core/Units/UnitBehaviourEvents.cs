namespace Progenitor.Units;

/// <summary>
/// One step of a unit sequence ended. Published on <see cref="Events.EventBus"/> from the
/// main-thread poll, with the owning handle, so plugin <c>before</c>/<c>after</c>/priority
/// order is the same as every other event.
/// </summary>
public sealed class UnitStepCompleted
{
    public IUnitHandle Handle { get; }

    /// <summary>The step that ended.</summary>
    public UnitStep Step { get; }

    /// <summary>Zero-based index of <see cref="Step"/> in the sequence.</summary>
    public int Index { get; }

    /// <summary>Length of the sequence this step belongs to.</summary>
    public int Count { get; }

    public UnitStepOutcome Outcome { get; }

    /// <summary>Why it failed or was skipped; empty when it completed.</summary>
    public string Reason { get; }

    public UnitStepCompleted(IUnitHandle handle, UnitStep step, int index, int count, UnitStepOutcome outcome, string? reason)
    {
        Handle = handle;
        Step = step;
        Index = index;
        Count = count;
        Outcome = outcome;
        Reason = reason ?? "";
    }
}

/// <summary>
/// A unit sequence ended: every step ran, a step failed, or something stopped it.
/// <para>
/// <b>Despawn and Core uninstall</b> stop the sequencer <b>silently</b> and do not publish
/// this — the handle is on its way out and a subscriber would be handed a disposed unit.
/// <b>The plugin sweep is the exception (FUNIT4, R57 finding 1):</b> a still-live
/// sequence owned by the unloading assembly ends with a <b>published</b>
/// <see cref="UnitSequenceOutcome.Stopped"/>, because the handle stays and somebody else may
/// want to park the unit; anyone else's sequence keeps playing with the unloading assembly's
/// <see cref="UnitStep.Then"/> steps neutralized and nothing published. (The units
/// sweep despawns for its own owner take the silent Despawn route, so
/// <c>.rat off</c> still reports nothing.)
/// </para>
/// </summary>
public sealed class UnitSequenceCompleted
{
    public IUnitHandle Handle { get; }

    public UnitSequenceOutcome Outcome { get; }

    /// <summary>How many steps ended before the sequence did (a failed step is not counted).</summary>
    public int StepsCompleted { get; }

    /// <summary>Length of the sequence.</summary>
    public int Count { get; }

    /// <summary>Why it failed or stopped; empty when it completed.</summary>
    public string Reason { get; }

    public UnitSequenceCompleted(IUnitHandle handle, UnitSequenceOutcome outcome, int stepsCompleted, int count, string? reason)
    {
        Handle = handle;
        Outcome = outcome;
        StepsCompleted = stepsCompleted;
        Count = count;
        Reason = reason ?? "";
    }
}
