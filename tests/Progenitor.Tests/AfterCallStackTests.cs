using System;
using System.IO;
using System.Text.RegularExpressions;
using Progenitor.Bootstrap;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Jobs FHOOK1 and FHOOK2. The prefix-to-postfix hand-off stacks behind Bootstrap's
/// <c>SystemBase.Update</c> and <c>WorldUnmanagedImpl.UnmanagedUpdate</c> patches.
///
/// <para>The defect: Harmony skips the postfix when the patched original throws, so
/// <c>HarmonyPatches</c>'s prefix pushed a hand-off that no postfix ever took, and the only thing
/// that ever emptied the arrays was <c>ClearAfterSnapshots</c> at a Core unload. A system group
/// catches per-system exceptions and keeps going, so on a server that never reloads Core one
/// system throwing every frame appended an entry per frame for the whole process lifetime —
/// Bootstrap is not collectible, so nothing else ever released them.</para>
///
/// <para>FHOOK1 bounded the growth on the rendered-frame boundary, and left the <em>pairing</em>
/// wrong: the postfix still popped whatever was on top. FHOOK2 pairs by token, which is what the
/// mispairing tests below are about — a caller that catches the inner system's exception must
/// release its own hand-off, not the strand sitting above it. The frame-epoch sweep stays as the
/// backstop for the strand no caller ever encloses; both mechanisms are covered here.</para>
///
/// <para>Job FLATCH1 fixed the Core-side twin (<c>Progenitor.Hooks.HookCallStack</c>, covered by
/// <see cref="HookOrderTests"/>). These two types share the frame rule and not the code, for the
/// reasons in <see cref="AfterCallStack{T}"/>'s remarks: Bootstrap cannot reference Core,
/// Bootstrap reads its own clock at the push instead of being called from a per-frame sweep, and
/// the two files therefore cannot be the character-identical copies the
/// <c>ReloadAllowPolicy</c> pin requires.</para>
/// </summary>
public class AfterCallStackTests
{
    private struct TestAfterCall
    {
        public int Id;
        public object? Postfix;
    }

    // ---- the mispairing (job FHOOK2) ----------------------------------------------------------

    /// <summary>
    /// The case neither FHOOK1 nor FLATCH1 could test, and the reason this job exists.
    ///
    /// <para>An inner system's <c>Update</c> throws, so Harmony skips its postfix and its hand-off
    /// is stranded. Its caller — a system group, which catches per-system exceptions by design —
    /// swallows the exception and returns normally, so the caller's <em>own</em> postfix runs.
    /// With positional popping that postfix took the top of the stack, which is the inner
    /// system's strand, and dispatched another call's After handlers through another call's
    /// snapshot of the Core postfix delegate. LIFO pairing only ever held while every caller let
    /// the exception propagate.</para>
    ///
    /// <para>The token makes it structurally impossible: the caller releases the frame its own
    /// prefix pushed, and the strand it was covering goes with it as abandoned.</para>
    /// </summary>
    [Fact]
    public void A_caller_that_catches_releases_its_own_hand_off_and_not_the_strand()
    {
        var stack = new AfterCallStack<TestAfterCall>();

        long group = stack.Push(new TestAfterCall { Id = 1 }, 5);   // the system group's Update
        long inner = stack.Push(new TestAfterCall { Id = 2 }, 5);   // the inner system's, which throws

        // Harmony skips the inner postfix. The group catches, returns normally, and its postfix runs.
        var released = stack.Release(group);

        Assert.Equal(1, released.Id);               // positional popping answered 2 here
        Assert.Equal(0, stack.Depth);
        Assert.Equal(1, stack.AbandonedTotal);      // and the strand went with it, in the same pass
        Assert.Equal(0, stack.Release(inner).Id);   // the strand's own token can release nothing
    }

    /// <summary>
    /// The same shape one level deeper, because a group that catches can be nested inside a group
    /// that catches. Every hand-off above the released one was pushed inside that call and did not
    /// come back, so all of them are strands and all of them are released at once.
    /// </summary>
    [Fact]
    public void Releasing_a_frame_drops_every_strand_that_piled_up_above_it()
    {
        var stack = new AfterCallStack<TestAfterCall>();

        long outer = stack.Push(new TestAfterCall { Id = 1 }, 5);
        stack.Push(new TestAfterCall { Id = 2 }, 5);                // inner group, threw
        stack.Push(new TestAfterCall { Id = 3 }, 5);                // its system, threw
        long sibling = stack.Push(new TestAfterCall { Id = 4 }, 5); // a later sibling that returned
        Assert.Equal(4, stack.Release(sibling).Id);
        Assert.Equal(0, stack.AbandonedTotal);                      // nothing above the sibling
        Assert.Equal(3, stack.Depth);

        Assert.Equal(1, stack.Release(outer).Id);
        Assert.Equal(2, stack.AbandonedTotal);
        Assert.Equal(0, stack.Depth);
    }

    /// <summary>
    /// A hand-off released by its own postfix is not released twice, and the healthy nested case
    /// still unwinds in order — the token must not turn a well-formed unwind into a truncation.
    /// </summary>
    [Fact]
    public void A_healthy_nest_unwinds_in_order_and_abandons_nothing()
    {
        var stack = new AfterCallStack<TestAfterCall>();

        long a = stack.Push(new TestAfterCall { Id = 1 }, 5);
        long b = stack.Push(new TestAfterCall { Id = 2 }, 5);
        long c = stack.Push(new TestAfterCall { Id = 3 }, 5);

        Assert.Equal(3, stack.Release(c).Id);
        Assert.Equal(2, stack.Release(b).Id);
        Assert.Equal(1, stack.Release(a).Id);
        Assert.Equal(0, stack.Depth);
        Assert.Equal(0, stack.AbandonedTotal);
    }

    /// <summary>
    /// Tokens name one push for the life of the process. <c>ClearAfterSnapshots</c> empties the
    /// stack at a Core unload while a postfix may still be in flight above it (the unload can run
    /// nested inside the very Update being patched); if the counter restarted, that postfix's
    /// token would match a fresh generation's push and release it. The counter is never reset.
    /// </summary>
    [Fact]
    public void A_token_minted_before_a_core_unload_can_never_match_one_minted_after()
    {
        var stack = new AfterCallStack<TestAfterCall>();
        long before = stack.Push(new TestAfterCall { Id = 1 }, 5);

        stack.Clear();                                     // ClearAfterSnapshots at Unload

        long after = stack.Push(new TestAfterCall { Id = 2 }, 5);
        Assert.NotEqual(before, after);

        Assert.Equal(0, stack.Release(before).Id);         // the in-flight postfix releases nothing
        Assert.Equal(1, stack.Depth);                      // and did not take generation N+1's frame
        Assert.Equal(2, stack.Release(after).Id);
    }

    /// <summary>An unpaired postfix — a prefix that pushed nothing, or a frame a Core unload took
    /// out from under it — releases a default hand-off rather than somebody else's.</summary>
    [Fact]
    public void An_unpaired_postfix_releases_a_default_hand_off()
    {
        var stack = new AfterCallStack<TestAfterCall>();

        Assert.Equal(0, stack.Release(0).Id);              // __state == 0: the prefix pushed nothing
        Assert.Equal(0, stack.Release(99).Id);             // and an unknown token takes nothing

        long live = stack.Push(new TestAfterCall { Id = 5 }, 1);
        Assert.Equal(0, stack.Release(0).Id);
        Assert.Equal(0, stack.Release(live + 1000).Id);
        Assert.Equal(1, stack.Depth);                      // neither disturbed the live frame
        Assert.Equal(0, stack.AbandonedTotal);
        Assert.Equal(5, stack.Release(live).Id);
    }

    // ---- the frame-epoch backstop (job FHOOK1) ------------------------------------------------

    /// <summary>
    /// Why the sweep is still here after the token. The token only reaches strands lying above a
    /// frame somebody later releases. When the outermost patched <c>Update</c> on a call chain
    /// throws and the catch is in something that is not itself a patched <c>SystemBase.Update</c>
    /// — Unity's player loop, an unpatched group, or the unmanaged caller of
    /// <c>UnmanagedUpdate</c>, which is the ordinary case for that stack — the strand sits at the
    /// bottom with nothing enclosing it, and every later push lands and releases <em>above</em>
    /// it. Only the frame boundary can say it is abandoned.
    /// </summary>
    [Fact]
    public void A_strand_no_caller_encloses_is_released_by_the_frame_sweep_and_not_by_the_token()
    {
        var stack = new AfterCallStack<TestAfterCall>();

        // Frame 10: outermost patched Update throws; its caller is not one of ours.
        stack.Push(new TestAfterCall { Id = 7 }, 10);
        Assert.Equal(1, stack.Depth);
        Assert.Equal(0, stack.AbandonedTotal);

        // Frame 11: a healthy outermost update. It is pushed ABOVE the strand, so releasing it by
        // token drops nothing - which is exactly why the token alone would leak this one forever.
        long healthy = stack.Push(new TestAfterCall { Id = 8 }, 11);
        Assert.Equal(8, stack.Release(healthy).Id);
        Assert.Equal(1, stack.Depth);
        Assert.Equal(0, stack.AbandonedTotal);

        // Frame 12: by now frame 10's entry can only be a strand, and the sweep at the push takes it.
        long next = stack.Push(new TestAfterCall { Id = 9 }, 12);
        Assert.Equal(1, stack.AbandonedTotal);
        Assert.Equal(9, stack.Release(next).Id);
        Assert.Equal(0, stack.Depth);
    }

    /// <summary>
    /// The Bootstrap-specific hazard, and why the sweep counts epochs rather than comparing the
    /// frame number itself. <c>ProcessHost.Tick</c> runs as a postfix on
    /// <c>ServerBootstrapSystem.OnUpdate</c>, which is <b>inside</b> a patched
    /// <c>SystemBase.Update</c> — so <c>ProcessHost.FrameCount</c> advances while outer frames
    /// pushed earlier in that same rendered frame are still live. A sweep that dropped everything
    /// stamped below the current frame number would take those live frames' After hooks with it.
    /// </summary>
    [Fact]
    public void A_frame_pushed_before_the_tick_survives_the_counter_advancing_under_it()
    {
        var stack = new AfterCallStack<TestAfterCall>();

        // Rendered frame 11, before ProcessHost.Tick has run: the counter still reads 10.
        long group = stack.Push(new TestAfterCall { Id = 1 }, 10);   // outer group's Update
        long boot = stack.Push(new TestAfterCall { Id = 2 }, 10);    // the bootstrap system's own Update
        Assert.Equal(2, stack.Release(boot).Id);                     // it returns; Tick ran inside it

        // Same rendered frame, after Tick: the counter now reads 11 and the group is still live.
        long later = stack.Push(new TestAfterCall { Id = 3 }, 11);
        Assert.Equal(2, stack.Depth);
        Assert.Equal(0, stack.AbandonedTotal);
        Assert.Equal(3, stack.Release(later).Id);
        Assert.Equal(1, stack.Release(group).Id);                    // the group's own postfix, not swept
        Assert.Equal(0, stack.AbandonedTotal);
    }

    /// <summary>
    /// Stamps only ever advance, pushes only ever append and releases only ever truncate from the
    /// top, so the sweep's strands are always a bottom prefix; compaction has to keep the
    /// survivors and their tokens together or a postfix releases nothing and its After is lost.
    /// </summary>
    [Fact]
    public void Compaction_keeps_the_surviving_hand_offs_and_their_tokens()
    {
        var stack = new AfterCallStack<TestAfterCall>();
        stack.Push(new TestAfterCall { Id = 1 }, 1);          // stranded in frame 1
        stack.Push(new TestAfterCall { Id = 2 }, 2);          // stranded in frame 2

        // Frame 3 opens with both strands below, then two live nested frames on top.
        long a = stack.Push(new TestAfterCall { Id = 3 }, 3);
        long b = stack.Push(new TestAfterCall { Id = 4 }, 3);
        Assert.Equal(1, stack.AbandonedTotal);                // frame 1's, released at frame 3's push
        Assert.Equal(3, stack.Depth);

        // Frame 4 drops frame 2's strand from underneath the live pair without disturbing them.
        long c = stack.Push(new TestAfterCall { Id = 5 }, 4);
        Assert.Equal(2, stack.AbandonedTotal);
        Assert.Equal(3, stack.Depth);
        Assert.Equal(5, stack.Release(c).Id);
        Assert.Equal(4, stack.Release(b).Id);
        Assert.Equal(3, stack.Release(a).Id);                 // still findable after two compactions
        Assert.Equal(0, stack.Depth);
    }

    /// <summary>
    /// The leak itself, as the finding describes it: one system throwing on every frame of a
    /// server that never reloads Core. Against the old array-plus-depth this grows without bound
    /// for the life of the process. Here the throwing system is the outermost update, so no
    /// caller encloses its strand and the frame boundary is the only thing holding it — at the one
    /// frame of latency the sweep deliberately allows.
    /// </summary>
    [Fact]
    public void A_system_throwing_every_frame_does_not_grow_the_stack()
    {
        var stack = new AfterCallStack<TestAfterCall>();
        const int frames = 10_000;

        for (int frame = 1; frame <= frames; frame++)
        {
            // A healthy system pairs up...
            long token = stack.Push(new TestAfterCall { Id = frame }, frame);
            stack.Release(token);
            // ...and a throwing one does not.
            stack.Push(new TestAfterCall { Id = -frame }, frame);
            Assert.True(stack.Depth <= 2, "depth grew to " + stack.Depth + " on frame " + frame);
        }

        // Every strand but the two most recent has been released: one is this frame's, one is
        // last frame's and still inside the deliberate one-frame latency.
        Assert.Equal(frames - 2, stack.AbandonedTotal);
        Assert.Equal(2, stack.Depth);
    }

    /// <summary>
    /// The same leak where the throwing system <em>is</em> enclosed by a caller that catches —
    /// the group case. The token releases the strand inside the same rendered frame, so the stack
    /// never carries one at all and there is no latency to allow for.
    /// </summary>
    [Fact]
    public void A_caught_throw_every_frame_leaves_nothing_on_the_stack_at_all()
    {
        var stack = new AfterCallStack<TestAfterCall>();
        const int frames = 10_000;

        for (int frame = 1; frame <= frames; frame++)
        {
            long group = stack.Push(new TestAfterCall { Id = frame }, frame);
            stack.Push(new TestAfterCall { Id = -frame }, frame);   // throws; postfix skipped
            stack.Release(group);                                   // the group caught and returned
            Assert.Equal(0, stack.Depth);
        }

        Assert.Equal(frames, stack.AbandonedTotal);
    }

    /// <summary>
    /// What the release is for. In production the released slot holds Core's
    /// <c>ManagedUpdatePostfix</c> delegate, so a slot merely skipped rather than zeroed keeps a
    /// reference into the Core generation alive until the next unload. Both release paths must
    /// zero, so both are covered. (A live ALC pin is proven with dotnet-dump and gcroot, never by
    /// reasoning or a reflective scan; this is only the unit-level guard that the slot lets go.)
    /// </summary>
    [Fact]
    public void A_released_slot_does_not_retain_the_postfix_delegate()
    {
        var stack = new AfterCallStack<TestAfterCall>();

        // Path 1: swept on the frame boundary.
        var swept = PushStrandedPostfix(stack, frame: 1);
        long a = stack.Push(new TestAfterCall { Id = 2 }, 2);
        stack.Release(a);
        long b = stack.Push(new TestAfterCall { Id = 3 }, 3);
        stack.Release(b);
        Assert.Equal(1, stack.AbandonedTotal);

        // Path 2: dropped as a strand by the caller that enclosed it.
        long group = stack.Push(new TestAfterCall { Id = 4 }, 4);
        var dropped = PushStrandedPostfix(stack, frame: 4);
        stack.Release(group);
        Assert.Equal(2, stack.AbandonedTotal);
        Assert.Equal(0, stack.Depth);

        for (int i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        Assert.False(swept.IsAlive);
        Assert.False(dropped.IsAlive);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference PushStrandedPostfix(AfterCallStack<TestAfterCall> stack, int frame)
    {
        var postfix = new object();
        stack.Push(new TestAfterCall { Id = 1, Postfix = postfix }, frame);
        return new WeakReference(postfix);
    }

    /// <summary>
    /// <c>ClearAfterSnapshots</c> keeps its job: no hand-off may cross a Core unload holding
    /// generation N's postfix delegate, so that one clears live frames too.
    /// </summary>
    [Fact]
    public void Clear_drops_live_frames_too_for_a_core_unload()
    {
        var stack = new AfterCallStack<TestAfterCall>();
        stack.Push(new TestAfterCall { Id = 1 }, 1);
        stack.Push(new TestAfterCall { Id = 2 }, 1);

        stack.Clear();
        Assert.Equal(0, stack.Depth);

        // And the stack keeps working across the swap.
        long token = stack.Push(new TestAfterCall { Id = 3 }, 2);
        Assert.Equal(3, stack.Release(token).Id);
    }

    /// <summary>
    /// Job FDOC5 (R45 finding 34). The epoch paragraph used to justify itself with two claims the
    /// code contradicts — that pre-tick and post-tick pushes share an epoch, and that comparing
    /// against the raw frame number would release live outer frames — when the epoch is an exact
    /// relabeling of the sequence of distinct <c>processFrame</c> values and buys neither. What it
    /// <em>does</em> buy is the property this test pins: stamps are non-decreasing from the bottom
    /// of the stack upward whatever <c>Time.frameCount</c> does, so <c>DropStale</c>'s single
    /// forward scan still finds a bottom prefix when the counter repeats a value it used earlier
    /// (a wrap, or a rewind). Stamped with the raw frame number, the stack here would read
    /// 10, 5, 10 and one scan against 7 would drop the live top frame with the dead bottom ones.
    /// </summary>
    [Fact]
    public void A_frame_counter_that_goes_backwards_does_not_release_a_live_frame()
    {
        var stack = new AfterCallStack<TestAfterCall>();

        stack.Push(new TestAfterCall { Id = 1 }, 10);
        stack.Push(new TestAfterCall { Id = 2 }, 5);    // the counter went backwards
        long third = stack.Push(new TestAfterCall { Id = 3 }, 10);   // ...and back to a value it used
        long fourth = stack.Push(new TestAfterCall { Id = 4 }, 7);

        // One epoch of latency each, and nothing else: 1 and 2 are gone, 3 and 4 are live.
        Assert.Equal(2, stack.AbandonedTotal);
        Assert.Equal(2, stack.Depth);
        Assert.Equal(4, stack.Release(fourth).Id);
        Assert.Equal(3, stack.Release(third).Id);
        Assert.Equal(0, stack.Depth);
    }

    /// <summary>The operator line names the cause and says it will not repeat.</summary>
    [Fact]
    public void The_strand_report_names_the_site_and_the_cause()
    {
        string line = AfterCallStrand.Report(HarmonySlotPolicy.SystemBaseUpdate, 3);
        Assert.Contains(HarmonySlotPolicy.SystemBaseUpdate, line, StringComparison.Ordinal);
        Assert.Contains("threw", line, StringComparison.Ordinal);
        Assert.Contains("Harmony skips the postfix", line, StringComparison.Ordinal);
        Assert.Contains("3", line, StringComparison.Ordinal);
        Assert.Contains("an update hook", AfterCallStrand.Report("", 1), StringComparison.Ordinal);
    }

    /// <summary>
    /// Job FDOC5 (R45 finding 32). Neither counter observes an exception: <c>Release</c> counts
    /// what was sitting above the frame it released and <c>DropStale</c> counts what an earlier
    /// epoch left, and both are "a hand-off its own postfix never handed back". The line has to
    /// state that and give the throw as the cause, because an operator reading "left behind by an
    /// update that threw" goes looking for a throwing system on the strength of a count that
    /// never looked for one.
    /// </summary>
    [Fact]
    public void The_strand_report_states_what_was_measured_before_it_names_a_cause()
    {
        string line = AfterCallStrand.Report(HarmonySlotPolicy.SystemBaseUpdate, 2);

        // The measured fact, and it comes FIRST -- before the sentence that names the cause.
        int measured = line.IndexOf("never handed back by their own postfix", StringComparison.Ordinal);
        int cause = line.IndexOf("the cause is an update that threw", StringComparison.Ordinal);
        Assert.True(measured >= 0, "the line does not say what was counted: " + line);
        Assert.True(cause > measured, "the cause is not given as a cause, after the measurement: " + line);

        // The old wording asserted the throw as the observation. It must not come back.
        Assert.DoesNotContain("left behind by an update that threw", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// The Core twin. Job FDOC5 gave this line a caveat Bootstrap's did not need — "the frame
    /// released here belongs to the OUTER system and that system did not throw" — because Core's
    /// stack was popped positionally. Job FHOOK3 closed that, so the caveat is now <b>false</b>,
    /// and a diagnostic that warns about a mispairing that cannot happen sends an operator looking
    /// for the wrong thing just as surely as one that names the wrong system. FDOC5's shape is
    /// kept: what was measured comes first, the throw is named as the cause rather than as an
    /// observation, because the count still never watched an exception happen.
    /// </summary>
    [Fact]
    public void The_core_strand_report_states_what_was_measured_and_no_longer_warns_of_a_mispairing()
    {
        string line = Progenitor.Hooks.HookStrandReport.Report(4);

        Assert.Contains("4", line, StringComparison.Ordinal);

        int measured = line.IndexOf("never released by their own postfix", StringComparison.Ordinal);
        int cause = line.IndexOf("the cause is a system that threw", StringComparison.Ordinal);
        Assert.True(measured >= 0, "the line does not say what was counted: " + line);
        Assert.True(cause > measured, "the cause is not given as a cause, after the measurement: " + line);

        // The wording of both superseded lines must stay gone.
        Assert.DoesNotContain("left behind by a system that threw", line, StringComparison.Ordinal);
        Assert.DoesNotContain("OUTER system", line, StringComparison.Ordinal);
        Assert.DoesNotContain("pops positionally", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// The wording lives beside the stack, not inside <c>SystemHooks</c>, for the reason R45
    /// finding 13 gave: <c>SystemHooks</c> is <c>unsafe</c> and binds ProjectM, so a sentence
    /// written inline there is a sentence no test in this suite can read. Source pin, and it
    /// requires an uncommented call — a commented-out one satisfies a bare <c>Contains</c>.
    /// </summary>
    [Fact]
    public void SystemHooks_logs_the_strand_line_through_the_testable_report()
    {
        string body = SourcePin.MethodBody(
            File.ReadAllText(RepoFile("src", "Progenitor.Core", "Hooks", "SystemHooks.cs")),
            "private static void ReleaseAbandonedFrames(");

        // AssertStatementExactly, not AssertStatement: the whole statement is the assertion. The
        // loose form is satisfied by the same call sitting in the untaken half of a ternary, and a
        // report that does not run is the defect, not the fix.
        SourcePin.AssertStatementExactly(body, "Plugin.Logger?.LogWarning(HookStrandReport.Report(dropped));");

        // ...and the sentence is not ALSO written out inline here, which is how the two drift.
        Assert.DoesNotContain("hook call frame(s)", body, StringComparison.Ordinal);
    }

    // ---- production wiring --------------------------------------------------------------------

    /// <summary>
    /// The production wiring. <c>HarmonyPatches</c> is <c>unsafe</c> and binds ProjectM, Harmony
    /// and Unity.Entities, so it cannot be compiled here and this is a source pin — the kind that
    /// earns it: both stacks have to be stamped with Bootstrap's frame clock and cleared at a Core
    /// unload, or the type above is a well-tested thing nothing calls. The absence assertions are
    /// the shape of the leak FHOOK1 removed.
    /// </summary>
    [Fact]
    public void HarmonyPatches_stamps_both_stacks_with_the_process_frame_and_clears_them_on_unload()
    {
        string text = File.ReadAllText(RepoFile("src", "Progenitor.Bootstrap", "HarmonyPatches.cs"));

        SourcePin.AssertUncommented(text, "AfterCallStack<AfterCall> _managedAfter");
        SourcePin.AssertUncommented(text, "AfterCallStack<AfterCall> _unmanagedAfter");
        SourcePin.AssertUncommented(text, "_managedAfter.Push(new AfterCall { CoreToken = coreToken, Managed = postfix }, ProcessHost.FrameCount);");
        SourcePin.AssertUncommented(text, "_unmanagedAfter.Push(new AfterCall { CoreToken = coreToken, Unmanaged = postfix }, ProcessHost.FrameCount);");

        int clear = text.IndexOf("internal static void ClearAfterSnapshots()", StringComparison.Ordinal);
        Assert.True(clear > 0);
        string body = text.Substring(clear, Math.Min(600, text.Length - clear));
        SourcePin.AssertStatement(body, "_managedAfter.Clear();");
        SourcePin.AssertStatement(body, "_unmanagedAfter.Clear();");

        // The leaking shape: an unbounded array plus a depth counter that only an unload reset.
        Assert.DoesNotContain("_managedAfterDepth", text, StringComparison.Ordinal);
        Assert.DoesNotContain("_unmanagedAfterDepth", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Array.Resize(ref _managedAfter", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Array.Resize(ref _unmanagedAfter", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Job FHOOK2. The token has to reach the patch bodies or the stack pairs correctly and the
    /// hooks do not. Both hooks' <c>__state</c> must be the token and both postfixes must release
    /// by it; positional popping must be gone from the file entirely, since a single surviving
    /// call site is the mispairing back.
    /// </summary>
    [Fact]
    public void Both_update_hooks_carry_the_release_token_in_state_and_release_by_it()
    {
        string text = File.ReadAllText(RepoFile("src", "Progenitor.Bootstrap", "HarmonyPatches.cs"));

        SourcePin.AssertStatement(text, "private static bool Prefix(SystemBase __instance, ref long __state)");
        SourcePin.AssertStatement(text, "private static void Postfix(long __state)");
        SourcePin.AssertStatement(text, "public static bool Prefix(void* pSystemState, ref long __state)");
        SourcePin.AssertStatement(text, "public static void Postfix(long __state)");

        // Per METHOD BODY, not over the whole file. A file-wide Assert.Contains is satisfied by
        // the OTHER hook's copy of an identical line, so mutating one of the two paths passes -
        // which is how a mutation that broke the managed hook survived this test while it was
        // written that way (job FHOOK3). Both bodies are asserted, separately.
        string managedPre = SourcePin.MethodBody(
            text, "private static bool Prefix(SystemBase __instance, ref long __state)");
        string managedPost = SourcePin.MethodBody(text, "private static void Postfix(long __state)");
        string unmanagedPre = SourcePin.MethodBody(
            text, "public static bool Prefix(void* pSystemState, ref long __state)");
        string unmanagedPost = SourcePin.MethodBody(text, "public static void Postfix(long __state)");

        SourcePin.AssertStatementExactly(managedPre, "__state = PushManaged(coreToken, postfix);");
        SourcePin.AssertStatementExactly(unmanagedPre, "__state = PushUnmanaged(coreToken, postfix);");
        SourcePin.AssertStatementExactly(managedPost, "var frame = ReleaseManaged(__state);");
        SourcePin.AssertStatementExactly(unmanagedPost, "var frame = ReleaseUnmanaged(__state);");

        // Job FHOOK3: and the entry carries CORE's token onward, so Core's postfix releases the
        // frame this call pushed. A bool here would be the old channel back, whatever it is named.
        SourcePin.AssertStatement(text, "public long CoreToken;");
        Assert.DoesNotContain("public bool HasAfter;", text, StringComparison.Ordinal);
        foreach (string post in new[] { managedPost, unmanagedPost })
        {
            SourcePin.AssertStatementExactly(post, "if (frame.CoreToken == 0) return;");
            SourcePin.AssertStatementExactly(post, "try { slot(frame.CoreToken); }");
        }

        // The mispairing shape: a postfix taking whatever is on top of the stack.
        Assert.DoesNotContain("PopManaged", text, StringComparison.Ordinal);
        Assert.DoesNotContain("PopUnmanaged", text, StringComparison.Ordinal);
        Assert.DoesNotContain("_managedAfter.Pop()", text, StringComparison.Ordinal);
        Assert.DoesNotContain("_unmanagedAfter.Pop()", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Job FHOOK2, the ABI guard. Widening <c>__state</c> is confined to the two update hooks
    /// because HarmonyX declares that local per <em>patch declaring type</em>
    /// (<c>variables[patch.DeclaringType.FullName]</c> in <c>HarmonyManipulator</c>, typed from
    /// that type's first <c>__state</c> parameter). The other patch classes in this file must
    /// therefore be left alone — and a prefix and postfix inside <b>one</b> class that disagreed
    /// on the type would not throw at patch time, it would emit a load of the wrong type. So:
    /// nothing outside the two update hooks may have grown a <c>long __state</c>, and
    /// <c>StunStartHook</c>'s <c>bool</c> pair must still be a <c>bool</c> pair.
    /// </summary>
    [Fact]
    public void Widening_state_did_not_reach_any_other_patch_class()
    {
        string text = File.ReadAllText(RepoFile("src", "Progenitor.Bootstrap", "HarmonyPatches.cs"));

        // The only four __state declarations in the file that are 'long' are the update hooks'.
        var longState = Regex.Matches(text, @"(ref )?long __state");
        Assert.Equal(4, longState.Count);

        // StunStartHook still joins its prefix to its finalizer with the Enter bit, as a bool.
        int stun = text.IndexOf("private static class StunStartHook", StringComparison.Ordinal);
        Assert.True(stun > 0);
        string stunBody = text.Substring(stun);
        SourcePin.AssertUncommented(stunBody, "ref bool __state");
        SourcePin.AssertStatement(stunBody, "public static void Finalizer(StunProcess __instance, Exception? __exception, bool __state)");
        Assert.DoesNotContain("long __state", stunBody, StringComparison.Ordinal);
    }

    /// <summary>
    /// The ABI guard on the cross-ALC half, written by job FHOOK2 and now carrying job FHOOK3's
    /// widening. Those four signatures exist twice — once in Bootstrap's <c>HarmonySlots</c> and
    /// once in Core's <c>SystemHooks</c> — and <c>HarmonySlots.Bind</c> joins them with
    /// <c>Delegate.CreateDelegate</c>, which throws on any skew and takes the site <b>Inert</b>
    /// (job FLATCH1 measured that failure: no hooks at all for that Core generation). FHOOK2 pinned
    /// that the two declarations stay identical precisely so the widening it deferred could not be
    /// done by halves; FHOOK3 did it, and the identity loop is the part that must never be relaxed.
    ///
    /// <para>The exact-text assertions below are the second half: they say <em>which</em> shape is
    /// deployed. A change to them is a Bootstrap↔Core contract change and must bump
    /// <c>CoreLayout.ProtocolVersion</c>/<c>BootstrapLink.ProtocolVersion</c> in the same commit,
    /// which is what turns a half-deployed pair into a refused load with both numbers logged
    /// instead of an Inert site. <c>PluginUnloadReasonTests</c> pins that the two constants agree.
    /// </para>
    /// </summary>
    [Fact]
    public void The_update_slot_delegates_are_identical_on_both_sides_of_the_alc_boundary()
    {
        string bootstrap = File.ReadAllText(RepoFile("src", "Progenitor.Bootstrap", "HarmonySlots.cs"));
        string core = File.ReadAllText(RepoFile("src", "Progenitor.Core", "Hooks", "SystemHooks.cs"));

        foreach (string name in new[]
                 {
                     "ManagedUpdatePrefix", "ManagedUpdatePostfix",
                     "UnmanagedUpdatePrefix", "UnmanagedUpdatePostfix",
                 })
        {
            string b = DelegateSignature(bootstrap, name);
            string c = DelegateSignature(core, name);
            Assert.Equal(b, c);
        }

        // Job FHOOK3: the channel carries Core's call frame identity, both ways, on both pairs.
        Assert.Equal(
            "bool ManagedUpdatePrefix(SystemBase instance, ref long callToken);",
            DelegateSignature(bootstrap, "ManagedUpdatePrefix"));
        Assert.Equal(
            "void ManagedUpdatePostfix(long callToken);",
            DelegateSignature(bootstrap, "ManagedUpdatePostfix"));
        Assert.Equal(
            "bool UnmanagedUpdatePrefix(IntPtr pSystemState, ref long callToken);",
            DelegateSignature(bootstrap, "UnmanagedUpdatePrefix"));
        Assert.Equal(
            "void UnmanagedUpdatePostfix(long callToken);",
            DelegateSignature(bootstrap, "UnmanagedUpdatePostfix"));

        // And Core's forwarders match the delegates they are bound to. Bind uses
        // Delegate.CreateDelegate on the METHOD, so a forwarder that still took a bool would throw
        // there at Install time, not here.
        SourcePin.AssertStatement(core, "internal static void SlotPostfixManaged(long callToken) => PostfixManaged(callToken);");
        SourcePin.AssertStatement(core, "internal static void SlotPostfixUnmanaged(long callToken) => PostfixUnmanaged(callToken);");
    }

    /// <summary>
    /// Job FHOOK3, the deploy consequence, pinned because getting it wrong strands an operator
    /// with a Core that will not load. Widening the four update slot delegates is a Bootstrap↔Core
    /// contract change, and <c>CoreLayout</c>'s own rule says to bump on a slot-shape change. The
    /// bump is not paperwork: without it the ordinary two-part deploy (Core swaps this launch,
    /// Bootstrap's <c>.next</c> one launch later) would run a <c>long</c> Core against a
    /// <c>bool</c> Bootstrap, <c>Bind</c> would throw, and both system-hook sites would go Inert
    /// with hooks silently off. With it, that pairing is refused at load with both numbers logged.
    /// </summary>
    [Fact]
    public void Widening_the_slot_delegates_came_with_the_protocol_bump_that_makes_it_safe()
    {
        Assert.Equal(5, Progenitor.Bootstrap.CoreLayout.ProtocolVersion);

        string layout = File.ReadAllText(RepoFile("src", "Progenitor.Bootstrap", "CoreLayout.cs"));
        string link = File.ReadAllText(RepoFile("src", "Progenitor.Core", "BootstrapLink.cs"));

        // Both constants, in lockstep. BootstrapLink binds the game, so its side is a source pin.
        SourcePin.AssertStatement(layout, "public const int ProtocolVersion = 5;");
        SourcePin.AssertStatement(link, "public const int ProtocolVersion = 5;");

        // And the reason is written down at both constants, so the next bump is not a mystery.
        // Assert.Contains, deliberately: the pinned text IS the comment, so the SourcePin
        // helpers - which read the code part of a line and nothing else - cannot see it.
        Assert.Contains("FHOOK3", layout, StringComparison.Ordinal);
        Assert.Contains("FHOOK3", link, StringComparison.Ordinal);
    }

    private static string DelegateSignature(string text, string name)
    {
        var m = Regex.Match(text, @"delegate\s+([^\r\n]*?\b" + Regex.Escape(name) + @"\([^)]*\);)");
        Assert.True(m.Success, "delegate " + name + " not found");
        return Regex.Replace(m.Groups[1].Value, @"\s+", " ").Trim();
    }

    private static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Progenitor.sln")))
                return Path.Combine(dir.FullName, Path.Combine(parts));
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Progenitor.sln not found from " + AppContext.BaseDirectory);
    }
}
