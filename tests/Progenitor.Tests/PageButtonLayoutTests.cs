using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>CSAMPLE1: a custom page's buttons are the game's compact button sized to the caption, side by side. Game-free.</summary>
public sealed class PageButtonLayoutTests
{
    [Fact]
    public void A_short_caption_gets_the_minimum_and_a_long_one_its_text_width()
    {
        Assert.Equal(PageButtonLayout.MinWidth, PageButtonLayout.Width("OK"));
        float longer = PageButtonLayout.Width("Back to Progenitor Controls Sample");
        Assert.True(longer > PageButtonLayout.MinWidth);
        Assert.Equal(CellLayout.EstimateTextWidth("Back to Progenitor Controls Sample", PageButtonLayout.FontSize) + PageButtonLayout.CaptionPad, longer, 3);
    }

    [Fact]
    public void A_button_is_never_wider_than_its_row()
    {
        Assert.Equal(200f, PageButtonLayout.Width(new string('W', 200), 200f));
    }

    [Fact]
    public void Inline_buttons_sit_side_by_side_from_the_left_with_the_gap()
    {
        var lefts = PageButtonLayout.Lefts(new[] { 140f, 180f, 160f });
        Assert.Equal(new[] { 0f, 140f + PageButtonLayout.Gap, 140f + 180f + 2 * PageButtonLayout.Gap }, lefts);
    }
}

/// <summary>RADIOART1: the radio circle is a ring and a dot drawn in code (the game's fonts have no circle glyph). Game-free.</summary>
public sealed class RadioGlyphTests
{
    [Fact]
    public void The_ring_is_hollow_and_the_dot_is_solid_at_the_center()
    {
        int n = RadioGlyph.TextureSize, c = n / 2;
        Assert.Equal(0f, RadioGlyph.RingAlpha(c, c, n));
        Assert.Equal(1f, RadioGlyph.DotAlpha(c, c, n));
        int onRing = c + (int)(n * (RadioGlyph.RingRadiusShare - RadioGlyph.RingWidthShare * 0.5f));
        Assert.True(RadioGlyph.RingAlpha(onRing, c, n) > 0.9f);
        Assert.Equal(0f, RadioGlyph.DotAlpha(onRing, c, n));
        Assert.Equal(0f, RadioGlyph.RingAlpha(0, 0, n));
    }

    [Fact]
    public void The_chosen_ring_is_brighter()
    {
        Assert.True(RadioGlyph.RingGrey(true) > RadioGlyph.RingGrey(false));
    }

    /// <summary>
    /// Review 2026-10-11: on a custom page or a game window, a button in an inline block that already has a field or toggle
    /// row (BeginInline; Input; Button) sits beside that control, in the game's art; a page button row dropped it under the
    /// field. A page button is sized to the caption it shows now, and destroyed rows and buttons are forgotten.
    /// </summary>
    [Fact]
    public void A_page_button_joins_an_inline_field_and_follows_its_caption()
    {
        string runtime = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "UnityUiRuntime.cs");
        string add = SourcePin.MethodBody(runtime, "public IRuntimeWidget? AddPageButton(IRuntimePanel panel, string caption)");
        SourcePin.AssertUncommented(add, "if (host.HasInlineHost)");
        SourcePin.AssertUncommented(add, "var inline = AddButton(panel, caption ?? \"\");");
        SourcePin.AssertUncommented(add, "if (inline != null) SetGameButton(inline);");
        SourcePin.AssertUncommented(SourcePin.MethodBody(runtime, "private void PlacePageButtons(in PanelRow row, float height)"),
            "ShownCaption(b.Button, b.Caption)");
        SourcePin.AssertUncommented(SourcePin.MethodBody(runtime, "private void SyncPageButtonRows()"), "_pageButtons.Remove(key);");
    }
}
