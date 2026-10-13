using System;
using System.Collections.Generic;
using Progenitor.Plugins;

namespace Progenitor.Content;

/// <summary>
/// One editor's ordering keys. No delegate: that lives on <see cref="PrefabEditTable"/> in the
/// plugin ALC and is re-supplied when plugins re-register after a Core swap.
/// </summary>
internal readonly struct PrefabEditSpec
{
    public PrefabEditSpec(
        int index,
        int guidHash,
        string pluginId,
        string pluginName,
        int priority,
        string[] before,
        string[] after,
        int loadOrder,
        string? version = null)
    {
        Index = index;
        GuidHash = guidHash;
        PluginId = pluginId ?? "";
        PluginName = string.IsNullOrEmpty(pluginName) ? PluginId : pluginName;
        Priority = priority;
        Before = before ?? Array.Empty<string>();
        After = after ?? Array.Empty<string>();
        LoadOrder = loadOrder;
        Version = version ?? "";
    }

    /// <summary>Monotonic registration index; last tiebreak.</summary>
    public int Index { get; }

    public int GuidHash { get; }

    /// <summary>BepInEx plugin GUID; Harmony-style before/after id.</summary>
    public string PluginId { get; }

    /// <summary>Display name for conflict lines; falls back to <see cref="PluginId"/>.</summary>
    public string PluginName { get; }

    /// <summary><see cref="PrefabEditPriority"/> or an intermediate int.</summary>
    public int Priority { get; }

    /// <summary>Plugin ids this editor must run before (this first, them later).</summary>
    public string[] Before { get; }

    /// <summary>Plugin ids this editor must run after (them first, this later).</summary>
    public string[] After { get; }

    /// <summary>First-seen order of <see cref="PluginId"/> in this process (or restored intent).</summary>
    public int LoadOrder { get; }

    /// <summary>
    /// Optional caller token that participates in the apply fingerprint (R20 finding 4).
    /// Empty when unused. Changing it requires a new <c>Edit</c> registration; captured
    /// values that change without re-registering use <c>Invalidate</c> instead.
    /// </summary>
    public string Version { get; }
}

/// <summary>
/// Plugin-side intent with no editor delegate. The shape Bootstrap <c>ContentMemory</c> would
/// be allowed to hold (it currently does not — clones and key rows only). Tests use this to
/// simulate a Core reload: drop delegates, re-register from intents, re-run.
/// </summary>
internal readonly struct PrefabEditIntent
{
    public PrefabEditIntent(PrefabEditSpec spec)
    {
        GuidHash = spec.GuidHash;
        PluginId = spec.PluginId;
        PluginName = spec.PluginName;
        Priority = spec.Priority;
        Before = CopyIds(spec.Before);
        After = CopyIds(spec.After);
        LoadOrder = spec.LoadOrder;
        Version = spec.Version;
    }

    public int GuidHash { get; }
    public string PluginId { get; }
    public string PluginName { get; }
    public int Priority { get; }
    public string[] Before { get; }
    public string[] After { get; }
    public int LoadOrder { get; }
    public string Version { get; }

    private static string[] CopyIds(string[] src)
    {
        if (src == null || src.Length == 0) return Array.Empty<string>();
        var dst = new string[src.Length];
        Array.Copy(src, dst, src.Length);
        return dst;
    }
}

/// <summary>
/// PrefabGUID / Prefab tag are structural identity. Editors may change stats, recipe fields,
/// icon references; they must not retarget the clone's GUID or strip the Prefab tag.
/// <c>EntityManager</c> AddComponent / RemoveComponent / buffer edits are unrestorable.
/// </summary>
internal static class PrefabEditIdentity
{
    public const string PrefabGuidTypeName = "PrefabGUID";
    public const string PrefabTagTypeName = "Prefab";
    public const string ItemTypeGuidField = "ItemTypeGUID";

    public static bool IsIdentityType(string? typeName)
    {
        if (string.IsNullOrEmpty(typeName)) return false;
        return string.Equals(typeName, PrefabGuidTypeName, StringComparison.Ordinal)
            || string.Equals(typeName, PrefabTagTypeName, StringComparison.Ordinal);
    }

    public static bool IsIdentityType(Type? type) =>
        type != null && IsIdentityType(type.Name);
}

/// <summary>
/// One cross-plugin write of the same component on the same prefab. <see cref="Line"/> is
/// the operator-facing Warn string; the other fields are the once-per-generation dedupe key.
/// </summary>
internal readonly struct PrefabEditConflict
{
    public PrefabEditConflict(int guidHash, string component, string loserId, string winnerId, string line)
    {
        GuidHash = guidHash;
        Component = component ?? "";
        LoserId = loserId ?? "";
        WinnerId = winnerId ?? "";
        Line = line ?? "";
    }

    public int GuidHash { get; }
    public string Component { get; }
    public string LoserId { get; }
    public string WinnerId { get; }
    public string Line { get; }
}

/// <summary>
/// Deterministic editor order: numeric priority (Early-under / Late-on-top), then
/// Harmony-style before/after plugin ids as a topological constraint, then plugin load
/// order, then registration index. A before/after cycle refuses rather than culling an edge.
/// Graph resolve is <see cref="OrderSorter"/> so Content.Edit names the same closer as
/// EventBus and SystemHooks.
/// </summary>
internal static class PrefabEditOrder
{
    /// <summary>
    /// Compare for the default (no before/after) order: lower priority first, then earlier
    /// load order, then earlier registration.
    /// </summary>
    public static int CompareDefault(in PrefabEditSpec a, in PrefabEditSpec b) =>
        OrderSorter.CompareDefault(ToOrderSpec(in a), ToOrderSpec(in b), higherPriorityFirst: false);

    /// <summary>Map one editor's keys into the shared sorter spec (no delegate).</summary>
    public static OrderSpec ToOrderSpec(in PrefabEditSpec spec) =>
        new(spec.Index, spec.PluginId, spec.Priority, spec.Before, spec.After, spec.LoadOrder);

    /// <summary>
    /// Accept <paramref name="candidate"/> onto an already-acyclic per-prefab list. On a
    /// cycle refuses to store it and sets <paramref name="cycleLog"/> naming
    /// <c>droppedPlugin</c> from <see cref="OrderSorter.TryAccept"/> — the same plugin
    /// EventBus / SystemHooks log, not a DFS node that may already have been accepted.
    /// </summary>
    public static bool TryAccept(
        IReadOnlyList<PrefabEditSpec> existing,
        in PrefabEditSpec candidate,
        out string? cycleLog)
    {
        cycleLog = null;
        var mapped = Map(existing);
        if (!OrderSorter.TryAccept(
                mapped,
                ToOrderSpec(in candidate),
                higherPriorityFirst: false,
                out _,
                out var dropped,
                out var cycleIds))
        {
            cycleLog = PrefabEditLog.CycleRefused(
                dropped,
                candidate.GuidHash,
                cycleIds ?? Array.Empty<string>());
            return false;
        }

        return true;
    }

    /// <summary>
    /// Order <paramref name="specs"/> for one prefab. On a before/after cycle returns false,
    /// leaves <paramref name="orderedIndices"/> empty, and sets <paramref name="cycleLog"/>.
    /// Missing before/after ids (plugin not registered on this prefab) are ignored, same as Harmony.
    /// </summary>
    public static bool TryOrder(
        IReadOnlyList<PrefabEditSpec> specs,
        out int[] orderedIndices,
        out string? cycleLog)
    {
        orderedIndices = Array.Empty<int>();
        cycleLog = null;
        if (specs == null || specs.Count == 0) return true;

        var mapped = Map(specs);
        if (!OrderSorter.TryOrder(
                mapped,
                higherPriorityFirst: false,
                out orderedIndices,
                out var cycleIds,
                out var closing))
        {
            string dropped = string.IsNullOrEmpty(closing)
                ? (string.IsNullOrEmpty(specs[specs.Count - 1].PluginId)
                    ? "anon#" + specs[specs.Count - 1].Index
                    : specs[specs.Count - 1].PluginId)
                : closing;
            cycleLog = PrefabEditLog.CycleRefused(
                dropped,
                specs[0].GuidHash,
                cycleIds ?? Array.Empty<string>());
            return false;
        }

        return true;
    }

    private static OrderSpec[] Map(IReadOnlyList<PrefabEditSpec>? specs)
    {
        if (specs == null || specs.Count == 0) return Array.Empty<OrderSpec>();
        var mapped = new OrderSpec[specs.Count];
        for (int i = 0; i < specs.Count; i++)
            mapped[i] = ToOrderSpec(specs[i]);
        return mapped;
    }

    /// <summary>
    /// One Warn line per earlier editor that touched the same component as the last writer.
    /// Winner is whoever ran last (Late / after / later load / later registration).
    /// </summary>
    public static List<string> ConflictLines(
        IReadOnlyList<PrefabEditSpec> ordered,
        IReadOnlyList<IReadOnlyList<string>> touchesPerEditor)
    {
        var conflicts = Conflicts(ordered, touchesPerEditor);
        var lines = new List<string>(conflicts.Count);
        for (int i = 0; i < conflicts.Count; i++)
            lines.Add(conflicts[i].Line);
        return lines;
    }

    /// <summary>
    /// Structured form of <see cref="ConflictLines"/> so the apply pass can dedupe by
    /// guid + component + the two plugin ids without parsing the log string.
    /// </summary>
    public static List<PrefabEditConflict> Conflicts(
        IReadOnlyList<PrefabEditSpec> ordered,
        IReadOnlyList<IReadOnlyList<string>> touchesPerEditor)
    {
        var result = new List<PrefabEditConflict>();
        if (ordered == null || ordered.Count == 0) return result;
        if (touchesPerEditor == null) return result;

        var byComp = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        int n = ordered.Count;
        if (touchesPerEditor.Count < n) n = touchesPerEditor.Count;
        for (int i = 0; i < n; i++)
        {
            var touches = touchesPerEditor[i];
            if (touches == null) continue;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int t = 0; t < touches.Count; t++)
            {
                string? comp = touches[t];
                if (string.IsNullOrEmpty(comp)) continue;
                if (PrefabEditIdentity.IsIdentityType(comp)) continue;
                if (!seen.Add(comp)) continue;
                if (!byComp.TryGetValue(comp, out var list))
                {
                    list = new List<int>();
                    byComp[comp] = list;
                }
                list.Add(i);
            }
        }

        foreach (var kv in byComp)
        {
            var writers = kv.Value;
            if (writers.Count < 2) continue;
            int winnerIdx = writers[writers.Count - 1];
            var winner = ordered[winnerIdx];
            for (int p = 0; p < writers.Count - 1; p++)
            {
                var loser = ordered[writers[p]];
                if (IdsEqual(loser.PluginId, winner.PluginId)) continue;
                string reason = ConflictReason(loser, winner);
                string line = PrefabEditLog.Conflict(
                    winner.GuidHash,
                    kv.Key,
                    loser.PluginName,
                    loser.PluginId,
                    winner.PluginName,
                    winner.PluginId,
                    reason);
                result.Add(new PrefabEditConflict(
                    winner.GuidHash,
                    kv.Key,
                    loser.PluginId,
                    winner.PluginId,
                    line));
            }
        }

        return result;
    }

    public static string ConflictReason(in PrefabEditSpec loser, in PrefabEditSpec winner)
    {
        if (winner.Priority != loser.Priority)
            return PrefabEditLog.ReasonPriority(winner.Priority, loser.Priority);
        if (ConstrainedAfter(winner, loser))
            return PrefabEditLog.ReasonBeforeAfter(winner.PluginId, loser.PluginId);
        if (winner.LoadOrder != loser.LoadOrder)
            return PrefabEditLog.ReasonLoadOrder();
        return PrefabEditLog.ReasonRegistration();
    }

    /// <summary>True when <paramref name="later"/> declared after <paramref name="earlier"/>, or earlier declared before later.</summary>
    public static bool ConstrainedAfter(in PrefabEditSpec later, in PrefabEditSpec earlier)
    {
        if (ContainsId(later.After, earlier.PluginId)) return true;
        if (ContainsId(earlier.Before, later.PluginId)) return true;
        return false;
    }

    private static bool ContainsId(string[] ids, string pluginId)
    {
        if (ids == null || ids.Length == 0 || string.IsNullOrEmpty(pluginId)) return false;
        for (int i = 0; i < ids.Length; i++)
        {
            if (IdsEqual(ids[i], pluginId)) return true;
        }
        return false;
    }

    private static bool IdsEqual(string? a, string? b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
