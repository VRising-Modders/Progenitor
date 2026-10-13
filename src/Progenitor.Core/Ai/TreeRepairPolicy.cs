using System.Collections.Generic;

namespace Progenitor.Ai;

/// <summary>
/// Game-free decision behind Core's post-load behavior-tree repair.
///
/// <para>Observed live 2026-09-24: <c>BehaviourTreeInstance</c> is saved, but a reference to a tree
/// entity that is not itself saved (a private tree-variant entity) comes back as
/// <c>Entity.Null</c> in both fields. The server boots, then aborts inside Burst when a player comes
/// near and the unit's tree job reads the null tree (INC-20260924-02). Writing both fields to the
/// prefab tree was the proven repair; writing only the expected tree would send the job down its
/// abort-on-mismatch path, which reads the old (null) tree.</para>
///
/// <para>The tree job reads <c>BehaviourTree</c> of <c>TreeEntity</c> first; when it differs from
/// <c>ExpectedTreeEntity</c> it aborts the running nodes against <c>TreeEntity</c>'s blob and then
/// rebinds to the expected tree (native <c>BehaviourTreeJob.Execute</c>). So:
/// a valid <c>TreeEntity</c> with a broken <c>ExpectedTreeEntity</c> only needs the expected tree
/// set (the job's own abort path does the rest); a broken <c>TreeEntity</c> needs both set to the
/// binding's prefab tree, and then the saved running-node list is re-expanded against that blob
/// directly, so every running index must lie inside it.</para>
/// </summary>
internal static class TreeRepairPolicy
{
    internal enum Step
    {
        None,
        SetExpected,
        SetBoth,
        Refuse,
    }

    /// <summary>
    /// Reset a unit's <c>CastOptionsUser</c> to its prefab's cast options only when its own GUID does
    /// not resolve (for example a ctx.Ai cast-option copy whose plugin is gone or whose re-apply was
    /// refused) and the prefab's does. A GUID that resolves, a unit with none (0), or a prefab without
    /// resolvable cast options is left alone.
    /// </summary>
    internal static bool ShouldResetCastOptions(int currentGuid, bool currentResolves, int prefabGuid, bool prefabResolves) =>
        currentGuid != 0 && !currentResolves && prefabGuid != 0 && prefabResolves && currentGuid != prefabGuid;

    internal static Step Decide(
        bool hasBinding,
        bool prefabTreeFound,
        bool treeValid,
        bool expectedValid,
        IReadOnlyList<int> runningNodeIndexes,
        int prefabNodeCount,
        out string reason)
    {
        reason = "";
        if (!hasBinding) return Step.None;
        if (treeValid && expectedValid) return Step.None;
        if (!prefabTreeFound)
        {
            reason = "the binding's tree prefab is not registered";
            return Step.Refuse;
        }
        if (treeValid)
        {
            reason = "ExpectedTreeEntity is missing; the job's abort path rebinds from the running tree";
            return Step.SetExpected;
        }
        if (prefabNodeCount <= 0)
        {
            reason = "the prefab tree blob does not parse";
            return Step.Refuse;
        }
        if (runningNodeIndexes != null)
        {
            foreach (int index in runningNodeIndexes)
            {
                if (index < 0 || index >= prefabNodeCount)
                {
                    reason = "running node " + index + " is outside the prefab tree (" + prefabNodeCount + " nodes); left as it is";
                    return Step.Refuse;
                }
            }
        }
        reason = "TreeEntity is missing; both references set to the prefab tree";
        return Step.SetBoth;
    }
}
