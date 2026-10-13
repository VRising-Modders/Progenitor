using System;
using System.Collections.Generic;
using System.IO;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Job FUI30, over FMAN17's live check 3.
///
/// <para>
/// The deny panel drew nine correct rows on the <c>ConnectingView</c> disconnect screen and its
/// Close button drew nothing and could not be clicked. Every instrument the module had reported
/// success: the layout line counted the row, and <c>Ui: pointer entered a mod panel</c> fired —
/// which is <c>HoverCapture</c>'s own rect arithmetic and is true whether or not an
/// <c>EventSystem</c> exists. So there was no way to separate "swallowed" from "never raycast" from
/// "no EventSystem", and those three want three different fixes.
/// </para>
///
/// <para>
/// <b>What is and is not covered here.</b> <see cref="UiInteractionReport"/> is game-free and every
/// classification and sentence in it is tested below. The <i>reads</i> that fill it live in
/// <c>UnityUiRuntime.ProbeInteraction</c> and have <b>no behavioral coverage at all and cannot</b>:
/// nothing in this suite can produce an <c>EventSystem</c>, a <c>Canvas</c>, a <c>GraphicRaycaster</c>
/// or a <c>Graphic</c>, and a test double for them would assert the shape of the double. What the
/// source pins at the bottom hold is where those reads are called from and in what order — which is
/// the half a headless suite can actually keep honest.
/// </para>
/// </summary>
public class UiInteractionProbeTests
{
    /// <summary>A reading with every link present, for a test to break one field of.</summary>
    private static UiInteractionReading Live() => new()
    {
        When = "a show",
        Panel = "Server mods",
        Readable = Reading.Yes,
        Instances = 1,
        ActiveInstances = 1,
        InstanceDetail = "via FindObjectsOfTypeAll; EventSystem:on",
        CurrentExists = true,
        CurrentName = "EventSystem",
        InputModule = "InputSystemUIInputModule",
        PointerOverUi = Reading.Unreadable,
        CanvasName = "overlay-1",
        CanvasSortingOrder = UiOwnership.OverlaySorting,
        CanvasOverrideSorting = Reading.No,
        CanvasRenderMode = "ScreenSpaceOverlay",
        CanvasEnabled = Reading.Yes,
        RaycasterPresent = Reading.Yes,
        RaycasterEnabled = Reading.Yes,
        BlockingCanvasGroups = 0,
    };

    private static UiRowReading OperableRow() => new()
    {
        Panel = "Server mods",
        RowName = "button",
        Kind = "button",
        Source = ControlSource.Built,
        Readable = Reading.Yes,
        ActiveSelf = Reading.Yes,
        ActiveInHierarchy = Reading.Yes,
        Graphics = 2,
        VisibleGraphics = 2,
        RaycastGraphics = 1,
        ForeignCanvasGraphics = 0,
        NestedCanvases = 0,
        Click = "Button",
    };

    // ---- the screen-level chain ----------------------------------------------------------------

    /// <summary>
    /// <b>The test this job exists for.</b> Each link of the chain a click has to travel, broken one
    /// at a time from an otherwise-live reading, names itself. The order is the content: with no
    /// <c>EventSystem</c> the raycaster's state is not a finding, and a probe that reported it as
    /// one would send the reader to fix the layer that is working.
    /// </summary>
    [Fact]
    public void Each_broken_link_names_itself_and_the_chain_is_walked_outward()
    {
        Assert.Equal(UiInteractionVerdict.Live, UiInteractionReport.Classify(Live()));

        var noEventSystem = new UiInteractionReading
        {
            Readable = Reading.Yes,
            Instances = 0,
            // Everything downstream still looks perfect, and none of it can matter.
            CurrentExists = true,
            InputModule = "InputSystemUIInputModule",
            RaycasterPresent = Reading.Yes,
            RaycasterEnabled = Reading.Yes,
        };
        Assert.Equal(UiInteractionVerdict.NoEventSystem, UiInteractionReport.Classify(noEventSystem));

        Assert.Equal(
            UiInteractionVerdict.NoActiveEventSystem,
            UiInteractionReport.Classify(With(Live(), r => new UiInteractionReading
            {
                Readable = r.Readable, Instances = 2, ActiveInstances = 0,
                CurrentExists = r.CurrentExists, InputModule = r.InputModule,
                RaycasterPresent = r.RaycasterPresent, RaycasterEnabled = r.RaycasterEnabled,
            })));

        Assert.Equal(
            UiInteractionVerdict.NoCurrentEventSystem,
            UiInteractionReport.Classify(With(Live(), r => new UiInteractionReading
            {
                Readable = r.Readable, Instances = 1, ActiveInstances = 1,
                CurrentExists = false, InputModule = r.InputModule,
                RaycasterPresent = r.RaycasterPresent, RaycasterEnabled = r.RaycasterEnabled,
            })));

        Assert.Equal(
            UiInteractionVerdict.NoInputModule,
            UiInteractionReport.Classify(With(Live(), r => new UiInteractionReading
            {
                Readable = r.Readable, Instances = 1, ActiveInstances = 1, CurrentExists = true,
                InputModule = "",
                RaycasterPresent = r.RaycasterPresent, RaycasterEnabled = r.RaycasterEnabled,
            })));

        Assert.Equal(
            UiInteractionVerdict.NoRaycaster,
            UiInteractionReport.Classify(With(Live(), r => new UiInteractionReading
            {
                Readable = r.Readable, Instances = 1, ActiveInstances = 1, CurrentExists = true,
                InputModule = r.InputModule,
                RaycasterPresent = Reading.Yes, RaycasterEnabled = Reading.No,
            })));

        Assert.Equal(
            UiInteractionVerdict.RaycastsBlocked,
            UiInteractionReport.Classify(With(Live(), r => new UiInteractionReading
            {
                Readable = r.Readable, Instances = 1, ActiveInstances = 1, CurrentExists = true,
                InputModule = r.InputModule,
                RaycasterPresent = Reading.Yes, RaycasterEnabled = Reading.Yes,
                BlockingCanvasGroups = 1,
            })));
    }

    /// <summary>
    /// A probe that could not take its own reads claims nothing about the screen — not even the
    /// zeros it is holding. This is the same rule as <c>ScrollbarVerdict</c>'s unreadable flag: a
    /// diagnostic that substitutes a default for a read it did not get is the thing these files
    /// exist to stop, and an unread <c>Instances</c> of 0 would otherwise print as
    /// <c>NoEventSystem</c>, which is a very strong claim to make out of a caught exception.
    /// </summary>
    [Fact]
    public void A_probe_that_could_not_read_claims_nothing_about_the_screen()
    {
        var unreadable = new UiInteractionReading { Readable = Reading.Unreadable, Instances = 0 };
        Assert.Equal(UiInteractionVerdict.Unreadable, UiInteractionReport.Classify(unreadable));
        Assert.True(UiInteractionReport.IsWarning(UiInteractionVerdict.Unreadable));

        string line = UiInteractionReport.Describe(unreadable);
        Assert.Contains("verdict=Unreadable", line, StringComparison.Ordinal);
        Assert.Contains("nothing here is claimed", line, StringComparison.Ordinal);
        Assert.DoesNotContain("never asked about", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// The distinction FMAN17 named and could not settle offline, in the words the log has to use:
    /// with no <c>EventSystem</c> the panel is <b>not</b> being swallowed, it is never asked about,
    /// and the sentence says so — because "swallowed" sends a reader to sorting orders and raycast
    /// targets, and none of those can help.
    /// </summary>
    [Fact]
    public void No_event_system_is_reported_as_never_asked_rather_than_as_swallowed()
    {
        string line = UiInteractionReport.Describe(new UiInteractionReading
        {
            When = "a show",
            Panel = "Mods missing",
            Readable = Reading.Yes,
            Instances = 0,
            CanvasName = "overlay-1",
            CanvasSortingOrder = 5000,
            RaycasterPresent = Reading.Yes,
            RaycasterEnabled = Reading.Yes,
        });

        Assert.StartsWith(UiInteractionReport.ProbeTag, line, StringComparison.Ordinal);
        Assert.Contains("verdict=NoEventSystem", line, StringComparison.Ordinal);
        Assert.Contains("eventSystems=0", line, StringComparison.Ordinal);
        Assert.Contains("never asked about", line, StringComparison.Ordinal);
        Assert.Contains("no change to sorting order", line, StringComparison.Ordinal);

        // And the reads that do not decide it are still all in the line, because the next question
        // after "why did this fail" is always "what else was true at the time".
        Assert.Contains("raycaster=yes/yes", line, StringComparison.Ordinal);
        Assert.Contains("canvas=overlay-1", line, StringComparison.Ordinal);
        Assert.Contains("order=5000", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// A canvas above ours is reported and is <b>never</b> the verdict. Two canvases at the same
    /// order overlap only where their rects do, and this probe cannot see rects — so calling it a
    /// cause would be the diagnostic asserting something it did not measure. It is a lead, and the
    /// sentence says which one.
    /// </summary>
    [Fact]
    public void A_canvas_above_ours_is_a_lead_and_not_a_verdict()
    {
        var overlaid = new UiInteractionReading
        {
            When = "a show", Panel = "Mods missing",
            Readable = Reading.Yes, Instances = 1, ActiveInstances = 1,
            CurrentExists = true, InputModule = "StandaloneInputModule",
            RaycasterPresent = Reading.Yes, RaycasterEnabled = Reading.Yes,
            CanvasSortingOrder = 5000,
            CanvasAboveExists = true, CanvasAboveName = "ConnectingCanvas", CanvasAboveOrder = 9000,
        };

        Assert.Equal(UiInteractionVerdict.Live, UiInteractionReport.Classify(overlaid));
        Assert.False(UiInteractionReport.IsWarning(UiInteractionVerdict.Live));

        string line = UiInteractionReport.Describe(overlaid);
        Assert.Contains("canvasAbove=ConnectingCanvas@9000", line, StringComparison.Ordinal);
        Assert.Contains("would take a click wherever the two overlap", line, StringComparison.Ordinal);

        // With nothing above, the same verdict says the plain thing and names no suspect.
        string clean = UiInteractionReport.Describe(Live());
        Assert.Contains("canvasAbove=none", clean, StringComparison.Ordinal);
        Assert.Contains("a click over this panel should reach it", clean, StringComparison.Ordinal);
        Assert.DoesNotContain("overlap", clean, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every verdict gets its own sentence and every sentence carries the grep handle and the
    /// <c>verdict=</c> token. Two verdicts sharing a sentence is the failure mode that makes a log
    /// line worthless the first time it matters.
    /// </summary>
    [Fact]
    public void Every_screen_verdict_has_its_own_sentence()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (UiInteractionVerdict verdict in Enum.GetValues(typeof(UiInteractionVerdict)))
        {
            string line = UiInteractionReport.Describe(ReadingFor(verdict));
            Assert.Contains(UiInteractionReport.ProbeTag, line, StringComparison.Ordinal);
            Assert.Contains("verdict=" + verdict, line, StringComparison.Ordinal);
            Assert.True(seen.Add(Tail(line)), "two verdicts share a consequence: " + verdict);
        }

        // And exactly one of them is not a warning.
        int quiet = 0;
        foreach (UiInteractionVerdict verdict in Enum.GetValues(typeof(UiInteractionVerdict)))
        {
            if (!UiInteractionReport.IsWarning(verdict)) quiet++;
        }

        Assert.Equal(1, quiet);
    }

    /// <summary>
    /// The pointer read is reported and never consulted. It answers "a raycast landed on
    /// something", which is not "on us", so a screen where the game's own background is hit would
    /// otherwise read as proof that our panel is reachable.
    /// </summary>
    [Theory]
    [InlineData((int)Reading.Yes)]
    [InlineData((int)Reading.No)]
    [InlineData((int)Reading.Unreadable)]
    public void The_pointer_read_is_reported_and_never_part_of_the_verdict(int pointer)
    {
        var reading = new UiInteractionReading
        {
            Readable = Reading.Yes, Instances = 1, ActiveInstances = 1, CurrentExists = true,
            InputModule = "StandaloneInputModule",
            RaycasterPresent = Reading.Yes, RaycasterEnabled = Reading.Yes,
            PointerOverUi = (Reading)pointer,
        };

        Assert.Equal(UiInteractionVerdict.Live, UiInteractionReport.Classify(reading));
        Assert.Contains(
            "pointerOverUi=" + ((Reading)pointer switch
            {
                Reading.Yes => "yes",
                Reading.No => "no",
                _ => "unreadable",
            }),
            UiInteractionReport.Describe(reading),
            StringComparison.Ordinal);
    }

    // ---- the row-level chain -------------------------------------------------------------------

    /// <summary>
    /// <b>The row half this job exists for.</b> An inactive row is answered first, because it
    /// explains every other reading it could produce — a row that is not in the hierarchy has no
    /// canvas, no visible graphic and no raycast target, and reporting any of those instead would
    /// name a symptom. It is also the exact shape of the defect: laid out, measured and counted
    /// (every measurement in the module passes <c>includeInactive: true</c>) while drawing nothing
    /// and receiving nothing.
    /// </summary>
    [Fact]
    public void An_inactive_row_is_answered_first_because_it_explains_the_rest()
    {
        var inactive = new UiRowReading
        {
            Panel = "Mods missing", RowName = "button", Kind = "button",
            Source = ControlSource.Cloned,
            Readable = Reading.Yes,
            ActiveSelf = Reading.No,
            ActiveInHierarchy = Reading.No,
            // Everything a live row would have; none of it can be seen from outside the hierarchy.
            Graphics = 0, VisibleGraphics = 0, RaycastGraphics = 0, ForeignCanvasGraphics = 0,
            Click = "",
        };

        Assert.Equal(UiRowVerdict.Inactive, UiInteractionReport.ClassifyRow(inactive));

        string line = UiInteractionReport.DescribeRow(inactive);
        Assert.StartsWith(UiInteractionReport.RowTag, line, StringComparison.Ordinal);
        Assert.Contains("verdict=Inactive", line, StringComparison.Ordinal);
        Assert.Contains("source=clone", line, StringComparison.Ordinal);
        Assert.Contains("activeInHierarchy=no", line, StringComparison.Ordinal);
        Assert.Contains("An empty band in a panel whose layout line counts the row is this", line,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A graphic rendering through somebody else's canvas outranks "invisible" and "not
    /// raycastable" for the same reason: it is the cause and they are what it looks like. This is
    /// the other candidate for the deny panel's Close button that the dump could not decide — a
    /// cloned vanilla donor that brought its own <c>Canvas</c>.
    /// </summary>
    [Fact]
    public void A_foreign_canvas_outranks_the_symptoms_it_produces()
    {
        var foreign = new UiRowReading
        {
            Panel = "Mods missing", RowName = "button", Kind = "button",
            Source = ControlSource.Cloned,
            Readable = Reading.Yes,
            ActiveSelf = Reading.Yes, ActiveInHierarchy = Reading.Yes,
            Graphics = 2, VisibleGraphics = 0, RaycastGraphics = 0,
            ForeignCanvasGraphics = 2, NestedCanvases = 1,
            Click = "SimpleStunButton",
        };

        Assert.Equal(UiRowVerdict.ForeignCanvas, UiInteractionReport.ClassifyRow(foreign));
        string line = UiInteractionReport.DescribeRow(foreign);
        Assert.Contains("foreignCanvas=2", line, StringComparison.Ordinal);
        Assert.Contains("nestedCanvases=1", line, StringComparison.Ordinal);
        Assert.Contains("neither of which is ours", line, StringComparison.Ordinal);
    }

    /// <summary>Each remaining row state, one field at a time off an operable row.</summary>
    [Fact]
    public void Each_remaining_row_state_names_itself()
    {
        Assert.Equal(UiRowVerdict.Operable, UiInteractionReport.ClassifyRow(OperableRow()));

        Assert.Equal(UiRowVerdict.Unreadable, UiInteractionReport.ClassifyRow(new UiRowReading
        {
            Readable = Reading.Unreadable, ActiveInHierarchy = Reading.Yes, Graphics = 2,
            VisibleGraphics = 2, RaycastGraphics = 1, Click = "Button",
        }));

        Assert.Equal(UiRowVerdict.NoGraphic, UiInteractionReport.ClassifyRow(new UiRowReading
        {
            Readable = Reading.Yes, ActiveInHierarchy = Reading.Yes, Graphics = 0, Click = "Button",
        }));

        Assert.Equal(UiRowVerdict.Invisible, UiInteractionReport.ClassifyRow(new UiRowReading
        {
            Readable = Reading.Yes, ActiveInHierarchy = Reading.Yes, Graphics = 2,
            VisibleGraphics = 0, RaycastGraphics = 1, Click = "Button",
        }));

        Assert.Equal(UiRowVerdict.NotRaycastable, UiInteractionReport.ClassifyRow(new UiRowReading
        {
            Readable = Reading.Yes, ActiveInHierarchy = Reading.Yes, Graphics = 2,
            VisibleGraphics = 2, RaycastGraphics = 0, Click = "Button",
        }));

        Assert.Equal(UiRowVerdict.NoClickTarget, UiInteractionReport.ClassifyRow(new UiRowReading
        {
            Readable = Reading.Yes, ActiveInHierarchy = Reading.Yes, Graphics = 2,
            VisibleGraphics = 2, RaycastGraphics = 1, Click = "",
        }));

        // An unreadable activity flag is not evidence the row is active.
        Assert.Equal(UiRowVerdict.Inactive, UiInteractionReport.ClassifyRow(new UiRowReading
        {
            Readable = Reading.Yes, ActiveInHierarchy = Reading.Unreadable, Graphics = 2,
            VisibleGraphics = 2, RaycastGraphics = 1, Click = "Button",
        }));
    }

    /// <summary>
    /// A label is allowed to be unclickable — that is what a label is — and a control row is not.
    /// Without this the settings panel would emit thirty warnings a build about rows behaving
    /// exactly as designed, and the one row that matters would be in the middle of them.
    /// </summary>
    [Fact]
    public void A_label_may_be_unclickable_and_a_control_row_may_not()
    {
        Assert.False(UiInteractionReport.IsRowWarning("label", UiRowVerdict.NoClickTarget));
        Assert.False(UiInteractionReport.IsRowWarning("label", UiRowVerdict.NotRaycastable));
        Assert.True(UiInteractionReport.IsRowWarning("label", UiRowVerdict.Inactive));
        Assert.True(UiInteractionReport.IsRowWarning("label", UiRowVerdict.ForeignCanvas));

        Assert.True(UiInteractionReport.IsRowWarning("button", UiRowVerdict.NoClickTarget));
        Assert.True(UiInteractionReport.IsRowWarning("button", UiRowVerdict.NotRaycastable));
        Assert.False(UiInteractionReport.IsRowWarning("button", UiRowVerdict.Operable));
        Assert.False(UiInteractionReport.IsRowWarning("label", UiRowVerdict.Operable));
    }

    /// <summary>
    /// The row line carries <c>source=</c>, so "did we clone or build this" and "does it work" are
    /// one sentence. Those two questions were answered by two different instruments on two
    /// different screens, and the button row had neither.
    /// </summary>
    [Theory]
    [InlineData((int)ControlSource.Cloned, "source=clone")]
    [InlineData((int)ControlSource.Built, "source=built")]
    [InlineData((int)ControlSource.BuiltAfterCloneRejected, "source=built-after-clone-rejected")]
    [InlineData((int)ControlSource.NoneAfterCloneRejected, "source=none-after-clone-rejected")]
    [InlineData((int)ControlSource.None, "source=none")]
    public void The_row_line_says_which_path_built_the_row(int source, string token)
    {
        var row = new UiRowReading
        {
            Panel = "Mods missing", RowName = "button", Kind = "button",
            Source = (ControlSource)source,
            Readable = Reading.Yes, ActiveInHierarchy = Reading.Yes, ActiveSelf = Reading.Yes,
            Graphics = 2, VisibleGraphics = 2, RaycastGraphics = 1, Click = "Button",
        };

        Assert.Contains(token, UiInteractionReport.DescribeRow(row), StringComparison.Ordinal);
    }

    /// <summary>
    /// Every row verdict has its own consequence sentence, for the same reason the screen verdicts
    /// do.
    /// </summary>
    [Fact]
    public void Every_row_verdict_has_its_own_sentence()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (UiRowVerdict verdict in Enum.GetValues(typeof(UiRowVerdict)))
        {
            string line = UiInteractionReport.DescribeRow(RowReadingFor(verdict));
            Assert.Contains(UiInteractionReport.RowTag, line, StringComparison.Ordinal);
            Assert.Contains("verdict=" + verdict, line, StringComparison.Ordinal);
            Assert.True(seen.Add(Tail(line)), "two row verdicts share a consequence: " + verdict);
        }
    }

    /// <summary>
    /// An empty field prints as <c>?</c> and never as a gap. A field that vanishes is a field the
    /// next reader mis-parses as the one after it, which is a cheap way to make a diagnostic lie.
    /// </summary>
    [Fact]
    public void An_empty_field_prints_as_a_question_mark_rather_than_as_a_gap()
    {
        string line = UiInteractionReport.Describe(new UiInteractionReading { Readable = Reading.Yes });
        Assert.Contains("at ? for panel '': ", line, StringComparison.Ordinal);
        Assert.Contains("canvas=?", line, StringComparison.Ordinal);
        Assert.Contains("current=none", line, StringComparison.Ordinal);
        Assert.Contains("module=none", line, StringComparison.Ordinal);
        Assert.DoesNotContain("  ", line, StringComparison.Ordinal);

        string row = UiInteractionReport.DescribeRow(new UiRowReading { Readable = Reading.Yes });
        Assert.Contains("/ ? kind=?", row, StringComparison.Ordinal);
        Assert.Contains("click=none", row, StringComparison.Ordinal);
    }

    // ---- source pins over the reads, which cannot be linked into this assembly ------------------

    /// <summary>
    /// <b>The button row joins the plan.</b> It was the only one of the five row types with no
    /// donor filter, no operability re-check on the clone, no inactive-build discipline and no
    /// <c>source=</c> line — while being the row type most likely to take the clone path, because a
    /// <c>SimpleStunButton</c> exists on screens where a vanilla <c>Toggle</c> or
    /// <c>TMP_InputField</c> does not.
    /// </summary>
    [Fact]
    public void The_button_row_is_resolved_by_the_same_plan_as_the_other_three()
    {
        string body = SourcePin.MethodBody(Runtime(), "private static GameObject? CloneOrCreateButton(");

        SourcePin.AssertStatementExactly(body, "var attempt = ControlBuildPlan.Resolve(");
        SourcePin.AssertStatementExactly(
            body, "LogControlSource(ref _buttonSourceLogged, \"panel button\", \"button\", attempt.Source);");
        SourcePin.AssertStatementExactly(body, "caption = SetButtonText(built, text);");
        SourcePin.AssertStatementExactly(body, "Activate(built);");

        // The source line is emitted before the method can give up, which is R45 finding 2's rule
        // and the reason the fallback used to be reported by nothing.
        int logged = body.IndexOf("LogControlSource(", StringComparison.Ordinal);
        int gaveUp = body.IndexOf("return null", StringComparison.Ordinal);
        Assert.True(logged >= 0 && gaveUp > logged,
            "the button's source line must be emitted before the method can return null");

        // And the activation is the last thing that happens to the control, after the caption.
        int caption = body.IndexOf("caption = SetButtonText(built, text);", StringComparison.Ordinal);
        int activate = body.IndexOf("Activate(built);", StringComparison.Ordinal);
        Assert.True(caption >= 0 && activate > caption,
            "the button is activated after its caption is written, not before");
    }

    /// <summary>
    /// <b>The defect, pinned where it was.</b> Every donor sweep runs through
    /// <c>FindObjectsOfType(type, includeInactive: true)</c>, so an unshown widget belonging to a
    /// view the player is not looking at is a candidate — and <c>Instantiate</c> copies its
    /// <c>activeSelf</c>. All four sweeps refuse one now; the button's is the one that never had
    /// any filter at all.
    /// </summary>
    [Theory]
    [InlineData("private static SimpleStunButton? ButtonDonor(", "b")]
    [InlineData("private static Toggle? ToggleDonor(", "t")]
    [InlineData("private static Slider? SliderDonor(", "s")]
    [InlineData("private static TMP_InputField? InputDonor(", "f")]
    public void Every_donor_sweep_refuses_a_donor_that_is_not_on_screen(string signature, string variable)
    {
        string body = SourcePin.MethodBody(Runtime(), signature);
        SourcePin.AssertStatementExactly(body, "if (!IsShownDonor(" + variable + ".gameObject)) return;");
    }

    /// <summary>
    /// The build-order rule reaches the fifth row type. Both button paths start inactive and the
    /// single activation is in <c>CloneOrCreateButton</c>, after the caption — so a
    /// <c>SimpleStunSelectable</c>'s <c>OnEnable</c> color transition runs over a finished row
    /// rather than over a half-built one, and a clone can no longer inherit an inactive donor's
    /// <c>activeSelf</c> and be laid out, measured and counted while drawing nothing.
    /// </summary>
    [Fact]
    public void Both_button_paths_are_built_inactive_and_activated_once()
    {
        string runtime = Runtime();

        SourcePin.AssertStatementExactly(
            SourcePin.MethodBody(runtime, "private static GameObject? CloneButton("), "clone.SetActive(false);");
        SourcePin.AssertStatementExactly(
            SourcePin.MethodBody(runtime, "private static GameObject? BuildButton("), "go.SetActive(false);");

        // Exactly one activation on the button path, and it is the resolved control's.
        string resolve = SourcePin.MethodBody(runtime, "private static GameObject? CloneOrCreateButton(");
        Assert.Equal(
            resolve.IndexOf("Activate(", StringComparison.Ordinal),
            resolve.LastIndexOf("Activate(", StringComparison.Ordinal));
        Assert.DoesNotContain(
            "Activate(", SourcePin.MethodBody(runtime, "private static GameObject? CloneButton("),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Activate(", SourcePin.MethodBody(runtime, "private static GameObject? BuildButton("),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Where the probe is taken from, and when.</b> It is armed by a show and spent on the next
    /// <c>ReadPointer</c> — <c>HoverCapture</c>'s per-frame pass — so the readout is a frame later,
    /// with the canvas sized and every row's <c>OnEnable</c> run. A probe taken inside <c>Show</c>
    /// would report a zero-sized canvas, which is not a hypothetical: the deny panel's own layout
    /// line read <c>ruler=constant</c> for exactly that reason.
    /// </summary>
    [Fact]
    public void The_probe_is_armed_by_a_show_and_spent_a_frame_later()
    {
        string runtime = Runtime();

        SourcePin.AssertStatementExactly(
            SourcePin.MethodBody(runtime, "public void Show(IRuntimePanel panel)"),
            "try { ArmProbe(host); } catch { /* */ }");
        SourcePin.AssertStatementExactly(
            SourcePin.MethodBody(runtime, "public PointerSample ReadPointer()"),
            "try { FireArmedProbe(\"a show\"); } catch { /* */ }");
    }

    /// <summary>
    /// And the hide is the net under it: a panel shown and closed inside one frame, or one whose
    /// plugin never registered for the hover pass, would otherwise take its armed probe away
    /// unspent. The read happens <b>before</b> the deactivation, or every row would report
    /// <c>Inactive</c> truthfully and uselessly — the same ordering rule, and the same reason, as
    /// the scrollbar read-back three lines above it.
    /// </summary>
    [Fact]
    public void The_hide_spends_an_unspent_probe_before_the_panel_is_deactivated()
    {
        string hide = SourcePin.MethodBody(Runtime(), "public void Hide(IRuntimePanel panel)");

        SourcePin.AssertStatementExactly(hide, "try { FireArmedProbe(\"a hide\"); } catch { /* */ }");

        int probe = hide.IndexOf("FireArmedProbe(", StringComparison.Ordinal);
        int deactivate = hide.IndexOf("host.Visible = false", StringComparison.Ordinal);
        Assert.True(probe >= 0 && deactivate > probe,
            "the probe must be taken while the panel is still shown");
    }

    /// <summary>
    /// The one read that asks the input module to answer rather than reading a field is off unless
    /// the environment asks for it. Everything the verdict needs is a field read; this is the only
    /// line that could plausibly cost a client something on a screen where the module is half torn
    /// down, so it does not run by default.
    /// </summary>
    [Fact]
    public void The_module_read_is_off_unless_the_environment_asks_for_it()
    {
        string body = SourcePin.MethodBody(Runtime(), "private static Reading ProbePointerOverUi(");

        SourcePin.AssertStatementExactly(body, "if (!_probePointerEnabled) return Reading.Unreadable;");
        Assert.Contains("PROGENITOR_UI_PROBE_POINTER", Runtime(), StringComparison.Ordinal);

        int gate = body.IndexOf("if (!_probePointerEnabled)", StringComparison.Ordinal);
        int call = body.IndexOf("IsPointerOverGameObject()", StringComparison.Ordinal);
        Assert.True(gate >= 0 && call > gate, "the gate must precede the call it gates");
    }

    /// <summary>
    /// The button donor's caches are re-armed by the reset that means "a fresh panel is being
    /// built", exactly like the other three — R45 finding 27's rule, which exists because a donor
    /// that only lives on certain screens must be looked for again on each panel, and a latch that
    /// does not re-arm freezes the first answer for the life of the Core generation.
    /// </summary>
    [Fact]
    public void The_button_donor_cache_and_latch_are_re_armed_with_the_others()
    {
        string reset = SourcePin.MethodBody(Runtime(), "private static void ResetSceneCaches(");

        SourcePin.AssertStatementExactly(reset, "_buttonDonor = null;");
        SourcePin.AssertStatementExactly(reset, "_buttonDonorSearched = false;");
        SourcePin.AssertStatementExactly(reset, "_buttonSourceLogged = false;");
    }

    // ---- job FUI31: the canvas above ours, measured ---------------------------------------------

    /// <summary>
    /// <b>The test job FUI31 exists for.</b> The same canvas at the same order over the same panel
    /// is three different findings depending on one read, and the probe must not collapse them.
    ///
    /// <para>
    /// Measured and overlapping is a verdict, because part of the panel really is unreachable.
    /// Measured and not overlapping is <c>Live</c> with the suspect <em>cleared by name</em> — the
    /// reading both of job FUI30's live runs should have produced, where the button came back
    /// <c>Operable</c> and a real click reached it. And <b>not measured</b> is job FUI30's original
    /// sentence, unchanged and still not a verdict: a canvas above ours is a lead until somebody
    /// has looked at the rects, and an unreadable look is not a look.
    /// </para>
    /// </summary>
    [Fact]
    public void A_canvas_above_ours_becomes_a_verdict_only_once_the_overlap_was_actually_measured()
    {
        Assert.Equal(
            UiInteractionVerdict.CanvasAboveOverlaps,
            UiInteractionReport.Classify(Overlaid(Reading.Yes)));
        Assert.True(UiInteractionReport.IsWarning(UiInteractionVerdict.CanvasAboveOverlaps));

        Assert.Equal(UiInteractionVerdict.Live, UiInteractionReport.Classify(Overlaid(Reading.No)));
        Assert.Equal(
            UiInteractionVerdict.Live, UiInteractionReport.Classify(Overlaid(Reading.Unreadable)));

        string measured = UiInteractionReport.Describe(Overlaid(Reading.Yes));
        Assert.Contains("canvasAbove=CanvasUnscaledMainMenu@9000 overlapsPanel=yes", measured, StringComparison.Ordinal);
        Assert.Contains("covers part of this panel", measured, StringComparison.Ordinal);
        Assert.Contains("The rest of the panel is reachable", measured, StringComparison.Ordinal);

        string cleared = UiInteractionReport.Describe(Overlaid(Reading.No));
        Assert.Contains("canvasAbove=CanvasUnscaledMainMenu@9000 overlapsPanel=no", cleared, StringComparison.Ordinal);
        Assert.Contains("no raycast target of its own over this panel", cleared, StringComparison.Ordinal);
        Assert.Contains("takes nothing from it", cleared, StringComparison.Ordinal);

        // Unmeasured keeps FUI30's wording to the character: it is the sentence that is still true.
        string lead = UiInteractionReport.Describe(Overlaid(Reading.Unreadable));
        Assert.Contains("canvasAbove=CanvasUnscaledMainMenu@9000 overlapsPanel=unreadable", lead, StringComparison.Ordinal);
        Assert.Contains("would take a click wherever the two overlap", lead, StringComparison.Ordinal);

        // And with nothing above, no overlap token is printed at all: there is nothing to qualify.
        Assert.Contains("canvasAbove=none", UiInteractionReport.Describe(Live()), StringComparison.Ordinal);
        Assert.DoesNotContain("overlapsPanel=", UiInteractionReport.Describe(Live()), StringComparison.Ordinal);

        // The flag describes the at-or-above set, so with an empty set it is not a finding either.
        var nothingAbove = new UiInteractionReading
        {
            Readable = Reading.Yes, Instances = 1, ActiveInstances = 1, CurrentExists = true,
            InputModule = "InputSystemUIInputModule",
            RaycasterPresent = Reading.Yes, RaycasterEnabled = Reading.Yes,
            CanvasAboveExists = false, CanvasAboveOverlaps = Reading.Yes,
        };
        Assert.Equal(UiInteractionVerdict.Live, UiInteractionReport.Classify(nothingAbove));
    }

    /// <summary>
    /// The one verdict in this file that could be read as "raise the number" says out loud that
    /// raising it is the wrong answer, and why. That sentence is the durable half of job FUI31: the
    /// canvas we would be jumping over is <c>MainMenuCanvasBase</c>'s, which
    /// <c>GenericPopupSystem</c> parents every main-menu modal under, so outranking it means
    /// outranking the game's own loading screens, popups and disconnect UI — and this overlay is
    /// <c>DontDestroyOnLoad</c>, so it would keep outranking them across a scene load. A diagnostic
    /// that names a cause and leaves the obvious wrong fix unmentioned is how the wrong fix ships.
    /// </summary>
    [Fact]
    public void The_overlap_verdict_names_raising_the_sorting_order_as_the_wrong_answer()
    {
        string line = UiInteractionReport.Describe(Overlaid(Reading.Yes));

        Assert.Contains("Raising our sorting order is the wrong answer", line, StringComparison.Ordinal);
        Assert.Contains("loading screens, modal popups and disconnect UI", line, StringComparison.Ordinal);
        Assert.Contains("(job FUI31)", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// Two rectangles overlap when they share any area, edges included — the same closed test
    /// <see cref="ScreenRect.Contains"/> makes, so a graphic that meets the panel exactly on its
    /// border cannot answer differently on either side of a rounding error. Separation on
    /// <em>either</em> axis is enough to be disjoint, which is the half a hand-rolled containment
    /// test gets wrong.
    /// </summary>
    [Fact]
    public void Overlap_is_a_shared_area_on_both_axes_and_an_unknown_rect_shares_none()
    {
        var panel = ScreenRect.FromCorners(100f, 100f, 300f, 400f);

        Assert.True(UiInteractionReport.RectsOverlap(panel, ScreenRect.FromCorners(200f, 200f, 250f, 250f)));
        Assert.True(UiInteractionReport.RectsOverlap(panel, ScreenRect.FromCorners(0f, 0f, 4000f, 4000f)));

        // Touching on an edge counts, in both orders of the pair.
        var touching = ScreenRect.FromCorners(300f, 100f, 500f, 400f);
        Assert.True(UiInteractionReport.RectsOverlap(panel, touching));
        Assert.True(UiInteractionReport.RectsOverlap(touching, panel));

        // Clear on x only, clear on y only: either is enough.
        Assert.False(UiInteractionReport.RectsOverlap(panel, ScreenRect.FromCorners(301f, 100f, 500f, 400f)));
        Assert.False(UiInteractionReport.RectsOverlap(panel, ScreenRect.FromCorners(100f, 401f, 300f, 900f)));

        // An unreadable rect overlaps nothing, from either side. UnityUiRuntime reports that case
        // as Reading.Unreadable before it reaches here, so a false is always two rects that read.
        Assert.False(UiInteractionReport.RectsOverlap(panel, ScreenRect.Unknown));
        Assert.False(UiInteractionReport.RectsOverlap(ScreenRect.Unknown, panel));
        Assert.False(UiInteractionReport.RectsOverlap(ScreenRect.Unknown, ScreenRect.Unknown));
    }

    /// <summary>
    /// The overlap read counts only graphics that raycast through <em>that</em> canvas. A graphic
    /// under a nested <c>Canvas</c> registers to the nested one — the same fact
    /// <c>UiRowVerdict.ForeignCanvas</c> exists for, read here in the other direction — so counting
    /// it would report a canvas as covering us that cannot hit anything at all. The cap answers
    /// <c>Unreadable</c> and not <c>No</c>, because "I stopped looking" is not "nothing is there".
    /// </summary>
    [Fact]
    public void The_overlap_read_counts_only_the_canvas_own_raycast_targets_and_bounds_the_walk()
    {
        string body = SourcePin.MethodBody(
            Runtime(), "private static Reading CanvasCoversPanel(Canvas canvas, ScreenRect panelRect)");

        SourcePin.AssertStatementExactly(body, "if (!g.enabled || !g.raycastTarget) continue;");
        SourcePin.AssertStatementExactly(body, "if (own == null || !SamePointer(own, canvas)) continue;");
        SourcePin.AssertStatementExactly(
            body, "if (++examined > MaxProbedGraphicsPerCanvas) return Reading.Unreadable;");
    }

    /// <summary>
    /// Both rects go through the same conversion. The panel is measured by
    /// <c>ScreenRectOf</c> and so is every vanilla graphic tested against it — evidence 165 already
    /// paid once for two measurements of one screen taken in two different spaces, and comparing a
    /// canvas-unit rect against a device-pixel rect is that defect with an extra step.
    /// </summary>
    [Fact]
    public void The_panel_and_the_canvas_above_it_are_measured_in_one_space()
    {
        string runtime = Runtime();

        SourcePin.AssertStatementExactly(
            SourcePin.MethodBody(runtime, "public ScreenRect PanelRect(IRuntimePanel panel)"),
            "return ScreenRectOf(rt);");
        SourcePin.AssertStatementExactly(
            SourcePin.MethodBody(runtime, "private static ScreenRect PanelScreenRect(UnityPanel panel)"),
            "return rt == null ? ScreenRect.Unknown : ScreenRectOf(rt);");
        SourcePin.AssertStatementExactly(
            SourcePin.MethodBody(
                runtime, "private static Reading CanvasCoversPanel(Canvas canvas, ScreenRect panelRect)"),
            // Job FUI33 (R48 finding 1) moved the judgment into UiInteractionReport and left the
            // read here; the pin is still that the graphic's rect comes from the same ScreenRectOf
            // the panel's does, which is the assertion this test is about.
            "Rect = ScreenRectOf(rt),");
    }

    /// <summary>
    /// The sweep prefers an overlapping canvas to a higher non-overlapping one, and the probe hands
    /// it the panel's rect to compare against. That preference is what makes the single
    /// <c>overlapsPanel=no</c> on the line mean <em>nothing above overlaps</em> rather than "the
    /// tallest one happens not to" — a per-canvas answer would be a field a reader over-reads.
    /// </summary>
    [Fact]
    public void The_sweep_reports_an_overlapping_canvas_in_preference_to_a_higher_one()
    {
        string runtime = Runtime();

        SourcePin.AssertStatementExactly(
            SourcePin.MethodBody(runtime, "private static UiInteractionReading ReadInteraction("),
            "canvas, PanelScreenRect(panel),");

        string sweep = SourcePin.MethodBody(runtime, "private static void FindCanvasAbove(");
        SourcePin.AssertStatementExactly(sweep, "Reading hit = CanvasCoversPanel(c, panelRect);");
        SourcePin.AssertStatementExactly(sweep, "name = hitFound ? hitName : bestName;");
        SourcePin.AssertStatementExactly(sweep, "order = hitFound ? hitBest : best;");
    }

    /// <summary>
    /// The overlay's sorting order is 5000 and the argument for it lives beside it. Job FUI31 was
    /// asked whether to raise it above the <c>CanvasUnscaledMainMenu@9000</c> the probe reported;
    /// the answer is no, and the three facts behind that answer are on the constant so the next
    /// person to read the probe's line finds them before they change the number.
    ///
    /// <para>
    /// The value is pinned as a constant here rather than only in prose because the prose is what a
    /// mutation would leave standing.
    /// </para>
    /// </summary>
    [Fact]
    public void The_overlay_sorting_order_is_5000_and_the_argument_for_it_is_beside_it()
    {
        Assert.Equal(5000, UiOwnership.OverlaySorting);

        string ownership = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "UiOwnership.cs");
        SourcePin.AssertStatementExactly(ownership, "public const int OverlaySorting = 5000;");

        // The three facts a raiser has to have read, each by the thing it names.
        Assert.Contains("Order never produces a raycast hit", ownership, StringComparison.Ordinal);
        Assert.Contains("hosts the game's modals", ownership, StringComparison.Ordinal);
        Assert.Contains("serialized scene data", ownership, StringComparison.Ordinal);
        Assert.Contains("DontDestroyOnLoad", ownership, StringComparison.Ordinal);
    }

    // ---- helpers -------------------------------------------------------------------------------

    private static UiInteractionReading With(
        UiInteractionReading seed, Func<UiInteractionReading, UiInteractionReading> edit) => edit(seed);

    /// <summary>A reading that classifies as <paramref name="verdict"/>, for the uniqueness sweep.</summary>
    private static UiInteractionReading ReadingFor(UiInteractionVerdict verdict) => verdict switch
    {
        UiInteractionVerdict.Unreadable => new UiInteractionReading { Readable = Reading.Unreadable },
        UiInteractionVerdict.NoEventSystem => new UiInteractionReading { Readable = Reading.Yes },
        UiInteractionVerdict.NoActiveEventSystem =>
            new UiInteractionReading { Readable = Reading.Yes, Instances = 1 },
        UiInteractionVerdict.NoCurrentEventSystem =>
            new UiInteractionReading { Readable = Reading.Yes, Instances = 1, ActiveInstances = 1 },
        UiInteractionVerdict.NoInputModule => new UiInteractionReading
        {
            Readable = Reading.Yes, Instances = 1, ActiveInstances = 1, CurrentExists = true,
        },
        UiInteractionVerdict.NoRaycaster => new UiInteractionReading
        {
            Readable = Reading.Yes, Instances = 1, ActiveInstances = 1, CurrentExists = true,
            InputModule = "StandaloneInputModule",
        },
        UiInteractionVerdict.RaycastsBlocked => new UiInteractionReading
        {
            Readable = Reading.Yes, Instances = 1, ActiveInstances = 1, CurrentExists = true,
            InputModule = "StandaloneInputModule",
            RaycasterPresent = Reading.Yes, RaycasterEnabled = Reading.Yes,
            BlockingCanvasGroups = 1,
        },
        UiInteractionVerdict.CanvasAboveOverlaps => Overlaid(Reading.Yes),
        _ => Live(),
    };

    /// <summary>
    /// A live screen with one vanilla canvas at 9000 over our 5000 — the reading both live runs of
    /// job FUI30's probe produced — parameterized by whether the overlap read succeeded.
    /// </summary>
    private static UiInteractionReading Overlaid(Reading overlaps) => new()
    {
        When = "a show", Panel = "Mods missing",
        Readable = Reading.Yes, Instances = 1, ActiveInstances = 1,
        CurrentExists = true, CurrentName = "EventSystem",
        InputModule = "InputSystemUIInputModule",
        CanvasName = "overlay-1", CanvasSortingOrder = UiOwnership.OverlaySorting,
        CanvasRenderMode = "ScreenSpaceOverlay", CanvasEnabled = Reading.Yes,
        RaycasterPresent = Reading.Yes, RaycasterEnabled = Reading.Yes,
        CanvasAboveExists = true,
        CanvasAboveName = "CanvasUnscaledMainMenu",
        CanvasAboveOrder = 9000,
        CanvasAboveOverlaps = overlaps,
    };

    private static UiRowReading RowReadingFor(UiRowVerdict verdict) => verdict switch
    {
        UiRowVerdict.Unreadable => new UiRowReading { Readable = Reading.Unreadable },
        UiRowVerdict.Inactive => new UiRowReading { Readable = Reading.Yes, ActiveInHierarchy = Reading.No },
        UiRowVerdict.NoGraphic =>
            new UiRowReading { Readable = Reading.Yes, ActiveInHierarchy = Reading.Yes },
        UiRowVerdict.ForeignCanvas => new UiRowReading
        {
            Readable = Reading.Yes, ActiveInHierarchy = Reading.Yes, Graphics = 1,
            ForeignCanvasGraphics = 1,
        },
        UiRowVerdict.Invisible =>
            new UiRowReading { Readable = Reading.Yes, ActiveInHierarchy = Reading.Yes, Graphics = 1 },
        UiRowVerdict.NotRaycastable => new UiRowReading
        {
            Readable = Reading.Yes, ActiveInHierarchy = Reading.Yes, Graphics = 1, VisibleGraphics = 1,
        },
        UiRowVerdict.NoClickTarget => new UiRowReading
        {
            Readable = Reading.Yes, ActiveInHierarchy = Reading.Yes, Graphics = 1, VisibleGraphics = 1,
            RaycastGraphics = 1,
        },
        _ => OperableRow(),
    };

    /// <summary>The consequence half of a line — everything after the em dash the reads end with.</summary>
    private static string Tail(string line)
    {
        int at = line.LastIndexOf(" — ", StringComparison.Ordinal);
        return at < 0 ? line : line.Substring(at + 3);
    }

    private static string Runtime() => File.ReadAllText(
        Path.Combine(SourcePin.RepoRoot(), "src", "Progenitor.Core", "Ui", "UnityUiRuntime.cs"));
}
