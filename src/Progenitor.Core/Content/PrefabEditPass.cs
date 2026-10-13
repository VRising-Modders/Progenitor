using System;

namespace Progenitor.Content;

/// <summary>
/// The one edit pass, as the registry performs it, with every game-typed step behind a method
/// (job FEDIT2, closing R49 finding 2). Implemented by <c>PrefabRegistry</c> against the live
/// world and by the test suite against a component bag, so the ladder below runs once and both
/// callers run the same one.
///
/// Every member is called from the driver in <see cref="PrefabEditPass.Run"/> and in that order.
/// Ordering is part of the contract, not an implementation detail: <see cref="TryBeginRun"/> has
/// a side effect that <see cref="EndRun"/> must undo, <see cref="TryBind"/> must happen before
/// <see cref="DropBaselines"/> (a bind that fails must not forget what it could not restore), and
/// the drop/restore decision must happen before <see cref="RunEditors"/>.
/// </summary>
internal interface IPrefabEditPass
{
    /// <summary>False when the target world is gone (shutdown, a world swap mid-flush).</summary>
    bool WorldAlive { get; }

    /// <summary>Whether this prefab still has at least one registered editor.</summary>
    bool HasEditors { get; }

    /// <summary>Captured components waiting to be written back for this prefab.</summary>
    int BaselineCount { get; }

    /// <summary>
    /// Last editor gone: write the captured vanilla values back and forget them. False when the
    /// entity could not be bound, which leaves the baselines in the store for a later attempt —
    /// the caller treats that as an unserviced removal (see <see cref="PrefabEditPass.RemovalServiced"/>).
    /// </summary>
    bool RestoreOnly();

    /// <summary>False when a pass for this prefab is already in flight; the run is not entered.</summary>
    bool TryBeginRun();

    /// <summary>Always paired with a successful <see cref="TryBeginRun"/>, in a <c>finally</c>.</summary>
    void EndRun();

    /// <summary>Order the editors. False when the order could not be produced (a cycle).</summary>
    bool TrySnapshot(out int orderedCount);

    /// <summary>Trigger plus fingerprint: whether this pass has anything to do.</summary>
    bool ShouldRun();

    /// <summary>Bind the prefab entity. False when the collection does not have it yet.</summary>
    bool TryBind();

    /// <summary>Forget the baselines without writing them (the <c>NewClone</c> carve-out).</summary>
    void DropBaselines();

    /// <summary>Write the baselines back so the editors below start from vanilla.</summary>
    void RestoreBeforeRerun();

    /// <summary>Run the ordered editors on the bound entity.</summary>
    void RunEditors();
}

/// <summary>What one <see cref="PrefabEditPass.Run"/> did. Reported, never inferred.</summary>
internal enum PrefabEditPassOutcome
{
    /// <summary>The pass threw; the caller logged it. Nothing was restored and nothing ran.</summary>
    Threw,

    /// <summary>No live world. Nothing was read or written.</summary>
    NoWorld,

    /// <summary>Last editor gone and baselines written back; the entity is vanilla again.</summary>
    Restored,

    /// <summary>Last editor gone with nothing captured: there was nothing to put back.</summary>
    NothingToRestore,

    /// <summary>Last editor gone and the entity could not be bound; the baselines are still owed.</summary>
    RestoreOnlyBindFailed,

    /// <summary>
    /// No editors and a trigger other than <c>EditorsChanged</c>: this pass is not the one that
    /// restores. Unreachable from the removal drain, which always passes <c>EditorsChanged</c>.
    /// </summary>
    NotThisTrigger,

    /// <summary>A pass for this prefab was already in flight.</summary>
    RunInFlight,

    /// <summary>The ordered set could not be produced (a priority cycle).</summary>
    CycleRefused,

    /// <summary>The prefab has editors but the ordered set came back empty.</summary>
    NoOrderedEditors,

    /// <summary>The fingerprint says this trigger has nothing to re-run.</summary>
    Skipped,

    /// <summary>The prefab entity is not in the collection yet; a later apply seam retries.</summary>
    BindFailed,

    /// <summary>Baselines dropped (new clone) and the editors ran on top of the applicator's values.</summary>
    DroppedThenRan,

    /// <summary>Baselines written back and the editors re-ran from vanilla.</summary>
    RestoredThenRan,
}

/// <summary>
/// The edit pass's decision logic, with no ECS type in it (job FEDIT2).
///
/// <para><b>Why it is a type of its own.</b> The ladder used to live inside
/// <c>PrefabRegistry.TryRunEdits</c>, which the test project does not compile, so job FEDIT1's
/// twelve baseline tests drove a hand-written copy of it in the test file instead — and by the
/// time R49 read the two side by side the copy had already diverged in four places (no
/// <c>TryBeginRun</c>, the <c>NewClone</c> drop before the bind rather than after, no
/// error handling on the restore, no <c>ItemData</c> re-sync). A double that cannot represent a
/// branch cannot fail when that branch breaks. So the ladder lives here, the game-typed steps
/// live behind <see cref="IPrefabEditPass"/>, and the suite drives this method.</para>
///
/// <para><b>Restore before every trigger but one.</b> Restoring first is what makes a pass
/// idempotent: an editor doing read-modify-write sees vanilla again on every re-run.
/// <c>NewClone</c> is the exception because the applicator has just written the definition's own
/// values onto a fresh (or reset) entity, so the captured snapshot is not vanilla any more and
/// writing it back would clobber the applicator.</para>
/// </summary>
internal static class PrefabEditPass
{
    /// <summary>
    /// One edit pass. The order of the steps is the contract; see <see cref="IPrefabEditPass"/>.
    /// </summary>
    internal static PrefabEditPassOutcome Run(PrefabEditTrigger trigger, IPrefabEditPass pass)
    {
        if (pass == null) throw new ArgumentNullException(nameof(pass));
        if (!pass.WorldAlive) return PrefabEditPassOutcome.NoWorld;

        if (!pass.HasEditors)
        {
            // Last editor of this prefab is gone (Dispose / plugin unload / Unrequire sweep).
            // Put the components it wrote back, then forget them: the entity is vanilla again.
            if (trigger != PrefabEditTrigger.EditorsChanged) return PrefabEditPassOutcome.NotThisTrigger;
            if (pass.BaselineCount == 0) return PrefabEditPassOutcome.NothingToRestore;
            return pass.RestoreOnly()
                ? PrefabEditPassOutcome.Restored
                : PrefabEditPassOutcome.RestoreOnlyBindFailed;
        }

        if (!pass.TryBeginRun()) return PrefabEditPassOutcome.RunInFlight;
        try
        {
            if (!pass.TrySnapshot(out int orderedCount)) return PrefabEditPassOutcome.CycleRefused;
            if (orderedCount == 0) return PrefabEditPassOutcome.NoOrderedEditors;
            if (!pass.ShouldRun()) return PrefabEditPassOutcome.Skipped;

            // Bind first: a bind that fails must leave the baselines alone. Dropping them here
            // would forget vanilla for an entity we never touched.
            if (!pass.TryBind()) return PrefabEditPassOutcome.BindFailed;

            if (trigger == PrefabEditTrigger.NewClone)
            {
                pass.DropBaselines();
                pass.RunEditors();
                return PrefabEditPassOutcome.DroppedThenRan;
            }

            pass.RestoreBeforeRerun();
            pass.RunEditors();
            return PrefabEditPassOutcome.RestoredThenRan;
        }
        finally
        {
            pass.EndRun();
        }
    }

    /// <summary>
    /// Whether a pass run for a drained removal actually put the removed editor's components
    /// back (or established there was nothing to put back). False means the drain took the GUID
    /// off the pending set and nothing happened, which is R49 finding 6: for a prefab that still
    /// has editors the next <c>CollectionReady</c> sweep recovers, but for the last-editor case
    /// nothing else ever visits the GUID again — <c>RestoreEditsOnly</c> is reachable only from
    /// an <c>EditorsChanged</c> trigger, and only a drained removal produces one. The caller
    /// re-queues instead of losing it.
    /// </summary>
    internal static bool RemovalServiced(PrefabEditPassOutcome outcome)
    {
        switch (outcome)
        {
            case PrefabEditPassOutcome.Restored:
            case PrefabEditPassOutcome.NothingToRestore:
            case PrefabEditPassOutcome.NotThisTrigger:
            case PrefabEditPassOutcome.DroppedThenRan:
            case PrefabEditPassOutcome.RestoredThenRan:
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Whether re-queueing an unserviced removal can help (R50 finding 6). Two outcomes it
    /// cannot help:
    ///
    /// <list type="bullet">
    /// <item><description><see cref="PrefabEditPassOutcome.CycleRefused"/> — a priority cycle is
    /// a property of the registered editors, not of this frame. Thirty re-queues cannot break it;
    /// what they do is log <c>TrySnapshotOrdered</c>'s cycle line thirty-one times, because it is
    /// produced fresh on every call and is not deduped.</description></item>
    /// <item><description><see cref="PrefabEditPassOutcome.BindFailed"/> — this is the
    /// <em>has-editors</em> branch (the last-editor bind failure is
    /// <see cref="PrefabEditPassOutcome.RestoreOnlyBindFailed"/>, which does need the retry). The
    /// removal already cleared the applied fingerprint, <c>TryRunAllEdits</c> walks every GUID with
    /// editors and <c>PrefabEditApply.ShouldRun</c> is true for <c>CollectionReady</c> on a cleared
    /// fingerprint, so the next sweep restores and re-runs this prefab on its own. The ordinary dev
    /// loop reaches it: a hot reload while the prefab collection is still filling is seconds away
    /// from ready and thirty flush frames are not.</description></item>
    /// </list>
    /// </summary>
    internal static bool RemovalRetryHelps(PrefabEditPassOutcome outcome)
    {
        switch (outcome)
        {
            case PrefabEditPassOutcome.CycleRefused:
            case PrefabEditPassOutcome.BindFailed:
                return false;
            default:
                return true;
        }
    }

    /// <summary>Why a removal was neither serviced nor re-queued. Empty when it was.</summary>
    internal static string RemovalNotRetriedReason(PrefabEditPassOutcome outcome)
    {
        switch (outcome)
        {
            case PrefabEditPassOutcome.CycleRefused:
                return "the ordered set has a priority cycle, which a retry cannot break";
            case PrefabEditPassOutcome.BindFailed:
                return "the prefab still has editors, so the next CollectionReady sweep owns it";
            default:
                return "";
        }
    }

    /// <summary>
    /// How many extra flush frames an unserviced removal may be re-queued before it is given up
    /// on. Same shape as <c>PrefabLiveRefresh.MaxOwedApplyRetries</c>: bounded, because a
    /// permanently dead world must not re-arm the frame hook forever.
    /// </summary>
    internal const int MaxRemovalRetries = 30;

    /// <summary>True while an unserviced removal still has budget to be re-queued.</summary>
    internal static bool ShouldRetryRemoval(int retries) => retries < MaxRemovalRetries;

    /// <summary>What the flush does with a removal it has just drained and run.</summary>
    internal enum RemovalRetryAction
    {
        /// <summary>The pass restored (or there was nothing to restore). Forget the retry count.</summary>
        Serviced,

        /// <summary>Put the removal back and arm the flush again.</summary>
        Requeue,

        /// <summary>Budget spent: log once and stop. The prefab keeps the removed values.</summary>
        GiveUp,

        /// <summary>
        /// Nothing was owed: no editor of this prefab ever captured a baseline, so there is no
        /// value to put back and nothing to retry or warn about (R50 finding 6).
        /// </summary>
        NotOwed,

        /// <summary>
        /// Owed, but not by this path: another trigger restores it, or no retry can help. One
        /// Info line naming the reason, no re-queue (R50 finding 6).
        /// </summary>
        OwnedElsewhere,
    }

    /// <summary>
    /// The retry decision for one drained removal, kept here so the suite drives the same
    /// function the flush does (R49 findings 2 and 6, R50 finding 6). <paramref name="retries"/>
    /// is what to store for the next round: zero unless the removal is re-queued.
    ///
    /// <para><paramref name="baselineCount"/> is what the prefab actually owes. A GUID whose
    /// editors only read, or whose editors never ran, has captured nothing: before FEDW2 such a
    /// removal still burned thirty flush frames and then printed a warning saying the prefab
    /// "keeps the removed editor's values", which were never captured and never written.</para>
    /// </summary>
    internal static RemovalRetryAction NextRemovalAction(
        PrefabEditPassOutcome outcome, int retriesSoFar, int baselineCount, out int retries)
    {
        if (RemovalServiced(outcome))
        {
            retries = 0;
            return RemovalRetryAction.Serviced;
        }
        if (baselineCount <= 0)
        {
            retries = 0;
            return RemovalRetryAction.NotOwed;
        }
        if (!RemovalRetryHelps(outcome))
        {
            retries = 0;
            return RemovalRetryAction.OwnedElsewhere;
        }
        // The budget is read before it is spent, so exactly MaxRemovalRetries re-queues happen.
        if (!ShouldRetryRemoval(retriesSoFar))
        {
            retries = retriesSoFar;
            return RemovalRetryAction.GiveUp;
        }
        retries = retriesSoFar + 1;
        return RemovalRetryAction.Requeue;
    }

    /// <summary>
    /// Pending runs that still need their own pass: a GUID the removal drain already visited was
    /// re-run by that <c>EditorsChanged</c> pass, so running it again would restore and re-run a
    /// second time for nothing. Kept here because the drain order is the part of the flush that
    /// can be reasoned about without a world.
    /// </summary>
    internal static int[] PendingRunsToRun(int[]? removals, int[]? pendingRuns)
    {
        if (pendingRuns == null || pendingRuns.Length == 0) return Array.Empty<int>();
        if (removals == null || removals.Length == 0) return pendingRuns;
        var keep = new int[pendingRuns.Length];
        int n = 0;
        for (int i = 0; i < pendingRuns.Length; i++)
        {
            if (Array.IndexOf(removals, pendingRuns[i]) >= 0) continue;
            keep[n++] = pendingRuns[i];
        }
        if (n == pendingRuns.Length) return pendingRuns;
        if (n == 0) return Array.Empty<int>();
        var trimmed = new int[n];
        Array.Copy(keep, trimmed, n);
        return trimmed;
    }

    // -----------------------------------------------------------------------------
    // The write-site capture rule (the other half of R49 finding 2)
    // -----------------------------------------------------------------------------

    /// <summary>
    /// Cheap pre-check the write site runs before it reads anything: capture at most once per
    /// (prefab, component) per Core generation, so the first writer defines vanilla. Reads never
    /// reach here.
    /// </summary>
    internal static bool NeedsCapture(PrefabEditBaselines? store, int guidHash, string? typeName)
        => store != null && store.NeedsCapture(guidHash, typeName);

    /// <summary>
    /// Record the pre-write value. Refused when the component is not on the entity
    /// (<paramref name="componentPresent"/>): there is no vanilla value to put back, and the
    /// write itself is going to fail. Refused again on <see cref="NeedsCapture"/> because the
    /// write site's pre-check and this call are not one atomic step.
    /// </summary>
    internal static bool TryCapture(
        PrefabEditBaselines? store,
        int guidHash,
        string? typeName,
        bool componentPresent,
        int entityIndex,
        int entityVersion,
        Action<object> restore)
    {
        if (store == null || restore == null) return false;
        if (!componentPresent) return false;
        if (!store.NeedsCapture(guidHash, typeName)) return false;
        return store.Capture(guidHash, typeName, entityIndex, entityVersion, restore);
    }
}
