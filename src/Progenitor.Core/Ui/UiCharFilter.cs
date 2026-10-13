using System;
using System.Text;

namespace Progenitor.Ui;

/// <summary>
/// TEXTFILTER1: which characters a field accepts while it is being typed into. Applied to every
/// input path (the OS text queue, the US physical-key map, paste and the bridge's injected input)
/// before the edit, so a number box never shows a letter. The commit still validates the whole
/// value; this only keeps impossible characters out.
/// </summary>
internal enum UiCharFilter
{
    /// <summary>Free text.</summary>
    None = 0,

    /// <summary>Digits and one leading <c>-</c> or <c>+</c>.</summary>
    Integer = 1,

    /// <summary>As <see cref="Integer"/>, plus one decimal point (<c>.</c>).</summary>
    Decimal = 2,

    /// <summary>A color: one leading <c>#</c> and the hex digits <c>0-9 A-F a-f</c>.</summary>
    Hex = 3,
}

/// <summary>Game-free rules for <see cref="UiCharFilter"/>.</summary>
internal static class UiCharFilters
{
    /// <summary>
    /// The longest hex value a color field takes: <c>#RRGGBB</c> (7) or <c>#RRGGBBAA</c> (9).
    /// </summary>
    internal static int HexMaxLength(bool alpha) => alpha ? 9 : 7;

    /// <summary>
    /// Whether <paramref name="c"/> may replace the selection <paramref name="selStart"/>..
    /// <paramref name="selEnd"/> of <paramref name="text"/>. Position rules (a sign or <c>#</c>
    /// only first, one of each) are judged on the text as it would be after the insert.
    /// </summary>
    internal static bool Accepts(UiCharFilter filter, string? text, int selStart, int selEnd, char c)
    {
        if (filter == UiCharFilter.None) return true;
        string s = text ?? "";
        if (selStart < 0) selStart = 0;
        if (selEnd > s.Length) selEnd = s.Length;
        if (selEnd < selStart) selEnd = selStart;
        string before = s.Substring(0, selStart);
        string after = s.Substring(selEnd);

        switch (filter)
        {
            case UiCharFilter.Integer:
            case UiCharFilter.Decimal:
                if (c >= '0' && c <= '9')
                    return !StartsWithSign(after) || before.Length > 0;
                if (c == '-' || c == '+')
                    return before.Length == 0 && !StartsWithSign(after);
                if (c == '.' && filter == UiCharFilter.Decimal)
                    return before.IndexOf('.') < 0 && after.IndexOf('.') < 0 && !StartsWithSign(after);
                return false;
            case UiCharFilter.Hex:
                if (c == '#')
                    return before.Length == 0 && !after.StartsWith("#", StringComparison.Ordinal);
                if (IsHexDigit(c))
                    return !after.StartsWith("#", StringComparison.Ordinal) || before.Length > 0;
                return false;
            default:
                return true;
        }
    }

    /// <summary>
    /// The characters of <paramref name="insert"/> the filter lets through when it is inserted
    /// over the selection, judged one at a time as if typed. <paramref name="rejected"/> counts the rest.
    /// </summary>
    internal static string Filter(
        UiCharFilter filter, string? text, int selStart, int selEnd, string? insert, out int rejected)
    {
        rejected = 0;
        string add = insert ?? "";
        if (filter == UiCharFilter.None || add.Length == 0) return add;
        string s = text ?? "";
        if (selStart < 0) selStart = 0;
        if (selEnd > s.Length) selEnd = s.Length;
        if (selEnd < selStart) selEnd = selStart;
        string head = s.Substring(0, selStart);
        string tail = s.Substring(selEnd);
        var kept = new StringBuilder(add.Length);
        foreach (char c in add)
        {
            string current = head + kept + tail;
            int at = head.Length + kept.Length;
            if (Accepts(filter, current, at, at, c)) kept.Append(c);
            else rejected++;
        }
        return kept.ToString();
    }

    /// <summary>The filter a value of <paramref name="type"/> types with in a plain text field.</summary>
    internal static UiCharFilter ForValueType(Type? type)
    {
        if (type == null) return UiCharFilter.None;
        if (type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(sbyte)
            || type == typeof(uint) || type == typeof(ulong) || type == typeof(ushort) || type == typeof(byte))
            return UiCharFilter.Integer;
        if (type == typeof(float) || type == typeof(double) || type == typeof(decimal))
            return UiCharFilter.Decimal;
        return UiCharFilter.None;
    }

    /// <summary>A number field's filter: whole numbers for 0 decimals, else decimals.</summary>
    internal static UiCharFilter ForDecimals(int decimals) =>
        decimals <= 0 ? UiCharFilter.Integer : UiCharFilter.Decimal;

    private static bool StartsWithSign(string s) => s.Length > 0 && (s[0] == '-' || s[0] == '+');

    private static bool IsHexDigit(char c) =>
        (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
}
