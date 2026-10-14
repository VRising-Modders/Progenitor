using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// The scrollbar read-back is taken only once ScrollRect has had its update over the verdict's content. The 2026-10-14
/// live passes printed about 160 false "they disagree" lines per pass: a panel being built re-lays itself out once per
/// added row in one frame, before it is shown, and every pass read the bar the previous open had left.
/// </summary>
public sealed class ScrollbarReadBackTests
{
    private const int Verdict = 100;

    [Theory]
    [InlineData(101, true)]  // a later frame with the ScrollRect live: its LateUpdate has run over this content
    [InlineData(250, true)]
    [InlineData(100, false)] // the same frame as the verdict: the bar still holds an older content's answer
    [InlineData(99, false)]
    public void A_read_waits_for_a_frame_after_the_verdict(int now, bool read) =>
        Assert.Equal(
            read ? ReadBackAction.Read : ReadBackAction.Drop,
            ScrollbarVerdict.Decide(pending: true, noScroll: false, scrollLive: true, Verdict, now, endsVerdict: true));

    [Fact]
    public void A_scroll_rect_that_is_not_live_is_never_read() =>
        Assert.Equal(
            ReadBackAction.Drop,
            ScrollbarVerdict.Decide(pending: true, noScroll: false, scrollLive: false, Verdict, Verdict + 5, endsVerdict: true));

    [Fact]
    public void An_unsettled_verdict_survives_a_reposition_but_not_a_relayout_or_hide()
    {
        Assert.Equal(ReadBackAction.Defer,
            ScrollbarVerdict.Decide(pending: true, noScroll: false, scrollLive: true, Verdict, Verdict, endsVerdict: false));
        Assert.Equal(ReadBackAction.Drop,
            ScrollbarVerdict.Decide(pending: true, noScroll: false, scrollLive: true, Verdict, Verdict, endsVerdict: true));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public void Nothing_pending_or_no_scroll_view_means_no_read(bool pending, bool noScroll)
    {
        foreach (bool ends in new[] { true, false })
            Assert.Equal(ReadBackAction.None,
                ScrollbarVerdict.Decide(pending, noScroll, scrollLive: true, Verdict, Verdict + 1, ends));
    }

    [Theory]
    [InlineData(int.MinValue, 101)]
    [InlineData(100, int.MinValue)]
    public void An_unreadable_frame_counter_never_reads(int verdictFrame, int now) =>
        Assert.Equal(ReadBackAction.Drop,
            ScrollbarVerdict.Decide(pending: true, noScroll: false, scrollLive: true, verdictFrame, now, endsVerdict: true));

    /// <summary>The 2026-10-14 Widgets build, replayed: seven passes in one frame, then one settled re-layout.</summary>
    [Fact]
    public void A_build_in_one_frame_reads_back_once_after_it_settles()
    {
        bool pending = false;
        int verdictFrame = int.MinValue, reads = 0;
        void Pass(int frame, bool live)
        {
            var action = ScrollbarVerdict.Decide(pending, false, live, verdictFrame, frame, endsVerdict: true);
            if (action == ReadBackAction.Read) reads++;
            pending = true; // the pass arms a new verdict
            verdictFrame = frame;
        }

        for (int row = 0; row < 7; row++) Pass(500, live: false); // built while hidden
        Pass(500, live: false);                                   // Show lays out before it activates
        Assert.Equal(0, reads);
        Pass(501, live: true);                                     // the settle re-measure on the next frame
        Assert.Equal(1, reads);
    }

    [Fact]
    public void A_repeated_line_is_printed_once()
    {
        string agree = ScrollbarVerdict.Describe("Widgets", true, ScrollbarState.Shown, "a re-layout");
        string disagree = ScrollbarVerdict.Describe("Widgets", false, ScrollbarState.Shown, "a re-layout");
        Assert.True(ScrollbarVerdict.ShouldLog(null, agree));
        Assert.False(ScrollbarVerdict.ShouldLog(agree, agree));
        Assert.True(ScrollbarVerdict.ShouldLog(agree, disagree));
    }

    [Fact]
    public void The_panel_gates_every_read_back_on_the_frame_and_skips_the_strip()
    {
        string source = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "UnityUiRuntime.cs");
        string read = SourcePin.MethodBody(source, "private void ReadBackScrollbar(string when, bool endsVerdict)");
        Assert.Contains("ScrollbarVerdict.Decide(", read);
        Assert.Contains("isActiveAndEnabled", read);
        Assert.Contains("ScrollbarVerdict.ShouldLog(", read);

        SourcePin.AssertStatement(SourcePin.MethodBody(source, "public void Reposition()"),
            "ReadBackScrollbar(\"a reposition\", endsVerdict: false);");
        string layout = SourcePin.MethodBody(source, "private void RunLayout(bool resetScroll, bool firstShow)");
        SourcePin.AssertStatement(layout, "ReadBackScrollbar(\"a re-layout\", endsVerdict: true);");
        SourcePin.AssertStatement(layout, "_verdictPending = _scrollbar != null && !NoScroll && !_strip;");
        SourcePin.AssertStatement(layout, "_verdictFrame = FrameNow();");
    }
}
