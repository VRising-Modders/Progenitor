using System;

namespace Progenitor.Ui;

/// <summary>
/// Which path a settings editor control took, as an observed fact rather than an intention.
///
/// <para>
/// The three "did we clone a vanilla widget or build one" answers used to be a <c>bool</c>, and a
/// bool cannot say <i>"we cloned one and it came out inert, so we built one instead"</i> — which is
/// exactly the case evidence 176 names as the likely one and the case R45 finding 2 found was
/// reported by nothing at all.
/// </para>
/// </summary>
internal enum ControlSource
{
    /// <summary>No donor existed and the built control was not operable either. The row is a label.</summary>
    None = 0,

    /// <summary>A vanilla donor was cloned and the clone passed the operability re-check.</summary>
    Cloned = 1,

    /// <summary>No live donor was widget-sized and operable, so one was built from parts.</summary>
    Built = 2,

    /// <summary>
    /// A donor existed, the clone came out without the references that make it operable, and the
    /// build path recovered the row. This is the state the fallback exists for.
    /// </summary>
    BuiltAfterCloneRejected = 3,

    /// <summary>The clone was rejected <b>and</b> the built fallback was not operable. The row is a label.</summary>
    NoneAfterCloneRejected = 4,
}

/// <summary>
/// The clone-then-build recovery for a settings editor control, and the sentence that reports which
/// path it took. Game-free, so the branch that decides whether a settings panel can be operated at
/// all has tests that do not need Unity.
///
/// <para>
/// <b>Why this exists (R45 finding 2, over evidence 176).</b> The first cut ran
/// <c>clone → if null build → operability check → destroy on failure</c>. So the build path was
/// reached <i>only</i> when there was no donor, and a donor that produced an inert clone destroyed
/// the control and returned null with no second attempt. Donors are pre-filtered and cached per
/// panel build, so every row of that type takes the same path and fails the same way: one bad donor
/// and the whole settings panel is display-only again — evidence 176's defect verbatim, produced by
/// the commit that fixed it. Worse, the <c>source=</c> line that would have said so was emitted
/// <i>after</i> the gate that gave up, so the failure was silent.
/// </para>
///
/// <para>
/// <b>The order matters and is the whole content of this type.</b> A clone is tried first because
/// the module's rule since evidence 88 is to borrow vanilla pieces rather than ship art. A clone
/// that fails its re-check is destroyed and the build path is then tried on the same row, and the
/// resulting <see cref="ControlSource"/> carries the rejection forward so the log can report the
/// recovery instead of being skipped by it.
/// </para>
/// </summary>
internal static class ControlBuildPlan
{
    /// <summary>
    /// A control and the path it came from. <see cref="Control"/> is non-null exactly when the
    /// caller has something operable to put in the row.
    /// </summary>
    internal readonly struct Attempt<T> where T : class
    {
        internal Attempt(T? control, ControlSource source)
        {
            Control = control;
            Source = source;
        }

        internal T? Control { get; }

        internal ControlSource Source { get; }
    }

    /// <summary>
    /// Clone, re-check, and on failure destroy the clone and build instead — then re-check that.
    ///
    /// <para>
    /// Every callback is wrapped: a throwing donor lookup, a throwing predicate or a throwing
    /// builder is the same as an absent one, because the caller's alternative is a settings panel
    /// nobody can operate. <paramref name="drop"/> is told whether what it is destroying was a
    /// clone (<c>true</c>) or a built control (<c>false</c>) so the destroy can be reported for
    /// what it was.
    /// </para>
    /// </summary>
    /// <param name="clone">Produce a clone of a vanilla donor, or null when there is no donor.</param>
    /// <param name="build">Build the control from parts, or null when that failed.</param>
    /// <param name="operable">The operability predicate, re-run on whatever was produced.</param>
    /// <param name="drop">Destroy a candidate that failed the predicate. (candidate, wasClone).</param>
    internal static Attempt<T> Resolve<T>(
        Func<T?> clone,
        Func<T?> build,
        Func<T, bool> operable,
        Action<T, bool> drop)
        where T : class
    {
        bool cloneRejected = false;

        T? candidate = Call(clone);
        if (candidate != null)
        {
            if (Check(operable, candidate)) return new Attempt<T>(candidate, ControlSource.Cloned);

            // Instantiate remaps only references inside the copied subtree, so a donor whose
            // checkmark, fill or text component lives outside its own hierarchy comes out inert
            // (evidence 176). That is a fact about the donor, not a reason to give up on the row.
            cloneRejected = true;
            Drop(drop, candidate, wasClone: true);
        }

        candidate = Call(build);
        if (candidate != null)
        {
            if (Check(operable, candidate))
                return new Attempt<T>(candidate, cloneRejected ? ControlSource.BuiltAfterCloneRejected : ControlSource.Built);

            Drop(drop, candidate, wasClone: false);
        }

        return new Attempt<T>(null, cloneRejected ? ControlSource.NoneAfterCloneRejected : ControlSource.None);
    }

    /// <summary>True when no operable control was produced and the row falls back to a plain label.</summary>
    internal static bool IsFailure(ControlSource source) =>
        source == ControlSource.None || source == ControlSource.NoneAfterCloneRejected;

    /// <summary>True when a donor was found, cloned, and the clone failed the operability re-check.</summary>
    internal static bool CloneRejected(ControlSource source) =>
        source == ControlSource.BuiltAfterCloneRejected || source == ControlSource.NoneAfterCloneRejected;

    /// <summary>
    /// The once-per-panel-build sentence that answers the donor question. It names the path that
    /// was actually taken, including the two that only exist because a clone was rejected, so the
    /// instrument reports the fallback rather than being skipped by it.
    ///
    /// <para>
    /// <b>Two names, not one</b> (job FUI30). <paramref name="kind"/> is what the line is
    /// <i>about</i> and is the grep string an operator has already learned — <c>settings toggle</c>,
    /// <c>panel button</c>; <paramref name="noun"/> is the bare widget word the explanation reads
    /// with. They were one parameter with the word "settings" welded into this formatter, which was
    /// true while the only caller was the settings panel and became false the moment a button row
    /// on a disconnect screen needed the same sentence. Splitting them here rather than at the call
    /// site is what keeps every existing line byte-identical.
    /// </para>
    /// </summary>
    /// <param name="kind">The subject of the line, e.g. <c>settings toggle</c> or <c>panel button</c>.</param>
    /// <param name="noun">The bare widget word used inside the explanation, e.g. <c>toggle</c>.</param>
    /// <param name="source">The path that was actually taken.</param>
    internal static string Describe(string kind, string noun, ControlSource source) => source switch
    {
        ControlSource.Cloned =>
            "Ui: " + kind + " source=clone (a vanilla " + noun
            + " of widget size was live and operable, and the clone passed the re-check)",
        ControlSource.Built =>
            "Ui: " + kind + " source=built (no live vanilla " + noun
            + " was both widget-sized and operable, so one was built from parts)",
        ControlSource.BuiltAfterCloneRejected =>
            "Ui: " + kind + " source=built-after-clone-rejected (a vanilla " + noun
            + " donor was cloned and the clone came out without the references that make it"
            + " operable, so it was destroyed and one was built from parts instead; the row works)",
        ControlSource.NoneAfterCloneRejected =>
            "Ui: " + kind + " source=none (a vanilla " + noun
            + " donor was cloned and came out inert, and the built fallback was not operable"
            + " either; " + noun + " rows fall back to a plain label and cannot be operated)",
        _ =>
            "Ui: " + kind + " source=none (there was no donor and the built " + noun
            + " was not operable; " + noun + " rows fall back to a plain label and cannot be"
            + " operated)",
    };

    /// <summary>
    /// A recovered clone and both give-up outcomes are warnings; a clean clone or build is
    /// information. Kept here so the level cannot drift from the sentence it labels.
    /// </summary>
    internal static bool IsWarning(ControlSource source) =>
        source != ControlSource.Cloned && source != ControlSource.Built;

    private static T? Call<T>(Func<T?> f) where T : class
    {
        try { return f(); }
        catch { return null; }
    }

    private static bool Check<T>(Func<T, bool> operable, T candidate) where T : class
    {
        try { return operable(candidate); }
        catch { return false; }
    }

    private static void Drop<T>(Action<T, bool> drop, T candidate, bool wasClone) where T : class
    {
        try { drop(candidate, wasClone); }
        catch { /* a control that could not be destroyed is still not returned to the row */ }
    }
}
