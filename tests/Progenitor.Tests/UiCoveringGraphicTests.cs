using System;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Whether a graphic on a canvas above ours can actually take a click — job FUI33, closing R48
/// finding 1.
///
/// <para>Job FUI30 shipped <c>canvasAbove=</c> as a reported field and said in its own type doc
/// that it must never be a verdict, because two canvases at one order overlap only where their
/// rects do and the read could not see rects. Job FUI31 added the rect read and moved the field
/// into <c>Classify</c> — a real promotion, on a read that tested enabled, raycast target, own
/// canvas and overlap, and none of the other things <c>GraphicRaycaster</c> tests before it will
/// hand that graphic a pointer. The first of those, <c>CanvasGroup.blocksRaycasts</c>, is the one
/// filter this framework already modeled — for <em>our</em> panel, as the
/// <see cref="UiInteractionVerdict.RaycastsBlocked"/> verdict — so a vanilla screen parked the
/// ordinary way (visible, inert, <c>blocksRaycasts</c> off) made every mod panel shown on it warn
/// that part of it was unclickable.</para>
///
/// <para>The point of the split is that these are now decisions with tests rather than one
/// expression inside a file the suite does not compile.</para>
///
/// <para><b>Job FUI34 (R49 finding 1) then took one of FUI33's four added filters back out.</b>
/// <c>GraphicRaycaster</c> and <c>Graphic.Raycast</c> never read <c>Graphic.color.a</c>, so the
/// alpha clause did not exclude spacers (a spacer is not a raycast target) — it excluded
/// deliberate invisible click blockers, which is the one graphic shape that really does steal a
/// click and leave nothing in a screenshot. The probe went from crying wolf to staying silent
/// about the exact thing it exists to name, and this file pinned the wrong belief; the assertion
/// is now the inverse. The <c>CanvasGroup</c> walk was wrong in the same direction for one more
/// reason (<c>ignoreParentGroups</c>) and is pinned here too, because it lives in a file the suite
/// does not compile.</para>
///
/// <para><b>Job FUI35 (R50 finding 1) then undid FUI34's second <c>CanvasGroup</c> rule, which was
/// an engine fact nobody had checked.</b> FUI34 also skipped <b>disabled</b> groups, on R49's
/// unsourced claim that <c>CanvasGroup.IsRaycastLocationValid</c> is
/// <c>!enabled || blocksRaycasts</c>. It is <c>return blocksRaycasts;</c> in every Unity version,
/// and the <c>enabled</c> skip is Unity 6's <c>Graphic.Raycast</c>, which this build is not (see
/// the pin below for the sources and the dump lines). That is three reviews running where an
/// asserted engine rule was implemented and pinned unchecked, so the pin is now a negative one and
/// the sources are named where the rule is written down.</para>
/// </summary>
public class UiCoveringGraphicTests
{
    private static readonly ScreenRect Panel = ScreenRect.FromCorners(100f, 100f, 400f, 400f);

    /// <summary>A graphic that covers the panel and that uGUI would hand the click to.</summary>
    private static UiInteractionReport.CoveringGraphicReading Covering() =>
        new()
        {
            Enabled = true,
            RaycastTarget = true,
            OwnCanvas = true,
            Alpha = 1f,
            Culled = false,
            Depth = 0,
            NonBlockingCanvasGroups = 0,
            Rect = ScreenRect.FromCorners(0f, 0f, 1920f, 1080f),
        };

    [Fact]
    public void A_full_screen_opaque_raycast_target_takes_the_click()
    {
        Assert.True(UiInteractionReport.TakesClickOver(Covering(), Panel));
    }

    /// <summary>
    /// <b>R48 finding 1, the case that motivated it.</b> Enabled, a raycast target, its own
    /// canvas, over the panel — and under a <c>CanvasGroup</c> with <c>blocksRaycasts</c> off, so
    /// the raycaster refuses it and it takes nothing.
    /// </summary>
    [Fact]
    public void A_graphic_under_a_non_blocking_canvas_group_takes_nothing()
    {
        var g = Covering() with { NonBlockingCanvasGroups = 1 };

        Assert.False(UiInteractionReport.TakesClickOver(g, Panel));
    }

    /// <summary>
    /// <b>R49 finding 1, and the inverse of what this test asserted at FUI33.</b> A fully
    /// transparent full-screen <c>Image</c> with <c>raycastTarget</c> on <em>takes</em> the click:
    /// <c>GraphicRaycaster</c> skips on depth, raycast target and cull, then the rect, then
    /// <c>Graphic.Raycast</c> applies <c>CanvasGroup.blocksRaycasts</c> and any
    /// <c>ICanvasRaycastFilter</c> — <c>Graphic.color.a</c> is read nowhere on that path.
    ///
    /// <para>FUI33 filtered alpha out and called the graphic "a layout spacer" in this very
    /// summary, which was the contradiction: a spacer has <c>raycastTarget</c> off and is refused
    /// by the second line of the method. An alpha-0 raycast target is a deliberate modal blocker,
    /// the one graphic that eats a mod panel's clicks and leaves nothing in a screenshot, and the
    /// probe's whole job is to name it.</para>
    /// </summary>
    [Fact]
    public void A_transparent_raycast_target_takes_the_click()
    {
        Assert.True(UiInteractionReport.TakesClickOver(Covering() with { Alpha = 0f }, Panel));

        // Alpha decides nothing at all now, in either direction, so no threshold is involved.
        Assert.True(
            UiInteractionReport.TakesClickOver(
                Covering() with { Alpha = UiInteractionReport.VisibleAlpha }, Panel));
        Assert.True(
            UiInteractionReport.TakesClickOver(
                Covering() with { Alpha = 1f }, Panel));

        // And a transparent graphic that is *not* a raycast target is still refused — by the
        // raycast-target read, which is the filter uGUI actually applies to a spacer.
        Assert.False(
            UiInteractionReport.TakesClickOver(
                Covering() with { Alpha = 0f, RaycastTarget = false }, Panel));
    }

    /// <summary>
    /// The alpha read survives as a diagnostic on the reading and reaches no decision: the runtime
    /// still fills it (pinned in <see cref="The_runtime_reads_every_filter_and_decides_none_of_them"/>)
    /// and <c>TakesClickOver</c> answers the same for every value of it.
    /// </summary>
    [Fact]
    public void Alpha_is_read_and_never_judged()
    {
        foreach (float a in new[] { 0f, 0.005f, UiInteractionReport.VisibleAlpha, 0.5f, 1f })
        {
            var g = Covering() with { Alpha = a };
            Assert.Equal(a, g.Alpha);
            Assert.True(UiInteractionReport.TakesClickOver(g, Panel));

            // Every other filter still decides while alpha does not.
            Assert.False(UiInteractionReport.TakesClickOver(g with { Culled = true }, Panel));
        }
    }

    [Fact]
    public void A_culled_or_undrawn_graphic_takes_nothing()
    {
        Assert.False(UiInteractionReport.TakesClickOver(Covering() with { Culled = true }, Panel));

        // depth == -1 is uGUI's own marker for "not in the batch".
        Assert.False(UiInteractionReport.TakesClickOver(Covering() with { Depth = -1 }, Panel));
        Assert.True(UiInteractionReport.TakesClickOver(Covering() with { Depth = 7 }, Panel));
    }

    [Fact]
    public void The_filters_FUI31_already_had_still_hold()
    {
        Assert.False(UiInteractionReport.TakesClickOver(Covering() with { Enabled = false }, Panel));
        Assert.False(UiInteractionReport.TakesClickOver(Covering() with { RaycastTarget = false }, Panel));

        // A graphic under a nested canvas registers to that canvas, so this one's raycaster never
        // sees it: counting it would report a canvas as covering us that cannot hit anything.
        Assert.False(UiInteractionReport.TakesClickOver(Covering() with { OwnCanvas = false }, Panel));
    }

    [Fact]
    public void Geometry_still_decides_when_every_filter_passes()
    {
        var elsewhere = Covering() with { Rect = ScreenRect.FromCorners(500f, 500f, 600f, 600f) };
        Assert.False(UiInteractionReport.TakesClickOver(elsewhere, Panel));

        var touching = Covering() with { Rect = ScreenRect.FromCorners(400f, 400f, 600f, 600f) };
        Assert.True(UiInteractionReport.TakesClickOver(touching, Panel));

        // An unreadable rect on either side is not an overlap. The caller reports that as
        // Unreadable long before it reaches here, which is why a false from this is always two
        // rects that were both read.
        Assert.False(UiInteractionReport.TakesClickOver(Covering() with { Rect = ScreenRect.Unknown }, Panel));
        Assert.False(UiInteractionReport.TakesClickOver(Covering(), ScreenRect.Unknown));
    }

    /// <summary>
    /// A blocked or invisible graphic must not reach the classifier as a <c>Yes</c> — that is the
    /// whole chain, from the read to the sentence an operator acts on. With the filters applied the
    /// reading is <see cref="Reading.No"/>, the verdict is <see cref="UiInteractionVerdict.Live"/>,
    /// and the line says the canvas above takes nothing from this panel.
    /// </summary>
    [Fact]
    public void A_blocked_canvas_above_leaves_the_screen_verdict_live()
    {
        var blocked = Covering() with { NonBlockingCanvasGroups = 1 };
        Reading overlaps = UiInteractionReport.TakesClickOver(blocked, Panel) ? Reading.Yes : Reading.No;

        var reading = new UiInteractionReading
        {
            When = "a show",
            Panel = "Progenitor Settings",
            Readable = Reading.Yes,
            Instances = 1,
            ActiveInstances = 1,
            CurrentExists = true,
            InputModule = "InputSystemUIInputModule",
            RaycasterPresent = Reading.Yes,
            RaycasterEnabled = Reading.Yes,
            BlockingCanvasGroups = 0,
            CanvasAboveExists = true,
            CanvasAboveName = "CanvasUnscaledMainMenu",
            CanvasAboveOrder = 9000,
            CanvasAboveOverlaps = overlaps,
        };

        Assert.Equal(Reading.No, overlaps);
        Assert.Equal(UiInteractionVerdict.Live, UiInteractionReport.Classify(reading));
        Assert.False(UiInteractionReport.IsWarning(UiInteractionReport.Classify(reading)));

        string line = UiInteractionReport.Describe(reading);
        Assert.Contains("overlapsPanel=no", line, StringComparison.Ordinal);
        Assert.Contains("takes nothing from it", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// R49 finding 1's concrete scenario, end to end: the deny panel opens on a menu screen whose
    /// canvas carries the usual full-screen alpha-0 <c>raycastTarget</c> input blocker. Under FUI33
    /// this chain ended in <see cref="UiInteractionVerdict.Live"/> and the operator read a line
    /// saying the screen was fine while the player's clicks went to the blocker. It must warn.
    /// </summary>
    [Fact]
    public void An_invisible_blocker_above_us_is_named_rather_than_passed_over()
    {
        var blocker = Covering() with { Alpha = 0f };
        Reading overlaps = UiInteractionReport.TakesClickOver(blocker, Panel) ? Reading.Yes : Reading.No;

        var reading = new UiInteractionReading
        {
            When = "a show",
            Panel = "Progenitor Settings",
            Readable = Reading.Yes,
            Instances = 1,
            ActiveInstances = 1,
            CurrentExists = true,
            InputModule = "InputSystemUIInputModule",
            RaycasterPresent = Reading.Yes,
            RaycasterEnabled = Reading.Yes,
            BlockingCanvasGroups = 0,
            CanvasAboveExists = true,
            CanvasAboveName = "CanvasUnscaledMainMenu",
            CanvasAboveOrder = 9000,
            CanvasAboveOverlaps = overlaps,
        };

        Assert.Equal(Reading.Yes, overlaps);
        Assert.Equal(UiInteractionVerdict.CanvasAboveOverlaps, UiInteractionReport.Classify(reading));
        Assert.True(UiInteractionReport.IsWarning(UiInteractionReport.Classify(reading)));
        Assert.Contains("overlapsPanel=yes", UiInteractionReport.Describe(reading), StringComparison.Ordinal);
    }

    /// <summary>
    /// The production wiring, which cannot be compiled here: every filter above is read off the
    /// graphic and handed to the judgment, and the <c>CanvasGroup</c> walk is the same one the
    /// panel's own <see cref="UiInteractionVerdict.RaycastsBlocked"/> verdict uses — generalized
    /// to a transform rather than copied.
    /// </summary>
    [Fact]
    public void The_runtime_reads_every_filter_and_decides_none_of_them()
    {
        string body = SourcePin.MethodBody(
            SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "UnityUiRuntime.cs"),
            "private static Reading CanvasCoversPanel(");

        SourcePin.AssertStatement(body, "Alpha = g.color.a,");
        SourcePin.AssertStatement(body, "Culled = GraphicCulled(g),");
        SourcePin.AssertStatement(body, "Depth = GraphicDepth(g),");
        SourcePin.AssertStatement(body, "NonBlockingCanvasGroups = CountBlockingCanvasGroups(go.transform),");
        SourcePin.AssertStatement(
            body, "if (UiInteractionReport.TakesClickOver(reading, panelRect)) return Reading.Yes;");

        // Job FUI34, R49 finding 5: the reading is built in two steps, geometry first, so a
        // graphic nowhere near the panel never pays for the 64-level parent walk. The pre-pass
        // calls the same shared predicate the judgment ends on, so it is an ordering and not a
        // second opinion — the read/judge split above still holds.
        SourcePin.AssertStatementExactly(
            body, "if (!UiInteractionReport.RectsOverlap(panelRect, geometry.Rect)) continue;");
        Assert.True(
            body.IndexOf("NonBlockingCanvasGroups = ", StringComparison.Ordinal)
            > body.IndexOf("RectsOverlap(panelRect, geometry.Rect)", StringComparison.Ordinal),
            "the parent-chain walk must be reached only after the rect test");
    }

    /// <summary>
    /// The <c>CanvasGroup</c> walk, which also cannot be compiled here (job FUI34, R49 finding 1;
    /// corrected by job FUI35, R50 finding 1). <c>Graphic.Raycast</c> stops consulting groups
    /// after the first <c>ignoreParentGroups</c>, and reads that flag off the component itself, so
    /// the walk ends at a disabled group carrying it. That stop is the rule this pin keeps.
    ///
    /// <para>The rule it now forbids is the <c>enabled</c> skip FUI34 added on R49's word.
    /// <c>CanvasGroup.IsRaycastLocationValid</c> is <c>return blocksRaycasts;</c> in every Unity
    /// version (<c>UnityCsReference</c>,
    /// <c>Modules/UI/ScriptBindings/CanvasGroup.bindings.cs</c>); the <c>enabled</c> skip exists
    /// only in Unity 6's rewritten <c>Graphic.Raycast</c> (<c>Unity-Technologies/uGUI</c>, branch
    /// <c>6000.0</c>, <c>Runtime/UI/Core/Graphic.cs</c>), and this build is not that. Settled
    /// against the client metadata: <c>CanvasGroup.IsRaycastLocationValid</c>
    /// shares its native method with <c>get_blocksRaycasts</c> — the method is the getter — and
    /// <c>UnityEngine.UI.Graphic</c> declares only <c>Raycast(Vector2, Camera)</c>, with
    /// <c>private Vector4 m_RaycastPadding; // 0x3C</c>, the <c>com.unity.ugui</c> 1.0.0 layout.
    /// A disabled group with <c>blocksRaycasts</c> off therefore still blocks here, and skipping
    /// it under-counts: for a foreign graphic that is a false <c>true</c> from
    /// <c>TakesClickOver</c>, the crying-wolf warning FUI33 existed to stop.</para>
    /// </summary>
    [Fact]
    public void The_canvas_group_walk_stops_at_ignore_parent_groups_and_never_skips_a_disabled_group()
    {
        string body = SourcePin.MethodBody(
            SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "UnityUiRuntime.cs"),
            "private static int CountBlockingCanvasGroups(Transform? from)");

        SourcePin.AssertStatementExactly(body, "stop = group.ignoreParentGroups;");
        SourcePin.AssertStatementExactly(body, "if (!group.blocksRaycasts) n++;");
        SourcePin.AssertStatementExactly(body, "if (stop) break;");

        // The negative pin R50 finding 1 asked for: `enabled` is not a term in this walk on this
        // build, and no future edit may reintroduce it without deleting these lines and answering
        // the dump evidence in the summary above.
        //
        // Job FCFG5 (R52 finding 8) widened it from `group.enabled` alone, which forbade one
        // spelling of the rule rather than the rule: `if (!group.isActiveAndEnabled) continue;`
        // above the count, or the same test through any other local, passed every assertion here.
        // These three cover every way the removed skip can be written, and the block-shape pin
        // below covers a fourth — a new condition folded into the `if` itself.
        SourcePin.AssertNoStatementContaining(body, ".enabled");
        SourcePin.AssertNoStatementContaining(body, "isActiveAndEnabled");
        SourcePin.AssertNoStatementContaining(body, "activeInHierarchy");

        // The guard the count sits behind is liveness only — no filtering term may join it.
        SourcePin.AssertStatementExactly(body, "if (Alive(group) && group != null)");

        // The old unconditional count must not come back.
        SourcePin.AssertNoStatementContaining(body, "Alive(group) && group != null && !group.blocksRaycasts");

        // And the walk is shared rather than duplicated: the panel overload forwards to the
        // transform one, so a filter fixed on one side cannot stay broken on the other.
        string runtime = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "UnityUiRuntime.cs");
        Assert.Contains(
            "CountBlockingCanvasGroups(panel.GameObject == null ? null : panel.GameObject.transform)",
            runtime,
            StringComparison.Ordinal);
    }
}
