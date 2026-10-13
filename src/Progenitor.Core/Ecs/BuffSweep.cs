using System;

#if !PROGENITOR_TESTS
using ProjectM;
using ProjectM.Shared;
using Unity.Entities;
#endif

namespace Progenitor.Ecs;

/// <summary>
/// DIAG5 two-phase despawn of a unit that still carries <c>BuffBuffer</c>.
/// Phase <c>buffs</c> (<see cref="Sweep"/> / <see cref="DestroyBuffs"/>):
/// <c>DestroyUtility.Destroy(TryRemoveBuff)</c> alone on each still-existing copied row
/// (skip already-tagged). Phase <c>waiting-buffs</c>: poll until every listed
/// id reports <c>Exists=false</c>. Phase <c>tagged</c>: only then
/// <c>DestroyUtility.Destroy</c> the unit — alone, so the removal replicates to clients
/// (<see cref="DestroyUnitReplicated"/>); <c>DestroyTag</c> is only a logged fallback after
/// <see cref="UnitDestroyFallbackFrames"/> frames. The phase keeps its historical name. Stale <c>BuffBuffer</c>
/// rows (the entity is already gone) do not block <c>tagged</c>. A live row that is not
/// in the listed set is a new buff: destroy it now and start another cycle, capped at
/// <see cref="MaxCycles"/>, then phase <c>stuck</c> without tagging.
///
/// <para>
/// <b>No direct <c>DestroyTag</c> on a buff.</b> <c>ProcessDestroyEventSystem.TagNewDestroyedEntity</c>
/// (server) skips an entity that already carries <c>DestroyTag</c>, so a buff
/// tagged in the same frame never enters the game's destroy flow. Live, that removed the buff but
/// left its <c>BuffBuffer</c> row on the unit (rig qualification 4, 2026-09-07), and a unit that
/// then outlived its despawn was saved with rows naming no entity (the film save, 2026-09-27),
/// which aborts a Burst buff job when the unit dies. <see cref="PruneDanglingRows"/> removes any
/// such row before the despawn finishes or gives up.
/// </para>
///
/// <para>
/// DIAG5: the Unity Error <c>destroyed without being detached</c> is the game's own
/// <c>DetachSystem.DebugJob</c> logging a live detach. It fires for every mod-reachable
/// order (Attach strip, destroy-then-DetachSystem, or both) and is not a failure. Failure
/// signals are <c>Tried to detach … AttachedBuffer</c> (missing parent row),
/// <c>points at buff.Target</c>, a Burst abort, or a dead process. Do not strip
/// <c>Attach</c>; do not call private <c>DetachEntity</c>.
/// </para>
///
/// <para>Reads <c>BuffBuffer</c> through the non-generic <c>GetBufferLength</c> /
/// <c>GetBufferRawRO</c> pair (never <c>GetBuffer&lt;T&gt;</c>; interop trap 2). Element stride
/// and the Entity field offset are dump-derived <see cref="Layout"/> constants, never
/// <c>TypeManager</c>. A reported length above <see cref="MaxElements"/> is refused before
/// any allocation or raw read (R27/R30 length-lie class).</para>
/// </summary>
public static class BuffSweep
{
    /// <summary>Hard cap on <c>BuffBuffer</c> elements walked in one call.</summary>
    public const int MaxElements = 256;

    /// <summary>
    /// Hard cap on destroy cycles for one despawn. Cycle 1 is the initial
    /// <see cref="Sweep"/>. Each later live row that is not in the listed set starts
    /// another cycle. Exceeding this is phase <c>stuck</c> without tagging the unit.
    /// </summary>
    public const int MaxCycles = 4;

    /// <summary>
    /// DIAG5: frames the Units despawn path will poll listed buff ids
    /// before logging a warning and re-arming. The unit is still never
    /// <c>DestroyUtility.Destroy</c>'d while a listed buff id <c>Exists</c>.
    /// </summary>
    public const int DestroyTagDeferFrames = 120;

    /// <summary><c>BuffBuffer</c> native element stride (server metadata).</summary>
    public const int ElementStride = Layout.BuffBufferStride;

    /// <summary><c>BuffBuffer.Entity</c> byte offset inside one element (server metadata).</summary>
    public const int EntityOffset = Layout.BuffBufferEntityOffset;

    /// <summary>Finalize / poll takes no mutation this call.</summary>
    public const string ActionNone = "none";

    /// <summary><c>DestroyUtility.Destroy(TryRemoveBuff)</c> on remaining buffs.</summary>
    public const string ActionDestroyBuffs = "destroy-buffs";

    /// <summary><c>DestroyUtility.Destroy</c> on the unit (DestroyTag only as the delayed fallback). Never before buffs are absent.</summary>
    public const string ActionDestroyUnit = "destroy-unit";

    /// <summary>One <c>BuffBuffer</c> row's Entity, copied out before any destroy runs.</summary>
    public readonly struct EntityId
    {
        public readonly int Index;
        public readonly int Version;

        public EntityId(int index, int version)
        {
            Index = index;
            Version = version;
        }

        public bool IsNull => Index == 0 && Version == 0;

        public override string ToString() => Index + ":" + Version;
    }

    /// <summary>
    /// How many buffer rows were walked, issued a destroy, skipped, or were already
    /// gone (stale), and whether the pass is complete enough for a later-frame
    /// <c>DestroyTag</c> on the unit.
    /// </summary>
    public readonly struct Result
    {
        public readonly int Found;
        public readonly int Destroyed;
        public readonly int Skipped;
        public readonly int Stale;
        public readonly bool Complete;
        public readonly string? Reason;
        public readonly EntityId[] DestroyedIds;
        public readonly EntityId[] StaleIds;

        public Result(int found, int destroyed, int skipped)
            : this(found, destroyed, skipped, complete: true, reason: null)
        {
        }

        public Result(int found, int destroyed, int skipped, bool complete, string? reason)
            : this(found, destroyed, skipped, complete, reason, stale: 0, destroyedIds: null, staleIds: null)
        {
        }

        public Result(
            int found,
            int destroyed,
            int skipped,
            bool complete,
            string? reason,
            int stale,
            EntityId[]? destroyedIds,
            EntityId[]? staleIds = null)
        {
            Found = found;
            Destroyed = destroyed;
            Skipped = skipped;
            Stale = stale < 0 ? 0 : stale;
            Complete = complete;
            Reason = reason;
            DestroyedIds = destroyedIds ?? Array.Empty<EntityId>();
            StaleIds = staleIds ?? Array.Empty<EntityId>();
        }

        /// <summary>
        /// Buff ids copied for later phases. Same storage as <see cref="DestroyedIds"/>:
        /// still-existing rows (destroyed this call or already-tagged).
        /// </summary>
        public EntityId[] BuffIds => DestroyedIds;
    }

    /// <summary>
    /// DIAG5 finalize decision: the phase string the Dev op returns, the mutation this
    /// call should take (<see cref="ActionNone"/>, <see cref="ActionDestroyBuffs"/>,
    /// <see cref="ActionDestroyUnit"/>), and an optional <see cref="Reason"/> (phase
    /// <c>stuck</c>).
    /// </summary>
    public readonly struct FinalizeStep
    {
        public readonly string Phase;
        public readonly string Action;
        public readonly string? Reason;

        public FinalizeStep(string phase, string action)
            : this(phase, action, reason: null)
        {
        }

        public FinalizeStep(string phase, string action, string? reason)
        {
            Phase = phase ?? "";
            Action = action ?? ActionNone;
            Reason = reason;
        }
    }

    /// <summary>
    /// How many elements of a buffer of length <paramref name="reportedLength"/> to walk.
    /// Negative or zero yields 0; above <see cref="MaxElements"/> yields the cap. Sweep
    /// itself refuses an over-cap length before calling this (see <see cref="LengthAboveCap"/>).
    /// </summary>
    public static int BoundLength(int reportedLength)
    {
        if (reportedLength <= 0) return 0;
        return reportedLength > MaxElements ? MaxElements : reportedLength;
    }

    /// <summary>
    /// True when <paramref name="reportedLength"/> is above <see cref="MaxElements"/>.
    /// Sweep refuses that length before allocating or reading; it does not silently cap.
    /// </summary>
    public static bool LengthAboveCap(int reportedLength) => reportedLength > MaxElements;

    /// <summary>
    /// Copy Entity (index, version) pairs out of a raw <c>BuffBuffer</c> span, in element
    /// order, capped at <see cref="MaxElements"/>. Stops if the span is shorter than the
    /// claimed walk. Does not check existence — that is the live half.
    /// </summary>
    public static int CopyEntities(ReadOnlySpan<byte> bytes, int reportedLength, Span<EntityId> dest)
    {
        int n = BoundLength(reportedLength);
        if (n > dest.Length) n = dest.Length;
        int copied = 0;
        for (int i = 0; i < n; i++)
        {
            long at = (long)i * ElementStride + EntityOffset;
            if (at + 8 > bytes.Length) break;
            dest[copied++] = new EntityId(ReadInt32(bytes, (int)at), ReadInt32(bytes, (int)at + 4));
        }

        return copied;
    }

    /// <summary>
    /// Buffer-order destroy list: existing, non-null rows, in the order they were copied.
    /// <paramref name="exists"/> is parallel to <paramref name="copied"/>.
    /// </summary>
    public static int SelectExisting(
        ReadOnlySpan<EntityId> copied,
        ReadOnlySpan<bool> exists,
        Span<EntityId> dest)
    {
        int n = copied.Length;
        if (exists.Length < n) n = exists.Length;
        int written = 0;
        for (int i = 0; i < n; i++)
        {
            if (!exists[i] || copied[i].IsNull) continue;
            if (written >= dest.Length) break;
            dest[written++] = copied[i];
        }

        return written;
    }

    /// <summary>
    /// DIAG5: maps {existingLeft, unitExists} to the next finalize phase and the action
    /// to take. A missing unit is <c>tagged</c> with no mutation (the unit is never
    /// destroyed before this phase). When some listed buff still exists, wait
    /// (<c>waiting-buffs</c>); do not destroy listed ids again. Stale buffer rows do
    /// not block <c>tagged</c>; pass <paramref name="newLiveLeft"/> for a live row that
    /// is not in the listed set.
    /// </summary>
    public static FinalizeStep DecideFinalizeStep(bool existingLeft, bool unitExists) =>
        DecideFinalizeStep(existingLeft, unitExists, newLiveLeft: false);

    /// <summary>
    /// DIAG5 two-phase machine: listed ids that still exist are <c>waiting-buffs</c>
    /// (no mutation). When they are gone, a new live <c>BuffBuffer</c> row is phase
    /// <c>buffs</c> + <see cref="ActionDestroyBuffs"/> under <see cref="MaxCycles"/>,
    /// or <c>stuck</c> at the cap with no unit tag. <c>tagged</c> +
    /// <see cref="ActionDestroyUnit"/> only when the unit still exists, nothing listed
    /// exists, and there is no new live row.
    /// </summary>
    public static FinalizeStep DecideFinalizeStep(
        bool existingLeft,
        bool unitExists,
        bool newLiveLeft,
        int cycle = 1)
    {
        if (cycle < 1) cycle = 1;
        if (!unitExists)
            return new FinalizeStep("tagged", ActionNone);
        if (existingLeft)
            return new FinalizeStep("waiting-buffs", ActionNone);
        if (newLiveLeft)
        {
            if (cycle >= MaxCycles)
                return new FinalizeStep("stuck", ActionNone, FormatStuckReason(cycle));
            return new FinalizeStep("buffs", ActionDestroyBuffs);
        }

        return new FinalizeStep("tagged", ActionDestroyUnit);
    }

    /// <summary>
    /// True when every entry in <paramref name="exists"/> is false (every listed buff id
    /// is gone). Empty is absent. This is listed ids only: a stale <c>BuffBuffer</c> row
    /// whose entity does not exist is not part of this test and does not block
    /// <c>tagged</c>. A live row that is not listed is a new buff (see
    /// <see cref="ClassifyBufferRows"/>).
    /// </summary>
    public static bool AllAbsent(ReadOnlySpan<bool> exists)
    {
        for (int i = 0; i < exists.Length; i++)
        {
            if (exists[i]) return false;
        }

        return true;
    }

    /// <summary>
    /// True when <paramref name="id"/> appears in <paramref name="ids"/> (index and
    /// version). Null ids never match.
    /// </summary>
    public static bool ContainsId(ReadOnlySpan<EntityId> ids, EntityId id)
    {
        if (id.IsNull) return false;
        for (int i = 0; i < ids.Length; i++)
        {
            if (ids[i].Index == id.Index && ids[i].Version == id.Version)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Classify remaining <c>BuffBuffer</c> rows against the listed set.
    /// A row whose entity does not exist is stale (ignored for <c>tagged</c>).
    /// A row whose entity exists and is not in <paramref name="listed"/> is a new live
    /// buff (another destroy cycle). Listed live rows are neither.
    /// Null rows are skipped. Counts increment even when a dest span is full.
    /// </summary>
    public static void ClassifyBufferRows(
        ReadOnlySpan<EntityId> listed,
        ReadOnlySpan<EntityId> rows,
        ReadOnlySpan<bool> rowExists,
        Span<EntityId> staleDest,
        Span<EntityId> newLiveDest,
        out int staleCount,
        out int newLiveCount)
    {
        staleCount = 0;
        newLiveCount = 0;
        int n = rows.Length;
        if (rowExists.Length < n) n = rowExists.Length;
        for (int i = 0; i < n; i++)
        {
            if (rows[i].IsNull) continue;
            if (!rowExists[i])
            {
                if (staleCount < staleDest.Length)
                    staleDest[staleCount] = rows[i];
                staleCount++;
                continue;
            }

            if (ContainsId(listed, rows[i])) continue;
            if (newLiveCount < newLiveDest.Length)
                newLiveDest[newLiveCount] = rows[i];
            newLiveCount++;
        }
    }

    /// <summary>
    /// Rows of one holder's <c>BuffBuffer</c> that name no entity, as indices from highest to
    /// lowest so <c>RemoveAt</c> in that order leaves the others in place.
    /// <paramref name="rowExists"/> is false for a null row as well as a destroyed one: both are
    /// the dangling reference a Burst buff job aborts on. Returns how many indices were written.
    /// </summary>
    public static int DanglingRowsDescending(ReadOnlySpan<bool> rowExists, Span<int> dest)
    {
        int written = 0;
        for (int i = rowExists.Length - 1; i >= 0; i--)
        {
            if (rowExists[i]) continue;
            if (written >= dest.Length) break;
            dest[written++] = i;
        }

        return written;
    }

    /// <summary>Info line for rows <see cref="PruneDanglingRows"/> removed.</summary>
    public static string FormatPruneLog(string label, int index, int version, int removed, int rows) =>
        label + ": removed " + removed + " of " + rows + " BuffBuffer row(s) naming no entity on "
        + index + ":" + version;

    /// <summary>Phase <c>stuck</c> reason: the destroy cycle cap was exceeded.</summary>
    public static string FormatStuckReason(int cycle) =>
        "cycle cap (" + MaxCycles + ") exceeded after " + cycle + " destroy cycle(s)";

    /// <summary>
    /// DIAG5: issue <c>DestroyUtility.Destroy(TryRemoveBuff)</c> on a buff only while it exists
    /// and the game has not tagged it yet. Already-tagged or absent rows are left alone so a
    /// buffs pass is idempotent.
    /// </summary>
    public static bool ShouldDestroyBuff(bool exists, bool hasDestroyTag) => exists && !hasDestroyTag;

    /// <summary>
    /// Completeness of a copy+destroy pass. Incomplete when the reported length is above the
    /// cap (nothing read), not every row was copied, a still-existing row was skipped for a
    /// reason other than absence, the buffer was not read, or an exception was swallowed.
    /// Skipping a row because the entity is already gone is not incomplete.
    /// </summary>
    public static Result FromPass(
        int reportedLength,
        int copied,
        int destroyed,
        int skippedAbsent,
        int skippedError,
        bool exceptionSwallowed,
        bool readAttempted,
        int stale = 0,
        EntityId[]? destroyedIds = null,
        EntityId[]? staleIds = null)
    {
        int skipped = skippedAbsent + skippedError;
        if (LengthAboveCap(reportedLength))
            return new Result(0, 0, 0, false, "length " + reportedLength + " above cap", 0, null, null);
        if (exceptionSwallowed)
            return new Result(copied, destroyed, skipped, false, "exception swallowed", stale, destroyedIds, staleIds);
        if (reportedLength > 0 && !readAttempted)
            return new Result(0, 0, 0, false, "buffer not read", 0, null, null);
        int walk = BoundLength(reportedLength);
        if (walk > 0 && copied < walk)
            return new Result(copied, destroyed, skipped, false, "not every row was read", stale, destroyedIds, staleIds);
        if (skippedError > 0)
            return new Result(copied, destroyed, skipped, false, "row skipped", stale, destroyedIds, staleIds);
        return new Result(copied, destroyed, skipped, true, null, stale, destroyedIds, staleIds);
    }

    /// <summary>
    /// Completeness gate for a later-frame unit <c>DestroyTag</c> (DIAG5). Phase
    /// <c>buffs</c> / <c>stuck</c> never tags the unit. The live path also requires
    /// every copied id <c>Exists=false</c> (see <see cref="AllAbsent"/>); stale buffer
    /// rows do not block, new live rows start another cycle.
    /// </summary>
    public static bool ShouldAddDestroyTag(in Result sweep) => sweep.Complete;

    /// <summary>Same refuse as the Dev <c>entity</c> op: <c>PlayerCharacter</c> or <c>User</c>.</summary>
    public static bool RefuseAsPlayerOrUser(bool hasPlayerCharacter, bool hasUser) =>
        hasPlayerCharacter || hasUser;

    /// <summary>The one log line per unit: <c>BuffSweep unit=&lt;i:v&gt; buffs=&lt;n&gt; destroyed=&lt;m&gt; skipped=&lt;k&gt; stale=&lt;s&gt;</c>.</summary>
    public static string FormatLog(int index, int version, int found, int destroyed, int skipped, int stale = 0) =>
        "BuffSweep unit=" + index + ":" + version
        + " buffs=" + found
        + " destroyed=" + destroyed
        + " skipped=" + skipped
        + " stale=" + stale;

    /// <summary>Warning when the unit must not receive <c>DestroyTag</c> because the sweep was incomplete.</summary>
    public static string FormatIncompleteUnitWarning(int index, int version, string? reason) =>
        "BuffSweep incomplete on " + index + ":" + version
        + "; DestroyTag not added"
        + (string.IsNullOrEmpty(reason) ? "" : " (" + reason + ")");

    /// <summary>Warning when Units despawn refuses a player or user entity.</summary>
    public static string FormatPlayerRefuseLog(int index, int version) =>
        "Units: despawn refused " + index + ":" + version + " (PlayerCharacter or User)";

    /// <summary>
    /// DIAG5: Units despawn polled <see cref="DestroyTagDeferFrames"/> frames while listed
    /// buffs still existed and is re-arming. The unit is still not destroyed.
    /// </summary>
    public static string FormatDeferTimeoutWarning(int index, int version, int frames, int pending) =>
        FormatDeferTimeoutWarning(index, version, frames, pending, "buffs");

    /// <summary>Same warning with the phase that timed out (always <c>buffs</c> in DIAG5).</summary>
    public static string FormatDeferTimeoutWarning(int index, int version, int frames, int pending, string phase) =>
        "BuffSweep DIAG5: proceeding past " + phase + " on " + index + ":" + version
        + " after " + frames + " frames with " + pending + " buff(s) still pending";

    /// <summary>
    /// Frames <see cref="DestroyUnitReplicated"/> lets the game's own destroy flow run before
    /// the <c>DestroyTag</c> fallback. Live 2026-09-24: <c>DestroyUtility.Destroy</c> alone removed
    /// a loot drop and a boss on the server and on the client within 2 s.
    /// </summary>
    public const int UnitDestroyFallbackFrames = 120;

    /// <summary>
    /// The replicated unit removal's only fallback: tag the unit only when it still exists, carries
    /// no <c>DestroyTag</c>, and <see cref="UnitDestroyFallbackFrames"/> frames have passed.
    /// </summary>
    public static bool ShouldTagUnitFallback(bool unitExists, bool hasDestroyTag, int framesWaited) =>
        unitExists && !hasDestroyTag && framesWaited >= UnitDestroyFallbackFrames;

    /// <summary>Warning logged when the fallback tags a unit the destroy flow did not finish.</summary>
    public static string FormatUnitFallbackWarning(string label, int index, int version, int frames) =>
        label + ": " + index + ":" + version + " still exists " + frames
        + " frames after DestroyUtility.Destroy; adding DestroyTag as a fallback (clients may keep a stale copy)";

    /// <summary>
    /// Frames without a watcher run after which <see cref="DestroyUnitReplicated"/> treats a posted
    /// watcher as lost. A rolled-back Core swap (Uninstall, <c>DropCoreWork</c>, then
    /// <c>ReinstallCurrent</c> of the same load context) drops the queued pass but keeps this
    /// class's statics, so the posted flag alone would stay set with nothing running.
    /// </summary>
    public const int UnitWatcherStaleFrames = 30;

    /// <summary>
    /// Post a new watcher when none is posted, or when the posted one has not run for more than
    /// <see cref="UnitWatcherStaleFrames"/> frames (it was dropped). A newer post supersedes the old one.
    /// </summary>
    public static bool ShouldPostUnitWatcher(bool watcherPosted, int lastWatcherFrame, int now) =>
        !watcherPosted || now - lastWatcherFrame > UnitWatcherStaleFrames;

    /// <summary>Warning logged when a lost watcher is replaced.</summary>
    public static string FormatStaleWatcherWarning(int framesSinceRun, int pending) =>
        "BuffSweep: destroy watcher has not run for " + framesSinceRun + " frames (dropped by a Core swap rollback?); re-posting for "
        + pending + " pending removal(s)";

#if !PROGENITOR_TESTS
    /// <summary>
    /// The unit's final DIAG5 step: <c>DestroyUtility.Destroy</c> <b>only</b>, so the game's destroy
    /// flow finishes the entity and replicates the removal. Adding <c>DestroyTag</c> in the same
    /// frame removed the entity on the server but the client never heard (live 2026-09-24: a loot
    /// drop stayed visible, 397 of 400 removed Frostmaws stayed on the client as disabled
    /// entities). One shared watcher checks every pending unit once a frame; only if one still exists after
    /// <see cref="UnitDestroyFallbackFrames"/> frames is <c>DestroyTag</c> added, with a warning.
    /// A world reload ends the poll (the id would name another entity). A watcher lost to a
    /// rolled-back Core swap is replaced by the next call (<see cref="ShouldPostUnitWatcher"/>).
    /// </summary>
    public static void DestroyUnitReplicated(EntityManager em, Entity unit, DestroyDebugReason reason, string label)
    {
        DestroyUtility.Destroy(em, unit, reason, callerFilePath: null, callerLineNumber: 0);
        int now = UnityEngine.Time.frameCount;
        int token, stale = -1, pending = 0;
        lock (WatchGate)
        {
            Watched.Add(new DestroyWatch(em, unit, label, Progenitor.WorldState.WorldEpoch.Current, now));
            if (!ShouldPostUnitWatcher(_watcherPosted, _watcherFrame, now)) return;
            if (_watcherPosted)
            {
                stale = now - _watcherFrame;
                pending = Watched.Count;
            }
            _watcherPosted = true;
            _watcherFrame = now;
            token = ++_watcherToken;
        }
        if (stale >= 0) Plugin.Logger?.LogWarning(FormatStaleWatcherWarning(stale, pending));
        PostWatcher(token);
    }

    private readonly struct DestroyWatch
    {
        internal DestroyWatch(EntityManager em, Entity unit, string label, int epoch, int startFrame)
        {
            Em = em;
            Unit = unit;
            Label = label;
            Epoch = epoch;
            StartFrame = startFrame;
        }

        internal EntityManager Em { get; }
        internal Entity Unit { get; }
        internal string Label { get; }
        internal int Epoch { get; }
        internal int StartFrame { get; }
    }

    private static readonly object WatchGate = new();
    private static readonly System.Collections.Generic.List<DestroyWatch> Watched = new();
    private static bool _watcherPosted;
    /// <summary>Frame the current watcher was posted or last ran.</summary>
    private static int _watcherFrame;
    /// <summary>Identifies the current watcher chain; a pass from a superseded chain exits.</summary>
    private static int _watcherToken;

    /// <summary>
    /// One watcher for every pending removal, whoever asked: it runs once per frame while anything is
    /// pending and measures the wait in real frames (<c>Time.frameCount</c>), not in its own runs.
    /// </summary>
    private static void PostWatcher(int token)
    {
        try
        {
            Dev.MainThread.PostDelayed(0, () => WatchPass(token));
        }
        catch (Exception e)
        {
            lock (WatchGate)
            {
                if (token == _watcherToken) _watcherPosted = false;
            }
            Plugin.Logger?.LogWarning("BuffSweep: destroy watch defer threw: " + e.Message);
        }
    }

    private static void WatchPass(int token)
    {
        DestroyWatch[] pending;
        int now = UnityEngine.Time.frameCount;
        lock (WatchGate)
        {
            if (token != _watcherToken) return;
            _watcherFrame = now;
            pending = Watched.ToArray();
        }
        var keep = new System.Collections.Generic.List<DestroyWatch>(pending.Length);
        foreach (var w in pending)
        {
            try
            {
                if (Progenitor.WorldState.WorldEpoch.IsStale(w.Epoch)) continue;
                if (!w.Em.Exists(w.Unit)) continue;
                bool tagged = w.Unit.Has<DestroyTag>(w.Em);
                if (tagged) continue;
                int frames = now - w.StartFrame;
                if (ShouldTagUnitFallback(true, tagged, frames))
                {
                    Plugin.Logger?.LogWarning(FormatUnitFallbackWarning(w.Label, w.Unit.Index, w.Unit.Version, frames));
                    w.Unit.Add<DestroyTag>(w.Em);
                    continue;
                }
                keep.Add(w);
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning(w.Label + ": destroy watch on " + w.Unit.Index + ":" + w.Unit.Version + " threw: " + e.Message);
            }
        }
        bool again;
        lock (WatchGate)
        {
            // Keep entries added while this pass ran.
            var added = Watched.GetRange(pending.Length, Watched.Count - pending.Length);
            Watched.Clear();
            Watched.AddRange(keep);
            Watched.AddRange(added);
            again = Watched.Count > 0;
            _watcherPosted = again;
        }
        if (again) PostWatcher(token);
    }

    /// <summary>Removals still being watched (diagnostics and Core uninstall).</summary>
    internal static int PendingUnitDestroys
    {
        get { lock (WatchGate) return Watched.Count; }
    }

    /// <summary>
    /// DIAG5 phase <c>buffs</c>: while <paramref name="unit"/> exists, copy <c>BuffBuffer</c>
    /// rows and <c>DestroyUtility.Destroy(..., TryRemoveBuff)</c> (no direct <c>DestroyTag</c>) each
    /// still-existing buff that does not already carry <c>DestroyTag</c>. Does not destroy
    /// or tag the unit. No-op when the unit is missing or has no buffer. A reported length
    /// above <see cref="MaxElements"/> is refused before any raw read.
    /// <see cref="Result.DestroyedIds"/> is the listed set (still-existing ids);
    /// <see cref="Result.StaleIds"/> is rows whose entity did not exist.
    /// </summary>
    public static unsafe Result Sweep(EntityManager em, Entity unit)
    {
        int found = 0, skippedAbsent = 0, skippedError = 0, destroyed = 0;
        int reportedLength = 0;
        bool exceptionSwallowed = false;
        bool readAttempted = false;
        EntityId[]? buffIds = null;
        EntityId[]? staleIds = null;
        try
        {
            if (unit == Entity.Null || !em.Exists(unit))
            {
                readAttempted = true;
            }
            else
            {
                var bufferType = ComponentIndex.Of<BuffBuffer>(ComponentType.AccessMode.ReadOnly);
                if (!em.HasComponent(unit, bufferType))
                {
                    readAttempted = true;
                }
                else
                {
                    var typeIndex = ComponentIndex.IndexOf<BuffBuffer>();
                    reportedLength = em.GetBufferLength(unit, typeIndex);
                    if (LengthAboveCap(reportedLength))
                    {
                        var refused = FromPass(reportedLength, 0, 0, 0, 0, false, false);
                        Plugin.Logger?.LogInfo(FormatLog(unit.Index, unit.Version, refused.Found, refused.Destroyed, refused.Skipped, refused.Stale));
                        return refused;
                    }

                    int walk = BoundLength(reportedLength);
                    if (walk <= 0)
                    {
                        readAttempted = true;
                    }
                    else
                    {
                        void* raw = em.GetBufferRawRO(unit, typeIndex);
                        if (raw != null)
                        {
                            readAttempted = true;
                            var span = new ReadOnlySpan<byte>(raw, walk * ElementStride);
                            var copied = new EntityId[MaxElements];
                            found = CopyEntities(span, reportedLength, copied);
                            var kept = new EntityId[found];
                            var staleBuf = new EntityId[found];
                            int keptCount = DestroyCopied(
                                em,
                                copied,
                                found,
                                kept,
                                staleBuf,
                                out skippedAbsent,
                                out skippedError,
                                out destroyed);
                            buffIds = Shrink(kept, keptCount);
                            staleIds = Shrink(staleBuf, skippedAbsent);
                        }
                    }
                }
            }
        }
        catch (Exception e)
        {
            exceptionSwallowed = true;
            Plugin.Logger?.LogWarning("BuffSweep threw on " + unit.Index + ":" + unit.Version + ": " + e.Message);
        }

        var result = FromPass(
            reportedLength,
            found,
            destroyed,
            skippedAbsent,
            skippedError,
            exceptionSwallowed,
            readAttempted,
            skippedAbsent,
            buffIds,
            staleIds);
        Plugin.Logger?.LogInfo(FormatLog(unit.Index, unit.Version, result.Found, result.Destroyed, result.Skipped, result.Stale));
        return result;
    }

    /// <summary>
    /// DIAG5 phase <c>buffs</c>: <c>DestroyUtility.Destroy(..., TryRemoveBuff)</c> (no direct
    /// <c>DestroyTag</c>) on each still-existing id that does not already carry
    /// <c>DestroyTag</c>. Already-tagged rows are skipped (idempotent). Returns how many
    /// ids received a destroy this call. Does not touch the unit.
    /// </summary>
    public static int DestroyBuffs(EntityManager em, ReadOnlySpan<EntityId> ids) =>
        DestroyBuffs(em, ids, dest: default);

    /// <summary>
    /// Same as <see cref="DestroyBuffs(EntityManager, ReadOnlySpan{EntityId})"/>, writing the
    /// ids that received a destroy this call into <paramref name="dest"/> (capped at dest length).
    /// </summary>
    public static int DestroyBuffs(EntityManager em, ReadOnlySpan<EntityId> ids, Span<EntityId> dest)
    {
        int destroyed = 0, skipped = 0;
        for (int i = 0; i < ids.Length; i++)
        {
            var id = ids[i];
            var buff = new Entity { Index = id.Index, Version = id.Version };
            try
            {
                if (id.IsNull || !em.Exists(buff))
                {
                    skipped++;
                    continue;
                }

                if (!ShouldDestroyBuff(true, buff.Has<DestroyTag>(em)))
                {
                    skipped++;
                    continue;
                }

                DestroyUtility.Destroy(em, buff, DestroyDebugReason.TryRemoveBuff);
                if (destroyed < dest.Length)
                    dest[destroyed] = id;
                destroyed++;
            }
            catch (Exception e)
            {
                skipped++;
                Plugin.Logger?.LogWarning(
                    "BuffSweep destroy " + id.Index + ":" + id.Version + " threw: " + e.Message);
            }
        }

        Plugin.Logger?.LogInfo(FormatLog(0, 0, ids.Length, destroyed, skipped, 0));
        return destroyed;
    }

    /// <summary>
    /// Remove every <c>BuffBuffer</c> row on <paramref name="unit"/> that names no entity
    /// (<see cref="DanglingRowsDescending"/>), so a unit that outlives its despawn is never saved
    /// with one. The read is raw, as in <see cref="Peek"/>; the edit is
    /// <c>EntityManager.GetBuffer&lt;BuffBuffer&gt;</c> (already used by Core's
    /// buffer walks) and <c>DynamicBuffer&lt;BuffBuffer&gt;.RemoveAt</c> (run live by
    /// Bootstrap's load repair). The typed length is re-read first and a disagreement removes
    /// nothing. A row whose existence check throws is kept. Returns the rows removed, or -1 when
    /// the buffer could not be read or edited.
    /// </summary>
    public static unsafe int PruneDanglingRows(EntityManager em, Entity unit, string label)
    {
        try
        {
            if (unit == Entity.Null || !em.Exists(unit)) return 0;
            var bufferType = ComponentIndex.Of<BuffBuffer>(ComponentType.AccessMode.ReadOnly);
            if (!em.HasComponent(unit, bufferType)) return 0;
            var typeIndex = ComponentIndex.IndexOf<BuffBuffer>();
            int rows = em.GetBufferLength(unit, typeIndex);
            if (rows <= 0) return 0;
            if (LengthAboveCap(rows)) return -1;
            void* raw = em.GetBufferRawRO(unit, typeIndex);
            if (raw == null) return -1;

            var ids = new EntityId[rows];
            if (CopyEntities(new ReadOnlySpan<byte>(raw, rows * ElementStride), rows, ids) != rows) return -1;
            var exists = new bool[rows];
            for (int i = 0; i < rows; i++)
            {
                if (ids[i].IsNull) continue;
                var buff = new Entity { Index = ids[i].Index, Version = ids[i].Version };
                try { exists[i] = em.Exists(buff); }
                catch { exists[i] = true; }
            }

            var remove = new int[rows];
            int count = DanglingRowsDescending(exists, remove);
            if (count == 0) return 0;

            var buffer = em.GetBuffer<BuffBuffer>(unit);
            if (buffer.Length != rows) return -1;
            for (int k = 0; k < count; k++)
                buffer.RemoveAt(remove[k]);
            Plugin.Logger?.LogInfo(FormatPruneLog(label, unit.Index, unit.Version, count, rows));
            return count;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning(label + ": BuffBuffer prune on " + unit.Index + ":" + unit.Version + " threw: " + e.Message);
            return -1;
        }
    }

    /// <summary>
    /// True when every listed id reports <c>Exists=false</c>. Null ids are ignored.
    /// </summary>
    public static bool AllAbsent(EntityManager em, ReadOnlySpan<EntityId> ids)
    {
        for (int i = 0; i < ids.Length; i++)
        {
            if (ids[i].IsNull) continue;
            var buff = new Entity { Index = ids[i].Index, Version = ids[i].Version };
            try
            {
                if (em.Exists(buff)) return false;
            }
            catch
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Read-only copy of still-listed <c>BuffBuffer</c> rows. Used by DIAG5 finalize to
    /// classify stale vs new-live rows. Does not destroy. Stale rows do not block
    /// <c>tagged</c>; an empty buffer is not required.
    /// </summary>
    public static unsafe Result Peek(EntityManager em, Entity unit, Span<EntityId> dest, out int copied)
    {
        copied = 0;
        int reportedLength = 0;
        bool exceptionSwallowed = false;
        bool readAttempted = false;
        try
        {
            if (unit == Entity.Null || !em.Exists(unit))
            {
                readAttempted = true;
                return FromPass(0, 0, 0, 0, 0, false, true);
            }

            var bufferType = ComponentIndex.Of<BuffBuffer>(ComponentType.AccessMode.ReadOnly);
            if (!em.HasComponent(unit, bufferType))
            {
                readAttempted = true;
                return FromPass(0, 0, 0, 0, 0, false, true);
            }

            var typeIndex = ComponentIndex.IndexOf<BuffBuffer>();
            reportedLength = em.GetBufferLength(unit, typeIndex);
            if (LengthAboveCap(reportedLength))
                return FromPass(reportedLength, 0, 0, 0, 0, false, false);

            int walk = BoundLength(reportedLength);
            if (walk <= 0)
            {
                readAttempted = true;
                return FromPass(reportedLength, 0, 0, 0, 0, false, true);
            }

            void* raw = em.GetBufferRawRO(unit, typeIndex);
            if (raw == null)
                return FromPass(reportedLength, 0, 0, 0, 0, false, false);

            readAttempted = true;
            var span = new ReadOnlySpan<byte>(raw, walk * ElementStride);
            copied = CopyEntities(span, reportedLength, dest);
        }
        catch (Exception e)
        {
            exceptionSwallowed = true;
            Plugin.Logger?.LogWarning("BuffSweep Peek threw on " + unit.Index + ":" + unit.Version + ": " + e.Message);
        }

        return FromPass(reportedLength, copied, 0, 0, 0, exceptionSwallowed, readAttempted);
    }

    /// <summary>
    /// Peek the unit's <c>BuffBuffer</c> and classify each row as stale (entity gone) or
    /// new-live (exists and not in <paramref name="listed"/>). Incomplete peeks leave
    /// both counts at 0; the caller must not tag. An <c>Exists</c> throw is fail-closed
    /// (treated as live).
    /// </summary>
    public static Result ScanBuffer(
        EntityManager em,
        Entity unit,
        ReadOnlySpan<EntityId> listed,
        Span<EntityId> staleDest,
        Span<EntityId> newLiveDest,
        out int staleCount,
        out int newLiveCount)
    {
        staleCount = 0;
        newLiveCount = 0;
        var peekDest = new EntityId[MaxElements];
        var peek = Peek(em, unit, peekDest, out int copied);
        if (!peek.Complete || copied <= 0) return peek;

        var exists = new bool[copied];
        for (int i = 0; i < copied; i++)
        {
            if (peekDest[i].IsNull)
            {
                exists[i] = false;
                continue;
            }

            var buff = new Entity { Index = peekDest[i].Index, Version = peekDest[i].Version };
            try { exists[i] = em.Exists(buff); }
            catch { exists[i] = true; }
        }

        ClassifyBufferRows(
            listed,
            peekDest.AsSpan(0, copied),
            exists,
            staleDest,
            newLiveDest,
            out staleCount,
            out newLiveCount);
        return peek;
    }

    private static int DestroyCopied(
        EntityManager em,
        EntityId[] copied,
        int found,
        EntityId[] keptDest,
        EntityId[] staleDest,
        out int skippedAbsent,
        out int skippedError,
        out int destroyed)
    {
        skippedAbsent = 0;
        skippedError = 0;
        destroyed = 0;
        int kept = 0;
        for (int i = 0; i < found; i++)
        {
            var id = copied[i];
            var buff = new Entity { Index = id.Index, Version = id.Version };
            try
            {
                if (id.IsNull) continue;
                if (!em.Exists(buff))
                {
                    if (skippedAbsent < staleDest.Length)
                        staleDest[skippedAbsent] = id;
                    skippedAbsent++;
                    continue;
                }

                if (ShouldDestroyBuff(true, buff.Has<DestroyTag>(em)))
                {
                    DestroyUtility.Destroy(em, buff, DestroyDebugReason.TryRemoveBuff);
                    destroyed++;
                }

                if (kept < keptDest.Length)
                    keptDest[kept] = id;
                kept++;
            }
            catch (Exception e)
            {
                skippedError++;
                Plugin.Logger?.LogWarning(
                    "BuffSweep destroy " + id.Index + ":" + id.Version + " threw: " + e.Message);
            }
        }

        return kept;
    }

    private static EntityId[] Shrink(EntityId[] buf, int count)
    {
        if (count <= 0) return Array.Empty<EntityId>();
        if (count == buf.Length) return buf;
        var exact = new EntityId[count];
        Array.Copy(buf, exact, count);
        return exact;
    }
#endif

    private static int ReadInt32(ReadOnlySpan<byte> bytes, int at) =>
        bytes[at] | (bytes[at + 1] << 8) | (bytes[at + 2] << 16) | (bytes[at + 3] << 24);
}
