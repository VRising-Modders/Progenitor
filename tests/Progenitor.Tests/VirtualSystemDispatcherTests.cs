using System;
using System.Collections.Generic;
using Progenitor.Systems;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Game-free coverage of virtual-system ordering, isolation, replace, Stop, and RemoveAll.
/// Each test uses its own dispatcher instance so parallel runs do not share slots.
/// </summary>
[Collection("PluginLogger")]
public class VirtualSystemDispatcherTests
{
    [Fact]
    public void Dispatch_runs_slots_in_ascending_order_then_registration()
    {
        var d = new VirtualSystemDispatcher();
        var owner = new object();
        var order = new List<string>();

        using var mid = d.Add("mid", "a", _ => order.Add("mid"), owner, order: 0);
        using var last = d.Add("last", "a", _ => order.Add("last"), owner, order: 10);
        using var first = d.Add("first", "a", _ => order.Add("first"), owner, order: -5);

        d.Dispatch("a");

        Assert.Equal(new[] { "first", "mid", "last" }, order);
    }

    [Fact]
    public void Same_order_keeps_registration_sequence()
    {
        var d = new VirtualSystemDispatcher();
        var owner = new object();
        var order = new List<int>();

        using var a = d.Add("a", "k", _ => order.Add(1), owner, order: 7);
        using var b = d.Add("b", "k", _ => order.Add(2), owner, order: 7);
        using var c = d.Add("c", "k", _ => order.Add(3), owner, order: 7);

        d.Dispatch("k");

        Assert.Equal(new[] { 1, 2, 3 }, order);
    }

    [Fact]
    public void Different_anchors_do_not_mix()
    {
        var d = new VirtualSystemDispatcher();
        var owner = new object();
        var hits = new List<string>();

        using var a = d.Add("a", "one", _ => hits.Add("one"), owner);
        using var b = d.Add("b", "two", _ => hits.Add("two"), owner);

        d.Dispatch("one");
        Assert.Equal(new[] { "one" }, hits);

        d.Dispatch("two");
        Assert.Equal(new[] { "one", "two" }, hits);
        Assert.Equal(0, d.CountFor("missing"));
    }

    [Fact]
    public void Dispose_unregisters_only_that_slot()
    {
        var d = new VirtualSystemDispatcher();
        var owner = new object();
        var hits = 0;

        var keep = d.Add("keep", "k", _ => hits += 1, owner);
        var drop = d.Add("drop", "k", _ => hits += 10, owner);
        drop.Dispose();

        d.Dispatch("k");
        keep.Dispose();

        Assert.Equal(1, hits);
        Assert.Equal(0, d.Count);
    }

    [Fact]
    public void RemoveAll_drops_every_slot_for_that_owner()
    {
        var d = new VirtualSystemDispatcher();
        var owner = new object();
        var other = new object();
        var hits = 0;

        d.Add("a", "k", _ => hits += 1, owner);
        d.Add("b", "k", _ => hits += 1, owner);
        using var kept = d.Add("c", "k", _ => hits += 10, other);

        Assert.Equal(3, d.Count);
        Assert.Equal(2, d.RemoveAll(owner));
        Assert.Equal(1, d.Count);

        d.Dispatch("k");
        Assert.Equal(10, hits);
        Assert.Equal(0, d.RemoveAll(owner));
    }

    [Fact]
    public void Duplicate_name_from_same_owner_replaces()
    {
        var d = new VirtualSystemDispatcher();
        var owner = new object();
        var hits = 0;

        using var first = d.Add("dup", "k", _ => hits += 1, owner, order: 5);
        using var second = d.Add("dup", "k", _ => hits += 10, owner, order: 1);

        Assert.Equal(1, d.Count);
        d.Dispatch("k");
        Assert.Equal(10, hits);

        first.Dispose();
        d.Dispatch("k");
        Assert.Equal(20, hits);
    }

    [Fact]
    public void Same_name_from_different_owners_both_run()
    {
        var d = new VirtualSystemDispatcher();
        var hits = 0;
        using var a = d.Add("shared", "k", _ => hits += 1, new object());
        using var b = d.Add("shared", "k", _ => hits += 10, new object());

        d.Dispatch("k");
        Assert.Equal(11, hits);
        Assert.Equal(2, d.Count);
    }

    [Fact]
    public void Throwing_slot_is_logged_and_does_not_stop_the_others()
    {
        var log = new Progenitor.TestLogSource();
        Progenitor.Plugin.Logger = log;
        var d = new VirtualSystemDispatcher();
        var owner = new object();
        var laterRan = false;
        try
        {
            using var boom = d.Add("boom", "k", _ => throw new InvalidOperationException("boom"), owner, order: -1);
            using var later = d.Add("later", "k", _ => laterRan = true, owner, order: 0);

            d.Dispatch("k");

            Assert.True(laterRan);
            Assert.Contains(log.Errors, m => m.Contains("boom", StringComparison.Ordinal) && m.Contains("VirtualSystems", StringComparison.Ordinal));

            var slots = d.SnapshotSlots();
            VirtualSystemSlot? boomSlot = null;
            for (int i = 0; i < slots.Length; i++)
                if (slots[i].Name == "boom") boomSlot = slots[i];
            Assert.NotNull(boomSlot);
            Assert.Equal(1, boomSlot!.Frame);
            Assert.Contains("InvalidOperationException", boomSlot.LastError, StringComparison.Ordinal);
            Assert.True(boomSlot.LastDuration >= TimeSpan.Zero);
        }
        finally
        {
            Progenitor.Plugin.Logger = null;
        }
    }

    [Fact]
    public void Successful_invoke_clears_LastError()
    {
        var d = new VirtualSystemDispatcher();
        var owner = new object();
        int n = 0;
        using var slotHandle = d.Add("flip", "k", _ =>
        {
            n++;
            if (n == 1) throw new InvalidOperationException("once");
        }, owner);

        d.Dispatch("k");
        var slot = d.SnapshotSlots()[0];
        Assert.Contains("once", slot.LastError, StringComparison.Ordinal);

        d.Dispatch("k");
        Assert.Null(slot.LastError);
        Assert.Equal(2, slot.Frame);
    }

    [Fact]
    public void Stop_from_inside_unregisters_before_next_dispatch()
    {
        var d = new VirtualSystemDispatcher();
        var owner = new object();
        var hits = 0;
        using var handle = d.Add("once", "k", s =>
        {
            hits++;
            s.Stop();
        }, owner);

        d.Dispatch("k");
        d.Dispatch("k");

        Assert.Equal(1, hits);
        Assert.Equal(0, d.Count);
    }

    [Fact]
    public void Snapshot_keeps_later_slots_when_an_earlier_one_RemoveAlls()
    {
        var d = new VirtualSystemDispatcher();
        var owner = new object();
        var laterRan = false;

        d.Add("first", "k", _ => d.RemoveAll(owner), owner, order: 0);
        d.Add("later", "k", _ => laterRan = true, owner, order: 1);

        d.Dispatch("k");

        Assert.True(laterRan);
        Assert.Equal(0, d.Count);
    }

    [Fact]
    public void Frame_increments_including_on_throw()
    {
        var d = new VirtualSystemDispatcher();
        var owner = new object();
        using var handle = d.Add("n", "k", s =>
        {
            if (s.Frame == 2) throw new InvalidOperationException("second");
        }, owner);

        d.Dispatch("k");
        d.Dispatch("k");
        d.Dispatch("k");

        var slot = d.SnapshotSlots()[0];
        Assert.Equal(3, slot.Frame);
        Assert.Null(slot.LastError);
    }

    [Fact]
    public void Nested_Dispatch_of_the_same_key_is_refused()
    {
        var log = new Progenitor.TestLogSource();
        Progenitor.Plugin.Logger = log;
        var d = new VirtualSystemDispatcher();
        var owner = new object();
        var hits = 0;
        try
        {
            using var sub = d.Add("nest", "k", _ =>
            {
                hits++;
                d.Dispatch("k");
            }, owner);

            d.Dispatch("k");

            Assert.Equal(1, hits);
            Assert.Contains(log.Warnings, m => m.Contains("k", StringComparison.Ordinal));
        }
        finally
        {
            Progenitor.Plugin.Logger = null;
        }
    }

    [Fact]
    public void Nested_Dispatch_of_a_different_key_is_delivered()
    {
        var d = new VirtualSystemDispatcher();
        var owner = new object();
        var sawInner = false;
        using var outer = d.Add("outer", "a", _ => d.Dispatch("b"), owner);
        using var inner = d.Add("inner", "b", _ => sawInner = true, owner);

        d.Dispatch("a");

        Assert.True(sawInner);
    }

    [Fact]
    public void Nested_Dispatch_of_a_different_key_does_not_skip_later_outer_slots()
    {
        var d = new VirtualSystemDispatcher();
        var owner = new object();
        var order = new List<string>();
        using var first = d.Add("first", "a", _ =>
        {
            order.Add("first");
            d.Dispatch("b");
        }, owner, order: 0);
        using var later = d.Add("later", "a", _ => order.Add("later"), owner, order: 1);
        using var inner = d.Add("inner", "b", _ => order.Add("inner"), owner);

        d.Dispatch("a");

        Assert.Equal(new[] { "first", "inner", "later" }, order);
    }

    [Fact]
    public void Dispatch_of_unknown_anchor_is_a_noop()
    {
        var d = new VirtualSystemDispatcher();
        d.Dispatch("nobody");
        Assert.Equal(0, d.Count);
        Assert.Equal("count=0", d.Describe());
    }

    [Fact]
    public void Describe_lists_name_anchor_order_and_frames()
    {
        var d = new VirtualSystemDispatcher();
        var owner = new object();
        using var handle = d.Add("sample.users", "default:after-sim", _ => { }, owner, order: 3, anchorDisplay: "After SimulationSystemGroup (Server)");

        d.Dispatch("default:after-sim");
        var text = d.Describe();

        Assert.Contains("count=1", text, StringComparison.Ordinal);
        Assert.Contains("sample.users", text, StringComparison.Ordinal);
        Assert.Contains("After SimulationSystemGroup (Server)", text, StringComparison.Ordinal);
        Assert.Contains("order=3", text, StringComparison.Ordinal);
        Assert.Contains("frames=1", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Add_and_Remove_publish_a_new_snapshot_Dispatch_does_not()
    {
        var d = new VirtualSystemDispatcher();
        var owner = new object();
        var a = d.Add("a", "k", _ => { }, owner);

        var s1 = d.SnapshotSlots();
        Assert.Single(s1);
        Assert.Same(s1, d.SnapshotSlots());

        d.Dispatch("k");
        Assert.Same(s1, d.SnapshotSlots());

        var b = d.Add("b", "k", _ => { }, owner);
        var s2 = d.SnapshotSlots();
        Assert.NotSame(s1, s2);
        Assert.Equal(2, s2.Length);

        a.Dispose();
        var s3 = d.SnapshotSlots();
        Assert.NotSame(s2, s3);
        Assert.Single(s3);
        Assert.Equal(1, d.CountFor("k"));

        b.Dispose();
        Assert.Empty(d.SnapshotSlots());
        Assert.Equal(0, d.CountFor("k"));
        Assert.Equal(0, d.Count);
    }

    [Fact]
    public void Dispatch_does_not_time_every_invoke()
    {
        var d = new VirtualSystemDispatcher();
        var owner = new object();
        using var handle = d.Add("n", "k", _ => { }, owner);

        d.Dispatch("k");
        var slot = d.SnapshotSlots()[0];
        Assert.Equal(1, slot.Frame);
        Assert.Equal(0, slot.LastTimedFrame);

        int remaining = VirtualSystemDispatcher.TimeEveryFrames - 1;
        for (int i = 0; i < remaining; i++)
            d.Dispatch("k");

        Assert.Equal(VirtualSystemDispatcher.TimeEveryFrames, slot.Frame);
        Assert.Equal(VirtualSystemDispatcher.TimeEveryFrames, slot.LastTimedFrame);

        d.Dispatch("k");
        Assert.Equal(VirtualSystemDispatcher.TimeEveryFrames + 1, slot.Frame);
        Assert.Equal(VirtualSystemDispatcher.TimeEveryFrames, slot.LastTimedFrame);
    }

    [Fact]
    public void Describe_times_the_next_dispatch_only()
    {
        var d = new VirtualSystemDispatcher();
        var owner = new object();
        using var handle = d.Add("n", "k", _ => { }, owner);

        d.Dispatch("k");
        var slot = d.SnapshotSlots()[0];
        Assert.Equal(0, slot.LastTimedFrame);

        d.Describe();
        d.Dispatch("k");
        Assert.Equal(2, slot.LastTimedFrame);

        d.Dispatch("k");
        Assert.Equal(2, slot.LastTimedFrame);
        Assert.Equal(3, slot.Frame);
    }

    [Fact]
    public void Add_rejects_null_or_blank_arguments()
    {
        var d = new VirtualSystemDispatcher();
        var owner = new object();
        Action<VirtualSystemSlot> nop = _ => { };

        Assert.Throws<ArgumentException>(() => d.Add("", "k", nop, owner));
        Assert.Throws<ArgumentException>(() => d.Add("  ", "k", nop, owner));
        Assert.Throws<ArgumentException>(() => d.Add("n", "", nop, owner));
        Assert.Throws<ArgumentNullException>(() => d.Add("n", "k", null!, owner));
        Assert.Throws<ArgumentNullException>(() => d.Add("n", "k", nop, null!));
        Assert.Throws<ArgumentNullException>(() => d.RemoveAll(null!));
    }
}
