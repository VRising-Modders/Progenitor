using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using BepInEx.Configuration;
using Il2CppInterop.Runtime;
using ProjectM;
using ProjectM.Shared;
using Stunlock.Core;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Transforms;

namespace Progenitor.Bootstrap;

/// <summary>
/// World-save load guard (jobs FSAVE and FSAVE2; research <c>save-safety.md</c>, job RD3).
///
/// <para>A world save stores a mod prefab GUID exactly like a vanilla one. Loading that save on a
/// server that no longer has the mod reaches
/// <c>ProjectM.LoadPersistenceSystemV2.CopyDataFromPrefabs(DeserializationJobData jobData)</c>.
/// For every <i>root</i> deserialized entity
/// (no <c>Unity.Transforms.Parent</c>) in an archetype that carries
/// <c>PrefabGUID</c> it calls
/// <c>PrefabLookupMap.TryGetConvertedPrefab</c> and, on a miss, logs
/// <c>Failed to load due to missing prefab {0}. Amount of prefabs registered: {1}. EntityInfo:</c>
/// and <b>throws</b>. <c>OnUpdate</c>'s own
/// catch turns that into <c>PersistenceV2 - Load Persistence Failed</c> +
/// <c>ServerStartupState.State.Failed</c>, the world is only
/// half loaded, and <c>SerializePersistenceSystemV2.TriggerSave</c> saves anyway after a warning
/// — the next autosave overwrites the good save.</para>
///
/// <para><b>The vanilla outcome is a failed load, never a silent loss.</b> That makes it fail-safe:
/// the operator sees the failure and the save on disk is intact until the next autosave. This guard
/// is therefore only allowed to replace it when the evidence says "a mod is missing" — never when
/// it says "the prefab collection is not built". Every ceiling in
/// <see cref="SaveSafetyPolicy.EvaluateBlastRadius"/> falls back to the vanilla failure.</para>
///
/// <para><b>Shape of one guarded load (FSAVE2).</b>
/// <list type="number">
/// <item><description><b>Plan</b>, mutating nothing: walk the archetypes, tally the unresolvable
/// roots, expand them through <c>LinkedEntityGroup</c> and the <c>Parent</c> chain, and count what
/// the drop would really cost.</description></item>
/// <item><description><b>Validate</b>: the lookup must be populated and must resolve a known
/// vanilla guid, and the total must be inside <c>MaxDropPercent</c> / <c>MaxDropEntities</c>.
/// Otherwise nothing is touched and the vanilla throw happens.</description></item>
/// <item><description><b>Apply</b> the list edit only: every doomed entity is compacted out of
/// <c>DeserializedArchetype.NewEntities</c> <i>and</i> <c>OldEntities</c> in lockstep, order
/// preserved. Snapshots are taken first, so a throw mid-edit rolls the whole thing back and no
/// entity has been destroyed.</description></item>
/// <item><description><b>Destroy</b> in the <i>postfix</i>, after <c>CopyDataFromPrefabs</c> has
/// returned. The irreversible ECS change is last, and the loader never reads a list this guard is
/// still editing. First every deserialized <c>BuffBuffer</c> row naming a dropped entity (or no
/// entity at all) is removed; then each dropped entity is stripped and handed to
/// <c>DestroyUtility.Destroy</c>, so the game's <c>ModificationCleanupSystem</c> clears the
/// modifications it sourced (M2.11 connect crash, 2026-09-28; research <c>save-safety.md</c> §9).</description></item>
/// </list></para>
///
/// <para><b>Ordering evidence (R29 finding 2).</b> The positional pairing of <c>OldEntities[i]</c>
/// with <c>NewEntities[i]</c> is consumed exactly once, by
/// <c>SetupOldToNewEntityRemappingHashMap</c> (its native body:
/// <c>for (i &lt; archetype.EntityCount) { fromOldToNew[Old[i]] = New[i];
/// fromNewToOld[New[i]] = Old[i]; }</c>). That is <c>OnUpdate</c> step 10, six
/// steps before this guard's seam at step 16. <c>CopyDataFromPrefabs</c> itself
/// only ever reads <c>NewEntities</c> (object offset <c>0x18</c>) and <c>ComponentTypes</c>
/// (<c>0x20</c>) — <c>OldEntities</c> (<c>0x10</c>) never appears in its native code — and it
/// uses the index purely as a cursor. So a reorder at this seam cannot rebind anything on this
/// build; the guard compacts in order anyway, at identical cost, so the question cannot come back
/// if the game moves the remap.</para>
///
/// <para><b>Structural-change timing (R29 finding 18).</b> The loader performs its own structural
/// changes in this very window: <c>ReinstantiateMegaStatics</c> (step 12) calls
/// <c>EntityManager.Instantiate</c> and <c>EntityManager.AddComponent</c>
/// (in the game's native <c>LoadPersistenceSystemV2.ReinstantiateMegaStatics</c>),
/// between the remap setup and this seam. Nothing chunk-derived is held across it.
/// The guard still defers its destroys to the postfix, so the only structural change it makes
/// happens after <c>CopyDataFromPrefabs</c> has finished reading anything.</para>
///
/// <para>Lives in Bootstrap because Bootstrap owns every Harmony site and because the guard has to
/// work in a process where no Core and no plugin is loaded at all.</para>
///
/// <para>Interop rules followed here: no IL2CPP generic over a game value type is instantiated.
/// The <c>NativeList&lt;Entity&gt;</c> / <c>NativeList&lt;ComponentType&gt;</c> payloads are read
/// and edited through the raw Collections 2.x <c>UnsafeList</c> layout
/// (<c>Ptr</c>@0, <c>m_length</c>@8, <c>m_capacity</c>@12); the component reads are
/// <c>GetComponentDataRawRO(entity, TypeIndex)</c> and <c>GetBufferRawRO</c> /
/// <c>GetBufferLength</c>; the type indices come from
/// <c>TypeManager.GetTypeIndex(Il2CppType.Of&lt;T&gt;())</c>. The only game calls are
/// <c>PrefabLookupMap.TryGetConvertedPrefab</c> and <c>PrefabLookupMap.Count()</c>, which is
/// exactly what the loader calls (and what it prints in its own failure line).</para>
/// </summary>
internal static unsafe class SaveSafety
{
    private static bool _dropUnknownPrefabs = SaveSafetyPolicy.DropUnknownPrefabsDefault;
    private static bool _failLoadInstead = SaveSafetyPolicy.FailLoadInsteadDefault;
    private static double _maxDropPercent = SaveSafetyPolicy.MaxDropPercentDefault;
    private static int _maxDropEntities = SaveSafetyPolicy.MaxDropEntitiesDefault;
    private static int _minPrefabLookupCount = SaveSafetyPolicy.MinPrefabLookupCountDefault;
    private static Func<bool>? _dropReader;
    private static Func<bool>? _failReader;
    private static Func<double>? _percentReader;
    private static Func<int>? _maxEntitiesReader;
    private static Func<int>? _minLookupReader;

    private static string? _saveName;
    private static int _armed;
    private static int _loads;
    private static int _droppedEntities;
    private static int _droppedGuids;
    private static int _refusals;

    /// <summary>
    /// The plan the prefix committed to the archetype lists and the postfix has to carry out. One
    /// load at a time, on the game's main thread; the prefix always assigns (including
    /// <c>null</c>), so a plan can never leak into a later load.
    /// </summary>
    private static DropPlan? _pending;

    /// <summary>Bound from Bootstrap's shared BepInEx config file. BCL types only (Core config policy).</summary>
    internal static void BindConfig(ConfigFile config)
    {
        if (config == null) return;

        ConfigEntry<bool> drop = config.Bind(
            SaveSafetyPolicy.ConfigSection,
            SaveSafetyPolicy.DropUnknownPrefabsKey,
            SaveSafetyPolicy.DropUnknownPrefabsDefault,
            SaveSafetyPolicy.DropUnknownPrefabsDescription);
        ConfigEntry<bool> fail = config.Bind(
            SaveSafetyPolicy.ConfigSection,
            SaveSafetyPolicy.FailLoadInsteadKey,
            SaveSafetyPolicy.FailLoadInsteadDefault,
            SaveSafetyPolicy.FailLoadInsteadDescription);
        ConfigEntry<double> percent = config.Bind(
            SaveSafetyPolicy.ConfigSection,
            SaveSafetyPolicy.MaxDropPercentKey,
            SaveSafetyPolicy.MaxDropPercentDefault,
            SaveSafetyPolicy.MaxDropPercentDescription);
        ConfigEntry<int> maxEntities = config.Bind(
            SaveSafetyPolicy.ConfigSection,
            SaveSafetyPolicy.MaxDropEntitiesKey,
            SaveSafetyPolicy.MaxDropEntitiesDefault,
            SaveSafetyPolicy.MaxDropEntitiesDescription);
        ConfigEntry<int> minLookup = config.Bind(
            SaveSafetyPolicy.ConfigSection,
            SaveSafetyPolicy.MinPrefabLookupCountKey,
            SaveSafetyPolicy.MinPrefabLookupCountDefault,
            SaveSafetyPolicy.MinPrefabLookupCountDescription);

        _dropUnknownPrefabs = drop.Value;
        _failLoadInstead = fail.Value;
        _maxDropPercent = percent.Value;
        _maxDropEntities = maxEntities.Value;
        _minPrefabLookupCount = minLookup.Value;

        _dropReader = () => drop.Value;
        _failReader = () => fail.Value;
        _percentReader = () => percent.Value;
        _maxEntitiesReader = () => maxEntities.Value;
        _minLookupReader = () => minLookup.Value;
    }

    /// <summary>
    /// Effective mode, read live through the bound <c>ConfigEntry</c> so a cfg reload takes effect
    /// on the next load without a restart. Falls back to the value bound at startup.
    /// </summary>
    internal static SaveSafetyPolicy.Mode CurrentMode()
    {
        bool drop = _dropUnknownPrefabs;
        bool fail = _failLoadInstead;
        try { if (_dropReader != null) drop = _dropReader(); }
        catch { /* fall back to the last bound value */ }
        try { if (_failReader != null) fail = _failReader(); }
        catch { /* fall back to the last bound value */ }
        return SaveSafetyPolicy.ResolveMode(drop, fail);
    }

    internal static double CurrentMaxDropPercent()
    {
        double value = _maxDropPercent;
        try { if (_percentReader != null) value = _percentReader(); }
        catch { /* fall back */ }
        return SaveSafetyPolicy.ClampMaxDropPercent(value);
    }

    internal static int CurrentMaxDropEntities()
    {
        int value = _maxDropEntities;
        try { if (_maxEntitiesReader != null) value = _maxEntitiesReader(); }
        catch { /* fall back */ }
        return SaveSafetyPolicy.ClampMaxDropEntities(value);
    }

    internal static int CurrentMinPrefabLookupCount()
    {
        int value = _minPrefabLookupCount;
        try { if (_minLookupReader != null) value = _minLookupReader(); }
        catch { /* fall back */ }
        return SaveSafetyPolicy.ClampMinPrefabLookupCount(value);
    }

    /// <summary>
    /// Called once when the Harmony site is installed so the effective mode is in the log before
    /// any save is read. <c>LoadPersistenceSystemV2</c> has no readable save-path field
    /// (its metadata lists only queries and the prefab-collection reference), so the save name
    /// comes from <see cref="NoteSave"/> instead.
    /// </summary>
    internal static void MarkArmed()
    {
        if (Interlocked.Exchange(ref _armed, 1) != 0) return;
        ProcessHost.Log?.LogInfo(SaveSafetyPolicy.FormatBound(CurrentMode()));
    }

    /// <summary>
    /// Records the save this world is about to load, from the
    /// <c>ServerWorldManager.BeginCreateServerWorld</c> postfix Bootstrap already owns. Bootstrap
    /// keeps it for the process so the guard can name the save with no Core loaded.
    /// </summary>
    internal static void NoteSave(string? saveDirectory, string? saveName)
    {
        _ = saveDirectory;
        if (!string.IsNullOrWhiteSpace(saveName))
            _saveName = saveName;
    }

    internal static string DescribePins() =>
        SaveSafetyPolicy.FormatPins(
            Volatile.Read(ref _armed) != 0,
            CurrentMode(),
            Volatile.Read(ref _loads),
            Volatile.Read(ref _droppedEntities),
            Volatile.Read(ref _droppedGuids),
            _saveName)
        + " refusals=" + Volatile.Read(ref _refusals).ToString(System.Globalization.CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------ the two hook bodies

    /// <summary>
    /// The prefix body: plan, validate, and edit the archetype lists. Destroys nothing. Never
    /// throws into the loader — any failure leaves the load vanilla.
    /// </summary>
    internal static void BeforeCopyDataFromPrefabs(LoadPersistenceSystemV2 system, DeserializationJobData jobData)
    {
        // A plan from a previous call that never reached its postfix must not be applied now.
        var abandoned = Interlocked.Exchange(ref _pending, null);
        if (abandoned != null)
            ProcessHost.Log?.LogWarning(SaveSafetyPolicy.FormatPlanAbandoned(abandoned.Doomed.Length));

        var mode = CurrentMode();
        if (!SaveSafetyPolicy.ModeScans(mode)) return;
        if (system == null || jobData == null) return;

        try { _pending = Plan(system, jobData, mode); }
        catch (Exception e)
        {
            _pending = null;
            ProcessHost.Log?.LogError(SaveSafetyPolicy.FormatThrew(e.ToString()));
        }
    }

    /// <summary>
    /// The postfix body: the only irreversible step, and the only structural change. Runs after
    /// <c>CopyDataFromPrefabs</c> has returned normally, so nothing the loader reads is edited
    /// underneath it (R29 findings 17 and 18). If the loader threw instead, Harmony skips this and
    /// nothing is destroyed — the load has already failed exactly as vanilla.
    ///
    /// <para>Order matters. The <c>BuffBuffer</c> repair runs first, while every dropped entity
    /// still exists, so a row naming one is told apart from a row naming nothing. It runs on every
    /// scanning load, not only on loads that drop: a save autosaved after an earlier drop by the
    /// pre-fix guard carries the dangling row with nothing left to drop (fscope1-serverB run 2).
    /// Then the plan's entities are retired.</para>
    /// </summary>
    internal static void AfterCopyDataFromPrefabs(LoadPersistenceSystemV2? system, DeserializationJobData? jobData)
    {
        var plan = Interlocked.Exchange(ref _pending, null);
        var mode = CurrentMode();

        if (SaveSafetyPolicy.ModeScans(mode) && system != null && jobData != null)
        {
            try
            {
                var dropped = new HashSet<Entity>(EntityComparer.Instance);
                if (plan != null)
                {
                    for (int i = 0; i < plan.Doomed.Length; i++) dropped.Add(plan.Doomed[i]);
                }
                RepairBuffRows(system.EntityManager, jobData, dropped, SaveSafetyPolicy.ModeDrops(mode));
            }
            catch (Exception e)
            {
                ProcessHost.Log?.LogError(SaveSafetyPolicy.FormatThrew(e.ToString()));
            }
        }

        if (plan == null) return;

        try { Retire(plan); }
        catch (Exception e)
        {
            ProcessHost.Log?.LogError(SaveSafetyPolicy.FormatThrew(e.ToString()));
        }
    }

    // ------------------------------------------------------------------ plan

    private sealed class DropPlan
    {
        internal World? World;
        internal EntityManager Manager;
        internal Entity[] Doomed = Array.Empty<Entity>();
        internal int Roots;
        internal int Children;
        internal int Guids;
        internal int Scanned;
        internal int ElapsedMs;
    }

    /// <summary>One <c>DeserializedArchetype</c>, its validated raw list layout, and its role.</summary>
    private sealed class ArchetypeRow
    {
        internal DeserializationJobData.DeserializedArchetype Archetype = null!;

        /// <summary>
        /// The <c>UnsafeList.Ptr</c> payloads, read once. Held as <see cref="IntPtr"/> so the
        /// compaction callback can capture them (a lambda cannot close over a pointer local).
        /// </summary>
        internal IntPtr News;

        internal IntPtr Olds;
        internal int Length;
        internal bool Paired;
        internal bool HasPrefabGuid;
    }

    /// <summary>
    /// Explicit <see cref="Entity"/> equality for the plan's sets and maps. The interop struct is
    /// <c>{ int Index; int Version; }</c>; hashing it by hand keeps the plan off
    /// <c>ValueType.GetHashCode</c> and off any equality the interop generator may or may not have
    /// emitted.
    /// </summary>
    private sealed class EntityComparer : IEqualityComparer<Entity>
    {
        internal static readonly EntityComparer Instance = new();

        public bool Equals(Entity a, Entity b) => a.Index == b.Index && a.Version == b.Version;

        public int GetHashCode(Entity e) => (e.Index * 397) ^ e.Version;
    }

    private static DropPlan? Plan(LoadPersistenceSystemV2 system, DeserializationJobData jobData, SaveSafetyPolicy.Mode mode)
    {
        var world = system.World;
        if (world == null || !world.IsCreated) return null;

        var prefabs = world.GetExistingSystemManaged<PrefabCollectionSystem>();
        if (prefabs == null)
        {
            ProcessHost.Log?.LogWarning(SaveSafetyPolicy.FormatNoPrefabCollection());
            return null;
        }

        var lookup = prefabs._PrefabLookupMap;
        if (!lookup.IsCreated)
        {
            ProcessHost.Log?.LogWarning(SaveSafetyPolicy.FormatNoPrefabCollection());
            return null;
        }

        var archetypes = jobData.Archetypes;
        if (archetypes == null) return null;

        var em = system.EntityManager;
        var prefabGuidIndex = TypeManager.GetTypeIndex(Il2CppType.Of<PrefabGUID>());
        var parentIndex = TypeManager.GetTypeIndex(Il2CppType.Of<Parent>());
        var linkedIndex = TypeManager.GetTypeIndex(Il2CppType.Of<LinkedEntityGroup>());
        var parentType = new ComponentType(Il2CppType.Of<Parent>(), ComponentType.AccessMode.ReadOnly);
        var linkedType = new ComponentType(Il2CppType.Of<LinkedEntityGroup>(), ComponentType.AccessMode.ReadOnly);

        Interlocked.Increment(ref _loads);
        var started = Stopwatch.StartNew();

        // ---- pass 0: every archetype, its layout, and where each deserialized entity lives.
        //
        // A layout that cannot be validated aborts the whole load's plan here, before any scanning
        // and long before any mutation: an archetype whose length disagrees with the game's own
        // EntityCount means the Collections layout moved, and reading `length` entities off `Ptr`
        // would then walk memory that is not the list. Fail closed, and let the vanilla load fail.
        var rows = new List<ArchetypeRow>();
        var rowOf = new Dictionary<Entity, int>(EntityComparer.Instance);
        var everyEntity = new List<Entity>();

        int archetypeCount = archetypes.Count;
        for (int a = 0; a < archetypeCount; a++)
        {
            var archetype = archetypes[a];
            if (archetype == null) continue;

            bool valid = TryReadLists(archetype, out Entity* news, out Entity* olds, out int length, out bool paired);
            if (news == null || length == 0) continue;   // an empty archetype: nothing to plan.
            if (!valid)                                  // includes a negative length
            {
                ProcessHost.Log?.LogError(SaveSafetyPolicy.FormatAbortedBeforeMutation(
                    "a DeserializedArchetype's NewEntities/OldEntities layout could not be validated"));
                return null;
            }

            var row = new ArchetypeRow
            {
                Archetype = archetype,
                News = (IntPtr)news,
                Olds = paired && olds != null ? (IntPtr)olds : IntPtr.Zero,
                Length = length,
                Paired = paired && olds != null,
                HasPrefabGuid = ArchetypeHasType(archetype.ComponentTypes, prefabGuidIndex),
            };
            int rowIndex = rows.Count;
            rows.Add(row);

            for (int i = 0; i < length; i++)
            {
                Entity entity = news[i];
                everyEntity.Add(entity);
                rowOf[entity] = rowIndex;
            }
        }

        // ---- pass 1: the loader's own predicate, over the archetypes it would look at.
        var tally = new SaveSafetyPolicy.UnknownPrefabTally();
        var doomed = new HashSet<Entity>(EntityComparer.Instance);
        int scanned = 0;

        for (int r = 0; r < rows.Count; r++)
        {
            var row = rows[r];
            if (!row.HasPrefabGuid) continue;
            var news = (Entity*)row.News;
            if (news == null) continue;

            for (int i = 0; i < row.Length; i++)
            {
                Entity entity = news[i];
                if (!em.Exists(entity)) continue;

                void* raw;
                try { raw = em.GetComponentDataRawRO(entity, prefabGuidIndex); }
                catch { continue; }
                if (raw == null) continue;

                scanned++;

                int guidHash = *(int*)raw;
                var guid = new PrefabGUID(guidHash);

                // The loader's own call. ErrorFeedbackType.None because this guard logs its own
                // line and the game's LogMissingPrefab would only duplicate it.
                bool resolved;
                try
                {
                    resolved = lookup.TryGetConvertedPrefab(
                        guid, PrefabLookupMap.ErrorFeedbackType.None, false, out _);
                }
                catch
                {
                    // A lookup that threw is not proof the prefab is missing; leave the entity alone.
                    continue;
                }

                // Resolvable is the overwhelming majority, so the Parent test — the second interop
                // call per entity — is deferred until after the miss. The loader tests Parent first
                // purely to skip work; the decision is identical either way.
                if (resolved) continue;

                bool hasParent;
                try { hasParent = em.HasComponent(entity, parentType); }
                catch { continue; }

                if (!SaveSafetyPolicy.IsUnknownRoot(hasParent, resolved)) continue;

                tally.Add(guidHash);
                doomed.Add(entity);
            }
        }

        if (tally.IsEmpty)
        {
            ProcessHost.Log?.LogInfo(SaveSafetyPolicy.FormatClean(scanned, (int)started.ElapsedMilliseconds));
            return null;
        }

        int roots = doomed.Count;

        // ---- pass 2: children. A root's LinkedEntityGroup first (DestroyEntity takes those with
        // it anyway, so they have to leave NewEntities too), then every entity whose Parent chain
        // reaches something already doomed (R29 finding 9).
        ExpandLinkedGroups(em, doomed, rowOf, linkedType, linkedIndex);

        SaveSafetyPolicy.CollectDoomedDescendants(
            everyEntity,
            (Entity child, out Entity parent) => TryReadParent(em, child, parentType, parentIndex, out parent),
            doomed,
            SaveSafetyPolicy.MaxParentChainDepth);

        int total = doomed.Count;
        int children = total - roots;

        // ---- validate: is the lookup itself trustworthy, and is the blast radius sane?
        int lookupCount = ReadLookupCount(lookup);
        bool probeResolved = ProbeKnownVanillaGuids(lookup);
        int maxDropEntities = CurrentMaxDropEntities();
        double maxDropPercent = CurrentMaxDropPercent();
        int minLookupCount = CurrentMinPrefabLookupCount();

        // In FailLoad the operator has already chosen the vanilla abort, so there is nothing to
        // refuse and no refusal line to print.
        var refusal = mode == SaveSafetyPolicy.Mode.Drop
            ? SaveSafetyPolicy.EvaluateBlastRadius(
                lookupCount, minLookupCount, probeResolved, total, scanned, maxDropEntities, maxDropPercent)
            : SaveSafetyPolicy.Refusal.None;

        bool willDrop = mode == SaveSafetyPolicy.Mode.Drop && SaveSafetyPolicy.MayDrop(refusal);
        var reportedMode = willDrop ? SaveSafetyPolicy.Mode.Drop : SaveSafetyPolicy.Mode.FailLoad;

        var log = ProcessHost.Log;
        foreach (var line in tally.FormatLines(reportedMode))
            log?.LogWarning(line);
        foreach (int guidHash in tally.Guids())
        {
            if (WasClonedByThisProcess(guidHash))
                log?.LogWarning(SaveSafetyPolicy.FormatKnownModGuid(guidHash));
        }

        if (!SaveSafetyPolicy.MayDrop(refusal))
        {
            Interlocked.Increment(ref _refusals);
            log?.LogError(SaveSafetyPolicy.FormatRefusedToDrop(
                refusal, total, scanned, lookupCount, minLookupCount, maxDropEntities, maxDropPercent));
            log?.LogWarning(SaveSafetyPolicy.FormatFailLoadSummary(total, tally.GuidCount, scanned));
            log?.LogError(SaveSafetyPolicy.FormatFailLoadWarning(total, tally.GuidCount, _saveName));
            return null;
        }

        if (!willDrop)
        {
            log?.LogWarning(SaveSafetyPolicy.FormatFailLoadSummary(total, tally.GuidCount, scanned));
            log?.LogError(SaveSafetyPolicy.FormatFailLoadWarning(total, tally.GuidCount, _saveName));
            return null;
        }

        // ---- validate the edit itself before touching a single byte (R29 finding 17).
        // Every archetype in `rows` passed validation in pass 0, so a doomed entity either has a
        // row to be compacted out of or was never in a NewEntities at all (a LinkedEntityGroup
        // member the loader does not list), in which case there is nothing to edit and it is simply
        // destroyed with its root.
        var affectedRows = new HashSet<int>();
        foreach (Entity entity in doomed)
        {
            if (rowOf.TryGetValue(entity, out int rowIndex))
                affectedRows.Add(rowIndex);
        }

        // ---- apply: compaction only, with a rollback snapshot per edited archetype.
        var edited = new List<(ArchetypeRow Row, Entity[] News, Entity[] Olds)>();
        try
        {
            foreach (int rowIndex in affectedRows)
            {
                var row = rows[rowIndex];
                var news = (Entity*)row.News;
                var olds = (Entity*)row.Olds;
                int length = row.Length;
                if (news == null || CurrentLength(row.Archetype) != length)
                    throw new InvalidOperationException("DeserializedArchetype length changed between plan and apply.");

                var newsSnapshot = new Entity[length];
                for (int i = 0; i < length; i++) newsSnapshot[i] = news[i];
                Entity[] oldsSnapshot = Array.Empty<Entity>();
                if (row.Paired && olds != null)
                {
                    oldsSnapshot = new Entity[length];
                    for (int i = 0; i < length; i++) oldsSnapshot[i] = olds[i];
                }
                edited.Add((row, newsSnapshot, oldsSnapshot));

                var remove = new bool[length];
                for (int i = 0; i < length; i++) remove[i] = doomed.Contains(news[i]);

                IntPtr newsHandle = row.News;
                IntPtr oldsHandle = row.Paired ? row.Olds : IntPtr.Zero;

                int newLength = SaveSafetyPolicy.CompactInOrder(
                    length,
                    i => remove[i],
                    (from, to) =>
                    {
                        var n = (Entity*)newsHandle;
                        n[to] = n[from];
                        if (oldsHandle != IntPtr.Zero)
                        {
                            var o = (Entity*)oldsHandle;
                            o[to] = o[from];
                        }
                    });

                WriteLengths(row.Archetype, newLength, row.Paired);
            }
        }
        catch (Exception e)
        {
            int restored = Restore(edited);
            ProcessHost.Log?.LogError(SaveSafetyPolicy.FormatRestoredAfterThrow(restored, e.Message));
            return null;
        }

        log?.LogWarning(SaveSafetyPolicy.FormatPlan(mode, total, children, tally.GuidCount, scanned));

        var order = new Entity[doomed.Count];
        doomed.CopyTo(order);
        return new DropPlan
        {
            World = world,
            Manager = em,
            Doomed = order,
            Roots = roots,
            Children = children,
            Guids = tally.GuidCount,
            Scanned = scanned,
            ElapsedMs = (int)started.ElapsedMilliseconds,
        };
    }

    /// <summary>
    /// Puts every edited archetype back exactly as it was — contents first, then the length — so a
    /// throw partway through the sweep can never leave a half-applied edit behind (R29 finding 17).
    /// Returns how many were restored.
    /// </summary>
    private static int Restore(List<(ArchetypeRow Row, Entity[] News, Entity[] Olds)> edited)
    {
        int restored = 0;
        for (int e = 0; e < edited.Count; e++)
        {
            var entry = edited[e];
            try
            {
                var news = (Entity*)entry.Row.News;
                var olds = (Entity*)entry.Row.Olds;
                if (news == null) continue;
                for (int i = 0; i < entry.News.Length; i++) news[i] = entry.News[i];
                bool paired = olds != null && entry.Olds.Length == entry.Row.Length;
                if (paired)
                {
                    for (int i = 0; i < entry.Olds.Length; i++) olds[i] = entry.Olds[i];
                }
                WriteLengths(entry.Row.Archetype, entry.Row.Length, paired);
                restored++;
            }
            catch
            {
                // Nothing better is available; the load will fail as vanilla either way.
            }
        }
        return restored;
    }

    /// <summary>The live <c>NewEntities</c> length, with no layout logging.</summary>
    private static int CurrentLength(DeserializationJobData.DeserializedArchetype archetype)
    {
        var data = archetype.NewEntities.m_ListData;
        return data == null ? -1 : data->m_length;
    }

    // ------------------------------------------------------------------ BuffBuffer repair (postfix only)

    /// <summary>
    /// Removes every <c>BuffBuffer</c> row on a deserialized entity that names an entity this load
    /// drops, or names no entity at all (<see cref="SaveSafetyPolicy.ClassifyBuffRow"/>). The row is
    /// the one persisted reference from a wearer to an arbitrary buff instance, and a Burst job that
    /// walks it aborts the server on a missing entity (fscope1-serverB, 2026-09-28).
    ///
    /// <para>Runs after <c>RemapEntities</c> (<c>OnUpdate</c> step 13), so every
    /// row already holds this world's entity id. The read is raw (<c>GetBufferRawRO</c>, stride and
    /// offsets from <see cref="SaveSafetyPolicy.BuffRowStride"/>, pinned to <c>Layout</c> by a test).
    /// The edit uses <c>EntityManager.GetBuffer&lt;BuffBuffer&gt;</c> and
    /// <c>DynamicBuffer&lt;BuffBuffer&gt;.RemoveAt</c> (both listed in the game's metadata), both existing
    /// IL2CPP instantiations; Bootstrap is not collectible, so neither can pin a generation. The
    /// buffer length is re-read through the typed buffer before any removal, and a disagreement
    /// skips that holder rather than removing the wrong row.</para>
    /// </summary>
    private static void RepairBuffRows(
        EntityManager em, DeserializationJobData jobData, HashSet<Entity> dropped, bool repair)
    {
        var archetypes = jobData.Archetypes;
        if (archetypes == null) return;

        var started = Stopwatch.StartNew();
        var buffIndex = TypeManager.GetTypeIndex(Il2CppType.Of<BuffBuffer>());
        var buffType = new ComponentType(Il2CppType.Of<BuffBuffer>(), ComponentType.AccessMode.ReadOnly);
        var prefabGuidIndex = TypeManager.GetTypeIndex(Il2CppType.Of<PrefabGUID>());
        var prefabGuidType = new ComponentType(Il2CppType.Of<PrefabGUID>(), ComponentType.AccessMode.ReadOnly);
        var tally = new SaveSafetyPolicy.BuffRowTally();
        var verdicts = new List<SaveSafetyPolicy.BuffRowVerdict>();
        var guids = new List<int>();

        int archetypeCount = archetypes.Count;
        for (int a = 0; a < archetypeCount; a++)
        {
            var archetype = archetypes[a];
            if (archetype == null) continue;
            if (!ArchetypeHasType(archetype.ComponentTypes, buffIndex)) continue;

            // After the prefix's compaction: the dropped entities are no longer listed, so they
            // are never treated as holders. A layout that does not validate is skipped, not read.
            if (!TryReadLists(archetype, out Entity* news, out _, out int length, out _)) continue;

            for (int i = 0; i < length; i++)
            {
                Entity holder = news[i];
                try
                {
                    if (!em.Exists(holder) || !em.HasComponent(holder, buffType)) continue;
                    byte* raw = (byte*)em.GetBufferRawRO(holder, buffIndex);
                    int rows = em.GetBufferLength(holder, buffIndex);
                    if (raw == null || rows <= 0 || rows > SaveSafetyPolicy.MaxPlausibleBuffRows) continue;

                    tally.NoteHolder();
                    verdicts.Clear();
                    guids.Clear();
                    for (int r = 0; r < rows; r++)
                    {
                        byte* row = raw + r * SaveSafetyPolicy.BuffRowStride;
                        Entity target = *(Entity*)(row + SaveSafetyPolicy.BuffRowEntityOffset);
                        int rowGuid = *(int*)(row + SaveSafetyPolicy.BuffRowPrefabGuidOffset);
                        bool isDropped = dropped.Contains(target);
                        bool exists = !isDropped && target != Entity.Null && em.Exists(target);
                        bool guidMatches = exists && TargetGuidIs(em, target, prefabGuidType, prefabGuidIndex, rowGuid);
                        verdicts.Add(SaveSafetyPolicy.ClassifyBuffRow(isDropped, exists, guidMatches));
                        guids.Add(rowGuid);
                    }

                    int[] remove = SaveSafetyPolicy.RowsToRemove(verdicts);
                    if (remove.Length == 0) continue;

                    for (int k = 0; k < remove.Length; k++)
                        tally.Add(verdicts[remove[k]], guids[remove[k]]);
                    tally.NoteEdited();
                    if (!repair) continue;

                    var buffer = em.GetBuffer<BuffBuffer>(holder);
                    if (buffer.Length != rows)
                    {
                        tally.NoteFailed();
                        continue;
                    }
                    for (int k = 0; k < remove.Length; k++)   // descending, so indices stay valid
                        buffer.RemoveAt(remove[k]);
                }
                catch (Exception e)
                {
                    tally.NoteFailed();
                    ProcessHost.Log?.LogWarning("SaveSafety: BuffBuffer check skipped a holder: " + e.Message);
                }
            }
        }

        string line = SaveSafetyPolicy.FormatBuffRows(tally, repair, (int)started.ElapsedMilliseconds);
        if (tally.IsClean) ProcessHost.Log?.LogInfo(line);
        else if (repair || tally.IsEmpty) ProcessHost.Log?.LogWarning(line);
        else ProcessHost.Log?.LogError(line);
    }

    /// <summary>
    /// The row's guid against the target's own <c>PrefabGUID</c> (a 4-byte int, read raw exactly as
    /// pass 1 reads it). A target without one cannot be a buff instance, so it does not match.
    /// </summary>
    private static bool TargetGuidIs(
        EntityManager em, Entity target, ComponentType prefabGuidType, TypeIndex prefabGuidIndex, int rowGuid)
    {
        if (!em.HasComponent(target, prefabGuidType)) return false;
        void* raw = em.GetComponentDataRawRO(target, prefabGuidIndex);
        return raw != null && *(int*)raw == rowGuid;
    }

    // ------------------------------------------------------------------ retire (postfix only)

    /// <summary>
    /// Destroys the plan's entities the way the game destroys anything, not with a bare
    /// <c>DestroyEntity</c>.
    ///
    /// <para><b>Why.</b> A dropped buff was the <i>source</i> of registered stat and flag
    /// modifications on its wearer (<c>ModificationsRegistry._SourceReferences</c>).
    /// The registry is deserialized at <c>OnUpdate</c> step 18 (<c>DeserializeSystemData</c>), after
    /// this postfix, through the old-to-new map built at step 10 — which still maps the dropped
    /// buff to its new id. Vanilla removes a source's modifications only in
    /// <c>ModificationCleanupSystem</c> (<c>ReactToDestroyTagGroup</c>, <c>OrderLast</c>),
    /// which calls <c>DisposeEntityEntries</c> and
    /// <c>ClearLooseSourceModifications(entity)</c> for every entity carrying
    /// <c>DestroyTag</c>, keyed by the entity id alone. A bare <c>DestroyEntity</c> never reaches it,
    /// so the wearer kept the Sample buff's invulnerable / sun-immune bits for good, and the next
    /// autosave logged <c>Could not remap modification source entity</c> (fscope1-serverB run 2).</para>
    ///
    /// <para><b>How.</b> Each entity is first stripped of every component but
    /// <c>LinkedEntityGroup</c> (<see cref="Strip"/>), so none of the game's buff, sequence or
    /// script destroy systems — which expect the prefab data this entity never received — can match
    /// it; an empty entity with <c>DestroyTag</c> is the same shape as any destroyed event entity.
    /// Then <c>DestroyUtility.Destroy(EntityManager, …)</c> (already called from Bootstrap by
    /// <c>ControlFormService</c>) queues the
    /// vanilla destroy. A delayed check (<see cref="VerifyRetired"/>) destroys a survivor directly
    /// only after <see cref="SaveSafetyPolicy.RetireVerifyMaxAttempts"/> intervals. A throw on either
    /// call falls back to <c>DestroyEntity</c> at once.</para>
    ///
    /// <para>Entities without a <c>LinkedEntityGroup</c> go first. A root's fallback
    /// <c>DestroyEntity</c> takes its group with it, so a member still waiting its turn would vanish
    /// without the vanilla cleanup.</para>
    /// </summary>
    private static void Retire(DropPlan plan)
    {
        var em = plan.Manager;
        var log = ProcessHost.Log;
        var linkedIndex = TypeManager.GetTypeIndex(Il2CppType.Of<LinkedEntityGroup>());
        var linkedType = new ComponentType(Il2CppType.Of<LinkedEntityGroup>(), ComponentType.AccessMode.ReadOnly);

        var members = new List<Entity>();
        var roots = new List<Entity>();
        for (int i = 0; i < plan.Doomed.Length; i++)
        {
            Entity entity = plan.Doomed[i];
            bool hasGroup;
            try
            {
                if (!em.Exists(entity)) continue;
                hasGroup = em.HasComponent(entity, linkedType);
            }
            catch
            {
                continue;
            }
            (hasGroup ? roots : members).Add(entity);
        }

        int retired = 0;
        int raw = 0;
        int failed = 0;
        foreach (var batch in new[] { members, roots })
        {
            foreach (Entity entity in batch)
            {
                try
                {
                    if (!em.Exists(entity)) continue;   // taken by an earlier fallback destroy
                    Strip(em, entity, linkedIndex);
                    DestroyUtility.Destroy(em, entity, DestroyDebugReason.None);
                    retired++;
                }
                catch (Exception e)
                {
                    log?.LogWarning("SaveSafety: retiring a dropped entity through DestroyUtility failed (" + e.Message
                        + "); destroying it directly.");
                    try
                    {
                        if (em.Exists(entity))
                        {
                            em.DestroyEntity(entity);
                            raw++;
                        }
                    }
                    catch (Exception e2)
                    {
                        failed++;
                        log?.LogWarning("SaveSafety: DestroyEntity failed for a dropped entity: " + e2.Message);
                    }
                }
            }
        }

        // Charged from what actually happened, never from the plan (R29 residual 21.6).
        Interlocked.Add(ref _droppedEntities, retired + raw);
        Interlocked.Add(ref _droppedGuids, plan.Guids);

        log?.LogInfo(SaveSafetyPolicy.FormatRetired(retired, plan.Doomed.Length, raw, failed));
        log?.LogWarning(SaveSafetyPolicy.FormatSummary(
            plan.Doomed.Length, plan.Children, plan.Guids, plan.Scanned, plan.ElapsedMs));
        log?.LogWarning(SaveSafetyPolicy.FormatAutosaveWarning(plan.Doomed.Length, plan.Guids, _saveName));
        log?.LogInfo(SaveSafetyPolicy.InventorySlotsNote);

        if (retired > 0)
            ScheduleVerify(plan.World, em, plan.Doomed, planned: plan.Doomed.Length, gone: 0, attempt: 1);
    }

    private static void ScheduleVerify(World? world, EntityManager em, Entity[] pending, int planned, int gone, int attempt) =>
        WorkQueue.PostDelayed(
            SaveSafetyPolicy.RetireVerifySeconds,
            () => VerifyRetired(world, em, pending, planned, gone, attempt));

    /// <summary>
    /// Removes every component except <c>LinkedEntityGroup</c> with the non-generic
    /// <c>RemoveComponent(Entity, ComponentType)</c>, listed by <c>GetComponentTypes</c> (both used
    /// from Core's <c>EntityExtensions</c>). That includes <c>Disabled</c>, which would hide the
    /// entity from the destroy queries. <c>LinkedEntityGroup</c> stays so the final destroy still
    /// takes any unlisted member; the listed ones are retired on their own
    /// (<see cref="ExpandLinkedGroups"/>).
    /// </summary>
    private static void Strip(EntityManager em, Entity entity, TypeIndex keep)
    {
        var types = em.GetComponentTypes(entity, Unity.Collections.Allocator.Temp);
        try
        {
            for (int t = 0; t < types.Length; t++)
            {
                if (types[t].TypeIndex.Value == keep.Value) continue;
                try { em.RemoveComponent(entity, types[t]); }
                catch
                {
                    // A component that will not come off stays; DestroyUtility still queues the
                    // entity, and the verify pass destroys it directly if the game does not.
                }
            }
        }
        finally
        {
            types.Dispose();
        }
    }

    /// <summary>
    /// The delayed half of <see cref="Retire"/>, on the main thread from <see cref="WorkQueue"/>.
    /// The timer is wall-clock and says nothing about whether the game's destroy systems have run,
    /// so a survivor is re-checked (<see cref="SaveSafetyPolicy.RetireCheckAction"/>) and destroyed
    /// directly only on the last attempt; one line is logged when the check ends.
    /// </summary>
    private static void VerifyRetired(World? world, EntityManager em, Entity[] pending, int planned, int gone, int attempt)
    {
        // A world torn down in the meantime (server stopping) has nothing left to check, and its
        // EntityManager must not be touched.
        try { if (world == null || !world.IsCreated) return; }
        catch { return; }

        var waiting = new List<Entity>();
        int forced = 0;
        int failed = 0;
        for (int i = 0; i < pending.Length; i++)
        {
            try
            {
                var action = SaveSafetyPolicy.RetireCheckAction(
                    em.Exists(pending[i]), attempt, SaveSafetyPolicy.RetireVerifyMaxAttempts);
                if (action == SaveSafetyPolicy.RetireCheck.Gone) gone++;
                else if (action == SaveSafetyPolicy.RetireCheck.Wait) waiting.Add(pending[i]);
                else
                {
                    em.DestroyEntity(pending[i]);
                    forced++;
                }
            }
            catch
            {
                failed++;
            }
        }

        if (waiting.Count > 0)
        {
            ScheduleVerify(world, em, waiting.ToArray(), planned, gone, attempt + 1);
            return;
        }

        var line = SaveSafetyPolicy.FormatRetireVerified(planned, gone, forced, failed, attempt);
        if (forced > 0 || failed > 0) ProcessHost.Log?.LogWarning(line);
        else ProcessHost.Log?.LogInfo(line);
    }

    // ------------------------------------------------------------------ raw list access

    /// <summary>
    /// Reads and validates one archetype's raw list layout. <paramref name="paired"/> is true when
    /// <c>OldEntities</c> is present with the same length, so the two must move in lockstep; a
    /// zero-length <c>OldEntities</c> carries no pairing and is left alone. Any other disagreement
    /// is a layout surprise and makes the archetype non-editable (the RawWriteGuard rule).
    /// </summary>
    private static bool TryReadLists(
        DeserializationJobData.DeserializedArchetype archetype,
        out Entity* news,
        out Entity* olds,
        out int length,
        out bool paired)
    {
        news = null;
        olds = null;
        length = 0;
        paired = false;

        var newData = archetype.NewEntities.m_ListData;
        if (newData == null) return false;
        length = newData->m_length;
        news = newData->Ptr;
        if (news == null || length <= 0) return false;
        if (!LengthLooksSane(archetype, length)) return false;

        var oldData = archetype.OldEntities.m_ListData;
        if (oldData == null || oldData->Ptr == null || oldData->m_length == 0) return true;

        if (oldData->m_length != length)
        {
            ProcessHost.Log?.LogError(SaveSafetyPolicy.FormatLayoutRefused(length, oldData->m_length));
            return false;
        }

        olds = oldData->Ptr;
        paired = true;
        return true;
    }

    private static void WriteLengths(DeserializationJobData.DeserializedArchetype archetype, int length, bool paired)
    {
        var newData = archetype.NewEntities.m_ListData;
        if (newData != null) newData->m_length = length;
        if (!paired) return;
        var oldData = archetype.OldEntities.m_ListData;
        if (oldData != null) oldData->m_length = length;
    }

    /// <summary>
    /// Raw scan of <c>DeserializedArchetype.ComponentTypes</c> for a <see cref="TypeIndex"/>. Same
    /// filter the loader applies before it looks at any entity.
    /// </summary>
    private static bool ArchetypeHasType(Unity.Collections.NativeList<ComponentType> types, TypeIndex wanted)
    {
        var data = types.m_ListData;
        if (data == null) return false;
        int length = data->m_length;
        var items = data->Ptr;
        if (items == null || length <= 0 || length > MaxPlausibleLength) return false;
        for (int i = 0; i < length; i++)
        {
            if (items[i].TypeIndex.Value == wanted.Value) return true;
        }
        return false;
    }

    /// <summary>
    /// Cross-check the raw <c>UnsafeList</c> length against the game's own
    /// <c>DeserializedArchetype.EntityCount</c> before trusting the layout.
    /// The native getter just loads the <c>NewEntities</c> list data and returns its length
    /// — the <c>NewEntities</c> field at struct offset <c>0x18</c>, then
    /// <c>UnsafeList.m_length</c> at <c>+8</c> — so on a correct layout the two always agree, and a
    /// disagreement means the Collections layout moved in a game update. Refuse rather than write
    /// into the wrong offset (the RawWriteGuard rule).
    /// </summary>
    private static bool LengthLooksSane(DeserializationJobData.DeserializedArchetype archetype, int length)
    {
        if (length < 0 || length > MaxPlausibleLength)
        {
            ProcessHost.Log?.LogError(SaveSafetyPolicy.FormatLayoutRefused(length, -1));
            return false;
        }

        int entityCount;
        try { entityCount = archetype.EntityCount; }
        catch { return true; }

        if (entityCount == length) return true;
        ProcessHost.Log?.LogError(SaveSafetyPolicy.FormatLayoutRefused(length, entityCount));
        return false;
    }

    // ------------------------------------------------------------------ entity reads

    /// <summary>
    /// <c>Unity.Transforms.Parent</c> is <c>{ Entity Value; }</c>, so the raw
    /// read is one 8-byte <see cref="Entity"/>. <c>HasComponent</c> first, because the majority of
    /// deserialized entities have no parent and an exception per entity would be ruinous.
    /// </summary>
    private static bool TryReadParent(
        EntityManager em, Entity child, ComponentType parentType, TypeIndex parentIndex, out Entity parent)
    {
        parent = Entity.Null;
        try
        {
            if (!em.HasComponent(child, parentType)) return false;
            void* raw = em.GetComponentDataRawRO(child, parentIndex);
            if (raw == null) return false;
            parent = *(Entity*)raw;
        }
        catch
        {
            return false;
        }
        return parent != Entity.Null && parent != child;
    }

    /// <summary>
    /// <c>LinkedEntityGroup</c> is <c>{ Entity Value; }</c>, a buffer element, so
    /// the raw read is an <see cref="Entity"/> array. A listed member joins the plan: it has to
    /// leave <c>NewEntities</c>, and it is retired on its own so the modifications it sourced are
    /// cleared too. An unlisted member is not this load's to strip (it could be a prefab or a
    /// reinstantiated MegaStatic); the root keeps its <c>LinkedEntityGroup</c> through
    /// <see cref="Strip"/>, so the root's own destroy still takes it, as before.
    /// </summary>
    private static void ExpandLinkedGroups(
        EntityManager em,
        HashSet<Entity> doomed,
        Dictionary<Entity, int> rowOf,
        ComponentType linkedType,
        TypeIndex linkedIndex)
    {
        var roots = new List<Entity>(doomed);
        for (int r = 0; r < roots.Count; r++)
        {
            Entity root = roots[r];
            try
            {
                if (!em.Exists(root)) continue;
                if (!em.HasComponent(root, linkedType)) continue;
                void* raw = em.GetBufferRawRO(root, linkedIndex);
                int length = em.GetBufferLength(root, linkedIndex);
                if (raw == null || length <= 0 || length > MaxPlausibleLinkedGroup) continue;
                var members = (Entity*)raw;
                for (int i = 0; i < length; i++)
                {
                    Entity member = members[i];
                    if (member == root || member == Entity.Null) continue;
                    if (rowOf.ContainsKey(member)) doomed.Add(member);
                }
            }
            catch
            {
                // An unreadable linked group is not a reason to abandon the load; the root keeps
                // its LinkedEntityGroup through Strip, so the final destroy still takes the group.
            }
        }
    }

    // ------------------------------------------------------------------ lookup liveness

    /// <summary>
    /// <c>PrefabLookupMap.Count()</c> — the number the game
    /// itself prints as <c>Amount of prefabs registered</c> in the failure this guard replaces.
    /// Returns <c>0</c> when it cannot be read, which refuses every drop.
    /// </summary>
    private static int ReadLookupCount(PrefabLookupMap lookup)
    {
        try { return lookup.Count(); }
        catch { return 0; }
    }

    private static bool ProbeKnownVanillaGuids(PrefabLookupMap lookup)
    {
        var guids = SaveSafetyPolicy.KnownVanillaGuids;
        for (int i = 0; i < guids.Length; i++)
        {
            try
            {
                if (lookup.TryGetConvertedPrefab(
                        new PrefabGUID(guids[i]), PrefabLookupMap.ErrorFeedbackType.None, false, out _))
                    return true;
            }
            catch
            {
                // Try the next candidate.
            }
        }
        return false;
    }

    /// <summary>
    /// The "known mod guids" set, Bootstrap-side and Core-free: <c>ContentMemory</c> already keeps
    /// the GUIDs this process cloned for the process lifetime (it survives a collectible Core swap).
    /// </summary>
    private static bool WasClonedByThisProcess(int guidHash)
    {
        try
        {
            var snapshot = ProcessHost.Content.Take();
            var cloned = snapshot.ClonedGuids;
            if (cloned == null) return false;
            for (int i = 0; i < cloned.Length; i++)
            {
                if (cloned[i] == guidHash) return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Sanity bound on a raw list length. A real save is far below this.</summary>
    private const int MaxPlausibleLength = 50_000_000;

    /// <summary>Sanity bound on one entity's <c>LinkedEntityGroup</c>.</summary>
    private const int MaxPlausibleLinkedGroup = 100_000;
}
