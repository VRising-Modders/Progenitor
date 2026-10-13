using System;
using System.Linq;
using Progenitor.Ui;
using Progenitor.Ui.GameArt;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// CONTROLS1 (2026-10-03): every settings control the page builder offers is drawn from the game's
/// Options page parts. These are the game-free halves: the composed colors, the row geometry at the
/// workspace's real widths, the per-control alignment rules, and source pins that the runtime applies
/// them. The drawing itself is not live-verified here.
/// </summary>
public sealed class SettingsControlsVanillaTests
{
    private static double ToLinear(double srgb) =>
        srgb <= 0.04045 ? srgb / 12.92 : Math.Pow((srgb + 0.055) / 1.055, 2.4);

    private static double ToSrgb(double linear) =>
        linear <= 0.0031308 ? linear * 12.92 : 1.055 * Math.Pow(linear, 1.0 / 2.4) - 0.055;

    /// <summary>A color blended over a ground in linear space, as the game's UI composes it, in 0..255 sRGB.</summary>
    private static (double R, double G, double B) BlendLinear((float R, float G, float B, float A) top, (double R, double G, double B) ground)
    {
        double Mix(float c, double g) => 255.0 * ToSrgb(ToLinear(c) * top.A + ToLinear(g / 255.0) * (1 - top.A));
        return (Mix(top.R, ground.R), Mix(top.G, ground.G), Mix(top.B, ground.B));
    }

    /// <summary>
    /// The lit row band composes like the game's quiet band, not a saturated blue. The live page (2026-10-03)
    /// read rgb(30,53,81) at the old alpha on its rgb(18,0,0) ground, which is the linear blend (checked here,
    /// so the model is the one the game uses); the game's own band reads about rgb(16,32,45) at its top over
    /// near-black. What this would catch: a band alpha tuned with sRGB arithmetic again.
    /// </summary>
    [Fact]
    public void The_row_band_composes_like_the_games_band_in_linear_space()
    {
        var hue = GameSettingsLook.RowBandHue;
        var old = BlendLinear((hue.R, hue.G, hue.B, 0.3f), (18, 0, 0));
        Assert.InRange(old.G, 47, 59);
        Assert.InRange(old.B, 75, 87);

        // Round 2, measured live 2026-10-03: the flat 0.1 band drew rgb(26,28,46) on rgb(22,0,0).
        var measured = BlendLinear(GameSettingsLook.FlatRowBand(), (22, 0, 0));
        Assert.InRange(measured.R, 22, 32);
        Assert.InRange(measured.G, 23, 34);
        Assert.InRange(measured.B, 40, 52);

        // With the game's band sprite, the game's own composition: 0.753 x 0.102 lit, nothing at rest.
        var sprite = SettingsControlLook.SpriteRowBand(lit: true);
        Assert.Equal(0.753f * 0.102f, sprite.A, 4);
        Assert.Equal(0f, SettingsControlLook.SpriteRowBand(lit: false).A);
        Assert.True(SettingsControlLook.SkinRetryEveryFrames > 0 && SettingsControlLook.SkinRetryForFrames >= 60 * SettingsControlLook.SkinRetryEveryFrames);

        var band = GameSettingsLook.FlatRowBand();
        var onBlack = BlendLinear(band, (2, 2, 3));
        Assert.InRange(onBlack.G, 26, 40);
        Assert.InRange(onBlack.B, 38, 56);
        var onPage = BlendLinear(band, (18, 0, 0));
        Assert.True(onPage.B < 60, "the lit band stays a quiet steel on the page's red ground");
        Assert.Equal(GameSettingsLook.FlatBandAlpha, band.A);
    }

    /// <summary>
    /// At the workspace's widths (the coordinator's 1236/1664 content and the narrow 860), with and without
    /// the description column, the slider's track is the larger part of its pair, its value box ends on the
    /// compact edge after the track, and a 2-4 option radio's segments each hold a 14-character caption.
    /// What this would catch: the stub track beside a wide box seen live, or segments that cut their captions.
    /// </summary>
    [Theory]
    [InlineData(860f)]
    [InlineData(1236f)]
    [InlineData(1664f)]
    public void Controls_fit_the_row_at_the_workspace_widths(float contentWidth)
    {
        foreach (float rowWidth in new[] { contentWidth, ModSettingsDescriptionLayout.HasColumn(contentWidth) ? ModSettingsDescriptionLayout.RowsWidth(contentWidth) : contentWidth })
        {
            var g = ModSettingsRowGrid.For(rowWidth);
            var track = g.ControlSlot(SettingsControlShape.SliderWithValue);
            Assert.True(g.SliderHasValueBox, "value box at " + rowWidth);
            var box = g.SliderValueBoxSlot;
            Assert.Equal(g.CompactRight, box.Left + box.Width);
            Assert.True(track.Left + track.Width + ModSettingsRowGrid.SliderValueGap <= box.Left + 0.01f);
            Assert.True(track.Width >= 1.5f * box.Width, $"track {track.Width} against box {box.Width} at {rowWidth}");
            Assert.True(box.Width >= CellLayout.EstimateTextWidth("1000.5", SettingsControlLook.FieldText), "the box holds a short number");

            // GRIDROW1: a plain slider's track (no value box) ends where a slider field's track ends.
            float slotWidth = g.CompactRight - g.ControlLeft;
            Assert.Equal(track.Width, ModSettingsRowGrid.SliderTrackWidth(slotWidth, g.SliderHasValueBox), 2);

            // Radio segments: the Tall slot spans the control column; 2-4 options of up to 14 characters
            // (UiOptionsRules.FitsSegmented). Two short options fit at the value size; any fitting set fits
            // at the segment's smallest auto-size, except four long options on the narrow pane.
            var slot = g.ControlSlot(SettingsControlShape.Tall);
            Assert.True(slot.Width / 2 - 4f >= CellLayout.EstimateTextWidth("Seven c", SettingsControlLook.FieldText) + 8f);
            for (int n = 2; n <= 4; n++)
            {
                float segment = slot.Width / n - 4f - 8f;
                if (n == 4 && rowWidth < 900f) continue;
                Assert.True(segment >= CellLayout.EstimateTextWidth("Fourteen chars", SettingsControlLook.SegmentMinText),
                    $"{n} segments of {segment} at {rowWidth}");
            }

            // Every control still ends before the reset arrow.
            float resetLeft = g.ResetCenterX - ModSettingsRowGrid.ResetSize * 0.5f;
            Assert.True(g.BarRight <= resetLeft);
        }
    }

    /// <summary>
    /// Each control's look rule: numbers center, free text starts left (a text area at the top), field and
    /// read-only text is the value gray (never bright white), the chosen segment and a checked option are
    /// brighter than the rest, and the segment's index bar is the game's indicator hue. What this would catch:
    /// a centered text field, a white "Locked · 99", or a choice that only color-blind-unsafe hue tells apart.
    /// </summary>
    [Fact]
    public void Each_control_takes_the_games_text_and_state_tones()
    {
        Assert.True(SettingsControlLook.FieldCentered("slider-entry"));
        Assert.True(SettingsControlLook.FieldCentered("numeric"));
        Assert.True(SettingsControlLook.FieldCentered("color"));
        Assert.False(SettingsControlLook.FieldCentered("input"));
        Assert.False(SettingsControlLook.FieldCentered("textarea"));
        Assert.True(SettingsControlLook.FieldTopAligned("textarea"));
        Assert.False(SettingsControlLook.FieldTopAligned("input"));

        Assert.InRange(SettingsControlLook.FieldTextGrey, GameSettingsLook.ValueGrey, 0.9f);
        Assert.Equal(GameSettingsLook.ValueText, SettingsControlLook.FieldText);
        Assert.Equal(12f, SettingsControlLook.FieldTextInset);

        Assert.True(SettingsControlLook.SegmentCaptionGrey(true) > SettingsControlLook.SegmentCaptionGrey(false) + 0.3f);
        Assert.True(SettingsControlLook.SegmentFrameGrey(true) > SettingsControlLook.SegmentFrameGrey(false));
        var on = SettingsControlLook.SegmentIndicator(true);
        var off = SettingsControlLook.SegmentIndicator(false);
        Assert.Equal((GameSettingsLook.SelectorIndicator.R, GameSettingsLook.SelectorIndicator.G, GameSettingsLook.SelectorIndicator.B), (on.R, on.G, on.B));
        Assert.Equal(1f, on.A);
        Assert.True(off.A < GameSettingsLook.SelectorIndicator.A);
        Assert.Equal(5f, SettingsControlLook.IndicatorHeight);
        Assert.Equal(48f, SettingsControlLook.SegmentHeight);

        // A list option is the grid checkbox at the name's grays.
        Assert.Equal(ModSettingsRowGrid.CheckboxSize, SettingsControlLook.ListBoxSize);
        Assert.Equal(ModSettingsRowGrid.CheckmarkSize, SettingsControlLook.ListMarkSize);
        Assert.Equal(GameSettingsLook.NameGrey(false), SettingsControlLook.ListCaptionGrey(false));
        Assert.Equal(GameSettingsLook.NameGrey(true), SettingsControlLook.ListCaptionGrey(true));
        Assert.True(SettingsControlLook.ListLineHeight >= SettingsControlLook.ListBoxSize + 4f);

        Assert.Equal(31f, SettingsControlLook.SwatchSize(ModSettingsRowGrid.ValueBoxHeight));
    }

    /// <summary>
    /// The runtime applies the look to every control the builder offers: radio and multi-select, text area,
    /// info and read-only values, the number's steppers; the settings search is the value box; and the game
    /// button art has a second donor, so a header button is not the kit's flat box. What this would catch: a
    /// control kind dropped from the skin switch, so it silently draws the kit look again.
    /// </summary>
    [Fact]
    public void The_runtime_skins_every_settings_control()
    {
        string look = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "UnityUiRuntime.SettingsLook.cs");
        string skin = SourcePin.MethodBody(look, "private static bool SkinGridControl(string kind, GameObject? control)");
        foreach (var kind in new[] { "\"radio\"", "\"multiselect\"", "\"textarea\"", "\"info\"", "\"numeric\"", "\"input\"", "\"color\"", "\"slider-entry\"", "\"slider\"", "\"choice\"", "\"keybind\"", "\"button\"" })
            SourcePin.AssertUncommented(skin, "case " + kind);
        SourcePin.AssertUncommented(skin, "SkinOptions(control)");
        SourcePin.AssertUncommented(skin, "SkinSteppers(control)");
        SourcePin.AssertUncommented(skin, "SkinInfoValue(control)");
        SourcePin.AssertUncommented(SourcePin.MethodBody(look, "internal static bool TryGameButtonArt("),
            "SettingsArtDonors.TryPart(SettingsArtPart.MenuButton");
        SourcePin.AssertUncommented(SourcePin.MethodBody(look, "private static bool SkinListOption("), "SettingsArtPart.CheckboxFrame");
        SourcePin.AssertUncommented(SourcePin.MethodBody(look, "private static bool SkinSegment("), "SettingsArtPart.ValueBox");

        string runtime = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "UnityUiRuntime.cs");
        SourcePin.AssertUncommented(SourcePin.MethodBody(runtime, "private static bool PaintKeyCap(Cell cell)"), "TryGameButtonArt(");

        // Round 2 (live 2026-10-03, in a world after a hot swap): neither SimpleMenuButton donor drew, so the
        // popup button is the third donor; a skin missing art is retried as the kit's art arrives; a clicked
        // field no longer keeps the hover rim; the band is the game's own gradient sprite.
        string art = SourcePin.MethodBody(look, "internal static bool TryGameButtonArt(out Sprite? sprite, out Image.Type type, out Color color, out float ppu, out string donor)");
        SourcePin.AssertUncommented(art, "SettingsArtDonors.TryPart(SettingsArtPart.PopupButton");
        Assert.True(art.IndexOf("SettingsArtPart.MenuButton", StringComparison.Ordinal) < art.IndexOf("SettingsArtPart.PopupButton", StringComparison.Ordinal),
            "the Options row button wins over the popup button");
        SourcePin.AssertUncommented(look, "RetrySkinLater(c, () => SkinGridControl(k, c));");
        SourcePin.AssertUncommented(SourcePin.MethodBody(runtime, "private static void PaintButton(Cell cell)"), "RetrySkinLater(cell.Go,");
        SourcePin.AssertUncommented(SourcePin.MethodBody(runtime, "internal static void ReleaseInputPumps()"), "ReleaseSkinRetries();");
        SourcePin.AssertUncommented(SourcePin.MethodBody(look, "private static void ApplySpriteSwap("), "state.selectedSprite = null;");
        SourcePin.AssertUncommented(SourcePin.MethodBody(look, "private void PaintChrome(GameObject go, GridChrome chrome)"), "SettingsArtPart.RowBand");
        SourcePin.AssertUncommented(SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "ModSettingsView.cs"), "add(row, RowDrawn,");

        // Round 3: fields, steppers, segments and option boxes answer the pointer with a tint, never a hover sprite.
        SourcePin.AssertUncommented(SourcePin.MethodBody(look, "private static bool SkinField("), "ApplyQuietTint(field, box);");
        SourcePin.AssertUncommented(SourcePin.MethodBody(look, "private static bool SkinSegment("), "ApplyQuietTint(");
        SourcePin.AssertUncommented(SourcePin.MethodBody(look, "private static bool SkinListOption("), "ApplyQuietTint(");
        SourcePin.AssertUncommented(SourcePin.MethodBody(look, "private static bool SkinSteppers("), "ApplyQuietTint(");
        foreach (var body in new[] { "private static bool SkinField(", "private static bool SkinSegment(", "private static bool SkinListOption(", "private static bool SkinSteppers(" })
            Assert.DoesNotContain("ApplySpriteSwap(", SourcePin.MethodBody(look, body), StringComparison.Ordinal);
    }

    /// <summary>
    /// Round 3: no heading-type row on the settings page lights under the pointer, and the Open in Keybinds
    /// button on the row the page actually draws for a section (the subheading) takes the game button art.
    /// What this would catch: the live "General" header lit like a setting, or Open in Keybinds left on the
    /// kit's flat fill because its spec had no skin.
    /// </summary>
    [Fact]
    public void Headings_are_plain_and_Open_in_Keybinds_is_a_game_button()
    {
        var sub = ModSettingsScopes.SubheadingRow(1, "Keys", openKeybinds: true);
        Assert.True(sub.Plain);
        var open = Assert.Single(sub.Cells, c => c.Key == ModSettingsView.CellOpenKeybinds);
        Assert.Equal(CellSkin.KeyCap, open.Skin);
        Assert.True(ModSettingsView.SectionRow(2, "General").Plain);
        Assert.True(ModSettingsView.GameSectionRow(3, "Keys", openKeybinds: true).Plain);
        Assert.Equal(CellSkin.KeyCap, ModSettingsView.GameSectionRow(3, "Keys", openKeybinds: true).Cells.Single(c => c.Key == ModSettingsView.CellOpenKeybinds).Skin);
        Assert.True(ModSettingsScopes.HeadingRow(0, "Your settings").Plain);
        Assert.True(ModSettingsScopes.LineRow(1, "Applies to everyone on X").Plain);

        Assert.InRange(SettingsControlLook.QuietRestGrey, 0.75f, 0.95f);
        Assert.True(SettingsControlLook.QuietFocus.B >= SettingsControlLook.QuietFocus.R, "focus is a steel, not a warm, tint");
    }

    [Fact]
    public void Sample_choices_hold_no_comma()
    {
        // A choice containing a comma is split into two options by the "one of:" parse (SettingBinding,
        // SettingsRow); live 2026-10-03 "Always, even in combat" drew as "Always" and "even in combat".
        string sample = SourcePin.ReadFile("samples", "Progenitor.Sample", "SampleMenu.cs");
        SourcePin.AssertUncommented(sample, "\"Only out of combat\", \"In and out of combat\", \"Never\"");

        string view = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "ModSettingsView.cs");
        SourcePin.AssertUncommented(SourcePin.MethodBody(view, "private void BuildHeader(IUiPanel panel, Action<IUiHandle, string, object?> add)"),
            "skin: CellSkin.ValueBox");
    }
}
