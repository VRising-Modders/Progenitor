using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Progenitor.Commands;

/// <summary>
/// Splits a command-argument string into tokens and converts tokens to the supported parameter
/// types. Kept as a public static surface so the rules can be checked without spinning up a world.
/// </summary>
/// <remarks>
/// Tokenize rules:
/// <list type="number">
/// <item>Tokens are separated by any <see cref="char.IsWhiteSpace(char)"/> character.</item>
/// <item>A token that starts with <c>"</c> runs until the next unescaped <c>"</c>; the quotes are
/// not part of the value. An unclosed quote takes the remainder of the string.</item>
/// <item>Inside quotes, a backslash escapes the next character: <c>\"</c> is a literal quote,
/// <c>\\</c> is a literal backslash, and <c>\X</c> is just <c>X</c>. A trailing backslash at
/// end-of-string is kept.</item>
/// <item>Quotes only start a quoted token at a token boundary. A quote in the middle of an
/// unquoted token is a literal character.</item>
/// <item>Unquoted tokens do not interpret backslash. <c>a\ b</c> is two tokens, <c>a\</c> and
/// <c>b</c>.</item>
/// <item>An empty quoted span <c>""</c> produces an empty-string token.</item>
/// </list>
/// </remarks>
public static class CommandParser
{
    /// <summary>A token plus the span it occupied in the original string (for remainder capture).</summary>
    internal readonly struct ArgToken
    {
        public readonly string Value;
        public readonly int Start;
        public readonly int End;

        public ArgToken(string value, int start, int end)
        {
            Value = value;
            Start = start;
            End = end;
        }
    }

    /// <summary>Splits <paramref name="input"/> according to the rules on this type.</summary>
    public static IReadOnlyList<string> Tokenize(string? input)
    {
        var tokens = TokenizeDetailed(input);
        if (tokens.Count == 0) return Array.Empty<string>();
        var values = new string[tokens.Count];
        for (int i = 0; i < tokens.Count; i++) values[i] = tokens[i].Value;
        return values;
    }

    internal static List<ArgToken> TokenizeDetailed(string? input)
    {
        var list = new List<ArgToken>();
        if (string.IsNullOrEmpty(input)) return list;

        int i = 0;
        int n = input!.Length;
        while (i < n)
        {
            while (i < n && char.IsWhiteSpace(input[i])) i++;
            if (i >= n) break;

            int start = i;
            if (input[i] == '"')
            {
                i++;
                var sb = new StringBuilder();
                while (i < n)
                {
                    char c = input[i++];
                    if (c == '"') break;
                    if (c == '\\')
                    {
                        if (i < n) sb.Append(input[i++]);
                        else sb.Append('\\');
                        continue;
                    }
                    sb.Append(c);
                }
                list.Add(new ArgToken(sb.ToString(), start, i));
            }
            else
            {
                int j = i + 1;
                while (j < n && !char.IsWhiteSpace(input[j])) j++;
                list.Add(new ArgToken(input.Substring(i, j - i), i, j));
                i = j;
            }
        }

        return list;
    }

    internal static bool IsSupportedType(Type type) =>
        type == typeof(string)
        || type == typeof(int)
        || type == typeof(long)
        || type == typeof(float)
        || type == typeof(double)
        || type == typeof(bool);

    /// <summary>
    /// Converts a token to <paramref name="type"/>. <paramref name="specificity"/> is higher for
    /// narrower types so <c>5</c> prefers <see cref="int"/> over <see cref="string"/>.
    /// </summary>
    internal static bool TryConvert(string token, Type type, out object? value, out int specificity)
    {
        value = null;
        specificity = 0;
        if (type == typeof(string))
        {
            value = token;
            specificity = 1;
            return true;
        }

        if (type == typeof(bool))
        {
            if (IsBool(token, out bool b, out int spec))
            {
                value = b;
                specificity = spec;
                return true;
            }
            return false;
        }

        if (type == typeof(int))
        {
            if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i))
            {
                value = i;
                specificity = 40;
                return true;
            }
            return false;
        }

        if (type == typeof(long))
        {
            if (long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l))
            {
                value = l;
                specificity = 30;
                return true;
            }
            return false;
        }

        if (type == typeof(float))
        {
            if (float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out float f))
            {
                value = f;
                specificity = 20;
                return true;
            }
            return false;
        }

        if (type == typeof(double))
        {
            if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
            {
                value = d;
                specificity = 15;
                return true;
            }
            return false;
        }

        return false;
    }

    private static bool IsBool(string token, out bool value, out int specificity)
    {
        value = false;
        specificity = 0;
        if (token.Equals("true", StringComparison.OrdinalIgnoreCase)
            || token.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || token.Equals("on", StringComparison.OrdinalIgnoreCase))
        {
            value = true;
            specificity = 50;
            return true;
        }
        if (token.Equals("false", StringComparison.OrdinalIgnoreCase)
            || token.Equals("no", StringComparison.OrdinalIgnoreCase)
            || token.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            value = false;
            specificity = 50;
            return true;
        }
        if (token == "1")
        {
            value = true;
            specificity = 10;
            return true;
        }
        if (token == "0")
        {
            value = false;
            specificity = 10;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Verifies tokenizer and conversion rules. Called from <see cref="CommandSystem.Install"/> so
    /// a broken parser shows up in the log without needing a unit-test project.
    /// </summary>
    /// <returns>True when every case passed.</returns>
    public static bool RunSelfCheck(out string report)
    {
        var failures = new List<string>();

        void Expect(string input, params string[] expected)
        {
            var got = Tokenize(input);
            if (got.Count != expected.Length)
            {
                failures.Add($"Tokenize({Show(input)}) count {got.Count}, expected {expected.Length}: [{Join(got)}]");
                return;
            }
            for (int i = 0; i < expected.Length; i++)
            {
                if (got[i] != expected[i])
                {
                    failures.Add($"Tokenize({Show(input)})[{i}] = {Show(got[i])}, expected {Show(expected[i])}");
                    return;
                }
            }
        }

        Expect("", Array.Empty<string>());
        Expect("a b c", "a", "b", "c");
        Expect("  a   b  ", "a", "b");
        Expect("\"hello world\"", "hello world");
        Expect("cmd \"hello world\" x", "cmd", "hello world", "x");
        Expect("\"a \\\"b\\\" c\"", "a \"b\" c");
        Expect("\"unclosed", "unclosed");
        Expect("\"\"", "");
        Expect("yes \"\" no", "yes", "", "no");
        Expect("a\\ b", "a\\", "b");
        Expect("\"\\\\\"", "\\");
        Expect("\"\\n\"", "n");
        Expect("one\ttwo", "one", "two");
        Expect("say\"hello\"", "say\"hello\"");

        void ExpectConvert<T>(string token, T expected, bool shouldMatch = true)
        {
            bool ok = TryConvert(token, typeof(T), out var value, out _);
            if (shouldMatch)
            {
                if (!ok) { failures.Add($"TryConvert<{typeof(T).Name}>({Show(token)}) failed"); return; }
                if (!Equals(value, expected)) failures.Add($"TryConvert<{typeof(T).Name}>({Show(token)}) = {value}, expected {expected}");
            }
            else if (ok)
            {
                failures.Add($"TryConvert<{typeof(T).Name}>({Show(token)}) unexpectedly succeeded with {value}");
            }
        }

        ExpectConvert("42", 42);
        ExpectConvert("42", 42L);
        ExpectConvert("-3.5", -3.5f);
        ExpectConvert("1e2", 100.0);
        ExpectConvert("true", true);
        ExpectConvert("No", false);
        ExpectConvert("hello", "hello");
        ExpectConvert<int>("hello", 0, shouldMatch: false);
        ExpectConvert<int>("3.2", 0, shouldMatch: false);
        ExpectConvert<bool>("maybe", false, shouldMatch: false);

        if (!TryConvert("5", typeof(int), out _, out int intSpec)
            || !TryConvert("5", typeof(string), out _, out int strSpec)
            || intSpec <= strSpec)
        {
            failures.Add("int should out-specify string for token '5'");
        }

        report = failures.Count == 0 ? "" : string.Join("\n", failures);
        return failures.Count == 0;
    }

    private static string Show(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static string Join(IReadOnlyList<string> items)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < items.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(Show(items[i]));
        }
        return sb.ToString();
    }
}
