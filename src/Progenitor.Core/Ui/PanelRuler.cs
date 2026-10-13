namespace Progenitor.Ui;

/// <summary>
/// Which ruler a panel was measured against. Job FDOC5, closing R43 finding 5 (still open at R45
/// finding 38, in a file the batch between them rewrote 1,078 lines of).
/// </summary>
internal enum UiRuler
{
    /// <summary>The overlay canvas's own rect, in canvas units. The right answer.</summary>
    Canvas = 0,

    /// <summary>
    /// <c>Screen.height</c> / <c>Screen.width</c>, in device pixels. A <b>different coordinate
    /// system</b> from the one the panel is laid out in whenever the overlay adopted the game's
    /// <c>CanvasScaler</c>, which is why <see cref="PanelRuler"/> refuses to reach for it in that
    /// case. Reachable only on a build or a scene where no vanilla scaler could be copied, where
    /// the overlay really is at constant pixel size and one canvas unit really is one pixel.
    /// </summary>
    Screen = 1,

    /// <summary>
    /// <c>PanelLayout.FallbackScreenHeight</c> / <c>PanelFlow.FallbackCanvasWidth</c>. Nothing
    /// could be read; the panel is measured against a guess.
    /// </summary>
    Constant = 2,
}

/// <summary>
/// One measurement of the space a panel has, and the ruler it came from.
/// </summary>
internal readonly struct RulerReading
{
    internal RulerReading(float value, UiRuler ruler)
    {
        Value = value;
        Ruler = ruler;
    }

    internal float Value { get; }

    internal UiRuler Ruler { get; }

    /// <summary>The token the layout line prints: <c>canvas</c> / <c>screen</c> / <c>constant</c>.</summary>
    internal string Name => Ruler switch
    {
        UiRuler.Canvas => "canvas",
        UiRuler.Screen => "screen",
        _ => "constant",
    };
}

/// <summary>
/// The fallback chain behind <c>UnityUiRuntime.Panel.AvailableHeight</c>/<c>AvailableWidth</c>,
/// kept here so it is game-free and can be tested, and so the chain is written once rather than
/// once per axis.
///
/// <para><b>R43 finding 5, carried through R44 and R45 and closed here.</b> The doc comment on
/// <c>AvailableHeight</c> was explicit and correct — the overlay copies the game's
/// <c>CanvasScaler</c>, so one canvas unit is not one device pixel, and <c>Screen.height</c>
/// "would cap the panel against a number from a different coordinate system" — and the method then
/// fell back to <c>Screen.height</c> anyway, with nothing in the log saying which rung was used.
/// The branch is not hypothetical by the author's own hand: the <c>height &gt; 0f</c> guard exists
/// because the canvas rect can read zero, and the moment it most plausibly does is the only moment
/// this code runs, since <c>SettingsPanel.Open</c> builds a fresh panel and lays it out in the
/// same frame, before Unity's canvas update has sized the canvas. On a 4K display with the game's
/// UI scaled up, a canvas ~1080 units tall and a <c>Screen.height</c> of 2160 gave a panel capped
/// at 1728 canvas units inside a 1080-unit canvas — evidence 163's original symptom, reintroduced
/// by the fallback of the fix for it, reported as <c>panel=1728px (fits)</c>.</para>
///
/// <para><b>Two changes, and the second is the one that matters.</b> The reading now carries the
/// rung it came from, so a panel capped against the wrong unit is diagnosable rather than
/// invisible; and when the overlay is in the game's design space the <c>Screen</c> rung is
/// <b>skipped entirely</b> — a wrong number must not also be a plausible one. The constant is
/// wrong in a way a reader can see (it is always 1080) where a scaled <c>Screen.height</c> is
/// wrong in a way that looks like a measurement.</para>
/// </summary>
internal static class PanelRuler
{
    /// <summary>
    /// Pick the reading. <paramref name="canvas"/> and <paramref name="screen"/> are the raw
    /// values as read, with anything <c>&lt;= 0</c> (or unreadable) passed as <c>0f</c>;
    /// <paramref name="designSpace"/> is whether the overlay's scaler is scaling with the screen,
    /// i.e. whether canvas units and device pixels are different things.
    /// </summary>
    internal static RulerReading Choose(float canvas, bool designSpace, float screen, float constant)
    {
        if (canvas > 0f) return new RulerReading(canvas, UiRuler.Canvas);

        // The rung R43 asked to be dropped. In design space `screen` answers in a unit this panel
        // is not measured in, so it is not a worse measurement -- it is not a measurement.
        if (!designSpace && screen > 0f) return new RulerReading(screen, UiRuler.Screen);

        return new RulerReading(constant, UiRuler.Constant);
    }
}

/// <summary>
/// Why the canvas rung answered or did not (job FUI32). <see cref="PanelRuler.Choose"/> is handed
/// a single <c>0f</c> for every failure, which was enough to pick a rung and not enough to
/// diagnose one: "the rect is zero", "there is no <c>RectTransform</c>" and "the read threw" are
/// three different defects with three different fixes, and the layout line said the same word for
/// all three.
/// </summary>
internal enum CanvasRead
{
    /// <summary>The rect was read and is positive. The canvas rung answers.</summary>
    Sized = 0,

    /// <summary>
    /// The rect was read and is zero or negative — Unity has not sized this canvas yet. This is
    /// the one <see cref="RulerSettle"/> exists for and, on this build, the only one ever
    /// observed: a root <c>ScreenSpaceOverlay</c> canvas created during <c>Update</c> has a zero
    /// rect until the canvas update at the end of that frame.
    /// </summary>
    Zero = 1,

    /// <summary>No overlay, or no <c>RectTransform</c> on it. A structural defect, not a timing one.</summary>
    NoRect = 2,

    /// <summary>The read threw. An interop defect, not a timing one.</summary>
    Threw = 3,
}

/// <summary>
/// What a layout pass has to say about <i>when</i> it was measured, as opposed to which rung
/// answered (job FUI32).
/// </summary>
internal enum RulerSettleState
{
    /// <summary>The canvas answered, and no earlier pass of this panel was owed a re-measure.</summary>
    Final = 0,

    /// <summary>
    /// The canvas did not answer, so this pass sized the panel against a fallback and a
    /// re-measure is armed for a later frame. <b>A reader must not trust this line's numbers.</b>
    /// </summary>
    Armed = 1,

    /// <summary>The re-measure, taken after the canvas became readable. This is the true pass.</summary>
    Settled = 2,

    /// <summary>
    /// The canvas never became readable inside the budget. The panel keeps the fallback, and the
    /// log says so rather than leaving the last line looking like a measurement.
    /// </summary>
    GaveUp = 3,
}

/// <summary>What <see cref="RulerSettle.Step"/> tells the frame pass to do with an owed panel.</summary>
internal enum RulerSettleStep
{
    /// <summary>The canvas is still unreadable and there is budget left. Ask again next frame.</summary>
    Wait = 0,

    /// <summary>The canvas answers now. Lay the panel out again against the real ruler.</summary>
    Relayout = 1,

    /// <summary>Budget spent. Stop asking and say, once, that the panel is on a guess.</summary>
    GiveUp = 2,
}

/// <summary>
/// The deferred re-measure behind <c>UnityUiRuntime.Panel</c>'s layout pass — game-free, so the
/// decision has tests where the reads it is made from cannot (job FUI32).
///
/// <para><b>The measurement FDOC5 owed, taken.</b> FDOC5 closed R43 finding 5 by making the
/// fallback chain one decision and putting <c>ruler=</c> on the layout line, and predicted that
/// the canvas rung would be the one that answered. It never is. Every panel on every screen
/// reported <c>ruler=constant</c>, so every panel is sized from <c>FallbackScreenHeight</c> rather
/// than from anything measured.</para>
///
/// <para><b>What that did NOT cost, measured 2026-09-05 and worth stating because the obvious
/// guess is wrong.</b> The live run reads <c>avail=1080u</c> from the canvas rung too - the same
/// number the constant supplies. The canvas is 1080 DESIGN units because <c>AdoptGameScaler</c>
/// puts a <c>ScaleWithScreenSize</c> scaler on it against a 1080-tall reference; the client's
/// 1024 is device pixels and never reaches this arithmetic. So the panels were not mis-sized on
/// this client, and the earlier reading of this defect - that a 1024-tall client got
/// <c>0.8 x 1080 = 864u</c> where <c>819u</c> was meant - was wrong, twice, before the rung was
/// made to answer. What was real is narrower and still worth fixing: a fallback was reporting as
/// though it were a measurement, and the two agree only while the game's reference resolution
/// equals <see cref="PanelLayout.FallbackScreenHeight"/>. Nothing guarantees that, and when it
/// stops being true this line is what will say so.</para>
///
/// <para><b>The mechanism, and it is not a race.</b> <c>CreatePanel</c> builds this panel a canvas
/// of its own; <c>SettingsPanel.Open</c> then fills it and shows it inside the same
/// <c>Plugin.Frame</c> tick, so the layout pass runs during <c>Update</c> on a canvas created
/// during that same <c>Update</c>. A root <c>ScreenSpaceOverlay</c> canvas's <c>RectTransform</c>
/// is sized by Unity's canvas update in <c>PostLateUpdate</c>, after every <c>Update</c> has run,
/// so its rect is <c>(0,0)</c> for the whole of the frame it was created in. Deterministic, every
/// panel, every screen — which is exactly what was observed.</para>
///
/// <para><b>And one frame is enough, which is a fact about this build rather than a hope.</b>
/// This uGUI's <c>CanvasScaler</c> has no <c>Update</c>; it recomputes from
/// <c>Canvas.preWillRenderCanvases</c> (<c>CanvasScaler.Canvas_preWillRenderCanvases</c>,
/// client), which fires immediately <i>before</i> the same end-of-frame canvas
/// update. So <c>AdoptGameScaler</c>'s <c>ScaleWithScreenSize</c> is already in effect when Unity
/// sizes the rect at the end of the creation frame, and the first readable rect is already in the
/// game's design space. Had this build's scaler still driven <c>Handle</c> from <c>Update</c>, the
/// creation frame would have sized the rect at <c>scaleFactor = 1</c> and the next frame would
/// have read a pixel-sized rect and called it <c>canvas</c> — a wrong number wearing the right
/// token, which is the failure mode FDOC5 refused for the <c>Screen</c> rung. The budget below
/// exists for the frames where the canvas update does not run at all (a minimized window, a load),
/// not because the timing is uncertain.</para>
/// </summary>
internal static class RulerSettle
{
    /// <summary>
    /// How many frames the canvas is given to become readable. One is always enough on this build
    /// (see the type doc); the rest is headroom for frames where Unity's canvas update does not
    /// run, and a bound so a panel on a screen that never sizes says so once instead of
    /// re-measuring for the rest of the session.
    /// </summary>
    internal const int MaxFrames = 8;

    /// <summary>The token the layout line prints for <c>canvas=</c>.</summary>
    internal static string Name(CanvasRead read) => read switch
    {
        CanvasRead.Sized => "sized",
        CanvasRead.Zero => "zero",
        CanvasRead.NoRect => "norect",
        _ => "threw",
    };

    /// <summary>The token the layout line prints for <c>settle=</c>.</summary>
    internal static string Name(RulerSettleState state) => state switch
    {
        RulerSettleState.Final => "final",
        RulerSettleState.Armed => "armed",
        RulerSettleState.Settled => "settled",
        _ => "gaveup",
    };

    /// <summary>
    /// Whether a pass measured with this reading is owed a re-measure. Only the canvas is final:
    /// both fallbacks answer about something other than this canvas, so both are provisional until
    /// the canvas can be asked.
    /// </summary>
    internal static bool Owed(RulerReading reading) => reading.Ruler != UiRuler.Canvas;

    /// <summary>
    /// The <c>settle=</c> token for a pass. <paramref name="settling"/> is whether this pass is
    /// the re-measure a previous one armed; <paramref name="exhausted"/> is whether the budget has
    /// already been spent on this panel.
    /// </summary>
    internal static RulerSettleState StateOf(RulerReading reading, bool settling, bool exhausted)
    {
        if (!Owed(reading)) return settling ? RulerSettleState.Settled : RulerSettleState.Final;

        return exhausted ? RulerSettleState.GaveUp : RulerSettleState.Armed;
    }

    /// <summary>
    /// What the frame pass should do, given a fresh reading and how many frames this panel has
    /// waited. The budget is checked <i>after</i> the reading, so a canvas that answers on the
    /// last allowed frame is still used rather than thrown away.
    /// </summary>
    internal static RulerSettleStep Step(RulerReading now, int framesWaited, int budget)
    {
        if (!Owed(now)) return RulerSettleStep.Relayout;

        return framesWaited >= budget ? RulerSettleStep.GiveUp : RulerSettleStep.Wait;
    }

    /// <summary>
    /// The line logged when the canvas becomes readable, naming <b>both</b> readings. Without it
    /// the log would carry a first pass at the wrong size and a second at the right one with
    /// nothing saying they are the same panel or which to believe — printing only the final answer
    /// is the thing this job was told not to do.
    /// </summary>
    internal static string Describe(string panel, RulerReading laidOutWith, RulerReading now, int frames) =>
        "Ui: panel '" + panel + "' canvas readable after " + frames + Frames(frames)
        + ": the pass above measured avail=" + ((int)laidOutWith.Value) + "u ruler=" + laidOutWith.Name
        + ", this canvas is avail=" + ((int)now.Value) + "u ruler=" + now.Name
        + " -- that pass sized the panel against the wrong ruler and the re-layout below replaces it.";

    /// <summary>
    /// The line logged when the budget runs out, so the last thing said about the panel is that
    /// its size is a guess rather than a number that merely looks like one.
    /// </summary>
    internal static string DescribeGiveUp(string panel, RulerReading laidOutWith, CanvasRead read, int frames) =>
        "Ui: panel '" + panel + "' canvas never became readable (" + frames + Frames(frames)
        + ", canvas=" + Name(read) + "): the panel keeps avail=" + ((int)laidOutWith.Value)
        + "u ruler=" + laidOutWith.Name + ", which is a guess and not a measurement of this screen.";

    private static string Frames(int frames) => frames == 1 ? " frame" : " frames";
}
