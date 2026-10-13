using System;
using System.Collections.Generic;
using System.Text;

namespace Progenitor.Ui;

/// <summary>One injected key press for the bridge's <c>mods_ui type via=keys</c>.</summary>
internal readonly struct UiTestChord
{
    public UiTestChord(UiKey key, bool ctrl, bool alt, bool shift, bool meta)
    {
        Key = key;
        Ctrl = ctrl;
        Alt = alt;
        Shift = shift;
        Meta = meta;
    }

    public UiKey Key { get; }
    public bool Ctrl { get; }
    public bool Alt { get; }
    public bool Shift { get; }
    public bool Meta { get; }

    public override string ToString() =>
        (Ctrl ? "Ctrl+" : "") + (Alt ? "Alt+" : "") + (Shift ? "Shift+" : "") + (Meta ? "Meta+" : "") + Key;
}

/// <summary>
/// TEXTTEST1: input the dev bridge queued for the focused field. The shown-input pump takes up to
/// <see cref="PerFrame"/> items a frame: <see cref="Text"/> joins the OS text drained from
/// Bootstrap's queue (so it meets the same control-character drop, field filter, limit and caret
/// code), and <see cref="Keys"/> go through <see cref="UiTypedKeys.Decide"/> as the physical US map
/// would. Injected text never counts as the OS queue proving itself. <see cref="Clipboard"/>, when
/// set, replaces the system clipboard for Ctrl+C / X / V, so a test never reads or overwrites the
/// player's clipboard; the bridge clears it after every call.
/// </summary>
internal static class UiTextInjection
{
    internal static readonly Queue<char> Text = new();
    internal static readonly Queue<UiTestChord> Keys = new();
    internal static int PerFrame = 1;
    internal static string? Clipboard;

    internal static int Pending => Text.Count + Keys.Count;

    internal static void Clear()
    {
        Text.Clear();
        Keys.Clear();
        PerFrame = 1;
        Clipboard = null;
    }
}

/// <summary>
/// TEXTTEST1: game-free parsing and generation for the bridge's text-field test ops
/// (<c>mods_ui type</c> / <c>paste</c>). Nothing here touches the game, the OS or the clipboard.
/// </summary>
internal static class UiTextTestInput
{
    /// <summary>The longest generated paste (<c>length</c> / <c>repeat</c>): 1,000,000 characters.</summary>
    internal const int MaxGeneratedLength = 1_000_000;

    /// <summary>The most input items one <c>type</c> call queues.</summary>
    internal const int MaxTypedItems = 20_000;

    /// <summary>
    /// Parse a chord such as <c>H</c>, <c>Shift+1</c>, <c>Ctrl+A</c>, <c>Enter</c>, <c>Left</c>,
    /// <c>Numpad5</c> or <c>Shift+/</c>. Modifiers (<c>Ctrl</c>/<c>Control</c>, <c>Alt</c>,
    /// <c>Shift</c>, <c>Meta</c>/<c>Win</c>/<c>Cmd</c>) come before the key, joined by <c>+</c>. A
    /// key written as a punctuation character is its US key (<c>+</c> is the <c>=</c> key, so the
    /// plus sign is <c>Shift++</c>). Case-insensitive.
    /// </summary>
    internal static bool TryParseChord(string? text, out UiTestChord chord, out string error)
    {
        chord = default;
        error = "";
        string s = (text ?? "").Trim();
        if (s.Length == 0)
        {
            error = "empty chord";
            return false;
        }

        var parts = new List<string>();
        int start = 0;
        for (int i = 0; i < s.Length; i++)
        {
            // A '+' that is the last character, or follows another '+', is the key itself.
            if (s[i] == '+' && i > start)
            {
                parts.Add(s.Substring(start, i - start).Trim());
                start = i + 1;
            }
        }
        parts.Add(s.Substring(start).Trim());

        bool ctrl = false, alt = false, shift = false, meta = false;
        for (int i = 0; i < parts.Count - 1; i++)
        {
            switch (parts[i].ToLowerInvariant())
            {
                case "ctrl": case "control": ctrl = true; break;
                case "alt": alt = true; break;
                case "shift": shift = true; break;
                case "meta": case "win": case "cmd": meta = true; break;
                default:
                    error = "unknown modifier '" + parts[i] + "' in '" + s + "'";
                    return false;
            }
        }

        if (!TryParseKey(parts[parts.Count - 1], out var key))
        {
            error = "unknown key '" + parts[parts.Count - 1] + "' in '" + s + "'";
            return false;
        }

        chord = new UiTestChord(key, ctrl, alt, shift, meta);
        return true;
    }

    internal static bool TryParseKey(string? name, out UiKey key)
    {
        key = UiKey.None;
        string n = (name ?? "").Trim();
        if (n.Length == 0) return false;
        if (n.Length == 1)
        {
            char c = n[0];
            if (c >= 'a' && c <= 'z') c = (char)(c - 32);
            if (c >= 'A' && c <= 'Z') { key = UiKey.A + (c - 'A'); return true; }
            switch (c)
            {
                case '0': key = UiKey.Digit0; return true;
                case '1': key = UiKey.Digit1; return true;
                case '2': key = UiKey.Digit2; return true;
                case '3': key = UiKey.Digit3; return true;
                case '4': key = UiKey.Digit4; return true;
                case '5': key = UiKey.Digit5; return true;
                case '6': key = UiKey.Digit6; return true;
                case '7': key = UiKey.Digit7; return true;
                case '8': key = UiKey.Digit8; return true;
                case '9': key = UiKey.Digit9; return true;
                case ' ': key = UiKey.Space; return true;
                case '`': key = UiKey.Backquote; return true;
                case '-': key = UiKey.Minus; return true;
                case '=': key = UiKey.Equals; return true;
                case '+': key = UiKey.Equals; return true;
                case '[': key = UiKey.LeftBracket; return true;
                case ']': key = UiKey.RightBracket; return true;
                case '\\': key = UiKey.Backslash; return true;
                case ';': key = UiKey.Semicolon; return true;
                case '\'': key = UiKey.Quote; return true;
                case ',': key = UiKey.Comma; return true;
                case '.': key = UiKey.Period; return true;
                case '/': key = UiKey.Slash; return true;
            }
            return false;
        }

        switch (n.ToLowerInvariant())
        {
            case "left": key = UiKey.LeftArrow; return true;
            case "right": key = UiKey.RightArrow; return true;
            case "up": key = UiKey.UpArrow; return true;
            case "down": key = UiKey.DownArrow; return true;
            case "esc": key = UiKey.Escape; return true;
            case "return": key = UiKey.Enter; return true;
            case "del": key = UiKey.Delete; return true;
            case "bksp": key = UiKey.Backspace; return true;
        }

        if (Enum.TryParse(n, ignoreCase: true, out UiKey parsed) && parsed != UiKey.None
            && Enum.IsDefined(typeof(UiKey), parsed) && !int.TryParse(n, out _))
        {
            key = parsed;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Deterministic test text of exactly <paramref name="length"/> characters: lowercase words of
    /// 2–9 letters separated by spaces, a period every few words and a line break about every
    /// 12 words, so a text area both wraps and breaks. Same <paramref name="seed"/>, same text.
    /// </summary>
    internal static string Generate(int length, int seed = 1)
    {
        if (length <= 0) return "";
        if (length > MaxGeneratedLength) length = MaxGeneratedLength;
        var sb = new StringBuilder(length + 16);
        uint state = (uint)seed * 2654435761u + 1u;
        uint Next()
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            return state;
        }

        int words = 0;
        while (sb.Length < length)
        {
            int letters = 2 + (int)(Next() % 8);
            for (int i = 0; i < letters; i++) sb.Append((char)('a' + (int)(Next() % 26)));
            words++;
            if (words % 12 == 0) sb.Append(".\n");
            else if (words % 5 == 0) sb.Append(". ");
            else sb.Append(' ');
        }
        sb.Length = length;
        return sb.ToString();
    }

    /// <summary><paramref name="text"/> repeated <paramref name="repeat"/> times, capped at <see cref="MaxGeneratedLength"/>.</summary>
    internal static string Repeat(string? text, int repeat)
    {
        string s = text ?? "";
        if (repeat <= 1 || s.Length == 0) return s;
        long total = (long)s.Length * repeat;
        int count = total > MaxGeneratedLength ? Math.Max(1, MaxGeneratedLength / s.Length) : repeat;
        var sb = new StringBuilder(s.Length * count);
        for (int i = 0; i < count; i++) sb.Append(s);
        return sb.ToString();
    }

    /// <summary>A short, stable fingerprint of a long text for readbacks (FNV-1a 64, hex).</summary>
    internal static string Hash(string? text)
    {
        ulong h = 14695981039346656037ul;
        foreach (char c in text ?? "")
        {
            h ^= c;
            h *= 1099511628211ul;
        }
        return h.ToString("x16");
    }
}
