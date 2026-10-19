using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Progenitor.Networking;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Live 2026.10.128 on the join page's mods list: a recommended row's reason cut far short of the row's edge, and
/// <c>join_ui forget_seen</c> at the title screen doing nothing.
/// </summary>
public sealed class JoinModsPageFitTests
{
    private const string Lead = "Changed · ";

    // A drawn font narrower than the estimate (about what the game's note font measures).
    private static float Drawn(string s) => s.Length * CellLayout.TwoLineNoteFontSize * 0.45f;

    private static JoinRecommendedRow Row(string reason, string package = "Nightbreak-PopupTotals", string version = "1.0.0") =>
        new(new RecommendedMod("nightbreak.popuptotals", package, version, reason), "PopupTotals", null);

    private static CellSpec NameCell(CellRowSpec spec) => spec.Cells.Single(c => c.Key == JoinFlowLayout.CellName);

    [Fact]
    public void A_recommended_reason_offers_shorter_lines_that_keep_it_whole()
    {
        var choices = NameCell(JoinFlowLayout.Recommended(Row("Item totals while you gather."), false)).NoteChoices;
        Assert.Equal(new[]
        {
            "“Item totals while you gather.” · Thunderstore · Nightbreak · 1.0.0",
            "“Item totals while you gather.” · 1.0.0",
            "“Item totals while you gather.”",
        }, choices);
        // The readback's text keeps every part.
        Assert.EndsWith("“Item totals while you gather.” · Thunderstore · Nightbreak · 1.0.0",
            NameCell(JoinFlowLayout.Recommended(Row("Item totals while you gather."), false)).Text);
        // No reason: the source and version, then the source.
        Assert.Equal(new[] { "Thunderstore · Nightbreak · 1.0.0", "Thunderstore · Nightbreak", "1.0.0" },
            JoinFlowLayout.RecommendedNoteChoices("", "Thunderstore · Nightbreak", "1.0.0"));
    }

    [Fact]
    public void The_line_drawn_is_the_first_choice_that_fits_and_a_short_reason_keeps_its_closing_quote()
    {
        var choices = JoinFlowLayout.RecommendedNoteChoices("“Item totals while you gather.”", "Thunderstore · Nightbreak", "1.0.0");
        // The live row: the name cell about 316 px wide. The estimate used to cut “Item totals while you gather.… here.
        float width = 312f;
        Assert.Equal(Lead + "“Item totals while you gather.” · 1.0.0", CellLayout.FitNoteLine(Lead, choices, width, Drawn));
        // Wide: everything.
        Assert.Equal(Lead + choices[0], CellLayout.FitNoteLine(Lead, choices, 900f, Drawn));
        // Narrow: the reason alone, whole.
        float reasonOnly = Drawn(Lead + choices[2]);
        Assert.Equal(Lead + choices[2], CellLayout.FitNoteLine(Lead, choices, reasonOnly + 1f, Drawn));
    }

    [Fact]
    public void A_reason_too_long_for_the_row_is_cut_at_the_measured_edge_and_keeps_its_closing_quote()
    {
        var choices = JoinFlowLayout.RecommendedNoteChoices("“Shows how many of an item you have while you gather.”", "", "1.0.0");
        float width = 300f;
        string line = CellLayout.FitNoteLine(Lead, choices, width, Drawn);
        Assert.StartsWith(Lead + "“Shows how many", line);
        Assert.EndsWith("…”", line);
        Assert.True(Drawn(line) <= width + 0.5f);
        // The edge, not short of it: within two glyphs of the width (a cut never ends on a space).
        Assert.True(Drawn(line) >= width - 2f * CellLayout.TwoLineNoteFontSize * 0.45f, "cut at " + Drawn(line) + " of " + width);
        Assert.StartsWith(line.Substring(Lead.Length + 1, line.Length - Lead.Length - 3), "Shows how many of an item you have while you gather.");
    }

    [Fact]
    public void Without_a_measurement_the_fit_uses_the_estimate()
    {
        var choices = new[] { "“Item totals while you gather.” · Thunderstore · Nightbreak · 1.0.0", "“Item totals while you gather.”" };
        string line = CellLayout.FitNoteLine(Lead, choices, 330f, _ => -1f);
        Assert.True(CellLayout.EstimateNoteWidth(line) <= 330.5f);
        Assert.Equal(Lead + "“Item totals while you gather.”", line);
        Assert.Equal(line, CellLayout.FitNoteLine(Lead, choices, 330f, null));
    }

    [Fact]
    public void A_recommended_row_with_no_state_word_gives_the_name_the_state_cells_width()
    {
        float inner = JoinFlowLayout.SectionRowWidth(DialogLayout.ContentRowWidth());
        var bare = JoinFlowLayout.InSection(JoinFlowLayout.Recommended(Row("Item totals while you gather."), false), CellSection.Green, last: true);
        var action = bare.Cells.Single(c => c.Key == JoinFlowLayout.CellAction);
        Assert.False(action.Visible);
        var placed = MenuPanelLayout.CellColumns(DialogLayout.ContentRowWidth(), bare);
        float name = placed.Single(p => p.Key == JoinFlowLayout.CellName).Width;
        Assert.True(name >= inner - 44f - 40f, "the name cell is " + name + " of " + inner);

        // An installed copy shows its word, in the state cell's width.
        var installed = new JoinRecommendedRow(new RecommendedMod("a", "O-A", "1.0", "Maps"), "Alpha", "1.0");
        var word = JoinFlowLayout.Recommended(installed, false).Cells.Single(c => c.Key == JoinFlowLayout.CellAction);
        Assert.True(word.Visible);
        Assert.Equal(JoinText.InstalledWord, word.Text);
        // An install in the batch brings the cell back with its phase word.
        var state = new JoinInstallRowState(new JoinInstallRequest("nightbreak.popuptotals", "PopupTotals", "Nightbreak-PopupTotals", "1.0.0",
            JoinInstallKind.Install, required: false, modId: "nightbreak.popuptotals")) { Phase = JoinInstallPhase.Downloading };
        var during = JoinFlowLayout.Recommended(Row("Maps"), true, "", state).Cells.Single(c => c.Key == JoinFlowLayout.CellAction);
        Assert.True(during.Visible);
        Assert.Equal(JoinText.InstallPhaseWord(state), during.Text);
    }

    [Fact]
    public void Forget_seen_by_host_or_everything_clears_the_rows()
    {
        string dir = Path.Combine(Path.GetTempPath(), "progenitor-seen-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, JoinSeenStore.FileName);
        try
        {
            var now = new DateTime(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);
            Assert.True(JoinSeenStore.Update(path, "a1b2@127.0.0.1:9976", r => r.MessageHash = "m", now));
            Assert.True(JoinSeenStore.Update(path, "127.0.0.1:9976", r => r.MessageHash = "m", now));
            Assert.True(JoinSeenStore.Update(path, "c3d4@10.0.0.2:9876", r => r.MessageHash = "m", now));
            Assert.Equal(2, JoinSeenStore.ForgetHost(path, "127.0.0.1:9976"));
            Assert.Equal(new[] { "c3d4@10.0.0.2:9876" }, JoinSeenStore.Read(path).Select(r => r.Key));
            Assert.Equal(0, JoinSeenStore.ForgetHost(path, "127.0.0.1:9976"));
            // An empty host: every server.
            Assert.Equal(1, JoinSeenStore.ForgetHost(path, ""));
            Assert.Empty(JoinSeenStore.Read(path));
            Assert.Equal(0, JoinSeenStore.ForgetHost(path, null));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* */ }
        }
    }
}
