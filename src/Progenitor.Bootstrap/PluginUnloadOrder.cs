using System;
using System.Collections.Generic;

namespace Progenitor.Bootstrap;

/// <summary>
/// Reverse-dependency order for unloading reloadable plugins during a Core swap.
/// Insertion order is wrong when plugin A's <c>Unload</c> still talks to plugin B:
/// B must stay loaded until A has finished. Independent plugins use last-loaded-first
/// as the tie-break. Game-free so the unit tests can pin the order without BepInEx.
/// </summary>
public static class PluginUnloadOrder
{
    public readonly struct Node
    {
        public Node(string guid, string[]? dependencies, int loadIndex)
        {
            Guid = guid ?? "";
            Dependencies = dependencies ?? Array.Empty<string>();
            LoadIndex = loadIndex;
        }

        /// <summary>BepInPlugin GUID. Empty GUIDs are treated as independent.</summary>
        public string Guid { get; }

        /// <summary>
        /// <c>[BepInDependency]</c> GUIDs. Entries that are not in the loaded set
        /// (Bootstrap's <c>progenitor</c>, missing plugins) are ignored.
        /// </summary>
        public string[] Dependencies { get; }

        /// <summary>Insertion / load index. Higher means loaded later.</summary>
        public int LoadIndex { get; }
    }

    /// <summary>
    /// Indices into <paramref name="nodes"/>: dependents first, then the plugins they
    /// depend on. Cycles among remaining nodes fall back to last-loaded-first.
    /// Duplicate GUIDs map to the first node; later duplicates stay independent.
    /// </summary>
    public static int[] ReverseTopological(IReadOnlyList<Node> nodes)
    {
        if (nodes == null || nodes.Count == 0) return Array.Empty<int>();
        int n = nodes.Count;
        var guidToIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < n; i++)
        {
            string guid = nodes[i].Guid;
            if (string.IsNullOrEmpty(guid)) continue;
            if (!guidToIndex.ContainsKey(guid))
                guidToIndex[guid] = i;
        }

        // remainingDependents[j] = how many in-set plugins still need to unload before j
        // (they depend on j). A plugin with zero dependents is free to unload now.
        var remainingDependents = new int[n];
        var iDependOn = new List<int>[n];
        for (int i = 0; i < n; i++)
            iDependOn[i] = new List<int>();

        for (int i = 0; i < n; i++)
        {
            var deps = nodes[i].Dependencies;
            if (deps == null) continue;
            for (int d = 0; d < deps.Length; d++)
            {
                string dep = deps[d];
                if (string.IsNullOrEmpty(dep)) continue;
                if (!guidToIndex.TryGetValue(dep, out int j) || j == i) continue;
                // i depends on j: i unloads before j.
                iDependOn[i].Add(j);
                remainingDependents[j]++;
            }
        }

        var ready = new List<int>(n);
        for (int i = 0; i < n; i++)
        {
            if (remainingDependents[i] == 0)
                ready.Add(i);
        }
        SortLastLoadedFirst(ready, nodes);

        var order = new int[n];
        var placed = new bool[n];
        int written = 0;
        while (written < n)
        {
            if (ready.Count == 0)
            {
                // Cycle: dump remaining last-loaded-first.
                for (int i = 0; i < n; i++)
                {
                    if (!placed[i]) ready.Add(i);
                }
                SortLastLoadedFirst(ready, nodes);
                for (int r = 0; r < ready.Count; r++)
                    order[written++] = ready[r];
                break;
            }

            int next = ready[0];
            ready.RemoveAt(0);
            if (placed[next]) continue;
            placed[next] = true;
            order[written++] = next;

            var deps = iDependOn[next];
            var newlyReady = new List<int>();
            for (int d = 0; d < deps.Count; d++)
            {
                int j = deps[d];
                if (placed[j]) continue;
                remainingDependents[j]--;
                if (remainingDependents[j] == 0)
                    newlyReady.Add(j);
            }
            if (newlyReady.Count > 0)
            {
                SortLastLoadedFirst(newlyReady, nodes);
                ready.AddRange(newlyReady);
                SortLastLoadedFirst(ready, nodes);
            }
        }
        return order;
    }

    private static void SortLastLoadedFirst(List<int> indices, IReadOnlyList<Node> nodes)
    {
        indices.Sort((a, b) =>
        {
            int byLoad = nodes[b].LoadIndex.CompareTo(nodes[a].LoadIndex);
            return byLoad != 0 ? byLoad : b.CompareTo(a);
        });
    }
}
