using System;
using System.Collections.Generic;
using System.Globalization;

namespace Progenitor.Bootstrap;

/// <summary>
/// Game-free rules and log text for the world-save load guard (job FSAVE, research
/// <c>save-safety.md</c>). A world save records a mod prefab GUID exactly like a vanilla one.
/// Starting that world <b>without</b> the mod makes
/// <c>ProjectM.LoadPersistenceSystemV2.CopyDataFromPrefabs(DeserializationJobData)</c>
/// throw <c>Failed to load due to missing prefab {guid}</c> for any
/// <i>root</i> deserialized entity (no <c>Unity.Transforms.Parent</c>) whose <c>PrefabGUID</c>
/// misses <c>PrefabLookupMap.TryGetConvertedPrefab</c>; the load aborts half-way
/// (<c>PersistenceV2 - Load Persistence Failed</c>, <c>ServerStartupState.State.Failed</c>) and the
/// next autosave writes the half-loaded world over the good save
/// (<c>PersistenceV2 - Saving game even though the server startup didn't fully succeed!</c>).
/// This type holds only the decisions and the strings so they can be tested
/// without the game.
/// </summary>
public static class SaveSafetyPolicy
{
    public const string ConfigSection = "SaveSafety";
    public const string DropUnknownPrefabsKey = "DropUnknownPrefabs";
    public const string FailLoadInsteadKey = "FailLoadInstead";
    public const string MaxDropPercentKey = "MaxDropPercent";
    public const string MaxDropEntitiesKey = "MaxDropEntities";
    public const string MinPrefabLookupCountKey = "MinPrefabLookupCount";

    public const bool DropUnknownPrefabsDefault = true;
    public const bool FailLoadInsteadDefault = false;

    /// <summary>
    /// Blast-radius ceiling as a percentage of the entities the guard actually looked at. A missing
    /// mod costs a handful of entities out of tens of thousands; anything above this is the
    /// signature of a broken <c>PrefabLookupMap</c>, not of a missing mod (job FSAVE2, R29
    /// finding 1).
    /// </summary>
    public const double MaxDropPercentDefault = 0.5;

    /// <summary>Absolute ceiling, so a small save cannot be emptied by the percentage alone.</summary>
    public const int MaxDropEntitiesDefault = 5000;

    /// <summary>
    /// Liveness floor on <c>PrefabLookupMap.Count()</c> (the same counter the game prints in its
    /// own <c>Amount of prefabs registered: {1}</c> line).
    /// A fully built V Rising prefab collection holds tens of thousands of entries; a map with
    /// fewer than this has not been populated yet, and every miss it reports is the map's fault.
    /// </summary>
    public const int MinPrefabLookupCountDefault = 5000;

    public const string DropUnknownPrefabsDescription =
        "When true (default), Progenitor removes and destroys deserialized root entities whose PrefabGUID the prefab collection cannot resolve, so a world saved with a mod still loads after that mod is removed. Those entities are gone for good and the next autosave will not contain them. The drop is capped by MaxDropPercent / MaxDropEntities and refused outright when the prefab lookup itself looks unpopulated: over the cap the guard does nothing and the load fails exactly as vanilla, which never loses data silently. Set false to leave every load exactly as vanilla (it aborts on the first unknown GUID and the next autosave overwrites the good save).";

    public const string FailLoadInsteadDescription =
        "When true, Progenitor only reports the unknown prefab GUIDs and lets the vanilla load abort instead of dropping anything. Overrides DropUnknownPrefabs. For operators who would rather lose the boot than lose the entities — stop the server before the next autosave, restore the mod, and start again.";

    public const string MaxDropPercentDescription =
        "Ceiling on how much of one load the guard may drop, as a percentage of the entities it scanned (default 0.5). Above it the guard drops nothing and lets the vanilla load failure happen, because a large miss ratio means the prefab lookup is broken, not that a mod is missing. 0 disables dropping entirely.";

    public const string MaxDropEntitiesDescription =
        "Absolute ceiling on how many entities the guard may drop in one load (default 5000), counting children of a dropped root. Above it the guard drops nothing and lets the vanilla load failure happen. 0 disables dropping entirely.";

    public const string MinPrefabLookupCountDescription =
        "Minimum number of prefabs that must be registered in PrefabLookupMap before the guard trusts a lookup miss (default 5000). A vanilla V Rising collection holds tens of thousands; a smaller map means the collection is not built yet, so every miss is the map's fault and nothing may be dropped.";

    /// <summary>
    /// Positive liveness probe. At least one of these must resolve in <c>PrefabLookupMap</c> before
    /// a single miss is believed. <c>-257494203</c> is <c>Item_Ingredient_Crystal</c> (the Sample's
    /// own clone source, <c>samples/Progenitor.Sample/Plugin.cs</c>); the other two are the vanilla
    /// equip-buff bases read out of the test server's own save in <c>save-safety.md</c> §1.8. If a
    /// game update retires all three the probe fails and the guard refuses to drop — the fail-safe
    /// direction.
    /// </summary>
    public static readonly int[] KnownVanillaGuids = { -257494203, 1872694456, 1971020070 };

    /// <summary>Bound on the <c>Parent</c> chain walk. A prefab hierarchy is a handful deep.</summary>
    public const int MaxParentChainDepth = 64;

    public static double ClampMaxDropPercent(double value) =>
        double.IsNaN(value) ? 0d : value < 0d ? 0d : value > 100d ? 100d : value;

    public static int ClampMaxDropEntities(int value) => value < 0 ? 0 : value;

    /// <summary>Never below 1: an empty lookup must always refuse.</summary>
    public static int ClampMinPrefabLookupCount(int value) => value < 1 ? 1 : value;

    /// <summary>What the load guard does when it meets an unresolvable root entity.</summary>
    public enum Mode
    {
        /// <summary>Guard off: vanilla behavior, and no scan at all.</summary>
        Off = 0,

        /// <summary>Remove from <c>DeserializedArchetype.NewEntities</c> and destroy. Default.</summary>
        Drop = 1,

        /// <summary>Scan and report only; the vanilla load then aborts.</summary>
        FailLoad = 2,
    }

    /// <summary>
    /// <see cref="FailLoadInsteadKey"/> wins: an operator who asked for the vanilla abort gets it
    /// even if <see cref="DropUnknownPrefabsKey"/> is left at its default true.
    /// </summary>
    public static Mode ResolveMode(bool dropUnknownPrefabs, bool failLoadInstead)
    {
        if (failLoadInstead) return Mode.FailLoad;
        return dropUnknownPrefabs ? Mode.Drop : Mode.Off;
    }

    /// <summary>True when the prefix should walk the archetypes at all.</summary>
    public static bool ModeScans(Mode mode) => mode != Mode.Off;

    /// <summary>True when the prefix may remove and destroy.</summary>
    public static bool ModeDrops(Mode mode) => mode == Mode.Drop;

    /// <summary>
    /// The loader's own predicate: only a <i>root</i> instance (no <c>Parent</c>) is looked up, and
    /// only a miss in the prefab lookup is a problem. A child entity is skipped by the game too, so
    /// the guard must skip it as well — dropping one would remove a resolvable prefab's linked part.
    /// </summary>
    public static bool IsUnknownRoot(bool hasParent, bool resolvedInPrefabMap) =>
        !hasParent && !resolvedInPrefabMap;

    /// <summary>Full decision for one deserialized entity.</summary>
    public static bool ShouldDrop(Mode mode, bool hasParent, bool resolvedInPrefabMap) =>
        ModeDrops(mode) && IsUnknownRoot(hasParent, resolvedInPrefabMap);

    /// <summary>
    /// True when the entity is left exactly as the game deserialized it. Every entity the guard
    /// does not drop is kept — the guard never rewrites component data.
    /// </summary>
    public static bool ShouldKeep(Mode mode, bool hasParent, bool resolvedInPrefabMap) =>
        !ShouldDrop(mode, hasParent, resolvedInPrefabMap);

    // ---------------------------------------------------------------- blast radius (FSAVE2)

    /// <summary>Why the guard refused to drop anything this load. <see cref="None"/> means proceed.</summary>
    public enum Refusal
    {
        None = 0,

        /// <summary><c>PrefabLookupMap.Count()</c> is zero: nothing is registered at all.</summary>
        LookupEmpty = 1,

        /// <summary>The lookup holds fewer than <see cref="MinPrefabLookupCountKey"/> prefabs.</summary>
        LookupTooSmall = 2,

        /// <summary>No <see cref="KnownVanillaGuids"/> entry resolved, so the map is not trustworthy.</summary>
        LookupProbeFailed = 3,

        /// <summary>Over <see cref="MaxDropEntitiesKey"/>.</summary>
        TooManyEntities = 4,

        /// <summary>Over <see cref="MaxDropPercentKey"/> of what was scanned.</summary>
        RatioTooHigh = 5,
    }

    /// <summary>
    /// Fail-safe by construction (R29 finding 1). Vanilla's behavior for an unresolvable prefab is
    /// to <b>throw</b> — the load fails, the operator notices, and the save on disk is untouched
    /// until the next autosave. That is a fail-safe outcome, and the guard is only allowed to
    /// replace it when the evidence says "a mod is missing" rather than "the prefab collection is
    /// not built". Everything below is a reason to keep the vanilla failure.
    /// </summary>
    /// <param name="lookupCount">
    /// <c>PrefabLookupMap.Count()</c> at the moment of the scan — the same number the game itself
    /// prints as <c>Amount of prefabs registered</c>.
    /// </param>
    /// <param name="vanillaProbeResolved">
    /// True when at least one <see cref="KnownVanillaGuids"/> entry resolved in the same map.
    /// </param>
    /// <param name="dropEntities">Roots <b>plus</b> the children collected with them.</param>
    /// <param name="scanned">Entities the guard looked at (those carrying a <c>PrefabGUID</c>).</param>
    public static Refusal EvaluateBlastRadius(
        int lookupCount,
        int minLookupCount,
        bool vanillaProbeResolved,
        int dropEntities,
        int scanned,
        int maxDropEntities,
        double maxDropPercent)
    {
        if (dropEntities <= 0) return Refusal.None;

        if (lookupCount <= 0) return Refusal.LookupEmpty;
        if (lookupCount < ClampMinPrefabLookupCount(minLookupCount)) return Refusal.LookupTooSmall;
        if (!vanillaProbeResolved) return Refusal.LookupProbeFailed;

        if (dropEntities > ClampMaxDropEntities(maxDropEntities)) return Refusal.TooManyEntities;

        // Dropping without having scanned anything cannot be justified by a ratio, so refuse.
        if (scanned <= 0) return Refusal.RatioTooHigh;
        if (dropEntities * 100d > scanned * ClampMaxDropPercent(maxDropPercent)) return Refusal.RatioTooHigh;

        return Refusal.None;
    }

    /// <summary>True when the guard may go ahead and mutate.</summary>
    public static bool MayDrop(Refusal refusal) => refusal == Refusal.None;

    // ---------------------------------------------------------------- order-preserving compaction

    /// <summary>
    /// Order-preserving in-place compaction driven by a single <paramref name="move"/> callback, so
    /// two parallel arrays are always moved in lockstep and can never drift apart. Returns the new
    /// length; the caller writes it into every array it moved.
    ///
    /// <para><b>Why not <c>RemoveAtSwapBack</c>.</b>
    /// <c>SetupOldToNewEntityRemappingHashMap</c> pairs
    /// <c>OldEntities[i]</c> with <c>NewEntities[i]</c> by ordinal in its native body.
    /// It runs at <c>OnUpdate</c> step 10, six steps <i>before</i>
    /// the guard's seam at step 16, so a reorder here cannot rebind anything on
    /// this build. Compacting in order costs the same and removes the whole class of concern
    /// (R29 finding 2).</para>
    /// </summary>
    public static int CompactInOrder(int length, Func<int, bool> removeAt, Action<int, int> move)
    {
        if (removeAt == null) throw new ArgumentNullException(nameof(removeAt));
        if (move == null) throw new ArgumentNullException(nameof(move));
        if (length <= 0) return length < 0 ? 0 : length;

        int write = 0;
        for (int read = 0; read < length; read++)
        {
            if (removeAt(read)) continue;
            if (write != read) move(read, write);
            write++;
        }
        return write;
    }

    // ---------------------------------------------------------------- doomed children

    /// <summary>Reads an entity's parent. False when it has none (i.e. it is a root).</summary>
    public delegate bool ParentOf<T>(T child, out T parent);

    /// <summary>
    /// R29 finding 9. Every entity whose <c>Parent</c> chain reaches an already-doomed root joins
    /// the doomed set, together with the intermediate links of that chain. Cycle-safe and bounded
    /// by <paramref name="maxDepth"/>. Returns how many entries were added.
    ///
    /// <para>A child of a <i>resolvable</i> root is still left alone — the loader skips it
    /// and dropping it would delete part of a prefab that loads fine. What
    /// changes here is that a child of a root the guard is about to destroy is no longer left
    /// behind pointing at a dead parent, and no longer invisible in the counts.</para>
    /// </summary>
    public static int CollectDoomedDescendants<T>(
        IReadOnlyList<T> candidates,
        ParentOf<T> parentOf,
        ISet<T> doomed,
        int maxDepth)
    {
        if (candidates == null) throw new ArgumentNullException(nameof(candidates));
        if (parentOf == null) throw new ArgumentNullException(nameof(parentOf));
        if (doomed == null) throw new ArgumentNullException(nameof(doomed));
        if (maxDepth <= 0) return 0;

        int added = 0;
        var chain = new List<T>();
        for (int i = 0; i < candidates.Count; i++)
        {
            T entity = candidates[i];
            if (doomed.Contains(entity)) continue;

            chain.Clear();
            T cursor = entity;
            bool reachesDoomedRoot = false;

            for (int depth = 0; depth < maxDepth; depth++)
            {
                if (!parentOf(cursor, out T parent)) break;          // a root: not doomed by descent
                if (doomed.Contains(parent)) { reachesDoomedRoot = true; break; }
                if (chain.Contains(parent)) break;                    // cycle: refuse to loop
                chain.Add(parent);
                cursor = parent;
            }

            if (!reachesDoomedRoot) continue;

            if (doomed.Add(entity)) added++;
            for (int c = 0; c < chain.Count; c++)
            {
                if (doomed.Add(chain[c])) added++;
            }
        }
        return added;
    }

    // ---------------------------------------------------------------- references to a dropped entity (M2.11 crash)

    /// <summary>
    /// <c>ProjectM.BuffBuffer</c> element stride: <c>{ Entity Entity @0x0; PrefabGUID PrefabGuid @0x8; }</c>
    /// (server metadata, <c>[InternalBufferCapacity(1)]</c>, no
    /// <c>[PersistenceV2.ExcludeComponent]</c>, so it is saved). Mirrors
    /// <c>Layout.BuffBufferStride</c>; Bootstrap must not compile-reference Core, and a test pins the
    /// two together.
    /// </summary>
    public const int BuffRowStride = 12;

    /// <summary><c>BuffBuffer.Entity</c> offset. Mirrors <c>Layout.BuffBufferEntityOffset</c>.</summary>
    public const int BuffRowEntityOffset = 0;

    /// <summary><c>BuffBuffer.PrefabGuid</c> offset. Mirrors <c>Layout.BuffBufferPrefabGuidOffset</c>.</summary>
    public const int BuffRowPrefabGuidOffset = 8;

    /// <summary>Sanity bound on one entity's <c>BuffBuffer</c>. A character carries a few dozen.</summary>
    public const int MaxPlausibleBuffRows = 4096;

    /// <summary>
    /// Interval between checks that every retired entity is really gone. The vanilla destroy path
    /// takes a couple of frames; the check is wall-clock, so a survivor gets several intervals
    /// before it is destroyed directly (<see cref="RetireCheckAction"/>).
    /// </summary>
    public const double RetireVerifySeconds = 10d;

    /// <summary>Checks before a survivor is destroyed directly regardless (about a minute).</summary>
    public const int RetireVerifyMaxAttempts = 6;

    /// <summary>What one delayed check does with one retired entity.</summary>
    public enum RetireCheck
    {
        /// <summary>The game destroyed it.</summary>
        Gone = 0,

        /// <summary>Still present: check again later.</summary>
        Wait = 1,

        /// <summary>Destroy it directly: the game had its chance.</summary>
        Force = 2,
    }

    /// <summary>
    /// A survivor is only destroyed directly on the last attempt. Destroying it before
    /// <c>ModificationCleanupSystem</c> has seen its <c>DestroyTag</c> would recreate the orphaned
    /// registry modifications the retire path exists to avoid, and a slow host or a long hitch
    /// after the load can delay the game's destroy systems past one interval.
    /// </summary>
    public static RetireCheck RetireCheckAction(bool exists, int attempt, int maxAttempts)
    {
        if (!exists) return RetireCheck.Gone;
        return attempt >= maxAttempts ? RetireCheck.Force : RetireCheck.Wait;
    }

    /// <summary>What the postfix does with one row of a deserialized entity's <c>BuffBuffer</c>.</summary>
    public enum BuffRowVerdict
    {
        /// <summary>A live buff instance. Never touched.</summary>
        Keep = 0,

        /// <summary>The row names an entity this load is dropping.</summary>
        DroppedTarget = 1,

        /// <summary>
        /// The row names an entity that does not exist — a save written after an earlier drop left
        /// the row behind (the fscope1-serverB run-2 crash).
        /// </summary>
        Dangling = 2,

        /// <summary>
        /// The row names a live entity whose <c>PrefabGUID</c> is not the row's. A stale id from an
        /// older world can collide with an unrelated entity in this one; such a row is as dangling
        /// as a missing one, and the buff jobs would act on the wrong entity.
        /// </summary>
        Mismatched = 3,
    }

    /// <summary>
    /// Why this exists (2026-09-28, fscope1-serverB). Destroying a dropped buff with a bare
    /// <c>DestroyEntity</c> leaves the wearer's <c>BuffBuffer</c> row pointing at nothing. The row is
    /// persisted; the wearer is offline (disabled) so nothing reads it until the player connects,
    /// and then a Burst job that walks the buffer — the same shape as
    /// <c>DestroyBuffsWithDeadTargets_Job</c>, which hands every row's entity to
    /// <c>DestroyUtility.Destroy(commandBuffer, …)</c> — throws
    /// <c>The entity does not exist … AppendDestroyedEntityRecordError</c>, and Burst aborts the
    /// server. A row is removed only when it names an entity this load drops, an entity that does
    /// not exist, or a live entity whose prefab guid differs from the one the row records (the game
    /// writes the row from the buff's own guid). A vanilla save has none of the three, so a clean
    /// save is never edited.
    /// </summary>
    public static BuffRowVerdict ClassifyBuffRow(bool targetIsDropped, bool targetExists, bool guidMatches)
    {
        if (targetIsDropped) return BuffRowVerdict.DroppedTarget;
        if (!targetExists) return BuffRowVerdict.Dangling;
        return guidMatches ? BuffRowVerdict.Keep : BuffRowVerdict.Mismatched;
    }

    /// <summary>
    /// The rows to remove from one buffer, <b>descending</b>, so removing them one at a time with
    /// <c>RemoveAt</c> never shifts an index still waiting to be removed.
    /// </summary>
    public static int[] RowsToRemove(IReadOnlyList<BuffRowVerdict> verdicts)
    {
        if (verdicts == null) throw new ArgumentNullException(nameof(verdicts));
        var remove = new List<int>();
        for (int i = verdicts.Count - 1; i >= 0; i--)
        {
            if (verdicts[i] != BuffRowVerdict.Keep) remove.Add(i);
        }
        return remove.ToArray();
    }

    /// <summary>
    /// Counts removed rows per buff guid, so the log names what was cleaned — the guid is what an
    /// operator can match against a missing mod.
    /// </summary>
    public sealed class BuffRowTally
    {
        private readonly Dictionary<int, int> _dropped = new();
        private readonly Dictionary<int, int> _dangling = new();
        private readonly Dictionary<int, int> _mismatched = new();

        public int Holders { get; private set; }
        public int HoldersEdited { get; private set; }
        public int DroppedRows { get; private set; }
        public int DanglingRows { get; private set; }
        public int MismatchedRows { get; private set; }
        public int Failed { get; private set; }

        /// <summary>No row to remove. Says nothing about holders that could not be read.</summary>
        public bool IsEmpty => DroppedRows == 0 && DanglingRows == 0 && MismatchedRows == 0;

        /// <summary>No row to remove and every holder was read; only this may be logged as clean.</summary>
        public bool IsClean => IsEmpty && Failed == 0;

        public void NoteHolder() => Holders++;

        public void NoteEdited() => HoldersEdited++;

        public void NoteFailed() => Failed++;

        public void Add(BuffRowVerdict verdict, int guidHash)
        {
            if (verdict == BuffRowVerdict.DroppedTarget)
            {
                DroppedRows++;
                _dropped.TryGetValue(guidHash, out int n);
                _dropped[guidHash] = n + 1;
            }
            else if (verdict == BuffRowVerdict.Dangling)
            {
                DanglingRows++;
                _dangling.TryGetValue(guidHash, out int n);
                _dangling[guidHash] = n + 1;
            }
            else if (verdict == BuffRowVerdict.Mismatched)
            {
                MismatchedRows++;
                _mismatched.TryGetValue(guidHash, out int n);
                _mismatched[guidHash] = n + 1;
            }
        }

        /// <summary>Ascending guids with counts, e.g. <c>650563057x1</c>.</summary>
        public string DescribeGuids()
        {
            var parts = new List<string>();
            Append(parts, _dropped, "dropped");
            Append(parts, _dangling, "dangling");
            Append(parts, _mismatched, "mismatched");
            return parts.Count == 0 ? "none" : string.Join(", ", parts);
        }

        private static void Append(List<string> parts, Dictionary<int, int> counts, string kind)
        {
            var keys = new int[counts.Count];
            counts.Keys.CopyTo(keys, 0);
            Array.Sort(keys);
            foreach (int key in keys)
                parts.Add(kind + " " + Int(key) + "x" + Int(counts[key]));
        }
    }

    // ---------------------------------------------------------------- log text

    /// <summary>
    /// One line per load, clean or not, so a live run shows the check ran. <paramref name="repaired"/>
    /// is false in <see cref="Mode.FailLoad"/>, where the guard reports and never edits.
    /// </summary>
    public static string FormatBuffRows(BuffRowTally tally, bool repaired, int elapsedMs)
    {
        if (tally == null) throw new ArgumentNullException(nameof(tally));
        if (tally.IsEmpty)
        {
            if (tally.Failed > 0)
            {
                return "SaveSafety: BuffBuffer check: " + Int(tally.Holders)
                    + " deserialized buff holder(s) read and every row names a live entity, but "
                    + Int(tally.Failed) + " holder(s) could not be read and were not checked (" + Int(elapsedMs) + " ms).";
            }
            return "SaveSafety: BuffBuffer check: " + Int(tally.Holders)
                + " deserialized buff holder(s), every row names a live entity (" + Int(elapsedMs) + " ms).";
        }

        string rows = Int(tally.DroppedRows) + " row(s) naming a dropped entity, "
            + Int(tally.DanglingRows) + " row(s) naming no entity and "
            + Int(tally.MismatchedRows) + " row(s) naming an entity of another prefab ("
            + tally.DescribeGuids() + ") on "
            + Int(tally.HoldersEdited) + " of " + Int(tally.Holders) + " buff holder(s)";
        if (!repaired)
        {
            return "SaveSafety: BuffBuffer check found " + rows
                + "; SaveSafety.FailLoadInstead=true, so nothing was removed. A player whose character holds one of these rows will crash the server on connect ('The entity does not exist').";
        }
        return "SaveSafety: BuffBuffer check removed " + rows
            + " (" + Int(elapsedMs) + " ms)"
            + (tally.Failed > 0 ? "; " + Int(tally.Failed) + " holder(s) could not be read or edited and may still carry such a row" : "")
            + ".";
    }

    /// <summary>
    /// The postfix's result. A retired entity is stripped to no components and handed to the game's
    /// own <c>DestroyUtility.Destroy</c>, so <c>ModificationCleanupSystem</c> clears every stat or
    /// flag modification it was the source of before <c>DestroySystem_Server</c> removes it.
    /// </summary>
    public static string FormatRetired(int retired, int planned, int rawDestroyed, int failed) =>
        "SaveSafety: retired " + Int(retired) + " of " + Int(planned)
        + " planned entity(ies) after CopyDataFromPrefabs returned: stripped to no components and handed to DestroyUtility.Destroy, so the game's ModificationCleanupSystem clears the stat/flag modifications they were the source of"
        + (rawDestroyed > 0 ? "; " + Int(rawDestroyed) + " fell back to DestroyEntity (their modifications stay registered)" : "")
        + (failed > 0 ? "; " + Int(failed) + " could not be destroyed and are still in the world" : "")
        + ".";

    /// <summary>The final delayed check that every retired entity is really gone.</summary>
    public static string FormatRetireVerified(int planned, int gone, int forced, int failed, int attempts) =>
        "SaveSafety: " + Int(gone) + " of " + Int(planned) + " retired entity(ies) were destroyed by the game"
        + (forced > 0 ? "; " + Int(forced) + " were still present after " + Int(attempts) + " check(s) "
            + Percent(RetireVerifySeconds) + " s apart and were destroyed directly (their registry modifications may stay behind)" : "")
        + (failed > 0 ? "; " + Int(failed) + " could not be destroyed" : "")
        + ".";

    public static string FormatBound(Mode mode)
    {
        switch (mode)
        {
            case Mode.Drop:
                return "SaveSafety: load guard armed (SaveSafety.DropUnknownPrefabs=true). A save that stores a prefab guid this process cannot resolve will load with those entities dropped instead of failing, but only within SaveSafety.MaxDropPercent / SaveSafety.MaxDropEntities and only when the prefab lookup is populated; over the cap the guard drops nothing and the vanilla load failure happens instead, which never loses data silently.";
            case Mode.FailLoad:
                return "SaveSafety: load guard set to report-only (SaveSafety.FailLoadInstead=true). An unknown prefab guid will be named and the vanilla load will then abort — stop the server before the next autosave.";
            default:
                return "SaveSafety: load guard disabled (SaveSafety.DropUnknownPrefabs=false). An unknown prefab guid aborts the load and the next autosave overwrites the good save.";
        }
    }

    /// <summary>The per-GUID drop line. One per distinct GUID, not one per entity.</summary>
    public static string FormatDropped(int count, int guidHash) =>
        "SaveSafety: dropped " + Int(count) + " entity(ies) with unknown prefab guid " + Int(guidHash)
        + " (mod missing?) so the load can continue";

    /// <summary>The per-GUID line in <see cref="Mode.FailLoad"/>: nothing is removed.</summary>
    public static string FormatWouldDrop(int count, int guidHash) =>
        "SaveSafety: " + Int(count) + " entity(ies) use unknown prefab guid " + Int(guidHash)
        + " (mod missing?); SaveSafety.FailLoadInstead=true, so the vanilla load will abort here";

    /// <summary>
    /// <paramref name="scanned"/> is every deserialized entity that carries a <c>PrefabGUID</c>
    /// (the loader's archetype filter), not only the roots: the <c>Parent</c> test is deferred
    /// until after a lookup miss, so a resolvable entity is never asked whether it is a root.
    /// </summary>
    public static string FormatSummary(int entities, int children, int guids, int scanned, int elapsedMs) =>
        "SaveSafety: load guard removed " + Int(entities) + " entity(ies) ("
        + Int(children) + " of them children of a dropped root) across " + Int(guids)
        + " unknown prefab guid(s) out of " + Int(scanned)
        + " entity(ies) scanned in " + Int(elapsedMs) + " ms; the rest of the save loaded normally.";

    /// <summary>
    /// Logged once the plan is committed to <c>NewEntities</c>/<c>OldEntities</c> and before a
    /// single entity is destroyed. Repeats the mode, because <see cref="FormatBound"/> is printed
    /// at patch time and the mode is re-read live per load (R29 residual 21.7).
    /// </summary>
    public static string FormatPlan(Mode mode, int entities, int children, int guids, int scanned) =>
        "SaveSafety: mode=" + mode + "; removing " + Int(entities) + " entity(ies) ("
        + Int(children) + " child(ren)) across " + Int(guids) + " unknown prefab guid(s) out of "
        + Int(scanned)
        + " scanned from DeserializedArchetype.NewEntities and OldEntities in lockstep, order preserved; they are destroyed after CopyDataFromPrefabs returns.";

    /// <summary>The whole point of FSAVE2: the cap was hit, so nothing is dropped and vanilla runs.</summary>
    public static string FormatRefusedToDrop(
        Refusal reason,
        int drops,
        int scanned,
        int lookupCount,
        int minLookupCount,
        int maxDropEntities,
        double maxDropPercent) =>
        "SaveSafety: refusing to drop " + Int(drops) + " of " + Int(scanned) + " entities ("
        + DescribeRefusal(reason, lookupCount, minLookupCount, maxDropEntities, maxDropPercent)
        + "); falling back to the vanilla load failure";

    public static string DescribeRefusal(
        Refusal reason,
        int lookupCount,
        int minLookupCount,
        int maxDropEntities,
        double maxDropPercent)
    {
        switch (reason)
        {
            case Refusal.LookupEmpty:
                return "PrefabLookupMap.Count()=0, so every miss is the lookup's fault and not the save's";
            case Refusal.LookupTooSmall:
                return "PrefabLookupMap.Count()=" + Int(lookupCount) + ", below the "
                    + ConfigSection + "." + MinPrefabLookupCountKey + "=" + Int(ClampMinPrefabLookupCount(minLookupCount))
                    + " floor — the prefab collection is not built yet";
            case Refusal.LookupProbeFailed:
                return "no known vanilla prefab guid resolved in a lookup of " + Int(lookupCount)
                    + " prefab(s), so the lookup cannot be trusted";
            case Refusal.TooManyEntities:
                return "over " + ConfigSection + "." + MaxDropEntitiesKey + "=" + Int(ClampMaxDropEntities(maxDropEntities));
            case Refusal.RatioTooHigh:
                return "over " + ConfigSection + "." + MaxDropPercentKey + "="
                    + Percent(ClampMaxDropPercent(maxDropPercent)) + "% of the entities scanned";
            default:
                return "no reason";
        }
    }

    /// <summary>A refusal to start: the plan could not be validated, so nothing was touched.</summary>
    public static string FormatAbortedBeforeMutation(string why) =>
        "SaveSafety: aborted before changing anything (" + (why ?? "") + "); falling back to the vanilla load failure";

    /// <summary>A throw partway through the list edit, rolled back. Nothing was destroyed.</summary>
    public static string FormatRestoredAfterThrow(int archetypes, string message) =>
        "SaveSafety: the NewEntities/OldEntities edit threw and was rolled back across " + Int(archetypes)
        + " archetype(s); nothing was destroyed and the load falls back to the vanilla failure: " + (message ?? "");

    /// <summary>
    /// Logged when the prefix committed a plan but the postfix never ran — <c>CopyDataFromPrefabs</c>
    /// threw for an unrelated reason, so Harmony skipped the postfix. Nothing was destroyed; the
    /// load has failed exactly as vanilla.
    /// </summary>
    public static string FormatPlanAbandoned(int planned) =>
        "SaveSafety: a plan for " + Int(planned)
        + " entity(ies) was never applied because CopyDataFromPrefabs did not return normally; nothing was destroyed and the load failed as vanilla.";

    public static string FormatFailLoadSummary(int entities, int guids, int scanned) =>
        "SaveSafety: " + Int(entities) + " entity(ies) across " + Int(guids)
        + " unknown prefab guid(s) out of " + Int(scanned)
        + " entity(ies) scanned. Nothing was dropped (SaveSafety.FailLoadInstead=true); expect 'PersistenceV2 - Load Persistence Failed'.";

    public static string FormatClean(int scanned, int elapsedMs) =>
        "SaveSafety: load guard scanned " + Int(scanned)
        + " entity(ies) with a prefab guid in " + Int(elapsedMs)
        + " ms; every prefab guid resolved, nothing dropped.";

    /// <summary>
    /// The loud one. A drop is permanent the moment the game autosaves, so this names the save and
    /// says what the operator has to do <em>before</em> that happens.
    /// </summary>
    public static string FormatAutosaveWarning(int entities, int guids, string? saveName)
    {
        return "SaveSafety: WARNING — " + DescribeSave(saveName) + " lost " + Int(entities)
            + " entity(ies) across " + Int(guids)
            + " unknown prefab guid(s), and the NEXT AUTOSAVE WILL NOT CONTAIN THEM. To get them back, stop the server before that autosave, restore the save folder from a backup, and put the missing mod back. To accept the loss, do nothing.";
    }

    /// <summary>Same warning shape for the report-only mode: nothing lost yet, but the load failed.</summary>
    public static string FormatFailLoadWarning(int entities, int guids, string? saveName)
    {
        return "SaveSafety: WARNING — " + DescribeSave(saveName) + " references " + Int(entities)
            + " entity(ies) across " + Int(guids)
            + " unknown prefab guid(s) and the load is about to fail. The server will keep running and WILL STILL AUTOSAVE the half-loaded world over the good save. Stop it now.";
    }

    /// <summary>
    /// Diagnostic note for a GUID this process itself cloned (it is in Bootstrap's
    /// <c>ContentMemory</c> clone set) yet could not resolve at load time — that is a Progenitor
    /// bug or a plugin that failed to apply, not a missing mod.
    /// </summary>
    public static string FormatKnownModGuid(int guidHash) =>
        "SaveSafety: prefab guid " + Int(guidHash)
        + " is in this process's Progenitor clone set but was not registered before the load ran; the owning plugin did not apply in time.";

    public static string DescribeSave(string? saveName) =>
        string.IsNullOrWhiteSpace(saveName) ? "this world (save name unknown)" : "save '" + saveName.Trim() + "'";

    public static string FormatNoPrefabCollection() =>
        "SaveSafety: PrefabCollectionSystem / PrefabLookupMap was not readable at CopyDataFromPrefabs; the load guard did nothing this load.";

    public static string FormatLayoutRefused(int newEntities, int entityCount) =>
        "SaveSafety: refusing to edit a DeserializedArchetype whose NewEntities length (" + Int(newEntities)
        + ") does not match EntityCount (" + Int(entityCount) + "); layout changed, leaving the load vanilla.";

    public static string FormatThrew(string message) =>
        "SaveSafety: load guard threw and was skipped for this load: " + (message ?? "");

    /// <summary>
    /// Inventory slots are documented, never touched. <c>InventoryBuffer</c> is
    /// buffer data on the inventory entity, not an entity, so it never reaches the
    /// <c>CopyDataFromPrefabs</c> loop, and <c>RepairBrokenInventoryItemsSystem</c>
    /// jumps to the next slot on an item-lookup miss
    /// in its native body. The slot survives verbatim and comes back intact
    /// when the plugin returns with the same id.
    /// </summary>
    public const string InventorySlotsNote =
        "SaveSafety: inventory slots holding an unknown item guid are kept verbatim by the game (RepairBrokenInventoryItemsSystem skips them) and are not touched by this guard.";

    public static string FormatPins(bool armed, Mode mode, int loads, int droppedEntities, int droppedGuids, string? saveName) =>
        "pin-diag SaveSafety armed=" + (armed ? "True" : "False")
        + " mode=" + mode
        + " loads=" + Int(loads)
        + " droppedEntities=" + Int(droppedEntities)
        + " droppedGuids=" + Int(droppedGuids)
        + " save=" + (string.IsNullOrWhiteSpace(saveName) ? "?" : saveName.Trim());

    private static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Percent(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>
    /// Counts unresolvable root entities per GUID so the guard logs one line per GUID with a count,
    /// in a stable (ascending GUID) order rather than archetype-walk order.
    /// </summary>
    public sealed class UnknownPrefabTally
    {
        private readonly Dictionary<int, int> _counts = new();

        public void Add(int guidHash)
        {
            _counts.TryGetValue(guidHash, out int n);
            _counts[guidHash] = n + 1;
        }

        /// <summary>Distinct unknown GUIDs.</summary>
        public int GuidCount => _counts.Count;

        /// <summary>Total entities behind those GUIDs.</summary>
        public int EntityCount
        {
            get
            {
                int total = 0;
                foreach (var pair in _counts) total += pair.Value;
                return total;
            }
        }

        public bool IsEmpty => _counts.Count == 0;

        public int CountFor(int guidHash) => _counts.TryGetValue(guidHash, out int n) ? n : 0;

        /// <summary>Ascending GUID hashes, so two runs of the same save log in the same order.</summary>
        public int[] Guids()
        {
            var keys = new int[_counts.Count];
            _counts.Keys.CopyTo(keys, 0);
            Array.Sort(keys);
            return keys;
        }

        /// <summary>One line per GUID, ascending, in the mode's wording.</summary>
        public string[] FormatLines(Mode mode)
        {
            var keys = Guids();
            var lines = new string[keys.Length];
            for (int i = 0; i < keys.Length; i++)
            {
                lines[i] = mode == Mode.FailLoad
                    ? FormatWouldDrop(_counts[keys[i]], keys[i])
                    : FormatDropped(_counts[keys[i]], keys[i]);
            }
            return lines;
        }
    }
}
