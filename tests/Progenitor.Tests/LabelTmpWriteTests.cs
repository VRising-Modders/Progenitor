using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// ALIGN2: caption alignment is the horizontal anchor only. ALIGN1 wrote
/// combined <c>TextAlignmentOptions.Left/Center</c> (513/514) onto every
/// workspace TMP; those values letter-spaced the Mods strip, Search, sidebar
/// and footer (gen 17 vs gen 16).
/// </summary>
public class LabelTmpWriteTests
{
    [Fact]
    public void Caption_write_is_horizontal_anchor_only_and_does_not_touch_spacing()
    {
        Assert.Equal(1, LabelTmpWrite.Horizontal(left: true));
        Assert.Equal(2, LabelTmpWrite.Horizontal(left: false));
        Assert.Equal(513, LabelTmpWrite.Combined(left: true));
        Assert.Equal(514, LabelTmpWrite.Combined(left: false));
        Assert.Equal(LabelTmpWrite.HorizontalLeft, LabelTmpWrite.Horizontal(true));
        Assert.Equal(LabelTmpWrite.HorizontalCenter, LabelTmpWrite.Horizontal(false));
        Assert.Equal(0, LabelTmpWrite.Horizontal(true) & LabelTmpWrite.Flush);
        Assert.Equal(0, LabelTmpWrite.Horizontal(false) & LabelTmpWrite.Flush);
        Assert.Equal(0, LabelTmpWrite.Horizontal(true) & LabelTmpWrite.Justified);
        Assert.Equal(0, LabelTmpWrite.Horizontal(false) & LabelTmpWrite.Justified);
        Assert.NotEqual(LabelTmpWrite.Flush, LabelTmpWrite.Horizontal(true));
        Assert.NotEqual(LabelTmpWrite.Justified, LabelTmpWrite.Horizontal(true));
        Assert.False(LabelTmpWrite.WritesCharacterSpacing);
        Assert.False(LabelTmpWrite.WritesWordSpacing);
        // Revert: writing 513/514 as alignment, or assigning characterSpacing, restores gen 17 tracking.
    }

    [Fact]
    public void Alignment_path_writes_horizontalAlignment_masked_and_never_spacing_or_flush()
    {
        string runtime = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "UnityUiRuntime.cs");
        string apply = SourcePin.MethodBody(runtime, "private static void ApplyHorizontalAlign");
        SourcePin.AssertUncommented(apply, "tmp.horizontalAlignment");
        SourcePin.AssertUncommented(apply, "LabelTmpWrite.HorizontalMask");
        SourcePin.AssertUncommented(apply, "left ? TextAlignmentOptions.Left : TextAlignmentOptions.Center");
        SourcePin.AssertNoStatementContaining(apply, "characterSpacing");
        SourcePin.AssertNoStatementContaining(apply, "wordSpacing");
        SourcePin.AssertNoStatementContaining(apply, "Justified");
        SourcePin.AssertNoStatementContaining(apply, "Flush");
        SourcePin.AssertNoStatementContaining(apply, "tmp.alignment");

        string caption = SourcePin.MethodBody(runtime, "private static void ApplyCaptionAlign");
        SourcePin.AssertUncommented(caption, "ApplyHorizontalAlign(tmp, left)");
        SourcePin.AssertNoStatementContaining(caption, "characterSpacing");
        SourcePin.AssertNoStatementContaining(caption, "wordSpacing");
        SourcePin.AssertNoStatementContaining(caption, "Justified");
        SourcePin.AssertNoStatementContaining(caption, "Flush");
        SourcePin.AssertNoStatementContaining(caption, "tmp.alignment");

        string make = SourcePin.MethodBody(runtime, "private static GameObject? MakeLabel(");
        SourcePin.AssertUncommented(make, "left ? TextAlignmentOptions.Left : TextAlignmentOptions.Center");
        SourcePin.AssertUncommented(make, "tmp.horizontalAlignment");
        SourcePin.AssertUncommented(make, "LabelTmpWrite.HorizontalMask");
        SourcePin.AssertUncommented(make, "tmp.enableAutoSizing = false");
        SourcePin.AssertUncommented(make, "tmp.fontStyle = FontStyles.Normal");
        SourcePin.AssertUncommented(make, "labelRt.localScale = Vector3.one");
        SourcePin.AssertUncommented(make, "ApplyCachedFont(tmp)");
        SourcePin.AssertNoStatementContaining(make, "characterSpacing");
        SourcePin.AssertNoStatementContaining(make, "wordSpacing");
        SourcePin.AssertNoStatementContaining(make, "Justified");
        SourcePin.AssertNoStatementContaining(make, "Flush");
        SourcePin.AssertNoStatementContaining(make, "tmp.alignment");

        string clone = SourcePin.MethodBody(runtime, "private static GameObject? CloneOrCreateButton(");
        SourcePin.AssertUncommented(clone, "ApplyHorizontalAlign(label, left || multiline)");
        SourcePin.AssertNoStatementContaining(clone, "characterSpacing");
        SourcePin.AssertNoStatementContaining(clone, "tmp.alignment");
    }

    [Fact]
    public void Inline_row_puts_the_field_on_the_left_and_buttons_on_the_right_with_a_gap()
    {
        var geo = SettingsAlign.InlineRow(700f, 2);
        Assert.Equal(2, geo.ButtonCount);
        Assert.Equal(SettingsAlign.InlineButtonWidth, geo.ButtonWidth);
        Assert.Equal(SettingsAlign.InlineGap, geo.Gap);
        Assert.True(geo.FieldWidth > geo.ButtonWidth);
        Assert.True(geo.FieldLeft < geo.ButtonLeft(0));
        Assert.Equal(geo.Gap, geo.ButtonLeft(0) - geo.FieldRight, 3);
        Assert.Equal(geo.Gap, geo.ButtonLeft(1) - geo.ButtonRight(0), 3);
        Assert.True(geo.ButtonRight(1) <= 700f * 0.5f + 0.01f);
        Assert.True(geo.FieldLeft >= -700f * 0.5f - 0.01f);
        // Revert: placing captions at RowWidth while the button rects sit on the right is ApplyClear.
    }

    [Fact]
    public void Inline_captions_are_stretched_into_the_button_rects()
    {
        string runtime = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "UnityUiRuntime.cs");
        string place = SourcePin.MethodBody(runtime, "private void PlaceInlineRow(");
        SourcePin.AssertUncommented(place, "SettingsAlign.InlineRow(");
        SourcePin.AssertUncommented(place, "FitInlineButtonCaption(");
        string fit = SourcePin.MethodBody(runtime, "private static void FitInlineButtonCaption");
        SourcePin.AssertUncommented(fit, "PrepareStripButtonCaption(");
        SourcePin.AssertUncommented(fit, "GetComponentInChildren<TextMeshProUGUI>");
    }

    [Fact]
    public void A_body_row_that_repeats_the_pane_title_is_dropped()
    {
        string runtime = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "UnityUiRuntime.cs");
        string add = SourcePin.MethodBody(runtime, "public IRuntimeWidget? AddLabel(");
        SourcePin.AssertUncommented(add, "IsRedundantPaneTitle");
        SourcePin.AssertUncommented(add, "SkipRedundantPaneTitle");
        string skip = SourcePin.MethodBody(runtime, "private static bool IsRedundantPaneTitle");
        SourcePin.AssertUncommented(skip, "host.Title");
        SourcePin.AssertUncommented(skip, "host.IsMenuPane");
        SourcePin.AssertUncommented(skip, "host.IsSidebar");
        SourcePin.AssertUncommented(skip, "host.IsStrip");
    }

    [Fact]
    public void SweepFont_picks_by_donor_rank_not_walk_order()
    {
        string runtime = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "UnityUiRuntime.cs");
        string sweep = SourcePin.MethodBody(runtime, "private static TMP_FontAsset? SweepFont(");
        SourcePin.AssertUncommented(sweep, "PickFromLoadedFontAssets()");
        SourcePin.AssertUncommented(sweep, "UiFontDonor.PickIndex(");
        SourcePin.AssertUncommented(sweep, "UiFontDonor.KindMenuButtonPrefab");
        SourcePin.AssertUncommented(sweep, "UiFontDonor.KindOptionsLabel");
        SourcePin.AssertUncommented(sweep, "MenuButtonPrefab?.ButtonText");
        SourcePin.AssertUncommented(sweep, "RememberSweep(");
        SourcePin.AssertNoStatementContaining(sweep, "if (found != null) return");

        string loaded = SourcePin.MethodBody(runtime, "private static TMP_FontAsset? PickFromLoadedFontAssets(");
        SourcePin.AssertUncommented(loaded, "UiFontDonor.KindAssetList");
        SourcePin.AssertUncommented(loaded, "UiFontDonor.PickLoadedIndex(");
        SourcePin.AssertUncommented(loaded, "font.material");
        SourcePin.AssertUncommented(loaded, "ForEachLoadedFontAsset(");

        string enumerate = SourcePin.MethodBody(runtime, "private static void ForEachLoadedFontAsset(");
        SourcePin.AssertUncommented(enumerate, "Il2CppType.Of<TMP_FontAsset>");
        SourcePin.AssertUncommented(enumerate, "FindObjectsOfTypeAll");
        SourcePin.AssertNoStatementContaining(enumerate, "FindObjectsOfType(");

        string remember = SourcePin.MethodBody(runtime, "private static TMP_FontAsset? RememberSweep(");
        SourcePin.AssertUncommented(remember, "chosen.Font.material");
        SourcePin.AssertUncommented(remember, "UiFontDonor.SourceForKind(");
        // Revert: first-non-owned TMP wins, or pairing the body asset with another variant's material.
    }

    [Fact]
    public void Label_path_writes_no_spacing_and_pins_normal_style_and_unit_scale()
    {
        Assert.Equal(0f, TmpReadback.LabelDefaults(UiFontDonor.BodyFontName)["characterSpacing"]);
        Assert.Equal(0f, TmpReadback.LabelDefaults(UiFontDonor.BodyFontName)["wordSpacing"]);
        Assert.Equal(0, TmpReadback.LabelDefaults(UiFontDonor.BodyFontName)["fontStyle"]);
        Assert.False((bool)TmpReadback.LabelDefaults(UiFontDonor.BodyFontName)["enableAutoSizing"]!);
        Assert.False(LabelTmpWrite.WritesCharacterSpacing);
        Assert.False(LabelTmpWrite.WritesWordSpacing);
        string runtime = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "UnityUiRuntime.cs");
        string apply = SourcePin.MethodBody(runtime, "private static void ApplyCachedFont");
        SourcePin.AssertUncommented(apply, "tmp.font = font");
        SourcePin.AssertUncommented(apply, "tmp.fontSharedMaterial = mat");
        SourcePin.AssertNoStatementContaining(apply, "characterSpacing");
        SourcePin.AssertNoStatementContaining(apply, "wordSpacing");
        SourcePin.AssertNoStatementContaining(apply, "fontStyle");
        // Revert: writing characterSpacing, or dropping ApplyCachedFont, restores tracked glyphs.
    }
}
