using System;
using System.Collections.Generic;
using System.Linq;
using Progenitor.Ui;
using Progenitor.Ui.GameArt;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// MODPAGE2 (user feedback 2026-10-09: "letters have no padding on the margins"; layout approved live the same day): the
/// mod page's spacing rules, each taken from the settings grid the Preferences rows use or the game's Options numbers, and
/// checked here on the cell placements the runtime draws.
/// </summary>
public sealed class ModPageLayoutTests
{
    private const float Content = 1288f;

    private static float Left(CellPlacement p) => p.X - p.Width * 0.5f;

    private static float Right(CellPlacement p) => p.X + p.Width * 0.5f;

    private static CellPlacement Placed(float width, CellRowSpec spec, string key) =>
        CellLayout.Resolve(width, spec.Cells, spec.RightInset).Single(p => p.Key == key);

    [Fact]
    public void The_spacing_rules_come_from_the_settings_grid_and_the_game()
    {
        Assert.Equal(ModSettingsRowGrid.NameInset, ModPageSpacing.Margin);
        Assert.Equal(19f, ModPageSpacing.Margin);                       // the game's 16 units
        Assert.Equal(70f, ModPageSpacing.RowPitch);                     // the game's 58-unit entry
        Assert.Equal(ModPageSpacing.RowPitch - PanelLayout.RowGap, ModPageSpacing.FactRowHeight);
        Assert.Equal(12f, ModPageSpacing.ValueInset);                   // the game's 10 units: a dropdown caption's inset
        Assert.Equal(SettingsAlign.InlineGap, ModPageSpacing.ButtonGap);
        Assert.Equal(SettingsAlign.HeadingExtraTop, ModPageSpacing.HeadingGapAbove);
        Assert.Equal(ModPageSpacing.Margin, ModPageSpacing.ActionsRightInset);
        // The details grid sits beside the description column, and x1 is that grid's control column.
        float facts = ModPageSpacing.FactsWidth(Content);
        Assert.Equal(ModSettingsDescriptionLayout.RowsWidth(Content), facts);
        Assert.True(facts < Content);
        Assert.Equal(ModSettingsRowGrid.For(facts).ControlLeft, ModPageSpacing.X1(facts));
        Assert.Equal(ModSettingsRowGrid.For(facts).BarRight, ModPageSpacing.ValueRight(facts));
    }

    [Fact]
    public void A_details_row_puts_its_name_at_the_margin_and_its_value_at_x1()
    {
        float facts = ModPageSpacing.FactsWidth(Content);
        var spec = ModPagePane.FactRow("client", ModPagePane.FactClient,
            new[] { ModPagePane.FactText(ModPagePane.CellFactValue, "Installed 0.1.0", CellTone.OkBright) }, facts);
        float rowLeft = -facts * 0.5f;

        var name = Placed(facts, spec, ModPagePane.CellFactName);
        // The caption draws CellTextOffset inside its cell, so the cell starts that much before the margin.
        Assert.Equal(ModPageSpacing.Margin - ModPageSpacing.CellTextOffset, Left(name) - rowLeft, 2);
        Assert.Equal(ModPageSpacing.X1(facts) - CellLayout.Gap, Right(name) - rowLeft, 2);

        var value = Placed(facts, spec, ModPagePane.CellFactValue);
        Assert.Equal(ModPageSpacing.X1(facts) + ModPageSpacing.ValueInset - ModPageSpacing.CellTextOffset, Left(value) - rowLeft, 2);
        Assert.True(Right(value) - rowLeft <= ModPageSpacing.ValueRight(facts) + 0.01f, "a value ends at the grid's bar edge");

        // One pitch, the game's separator and band, and one name color for every row (the grid's resting gray).
        Assert.Equal(ModPageSpacing.FactRowHeight, spec.ExplicitHeight);
        Assert.True(spec.Separator);
        Assert.Equal(CellRowSkin.GameEntry, spec.Skin);
        Assert.Equal(CellTone.SettingName, spec.Cells.Single(c => c.Key == ModPagePane.CellFactName).Tone);
        var gray = GameSettingsLook.NameGrey(false);
        Assert.Equal(new PaletteColor(gray, gray, gray), MenuPalette.TextColor(CellTone.SettingName));
    }

    [Fact]
    public void A_control_value_starts_at_x1_and_buttons_keep_the_kit_gap()
    {
        float facts = ModPageSpacing.FactsWidth(Content);
        var spec = ModPagePane.FactRow("store", ModPagePane.FactStore, ModPagePane.StoreButtons("https://example.test/p", share: true), facts, textValue: false);
        float rowLeft = -facts * 0.5f;
        var store = Placed(facts, spec, ModPagePane.CellStore);
        var share = Placed(facts, spec, ModPagePane.CellShare);
        Assert.Equal(ModPageSpacing.X1(facts), Left(store) - rowLeft, 2);
        Assert.Equal(ModPageSpacing.ButtonGap, Left(share) - Right(store), 2);
    }

    [Fact]
    public void The_actions_bar_ends_its_last_button_one_margin_from_the_right()
    {
        var line = ModFrameworkLine.Compose(new ModFrameworkFacts
        {
            PluginId = "example.layout",
            PageState = ModPageState.Installed,
            Version = "1.0.0",
            Sides = ModSides.Client,
            ActionCaption = ModPageCopy.Uninstall,
            ActionTone = LabelTone.Orange,
        });
        var bar = ModPagePane.ActionsBar(line);
        Assert.Equal(ModPagePane.FrameworkKey, bar.Key);
        Assert.Equal(ModPageSpacing.LeadKey, bar.Cells[0].Key);
        float rowLeft = -Content * 0.5f;
        var uninstall = Placed(Content, bar, ModFrameworkLine.CellAction);
        Assert.Equal(ModPageSpacing.Margin, Content * 0.5f - Right(uninstall), 2);
        // The first state cell (the switch, or the status word) starts at the margin less the caption offset.
        var first = CellLayout.Resolve(Content, bar.Cells, bar.RightInset).Where(p => p.Visible && p.Key != ModPageSpacing.LeadKey).OrderBy(p => p.X).First();
        Assert.Equal(ModPageSpacing.Margin - ModPageSpacing.CellTextOffset, Left(first) - rowLeft, 2);
    }

    [Fact]
    public void Section_headings_sit_at_the_margin_with_the_settings_heading_space_above()
    {
        var heading = ModPagePane.SectionHeading("details", ModPagePane.DetailsHeading);
        Assert.Equal(ModPageSpacing.LeadKey, heading.Cells[0].Key);
        Assert.Equal(ModPageSpacing.HeadingGapAbove, heading.GapAbove);
        Assert.Equal(CellRowSkin.CategoryHeading, heading.Skin);
        var title = Placed(Content, heading, "title");
        Assert.Equal(ModPageSpacing.Margin - ModPageSpacing.CellTextOffset, Left(title) + Content * 0.5f, 2);
        Assert.Equal(ModPageSpacing.HeadingFontSize, heading.Cells.Single(c => c.Key == "title").FontSize);
    }

    [Fact]
    public void The_settings_scope_rows_and_the_header_start_at_the_margin()
    {
        Assert.Equal(ModPageSpacing.LeadKey, ModSettingsScopes.HeadingRow(0, "Your settings").Cells[0].Key);
        Assert.Equal(ModPageSpacing.LeadKey, ModSettingsScopes.LineRow(0, "Applies to everyone").Cells[0].Key);
    }

    [Fact]
    public void Each_details_row_and_option_explains_itself_in_the_column()
    {
        foreach (var help in new[]
                 {
                     ModPagePane.SidesHelp, ModPagePane.ClientHelp, ModPagePane.ServerHelp, ModPagePane.ReleasedHelp,
                     ModPagePane.StoreHelp, ModPagePane.PagesHelp, ModPagePane.KeptHelp, ModPagePane.UpdateHelp,
                 })
        {
            Assert.False(string.IsNullOrWhiteSpace(help));
            Assert.EndsWith(".", help, StringComparison.Ordinal);
        }

        Assert.Equal("Point at a row to read what it is.", ModPageDetails.ColumnIntro);
        Assert.Equal("None here. Set it up the way its author describes.", ModPagePane.NoSettingsFact);
    }

    [Fact]
    public void About_wraps_at_a_readable_width()
    {
        Assert.Equal(820f, ModPageSpacing.ReadableWidth);
        Assert.True(ModPagePane.AboutRowHeight(3) > ModPagePane.AboutRowHeight(1));
        Assert.Equal(ModPagePane.AboutRowHeight(1), ModPagePane.AboutRowHeight(0));
    }
}
