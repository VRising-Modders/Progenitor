using System;
using Progenitor.Ai;
using Xunit;
using Step = Progenitor.Ai.TreeRepairPolicy.Step;

namespace Progenitor.Tests;

/// <summary>
/// Core's post-load tree repair. Live 2026-09-24: a unit saved on a private tree variant reloads
/// with <c>BehaviourTreeInstance.TreeEntity = Entity.Null</c> and is brain-dead; the repair points it
/// back at its binding's prefab tree.
/// </summary>
public class TreeRepairPolicyTests
{
    private static Step Decide(bool binding, bool prefab, bool tree, bool expected, int[] running, int nodes) =>
        TreeRepairPolicy.Decide(binding, prefab, tree, expected, running, nodes, out _);

    [Fact]
    public void A_healthy_unit_or_one_without_a_binding_is_left_alone()
    {
        Assert.Equal(Step.None, Decide(true, true, true, true, Array.Empty<int>(), 421));
        Assert.Equal(Step.None, Decide(false, true, false, false, Array.Empty<int>(), 421));
    }

    [Fact]
    public void A_null_tree_is_pointed_at_the_prefab_tree_on_both_references()
    {
        Assert.Equal(Step.SetBoth, Decide(true, true, false, false, new[] { 0, 5, 420 }, 421));
        Assert.Equal(Step.SetBoth, Decide(true, true, false, true, Array.Empty<int>(), 421));
    }

    [Fact]
    public void A_missing_expected_tree_is_rebound_through_the_jobs_own_abort_path()
    {
        Assert.Equal(Step.SetExpected, Decide(true, true, true, false, new[] { 999 }, 421));
    }

    [Fact]
    public void Running_nodes_outside_the_prefab_tree_or_a_missing_prefab_tree_refuse_the_repair()
    {
        Assert.Equal(Step.Refuse, Decide(true, true, false, false, new[] { 3, 421 }, 421));
        Assert.Equal(Step.Refuse, Decide(true, false, false, false, Array.Empty<int>(), 0));
        Assert.Equal(Step.Refuse, Decide(true, true, false, false, Array.Empty<int>(), 0));
        TreeRepairPolicy.Decide(true, true, false, false, new[] { 500 }, 421, out string reason);
        Assert.Contains("running node 500", reason);
    }

    [Fact]
    public void Cast_options_are_reset_only_when_the_units_guid_is_dangling_and_the_prefabs_resolves()
    {
        Assert.True(TreeRepairPolicy.ShouldResetCastOptions(-12345, currentResolves: false, prefabGuid: 1557464335, prefabResolves: true));
        Assert.False(TreeRepairPolicy.ShouldResetCastOptions(-12345, currentResolves: true, prefabGuid: 1557464335, prefabResolves: true));
        Assert.False(TreeRepairPolicy.ShouldResetCastOptions(0, currentResolves: false, prefabGuid: 1557464335, prefabResolves: true));
        Assert.False(TreeRepairPolicy.ShouldResetCastOptions(-12345, currentResolves: false, prefabGuid: 0, prefabResolves: false));
        Assert.False(TreeRepairPolicy.ShouldResetCastOptions(-12345, currentResolves: false, prefabGuid: 1557464335, prefabResolves: false));
    }
}
