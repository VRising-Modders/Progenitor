using System;
using System.Collections.Generic;
using System.Linq;
using Progenitor.Ui;
using Progenitor.Ui.GameArt;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// DIALOGS2: Progenitor's dialogs drawn like the game's own popup (<c>CommonPopup</c>, probe D1 2026-10-01):
/// the <c>Window_Box_Background</c> panel, the <c>Divider_Big_02</c> header rule, equal <c>SmallerButton_*</c>
/// buttons with their hover/pressed/disabled sprites, and the black 0.8 blocker. Game-free model checks and
/// source pins; the live look is the coordinator's check.
/// </summary>
[Collection("PluginLogger")]
public sealed class DialogsVanillaTests : IDisposable
{
    public DialogsVanillaTests()
    {
        SettingsArtLadder.PrepareHook = null;
        SettingsArtLadder.Reset();
    }

    public void Dispose()
    {
        SettingsArtLadder.PrepareHook = null;
        SettingsArtLadder.Reset();
    }

    /// <summary>
    /// The popup parts are art parts of the shared ladder (so they are owned-copied on the same idle frames
    /// as the settings kit, in both hosts), matched by the names D1 read, and 9-sliced as the game slices
    /// them. What this would catch: a misspelled sprite name that silently draws the kit forever.
    /// </summary>
    [Fact]
    public void The_popup_parts_are_the_D1_sprites_on_the_shared_ladder()
    {
        Assert.Equal(SettingsArtPart.PopupFrame, SettingsArtLadder.PartOf("Window_Box_Background"));
        Assert.Equal(SettingsArtPart.PopupButton, SettingsArtLadder.PartOf("SmallerButton_Normal"));
        Assert.Equal(SettingsArtPart.PopupButtonHover, SettingsArtLadder.PartOf("SmallerButton_Normal_Hover"));
        Assert.Equal(SettingsArtPart.PopupButtonPressed, SettingsArtLadder.PartOf("SmallerButton_Pressed"));
        Assert.Equal(SettingsArtPart.PopupButtonDisabled, SettingsArtLadder.PartOf("SmallerButton_Disabled"));
        foreach (var part in new[]
                 {
                     SettingsArtPart.PopupFrame, SettingsArtPart.PopupButton, SettingsArtPart.PopupButtonHover,
                     SettingsArtPart.PopupButtonPressed, SettingsArtPart.PopupButtonDisabled,
                 })
            Assert.True(SettingsArtLadder.Sliced(part), part.ToString());
        Assert.Equal((32f, 32f, 32f, 32f), GameSettingsLook.PopupFrameBorder);
        Assert.Equal((40f, 0f, 70f, 0f), GameSettingsLook.PopupButtonBorder);
        Assert.Equal((0f, 0f, 0f, 0.8f), GameSettingsLook.PopupBlocker);
        Assert.Equal(26, SettingsArtLadder.PartCount); // WINDOWS1 added the window title ornament

        string donors = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "GameArt", "SettingsArtDonors.cs");
        string menus = SourcePin.MethodBody(donors, "private static void ReadMenus(");
        SourcePin.AssertUncommented(menus, "var popups = resident ? Loaded<global::CommonPopup>() : Scene<global::CommonPopup>();");
        SourcePin.AssertUncommented(menus, "Collect(found, popup.gameObject, SettingsArtLadder.DonorPopup)");
    }

    /// <summary>
    /// Dialog buttons are equal and wide like the game's (256 each when the row holds them), drawn with the
    /// popup skin, Cancel first (our No/Yes order). A long caption still widens them all equally. What this
    /// would catch: the narrow kit buttons, unequal widths, or a reordered Yes/No.
    /// </summary>
    [Fact]
    public void Buttons_are_equal_wide_popup_buttons_in_our_order()
    {
        var buttons = new[]
        {
            new DialogButton("yes", "Yes", DialogButtonRole.Main, CellTone.Danger),
            new DialogButton("no", "No", DialogButtonRole.Cancel, CellTone.Normal),
        };
        var spec = DialogLayout.ButtonRowSpec(UiDialogs.ButtonsRow, buttons, DialogLayout.ContentRowWidth());
        var cells = spec.Cells.Where(c => c.Kind == CellKind.Button).ToList();
        Assert.Equal(new[] { "no", "yes" }, cells.Select(c => c.Key).ToArray());
        Assert.All(cells, c => Assert.Equal(CellSkin.PopupButton, c.Skin));
        Assert.Single(cells.Select(c => c.Width.Value).Distinct());
        float expected = Math.Min(DialogLayout.PreferredButtonWidth, DialogLayout.MaxEqualWidth(2, DialogLayout.ContentRowWidth()));
        Assert.Equal(Math.Max(expected, DialogLayout.ButtonWidth(new[] { "No", "Yes" }, DialogLayout.ContentRowWidth())), cells[0].Width.Value, 2);
        Assert.Equal(CellTone.Danger, cells[1].Tone);

        var three = DialogLayout.ButtonRowSpec(UiDialogs.ButtonsRow, new[]
        {
            new DialogButton("a", "Keep both", DialogButtonRole.Option, CellTone.Normal),
            new DialogButton("b", "Use the new key for this mod", DialogButtonRole.Option, CellTone.Normal),
            new DialogButton("c", "Cancel", DialogButtonRole.Cancel, CellTone.Normal),
        }, DialogLayout.ContentRowWidth());
        var widths = three.Cells.Where(c => c.Kind == CellKind.Button).Select(c => c.Width.Value).Distinct().ToList();
        Assert.Single(widths);
        Assert.True(widths[0] * 3 <= DialogLayout.ContentRowWidth(), "three equal buttons still fit the row");
    }

    /// <summary>
    /// The title row reserves the header rule's band (the rule and its ornament sit under the title, never
    /// on the body); the rule spans the game's share of the content. The dialogs readback names each part's
    /// source; game-free every part is the kit's.
    /// </summary>
    [Fact]
    public void The_header_rule_band_and_the_art_readback()
    {
        Assert.Equal(DialogLayout.TitleRuleHeight + DialogLayout.TitleRuleBottom + 8f, DialogLayout.TitleRuleBand);
        Assert.InRange(DialogLayout.TitleRuleWidth(), DialogLayout.ContentRowWidth() * 0.9f, DialogLayout.ContentRowWidth());
        Assert.Equal(0.9f, DialogLayout.TextGrey);

        SettingsArtLadder.Prepare();
        var art = SettingsArtLadder.DescribeDialog();
        foreach (var key in new[] { "frame", "titleRule", "button", "buttonHover", "buttonPressed", "buttonDisabled", "check", "checkMark" })
            Assert.Equal("kit", ((Dictionary<string, object?>)art[key]!)["source"]);
        Assert.Equal("game-blocker-black-0.8", art["overlay"]);
        Assert.True(UiDialog.Describe().ContainsKey("art"));
    }

    /// <summary>
    /// The runtime draws the game's popup: the frame and the blocker in the dialog chrome, the header rule
    /// under the title (with its reserved band), the popup button skin with the game's sprite swap and a red
    /// destructive caption, and a kit fallback for each when the art is not ready.
    /// </summary>
    [Fact]
    public void The_runtime_draws_the_games_popup_with_kit_fallbacks()
    {
        string runtime = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "UnityUiRuntime.cs");
        string chrome = SourcePin.MethodBody(runtime, "private void PaintDialogChrome(");
        SourcePin.AssertUncommented(chrome, "PaintPart(image, GameArt.SettingsArtPart.PopupFrame, Color.white,");
        SourcePin.AssertUncommented(chrome, "fill.color = Rgba(GameArt.GameSettingsLook.PopupBlocker);");
        SourcePin.AssertUncommented(SourcePin.MethodBody(runtime, "public IRuntimeWidget? AddDialogText("), "if (title) AddDialogTitleRule(go);");
        SourcePin.AssertUncommented(runtime, "if (title) height += DialogLayout.TitleRuleBand;");
        SourcePin.AssertUncommented(runtime, "if (cell.Spec.Skin == CellSkin.PopupButton && PaintPopupButton(cell)) return;");
        string paint = SourcePin.MethodBody(runtime, "private static bool PaintPopupButton(");
        // VANILLA1: the Button states moved to a helper shared with the filter chips.
        SourcePin.AssertUncommented(paint, "ApplyPopupStates(cell);");
        SourcePin.AssertUncommented(SourcePin.MethodBody(runtime, "private static void ApplyPopupStates("), "button.transition = Selectable.Transition.SpriteSwap;");
        SourcePin.AssertUncommented(paint, "? Rgba(DialogLayout.DangerCaptionRgb)");
        SourcePin.AssertUncommented(paint, "return false;");

        string look = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "UnityUiRuntime.SettingsLook.cs");
        SourcePin.AssertUncommented(SourcePin.MethodBody(look, "private static void AddDialogTitleRule("), "SettingsArtPart.DetailsOrnament");
    }
}
