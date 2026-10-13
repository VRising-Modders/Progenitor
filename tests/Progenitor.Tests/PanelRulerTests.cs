using System;
using System.IO;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Job FDOC5, closing R43 finding 5 — raised again as R45 finding 38 after the batch between them
/// rewrote 1,078 lines of the file it lives in without touching it.
///
/// <para>The defect was a fallback chain that reached for <c>Screen.height</c> under a doc comment
/// that condemned <c>Screen.height</c> three lines above, with nothing in the log saying which
/// rung had answered. On a 4K display with the game's UI scaled up, a canvas ~1080 units tall and
/// a <c>Screen.height</c> of 2160 capped the panel at 1728 canvas units inside a 1080-unit canvas,
/// and the layout line printed <c>panel=1728px (fits)</c> — evidence 163's symptom, reintroduced
/// by the fallback of the fix for it.</para>
/// </summary>
public class PanelRulerTests
{
    /// <summary>A canvas that answers is always the ruler, whatever else is readable.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_readable_canvas_is_the_ruler(bool designSpace)
    {
        var r = PanelRuler.Choose(canvas: 1080f, designSpace: designSpace, screen: 2160f, constant: 999f);

        Assert.Equal(UiRuler.Canvas, r.Ruler);
        Assert.Equal(1080f, r.Value);
        Assert.Equal("canvas", r.Name);
    }

    /// <summary>
    /// The heart of R43 finding 5. With the game's scaler adopted, one canvas unit is not one
    /// device pixel, so <c>Screen.height</c> is not a worse measurement of the canvas — it is not
    /// a measurement of the canvas at all, and 0.8 × 2160 inside a 1080-unit canvas is a panel
    /// twice as tall as the screen with <c>(fits)</c> beside it. The constant is wrong too, and
    /// wrong in a way a reader can see: it is 1080 whatever the display is doing.
    /// </summary>
    [Fact]
    public void In_design_space_an_unreadable_canvas_never_falls_back_to_pixels()
    {
        var r = PanelRuler.Choose(canvas: 0f, designSpace: true, screen: 2160f, constant: 1080f);

        Assert.Equal(UiRuler.Constant, r.Ruler);
        Assert.Equal(1080f, r.Value);
        Assert.Equal("constant", r.Name);
        Assert.NotEqual(2160f, r.Value);
    }

    /// <summary>
    /// ...and the rung is kept for the case where it is genuinely the same unit: no vanilla
    /// <c>CanvasScaler</c> could be copied, so <c>AdoptGameScaler</c> left the overlay at
    /// <c>ConstantPixelSize</c> with <c>scaleFactor = 1</c> and a canvas unit really is a pixel.
    /// Deleting the rung outright would replace a real measurement with 1080 there.
    /// </summary>
    [Fact]
    public void Outside_design_space_the_screen_is_a_real_measurement()
    {
        var r = PanelRuler.Choose(canvas: 0f, designSpace: false, screen: 2160f, constant: 1080f);

        Assert.Equal(UiRuler.Screen, r.Ruler);
        Assert.Equal(2160f, r.Value);
        Assert.Equal("screen", r.Name);
    }

    /// <summary>Nothing readable at all is the constant, and says so.</summary>
    [Fact]
    public void Nothing_readable_is_the_constant_and_is_named_as_such()
    {
        var r = PanelRuler.Choose(canvas: 0f, designSpace: false, screen: 0f, constant: 1080f);

        Assert.Equal(UiRuler.Constant, r.Ruler);
        Assert.Equal(1080f, r.Value);
        Assert.Equal("constant", r.Name);
    }

    /// <summary>
    /// The three rungs are distinguishable in the log. R43 asked for exactly one field — the
    /// point of the finding is that an invisible mode becomes a diagnosable one — so the names
    /// must not collide.
    /// </summary>
    [Fact]
    public void The_three_rungs_have_three_names()
    {
        string canvas = PanelRuler.Choose(1080f, true, 0f, 1080f).Name;
        string screen = PanelRuler.Choose(0f, false, 2160f, 1080f).Name;
        string constant = PanelRuler.Choose(0f, true, 2160f, 1080f).Name;

        Assert.NotEqual(canvas, screen);
        Assert.NotEqual(canvas, constant);
        Assert.NotEqual(screen, constant);
    }

    /// <summary>
    /// The production wiring, and the half R43 said turns an invisible mode into a diagnosable
    /// one. <c>UnityUiRuntime</c> binds Unity and cannot be compiled here, so this is a source
    /// pin — and it is the strict form, because the assertion is that the layout line carries the
    /// ruler <em>unconditionally</em>: a <c>ruler=</c> that only prints on the canvas rung would
    /// be silent in precisely the case the field exists for.
    /// </summary>
    [Fact]
    public void The_layout_line_names_the_ruler_and_reports_canvas_units()
    {
        string runtime = File.ReadAllText(Path.Combine(
            SourcePin.RepoRoot(), "src", "Progenitor.Core", "Ui", "UnityUiRuntime.cs"));
        string body = SourcePin.MethodBody(runtime, "private void RunLayout(");

        // One reading, taken once and carried, so the cap and the line cannot describe different
        // measurements of the same frame.
        SourcePin.AssertStatementExactly(body, "RulerReading available = MeasureHeight();");
        // The version footer's band (PanelFooterLayout) is added to what the frame is sized for,
        // never to the content rect, and the cap still reads the one carried measurement.
        SourcePin.AssertUncommented(body, "PanelLayout.PanelHeightFor(contentHeight + footerBand, available.Value)");

        // The field itself, and the unit correction beside it.
        SourcePin.AssertUncommented(body, "ruler=\" + available.Name");
        SourcePin.AssertUncommented(body, "+ \"u panel=\"");

        // The `px` suffix on two canvas-unit numbers was R45 finding 38's second half: the same
        // unit confusion the fallback chain had, stated as fact in the diagnostic line.
        Assert.DoesNotContain("px panel=", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// And the rung is decided in one place. <c>AvailableHeight</c> / <c>AvailableWidth</c> used
    /// to carry a hand-written chain each, which is how the two of them ended up with the same
    /// defect twice; both go through <see cref="PanelRuler"/> now, and no raw <c>Screen.</c> read
    /// decides anything outside the two reader methods that exist to feed it.
    /// </summary>
    [Fact]
    public void Both_axes_go_through_the_one_chain()
    {
        string runtime = File.ReadAllText(Path.Combine(
            SourcePin.RepoRoot(), "src", "Progenitor.Core", "Ui", "UnityUiRuntime.cs"));

        SourcePin.AssertStatementExactly(runtime, "private float AvailableHeight() => MeasureHeight().Value;");
        SourcePin.AssertStatementExactly(runtime, "private float AvailableWidth() => MeasureWidth().Value;");

        // Screen.height / Screen.width are read in exactly one place: ReadScreenExtent, which
        // measures and decides nothing. Anywhere else is a second chain.
        string reader = SourcePin.MethodBody(runtime, "private static float ReadScreenExtent(");
        SourcePin.AssertUncommented(reader, "Screen.height");
        SourcePin.AssertUncommented(reader, "Screen.width");

        int screenReads = 0;
        foreach (string raw in runtime.Split('\n'))
        {
            string trimmed = raw.Trim();
            if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith("///", StringComparison.Ordinal))
                continue;
            if (trimmed.Contains("Screen.height") || trimmed.Contains("Screen.width")) screenReads++;
        }

        // Three lines, and each is accounted for: the one inside ReadScreenExtent, and the two in
        // the pointer-bounds test, which is a genuinely different question — is the cursor outside
        // the window — asked in real device pixels, where Screen is the right ruler. Anything more
        // is a second chain and this test should be read again before the number is changed.
        Assert.Equal(3, screenReads);
    }

    // ---- job FUI32: the rung FDOC5 predicted would answer, and never did ---------------------
    //
    // FDOC5 owed one live check: the layout line had to read `ruler=canvas`. It read
    // `ruler=constant` on every panel, on every screen, across two Core generations and two window
    // sizes -- so every panel was sized from FallbackScreenHeight (0.8 x 1080 = 864u where
    // 0.8 x 1024 = 819u was meant) and was right only on a 1080-tall display, by coincidence.
    //
    // The cause is not a race. CreatePanel gives each panel a canvas of its own and
    // SettingsPanel.Open fills and shows it in the same Plugin.Frame tick, so the layout pass runs
    // during Update on a canvas created during that same Update; a root ScreenSpaceOverlay canvas
    // is sized by Unity in PostLateUpdate, after every Update. The rect is therefore zero for the
    // whole creation frame, deterministically. One frame of deferral is enough on this build and
    // that is checkable rather than hoped: this uGUI's CanvasScaler has no Update and recomputes
    // from Canvas.preWillRenderCanvases, which fires immediately before
    // the same end-of-frame canvas update -- so AdoptGameScaler's ScaleWithScreenSize is already
    // in effect when the rect is first sized, and the first readable rect is design space.

    /// <summary>
    /// Only the canvas is a final answer. Both fallbacks measure something that is not this
    /// canvas, so a pass that used one is provisional and must be taken again — including the
    /// <c>Screen</c> rung, which is a real measurement of the wrong thing rather than no
    /// measurement at all.
    /// </summary>
    [Fact]
    public void Only_a_canvas_reading_is_final()
    {
        Assert.False(RulerSettle.Owed(PanelRuler.Choose(1024f, true, 2160f, 1080f)));
        Assert.True(RulerSettle.Owed(PanelRuler.Choose(0f, true, 2160f, 1080f)));
        Assert.True(RulerSettle.Owed(PanelRuler.Choose(0f, false, 2160f, 1080f)));
    }

    /// <summary>
    /// The wait ends the moment the canvas answers, and it ends at the budget rather than never.
    /// The budget is checked <i>after</i> the reading on purpose: a canvas that becomes readable
    /// on the last allowed frame is used, not thrown away for being late.
    /// </summary>
    [Fact]
    public void The_wait_ends_on_an_answer_and_at_the_budget()
    {
        var canvas = PanelRuler.Choose(1024f, true, 0f, 1080f);
        var fallback = PanelRuler.Choose(0f, true, 2160f, 1080f);

        Assert.Equal(RulerSettleStep.Wait, RulerSettle.Step(fallback, framesWaited: 1, budget: 8));
        Assert.Equal(RulerSettleStep.Relayout, RulerSettle.Step(canvas, framesWaited: 1, budget: 8));

        // On the last allowed frame, an answer still wins over the budget.
        Assert.Equal(RulerSettleStep.Relayout, RulerSettle.Step(canvas, framesWaited: 8, budget: 8));
        Assert.Equal(RulerSettleStep.GiveUp, RulerSettle.Step(fallback, framesWaited: 8, budget: 8));

        // And a spent budget never silently becomes a wait again.
        Assert.Equal(RulerSettleStep.GiveUp, RulerSettle.Step(fallback, framesWaited: 99, budget: 8));
    }

    /// <summary>
    /// The four states a layout line can report about <i>when</i> it was measured, and the reason
    /// there are four rather than two. "The canvas answered" is not one fact: a first pass that
    /// answered is <c>final</c> and a re-measure that answered is <c>settled</c>, and only the
    /// second tells a reader that the line above it in the log was taken at the wrong size.
    /// </summary>
    [Fact]
    public void The_settle_state_says_whether_an_earlier_pass_was_wrong()
    {
        var canvas = PanelRuler.Choose(1024f, true, 0f, 1080f);
        var fallback = PanelRuler.Choose(0f, true, 2160f, 1080f);

        Assert.Equal(RulerSettleState.Final, RulerSettle.StateOf(canvas, settling: false, exhausted: false));
        Assert.Equal(RulerSettleState.Settled, RulerSettle.StateOf(canvas, settling: true, exhausted: false));
        Assert.Equal(RulerSettleState.Armed, RulerSettle.StateOf(fallback, settling: false, exhausted: false));
        Assert.Equal(RulerSettleState.GaveUp, RulerSettle.StateOf(fallback, settling: false, exhausted: true));
    }

    /// <summary>
    /// Four states, four tokens, and four canvas reasons with four tokens — for the same reason
    /// R43 wanted three distinct rung names. A diagnostic whose words collide reports an invisible
    /// mode as a different invisible mode.
    /// </summary>
    [Fact]
    public void Every_settle_state_and_every_canvas_reason_has_its_own_token()
    {
        string[] states =
        {
            RulerSettle.Name(RulerSettleState.Final),
            RulerSettle.Name(RulerSettleState.Armed),
            RulerSettle.Name(RulerSettleState.Settled),
            RulerSettle.Name(RulerSettleState.GaveUp),
        };
        Assert.Equal(4, new System.Collections.Generic.HashSet<string>(states).Count);

        string[] reads =
        {
            RulerSettle.Name(CanvasRead.Sized),
            RulerSettle.Name(CanvasRead.Zero),
            RulerSettle.Name(CanvasRead.NoRect),
            RulerSettle.Name(CanvasRead.Threw),
        };
        Assert.Equal(4, new System.Collections.Generic.HashSet<string>(reads).Count);

        // "Unity has not sized it yet" is the one a deferred re-measure fixes; "there is no rect"
        // and "the read threw" are structural and interop defects that it cannot. Establishing
        // which of the three is happening is the whole reason this token exists, so a reading that
        // cannot be told apart from the others is the defect, not a tidiness complaint.
        Assert.NotEqual(RulerSettle.Name(CanvasRead.Zero), RulerSettle.Name(CanvasRead.Threw));
    }

    /// <summary>
    /// The line that joins the two layout lines. Constraint from the job: if a panel is laid out
    /// before the canvas is readable and re-laid out after, the log must <b>say so</b> rather than
    /// printing only the final answer — so this line has to carry both readings, both rung names,
    /// and a statement that the earlier pass was wrong.
    /// </summary>
    [Fact]
    public void The_settle_line_carries_both_readings_and_says_the_first_was_wrong()
    {
        var was = PanelRuler.Choose(0f, true, 2160f, 1080f);
        var now = PanelRuler.Choose(1024f, true, 2160f, 1080f);

        string line = RulerSettle.Describe("Progenitor Settings", was, now, frames: 1);

        Assert.Contains("Progenitor Settings", line, StringComparison.Ordinal);
        Assert.Contains("avail=1080u ruler=constant", line, StringComparison.Ordinal);
        Assert.Contains("avail=1024u ruler=canvas", line, StringComparison.Ordinal);
        Assert.Contains("wrong ruler", line, StringComparison.Ordinal);
        Assert.Contains("1 frame", line, StringComparison.Ordinal);
        Assert.DoesNotContain("1 frames", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// And when the canvas never answers, the last thing said about the panel is that its size is
    /// a guess — with the reason the canvas could not be read, which is what separates "Unity has
    /// not sized it" from "the read threw".
    /// </summary>
    [Fact]
    public void The_give_up_line_names_the_guess_and_the_reason()
    {
        var was = PanelRuler.Choose(0f, true, 2160f, 1080f);

        string line = RulerSettle.DescribeGiveUp("Progenitor Settings", was, CanvasRead.Zero, frames: 8);

        Assert.Contains("avail=1080u ruler=constant", line, StringComparison.Ordinal);
        Assert.Contains("canvas=zero", line, StringComparison.Ordinal);
        Assert.Contains("8 frames", line, StringComparison.Ordinal);
        Assert.Contains("guess", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// The production wiring of <em>when</em>, the half of this job that cannot be compiled here.
    /// Both tokens are pinned in the strict form because the assertion is that they print
    /// <em>unconditionally</em>: a <c>settle=</c> that appeared only on the provisional pass would
    /// leave a reader unable to tell a settled line from a line that never had to settle, which is
    /// the same shape of defect as a <c>ruler=</c> that printed only on the canvas rung.
    /// </summary>
    [Fact]
    public void The_layout_line_says_when_it_was_measured_and_why_the_canvas_was_not()
    {
        string body = SourcePin.MethodBody(
            SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "UnityUiRuntime.cs"),
            "private void RunLayout(");

        SourcePin.AssertStatementExactly(
            body, "var settle = RulerSettle.StateOf(available, _settling, _settleExhausted);");
        SourcePin.AssertStatementExactly(body, "+ \" canvas=\" + RulerSettle.Name(_canvasRead)");
        SourcePin.AssertStatementExactly(body, "+ \" settle=\" + RulerSettle.Name(settle)");
    }

    /// <summary>
    /// And the pass that admitted to being provisional is the pass that arms the re-measure. The
    /// pin is over <c>RunLayout</c>'s body rather than the file, because arming from anywhere else
    /// — a show, a reposition — would arm panels whose reading was already final and would not arm
    /// the one case that needs it.
    /// </summary>
    [Fact]
    public void A_provisional_pass_is_the_pass_that_arms_the_re_measure()
    {
        string body = SourcePin.MethodBody(
            SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "UnityUiRuntime.cs"),
            "private void RunLayout(");

        SourcePin.AssertStatementExactly(body, "if (settle == RulerSettleState.Armed) ArmSettle(this);");

        // And the budget is reset by the pass that means "a canvas this panel has never measured",
        // which is the show. A budget that survived a show would leave a panel opened once on a
        // screen whose canvas never sized on the fallback for the rest of the session, silently,
        // however many times it was re-opened.
        SourcePin.AssertStatementExactly(body, "if (firstShow) ResetSettleBudget();");
    }

    /// <summary>
    /// The frame pass itself: it re-reads (rather than re-laying out blindly), it names both
    /// readings before it replaces one with the other, and it is driven by <c>Plugin.Frame</c> —
    /// not by <c>ReadPointer</c>, which FUI30's probe uses and which is never called for a panel
    /// whose plugin did not register for the hover pass. A re-measure that only happened for
    /// hover-routed panels would leave exactly the panels this job measured on the fallback.
    /// </summary>
    [Fact]
    public void The_re_measure_is_frame_driven_and_reports_what_it_replaced()
    {
        string runtime = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "UnityUiRuntime.cs");

        string tick = SourcePin.MethodBody(runtime, "public bool SettleTick()");
        SourcePin.AssertStatementExactly(tick, "RulerReading now = MeasureHeight();");
        SourcePin.AssertStatementExactly(
            tick, "Plugin.Logger?.LogDiag(RulerSettle.Describe(Text, _laidOutWith, now, _settleFrames));");

        string arm = SourcePin.MethodBody(runtime, "private static void ArmSettle(");
        SourcePin.AssertStatementExactly(arm, "Plugin.Frame += SettlePump;");
    }
}
