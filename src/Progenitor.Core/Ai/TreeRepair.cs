#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using ProjectM.Behaviours;
using Progenitor.Ecs;
using Progenitor.WorldState;
using Unity.Entities;

namespace Progenitor.Ai;

/// <summary>
/// Core hardening, installed with Core on the server: on the first frame after every world load,
/// and again at 2 s, 10 s and 30 s for late-streamed units, find units whose <c>BehaviourTreeInstance.TreeEntity</c>
/// or <c>ExpectedTreeEntity</c> is <c>Entity.Null</c> or names a missing tree, and point them back
/// at their <c>BehaviourTreeBinding</c>'s prefab tree so vanilla AI resumes. Every unit, not only
/// ctx.Ai's; every repair and refusal is logged. Decision: <see cref="TreeRepairPolicy"/>.
///
/// <para>Live 2026-09-24 (INC-20260924-02): units saved on a private tree came back with both
/// references <c>Entity.Null</c>; the server booted, then aborted inside Burst ("The entity does not
/// exist") as soon as a player came near and the tree job read the null tree. Writing both
/// references to the binding's prefab tree, with no player connected, was the proven repair.</para>
/// </summary>
internal static unsafe class TreeRepair
{
    // 0 = the first frame after the load step, before a player can be near a unit; the later passes
    // catch units that stream in late.
    private static readonly double[] Delays = { 0.0, 2.0, 10.0, 30.0 };
    private static bool _installed;

    internal static void Install()
    {
        if (_installed || !Plugin.IsServerProcess) return;
        _installed = true;
        WorldEpoch.Advanced += OnAdvanced;
        // A Core hot swap into a running world gets no load event; check once anyway.
        var world = Worlds.Server;
        if (world != null && world.IsCreated) Schedule("core install");
    }

    internal static void Uninstall()
    {
        if (!_installed) return;
        WorldEpoch.Advanced -= OnAdvanced;
        _installed = false;
    }

    private static void OnAdvanced(int epoch) => Schedule("world load");

    private static void Schedule(string why)
    {
        foreach (double delay in Delays)
        {
            double d = delay;
            Dev.MainThread.PostDelayed(d, () => Run(why + " +" + d + " s"));
        }
    }

    /// <summary>One pass. Returns how many units were repaired.</summary>
    internal static int Run(string why)
    {
        int repaired = 0, refused = 0, resetCo = 0;
        try
        {
            var world = Worlds.Server;
            if (world == null || !world.IsCreated) return 0;
            var em = world.EntityManager;
            var units = Query.Entities(
                em,
                includeDisabled: true,
                includeSystems: false,
                ComponentIndex.Of<BehaviourTreeInstance>(ComponentType.AccessMode.ReadOnly),
                ComponentIndex.Of<BehaviourTreeBinding>(ComponentType.AccessMode.ReadOnly));
            var nodeCounts = new Dictionary<int, int>();
            foreach (var unit in units)
            {
                if (ResetCastOptions(world, unit, why)) resetCo++;
                var tree = AiRuntime.CurrentTree(em, unit);
                var expected = AiRuntime.ExpectedTree(em, unit);
                bool treeValid = ValidTree(em, tree);
                bool expectedValid = ValidTree(em, expected);
                if (treeValid && expectedValid) continue;

                int treeGuid = AiRuntime.TreeGuidOf(em, unit);
                var prefabTree = AiRuntime.PrefabEntity(world, treeGuid);
                bool prefabFound = ValidTree(em, prefabTree);
                if (!nodeCounts.TryGetValue(treeGuid, out int nodeCount))
                {
                    nodeCount = prefabFound ? NodeCount(em, prefabTree) : 0;
                    nodeCounts[treeGuid] = nodeCount;
                }
                var step = TreeRepairPolicy.Decide(true, prefabFound, treeValid, expectedValid, Running(em, unit), nodeCount, out string reason);
                string line = "Ai: tree repair (" + why + ") " + AiRuntime.Fmt(unit) + " prefab " + AiRuntime.Name(world, AiRuntime.PrefabGuidOf(em, unit))
                    + " tree=" + AiRuntime.Fmt(tree) + " expected=" + AiRuntime.Fmt(expected) + ": ";
                switch (step)
                {
                    case TreeRepairPolicy.Step.SetExpected:
                        AiRuntime.WriteExpectedTree(em, unit, prefabTree);
                        repaired++;
                        Plugin.Logger?.LogWarning(line + reason);
                        break;
                    case TreeRepairPolicy.Step.SetBoth:
                        AiRuntime.WriteBothTrees(em, unit, prefabTree);
                        repaired++;
                        Plugin.Logger?.LogWarning(line + reason);
                        break;
                    case TreeRepairPolicy.Step.Refuse:
                        refused++;
                        Plugin.Logger?.LogError(line + "NOT repaired: " + reason + ". A unit without a valid tree can abort the server when its tree runs; remove it before a player comes near.");
                        break;
                }
            }
            if (repaired > 0 || refused > 0 || resetCo > 0)
                Plugin.Logger?.LogInfo("Ai: tree repair (" + why + "): " + repaired + " unit(s) repaired, " + refused + " refused, " + resetCo + " cast-option reference(s) reset");
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ai: tree repair (" + why + ") threw: " + e.Message);
        }
        return repaired;
    }

    /// <summary>
    /// A unit whose <c>CastOptionsUser</c> names a GUID the prefab lookup cannot resolve goes back to
    /// its prefab's cast options (<see cref="TreeRepairPolicy.ShouldResetCastOptions"/>). Logged.
    /// </summary>
    private static bool ResetCastOptions(World world, Entity unit, string why)
    {
        try
        {
            var em = world.EntityManager;
            int current = AiRuntime.CastOptionsGuid(em, unit);
            if (current == 0) return false;
            bool currentResolves = AiRuntime.PrefabEntity(world, current) != Entity.Null;
            if (currentResolves) return false;
            var prefab = AiRuntime.PrefabEntity(world, AiRuntime.PrefabGuidOf(em, unit));
            int prefabCo = prefab == Entity.Null ? 0 : AiRuntime.CastOptionsGuid(em, prefab);
            bool prefabResolves = prefabCo != 0 && AiRuntime.PrefabEntity(world, prefabCo) != Entity.Null;
            if (!TreeRepairPolicy.ShouldResetCastOptions(current, currentResolves, prefabCo, prefabResolves)) return false;
            AiRuntime.AssignCastOptions(world, unit, prefabCo);
            Plugin.Logger?.LogWarning("Ai: tree repair (" + why + ") " + AiRuntime.Fmt(unit) + " prefab " + AiRuntime.Name(world, AiRuntime.PrefabGuidOf(em, unit))
                + ": CastOptionsUser " + current + " does not resolve; reset to the prefab's " + AiRuntime.Name(world, prefabCo) + " (" + prefabCo + ")");
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ai: cast-option reset on " + AiRuntime.Fmt(unit) + " threw: " + e.Message);
            return false;
        }
    }

    private static bool ValidTree(EntityManager em, Entity tree)
    {
        try
        {
            return tree != Entity.Null && em.Exists(tree) && AiRuntime.TreeBlob(em, tree) != null;
        }
        catch
        {
            return false;
        }
    }

    private static int NodeCount(EntityManager em, Entity tree)
    {
        byte* blob = AiRuntime.TreeBlob(em, tree);
        int length = AiRuntime.BlobLength(blob);
        if (length <= 0) return 0;
        return TreeBlobReader.TryNodes(new ReadOnlySpan<byte>(blob, length), out _, out int count) ? count : 0;
    }

    private static List<int> Running(EntityManager em, Entity unit)
    {
        var list = new List<int>();
        byte* raw = AiRuntime.Buffer<BehaviourTreeNodeInstanceElement>(em, unit, Layout.BehaviourTreeNodeInstanceElementStride, write: false, out int length);
        for (int i = 0; raw != null && i < length && i < TreeBlobReader.MaxNodes; i++)
            list.Add(*(ushort*)(raw + i * Layout.BehaviourTreeNodeInstanceElementStride + Layout.BehaviourTreeNodeInstanceIndexOffset));
        return list;
    }
}
#endif
