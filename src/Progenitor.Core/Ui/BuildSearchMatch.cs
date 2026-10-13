using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Progenitor.Ui;

/// <summary>
/// BUILDSEARCH1: the game-free half of the build-menu search. How a query and an entry's names are
/// normalized, and when an entry matches.
///
/// <para><b>The rule.</b> The query is split on whitespace into words; an entry matches when every
/// word appears somewhere in the entry's searchable text (any order, any position). Case, accents and
/// the underscores of prefab names are ignored, so <c>stone wall</c>, <c>WALL stone</c> and
/// <c>wáll</c> all find the stone wall. An empty or all-space query matches everything: it is the
/// unfiltered menu.</para>
///
/// <para><b>An entry's searchable text</b> is the localized name of every blueprint in the group (the
/// cell and its variations), plus each blueprint's prefab name with its <c>TM_</c> prefix dropped and
/// underscores turned into spaces. The prefab name is what makes a mod's building findable before its
/// display name is localized, and lets an English word find an item on a non-English client.</para>
/// </summary>
internal static class BuildSearchMatch
{
    /// <summary>The longest query the field accepts.</summary>
    internal const int MaxQueryLength = 64;

    /// <summary>Between two names in <see cref="Join"/>: a line break, which <see cref="Normalize"/> never leaves in a word.</summary>
    private const char Separator = '\n';

    /// <summary>The words of a query, normalized. Empty for an empty or all-space query.</summary>
    internal static string[] Words(string? query)
    {
        string normalized = Normalize(query);
        if (normalized.Length == 0) return Array.Empty<string>();
        return normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>True when every word occurs in <paramref name="normalizedText"/> (see <see cref="Normalize"/>).</summary>
    internal static bool Matches(IReadOnlyList<string> words, string? normalizedText)
    {
        if (words == null || words.Count == 0) return true;
        if (string.IsNullOrEmpty(normalizedText)) return false;
        for (int i = 0; i < words.Count; i++)
        {
            if (normalizedText!.IndexOf(words[i], StringComparison.Ordinal) < 0) return false;
        }

        return true;
    }

    /// <summary>
    /// Lower case (invariant), accents removed, underscores and every run of whitespace or control
    /// characters collapsed to one space, trimmed.
    /// </summary>
    internal static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        string decomposed;
        try { decomposed = text!.Normalize(NormalizationForm.FormD); }
        catch (ArgumentException) { decomposed = text!; }

        var sb = new StringBuilder(decomposed.Length);
        bool pendingSpace = false;
        foreach (char raw in decomposed)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(raw);
            if (category == UnicodeCategory.NonSpacingMark) continue;
            if (raw == '_' || char.IsWhiteSpace(raw) || char.IsControl(raw))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }

            sb.Append(char.ToLowerInvariant(raw));
        }

        return sb.ToString();
    }

    /// <summary>
    /// A prefab name as searchable words: <c>TM_Castle_Wall_Tier02_Stone</c> becomes
    /// <c>castle wall tier02 stone</c>. Only the leading <c>TM_</c> (tile model) prefix is dropped.
    /// </summary>
    internal static string PrefabWords(string? prefabName)
    {
        if (string.IsNullOrEmpty(prefabName)) return "";
        string name = prefabName!;
        if (name.StartsWith("TM_", StringComparison.OrdinalIgnoreCase)) name = name.Substring(3);
        return Normalize(name);
    }

    /// <summary>One searchable text from an entry's names, each normalized and joined by a line break, which no normalized word contains.</summary>
    internal static string Join(IEnumerable<string?> names)
    {
        var sb = new StringBuilder();
        foreach (var name in names)
        {
            string n = Normalize(name);
            if (n.Length == 0) continue;
            if (sb.Length > 0) sb.Append(Separator);
            sb.Append(n);
        }

        return sb.ToString();
    }

    /// <summary>BUILDSEARCH3: the count while the row shows results from every tab: "12 found".</summary>
    internal static string ResultsText(int shown) =>
        shown.ToString(CultureInfo.InvariantCulture) + " found";

    /// <summary>BUILDSEARCH3: more matched than the row is given: "48 of 212 found".</summary>
    internal static string ResultsText(int shown, int matched) =>
        matched > shown
            ? shown.ToString(CultureInfo.InvariantCulture) + " of " + matched.ToString(CultureInfo.InvariantCulture) + " found"
            : ResultsText(shown);

    /// <summary>Characters in a query, spaces excluded (the all-tabs search needs <c>BuildSearchResults.MinQueryChars</c>).</summary>
    internal static int QueryChars(IReadOnlyList<string> words)
    {
        int n = 0;
        if (words == null) return 0;
        for (int i = 0; i < words.Count; i++) n += words[i]?.Length ?? 0;
        return n;
    }

    /// <summary>The count shown beside the field while a query is active: "3 of 17".</summary>
    internal static string CountText(int visible, int total) =>
        visible.ToString(CultureInfo.InvariantCulture) + " of " + total.ToString(CultureInfo.InvariantCulture);
}
