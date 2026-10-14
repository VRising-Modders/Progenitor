namespace Progenitor.Ui;

/// <summary>What a read of the scrollbar's own active flag actually found.</summary>
internal enum ScrollbarState
{
    /// <summary>The flag threw when it was read. Reported as unknown, never as hidden.</summary>
    Unreadable = 0,

    /// <summary><c>ScrollRect</c> has the bar's GameObject active.</summary>
    Shown = 1,

    /// <summary><c>ScrollRect</c> has the bar's GameObject inactive.</summary>
    Hidden = 2,
}

/// <summary>What a pass over a panel does with the verdict it may still owe a read-back.</summary>
internal enum ReadBackAction
{
    /// <summary>Nothing is owed, or the panel has no scroll view: clear the pending flag, log nothing.</summary>
    None = 0,

    /// <summary>The bar cannot hold the answer yet and a later pass can still read it: keep it pending.</summary>
    Defer = 1,

    /// <summary>The bar cannot hold the answer yet and this pass ends the verdict: clear it, log nothing.</summary>
    Drop = 2,

    /// <summary>The bar holds <c>ScrollRect</c>'s answer to this verdict: read it and log.</summary>
    Read = 3,
}

/// <summary>
/// The sentence a panel prints when it reads back what actually happened to its scrollbar.
///
/// <para>
/// <b>Why this exists (R45 finding 22, over evidence 175).</b> Two layers compute whether the list
/// overflows: <c>PanelLayout.Scrolls</c>, which is arithmetic over numbers we chose, and
/// <c>ScrollRect</c>, which measures the real rects and is the thing that draws — or does not draw
/// — the bar. Evidence 175 caught them disagreeing (<c>Scrolls(211, 211) == false</c> against
/// <c>211 &gt; 195</c>) and the disagreement was invisible because the log printed the half that
/// does not draw. <c>c9ee05d</c> corrected the inset so the two should now agree, which is a fix by
/// construction with nothing asserting it, and left a hand-written <c>SetActive</c> beside the
/// <c>AutoHide</c> that had replaced it — two owners of one GameObject.
/// </para>
///
/// <para>
/// So there is one owner, <c>AutoHide</c>, and the panel reads that owner's answer back and prints
/// it beside its own. The read is deliberately <b>deferred to the next pass over the panel</b>
/// rather than taken at the end of the layout that set the content size: <c>ScrollRect</c> decides
/// in its own update, so a read taken immediately would report the previous content's answer while
/// looking like this one's. A verdict is therefore logged, and then checked the next time anything
/// touches the panel — a re-layout or a reposition — which is the first moment the answer is about
/// the content that produced it. "Next pass" means a later frame with the panel live: see
/// <see cref="Decide"/>.
/// </para>
/// </summary>
internal static class ScrollbarVerdict
{
    /// <summary>
    /// True when our arithmetic and the bar's own flag say the same thing. An unreadable flag is
    /// not agreement and is not disagreement; see <see cref="Describe"/>.
    /// </summary>
    internal static bool Agrees(bool ourScrolls, ScrollbarState observed) =>
        (ourScrolls && observed == ScrollbarState.Shown) || (!ourScrolls && observed == ScrollbarState.Hidden);

    /// <summary>
    /// A disagreement, or a flag that could not be read, is the line worth reading; agreement is not. Every
    /// read-back is a file-only diagnostic now (docs/bridge.md, "Logging policy"), so this only classifies.
    /// </summary>
    internal static bool IsWarning(bool ourScrolls, ScrollbarState observed) => !Agrees(ourScrolls, observed);

    /// <summary>
    /// P7b: whether an armed read-back is taken at all. A panel with no scroll view (<c>NoScroll</c>, the pick band)
    /// has no bar whose answer could agree or disagree with the layout's, so it is never read, whenever it was armed.
    /// </summary>
    internal static bool ShouldReadBack(bool pending, bool noScroll) => pending && !noScroll;

    /// <summary>
    /// Whether the bar can hold <c>ScrollRect</c>'s answer to the pending verdict, and what to do when it cannot.
    ///
    /// <para>
    /// <b>Why (2026-10-14 live passes, about 160 false "they disagree" lines per pass).</b> <c>ScrollRect</c>
    /// sets the bar in its own <c>LateUpdate</c>, which runs once per frame and only while it is active and
    /// enabled. The framework lays a panel out from its tick, before that update, and a panel being built
    /// re-lays itself out once per added row, several times in one frame and usually before it is shown. The
    /// "next pass" the read-back was deferred to was then the same frame, or a frame in which the panel was
    /// inactive, and the bar still carried the answer to an older content: every first build of the Widgets
    /// page and the Mods workspace read its empty-panel <c>(fits)</c> against the bar of the last open.
    /// </para>
    ///
    /// <para>
    /// So a read is taken only when the <c>ScrollRect</c> is live now and a frame has started since the
    /// verdict (<paramref name="nowFrame"/> &gt; <paramref name="verdictFrame"/>). <c>Show</c> lays the panel
    /// out before it activates it, so a verdict made while hidden is replaced before the bar can run, and
    /// <c>Hide</c> reads before it deactivates. Otherwise the pass defers the read, unless it
    /// <paramref name="endsVerdict"/> — a re-layout replaces the verdict and a hide is the last check — in
    /// which case the unsettled verdict is dropped unlogged: it was never checkable, and printing it as a
    /// disagreement is the false report this exists to stop. An unreadable frame counter is passed as
    /// <see cref="int.MinValue"/> and never reads.
    /// </para>
    /// </summary>
    internal static ReadBackAction Decide(
        bool pending, bool noScroll, bool scrollLive, int verdictFrame, int nowFrame, bool endsVerdict)
    {
        if (!ShouldReadBack(pending, noScroll)) return ReadBackAction.None;
        bool settled = scrollLive && nowFrame != int.MinValue && verdictFrame != int.MinValue && nowFrame > verdictFrame;
        if (settled) return ReadBackAction.Read;
        return endsVerdict ? ReadBackAction.Drop : ReadBackAction.Defer;
    }

    /// <summary>
    /// Whether a read-back line is printed: only when it differs from the last one this panel printed since it
    /// was shown. A panel re-laid out on every keystroke otherwise repeats the same agreeing sentence each time.
    /// </summary>
    internal static bool ShouldLog(string? lastLogged, string line) => !string.Equals(lastLogged, line, System.StringComparison.Ordinal);

    /// <summary>
    /// The line itself. It names which verdict is being checked, what was read, and when it was
    /// read, because the whole failure mode this replaces was a true sentence about our numbers
    /// printed next to a screen showing somebody else's.
    /// </summary>
    internal static string Describe(string panel, bool ourScrolls, ScrollbarState observed, string when)
    {
        string ours = ourScrolls ? "(scrolls)" : "(fits)";
        string head = "Ui: panel '" + panel + "' scrollbar read back at " + when + ": ";

        return observed switch
        {
            ScrollbarState.Shown when ourScrolls =>
                head + "ScrollRect has the bar shown, and the last verdict was " + ours + " — they agree.",
            ScrollbarState.Hidden when !ourScrolls =>
                head + "ScrollRect has the bar hidden, and the last verdict was " + ours + " — they agree.",
            ScrollbarState.Shown =>
                head + "ScrollRect has the bar SHOWN while the last verdict was " + ours
                + " — they disagree. The verdict is PanelLayout's arithmetic and the bar is"
                + " ScrollRect's measurement of the real rects; evidence 175 is these two drifting"
                + " by one constant.",
            ScrollbarState.Hidden =>
                head + "ScrollRect has the bar HIDDEN while the last verdict was " + ours
                + " — they disagree. The verdict is PanelLayout's arithmetic and the bar is"
                + " ScrollRect's measurement of the real rects; evidence 175 is these two drifting"
                + " by one constant.",
            _ =>
                head + "the bar's own active flag could not be read, so whether it agrees with "
                + ours + " is unknown — not assumed.",
        };
    }
}
