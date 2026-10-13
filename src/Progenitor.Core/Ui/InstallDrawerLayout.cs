using System;
using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>
/// The Install drawer's geometry, game-free. INSTALLSIDES2 item 4 (option A, user-approved
/// 2026-10-03): the drawer is a compact, fixed panel with no scrolling of its own — the facts in two
/// columns and a short description — and <c>Read full description</c> opens the whole README in its
/// own reader window. Its body is as tall as its (already clamped) text, at most
/// <see cref="CompactMaxBody"/>, and always leaves <see cref="NextRowPeek"/> of the next listing in
/// view. The mouse wheel over it scrolls the list. (INSTALLSIDES1's 80%-of-viewport sizing,
/// <see cref="BodyHeight"/>, is kept for its readers but no longer used by the drawer.)
/// </summary>
internal static class InstallDrawerLayout
{
    /// <summary>The share of the list viewport the open row and its drawer take together.</summary>
    public const float BlockShare = 0.8f;

    /// <summary>The body is never shorter than this, so even a cramped viewport shows a few lines.</summary>
    public const float MinBodyHeight = 96f;

    /// <summary>How much of the next row must stay visible under the drawer.</summary>
    public const float NextRowPeek = 40f;

    /// <summary>The drawer head row (the Collapse button and the install fact).</summary>
    public const float HeadHeight = 44f;

    /// <summary>
    /// Where the drawer's own content starts: past the expand button and the icon, under the mod's
    /// name — the indent that says the drawer belongs to the row above it.
    /// </summary>
    public const float Indent = InstallColumns.ExpandWidth + CellLayout.Gap + CellLayout.ImageSize + CellLayout.Gap;

    /// <summary>The seam joining the drawer to its row: one pixel of the accent color, this opaque.</summary>
    public const float SeamAlpha = 0.45f;

    /// <inheritdoc cref="SeamAlpha"/>
    public const float SeamHeight = 1f;

    /// <summary>The body's faint shade over the selected fill, so the scrolling area reads as a well.</summary>
    public const float BodyShadeAlpha = 0.18f;

    /// <summary>The list viewport assumed before the workspace has measured one (live 3840×1080: 708 px).</summary>
    public const float FallbackViewport = 708f;

    /// <summary>INSTALLSIDES2 item 4: the compact body's ceiling.</summary>
    public const float CompactMaxBody = 280f;

    /// <summary>INSTALLSIDES2 item 4: the compact body's floor (one or two lines).</summary>
    public const float CompactMinBody = 48f;

    /// <summary>INSTALLSIDES2 item 4: the short description's length (about three lines at the drawer's width).</summary>
    public const int ExcerptChars = 300;

    /// <summary>
    /// INSTALLSIDES2 item 4: the compact body's height — what its text needs, at most
    /// <see cref="CompactMaxBody"/>, at most what keeps <see cref="NextRowPeek"/> of the next listing
    /// visible under the open row and head, at least <see cref="CompactMinBody"/>.
    /// </summary>
    public static float CompactBodyHeight(float viewport, float rowHeight, float contentHeight,
        float headHeight = HeadHeight, float gap = PanelLayout.RowGap)
    {
        if (float.IsNaN(viewport) || float.IsInfinity(viewport) || viewport <= 0f) viewport = FallbackViewport;
        if (float.IsNaN(rowHeight) || rowHeight < 0f) rowHeight = 0f;
        if (float.IsNaN(contentHeight) || float.IsInfinity(contentHeight) || contentHeight < 0f) contentHeight = 0f;
        float chrome = rowHeight + headHeight + 2f * gap;
        float body = Math.Min(contentHeight, CompactMaxBody);
        body = Math.Min(body, viewport - chrome - NextRowPeek);
        if (body < CompactMinBody) body = CompactMinBody;
        return (float)Math.Round(body);
    }

    /// <summary>
    /// INSTALLSIDES2 item 4: <paramref name="text"/> cut to about <paramref name="max"/> characters at
    /// a word boundary, with an ellipsis when anything was cut.
    /// </summary>
    public static string Excerpt(string? text, int max = ExcerptChars)
    {
        string t = (text ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        while (t.Contains("  ")) t = t.Replace("  ", " ");
        if (t.Length <= max) return t;
        int cut = t.LastIndexOf(' ', Math.Max(0, max - 1));
        if (cut < max / 2) cut = max;
        return t.Substring(0, cut).TrimEnd(' ', ',', ';', ':', '.', '-') + "\u2026";
    }

    /// <summary>
    /// The body's height for a list viewport <paramref name="viewport"/> px tall: the block share
    /// minus the row, the head and the two row gaps, at most what keeps <see cref="NextRowPeek"/> of
    /// the next listing visible, at least <see cref="MinBodyHeight"/>; and when the text needs less
    /// (<paramref name="contentHeight"/> &gt; 0), only that.
    /// </summary>
    public static float BodyHeight(float viewport, float rowHeight, float contentHeight = 0f,
        float headHeight = HeadHeight, float gap = PanelLayout.RowGap)
    {
        if (float.IsNaN(viewport) || float.IsInfinity(viewport) || viewport <= 0f) viewport = FallbackViewport;
        if (float.IsNaN(rowHeight) || rowHeight < 0f) rowHeight = 0f;
        float chrome = rowHeight + headHeight + 2f * gap;
        float target = viewport * BlockShare - chrome;
        float ceiling = viewport - chrome - NextRowPeek;
        float body = Math.Min(target, ceiling);
        if (contentHeight > 0f && !float.IsNaN(contentHeight) && !float.IsInfinity(contentHeight))
            body = Math.Min(body, contentHeight);
        if (body < MinBodyHeight) body = MinBodyHeight;
        return (float)Math.Round(body);
    }

    /// <summary>The whole open block (row, head, body and the gaps between them).</summary>
    public static float BlockHeight(float bodyHeight, float rowHeight, float headHeight = HeadHeight, float gap = PanelLayout.RowGap) =>
        rowHeight + gap + headHeight + gap + bodyHeight;

    /// <summary>
    /// The text's height estimate at <paramref name="width"/>, from the kit's glyph estimate: the
    /// wrapped lines of each entry at its size, the line gaps and the pads. The runtime measures the
    /// real text; this only decides how short a sparse drawer may be.
    /// </summary>
    public static float EstimateContentHeight(IReadOnlyList<CellLine> lines, float width)
    {
        if (lines == null || lines.Count == 0) return 0f;
        float textWidth = Math.Max(40f, width - 2f * CellLayout.ScrollPad - CellLayout.ScrollBarWidth - 4f);
        float total = 2f * CellLayout.ScrollPad;
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            string text = line.Kind == CellLineKind.Fact ? line.Label + "  " + line.Text : line.Text;
            // A two-fact line: each fact has half the width; the line is as tall as the taller one.
            float width2 = line.HasSecond ? textWidth * CellLayout.FactSecondColumnPercent / 100f : textWidth;
            int count = 0;
            foreach (string part in (text ?? "").Replace("\r", "").Split('\n'))
                count += Math.Max(1, MenuPanelLayout.WrappedLines(part, width2, line.FontSize, 400));
            if (line.HasSecond)
                count = Math.Max(count, Math.Max(1, MenuPanelLayout.WrappedLines(line.Label2 + "  " + line.Text2, width2, line.FontSize, 400)));
            total += count * line.FontSize * 1.3f;
            if (i > 0) total += CellLayout.ScrollLineGap;
        }

        return total;
    }
}
