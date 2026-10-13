using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using Progenitor.Ecs;

#if !PROGENITOR_TESTS
using ProjectM;
using ProjectM.Network;
using ProjectM.Shared;
using Unity.Entities;
#endif

namespace Progenitor.Dev.Ops;

#if !PROGENITOR_TESTS
/// <summary>
/// Debug-bridge <c>entity</c> op: confirm whether <c>index:version</c> ids exist, request
/// destroy (DIAG5 phase <c>buffs</c>: <c>DestroyUtility.Destroy(TryRemoveBuff)</c> on
/// each still-existing <c>BuffBuffer</c> row), or finalize a pending
/// destroy (state machine: <c>waiting-buffs</c> / <c>buffs</c> / <c>tagged</c> /
/// <c>stuck</c>). The unit is never <c>DestroyUtility.Destroy</c>'d before phase
/// <c>tagged</c>, and there it gets <c>DestroyUtility.Destroy</c> <b>alone</b> so the removal
/// replicates to clients (<see cref="BuffSweep.DestroyUnitReplicated"/>); <c>DestroyTag</c> is
/// only a logged fallback if the unit still exists after
/// <see cref="BuffSweep.UnitDestroyFallbackFrames"/> frames. The phase keeps its old name. Stale <c>BuffBuffer</c> rows do not block <c>tagged</c>; a new live
/// row is destroyed now and starts another cycle (cap
/// <see cref="BuffSweep.MaxCycles"/>) then <c>stuck</c>. Player / user / prefab
/// entities are refused. Argument parse and the 64-id cap live in
/// <see cref="EntityLifecycleArgs"/> so tests can cover them without interop.
/// </summary>
public static class EntityLifecycleOps
{
    /// <summary>
    /// <paramref name="args"/>: <c>action</c> is <c>exists</c>, <c>destroy</c>, or
    /// <c>finalize</c>; <c>entities</c> is an array of <c>index:version</c> (cap 64);
    /// <c>reason</c> is <c>None</c> or <c>TryRemoveBuff</c> (finalize unit destroy, default None);
    /// <c>buffs</c> is an object keyed by unit id whose values are buff-id arrays (finalize).
    /// <c>world</c> is selected by the caller (the Dev <c>Handlers</c> world resolver).
    /// </summary>
    public static object Run(World world, JsonElement args)
    {
        try
        {
            if (world == null || !world.IsCreated)
                throw new InvalidOperationException("the game world does not exist yet (not in a game)");

            var action = ReadAction(args);
            var ids = EntityLifecycleArgs.ParseEntities(args);
            var em = world.EntityManager;
            var entries = new List<object>(ids.Length);
            var errors = new List<object>();

            if (action == "exists")
                RunExists(world, em, ids, entries, errors);
            else if (action == "finalize")
                RunFinalize(em, ids, EntityLifecycleArgs.ParseBuffs(args), ReadReason(args), entries, errors);
            else if (action == "force")
                RunForce(em, ids, ReadReason(args), entries, errors);
            else
                RunDestroy(em, ids, ReadReason(args), entries, errors);

            return new { action, count = entries.Count, entries, errors };
        }
        catch (Exception e) when (e is not ArgumentException and not InvalidOperationException)
        {
            Plugin.Logger?.LogError($"entity op threw: {e}");
            throw new InvalidOperationException($"entity op failed: {e.Message}", e);
        }
    }

    private static void RunExists(
        World world,
        EntityManager em,
        EntityLifecycleArgs.ParsedId[] ids,
        List<object> entries,
        List<object> errors)
    {
        var prefabs = EntityOps.TryPrefabCollection(world);
        foreach (var id in ids)
        {
            try
            {
                if (!id.IsValid)
                {
                    errors.Add(new { entity = id.Text, error = id.Error });
                    continue;
                }

                var entity = new Entity { Index = id.Index, Version = id.Version };
                bool exists = em.Exists(entity);
                entries.Add(new
                {
                    entity = FormatEntity(entity),
                    exists,
                    prefab = exists ? EntityOps.TryPrefabInfo(em, entity, prefabs) : null,
                });
            }
            catch (Exception e)
            {
                errors.Add(new { entity = id.Text, error = e.Message });
            }
        }
    }

    private static void RunDestroy(
        EntityManager em,
        EntityLifecycleArgs.ParsedId[] ids,
        DestroyDebugReason reason,
        List<object> entries,
        List<object> errors)
    {
        var playerType = ComponentIndex.Of<PlayerCharacter>(ComponentType.AccessMode.ReadOnly);
        var userType = ComponentIndex.Of<User>(ComponentType.AccessMode.ReadOnly);
        var prefabType = ComponentIndex.Of<Prefab>(ComponentType.AccessMode.ReadOnly);

        foreach (var id in ids)
        {
            try
            {
                if (!id.IsValid)
                {
                    errors.Add(new { entity = id.Text, error = id.Error });
                    continue;
                }

                var entity = new Entity { Index = id.Index, Version = id.Version };
                string formatted = FormatEntity(entity);
                if (!em.Exists(entity))
                {
                    ForgetDespawn(formatted);
                    entries.Add(new
                    {
                        entity = formatted,
                        existed = false,
                        requested = false,
                        phase = "buffs",
                        buffs = Array.Empty<string>(),
                        buffSweepComplete = true,
                        existsAfterRequest = false,
                        staleRows = Array.Empty<string>(),
                    });
                    continue;
                }

                if (em.HasComponent(entity, playerType) || em.HasComponent(entity, userType))
                {
                    errors.Add(new { entity = formatted, error = "refused: player entity" });
                    continue;
                }

                if (em.HasComponent(entity, prefabType))
                {
                    errors.Add(new { entity = formatted, error = "refused: prefab entity" });
                    continue;
                }

                // DIAG5 phase buffs: DestroyUtility.Destroy(TryRemoveBuff) alone on
                // each still-existing BuffBuffer row. Do not strip Attach. Do not destroy
                // or tag the unit. Finalize waits for listed ids, then tags.
                var sweep = BuffSweep.Sweep(em, entity);
                var outcome = EntityLifecycleArgs.AfterSweep(sweep.Complete, sweep.Stale, sweep.Reason);
                bool existsAfterRequest = em.Exists(entity);
                string[] buffIds = FormatIds(sweep.DestroyedIds);
                RememberDespawn(formatted, cycle: 1, sweep.BuffIds);
                string[] staleRows = FormatIds(sweep.StaleIds);
                if (!sweep.Complete)
                    Plugin.Logger?.LogWarning(BuffSweep.FormatIncompleteUnitWarning(entity.Index, entity.Version, sweep.Reason));
                Plugin.Logger?.LogInfo(
                    $"entity destroy {formatted} reason={reason} phase=buffs existsAfterRequest={existsAfterRequest} buffs={buffIds.Length} buffSweepComplete={outcome.BuffSweepComplete} staleRows={staleRows.Length}");
                entries.Add(new
                {
                    entity = formatted,
                    existed = true,
                    requested = true,
                    phase = outcome.Phase,
                    buffs = buffIds,
                    buffSweepComplete = outcome.BuffSweepComplete,
                    existsAfterRequest,
                    reason = outcome.Reason,
                    staleRows,
                });
            }
            catch (Exception e)
            {
                errors.Add(new { entity = id.Text, error = e.Message });
            }
        }
    }

    private static void RunFinalize(
        EntityManager em,
        EntityLifecycleArgs.ParsedId[] ids,
        Dictionary<string, EntityLifecycleArgs.ParsedId[]> buffsByUnit,
        DestroyDebugReason reason,
        List<object> entries,
        List<object> errors)
    {
        var playerType = ComponentIndex.Of<PlayerCharacter>(ComponentType.AccessMode.ReadOnly);
        var userType = ComponentIndex.Of<User>(ComponentType.AccessMode.ReadOnly);
        var prefabType = ComponentIndex.Of<Prefab>(ComponentType.AccessMode.ReadOnly);

        foreach (var id in ids)
        {
            try
            {
                if (!id.IsValid)
                {
                    errors.Add(new { entity = id.Text, error = id.Error });
                    continue;
                }

                var entity = new Entity { Index = id.Index, Version = id.Version };
                string formatted = FormatEntity(entity);
                if (!buffsByUnit.TryGetValue(formatted, out var supplied) && !buffsByUnit.TryGetValue(id.Text, out supplied))
                    supplied = Array.Empty<EntityLifecycleArgs.ParsedId>();

                if (!em.Exists(entity))
                {
                    ForgetDespawn(formatted);
                    var gone = EntityLifecycleArgs.AfterFinalize(new BuffSweep.FinalizeStep("tagged", BuffSweep.ActionNone), tagAddSucceeded: false);
                    Plugin.Logger?.LogInfo($"entity finalize {formatted} phase=tagged existed=false");
                    entries.Add(new
                    {
                        entity = formatted,
                        existed = false,
                        phase = gone.Phase,
                        staleRows = Array.Empty<string>(),
                    });
                    continue;
                }

                if (em.HasComponent(entity, playerType) || em.HasComponent(entity, userType))
                {
                    errors.Add(new { entity = formatted, error = "refused: player entity" });
                    continue;
                }

                if (em.HasComponent(entity, prefabType))
                {
                    errors.Add(new { entity = formatted, error = "refused: prefab entity" });
                    continue;
                }

                var listed = ToEntityIds(supplied);
                TryGetDespawn(formatted, out int cycle, out var persistedListed);
                listed = UnionListed(listed, persistedListed);

                var staleBuf = new BuffSweep.EntityId[BuffSweep.MaxElements];
                var newLiveBuf = new BuffSweep.EntityId[BuffSweep.MaxElements];
                var scan = BuffSweep.ScanBuffer(
                    em,
                    entity,
                    listed,
                    staleBuf,
                    newLiveBuf,
                    out int staleCount,
                    out int newLiveCount);
                bool scanComplete = scan.Complete;

                var existingBuf = new BuffSweep.EntityId[BuffSweep.MaxElements];
                int existingCount = CollectExisting(em, listed, existingBuf);

                var step = BuffSweep.DecideFinalizeStep(
                    existingLeft: existingCount > 0,
                    unitExists: true,
                    newLiveLeft: newLiveCount > 0,
                    cycle: cycle);
                // An unread buffer must not tag: we cannot tell stale from new-live.
                if (step.Phase == "tagged" && !scanComplete)
                    step = new BuffSweep.FinalizeStep("waiting-buffs", BuffSweep.ActionNone);

                string[] staleRows = scanComplete ? FormatIds(staleBuf, staleCount) : Array.Empty<string>();

                if (step.Action == BuffSweep.ActionDestroyBuffs)
                {
                    var destroyedNowBuf = new BuffSweep.EntityId[newLiveCount];
                    int destroyedNow = BuffSweep.DestroyBuffs(em, newLiveBuf.AsSpan(0, newLiveCount), destroyedNowBuf);
                    string[] newIds = FormatIds(destroyedNowBuf, destroyedNow);
                    int nextCycle = cycle + 1;
                    RememberDespawn(formatted, nextCycle, UnionListed(listed, CopyIds(newLiveBuf, newLiveCount)));
                    Plugin.Logger?.LogInfo(
                        $"entity finalize {formatted} phase=buffs buffs={newIds.Length} staleRows={staleRows.Length} cycle={nextCycle}");
                    entries.Add(new
                    {
                        entity = formatted,
                        existed = true,
                        phase = "buffs",
                        buffs = newIds,
                        staleRows,
                        cycle = nextCycle,
                    });
                    continue;
                }

                if (step.Phase == "waiting-buffs")
                {
                    string[] pending = FormatIds(existingBuf, existingCount);
                    Plugin.Logger?.LogInfo(
                        $"entity finalize {formatted} phase=waiting-buffs pending={pending.Length} staleRows={staleRows.Length} cycle={cycle}");
                    entries.Add(new
                    {
                        entity = formatted,
                        existed = true,
                        phase = step.Phase,
                        pending,
                        staleRows,
                        cycle,
                    });
                    continue;
                }

                if (step.Phase == "stuck")
                {
                    string[] newBuffs = FormatIds(newLiveBuf, newLiveCount);
                    string stuckReason = step.Reason ?? BuffSweep.FormatStuckReason(cycle);
                    Plugin.Logger?.LogWarning(
                        $"entity finalize {formatted} phase=stuck reason={stuckReason} pending={newBuffs.Length} staleRows={staleRows.Length} cycle={cycle}");
                    entries.Add(new
                    {
                        entity = formatted,
                        existed = true,
                        phase = "stuck",
                        reason = stuckReason,
                        pending = newBuffs,
                        staleRows,
                        cycle,
                    });
                    continue;
                }

                // DIAG5 tagged: every listed buff is gone; stale BuffBuffer rows are ignored.
                // Replicated removal: DestroyUtility.Destroy ALONE, so the game's destroy flow
                // finishes the unit and clients hear about it. An immediate DestroyTag removed it
                // on the server only (live 2026-09-24: a loot drop and 397 of 400 Frostmaws stayed
                // on the client). DestroyTag is added only as a logged fallback when the unit still
                // exists after BuffSweep.UnitDestroyFallbackFrames frames. Never on stuck.
                ForgetDespawn(formatted);
                try
                {
                    BuffSweep.DestroyUnitReplicated(em, entity, reason, "entity finalize");
                }
                catch (Exception e)
                {
                    errors.Add(new { entity = formatted, error = "DestroyUtility.Destroy threw: " + e.Message });
                }

                var outcome = EntityLifecycleArgs.AfterFinalize(step, tagAddSucceeded: false);
                Plugin.Logger?.LogInfo(
                    $"entity finalize {formatted} phase=tagged destroy=replicated existsAfter={em.Exists(entity)} staleRows={staleRows.Length} cycle={cycle}");
                entries.Add(new
                {
                    entity = formatted,
                    existed = true,
                    existsAfter = em.Exists(entity),
                    phase = outcome.Phase,
                    destroy = "replicated",
                    destroyTagAdded = outcome.DestroyTagAdded,
                    destroyTagFallbackFrames = BuffSweep.UnitDestroyFallbackFrames,
                    staleRows,
                    cycle,
                });
            }
            catch (Exception e)
            {
                errors.Add(new { entity = id.Text, error = e.Message });
            }
        }
    }

    private static BuffSweep.EntityId[] ToEntityIds(EntityLifecycleArgs.ParsedId[] supplied)
    {
        if (supplied == null || supplied.Length == 0) return Array.Empty<BuffSweep.EntityId>();
        int n = 0;
        for (int i = 0; i < supplied.Length; i++)
        {
            if (supplied[i].IsValid) n++;
        }

        if (n == 0) return Array.Empty<BuffSweep.EntityId>();
        var ids = new BuffSweep.EntityId[n];
        int w = 0;
        for (int i = 0; i < supplied.Length; i++)
        {
            if (!supplied[i].IsValid) continue;
            ids[w++] = new BuffSweep.EntityId(supplied[i].Index, supplied[i].Version);
        }

        return ids;
    }

    private static int CollectExisting(
        EntityManager em,
        BuffSweep.EntityId[] listed,
        BuffSweep.EntityId[] existingDest)
    {
        int existingCount = 0;
        for (int i = 0; i < listed.Length; i++)
        {
            var id = listed[i];
            if (id.IsNull) continue;
            var buff = new Entity { Index = id.Index, Version = id.Version };
            bool exists;
            try { exists = em.Exists(buff); }
            catch { exists = true; }
            if (!exists) continue;
            if (existingCount < existingDest.Length)
                existingDest[existingCount] = id;
            existingCount++;
        }

        return existingCount;
    }

    private struct PendingDespawn
    {
        public int Cycle;
        public BuffSweep.EntityId[] Listed;
    }

    private static readonly Dictionary<string, PendingDespawn> PendingByUnit = new(StringComparer.Ordinal);
    private static readonly object PendingGate = new();

    private static void RememberDespawn(string formatted, int cycle, BuffSweep.EntityId[] listed)
    {
        if (cycle < 1) cycle = 1;
        lock (PendingGate)
        {
            PendingByUnit[formatted] = new PendingDespawn
            {
                Cycle = cycle,
                Listed = listed ?? Array.Empty<BuffSweep.EntityId>(),
            };
        }
    }

    private static bool TryGetDespawn(string formatted, out int cycle, out BuffSweep.EntityId[] listed)
    {
        lock (PendingGate)
        {
            if (PendingByUnit.TryGetValue(formatted, out var row))
            {
                cycle = row.Cycle < 1 ? 1 : row.Cycle;
                listed = row.Listed ?? Array.Empty<BuffSweep.EntityId>();
                return true;
            }
        }

        cycle = 1;
        listed = Array.Empty<BuffSweep.EntityId>();
        return false;
    }

    private static void ForgetDespawn(string formatted)
    {
        lock (PendingGate)
            PendingByUnit.Remove(formatted);
    }

    private static BuffSweep.EntityId[] CopyIds(BuffSweep.EntityId[] buf, int count)
    {
        if (buf == null || count <= 0) return Array.Empty<BuffSweep.EntityId>();
        if (count > buf.Length) count = buf.Length;
        var copy = new BuffSweep.EntityId[count];
        Array.Copy(buf, copy, count);
        return copy;
    }

    private static BuffSweep.EntityId[] UnionListed(BuffSweep.EntityId[] a, BuffSweep.EntityId[] b)
    {
        if (a == null || a.Length == 0) return b ?? Array.Empty<BuffSweep.EntityId>();
        if (b == null || b.Length == 0) return a;
        int extra = 0;
        for (int i = 0; i < b.Length; i++)
        {
            if (b[i].IsNull || BuffSweep.ContainsId(a, b[i])) continue;
            extra++;
        }

        if (extra == 0) return a;
        var result = new BuffSweep.EntityId[a.Length + extra];
        Array.Copy(a, result, a.Length);
        int w = a.Length;
        for (int i = 0; i < b.Length; i++)
        {
            if (b[i].IsNull || BuffSweep.ContainsId(a, b[i])) continue;
            result[w++] = b[i];
        }

        if (w == result.Length) return result;
        var exact = new BuffSweep.EntityId[w];
        Array.Copy(result, exact, w);
        return exact;
    }

    /// <summary>
    /// Same refusal as <c>destroy</c>: sweep buffs, and do not destroy the unit
    /// while any of those buffs still exist. Tagging the owner first persists a buff whose
    /// <c>Buff.Target</c> is gone, and the next boot aborts in
    /// <c>Destroy_KnockbackResistanceBuff</c>. Still refuses players and prefab entities.
    /// </summary>
    private static void RunForce(
        EntityManager em,
        EntityLifecycleArgs.ParsedId[] ids,
        DestroyDebugReason reason,
        List<object> entries,
        List<object> errors)
    {
        var playerType = ComponentIndex.Of<PlayerCharacter>(ComponentType.AccessMode.ReadOnly);
        var userType = ComponentIndex.Of<User>(ComponentType.AccessMode.ReadOnly);
        var prefabType = ComponentIndex.Of<Prefab>(ComponentType.AccessMode.ReadOnly);
        foreach (var id in ids)
        {
            try
            {
                if (!id.IsValid)
                {
                    errors.Add(new { entity = id.Text, error = id.Error });
                    continue;
                }

                var entity = new Entity { Index = id.Index, Version = id.Version };
                string formatted = FormatEntity(entity);
                if (!em.Exists(entity))
                {
                    entries.Add(new { entity = formatted, existed = false, phase = "gone" });
                    continue;
                }

                if (em.HasComponent(entity, playerType) || em.HasComponent(entity, userType))
                {
                    errors.Add(new { entity = formatted, error = "refused: player entity" });
                    continue;
                }

                if (em.HasComponent(entity, prefabType))
                {
                    errors.Add(new { entity = formatted, error = "refused: prefab entity" });
                    continue;
                }

                var sweep = BuffSweep.Sweep(em, entity);
                int liveBuffs = 0;
                var buffIds = sweep.BuffIds;
                if (buffIds != null)
                {
                    for (int b = 0; b < buffIds.Length; b++)
                    {
                        var buff = new Entity { Index = buffIds[b].Index, Version = buffIds[b].Version };
                        if (em.Exists(buff)) liveBuffs++;
                    }
                }

                if (liveBuffs > 0 || !sweep.Complete)
                {
                    Plugin.Logger?.LogInfo(
                        "entity force refused " + formatted + " liveBuffs=" + liveBuffs
                        + " complete=" + sweep.Complete);
                    entries.Add(new
                    {
                        entity = formatted,
                        existed = true,
                        phase = "refused",
                        destroyTagAdded = false,
                        existsAfter = em.Exists(entity),
                        liveBuffs,
                        reason = "buffs still exist; unit was not destroyed",
                    });
                    continue;
                }

                // Same replicated removal as finalize: Destroy alone, DestroyTag only as the
                // delayed, logged fallback.
                BuffSweep.DestroyUnitReplicated(em, entity, reason, "entity force");
                Plugin.Logger?.LogInfo("entity force " + formatted + " destroy=replicated exists=" + em.Exists(entity));
                entries.Add(new
                {
                    entity = formatted,
                    existed = true,
                    phase = "forced",
                    destroy = "replicated",
                    destroyTagAdded = false,
                    existsAfter = em.Exists(entity),
                });
            }
            catch (Exception e)
            {
                errors.Add(new { entity = id.Text, error = e.Message });
            }
        }
    }

    private static string[] FormatIds(BuffSweep.EntityId[] ids) =>
        FormatIds(ids, ids == null ? 0 : ids.Length);

    private static string[] FormatIds(BuffSweep.EntityId[] ids, int count)
    {
        if (ids == null || count <= 0) return Array.Empty<string>();
        if (count > ids.Length) count = ids.Length;
        var result = new string[count];
        for (int i = 0; i < count; i++)
            result[i] = ids[i].ToString();
        return result;
    }

    private static string ReadAction(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("action", out var el) || el.ValueKind != JsonValueKind.String)
            throw new ArgumentException("args.action is required (exists, destroy, or finalize)");
        var action = el.GetString();
        if (string.IsNullOrWhiteSpace(action))
            throw new ArgumentException("args.action is required (exists, destroy, or finalize)");
        action = action.Trim().ToLowerInvariant();
        if (action is not "exists" and not "destroy" and not "finalize" and not "force")
            throw new ArgumentException($"args.action must be exists, destroy, finalize, or force (got '{el.GetString()}')");
        return action;
    }

    private static DestroyDebugReason ReadReason(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("reason", out var el)
            || el.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return DestroyDebugReason.None;
        if (el.ValueKind != JsonValueKind.String)
            throw new ArgumentException("args.reason must be 'None' or 'TryRemoveBuff'");
        var text = el.GetString();
        if (string.IsNullOrWhiteSpace(text) || text.Equals("None", StringComparison.OrdinalIgnoreCase))
            return DestroyDebugReason.None;
        if (text.Equals("TryRemoveBuff", StringComparison.OrdinalIgnoreCase))
            return DestroyDebugReason.TryRemoveBuff;
        throw new ArgumentException($"args.reason must be 'None' or 'TryRemoveBuff' (got '{text}')");
    }

    private static string FormatEntity(Entity entity) => $"{entity.Index}:{entity.Version}";
}
#endif

/// <summary>
/// Game-free argument helpers for the debug-bridge <c>entity</c> op: strict
/// <c>index:version</c> parse (both ints, version ≥ 1, index ≥ 0) and a 64-entry cap.
/// Live exists/destroy/finalize stays in <c>EntityLifecycleOps</c> so this file can compile in
/// the test project without interop.
/// </summary>
internal static class EntityLifecycleArgs
{
    /// <summary>Hard cap; the live op never walks more than this in one call.</summary>
    public const int MaxEntities = 64;

    /// <summary>
    /// DIAG5 phase <c>buffs</c> entry fields. The unit is never destroyed or DestroyTagged
    /// in this phase.
    /// </summary>
    internal readonly struct DestroyPhase1Outcome
    {
        public readonly string Phase;
        public readonly bool BuffSweepComplete;
        public readonly int Stale;
        public readonly string? Reason;

        public DestroyPhase1Outcome(bool buffSweepComplete, int stale, string? reason)
        {
            Phase = "buffs";
            BuffSweepComplete = buffSweepComplete;
            Stale = stale < 0 ? 0 : stale;
            Reason = buffSweepComplete ? null : reason;
        }
    }

    /// <summary>
    /// DIAG5 finalize entry fields. <c>destroyTagAdded</c> is true only on <c>tagged</c>
    /// when the add ran. Phase <c>stuck</c> never sets it.
    /// </summary>
    internal readonly struct FinalizeOutcome
    {
        public readonly string Phase;
        public readonly bool DestroyTagAdded;

        public FinalizeOutcome(string phase, bool destroyTagAdded)
        {
            Phase = phase;
            DestroyTagAdded = destroyTagAdded;
        }
    }

    /// <summary>
    /// Map a copy+destroy pass onto the Dev op phase-<c>buffs</c> entry. Never reports a
    /// unit DestroyTag and never issues unit destroy.
    /// </summary>
    public static DestroyPhase1Outcome AfterSweep(bool sweepComplete, int stale, string? reason) =>
        new DestroyPhase1Outcome(sweepComplete, stale, reason);

    /// <summary>
    /// Map a <see cref="BuffSweep.FinalizeStep"/> onto the Dev op finalize entry. Only
    /// <c>tagged</c> with <see cref="BuffSweep.ActionDestroyUnit"/> can report
    /// <c>destroyTagAdded</c>.
    /// </summary>
    public static FinalizeOutcome AfterFinalize(in BuffSweep.FinalizeStep step, bool tagAddSucceeded) =>
        new FinalizeOutcome(
            step.Phase,
            step.Phase == "tagged" && step.Action == BuffSweep.ActionDestroyUnit && tagAddSucceeded);

    /// <summary>
    /// Convenience for tests: a missing unit is <c>tagged</c> with no DestroyTag; otherwise
    /// the two-phase state machine decides (stale rows ignored; no new-live rows). Already-tagged
    /// residues use <see cref="AfterFinalize(in BuffSweep.FinalizeStep, bool)"/> directly.
    /// </summary>
    public static FinalizeOutcome AfterFinalize(bool allGone, bool tagAddSucceeded, bool unitExists = true)
    {
        var step = BuffSweep.DecideFinalizeStep(
            existingLeft: !allGone,
            unitExists: unitExists);
        return AfterFinalize(step, tagAddSucceeded);
    }

    /// <summary>One <c>args.entities</c> item after the cap check. <see cref="Error"/> is set when the id did not parse.</summary>
    public readonly struct ParsedId
    {
        public readonly string Text;
        public readonly int Index;
        public readonly int Version;
        public readonly string? Error;

        public bool IsValid => Error == null;

        public ParsedId(string text, int index, int version, string? error)
        {
            Text = text;
            Index = index;
            Version = version;
            Error = error;
        }
    }

    /// <summary>
    /// <c>index:version</c>. Both sides must be ints; index must be ≥ 0 and version ≥ 1 so
    /// a bare integer, <c>12:0</c>, or a negative index cannot silently become an entity.
    /// </summary>
    public static bool TryParseId(string text, out int index, out int version)
    {
        index = 0;
        version = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var colon = text.IndexOf(':');
        if (colon <= 0 || colon == text.Length - 1) return false;
        if (!int.TryParse(text.AsSpan(0, colon), NumberStyles.Integer, CultureInfo.InvariantCulture, out index)
            || !int.TryParse(text.AsSpan(colon + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out version))
            return false;
        return index >= 0 && version >= 1;
    }

    /// <summary>
    /// <c>args.entities</c> must be an array of at most <see cref="MaxEntities"/> items.
    /// Over the cap is a whole-call error; an unparseable id is a per-entry <see cref="ParsedId.Error"/>.
    /// </summary>
    public static ParsedId[] ParseEntities(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("entities", out var el) || el.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("args.entities is required (array of 'index:version')");

        int n = el.GetArrayLength();
        if (n > MaxEntities)
            throw new ArgumentException($"args.entities is capped at {MaxEntities} entries (got {n})");

        var result = new ParsedId[n];
        int i = 0;
        foreach (var item in el.EnumerateArray())
        {
            string text = item.ValueKind == JsonValueKind.String
                ? (item.GetString() ?? "")
                : item.GetRawText();
            if (!TryParseId(text, out var index, out var version))
            {
                result[i] = new ParsedId(
                    text,
                    0,
                    0,
                    $"must be 'index:version' with version >= 1 (got '{text}')");
            }
            else
            {
                result[i] = new ParsedId(text, index, version, null);
            }
            i++;
        }

        return result;
    }

    /// <summary>
    /// <c>args.buffs</c> is an object keyed by unit <c>index:version</c> whose values are
    /// arrays of buff ids (same parse as <see cref="ParseEntities"/>). Missing or null is
    /// an empty map (finalize then re-reads the unit's <c>BuffBuffer</c>). Each unit's list
    /// is capped at <see cref="Progenitor.Ecs.BuffSweep.MaxElements"/>.
    /// </summary>
    public static Dictionary<string, ParsedId[]> ParseBuffs(JsonElement args)
    {
        var result = new Dictionary<string, ParsedId[]>(StringComparer.Ordinal);
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("buffs", out var el)
            || el.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return result;
        if (el.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("args.buffs must be an object keyed by unit 'index:version'");

        foreach (var prop in el.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.Array)
                throw new ArgumentException($"args.buffs['{prop.Name}'] must be an array of 'index:version'");

            int n = prop.Value.GetArrayLength();
            if (n > Progenitor.Ecs.BuffSweep.MaxElements)
                throw new ArgumentException(
                    $"args.buffs['{prop.Name}'] is capped at {Progenitor.Ecs.BuffSweep.MaxElements} entries (got {n})");

            var ids = new ParsedId[n];
            int i = 0;
            foreach (var item in prop.Value.EnumerateArray())
            {
                string text = item.ValueKind == JsonValueKind.String
                    ? (item.GetString() ?? "")
                    : item.GetRawText();
                if (!TryParseId(text, out var index, out var version))
                {
                    ids[i] = new ParsedId(
                        text,
                        0,
                        0,
                        $"must be 'index:version' with version >= 1 (got '{text}')");
                }
                else
                {
                    ids[i] = new ParsedId(text, index, version, null);
                }
                i++;
            }

            result[prop.Name] = ids;
        }

        return result;
    }
}
