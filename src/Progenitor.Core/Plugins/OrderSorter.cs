using System;
using System.Collections.Generic;

namespace Progenitor.Plugins;

/// <summary>
/// One registration's ordering keys. No delegate: EventBus, SystemHooks, and Content.Edit each
/// hold their own callback and pass these keys into <see cref="OrderSorter"/>.
/// </summary>
public readonly struct OrderSpec
{
    /// <summary>
    /// Build a spec. <paramref name="before"/> / <paramref name="after"/> are stored as given
    /// (callers copy if the array is caller-owned). Empty plugin id cannot be targeted by
    /// another registration's before/after list.
    /// </summary>
    public OrderSpec(
        int index,
        string? pluginId,
        int priority,
        string[]? before,
        string[]? after,
        int loadOrder)
    {
        Index = index;
        PluginId = pluginId ?? "";
        Priority = priority;
        Before = before ?? Array.Empty<string>();
        After = after ?? Array.Empty<string>();
        LoadOrder = loadOrder;
    }

    /// <summary>Monotonic registration index; last tiebreak so equal keys stay insertion-ordered.</summary>
    public int Index { get; }

    /// <summary>BepInEx plugin GUID; Harmony-style before/after id.</summary>
    public string PluginId { get; }

    /// <summary>
    /// Numeric priority. EventBus and SystemHooks run higher first (Harmony). Content.Edit
    /// passes <c>higherPriorityFirst: false</c> so Early (−1000) runs under Late.
    /// </summary>
    public int Priority { get; }

    /// <summary>Plugin ids this registration must run before (this first, them later).</summary>
    public string[] Before { get; }

    /// <summary>Plugin ids this registration must run after (them first, this later).</summary>
    public string[] After { get; }

    /// <summary>First-seen order of <see cref="PluginId"/> in this Core generation.</summary>
    public int LoadOrder { get; }
}

/// <summary>
/// Deterministic order shared by EventBus, SystemHooks, and Content.Edit: numeric priority,
/// then Harmony-style before/after plugin ids as a topological constraint, then plugin load
/// order, then registration index. Missing before/after ids are ignored (same as Harmony).
///
/// The <em>closing</em> registration of a before/after cycle is the node at which DFS,
/// walking plugin ids then registration index (not list or subscribe order within a
/// frame), observes the back edge that completes the cycle. <see cref="TryOrder"/>'s
/// <c>closingPlugin</c> is that node and does not change if A then B subscribed or B
/// then A. Incremental <see cref="TryAccept"/> still refuses to store the candidate
/// being added — EventBus, SystemHooks, and Content.Edit all drop that same registration
/// and the Warn names it — because already-accepted rows are not removed. Content.Edit
/// <c>TryAdd</c> calls <see cref="TryAccept"/> so its cycle line names the same plugin.
/// </summary>
public static class OrderSorter
{
    private static readonly object LoadGate = new();
    private static readonly Dictionary<string, int> LoadSlots = new(StringComparer.OrdinalIgnoreCase);
    private static int _nextLoad;

    /// <summary>
    /// First-seen slot for <paramref name="pluginId"/> in this Core generation. Empty ids share
    /// one slot so priority-only (unnamed) registrations keep registration order among themselves.
    /// </summary>
    public static int AssignLoadOrder(string? pluginId)
    {
        string id = pluginId ?? "";
        lock (LoadGate)
        {
            if (LoadSlots.TryGetValue(id, out int existing)) return existing;
            int assigned = _nextLoad++;
            LoadSlots[id] = assigned;
            return assigned;
        }
    }

    /// <summary>
    /// Compare for the default (no before/after) order. <paramref name="higherPriorityFirst"/>
    /// is true for EventBus / SystemHooks (Harmony) and false for Content.Edit (Early-under).
    /// </summary>
    public static int CompareDefault(in OrderSpec a, in OrderSpec b, bool higherPriorityFirst)
    {
        int c = higherPriorityFirst
            ? b.Priority.CompareTo(a.Priority)
            : a.Priority.CompareTo(b.Priority);
        if (c != 0) return c;
        c = a.LoadOrder.CompareTo(b.LoadOrder);
        if (c != 0) return c;
        return a.Index.CompareTo(b.Index);
    }

    /// <summary>
    /// Order <paramref name="specs"/>. On a before/after cycle returns false, leaves
    /// <paramref name="orderedIndices"/> empty, and sets <paramref name="cycleIds"/> to the
    /// plugin-id path including the repeated start (A -&gt; B -&gt; A).
    /// <paramref name="closingPlugin"/> is the DFS closer: the plugin id of the node at
    /// which a plugin-id-then-index walk observes the back edge. Independent of the order
    /// the specs were listed or subscribed in a frame.
    /// </summary>
    public static bool TryOrder(
        IReadOnlyList<OrderSpec> specs,
        bool higherPriorityFirst,
        out int[] orderedIndices,
        out IReadOnlyList<string>? cycleIds,
        out string closingPlugin)
    {
        orderedIndices = Array.Empty<int>();
        cycleIds = null;
        closingPlugin = "";
        if (specs == null || specs.Count == 0) return true;

        int n = specs.Count;
        var adj = new List<int>[n];
        var indeg = new int[n];
        for (int i = 0; i < n; i++)
            adj[i] = new List<int>();

        for (int i = 0; i < n; i++)
        {
            var spec = specs[i];
            AddEdges(specs, adj, indeg, i, spec.Before, fromSelf: true);
            AddEdges(specs, adj, indeg, i, spec.After, fromSelf: false);
        }

        if (TryFindCycle(specs, adj, out var cycle, out closingPlugin))
        {
            cycleIds = cycle;
            return false;
        }

        var remaining = new bool[n];
        for (int i = 0; i < n; i++) remaining[i] = true;
        var result = new int[n];
        int written = 0;
        while (written < n)
        {
            int best = -1;
            for (int i = 0; i < n; i++)
            {
                if (!remaining[i] || indeg[i] != 0) continue;
                if (best < 0 || CompareDefault(specs[i], specs[best], higherPriorityFirst) < 0)
                    best = i;
            }

            if (best < 0)
            {
                TryFindCycle(specs, adj, out cycle, out closingPlugin);
                cycleIds = cycle;
                return false;
            }

            remaining[best] = false;
            result[written++] = best;
            var neighbours = adj[best];
            for (int k = 0; k < neighbours.Count; k++)
                indeg[neighbours[k]]--;
        }

        orderedIndices = result;
        return true;
    }

    /// <summary>
    /// Priority / load-order / registration order only (before/after ignored). Defensive
    /// fallback when a prefix that should already be acyclic still fails <see cref="TryOrder"/>.
    /// </summary>
    public static int[] OrderByDefault(IReadOnlyList<OrderSpec> specs, bool higherPriorityFirst)
    {
        if (specs == null || specs.Count == 0) return Array.Empty<int>();
        int n = specs.Count;
        var indices = new int[n];
        for (int i = 0; i < n; i++) indices[i] = i;
        Array.Sort(indices, (a, b) => CompareDefault(specs[a], specs[b], higherPriorityFirst));
        return indices;
    }

    /// <summary>
    /// Accept <paramref name="candidate"/> as a new registration on a site whose
    /// <paramref name="existing"/> list is already acyclic. On success
    /// <paramref name="orderedIndices"/> permutes <c>existing + candidate</c> (candidate is
    /// index <c>existing.Count</c>). On a cycle returns false, leaves
    /// <paramref name="orderedIndices"/> empty, and sets <paramref name="droppedPlugin"/> to
    /// the candidate (never an innocent already-accepted row) plus <paramref name="cycleIds"/>
    /// for the Warn line. Callers must not store the candidate; existing rows and their
    /// acyclic edges stay. This is the Content.Edit <c>TryAdd</c> rule, shared so EventBus
    /// and SystemHooks drop the same registration.
    /// </summary>
    public static bool TryAccept(
        IReadOnlyList<OrderSpec> existing,
        in OrderSpec candidate,
        bool higherPriorityFirst,
        out int[] orderedIndices,
        out string droppedPlugin,
        out IReadOnlyList<string>? cycleIds)
    {
        droppedPlugin = candidate.PluginId ?? "";
        cycleIds = null;
        orderedIndices = Array.Empty<int>();

        int n = existing == null ? 0 : existing.Count;
        var specs = new OrderSpec[n + 1];
        for (int i = 0; i < n; i++)
            specs[i] = existing![i];
        specs[n] = candidate;

        if (TryOrder(specs, higherPriorityFirst, out orderedIndices, out cycleIds, out _))
        {
            droppedPlugin = "";
            cycleIds = null;
            return true;
        }

        orderedIndices = Array.Empty<int>();
        droppedPlugin = string.IsNullOrEmpty(candidate.PluginId)
            ? "anon#" + candidate.Index
            : candidate.PluginId;
        return false;
    }

    /// <summary>
    /// Order <paramref name="specs"/> treating the last item as the candidate just being
    /// added (same as <see cref="TryAccept"/>). On a cycle omits that last index and orders
    /// the prefix, so acyclic edges among the rest survive. Tests use this on a combined list;
    /// live subscribe builds existing + candidate and calls <see cref="TryAccept"/> directly.
    /// </summary>
    public static int[] ResolveDroppingCloser(
        IReadOnlyList<OrderSpec> specs,
        bool higherPriorityFirst,
        out string droppedPlugin,
        out IReadOnlyList<string>? cycleIds)
    {
        droppedPlugin = "";
        cycleIds = null;
        if (specs == null || specs.Count == 0) return Array.Empty<int>();

        if (specs.Count == 1)
        {
            if (TryAccept(Array.Empty<OrderSpec>(), specs[0], higherPriorityFirst, out var one, out droppedPlugin, out cycleIds))
                return one;
            return Array.Empty<int>();
        }

        var prefix = new OrderSpec[specs.Count - 1];
        for (int i = 0; i < prefix.Length; i++)
            prefix[i] = specs[i];
        if (TryAccept(prefix, specs[specs.Count - 1], higherPriorityFirst, out var ordered, out droppedPlugin, out cycleIds))
            return ordered;
        if (TryOrder(prefix, higherPriorityFirst, out var kept, out _, out _))
            return kept;
        return OrderByDefault(prefix, higherPriorityFirst);
    }

    /// <summary>
    /// One Warn line when a before/after cycle drops the registration that closed it.
    /// Stable so operators and tests can grep it. Names the dropped plugin id and the
    /// cycle path; acyclic edges at the site are not mentioned because they stay.
    /// </summary>
    public static string CycleDroppedLog(
        string owner,
        string site,
        string droppedPlugin,
        IReadOnlyList<string>? cycleIds)
    {
        return (owner ?? "") + ": refused before/after cycle on " + (site ?? "")
            + " from " + (droppedPlugin ?? "")
            + ": " + FormatCycle(cycleIds) + "; dropping that registration";
    }

    /// <summary>Plugin-id path including the repeated start, joined with <c> -&gt; </c>.</summary>
    public static string FormatCycle(IReadOnlyList<string>? cycleIds)
    {
        if (cycleIds == null || cycleIds.Count == 0) return "";
        var parts = new string[cycleIds.Count];
        for (int i = 0; i < cycleIds.Count; i++)
            parts[i] = cycleIds[i] ?? "";
        return string.Join(" -> ", parts);
    }

    /// <summary>
    /// Diagnostic dump of a resolved list. Empty lists are a single <c>(none)</c> line so a
    /// future <c>hooks_order</c> op can print this verbatim.
    /// </summary>
    public static string FormatDescribe(string owner, string site, IReadOnlyList<OrderSpec>? ordered)
    {
        string head = (owner ?? "") + " " + (site ?? "");
        if (ordered == null || ordered.Count == 0)
            return head + ": (none)";
        var lines = new string[ordered.Count + 1];
        lines[0] = head + ":";
        for (int i = 0; i < ordered.Count; i++)
            lines[i + 1] = "  " + (i + 1) + ". " + FormatLine(ordered[i]);
        return string.Join("\n", lines);
    }

    /// <summary>One dump line: plugin id, priority, and any before/after constraints.</summary>
    public static string FormatLine(in OrderSpec spec)
    {
        string name = spec.PluginId.Length == 0 ? "anon#" + spec.Index : spec.PluginId;
        string line = name + " prio=" + spec.Priority;
        string before = FormatIdList(spec.Before);
        if (before.Length != 0) line += " before=[" + before + "]";
        string after = FormatIdList(spec.After);
        if (after.Length != 0) line += " after=[" + after + "]";
        return line;
    }

    /// <summary>Copy before/after ids so a caller cannot mutate edges after register.</summary>
    public static string[] CopyIds(string[]? src)
    {
        if (src == null || src.Length == 0) return Array.Empty<string>();
        var dst = new string[src.Length];
        for (int i = 0; i < src.Length; i++)
            dst[i] = src[i] ?? "";
        return dst;
    }

    /// <summary>Permute <paramref name="list"/> in place to match <paramref name="indices"/>.</summary>
    public static void ApplyPermutation<T>(IList<T> list, int[] indices)
    {
        if (list == null || indices == null || indices.Length != list.Count) return;
        bool identity = true;
        for (int i = 0; i < indices.Length; i++)
        {
            if (indices[i] == i) continue;
            identity = false;
            break;
        }
        if (identity) return;
        var copy = new T[list.Count];
        for (int i = 0; i < list.Count; i++)
            copy[i] = list[i];
        for (int i = 0; i < indices.Length; i++)
            list[i] = copy[indices[i]];
    }

    private static string FormatIdList(string[] ids)
    {
        if (ids == null || ids.Length == 0) return "";
        int n = 0;
        for (int i = 0; i < ids.Length; i++)
        {
            if (!string.IsNullOrEmpty(ids[i])) n++;
        }
        if (n == 0) return "";
        var parts = new string[n];
        int w = 0;
        for (int i = 0; i < ids.Length; i++)
        {
            if (string.IsNullOrEmpty(ids[i])) continue;
            parts[w++] = ids[i];
        }
        return string.Join(",", parts);
    }

    private static void AddEdges(
        IReadOnlyList<OrderSpec> specs,
        List<int>[] adj,
        int[] indeg,
        int self,
        string[] ids,
        bool fromSelf)
    {
        if (ids == null || ids.Length == 0) return;
        for (int k = 0; k < ids.Length; k++)
        {
            string id = ids[k];
            if (string.IsNullOrEmpty(id)) continue;
            for (int j = 0; j < specs.Count; j++)
            {
                if (j == self) continue;
                if (string.IsNullOrEmpty(specs[j].PluginId)) continue;
                if (!IdsEqual(specs[j].PluginId, id)) continue;
                int from = fromSelf ? self : j;
                int to = fromSelf ? j : self;
                if (HasEdge(adj[from], to)) continue;
                adj[from].Add(to);
                indeg[to]++;
            }
        }

        // Self before/after is a one-node cycle. Represent as a self-loop so cycle detect sees it.
        if (!string.IsNullOrEmpty(specs[self].PluginId)
            && ContainsId(ids, specs[self].PluginId)
            && !HasEdge(adj[self], self))
        {
            adj[self].Add(self);
            indeg[self]++;
        }
    }

    private static bool HasEdge(List<int> edges, int to)
    {
        for (int i = 0; i < edges.Count; i++)
        {
            if (edges[i] == to) return true;
        }
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

    private static bool TryFindCycle(
        IReadOnlyList<OrderSpec> specs,
        List<int>[] adj,
        out List<string> cycleIds,
        out string closingPlugin)
    {
        int n = specs.Count;
        var color = new byte[n];
        var parent = new int[n];
        for (int i = 0; i < n; i++) parent[i] = -1;

        // Visit plugin ids then registration index so the closer does not depend on
        // the order specs were listed (or subscribed in one frame).
        var visit = new int[n];
        for (int i = 0; i < n; i++) visit[i] = i;
        Array.Sort(visit, (a, b) =>
        {
            int c = string.Compare(specs[a].PluginId, specs[b].PluginId, StringComparison.OrdinalIgnoreCase);
            if (c != 0) return c;
            return specs[a].Index.CompareTo(specs[b].Index);
        });

        for (int k = 0; k < n; k++)
        {
            int i = visit[k];
            if (color[i] != 0) continue;
            if (DfsCycle(specs, adj, i, color, parent, out cycleIds, out closingPlugin))
                return true;
        }

        cycleIds = new List<string>();
        closingPlugin = specs.Count > 0 ? specs[specs.Count - 1].PluginId : "";
        return false;
    }

    private static bool DfsCycle(
        IReadOnlyList<OrderSpec> specs,
        List<int>[] adj,
        int node,
        byte[] color,
        int[] parent,
        out List<string> cycleIds,
        out string closingPlugin)
    {
        color[node] = 1;
        var edges = adj[node];
        for (int k = 0; k < edges.Count; k++)
        {
            int next = edges[k];
            if (color[next] == 1)
            {
                cycleIds = Reconstruct(specs, parent, next, node);
                closingPlugin = specs[node].PluginId;
                return true;
            }

            if (color[next] != 0) continue;
            parent[next] = node;
            if (DfsCycle(specs, adj, next, color, parent, out cycleIds, out closingPlugin))
                return true;
        }

        color[node] = 2;
        cycleIds = new List<string>();
        closingPlugin = "";
        return false;
    }

    private static List<string> Reconstruct(
        IReadOnlyList<OrderSpec> specs,
        int[] parent,
        int cycleStart,
        int cycleEnd)
    {
        var stack = new List<int>();
        int cur = cycleEnd;
        stack.Add(cur);
        int guard = specs.Count + 2;
        while (cur != cycleStart && cur >= 0 && guard-- > 0)
        {
            cur = parent[cur];
            if (cur < 0) break;
            stack.Add(cur);
        }

        stack.Reverse();
        var ids = new List<string>(stack.Count + 1);
        for (int i = 0; i < stack.Count; i++)
            ids.Add(specs[stack[i]].PluginId);
        ids.Add(specs[cycleStart].PluginId);
        return ids;
    }
}
