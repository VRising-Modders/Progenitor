using System;
using System.Collections.Generic;
using Progenitor.Sample;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Job FSAMP. Game-free coverage of the <c>Ctrl+F9</c> log panel's rows and of the chord itself.
/// The Unity panel is behind <c>!PROGENITOR_TESTS</c>; what is pinned here is the text a player
/// reads and the fact that the second keybind really is a modifier chord whose cfg spelling is
/// the BepInEx <c>KeyboardShortcut</c> form <c>docs/modules.md</c> documents.
/// </summary>
[Collection("PluginLogger")]
public class SampleLogPanelTests : IDisposable
{
    public SampleLogPanelTests() => SampleLog.Clear();

    public void Dispose() => SampleLog.Clear();

    [Fact]
    public void An_empty_ring_still_renders_a_header_and_one_explanatory_row()
    {
        var rows = SampleLogPanel.Rows(Array.Empty<string>());

        Assert.Equal(2, rows.Count);
        Assert.Equal("Last 0 of 3 Sample line(s) in this process:", rows[0]);
        Assert.Equal(SampleLogPanel.EmptyRow, rows[1]);
    }

    [Fact]
    public void A_null_ring_is_treated_as_empty_rather_than_throwing()
    {
        var rows = SampleLogPanel.Rows(null);
        Assert.Equal(SampleLogPanel.EmptyRow, rows[1]);
    }

    [Fact]
    public void The_rows_are_the_last_three_lines_oldest_first()
    {
        SampleLog.Info("epoch", "one");
        SampleLog.Info("access", "two");
        SampleLog.Info("godmode", "three");
        SampleLog.Info("patchdemo", "four");

        var rows = SampleLogPanel.Rows(SampleLog.Recent(SampleLog.PanelLines));

        Assert.Equal(4, rows.Count);
        Assert.Equal("Last 3 of 3 Sample line(s) in this process:", rows[0]);
        Assert.Equal("Sample access: two", rows[1]);
        Assert.Equal("Sample godmode: three", rows[2]);
        Assert.Equal("Sample patchdemo: four", rows[3]);
    }

    [Fact]
    public void A_line_recorded_by_another_Sample_file_reaches_the_panel()
    {
        // SampleUi writes its own "Sample: ui …" lines; Record is how they land in the ring so a
        // client, where almost nothing else logs, has something to show.
        SampleLog.Record("Sample: ui demo open");

        var rows = SampleLogPanel.Rows(SampleLog.Recent(SampleLog.PanelLines));
        Assert.Equal("Sample: ui demo open", rows[1]);
    }

    [Fact]
    public void The_second_bind_is_a_real_modifier_chord_and_its_cfg_spelling_is_F9_plus_Ctrl()
    {
        var chord = new KeyChord(UiKey.F9, KeyModifiers.Ctrl);

        Assert.Equal("F9 + Ctrl", chord.ToString());
        Assert.NotEqual(new KeyChord(UiKey.F9), chord);
        Assert.Equal("ToggleLogPanel", SampleLogPanel.KeybindId);
    }

    [Fact]
    public void The_chord_round_trips_through_the_cfg_parser()
    {
        Assert.True(KeyChord.TryParse("F9 + Ctrl", out var parsed));
        Assert.Equal(new KeyChord(UiKey.F9, KeyModifiers.Ctrl), parsed);
    }

    [Fact]
    public void The_settings_demo_entries_declare_the_range_and_the_list_the_panel_shows()
    {
        Assert.Equal(1, SampleUi.SpawnMultiplierMin);
        Assert.Equal(10, SampleUi.SpawnMultiplierMax);
        Assert.InRange(SampleUi.SpawnMultiplierDefault, SampleUi.SpawnMultiplierMin, SampleUi.SpawnMultiplierMax);
        Assert.Equal(new[] { "Low", "Med", "High" }, SampleUi.DifficultyValues);
        Assert.Contains(SampleUi.DifficultyDefault, SampleUi.DifficultyValues);
        // Element 0 is what AcceptableValueList.Clamp would return for anything invalid, which is
        // why the panel refuses instead of writing - and why the default is deliberately not it.
        Assert.NotEqual(SampleUi.DifficultyDefault, SampleUi.DifficultyValues[0]);
    }
}
