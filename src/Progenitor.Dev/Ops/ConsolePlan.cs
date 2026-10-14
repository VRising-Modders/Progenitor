using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace Progenitor.Dev.Ops;

/// <summary>
/// The game-free half of the <c>console</c> op (<see cref="ConsoleOps"/>): argument parsing, the command name, and turning
/// the console's rich-text rows into plain lines. Tested in <c>BridgeConsoleTests</c>.
/// </summary>
internal static class ConsolePlan
{
    internal const string UsageMessage =
        "console needs args.text: one command line for the game's own console (for example \"adminauth\"). "
        + "Optional: args.waitMs (0-10000, default 1000: how long to collect the console's output after the command), "
        + "args.echo (default true: the console shows \"> <text>\" as if typed).";

    internal const int MaxTextLength = 512;
    internal const int DefaultWaitMs = 1000;
    internal const int MaxWaitMs = 10_000;

    /// <summary>The most output lines a reply carries (the newest are kept).</summary>
    internal const int MaxLines = 200;

    internal const int MaxLineLength = 2000;

    /// <summary>The prefix the game's console writes for a command it does not know (its color codes removed).</summary>
    internal const string NotFoundPrefix = "COMMAND NOT FOUND>";

    internal sealed class Request
    {
        internal string Text { get; init; } = "";

        internal string Command { get; init; } = "";

        internal int WaitMs { get; init; }

        internal bool Echo { get; init; }

        internal Dictionary<string, object?> Describe() => new(StringComparer.Ordinal)
        {
            ["text"] = Text,
            ["command"] = Command,
            ["waitMs"] = WaitMs,
            ["echo"] = Echo,
        };
    }

    internal static Request Parse(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object) throw new ArgumentException(UsageMessage);
        if (!args.TryGetProperty("text", out var t) || t.ValueKind != JsonValueKind.String)
            throw new ArgumentException(UsageMessage);
        string text = (t.GetString() ?? "").Trim();
        if (text.Length == 0) throw new ArgumentException("console: args.text is empty. " + UsageMessage);
        if (text.Length > MaxTextLength) throw new ArgumentException("console: args.text is at most " + MaxTextLength + " characters.");
        if (text.IndexOf('\n') >= 0 || text.IndexOf('\r') >= 0)
            throw new ArgumentException("console: args.text is one command line (no line breaks); send one call per command.");

        int waitMs = DefaultWaitMs;
        if (args.TryGetProperty("waitMs", out var w))
        {
            if (w.ValueKind != JsonValueKind.Number || !w.TryGetInt32(out waitMs) || waitMs < 0 || waitMs > MaxWaitMs)
                throw new ArgumentException("console: args.waitMs is a whole number from 0 to " + MaxWaitMs + ".");
        }

        bool echo = true;
        if (args.TryGetProperty("echo", out var e))
        {
            if (e.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new ArgumentException("console: args.echo is true or false.");
            echo = e.GetBoolean();
        }

        return new Request { Text = text, Command = CommandName(text), WaitMs = waitMs, Echo = echo };
    }

    /// <summary>The command word: the text up to the first space or tab (the console matches it without case).</summary>
    internal static string CommandName(string text)
    {
        text = text.Trim();
        int i = 0;
        while (i < text.Length && text[i] != ' ' && text[i] != '\t') i++;
        return text.Substring(0, i);
    }

    /// <summary>
    /// The rich-text tags the console's rows can carry (Unity's and TextMesh Pro's text styling). Only these are removed: a
    /// usage hint such as <c>&lt;playerName&gt;</c> is text and stays.
    /// </summary>
    internal static readonly HashSet<string> RichTextTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "color", "b", "i", "u", "size", "s", "sup", "sub", "mark", "alpha",
    };

    /// <summary>One console row as plain text: known rich-text tags (<c>&lt;color=…&gt;</c>, <c>&lt;/color&gt;</c>, <c>&lt;b&gt;</c>…) removed.</summary>
    internal static string Plain(string? row)
    {
        if (string.IsNullOrEmpty(row)) return "";
        var sb = new StringBuilder(row!.Length);
        int i = 0;
        while (i < row.Length)
        {
            char c = row[i];
            if (c == '<')
            {
                int close = row.IndexOf('>', i + 1);
                // A tag is a name (optionally after '/') with no space before '=' or '>': "<color=#ff0000>", "</color>", "<b>".
                if (close > i + 1 && IsTag(row, i + 1, close))
                {
                    i = close + 1;
                    continue;
                }
            }

            sb.Append(c);
            i++;
        }

        string s = sb.ToString().TrimEnd();
        return s.Length > MaxLineLength ? s.Substring(0, MaxLineLength) + "…" : s;
    }

    private static bool IsTag(string s, int start, int end)
    {
        int i = start;
        bool closing = s[i] == '/';
        if (closing) i++;
        int nameStart = i;
        while (i < end && char.IsLetter(s[i])) i++;
        if (i == nameStart || !RichTextTags.Contains(s.Substring(nameStart, i - nameStart))) return false;
        // "<color=#ff0000>", "<b>", "</color>"; a closing tag takes no value.
        return i == end || (!closing && s[i] == '=');
    }

    /// <summary>Plain lines from rows, dropping empty ones, keeping the newest <see cref="MaxLines"/>.</summary>
    internal static List<string> Lines(IEnumerable<string?> rows, out bool truncated)
    {
        var lines = new List<string>();
        foreach (var row in rows)
        {
            string p = Plain(row);
            if (p.Length > 0) lines.Add(p);
        }

        truncated = lines.Count > MaxLines;
        if (truncated) lines.RemoveRange(0, lines.Count - MaxLines);
        return lines;
    }

    /// <summary>True when the console answered that it does not know the command.</summary>
    internal static bool SaysNotFound(IEnumerable<string> lines)
    {
        foreach (var l in lines)
            if (l.StartsWith(NotFoundPrefix, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// Where the new messages start in a message list that may have dropped old entries meanwhile: just after the last
    /// message seen before the command (<paramref name="lastBefore"/>, found by identity in <paramref name="ids"/>). With
    /// no message before, everything is new; when that message has been dropped, so has everything older, and every
    /// message left is new.
    /// </summary>
    internal static int NewStart(IReadOnlyList<IntPtr> ids, IntPtr lastBefore)
    {
        if (lastBefore == IntPtr.Zero) return 0;
        for (int i = ids.Count - 1; i >= 0; i--)
            if (ids[i] == lastBefore) return i + 1;
        return 0;
    }
}
