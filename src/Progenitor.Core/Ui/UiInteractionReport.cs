namespace Progenitor.Ui;

/// <summary>
/// A read that can fail. Three states, never two, for the same reason
/// <see cref="ScrollbarState"/> has three: "I could not read this" is a different fact from "this
/// is false", and a diagnostic that folds the first into the second is the thing this file exists
/// to stop.
/// </summary>
internal enum Reading
{
    /// <summary>The read threw, or the object it was to be taken from was gone.</summary>
    Unreadable = 0,

    /// <summary>Read, and false.</summary>
    No = 1,

    /// <summary>Read, and true.</summary>
    Yes = 2,
}

/// <summary>
/// Whether uGUI can deliver a pointer event to a mod panel on the screen the probe ran on, and if
/// not, which link of the chain is missing. Ordered from "nothing downstream can matter" outward:
/// the classifier returns the first one that holds, so the sentence names the cause rather than a
/// symptom of it.
/// </summary>
internal enum UiInteractionVerdict
{
    /// <summary>The probe could not complete its reads. Nothing is claimed either way.</summary>
    Unreadable = 0,

    /// <summary>
    /// There is no <c>EventSystem</c> in the process at all. Nothing on any canvas is raycast, so a
    /// mod panel's button is not "swallowed", it is never asked about. This is the state
    /// <c>ConnectingView.Start</c>'s <c>LoadScene(1, LoadSceneMode.Single)</c> can produce, because
    /// which <c>EventSystem</c> survives a Single load is serialized scene data
    /// (FMAN17).
    /// </summary>
    NoEventSystem = 1,

    /// <summary>Instances exist and not one of them is active and enabled.</summary>
    NoActiveEventSystem = 2,

    /// <summary>An active instance exists but <c>EventSystem.current</c> is null.</summary>
    NoCurrentEventSystem = 3,

    /// <summary>
    /// <c>EventSystem.current</c> exists with no <c>currentInputModule</c>: it is running and it is
    /// processing no pointer events.
    /// </summary>
    NoInputModule = 4,

    /// <summary>The mod overlay canvas has no enabled <c>GraphicRaycaster</c> of its own.</summary>
    NoRaycaster = 5,

    /// <summary>A <c>CanvasGroup</c> over the panel has <c>blocksRaycasts</c> off.</summary>
    RaycastsBlocked = 6,

    /// <summary>
    /// Every link is present, and a vanilla canvas that sorts at or above ours has an enabled
    /// raycast target of its own whose screen rect overlaps the panel's. Over that region the
    /// click goes to the vanilla canvas, because <c>EventSystem</c>'s comparer sorts hits by
    /// <c>GraphicRaycaster.sortOrderPriority</c> and the higher order wins.
    ///
    /// <para>
    /// This is the <b>last</b> link, and it is a partial claim on purpose: it says part of the
    /// panel is unreachable, never that the panel is dead. It is only reached when the overlap was
    /// actually measured — an unreadable overlap leaves the verdict <see cref="Live"/> and says so
    /// in the sentence, because job FUI30's rule stands wherever the measurement is missing: a
    /// canvas above ours is not a cause until somebody has looked at the rects (job FUI31).
    /// </para>
    /// </summary>
    CanvasAboveOverlaps = 7,

    /// <summary>Every link is present: a click over a mod panel should reach it.</summary>
    Live = 8,
}

/// <summary>
/// One probe of "is uGUI interaction alive on this screen", as read values rather than as
/// conclusions. The Unity reads live in <c>UnityUiRuntime.ProbeInteraction</c>; everything that
/// decides what they mean lives here, so the decision has tests and the reads have none — which is
/// the honest split, because nothing headless can produce an <c>EventSystem</c>.
/// </summary>
internal sealed class UiInteractionReading
{
    /// <summary>Why the probe ran: "a show", "a hide", the caller's own word.</summary>
    internal string When { get; init; } = "";

    /// <summary>The panel the overlay belongs to, for the log line.</summary>
    internal string Panel { get; init; } = "";

    /// <summary>Whether the <c>EventSystem</c> block was read without throwing.</summary>
    internal Reading Readable { get; init; } = Reading.Unreadable;

    /// <summary><c>Resources.FindObjectsOfTypeAll&lt;EventSystem&gt;()</c> — every instance, loaded or not.</summary>
    internal int Instances { get; init; }

    /// <summary>How many of those answered <c>isActiveAndEnabled</c>.</summary>
    internal int ActiveInstances { get; init; }

    /// <summary>Per instance: <c>name:on</c> / <c>name:off</c> / <c>name:?</c>.</summary>
    internal string InstanceDetail { get; init; } = "";

    /// <summary>Whether <c>EventSystem.current</c> answered non-null.</summary>
    internal bool CurrentExists { get; init; }

    /// <summary>The name of <c>EventSystem.current</c>'s GameObject, or empty.</summary>
    internal string CurrentName { get; init; } = "";

    /// <summary>The IL2CPP type name of <c>currentInputModule</c>, or empty for none.</summary>
    internal string InputModule { get; init; } = "";

    /// <summary>
    /// <c>EventSystem.current.IsPointerOverGameObject()</c> — the module's own answer from its last
    /// update. It says a raycast landed somewhere, not that it landed on us, which is why it is a
    /// reported field and never part of the verdict.
    /// </summary>
    internal Reading PointerOverUi { get; init; } = Reading.Unreadable;

    /// <summary>The mod overlay canvas's GameObject name.</summary>
    internal string CanvasName { get; init; } = "";

    /// <summary><c>Canvas.sortingOrder</c>; <see cref="UiOwnership.OverlaySorting"/> is what we asked for.</summary>
    internal int CanvasSortingOrder { get; init; }

    internal Reading CanvasOverrideSorting { get; init; } = Reading.Unreadable;

    /// <summary><c>Canvas.renderMode</c> as text; <c>ScreenSpaceOverlay</c> is what we asked for.</summary>
    internal string CanvasRenderMode { get; init; } = "";

    internal Reading CanvasEnabled { get; init; } = Reading.Unreadable;

    internal Reading RaycasterPresent { get; init; } = Reading.Unreadable;

    internal Reading RaycasterEnabled { get; init; } = Reading.Unreadable;

    /// <summary>
    /// <c>CanvasGroup</c>s in the panel's own parent chain whose <c>blocksRaycasts</c> is off. One
    /// is enough to make every graphic under it untouchable while it stays perfectly visible.
    /// </summary>
    internal int BlockingCanvasGroups { get; init; }

    /// <summary>Whether some other raycastable canvas sorts at or above the mod overlay.</summary>
    internal bool CanvasAboveExists { get; init; }

    internal string CanvasAboveName { get; init; } = "";

    internal int CanvasAboveOrder { get; init; }

    /// <summary>
    /// Whether <b>any</b> raycastable canvas at or above ours has an enabled raycast target of its
    /// own whose screen rect overlaps this panel's — the measurement job FUI30 declared it could
    /// not take, and job FUI31 added because the answer decides whether the mod overlay's sorting
    /// order is a live hazard or an arithmetic one.
    ///
    /// <para>
    /// It is a property of the <em>whole</em> at-or-above set, not of <see cref="CanvasAboveName"/>
    /// alone, because <c>FindCanvasAbove</c> reports an overlapping canvas in preference to a
    /// higher non-overlapping one. So <see cref="Reading.No"/> here means nothing above overlaps,
    /// which is the statement a reader wants and the one a per-canvas field could not make.
    /// </para>
    /// </summary>
    internal Reading CanvasAboveOverlaps { get; init; } = Reading.Unreadable;
}

/// <summary>What one built row of a panel turned out to be, once it was on screen.</summary>
internal enum UiRowVerdict
{
    /// <summary>The row's own reads threw.</summary>
    Unreadable = 0,

    /// <summary>
    /// The row is not active in the hierarchy. It is laid out, measured and counted — every
    /// measurement this module takes passes <c>includeInactive: true</c> — and it draws nothing and
    /// receives nothing. The first candidate for an empty band in a panel whose log says the row
    /// is there.
    /// </summary>
    Inactive = 1,

    /// <summary>
    /// A graphic in the row renders through a canvas that is not the panel's overlay — a nested
    /// <c>Canvas</c> a cloned vanilla donor brought with it. It then sorts by that canvas's order
    /// and is raycast by that canvas's raycaster, which is to say by neither of ours.
    /// </summary>
    ForeignCanvas = 2,

    /// <summary>The row carries no <c>Graphic</c> at all, so there is nothing to draw or to hit.</summary>
    NoGraphic = 3,

    /// <summary>Every graphic is disabled or fully transparent.</summary>
    Invisible = 4,

    /// <summary>Nothing in the row is a raycast target, so a pointer passes straight through it.</summary>
    NotRaycastable = 5,

    /// <summary>Nothing in the row can raise a click.</summary>
    NoClickTarget = 6,

    /// <summary>Visible, raycastable, and it has something to click.</summary>
    Operable = 7,
}

/// <summary>One row of a panel, as read off the live objects.</summary>
internal sealed class UiRowReading
{
    internal string Panel { get; init; } = "";

    internal string RowName { get; init; } = "";

    /// <summary>"button", "toggle", "slider", "input", "label".</summary>
    internal string Kind { get; init; } = "";

    /// <summary>Which path built it, from <see cref="ControlBuildPlan"/>.</summary>
    internal ControlSource Source { get; init; } = ControlSource.None;

    internal Reading Readable { get; init; } = Reading.Unreadable;

    internal Reading ActiveSelf { get; init; } = Reading.Unreadable;

    internal Reading ActiveInHierarchy { get; init; } = Reading.Unreadable;

    /// <summary><c>Graphic</c> components in the row's subtree, inactive ones included.</summary>
    internal int Graphics { get; init; }

    /// <summary>Of those, how many are enabled and carry an alpha above <see cref="UiInteractionReport.VisibleAlpha"/>.</summary>
    internal int VisibleGraphics { get; init; }

    /// <summary>Of those, how many are enabled and <c>raycastTarget</c>.</summary>
    internal int RaycastGraphics { get; init; }

    /// <summary>Of those, how many render through a canvas that is not the panel's overlay.</summary>
    internal int ForeignCanvasGraphics { get; init; }

    /// <summary><c>Canvas</c> components inside the row itself.</summary>
    internal int NestedCanvases { get; init; }

    /// <summary>The click component found, or empty.</summary>
    internal string Click { get; init; } = "";
}

/// <summary>
/// The one question this framework has no way to answer today and will keep asking: <b>is uGUI
/// interaction alive on this screen, and if not, which link is missing?</b>
///
/// <para>
/// <b>Why it exists (job FUI30, over FMAN17).</b> The deny panel drew nine correct rows on the
/// <c>ConnectingView</c> disconnect screen and its Close button drew nothing and could not be
/// clicked. Every instrument the module had said the panel was fine: the layout line counted the
/// row, and the hover line fired — but the hover line is <c>HoverCapture</c>'s own rect arithmetic
/// over a screen-space rectangle, which is true whether or not an <c>EventSystem</c> exists. There
/// was no way to tell "swallowed" from "never raycast" from "no EventSystem" without a screenshot
/// and a synthetic click, and those three want three different fixes.
/// </para>
///
/// <para>
/// <b>The split.</b> Every Unity read is in <c>UnityUiRuntime.ProbeInteraction</c> and every
/// decision about what the values mean is here, game-free and tested. The reads themselves cannot
/// be tested headless — nothing in the suite can produce an <c>EventSystem</c>, a <c>Canvas</c> or
/// a <c>Graphic</c> — and this file makes no pretense otherwise: what has coverage is the
/// classifier and the sentences, which is the half that decides what a reader concludes.
/// </para>
/// </summary>
internal static class UiInteractionReport
{
    /// <summary>
    /// Below this a graphic is treated as drawing nothing. Not zero: a vanilla button fades its
    /// targets in through <c>SimpleStunSelectable.StartColorTween</c> and a residual alpha of a
    /// few thousandths is not a thing a player can see.
    /// </summary>
    internal const float VisibleAlpha = 0.01f;

    /// <summary>The grep handle for the screen-level line.</summary>
    internal const string ProbeTag = "Ui: interaction probe";

    /// <summary>The grep handle for a per-row line.</summary>
    internal const string RowTag = "Ui: interaction row";

    /// <summary>
    /// The first missing link, walking the chain a click has to travel. The order is the content:
    /// with no <c>EventSystem</c> the raycaster's state is not a finding, and reporting it as one
    /// is how a reader ends up fixing the wrong layer.
    /// </summary>
    internal static UiInteractionVerdict Classify(UiInteractionReading r)
    {
        if (r == null || r.Readable != Reading.Yes) return UiInteractionVerdict.Unreadable;
        if (r.Instances <= 0) return UiInteractionVerdict.NoEventSystem;
        if (r.ActiveInstances <= 0) return UiInteractionVerdict.NoActiveEventSystem;
        if (!r.CurrentExists) return UiInteractionVerdict.NoCurrentEventSystem;
        if (r.InputModule.Length == 0) return UiInteractionVerdict.NoInputModule;
        if (r.RaycasterPresent != Reading.Yes || r.RaycasterEnabled != Reading.Yes)
            return UiInteractionVerdict.NoRaycaster;
        if (r.BlockingCanvasGroups > 0) return UiInteractionVerdict.RaycastsBlocked;
        if (r.CanvasAboveExists && r.CanvasAboveOverlaps == Reading.Yes)
            return UiInteractionVerdict.CanvasAboveOverlaps;
        return UiInteractionVerdict.Live;
    }

    /// <summary>
    /// Anything but <see cref="UiInteractionVerdict.Live"/> means a click aimed at a mod panel on
    /// this screen does not reach it, which an operator wants at warning level whether or not a
    /// panel is open.
    ///
    /// <para>
    /// <b>Not every warning is the same size, and R48 finding 1 was right that the old wording said
    /// it was.</b> Every verdict below <see cref="UiInteractionVerdict.CanvasAboveOverlaps"/> is a
    /// missing link in the chain and means the panel cannot be operated at all;
    /// <c>CanvasAboveOverlaps</c> is the one partial claim in the enum — part of the panel is
    /// covered and the rest of it is reachable — and its own summary and its consequence sentence
    /// both say so. Both are worth a warning, because a panel a player can only half-click is a
    /// defect; only the first group is worth the sentence "the panel is dead".
    /// </para>
    /// </summary>
    internal static bool IsWarning(UiInteractionVerdict verdict) => verdict != UiInteractionVerdict.Live;

    /// <summary>
    /// One graphic on a canvas that sorts at or above ours, as read — the values
    /// <c>UnityUiRuntime.CanvasCoversPanel</c> takes off a <c>Graphic</c>, with nothing decided
    /// yet. Job FUI33 (R48 finding 1): the read and the judgment were one expression inside a
    /// file the suite does not compile, so the judgment had no tests and, as it turned out, no
    /// filters either.
    /// </summary>
    internal readonly struct CoveringGraphicReading
    {
        /// <summary><c>Graphic.enabled</c>.</summary>
        internal bool Enabled { get; init; }

        /// <summary><c>Graphic.raycastTarget</c>.</summary>
        internal bool RaycastTarget { get; init; }

        /// <summary>
        /// Whether <c>Graphic.canvas</c> is the canvas being judged. A graphic under a nested
        /// <c>Canvas</c> registers to that one instead, so the outer canvas's raycaster never sees
        /// it — the fact <see cref="UiRowVerdict.ForeignCanvas"/> exists for, read here in the
        /// other direction.
        /// </summary>
        internal bool OwnCanvas { get; init; }

        /// <summary>
        /// <c>Graphic.color.a</c>. <b>Diagnostic only — never a filter</b> (job FUI34, R49 finding
        /// 1). <c>GraphicRaycaster</c> and <c>Graphic.Raycast</c> never consult the graphic's
        /// color: a fully transparent <c>Image</c> with <c>raycastTarget</c> on takes the click,
        /// and that is the canonical uGUI modal blocker rather than an edge case. The row probe
        /// counts alpha for a different question ("is anything of this row visible"), which is why
        /// the value is read and kept here without reaching <see cref="TakesClickOver"/>.
        /// </summary>
        internal float Alpha { get; init; }

        /// <summary><c>Graphic.canvasRenderer.cull</c>: the renderer was culled out this frame.</summary>
        internal bool Culled { get; init; }

        /// <summary>
        /// <c>Graphic.depth</c> (<c>CanvasRenderer.absoluteDepth</c>). <c>-1</c> is uGUI's own
        /// marker for "this graphic is not in the batch", i.e. it draws nothing.
        /// </summary>
        internal int Depth { get; init; }

        /// <summary>
        /// How many <c>CanvasGroup</c>s over this graphic have <c>blocksRaycasts</c> off, counted
        /// the same way <c>CountBlockingCanvasGroups</c> counts them over our own panel. One is
        /// enough: the raycaster walks the parent chain and refuses the graphic.
        ///
        /// <para>The walk models one rule <c>Graphic.Raycast</c> applies to that chain (job FUI34,
        /// R49 finding 1): the first group with <c>ignoreParentGroups</c> is the last one
        /// consulted. It deliberately does <b>not</b> skip a disabled group. FUI34 also added an
        /// <c>enabled</c> test on the strength of "<c>IsRaycastLocationValid</c> is
        /// <c>!enabled || blocksRaycasts</c>", which is not true of any Unity version; job FUI35
        /// (R50 finding 1) removed it. The sourced rule:
        /// <c>CanvasGroup.IsRaycastLocationValid</c> is <c>return blocksRaycasts;</c>
        /// (<c>UnityCsReference</c>, <c>Modules/UI/ScriptBindings/CanvasGroup.bindings.cs</c>), and
        /// the <c>enabled</c> skip exists only in Unity 6's <c>Graphic.Raycast</c>
        /// (<c>Unity-Technologies/uGUI</c>, branch <c>6000.0</c>, <c>Runtime/UI/Core/Graphic.cs</c>),
        /// which this build is not — the client metadata has
        /// <c>CanvasGroup.IsRaycastLocationValid</c> sharing the <c>get_blocksRaycasts</c> method address
        /// and <c>Graphic</c> with no <c>Raycast(Vector2, Camera, bool)</c>
        /// overload. Skipping a disabled group made the count too low, which for a foreign graphic
        /// is a false <c>true</c> from <see cref="TakesClickOver"/> — an over-warning about a
        /// canvas that takes nothing.</para>
        /// </summary>
        internal int NonBlockingCanvasGroups { get; init; }

        /// <summary>The graphic's own screen rect.</summary>
        internal ScreenRect Rect { get; init; }
    }

    /// <summary>
    /// Would uGUI hand this graphic a pointer over <paramref name="panelRect"/>? Job FUI33, closing
    /// R48 finding 1.
    ///
    /// <para><b>What was wrong.</b> Job FUI31 promoted <c>canvasAbove</c> from a reported field to a
    /// <see cref="Classify"/> verdict on a read that tested four things — enabled, raycast target,
    /// own canvas, rects overlap — while <c>GraphicRaycaster</c> tests more before it will produce
    /// a hit. The one that matters most is not hypothetical to this codebase: a
    /// <c>CanvasGroup</c> with <c>blocksRaycasts</c> off is exactly how a game leaves a view
    /// visible and inert, and it is the single filter this framework already models for <b>our</b>
    /// panel (the <see cref="UiInteractionVerdict.RaycastsBlocked"/> verdict). Without it a
    /// parked view on the main-menu canvas made <em>every</em> mod panel
    /// shown on that screen log a warning saying part of it is unclickable — about a canvas that
    /// takes nothing — and FUI31's own owed-check list maps that reading to "build a per-row
    /// overlap read", which would have been built to fix a canvas that was never in the way.</para>
    ///
    /// <para><b>Alpha is not a filter, and was (job FUI34, R49 finding 1).</b> FUI33 added
    /// <c>g.Alpha &gt; VisibleAlpha</c> alongside the raycaster's own filters. Nothing in uGUI reads
    /// <c>Graphic.color.a</c> on the hit path: <c>GraphicRaycaster.Raycast</c> skips on
    /// <c>depth == -1</c>, <c>!raycastTarget</c> and <c>canvasRenderer.cull</c>, then the rect test,
    /// then <c>Graphic.Raycast</c> applies <c>CanvasGroup.blocksRaycasts</c> and any
    /// <c>ICanvasRaycastFilter</c>. A full-screen <c>Image</c> at alpha 0 with <c>raycastTarget</c>
    /// on therefore takes the click — and that is not a stray spacer (a spacer has
    /// <c>raycastTarget</c> off and is already refused two lines up), it is the canonical uGUI
    /// modal blocker: the one graphic that eats a mod panel's clicks while leaving no trace in a
    /// screenshot. Filtering it out made the probe silent about exactly the shape it exists to
    /// name. The value is still read into <see cref="CoveringGraphicReading.Alpha"/>, where it is a
    /// diagnostic and not a verdict.</para>
    ///
    /// <para><b>Which direction this errs in.</b> Not one direction any more — the invariant is a
    /// list, because the reads are not all of one shape:</para>
    /// <list type="bullet">
    /// <item><description><b>Towards <c>true</c> where uGUI would say no</b> (over-warning, safe for
    /// an instrument whose job is to name a dead panel):
    /// <c>Image.alphaHitTestMinimumThreshold</c> (per-pixel, opt-in per Image, needs a readable
    /// texture) and <c>GraphicRaycaster.ignoreReversedGraphics</c> (needs the graphic's world
    /// normal) are further refusals uGUI can make that this method does not model.</description></item>
    /// <item><description><b>Towards <c>false</c> where uGUI would say yes</b> (under-warning, the
    /// dangerous direction, so it is named rather than left implied): the
    /// <see cref="CoveringGraphicReading.NonBlockingCanvasGroups"/> walk stops at the first
    /// <c>ignoreParentGroups</c> group, but it does not stop where
    /// <c>Graphic.Raycast</c> stops for a parent <c>Canvas</c> with <c>overrideSorting</c>. A
    /// non-blocking group above such a canvas is counted here and ignored by uGUI, and the graphic
    /// then reports as taking nothing. Unreachable on the screens this probe runs against today
    /// (the canvases at or above <c>UiOwnership.OverlaySorting</c> are the roots of their own
    /// chains), which is why it is documented rather than modeled.</description></item>
    /// </list>
    /// </summary>
    internal static bool TakesClickOver(CoveringGraphicReading g, ScreenRect panelRect)
    {
        // Cheapest and most selective first: geometry decides most candidates and costs a
        // comparison, the parent-chain count is the read that costs a walk.
        if (!g.Enabled || !g.RaycastTarget) return false;
        if (!g.OwnCanvas) return false;
        if (!RectsOverlap(panelRect, g.Rect)) return false;
        if (g.Culled) return false;

        // Depth is the renderer's own answer to "am I in the batch"; -1 is uGUI's marker for
        // nothing drawn. Anything >= 0 is drawn, and a build that ever reports something else
        // here should read as drawn rather than as absent.
        if (g.Depth < 0) return false;
        return g.NonBlockingCanvasGroups == 0;
    }

    /// <summary>
    /// The screen-level line: every read, then the verdict, then the one sentence that says what to
    /// do about it. One line, because the question it answers is asked while reading a log next to
    /// a screenshot.
    /// </summary>
    internal static string Describe(UiInteractionReading r)
    {
        if (r == null) return ProbeTag + " produced no reading at all.";

        var verdict = Classify(r);
        return ProbeTag + " at " + Word(r.When) + " for panel '" + r.Panel + "': "
            + "verdict=" + verdict
            + " eventSystems=" + r.Instances + " active=" + r.ActiveInstances
            + " [" + r.InstanceDetail + "]"
            + " current=" + (r.CurrentExists ? Word(r.CurrentName) : "none")
            + " module=" + (r.InputModule.Length == 0 ? "none" : r.InputModule)
            + " pointerOverUi=" + Flag(r.PointerOverUi)
            + " canvas=" + Word(r.CanvasName)
            + " order=" + r.CanvasSortingOrder
            + " override=" + Flag(r.CanvasOverrideSorting)
            + " mode=" + Word(r.CanvasRenderMode)
            + " canvasEnabled=" + Flag(r.CanvasEnabled)
            + " raycaster=" + Flag(r.RaycasterPresent) + "/" + Flag(r.RaycasterEnabled)
            + " blockingCanvasGroups=" + r.BlockingCanvasGroups
            + " canvasAbove=" + (r.CanvasAboveExists
                ? Word(r.CanvasAboveName) + "@" + r.CanvasAboveOrder
                  + " overlapsPanel=" + Flag(r.CanvasAboveOverlaps)
                : "none")
            + " — " + Consequence(verdict, r);
    }

    private static string Consequence(UiInteractionVerdict verdict, UiInteractionReading r) => verdict switch
    {
        UiInteractionVerdict.Unreadable =>
            "the probe's own reads failed, so nothing here is claimed; treat every field as unknown.",
        UiInteractionVerdict.NoEventSystem =>
            "there is no EventSystem in this process, so NOTHING on any canvas is raycast on this"
            + " screen. A mod panel's button is not being swallowed, it is never asked about, and no"
            + " change to sorting order, raycast targets or canvas groups can fix it.",
        UiInteractionVerdict.NoActiveEventSystem =>
            "every EventSystem instance is inactive or disabled, so no canvas is raycast on this"
            + " screen. Same consequence as having none: the panel is never asked about.",
        UiInteractionVerdict.NoCurrentEventSystem =>
            "an active EventSystem exists but EventSystem.current is null, so nothing dispatches"
            + " pointer events. Something took the current one away without a replacement.",
        UiInteractionVerdict.NoInputModule =>
            "EventSystem.current has no currentInputModule, so it is running and processing no"
            + " pointer events at all.",
        UiInteractionVerdict.NoRaycaster =>
            "the mod overlay canvas has no enabled GraphicRaycaster of its own, so uGUI never"
            + " considers anything on it — the panel draws and cannot be hit.",
        UiInteractionVerdict.RaycastsBlocked =>
            "a CanvasGroup over this panel has blocksRaycasts off, so every graphic under it stays"
            + " visible and untouchable.",
        UiInteractionVerdict.CanvasAboveOverlaps =>
            "every link is present, but " + Word(r.CanvasAboveName) + " sorts at order "
            + r.CanvasAboveOrder + ", at or above ours, and one of ITS OWN raycast targets covers"
            + " part of this panel — so a click on the covered part goes there and not to us. The"
            + " rest of the panel is reachable. Raising our sorting order is the wrong answer:"
            + " above the main-menu canvas a mod panel also outranks the game's loading screens,"
            + " modal popups and disconnect UI (job FUI31).",
        _ => r.CanvasAboveExists
            ? (r.CanvasAboveOverlaps == Reading.No
                ? "every link is present and " + Word(r.CanvasAboveName) + " sorts at order "
                  + r.CanvasAboveOrder + ", at or above ours, with no raycast target of its own over"
                  + " this panel — so it takes nothing from it and a click should reach us."
                : "every link is present, so a click over this panel should reach it — but "
                  + Word(r.CanvasAboveName) + " is raycastable at order " + r.CanvasAboveOrder
                  + ", at or above ours, and would take a click wherever the two overlap.")
            : "every link is present: a click over this panel should reach it.",
    };

    /// <summary>
    /// Do two screen-pixel rectangles share any area? Edges included, to match
    /// <see cref="ScreenRect.Contains"/>: a graphic that meets the panel exactly on its border is
    /// reported as touching it, because the alternative is a diagnostic that answers differently
    /// on either side of a rounding error.
    ///
    /// <para>
    /// An unknown rect on either side overlaps nothing. That is not a fail-closed default, it is
    /// the caller's contract: <c>UnityUiRuntime</c> reports an unreadable rect as
    /// <see cref="Reading.Unreadable"/> before it ever gets here, so a <c>false</c> from this
    /// method always means two rects that were both read.
    /// </para>
    /// </summary>
    internal static bool RectsOverlap(ScreenRect a, ScreenRect b)
    {
        if (!a.IsKnown || !b.IsKnown) return false;
        if (a.MaxX < b.MinX || b.MaxX < a.MinX) return false;
        if (a.MaxY < b.MinY || b.MaxY < a.MinY) return false;
        return true;
    }

    /// <summary>
    /// What one row turned out to be. Ordered like <see cref="Classify"/> and for the same reason:
    /// an inactive row explains every other reading it could produce, so it is answered first.
    /// </summary>
    internal static UiRowVerdict ClassifyRow(UiRowReading r)
    {
        if (r == null || r.Readable != Reading.Yes) return UiRowVerdict.Unreadable;
        if (r.ActiveInHierarchy != Reading.Yes) return UiRowVerdict.Inactive;
        if (r.Graphics <= 0) return UiRowVerdict.NoGraphic;
        if (r.ForeignCanvasGraphics > 0) return UiRowVerdict.ForeignCanvas;
        if (r.VisibleGraphics <= 0) return UiRowVerdict.Invisible;
        if (r.RaycastGraphics <= 0) return UiRowVerdict.NotRaycastable;
        if (r.Click.Length == 0) return UiRowVerdict.NoClickTarget;
        return UiRowVerdict.Operable;
    }

    /// <summary>
    /// A label row is allowed to have no click target and no raycast target; that is what a label
    /// is. Every other verdict on any row, and any non-<see cref="UiRowVerdict.Operable"/> verdict
    /// on a row that carries a control, is a warning.
    /// </summary>
    internal static bool IsRowWarning(string kind, UiRowVerdict verdict)
    {
        if (verdict == UiRowVerdict.Operable) return false;
        bool label = string.Equals(kind, "label", System.StringComparison.Ordinal);
        if (label && (verdict == UiRowVerdict.NoClickTarget || verdict == UiRowVerdict.NotRaycastable))
            return false;
        return true;
    }

    /// <summary>
    /// The per-row line. It carries <c>source=</c> so the clone-or-build question and the
    /// does-it-work question are answered by the same sentence, which is the pairing the deny
    /// panel's Close button needed and did not have.
    /// </summary>
    internal static string DescribeRow(UiRowReading r)
    {
        if (r == null) return RowTag + " produced no reading at all.";

        var verdict = ClassifyRow(r);
        return RowTag + " '" + r.Panel + "' / " + Word(r.RowName)
            + " kind=" + Word(r.Kind)
            + " source=" + SourceWord(r.Source)
            + " verdict=" + verdict
            + " activeSelf=" + Flag(r.ActiveSelf)
            + " activeInHierarchy=" + Flag(r.ActiveInHierarchy)
            + " graphics=" + r.Graphics
            + " visible=" + r.VisibleGraphics
            + " raycast=" + r.RaycastGraphics
            + " foreignCanvas=" + r.ForeignCanvasGraphics
            + " nestedCanvases=" + r.NestedCanvases
            + " click=" + (r.Click.Length == 0 ? "none" : r.Click)
            + " — " + RowConsequence(verdict);
    }

    private static string RowConsequence(UiRowVerdict verdict) => verdict switch
    {
        UiRowVerdict.Unreadable =>
            "the row's own reads failed; nothing here is claimed.",
        UiRowVerdict.Inactive =>
            "the row is INACTIVE, so it draws nothing and receives nothing while still being"
            + " measured, laid out and counted — every measurement this module takes passes"
            + " includeInactive. An empty band in a panel whose layout line counts the row is this.",
        UiRowVerdict.ForeignCanvas =>
            "a graphic in this row renders through a canvas that is not the panel's overlay, so it"
            + " sorts by that canvas's order and is raycast by that canvas's raycaster — neither of"
            + " which is ours. A cloned vanilla donor that brought its own Canvas does this.",
        UiRowVerdict.NoGraphic =>
            "the row carries no Graphic, so there is nothing to draw and nothing to hit.",
        UiRowVerdict.Invisible =>
            "every graphic in the row is disabled or fully transparent: the row occupies its band"
            + " and paints nothing into it.",
        UiRowVerdict.NotRaycastable =>
            "nothing in the row is a raycast target, so a pointer passes through it even with a live"
            + " EventSystem.",
        UiRowVerdict.NoClickTarget =>
            "the row has nothing that can raise a click.",
        _ => "visible, raycastable and clickable.",
    };

    /// <summary>The <c>source=</c> token, kept short and identical to <see cref="ControlBuildPlan"/>'s.</summary>
    private static string SourceWord(ControlSource source) => source switch
    {
        ControlSource.Cloned => "clone",
        ControlSource.Built => "built",
        ControlSource.BuiltAfterCloneRejected => "built-after-clone-rejected",
        ControlSource.NoneAfterCloneRejected => "none-after-clone-rejected",
        _ => "none",
    };

    private static string Flag(Reading r) => r switch
    {
        Reading.Yes => "yes",
        Reading.No => "no",
        _ => "unreadable",
    };

    /// <summary>
    /// An empty or whitespace value prints as <c>?</c> rather than as a gap in the line. A field
    /// that silently vanishes is a field a reader mis-parses as the next one.
    /// </summary>
    private static string Word(string? s) => string.IsNullOrWhiteSpace(s) ? "?" : s;
}
