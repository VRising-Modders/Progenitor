using System;
using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>
/// BRIDGEUI1: caret moves by line in a multi-line field for the bridge's UI-layer keys (Up, Down, Home, End), from the
/// field's line starts: the drawn lines when the text component has laid them out (wrapping included), else the text's
/// line breaks. Game-free. Home and End stay on the caret's line; Up and Down keep the column, clamped to the line.
/// </summary>
internal static class UiCaretLines
{
    /// <summary>The starts of the text's lines by line break (always begins with 0).</summary>
    internal static int[] BreakStarts(string? text)
    {
        var starts = new List<int> { 0 };
        string s = text ?? "";
        for (int i = 0; i < s.Length; i++)
            if (s[i] == '\n') starts.Add(i + 1);
        return starts.ToArray();
    }

    /// <summary>The line holding <paramref name="caret"/> (the last line whose start is at or before it).</summary>
    internal static int LineOf(int[] starts, int caret)
    {
        int line = 0;
        for (int i = 0; i < starts.Length; i++)
            if (starts[i] <= caret) line = i;
        return line;
    }

    /// <summary>Where line <paramref name="line"/> ends: before its line break, or at the next drawn line's start.</summary>
    internal static int LineEnd(string text, int[] starts, int line)
    {
        if (line + 1 >= starts.Length) return text.Length;
        int next = starts[line + 1];
        return next > 0 && next <= text.Length && text[next - 1] == '\n' ? next - 1 : next;
    }

    /// <summary>
    /// Whether a synthetic click places the caret at the clicked character: only on a field that already had the
    /// keyboard when the click pressed, and never while the kit's select-all of a first focus is still pending (it runs
    /// on the next typed-key pass, so a caret placed now would be undone or, worse, put the caret at 0 before typing).
    /// A first click keeps the kit's select-all, so typing replaces the value.
    /// </summary>
    internal static bool ClickPlacesCaret(bool hadKeyboardAtPress, bool selectAllPending) => hadKeyboardAtPress && !selectAllPending;

    /// <summary>Whether <paramref name="key"/> is one of the line moves.</summary>
    internal static bool IsLineKey(UiKey key) => key is UiKey.Home or UiKey.End or UiKey.UpArrow or UiKey.DownArrow;

    /// <summary>
    /// The caret after <paramref name="key"/> from <paramref name="caret"/>, or -1 when the key is not a line move.
    /// Up on the first line goes to the start, Down on the last to the end.
    /// </summary>
    internal static int Move(UiKey key, string? text, int[]? starts, int caret)
    {
        string s = text ?? "";
        if (starts == null || starts.Length == 0) starts = new[] { 0 };
        caret = Math.Max(0, Math.Min(s.Length, caret));
        int line = LineOf(starts, caret);
        int column = caret - starts[line];
        switch (key)
        {
            case UiKey.Home: return starts[line];
            case UiKey.End: return LineEnd(s, starts, line);
            case UiKey.UpArrow:
                if (line == 0) return 0;
                return Math.Min(starts[line - 1] + column, LineEnd(s, starts, line - 1));
            case UiKey.DownArrow:
                if (line + 1 >= starts.Length) return s.Length;
                return Math.Min(starts[line + 1] + column, LineEnd(s, starts, line + 1));
            default:
                return -1;
        }
    }
}
