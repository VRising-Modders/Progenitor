using System;
using System.Collections.Generic;
using System.Text;

#if !PROGENITOR_TESTS
using ProjectM;
using Progenitor.Ecs;
using Progenitor.Events;
using Stunlock.Core;
using Unity.Collections;
using Unity.Entities;
using PrefabTag = Unity.Entities.Prefab;
#endif

namespace Progenitor.Content;

/// <summary>
/// Per-owner station-row ledger (design §6.5). Game-free: tests drive the algebra over a
/// list; the live path asks the same table before it writes a workstation buffer. Append only
/// when the GUID is absent; remove the row only when this owner appended it and no other
/// owner still claims it. Vanilla recipes are never written.
/// </summary>
internal sealed class RecipeStationLedger
{
    private readonly List<Entry> _entries = new();

    internal readonly struct Entry
    {
        public Entry(int stationGuidHash, int recipeGuidHash, int ownerId, bool appended)
        {
            StationGuidHash = stationGuidHash;
            RecipeGuidHash = recipeGuidHash;
            OwnerId = ownerId;
            Appended = appended;
        }

        public int StationGuidHash { get; }
        public int RecipeGuidHash { get; }
        public int OwnerId { get; }
        public bool Appended { get; }
    }

    /// <summary>
    /// Decide whether to append. Returns true when the caller must write the row at the tail.
    /// Idempotent: a second claim for the same (station, recipe, owner) writes nothing.
    /// </summary>
    public bool RecordAppend(int stationGuidHash, int recipeGuidHash, int ownerId, bool guidAlreadyPresent)
    {
        for (int i = 0; i < _entries.Count; i++)
        {
            var e = _entries[i];
            if (e.StationGuidHash != stationGuidHash || e.RecipeGuidHash != recipeGuidHash || e.OwnerId != ownerId)
                continue;
            return false;
        }

        bool append = !guidAlreadyPresent;
        _entries.Add(new Entry(stationGuidHash, recipeGuidHash, ownerId, append));
        return append;
    }

    /// <summary>
    /// Drop this owner's claim. Returns true when the caller must remove the matching GUID
    /// from the buffer: this entry had <c>appended = true</c> and no other owner still claims
    /// (station, recipe). Removing an absent claim is a no-op.
    /// </summary>
    public bool RecordRemove(int stationGuidHash, int recipeGuidHash, int ownerId)
    {
        int found = -1;
        bool appended = false;
        for (int i = 0; i < _entries.Count; i++)
        {
            var e = _entries[i];
            if (e.StationGuidHash != stationGuidHash || e.RecipeGuidHash != recipeGuidHash || e.OwnerId != ownerId)
                continue;
            found = i;
            appended = e.Appended;
            break;
        }
        if (found < 0) return false;
        _entries.RemoveAt(found);
        int promote = -1;
        for (int i = 0; i < _entries.Count; i++)
        {
            var e = _entries[i];
            if (e.StationGuidHash != stationGuidHash || e.RecipeGuidHash != recipeGuidHash)
                continue;
            if (promote < 0) promote = i;
        }
        if (promote >= 0)
        {
            if (appended)
            {
                var keep = _entries[promote];
                _entries[promote] = new Entry(keep.StationGuidHash, keep.RecipeGuidHash, keep.OwnerId, appended: true);
            }
            return false;
        }
        return appended;
    }

    public void Clear() => _entries.Clear();

    public int Count => _entries.Count;

    public bool HasClaim(int stationGuidHash, int recipeGuidHash, int ownerId)
    {
        for (int i = 0; i < _entries.Count; i++)
        {
            var e = _entries[i];
            if (e.StationGuidHash == stationGuidHash && e.RecipeGuidHash == recipeGuidHash && e.OwnerId == ownerId)
                return true;
        }
        return false;
    }

    /// <summary>Copy of current rows, for tests of a full unload leaving the list unchanged.</summary>
    public int[] SnapshotGuids(IReadOnlyList<int> buffer)
    {
        var copy = new int[buffer.Count];
        for (int i = 0; i < buffer.Count; i++) copy[i] = buffer[i];
        return copy;
    }

    /// <summary>
    /// Apply the append decision to a game-free station list. Returns the list after the call.
    /// </summary>
    public static List<int> ApplyAppend(List<int> buffer, RecipeStationLedger ledger, int station, int recipe, int owner)
    {
        bool present = Contains(buffer, recipe);
        if (ledger.RecordAppend(station, recipe, owner, present) && !present)
            buffer.Add(recipe);
        return buffer;
    }

    /// <summary>
    /// Apply the remove decision: drop the single matching GUID, re-resolving the index now.
    /// </summary>
    public static List<int> ApplyRemove(List<int> buffer, RecipeStationLedger ledger, int station, int recipe, int owner)
    {
        if (!ledger.RecordRemove(station, recipe, owner)) return buffer;
        for (int i = buffer.Count - 1; i >= 0; i--)
        {
            if (buffer[i] != recipe) continue;
            buffer.RemoveAt(i);
            break;
        }
        return buffer;
    }

    private static bool Contains(List<int> buffer, int recipe)
    {
        for (int i = 0; i < buffer.Count; i++)
            if (buffer[i] == recipe) return true;
        return false;
    }
}

/// <summary>
/// Frame-retry budget for recipe applies that have not succeeded yet. Game-free. A failed
/// apply is expensive (the by-name source lookup scans the prefab name table), so attempts back
/// off to frames 1, 2, 4, 8, … after the definition went pending, and the definition is dropped
/// at <see cref="GiveUpFrames"/>. The owner subscribes <c>Plugin.Frame</c> only while
/// <see cref="Count"/> is non-zero, so an applied or abandoned definition costs nothing per
/// frame. WorldReady and re-registration start a fresh budget.
/// </summary>
internal sealed class RecipeRetrySchedule
{
    public const int GiveUpFrames = 3600;

    private readonly Dictionary<int, int> _frames = new();
    private int[] _scratch = new int[4];

    public int Count => _frames.Count;

    public bool Contains(int guidHash) => _frames.ContainsKey(guidHash);

    /// <summary>Start (or restart) the budget for <paramref name="guidHash"/>.</summary>
    public void Note(int guidHash) => _frames[guidHash] = 0;

    public bool Remove(int guidHash) => _frames.Remove(guidHash);

    public void Clear() => _frames.Clear();

    /// <summary>
    /// Advance every pending definition one frame. Fills <paramref name="attempt"/> with the
    /// hashes to try this frame and <paramref name="gaveUp"/> with those whose budget ran out;
    /// those are already removed.
    /// </summary>
    public void Step(List<int> attempt, List<int> gaveUp)
    {
        attempt.Clear();
        gaveUp.Clear();
        int n = _frames.Count;
        if (n == 0) return;
        if (_scratch.Length < n) _scratch = new int[Math.Max(n, _scratch.Length * 2)];
        _frames.Keys.CopyTo(_scratch, 0);
        for (int i = 0; i < n; i++)
        {
            int hash = _scratch[i];
            int frames = _frames[hash] + 1;
            if (frames >= GiveUpFrames)
            {
                _frames.Remove(hash);
                gaveUp.Add(hash);
                continue;
            }
            _frames[hash] = frames;
            if (IsAttemptFrame(frames)) attempt.Add(hash);
        }
    }

    /// <summary>True on frames 1, 2, 4, 8, … (powers of two).</summary>
    public static bool IsAttemptFrame(int frames) => frames > 0 && (frames & (frames - 1)) == 0;
}

/// <summary>
/// Registers cloned recipes, executes the apply plan on the server (and on a client that
/// registered the same definition), and reverses owned station rows on unregister.
/// Does not go through <see cref="PrefabRegistry"/>: recipes have no ItemKeyLookup row.
/// </summary>
internal static class RecipeApply
{
    private static readonly object Gate = new();
    private static readonly List<RecipeEntry> Entries = new();
    private static readonly RecipeStationLedger Ledger = new();
    private static readonly HashSet<int> ClonedHere = new();
    private static readonly RecipeRetrySchedule Pending = new();
    private static bool _hooks;
#if !PROGENITOR_TESTS
    private static IDisposable? _worldReadySub;
    private static bool _frameHooked;
    private static readonly List<int> AttemptScratch = new();
    private static readonly List<int> GaveUpScratch = new();
#endif

    internal static RecipeStationLedger TestLedger => Ledger;

    /// <summary>
    /// A new client world replaced the one every station claim and recipe clone was recorded in
    /// (FSCOPE1). Registrations stay and re-apply on this world's retry. Returns how many clones
    /// were forgotten. Never for a Core swap of a surviving world.
    /// </summary>
    internal static int ForgetClientWorld()
    {
        lock (Gate)
        {
            int n = ClonedHere.Count;
            Ledger.Clear();
            ClonedHere.Clear();
            return n;
        }
    }

    internal static RecipeRetrySchedule TestPending => Pending;

    /// <summary>
    /// Drop WorldReady / Frame hooks and reverse every remaining registration. Call from
    /// Core uninstall (<c>PrefabRegistry.Uninstall</c>). Without this a Core swap keeps the
    /// previous generation's Frame delegate. Wiring that call is a coordinator item: this
    /// file does not own PrefabRegistry.
    /// </summary>
    internal static void Uninstall()
    {
        RecipeEntry[] snapshot;
        lock (Gate)
        {
            snapshot = Entries.Count == 0 ? Array.Empty<RecipeEntry>() : Entries.ToArray();
            Entries.Clear();
            Pending.Clear();
            _hooks = false;
        }
        for (int i = 0; i < snapshot.Length; i++)
        {
            snapshot[i].Handle.Invalidate();
            TryUnapply(snapshot[i].Definition);
        }
        lock (Gate)
        {
            Ledger.Clear();
            ClonedHere.Clear();
        }
#if !PROGENITOR_TESTS
        try { _worldReadySub?.Dispose(); } catch { }
        _worldReadySub = null;
        lock (Gate)
        {
            try { Plugin.Frame -= OnFrameRetry; } catch { }
            _frameHooked = false;
        }
#endif
    }

    internal static void ResetForTests()
    {
        lock (Gate)
        {
            Entries.Clear();
            Ledger.Clear();
            ClonedHere.Clear();
            Pending.Clear();
        }
    }

    internal static IReadOnlyList<RecipeDefinition> Definitions
    {
        get
        {
            lock (Gate)
            {
                if (Entries.Count == 0) return Array.Empty<RecipeDefinition>();
                var copy = new RecipeDefinition[Entries.Count];
                for (int i = 0; i < Entries.Count; i++)
                    copy[i] = Entries[i].Definition;
                return copy;
            }
        }
    }

    internal static IDisposable Add(RecipeDefinition def)
    {
        var handle = new RecipeRegistrationHandle();
        RecipeEntry? replaced = null;
        lock (Gate)
        {
            EnsureHooksNoLock();
            for (int i = Entries.Count - 1; i >= 0; i--)
            {
                if (Entries[i].Definition.GuidHash != def.GuidHash) continue;
                replaced = Entries[i];
                Entries.RemoveAt(i);
                break;
            }
            Entries.Add(new RecipeEntry(def, handle));
        }

        if (replaced != null)
        {
            replaced.Handle.Invalidate();
            TryUnapply(replaced.Definition);
        }

        TryApplyNow(def);
#if !PROGENITOR_TESTS
        UnlockPrune.NoteRegistered(def);
#endif
        return handle;
    }

    internal static void Unregister(RecipeRegistrationHandle handle)
    {
        RecipeEntry? removed = null;
        lock (Gate)
        {
            for (int i = Entries.Count - 1; i >= 0; i--)
            {
                if (!ReferenceEquals(Entries[i].Handle, handle)) continue;
                removed = Entries[i];
                Entries.RemoveAt(i);
                DropPendingNoLock(removed.Definition.GuidHash);
                break;
            }
        }
        if (removed == null) return;
        removed.Handle.Invalidate();
        TryUnapply(removed.Definition);
#if !PROGENITOR_TESTS
        UnlockPrune.AfterUnregister(new[] { removed.Definition });
#endif
    }

    internal static void UnregisterAll(object owner)
    {
        var batch = new List<RecipeEntry>();
        lock (Gate)
        {
            for (int i = Entries.Count - 1; i >= 0; i--)
            {
                if (!ReferenceEquals(Entries[i].Definition.Owner, owner)) continue;
                batch.Add(Entries[i]);
                DropPendingNoLock(Entries[i].Definition.GuidHash);
                Entries.RemoveAt(i);
            }
        }
        for (int i = 0; i < batch.Count; i++)
        {
            batch[i].Handle.Invalidate();
            TryUnapply(batch[i].Definition);
        }
#if !PROGENITOR_TESTS
        var defs = new RecipeDefinition[batch.Count];
        for (int i = 0; i < batch.Count; i++) defs[i] = batch[i].Definition;
        UnlockPrune.AfterUnregister(defs);
#endif
    }

    /// <summary>True when this process cloned <paramref name="guidHash"/> into the current world.</summary>
    internal static bool IsClonedHere(int guidHash)
    {
        lock (Gate) return ClonedHere.Contains(guidHash);
    }

    internal static bool TryGet(string id, out RecipeDefinition def)
    {
        lock (Gate)
        {
            for (int i = 0; i < Entries.Count; i++)
            {
                if (!string.Equals(Entries[i].Definition.Id, id, StringComparison.Ordinal)) continue;
                def = Entries[i].Definition;
                return true;
            }
        }
        def = null!;
        return false;
    }

    private static void EnsureHooksNoLock()
    {
        if (_hooks) return;
        _hooks = true;
#if !PROGENITOR_TESTS
        try
        {
            _worldReadySub = EventBus.On<WorldReady>(OnWorldReady);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("RecipeApply: hook install threw: " + e);
        }
#endif
    }

    private static void TryApplyNow(RecipeDefinition def)
    {
#if !PROGENITOR_TESTS
        var world = TargetWorld();
        if (world == null || !world.IsCreated) return; // WorldReady retries.
        if (!TryApply(world, def, out var report))
        {
            Plugin.Logger?.LogWarning("RecipeApply: apply '" + def.Id + "' pending: " + report);
            NotePending(def.GuidHash);
        }
        else
        {
            DropPending(def.GuidHash);
            Plugin.Logger?.LogInfo("RecipeApply: applied '" + def.Id + "': " + report);
        }
#endif
    }

    private static void DropPendingNoLock(int guidHash)
    {
        Pending.Remove(guidHash);
#if !PROGENITOR_TESTS
        if (Pending.Count == 0) StopFrameRetryNoLock();
#endif
    }

    private static void TryUnapply(RecipeDefinition def)
    {
#if !PROGENITOR_TESTS
        var world = TargetWorld();
        if (world == null || !world.IsCreated)
        {
            DropLedgerClaims(def);
            return;
        }
        if (!TryUnapply(world, def, out var report))
            Plugin.Logger?.LogWarning("RecipeApply: unapply '" + def.Id + "': " + report);
        else
            Plugin.Logger?.LogInfo("RecipeApply: unapplied '" + def.Id + "': " + report);
#else
        DropLedgerClaims(def);
#endif
    }

    private static void DropLedgerClaims(RecipeDefinition def)
    {
        int ownerId = OwnerId(def.Owner);
        for (int i = 0; i < def.Stations.Count; i++)
        {
            var station = def.Stations[i];
            int stationHash = station.GuidHash;
            Ledger.RecordRemove(stationHash, def.GuidHash, ownerId);
        }
    }

    internal static int OwnerId(object owner) =>
        owner == null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(owner);

#if !PROGENITOR_TESTS
    private static World? TargetWorld() =>
        Plugin.IsServerProcess ? Worlds.Server : Worlds.Client;

    private static void OnWorldReady(WorldReady ev)
    {
        try { RetryPending(ev.World); }
        catch (Exception e) { Plugin.Logger?.LogError("RecipeApply: WorldReady threw: " + e); }
    }

    private static void NotePending(int guidHash)
    {
        lock (Gate)
        {
            Pending.Note(guidHash);
            if (_frameHooked) return;
            Plugin.Frame += OnFrameRetry;
            _frameHooked = true;
        }
    }

    private static void StopFrameRetryNoLock()
    {
        if (!_frameHooked) return;
        try { Plugin.Frame -= OnFrameRetry; } catch { }
        _frameHooked = false;
    }

    private static void DropPending(int guidHash)
    {
        lock (Gate) DropPendingNoLock(guidHash);
    }

    private static RecipeDefinition? FindNoLock(int guidHash)
    {
        for (int i = 0; i < Entries.Count; i++)
            if (Entries[i].Definition.GuidHash == guidHash) return Entries[i].Definition;
        return null;
    }

    /// <summary>
    /// Retries applies that have not succeeded. Subscribed only while <see cref="Pending"/> is
    /// non-empty, and attempts only on the backoff frames <see cref="RecipeRetrySchedule"/>
    /// picks: a failed apply (for example an unresolvable source name) costs milliseconds, so
    /// it must not run every frame, and an applied recipe must cost nothing.
    /// </summary>
    private static void OnFrameRetry()
    {
        try
        {
            var world = TargetWorld();
            if (world == null || !world.IsCreated) return; // The budget waits for a world.
            var retry = new List<RecipeDefinition>();
            var dropped = new List<RecipeDefinition>();
            lock (Gate)
            {
                Pending.Step(AttemptScratch, GaveUpScratch);
                for (int i = 0; i < AttemptScratch.Count; i++)
                {
                    var def = FindNoLock(AttemptScratch[i]);
                    if (def == null) Pending.Remove(AttemptScratch[i]);
                    else retry.Add(def);
                }
                for (int i = 0; i < GaveUpScratch.Count; i++)
                {
                    var def = FindNoLock(GaveUpScratch[i]);
                    if (def != null) dropped.Add(def);
                }
                if (Pending.Count == 0) StopFrameRetryNoLock();
            }

            for (int i = 0; i < retry.Count; i++)
            {
                var def = retry[i];
                if (IsApplied(world, def)) { DropPending(def.GuidHash); continue; }
                if (!TryApply(world, def, out var report)) continue;
                DropPending(def.GuidHash);
                Plugin.Logger?.LogInfo("RecipeApply: applied '" + def.Id + "': " + report);
            }

            for (int i = 0; i < dropped.Count; i++)
                Plugin.Logger?.LogWarning("RecipeApply: stopped retrying apply of '" + dropped[i].Id + "' after " +
                    RecipeRetrySchedule.GiveUpFrames + " frames; the next WorldReady or re-registration will try again.");
        }
        catch (Exception e) { Plugin.Logger?.LogError("RecipeApply: frame retry threw: " + e); }
    }

    private static void RetryPending(World? world)
    {
        if (world == null || !world.IsCreated) return;
        RecipeDefinition[] snapshot;
        lock (Gate)
        {
            if (Entries.Count == 0) return;
            snapshot = new RecipeDefinition[Entries.Count];
            for (int i = 0; i < Entries.Count; i++)
                snapshot[i] = Entries[i].Definition;
        }
        for (int i = 0; i < snapshot.Length; i++)
        {
            var def = snapshot[i];
            if (IsApplied(world, def)) { DropPending(def.GuidHash); continue; }
            if (TryApply(world, def, out var report))
            {
                DropPending(def.GuidHash);
                Plugin.Logger?.LogInfo("RecipeApply: applied '" + def.Id + "': " + report);
            }
            else
                NotePending(def.GuidHash);
        }
    }

    private static bool IsApplied(World world, RecipeDefinition def)
    {
        try
        {
            return PrefabLookup.TryGetEntity(world, new PrefabGUID(def.GuidHash), out _);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Clone the source recipe, patch <c>RecipeData</c> in place through <c>GetComponentDataRawRW</c>
    /// at the dump offsets (no <c>SetComponentData&lt;RecipeData&gt;</c> instantiation exists, and the
    /// typed raw write is refused natively on size),
    /// rewrite requirement / output buffers, insert collection maps and
    /// <c>GameDataSystem.RecipeHashLookupMap</c>, and append the GUID to each workstation's
    /// <c>WorkstationRecipesBuffer</c> when the ledger says this owner is the one that put it there.
    /// </summary>
    internal static unsafe bool TryApply(World world, RecipeDefinition def, out string report)
    {
        var sb = new StringBuilder();
        var work = new ApplyWork();
        try
        {
            if (world == null || !world.IsCreated)
            {
                report = "world missing";
                return false;
            }

            var em = world.EntityManager;
            var prefabs = world.GetExistingSystemManaged<PrefabCollectionSystem>();
            var gameData = world.GetExistingSystemManaged<GameDataSystem>();
            if (prefabs == null || gameData == null)
            {
                report = "PrefabCollectionSystem or GameDataSystem missing";
                return false;
            }

            if (!TryResolveSource(prefabs, def, out var source, out var sourceWhy))
            {
                report = sourceWhy;
                return false;
            }
            def.SourceGuidHash = source.GuidHash;

            var target = new PrefabGUID(def.GuidHash);
            work.Target = target;
            if (target.GuidHash == source.GuidHash)
            {
                report = "refusing to write a vanilla recipe GUID";
                return false;
            }

            if (!prefabs._PrefabGuidToEntityMap.TryGetValue(source, out var sourceEntity) || !em.Exists(sourceEntity))
            {
                report = "source prefab " + source.GuidHash + " not in _PrefabGuidToEntityMap";
                return false;
            }

            var recipeType = ComponentIndex.Of<RecipeData>(ComponentType.AccessMode.ReadOnly);
            if (!em.HasComponent(sourceEntity, recipeType))
            {
                report = "source has no RecipeData (DefineRecipe requires a recipe prefab)";
                return false;
            }

            if (!TryResolveItem(prefabs, def.Output, out var outputGuid, out var outputWhy))
            {
                report = "output: " + outputWhy;
                return false;
            }

            var resolvedInputs = new PrefabGUID[def.Inputs.Count];
            var resolvedAmounts = new int[def.Inputs.Count];
            for (int i = 0; i < def.Inputs.Count; i++)
            {
                if (!TryResolveItem(prefabs, def.Inputs[i], out resolvedInputs[i], out var inWhy))
                {
                    report = "input " + i + ": " + inWhy;
                    return false;
                }
                resolvedAmounts[i] = def.Inputs[i].Amount;
            }

            var resolvedStations = new PrefabGUID[def.Stations.Count];
            for (int i = 0; i < def.Stations.Count; i++)
            {
                if (!TryResolveStation(prefabs, def.Stations[i], out resolvedStations[i], out var stWhy))
                {
                    report = "station " + i + ": " + stWhy;
                    return false;
                }
            }

            bool haveExisting = prefabs._PrefabGuidToEntityMap.TryGetValue(target, out var existing)
                                && em.Exists(existing);
            bool clonedHere;
            lock (Gate) clonedHere = ClonedHere.Contains(def.GuidHash);
            if (haveExisting && !clonedHere)
            {
                report = "existing recipe GUID is vanilla or foreign; apply refused";
                return false;
            }

            var map = gameData.RecipeHashLookupMap;
            if (map.IsCreated && map.Count() >= map.Capacity && !haveExisting)
            {
                report = "RecipeHashLookupMap full (capacity " + map.Capacity + ")";
                return false;
            }

            Entity clone;
            if (haveExisting)
            {
                clone = existing;
                sb.Append("updated existing clone " + clone.Index + ":" + clone.Version + "; ");
            }
            else
            {
                clone = em.Instantiate(sourceEntity);
                work.CreatedClone = true;
                work.Clone = clone;
                var prefabType = ComponentIndex.Of<PrefabTag>(ComponentType.AccessMode.ReadWrite);
                if (!em.HasComponent(clone, prefabType))
                    em.AddComponent(clone, prefabType);
                em.SetComponentData(clone, target);
                lock (Gate) ClonedHere.Add(def.GuidHash);
                work.MarkedClonedHere = true;
                sb.Append("clone " + clone.Index + ":" + clone.Version + "; ");
            }

            // The RecipeData generic setter is absent from the IL2CPP list (the game's metadata
            // has GetComponentData, not Set), and the typed Write<RecipeData> is refused natively:
            // SetComponentDataRaw's SizeInChunk check rejects sizeof 24 (observed live, S3).
            // Patch the clone's chunk memory field by field at the dump offsets instead.
            byte* rd = (byte*)em.GetComponentDataRawRW(clone, ComponentIndex.IndexOf<RecipeData>());
            if (rd == null)
            {
                RollbackApply(world, prefabs, gameData, def, work);
                report = sb + "RecipeData GetComponentDataRawRW returned null";
                return false;
            }
            *(Entity*)(rd + Layout.RecipeDataEntityOffset) = clone;
            *(PrefabGUID*)(rd + Layout.RecipeDataGuidOffset) = target;
            *(float*)(rd + Layout.RecipeDataCraftDurationOffset) = def.CraftDuration;
            *(int*)(rd + Layout.RecipeDataHudSortingOrderOffset) = def.HudSortingOrder;
            rd[Layout.RecipeDataAlwaysUnlockedOffset] = def.AlwaysUnlocked ? (byte)1 : (byte)0;
            rd[Layout.RecipeDataHideInStationOffset] = 0;
            rd[Layout.RecipeDataIgnoreServerSettingsOffset] = 0;
            // Managed copy of the same values, for the RecipeHashLookupMap value below.
            var data = clone.TryRead<RecipeData>(em, out var existingData) ? existingData : default;
            data.Entity = clone;
            data.Guid = target;
            data.CraftDuration = def.CraftDuration;
            data.HudSortingOrder = def.HudSortingOrder;
            data.AlwaysUnlocked = def.AlwaysUnlocked;
            data.HideInStation = false;
            data.IgnoreServerSettings = false;
            sb.Append("RecipeData duration=" + def.CraftDuration.ToString("G", System.Globalization.CultureInfo.InvariantCulture) + "; ");

            if (!TryRewriteBuffers(em, clone, outputGuid, def.Output.Amount, resolvedInputs, resolvedAmounts, sb, out var bufWhy))
            {
                RollbackApply(world, prefabs, gameData, def, work);
                report = sb + bufWhy;
                return false;
            }

            if (!haveExisting)
            {
                prefabs._PrefabGuidToEntityMap.TryAdd(target, clone);
                if (prefabs._PrefabDataLookup.TryGetValue(source, out var assetData))
                {
                    assetData.AssetName = new FixedString128Bytes(def.Id);
                    prefabs._PrefabDataLookup.TryAdd(target, assetData);
                }
                prefabs._ConversionStateLookup.TryAdd(target, AssetConversionState.Converted);
                var spawn = prefabs._SpawnableNameToPrefabGuidDictionary;
                if (spawn != null && !spawn.TryGetValue(def.Id, out _))
                    spawn[def.Id] = target;
                work.MapsInserted = true;
                sb.Append("maps inserted; ");
            }

            if (map.IsCreated)
            {
                if (!haveExisting)
                {
                    bool added = map.TryAdd(target, data);
                    sb.Append("recipeMap=" + added + "; ");
                    if (!added)
                    {
                        RollbackApply(world, prefabs, gameData, def, work);
                        report = sb + "RecipeHashLookupMap.TryAdd failed";
                        return false;
                    }
                    work.HashAdded = true;
                }
                else
                {
                    map.Remove(target);
                    bool added = map.TryAdd(target, data);
                    sb.Append("recipeMap=" + added + "; ");
                    if (!added)
                    {
                        report = sb + "RecipeHashLookupMap.TryAdd failed";
                        return false;
                    }
                }
            }
            else
            {
                sb.Append("recipeMap=not-created; ");
            }

            TryWriteOriginalDuration(gameData, target, def.CraftDuration, sb);

            int ownerId = OwnerId(def.Owner);
            work.Stations = resolvedStations;
            work.OwnerId = ownerId;
            for (int i = 0; i < resolvedStations.Length; i++)
            {
                AppendStation(em, prefabs, resolvedStations[i], target, ownerId, sb);
            }

            report = sb.ToString();
            return true;
        }
        catch (Exception ex)
        {
            try
            {
                var prefabs = world != null && world.IsCreated
                    ? world.GetExistingSystemManaged<PrefabCollectionSystem>()
                    : null;
                var gameData = world != null && world.IsCreated
                    ? world.GetExistingSystemManaged<GameDataSystem>()
                    : null;
                if (world != null)
                    RollbackApply(world, prefabs, gameData, def, work);
            }
            catch { }
            report = sb + " EXCEPTION " + ex;
            return false;
        }
    }

    private sealed class ApplyWork
    {
        public bool CreatedClone;
        public Entity Clone;
        public bool MapsInserted;
        public bool HashAdded;
        public bool MarkedClonedHere;
        public PrefabGUID Target;
        public PrefabGUID[]? Stations;
        public int OwnerId;
    }

    private static void RollbackApply(
        World world,
        PrefabCollectionSystem? prefabs,
        GameDataSystem? gameData,
        RecipeDefinition def,
        ApplyWork work)
    {
        try
        {
            var em = world.EntityManager;
            if (work.Stations != null && prefabs != null)
            {
                for (int i = 0; i < work.Stations.Length; i++)
                    RemoveStation(em, prefabs, work.Stations[i], work.Target, work.OwnerId, new StringBuilder());
            }

            if (work.HashAdded && gameData != null && gameData.RecipeHashLookupMap.IsCreated)
            {
                try { gameData.RecipeHashLookupMap.Remove(work.Target); } catch { }
                TryRemoveOriginalDuration(gameData, work.Target, new StringBuilder());
            }

            if (work.MapsInserted && prefabs != null)
            {
                try { prefabs._PrefabGuidToEntityMap.Remove(work.Target); } catch { }
                try { prefabs._PrefabDataLookup.Remove(work.Target); } catch { }
                try { prefabs._ConversionStateLookup.Remove(work.Target); } catch { }
                try
                {
                    var spawn = prefabs._SpawnableNameToPrefabGuidDictionary;
                    if (spawn != null && spawn.ContainsKey(def.Id))
                        spawn.Remove(def.Id);
                }
                catch { }
            }

            if (work.CreatedClone && work.Clone != Entity.Null && em.Exists(work.Clone))
            {
                try { em.DestroyEntity(work.Clone); }
                catch (Exception e)
                {
                    Plugin.Logger?.LogWarning("RecipeApply: rollback DestroyEntity threw: " + e.Message);
                }
            }

            if (work.MarkedClonedHere)
            {
                lock (Gate) ClonedHere.Remove(def.GuidHash);
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("RecipeApply: rollback threw: " + e.Message);
        }
    }

    internal static bool TryUnapply(World world, RecipeDefinition def, out string report)
    {
        var sb = new StringBuilder();
        try
        {
            var em = world.EntityManager;
            var prefabs = world.GetExistingSystemManaged<PrefabCollectionSystem>();
            var gameData = world.GetExistingSystemManaged<GameDataSystem>();
            var target = new PrefabGUID(def.GuidHash);
            int ownerId = OwnerId(def.Owner);

            if (prefabs != null)
            {
                for (int i = 0; i < def.Stations.Count; i++)
                {
                    if (!TryResolveStation(prefabs, def.Stations[i], out var station, out _))
                        continue;
                    RemoveStation(em, prefabs, station, target, ownerId, sb);
                }
            }
            else
            {
                DropLedgerClaims(def);
            }

            if (gameData != null && gameData.RecipeHashLookupMap.IsCreated)
            {
                gameData.RecipeHashLookupMap.Remove(target);
                sb.Append("recipeMap removed; ");
                TryRemoveOriginalDuration(gameData, target, sb);
            }

            Entity clone = Entity.Null;
            bool haveClone = prefabs != null
                && prefabs._PrefabGuidToEntityMap.TryGetValue(target, out clone)
                && clone != Entity.Null
                && em.Exists(clone);
            if (haveClone)
            {
                try { prefabs!._PrefabGuidToEntityMap.Remove(target); } catch { }
                try { prefabs!._PrefabDataLookup.Remove(target); } catch { }
                try { prefabs!._ConversionStateLookup.Remove(target); } catch { }
                try
                {
                    var spawn = prefabs!._SpawnableNameToPrefabGuidDictionary;
                    if (spawn != null && spawn.ContainsKey(def.Id))
                        spawn.Remove(def.Id);
                }
                catch { }
                try { em.DestroyEntity(clone); }
                catch (Exception e)
                {
                    Plugin.Logger?.LogWarning("RecipeApply: DestroyEntity threw: " + e.Message);
                }
                lock (Gate) ClonedHere.Remove(def.GuidHash);
                sb.Append("clone destroyed; ");
            }
            else
                sb.Append("no clone; ");

            report = sb.ToString();
            return true;
        }
        catch (Exception ex)
        {
            report = sb + " EXCEPTION " + ex;
            return false;
        }
    }

    private static bool TryResolveSource(PrefabCollectionSystem prefabs, RecipeDefinition def, out PrefabGUID source, out string why)
    {
        source = default;
        why = "";
        if (def.SourceGuidHash != 0)
        {
            source = new PrefabGUID(def.SourceGuidHash);
            return true;
        }
        if (string.IsNullOrEmpty(def.SourceName))
        {
            why = "no source guid or spawnable name";
            return false;
        }
        try
        {
            PrefabGUID byName = default;
            if (prefabs._PrefabLookupMap.TryGetPrefabGuidWithName(def.SourceName, out byName, true) && byName._Value != 0)
            {
                source = byName;
                return true;
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("RecipeApply: TryGetPrefabGuidWithName('" + def.SourceName + "') threw: " + e.Message);
        }
        var map = prefabs._SpawnableNameToPrefabGuidDictionary;
        if (map != null && map.TryGetValue(def.SourceName, out source))
            return true;
        why = "source prefab '" + def.SourceName + "' not found by PrefabLookupMap name or spawnable name";
        source = default;
        return false;
    }

    private static bool TryResolveItem(PrefabCollectionSystem prefabs, RecipeItemRef item, out PrefabGUID guid, out string why)
    {
        guid = default;
        why = "";
        if (item.GuidHash != 0)
        {
            guid = new PrefabGUID(item.GuidHash);
            return true;
        }
        if (string.IsNullOrEmpty(item.Name))
        {
            why = "empty item token";
            return false;
        }

        try
        {
            PrefabGUID byName = default;
            if (prefabs._PrefabLookupMap.TryGetPrefabGuidWithName(item.Name, out byName, true) && byName._Value != 0)
            {
                guid = byName;
                return true;
            }
        }
        catch { }

        var map = prefabs._SpawnableNameToPrefabGuidDictionary;
        if (map != null && map.TryGetValue(item.Name, out guid))
            return true;

        // Progenitor item id: both sides hash the same string. The item apply must have
        // completed first (DefineItem before DefineRecipe; PrfI/PrfD before any future PrfR).
        var hashed = new PrefabGUID(PrefabGUID.CalculateGuidHash(item.Name));
        if (prefabs._PrefabGuidToEntityMap.TryGetValue(hashed, out _))
        {
            guid = hashed;
            return true;
        }

        why = "item '" + item.Name + "' not in PrefabLookupMap, spawnable names, or applied Progenitor items";
        return false;
    }

    private static bool TryResolveStation(PrefabCollectionSystem prefabs, RecipeStationRef station, out PrefabGUID guid, out string why)
    {
        guid = default;
        why = "";
        if (station.GuidHash != 0)
        {
            guid = new PrefabGUID(station.GuidHash);
            return true;
        }
        if (string.IsNullOrEmpty(station.Name))
        {
            why = "empty station token";
            return false;
        }
        try
        {
            PrefabGUID byName = default;
            if (prefabs._PrefabLookupMap.TryGetPrefabGuidWithName(station.Name, out byName, true) && byName._Value != 0)
            {
                guid = byName;
                return true;
            }
        }
        catch { }
        var map = prefabs._SpawnableNameToPrefabGuidDictionary;
        if (map != null && map.TryGetValue(station.Name, out guid))
            return true;
        why = "station '" + station.Name + "' not found";
        return false;
    }

    private static unsafe bool TryRewriteBuffers(
        EntityManager em,
        Entity clone,
        PrefabGUID output,
        int outputAmount,
        PrefabGUID[] inputs,
        int[] amounts,
        StringBuilder sb,
        out string why)
    {
        why = "";
        try
        {
            // Grow through the Clear/Add instantiations the game ships, then write
            // Guid/Amount at the metadata-verified offsets — never sizeof of the interop wrapper.
            var outputs = em.GetBuffer<RecipeOutputBuffer>(clone);
            outputs.Clear();
            outputs.Add(default);
            var outputIndex = ComponentIndex.IndexOf<RecipeOutputBuffer>();
            int outputLength = em.GetBufferLength(clone, outputIndex);
            byte* outputRaw = (byte*)em.GetBufferRawRW(clone, outputIndex);
            if (outputRaw == null || outputLength != 1)
            {
                why = "output buffer grow failed (length " + outputLength + ")";
                return false;
            }
            RecipeBufferLayout.WriteOutput(outputRaw, 0, output.GuidHash, outputAmount);
            if (!RecipeBufferLayout.TryReadOutput(outputRaw, 0, out int outGuid, out int outAmt)
                || outGuid != output.GuidHash || outAmt != outputAmount)
            {
                why = "output buffer readback mismatch at dump offsets";
                return false;
            }

            var req = em.GetBuffer<RecipeRequirementBuffer>(clone);
            req.Clear();
            for (int i = 0; i < inputs.Length; i++)
                req.Add(default);
            var reqIndex = ComponentIndex.IndexOf<RecipeRequirementBuffer>();
            int reqLength = em.GetBufferLength(clone, reqIndex);
            byte* reqRaw = (byte*)em.GetBufferRawRW(clone, reqIndex);
            if (reqRaw == null || reqLength != inputs.Length)
            {
                why = "requirement buffer grow failed (length " + reqLength + ")";
                return false;
            }
            for (int i = 0; i < inputs.Length; i++)
            {
                RecipeBufferLayout.WriteRequirement(reqRaw, i, inputs[i].GuidHash, amounts[i]);
                if (!RecipeBufferLayout.TryReadRequirement(reqRaw, i, out int inGuid, out int inAmt)
                    || inGuid != inputs[i].GuidHash || inAmt != amounts[i])
                {
                    why = "requirement buffer readback mismatch at dump offsets";
                    return false;
                }
            }

            var unitType = ComponentIndex.Of<RecipeOutputUnitBuffer>(ComponentType.AccessMode.ReadWrite);
            if (em.HasComponent(clone, unitType))
            {
                em.GetBuffer<RecipeOutputUnitBuffer>(clone).Clear();
                var unitIndex = ComponentIndex.IndexOf<RecipeOutputUnitBuffer>();
                int unitLength = em.GetBufferLength(clone, unitIndex);
                if (unitLength > 0)
                {
                    byte* unitRaw = (byte*)em.GetBufferRawRO(clone, unitIndex);
                    if (unitRaw != null && unitLength <= RecipeBufferLayout.MaxElements)
                    {
                        for (int i = 0; i < unitLength; i++)
                            RecipeBufferLayout.TryReadOutputUnit(unitRaw, i, out _, out _);
                    }
                    why = "RecipeOutputUnitBuffer still has " + unitLength + " rows after Clear";
                    return false;
                }
            }

            var linkType = ComponentIndex.Of<RecipeLinkBuffer>(ComponentType.AccessMode.ReadWrite);
            if (em.HasComponent(clone, linkType))
            {
                em.GetBuffer<RecipeLinkBuffer>(clone).Clear();
                var linkIndex = ComponentIndex.IndexOf<RecipeLinkBuffer>();
                int linkLength = em.GetBufferLength(clone, linkIndex);
                if (linkLength > 0)
                {
                    byte* linkRaw = (byte*)em.GetBufferRawRO(clone, linkIndex);
                    if (linkRaw != null && linkLength <= RecipeBufferLayout.MaxElements)
                    {
                        for (int i = 0; i < linkLength; i++)
                            RecipeBufferLayout.TryReadLink(linkRaw, i, out _);
                    }
                    why = "RecipeLinkBuffer still has " + linkLength + " rows after Clear";
                    return false;
                }
            }

            sb.Append("buffers output=1 req=" + inputs.Length + "; ");
            return true;
        }
        catch (Exception e)
        {
            why = "buffer rewrite threw: " + e.Message;
            return false;
        }
    }

    private static void TryWriteOriginalDuration(GameDataSystem gameData, PrefabGUID target, float duration, StringBuilder sb)
    {
        try
        {
            var durations = gameData._OriginalRecipeDurations;
            if (!durations.IsCreated)
            {
                sb.Append("originalDurations=not-created; ");
                return;
            }
            durations.Remove(target);
            bool added = durations.TryAdd(target, duration);
            sb.Append("originalDurations=" + added + "; ");
        }
        catch (Exception e)
        {
            sb.Append("originalDurations threw " + e.GetType().Name + "; ");
        }
    }

    private static void TryRemoveOriginalDuration(GameDataSystem gameData, PrefabGUID target, StringBuilder sb)
    {
        try
        {
            var durations = gameData._OriginalRecipeDurations;
            if (durations.IsCreated)
            {
                durations.Remove(target);
                sb.Append("originalDurations removed; ");
            }
        }
        catch { }
    }

    private static unsafe void AppendStation(
        EntityManager em,
        PrefabCollectionSystem prefabs,
        PrefabGUID station,
        PrefabGUID recipe,
        int ownerId,
        StringBuilder sb)
    {
        if (!prefabs._PrefabGuidToEntityMap.TryGetValue(station, out var stationEntity) || !em.Exists(stationEntity))
        {
            sb.Append("station prefab " + station.GuidHash + " missing; ");
            return;
        }

        var bufferType = ComponentIndex.Of<WorkstationRecipesBuffer>(ComponentType.AccessMode.ReadWrite);
        if (!em.HasComponent(stationEntity, bufferType))
        {
            sb.Append("station " + station.GuidHash + " has no WorkstationRecipesBuffer; ");
            return;
        }

        bool present = BufferContains(em, stationEntity, recipe.GuidHash);
        bool write = Ledger.RecordAppend(station.GuidHash, recipe.GuidHash, ownerId, present);
        if (write)
            AddWorkstationRow(em, stationEntity, recipe);
        sb.Append("stationPrefab " + station.GuidHash + (write ? " appended; " : " claimed; "));

        // WorkstationRecipesBuffer is not networked (no Snapshot_ type in the game's metadata).
        // Live instances on both sides inherit the prefab row; append here too.
        var instances = Query.Entities(em, includeDisabled: true, includeSystems: false, ComponentIndex.Of<PrefabGUID>(ComponentType.AccessMode.ReadOnly));
        var prefabType = ComponentIndex.Of<PrefabTag>(ComponentType.AccessMode.ReadOnly);
        int live = 0;
        for (int i = 0; i < instances.Count; i++)
        {
            var e = instances[i];
            if (!em.Exists(e) || em.HasComponent(e, prefabType)) continue;
            if (!e.TryRead<PrefabGUID>(em, out var pg) || pg.GuidHash != station.GuidHash) continue;
            if (!em.HasComponent(e, bufferType)) continue;
            bool instPresent = BufferContains(em, e, recipe.GuidHash);
            if (!instPresent)
                AddWorkstationRow(em, e, recipe);
            live++;
        }
        sb.Append("live=" + live + "; ");
    }

    private static unsafe void RemoveStation(
        EntityManager em,
        PrefabCollectionSystem prefabs,
        PrefabGUID station,
        PrefabGUID recipe,
        int ownerId,
        StringBuilder sb)
    {
        bool removeRow = Ledger.RecordRemove(station.GuidHash, recipe.GuidHash, ownerId);
        var bufferType = ComponentIndex.Of<WorkstationRecipesBuffer>(ComponentType.AccessMode.ReadWrite);
        var prefabType = ComponentIndex.Of<PrefabTag>(ComponentType.AccessMode.ReadOnly);

        if (removeRow
            && prefabs._PrefabGuidToEntityMap.TryGetValue(station, out var stationEntity)
            && em.Exists(stationEntity)
            && em.HasComponent(stationEntity, bufferType))
        {
            RemoveWorkstationRow(em, stationEntity, recipe);
            sb.Append("stationPrefab row removed; ");
        }
        else
            sb.Append("stationPrefab row kept; ");

        if (!removeRow) return;

        var instances = Query.Entities(em, includeDisabled: true, includeSystems: false, ComponentIndex.Of<PrefabGUID>(ComponentType.AccessMode.ReadOnly));
        int live = 0;
        for (int i = 0; i < instances.Count; i++)
        {
            var e = instances[i];
            if (!em.Exists(e) || em.HasComponent(e, prefabType)) continue;
            if (!e.TryRead<PrefabGUID>(em, out var pg) || pg.GuidHash != station.GuidHash) continue;
            if (!em.HasComponent(e, bufferType)) continue;
            RemoveWorkstationRow(em, e, recipe);
            live++;
        }
        sb.Append("liveRemoved=" + live + "; ");
    }

    private static unsafe bool BufferContains(EntityManager em, Entity entity, int recipeHash)
    {
        var typeIndex = ComponentIndex.IndexOf<WorkstationRecipesBuffer>();
        int length = em.GetBufferLength(entity, typeIndex);
        if (length <= 0 || length > 4096) return false;
        void* raw = em.GetBufferRawRO(entity, typeIndex);
        if (raw == null) return false;
        int stride = Layout.WorkstationRecipesBufferStride;
        byte* p = (byte*)raw;
        for (int i = 0; i < length; i++)
        {
            int guid = *(int*)(p + (i * stride) + Layout.WorkstationRecipesBufferRecipeGuidOffset);
            if (guid == recipeHash) return true;
        }
        return false;
    }

    private static void AddWorkstationRow(EntityManager em, Entity entity, PrefabGUID recipe)
    {
        var buffer = em.GetBuffer<WorkstationRecipesBuffer>(entity);
        buffer.Add(new WorkstationRecipesBuffer { RecipeGuid = recipe });
    }

    private static void RemoveWorkstationRow(EntityManager em, Entity entity, PrefabGUID recipe)
    {
        // DynamicBuffer.RemoveAt is not on the game's instantiation list; Clear + Add are.
        // Rebuild the buffer without this GUID.
        var buffer = em.GetBuffer<WorkstationRecipesBuffer>(entity);
        int length = buffer.Length;
        if (length <= 0) return;
        var keep = new WorkstationRecipesBuffer[length];
        int n = 0;
        for (int i = 0; i < length; i++)
        {
            var row = buffer[i];
            if (row.RecipeGuid.GuidHash == recipe.GuidHash) continue;
            keep[n++] = row;
        }
        if (n == length) return;
        buffer.Clear();
        for (int i = 0; i < n; i++)
            buffer.Add(keep[i]);
    }
#endif

    internal sealed class RecipeEntry
    {
        public RecipeEntry(RecipeDefinition def, RecipeRegistrationHandle handle)
        {
            Definition = def;
            Handle = handle;
            handle.Bind(this);
        }

        public RecipeDefinition Definition { get; }
        public RecipeRegistrationHandle Handle { get; }
    }

    internal sealed class RecipeRegistrationHandle : IDisposable
    {
        private RecipeEntry? _entry;
        private bool _disposed;

        internal void Bind(RecipeEntry entry) => _entry = entry;

        internal RecipeDefinition? Definition => _entry?.Definition;

        internal void Invalidate()
        {
            _disposed = true;
            _entry = null;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            var handle = this;
            Unregister(handle);
        }
    }
}
