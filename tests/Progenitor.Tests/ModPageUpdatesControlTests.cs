using System;
using System.IO;
using Progenitor.Install;
using Progenitor.Ui;
using Progenitor.Ui.GameArt;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// User report 2026-10-09: the mod page's <b>Updates</b> control read <c>&lt; ilobal (Automatic &gt;</c> (the off-grid
/// selector's caption ran under both arrows) and "Global" did not say where its value lives. The row is now the game's
/// dropdown bar on the settings grid, captioned <b>Default (…)</b>, with a help line and a Preferences » Updates link.
/// </summary>
public sealed class ModPageUpdatesControlTests
{
    [Fact]
    public void The_choices_say_Default_and_name_the_Preferences_value()
    {
        var labels = ModsMenu.ModUpdateChoiceLabels("Automatic", "1.2.0");
        Assert.Equal(new[] { "Default (Automatic)", "Automatic", "Notify", "Off", "Keep 1.2.0" }, labels);
        Assert.Equal("Default (Off)", ModsMenu.ModUpdateChoiceLabels("Off", "1.2.0")[0]);
        Assert.Equal("Default (" + UpdateSettings.DefaultMode + ")", ModsMenu.ModUpdateChoiceLabels("", "")[0]);
        Assert.Equal("Keep this version", ModsMenu.ModUpdateChoiceLabels("Notify", " ")[4]);
        Assert.Equal(ClientAutoUpdate.ModeChoices.Length, labels.Length);
        foreach (var l in labels) Assert.DoesNotContain("Global", l, StringComparison.Ordinal);
    }

    [Fact]
    public void Each_choice_explains_itself_in_order()
    {
        var d = ModsMenu.ModUpdateChoiceDescriptions("Notify", "1.2.0");
        Assert.Equal(ClientAutoUpdate.ModeChoices.Length, d.Length);
        Assert.Equal("Follow Preferences › Updates (currently Notify).", d[0]);
        Assert.StartsWith("Install updates for this mod by itself", d[1], StringComparison.Ordinal);
        Assert.StartsWith("Show updates for this mod", d[2], StringComparison.Ordinal);
        Assert.StartsWith("Never check this mod for updates", d[3], StringComparison.Ordinal);
        Assert.StartsWith("Stay on 1.2.0.", d[4], StringComparison.Ordinal);
        Assert.StartsWith("Stay on this version.", ModsMenu.ModUpdateChoiceDescriptions("", "")[4], StringComparison.Ordinal);
        Assert.Contains("(currently " + UpdateSettings.DefaultMode + ")", ModsMenu.ModUpdateChoiceDescriptions("", "")[0], StringComparison.Ordinal);
    }

    [Fact]
    public void The_stored_word_stays_Global_and_its_message_names_Preferences()
    {
        // The file and the bridge keep "Global"; only the caption changed.
        Assert.Equal("Global", ClientAutoUpdate.ModeChoices[0]);
        Assert.Equal("Keep", ClientAutoUpdate.ModeChoices[4]);
        Assert.Contains("Preferences ›", ModsMenu.ModUpdatesHelp, StringComparison.Ordinal);
        Assert.StartsWith("How this mod updates.", ModsMenu.ModUpdatesHelp, StringComparison.Ordinal);
        Assert.Equal("link-preferences-updates", ModsMenu.ModUpdatesLinkKey);
        Assert.Equal("Updates", ConfigurationPane.UpdatesGroup);
    }

    [Theory]
    [InlineData("Default (Automatic)")]
    [InlineData("Default (Notify)")]
    [InlineData("Keep this version")]
    [InlineData("Keep 10.20.30")]
    public void Every_caption_fits_the_dropdown_bar_at_the_game_text_size(string caption)
    {
        static float Area(float control) =>
            control - GameSettingsLook.Kit(GameSettingsLook.DropdownCaptionLeft) - (UiControlDraw.ChoiceArrowWidth + 4f);
        float wanted = CellLayout.EstimateTextWidth(caption, GameSettingsLook.DropdownText);
        // The grid's own control width (a page wide enough for its full column): the game's text size, unshrunk.
        Assert.True(wanted <= Area(ModSettingsGrid.ControlWidth), caption + " needs " + wanted + " of " + Area(ModSettingsGrid.ControlWidth));
        // The narrowest column the grid draws: it still fits once shrunk to the least size, before any ellipsis.
        float shrunk = wanted * UiControlDraw.ChoiceValueMinFontSize / GameSettingsLook.DropdownText;
        Assert.True(shrunk <= Area(ModSettingsGrid.MinControlWidth), caption + " needs " + shrunk + " of " + Area(ModSettingsGrid.MinControlWidth));
    }

    [Fact]
    public void The_selector_value_area_never_reaches_an_arrow()
    {
        Assert.Equal(180f - 2f * (UiControlDraw.ChoiceArrowWidth + UiControlDraw.ChoiceValuePad),
            UiControlDraw.ChoiceValueAreaWidth(180f));
        Assert.Equal(0f, UiControlDraw.ChoiceValueAreaWidth(40f));
        Assert.True(UiControlDraw.ChoiceValuePad > 0f);
        Assert.True(UiControlDraw.ChoiceValueMinFontSize < UiControlDraw.ChoiceValueFontSize);
    }

    [Fact]
    public void The_runtime_fits_the_value_inside_its_own_area()
    {
        // Source scan: the selector, the dropdown bar's caption and each list option are fitted (no wrap, shrink, ellipsis).
        string root = FindRepoRoot();
        string runtime = File.ReadAllText(Path.Combine(root, "src", "Progenitor.Core", "Ui", "UnityUiRuntime.cs"));
        string look = File.ReadAllText(Path.Combine(root, "src", "Progenitor.Core", "Ui", "UnityUiRuntime.SettingsLook.cs"));
        Assert.Contains("FitInsideArea(tmp, UiControlDraw.ChoiceValueFontSize, UiControlDraw.ChoiceValueMinFontSize)", runtime);
        Assert.Contains("FitInsideArea(caption, 16f, UiControlDraw.ChoiceValueMinFontSize)", runtime);
        Assert.Contains("TextOverflowModes.Ellipsis", runtime);
        Assert.Contains("FitInsideArea(tmp, GameSettingsLook.DropdownText", look);
        string updates = File.ReadAllText(Path.Combine(root, "src", "Progenitor.Core", "Ui", "ModsMenu.Updates.cs"));
        Assert.Contains("panel.SettingsGrid(true)", updates);
        Assert.Contains("SettingsLinks.TextLink(ModUpdatesLinkKey", updates);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "src", "Progenitor.Core", "Ui", "UnityUiRuntime.cs")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}
