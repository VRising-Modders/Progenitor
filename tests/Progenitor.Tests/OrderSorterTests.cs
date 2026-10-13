using System;
using System.Collections.Generic;
using Progenitor.Content;
using Progenitor.Plugins;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Game-free coverage of the shared sorter (roadmap M2.6). Content.Edit
/// <c>TryAdd</c> calls <see cref="OrderSorter.TryAccept"/> so a cycle Warn names the
/// same plugin as EventBus / SystemHooks. These cases prove lower-priority-first
/// (Content) and higher-priority-first (hooks/events), and that the DFS closer is
/// independent of list order.
/// </summary>
public class OrderSorterTests
{
    [Fact]
    public void Higher_priority_runs_first()
    {
        var specs = new[]
        {
            Spec(0, "low", priority: 0),
            Spec(1, "high", priority: 100),
            Spec(2, "mid", priority: 50),
        };
        Assert.True(OrderSorter.TryOrder(specs, higherPriorityFirst: true, out var indices, out var cycle, out _));
        Assert.Null(cycle);
        Assert.Equal(new[] { 1, 2, 0 }, indices);
    }

    [Fact]
    public void Lower_priority_runs_first_when_content_mode()
    {
        var specs = new[]
        {
            Spec(0, "late", priority: 1000),
            Spec(1, "early", priority: -1000),
            Spec(2, "default", priority: 0),
        };
        Assert.True(OrderSorter.TryOrder(specs, higherPriorityFirst: false, out var indices, out _, out _));
        Assert.Equal(new[] { 1, 2, 0 }, indices);
    }

    [Fact]
    public void Same_priority_uses_plugin_load_order_then_registration()
    {
        var specs = new[]
        {
            Spec(0, "a", loadOrder: 0),
            Spec(1, "b", loadOrder: 1),
            Spec(2, "a", loadOrder: 0),
        };
        Assert.True(OrderSorter.TryOrder(specs, higherPriorityFirst: true, out var indices, out _, out _));
        Assert.Equal(new[] { 0, 2, 1 }, indices);
    }

    [Fact]
    public void Before_after_overrides_numeric_priority()
    {
        // High wants to run after Low, so Low is first despite High's number.
        var specs = new[]
        {
            Spec(0, "high", priority: 100, after: new[] { "low" }),
            Spec(1, "low", priority: 0),
        };
        Assert.True(OrderSorter.TryOrder(specs, higherPriorityFirst: true, out var indices, out var cycle, out _));
        Assert.Null(cycle);
        Assert.Equal(new[] { 1, 0 }, indices);
    }

    [Fact]
    public void Missing_before_after_id_is_ignored()
    {
        var specs = new[]
        {
            Spec(0, "a", before: new[] { "not.loaded" }),
            Spec(1, "b"),
        };
        Assert.True(OrderSorter.TryOrder(specs, higherPriorityFirst: true, out var indices, out var cycle, out _));
        Assert.Null(cycle);
        Assert.Equal(new[] { 0, 1 }, indices);
    }

    [Fact]
    public void Before_after_cycle_returns_false_with_path()
    {
        var specs = new[]
        {
            Spec(0, "a", after: new[] { "b" }),
            Spec(1, "b", after: new[] { "a" }),
        };
        Assert.False(OrderSorter.TryOrder(specs, higherPriorityFirst: true, out var indices, out var cycle, out var closing));
        Assert.Empty(indices);
        Assert.Equal(new[] { "a", "b", "a" }, cycle);
        Assert.Equal("b", closing);
    }

    [Fact]
    public void Self_before_is_a_cycle()
    {
        var specs = new[] { Spec(0, "a", before: new[] { "a" }) };
        Assert.False(OrderSorter.TryOrder(specs, higherPriorityFirst: true, out var indices, out var cycle, out var closing));
        Assert.Empty(indices);
        Assert.Equal(new[] { "a", "a" }, cycle);
        Assert.Equal("a", closing);
    }

    [Fact]
    public void TryAccept_self_before_drops_the_candidate()
    {
        var candidate = Spec(0, "a", before: new[] { "a" });
        Assert.False(OrderSorter.TryAccept(Array.Empty<OrderSpec>(), candidate, higherPriorityFirst: true, out var indices, out var dropped, out var cycle));
        Assert.Empty(indices);
        Assert.Equal("a", dropped);
        Assert.Equal(new[] { "a", "a" }, cycle);
    }

    [Fact]
    public void TryAccept_drops_only_the_candidate_on_cycle()
    {
        var existing = new[] { Spec(0, "a", priority: 0, after: new[] { "b" }) };
        var candidate = Spec(1, "b", priority: 100, after: new[] { "a" });
        Assert.False(OrderSorter.TryAccept(existing, candidate, higherPriorityFirst: true, out var indices, out var dropped, out var cycle));
        Assert.Empty(indices);
        Assert.Equal("b", dropped);
        Assert.Equal(new[] { "a", "b", "a" }, cycle);
        Assert.True(OrderSorter.TryOrder(existing, higherPriorityFirst: true, out var kept, out var keptCycle, out _));
        Assert.Null(keptCycle);
        Assert.Equal(new[] { 0 }, kept);
    }

    [Fact]
    public void TryOrder_closing_plugin_does_not_depend_on_list_order()
    {
        var ab = new[]
        {
            Spec(0, "a", after: new[] { "b" }),
            Spec(1, "b", after: new[] { "a" }),
        };
        var ba = new[]
        {
            Spec(1, "b", after: new[] { "a" }),
            Spec(0, "a", after: new[] { "b" }),
        };
        Assert.False(OrderSorter.TryOrder(ab, higherPriorityFirst: true, out _, out var cycleAb, out var closeAb));
        Assert.False(OrderSorter.TryOrder(ba, higherPriorityFirst: true, out _, out var cycleBa, out var closeBa));
        Assert.Equal("b", closeAb);
        Assert.Equal("b", closeBa);
        Assert.Equal(new[] { "a", "b", "a" }, cycleAb);
        Assert.Equal(new[] { "a", "b", "a" }, cycleBa);
    }

    [Fact]
    public void TryAccept_three_cycle_drops_the_candidate_not_an_accepted_dfs_node()
    {
        var existing = new[]
        {
            Spec(0, "a", after: new[] { "b" }),
            Spec(1, "b", after: new[] { "c" }),
        };
        var candidate = Spec(2, "c", after: new[] { "a" });
        Assert.False(OrderSorter.TryAccept(existing, candidate, higherPriorityFirst: true, out _, out var dropped, out var cycle));
        Assert.Equal("c", dropped);
        Assert.Equal(new[] { "a", "c", "b", "a" }, cycle);
        Assert.True(OrderSorter.TryOrder(existing, higherPriorityFirst: true, out var kept, out var keptCycle, out _));
        Assert.Null(keptCycle);
        // A's after-B survives; B's after-C is missing, so B then A.
        Assert.Equal(new[] { 1, 0 }, kept);
    }

    [Fact]
    public void TryAccept_keeps_acyclic_edges_when_a_closer_is_dropped()
    {
        // A↔B would close; C after D must still apply. B is the candidate (last).
        var existing = new[]
        {
            Spec(0, "a", priority: 0, after: new[] { "b" }),
            Spec(1, "c", priority: 0, after: new[] { "d" }),
            Spec(2, "d", priority: 0),
        };
        var candidate = Spec(3, "b", priority: 100, after: new[] { "a" });
        Assert.False(OrderSorter.TryAccept(existing, candidate, higherPriorityFirst: true, out _, out var dropped, out var cycle));
        Assert.Equal("b", dropped);
        Assert.Equal(new[] { "a", "b", "a" }, cycle);

        Assert.True(OrderSorter.TryOrder(existing, higherPriorityFirst: true, out var kept, out var keptCycle, out _));
        Assert.Null(keptCycle);
        // D before C; A's after-B is missing so A stays first by load/index.
        Assert.Equal(new[] { 0, 2, 1 }, kept);
    }

    [Fact]
    public void ResolveDroppingCloser_omits_the_last_spec_on_cycle()
    {
        var specs = new[]
        {
            Spec(0, "a", priority: 0, after: new[] { "b" }),
            Spec(1, "b", priority: 100, after: new[] { "a" }),
        };
        var indices = OrderSorter.ResolveDroppingCloser(specs, higherPriorityFirst: true, out var dropped, out var cycle);
        Assert.Equal("b", dropped);
        Assert.Equal(new[] { "a", "b", "a" }, cycle);
        Assert.Equal(new[] { 0 }, indices);
    }

    [Fact]
    public void Ids_are_compared_case_insensitively()
    {
        var specs = new[]
        {
            Spec(0, "High.Id", priority: 0, after: new[] { "LOW.id" }),
            Spec(1, "low.id", priority: 100),
        };
        Assert.True(OrderSorter.TryOrder(specs, higherPriorityFirst: true, out var indices, out var cycle, out _));
        Assert.Null(cycle);
        Assert.Equal(new[] { 1, 0 }, indices);
    }

    [Fact]
    public void Empty_plugin_id_cannot_be_targeted()
    {
        var specs = new[]
        {
            Spec(0, "", priority: 0),
            Spec(1, "named", priority: 100, after: new[] { "" }),
        };
        Assert.True(OrderSorter.TryOrder(specs, higherPriorityFirst: true, out var indices, out var cycle, out _));
        Assert.Null(cycle);
        // No edge from the empty id, so higher priority (named) still first.
        Assert.Equal(new[] { 1, 0 }, indices);
    }

    [Fact]
    public void CycleDroppedLog_is_stable_for_operators()
    {
        Assert.Equal(
            "EventBus: refused before/after cycle on CycleEvent from b.id: a.id -> b.id -> a.id; dropping that registration",
            OrderSorter.CycleDroppedLog("EventBus", "CycleEvent", "b.id", new[] { "a.id", "b.id", "a.id" }));
        Assert.Equal(
            "SystemHooks: refused before/after cycle on MoveEntitySystem Before from b.id: a.id -> b.id -> a.id; dropping that registration",
            OrderSorter.CycleDroppedLog("SystemHooks", "MoveEntitySystem Before", "b.id", new[] { "a.id", "b.id", "a.id" }));
    }

    [Fact]
    public void AssignLoadOrder_is_sticky_for_a_plugin_id()
    {
        string a = "load." + Guid.NewGuid().ToString("N");
        string b = "load." + Guid.NewGuid().ToString("N");
        int a1 = OrderSorter.AssignLoadOrder(a);
        int b1 = OrderSorter.AssignLoadOrder(b);
        int a2 = OrderSorter.AssignLoadOrder(a);
        Assert.Equal(a1, a2);
        Assert.True(a1 < b1);
    }

    [Fact]
    public void Equal_priority_survives_a_higher_registration_index_when_load_order_is_earlier()
    {
        // Plugin A re-registered (index 5) still precedes B (index 1) because A loaded first.
        var specs = new[]
        {
            Spec(5, "a", priority: 0, loadOrder: 0),
            Spec(1, "b", priority: 0, loadOrder: 1),
        };
        Assert.True(OrderSorter.TryOrder(specs, higherPriorityFirst: true, out var indices, out var cycle, out _));
        Assert.Null(cycle);
        Assert.Equal(new[] { 0, 1 }, indices);
    }

    [Fact]
    public void FormatDescribe_empty_is_none()
    {
        Assert.Equal("EventBus UnusedEvent: (none)", OrderSorter.FormatDescribe("EventBus", "UnusedEvent", Array.Empty<OrderSpec>()));
    }

    [Fact]
    public void FormatDescribe_lists_plugin_prio_and_constraints()
    {
        var specs = new[]
        {
            Spec(0, "mod.b", priority: 0),
            Spec(1, "mod.a", priority: 100, after: new[] { "mod.b" }),
        };
        string text = OrderSorter.FormatDescribe("EventBus", "ChatMessage", specs);
        Assert.Equal(
            "EventBus ChatMessage:\n  1. mod.b prio=0\n  2. mod.a prio=100 after=[mod.b]",
            text);
    }

    [Fact]
    public void Matches_PrefabEditOrder_when_lower_priority_first()
    {
        var prefabs = new[]
        {
            new PrefabEditSpec(0, 42, "a", "A", 100, Array.Empty<string>(), new[] { "b" }, 0),
            new PrefabEditSpec(1, 42, "b", "B", 0, Array.Empty<string>(), Array.Empty<string>(), 1),
        };
        Assert.True(PrefabEditOrder.TryOrder(prefabs, out var expected, out var prefabCycle));
        Assert.Null(prefabCycle);

        var specs = new[]
        {
            Spec(0, "a", priority: 100, after: new[] { "b" }, loadOrder: 0),
            Spec(1, "b", priority: 0, loadOrder: 1),
        };
        Assert.True(OrderSorter.TryOrder(specs, higherPriorityFirst: false, out var actual, out var cycle, out _));
        Assert.Null(cycle);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Matches_PrefabEditOrder_cycle_path()
    {
        var prefabs = new[]
        {
            new PrefabEditSpec(0, 42, "a", "A", 0, Array.Empty<string>(), new[] { "b" }, 0),
            new PrefabEditSpec(1, 42, "b", "B", 0, Array.Empty<string>(), new[] { "a" }, 1),
        };
        Assert.False(PrefabEditOrder.TryOrder(prefabs, out _, out var prefabLog));

        var specs = new[]
        {
            Spec(0, "a", after: new[] { "b" }, loadOrder: 0),
            Spec(1, "b", after: new[] { "a" }, loadOrder: 1),
        };
        Assert.False(OrderSorter.TryOrder(specs, higherPriorityFirst: false, out _, out var cycle, out var closing));
        Assert.Equal("b", closing);
        Assert.Equal(new[] { "a", "b", "a" }, cycle);
        Assert.Contains("a -> b -> a", prefabLog, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyPermutation_reorders_in_place()
    {
        var list = new List<string> { "a", "b", "c" };
        OrderSorter.ApplyPermutation(list, new[] { 2, 0, 1 });
        Assert.Equal(new[] { "c", "a", "b" }, list);
    }

    private static OrderSpec Spec(
        int index,
        string pluginId,
        int priority = 0,
        string[]? before = null,
        string[]? after = null,
        int loadOrder = 0) =>
        new(index, pluginId, priority, before, after, loadOrder);
}
