using System.Collections.Generic;
using Progenitor.Materials;
using Progenitor.Networking;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// The refinement station recipe tooltip counts (<see cref="MaterialOperation.RefineView"/>): the game's
/// line format and color rule, the decision per line, the restore memo and the view selection. Game-free.
/// </summary>
public class MaterialRefineTooltipTests
{
    private const int Glass = 301;
    private const int Plank = 302;
    private const ulong Station = 0x0000002A00000003;
    private const ulong Heart = 0x0000001100000001;

    [Theory]
    [InlineData("0/3", true, 0, 3)]
    [InlineData("12/3", true, 12, 3)]
    [InlineData("3/0", true, 3, 0)]
    [InlineData("", false, 0, 0)]
    [InlineData("3", false, 0, 0)]
    [InlineData("/3", false, 0, 0)]
    [InlineData("3/", false, 0, 0)]
    [InlineData("1/2/3", false, 0, 0)]
    [InlineData("-1/3", false, 0, 0)]
    [InlineData(" 1/3", false, 0, 0)]
    [InlineData("a/3", false, 0, 0)]
    public void Lines_parse_only_in_the_game_format(string text, bool ok, int have, int need)
    {
        Assert.Equal(ok, MaterialRefineTooltip.TryParse(text, out int h, out int n));
        if (!ok) return;
        Assert.Equal(have, h);
        Assert.Equal(need, n);
        Assert.Equal(text, MaterialRefineTooltip.Format(h, n));
    }

    [Theory]
    [InlineData(0, 3, (int)RefineLineState.Missing)]
    [InlineData(0, 0, (int)RefineLineState.Missing)]
    [InlineData(1, 3, (int)RefineLineState.Partial)]
    [InlineData(2, 3, (int)RefineLineState.Partial)]
    [InlineData(3, 3, (int)RefineLineState.Met)]
    [InlineData(40, 3, (int)RefineLineState.Met)]
    public void The_color_follows_the_games_rule(int have, int need, int state) =>
        Assert.Equal((RefineLineState)state, MaterialRefineTooltip.StateOf(have, need));

    [Fact]
    public void A_governed_line_shows_the_station_count_plus_the_pool()
    {
        var totals = new Dictionary<int, int> { [Glass] = 3 };
        var line = MaterialRefineTooltip.Decide(0, Glass, "0/3", totals);
        Assert.True(line.Write);
        Assert.Equal(0, line.GameHave);
        Assert.Equal(3, line.ProviderHave);
        Assert.Equal(3, line.ShownHave);
        Assert.Equal("3/3", line.Text);
        Assert.Equal(RefineLineState.Met, line.State);

        // The station's own units are kept (refining takes them first), the pool adds to them.
        var partial = MaterialRefineTooltip.Decide(1, Glass, "1/5", totals);
        Assert.Equal("4/5", partial.Text);
        Assert.Equal(RefineLineState.Partial, partial.State);

        // Governed with an empty pool: the game's own count and color, written (it is still governed).
        var empty = MaterialRefineTooltip.Decide(0, Glass, "0/3", new Dictionary<int, int> { [Glass] = 0 });
        Assert.True(empty.Write);
        Assert.Equal("0/3", empty.Text);
        Assert.Equal(RefineLineState.Missing, empty.State);
    }

    [Fact]
    public void An_ungoverned_or_unreadable_line_keeps_the_games_text()
    {
        var totals = new Dictionary<int, int> { [Glass] = 3 };
        var plank = MaterialRefineTooltip.Decide(1, Plank, "2/8", totals);
        Assert.False(plank.Write);
        Assert.Equal(-1, plank.ProviderHave);
        Assert.Equal("2/8", plank.Text);
        Assert.Equal("not governed", plank.Note);

        Assert.False(MaterialRefineTooltip.Decide(0, Glass, "0/3", null).Write);

        var odd = MaterialRefineTooltip.Decide(0, Glass, "3", totals);
        Assert.False(odd.Write);
        Assert.Equal("unreadable line", odd.Note);
    }

    [Fact]
    public void The_shown_count_saturates()
    {
        Assert.Equal(int.MaxValue, MaterialRefineTooltip.Shown(int.MaxValue, 5));
        Assert.Equal(5, MaterialRefineTooltip.Shown(-2, 5));
    }

    [Fact]
    public void Lines_match_only_when_every_requirement_has_a_line()
    {
        Assert.True(MaterialRefineTooltip.LinesMatch(2, 2));
        Assert.False(MaterialRefineTooltip.LinesMatch(1, 2));
        Assert.False(MaterialRefineTooltip.LinesMatch(0, 0));
    }

    [Fact]
    public void Progenitors_text_is_put_back_before_the_game_reads_the_line_again()
    {
        var memo = new RefineRowMemo();
        var totals = new Dictionary<int, int> { [Glass] = 5 };
        var line = MaterialRefineTooltip.Decide(0, Glass, "0/3", totals);
        memo.Wrote(line, "0/3");
        Assert.Equal(1, memo.Count);

        // The game skipped the line: it still holds "5/3", which is ours; the game's "0/3" goes back.
        Assert.True(memo.TryRestore(0, "5/3", out string text, out var state));
        Assert.Equal("0/3", text);
        Assert.Equal(RefineLineState.Missing, state);

        // The game rewrote the line (even to the same number): nothing to put back.
        Assert.False(memo.TryRestore(0, "4/3", out _, out _));
        Assert.False(memo.TryRestore(1, "5/3", out _, out _));

        // A line that was not written is not remembered.
        memo.Clear();
        memo.Wrote(MaterialRefineTooltip.Decide(1, Plank, "2/8", totals), "2/8");
        Assert.Equal(0, memo.Count);
    }

    [Fact]
    public void Repeated_frames_never_add_the_pool_twice()
    {
        // Frame after frame the game writes "0/3"; Progenitor puts it back before the next game pass, so
        // each pass reads the game's 0 and shows 0 + pool, never pool + pool.
        var memo = new RefineRowMemo();
        var totals = new Dictionary<int, int> { [Glass] = 2 };
        string onScreen = "0/3";
        for (int frame = 0; frame < 4; frame++)
        {
            if (memo.TryRestore(0, onScreen, out string game, out _)) onScreen = game; // Before
            memo.Clear();
            // (the game may or may not rewrite "0/3" here; either way the line reads "0/3")
            var line = MaterialRefineTooltip.Decide(0, Glass, onScreen, totals);       // After
            if (line.Write) { memo.Wrote(line, onScreen); onScreen = line.Text; }
            Assert.Equal("2/3", onScreen);
        }
    }

    [Fact]
    public void Only_a_view_for_the_very_station_applies()
    {
        var store = new MaterialOverlayStore();
        Assert.False(MaterialRefineTooltip.TryTotals(store, Station, out _, out _));
        store.Apply(new MaterialView(MaterialOperation.RefineView, 3, 1, 1, 5, false, false,
            new[] { new MaterialLine(Glass, 3) }, Heart, Station));
        Assert.True(MaterialRefineTooltip.TryTotals(store, Station, out var view, out var totals));
        Assert.Equal(Station, view.StationNet);
        Assert.Equal(3, totals[Glass]);
        Assert.False(MaterialRefineTooltip.TryTotals(store, Station + 1, out _, out _));
        Assert.False(MaterialRefineTooltip.TryTotals(store, 0, out _, out _));
    }

    [Fact]
    public void RefineView_is_a_display_only_view_operation()
    {
        Assert.Equal(7, (int)MaterialOperation.RefineView);
        Assert.True(MaterialOperations.IsSupported(MaterialOperation.RefineView));
        Assert.Contains(MaterialOperation.RefineView, MaterialOperations.Supported);
        Assert.True(MaterialOperations.IsDisplayOnly(MaterialOperation.RefineView));
        Assert.False(MaterialOperations.IsDisplayOnly(MaterialOperation.Craft));
        Assert.False(MaterialOperations.RequiresStandingPlot(MaterialOperation.RefineView));
        Assert.Contains(MaterialOperation.RefineView, MaterialClientPolicy.ViewOperations);
        Assert.True(MaterialClientPolicy.NeedsStation(MaterialOperation.RefineView));
    }

    [Fact]
    public void RefineView_views_go_only_to_clients_that_read_them()
    {
        Assert.False(MaterialClientPolicy.ReadByPeer(MaterialOperation.RefineView, peerReadsCharacterCraft: true));
        Assert.True(MaterialClientPolicy.ReadByPeer(MaterialOperation.RefineView, peerReadsCharacterCraft: false, peerReadsRefineView: true));
        Assert.True(MaterialClientPolicy.ReadByPeer(MaterialOperation.Craft, false, false));
        Assert.False(MaterialClientPolicy.ReadByPeer(MaterialOperation.CharacterCraft, false, true));
        Assert.True((Handshake.SelfCapabilities & Handshake.Capability.MaterialRefineView) != 0);
        Assert.Equal(536870912u, (uint)Handshake.Capability.MaterialRefineView);
    }

    [Fact]
    public void A_RefineView_view_round_trips_and_never_replaces_a_merged_count()
    {
        var view = new MaterialView(MaterialOperation.RefineView, 3, 9, 4, 5, false, false,
            new[] { new MaterialLine(Glass, 3) }, Heart, Station, 77);
        Assert.True(MaterialViewWire.TryDecode(MaterialViewWire.Encode(view), out var back, out string why), why);
        Assert.Equal(MaterialOperation.RefineView, back!.Operation);
        Assert.Equal(Station, back.StationNet);

        // Operation 8 is still malformed.
        var bytes = MaterialViewWire.Encode(view);
        bytes[1] = 8;
        Assert.False(MaterialViewWire.TryDecode(bytes, out _, out _));

        // Held, it applies to no count the game makes (build, craft, upgrade, character craft).
        var store = new MaterialOverlayStore();
        store.Apply(view);
        foreach (var call in new[]
                 {
                     MaterialClientPolicy.Call(true, Heart, true, 0),
                     MaterialClientPolicy.Call(true, Heart, false, Station),
                     MaterialClientPolicy.Call(true, Heart, false, 0, Station),
                     MaterialClientPolicy.Call(true, Heart, false, 0, 0, characterCraft: true),
                 })
            Assert.False(MaterialClientPolicy.TrySelect(store, call, out _, out _));
    }
}
