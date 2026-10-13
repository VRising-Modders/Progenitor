using System;
using System.IO;
using System.Linq;
using Progenitor.Networking;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// RADIODESC1 (live 2026-10-06, Server tab): settings-grid radio explanations were cut to one line with an ellipsis,
/// and the Players see preview under Message without Progenitor too. Explanations wrap to up to four lines, each line's
/// height from the measured text; the preview wraps in a fixed four lines.
/// </summary>
public sealed class RadioDescriptionWrapTests
{
    [Theory]
    [InlineData(0f, 1)]
    [InlineData(float.NaN, 1)]
    [InlineData(15.6f, 1)]
    [InlineData(17f, 1)]
    [InlineData(18f, 1)]
    [InlineData(33f, 2)]
    [InlineData(50f, 3)]
    [InlineData(66f, 4)]
    [InlineData(400f, 4)]
    public void An_explanation_gets_the_lines_its_measured_text_needs_up_to_four(float measured, int lines)
    {
        Assert.Equal(lines, UiOptionsRules.RadioDescriptionLines(measured));
        Assert.Equal(UiRadioList.MaxAboutLines, UiOptionsRules.RadioDescriptionLines(1_000f));
    }

    [Fact]
    public void Lines_stack_by_their_own_pitches_and_one_line_keeps_the_old_pitch()
    {
        Assert.Equal(UiOptionsRules.RadioLineWithDescriptionHeight, UiOptionsRules.RadioLinePitch(true, 1));
        Assert.Equal(UiOptionsRules.RadioLineWithDescriptionHeight + 2f * UiRadioList.AboutLineHeight, UiOptionsRules.RadioLinePitch(true, 3));
        Assert.Equal(UiOptionsRules.RadioLinePitch(true, 4), UiOptionsRules.RadioLinePitch(true, 9));
        Assert.Equal(UiRadioList.LineHeight + UiRadioList.OptionGap, UiOptionsRules.RadioLinePitch(false, 0));

        var pitches = new[] { UiOptionsRules.RadioLinePitch(true, 2), UiOptionsRules.RadioLinePitch(true, 1), UiOptionsRules.RadioLinePitch(true, 3) };
        var tops = UiOptionsRules.RadioLineTops(pitches);
        Assert.Equal(UiOptionsRules.RadioListPad, tops[0]);
        Assert.Equal(tops[0] + pitches[0], tops[1]);
        Assert.Equal(tops[1] + pitches[1], tops[2]);
        Assert.Equal(pitches.Sum() + 2f * UiOptionsRules.RadioListPad, UiOptionsRules.RadioListHeight(pitches));
        // Three one-line options: exactly the old n × line + 4.
        var ones = new[] { 1, 1, 1 }.Select(n => UiOptionsRules.RadioLinePitch(true, n)).ToArray();
        Assert.Equal(3f * UiOptionsRules.RadioLineWithDescriptionHeight + 4f, UiOptionsRules.RadioListHeight(ones));
    }

    [Fact]
    public void The_runtime_wraps_measures_and_stacks_described_lines()
    {
        string src = Source("Ui", "UnityUiRuntime.cs");
        Assert.Contains("try { detail.enableWordWrapping = true; } catch { /* */ }", src);
        Assert.Contains("if (radioList && UiOptionsRules.AnyDescription(options))", src);
        Assert.Contains("height = Math.Max(RowHeight, PlaceRadioLines(radioLines, columns.ControlWidth));", src);
        Assert.Contains("measured = description.GetPreferredValues(description.text, textWidth, 0f).y;", src);
        Assert.Contains("count = UiOptionsRules.RadioDescriptionLines(measured);", src);
        Assert.Contains("drt.offsetMin = new Vector2(drt.offsetMin.x, -(top + counts[i] * UiRadioList.AboutLineHeight));", src);
    }

    [Fact]
    public void The_players_see_preview_wraps_in_a_fixed_four_lines()
    {
        string preview = NoProgenitorMessageRow.PreviewLine("");
        var row = ConfigurationPane.WrappingStatusRowSpec("p", NoProgenitorMessageRow.PreviewLabel, preview, 760f, NoProgenitorMessageRow.PreviewLines);
        Assert.False(row.FitWrap); // its text changes while typed: a fixed height, not refitted
        Assert.Equal(NoProgenitorMessageRow.PreviewLines * ConfigurationPane.StatusWrapLineHeight + 12f, row.Height);
        Assert.Equal(CellWrap.Wrap, row.Find(ConfigurationPane.CellStatusValue)!.Wrap);
        Assert.InRange(NoProgenitorMessageRow.PreviewLines, 3, 4);
        // The Placeholders row still fits its fixed text.
        Assert.True(ConfigurationPane.WrappingStatusRowSpec("t", "Placeholders", "a\nb", 760f).FitWrap);

        string join = Source("Networking", "JoinServerSettings.cs");
        // TOKENINSERT1: the preview is now the Players see line under the field (TextTokens), not an info row.
        Assert.DoesNotContain(".StatusWraps(NoProgenitorMessageRow.PreviewLines)", join);
        string view = Source("Ui", "ModSettingsView.cs");
        Assert.Contains("StatusWrapEstimateWidth, item.Options.StatusWrapLines)", view);
    }

    private static string Source(string dir, string file)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "Progenitor.sln"))) d = d.Parent;
        return File.ReadAllText(Path.Combine(d!.FullName, "src", "Progenitor.Core", dir, file));
    }
}
