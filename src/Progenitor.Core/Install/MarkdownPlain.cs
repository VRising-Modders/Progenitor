using System;
using System.Collections.Generic;
using System.Text;

namespace Progenitor.Install;

/// <summary>
/// One README/changelog row: a heading, paragraph, list item, code line, table row or quoted note.
/// Blank lines are not blocks.
/// </summary>
internal enum MarkdownBlockKind
{
    Heading = 1,
    Paragraph = 2,
    ListItem = 3,
    Code = 4,
    Table = 5,

    /// <summary>ABOUTMD1: a blockquote (<c>&gt; text</c>), its markers removed; drawn as an indented note.</summary>
    Quote = 6,
}

/// <summary>Kind plus the already-inlined plain text for one listing-page row.</summary>
internal readonly struct MarkdownBlock
{
    public MarkdownBlock(MarkdownBlockKind kind, string text)
        : this(kind, text, 0, null, null)
    {
    }

    public MarkdownBlock(MarkdownBlockKind kind, string text, int level, IReadOnlyList<string>? cells, IReadOnlyList<string>? header)
    {
        Kind = kind;
        Text = text ?? "";
        Level = level < 0 ? 0 : level;
        Cells = cells;
        Header = header;
    }

    public MarkdownBlockKind Kind { get; }

    /// <summary>
    /// The block as one plain line. ABOUTMD1: a table row reads <c>first cell — Header: cell · Header: cell</c>, the
    /// header row used as the labels (<see cref="MarkdownPlain.TableRowLine"/>), never the raw pipes.
    /// </summary>
    public string Text { get; }

    /// <summary>ABOUTMD1: a heading's level (1 to 6), a list item's nesting depth (0 at the margin); 0 otherwise.</summary>
    public int Level { get; }

    /// <summary>ABOUTMD1: a table row's plain cells (as many as its header); null for every other block and for a cut row.</summary>
    public IReadOnlyList<string>? Cells { get; }

    /// <summary>ABOUTMD1: the plain header cells of the row's table, the same list for every row of one table; else null.</summary>
    public IReadOnlyList<string>? Header { get; }

    /// <summary>This block with other text: a table row loses its cells, which no longer match the text.</summary>
    internal MarkdownBlock WithText(string text) =>
        Kind == MarkdownBlockKind.Table ? new(Kind, text, Level, null, null) : new(Kind, text, Level, Cells, Header);
}

/// <summary>
/// Thunderstore README/changelog markdown to the listing page's plain text.
/// Headings become their own line; images, HTML tags, HTML comments and badges are dropped;
/// links keep the link text; a table row keeps its cells, labeled by the header row; a blockquote
/// loses its markers. Not a CommonMark implementation.
/// </summary>
internal static class MarkdownPlain
{
    public const int DefaultMaxChars = 6000;
    public const string Bullet = "• ";

    /// <summary>Between labeled cells of a table row, and between the cells of a pipe row that is not a table: never a raw pipe.</summary>
    public const string CellJoin = " · ";

    /// <summary>ABOUTMD1: between a table row's first cell and its labeled cells.</summary>
    public const string RowLead = " — ";

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
    /// One block per heading, paragraph, list item, code line, table row or quote.
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
            blocks.Add(line.Block);
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

            result.Add(blocks[i].WithText(Truncate(text, remaining)));
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

    /// <summary>
    /// ABOUTMD1: a table row as one line: the first cell, then each other non-empty cell after its header label
    /// (<c>Display / Show notices — Default: On · Who decides: Each player · What it does: …</c>).
    /// </summary>
    internal static string TableRowLine(IReadOnlyList<string>? header, IReadOnlyList<string>? row)
    {
        if (row == null || row.Count == 0) return "";
        string first = (row[0] ?? "").Trim();
        var rest = new List<string>(row.Count);
        for (int j = 1; j < row.Count; j++)
        {
            string cell = (row[j] ?? "").Trim();
            if (cell.Length == 0) continue;
            string label = header != null && j < header.Count ? (header[j] ?? "").Trim() : "";
            rest.Add(label.Length > 0 ? label + ": " + cell : cell);
        }

        if (rest.Count == 0) return first;
        string joined = string.Join(CellJoin, rest);
        return first.Length == 0 ? joined : first + RowLead + joined;
    }

    private readonly struct CollectedLine
    {
        public CollectedLine(MarkdownBlock block, bool blank)
        {
            Block = block;
            Blank = blank;
        }

        public MarkdownBlock Block { get; }
        public string Text => Block.Text;
        public bool Blank { get; }
    }

    /// <summary>
    /// The document as blocks and paragraph breaks. ABOUTMD1: consecutive lines of one paragraph, list item or quote are
    /// one block (Markdown's soft line breaks); a GitHub table (a header row, its <c>---</c> separator, then rows) gives one
    /// labeled row per data row; a blockquote is a <see cref="MarkdownBlockKind.Quote"/>; a setext underline makes the
    /// paragraph above it a heading; HTML comments, including ones over several lines, are dropped.
    /// </summary>
    private static List<CollectedLine> CollectLines(string? markdown)
    {
        var output = new List<CollectedLine>();
        if (string.IsNullOrEmpty(markdown)) return output;
        string source = NormalizeNewlines(markdown);
        var lines = SplitLines(source);
        bool inFence = false;
        bool inComment = false;
        bool lastBlank = true;
        // The block the next plain line continues (a soft line break), or null.
        MarkdownBlockKind? open = null;

        void Emit(MarkdownBlock block)
        {
            if (string.IsNullOrEmpty(block.Text)) return;
            output.Add(new CollectedLine(block, blank: false));
            lastBlank = false;
        }

        void Append(string text)
        {
            if (string.IsNullOrEmpty(text) || output.Count == 0) return;
            var last = output[output.Count - 1].Block;
            output[output.Count - 1] = new CollectedLine(
                new MarkdownBlock(last.Kind, last.Text + " " + text, last.Level, null, null), blank: false);
        }

        void EmitBlank()
        {
            open = null;
            if (output.Count == 0 || lastBlank) return;
            output.Add(new CollectedLine(new MarkdownBlock(MarkdownBlockKind.Paragraph, ""), blank: true));
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

                Emit(new MarkdownBlock(MarkdownBlockKind.Code, line));
                continue;
            }

            if (inComment || line.IndexOf("<!--", StringComparison.Ordinal) >= 0)
            {
                bool hadText = line.Trim().Length > 0;
                line = StripComments(line, ref inComment);
                // A line that was only a comment is no line at all (not a paragraph break).
                if (hadText && line.Trim().Length == 0) continue;
            }

            if (IsFence(line))
            {
                EmitBlank();
                inFence = true;
                continue;
            }

            if (i + 1 < lines.Length && IsTableStart(line, lines[i + 1], out var header))
            {
                open = null;
                for (i += 2; i < lines.Length; i++)
                {
                    string rowLine = lines[i];
                    if (rowLine.Trim().Length == 0 || rowLine.IndexOf('|') < 0 || IsFence(rowLine)) break;
                    var cells = SplitCells(rowLine);
                    var row = new string[header.Length];
                    for (int c = 0; c < row.Length; c++) row[c] = c < cells.Count ? Inline(cells[c]) : "";
                    Emit(new MarkdownBlock(MarkdownBlockKind.Table, TableRowLine(header, row), 0, row, header));
                }

                i--;
                continue;
            }

            if (IsTableSeparator(line))
            {
                open = null;
                continue;
            }

            if (IsTableRow(line))
            {
                // A pipe row without a header separator is not a table: its cells, never the raw pipes.
                open = null;
                Emit(new MarkdownBlock(MarkdownBlockKind.Table, JoinTableRow(line)));
                continue;
            }

            if (open == MarkdownBlockKind.Paragraph && output.Count > 0 && IsSetextUnderline(line, out int setextLevel))
            {
                var last = output[output.Count - 1].Block;
                output[output.Count - 1] = new CollectedLine(
                    new MarkdownBlock(MarkdownBlockKind.Heading, last.Text, setextLevel, null, null), blank: false);
                EmitBlank();
                continue;
            }

            if (IsHeading(line, out var heading, out int headingLevel))
            {
                EmitBlank();
                Emit(new MarkdownBlock(MarkdownBlockKind.Heading, Inline(heading), headingLevel, null, null));
                EmitBlank();
                continue;
            }

            if (IsQuote(line, out string quoted))
            {
                if (quoted.Trim().Length == 0)
                {
                    // An empty quote line ends the quote's paragraph.
                    open = null;
                    continue;
                }

                // A GitHub alert's marker line (> [!NOTE]) says nothing a player needs.
                if (IsAlertMarker(quoted)) continue;
                string text;
                bool own = false;
                if (IsHeading(quoted, out var quotedHeading, out _))
                {
                    text = Inline(quotedHeading);
                    own = true;
                }
                else if (IsListItem(quoted, out var quotedItem, out string quotedMarker, out _))
                {
                    text = quotedMarker + Inline(quotedItem);
                    own = true;
                }
                else text = Inline(quoted.Trim());

                if (text.Trim().Length == 0) continue;
                if (open == MarkdownBlockKind.Quote && !own) Append(text);
                else Emit(new MarkdownBlock(MarkdownBlockKind.Quote, text));
                open = own ? null : MarkdownBlockKind.Quote;
                continue;
            }

            if (IsListItem(line, out var item, out string marker, out int depth))
            {
                Emit(new MarkdownBlock(MarkdownBlockKind.ListItem, marker + Inline(item), depth, null, null));
                open = MarkdownBlockKind.ListItem;
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

            string stripped = Inline(line.Trim());
            if (string.IsNullOrWhiteSpace(stripped)) continue;
            if (open != null && output.Count > 0)
            {
                // A soft line break: the line continues the paragraph, list item or quote above it.
                Append(stripped);
                continue;
            }

            Emit(new MarkdownBlock(MarkdownBlockKind.Paragraph, stripped));
            open = MarkdownBlockKind.Paragraph;
        }

        return output;
    }

    /// <summary>
    /// Inline Markdown to plain text: images dropped, links (also badges inside links) reduced to their text, code
    /// spans to their code, emphasis and strikethrough markers dropped, HTML tags dropped (an autolink keeps its
    /// address), backslash escapes and the common HTML entities resolved. The result is plain text: whatever draws it
    /// still escapes every <c>&lt;</c>.
    /// </summary>
    internal static string Inline(string line)
    {
        if (string.IsNullOrEmpty(line)) return "";
        var sb = new StringBuilder(line.Length);
        int i = 0;
        while (i < line.Length)
        {
            char c = line[i];
            if (c == '\\' && i + 1 < line.Length && IsAsciiPunctuation(line[i + 1]))
            {
                sb.Append(line[i + 1]);
                i += 2;
                continue;
            }

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

            if (c == '<' && TryTakeTag(line, i, out string kept, out int tagEnd))
            {
                sb.Append(kept);
                i = tagEnd;
                continue;
            }

            if (c == '&' && TryTakeEntity(line, i, out char entity, out int entityEnd))
            {
                sb.Append(entity);
                i = entityEnd;
                continue;
            }

            if (c == '~' && i + 1 < line.Length && line[i + 1] == '~')
            {
                int close = line.IndexOf("~~", i + 2, StringComparison.Ordinal);
                if (close > i + 2)
                {
                    sb.Append(Inline(line.Substring(i + 2, close - i - 2)));
                    i = close + 2;
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

    /// <summary>
    /// <paramref name="line"/> without its HTML comments. <paramref name="inComment"/> says whether the line starts inside
    /// one, and is left saying whether it ends inside one.
    /// </summary>
    private static string StripComments(string line, ref bool inComment)
    {
        var sb = new StringBuilder(line.Length);
        int i = 0;
        while (i < line.Length)
        {
            if (inComment)
            {
                int close = line.IndexOf("-->", i, StringComparison.Ordinal);
                if (close < 0) return sb.ToString();
                inComment = false;
                i = close + 3;
                continue;
            }

            int open = line.IndexOf("<!--", i, StringComparison.Ordinal);
            if (open < 0)
            {
                sb.Append(line, i, line.Length - i);
                break;
            }

            sb.Append(line, i, open - i);
            inComment = true;
            i = open + 4;
        }

        return sb.ToString();
    }

    private static bool IsHeading(string line, out string text) => IsHeading(line, out text, out _);

    private static bool IsHeading(string line, out string text, out int level)
    {
        text = "";
        level = 0;
        string t = line.TrimStart();
        if (t.Length == 0 || t[0] != '#') return false;
        int n = 0;
        while (n < t.Length && n < 6 && t[n] == '#') n++;
        if (n >= t.Length) return false;
        if (t[n] != ' ' && t[n] != '\t') return false;
        text = t.Substring(n + 1).Trim().TrimEnd('#', ' ', '\t');
        level = n;
        return text.Length > 0;
    }

    /// <summary>
    /// A list item: <c>-</c>, <c>*</c> or <c>+</c> (drawn as <see cref="Bullet"/>), or <c>1.</c> / <c>1)</c> (the number
    /// kept, <c>1. </c>). A task item's <c>[ ]</c> / <c>[x]</c> box is dropped. <paramref name="depth"/> is the nesting
    /// from the indent (0 at the margin, at most 3).
    /// </summary>
    private static bool IsListItem(string line, out string text, out string marker, out int depth)
    {
        text = "";
        marker = "";
        depth = 0;
        int indent = 0;
        while (indent < line.Length && (line[indent] == ' ' || line[indent] == '\t'))
            indent += 1;
        int columns = 0;
        for (int k = 0; k < indent; k++) columns += line[k] == '\t' ? 4 : 1;
        string t = line.Substring(indent);
        if (t.Length < 2) return false;
        if ((t[0] == '-' || t[0] == '*' || t[0] == '+') && (t[1] == ' ' || t[1] == '\t'))
        {
            // "- - -" and "* * *" are rules, not items.
            if (IsHorizontalRule(t)) return false;
            text = t.Substring(2).Trim();
            marker = Bullet;
        }
        else
        {
            int i = 0;
            while (i < t.Length && i < 9 && t[i] >= '0' && t[i] <= '9') i++;
            if (i == 0 || i >= t.Length || (t[i] != '.' && t[i] != ')')) return false;
            string number = t.Substring(0, i);
            i++;
            if (i >= t.Length || (t[i] != ' ' && t[i] != '\t')) return false;
            text = t.Substring(i + 1).Trim();
            marker = number + ". ";
        }

        if (text.StartsWith("[ ] ", StringComparison.Ordinal) || text.StartsWith("[x] ", StringComparison.OrdinalIgnoreCase))
            text = text.Substring(4).TrimStart();
        depth = columns < 2 ? 0 : Math.Min(3, Math.Max(1, columns / 3));
        return true;
    }

    private static bool IsTableRow(string line)
    {
        string t = line.Trim();
        if (t.Length < 3 || t[0] != '|') return false;
        return t.IndexOf('|', 1) >= 0;
    }

    private static bool IsTableSeparator(string line) => IsTableRow(line) && IsSeparatorRow(line);

    /// <summary>A GitHub table's first line: a pipe row whose next line is a separator with as many cells.</summary>
    private static bool IsTableStart(string line, string next, out string[] header)
    {
        header = Array.Empty<string>();
        if (line.IndexOf('|') < 0 || line.Trim().Length == 0 || IsFence(line) || IsSeparatorRow(line)) return false;
        if (!IsSeparatorRow(next)) return false;
        var cells = SplitCells(line);
        if (cells.Count == 0 || SplitCells(next).Count != cells.Count) return false;
        header = new string[cells.Count];
        for (int c = 0; c < cells.Count; c++) header[c] = Inline(cells[c]);
        return true;
    }

    /// <summary>A table's separator row (<c>|---|:--:|</c>, the outer pipes optional): dashes, colons, pipes and spaces, with a pipe.</summary>
    private static bool IsSeparatorRow(string line)
    {
        string t = line.Trim();
        if (t.Length < 3 || t.IndexOf('-') < 0 || t.IndexOf('|') < 0) return false;
        for (int i = 0; i < t.Length; i++)
        {
            char c = t[i];
            if (c == '|' || c == '-' || c == ':' || c == ' ' || c == '\t') continue;
            return false;
        }

        return true;
    }

    /// <summary>A row's cells: split on pipes that are not escaped (<c>\|</c>) or inside a code span; the outer pipes dropped.</summary>
    private static List<string> SplitCells(string line)
    {
        string t = line.Trim();
        if (t.StartsWith("|", StringComparison.Ordinal)) t = t.Substring(1);
        if (t.EndsWith("|", StringComparison.Ordinal) && !t.EndsWith("\\|", StringComparison.Ordinal)) t = t.Substring(0, t.Length - 1);
        var cells = new List<string>();
        var sb = new StringBuilder();
        bool code = false;
        for (int i = 0; i < t.Length; i++)
        {
            char c = t[i];
            if (c == '\\' && i + 1 < t.Length && t[i + 1] == '|')
            {
                sb.Append('|');
                i++;
                continue;
            }

            if (c == '`') code = !code;
            if (c == '|' && !code)
            {
                cells.Add(sb.ToString().Trim());
                sb.Clear();
                continue;
            }

            sb.Append(c);
        }

        cells.Add(sb.ToString().Trim());
        return cells;
    }

    /// <summary>A pipe row that is not part of a table: its non-empty cells joined by <see cref="CellJoin"/>.</summary>
    private static string JoinTableRow(string line)
    {
        var cells = SplitCells(line);
        var parts = new List<string>(cells.Count);
        for (int i = 0; i < cells.Count; i++)
        {
            string cell = Inline(cells[i]);
            if (cell.Length > 0) parts.Add(cell);
        }

        return string.Join(CellJoin, parts);
    }

    /// <summary>A setext underline: a run of <c>=</c> (level 1) or <c>-</c> (level 2), at least two, nothing else.</summary>
    private static bool IsSetextUnderline(string line, out int level)
    {
        level = 0;
        string t = line.Trim();
        if (t.Length < 2) return false;
        char c = t[0];
        if (c != '=' && c != '-') return false;
        for (int i = 1; i < t.Length; i++)
            if (t[i] != c) return false;
        level = c == '=' ? 1 : 2;
        return true;
    }

    /// <summary>A blockquote line (<c>&gt; text</c>, up to three spaces before it, nested markers too): its text without the markers.</summary>
    private static bool IsQuote(string line, out string text)
    {
        text = "";
        string t = line.TrimStart(' ');
        if (line.Length - t.Length > 3 || t.Length == 0 || t[0] != '>') return false;
        while (t.Length > 0 && t[0] == '>')
        {
            t = t.Substring(1);
            if (t.Length > 0 && t[0] == ' ') t = t.Substring(1);
            t = t.TrimStart(' ');
        }

        text = t;
        return true;
    }

    /// <summary>A GitHub alert's marker: <c>[!NOTE]</c>, <c>[!TIP]</c>, <c>[!IMPORTANT]</c>, <c>[!WARNING]</c>, <c>[!CAUTION]</c>.</summary>
    private static bool IsAlertMarker(string text)
    {
        string t = text.Trim();
        if (t.Length < 4 || !t.StartsWith("[!", StringComparison.Ordinal) || t[t.Length - 1] != ']') return false;
        for (int i = 2; i < t.Length - 1; i++)
            if (!char.IsLetter(t[i])) return false;
        return true;
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

    /// <summary>
    /// A link <c>[text](url)</c>, <c>[text][ref]</c> or <c>[text]</c> at <paramref name="start"/>: its text. Brackets and
    /// parentheses nest, so a badge inside a link (<c>[![alt](img)](url)</c>) is taken whole.
    /// </summary>
    private static bool TryTakeLink(string line, int start, out string text, out int end)
    {
        text = "";
        end = start;
        if (start >= line.Length || line[start] != '[') return false;
        int close = IndexOfClosing(line, start, '[', ']');
        if (close < 0) return false;
        text = line.Substring(start + 1, close - start - 1);
        int next = close + 1;
        if (next < line.Length && line[next] == '(')
        {
            int dest = IndexOfClosing(line, next, '(', ')');
            if (dest < 0) return false;
            end = dest + 1;
            return true;
        }

        if (next < line.Length && line[next] == '[')
        {
            int dest = IndexOfClosing(line, next, '[', ']');
            if (dest < 0) return false;
            end = dest + 1;
            return true;
        }

        end = close + 1;
        return true;
    }

    /// <summary>The index of the <paramref name="close"/> that closes the <paramref name="open"/> at <paramref name="start"/>, or -1.</summary>
    private static int IndexOfClosing(string line, int start, char open, char close)
    {
        int depth = 0;
        for (int i = start; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '\\' && i + 1 < line.Length)
            {
                i++;
                continue;
            }

            if (c == open) depth++;
            else if (c == close && --depth == 0) return i;
        }

        return -1;
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

    /// <summary>
    /// An HTML tag at <paramref name="start"/> (<c>&lt;</c> then a letter, <c>/</c> or <c>!</c>, up to the next
    /// <c>&gt;</c>): dropped, except an autolink (<c>&lt;https://…&gt;</c>), which keeps its address. A <c>&lt;</c> that
    /// starts no tag (<c>a &lt; b</c>) is text.
    /// </summary>
    private static bool TryTakeTag(string line, int start, out string kept, out int end)
    {
        kept = "";
        end = start;
        if (start + 1 >= line.Length) return false;
        char next = line[start + 1];
        if (!(char.IsLetter(next) || next == '/' || next == '!')) return false;
        int close = line.IndexOf('>', start + 1);
        if (close < 0) return false;
        string inner = line.Substring(start + 1, close - start - 1);
        if (inner.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || inner.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            kept = inner;
        else if (inner.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            kept = inner.Substring(7);
        end = close + 1;
        return true;
    }

    /// <summary>The common HTML entities (<c>&amp;amp;</c>, <c>&amp;lt;</c>, <c>&amp;nbsp;</c>, <c>&amp;#39;</c>, …).</summary>
    private static bool TryTakeEntity(string line, int start, out char value, out int end)
    {
        value = '\0';
        end = start;
        int semi = line.IndexOf(';', start + 1);
        if (semi < 0 || semi - start > 8) return false;
        string name = line.Substring(start + 1, semi - start - 1);
        switch (name)
        {
            case "amp": value = '&'; break;
            case "lt": value = '<'; break;
            case "gt": value = '>'; break;
            case "quot": value = '"'; break;
            case "apos": value = '\''; break;
            case "nbsp": value = ' '; break;
            case "mdash": value = '—'; break;
            case "ndash": value = '–'; break;
            case "hellip": value = '…'; break;
            case "copy": value = '©'; break;
            default:
                if (name.Length < 2 || name[0] != '#') return false;
                bool hex = name[1] == 'x' || name[1] == 'X';
                string digits = name.Substring(hex ? 2 : 1);
                if (!int.TryParse(digits, hex ? System.Globalization.NumberStyles.HexNumber : System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out int code) || code <= 0 || code > 0xFFFF)
                    return false;
                value = (char)code;
                break;
        }

        end = semi + 1;
        return true;
    }

    /// <summary>
    /// <c>*text*</c>, <c>**text**</c>, <c>_text_</c>, <c>__text__</c>: the text. The opening marker is followed by a
    /// non-space and the closing one follows a non-space (so <c>2 * 3 * 4</c> stays as written), and an underscore inside
    /// a word (<c>snake_case_name</c>) is not a marker.
    /// </summary>
    private static bool TryTakeEmphasis(string line, int start, out string inner, out int end)
    {
        inner = "";
        end = start;
        char mark = line[start];
        if (mark != '*' && mark != '_') return false;
        if (mark == '_' && start > 0 && char.IsLetterOrDigit(line[start - 1])) return false;
        int n = 1;
        if (start + 1 < line.Length && line[start + 1] == mark) n = 2;
        if (start + n >= line.Length || char.IsWhiteSpace(line[start + n])) return false;
        int from = start + n;
        while (true)
        {
            int close = line.IndexOf(new string(mark, n), from, StringComparison.Ordinal);
            if (close <= start + n) return false;
            bool closes = !char.IsWhiteSpace(line[close - 1])
                && (mark != '_' || close + n >= line.Length || !char.IsLetterOrDigit(line[close + n]));
            if (closes)
            {
                inner = line.Substring(start + n, close - start - n);
                end = close + n;
                return inner.Length > 0;
            }

            from = close + 1;
        }
    }

    private static bool IsAsciiPunctuation(char c) =>
        (c >= '!' && c <= '/') || (c >= ':' && c <= '@') || (c >= '[' && c <= '`') || (c >= '{' && c <= '~');
}
