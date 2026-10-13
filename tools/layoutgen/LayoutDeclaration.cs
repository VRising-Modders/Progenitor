using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Progenitor.LayoutGen;

/// <summary>What one <c>layout.txt</c> line asks the dump for.</summary>
public enum LayoutQueryKind
{
    /// <summary>A field's byte offset inside its declaring type: <c>Type.Field</c>.</summary>
    Offset = 0,

    /// <summary>A type's native size / array stride: <c>Type</c>.</summary>
    Size = 1,

    /// <summary>An enum member's value: <c>Enum.Member</c>.</summary>
    EnumValue = 2,

    /// <summary>An explicit metadata line-pattern regex with one capture group.</summary>
    Match = 3,
}

/// <summary>
/// One declaration line of <c>src/Progenitor.Core/Layout/layout.txt</c>, parsed.
///
/// <para>Game-free on purpose, exactly like <c>Access/AccessDeclaration.cs</c>:
/// <c>tools/layoutgen</c> and the unit tests both read declarations on a machine that never
/// installed V Rising, and only the <em>resolve</em> half needs the game's metadata.</para>
///
/// <para><b>Why the expected value is part of the declaration.</b> The whole point of this file
/// is that a layout constant is a wrong-memory write when it drifts, and a generator that simply
/// re-emitted whatever the dump says today would rewrite the constant silently on a game update.
/// So the declaration carries the value the code was written against, the dump carries the value
/// the game actually has, and <c>--check</c> exits 2 when they disagree. Nothing here ever
/// changes a value on its own.</para>
/// </summary>
public sealed class LayoutDeclaration
{
    public LayoutDeclaration(
        string name,
        string valueText,
        LayoutQueryKind kind,
        string query,
        string side,
        string citation,
        string anchor = "",
        int lineNumber = 0)
    {
        Name = name ?? "";
        ValueText = valueText ?? "";
        Kind = kind;
        Query = query ?? "";
        Side = side ?? "";
        Citation = citation ?? "";
        Anchor = anchor ?? "";
        LineNumber = lineNumber;
    }

    /// <summary>The emitted constant's name: <c>Layout.&lt;Name&gt;</c>.</summary>
    public string Name { get; }

    /// <summary>
    /// The expected value exactly as written in <c>layout.txt</c> — <c>20</c> or <c>0x40</c>.
    /// Emitted verbatim so a migrated site keeps the spelling its old literal had.
    /// </summary>
    public string ValueText { get; }

    public LayoutQueryKind Kind { get; }

    /// <summary>
    /// <c>Type.Field</c>, <c>Type</c>, <c>Enum.Member</c> or the regex source, depending on
    /// <see cref="Kind"/>.
    /// </summary>
    public string Query { get; }

    /// <summary>Which dump the query resolves against: <c>server</c> or <c>client</c>.</summary>
    public string Side { get; }

    /// <summary>
    /// For a <see cref="LayoutQueryKind.Match"/>: the type whose block the pattern is searched
    /// in (<c>… in FixedString128Bytes</c>), or <c>""</c> for the whole dump. Without it a
    /// pattern like <c>utf8MaxLengthInBytes = (\d+)</c> matches four differently-sized
    /// FixedString types and is refused as ambiguous — which is the right refusal, and the
    /// anchor is how a genuinely unique fact gets asked for anyway.
    /// </summary>
    public string Anchor { get; }

    /// <summary>The human citation carried over from the constant's old comment.</summary>
    public string Citation { get; }

    /// <summary>
    /// The 1-based <c>layout.txt</c> line this declaration was read from, or 0 when it was built
    /// in code (a test). Deliberately <b>not</b> part of <see cref="Canonical"/>: the declarations
    /// hash must not change when a comment is inserted above a line, or every generated file in
    /// the tree would go stale on a doc edit. It exists so an unresolved declaration can be
    /// reported at the place a human has to go and fix it (R53 finding 1).
    /// </summary>
    public int LineNumber { get; }

    /// <summary>The expected value as a number. Parsed by the parser, so this never throws here.</summary>
    public long Value => ParseValue(ValueText, out var v) ? v : 0;

    /// <summary>The canonical single-line form the declaration hash is taken over.</summary>
    public string Canonical =>
        Side + "|" + Kind.ToString().ToLowerInvariant() + "|" + Name + "|" + ValueText + "|" + Query
        + (Anchor.Length > 0 ? "|in " + Anchor : "");

    /// <summary>Decimal or <c>0x</c> hex. Nothing else is a layout value.</summary>
    public static bool ParseValue(string text, out long value)
    {
        value = 0;
        if (string.IsNullOrEmpty(text)) return false;
        bool negative = text[0] == '-';
        var body = negative ? text.Substring(1) : text;
        bool ok = body.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? long.TryParse(body.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value)
            : long.TryParse(body, NumberStyles.None, CultureInfo.InvariantCulture, out value);
        if (ok && negative) value = -value;
        return ok;
    }
}

/// <summary>
/// The <c>layout v1</c> grammar. One declaration per line:
///
/// <code>
/// layout v1                                  required first directive
/// dump server | client                       which side's metadata the lines that follow resolve against
/// offset &lt;Name&gt; = &lt;value&gt; : &lt;Type&gt;.&lt;Field&gt;   [# citation]
/// size   &lt;Name&gt; = &lt;value&gt; : &lt;Type&gt;           [# citation]
/// enum   &lt;Name&gt; = &lt;value&gt; : &lt;Enum&gt;.&lt;Member&gt;  [# citation]
/// match  &lt;Name&gt; = &lt;value&gt; : /&lt;regex&gt;/        [# citation]
/// </code>
///
/// A <c>match</c> regex must have exactly one capture group and that group must capture the
/// number. It is the escape hatch for a fact the metadata states but not as a field offset — a
/// <c>const</c> inside a type, say.
///
/// A citation names types, members and reasons. It never cites a file line: a line number in a
/// generated metadata file changes on every game update and says nothing a type name does not, so
/// a citation shaped like <c>file.cs:123</c> or <c>line 123</c> is refused.
/// </summary>
public static class LayoutDeclarationParser
{
    public static bool TryParse(
        IReadOnlyList<string> lines,
        out List<LayoutDeclaration> declarations,
        out List<string> errors)
    {
        declarations = new List<LayoutDeclaration>();
        errors = new List<string>();
        bool sawVersion = false;
        var side = "server";
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (int i = 0; i < lines.Count; i++)
        {
            var raw = lines[i] ?? "";
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            string where = "line " + (i + 1) + ": ";

            if (!sawVersion)
            {
                if (!string.Equals(line, "layout v1", StringComparison.Ordinal))
                {
                    errors.Add(where + "the first directive must be 'layout v1'");
                    return false;
                }
                sawVersion = true;
                continue;
            }

            if (line.StartsWith("dump ", StringComparison.Ordinal))
            {
                var value = line.Substring(5).Trim();
                if (value != "server" && value != "client")
                {
                    errors.Add(where + "dump must be 'server' or 'client', not '" + value + "'");
                    continue;
                }
                side = value;
                continue;
            }

            int space = line.IndexOf(' ');
            if (space <= 0) { errors.Add(where + "not a declaration"); continue; }
            var keyword = line.Substring(0, space);
            LayoutQueryKind kind;
            switch (keyword)
            {
                case "offset": kind = LayoutQueryKind.Offset; break;
                case "size": kind = LayoutQueryKind.Size; break;
                case "enum": kind = LayoutQueryKind.EnumValue; break;
                case "match": kind = LayoutQueryKind.Match; break;
                default:
                    errors.Add(where + "unknown declaration '" + keyword + "'");
                    continue;
            }

            var rest = line.Substring(space + 1).Trim();
            int eq = rest.IndexOf('=');
            if (eq < 0) { errors.Add(where + "missing '=' between the name and the expected value"); continue; }
            var name = rest.Substring(0, eq).Trim();
            var tail = rest.Substring(eq + 1).Trim();

            int colon = tail.IndexOf(':');
            if (colon < 0) { errors.Add(where + "missing ':' between the expected value and the query"); continue; }
            var valueText = tail.Substring(0, colon).Trim();
            var queryAndCitation = tail.Substring(colon + 1).Trim();

            if (name.Length == 0 || !IsIdentifier(name))
            {
                errors.Add(where + "'" + name + "' is not a C# identifier");
                continue;
            }
            if (!seen.Add(name)) { errors.Add(where + "'" + name + "' is declared twice"); continue; }
            if (!LayoutDeclaration.ParseValue(valueText, out _))
            {
                errors.Add(where + "'" + valueText + "' is not a decimal or 0x value");
                continue;
            }

            string query, citation, anchor = "";
            if (kind == LayoutQueryKind.Match)
            {
                if (queryAndCitation.Length == 0 || queryAndCitation[0] != '/')
                {
                    errors.Add(where + "a match query must be delimited by /slashes/");
                    continue;
                }
                int close = queryAndCitation.LastIndexOf('/');
                if (close <= 0) { errors.Add(where + "unterminated match regex"); continue; }
                query = queryAndCitation.Substring(1, close - 1);
                var after = queryAndCitation.Substring(close + 1);
                int hashAfter = after.IndexOf('#');
                var beforeCitation = (hashAfter < 0 ? after : after.Substring(0, hashAfter)).Trim();
                citation = Citation(hashAfter < 0 ? "" : after.Substring(hashAfter));
                if (beforeCitation.Length > 0)
                {
                    if (!beforeCitation.StartsWith("in ", StringComparison.Ordinal))
                    {
                        errors.Add(where + "unexpected '" + beforeCitation + "' after the match regex");
                        continue;
                    }
                    anchor = beforeCitation.Substring(3).Trim();
                    if (anchor.Length == 0) { errors.Add(where + "'in' needs a type name"); continue; }
                }
                if (query.Length == 0) { errors.Add(where + "empty match regex"); continue; }
            }
            else
            {
                int hash = queryAndCitation.IndexOf('#');
                query = (hash < 0 ? queryAndCitation : queryAndCitation.Substring(0, hash)).Trim();
                citation = Citation(hash < 0 ? "" : queryAndCitation.Substring(hash));
                if (query.Length == 0) { errors.Add(where + "empty query"); continue; }
                bool needsMember = kind != LayoutQueryKind.Size;
                if (needsMember && query.IndexOf('.') < 0)
                {
                    errors.Add(where + "'" + query + "' must be spelled Type.Member");
                    continue;
                }
            }

            if (CitesAFileLine(citation))
            {
                errors.Add(where + "the citation '" + citation + "' points at a file line; cite the type and"
                    + " member by name instead");
                continue;
            }

            declarations.Add(new LayoutDeclaration(name, valueText, kind, query, side, citation, anchor, i + 1));
        }

        if (!sawVersion) errors.Add("the file is empty; it must start with 'layout v1'");
        return errors.Count == 0;
    }

    private static string Citation(string text)
    {
        var trimmed = (text ?? "").Trim();
        if (trimmed.StartsWith("#", StringComparison.Ordinal)) trimmed = trimmed.Substring(1).Trim();
        return trimmed;
    }

    /// <summary>
    /// True when <paramref name="citation"/> cites a file line (<c>x.cs:123</c>, <c>x.cs 123</c>,
    /// <c>line 123</c>) rather than naming what it means.
    /// </summary>
    internal static bool CitesAFileLine(string citation) =>
        CitedFileLine.IsMatch(citation ?? "");

    private static readonly System.Text.RegularExpressions.Regex CitedFileLine = new(
        @"\.cs\s*:?\s*\d|\blines?\s+\d",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static bool IsIdentifier(string s)
    {
        if (s.Length == 0 || (!char.IsLetter(s[0]) && s[0] != '_')) return false;
        foreach (var c in s) if (!char.IsLetterOrDigit(c) && c != '_') return false;
        return true;
    }

    /// <summary>sha256 over the canonical declarations, in file order.</summary>
    public static string Hash(IReadOnlyList<LayoutDeclaration> declarations)
    {
        var sb = new StringBuilder();
        foreach (var d in declarations) sb.Append(d.Canonical).Append('\n');
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
        var hex = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes) hex.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        return hex.ToString();
    }
}
