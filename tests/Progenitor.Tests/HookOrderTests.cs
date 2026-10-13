using System;
using Progenitor.Hooks;
using Progenitor.Plugins;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Game-free coverage of per-site system-hook ordering (the live prefix in SystemHooks
/// calls the same <see cref="HookOrder.Resolve"/>).
/// </summary>
public class HookOrderTests
{
    [Fact]
    public void Resolve_runs_higher_priority_first()
    {
        var specs = new[]
        {
            Spec(0, "low", 0),
            Spec(1, "high", 100),
            Spec(2, "mid", 50),
        };
        var indices = HookOrder.Resolve(specs, "MoveEntitySystem", "Before", out var cycleLog);
        Assert.Null(cycleLog);
        Assert.Equal(new[] { 1, 2, 0 }, indices);
    }

    [Fact]
    public void Before_after_overrides_priority()
    {
        var specs = new[]
        {
            Spec(0, "high", 100, after: new[] { "low" }),
            Spec(1, "low", 0),
        };
        var indices = HookOrder.Resolve(specs, "MoveEntitySystem", "After", out var cycleLog);
        Assert.Null(cycleLog);
        Assert.Equal(new[] { 1, 0 }, indices);
    }

    [Fact]
    public void Cycle_drops_the_closer_and_returns_one_log_line()
    {
        var specs = new[]
        {
            Spec(0, "a.id", 0, after: new[] { "b.id" }),
            Spec(1, "b.id", 100, after: new[] { "a.id" }),
        };
        var indices = HookOrder.Resolve(specs, "MoveEntitySystem", "Before", out var cycleLog);
        Assert.Equal(new[] { 0 }, indices);
        Assert.Equal(
            "SystemHooks: refused before/after cycle on MoveEntitySystem Before from b.id: a.id -> b.id -> a.id; dropping that registration",
            cycleLog);
    }

    [Fact]
    public void Three_cycle_names_the_candidate_same_as_Content()
    {
        var specs = new[]
        {
            Spec(0, "a.id", 0, after: new[] { "b.id" }),
            Spec(1, "b.id", 0, after: new[] { "c.id" }),
            Spec(2, "c.id", 0, after: new[] { "a.id" }),
        };
        var indices = HookOrder.Resolve(specs, "MoveEntitySystem", "Before", out var cycleLog);
        // C dropped; A's after-B survives, so B then A.
        Assert.Equal(new[] { 1, 0 }, indices);
        Assert.Equal(
            "SystemHooks: refused before/after cycle on MoveEntitySystem Before from c.id: a.id -> c.id -> b.id -> a.id; dropping that registration",
            cycleLog);
    }

    [Fact]
    public void Cycle_keeps_acyclic_edges_on_the_same_phase()
    {
        var specs = new[]
        {
            Spec(0, "a.id", 0, after: new[] { "b.id" }),
            Spec(1, "c.id", 0, after: new[] { "d.id" }),
            Spec(2, "d.id", 0),
            Spec(3, "b.id", 100, after: new[] { "a.id" }),
        };
        var indices = HookOrder.Resolve(specs, "MoveEntitySystem", "Before", out var cycleLog);
        Assert.Equal(
            "SystemHooks: refused before/after cycle on MoveEntitySystem Before from b.id: a.id -> b.id -> a.id; dropping that registration",
            cycleLog);
        Assert.Equal(new[] { 0, 2, 1 }, indices);
    }

    [Fact]
    public void Unload_reresolves_remaining_hooks_without_the_named_plugin()
    {
        var remaining = new[]
        {
            Spec(0, "a.id", 0, after: new[] { "b.id" }),
            Spec(2, "c.id", 50),
        };
        var afterUnload = HookOrder.Resolve(remaining, "MoveEntitySystem", "Before", out var afterLog);
        Assert.Null(afterLog);
        // b.id is gone, so a's after constraint is ignored; c's higher priority runs first.
        Assert.Equal(new[] { 1, 0 }, afterUnload);
    }

    [Fact]
    public void Equal_priority_uses_load_order_then_registration_index()
    {
        var specs = new[]
        {
            Spec(5, "a", 0, loadOrder: 0),
            Spec(1, "b", 0, loadOrder: 1),
        };
        var indices = HookOrder.Resolve(specs, "ServerBootstrapSystem", "Before", out var log);
        Assert.Null(log);
        Assert.Equal(new[] { 0, 1 }, indices);
    }

    [Fact]
    public void PhaseNeedsResort_only_when_the_phase_lost_a_hook_and_still_has_one()
    {
        Assert.True(HookOrder.PhaseNeedsResort(removed: 1, remaining: 2));
        Assert.False(HookOrder.PhaseNeedsResort(removed: 0, remaining: 3));
        Assert.False(HookOrder.PhaseNeedsResort(removed: 2, remaining: 0));
        Assert.False(HookOrder.PhaseNeedsResort(removed: 0, remaining: 0));
    }

    [Fact]
    public void Priority_only_keeps_registration_order_at_the_same_priority()
    {
        var specs = new[]
        {
            Spec(0, "a", 10),
            Spec(1, "b", 10),
            Spec(2, "c", 10),
        };
        var indices = HookOrder.Resolve(specs, "ServerBootstrapSystem", "Before", out var log);
        Assert.Null(log);
        Assert.Equal(new[] { 0, 1, 2 }, indices);
    }

    [Fact]
    public void Describe_prints_both_phases()
    {
        var before = new[] { Spec(0, "mod.a", 100) };
        var after = Array.Empty<OrderSpec>();
        string text = HookOrder.Describe("MoveEntitySystem", before, after);
        Assert.Equal(
            "SystemHooks MoveEntitySystem Before:\n  1. mod.a prio=100\nSystemHooks MoveEntitySystem After: (none)",
            text);
    }

    private static OrderSpec Spec(int index, string pluginId, int priority, string[]? after = null, int? loadOrder = null) =>
        new(index, pluginId, priority, before: null, after, loadOrder ?? index);

    // ---- job FLATCH1 (R44 finding 8): the prefix/postfix call stack ----------------------------
    //
    // Harmony SKIPS the postfix when the patched original throws, so SystemHooks.PrefixManaged
    // pushes a CallFrame that is never popped. That frame holds a World and the resolved Entry[]
    // of handler delegates — plugin-owned callbacks that SystemHooks.RemoveAll exists to release
    // ("a leftover hook cannot pin a collectible plugin ALC") and can no longer reach. A system
    // group catches per-system exceptions and keeps going, so this repeats every frame.

    private struct TestFrame
    {
        public int Id;
        public object? Payload;
    }

    /// <summary>
    /// The boundary: a single system update cannot span two rendered frames, so anything still
    /// pushed from an earlier frame is abandoned by construction. Without the sweep the stranded
    /// frame stays at the bottom of the stack for the life of the Core generation.
    ///
    /// <para>The release lands one frame later on purpose — see the second test below — because
    /// the sweep runs from inside a system update and cannot tell an entry pushed earlier in the
    /// SAME frame from a live outer frame it is nested inside.</para>
    /// </summary>
    [Fact]
    public void A_frame_stranded_by_a_throwing_system_is_released_on_the_following_rendered_frame()
    {
        var stack = new HookCallStack<TestFrame>();

        // Frame 1: a hooked system's prefix pushes, the original throws, the postfix never runs.
        stack.Push(new TestFrame { Id = 7 });
        Assert.Equal(1, stack.Depth);
        Assert.Equal(0, stack.NextFrame());      // end of frame 1: still ambiguous, kept
        Assert.Equal(1, stack.Depth);

        // Frame 2: nothing pushed it, so by now it can only be a strand.
        Assert.Equal(1, stack.NextFrame());
        Assert.Equal(0, stack.Depth);
        Assert.Equal(1, stack.AbandonedTotal);

        // And a healthy system still pairs normally afterwards.
        long healthy = stack.Push(new TestFrame { Id = 8 });
        Assert.Equal(8, stack.Release(healthy).Id);
        Assert.Equal(0, stack.Depth);
        Assert.Equal(0, stack.NextFrame());
        Assert.Equal(1, stack.AbandonedTotal);
    }

    /// <summary>
    /// The sweep runs from <c>SystemHooks.OnFrame</c>, which is itself reached from inside
    /// <c>BootstrapSystem</c>'s update — so frames pushed during the CURRENT rendered frame are
    /// live and must survive. A sweep that just emptied the stack would swallow their After hooks.
    /// </summary>
    [Fact]
    public void Frames_pushed_in_the_current_rendered_frame_are_live_and_survive_the_sweep()
    {
        var stack = new HookCallStack<TestFrame>();
        long outer = stack.Push(new TestFrame { Id = 1 });
        long inner = stack.Push(new TestFrame { Id = 2 });

        Assert.Equal(0, stack.NextFrame());
        Assert.Equal(2, stack.Depth);
        Assert.Equal(2, stack.Release(inner).Id);
        Assert.Equal(1, stack.Release(outer).Id);
    }

    /// <summary>
    /// Stamps only ever advance and pushes only ever append, so the abandoned entries are a
    /// bottom prefix; compaction must keep the survivors in LIFO order or a postfix would be
    /// handed another system's frame.
    /// </summary>
    [Fact]
    public void Compaction_keeps_the_surviving_frames_in_lifo_order()
    {
        var stack = new HookCallStack<TestFrame>();
        stack.Push(new TestFrame { Id = 1 });   // stranded in frame 1
        stack.NextFrame();                      // -> frame 2
        stack.Push(new TestFrame { Id = 2 });   // stranded in frame 2

        // Frame 3 opens with the two strands still at the bottom, then two live nested frames
        // are pushed on top of them.
        Assert.Equal(1, stack.NextFrame());     // -> frame 3, frame 1's strand released
        long third = stack.Push(new TestFrame { Id = 3 });
        long fourth = stack.Push(new TestFrame { Id = 4 });
        Assert.Equal(3, stack.Depth);

        // Frame 4's sweep drops frame 2's strand from underneath the live pair without
        // disturbing their order.
        Assert.Equal(1, stack.NextFrame());
        Assert.Equal(2, stack.AbandonedTotal);
        Assert.Equal(2, stack.Depth);
        Assert.Equal(4, stack.Release(fourth).Id);
        Assert.Equal(3, stack.Release(third).Id);
        Assert.Equal(0, stack.Depth);
    }

    /// <summary>
    /// <b>Job FHOOK3 closed this, and this test is the deliberate replacement for FHOOK2's
    /// characterization test</b> (<c>A_catching_caller_still_pops_positionally_when_both_systems_
    /// have_after_handlers</c>), which said "when this stack learns the frame identity, delete
    /// this test — it will fail". It learned it; this asserts the new fact instead of deleting the
    /// coverage.
    ///
    /// <para><b>Why it was closed after two jobs declined.</b> FHOOK1 and FHOOK2 both weighed the
    /// cross-ALC cost against a residue they described as needing two nested hooked systems and a
    /// throw — FHOOK2's words were that "hooks land on a handful of named system types and the
    /// catching groups are not among them". That last part is false for this framework, and it is
    /// the fact that changed the answer: <c>VirtualSystems.EnsureRelay</c> registers
    /// <c>SystemHooks.After(typeof(SimulationSystemGroup))</c> the first time any plugin calls
    /// <c>ctx.Systems.Add</c> on the default anchor — the documented per-tick seam — and
    /// <c>SimulationSystemGroup</c> derives from <c>SystemBase</c> (so its <c>Update</c> is
    /// patched) and catches each child's exception. The "outer system also has After handlers"
    /// precondition is therefore met by Progenitor's own flagship API, and the symptom was every
    /// default-anchor virtual system silently skipped — permanently, if the inner system threw
    /// every frame.</para>
    ///
    /// <para>The price is recorded where it is paid: the four update slot delegates widened from
    /// <c>bool hasAfter</c> to <c>long callToken</c>, which is a Bootstrap↔Core contract change and
    /// bumps <c>CoreLayout.ProtocolVersion</c>/<c>BootstrapLink.ProtocolVersion</c> to 4. That bump
    /// is what makes it safe: a half-deployed pair is refused at load with both numbers logged,
    /// rather than <c>Delegate.CreateDelegate</c> throwing in <c>HarmonySlots.Bind</c> and taking
    /// both sites Inert with system hooks silently off.</para>
    /// </summary>
    [Fact]
    public void A_catching_caller_releases_its_own_frame_when_both_systems_have_after_handlers()
    {
        var stack = new HookCallStack<TestFrame>();

        long group = stack.Push(new TestFrame { Id = 1 });   // the group's own frame; it has After
        stack.Push(new TestFrame { Id = 2 });                // the inner system's; its Update throws

        // The group catches and returns. Bootstrap's token-paired postfix calls ours with the CORE
        // token the group's prefix minted, so the group gets its OWN frame and dispatches its own
        // After handlers -- which is what ctx.Systems.Add(AfterSimulation, ...) rides on.
        Assert.Equal(1, stack.Release(group).Id);

        // And the inner system's strand went with it, in the same pass: everything above a
        // released frame was pushed inside that call and did not come back.
        Assert.Equal(0, stack.Depth);
        Assert.Equal(1, stack.AbandonedTotal);

        // Nothing is left for the sweep to find.
        Assert.Equal(0, stack.NextFrame());
        Assert.Equal(0, stack.NextFrame());
        Assert.Equal(1, stack.AbandonedTotal);
    }

    /// <summary>
    /// The other half of the same case, unchanged by FHOOK3 and still worth pinning: when only the
    /// inner system carries After handlers, nothing mispairs at all. The group never pushed here,
    /// and Bootstrap's postfix carries a zero Core token for it, so this stack is not touched for
    /// the group's call. The strand is the sweep's, one rendered frame later.
    /// </summary>
    [Fact]
    public void Only_the_inner_system_hooked_is_not_mispaired_at_all()
    {
        var stack = new HookCallStack<TestFrame>();

        stack.Push(new TestFrame { Id = 2 });   // the inner system's frame; its Update throws
        // The group has no After handlers, so PrefixManaged pushed nothing for it and Bootstrap's
        // postfix returns on CoreToken == 0 without calling ours.

        Assert.Equal(1, stack.Depth);
        Assert.Equal(0, stack.NextFrame());
        Assert.Equal(1, stack.NextFrame());     // released as a strand, never dispatched
        Assert.Equal(1, stack.AbandonedTotal);
    }

    /// <summary>
    /// An unpaired postfix — the zero token a prefix that pushed nothing hands back, and a token
    /// whose frame the sweep already released — releases <em>nothing</em>. The positional pop
    /// answered both of those by handing back somebody else's frame.
    /// </summary>
    [Fact]
    public void An_unpaired_postfix_releases_nothing_rather_than_someone_elses_frame()
    {
        var stack = new HookCallStack<TestFrame>();
        Assert.Equal(0, stack.Release(0).Id);

        long stranded = stack.Push(new TestFrame { Id = 5 });
        stack.NextFrame();
        stack.NextFrame();                       // swept away underneath the postfix
        Assert.Equal(0, stack.Depth);

        long live = stack.Push(new TestFrame { Id = 6 });
        Assert.Equal(0, stack.Release(stranded).Id);   // not somebody else's frame
        Assert.Equal(1, stack.Depth);                  // ...and it did not take the live one
        Assert.Equal(6, stack.Release(live).Id);
    }

    /// <summary>
    /// The ABA guard. <see cref="HookCallStack{T}.Clear"/> is Core teardown, and a postfix still in
    /// flight across it holds a token for a frame that is gone. The token counter is deliberately
    /// not reset, so that postfix releases nothing instead of releasing whatever the restored
    /// installation pushed into the same slot. (Core statics normally die with the collectible
    /// ALC, but <c>CoreLoader</c>'s restore-in-place re-Installs the same assembly in the same
    /// ALC, so these statics do survive an Uninstall/Install pair.)
    /// </summary>
    [Fact]
    public void Clear_does_not_reset_the_token_counter_so_a_stale_postfix_releases_nothing()
    {
        var stack = new HookCallStack<TestFrame>();
        long beforeTeardown = stack.Push(new TestFrame { Id = 1 });

        stack.Clear();

        long afterTeardown = stack.Push(new TestFrame { Id = 2 });
        Assert.NotEqual(beforeTeardown, afterTeardown);
        Assert.Equal(0, stack.Release(beforeTeardown).Id);
        Assert.Equal(1, stack.Depth);
        Assert.Equal(2, stack.Release(afterTeardown).Id);
    }

    [Fact]
    public void Clear_drops_live_frames_too_for_core_teardown()
    {
        var stack = new HookCallStack<TestFrame>();
        stack.Push(new TestFrame { Id = 1 });
        long inner = stack.Push(new TestFrame { Id = 2 });
        stack.Clear();
        Assert.Equal(0, stack.Depth);
        Assert.Equal(0, stack.Release(inner).Id);
    }

    /// <summary>
    /// The point of the sweep: the released slot must stop referencing the frame's payload. In
    /// production that payload is a <c>World</c> and an <c>Entry[]</c> of plugin delegates, so a
    /// slot that is merely skipped rather than zeroed keeps a collectible plugin ALC alive.
    /// (A live pin is proven with dotnet-dump and gcroot, never a reflective scan; this is the
    /// unit-level guard that the array slot itself lets go.)
    /// </summary>
    [Fact]
    public void A_released_slot_does_not_retain_the_frames_payload()
    {
        var stack = new HookCallStack<TestFrame>();
        var weak = PushStrandedPayload(stack);

        stack.NextFrame();
        Assert.Equal(1, stack.NextFrame());
        for (int i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        Assert.False(weak.IsAlive);
    }

    /// <summary>
    /// The same guarantee on the token path FHOOK3 added: <see cref="HookCallStack{T}.Release"/>
    /// drops the strand sitting above the frame it releases, and those slots must be zeroed too.
    /// A <c>_depth--</c> that only moved the counter would leave the inner system's <c>World</c>
    /// and plugin <c>Entry[]</c> reachable from the array for the life of the generation, which is
    /// the pin this whole stack exists to avoid.
    /// </summary>
    [Fact]
    public void A_strand_dropped_by_a_token_release_does_not_retain_its_payload()
    {
        var stack = new HookCallStack<TestFrame>();
        long outer = stack.Push(new TestFrame { Id = 1 });
        var weak = PushStrandedPayload(stack);

        Assert.Equal(1, stack.Release(outer).Id);
        Assert.Equal(1, stack.AbandonedTotal);
        for (int i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        Assert.False(weak.IsAlive);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference PushStrandedPayload(HookCallStack<TestFrame> stack)
    {
        var payload = new object();
        stack.Push(new TestFrame { Id = 1, Payload = payload });
        return new WeakReference(payload);
    }

    /// <summary>
    /// The production wiring. <c>SystemHooks</c> is <c>unsafe</c> and takes game types, so this is
    /// a source pin: the sweep has to run per rendered frame and the stack has to be dropped at
    /// Core teardown, or the type above is just a well-tested thing nothing calls.
    /// </summary>
    [Fact]
    public void SystemHooks_sweeps_the_call_stack_every_frame_and_clears_it_on_uninstall()
    {
        string text = System.IO.File.ReadAllText(System.IO.Path.Combine(
            SourcePin.RepoRoot(), "src", "Progenitor.Core", "Hooks", "SystemHooks.cs"));

        SourcePin.AssertUncommented(text, "HookCallStack<CallFrame> Frames");

        // AssertStatementExactly, not Contains: a pinned call is satisfied by the same text
        // commented out (FUI26) or sitting in the untaken half of a ternary (FDOC5), and a sweep
        // that does not run is the defect these pins exist to catch.
        SourcePin.AssertStatementExactly(
            SourcePin.MethodBody(text, "private static void OnFrame()"), "ReleaseAbandonedFrames();");
        SourcePin.AssertStatement(
            SourcePin.MethodBody(text, "private static void ReleaseAbandonedFrames()"),
            "int dropped = Frames.NextFrame();");
        SourcePin.AssertStatementExactly(
            SourcePin.MethodBody(text, "public static void Uninstall(object? harmony)"), "Frames.Clear();");
    }

    /// <summary>
    /// Job FHOOK3, the production wiring for the token. <c>SystemHooks</c> is <c>unsafe</c> and
    /// binds ProjectM and Unity.Entities, so it cannot be compiled here and this is a source pin.
    /// Both update prefixes must hand the token out through <c>ref long __state</c>, both postfixes
    /// must release by it, and the positional pop must be gone from the file entirely — one
    /// surviving <c>Frames.Pop()</c> is the mispairing back for that seam.
    ///
    /// <para>The <c>ref long</c> pins double as the <c>HarmonyFallback</c> pin: on the
    /// no-Bootstrap path these four methods <em>are</em> the Harmony prefix/postfix pair, and
    /// HarmonyX types <c>__state</c> once per patch declaring type
    /// (<c>harmony-patches.md</c> §8.12), so all four must agree or Harmony emits a load of the
    /// wrong type instead of failing at patch time.</para>
    /// </summary>
    [Fact]
    public void Both_core_update_hooks_carry_the_call_token_in_state_and_release_by_it()
    {
        string text = System.IO.File.ReadAllText(System.IO.Path.Combine(
            SourcePin.RepoRoot(), "src", "Progenitor.Core", "Hooks", "SystemHooks.cs"));

        SourcePin.AssertStatement(text, "internal static bool PrefixManaged(SystemBase __instance, ref long __state)");
        SourcePin.AssertStatement(text, "internal static void PostfixManaged(long __state)");
        SourcePin.AssertStatement(text, "internal static bool PrefixUnmanaged(void* pSystemState, ref long __state)");
        SourcePin.AssertStatement(text, "internal static void PostfixUnmanaged(long __state)");

        SourcePin.AssertStatementExactly(
            SourcePin.MethodBody(text, "internal static void PostfixManaged(long __state)"),
            "var frame = Release(__state);");
        SourcePin.AssertStatementExactly(
            SourcePin.MethodBody(text, "internal static void PostfixUnmanaged(long __state)"),
            "var frame = Release(__state);");
        SourcePin.AssertStatementExactly(
            SourcePin.MethodBody(text, "internal static bool PrefixManaged(SystemBase __instance, ref long __state)"),
            "__state = Push(frame);");
        SourcePin.AssertStatementExactly(
            SourcePin.MethodBody(text, "internal static bool PrefixUnmanaged(void* pSystemState, ref long __state)"),
            "__state = Push(frame);");

        // The mispairing shape: a postfix taking whatever is on top of the stack.
        Assert.DoesNotContain("Frames.Pop()", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ref bool __state", text, StringComparison.Ordinal);
    }
}
