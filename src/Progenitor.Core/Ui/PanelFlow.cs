using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>
/// Where a panel's frame sits relative to the center of the overlay canvas, <b>in canvas
/// units</b> — the same space <c>RectTransform.anchoredPosition</c> and
/// <c>RectTransform.sizeDelta</c> are in, and deliberately not the screen pixels
/// <see cref="ScreenRect"/> uses. The two rulers are the subject of evidence 165: the overlay
/// copies the game's <c>CanvasScaler</c>, so a canvas unit is not a device pixel. Placement is
/// arithmetic on the canvas's own rect and never leaves it; only the hover hit test crosses over
/// into pixels, because only the mouse forces it to.
/// </summary>
internal readonly struct PanelOffset
{
    /// <summary>Dead center — what a single open panel gets, and what every screenshot shows.</summary>
    internal static readonly PanelOffset Centre = default;

    internal PanelOffset(float x, float y)
    {
        X = x;
        Y = y;
    }

    /// <summary>Rightward from the canvas center.</summary>
    internal float X { get; }

    /// <summary>Upward from the canvas center; the overflow cascade steps down, so this is negative.</summary>
    internal float Y { get; }

    internal bool IsCentre => X == 0f && Y == 0f;
}

/// <summary>
/// Which of the shown panels this one is: the <see cref="Rank"/>th of <see cref="Count"/>.
///
/// <para>
/// <b>Why a rank and not a slot (job FUI21).</b> FUI20 handed the runtime a slot id and let the
/// panel place itself from it alone. That is enough for a cascade, where a panel's position
/// depends only on its own index, and not enough for a layout that lays panels out <i>next to</i>
/// each other: two panels are centered as a pair, three as a triple, so where the first one goes
/// is a fact about how many are open. The rank is that panel's place in the shown set and the
/// count is the size of the set, which is everything the arithmetic needs and nothing about the
/// other panels' contents — the framework is the only layer that can see the whole set, so the
/// framework is where the set is counted (<c>Ui.ReflowPanels</c>).
/// </para>
///
/// <para>
/// The slot survives underneath as the <i>ordering</i>: <see cref="CascadeSlots"/> still hands out
/// the lowest free index, so the longest-open panel keeps the lowest slot and therefore the
/// leftmost column, and a panel that opens and closes does not shuffle the ones that stayed.
/// </para>
/// </summary>
internal readonly struct PanelSpot
{
    /// <summary>A panel that is not shown and holds no place in the set.</summary>
    internal static readonly PanelSpot None = new(-1, 0);

    internal PanelSpot(int rank, int count)
    {
        Rank = rank;
        Count = count;
    }

    /// <summary>This panel's index among the shown panels, ordered by slot.</summary>
    internal int Rank { get; }

    /// <summary>How many panels are shown, this one included.</summary>
    internal int Count { get; }

    /// <summary>False for <see cref="None"/> and for any pair that cannot describe a set.</summary>
    internal bool IsPlaced => Rank >= 0 && Count > 0 && Rank < Count;
}

/// <summary>
/// Where the shown panels go. Game-free, so the arithmetic that decides whether a panel lands on
/// top of another one, or off the edge of the screen, has tests — the same reason
/// <see cref="PanelLayout"/> and <see cref="HoverRouter"/> are separate types.
///
/// <para>
/// <b>Why this replaces FUI20's cascade (job FUI21).</b> FUI20 fixed "every panel is drawn dead
/// center" by stepping each further panel 40 units down and right. The mechanism worked — the log
/// showed <c>slot=0 at=(0,0)</c> and <c>slot=1 at=(40,-40)</c> — and the screen was still
/// unreadable: the frame is 640 wide, so a 40-unit step leaves two panels overlapping by more
/// than 90%, titles on titles and rows interleaved. The defect FUI20 set out to fix was still
/// visibly present; only its mechanism had changed.
/// </para>
///
/// <para>
/// <b>A cascade is the wrong model here, not a mistuned one.</b> Cascading is a desktop-windowing
/// idiom, and it is only tolerable because the user can drag the top window off the one beneath
/// it. These panels cannot be dragged at all: there is no title-bar drag, no move affordance, no
/// z-order control. An overlap the player can resolve is a small cost; an overlap they cannot
/// resolve is a panel they simply cannot read. So the step is not the thing to tune — a step big
/// enough to make two 640x328 frames both readable is a step that has moved the second panel
/// somewhere else entirely, which is to say it is no longer a cascade but a layout.
/// </para>
///
/// <para>
/// <b>The model: tile if they fit, cascade only when they do not.</b> Every panel is exactly
/// <c>UnityUiRuntime.PanelWidth</c> wide — the frame's width is a constant, only its height is
/// content-derived (evidence 163) — so how many frames fit side by side is a question about the
/// canvas alone, answerable without knowing anything about the other panels. Shown panels are
/// laid out in <see cref="Columns"/> columns, the occupied columns centered as a group and each
/// panel centered vertically in its own. One panel is therefore dead center, two on a normal
/// client sit side by side with a real gap between them and nothing overlapping at all, and only
/// when there are more panels than columns does the surplus stack — and then by a full
/// <see cref="CascadeStep"/>, a whole title band, so the panel underneath still shows its title,
/// for as long as the canvas has room for a step that size (<see cref="CascadeStepFor"/>).
/// </para>
///
/// <para>
/// <b>Why vertical tiling is not the fallback.</b> Stacking two panels one above the other would
/// also avoid overlap, and it would need every panel's height to place any of them: a panel that
/// re-lays itself out taller (the rebind flow rewrites a row's text at runtime, R43 finding 4)
/// would then shove its neighbors around while the player is reading them. Column placement
/// depends only on the constant width and the count, so a panel's own re-layout can never move a
/// different panel.
/// </para>
///
/// <para>
/// <b>And no panel's own height moves it either</b> (job FUI23, R44 finding 5). Height used to
/// enter the arithmetic in one place — how far the overflow cascade may step before it would push
/// a frame off the bottom — as the panel's <i>own</i> height, and that was enough to break the
/// guarantee below. <see cref="DepthCapacity"/> falls as a panel grows, and the cascade offset is
/// <c>depth mod (capacity + 1)</c>, so a panel at depth 1 sat at <c>-CascadeStep</c> while it had
/// room and snapped to <c>0</c> — onto the title band of the panel above it — the moment it did
/// not. That is reachable through the rebind flow: the refusal notice appends a sentence, the row
/// grows, the re-layout pass recomputes the offset with the new height, and the panel jumps a full
/// <see cref="CascadeStep"/> under the player's own click. The room the ladder is sized against
/// is now derived from the tallest panel this canvas permits
/// (<see cref="PanelLayout.PanelHeightFor"/>'s cap), which is a function of the canvas alone, so
/// every panel on one canvas cascades on the same ladder and the offset is a pure function of
/// rank, count and canvas. It is also still safe by the same argument as before: a real panel is
/// never taller than that cap, so a ladder sized for the cap always has at least the room it
/// needs.
/// </para>
///
/// <para>
/// <b>And the ladder reaches every panel that has to stand on it</b> (job FUI25, R45 finding 4).
/// The first cut of the canvas-wide ladder kept FUI21's <c>depth mod (capacity + 1)</c> wrap while
/// replacing the capacity with a canvas-wide one, and on the shipped client canvas that capacity
/// is <b>1</b>: 1080 units, a cap of 864, 92 units of room, one whole 72-unit step. The wrap
/// period was therefore 2, and with two columns the fifth open panel is column 0 depth 2 —
/// <c>2 mod 2 == 0</c> — drawn at exactly the offset of the first, same column, same y, 100%
/// coincident. That is worse than the defect FUI21 was written to fix, which was a 40-unit step
/// leaving 90% overlap; here the covered panel shows nothing at all and the player has no way to
/// know it exists. The ladder now sizes its own step from the column's occupancy
/// (<see cref="CascadeStepFor"/>) instead of repeating: it is a whole title band while the canvas
/// affords one, and past that it is <c>room / deepest</c>, so the deepest panel lands exactly on
/// the margin and no two panels in a column ever share a place. <b>The property given up is that
/// the step is always a whole title band</b> — a column of five on this canvas steps by 23 rather
/// than 72 — and it is given up deliberately, because zero is the one step at which the covered
/// panel stops being on screen at all. <b>A second property is given up with it, and FUI25 did not
/// say so</b>: a step that depends on the column's occupancy re-spaces that column when a panel is
/// added to it. See "What a Show costs the panels already open" below for the arithmetic and the
/// argument, which job FUI28 owed R46 finding 2.
/// </para>
///
/// <para>
/// <b>What moves, and when.</b> The layout is a pure function of the shown set, so opening or
/// closing a panel re-flows all of them: two side-by-side panels become one centered panel when
/// one closes, rather than leaving the survivor parked in a column with an empty column beside it
/// (which is the "off-center with nothing to be off-center from" that FUI20 rightly rejected for
/// monotonic slot ids). The guarantee that replaces FUI20's "a panel never moves while shown" is
/// narrower and is the one that protects the player: <i>a re-layout caused by using a panel never
/// moves anything</i>. <c>UnityPanel.Relayout</c> — the pass that runs when a row's text changes
/// under the player's own click — recomputes the offset from the same rank, the same count and
/// the same canvas, and lands on the same position by construction. Only Show and Hide, which are
/// the player deliberately opening or closing something, change the set.
/// </para>
///
/// <para>
/// <b>Those three are the whole input list, and naming them is load-bearing.</b> The guarantee was
/// first written from rank and count while <see cref="Offset"/> took a fourth thing a re-layout is
/// allowed to change — the panel's own height — which is exactly how it came to be false (R44
/// finding 5). <c>PanelFlowTests.A_panel_that_grows_under_the_player_does_not_move</c> drives a
/// second layout at a <i>different</i> height and is the test that says so.
/// </para>
///
/// <para>
/// <b>What a Show costs the panels already open, in numbers</b> (job FUI28, R46 finding 2). FUI25
/// made the vertical step a function of the column's occupancy and justified the churn that
/// follows by saying an open "already re-flows the whole set sideways by design". That is false on
/// the shipped canvas from the second panel onward, and the arithmetic is short enough to settle
/// it: <c>occupied</c> is <c>min(count, columns)</c> and <see cref="Columns"/> is 2 on 1638x1080,
/// so every panel's x is settled once two panels are open, while opening the fifth takes column 0
/// from two panels to three and re-spaces it from a 72-unit step to 46. Rank 2 moves 26 units up
/// and <i>nothing moves sideways at all</i>. 26 units is more than half a row pitch
/// (<see cref="PanelLayout.MinRowHeight"/> + <see cref="PanelLayout.RowGap"/> = 48), so it can
/// change which row is under a resting cursor. That is the honest statement of the price;
/// <c>PanelFlowTests.Opening_the_fifth_panel_re_spaces_column_zero_and_moves_nothing_sideways</c>
/// pins those exact coordinates, and
/// <c>The_only_panels_a_new_panel_moves_are_the_ones_sharing_its_column</c> pins the general shape.
/// </para>
///
/// <para>
/// <b>The churn is kept, and "a Show is not a re-layout" is not the reason.</b> Naming the trigger
/// is what made the old sentence sound safe, and the trigger is not what protects the player. What
/// protects the player is whether the move is caused by the input currently aimed at the thing
/// that moves. FUI23's defect was exactly that: a click on a rebind row grew the row, the
/// re-layout moved that panel, and the next click of the same interaction landed elsewhere — a
/// control walking out from under the hand operating it, every time, with nothing the player can
/// do about it. A Show is a separate act whose own consequence is a 640-unit-wide panel appearing
/// on screen, no targeting is in flight through it, and the move it causes is bounded by
/// <see cref="CascadeRoom"/> (92 units here, so 26) where the horizontal re-flow this design
/// already accepts and depends on moves a panel 332 units at one panel → two.
/// </para>
///
/// <para>
/// <b>And the churn is not an artifact of FUI25's implementation — it is what even spacing means.</b>
/// A column whose rungs are equal and whose deepest panel lands on the margin cannot also hold
/// every rung still when a panel is added to it: the two properties are contradictory, so the only
/// way to buy stillness is to give up one of them. A ladder that never churns is available and was
/// worked out rather than waved at — a whole band while the room allows, then half the remaining
/// room per rung, a pure function of depth and canvas — and it starves the deep rungs to do it: a
/// column of three then reads 0 / −72 / −82, a 10-unit sliver for the deepest, where the shipped
/// ladder gives 0 / −46 / −92 and every covered panel shows the same amount of title. Note also
/// <b>where the churn starts</b>: while whole bands fit, <see cref="CascadeStepFor"/> returns the
/// constant and adding a panel to the column moves nothing. The first re-spacing happens at the
/// exact depth where the alternative FUI25 replaced drew two panels in the same place. The price
/// of never overlapping is that the rungs re-space, and it is paid here on purpose.
/// </para>
///
/// <para>
/// <b>Nothing goes off-screen, by construction rather than by a clamp.</b> <see cref="Columns"/>
/// only ever returns a column count whose whole group fits inside the canvas minus
/// <see cref="EdgeMargin"/>, so no horizontal clamp is needed or wanted — a clamp would put every
/// over-far panel back in the same place, which is the original defect. The overflow cascade
/// steps by <see cref="CascadeStepFor"/>, which never lets the deepest panel in a column pass
/// <see cref="CascadeRoom"/>, so the deepest panel lands at the margin and never past it. A panel
/// with no room at all (wider or taller than its canvas) is centered, which is the only honest
/// answer: it will overlap whatever else is open wherever it is put.
/// </para>
///
/// <para>
/// <b>Prior art.</b> Tiling window managers (i3, dwm) lay clients out side by side and only stack
/// when the frame budget runs out. The choice to tile first and cascade only on overflow is
/// theirs; no code is copied from any of them.
/// </para>
/// </summary>
internal static class PanelFlow
{
    /// <summary>
    /// Gap between two side-by-side frames, in canvas units. Wide enough that the two frames read
    /// as two panels rather than as one wide one, and small enough that a 1600-unit canvas still
    /// fits two 640-wide columns with room to spare.
    /// </summary>
    internal const float ColumnGap = 24f;

    /// <summary>
    /// How close a frame may come to the canvas edge. Not zero: a frame flush with the edge reads
    /// as clipped even when it is not.
    /// </summary>
    internal const float EdgeMargin = 16f;

    /// <summary>
    /// The vertical step between two panels that had to share a column, in canvas units. A full
    /// <see cref="PanelLayout.HeaderHeight"/>, not a fraction of one: the point of the step is
    /// that the panel underneath still shows its whole title band, which is the only part of a
    /// covered panel that tells the player it is there and which panel it is. FUI20's 40 cut the
    /// band in half and put the second title on top of the first one's text.
    /// </summary>
    internal const float CascadeStep = PanelLayout.HeaderHeight;

    /// <summary>Canvas width assumed when the real one cannot be read; see <see cref="PanelLayout.FallbackScreenHeight"/>.</summary>
    internal const float FallbackCanvasWidth = 1920f;

    /// <summary>
    /// The offset for <paramref name="spot"/>, given the frame width every panel shares and the
    /// size of the canvas they sit on, all in canvas units.
    ///
    /// <para>
    /// <b>The panel's own height is deliberately not a parameter</b> (job FUI23, R44 finding 5).
    /// It used to be, and the guarantee on this type — a re-layout caused by using a panel never
    /// moves anything — was written from rank and count while the code took that third input, the
    /// one thing a re-layout is allowed to change. A growing panel at depth ≥ 1 could therefore
    /// jump a whole <see cref="CascadeStep"/> under the player's click. A signature that cannot see
    /// the height is what makes the guarantee structural rather than remembered; the vertical
    /// ladder comes from <see cref="CascadeStepFor"/>, whose two inputs are the canvas and how
    /// many panels share the column — both facts about the set, neither about a panel's contents.
    /// </para>
    ///
    /// <para>
    /// Total by construction: an unplaced spot, a NaN size, a zero canvas and a panel wider than
    /// its canvas all resolve to <see cref="PanelOffset.Centre"/> rather than to a rect nobody can
    /// see. This runs inside a Unity layout pass on the main thread, where a throw leaves a panel
    /// half-placed.
    /// </para>
    /// </summary>
    internal static PanelOffset Offset(
        PanelSpot spot,
        float panelWidth,
        float canvasWidth,
        float canvasHeight)
    {
        if (!spot.IsPlaced) return PanelOffset.Centre;

        float canvasW = SaneCanvas(canvasWidth, FallbackCanvasWidth);
        float canvasH = SaneCanvas(canvasHeight, PanelLayout.FallbackScreenHeight);
        float panelW = SaneSize(panelWidth);

        int columns = Columns(canvasW, panelW);
        int occupied = spot.Count < columns ? spot.Count : columns;
        int column = spot.Rank % columns;
        int depth = spot.Rank / columns;

        // The occupied columns are centered as a group, so one panel is dead center, two straddle
        // the center, three put the middle one on it. The pitch is a frame plus a gap.
        float x = (column - ((occupied - 1) * 0.5f)) * (panelW + ColumnGap);

        float y = 0f;
        if (depth > 0)
        {
            // How many panels share this column, and therefore how far down the ladder has to
            // reach. Column c holds ranks c, c + columns, c + 2*columns, …, so the count is a
            // fact about the set and the canvas and never about any panel's contents. Rank is
            // less than Count on a placed spot and column is at most rank, so the numerator is
            // never negative.
            int inColumn = ((spot.Count - column - 1) / columns) + 1;
            y = -depth * CascadeStepFor(canvasH, inColumn - 1);
        }

        return new PanelOffset(x, y);
    }

    /// <summary>
    /// How many <paramref name="panelWidth"/>-wide frames fit side by side on this canvas with a
    /// <see cref="ColumnGap"/> between them and <see cref="EdgeMargin"/> at each end. Never less
    /// than one: a canvas too narrow for a single frame still has to put it somewhere, and that
    /// somewhere is the center.
    /// </summary>
    internal static int Columns(float canvasWidth, float panelWidth)
    {
        float canvas = SaneCanvas(canvasWidth, FallbackCanvasWidth);
        float panel = SaneSize(panelWidth);
        if (panel <= 0f) return 1;

        float usable = canvas - (2f * EdgeMargin);
        if (usable < panel) return 1;

        // n columns occupy n*(panel + gap) - gap, so n fits while n*(panel+gap) <= usable + gap.
        int columns = (int)((usable + ColumnGap) / (panel + ColumnGap));
        return columns < 1 ? 1 : columns;
    }

    /// <summary>
    /// How far down a panel on this canvas may be stepped before its bottom edge would come within
    /// <see cref="EdgeMargin"/> of the canvas's, in canvas units. Zero means there is no room to
    /// move down at all, and stepping anyway would put a footer off the screen — the one failure
    /// mode worse than an overlap, because the player cannot scroll a panel they cannot see.
    ///
    /// <para>
    /// <b>Measured from the tallest panel this canvas permits, not from the panel being placed</b>
    /// (job FUI23, R44 finding 5). Using the individual panel's height made the vertical offset a
    /// function of a quantity a re-layout changes, so a panel that grew under the player's own
    /// click could cross the threshold and snap onto the panel above it. Every panel on one canvas
    /// now cascades on the same ladder. Nothing is at risk of going off the bottom: a real panel is
    /// never taller than <see cref="PanelLayout.PanelHeightFor"/>'s cap, so a panel stepped by a
    /// ladder sized for the cap has at least as much room below it as the ladder assumed.
    /// </para>
    /// </summary>
    internal static float CascadeRoom(float canvasHeight)
    {
        float canvas = SaneCanvas(canvasHeight, PanelLayout.FallbackScreenHeight);

        // The tallest a panel on this canvas can be laid out at: PanelHeightFor's own cap, asked
        // for with more content than any panel could have. Reading the cap through the method that
        // enforces it means a change to ScreenFraction or MinPanelHeight cannot leave the two
        // halves disagreeing.
        float tallest = PanelLayout.PanelHeightFor(float.MaxValue, canvas);

        // The frame is centered, so half the leftover is available below it.
        float room = ((canvas - tallest) * 0.5f) - EdgeMargin;
        return room > 0f ? room : 0f;
    }

    /// <summary>
    /// How many whole <see cref="CascadeStep"/>s — whole title bands — fit in
    /// <see cref="CascadeRoom"/>. This is the depth at which the ladder still reads the way the
    /// design intends, not a limit on how deep it may go: past it the step shrinks
    /// (<see cref="CascadeStepFor"/>) rather than the ladder stopping or repeating.
    ///
    /// <para>
    /// On the shipped client canvas (1638x1080) this is <b>1</b>, and that number is the whole of
    /// R45 finding 4: the cap is 864, the room is 92, and 92 holds exactly one 72-unit band.
    /// </para>
    /// </summary>
    internal static int DepthCapacity(float canvasHeight)
    {
        float room = CascadeRoom(canvasHeight);
        if (room < CascadeStep) return 0;
        return (int)(room / CascadeStep);
    }

    /// <summary>
    /// The vertical step for a column whose deepest panel sits <paramref name="deepest"/> rungs
    /// below its first — i.e. one less than the number of panels sharing that column. A whole
    /// <see cref="CascadeStep"/> while the canvas has room for one at that depth, and
    /// <c>room / deepest</c> once it does not, so the deepest panel lands exactly on the margin.
    ///
    /// <para>
    /// <b>Why the step shrinks instead of the ladder repeating</b> (job FUI25, R45 finding 4).
    /// FUI21 wrapped the depth — <c>depth mod (capacity + 1)</c> — on the reasoning that a bounded
    /// repeat beats a step too small to read, which is right about a <i>fixed</i> step chosen once
    /// for every canvas and wrong about this one. A repeat's collision is not a small step, it is
    /// a step of <b>zero</b>: two frames of the same width drawn at the same offset in the same
    /// column, the lower one showing nothing whatever, so the player cannot tell it is open, which
    /// panel it is, or that anything went wrong. A 23-unit sliver of a title band is a worse panel
    /// than a 72-unit one and a far better one than a panel that is not on screen. On the shipped
    /// canvas the wrap period was 2 and five open panels put rank 4 exactly on rank 0.
    /// </para>
    ///
    /// <para>
    /// The step is a function of the canvas and the column's occupancy and of nothing else — in
    /// particular not of any panel's own height, which is the invariant FUI23 established and this
    /// keeps (see <see cref="Offset"/>). A re-layout caused by <i>using</i> a panel leaves the set
    /// alone and therefore lands on the same step, which is the guarantee that protects the player
    /// and the one this method may not break.
    /// </para>
    ///
    /// <para>
    /// <b>What that leaves true about an open, corrected</b> (job FUI28, R46 finding 2). The
    /// sentence that stood here said occupancy changes only when the player opens or closes a
    /// panel, "which already re-flows the whole set sideways by design" — offering the horizontal
    /// re-flow as the thing the vertical churn rides along with. The first clause is true and the
    /// second is false in the exact regime this method introduces. On 1638x1080 <c>occupied</c> is
    /// <c>min(count, 2)</c>, so it stops changing at two panels and no x moves again; opening the
    /// fifth panel takes column 0 from two to three, tightens its step from 72 to 46, and moves
    /// rank 2 26 units up with the whole set stationary sideways. Columns also step at different
    /// rates from each other in that state — column 1 still holds two panels and keeps its whole
    /// band — which is not wrong and was not what any of the prose described. Why the churn is kept
    /// anyway, and what the still alternative costs, is argued on <see cref="PanelFlow"/> itself;
    /// the coordinates are pinned by
    /// <c>PanelFlowTests.Opening_the_fifth_panel_re_spaces_column_zero_and_moves_nothing_sideways</c>.
    /// </para>
    ///
    /// <para>
    /// <b>Still open, and deliberately not floored.</b> <c>room / deepest</c> decays without a
    /// bound — 92/2 = 46, 92/4 = 23, 92/9 ≈ 10.2 — so a column of ten on this canvas steps by ten
    /// units, which is a title band nobody can read. A floor cannot be added without giving back
    /// one of the two properties this method exists for: either the deepest panel passes the margin
    /// and goes off the canvas, or two panels share a rung, which is the 100% overlap R45 finding 4
    /// found. Ten panels in one column means twenty open panels on this canvas and no client has
    /// ever been near it, so the honest answer is the arithmetic, said out loud (R46's note on
    /// finding 2) rather than a clamp that trades a visible defect for an invisible one.
    /// </para>
    /// </summary>
    internal static float CascadeStepFor(float canvasHeight, int deepest)
    {
        if (deepest <= 0) return 0f;

        float room = CascadeRoom(canvasHeight);
        if (room <= 0f) return 0f;

        float wanted = deepest * CascadeStep;
        return wanted <= room ? CascadeStep : room / deepest;
    }

    private static float SaneCanvas(float value, float fallback) =>
        Finite(value) && value > 0f ? value : fallback;

    private static float SaneSize(float value) =>
        Finite(value) && value > 0f ? value : 0f;

    private static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
}

/// <summary>
/// The lowest-free-index allocator behind <see cref="PanelFlow"/>. Separate from the arithmetic
/// because it is the half that has a lifetime: a slot is held for exactly as long as its panel is
/// shown, and a leaked one would count a panel that is not on screen into every later layout.
///
/// <para>
/// <b>What the slot is for since job FUI21.</b> It is no longer a position — the position comes
/// from the panel's rank in the shown set — it is the <i>order</i> that rank is taken in. Lowest
/// free means the longest-open panel keeps the lowest slot and therefore the leftmost column, so
/// a panel that opens and closes beside it does not make it swap sides.
/// </para>
///
/// <para>
/// <see cref="Release"/> is total: releasing a slot that is not held, or one out of range, is a
/// no-op rather than a throw. It is called from a Hide path that also runs from Dispose and from
/// Core Uninstall, and a double release that corrupted the set would be far worse than an
/// ignored one.
/// </para>
/// </summary>
internal sealed class CascadeSlots
{
    private readonly List<bool> _used = new();

    /// <summary>How many slots are currently held — i.e. how many panels are shown.</summary>
    internal int Count
    {
        get
        {
            int n = 0;
            for (int i = 0; i < _used.Count; i++)
            {
                if (_used[i]) n++;
            }

            return n;
        }
    }

    /// <summary>Take the lowest free slot.</summary>
    internal int Acquire()
    {
        for (int i = 0; i < _used.Count; i++)
        {
            if (!_used[i])
            {
                _used[i] = true;
                return i;
            }
        }

        _used.Add(true);
        return _used.Count - 1;
    }

    /// <summary>
    /// How many held slots come before <paramref name="slot"/> — that slot's rank in the shown
    /// set, which is what <see cref="PanelFlow.Offset"/> lays out from. Held slots are usually
    /// 0..n-1, but not always: hiding the middle panel of three leaves a hole, and the panel above
    /// it must then move up a column rather than leave a gap on screen.
    /// </summary>
    internal int RankOf(int slot)
    {
        if (slot < 0) return -1;

        int rank = 0;
        int end = slot < _used.Count ? slot : _used.Count;
        for (int i = 0; i < end; i++)
        {
            if (_used[i]) rank++;
        }

        return rank;
    }

    /// <summary>Hand a slot back. Unknown, negative and already-free slots are ignored.</summary>
    internal void Release(int slot)
    {
        if (slot < 0 || slot >= _used.Count) return;
        _used[slot] = false;

        // Keep the tail short so a session that opens and closes many panels does not grow the
        // list forever. Only trailing frees go: an interior free slot is the next Acquire's.
        int last = _used.Count - 1;
        while (last >= 0 && !_used[last])
        {
            _used.RemoveAt(last);
            last--;
        }
    }

    /// <summary>Forget every slot (Core Uninstall, a test).</summary>
    internal void Reset() => _used.Clear();
}

/// <summary>
/// The process-wide allocator the panel handles share, in the shape <see cref="UiHoverGate"/>
/// established for <see cref="HoverRouter"/>: the decision type is a plain instance with tests,
/// and this is the one static that owns an instance of it and the lock around it.
///
/// <para>
/// Dropped with the Core generation like every other piece of <see cref="Ui"/> state, so a
/// <c>.corereload</c> starts from a centered first panel again rather than inheriting slots held by
/// panels that no longer exist.
/// </para>
/// </summary>
internal static class UiPanelSlots
{
    private static readonly object Gate = new();
    private static readonly CascadeSlots Slots = new();

    /// <summary>The slot a panel that is not shown holds: none.</summary>
    internal const int NoSlot = -1;

    /// <summary>How many panels currently hold a slot.</summary>
    internal static int Count
    {
        get { lock (Gate) return Slots.Count; }
    }

    internal static int Acquire()
    {
        lock (Gate) return Slots.Acquire();
    }

    /// <summary>This slot's rank in the shown set; see <see cref="CascadeSlots.RankOf"/>.</summary>
    internal static int RankOf(int slot)
    {
        lock (Gate) return Slots.RankOf(slot);
    }

    internal static void Release(int slot)
    {
        if (slot < 0) return;
        lock (Gate) Slots.Release(slot);
    }

    internal static void Reset()
    {
        lock (Gate) Slots.Reset();
    }

    /// <summary>One grep line for the pin diagnostic.</summary>
    internal static string Describe() => "panelSlots=" + Count;
}
