using System;
using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>
/// P7b: how wide a chip or tab caption draws in the game's serif face, per character, so a short word is never cut. The
/// kit's mean-glyph estimate (<see cref="MenuPanelLayout.AverageGlyphEm"/>, 0.55 em for every character) under-measures
/// capitals and wide letters: live at 1920×1080 the tab <c>HUD</c> drew <c>H…</c> and the filter chip <c>Wood</c> drew
/// <c>Wo…</c> in chips sized from it. Here every character has its own advance in ems (capitals as the Install header's
/// table, <see cref="InstallColumns"/>; lowercase, digits and marks for a book serif), times <see cref="FaceScale"/> for
/// the game face's extra width, and an unknown character counts as a wide one. Game-free.
/// </summary>
internal static class UiChipsText
{
    /// <summary>How much wider the game's face draws than the table (the Install header's calibration, which keeps a margin).</summary>
    internal const float FaceScale = 1.15f;

    /// <summary>Any character not in the table (accented letters, other scripts) counts this wide.</summary>
    internal const float UnknownEm = 0.62f;

    private static readonly Dictionary<char, float> Em = new()
    {
        ['A'] = .67f, ['B'] = .64f, ['C'] = .65f, ['D'] = .66f, ['E'] = .57f, ['F'] = .56f, ['G'] = .68f,
        ['H'] = .71f, ['I'] = .27f, ['J'] = .55f, ['K'] = .64f, ['L'] = .54f, ['M'] = .87f, ['N'] = .71f,
        ['O'] = .69f, ['P'] = .63f, ['Q'] = .69f, ['R'] = .62f, ['S'] = .61f, ['T'] = .60f, ['U'] = .65f,
        ['V'] = .64f, ['W'] = .89f, ['X'] = .63f, ['Y'] = .60f, ['Z'] = .60f,
        ['a'] = .50f, ['b'] = .55f, ['c'] = .45f, ['d'] = .55f, ['e'] = .50f, ['f'] = .33f, ['g'] = .50f,
        ['h'] = .55f, ['i'] = .28f, ['j'] = .28f, ['k'] = .52f, ['l'] = .28f, ['m'] = .83f, ['n'] = .55f,
        ['o'] = .55f, ['p'] = .55f, ['q'] = .55f, ['r'] = .38f, ['s'] = .42f, ['t'] = .33f, ['u'] = .55f,
        ['v'] = .50f, ['w'] = .75f, ['x'] = .50f, ['y'] = .50f, ['z'] = .45f,
        ['0'] = .55f, ['1'] = .55f, ['2'] = .55f, ['3'] = .55f, ['4'] = .55f, ['5'] = .55f, ['6'] = .55f,
        ['7'] = .55f, ['8'] = .55f, ['9'] = .55f,
        [' '] = .28f, ['·'] = .30f, ['.'] = .27f, [','] = .27f, [':'] = .27f, [';'] = .27f, ['\''] = .22f,
        ['-'] = .35f, ['+'] = .58f, ['('] = .35f, [')'] = .35f, ['/'] = .35f, ['&'] = .72f, ['%'] = .80f,
        ['!'] = .30f, ['?'] = .45f, ['…'] = 1.0f,
    };

    /// <summary>The advance of <paramref name="c"/> in ems (before <see cref="FaceScale"/>).</summary>
    internal static float EmOf(char c) => Em.TryGetValue(c, out float em) ? em : UnknownEm;

    /// <summary>The width <paramref name="text"/> draws at <paramref name="fontSize"/>, in canvas units (its longest line).</summary>
    internal static float Width(string? text, float fontSize)
    {
        if (string.IsNullOrEmpty(text) || !(fontSize > 0f)) return 0f;
        float widest = 0f, line = 0f;
        foreach (char c in text!)
        {
            if (c == '\n')
            {
                widest = Math.Max(widest, line);
                line = 0f;
                continue;
            }

            if (c == '\r') continue;
            line += EmOf(c);
        }

        return Math.Max(widest, line) * fontSize * FaceScale;
    }

    /// <summary>
    /// <paramref name="text"/> unchanged when it fits <paramref name="room"/> at <paramref name="fontSize"/>, else its
    /// longest start that fits with an ellipsis after it (at least the ellipsis).
    /// </summary>
    internal static string Clip(string? text, float room, float fontSize)
    {
        text ??= "";
        if (Width(text, fontSize) <= room + .01f) return text;
        float ellipsis = EmOf('…') * fontSize * FaceScale;
        float used = 0f;
        int keep = 0;
        for (int i = 0; i < text.Length; i++)
        {
            float w = EmOf(text[i]) * fontSize * FaceScale;
            if (used + w + ellipsis > room + .01f) break;
            used += w;
            keep = i + 1;
        }

        return text.Substring(0, keep).TrimEnd() + "…";
    }
}
