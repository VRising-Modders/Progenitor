using System;
using System.Collections.Generic;
using System.Text;

namespace Progenitor.Install;

/// <summary>
/// One README/changelog row: a heading, paragraph line, list item, code line or table row.
/// Blank lines are not blocks.
/// </summary>
internal enum MarkdownBlockKind
{
    Heading = 1,
    Paragraph = 2,
    ListItem = 3,
    Code = 4,
    Table = 5,
}

/// <summary>Kind plus the already-inlined plain text for one listing-page row.</summary>
internal readonly struct MarkdownBlock
{
    public MarkdownBlock(MarkdownBlockKind kind, string text)
    {
        Kind = kind;
        Text = text ?? "";
    }

    public MarkdownBlockKind Kind { get; }

    public string Text { get; }
}

/// <summary>
/// Thunderstore README/changelog markdown to the listing page's plain text.
/// Headings become their own line; images, HTML tags and badges are dropped;
/// links keep the link text. Not a CommonMark implementation.
/// </summary>
internal static class MarkdownPlain
{
    public const int DefaultMaxChars = 6000;
    public const string Bullet = "• ";
    public const string CellJoin = " | ";
    public const string Ellipsis = "…";

    public static string Render(string? markdown, int maxChars = DefaultMaxChars)
    {
        var lines = CollectLines(markdown);
        var texts = new List<string>(lines.Count);
        for (int i = 0; i < lines.Count; i++)
            texts.Add(lines[i].Text);
        return Truncate(JoinBlocks(texts), maxChars);
    }

    /// <summary>
    /// One block per heading, paragraph line, list item, code line or table row.
    /// The character cap is applied across the stream (mid-block cut plus ellipsis).
    /// </summary>
    public static List<MarkdownBlock> RenderBlocks(string? markdown, int maxChars = DefaultMaxChars)
    {
        var lines = CollectLines(markdown);
        var blocks = new List<MarkdownBlock>(lines.Count);
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.Blank) continue;
            blocks.Add(new MarkdownBlock(line.Kind, line.Text));
        }

        return CapBlocks(blocks, maxChars);
    }

    internal static List<MarkdownBlock> CapBlocks(IReadOnlyList<MarkdownBlock> blocks, int maxChars)
    {
        var result = new List<MarkdownBlock>();
        if (blocks == null || blocks.Count == 0) return result;
        if (maxChars <= 0 || maxChars == int.MaxValue)
        {
            for (int i = 0; i < blocks.Count; i++)
                result.Add(blocks[i]);
            return result;
        }

        int used = 0;
        for (int i = 0; i < blocks.Count; i++)
        {
            if (used >= maxChars) break;
            string text = blocks[i].Text ?? "";
            int remaining = maxChars - used;
            if (text.Length <= remaining)
            {
                result.Add(blocks[i]);
                used += text.Length;
                continue;
            }

            result.Add(new MarkdownBlock(blocks[i].Kind, Truncate(text, remaining)));
            break;
        }

        return result;
    }

    internal static int ContentLength(IReadOnlyList<MarkdownBlock> blocks)
    {
        if (blocks == null || blocks.Count == 0) return 0;
        int n = 0;
        for (int i = 0; i < blocks.Count; i++)
            n += (blocks[i].Text ?? "").Length;
        return n;
    }

    internal static string Join(IReadOnlyList<MarkdownBlock> blocks)
    {
        if (blocks == null || blocks.Count == 0) return "";
        var texts = new List<string>(blocks.Count);
        for (int i = 0; i < blocks.Count; i++)
            texts.Add(blocks[i].Text ?? "");
        return JoinBlocks(texts);
    }

    /// <summary>
    /// Split a changelog into the latest entry (from the start through the first
    /// version boundary's body) and the rest. No second boundary means <paramref name="rest"/>
    /// is empty.
    /// </summary>
    public static void SplitLatest(string? markdown, out string latest, out string rest)
    {
        latest = markdown ?? "";
        rest = "";
        if (string.IsNullOrWhiteSpace(markdown)) return;

        string source = NormalizeNewlines(markdown);
        var lines = SplitLines(source);
        int first = -1;
        int second = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            if (!IsEntryBoundary(lines[i])) continue;
            if (first < 0) first = i;
            else
            {
                second = i;
                break;
            }
        }

        if (first < 0 || second < 0) return;
        latest = JoinRange(lines, 0, second).TrimEnd();
        rest = JoinRange(lines, second, lines.Length).TrimEnd();
    }

    public static string Truncate(string text, int maxChars)
    {
        if (string.IsNullOrEmpty(text) || maxChars <= 0) return text ?? "";
        if (text.Length <= maxChars) return text;
        return text.Substring(0, maxChars) + Ellipsis;
    }

    private readonly struct CollectedLine
    {
        public CollectedLine(MarkdownBlockKind kind, string text, bool blank)
        {
            Kind = kind;
            Text = text ?? "";
            Blank = blank;
        }

        public MarkdownBlockKind Kind { get; }
        public string Text { get; }
        public bool Blank { get; }
    }

    private static List<CollectedLine> CollectLines(string? markdown)
    {
        var output = new List<CollectedLine>();
        if (string.IsNullOrEmpty(markdown)) return output;
        string source = NormalizeNewlines(markdown);
        var lines = SplitLines(source);
        bool inFence = false;
        bool lastBlank = true;

        void Emit(MarkdownBlockKind kind, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            output.Add(new CollectedLine(kind, text, blank: false));
            lastBlank = false;
        }

        void EmitBlank()
        {
            if (output.Count == 0 || lastBlank) return;
            output.Add(new CollectedLine(MarkdownBlockKind.Paragraph, "", blank: true));
            lastBlank = true;
        }

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (inFence)
            {
                if (IsFence(line))
                {
                    inFence = false;
                    EmitBlank();
                    continue;
                }

                Emit(MarkdownBlockKind.Code, line);
                continue;
            }

            if (IsFence(line))
            {
                EmitBlank();
                inFence = true;
                continue;
            }

            if (IsTableSeparator(line)) continue;

            if (IsTableRow(line))
            {
                Emit(MarkdownBlockKind.Table, JoinTableRow(line));
                continue;
            }

            if (IsHeading(line, out var heading))
            {
                EmitBlank();
                Emit(MarkdownBlockKind.Heading, Inline(heading));
                EmitBlank();
                continue;
            }

            if (IsListItem(line, out var item))
            {
                Emit(MarkdownBlockKind.ListItem, Bullet + Inline(item));
                continue;
            }

            if (IsHorizontalRule(line))
            {
                EmitBlank();
                continue;
            }

            if (IsReferenceDefinition(line)) continue;

            if (string.IsNullOrWhiteSpace(line))
            {
                EmitBlank();
                continue;
            }

            string stripped = Inline(line);
            if (string.IsNullOrWhiteSpace(stripped)) continue;
            Emit(MarkdownBlockKind.Paragraph, stripped);
        }

        return output;
    }

    internal static string Inline(string line)
    {
        if (string.IsNullOrEmpty(line)) return "";
        var sb = new StringBuilder(line.Length);
        int i = 0;
        while (i < line.Length)
        {
            char c = line[i];
            if (c == '!' && i + 1 < line.Length && line[i + 1] == '[')
            {
                if (TrySkipImage(line, i, out int end))
                {
                    i = end;
                    continue;
                }
            }

            if (c == '[')
            {
                if (TryTakeLink(line, i, out string text, out int end))
                {
                    sb.Append(Inline(text));
                    i = end;
                    continue;
                }
            }

            if (c == '`')
            {
                if (TryTakeCode(line, i, out string code, out int end))
                {
                    sb.Append(code);
                    i = end;
                    continue;
                }
            }

            if (c == '<')
            {
                int close = line.IndexOf('>', i + 1);
                if (close >= 0)
                {
                    i = close + 1;
                    continue;
                }
            }

            if (c == '*' || c == '_')
            {
                if (TryTakeEmphasis(line, i, out string inner, out int end))
                {
                    sb.Append(Inline(inner));
                    i = end;
                    continue;
                }
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString().TrimEnd();
    }

    private static string NormalizeNewlines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static string[] SplitLines(string source) =>
        source.Split('\n');

    private static string JoinBlocks(List<string> lines)
    {
        if (lines.Count == 0) return "";
        var sb = new StringBuilder();
        for (int i = 0; i < lines.Count; i++)
        {
            if (i > 0) sb.Append('\n');
            sb.Append(lines[i]);
        }

        return sb.ToString().Trim();
    }

    private static string JoinRange(string[] lines, int start, int end)
    {
        var sb = new StringBuilder();
        for (int i = start; i < end && i < lines.Length; i++)
        {
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(lines[i]);
        }

        return sb.ToString();
    }

    private static bool IsFence(string line)
    {
        string t = line.TrimStart();
        return t.StartsWith("```", StringComparison.Ordinal) || t.StartsWith("~~~", StringComparison.Ordinal);
    }

    private static bool IsHeading(string line, out string text)
    {
        text = "";
        string t = line.TrimStart();
        if (t.Length == 0 || t[0] != '#') return false;
        int n = 0;
        while (n < t.Length && n < 6 && t[n] == '#') n++;
        if (n >= t.Length) return false;
        if (t[n] != ' ' && t[n] != '\t') return false;
        text = t.Substring(n + 1).Trim().TrimEnd('#', ' ', '\t');
        return text.Length > 0;
    }

    private static bool IsListItem(string line, out string text)
    {
        text = "";
        string t = line.TrimStart();
        if (t.Length < 2) return false;
        if ((t[0] == '-' || t[0] == '*' || t[0] == '+') && (t[1] == ' ' || t[1] == '\t'))
        {
            text = t.Substring(2).Trim();
            return true;
        }

        int i = 0;
        while (i < t.Length && t[i] >= '0' && t[i] <= '9') i++;
        if (i == 0 || i >= t.Length || t[i] != '.') return false;
        i++;
        if (i >= t.Length || (t[i] != ' ' && t[i] != '\t')) return false;
        text = t.Substring(i + 1).Trim();
        return true;
    }

    private static bool IsTableRow(string line)
    {
        string t = line.Trim();
        if (t.Length < 3 || t[0] != '|') return false;
        return t.IndexOf('|', 1) >= 0;
    }

    private static bool IsTableSeparator(string line)
    {
        if (!IsTableRow(line)) return false;
        string t = line.Trim();
        for (int i = 0; i < t.Length; i++)
        {
            char c = t[i];
            if (c == '|' || c == '-' || c == ':' || c == ' ' || c == '\t') continue;
            return false;
        }

        return t.IndexOf('-') >= 0;
    }

    private static string JoinTableRow(string line)
    {
        string t = line.Trim();
        if (t.StartsWith("|", StringComparison.Ordinal)) t = t.Substring(1);
        if (t.EndsWith("|", StringComparison.Ordinal)) t = t.Substring(0, t.Length - 1);
        string[] cells = t.Split('|');
        var parts = new List<string>(cells.Length);
        for (int i = 0; i < cells.Length; i++)
        {
            string cell = Inline(cells[i].Trim());
            parts.Add(cell);
        }

        return string.Join(CellJoin, parts);
    }

    private static bool IsHorizontalRule(string line)
    {
        string t = line.Trim();
        if (t.Length < 3) return false;
        char c = t[0];
        if (c != '-' && c != '*' && c != '_') return false;
        for (int i = 0; i < t.Length; i++)
        {
            if (t[i] == c || t[i] == ' ' || t[i] == '\t') continue;
            return false;
        }

        int n = 0;
        for (int i = 0; i < t.Length; i++)
            if (t[i] == c) n++;
        return n >= 3;
    }

    private static bool IsReferenceDefinition(string line)
    {
        string t = line.TrimStart();
        if (t.Length < 4 || t[0] != '[') return false;
        int close = t.IndexOf(']', 1);
        if (close < 1 || close + 1 >= t.Length || t[close + 1] != ':') return false;
        return true;
    }

    private static bool IsEntryBoundary(string line)
    {
        string t = line.Trim();
        if (t.Length == 0) return false;
        if (IsHeading(t, out var heading))
            return LooksLikeVersion(heading);
        if (t[0] == '`' && t.Length >= 3 && t[t.Length - 1] == '`')
            return LooksLikeVersion(t.Substring(1, t.Length - 2).Trim());
        return LooksLikeVersion(t);
    }

    private static bool LooksLikeVersion(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        string t = text.Trim();
        if (t.Length >= 2 && t[0] == '[' && t[t.Length - 1] == ']')
            t = t.Substring(1, t.Length - 2).Trim();
        if (t.Length > 0 && (t[0] == 'v' || t[0] == 'V')) t = t.Substring(1);
        int dot = t.IndexOf('.');
        if (dot <= 0) return false;
        for (int i = 0; i < dot; i++)
            if (t[i] < '0' || t[i] > '9') return false;
        return true;
    }

    private static bool TrySkipImage(string line, int start, out int end)
    {
        end = start;
        if (start + 1 >= line.Length || line[start] != '!' || line[start + 1] != '[') return false;
        if (!TryTakeLink(line, start + 1, out _, out end)) return false;
        return true;
    }

    private static bool TryTakeLink(string line, int start, out string text, out int end)
    {
        text = "";
        end = start;
        if (start >= line.Length || line[start] != '[') return false;
        int close = IndexOfUnescaped(line, ']', start + 1);
        if (close < 0) return false;
        text = line.Substring(start + 1, close - start - 1);
        int next = close + 1;
        if (next < line.Length && line[next] == '(')
        {
            int dest = IndexOfUnescaped(line, ')', next + 1);
            if (dest < 0) return false;
            end = dest + 1;
            return true;
        }

        if (next < line.Length && line[next] == '[')
        {
            int dest = IndexOfUnescaped(line, ']', next + 1);
            if (dest < 0) return false;
            end = dest + 1;
            return true;
        }

        end = close + 1;
        return true;
    }

    private static bool TryTakeCode(string line, int start, out string code, out int end)
    {
        code = "";
        end = start;
        if (start >= line.Length || line[start] != '`') return false;
        int n = 1;
        while (start + n < line.Length && line[start + n] == '`') n++;
        int close = line.IndexOf(new string('`', n), start + n, StringComparison.Ordinal);
        if (close < 0) return false;
        code = line.Substring(start + n, close - start - n);
        end = close + n;
        return true;
    }

    private static bool TryTakeEmphasis(string line, int start, out string inner, out int end)
    {
        inner = "";
        end = start;
        char mark = line[start];
        if (mark != '*' && mark != '_') return false;
        int n = 1;
        if (start + 1 < line.Length && line[start + 1] == mark) n = 2;
        if (start + n >= line.Length) return false;
        int close = line.IndexOf(new string(mark, n), start + n, StringComparison.Ordinal);
        if (close <= start + n) return false;
        inner = line.Substring(start + n, close - start - n);
        if (inner.Length == 0) return false;
        end = close + n;
        return true;
    }

    private static int IndexOfUnescaped(string line, char ch, int start)
    {
        for (int i = start; i < line.Length; i++)
        {
            if (line[i] != ch) continue;
            return i;
        }

        return -1;
    }
}
