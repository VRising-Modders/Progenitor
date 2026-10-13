using System;
using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>Horizontal text alignment, as far as the owned caret needs to know it.</summary>
internal enum UiTextHAlign
{
    Left = 0,
    Center = 1,
    Right = 2,
}

/// <summary>Vertical text alignment, as far as the owned caret needs to know it.</summary>
internal enum UiTextVAlign
{
    Top = 0,
    Middle = 1,
    Bottom = 2,
}

/// <summary>Where the insertion point sits in a laid-out field.</summary>
internal readonly struct UiCaretSpot
{
    public UiCaretSpot(int line, int column, int lineCount, float x, float centerYDown, bool beyondView = false)
    {
        Line = line;
        Column = column;
        LineCount = lineCount;
        X = x;
        CenterYDown = centerYDown;
        BeyondView = beyondView;
    }

    /// <summary>
    /// The caret is on a line below what a top-aligned field can show. The field does not scroll,
    /// so the bar is clamped to the field's bottom edge; <see cref="Line"/> and <see cref="X"/>
    /// then describe the first hidden line, not the caret's real column.
    /// </summary>
    public bool BeyondView { get; }

    /// <summary>Visual line of the caret, 0 first. Counts both line breaks and word-wrap breaks.</summary>
    public int Line { get; }

    /// <summary>Characters between the start of that visual line and the caret.</summary>
    public int Column { get; }

    /// <summary>
    /// Visual lines laid out. For top-aligned text the layout stops at the caret's line, so this is
    /// <c>Line + 1</c> there; middle and bottom alignment lay out the whole text.
    /// </summary>
    public int LineCount { get; }

    /// <summary>Caret x from the left edge of the text's inner rect (rect minus margins).</summary>
    public float X { get; }

    /// <summary>Caret center, y-down, from the top edge of the text's inner rect.</summary>
    public float CenterYDown { get; }
}

/// <summary>
/// Game-free geometry for the owned caret (TEXTAREA1). The caret used to be placed at the text
/// rect's vertical center and at the width of the whole prefix, which is right only for one
/// left-aligned, middle-aligned line with no margins. A top-aligned text area put the caret on
/// the rect's middle line ("adsf" on line 1, the bar on line 2), Enter left it at the end of the
/// previous line, and a centered number box or a field with a text margin put it beside the text.
///
/// <para>
/// The rule here: split the text into visual lines (each <c>\n</c>, plus TMP-style greedy word
/// wrapping when the field wraps), find the line holding the caret, then take x from that line's
/// own prefix width and alignment, and y from the line index, the line advance and the vertical
/// alignment. Widths come from a measuring callback, so the Unity side supplies TMP's own
/// measurement and the tests supply a fixed-pitch one.
/// </para>
///
/// <para>
/// Wrapping follows TMP's documented behavior, not its code: a line breaks after the last space
/// that keeps the line's visible text inside the width, trailing spaces hang past the edge, and a
/// single word wider than the line breaks between characters. A caret at a soft-wrap boundary is
/// drawn at the start of the next line. When TMP breaks differently (for example on a
/// non-breaking character class this rule does not know), the readback's <c>tmpLineCount</c>
/// disagrees with <c>lineCount</c>.
/// </para>
/// </summary>
internal static class UiCaretLayout
{
    /// <summary>
    /// Start index of each visual line, up to and including the line that holds
    /// <paramref name="stopAt"/> (or every line when <paramref name="stopAt"/> is negative).
    /// </summary>
    internal static List<int> LineStarts(
        string? text, bool wrap, float width, Func<string, float> measure, int stopAt = -1,
        int maxLines = int.MaxValue)
    {
        if (measure == null) throw new ArgumentNullException(nameof(measure));
        string s = text ?? "";
        var starts = new List<int> { 0 };
        int paragraphStart = 0;
        while (true)
        {
            int newline = s.IndexOf('\n', paragraphStart);
            int paragraphEnd = newline < 0 ? s.Length : newline;
            if (wrap && width > 0f && paragraphEnd > paragraphStart)
                WrapParagraph(s, paragraphStart, paragraphEnd, width, measure, starts, stopAt, maxLines);
            if (starts.Count > maxLines) return starts;
            // The caret's paragraph is laid out: later lines cannot move it.
            if (stopAt >= 0 && (newline < 0 || stopAt <= newline))
                return starts;
            if (newline < 0) return starts;
            paragraphStart = newline + 1;
            starts.Add(paragraphStart);
        }
    }

    private static void WrapParagraph(
        string s, int start, int end, float width, Func<string, float> measure, List<int> starts, int stopAt,
        int maxLines)
    {
        int lineStart = start;
        while (lineStart < end)
        {
            if (stopAt >= 0 && lineStart > stopAt) return;
            if (starts.Count > maxLines) return;

            // Largest word boundary whose visible text (trailing spaces dropped) still fits. The
            // paragraph's end counts as a boundary: reaching it means the rest fits on this line.
            int best = -1;
            for (int j = lineStart + 1; j <= end; j++)
            {
                if (j < end && (s[j] == ' ' || s[j - 1] != ' '))
                {
                    // Inside a long word: once a prefix is already too wide, no later boundary
                    // can fit, so stop scanning (checked at power-of-two lengths only).
                    int span = j - lineStart;
                    if (span >= ProbeChunk && (span & (span - 1)) == 0 && s[j - 1] != ' '
                        && measure(s.Substring(lineStart, span)) > width)
                        break;
                    continue;
                }
                int visible = j;
                while (visible > lineStart && s[visible - 1] == ' ') visible--;
                if (Exceeds(s, lineStart, visible - lineStart, width, measure)) break;
                if (j == end) return;
                best = j;
            }

            if (best < 0)
            {
                // The first word alone is wider than the line: break between characters,
                // always taking at least one so the loop advances.
                best = lineStart + 1;
                for (int k = lineStart + 2; k <= end; k++)
                {
                    if (measure(s.Substring(lineStart, k - lineStart)) > width) break;
                    best = k;
                }
                if (best >= end) return;
            }

            starts.Add(best);
            lineStart = best;
        }
    }

    /// <summary>Length below which a span is measured in one call.</summary>
    private const int ProbeChunk = 64;

    /// <summary>
    /// Whether <paramref name="length"/> characters from <paramref name="start"/> are wider than
    /// <paramref name="width"/>. A long span is probed by doubling prefixes first: once a prefix is
    /// already too wide the whole span is, so a 4,000-character word costs a few short measures
    /// per line instead of one measure of everything that is left (which made a long paste
    /// quadratic).
    /// </summary>
    private static bool Exceeds(string s, int start, int length, float width, Func<string, float> measure)
    {
        if (length <= 0) return false;
        for (int probe = ProbeChunk; probe < length; probe *= 2)
        {
            if (measure(s.Substring(start, probe)) > width) return true;
        }
        return measure(s.Substring(start, length)) > width;
    }

    /// <summary>
    /// Locate <paramref name="caret"/> in <paramref name="text"/> laid out in an inner rect of
    /// <paramref name="innerWidth"/> × <paramref name="innerHeight"/>.
    /// <paramref name="lineAdvance"/> is the distance between two baselines.
    /// </summary>
    internal static UiCaretSpot Locate(
        string? text,
        int caret,
        bool wrap,
        float innerWidth,
        float innerHeight,
        float lineAdvance,
        UiTextHAlign horizontal,
        UiTextVAlign vertical,
        Func<string, float> measure)
    {
        if (measure == null) throw new ArgumentNullException(nameof(measure));
        string s = text ?? "";
        if (caret < 0) caret = 0;
        if (caret > s.Length) caret = s.Length;
        if (lineAdvance <= 0f) lineAdvance = 1f;

        bool whole = vertical != UiTextVAlign.Top;
        // Top-aligned text is laid out only as far as the field can show (plus one line): a caret
        // further down is out of view whatever its column, and a huge paste must not cost a
        // full layout per keystroke.
        int maxLines = int.MaxValue;
        if (!whole && innerHeight > 0f)
            maxLines = (int)Math.Min(int.MaxValue - 2, Math.Ceiling(innerHeight / lineAdvance)) + 1;
        var starts = LineStarts(s, wrap, innerWidth, measure, whole ? -1 : caret, maxLines);

        int line = 0;
        for (int i = starts.Count - 1; i >= 0; i--)
        {
            if (starts[i] <= caret) { line = i; break; }
        }

        if (starts.Count > maxLines && line >= maxLines)
        {
            // Below the last visible line: report the first hidden line at its left edge.
            float hiddenY = (maxLines + 0.5f) * lineAdvance;
            return new UiCaretSpot(maxLines, 0, maxLines + 1, 0f, hiddenY, beyondView: true);
        }

        int lineStart = starts[line];
        int lineEnd = line + 1 < starts.Count ? starts[line + 1] : NextBreak(s, lineStart);
        if (lineEnd > lineStart && lineEnd <= s.Length && s[lineEnd - 1] == '\n') lineEnd--;
        if (lineEnd < lineStart) lineEnd = lineStart;
        int column = caret - lineStart;

        float prefix = column > 0 ? measure(s.Substring(lineStart, column)) : 0f;
        float x = prefix;
        if (horizontal != UiTextHAlign.Left)
        {
            float lineWidth = lineEnd > lineStart ? measure(s.Substring(lineStart, lineEnd - lineStart).TrimEnd(' ')) : 0f;
            float slack = innerWidth - lineWidth;
            x = horizontal == UiTextHAlign.Center ? slack * 0.5f + prefix : slack + prefix;
        }

        int lineCount = whole ? starts.Count : line + 1;
        float block = lineCount * lineAdvance;
        float top = vertical switch
        {
            UiTextVAlign.Middle => (innerHeight - block) * 0.5f,
            UiTextVAlign.Bottom => innerHeight - block,
            _ => 0f,
        };
        float centerY = top + (line + 0.5f) * lineAdvance;
        return new UiCaretSpot(line, column, lineCount, x, centerY);
    }

    private static int NextBreak(string s, int from)
    {
        int newline = s.IndexOf('\n', from);
        return newline < 0 ? s.Length : newline;
    }

    /// <summary>
    /// TEXTAREA2: whether TMP's last layout (<paramref name="characterCount"/>,
    /// <c>textInfo.characterCount</c>) describes <paramref name="text"/>. Rich text is off on our
    /// fields, so TMP lays out one character per UTF-16 unit, and <c>TMP_InputField</c> may append
    /// one zero-width space. A surrogate pair (one glyph for two units) is not mapped: such text
    /// uses the measured layout instead.
    /// </summary>
    internal static bool TmpLayoutMatches(string? text, int characterCount, int lineCount)
    {
        string s = text ?? "";
        if (lineCount <= 0) return false;
        for (int i = 0; i < s.Length; i++)
            if (char.IsSurrogate(s[i])) return false;
        return characterCount == s.Length || characterCount == s.Length + 1;
    }

    /// <summary>
    /// TEXTAREA2: the caret's line among TMP's own laid-out lines (<paramref name="firstChar"/> /
    /// <paramref name="lastChar"/> per line, ascending, as <c>textInfo.lineInfo</c> gives them).
    /// The line whose first character is the last one at or before the caret, so a caret on a
    /// soft-wrap boundary or just after a line break is on the following line. Returns -1 when
    /// there are no lines.
    /// </summary>
    internal static int TmpLineOf(int caret, int lineCount, Func<int, int> firstChar)
    {
        if (lineCount <= 0 || firstChar == null) return -1;
        int lo = 0, hi = lineCount - 1, found = 0;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (firstChar(mid) <= caret) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return found;
    }

    /// <summary>
    /// TEXTAREA2: the scroll that keeps a caret bar visible: how far to move the text up (positive)
    /// or down (negative) so the bar from <paramref name="caretBottom"/> to <paramref name="caretTop"/>
    /// (y-up) fits between <paramref name="viewBottom"/> and <paramref name="viewTop"/>. 0 when it
    /// already fits; a bar taller than the view aligns its top.
    /// </summary>
    internal static float ScrollToShow(float caretBottom, float caretTop, float viewBottom, float viewTop)
    {
        if (caretTop > viewTop) return viewTop - caretTop;
        if (caretBottom < viewBottom) return viewBottom - caretBottom;
        return 0f;
    }

    /// <summary>
    /// The caret bar's height on a line: <see cref="UiOwnedCaret.HeightPx"/>, but never taller
    /// than one line advance, so the bar cannot reach into the line above or below.
    /// </summary>
    internal static float BarHeight(float lineAdvance)
    {
        float h = UiOwnedCaret.HeightPx;
        if (lineAdvance > 1f && lineAdvance < h) h = lineAdvance;
        return h;
    }
}
