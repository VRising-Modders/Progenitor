using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Progenitor.Content;

/// <summary>
/// Game-free policy for refreshing live buff instances after a clone changes.
/// Prefab-level edits never reach instances that already exist or that a save restores
/// (evidence 109: a pre-strip haste clone kept its glow until <c>.nodrain off</c> destroyed
/// it through <c>DestroyUtility.Destroy(..., TryRemoveBuff)</c>, which stops the sequence
/// via <c>BuffSequenceSpawned</c>). Default is off: a plugin that stacks its own state on
/// a live buff must not have Core destroy it. Never on first process start — save-restored
/// instances of an unchanged definition are left alone. A Core swap with an unchanged
/// definition hash does not refresh. Lost instances are not re-applied; stacks and
/// remaining duration are preserved where the buff has them. One instance per frame.
///
/// The fingerprint is built only from properties of the plugin build (kind, resolved source,
/// name / description / icon keys, and the ordered editor identity) — never a per-generation
/// index or counter — and is recorded once per apply, after this generation's editors have
/// registered. <see cref="LiveRefreshDecisions"/> holds the phases until then.
/// </summary>
internal static class PrefabLiveRefresh
{
    /// <summary>Logged token: the definition's builder fields (name / desc / icon / source / kind) moved.</summary>
    public const string ReasonDefinitionChanged = "definition changed";

    /// <summary>Logged token: same-id <c>CloneOf</c> retarget destroyed the old clone and <c>InsertNew</c>'d.</summary>
    public const string ReasonRetarget = "retarget";

    /// <summary>Logged token: an ordered edit pass wrote components on the clone.</summary>
    public const string ReasonEdit = "edit";

    /// <summary>
    /// Logged token: this process already cloned the GUID (plugin reload / Core snapshot) but
    /// this generation has no previous fingerprint to compare.
    /// </summary>
    public const string ReasonReload = "reload";

    /// <summary>
    /// Logged token: the client copied HUD fields (icon Sprite / name / description) onto
    /// existing instances of this GUID. Never a destroy.
    /// </summary>
    public const string ReasonIcon = "icon";

    /// <summary>Hard cap: at most one live instance is destroyed-and-reapplied per frame.</summary>
    public const int InstancesPerFrame = 1;

    private static readonly object Gate = new();
    private static readonly Dictionary<int, string> Fingerprints = new();

    /// <summary>
    /// GUIDs whose fingerprint came in from the previous generation and that this generation has
    /// not registered yet. A rehydrated row is <em>provisional</em> until the definition it
    /// belongs to is applied here; if the first apply pass finishes without it, the definition is
    /// gone (removed by the developer, a plugin that failed to load, the content module turned
    /// off) and the row is a fossil. Keeping it would let an unrelated later generation that
    /// re-adds the same GUID compare against a fingerprint no code in this process produced —
    /// R29 finding 11's "a stale fingerprint must not mask a real change".
    /// </summary>
    private static readonly HashSet<int> ProvisionalImports = new();

    /// <summary>
    /// True once <see cref="SettleApplyPass"/> has run for this generation, i.e. the first apply
    /// pass settled. Until then an empty <see cref="Fingerprints"/> is not evidence of anything
    /// (see <see cref="FingerprintExport"/>).
    ///
    /// <b>Single writer, and it must stay that way.</b> This is a Core-generation flag, and the
    /// only thing it authorizes — <see cref="FingerprintExport.AuthoritativelyEmpty"/> — lets the
    /// caller wipe Bootstrap <c>ContentMemory</c>, the one store that survives a collectible ALC
    /// unload and crosses a Core swap. R43 job FPRF1: the teardown export used to run the
    /// unclaimed sweep through a method that set this flag itself and then read it back one line
    /// later, so <see cref="FingerprintExport.Unsettled"/> was unreachable on the export path and
    /// a generation that never applied anything wiped the store on its way out (R27 finding 1's
    /// behavior, permanently and in silence). The sweep and the settle are now separate entry
    /// points — <see cref="SweepUnclaimedImports"/> and <see cref="SettleApplyPass"/> — and only
    /// the second may write here.
    /// </summary>
    private static bool _applyPassSettled;

    /// <summary>
    /// Whether the server should destroy-and-reapply live instances of this buff.
    /// Opt-in, buff-only, and never the first clone of a GUID in this process
    /// (<paramref name="alreadyClonedThisProcess"/> is <c>WasClonedHere</c> before this apply,
    /// including a snapshot-rehydrated clone). Only a changed definition (builder fields
    /// plus the ordered-edit version stamp), a retarget, or an edit pass whose stamp
    /// moved qualifies. A Core swap with an unchanged definition does not: a missing
    /// previous fingerprint is not a change (<paramref name="previousFingerprintMissing"/>
    /// is kept so call sites stay stable; it is not a refresh trigger).
    ///
    /// This is only meaningful because the fingerprint map now crosses the swap
    /// (<see cref="ExportFingerprints"/> / <see cref="ImportFingerprints"/>). Before FZ the
    /// map was a Core static cleared by <c>Uninstall</c>, so the previous fingerprint was
    /// null on the first apply of every new generation, <see cref="DefinitionChanged"/> was
    /// false by construction, and the opt-in refresh never fired on the one transition it
    /// exists for (R27 finding 1).
    /// </summary>
    public static bool ShouldRefresh(
        bool optIn,
        bool isBuff,
        bool alreadyClonedThisProcess,
        bool definitionChanged,
        bool retargeted,
        bool editPassChangedClone,
        bool previousFingerprintMissing)
    {
        if (!optIn || !isBuff || !alreadyClonedThisProcess) return false;
        _ = previousFingerprintMissing;
        return definitionChanged || retargeted || editPassChangedClone;
    }

    /// <summary>
    /// One row of the ordered edit identity: which plugin registered the editor, at what
    /// priority, with which optional <c>Version</c> token. Deliberately excludes the
    /// monotonic registration index and the per-guid invalidation counter. Both are
    /// per-generation: the same plugin build re-registering after a Core swap (or a plugin
    /// hot reload) gets fresh index values, which made the post-reload fingerprint differ
    /// from the swap-time one and destroyed every opted-in live buff on every Core swap
    /// (R26 finding 4, fixed in FU).
    /// </summary>
    public readonly struct EditIdentityRow
    {
        public EditIdentityRow(string? pluginId, int priority, string? version)
        {
            PluginId = pluginId ?? "";
            Priority = priority;
            Version = version ?? "";
        }

        public string PluginId { get; }

        public int Priority { get; }

        public string Version { get; }
    }

    /// <summary>
    /// Ordered identity of one prefab's editor set. Empty when there are no editors.
    /// Row order is the resolved apply order, so a reordered, added, or removed editor
    /// changes the string while a re-registration of the same set does not.
    /// </summary>
    public static string EditIdentity(IReadOnlyList<EditIdentityRow>? rows)
    {
        if (rows == null || rows.Count == 0) return "";
        var sb = new StringBuilder();
        for (int i = 0; i < rows.Count; i++)
        {
            if (i > 0) sb.Append('\n');
            sb.Append(rows[i].PluginId).Append('\0')
                .Append(rows[i].Priority.ToString(CultureInfo.InvariantCulture)).Append('\0')
                .Append(rows[i].Version);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Stable identity of the builder-side definition plus the ordered-edit identity
    /// (<see cref="EditIdentity"/>). Source hash is the resolved <c>CloneOf</c> after apply
    /// (0 until then). Not a wire field — live refresh is local to the process that registered
    /// <c>RefreshLiveInstances()</c>. Every input is a property of the plugin build, never of
    /// the generation it loaded into, so a Core swap that re-applies the same source,
    /// name/desc/icon keys, and editor set produces the same string and must not refresh.
    /// </summary>
    public static string Fingerprint(
        PrefabKind kind,
        int sourceHash,
        string? sourceName,
        string? localizedName,
        string? localizedDescription,
        string? iconPath,
        string? editStamp = null)
    {
        return ((int)kind).ToString()
            + "\n" + sourceHash.ToString()
            + "\n" + (sourceName ?? "")
            + "\n" + (localizedName ?? "")
            + "\n" + (localizedDescription ?? "")
            + "\n" + (iconPath ?? "")
            + "\n" + (editStamp ?? "");
    }

    /// <summary>
    /// True when a previous apply of this GUID left a fingerprint and the new one differs.
    /// An empty previous is not a change by itself (first fingerprint this generation);
    /// <see cref="ShouldRefresh"/> treats that as <c>previousFingerprintMissing</c> instead
    /// so first process start does not refresh save-restored instances.
    /// </summary>
    public static bool DefinitionChanged(string? previous, string? current)
    {
        if (string.IsNullOrEmpty(previous)) return false;
        return !string.Equals(previous, current ?? "", StringComparison.Ordinal);
    }

    /// <summary>Priority: retarget, then definition fields, then edit writes, then reload.</summary>
    public static string PickReason(
        bool definitionChanged,
        bool retargeted,
        bool editPassChangedClone,
        bool previousFingerprintMissing)
    {
        if (retargeted) return ReasonRetarget;
        if (definitionChanged) return ReasonDefinitionChanged;
        if (editPassChangedClone) return ReasonEdit;
        if (previousFingerprintMissing) return ReasonReload;
        return ReasonDefinitionChanged;
    }

    /// <summary>
    /// Exact server log line after a batch is queued.
    /// <c>PrefabRegistry: refreshed N live instance(s) of 'id' (reason)</c>.
    /// </summary>
    public static string FormatRefreshed(string id, int count, string reason) =>
        "PrefabRegistry: refreshed " + count + " live instance(s) of '" + (id ?? "") + "' (" + (reason ?? "") + ")";

    /// <summary>
    /// Exact server log when an opted-in already-cloned buff is not refreshed because the
    /// definition hash (source, edit stamp, icon/name keys) matches the last apply.
    /// Grep <c>refresh skipped: definition unchanged</c>.
    /// </summary>
    public static string FormatSkippedUnchanged(string id) =>
        "PrefabRegistry: refresh skipped: definition unchanged ('" + (id ?? "") + "')";

    /// <summary>Token inside <see cref="FormatSkippedUnchanged"/>.</summary>
    public const string SkippedUnchangedToken = "refresh skipped: definition unchanged";

    /// <summary>
    /// True when the server should emit <see cref="FormatSkippedUnchanged"/>: opted in,
    /// already cloned this process, and <see cref="ShouldRefresh"/> is false.
    /// First process start (<paramref name="alreadyClonedThisProcess"/> false) stays silent.
    /// </summary>
    public static bool ShouldLogSkip(
        bool optIn,
        bool isBuff,
        bool alreadyClonedThisProcess,
        bool willRefresh) =>
        optIn && isBuff && alreadyClonedThisProcess && !willRefresh;

    /// <summary>
    /// Stacks written to <c>ApplyBuffDebugEvent.Stacks</c>. Zero (unreadable / missing
    /// <c>Buff</c>) becomes 1 — the game's default — rather than applying zero stacks.
    /// </summary>
    public static byte PreserveStacks(byte stacks) => stacks == 0 ? (byte)1 : stacks;

    /// <summary>
    /// Remaining duration written back onto the new instance's <c>LifeTime.Duration</c>.
    /// Negative values are treated as missing (the clone's authored duration stays).
    /// </summary>
    public static bool ShouldRestoreDuration(bool hadLifeTime, float duration) =>
        hadLifeTime && duration >= 0f;

    /// <summary>
    /// Skip <c>ApplyBuff</c> when the instance is already gone and we did not destroy it
    /// this refresh (expired, cleansed, owner died, or recycled entity index).
    /// </summary>
    public static bool ShouldSkipLostInstance(bool instanceExists, bool destroyedByUs) =>
        !instanceExists && !destroyedByUs;

    /// <summary>Frame N: the live instance is still there; destroy it this frame.</summary>
    public static bool ShouldDestroyNow(bool instanceExists, bool destroyedByUs) =>
        instanceExists && !destroyedByUs;

    /// <summary>
    /// Frame N+1: we destroyed on the previous frame and the old entity is gone, so
    /// <c>ApplyBuff</c> will not see a <c>DestroyTag</c> duplicate.
    /// </summary>
    public static bool ShouldReapply(bool instanceExists, bool destroyedByUs) =>
        destroyedByUs && !instanceExists;

    /// <summary>
    /// Destroy is deferred; wait one more frame rather than applying beside the old
    /// instance. After <see cref="DestroyWaitFrames"/> we apply anyway.
    /// </summary>
    public static bool ShouldWaitForDestroy(bool instanceExists, bool destroyedByUs, int waitFrames) =>
        destroyedByUs && instanceExists && waitFrames < DestroyWaitFrames;

    /// <summary>Frames to wait after destroy before applying even if the old entity still exists.</summary>
    public const int DestroyWaitFrames = 2;

    /// <summary>What a queued live-refresh item must do on this frame.</summary>
    public enum RefreshStep
    {
        /// <summary>The instance is gone and we did not destroy it: drop the item.</summary>
        Skip = 0,
        /// <summary>Destroy the instance now and come back next frame.</summary>
        Destroy = 1,
        /// <summary>Our destroy has not settled yet; come back next frame.</summary>
        Wait = 2,
        /// <summary>The old entity is gone (or we waited long enough): ApplyBuff now.</summary>
        Apply = 3,
    }

    /// <summary>
    /// The part of a queued item that must survive a re-queue. FT: the apply loop used to
    /// pass its <c>Pending</c> struct <em>by value</em>, so <c>DestroyedByUs</c> set on the
    /// destroy frame never reached the next frame; the item came back looking never-destroyed
    /// with its entity already gone, <see cref="ShouldSkipLostInstance"/> dropped it, and the
    /// live buff was destroyed and never re-applied (evidence 131: <c>.nodrain off</c> replied
    /// "not present" after a Core swap). Always advance this through <see cref="Advance"/>.
    /// </summary>
    public struct RefreshState
    {
        /// <summary>True once this refresh destroyed the instance itself.</summary>
        public bool DestroyedByUs;

        /// <summary>How many frames we have waited for our own destroy to settle.</summary>
        public int WaitFrames;
    }

    /// <summary>
    /// One frame of the destroy → re-apply state machine. <paramref name="state"/> is taken
    /// by reference and must be written back onto the queued item: returning true means the
    /// item is re-queued and the mutated state is what the next frame reads. Returns false
    /// for <see cref="RefreshStep.Skip"/> and <see cref="RefreshStep.Apply"/> (the item is done).
    /// </summary>
    public static bool Advance(ref RefreshState state, bool instanceExists, out RefreshStep step)
    {
        if (ShouldSkipLostInstance(instanceExists, state.DestroyedByUs))
        {
            step = RefreshStep.Skip;
            return false;
        }

        if (ShouldDestroyNow(instanceExists, state.DestroyedByUs))
        {
            state.DestroyedByUs = true;
            state.WaitFrames = 0;
            step = RefreshStep.Destroy;
            return true;
        }

        if (ShouldWaitForDestroy(instanceExists, state.DestroyedByUs, state.WaitFrames))
        {
            state.WaitFrames++;
            step = RefreshStep.Wait;
            return true;
        }

        step = RefreshStep.Apply;
        return false;
    }

    /// <summary>
    /// What a destroy+reapply cannot restore. Documented so a plugin that stacks its own
    /// state on a live buff does not opt in expecting a perfect clone of the old instance.
    /// </summary>
    public const string CannotPreserve =
        "on-apply effects re-fire; Buff.StartTimeUnmodified (age) is not preserved; runtime buffers including BuffSequenceSpawned are recreated by ApplyBuff";

    /// <summary>
    /// Exact client log line after HUD fields are copied onto live instances.
    /// <c>PrefabRegistry: nudged N live instance(s) of 'id' (icon)</c>.
    /// </summary>
    public static string FormatNudged(string id, int count, string reason) =>
        "PrefabRegistry: nudged " + count + " live instance(s) of '" + (id ?? "") + "' (" + (reason ?? "") + ")";

    /// <summary>
    /// How many of a caller-supplied instance list should be nudged. Tests pass a fake
    /// list; live code passes the ECS query result. Null <paramref name="isLive"/>
    /// counts every element.
    /// </summary>
    public static int CountNudgeTargets<T>(IReadOnlyList<T>? instances, Func<T, bool>? isLive)
    {
        if (instances == null || instances.Count == 0) return 0;
        if (isLive == null) return instances.Count;
        int n = 0;
        for (int i = 0; i < instances.Count; i++)
        {
            if (isLive(instances[i])) n++;
        }
        return n;
    }

    /// <summary>True when the client should emit <see cref="FormatNudged"/>.</summary>
    public static bool ShouldLogNudge(int count) => count > 0;

    /// <summary>
    /// How many queued instances may run this frame. Always 0 or
    /// <see cref="InstancesPerFrame"/>; never more than <paramref name="remaining"/>.
    /// </summary>
    public static int TakeThisFrame(int remaining)
    {
        if (remaining <= 0) return 0;
        return remaining < InstancesPerFrame ? remaining : InstancesPerFrame;
    }

    /// <summary>Last successful apply fingerprint for <paramref name="guidHash"/>, or null.</summary>
    public static string? LastFingerprint(int guidHash)
    {
        lock (Gate)
            return Fingerprints.TryGetValue(guidHash, out var fp) ? fp : null;
    }

    /// <summary>
    /// Remember the fingerprint once an apply has settled so the next one can compare.
    /// Only the settle writes here: recording mid-apply, before the plugin's editors are
    /// back, stores a fingerprint the very next phase will disagree with (R26 finding 4).
    /// </summary>
    public static void RememberFingerprint(int guidHash, string fingerprint)
    {
        if (fingerprint == null) fingerprint = "";
        lock (Gate) Fingerprints[guidHash] = fingerprint;
    }

    /// <summary>
    /// Drop this generation's fingerprints (Core Uninstall). The map itself is handed to the
    /// next generation through Bootstrap <c>ContentMemory</c>: <c>PrefabRegistry.ExportProcessMemory</c>
    /// copies it out (from <c>Runtime.SnapshotContent</c>, which runs <em>before</em>
    /// <c>Plugin.Uninstall</c>) and <c>ImportProcessMemory</c> copies it back on the next
    /// <c>Install</c>, the same handoff FA already uses for the <c>CloneOf</c> hashes.
    /// </summary>
    public static void Reset()
    {
        lock (Gate)
        {
            Fingerprints.Clear();
            ProvisionalImports.Clear();
            _applyPassSettled = false;
        }
    }

    /// <summary>
    /// This generation registered (applied) <paramref name="guidHash"/>, so a fingerprint
    /// rehydrated for it is no longer provisional. Called from every apply seam that can record
    /// a fingerprint, before the settle takes the decision.
    /// </summary>
    public static void NoteRegistered(int guidHash)
    {
        lock (Gate) ProvisionalImports.Remove(guidHash);
    }

    /// <summary>
    /// Number of rehydrated fingerprints still waiting to be claimed by a registration.
    /// Diagnostics and tests only.
    /// </summary>
    public static int ProvisionalImportCount
    {
        get { lock (Gate) return ProvisionalImports.Count; }
    }

    /// <summary>Whether this generation's first apply pass has settled.</summary>
    public static bool ApplyPassSettled
    {
        get { lock (Gate) return _applyPassSettled; }
    }

    /// <summary>
    /// End of this generation's first apply pass: drop every rehydrated fingerprint whose
    /// definition was never registered here and report how many went (R29 finding 11), and record
    /// that the pass settled. Idempotent — only the first call does anything, and it is what makes
    /// an empty <see cref="Fingerprints"/> mean "nothing registered" rather than "nobody looked
    /// yet" (<see cref="FingerprintExport"/>).
    ///
    /// Dropping is the safe direction: a definition that comes back later simply has no previous
    /// fingerprint, and a missing previous fingerprint is explicitly not a refresh trigger
    /// (<see cref="ShouldRefresh"/>), so the cost is one skipped refresh rather than a destroy of
    /// a live buff against a fingerprint from a build nobody is running.
    /// Returns true when this call performed the sweep, false when the pass had already settled.
    ///
    /// <b>The only writer of <c>_applyPassSettled</c>.</b> Call this from the real end-of-apply-pass
    /// settle and from nowhere else: it is what makes an empty export authoritative, and an
    /// authoritative empty export wipes process-lifetime state. Teardown, diagnostics and any other
    /// caller that merely wants the fossil rows gone use <see cref="SweepUnclaimedImports"/>.
    /// </summary>
    public static bool SettleApplyPass(out int dropped) =>
        DropUnclaimed(markSettled: true, out dropped);

    /// <summary>
    /// The same sweep as <see cref="SettleApplyPass"/> without the authority: unclaimed rehydrated
    /// rows are dropped so a fossil cannot leave through the export (R29 finding 11), but the
    /// generation does not get to claim it completed an apply pass.
    ///
    /// This is the teardown export's entry point. It runs on a generation that may never have
    /// applied anything — the Core swap landed before the world existed, the content plugin was
    /// mid-write, the module was skipped for one generation — and in that case the emptiness it
    /// produces proves nothing. Leaving <c>_applyPassSettled</c> false keeps
    /// <see cref="ExportFingerprints"/> on <see cref="FingerprintExport.Unsettled"/>, so
    /// <c>ContentMemory</c> keeps its rows and the next generation still has something to compare
    /// against (R43 job FPRF1).
    /// Returns true when this call performed the sweep, false when the pass had already settled.
    /// </summary>
    public static bool SweepUnclaimedImports(out int dropped) =>
        DropUnclaimed(markSettled: false, out dropped);

    private static bool DropUnclaimed(bool markSettled, out int dropped)
    {
        dropped = 0;
        lock (Gate)
        {
            if (_applyPassSettled) return false;
            if (markSettled) _applyPassSettled = true;
            if (ProvisionalImports.Count == 0) return true;
            foreach (int guidHash in ProvisionalImports)
            {
                if (Fingerprints.Remove(guidHash)) dropped++;
            }
            ProvisionalImports.Clear();
        }
        return true;
    }

    /// <summary>Loud line for the unclaimed sweep. Grep <c>not re-registered</c>.</summary>
    public static string FormatFingerprintsDropped(int dropped) =>
        "PrefabRegistry: dropped " + dropped.ToString(CultureInfo.InvariantCulture)
        + " rehydrated live-refresh fingerprint(s) whose definition was not re-registered in this"
        + " generation's first apply pass. A fingerprint no definition here owns cannot be"
        + " compared against and must not cross another swap.";

    /// <summary>
    /// Copy the fingerprint map out of this generation as primitives so Bootstrap can hold it
    /// across a collectible-ALC swap. Strings and ints only — nothing here can pin the Core ALC.
    ///
    /// <b>One bad row never costs the whole handoff.</b> A row whose fingerprint is null or empty
    /// is not a settled fingerprint (<see cref="Fingerprint"/> never produces one) and would read
    /// on the far side as a real previous value that differs from everything, i.e. a spurious
    /// "definition changed" that destroys and re-applies an opted-in buff. Such rows are skipped
    /// and counted in <paramref name="skipped"/>; every other row still crosses (R28 finding 16).
    /// </summary>
    public static void ExportFingerprints(out int[] guidHashes, out string[] fingerprints, out int skipped) =>
        ExportFingerprints(out guidHashes, out fingerprints, out skipped, out _);

    /// <summary>
    /// Why an export carries no rows (R29 finding 11). An empty array on its own is ambiguous —
    /// it is what a healthy generation with no definitions produces <em>and</em> what a
    /// generation whose rehydrate was swallowed produces — so the exporter states which it is and
    /// the store side (<c>ContentMemory</c>) only clears for the first.
    /// </summary>
    public enum FingerprintExport
    {
        /// <summary>At least one row is being exported.</summary>
        Rows = 0,

        /// <summary>
        /// Empty, and this generation's first apply pass settled
        /// (<see cref="SettleApplyPass"/>): nothing is registered here, so the store's rows
        /// belong to a build that is no longer running and may be cleared.
        /// </summary>
        AuthoritativelyEmpty = 1,

        /// <summary>
        /// Empty, and the first apply pass never settled — the Core generation was swapped out
        /// before it applied anything, or an earlier step threw. Indistinguishable from a failed
        /// export, so the store must be kept and the caller logs it.
        /// </summary>
        Unsettled = 2,
    }

    /// <summary>
    /// As <see cref="ExportFingerprints(out int[], out string[], out int)"/>, also reporting
    /// whether an empty result is authoritative. Note that an export that <em>throws</em> never
    /// gets here at all — the caller's try/catch skips the write and the store keeps what it
    /// holds, which is the "export failed, keep, log" half of R29 finding 11.
    /// </summary>
    public static void ExportFingerprints(
        out int[] guidHashes,
        out string[] fingerprints,
        out int skipped,
        out FingerprintExport status)
    {
        skipped = 0;
        lock (Gate)
        {
            status = _applyPassSettled ? FingerprintExport.AuthoritativelyEmpty : FingerprintExport.Unsettled;
            int n = Fingerprints.Count;
            if (n == 0)
            {
                guidHashes = Array.Empty<int>();
                fingerprints = Array.Empty<string>();
                return;
            }

            var keys = new int[n];
            var values = new string[n];
            int i = 0;
            foreach (var kv in Fingerprints)
            {
                if (string.IsNullOrEmpty(kv.Value))
                {
                    skipped++;
                    continue;
                }
                keys[i] = kv.Key;
                values[i] = kv.Value;
                i++;
            }

            // Rows only counts what actually crosses. If every row was skipped as empty the
            // export is empty again and keeps whichever empty-kind the pass state says, so a
            // map of nothing but junk rows cannot pass itself off as a real export.
            if (i > 0) status = FingerprintExport.Rows;

            if (i == n)
            {
                guidHashes = keys;
                fingerprints = values;
                return;
            }

            guidHashes = new int[i];
            fingerprints = new string[i];
            Array.Copy(keys, guidHashes, i);
            Array.Copy(values, fingerprints, i);
        }
    }

    /// <summary>
    /// Exact line for an export that carries no rows. Grep <c>live-refresh fingerprint export</c>.
    /// </summary>
    public static string FormatFingerprintExport(FingerprintExport status, int held)
    {
        switch (status)
        {
            case FingerprintExport.Rows:
                return "PrefabRegistry: live-refresh fingerprint export carries "
                    + held.ToString(CultureInfo.InvariantCulture) + " row(s).";
            case FingerprintExport.AuthoritativelyEmpty:
                return "PrefabRegistry: live-refresh fingerprint export is empty and this"
                    + " generation's first apply pass settled, so the emptiness is real; clearing"
                    + " Bootstrap ContentMemory rather than leaving a previous build's rows to be"
                    + " compared against.";
            default:
                return "PrefabRegistry: live-refresh fingerprint export is empty but this"
                    + " generation's first apply pass never settled, so the emptiness proves"
                    + " nothing; Bootstrap ContentMemory keeps the rows it holds.";
        }
    }

    /// <summary>Overload for callers that do not need the skipped count.</summary>
    public static void ExportFingerprints(out int[] guidHashes, out string[] fingerprints) =>
        ExportFingerprints(out guidHashes, out fingerprints, out _);

    /// <summary>Why an <see cref="ImportFingerprints"/> call merged nothing.</summary>
    public enum FingerprintImport
    {
        /// <summary>Rows were offered and at least one was merged.</summary>
        Taken = 0,

        /// <summary>Null or empty arrays: the previous generation handed over nothing at all.</summary>
        NothingOffered = 1,

        /// <summary>The parallel arrays disagreed in length; pairing them would refresh the wrong buff.</summary>
        LengthMismatch = 2,

        /// <summary>Every offered GUID already had a fingerprint recorded by this generation.</summary>
        AllAlreadyPresent = 3,
    }

    /// <summary>
    /// Restore fingerprints handed over by the previous generation. Merges rather than
    /// replaces so a rehydrate that lands after a first apply cannot erase it. A length
    /// mismatch is ignored (pairing the wrong fingerprint would refresh the wrong buff).
    /// Returns how many rows were offered; <paramref name="added"/> is how many were actually
    /// merged and <paramref name="status"/> says why the number is what it is, so the caller can
    /// log a rehydrate that took nothing instead of swallowing it (R28 finding 16).
    /// </summary>
    public static int ImportFingerprints(
        int[]? guidHashes,
        string[]? fingerprints,
        out int added,
        out FingerprintImport status)
    {
        added = 0;
        if (guidHashes == null || fingerprints == null || guidHashes.Length == 0)
        {
            status = FingerprintImport.NothingOffered;
            return 0;
        }
        if (guidHashes.Length != fingerprints.Length)
        {
            status = FingerprintImport.LengthMismatch;
            return 0;
        }

        lock (Gate)
        {
            for (int i = 0; i < guidHashes.Length; i++)
            {
                if (Fingerprints.ContainsKey(guidHashes[i])) continue;
                Fingerprints[guidHashes[i]] = fingerprints[i] ?? "";
                // Provisional until this generation registers the definition it belongs to.
                // The settle at the end of the first apply pass removes whatever is
                // left here, so a fossil row cannot cross another swap (R29 finding 11).
                if (!_applyPassSettled) ProvisionalImports.Add(guidHashes[i]);
                added++;
            }
        }

        status = added > 0 ? FingerprintImport.Taken : FingerprintImport.AllAlreadyPresent;
        return guidHashes.Length;
    }

    /// <summary>Overload for callers that do not inspect the outcome.</summary>
    public static int ImportFingerprints(int[]? guidHashes, string[]? fingerprints) =>
        ImportFingerprints(guidHashes, fingerprints, out _, out _);

    /// <summary>Exact log line for the fingerprint half of the Core-swap handoff.</summary>
    public static string FormatFingerprintHandoff(string verb, int count) =>
        "PrefabRegistry: " + (verb ?? "") + " " + count.ToString(CultureInfo.InvariantCulture)
        + " live-refresh fingerprint(s).";

    /// <summary>
    /// Loud line for a rehydrate that merged nothing. Only <see cref="FingerprintImport.Taken"/>
    /// and <see cref="FingerprintImport.AllAlreadyPresent"/> are benign; the other two mean the
    /// previous generation's fingerprints did not cross and the next definition comparison will
    /// see no previous value at all. Grep <c>live-refresh fingerprint rehydrate</c>.
    /// </summary>
    public static string FormatFingerprintImport(FingerprintImport status, int offered, int values, int added)
    {
        switch (status)
        {
            case FingerprintImport.Taken:
                return "PrefabRegistry: live-refresh fingerprint rehydrate merged "
                    + added.ToString(CultureInfo.InvariantCulture) + " of "
                    + offered.ToString(CultureInfo.InvariantCulture) + " offered row(s).";
            case FingerprintImport.AllAlreadyPresent:
                return "PrefabRegistry: live-refresh fingerprint rehydrate merged 0 of "
                    + offered.ToString(CultureInfo.InvariantCulture)
                    + " offered row(s): this generation had already settled all of them.";
            case FingerprintImport.LengthMismatch:
                return "PrefabRegistry: live-refresh fingerprint rehydrate took nothing: the handoff"
                    + " arrays disagree (guids=" + offered.ToString(CultureInfo.InvariantCulture)
                    + " fingerprints=" + values.ToString(CultureInfo.InvariantCulture)
                    + "). The previous generation's fingerprints are lost; the next definition"
                    + " comparison sees no previous value.";
            default:
                return "PrefabRegistry: live-refresh fingerprint rehydrate took nothing: Bootstrap"
                    + " ContentMemory held no rows. The previous generation's fingerprints are lost;"
                    + " the next definition comparison sees no previous value.";
        }
    }

    // ---------------------------------------------------------------------
    // Post-apply duration restore (R27 finding 12)
    // ---------------------------------------------------------------------

    /// <summary>
    /// Frames to keep looking for the instance <c>DebugEventsSystem.ApplyBuff</c> was asked to
    /// create. <c>ApplyBuff</c> only queues an <c>ApplyBuffDebugEvent</c>; the buff entity is
    /// built by the game's buff system on a later update, so the old same-call restore always
    /// looked at a frame where the new instance did not exist yet and silently dropped the
    /// remaining duration (R27 finding 12).
    /// </summary>
    public const int RestoreDurationFrames = 8;

    /// <summary>What the post-apply duration restore must do on this frame.</summary>
    public enum RestoreStep
    {
        /// <summary>The new instance exists: write <c>LifeTime.Duration</c> now and finish.</summary>
        Restore = 0,
        /// <summary>Not there yet and the budget is not spent: come back next frame.</summary>
        Wait = 1,
        /// <summary>Budget spent: log once and give up (the clone's authored duration stands).</summary>
        GiveUp = 2,
    }

    /// <summary>Frames already spent waiting for the re-applied instance to appear.</summary>
    public struct RestoreState
    {
        public int Frames;
    }

    /// <summary>
    /// One frame of the post-apply restore. <paramref name="state"/> is by reference and must
    /// be written back onto the queued item, exactly like <see cref="Advance"/>.
    /// </summary>
    public static RestoreStep AdvanceRestore(ref RestoreState state, bool instanceFound)
    {
        if (instanceFound) return RestoreStep.Restore;
        if (state.Frames < RestoreDurationFrames)
        {
            state.Frames++;
            return RestoreStep.Wait;
        }
        return RestoreStep.GiveUp;
    }

    /// <summary>
    /// Logged once when the re-applied instance never showed up inside
    /// <see cref="RestoreDurationFrames"/>. Grep <c>duration not restored</c>.
    /// </summary>
    public static string FormatDurationNotRestored(string id, float duration) =>
        "PrefabRegistry: live refresh of '" + (id ?? "") + "' duration not restored ("
        + duration.ToString(CultureInfo.InvariantCulture) + "s): the re-applied instance did not appear within "
        + RestoreDurationFrames.ToString(CultureInfo.InvariantCulture) + " frame(s)";

    // ---------------------------------------------------------------------
    // Owed applies (R27 finding 13)
    // ---------------------------------------------------------------------

    /// <summary>
    /// True while a refresh has destroyed the live instance and not yet re-applied it. Such an
    /// item must never be dropped: dropping it is what leaves the holder without the buff
    /// (a dead world between the two frames, a throw inside the apply, or Uninstall landing in
    /// the one-frame window — R27 finding 13).
    /// </summary>
    public static bool OwesApply(bool destroyedByUs, bool applied) => destroyedByUs && !applied;

    /// <summary>How many extra frames an owed apply may be retried before it is declared lost.</summary>
    public const int MaxOwedApplyRetries = 30;

    /// <summary>True while an owed apply still has retry budget left.</summary>
    public static bool ShouldRetryOwedApply(int retries) => retries < MaxOwedApplyRetries;

    /// <summary>
    /// Loud warning each time an owed apply is deferred instead of dropped.
    /// Grep <c>re-apply owed</c>.
    /// </summary>
    public static string FormatOwedApplyRetry(string id, string why, int retries) =>
        "PrefabRegistry: live refresh of '" + (id ?? "") + "' re-apply owed after destroy ("
        + (why ?? "") + "); retry " + retries.ToString(CultureInfo.InvariantCulture)
        + "/" + MaxOwedApplyRetries.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Loud error when the retry budget is spent and the holder really did lose the buff.
    /// Grep <c>destroyed but never re-applied</c>.
    /// </summary>
    public static string FormatOwedApplyLost(string id, string why) =>
        "PrefabRegistry: live refresh of '" + (id ?? "") + "' destroyed but never re-applied ("
        + (why ?? "") + "); the holder lost this buff";

    /// <summary>Logged when Uninstall drains owed applies synchronously before clearing the queue.</summary>
    public static string FormatOwedApplyDrained(int count) =>
        "PrefabRegistry: live refresh drained " + count.ToString(CultureInfo.InvariantCulture)
        + " owed re-apply(s) before uninstall.";
}

/// <summary>
/// Game-free accumulator that collapses the apply phases of one prefab into a single
/// live-refresh decision.
///
/// A Core swap (or a plugin hot reload) re-applies a definition in two phases: the clone
/// apply runs while the plugin's <c>Content.Edit</c> passes are not registered yet, and the
/// edit pass runs a moment later when they are. Deciding — and recording the fingerprint —
/// in each phase made the first phase log <c>refresh skipped: definition unchanged</c> against
/// an editor-less fingerprint and the second log <c>refreshed ... (definition changed)</c>
/// against the same definition, so every Core swap destroyed and re-applied opted-in buffs
/// and their on-apply effects re-fired (R26 finding 4, fixed in FU).
///
/// Both phases <see cref="Note"/> instead. The registry settles once, after the frame in which
/// the last phase landed — that is, once this generation's editors are registered — and only
/// then records the fingerprint. <see cref="TrySettle"/> removes the entry, so one settle can
/// never produce two decisions for the same prefab.
/// </summary>
internal sealed class LiveRefreshDecisions
{
    /// <summary>The settled inputs for one <c>AfterCloneChanged</c> call.</summary>
    public readonly struct Decision
    {
        public Decision(
            bool alreadyClonedThisProcess,
            bool definitionChanged,
            bool retargeted,
            bool editPassChangedClone,
            bool previousFingerprintMissing)
        {
            AlreadyClonedThisProcess = alreadyClonedThisProcess;
            DefinitionChanged = definitionChanged;
            Retargeted = retargeted;
            EditPassChangedClone = editPassChangedClone;
            PreviousFingerprintMissing = previousFingerprintMissing;
        }

        public bool AlreadyClonedThisProcess { get; }

        public bool DefinitionChanged { get; }

        public bool Retargeted { get; }

        public bool EditPassChangedClone { get; }

        public bool PreviousFingerprintMissing { get; }
    }

    private struct Phase
    {
        public bool AlreadyCloned;
        public bool Retargeted;
        public bool EditPassWrote;
        public bool InputsChanged;
        public string? Previous;
    }

    private readonly object _gate = new();
    private readonly Dictionary<int, Phase> _phases = new();

    /// <summary>Prefabs waiting to be settled.</summary>
    public int Count
    {
        get { lock (_gate) return _phases.Count; }
    }

    /// <summary>
    /// Record one apply phase. <paramref name="previousFingerprint"/> is the last recorded
    /// fingerprint read <em>before</em> any phase of this apply and is kept from the first
    /// note, so a later phase cannot compare against a fingerprint this apply just wrote.
    /// <paramref name="alreadyClonedThisProcess"/> likewise keeps the first note's value:
    /// only the first phase can tell a first clone from a re-apply.
    /// <paramref name="inputsChanged"/> is an <c>Invalidate</c>-driven re-run (captured editor
    /// values moved); it is the one refresh trigger that no build-stable fingerprint can see.
    /// </summary>
    public void Note(
        int guidHash,
        bool alreadyClonedThisProcess,
        bool retargeted,
        bool editPassWrote,
        bool inputsChanged,
        string? previousFingerprint)
    {
        lock (_gate)
        {
            if (_phases.TryGetValue(guidHash, out var phase))
            {
                phase.Retargeted |= retargeted;
                phase.EditPassWrote |= editPassWrote;
                phase.InputsChanged |= inputsChanged;
            }
            else
            {
                phase = new Phase
                {
                    AlreadyCloned = alreadyClonedThisProcess,
                    Retargeted = retargeted,
                    EditPassWrote = editPassWrote,
                    InputsChanged = inputsChanged,
                    Previous = string.IsNullOrEmpty(previousFingerprint) ? null : previousFingerprint,
                };
            }
            _phases[guidHash] = phase;
        }
    }

    /// <summary>Prefabs with an unsettled note, in no particular order.</summary>
    public int[] PendingGuids()
    {
        lock (_gate)
        {
            if (_phases.Count == 0) return Array.Empty<int>();
            var arr = new int[_phases.Count];
            _phases.Keys.CopyTo(arr, 0);
            return arr;
        }
    }

    /// <summary>
    /// Settle <paramref name="guidHash"/> against the fingerprint computed now that every
    /// phase has landed, and drop the note. False when nothing is pending, so a second
    /// settle of the same apply is a no-op rather than a second log line.
    /// </summary>
    public bool TrySettle(int guidHash, string? currentFingerprint, out Decision decision)
    {
        Phase phase;
        lock (_gate)
        {
            if (!_phases.TryGetValue(guidHash, out phase))
            {
                decision = default;
                return false;
            }
            _phases.Remove(guidHash);
        }

        bool changed = PrefabLiveRefresh.DefinitionChanged(phase.Previous, currentFingerprint);
        decision = new Decision(
            phase.AlreadyCloned,
            changed,
            phase.Retargeted,
            // An editor that rewrote the same values on a re-registered clone is not a change.
            editPassChangedClone: phase.EditPassWrote && (changed || phase.InputsChanged),
            previousFingerprintMissing: string.IsNullOrEmpty(phase.Previous));
        return true;
    }

    /// <summary>Drop every pending note (Core Uninstall).</summary>
    public void Clear()
    {
        lock (_gate) _phases.Clear();
    }
}

/// <summary>
/// Game-free arm / tick / stop counter behind the live-refresh settle. <c>PrefabRegistry</c>
/// owns the <c>Plugin.Frame</c> subscription and this owns the decision of when to subscribe,
/// when to fire, and when to unsubscribe — all three under one lock.
///
/// Two bugs it exists to make impossible (R27 finding 21). <see cref="Arm"/> re-arms on every
/// note: arming only when the countdown had run out gave a note landing on the last frame of
/// the window a zero-frame settle (one phase noted, decision taken without the rest), and it
/// let a late <c>Content.Edit</c> registration miss the window entirely — the settle then
/// recorded an editor-less fingerprint and the late edit pass read as <c>definition changed</c>,
/// destroying and re-applying an opted-in buff for nothing. And the flag is never read-then-written
/// outside the lock, so <c>Plugin.Frame</c> can neither be double-subscribed (two settles, two
/// decisions, two <c>AfterCloneChanged</c> for one apply) nor lose its subscription and leave
/// <c>refreshSettle=N</c> stuck in <c>DescribePins</c> forever.
/// </summary>
internal sealed class RefreshSettleCountdown
{
    private readonly object _gate = new();
    private readonly int _frames;
    private int _remaining;
    private bool _hooked;

    public RefreshSettleCountdown(int frames)
    {
        _frames = frames < 1 ? 1 : frames;
    }

    /// <summary>Frames left before the settle fires. 0 when disarmed.</summary>
    public int Remaining
    {
        get { lock (_gate) return _remaining; }
    }

    /// <summary>True while the frame handler is subscribed.</summary>
    public bool Hooked
    {
        get { lock (_gate) return _hooked; }
    }

    /// <summary>
    /// Arm or re-arm the full window. True exactly once per hook cycle: only that caller may
    /// subscribe the frame handler.
    /// </summary>
    public bool Arm()
    {
        lock (_gate)
        {
            _remaining = _frames;
            if (_hooked) return false;
            _hooked = true;
            return true;
        }
    }

    /// <summary>
    /// One frame of the window. True when the settle must run now, and <b>false on a disarmed
    /// countdown</b>: <c>_remaining &gt; 0 &amp;&amp; --_remaining &gt; 0</c> short-circuited at
    /// 0 without decrementing and then returned true, so a handler that <see cref="Stop"/> had
    /// already unsubscribed still ran once — <c>Plugin.Frame</c> snapshots its invocation list
    /// per frame, so a handler removed mid-frame still fires that frame — and settled
    /// immediately instead of no-opping (R28 finding 28.1).
    /// </summary>
    public bool Tick()
    {
        lock (_gate)
        {
            if (_remaining <= 0) return false;
            return --_remaining <= 0;
        }
    }

    /// <summary>
    /// Disarm. True exactly once per hook cycle: only that caller may unsubscribe, so a
    /// double stop cannot remove a handler a later <see cref="Arm"/> re-added.
    /// </summary>
    public bool Stop()
    {
        lock (_gate)
        {
            _remaining = 0;
            if (!_hooked) return false;
            _hooked = false;
            return true;
        }
    }
}

/// <summary>
/// Game-free one-at-a-time counter for the live refresh pump. The ECS queue lives in
/// <c>PrefabLiveRefreshApply</c>; tests pin that three enqueued instances take three takes.
/// </summary>
internal sealed class LiveRefreshSchedule
{
    private int _remaining;

    public int Remaining => _remaining;

    public void Enqueue(int count)
    {
        if (count > 0) _remaining += count;
    }

    /// <summary>True when one instance was reserved for this frame.</summary>
    public bool TryTakeOne()
    {
        if (PrefabLiveRefresh.TakeThisFrame(_remaining) <= 0) return false;
        _remaining--;
        return true;
    }
}
