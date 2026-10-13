using System;
using System.Collections.Generic;
using Progenitor.Events;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// EventBus is a static process-wide table. Each test uses its own event type so parallel runs
/// do not share subscribers. Shares <c>PluginLogger</c> with ModStoreTests because both assign
/// the process-wide <see cref="Plugin.Logger"/> shim.
/// </summary>
[Collection("PluginLogger")]
public class EventBusTests
{
    [Fact]
    public void Publish_runs_handlers_in_descending_priority()
    {
        var order = new List<string>();
        using var low = EventBus.On<OrderEvent>(e => order.Add("low"), priority: 0);
        using var high = EventBus.On<OrderEvent>(e => order.Add("high"), priority: 100);
        using var mid = EventBus.On<OrderEvent>(e => order.Add("mid"), priority: 50);

        EventBus.Publish(new OrderEvent());

        Assert.Equal(new[] { "high", "mid", "low" }, order);
    }

    [Fact]
    public void Same_priority_keeps_registration_order()
    {
        var order = new List<int>();
        using var first = EventBus.On<SamePriorityEvent>(_ => order.Add(1), priority: 10);
        using var second = EventBus.On<SamePriorityEvent>(_ => order.Add(2), priority: 10);

        EventBus.Publish(new SamePriorityEvent());

        Assert.Equal(new[] { 1, 2 }, order);
    }

    [Fact]
    public void Cancel_is_visible_to_later_handlers_but_does_not_skip_them()
    {
        var sawCancelled = false;
        using var first = EventBus.On<CancelEvent>(e => e.Cancel(), priority: 20);
        using var second = EventBus.On<CancelEvent>(e => sawCancelled = e.IsCancelled, priority: 10);

        var published = EventBus.Publish(new CancelEvent());

        Assert.True(published.IsCancelled);
        Assert.True(sawCancelled);
    }

    [Fact]
    public void Dispose_unsubscribes_only_that_handler()
    {
        var hits = 0;
        var keep = EventBus.On<DisposeEvent>(_ => hits += 1);
        var drop = EventBus.On<DisposeEvent>(_ => hits += 10);
        drop.Dispose();

        EventBus.Publish(new DisposeEvent());
        keep.Dispose();

        Assert.Equal(1, hits);
        Assert.Equal(0, EventBus.SubscriberCount<DisposeEvent>());
    }

    [Fact]
    public void RemoveAll_drops_every_subscription_for_that_owner()
    {
        var owner = new object();
        var other = new object();
        var hits = 0;

        EventBus.On<OwnerEvent>(_ => hits += 1, owner: owner);
        EventBus.On<OwnerEvent>(_ => hits += 1, owner: owner);
        using var kept = EventBus.On<OwnerEvent>(_ => hits += 10, owner: other);

        Assert.Equal(3, EventBus.SubscriberCount<OwnerEvent>());
        Assert.Equal(2, EventBus.RemoveAll(owner));
        Assert.Equal(1, EventBus.SubscriberCount<OwnerEvent>());

        EventBus.Publish(new OwnerEvent());
        Assert.Equal(10, hits);
        Assert.Equal(0, EventBus.RemoveAll(owner));
    }

    [Fact]
    public void RemoveDeclaredIn_drops_handlers_from_that_assembly()
    {
        var hits = 0;
        EventBus.On<DeclaredInEvent>(_ => hits += 1, owner: null);
        EventBus.On<DeclaredInEvent>(_ => hits += 10, owner: new object());
        Assert.Equal(2, EventBus.SubscriberCount<DeclaredInEvent>());
        Assert.Equal(2, EventBus.RemoveDeclaredIn(typeof(EventBusTests).Assembly));
        Assert.Equal(0, EventBus.SubscriberCount<DeclaredInEvent>());
        EventBus.Publish(new DeclaredInEvent());
        Assert.Equal(0, hits);
    }

    [Fact]
    public void A_throwing_handler_is_logged_and_does_not_stop_the_others()
    {
        var log = new Progenitor.TestLogSource();
        Progenitor.Plugin.Logger = log;
        var laterRan = false;
        try
        {
            using var boom = EventBus.On<ThrowEvent>(_ => throw new InvalidOperationException("boom"), priority: 20);
            using var later = EventBus.On<ThrowEvent>(_ => laterRan = true, priority: 10);

            var returned = EventBus.Publish(new ThrowEvent());

            Assert.NotNull(returned);
            Assert.True(laterRan);
            Assert.Contains(log.Errors, m => m.Contains("ThrowEvent", StringComparison.Ordinal) && m.Contains("boom", StringComparison.Ordinal));
        }
        finally
        {
            Progenitor.Plugin.Logger = null;
        }
    }

    [Fact]
    public void Publish_with_no_subscribers_returns_the_same_instance()
    {
        var ev = new UnusedEvent();
        Assert.Same(ev, EventBus.Publish(ev));
        Assert.Equal(0, EventBus.SubscriberCount<UnusedEvent>());
    }

    [Fact]
    public void Nested_Publish_of_the_same_type_is_refused_and_outer_handlers_still_run()
    {
        var log = new Progenitor.TestLogSource();
        Progenitor.Plugin.Logger = log;
        var order = new List<string>();
        try
        {
            using var first = EventBus.On<NestedSameEvent>(e =>
            {
                order.Add("first");
                EventBus.Publish(new NestedSameEvent());
                order.Add("after-nested");
            }, priority: 20);
            using var second = EventBus.On<NestedSameEvent>(_ => order.Add("second"), priority: 10);

            EventBus.Publish(new NestedSameEvent());

            Assert.Equal(new[] { "first", "after-nested", "second" }, order);
            Assert.Contains(log.Warnings, m => m.Contains("NestedSameEvent", StringComparison.Ordinal));
        }
        finally
        {
            Progenitor.Plugin.Logger = null;
        }
    }

    [Fact]
    public void Describe_lists_counts_by_type_after_subscribe()
    {
        using var a = EventBus.On<DescribeAEvent>(_ => { });
        using var b1 = EventBus.On<DescribeBEvent>(_ => { });
        using var b2 = EventBus.On<DescribeBEvent>(_ => { });
        Assert.Equal(1, EventBus.SubscriberCount<DescribeAEvent>());
        Assert.Equal(2, EventBus.SubscriberCount<DescribeBEvent>());
        Assert.True(EventBus.TotalSubscribers() >= 3);
        string text = EventBus.Describe();
        Assert.Contains("DescribeAEvent=1", text, StringComparison.Ordinal);
        Assert.Contains("DescribeBEvent=2", text, StringComparison.Ordinal);
        Assert.Contains("subscribers=", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Nested_Publish_of_a_different_type_is_delivered()
    {
        var sawInner = false;
        using var outer = EventBus.On<NestedOuterEvent>(_ => EventBus.Publish(new NestedInnerEvent()));
        using var inner = EventBus.On<NestedInnerEvent>(_ => sawInner = true);

        EventBus.Publish(new NestedOuterEvent());

        Assert.True(sawInner);
    }

    [Fact]
    public void Sequential_Publish_of_the_same_type_is_delivered()
    {
        var hits = 0;
        using var sub = EventBus.On<SequentialEvent>(_ => hits++);

        EventBus.Publish(new SequentialEvent());
        EventBus.Publish(new SequentialEvent());

        Assert.Equal(2, hits);
    }

    [Fact]
    public void Before_after_overrides_numeric_priority()
    {
        var order = new List<string>();
        using var high = EventBus.On<BeforeAfterEvent>(_ => order.Add("high"), priority: 100, pluginId: "high.id", after: new[] { "low.id" });
        using var low = EventBus.On<BeforeAfterEvent>(_ => order.Add("low"), priority: 0, pluginId: "low.id");

        EventBus.Publish(new BeforeAfterEvent());

        Assert.Equal(new[] { "low", "high" }, order);
        string dump = EventBus.DescribeOrder<BeforeAfterEvent>();
        Assert.Contains("low.id prio=0", dump, StringComparison.Ordinal);
        Assert.Contains("high.id prio=100 after=[low.id]", dump, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_before_after_id_is_ignored_and_priority_order_kept()
    {
        var order = new List<string>();
        using var first = EventBus.On<MissingIdEvent>(_ => order.Add("first"), priority: 0, pluginId: "missing.first", after: new[] { "not.loaded" });
        using var second = EventBus.On<MissingIdEvent>(_ => order.Add("second"), priority: 10, pluginId: "missing.second");

        EventBus.Publish(new MissingIdEvent());

        Assert.Equal(new[] { "second", "first" }, order);
    }

    [Fact]
    public void Cycle_drops_the_closer_and_logs_once()
    {
        var log = new Progenitor.TestLogSource();
        Progenitor.Plugin.Logger = log;
        var order = new List<string>();
        try
        {
            using var a = EventBus.On<CycleEvent>(_ => order.Add("a"), priority: 0, pluginId: "cycle.a", after: new[] { "cycle.b" });
            using var b = EventBus.On<CycleEvent>(_ => order.Add("b"), priority: 100, pluginId: "cycle.b", after: new[] { "cycle.a" });

            EventBus.Publish(new CycleEvent());

            Assert.Equal(new[] { "a" }, order);
            Assert.Equal(1, EventBus.SubscriberCount<CycleEvent>());
            Assert.Equal(
                new[] { "EventBus: refused before/after cycle on CycleEvent from cycle.b: cycle.a -> cycle.b -> cycle.a; dropping that registration" },
                log.Warnings);
        }
        finally
        {
            Progenitor.Plugin.Logger = null;
        }
    }

    [Fact]
    public void Three_cycle_drops_the_candidate_same_plugin_as_Content()
    {
        var log = new Progenitor.TestLogSource();
        Progenitor.Plugin.Logger = log;
        var order = new List<string>();
        try
        {
            using var a = EventBus.On<ThreeCycleEvent>(_ => order.Add("a"), pluginId: "tc.a", after: new[] { "tc.b" });
            using var b = EventBus.On<ThreeCycleEvent>(_ => order.Add("b"), pluginId: "tc.b", after: new[] { "tc.c" });
            using var c = EventBus.On<ThreeCycleEvent>(_ => order.Add("c"), pluginId: "tc.c", after: new[] { "tc.a" });

            EventBus.Publish(new ThreeCycleEvent());

            // C dropped; A's after-B survives, so B then A.
            Assert.Equal(new[] { "b", "a" }, order);
            Assert.Equal(2, EventBus.SubscriberCount<ThreeCycleEvent>());
            Assert.Equal(
                new[] { "EventBus: refused before/after cycle on ThreeCycleEvent from tc.c: tc.a -> tc.c -> tc.b -> tc.a; dropping that registration" },
                log.Warnings);
        }
        finally
        {
            Progenitor.Plugin.Logger = null;
        }
    }

    [Fact]
    public void Cycle_keeps_acyclic_edges_on_the_same_event()
    {
        var log = new Progenitor.TestLogSource();
        Progenitor.Plugin.Logger = log;
        var order = new List<string>();
        try
        {
            using var a = EventBus.On<CycleExtraEvent>(_ => order.Add("a"), pluginId: "cx.a", after: new[] { "cx.b" });
            using var c = EventBus.On<CycleExtraEvent>(_ => order.Add("c"), pluginId: "cx.c", after: new[] { "cx.d" });
            using var d = EventBus.On<CycleExtraEvent>(_ => order.Add("d"), pluginId: "cx.d");
            using var b = EventBus.On<CycleExtraEvent>(_ => order.Add("b"), priority: 100, pluginId: "cx.b", after: new[] { "cx.a" });

            EventBus.Publish(new CycleExtraEvent());

            Assert.Equal(new[] { "a", "d", "c" }, order);
            Assert.Equal(3, EventBus.SubscriberCount<CycleExtraEvent>());
            Assert.Equal(
                new[] { "EventBus: refused before/after cycle on CycleExtraEvent from cx.b: cx.a -> cx.b -> cx.a; dropping that registration" },
                log.Warnings);
        }
        finally
        {
            Progenitor.Plugin.Logger = null;
        }
    }

    [Fact]
    public void Unload_reresolves_remaining_handlers()
    {
        var order = new List<string>();
        var a = EventBus.On<UnloadOrderEvent>(_ => order.Add("a"), priority: 0, pluginId: "unload.a", after: new[] { "unload.b" });
        var b = EventBus.On<UnloadOrderEvent>(_ => order.Add("b"), priority: 0, pluginId: "unload.b");
        try
        {
            EventBus.Publish(new UnloadOrderEvent());
            Assert.Equal(new[] { "b", "a" }, order);

            order.Clear();
            b.Dispose();
            EventBus.Publish(new UnloadOrderEvent());
            Assert.Equal(new[] { "a" }, order);
            Assert.Equal(1, EventBus.SubscriberCount<UnloadOrderEvent>());
        }
        finally
        {
            a.Dispose();
            b.Dispose();
        }
    }

    [Fact]
    public void Unload_of_the_named_plugin_clears_its_before_after_edge()
    {
        var order = new List<string>();
        var a = EventBus.On<CycleUnloadEvent>(_ => order.Add("a"), priority: 0, pluginId: "cycu.a", after: new[] { "cycu.b" });
        var b = EventBus.On<CycleUnloadEvent>(_ => order.Add("b"), priority: 0, pluginId: "cycu.b");
        try
        {
            EventBus.Publish(new CycleUnloadEvent());
            Assert.Equal(new[] { "b", "a" }, order);

            order.Clear();
            b.Dispose();
            EventBus.Publish(new CycleUnloadEvent());
            Assert.Equal(new[] { "a" }, order);
            Assert.Equal(1, EventBus.SubscriberCount<CycleUnloadEvent>());
        }
        finally
        {
            a.Dispose();
            b.Dispose();
        }
    }

    [Fact]
    public void Equal_priority_named_plugins_keep_load_order_across_resubscribe()
    {
        string idA = "eq." + Guid.NewGuid().ToString("N");
        string idB = "eq." + Guid.NewGuid().ToString("N");
        var order = new List<string>();
        var a = EventBus.On<EqualReloadEvent>(_ => order.Add("a"), pluginId: idA);
        var b = EventBus.On<EqualReloadEvent>(_ => order.Add("b"), pluginId: idB);
        try
        {
            EventBus.Publish(new EqualReloadEvent());
            Assert.Equal(new[] { "a", "b" }, order);

            a.Dispose();
            a = EventBus.On<EqualReloadEvent>(_ => order.Add("a"), pluginId: idA);
            order.Clear();
            EventBus.Publish(new EqualReloadEvent());
            Assert.Equal(new[] { "a", "b" }, order);
        }
        finally
        {
            a.Dispose();
            b.Dispose();
        }
    }

    [Fact]
    public void RemoveAll_reresolves_only_event_types_that_lost_a_handler()
    {
        var owner = new object();
        var order = new List<string>();
        using var keptHigh = EventBus.On<RemoveAllKeptEvent>(_ => order.Add("high"), priority: 0, pluginId: "rm.high", after: new[] { "rm.low" });
        using var keptLow = EventBus.On<RemoveAllKeptEvent>(_ => order.Add("low"), priority: 100, pluginId: "rm.low");
        EventBus.On<RemoveAllDroppedEvent>(_ => { }, owner: owner, pluginId: "rm.drop");

        Assert.Equal(1, EventBus.RemoveAll(owner));
        Assert.Equal(0, EventBus.SubscriberCount<RemoveAllDroppedEvent>());
        Assert.Equal(2, EventBus.SubscriberCount<RemoveAllKeptEvent>());

        EventBus.Publish(new RemoveAllKeptEvent());
        Assert.Equal(new[] { "low", "high" }, order);
        string dump = EventBus.DescribeOrder<RemoveAllKeptEvent>();
        Assert.Contains("rm.low prio=100", dump, StringComparison.Ordinal);
        Assert.Contains("rm.high prio=0 after=[rm.low]", dump, StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeOrder_empty_is_none()
    {
        Assert.Equal("EventBus NeverSubscribedEvent: (none)", EventBus.DescribeOrder<NeverSubscribedEvent>());
    }

    [Fact]
    public void Nested_Publish_of_the_same_type_is_logged_once()
    {
        var log = new Progenitor.TestLogSource();
        Progenitor.Plugin.Logger = log;
        try
        {
            using var sub = EventBus.On<NestedLogOnceEvent>(_ =>
            {
                EventBus.Publish(new NestedLogOnceEvent());
                EventBus.Publish(new NestedLogOnceEvent());
            });

            EventBus.Publish(new NestedLogOnceEvent());
            EventBus.Publish(new NestedLogOnceEvent());

            Assert.Equal(1, CountWarningsContaining(log, "NestedLogOnceEvent"));
        }
        finally
        {
            Progenitor.Plugin.Logger = null;
        }
    }

    [Fact]
    public void Ping_pong_nested_Publish_of_an_outer_type_is_refused()
    {
        var log = new Progenitor.TestLogSource();
        Progenitor.Plugin.Logger = log;
        var hitsA = 0;
        var hitsB = 0;
        try
        {
            using var a = EventBus.On<PingPongA>(_ =>
            {
                hitsA++;
                EventBus.Publish(new PingPongB());
            });
            using var b = EventBus.On<PingPongB>(_ =>
            {
                hitsB++;
                EventBus.Publish(new PingPongA());
            });

            EventBus.Publish(new PingPongA());

            Assert.Equal(1, hitsA);
            Assert.Equal(1, hitsB);
            Assert.Contains(log.Warnings, m => m.Contains("PingPongA", StringComparison.Ordinal));
        }
        finally
        {
            Progenitor.Plugin.Logger = null;
        }
    }

    private static int CountWarningsContaining(Progenitor.TestLogSource log, string token)
    {
        int n = 0;
        foreach (var m in log.Warnings)
            if (m.Contains(token, StringComparison.Ordinal)) n++;
        return n;
    }

    private sealed class OrderEvent { }
    private sealed class SamePriorityEvent { }
    private sealed class DisposeEvent { }
    private sealed class OwnerEvent { }
    private sealed class DeclaredInEvent { }
    private sealed class ThrowEvent { }
    private sealed class UnusedEvent { }
    private sealed class NestedSameEvent { }
    private sealed class NestedOuterEvent { }
    private sealed class NestedInnerEvent { }
    private sealed class SequentialEvent { }
    private sealed class NestedLogOnceEvent { }
    private sealed class PingPongA { }
    private sealed class PingPongB { }
    private sealed class DescribeAEvent { }
    private sealed class DescribeBEvent { }
    private sealed class BeforeAfterEvent { }
    private sealed class MissingIdEvent { }
    private sealed class CycleEvent { }
    private sealed class ThreeCycleEvent { }
    private sealed class CycleExtraEvent { }
    private sealed class UnloadOrderEvent { }
    private sealed class CycleUnloadEvent { }
    private sealed class EqualReloadEvent { }
    private sealed class RemoveAllDroppedEvent { }
    private sealed class RemoveAllKeptEvent { }
    private sealed class NeverSubscribedEvent { }

    private sealed class CancelEvent : ICancellable
    {
        public bool IsCancelled { get; private set; }
        public void Cancel() => IsCancelled = true;
    }
}
