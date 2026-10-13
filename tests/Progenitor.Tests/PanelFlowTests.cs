using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Job FUI21: where the shown panels go, now that they are laid out next to each other rather
/// than cascaded on top of each other.
///
/// <para>
/// <b>What FUI20 left.</b> FUI20 fixed "every panel is drawn dead center" with a 40-unit cascade,
/// and the live log said it worked — <c>slot=0 at=(0,0)</c>, <c>slot=1 at=(40,-40)</c>. The
/// screenshot said otherwise: the frame is 640 wide, so a 40-unit step leaves two panels
/// overlapping by more than 90%, titles on titles. And these panels cannot be dragged, so the
/// player has no way to resolve an overlap the framework hands them. The bar this file holds the
/// arithmetic to is therefore not "the second panel moved" but "both panels can be read".
/// </para>
///
/// <para>
/// The arithmetic and the slot bookkeeping are game-free for the same reason
/// <see cref="PanelLayout"/> is: "do these two panels overlap" and "is that one off the screen"
/// are questions about numbers, and the only thing the game contributes is how big the canvas is.
/// </para>
/// </summary>
public class PanelFlowTests
{
    // The shipped frame, and the client the defect was screenshotted on. Its overlay copies the
    // game's CanvasScaler (evidence 165), so the canvas is ~1638x1080 design units once the
    // scaler has settled and ~1602x1024 raw pixels in the frame before it does. Both are checked:
    // the panels have to be readable in either, because a panel opened on the first frame after a
    // Core reload is laid out in the second one.
    private const float PanelW = 640f;
    private const float ClientCanvasW = 1638f;
    private const float ClientCanvasH = 1080f;
    private const float SamplePanelH = 280f;
    private const float SampleLogPanelH = 328f;

    private static PanelOffset At(int rank, int count, float canvasW, float canvasH) =>
        PanelFlow.Offset(new PanelSpot(rank, count), PanelW, canvasW, canvasH);

    /// <summary>
    /// How far down a panel on this canvas may be stepped before its bottom edge would leave the
    /// margin, worked from <see cref="PanelLayout"/>'s own cap rather than read back out of
    /// <see cref="PanelFlow"/>: 92 units on the shipped 1080-unit canvas. A test that asked
    /// <c>PanelFlow</c> for this number would agree with the code by construction and could not
    /// disagree with it, which is how R45 finding 4 survived a green suite.
    /// </summary>
    private static float RoomOn(float canvasH) =>
        ((canvasH - PanelLayout.PanelHeightFor(float.MaxValue, canvasH)) * 0.5f) - PanelFlow.EdgeMargin;

    /// <summary>
    /// How many of <paramref name="count"/> panels land in <paramref name="column"/>: that column
    /// holds ranks <c>column</c>, <c>column + columns</c>, <c>column + 2*columns</c>, …
    /// </summary>
    private static int InColumn(int column, int count, int columns) =>
        ((count - column - 1) / columns) + 1;

    /// <summary>Left, right, top and bottom edges of a placed frame, in canvas units.</summary>
    private static (float Left, float Right, float Top, float Bottom) Frame(PanelOffset at, float height) =>
        (at.X - (PanelW * 0.5f), at.X + (PanelW * 0.5f), at.Y + (height * 0.5f), at.Y - (height * 0.5f));

    private static bool Overlaps(
        (float Left, float Right, float Top, float Bottom) a,
        (float Left, float Right, float Top, float Bottom) b) =>
        a.Left < b.Right && b.Left < a.Right && a.Bottom < b.Top && b.Bottom < a.Top;

    // ---- the promise a single panel keeps ---------------------------------------------------

    /// <summary>
    /// The only panel on screen is centered, exactly. Every screenshot the framework has produced
    /// shows a centered panel and the settings panel wants one; a layout that moved the lone panel
    /// would be a regression dressed as a fix.
    /// </summary>
    [Fact]
    public void One_panel_is_dead_centre()
    {
        var at = At(0, 1, ClientCanvasW, ClientCanvasH);

        Assert.Equal(0f, at.X);
        Assert.Equal(0f, at.Y);
        Assert.True(at.IsCentre);
    }

    /// <summary>
    /// A panel that holds no spot is centered too, so a Show that could not reach the allocator
    /// degrades to precisely the pre-FUI20 placement rather than to somewhere arbitrary.
    /// </summary>
    [Fact]
    public void A_panel_with_no_spot_is_centred()
    {
        Assert.True(PanelFlow.Offset(PanelSpot.None, PanelW, ClientCanvasW, ClientCanvasH).IsCentre);
        Assert.True(PanelFlow.Offset(new PanelSpot(-4, 2), PanelW, ClientCanvasW, ClientCanvasH).IsCentre);

        // A rank outside its own set describes nothing, so it is centered rather than guessed at.
        Assert.True(PanelFlow.Offset(new PanelSpot(3, 2), PanelW, ClientCanvasW, ClientCanvasH).IsCentre);
    }

    // ---- the defect itself, at the sizes it was screenshotted at ------------------------------

    /// <summary>
    /// The bar the job set: the sample's demo panel (F9, 640x280) and its log panel (Ctrl+F9,
    /// 640x328) open together on the live client must not overlap <b>at all</b> — not "overlap
    /// less than they did". FUI20's cascade put them 40 units apart, which is 600 units of the
    /// 640-wide frame on top of each other.
    /// </summary>
    [Fact]
    public void The_two_sample_panels_do_not_overlap_on_the_live_client()
    {
        foreach ((float canvasW, float canvasH) in new[] { (ClientCanvasW, ClientCanvasH), (1602f, 1024f) })
        {
            var demo = Frame(At(0, 2, canvasW, canvasH), SamplePanelH);
            var log = Frame(At(1, 2, canvasW, canvasH), SampleLogPanelH);

            Assert.False(Overlaps(demo, log), "the two sample panels still overlap on " + canvasW + "x" + canvasH);

            // Side by side, in slot order, with a real gap rather than a shared edge.
            Assert.True(demo.Right < log.Left);
            Assert.True(log.Left - demo.Right >= PanelFlow.ColumnGap - 0.001f);

            // Both still fully on the canvas, margins intact.
            foreach (var f in new[] { demo, log })
            {
                Assert.True(f.Left >= (-canvasW * 0.5f) + PanelFlow.EdgeMargin - 0.001f);
                Assert.True(f.Right <= (canvasW * 0.5f) - PanelFlow.EdgeMargin + 0.001f);
                Assert.True(f.Bottom >= (-canvasH * 0.5f) + PanelFlow.EdgeMargin - 0.001f);
                Assert.True(f.Top <= (canvasH * 0.5f) - PanelFlow.EdgeMargin + 0.001f);
            }
        }
    }

    /// <summary>
    /// Two panels are centered as a pair, which is the thing a slot-indexed cascade could not do:
    /// where the first one goes is a fact about how many are open, so it is the pair that
    /// straddles the middle rather than the first panel keeping the middle to itself.
    /// </summary>
    [Fact]
    public void A_pair_straddles_the_centre_instead_of_one_panel_keeping_it()
    {
        var first = At(0, 2, ClientCanvasW, ClientCanvasH);
        var second = At(1, 2, ClientCanvasW, ClientCanvasH);

        Assert.True(first.X < 0f);
        Assert.True(second.X > 0f);
        Assert.Equal(-first.X, second.X, 3);

        // Side by side, not stepped down: nothing is stacked while a column is free.
        Assert.Equal(0f, first.Y);
        Assert.Equal(0f, second.Y);
    }

    /// <summary>
    /// Three panels on a canvas with room for two columns: the third has nowhere new to go, so it
    /// shares a column and is stepped down by a whole title band — the panel underneath still
    /// shows its own title, which is the only thing that says it is there and which panel it is.
    /// A step smaller than the band is the FUI20 defect at a different magnitude.
    /// </summary>
    [Fact]
    public void The_panel_that_does_not_fit_a_column_of_its_own_clears_the_title_band()
    {
        Assert.Equal(2, PanelFlow.Columns(ClientCanvasW, PanelW));

        var first = At(0, 3, ClientCanvasW, ClientCanvasH);
        var third = At(2, 3, ClientCanvasW, ClientCanvasH);

        // Back in the first column, one full header band down.
        Assert.Equal(first.X, third.X, 3);
        Assert.Equal(-PanelLayout.HeaderHeight, third.Y, 3);
        Assert.True(
            first.Y - third.Y >= PanelLayout.HeaderHeight - 0.001f,
            "the stacked panel covers the title of the one above it");
    }

    /// <summary>
    /// The whole space that matters: for every canvas, panel height and set size, no two shown
    /// panels are drawn unreadably on top of each other and no panel leaves the canvas. Both
    /// halves are asserted together, because a clamp would satisfy the second by piling every
    /// over-far panel into the same corner, which is the original defect.
    ///
    /// <para>
    /// <b>The bound used to be the reason R45 finding 4 was invisible.</b> The loop ran
    /// <c>count &lt;= columns * (capacity + 1)</c> — exactly the counts that fit <i>without</i> the
    /// ladder having to tighten, and never one more. On 1638x1080 the canvas-wide capacity is 1, so
    /// that bound was four, and five — the first count that produced a 100% coincident pair — was
    /// structurally outside the test. The name promised the whole space and the bound excluded the
    /// crowded half of it, which is the half the arithmetic goes wrong in. It now runs to sixteen
    /// panels on every canvas, because with the ladder tightening rather than repeating there is no
    /// count at which the guarantee is allowed to lapse.
    /// </para>
    ///
    /// <para>
    /// Two overlap rules, and which one applies is itself a fact about the canvas rather than a
    /// let-off: while the column's deepest rung still fits a whole title band, the overlap must
    /// clear a whole title band; past that the step is the largest the room allows, and the bar is
    /// that the panels are still in <i>different places</i>. Zero is the value that matters — a
    /// covered panel at the same offset shows nothing at all.
    /// </para>
    /// </summary>
    [Fact]
    public void No_set_of_panels_is_drawn_unreadably_or_off_the_canvas()
    {
        float[] canvases = { 800f, 1280f, 1602f, 1638f, 1920f, 2560f, 3840f };
        float[] heights = { PanelLayout.MinPanelHeight, 280f, 328f, 700f, 864f };

        foreach (float canvasW in canvases)
        {
            foreach (float canvasH in new[] { canvasW * 9f / 16f, canvasW * 0.75f })
            {
                foreach (float panelH in heights)
                {
                    if (panelH >= canvasH) continue;

                    // Job FUI23: only heights this canvas can actually produce. The vertical ladder
                    // is sized for the tallest panel PanelHeightFor permits here (R44 finding 5),
                    // so a hand-made panel taller than that cap is outside the guarantee — and
                    // outside what any layout pass can hand the placement, because the cap and the
                    // canvas height come from the same read.
                    if (panelH > PanelLayout.PanelHeightFor(float.MaxValue, canvasH)) continue;

                    int columns = PanelFlow.Columns(canvasW, PanelW);
                    float room = RoomOn(canvasH);

                    for (int count = 1; count <= 16; count++)
                    {
                        var frames = new List<(float Left, float Right, float Top, float Bottom)>();
                        for (int rank = 0; rank < count; rank++)
                        {
                            var at = At(rank, count, canvasW, canvasH);
                            var frame = Frame(at, panelH);

                            int column = rank % columns;
                            int deepest = InColumn(column, count, columns) - 1;
                            bool wholeBands = deepest * PanelFlow.CascadeStep <= room + 0.001f;
                            float step = deepest <= 0 || room <= 0f
                                ? 0f
                                : (wholeBands ? PanelFlow.CascadeStep : room / deepest);

                            // Inside the canvas, margins intact — except on an axis where the
                            // frame simply does not fit, which has its own test below.
                            if (PanelW + (2f * PanelFlow.EdgeMargin) <= canvasW)
                            {
                                Assert.True(
                                    frame.Left >= (-canvasW * 0.5f) + PanelFlow.EdgeMargin - 0.001f
                                    && frame.Right <= (canvasW * 0.5f) - PanelFlow.EdgeMargin + 0.001f,
                                    "rank " + rank + " of " + count + " left the canvas sideways on " + canvasW);
                            }

                            if (panelH + (2f * PanelFlow.EdgeMargin) <= canvasH)
                            {
                                Assert.True(
                                    frame.Bottom >= (-canvasH * 0.5f) + PanelFlow.EdgeMargin - 0.001f,
                                    "rank " + rank + " of " + count + " was pushed off the bottom of "
                                    + canvasW + "x" + canvasH);
                                Assert.True(
                                    frame.Top <= (canvasH * 0.5f) - PanelFlow.EdgeMargin + 0.001f,
                                    "rank " + rank + " of " + count + " was pushed off the top of "
                                    + canvasW + "x" + canvasH);
                            }

                            for (int other = 0; other < frames.Count; other++)
                            {
                                var b = frames[other];
                                if (!Overlaps(frame, b)) continue;

                                float dy = frame.Top < b.Top ? b.Top - frame.Top : frame.Top - b.Top;

                                // A canvas with literally no room below a full-height frame is the
                                // one case where two panels legitimately share a place: there is
                                // nowhere to put the second one that does not push a footer off
                                // the screen. Every canvas in this sweep has room.
                                if (room > 0f)
                                {
                                    Assert.True(
                                        dy > 0.0001f,
                                        "ranks " + other + " and " + rank + " of " + count
                                        + " are drawn in the same place on " + canvasW + "x" + canvasH);
                                }

                                if (wholeBands && step > 0f)
                                {
                                    Assert.True(
                                        dy >= PanelLayout.HeaderHeight - 0.001f,
                                        "ranks " + other + " and " + rank + " of " + count
                                        + " overlap without clearing a title band on "
                                        + canvasW + "x" + canvasH);
                                }
                                else
                                {
                                    Assert.True(
                                        dy >= step - 0.001f,
                                        "ranks " + other + " and " + rank + " of " + count
                                        + " are closer than the tightened step " + step + " on "
                                        + canvasW + "x" + canvasH);
                                }
                            }

                            frames.Add(frame);
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// R45 finding 4 at the numbers it was found at, and the one test this job existed to write:
    /// <b>five panels on the shipped client canvas are drawn in five places.</b>
    ///
    /// <para>
    /// FUI23 replaced the per-panel ladder with a canvas-wide one and kept FUI21's
    /// <c>depth mod (capacity + 1)</c> wrap. On 1638x1080 that capacity is <b>one</b> — an 864 cap,
    /// 92 units of room, one whole 72-unit band — so the wrap period was two, and with two columns
    /// rank 4 is column 0 depth 2: <c>2 mod 2 == 0</c>, the offset of rank 0, same column, same y.
    /// Two 640-wide frames dead on top of each other, which is a worse outcome than the 90% overlap
    /// FUI21 was written to fix, because a covered panel at the same offset shows nothing at all.
    /// Four separate texts said this could only happen on a canvas too narrow for two columns; this
    /// canvas has two columns and is the one every screenshot was taken on.
    /// </para>
    /// </summary>
    [Fact]
    public void Five_panels_on_the_shipped_client_canvas_are_drawn_in_five_places()
    {
        Assert.Equal(2, PanelFlow.Columns(ClientCanvasW, PanelW));
        Assert.Equal(1, PanelFlow.DepthCapacity(ClientCanvasH));

        var places = new List<PanelOffset>();
        for (int rank = 0; rank < 5; rank++)
            places.Add(At(rank, 5, ClientCanvasW, ClientCanvasH));

        for (int a = 0; a < places.Count; a++)
        {
            for (int b = a + 1; b < places.Count; b++)
            {
                Assert.False(
                    places[a].X == places[b].X && places[a].Y == places[b].Y,
                    "ranks " + a + " and " + b + " of 5 are drawn in exactly the same place");
            }
        }

        // Column 0 holds ranks 0, 2 and 4 in 92 units of room, so its ladder tightens from a whole
        // 72-unit band to 46 and the deepest panel lands on the margin rather than back on the
        // first. Column 1 holds two panels, fits a whole band, and is untouched by the tightening.
        float room = RoomOn(ClientCanvasH);
        Assert.Equal(92f, room, 3);

        Assert.Equal(places[0].X, places[2].X, 3);
        Assert.Equal(places[0].X, places[4].X, 3);
        Assert.Equal(0f, places[0].Y, 3);
        Assert.Equal(-room * 0.5f, places[2].Y, 3);
        Assert.Equal(-room, places[4].Y, 3);

        Assert.Equal(places[1].X, places[3].X, 3);
        Assert.Equal(0f, places[1].Y, 3);
        Assert.Equal(-PanelLayout.HeaderHeight, places[3].Y, 3);
    }

    /// <summary>
    /// R46 finding 2, as arithmetic rather than as prose: <b>opening the fifth panel moves the
    /// third one 26 units up and moves nothing sideways at all.</b>
    ///
    /// <para>
    /// FUI25 closed the 100% overlap by making the cascade step a function of the column's
    /// occupancy, and justified the churn that creates by saying an open "already re-flows the
    /// whole set sideways by design". It does not, past two panels. <c>occupied</c> is
    /// <c>min(count, columns)</c> and this canvas has two columns, so every panel's x is settled
    /// once two are open; the fifth open takes column 0 from two panels to three, tightens its step
    /// from a whole 72-unit title band to 46, and rank 2 rises on its own while column 1 keeps its
    /// whole band. The churn is real and is kept deliberately (the argument is on
    /// <see cref="PanelFlow"/>); what this test exists for is that it is written down at the
    /// coordinates it happens at rather than described as something it is not.
    /// </para>
    ///
    /// <para>
    /// The horizontal re-flow the old sentence appealed to is asserted here too, at the one count
    /// where it happens: one panel becoming two moves rank 0 by 332 units. That is the number the
    /// 26 is being weighed against, so it is measured rather than remembered.
    /// </para>
    /// </summary>
    [Fact]
    public void Opening_the_fifth_panel_re_spaces_column_zero_and_moves_nothing_sideways()
    {
        Assert.Equal(2, PanelFlow.Columns(ClientCanvasW, PanelW));
        float room = RoomOn(ClientCanvasH);
        Assert.Equal(92f, room, 3);

        var before = new PanelOffset[4];
        for (int rank = 0; rank < 4; rank++) before[rank] = At(rank, 4, ClientCanvasW, ClientCanvasH);

        var after = new PanelOffset[5];
        for (int rank = 0; rank < 5; rank++) after[rank] = At(rank, 5, ClientCanvasW, ClientCanvasH);

        // Nothing moves sideways. This is the half the justifying sentence claimed, and it is the
        // half that is false: there is no re-flow for the vertical churn to ride along with.
        for (int rank = 0; rank < 4; rank++)
            Assert.Equal(before[rank].X, after[rank].X, 3);

        // Column 0: ranks 0 and 2 at four panels, ranks 0, 2 and 4 at five. Two panels fit a whole
        // title band in 92 units of room; three do not, so the step becomes room/2.
        Assert.Equal(0f, before[0].Y, 3);
        Assert.Equal(0f, after[0].Y, 3);
        Assert.Equal(-PanelLayout.HeaderHeight, before[2].Y, 3);
        Assert.Equal(-room * 0.5f, after[2].Y, 3);
        Assert.Equal(26f, after[2].Y - before[2].Y, 3);

        // More than half a row pitch, which is why this is a cost and not a rounding difference.
        Assert.True(
            (after[2].Y - before[2].Y) > (PanelLayout.MinRowHeight + PanelLayout.RowGap) * 0.5f,
            "the churn was assumed to be smaller than half a row pitch");

        // Column 1 holds two panels at both counts and keeps its whole band: in this state the two
        // columns genuinely cascade at different rates, which none of the prose used to say.
        Assert.Equal(before[1].Y, after[1].Y, 3);
        Assert.Equal(before[3].Y, after[3].Y, 3);
        Assert.Equal(-PanelLayout.HeaderHeight, after[3].Y, 3);

        // Rank 2 is the whole of what moved.
        int moved = 0;
        for (int rank = 0; rank < 4; rank++)
        {
            if (before[rank].X != after[rank].X || before[rank].Y != after[rank].Y) moved++;
        }

        Assert.Equal(1, moved);

        // And the move the design already accepts, for scale: one panel becoming two takes rank 0
        // off the center line by a full column pitch.
        var alone = At(0, 1, ClientCanvasW, ClientCanvasH);
        var paired = At(0, 2, ClientCanvasW, ClientCanvasH);
        Assert.Equal(0f, alone.X, 3);
        Assert.Equal(-(PanelW + PanelFlow.ColumnGap) * 0.5f, paired.X, 3);
        Assert.Equal(332f, alone.X - paired.X, 3);
    }

    /// <summary>
    /// The general shape of the same fact, so the coordinates above are an instance of a rule
    /// rather than a number that happens to hold at one count (R46 finding 2).
    ///
    /// <para>
    /// A panel opening appends the highest rank, so it lands in column <c>count % columns</c> and
    /// that is the <b>only</b> column whose occupancy changes: the ranks in every other column keep
    /// both coordinates exactly. Past two panels no x changes at all, because <c>occupied</c> has
    /// saturated. And the churn is not spread evenly over the counts — while whole title bands
    /// still fit, <see cref="PanelFlow.CascadeStepFor"/> returns the constant and adding a panel to
    /// a column moves nothing. On the shipped canvas the first count at which any panel moves is
    /// <b>five</b>, which is the exact count at which the ladder FUI25 replaced drew rank 4 on top
    /// of rank 0. The price of never overlapping is that the rungs re-space, and this says where
    /// the price starts being paid.
    /// </para>
    /// </summary>
    [Fact]
    public void The_only_panels_a_new_panel_moves_are_the_ones_sharing_its_column()
    {
        int columns = PanelFlow.Columns(ClientCanvasW, PanelW);
        Assert.Equal(2, columns);

        int firstCountThatMovesAnything = 0;

        for (int count = 2; count <= 16; count++)
        {
            int newColumn = count % columns;

            for (int rank = 0; rank < count; rank++)
            {
                var before = At(rank, count, ClientCanvasW, ClientCanvasH);
                var after = At(rank, count + 1, ClientCanvasW, ClientCanvasH);

                // occupied = min(count, columns) has saturated by two panels, so the horizontal
                // layout is settled and an open cannot move anything sideways again.
                Assert.Equal(before.X, after.X, 3);

                if (before.Y == after.Y) continue;

                Assert.True(
                    rank % columns == newColumn,
                    "opening panel " + (count + 1) + " moved rank " + rank + " in column "
                    + (rank % columns) + ", which the new panel did not land in");

                if (firstCountThatMovesAnything == 0) firstCountThatMovesAnything = count + 1;
            }
        }

        // A positive control: a layout that never re-spaces anything would satisfy every assertion
        // above, and would be the fixed-step ladder R45 finding 4 found drawing two panels in one
        // place. Five is where the shipped ladder starts tightening, and it is the count the
        // collision happened at.
        Assert.Equal(5, firstCountThatMovesAnything);
        Assert.Equal(1, PanelFlow.DepthCapacity(ClientCanvasH));
    }

    // ---- how many columns there are -----------------------------------------------------------

    /// <summary>
    /// The column count is the whole horizontal model, so it is pinned at the sizes that matter
    /// rather than only asserted to be positive. A 640-wide frame needs 1336 units for two columns
    /// and 2000 for three; the live client has 1602-1638 and therefore has exactly two.
    /// </summary>
    [Fact]
    public void The_canvas_decides_how_many_columns_there_are()
    {
        Assert.Equal(1, PanelFlow.Columns(1000f, PanelW));
        Assert.Equal(1, PanelFlow.Columns(1335f, PanelW));
        Assert.Equal(2, PanelFlow.Columns(1336f, PanelW));
        Assert.Equal(2, PanelFlow.Columns(1602f, PanelW));
        Assert.Equal(2, PanelFlow.Columns(ClientCanvasW, PanelW));
        Assert.Equal(2, PanelFlow.Columns(1920f, PanelW));
        Assert.Equal(2, PanelFlow.Columns(1999f, PanelW));
        Assert.Equal(3, PanelFlow.Columns(2000f, PanelW));
        Assert.Equal(5, PanelFlow.Columns(3360f, PanelW));
    }

    /// <summary>
    /// A column count is never zero and never one the canvas cannot hold: the group of occupied
    /// columns fits inside the canvas minus a margin at each end, which is what makes the
    /// horizontal clamp unnecessary rather than merely absent.
    /// </summary>
    [Fact]
    public void A_full_row_of_columns_always_fits_the_canvas()
    {
        for (float canvasW = 400f; canvasW <= 4000f; canvasW += 7f)
        {
            int columns = PanelFlow.Columns(canvasW, PanelW);
            Assert.True(columns >= 1);

            float group = (columns * PanelW) + ((columns - 1) * PanelFlow.ColumnGap);
            if (columns > 1)
            {
                Assert.True(
                    group <= canvasW - (2f * PanelFlow.EdgeMargin) + 0.001f,
                    columns + " columns do not fit a canvas " + canvasW + " wide");
            }
        }
    }

    // ---- the overflow cascade ------------------------------------------------------------------

    /// <summary>
    /// A canvas too narrow for two columns puts every panel in one and steps them down a whole
    /// title band each, in order, which is the readable version of what FUI20 shipped — for as many
    /// panels as the canvas has whole bands for.
    ///
    /// <para>
    /// <b>The ladder got shallower in job FUI23, and the cost was mis-stated in four places.</b> It
    /// used to be measured from the panel's own height, which made the offset move when a panel grew
    /// under the player's click; it is now measured from the tallest panel the canvas permits, so it
    /// is the same ladder for every panel on that canvas. On a 1080-unit canvas that is one whole
    /// band of room — 92 units — rather than the five rungs a 280-tall panel could personally have
    /// afforded. This test, <c>PanelFlow.DepthCapacity</c>, <c>docs/modules.md</c> and
    /// <c>fe601a6</c>'s commit message all said the cost "shows only on a canvas too narrow for two
    /// columns, i.e. the most crowded arrangement this layout can produce". That is false, and it is
    /// why R45 finding 4 was invisible: <b>the shipped 1638-wide canvas has two columns, and three
    /// panels share column 0 as soon as five are open.</b> What the arithmetic actually says is that
    /// the ladder has to tighten as soon as a column holds more panels than it has whole bands for,
    /// which on this canvas is the third panel in any column — five panels open, not a narrow canvas
    /// at all.
    /// </para>
    /// </summary>
    [Fact]
    public void A_narrow_canvas_stacks_by_a_whole_title_band()
    {
        const float narrow = 1000f;
        Assert.Equal(1, PanelFlow.Columns(narrow, PanelW));

        int capacity = PanelFlow.DepthCapacity(ClientCanvasH);
        Assert.True(capacity >= 1, "a full-height canvas has to afford at least one step");

        float previous = 1f;
        for (int rank = 0; rank <= capacity; rank++)
        {
            var at = At(rank, capacity + 1, narrow, ClientCanvasH);
            Assert.Equal(0f, at.X);
            Assert.Equal(-rank * PanelLayout.HeaderHeight, at.Y, 3);
            Assert.True(at.Y < previous, "rank " + rank + " did not step past rank " + (rank - 1));
            previous = at.Y;
        }
    }

    /// <summary>
    /// A canvas with <b>no</b> room under a full-height frame does not step anything down: a panel
    /// is capped at 80% of the canvas (or <see cref="PanelLayout.MinPanelHeight"/>, whichever is
    /// larger), and on a canvas short enough that the leftover half is inside the edge margin there
    /// is nowhere to put a second panel that does not push a footer off the screen — the one
    /// failure mode worse than an overlap, because the player cannot scroll a panel they cannot
    /// see.
    ///
    /// <para>
    /// Job FUI23 (R44 finding 5): the room is measured from the tallest panel the canvas permits,
    /// not from the panel being placed, so this is a fact about the canvas. 232 units is exactly
    /// the boundary — the cap is <c>MinPanelHeight</c> 200, the leftover half is 16, and the edge
    /// margin takes all of it.
    /// </para>
    ///
    /// <para>
    /// Job FUI25 (R45 finding 4) narrowed this. It used to be asserted of an 800-unit canvas, on
    /// the reasoning that a canvas with no room for a whole <see cref="PanelFlow.CascadeStep"/> has
    /// no room at all. Those are different quantities: 800 units leaves 64, which is not a title
    /// band and is a perfectly good place to put the second panel — it lands exactly on the margin.
    /// Refusing to step there bought nothing and cost the player the whole of the second panel.
    /// </para>
    /// </summary>
    [Fact]
    public void A_canvas_with_no_vertical_room_does_not_step_anything_down()
    {
        Assert.Equal(0, PanelFlow.DepthCapacity(232f));
        Assert.Equal(0f, PanelFlow.CascadeRoom(232f), 3);
        Assert.Equal(0f, At(1, 2, 1000f, 232f).Y);

        // And a canvas with some room, but less than a whole band, uses what it has rather than
        // dropping the second panel onto the first.
        Assert.Equal(0, PanelFlow.DepthCapacity(800f));
        Assert.Equal(64f, PanelFlow.CascadeRoom(800f), 3);
        Assert.Equal(-64f, At(1, 2, 1000f, 800f).Y, 3);
    }

    /// <summary>
    /// The ladder tightens rather than repeating, so the deepest panel in a column lands on the
    /// margin instead of back on top of the first one.
    ///
    /// <para>
    /// <b>This test used to assert the opposite</b> (R45 finding 4). It pinned
    /// <c>At(capacity + 1, …).Y == 0</c> — the wrap — under a comment arguing that a bounded repeat
    /// beats a step too small to read. That argument is right about a <i>fixed</i> step chosen once
    /// for every canvas and wrong about this one, because a repeat's collision is not a small step,
    /// it is a step of <b>zero</b>: the covered panel shows nothing, and the player cannot tell it
    /// is open, which panel it is, or that anything went wrong. A 23-unit sliver of a title band is
    /// a worse panel than a 72-unit one and a far better one than a panel that is not on screen.
    /// </para>
    /// </summary>
    [Fact]
    public void The_deepest_panel_in_a_column_lands_on_the_margin_not_back_on_the_first()
    {
        const float narrow = 1000f;
        Assert.Equal(1, PanelFlow.Columns(narrow, PanelW));

        float room = RoomOn(ClientCanvasH);
        Assert.Equal(92f, room, 3);

        for (int count = 2; count <= 8; count++)
        {
            float step = (count - 1) * PanelFlow.CascadeStep <= room
                ? PanelFlow.CascadeStep
                : room / (count - 1);

            float previous = float.MaxValue;
            for (int rank = 0; rank < count; rank++)
            {
                var at = At(rank, count, narrow, ClientCanvasH);
                Assert.Equal(0f, at.X);
                Assert.Equal(-rank * step, at.Y, 3);
                Assert.True(
                    at.Y < previous - 0.0001f,
                    "rank " + rank + " of " + count + " did not step past rank " + (rank - 1));
                previous = at.Y;
            }

            // The deepest one is at least a whole title band down whatever the count, on the
            // margin when the ladder had to tighten, and never past it.
            var deepest = At(count - 1, count, narrow, ClientCanvasH);
            Assert.True(deepest.Y <= -PanelFlow.CascadeStep + 0.001f);
            Assert.True(deepest.Y >= -room - 0.001f);
            float bottom = deepest.Y - (SamplePanelH * 0.5f);
            Assert.True(bottom >= (-ClientCanvasH * 0.5f) + PanelFlow.EdgeMargin - 0.001f);
        }
    }

    // ---- the panel does not move under the player -----------------------------------------------

    /// <summary>
    /// The guarantee that replaces FUI20's "a panel never moves while shown": a re-layout caused
    /// by <i>using</i> a panel never moves anything. <c>UnityPanel.Relayout</c> runs when a row's
    /// text changes under the player's own click (the rebind flow, R43 finding 4) and recomputes
    /// the offset from the same spot — so the answer has to be identical.
    ///
    /// <para>
    /// <b>Calling the same pure function twice does not test that</b> (R45's Tests section). The
    /// predecessor did exactly that and asserted the two results were equal, which is a statement
    /// about arithmetic and not about <see cref="PanelFlow"/>. What makes the guarantee true is
    /// that the answer is a function of its arguments and of <b>nothing else</b> — no cache, no
    /// last-canvas memo, no "what did this panel measure last time" — so the property with teeth is
    /// structural: <c>PanelFlow</c> carries no mutable state for an answer to be remembered in.
    /// That is asserted here over every static field on the type, in the idiom
    /// <c>fe601a6</c> established, alongside a re-layout driven through a real interleaving so a
    /// memo keyed on the previous call would be exercised if one were ever added.
    /// </para>
    /// </summary>
    [Fact]
    public void Re_laying_out_the_same_set_puts_every_panel_back_where_it_was()
    {
        var mutable = typeof(PanelFlow)
            .GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(f => !f.IsLiteral && !f.IsInitOnly)
            .Select(f => f.Name)
            .ToArray();
        Assert.True(
            mutable.Length == 0,
            "PanelFlow grew mutable static state, so a re-layout can no longer be a pure function "
            + "of the spot and the canvas: " + string.Join(", ", mutable));

        // Interleaved on purpose: every rank of every set is laid out once, then the whole sweep
        // is run again in the opposite order and every answer has to match the first pass.
        var first = new Dictionary<(int Rank, int Count), PanelOffset>();
        for (int count = 1; count <= 8; count++)
        {
            for (int rank = 0; rank < count; rank++)
                first[(rank, count)] = At(rank, count, ClientCanvasW, ClientCanvasH);
        }

        for (int count = 8; count >= 1; count--)
        {
            for (int rank = count - 1; rank >= 0; rank--)
            {
                var again = At(rank, count, ClientCanvasW, ClientCanvasH);
                var before = first[(rank, count)];

                Assert.Equal(before.X, again.X);
                Assert.Equal(before.Y, again.Y);
            }
        }
    }

    /// <summary>
    /// R44 finding 5, as the property that was claimed and was not true: a panel that <b>grows</b>
    /// under the player's own click does not move — on either axis.
    ///
    /// <para>
    /// The guarantee on <see cref="PanelFlow"/> was derived from rank and count while
    /// <c>Offset</c> took a third input the re-layout pass is allowed to change: the panel's own
    /// height. <c>DepthCapacity</c> fell as the panel grew and the offset is
    /// <c>depth mod (capacity + 1)</c>, so a panel at depth 1 sat at <c>-72</c> while it had room
    /// and snapped to <c>0</c> the moment it did not — onto the title band of the panel above it.
    /// The reachable case is the rebind flow (R43 finding 4): the refusal notice appends a
    /// sentence, the row grows, the panel re-lays out taller, and it jumps under the click that is
    /// still happening. On an 800-unit canvas the threshold sits at 624, between the two heights
    /// below.
    /// </para>
    ///
    /// <para>
    /// The fix is that the height is not an input at all, so this is checked the only way a
    /// removed parameter can be: against the signature. <c>Offset</c> takes a spot, a frame width
    /// and a canvas, and <c>CascadeStepFor</c> takes a canvas and a column occupancy. A future edit
    /// that threads the panel's height back in fails here rather than in a screenshot.
    /// </para>
    ///
    /// <para>
    /// <b>And the experiment is now performed</b> (R45's Tests section). The comment used to
    /// describe two layouts at two different heights beside two calls with byte-identical arguments
    /// and no height anywhere: a claim about a measurement that was not taken, which is the disease
    /// this whole batch is about, inside the test written to close it. The removed rule is
    /// reproduced here as a local function so the two heights really are put through it and really
    /// do come out apart, and the shipped call really does answer once for both.
    /// </para>
    /// </summary>
    [Fact]
    public void A_panel_that_grows_under_the_player_does_not_move()
    {
        var offset = typeof(PanelFlow).GetMethod(
            "Offset", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.NotNull(offset);
        Assert.Equal(
            new[] { "spot", "panelWidth", "canvasWidth", "canvasHeight" },
            offset!.GetParameters().Select(p => p.Name).ToArray());

        var capacity = typeof(PanelFlow).GetMethod(
            "DepthCapacity", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.NotNull(capacity);
        Assert.Equal(
            new[] { "canvasHeight" },
            capacity!.GetParameters().Select(p => p.Name).ToArray());

        var stepFor = typeof(PanelFlow).GetMethod(
            "CascadeStepFor", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.NotNull(stepFor);
        Assert.Equal(
            new[] { "canvasHeight", "deepest" },
            stepFor!.GetParameters().Select(p => p.Name).ToArray());

        // The rule that was removed, restated: the room under the panel BEING PLACED, the number of
        // whole steps that fit in it, and the wrap. This is the arithmetic R44 finding 5 named.
        static float RemovedRuleY(int depth, float panelH, float canvasH)
        {
            float room = ((canvasH - panelH) * 0.5f) - PanelFlow.EdgeMargin;
            int rungs = room < PanelFlow.CascadeStep ? 0 : (int)(room / PanelFlow.CascadeStep);
            return rungs == 0 ? 0f : -(depth % (rungs + 1)) * PanelFlow.CascadeStep;
        }

        // Rank 2 of 3 is column 0, depth 1 — the one that used to move. The rebind flow appends a
        // refusal sentence, the row grows, and the panel re-lays out from 600 units to 640: on an
        // 800-unit canvas the removed rule's threshold sat at 624, between the two, so it really
        // did jump a whole title band under the click the player was still making.
        Assert.Equal(600f, PanelLayout.PanelHeightFor(600f - PanelLayout.HeaderHeight, 800f), 3);
        Assert.Equal(640f, PanelLayout.PanelHeightFor(640f - PanelLayout.HeaderHeight, 800f), 3);
        Assert.Equal(-PanelFlow.CascadeStep, RemovedRuleY(1, 600f, 800f), 3);
        Assert.Equal(0f, RemovedRuleY(1, 640f, 800f), 3);

        // The shipped call cannot tell those two apart, because it cannot see either number.
        var atShortRow = At(2, 3, ClientCanvasW, 800f);
        var atGrownRow = At(2, 3, ClientCanvasW, 800f);
        Assert.Equal(atShortRow.X, atGrownRow.X);
        Assert.Equal(atShortRow.Y, atGrownRow.Y);
    }

    /// <summary>
    /// The half of finding 5's fix that could have gone wrong the other way: a ladder sized for the
    /// tallest permitted panel must not step a real panel off the bottom of the canvas. Every panel
    /// height <see cref="PanelLayout.PanelHeightFor"/> can produce on a canvas, at every rung of
    /// that canvas's ladder, still has its bottom edge inside the margin.
    ///
    /// <para>
    /// <b>It used to place nothing below the top rung.</b> The canvas was always
    /// <see cref="ClientCanvasW"/>, which has two columns, and the spot was
    /// <c>At(capacity, capacity + 1, …)</c> — rank 1 of 2, i.e. column 1 <i>depth 0</i>, dead on the
    /// center line — for five of the six canvases in the list. A test of the ladder that walked the
    /// ladder in one case out of twenty-four. The width is now narrow enough to force one column, so
    /// rank <b>is</b> depth, and every rung of every set size is checked.
    /// </para>
    /// </summary>
    [Fact]
    public void The_canvas_wide_ladder_never_steps_a_real_panel_off_the_canvas()
    {
        const float narrow = 1000f;
        Assert.Equal(1, PanelFlow.Columns(narrow, PanelW));

        float[] canvases = { 800f, 900f, 1024f, 1080f, 1440f, 2160f };
        float[] contents = { 0f, 120f, 600f, 4000f };

        foreach (float canvasH in canvases)
        {
            for (int count = 2; count <= 10; count++)
            {
                for (int rank = 1; rank < count; rank++)
                {
                    var at = At(rank, count, narrow, canvasH);
                    Assert.True(at.Y <= 0f, "rank " + rank + " of " + count + " stepped upward");

                    foreach (float content in contents)
                    {
                        float panelH = PanelLayout.PanelHeightFor(content, canvasH);
                        float bottom = at.Y - (panelH * 0.5f);
                        float top = at.Y + (panelH * 0.5f);

                        Assert.True(
                            bottom >= (-canvasH * 0.5f) + PanelFlow.EdgeMargin - 0.001f,
                            "a " + panelH + "-tall panel at rank " + rank + " of " + count
                            + " left the bottom of a " + canvasH + " canvas");
                        Assert.True(
                            top <= (canvasH * 0.5f) - PanelFlow.EdgeMargin + 0.001f,
                            "a " + panelH + "-tall panel at rank " + rank + " of " + count
                            + " left the top of a " + canvasH + " canvas");
                    }
                }
            }
        }
    }

    // ---- nothing that reaches it from a layout pass can break it ---------------------------------

    /// <summary>
    /// A frame wider than the canvas it is on is centered on that axis, which is the only honest
    /// answer: it is going to overlap whatever else is open wherever it is put, and a clamp would
    /// only decide which edge it hangs off. Panels that cannot share a column fall back to the one
    /// column and cascade, which is the same rule as any other overflow.
    ///
    /// <para>
    /// Job FUI23: this used to also assert a <i>taller</i>-than-the-canvas panel was centered
    /// vertically. That case is gone with finding 5's fix — the offset cannot see a panel's height
    /// — and it was never reachable: <see cref="PanelLayout.PanelHeightFor"/> caps every panel at
    /// <see cref="PanelLayout.ScreenFraction"/> of the same canvas height the placement is given,
    /// so no panel that reaches this arithmetic is taller than its canvas.
    /// </para>
    /// </summary>
    [Fact]
    public void A_panel_wider_than_its_canvas_is_centred_sideways()
    {
        Assert.Equal(1, PanelFlow.Columns(400f, PanelW));
        Assert.Equal(0f, At(0, 2, 400f, 1080f).X);
        Assert.Equal(0f, At(1, 2, 400f, 1080f).X);

        // And a panel is never taller than its canvas by the time it gets here.
        foreach (float canvasH in new[] { 600f, 800f, 1080f, 2160f })
            Assert.True(PanelLayout.PanelHeightFor(float.MaxValue, canvasH) < canvasH);
    }

    /// <summary>
    /// Total on the inputs a Unity layout pass can actually hand it. This runs inside
    /// <c>UnityPanel.RunLayout</c> on the game's main thread, where a throw leaves a panel
    /// half-placed and a NaN offset makes the frame vanish.
    /// </summary>
    [Fact]
    public void Unreadable_sizes_resolve_to_something_finite()
    {
        float[] nasty = { float.NaN, float.PositiveInfinity, float.NegativeInfinity, 0f, -100f };

        foreach (float bad in nasty)
        {
            foreach (int rank in new[] { 0, 1, 5, 40 })
            {
                var spot = new PanelSpot(rank, 41);
                var a = PanelFlow.Offset(spot, bad, ClientCanvasW, ClientCanvasH);
                var c = PanelFlow.Offset(spot, PanelW, bad, ClientCanvasH);
                var d = PanelFlow.Offset(spot, PanelW, ClientCanvasW, bad);

                foreach (var at in new[] { a, c, d })
                {
                    Assert.False(float.IsNaN(at.X) || float.IsInfinity(at.X));
                    Assert.False(float.IsNaN(at.Y) || float.IsInfinity(at.Y));
                }
            }
        }
    }

    /// <summary>An unreadable canvas falls back rather than collapsing every panel onto the center.</summary>
    [Fact]
    public void An_unreadable_canvas_uses_the_fallback_size()
    {
        var real = PanelFlow.Offset(
            new PanelSpot(1, 2), PanelW, PanelFlow.FallbackCanvasWidth, PanelLayout.FallbackScreenHeight);
        var unreadable = PanelFlow.Offset(new PanelSpot(1, 2), PanelW, float.NaN, float.NaN);

        Assert.Equal(real.X, unreadable.X);
        Assert.Equal(real.Y, unreadable.Y);
        Assert.False(unreadable.IsCentre);
    }

    // ---- the slot allocator ------------------------------------------------------------------

    /// <summary>Slots come out lowest-first, which is what keeps the longest-open panel leftmost.</summary>
    [Fact]
    public void Slots_are_handed_out_lowest_first()
    {
        var slots = new CascadeSlots();

        Assert.Equal(0, slots.Acquire());
        Assert.Equal(1, slots.Acquire());
        Assert.Equal(2, slots.Acquire());
        Assert.Equal(3, slots.Count);
    }

    /// <summary>
    /// The rank is what the layout is computed from, and it is a position in the <i>held</i> set,
    /// not the slot id. Hiding the middle panel of three leaves a hole, and the panel after it
    /// must move up a column rather than leave a gap on screen with a panel beyond it.
    /// </summary>
    [Fact]
    public void A_hole_in_the_slots_does_not_leave_a_hole_in_the_layout()
    {
        var slots = new CascadeSlots();
        int a = slots.Acquire();
        int b = slots.Acquire();
        int c = slots.Acquire();

        Assert.Equal(0, slots.RankOf(a));
        Assert.Equal(1, slots.RankOf(b));
        Assert.Equal(2, slots.RankOf(c));

        slots.Release(b);

        Assert.Equal(2, slots.Count);
        Assert.Equal(0, slots.RankOf(a));
        Assert.Equal(1, slots.RankOf(c));
    }

    /// <summary>A rank is never asked for a slot that was never held.</summary>
    [Fact]
    public void The_rank_of_a_slot_nobody_holds_is_not_a_place_in_the_set()
    {
        var slots = new CascadeSlots();
        Assert.Equal(-1, slots.RankOf(-1));
        Assert.Equal(0, slots.RankOf(0));

        slots.Acquire();
        Assert.Equal(1, slots.RankOf(99));
    }

    /// <summary>
    /// The property that makes "centered" mean "the only panel on screen" rather than "the first
    /// panel ever opened": close everything and the next panel is centered again.
    /// </summary>
    [Fact]
    public void The_last_panel_left_open_is_the_whole_set_and_the_next_one_starts_centred()
    {
        var slots = new CascadeSlots();

        int a = slots.Acquire();
        int b = slots.Acquire();
        Assert.Equal(0, a);
        Assert.Equal(1, b);

        slots.Release(a);
        Assert.Equal(1, slots.Count);

        // The survivor is now the whole set, so it is ranked 0 of 1 — the center.
        Assert.Equal(0, slots.RankOf(b));
        Assert.True(At(slots.RankOf(b), slots.Count, ClientCanvasW, ClientCanvasH).IsCentre);

        // The freed slot is what the next panel gets.
        Assert.Equal(0, slots.Acquire());

        slots.Release(0);
        slots.Release(1);
        Assert.Equal(0, slots.Count);
        Assert.Equal(0, slots.Acquire());
    }

    /// <summary>
    /// A leaked slot counts a panel that is not on screen into every later layout, so the
    /// allocator must not grow under an open/close churn.
    /// </summary>
    [Fact]
    public void Opening_and_closing_many_panels_does_not_drift_the_set()
    {
        var slots = new CascadeSlots();

        for (int i = 0; i < 500; i++)
        {
            int slot = slots.Acquire();
            Assert.Equal(0, slot);
            slots.Release(slot);
        }

        Assert.Equal(0, slots.Count);
    }

    /// <summary>
    /// Release is total. It runs from Hide, from Dispose-while-shown and from a Show that threw,
    /// so a double release or an id from a previous Core generation must be ignored rather than
    /// corrupt the set — a freed slot handed out twice puts two panels in the same column.
    /// </summary>
    [Fact]
    public void Releasing_a_slot_that_is_not_held_is_ignored()
    {
        var slots = new CascadeSlots();

        slots.Release(0);
        slots.Release(-1);
        slots.Release(9999);
        Assert.Equal(0, slots.Count);

        int held = slots.Acquire();
        slots.Release(held);
        slots.Release(held);
        Assert.Equal(0, slots.Count);

        // Still hands out a clean set afterwards.
        Assert.Equal(0, slots.Acquire());
        Assert.Equal(1, slots.Acquire());
    }

    /// <summary>An interior hole is filled before the list is extended.</summary>
    [Fact]
    public void A_freed_middle_slot_is_reused_before_a_new_one_is_made()
    {
        var slots = new CascadeSlots();
        slots.Acquire();
        int middle = slots.Acquire();
        slots.Acquire();

        slots.Release(middle);

        Assert.Equal(middle, slots.Acquire());
        Assert.Equal(3, slots.Count);
    }

    /// <summary>Reset is what a Core swap does: no slot survives into the next generation.</summary>
    [Fact]
    public void Reset_forgets_every_slot()
    {
        var slots = new CascadeSlots();
        slots.Acquire();
        slots.Acquire();

        slots.Reset();

        Assert.Equal(0, slots.Count);
        Assert.Equal(0, slots.Acquire());
    }
}
