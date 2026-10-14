using System;
using System.Collections.Generic;
using System.IO;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Job FUI26, R45 findings 2, 22 and 27.
///
/// <para>
/// The subject is the branch that decides whether a settings panel can be operated at all. FUI24
/// rebuilt the editor controls by cloning vanilla donors and re-checking the operability
/// references on the clone — correctly, because <c>Instantiate</c> remaps only what is inside the
/// copied subtree — and then <b>destroyed</b> a clone that failed and returned nothing. The build
/// path existed and was reachable only when there was no donor at all. Donors are pre-filtered and
/// cached per panel build, so one bad donor made every row of that type read-only: evidence 176's
/// defect verbatim, produced by the commit that fixed it, and silent, because the <c>source=</c>
/// line that would have said so sat past the branch that gave up.
/// </para>
/// </summary>
public class UiControlBuildTests
{
    /// <summary>A stand-in for a control: it either has the references that make it operable or it does not.</summary>
    private sealed class FakeControl
    {
        public FakeControl(string name, bool operable)
        {
            Name = name;
            Operable = operable;
        }

        public string Name { get; }

        public bool Operable { get; }

        public bool Destroyed { get; set; }
    }

    private sealed class Recorder
    {
        public readonly List<string> Calls = new();

        public readonly List<string> Dropped = new();

        public Func<FakeControl?> Clone(FakeControl? result) => () =>
        {
            Calls.Add("clone");
            return result;
        };

        public Func<FakeControl?> Build(FakeControl? result) => () =>
        {
            Calls.Add("build");
            return result;
        };

        public Action<FakeControl, bool> Drop => (c, wasClone) =>
        {
            c.Destroyed = true;
            Dropped.Add(c.Name + (wasClone ? ":clone" : ":built"));
        };
    }

    private static bool Operable(FakeControl c) => c.Operable;

    // ---- finding 2: the recovery reaches the failure it was written for -----------------------

    /// <summary>
    /// <b>The test this job exists for.</b> A live donor whose clone comes out inert must leave the
    /// row with a working control built from parts — not a destroyed clone and a null.
    /// </summary>
    [Fact]
    public void A_donor_that_fails_the_recheck_yields_a_built_control_rather_than_a_dead_row()
    {
        var recorder = new Recorder();
        var inertClone = new FakeControl("clone", operable: false);
        var goodBuild = new FakeControl("built", operable: true);

        var attempt = ControlBuildPlan.Resolve(
            recorder.Clone(inertClone), recorder.Build(goodBuild), Operable, recorder.Drop);

        Assert.Same(goodBuild, attempt.Control);
        Assert.Equal(ControlSource.BuiltAfterCloneRejected, attempt.Source);
        Assert.False(ControlBuildPlan.IsFailure(attempt.Source));

        // The build path was actually tried, and the rejected clone was not left on the row.
        Assert.Equal(new[] { "clone", "build" }, recorder.Calls);
        Assert.True(inertClone.Destroyed);
        Assert.Equal(new[] { "clone:clone" }, recorder.Dropped);
        Assert.False(goodBuild.Destroyed);
    }

    /// <summary>
    /// The recovery is not a rewrite of the borrow-first rule: an operable clone still wins, and
    /// the builder is never called, so the module keeps using the game's own art when it can.
    /// </summary>
    [Fact]
    public void An_operable_clone_is_used_and_the_builder_is_never_called()
    {
        var recorder = new Recorder();
        var clone = new FakeControl("clone", operable: true);

        var attempt = ControlBuildPlan.Resolve(
            recorder.Clone(clone),
            recorder.Build(new FakeControl("built", operable: true)),
            Operable,
            recorder.Drop);

        Assert.Same(clone, attempt.Control);
        Assert.Equal(ControlSource.Cloned, attempt.Source);
        Assert.Equal(new[] { "clone" }, recorder.Calls);
        Assert.Empty(recorder.Dropped);
    }

    /// <summary>No donor is the case that already worked, and it still reports plain <c>built</c>.</summary>
    [Fact]
    public void No_donor_builds_and_reports_built_not_a_rejection()
    {
        var recorder = new Recorder();
        var built = new FakeControl("built", operable: true);

        var attempt = ControlBuildPlan.Resolve(
            recorder.Clone(null), recorder.Build(built), Operable, recorder.Drop);

        Assert.Same(built, attempt.Control);
        Assert.Equal(ControlSource.Built, attempt.Source);
        Assert.False(ControlBuildPlan.CloneRejected(attempt.Source));
    }

    /// <summary>
    /// Both halves failing is the only state in which the row really does revert to a label, and
    /// it is distinguishable from "there was no donor" — which is the difference between "this
    /// screen has no vanilla checkbox" and "this screen's vanilla checkbox is a bad donor".
    /// </summary>
    [Fact]
    public void A_rejected_clone_and_a_rejected_build_are_reported_as_a_rejected_clone()
    {
        var recorder = new Recorder();
        var clone = new FakeControl("clone", operable: false);
        var built = new FakeControl("built", operable: false);

        var attempt = ControlBuildPlan.Resolve(
            recorder.Clone(clone), recorder.Build(built), Operable, recorder.Drop);

        Assert.Null(attempt.Control);
        Assert.Equal(ControlSource.NoneAfterCloneRejected, attempt.Source);
        Assert.True(ControlBuildPlan.IsFailure(attempt.Source));
        Assert.True(ControlBuildPlan.CloneRejected(attempt.Source));
        Assert.Equal(new[] { "clone:clone", "built:built" }, recorder.Dropped);
    }

    [Fact]
    public void No_donor_and_a_rejected_build_report_none_without_a_rejection()
    {
        var recorder = new Recorder();
        var built = new FakeControl("built", operable: false);

        var attempt = ControlBuildPlan.Resolve(
            recorder.Clone(null), recorder.Build(built), Operable, recorder.Drop);

        Assert.Null(attempt.Control);
        Assert.Equal(ControlSource.None, attempt.Source);
        Assert.Equal(new[] { "built:built" }, recorder.Dropped);
    }

    /// <summary>
    /// A donor lookup or an <c>Instantiate</c> that throws is the same as no donor — including in
    /// what gets reported, because nothing was rejected: there was nothing to reject.
    /// </summary>
    [Fact]
    public void A_throwing_clone_is_the_same_as_no_donor()
    {
        var built = new FakeControl("built", operable: true);

        var attempt = ControlBuildPlan.Resolve(
            () => throw new InvalidOperationException("Instantiate threw"),
            () => built,
            Operable,
            (_, _) => { });

        Assert.Same(built, attempt.Control);
        Assert.Equal(ControlSource.Built, attempt.Source);
    }

    /// <summary>
    /// A predicate that throws rejects. The three real predicates read Unity properties through
    /// the interop, where a destroyed or half-built object throws rather than answering, and the
    /// answer that keeps a panel operable is "not proven operable, try the other path".
    /// </summary>
    [Fact]
    public void A_throwing_predicate_rejects_and_the_fallback_still_runs()
    {
        var clone = new FakeControl("clone", operable: true);
        var built = new FakeControl("built", operable: true);
        bool first = true;

        var attempt = ControlBuildPlan.Resolve(
            () => clone,
            () => built,
            _ =>
            {
                if (first)
                {
                    first = false;
                    throw new InvalidOperationException("the clone's references threw");
                }

                return true;
            },
            (_, _) => { });

        Assert.Same(built, attempt.Control);
        Assert.Equal(ControlSource.BuiltAfterCloneRejected, attempt.Source);
    }

    /// <summary>A builder that throws gives up quietly; the caller gets a null, never an exception.</summary>
    [Fact]
    public void A_throwing_builder_gives_up_without_throwing()
    {
        var attempt = ControlBuildPlan.Resolve<FakeControl>(
            () => null,
            () => throw new InvalidOperationException("AddComponent threw"),
            Operable,
            (_, _) => { });

        Assert.Null(attempt.Control);
        Assert.Equal(ControlSource.None, attempt.Source);
    }

    /// <summary>A destroy that throws does not stop the fallback; the row matters more than the corpse.</summary>
    [Fact]
    public void A_throwing_drop_does_not_stop_the_fallback()
    {
        var built = new FakeControl("built", operable: true);

        var attempt = ControlBuildPlan.Resolve(
            () => new FakeControl("clone", operable: false),
            () => built,
            Operable,
            (_, _) => throw new InvalidOperationException("DestroyImmediate threw"));

        Assert.Same(built, attempt.Control);
        Assert.Equal(ControlSource.BuiltAfterCloneRejected, attempt.Source);
    }

    // ---- finding 2: the instrument reports the fallback rather than being skipped by it -------

    /// <summary>
    /// Every outcome has its own sentence, every sentence carries the <c>source=</c> grep string,
    /// and the two that only exist because a clone was rejected say so — that is the whole point
    /// of the line. Before this, a rejected clone printed nothing at all.
    /// </summary>
    [Fact]
    public void Every_outcome_has_its_own_source_line()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (ControlSource source in Enum.GetValues(typeof(ControlSource)))
        {
            string line = ControlBuildPlan.Describe("settings toggle", "toggle", source);
            Assert.Contains("Ui: settings toggle source=", line);
            Assert.True(seen.Add(line), "two outcomes share a sentence: " + source);
        }

        string recovered = ControlBuildPlan.Describe("settings toggle", "toggle", ControlSource.BuiltAfterCloneRejected);
        Assert.Contains("source=built-after-clone-rejected", recovered);
        Assert.Contains("cloned", recovered);
        Assert.Contains("built from parts", recovered);

        // And the two give-up outcomes are the only ones that may claim the row is a label.
        Assert.Contains("plain label", ControlBuildPlan.Describe("settings toggle", "toggle", ControlSource.None));
        Assert.Contains("plain label", ControlBuildPlan.Describe("settings toggle", "toggle", ControlSource.NoneAfterCloneRejected));
        Assert.DoesNotContain("plain label", recovered);
        Assert.DoesNotContain("plain label", ControlBuildPlan.Describe("settings toggle", "toggle", ControlSource.Cloned));
        Assert.DoesNotContain("plain label", ControlBuildPlan.Describe("settings toggle", "toggle", ControlSource.Built));
    }

    /// <summary>
    /// The level is part of the report: a clean clone or build is information, and every outcome
    /// that involved something coming out inert is a warning an operator should see.
    /// </summary>
    [Fact]
    public void Only_a_clean_clone_or_build_is_logged_as_information()
    {
        Assert.False(ControlBuildPlan.IsWarning(ControlSource.Cloned));
        Assert.False(ControlBuildPlan.IsWarning(ControlSource.Built));
        Assert.True(ControlBuildPlan.IsWarning(ControlSource.BuiltAfterCloneRejected));
        Assert.True(ControlBuildPlan.IsWarning(ControlSource.None));
        Assert.True(ControlBuildPlan.IsWarning(ControlSource.NoneAfterCloneRejected));
    }

    // ---- finding 2 at the call sites ----------------------------------------------------------

    /// <summary>
    /// The three builders drive the plan, and the <c>source=</c> line is emitted <b>before</b> the
    /// early return — otherwise the fallback is reported by nothing, which is the half of finding 2
    /// that made the first defect invisible. Asserted over the source because the call sites
    /// themselves are Unity code that cannot be linked into this assembly.
    /// </summary>
    [Theory]
    [InlineData("private static GameObject? MakeToggle(", "_toggleSourceLogged")]
    [InlineData("private static GameObject? MakeSlider(", "_sliderSourceLogged")]
    [InlineData("private static GameObject? MakeInput(", "_inputSourceLogged")]
    public void Each_control_builder_logs_its_source_before_it_can_give_up(string signature, string latch)
    {
        string body = MethodBody(UiSource("UnityUiRuntime.cs"), signature);

        SourcePin.AssertUncommented(body, "ControlBuildPlan.Resolve(");
        SourcePin.AssertStatement(body, "LogControlSource(ref " + latch);

        int logged = body.IndexOf("LogControlSource(", StringComparison.Ordinal);
        int gaveUp = body.IndexOf("return null", StringComparison.Ordinal);
        Assert.True(logged >= 0 && gaveUp > logged,
            signature + ": the source line must be emitted before the method can return null, or "
            + "the one state it was written to catch is the one state it never reports.");
    }

    /// <summary>
    /// The build-order rule survives the new path: the rejected clone is destroyed without being
    /// activated, and the single <c>Activate</c> still runs after the component is configured.
    /// <c>Toggle</c> applies its checkmark alpha and <c>TMP_InputField</c> creates its caret in
    /// <c>OnEnable</c>, so a control activated before its references are set is inert (evidence
    /// 176).
    /// </summary>
    [Theory]
    [InlineData("private static GameObject? MakeToggle(")]
    [InlineData("private static GameObject? MakeSlider(")]
    [InlineData("private static GameObject? MakeInput(")]
    public void The_control_is_activated_once_and_only_after_it_is_configured(string signature)
    {
        string body = MethodBody(UiSource("UnityUiRuntime.cs"), signature);

        int resolve = body.IndexOf("ControlBuildPlan.Resolve(", StringComparison.Ordinal);
        int activate = body.IndexOf("Activate(built)", StringComparison.Ordinal);
        Assert.True(resolve >= 0 && activate > resolve, signature + ": activation must follow the build");
        Assert.Equal(activate, body.LastIndexOf("Activate(", StringComparison.Ordinal));

        // And the builders still start from the inactive root, which is where the rule lives.
        string newControl = MethodBody(UiSource("UnityUiRuntime.cs"), "private static GameObject NewControl(");
        SourcePin.AssertStatement(newControl, "go.SetActive(false)");
    }

    // ---- finding 27: the latches are re-armed by the reset they belong to ---------------------

    /// <summary>
    /// R45 finding 27. <c>ResetSceneCaches</c> exists because a donor that only exists on certain
    /// screens must be looked for again on each panel build — so the clone-or-build answer can
    /// differ between two panels in one session, and the latch that reports it spans exactly that
    /// change. Left un-reset, the first panel built in the process decides what the line says for
    /// the life of the Core generation: a panel built on the main menu records <c>built</c>
    /// permanently and the question stays unanswered while looking answered.
    /// </summary>
    [Fact]
    public void The_source_latches_are_re_armed_by_the_scene_cache_reset()
    {
        string reset = MethodBody(UiSource("UnityUiRuntime.cs"), "private static void ResetSceneCaches(");

        foreach (string field in new[]
                 {
                     "_toggleDonorSearched", "_sliderDonorSearched", "_inputDonorSearched",
                     "_toggleSourceLogged", "_sliderSourceLogged", "_inputSourceLogged",
                 })
        {
            SourcePin.AssertStatement(reset, field + " = false");
        }
    }

    /// <summary>
    /// And that the reset is still reached from the one place that means "a fresh panel is being
    /// built", so re-arming them is once per panel rather than never or every row.
    /// </summary>
    [Fact]
    public void The_scene_cache_reset_runs_at_the_top_of_a_panel_build()
    {
        string create = MethodBody(UiSource("UnityUiRuntime.cs"), "public IRuntimePanel? CreatePanel(");
        AssertStatement(create, "ResetSceneCaches();");
    }

    // ---- finding 22: one owner of the scrollbar, and a verdict read from the owner ------------

    /// <summary>
    /// The layout pass no longer touches the bar. <c>ScrollRect</c>'s <c>AutoHide</c> is the owner
    /// (evidence 175), and <c>c9ee05d</c> left the hand-written activation beside it, which is two
    /// owners of one GameObject driven by two different measurements of the same quantity.
    /// </summary>
    [Fact]
    public void The_layout_pass_does_not_set_the_scrollbar_active()
    {
        string runtime = UiSource("UnityUiRuntime.cs");
        string pass = MethodBody(runtime, "private void RunLayout(");

        Assert.DoesNotContain("_scrollbar.SetActive(", pass);
        Assert.DoesNotContain("SetActive(scrolls)", runtime);

        // The owner is still declared, exactly once.
        SourcePin.AssertUncommented(runtime, "ScrollRect.ScrollbarVisibility.AutoHide");
        Assert.DoesNotContain("ScrollbarVisibility.Permanent", runtime);

        // And the verdict is recorded for a read-back rather than acted on.
        SourcePin.AssertStatement(pass, "_verdictScrolls = scrolls");
        SourcePin.AssertStatement(pass, "_verdictPending = _scrollbar != null");
    }

    /// <summary>
    /// The read-back is taken from the owner's own flag, on a later pass, and from
    /// <c>activeSelf</c> — <c>activeInHierarchy</c> is false for every descendant of a hidden
    /// panel regardless of what <c>ScrollRect</c> decided, which would make a closed panel report
    /// a disagreement that never happened.
    /// </summary>
    [Fact]
    public void The_read_back_reads_the_owners_own_flag_and_is_deferred_to_a_later_pass()
    {
        string runtime = UiSource("UnityUiRuntime.cs");
        string read = MethodBody(runtime, "private void ReadBackScrollbar(");

        SourcePin.AssertUncommented(read, "_scrollbar.activeSelf");
        Assert.DoesNotContain("activeInHierarchy", read);
        SourcePin.AssertUncommented(read, "ScrollbarState.Unreadable");

        AssertStatement(MethodBody(runtime, "private void RunLayout("), "ReadBackScrollbar(\"a re-layout\", endsVerdict: true);");
        AssertStatement(MethodBody(runtime, "public void Reposition("), "ReadBackScrollbar(\"a reposition\", endsVerdict: false);");

        // A panel shown once and never re-laid out or repositioned would otherwise carry its
        // pending verdict away unchecked, which is the single-panel case.
        AssertStatement(
            MethodBody(runtime, "public void Hide(IRuntimePanel panel)"),
            "try { host.ReadBackScrollbarNow(\"a hide\"); }");
    }

    /// <summary>
    /// The two computations are named for their owners and compared, rather than one being printed
    /// as if it were both — evidence 175's <c>Scrolls(211, 211) == false</c> beside a screen with a
    /// bar on it.
    /// </summary>
    /// <remarks>
    /// The state arrives as an <c>int</c> only because <see cref="ScrollbarState"/> is internal and
    /// an xUnit test method has to be public; it is cast back on the first line.
    /// </remarks>
    [Theory]
    [InlineData(true, (int)ScrollbarState.Shown, true)]
    [InlineData(false, (int)ScrollbarState.Hidden, true)]
    [InlineData(true, (int)ScrollbarState.Hidden, false)]
    [InlineData(false, (int)ScrollbarState.Shown, false)]
    public void The_read_back_agrees_only_when_both_halves_say_the_same_thing(
        bool ourScrolls, int state, bool agrees)
    {
        var observed = (ScrollbarState)state;
        Assert.Equal(agrees, ScrollbarVerdict.Agrees(ourScrolls, observed));
        Assert.Equal(!agrees, ScrollbarVerdict.IsWarning(ourScrolls, observed));

        string line = ScrollbarVerdict.Describe("Sample", ourScrolls, observed, "a re-layout");
        Assert.Contains("Sample", line);
        Assert.Contains(ourScrolls ? "(scrolls)" : "(fits)", line);
        Assert.Contains(agrees ? "they agree" : "they disagree", line);
    }

    /// <summary>
    /// An unreadable flag is reported as unknown and is never silently folded into "hidden". A
    /// diagnostic must not report a state it did not observe — which is the same rule the layout
    /// line broke by printing our arithmetic as the bar's answer.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void An_unreadable_scrollbar_is_reported_as_unknown(bool ourScrolls)
    {
        Assert.False(ScrollbarVerdict.Agrees(ourScrolls, ScrollbarState.Unreadable));
        Assert.True(ScrollbarVerdict.IsWarning(ourScrolls, ScrollbarState.Unreadable));

        string line = ScrollbarVerdict.Describe("Sample", ourScrolls, ScrollbarState.Unreadable, "a reposition");
        Assert.Contains("could not be read", line);
        Assert.Contains("unknown", line);
        Assert.DoesNotContain("they agree", line);
        Assert.DoesNotContain("they disagree", line);
    }

    // ---- source helpers ----------------------------------------------------------------------

    private static string UiSource(string fileName) =>
        File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Progenitor.Core", "Ui", fileName));

    // Job FDOC5: the three helpers below moved to SourcePin, where the next job that needs one
    // finds them instead of writing a fourth copy. These stay as the names this file already
    // uses, forwarding — there is exactly one implementation of each.
    private static void AssertStatement(string body, string statement) =>
        SourcePin.AssertStatement(body, statement);

    private static string MethodBody(string source, string signature) =>
        SourcePin.MethodBody(source, signature);

    private static string FindRepoRoot() => SourcePin.RepoRoot();
}
