using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Progenitor.Ui;

/// <summary>
/// One colored run of a code area's text (<see cref="IUiCodeArea"/>): <see cref="Length"/> characters from
/// <see cref="Start"/> (offsets into <see cref="IUiCodeArea.Value"/>), drawn in <see cref="Color"/>.
/// A span that reaches past the end of the text is cut at the end; one that starts past it is dropped.
/// Where spans overlap, the later span in the list wins for the characters they share.
/// </summary>
public readonly struct UiTextSpan : IEquatable<UiTextSpan>
{
    /// <summary>
    /// A run of <paramref name="length"/> characters from <paramref name="start"/> in <paramref name="color"/>,
    /// <c>#RRGGBB</c> or <c>#RRGGBBAA</c> (the leading <c>#</c> is optional).
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="color"/> is not a hex color.</exception>
    public UiTextSpan(int start, int length, string color)
    {
        if (!UiColor.TryParseHex(color, alpha: true, out var parsed))
            throw new ArgumentException("color must be #RRGGBB or #RRGGBBAA", nameof(color));
        Start = start;
        Length = length;
        Rgba = parsed;
    }

    /// <summary>The first character's offset in the text.</summary>
    public int Start { get; }

    /// <summary>How many characters the run covers.</summary>
    public int Length { get; }

    /// <summary>The run's color as uppercase <c>#RRGGBB</c>, or <c>#RRGGBBAA</c> when it is not opaque.</summary>
    public string Color => Rgba.A >= 0.9999f ? Rgba.ToHex(alpha: false) : Rgba.ToHex(alpha: true);

    internal UiColor Rgba { get; }

    /// <summary>The offset just past the run.</summary>
    public int End => Start + Length;

    public bool Equals(UiTextSpan other) => Start == other.Start && Length == other.Length && Rgba.Equals(other.Rgba);

    public override bool Equals(object? obj) => obj is UiTextSpan other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Start, Length, Rgba);

    public override string ToString() => Start.ToString(CultureInfo.InvariantCulture) + "+" + Length.ToString(CultureInfo.InvariantCulture) + " " + Color;

    internal UiTextSpan(int start, int length, UiColor color)
    {
        Start = start;
        Length = length;
        Rgba = color;
    }
}

/// <summary>An error marker on one line of a code area: <see cref="Line"/> is 1-based.</summary>
public readonly struct UiLineError : IEquatable<UiLineError>
{
    public UiLineError(int line, string message)
    {
        Line = line;
        Message = message ?? "";
    }

    /// <summary>The line, counting from 1.</summary>
    public int Line { get; }

    /// <summary>What is wrong (the hover box's body, and the summary line under the area).</summary>
    public string Message { get; }

    public bool Equals(UiLineError other) => Line == other.Line && string.Equals(Message, other.Message, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is UiLineError other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Line, Message);

    public override string ToString() => "Line " + Line.ToString(CultureInfo.InvariantCulture) + ": " + Message;
}

/// <summary>
/// P7 code area: the game-free half — lines, the gutter, the colored markup, the error list and the
/// geometry the runtime places them with. The runtime and the tests read the same numbers.
///
/// <para><b>Lines.</b> A line ends at <c>\n</c> or at <c>\r\n</c> (one break). Text the area is given is
/// normalized to <c>\n</c> first (<see cref="Normalize"/>), so the field, the colored overlay and the gutter
/// all count the same lines. Word wrap is off: one text line is one gutter line.</para>
///
/// <para><b>Spans.</b> Offsets into the (normalized) value. A span is cut to the text; an empty span or one
/// starting at or past the end is dropped. Overlaps: <b>later wins</b> — each character takes the color of
/// the last span in the list that covers it, so a caller can lay a broad span (a comment) and refine
/// inside it with later ones.</para>
///
/// <para><b>Escaping.</b> The overlay is a rich-text label: every <c>&lt;</c> of the user's text is escaped
/// with <see cref="UiRichText.Literal"/>, so typed text never becomes markup; only the area's own
/// <c>color</c> tags are live.</para>
/// </summary>
internal static class UiCodeAreaModel
{
    /// <summary>The fewest and most lines an area may be asked to show.</summary>
    internal const int MinLines = 2;

    internal const int MaxLines = 40;

    /// <summary>The cell key of the area's input cell, and of the summary row's text cell.</summary>
    internal const string CellKey = "code";

    internal const string SummaryCellKey = "summary";

    /// <summary>The error-marker column at the gutter's left (the red dot).</summary>
    internal const float MarkerWidth = 14f;

    /// <summary>Air between the line numbers and the separator line.</summary>
    internal const float NumberGap = 6f;

    /// <summary>Air between the separator line and the code.</summary>
    internal const float TextGap = 6f;

    /// <summary>The line numbers never get narrower than this many digits, so the code does not shift at line 10.</summary>
    internal const int MinDigits = 2;

    /// <summary>The digit width used when the runtime cannot measure the font (18 px game font).</summary>
    internal const float FallbackDigitAdvance = 10f;

    /// <summary>The marker glyphs, best first; the runtime takes the first its font draws.</summary>
    internal static readonly string[] MarkerGlyphs = { "●", "•", "!" };

    /// <summary>The faint band behind an error line: the palette's red at this alpha.</summary>
    internal const float BandAlpha = 0.14f;

    /// <summary>The summary row under the area (the mock's "(!) Line 4: …").</summary>
    internal const float SummaryRowHeight = 24f;

    internal const float SummaryFontSize = 15f;

    internal static int ClampLines(int lines) => lines < MinLines ? MinLines : lines > MaxLines ? MaxLines : lines;

    /// <summary>
    /// The field's height for <paramref name="lines"/> lines: the editor's line pitch plus the field's
    /// padding — the inverse of <see cref="UiTextExpandLayout.LinesFor"/>.
    /// </summary>
    internal static float HeightFor(int lines) => ClampLines(lines) * UiTextExpandLayout.LinePitch + 12f;

    /// <summary><c>\r\n</c> and a lone <c>\r</c> become <c>\n</c>; null becomes empty.</summary>
    internal static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        if (text!.IndexOf('\r') < 0) return text;
        return text.Replace("\r\n", "\n").Replace('\r', '\n');
    }

    /// <summary>How many lines <paramref name="text"/> has (at least 1; a trailing break opens an empty last line).</summary>
    internal static int CountLines(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 1;
        int n = 1;
        for (int i = 0; i < text!.Length; i++)
        {
            char c = text[i];
            if (c == '\n') n++;
            else if (c == '\r')
            {
                n++;
                if (i + 1 < text.Length && text[i + 1] == '\n') i++;
            }
        }

        return n;
    }

    /// <summary>The offset each line starts at (index 0 is line 1).</summary>
    internal static List<int> LineStarts(string? text)
    {
        var starts = new List<int> { 0 };
        if (string.IsNullOrEmpty(text)) return starts;
        for (int i = 0; i < text!.Length; i++)
        {
            char c = text[i];
            if (c == '\n') starts.Add(i + 1);
            else if (c == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                starts.Add(i + 1);
            }
        }

        return starts;
    }

    /// <summary>The 1-based line that holds character <paramref name="offset"/> (clamped to the text).</summary>
    internal static int LineOfOffset(IReadOnlyList<int> starts, int offset)
    {
        if (starts == null || starts.Count == 0) return 1;
        int lo = 0, hi = starts.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (starts[mid] <= offset) lo = mid;
            else hi = mid - 1;
        }

        return lo + 1;
    }

    /// <summary>The gutter's numbers, one per line: <c>1\n2\n…</c>.</summary>
    internal static string GutterText(int lineCount)
    {
        lineCount = Math.Max(1, lineCount);
        var sb = new StringBuilder(lineCount * 4);
        for (int i = 1; i <= lineCount; i++)
        {
            if (i > 1) sb.Append('\n');
            sb.Append(i.ToString(CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }

    /// <summary>The marker column: <paramref name="glyph"/> on each error line, nothing on the others.</summary>
    internal static string MarkerText(int lineCount, IReadOnlyList<UiLineError> errors, string glyph)
    {
        lineCount = Math.Max(1, lineCount);
        var marked = new HashSet<int>();
        if (errors != null)
            foreach (var e in errors) marked.Add(e.Line);
        var sb = new StringBuilder(lineCount * 2);
        for (int i = 1; i <= lineCount; i++)
        {
            if (i > 1) sb.Append('\n');
            if (marked.Contains(i)) sb.Append(glyph ?? "");
        }

        return sb.ToString();
    }

    /// <summary>Digits the numbers take: the last line's, at least <see cref="MinDigits"/>.</summary>
    internal static int Digits(int lineCount)
    {
        int n = Math.Max(1, lineCount);
        int d = 1;
        while (n >= 10)
        {
            n /= 10;
            d++;
        }

        return Math.Max(MinDigits, d);
    }

    /// <summary>
    /// The gutter's width: the marker column, the numbers (<see cref="Digits"/> × <paramref name="digitAdvance"/>),
    /// the gap to the separator and the gap to the code.
    /// </summary>
    internal static float GutterWidth(int lineCount, float digitAdvance)
    {
        if (float.IsNaN(digitAdvance) || float.IsInfinity(digitAdvance) || digitAdvance <= 0f) digitAdvance = FallbackDigitAdvance;
        return (float)Math.Ceiling(MarkerWidth + Digits(lineCount) * digitAdvance + NumberGap + TextGap);
    }

    /// <summary>The separator line's x inside the gutter (from its left edge).</summary>
    internal static float SeparatorX(float gutterWidth) => Math.Max(0f, gutterWidth - TextGap * 0.5f);

    /// <summary>
    /// The top of <paramref name="line"/> (1-based) in the text's own space (y up), given the top of the first
    /// line and the line pitch: each line sits one pitch below the one above.
    /// </summary>
    internal static float LineTop(int line, float firstTop, float pitch)
    {
        if (pitch <= 0f || float.IsNaN(pitch)) pitch = UiTextExpandLayout.LinePitch;
        return firstTop - (Math.Max(1, line) - 1) * pitch;
    }

    /// <summary>The 1-based line at <paramref name="y"/> (same space as <see cref="LineTop"/>), or 0 outside the lines.</summary>
    internal static int LineAt(float y, float firstTop, float pitch, int lineCount)
    {
        if (pitch <= 0f || float.IsNaN(pitch)) pitch = UiTextExpandLayout.LinePitch;
        float down = firstTop - y;
        if (down < 0f || float.IsNaN(down)) return 0;
        int line = (int)Math.Floor(down / pitch) + 1;
        return line > Math.Max(1, lineCount) ? 0 : line;
    }

    /// <summary>
    /// The spans as drawn: cut to <paramref name="textLength"/>, empty ones dropped, overlaps resolved
    /// (later wins), adjacent runs of one color merged; sorted, never overlapping.
    /// </summary>
    internal static List<UiTextSpan> NormalizeSpans(IReadOnlyList<UiTextSpan>? spans, int textLength)
    {
        var result = new List<UiTextSpan>();
        if (spans == null || spans.Count == 0 || textLength <= 0) return result;
        // Which span colors each character: -1 none, else the span's index (a later write wins).
        var owner = new int[textLength];
        for (int i = 0; i < owner.Length; i++) owner[i] = -1;
        bool any = false;
        for (int s = 0; s < spans.Count; s++)
        {
            var span = spans[s];
            long start = Math.Max(0, span.Start);
            long end = Math.Min((long)textLength, (long)span.Start + span.Length);
            if (span.Length <= 0 || start >= textLength || end <= start) continue;
            for (long i = start; i < end; i++) owner[i] = s;
            any = true;
        }

        if (!any) return result;
        int at = 0;
        while (at < textLength)
        {
            int idx = owner[at];
            if (idx < 0)
            {
                at++;
                continue;
            }

            var color = spans[idx].Rgba;
            int runStart = at;
            while (at < textLength && owner[at] >= 0 && spans[owner[at]].Rgba.Equals(color)) at++;
            result.Add(new UiTextSpan(runStart, at - runStart, color));
        }

        return result;
    }

    /// <summary>
    /// The overlay's rich text: <paramref name="text"/> with every <c>&lt;</c> escaped and each span wrapped in a
    /// <c>color</c> tag. The characters (and so the layout) are the field's own.
    /// </summary>
    internal static string Markup(string? text, IReadOnlyList<UiTextSpan>? spans)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var runs = NormalizeSpans(spans, text!.Length);
        if (runs.Count == 0) return UiRichText.Literal(text);
        var sb = new StringBuilder(text.Length + runs.Count * 24);
        int at = 0;
        foreach (var run in runs)
        {
            if (run.Start > at) sb.Append(UiRichText.Literal(text.Substring(at, run.Start - at)));
            sb.Append("<color=").Append(run.Rgba.ToHex(alpha: true)).Append('>');
            sb.Append(UiRichText.Literal(text.Substring(run.Start, run.Length)));
            sb.Append("</color>");
            at = run.End;
        }

        if (at < text.Length) sb.Append(UiRichText.Literal(text.Substring(at)));
        return sb.ToString();
    }

    /// <summary>
    /// The errors as shown: lines below 1 dropped, lines past the end moved to the last line, sorted by line,
    /// one entry per line (messages on one line joined with a line break, exact repeats dropped).
    /// </summary>
    internal static List<UiLineError> NormalizeErrors(IReadOnlyList<UiLineError>? errors, int lineCount)
    {
        var result = new List<UiLineError>();
        if (errors == null || errors.Count == 0) return result;
        lineCount = Math.Max(1, lineCount);
        var byLine = new SortedDictionary<int, List<string>>();
        foreach (var e in errors)
        {
            if (e.Line < 1) continue;
            int line = Math.Min(e.Line, lineCount);
            if (!byLine.TryGetValue(line, out var messages)) byLine[line] = messages = new List<string>();
            string message = (e.Message ?? "").Trim();
            if (!messages.Contains(message)) messages.Add(message);
        }

        foreach (var pair in byLine)
        {
            var parts = pair.Value.FindAll(m => m.Length > 0);
            result.Add(new UiLineError(pair.Key, string.Join("\n", parts)));
        }

        return result;
    }

    /// <summary>The summary line under the area: <c>(!) Line 4: message</c>, <c>(+2 more)</c> when there are others; empty for none.</summary>
    internal static string Summary(IReadOnlyList<UiLineError>? errors)
    {
        if (errors == null || errors.Count == 0) return "";
        var first = errors[0];
        string message = first.Message ?? "";
        int nl = message.IndexOf('\n');
        if (nl >= 0) message = message.Substring(0, nl);
        string text = "(!) Line " + first.Line.ToString(CultureInfo.InvariantCulture) + (message.Length > 0 ? ": " + message : "");
        if (errors.Count > 1) text += " (+" + (errors.Count - 1).ToString(CultureInfo.InvariantCulture) + " more)";
        return text;
    }

    /// <summary>The hover box's title for an error line.</summary>
    internal static string TooltipTitle(int line) => "Line " + line.ToString(CultureInfo.InvariantCulture);
}
