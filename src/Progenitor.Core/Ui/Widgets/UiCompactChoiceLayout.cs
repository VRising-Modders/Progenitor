using System;
using System.Collections.Generic;
using Progenitor.Ui.GameArt;

namespace Progenitor.Ui;

/// <summary>
/// HUB1: the geometry of compact dropdowns (<see cref="UiControlsHub.CompactDropdown"/>): each is its caption followed by
/// the game's dropdown bar sized to its widest option, and the compact dropdowns of one inline block share a row, left to
/// right, wrapping to a further line only when the row is full. Game-free: <c>UiHubControlsTests</c> pins it, and the
/// Unity runtime places the items from <see cref="Place"/>.
/// </summary>
internal static class UiCompactChoiceLayout
{
    /// <summary>The caption's size: the kit row label's.</summary>
    internal const float LabelFontSize = 18f;

    /// <summary>The value's size on the game's dropdown bar.</summary>
    internal const float ValueFontSize = GameSettingsLook.DropdownText;

    /// <summary>Between a caption and its bar.</summary>
    internal const float LabelGap = 10f;

    /// <summary>Between one compact dropdown and the next on a line.</summary>
    internal const float ItemGap = 24f;

    /// <summary>Between two lines of a wrapped row.</summary>
    internal const float LineGap = 6f;

    /// <summary>One line's height (a kit row).</summary>
    internal const float LineHeight = UnityUiRuntimeMetrics.RowHeight;

    /// <summary>The bar's height: the settings grid's control height, centered on its line.</summary>
    internal const float BarHeight = PageButtonLayout.Height;

    /// <summary>The narrowest bar, so a one-letter option still reads as a dropdown.</summary>
    internal const float MinBarWidth = 72f;

    /// <summary>The widest bar; a longer option is shortened with an ellipsis on the bar (the open list shows it whole).</summary>
    internal const float MaxBarWidth = 320f;

    /// <summary>The longest caption before it is shortened with an ellipsis.</summary>
    internal const float MaxLabelWidth = 220f;

    /// <summary>The room the bar keeps left of its value and right of it for the arrow (the game's dropdown art).</summary>
    internal static float BarChrome => GameSettingsLook.Kit(GameSettingsLook.DropdownCaptionLeft) + UiControlDraw.ChoiceArrowWidth + 4f;

    /// <summary>One glyph of slack on a measured caption (the Install chips' rule: a capitalized word is never cut).</summary>
    internal static float Slack(float fontSize) => fontSize * MenuPanelLayout.AverageGlyphEm;

    /// <summary>A caption's width; 0 for an empty caption (the bar then starts the item).</summary>
    internal static float LabelWidth(string? caption)
    {
        if (string.IsNullOrWhiteSpace(caption)) return 0f;
        return Math.Min(MaxLabelWidth, (float)Math.Ceiling(UiChipsText.Width(caption, LabelFontSize) + Slack(LabelFontSize)));
    }

    /// <summary>The bar's width: the widest of <paramref name="labels"/> plus the bar's chrome, within <see cref="MinBarWidth"/>..<see cref="MaxBarWidth"/>.</summary>
    internal static float BarWidth(IEnumerable<string?>? labels)
    {
        float widest = 0f;
        if (labels != null)
            foreach (var label in labels)
                widest = Math.Max(widest, UiChipsText.Width(label ?? "", ValueFontSize));
        float w = (float)Math.Ceiling(widest + Slack(ValueFontSize) + BarChrome);
        return Math.Max(MinBarWidth, Math.Min(MaxBarWidth, w));
    }

    /// <summary>An item's whole width: caption, gap and bar (no gap without a caption).</summary>
    internal static float ItemWidth(float labelWidth, float barWidth) =>
        (labelWidth > 0f ? labelWidth + LabelGap : 0f) + barWidth;

    /// <summary>
    /// Where each item goes on a row <paramref name="rowWidth"/> wide: its left edge and line (0 first). Items keep their
    /// order; an item that would cross the row's right edge starts the next line (the first item of a line always stays
    /// on it, shortened by the row's clip when it alone is wider).
    /// </summary>
    internal static List<(float Left, int Line)> Place(float rowWidth, IReadOnlyList<(float Label, float Bar)> items)
    {
        var places = new List<(float, int)>(items?.Count ?? 0);
        if (items == null) return places;
        float row = float.IsNaN(rowWidth) || rowWidth < 1f ? UnityUiRuntimeMetrics.RowWidth : rowWidth;
        float x = 0f;
        int line = 0;
        for (int i = 0; i < items.Count; i++)
        {
            float w = ItemWidth(items[i].Label, items[i].Bar);
            if (x > 0f && x + w > row + 0.01f)
            {
                line++;
                x = 0f;
            }

            places.Add((x, line));
            x += w + ItemGap;
        }

        return places;
    }

    /// <summary>How many lines <see cref="Place"/> uses (1 for an empty row).</summary>
    internal static int Lines(IReadOnlyList<(float Left, int Line)> places)
    {
        int lines = 1;
        foreach (var p in places) lines = Math.Max(lines, p.Line + 1);
        return lines;
    }

    /// <summary>A row of <paramref name="lines"/> lines' height.</summary>
    internal static float RowHeight(int lines) =>
        Math.Max(1, lines) * LineHeight + (Math.Max(1, lines) - 1) * LineGap;

    /// <summary>A line's top edge from the row's top.</summary>
    internal static float LineTop(int line) => Math.Max(0, line) * (LineHeight + LineGap);
}
