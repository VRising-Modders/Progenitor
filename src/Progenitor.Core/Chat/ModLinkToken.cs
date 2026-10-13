using System;
using System.Collections.Generic;

namespace Progenitor.Chat;

/// <summary>
/// Chat stage 3 (mod links): the plain-text mod link a player puts in game chat: <c>[mod:Namespace-Name]</c> or
/// <c>[mod:Namespace-Name@1.2.3]</c>, the Thunderstore package full name with an optional version. It stays
/// readable as plain text for players without Progenitor; a Progenitor client draws it as a link that opens
/// the mod in the Mods menu (<c>ModLinks.Open</c>). Game-free.
///
/// <para>Chat text is untrusted, so the token is strict: the namespace and the name are each 1 to 64 of
/// <c>[A-Za-z0-9_]</c> joined by one <c>-</c>, and the version is 1 to 4 dot-separated groups of digits.
/// Anything else, a space, a second <c>-</c> or a missing <c>]</c> included, is plain text, and only the
/// validated package id and version ever leave this class. A line yields at most <see cref="MaxPerLine"/>
/// links.</para>
/// </summary>
internal static class ModLinkToken
{
    /// <summary>The text that opens a token.</summary>
    internal const string Prefix = "[mod:";

    /// <summary>The text that closes a token.</summary>
    internal const char Suffix = ']';

    /// <summary>The most links one chat line yields; later tokens on the line stay plain text.</summary>
    internal const int MaxPerLine = 8;

    /// <summary>The longest namespace or package name, in characters.</summary>
    internal const int MaxPartLength = 64;

    /// <summary>The most dot-separated groups a version has (<c>1.2.3.4</c>).</summary>
    internal const int MaxVersionParts = 4;

    /// <summary>The most digits in one version group.</summary>
    internal const int MaxVersionDigits = 9;

    /// <summary>The longest token: prefix, two parts and the dash, <c>@</c>, the longest version, suffix.</summary>
    internal const int MaxTokenLength = 5 + MaxPartLength * 2 + 1 + 1 + MaxVersionParts * MaxVersionDigits + (MaxVersionParts - 1) + 1;

    /// <summary>
    /// The token for <paramref name="package"/> (<c>Namespace-Name</c>) and the optional
    /// <paramref name="version"/>; a null or blank version leaves it out. Empty when the package or the
    /// version is not valid, so nothing malformed is ever offered to chat.
    /// </summary>
    internal static string Format(string package, string? version)
    {
        string p = (package ?? "").Trim();
        if (!IsPackage(p)) return "";
        string v = (version ?? "").Trim();
        if (v.Length == 0) return Prefix + p + Suffix;
        if (!IsVersion(v)) return "";
        return Prefix + p + "@" + v + Suffix;
    }

    /// <summary>
    /// Every valid token in <paramref name="text"/>, in order, at most <see cref="MaxPerLine"/>:
    /// <c>start</c> and <c>length</c> address the whole token as written (brackets included), with the
    /// validated package and version (null when the token has none). Never throws; null text has none.
    /// </summary>
    internal static IEnumerable<(int start, int length, string package, string? version)> Find(string text)
    {
        var found = new List<(int start, int length, string package, string? version)>();
        if (string.IsNullOrEmpty(text)) return found;
        int from = 0;
        while (found.Count < MaxPerLine && from < text.Length)
        {
            int start = text.IndexOf(Prefix, from, StringComparison.Ordinal);
            if (start < 0) break;
            if (TryParseAt(text, start, out int length, out string package, out string? version))
            {
                found.Add((start, length, package, version));
                from = start + length;
            }
            else
            {
                from = start + 1;
            }
        }

        return found;
    }

    /// <summary>
    /// Parse one token whole (<c>[mod:Ns-Name]</c> or <c>[mod:Ns-Name@1.2]</c>, nothing around it).
    /// False for anything else.
    /// </summary>
    internal static bool TryParse(string? token, out string package, out string? version)
    {
        package = "";
        version = null;
        if (string.IsNullOrEmpty(token) || !token!.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        return TryParseAt(token, 0, out int length, out package, out version) && length == token.Length;
    }

    /// <summary><c>Namespace-Name</c>: two parts of 1 to 64 <c>[A-Za-z0-9_]</c>, one dash between them.</summary>
    internal static bool IsPackage(string? package)
    {
        if (string.IsNullOrEmpty(package)) return false;
        int dash = package!.IndexOf('-');
        if (dash <= 0 || dash != package.LastIndexOf('-')) return false;
        return IsPart(package, 0, dash) && IsPart(package, dash + 1, package.Length - dash - 1);
    }

    /// <summary>1 to 4 dot-separated groups of 1 to 9 ASCII digits (<c>1</c>, <c>1.2.3</c>, <c>0.10.2.7</c>).</summary>
    internal static bool IsVersion(string? version)
    {
        if (string.IsNullOrEmpty(version)) return false;
        int parts = 0, digits = 0;
        foreach (char c in version!)
        {
            if (c == '.')
            {
                if (digits == 0) return false;
                parts++;
                digits = 0;
                continue;
            }

            if (c < '0' || c > '9' || ++digits > MaxVersionDigits) return false;
        }

        if (digits == 0) return false;
        return parts + 1 <= MaxVersionParts;
    }

    private static bool TryParseAt(string text, int start, out int length, out string package, out string? version)
    {
        length = 0;
        package = "";
        version = null;
        int body = start + Prefix.Length;
        int limit = Math.Min(text.Length, start + MaxTokenLength);
        int end = -1;
        for (int i = body; i < limit; i++)
        {
            char c = text[i];
            if (c == Suffix)
            {
                end = i;
                break;
            }

            // Only the token's own characters may come before the closing bracket.
            if (!IsWordChar(c) && c != '-' && c != '@' && c != '.') return false;
        }

        if (end <= body) return false;
        string inner = text.Substring(body, end - body);
        int at = inner.IndexOf('@');
        string p = at < 0 ? inner : inner.Substring(0, at);
        string? v = at < 0 ? null : inner.Substring(at + 1);
        if (!IsPackage(p)) return false;
        if (v != null && !IsVersion(v)) return false;
        package = p;
        version = v;
        length = end - start + 1;
        return true;
    }

    private static bool IsPart(string text, int start, int count)
    {
        if (count < 1 || count > MaxPartLength) return false;
        for (int i = start; i < start + count; i++)
            if (!IsWordChar(text[i])) return false;
        return true;
    }

    private static bool IsWordChar(char c) =>
        (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_';
}
