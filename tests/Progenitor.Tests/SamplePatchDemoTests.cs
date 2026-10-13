using System;
using System.Collections.Generic;
using System.IO;
using Progenitor.Sample;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Job FSAMP. Game-free coverage of <c>.patchdemo</c>: token parse, the reply for each outcome,
/// and the fire counter the prefix drives. The patch itself is behind <c>!PROGENITOR_TESTS</c>
/// (it needs HarmonyX and a live IL2CPP method), so what is pinned here is every string
/// <c>docs/bridge-ops.md</c> tells an operator to look for.
/// </summary>
[Collection("PluginLogger")]
public class SamplePatchDemoTests : IDisposable
{
    private readonly List<string> _lines = new();

    public SamplePatchDemoTests()
    {
        SamplePatchDemo.ResetCount();
        SampleLog.Clear();
        SampleLog.Sink = line => _lines.Add(line);
    }

    public void Dispose()
    {
        SamplePatchDemo.ResetCount();
        SampleLog.Clear();
    }

    [Theory]
    [InlineData("", SamplePatchDemo.Kind.Status)]
    [InlineData("   ", SamplePatchDemo.Kind.Status)]
    [InlineData("status", SamplePatchDemo.Kind.Status)]
    [InlineData("on", SamplePatchDemo.Kind.On)]
    [InlineData(" ON ", SamplePatchDemo.Kind.On)]
    [InlineData("off", SamplePatchDemo.Kind.Off)]
    [InlineData("Ping", SamplePatchDemo.Kind.Ping)]
    [InlineData("group", SamplePatchDemo.Kind.Group)]
    [InlineData(" UNSAFE ", SamplePatchDemo.Kind.Unsafe)]
    internal void Known_tokens_parse(string token, SamplePatchDemo.Kind expected)
    {
        Assert.True(SamplePatchDemo.TryParse(token, out var kind));
        Assert.Equal(expected, kind);
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("enable")]
    [InlineData("1")]
    public void An_unknown_token_is_refused_so_the_command_replies_usage(string token)
    {
        Assert.False(SamplePatchDemo.TryParse(token, out _));
        Assert.Equal("Usage: .patchdemo on | off | ping | group | unsafe | status", SamplePatchDemo.UsageReply);
    }

    [Fact]
    // FHARM5: the reply used to warn that the demo pinned the Sample's ALC for the process
    // lifetime. It no longer does — the body lives in a Bootstrap patch slot, so `off` and every
    // unload release it — and the reply now has to say where the body actually is, because that
    // is the thing a reader would otherwise get wrong when copying the demo.
    public void The_install_reply_names_the_target_the_id_and_where_the_body_lives()
    {
        string reply = SamplePatchDemo.OnReply(installed: true, id: 7);

        Assert.Contains(SamplePatchDemo.TargetName, reply);
        Assert.Contains("patch id 7", reply);
        Assert.Contains("Bootstrap patch slot", reply);
        Assert.DoesNotContain("pins the Sample's ALC", reply);
    }

    [Fact]
    public void A_refused_install_says_nothing_was_registered()
    {
        string reply = SamplePatchDemo.OnReply(installed: false, id: 0);

        Assert.Contains("NOT installed", reply);
        Assert.Contains("nothing was registered", reply);
        Assert.DoesNotContain("patch id", reply);
    }

    [Fact]
    public void The_off_reply_reports_the_RemoveAll_count_and_the_unload_rule()
    {
        string reply = SamplePatchDemo.OffReply(1);

        Assert.Contains("ctx.Patches.RemoveAll() unpatched 1 patch(es)", reply);
        Assert.Contains("on every unload reason", reply);
    }

    [Fact]
    public void Ping_distinguishes_not_sent_not_armed_fired_and_did_not_fire()
    {
        Assert.Contains("nothing was sent", SamplePatchDemo.PingReply(armed: true, sent: false, 0, 0));
        Assert.Contains(".patchdemo on first", SamplePatchDemo.PingReply(armed: false, sent: true, 0, 0));
        Assert.Contains("the prefix fired (fires=1)", SamplePatchDemo.PingReply(armed: true, sent: true, 0, 1));
        Assert.Contains("did NOT fire", SamplePatchDemo.PingReply(armed: true, sent: true, 1, 1));
    }

    [Fact]
    public void Recording_a_fire_counts_it_and_writes_one_greppable_line()
    {
        Assert.Equal(0, SamplePatchDemo.Fires);

        Assert.Equal(1, SamplePatchDemo.RecordFire());
        Assert.Equal(2, SamplePatchDemo.RecordFire());

        Assert.Equal(2, SamplePatchDemo.Fires);
        Assert.Equal(2, _lines.Count);
        Assert.StartsWith("Sample patchdemo: prefix fired on " + SamplePatchDemo.TargetName, _lines[0]);
        Assert.EndsWith("fires=2", _lines[1]);
    }

    [Fact]
    public void Detach_forgets_the_count_so_a_new_generation_starts_at_zero()
    {
        SamplePatchDemo.RecordFire();
        SamplePatchDemo.RecordPostfix();
        SamplePatchDemo.RecordFinalizer(new InvalidOperationException("boom"));
        SamplePatchDemo.Detach();

        Assert.Equal(0, SamplePatchDemo.Fires);
        Assert.Equal(0, SamplePatchDemo.PostfixFires);
        Assert.Equal(0, SamplePatchDemo.FinalizerFires);
        Assert.Equal(0, SamplePatchDemo.FinalizerExceptions);
        Assert.False(SamplePatchDemo.Armed);
        Assert.False(SamplePatchDemo.AnyArmed);
    }

    // ---- Job J14: the postfix and the finalizer ----

    [Fact]
    public void The_postfix_has_its_own_counter_and_its_own_greppable_line()
    {
        Assert.Equal(1, SamplePatchDemo.RecordPostfix());
        Assert.Equal(2, SamplePatchDemo.RecordPostfix());

        Assert.Equal(2, SamplePatchDemo.PostfixFires);
        Assert.Equal(0, SamplePatchDemo.Fires);
        Assert.StartsWith("Sample patchdemo: postfix ran after " + SamplePatchDemo.TargetName, _lines[0]);
        Assert.EndsWith("postfix=2", _lines[1]);
    }

    [Fact]
    public void A_clean_return_counts_a_finalizer_run_but_not_an_exception()
    {
        Assert.Equal(1, SamplePatchDemo.RecordFinalizer(null));

        Assert.Equal(1, SamplePatchDemo.FinalizerFires);
        Assert.Equal(0, SamplePatchDemo.FinalizerExceptions);
        Assert.Contains("clean return", _lines[0], StringComparison.Ordinal);
        Assert.DoesNotContain("OBSERVED", _lines[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// The point of the finalizer example: an exception is counted and named, and the log line
    /// says it was handed back. A demo that swallowed would be the worst thing here to copy.
    /// </summary>
    [Fact]
    public void An_observed_exception_is_counted_and_named_and_said_to_be_returned_unchanged()
    {
        SamplePatchDemo.RecordFinalizer(new InvalidOperationException("the game threw"));

        Assert.Equal(1, SamplePatchDemo.FinalizerFires);
        Assert.Equal(1, SamplePatchDemo.FinalizerExceptions);
        Assert.Contains("finalizer OBSERVED an exception", _lines[0], StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException", _lines[0], StringComparison.Ordinal);
        Assert.Contains("the game threw", _lines[0], StringComparison.Ordinal);
        Assert.Contains("returned unchanged, not swallowed", _lines[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// A seam that throws every frame must not turn the demo into a log flood, so the exception
    /// line is written once per Core generation while the counter keeps counting.
    /// </summary>
    [Fact]
    public void The_exception_line_is_written_once_per_generation_while_the_counter_keeps_counting()
    {
        for (int i = 0; i < 5; i++)
            SamplePatchDemo.RecordFinalizer(new InvalidOperationException("boom " + i));

        Assert.Equal(5, SamplePatchDemo.FinalizerExceptions);
        Assert.Single(_lines);

        SamplePatchDemo.ResetCount();
        SamplePatchDemo.RecordFinalizer(new InvalidOperationException("after the reset"));
        Assert.Equal(2, _lines.Count);
    }

    /// <summary>
    /// An exception message is not the mod author's text — it can carry a newline, and a newline
    /// in a log line is a forged log line.
    /// </summary>
    [Fact]
    public void An_exception_message_cannot_forge_a_log_line()
    {
        string line = SamplePatchDemo.FinalizerExceptionLine("Ex", "one\nSample patchdemo: forged");

        Assert.DoesNotContain("\n", line, StringComparison.Ordinal);
    }

    [Fact]
    public void An_exception_message_is_cut_rather_than_printed_whole()
    {
        string line = SamplePatchDemo.FinalizerExceptionLine(
            "Ex", new string('x', SamplePatchDemo.ExceptionMessageChars + 40));

        Assert.Contains("…", line, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('x', SamplePatchDemo.ExceptionMessageChars + 1), line, StringComparison.Ordinal);
    }

    [Fact]
    public void The_install_reply_reports_the_three_kinds_separately()
    {
        string all = SamplePatchDemo.KindsReply(prefix: true, postfix: true, finalizer: true);
        Assert.Contains("Kinds installed 3/3", all, StringComparison.Ordinal);
        Assert.Contains("prefix=yes postfix=yes finalizer=yes", all, StringComparison.Ordinal);

        string partial = SamplePatchDemo.KindsReply(prefix: true, postfix: false, finalizer: true);
        Assert.Contains("Kinds installed 2/3", partial, StringComparison.Ordinal);
        Assert.Contains("postfix=no", partial, StringComparison.Ordinal);
        Assert.Contains("not a group", partial, StringComparison.Ordinal);
    }

    [Fact]
    public void The_counters_reply_names_all_three_and_says_the_exceptions_went_back()
    {
        string reply = SamplePatchDemo.KindCountsReply(3, 3, 3, 1);

        Assert.Contains("prefix=3 postfix=3 finalizer=3", reply, StringComparison.Ordinal);
        Assert.Contains("of which 1 observed an exception", reply, StringComparison.Ordinal);
        Assert.Contains("returned to the game unchanged", reply, StringComparison.Ordinal);
    }

    /// <summary>
    /// The finalizer body is behind <c>!PROGENITOR_TESTS</c> (it is a <c>SlotFinalizer</c> over a
    /// game method), so the one thing that must never change about it is pinned in the source:
    /// it returns the exception it was handed. <c>return null;</c> there would make a real throw
    /// vanish and the game's caller would carry on as though the call had succeeded.
    /// </summary>
    [Fact]
    public void The_finalizer_body_returns_the_exception_it_was_handed()
    {
        string src = Source();

        SourcePin.AssertStatement(src, "private static Exception? FinalizerBody(object? instance, object?[] args, Exception? exception)");
        SourcePin.AssertStatementExactly(src, "return exception;");
        Assert.DoesNotContain("return null; // finalizer", src, StringComparison.Ordinal);
    }

    /// <summary>All three kinds are registered from the one <c>on</c>, through <c>ctx.Patches</c>.</summary>
    [Fact]
    public void On_installs_a_postfix_and_a_finalizer_next_to_the_prefix()
    {
        string src = Source();

        SourcePin.AssertStatementExactly(src, "var postfixHandle = plugin.Patches.Postfix(target, PostfixBody);");
        SourcePin.AssertStatementExactly(src, "var finalizerHandle = plugin.Patches.Finalizer(target, FinalizerBody);");
    }

    private static string Source()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, "samples", "Progenitor.Sample", "SamplePatchDemo.cs");
            if (File.Exists(candidate))
                return File.ReadAllText(candidate).Replace("\r\n", "\n");
            dir = dir.Parent;
        }

        throw new FileNotFoundException("samples/Progenitor.Sample/SamplePatchDemo.cs not found from " + AppContext.BaseDirectory);
    }

    [Fact]
    public void The_status_reply_says_whether_a_prefix_is_installed()
    {
        Assert.StartsWith("No prefix installed on", SamplePatchDemo.StatusReply(armed: false, fires: 0));
        Assert.StartsWith("Prefix installed on", SamplePatchDemo.StatusReply(armed: true, fires: 3));
        Assert.Contains("fired 3 time(s)", SamplePatchDemo.StatusReply(armed: true, fires: 3));
    }

    // ---- Job J21: PatchRegistry.All, and the ctx.UnsafeHarmony refusal ----

    /// <summary>
    /// The point of the refused group is that the live patch count is the same either side of it.
    /// If the reply printed only "refused" a reader would have no way to see the rollback happen.
    /// </summary>
    [Fact]
    public void The_refused_group_reply_shows_the_live_count_unchanged_and_names_the_bad_member()
    {
        string reply = SamplePatchDemo.GroupRefusedReply(refused: true, liveBefore: 3, liveAfter: 3);

        Assert.Contains("Group 1 of 2 refused, as designed", reply, StringComparison.Ordinal);
        Assert.Contains(SamplePatchDemo.GroupPrefixLabel, reply, StringComparison.Ordinal);
        Assert.Contains(SamplePatchDemo.RefusedMemberLabel, reply, StringComparison.Ordinal);
        Assert.Contains(SamplePatchDemo.MissingTargetName, reply, StringComparison.Ordinal);
        Assert.Contains("before=3 after=3", reply, StringComparison.Ordinal);
        Assert.Contains("PatchRegistry.All returned null", reply, StringComparison.Ordinal);
    }

    /// <summary>
    /// A group that was supposed to be refused and was not is a demo bug, and the reply says so
    /// rather than reading like a success.
    /// </summary>
    [Fact]
    public void A_group_that_should_have_been_refused_and_was_not_reads_as_a_bug()
    {
        string reply = SamplePatchDemo.GroupRefusedReply(refused: false, liveBefore: 3, liveAfter: 5);

        Assert.Contains("supposed to be refused REGISTERED", reply, StringComparison.Ordinal);
        Assert.Contains("bug in this demo", reply, StringComparison.Ordinal);
    }

    [Fact]
    public void The_landed_group_reply_names_both_members_and_the_live_count()
    {
        string reply = SamplePatchDemo.GroupLandedReply(handles: 2, liveAfter: 5);

        Assert.Contains("Group 2 of 2 registered 2 member(s)", reply, StringComparison.Ordinal);
        Assert.Contains(SamplePatchDemo.GroupPrefixLabel, reply, StringComparison.Ordinal);
        Assert.Contains(SamplePatchDemo.GroupPostfixLabel, reply, StringComparison.Ordinal);
        Assert.Contains("live patches=5", reply, StringComparison.Ordinal);

        Assert.Contains("nothing is registered", SamplePatchDemo.GroupLandedReply(0, 3), StringComparison.Ordinal);
    }

    [Fact]
    public void The_group_counter_line_is_separate_from_the_three_single_registrations()
    {
        Assert.Contains("No PatchRegistry.All group is registered",
            SamplePatchDemo.GroupCountsReply(0, 0), StringComparison.Ordinal);
        Assert.Contains("2 member(s) live, fired 7 time(s)",
            SamplePatchDemo.GroupCountsReply(2, 7), StringComparison.Ordinal);
    }

    [Fact]
    public void A_group_member_fire_is_counted_and_named_in_its_own_log_line()
    {
        Assert.Equal(0, SamplePatchDemo.GroupFires);

        Assert.Equal(1, SamplePatchDemo.RecordGroupFire(SamplePatchDemo.GroupPrefixLabel));
        Assert.Equal(2, SamplePatchDemo.RecordGroupFire(SamplePatchDemo.GroupPostfixLabel));

        Assert.Equal(2, SamplePatchDemo.GroupFires);
        Assert.Equal(0, SamplePatchDemo.Fires);
        Assert.Equal(0, SamplePatchDemo.PostfixFires);
        Assert.Contains("group member 'sample.group.prefix' fired group=1", _lines[0], StringComparison.Ordinal);
        Assert.Contains("group member 'sample.group.postfix' fired group=2", _lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void Detach_forgets_the_group_counter_too()
    {
        SamplePatchDemo.RecordGroupFire(SamplePatchDemo.GroupPrefixLabel);
        SamplePatchDemo.Detach();

        Assert.Equal(0, SamplePatchDemo.GroupFires);
        Assert.Equal(0, SamplePatchDemo.GroupMembers);
    }

    /// <summary>
    /// <c>PatchRegistry.All</c> is the static form and does not attach the applier, so a demo that
    /// ran it on a cold process would show every member refused rather than a rollback. The
    /// warm-up reply has to explain that, or the log line it produces looks like a bug.
    /// </summary>
    [Fact]
    public void The_warm_up_reply_explains_why_the_static_form_needs_one()
    {
        Assert.Contains("PatchRegistry.All (the static form) does not",
            SamplePatchDemo.GroupWarmedReply, StringComparison.Ordinal);
        Assert.Contains("disposed it", SamplePatchDemo.GroupWarmedReply, StringComparison.Ordinal);
    }

    /// <summary>
    /// The <c>ctx.UnsafeHarmony</c> example is a refusal, and the reply is the whole example: it
    /// has to name the cost (the read, not the patch) and point at the alternative.
    /// </summary>
    [Fact]
    public void The_unsafe_reply_says_the_read_itself_is_the_opt_out_and_names_the_alternative()
    {
        string reply = SamplePatchDemo.UnsafeReply;

        Assert.StartsWith("The Sample never reads ctx.UnsafeHarmony.", reply);
        Assert.Contains("not patching through it", reply, StringComparison.Ordinal);
        Assert.Contains("non-hot-swappable", reply, StringComparison.Ordinal);
        Assert.Contains("Use ctx.Patches", reply, StringComparison.Ordinal);
    }

    /// <summary>
    /// And the refusal is implemented, not only described: no file in the Sample reads the
    /// property. A future job that "just tries it" makes this test red rather than making the
    /// whole plugin non-hot-swappable and every other example in it unreachable.
    /// </summary>
    [Fact]
    public void No_Sample_source_file_reads_ctx_UnsafeHarmony()
    {
        string root = Path.Combine(SampleRoot(), "samples", "Progenitor.Sample");
        foreach (string file in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                continue;
            string body = WithoutStringLiterals(File.ReadAllText(file).Replace("\r\n", "\n"));
            SourcePin.AssertNoStatementContaining(body, "UnsafeHarmony");
        }
    }

    /// <summary>
    /// Blank the inside of every double-quoted run, line by line, so the scan above sees code and
    /// not the reply text — which names <c>ctx.UnsafeHarmony</c> on purpose and would otherwise be
    /// the only hit. Deliberately crude: a verbatim or interpolated string with an embedded quote
    /// is not modeled, which can only make the scan see <em>more</em> text than it should, never
    /// less, so it cannot hide a real read.
    /// </summary>
    private static string WithoutStringLiterals(string body)
    {
        var sb = new System.Text.StringBuilder(body.Length);
        bool inString = false;
        for (int i = 0; i < body.Length; i++)
        {
            char c = body[i];
            if (c == '\n') { inString = false; sb.Append(c); continue; }
            if (c == '"' && (i == 0 || body[i - 1] != '\\')) { inString = !inString; sb.Append(c); continue; }
            sb.Append(inString ? ' ' : c);
        }

        return sb.ToString();
    }

    /// <summary>The atomic group is registered through the static, explicitly-owned entry point.</summary>
    [Fact]
    public void The_group_goes_through_PatchRegistry_All_with_the_plugins_own_owner_pair()
    {
        string src = Source();

        SourcePin.AssertStatement(src, "var refused = PatchRegistry.All(");
        SourcePin.AssertStatement(src, "var landed = PatchRegistry.All(");
        SourcePin.AssertStatementExactly(src, "plugin.Owner,");
        SourcePin.AssertStatementExactly(src, "plugin.Guid,");
    }

    /// <summary>
    /// The refused member's target is resolved and comes back null; hard-coding <c>null</c> would
    /// still refuse the group but would stop looking like the thing it stands in for — a site that
    /// moved in a game patch.
    /// </summary>
    [Fact]
    public void The_refused_member_asks_for_a_method_that_is_looked_up_and_not_found()
    {
        string src = Source();

        SourcePin.AssertStatement(src, "private static MethodBase? ResolveMissingTarget()");
        SourcePin.AssertStatement(src, "new PatchRequest(ResolveMissingTarget(), (SlotPostfix)GroupPostfixBody, PatchKind.Postfix, 400, RefusedMemberLabel));");
    }

    private static string SampleRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "samples", "Progenitor.Sample")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("samples/Progenitor.Sample not found from " + AppContext.BaseDirectory);
    }
}
