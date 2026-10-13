namespace Progenitor.Ui;

/// <summary>
/// Caption casing for the owned MODS outer tab (TABS2, live 2026-09-29). The native outer tabs'
/// localized strings are mixed case ("Spellbook") and their TMP draws them in small caps, so an
/// all-caps "MODS" rendered as plain full capitals beside them. The caption follows the donor
/// sibling's case instead. Game-free.
/// </summary>
internal static class NativeTabCaption
{
    /// <summary>
    /// <paramref name="caption"/> in the sibling's case: title case ("Mods") when the sibling text
    /// has a lower-case letter, all caps when it has letters and none are lower case, and the
    /// caption unchanged when there is no sibling text to go by.
    /// </summary>
    internal static string MatchSiblingCase(string? siblingText, string caption)
    {
        if (string.IsNullOrEmpty(caption)) return caption ?? "";
        if (string.IsNullOrWhiteSpace(siblingText)) return caption;
        bool anyLetter = false, anyLower = false;
        foreach (char c in siblingText!)
        {
            if (!char.IsLetter(c)) continue;
            anyLetter = true;
            if (char.IsLower(c)) { anyLower = true; break; }
        }
        if (!anyLetter) return caption;
        if (!anyLower) return caption.ToUpperInvariant();
        string lower = caption.ToLowerInvariant();
        return char.ToUpperInvariant(lower[0]) + lower.Substring(1);
    }
}
