using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Progenitor.Ui;

/// <summary>
/// JOINLINKS1: one web link found in untrusted plain text. <see cref="Start"/> and <see cref="Length"/>
/// address the characters as written (what is drawn underlined); <see cref="Url"/> is the normalized
/// target (<c>https://</c> added to a scheme-less link), which is the only thing that ever opens.
/// </summary>
internal readonly struct UiTextLink
{
    internal UiTextLink(int index, int start, int length, string text, string url)
    {
        Index = index;
        Start = start;
        Length = length;
        Text = text ?? "";
        Url = url ?? "";
    }

    /// <summary>The link's position among the links of its text (0 for the first).</summary>
    internal int Index { get; }

    internal int Start { get; }

    internal int Length { get; }

    /// <summary>The link exactly as written.</summary>
    internal string Text { get; }

    /// <summary>The normalized http(s) URL that opens.</summary>
    internal string Url { get; }

    public override string ToString() => Index.ToString(CultureInfo.InvariantCulture) + ":" + Text + " -> " + Url;
}

/// <summary>
/// JOINLINKS1: web links in untrusted plain text (a server message), game-free so the detection, the
/// normalization and the escaping are tested. A cell asks for it with <see cref="CellSpec.Linkify"/>; the
/// runtime then draws the text with <see cref="Markup"/>: every character outside a link goes through
/// <see cref="UiRichText.Literal"/> exactly as before, and only the kit's own <c>link</c>, <c>color</c> and
/// <c>u</c> tags around each (escaped) link are live. Nothing the text itself contains becomes markup.
///
/// <para>What is a link: <c>http://</c> or <c>https://</c> followed by non-space characters; a
/// <c>www.</c> host; a bare <c>host.tld/path</c> whose host has a dot and an alphabetic TLD of 2–24
/// letters and is followed by a <c>/</c> (so <c>1.2.3</c>, <c>12:30</c> and <c>file.dll</c> are not).
/// Trailing <c>.,;:!?</c> and an unbalanced closing <c>)</c> or <c>]</c> are left out. A candidate longer
/// than <see cref="MaxUrlLength"/>, or holding a control or bidi-override character, is not a link.</para>
/// </summary>
internal static class UiLinks
{
    /// <summary>A longer link is left as plain text.</summary>
    internal const int MaxUrlLength = 2048;

    /// <summary>The pale blue a link is drawn in (<see cref="MenuPalette.LinkText"/>).</summary>
    internal static string ColorHex => Hex(MenuPalette.LinkText);

    /// <summary>The links of <paramref name="text"/>, in order. Empty for none.</summary>
    internal static IReadOnlyList<UiTextLink> Find(string? text)
    {
        var found = new List<UiTextLink>();
        if (string.IsNullOrEmpty(text)) return found;
        string s = text!;
        int i = 0;
        while (i < s.Length)
        {
            if (!StartsHere(s, i))
            {
                i++;
                continue;
            }

            int end = TokenEnd(s, i);
            if (TryLink(s, i, end, out int length, out string url))
            {
                found.Add(new UiTextLink(found.Count, i, length, s.Substring(i, length), url));
                i += length;
                continue;
            }

            // Not a link here; a later boundary in the same run may start one ("see:(https://…)").
            i++;
        }

        return found;
    }

    /// <summary>
    /// The normalized URL of a single candidate (as <see cref="Find"/> would match it at its start, after
    /// trimming), or null when it is not a link.
    /// </summary>
    internal static string? Normalize(string? candidate)
    {
        if (string.IsNullOrEmpty(candidate)) return null;
        var links = Find(candidate);
        return links.Count > 0 && links[0].Start == 0 ? links[0].Url : null;
    }

    /// <summary>
    /// True only for an absolute <c>http</c> or <c>https</c> URL with a host and no whitespace, control or
    /// bidi character, at most <see cref="MaxUrlLength"/> long: the last check before anything opens.
    /// </summary>
    internal static bool IsOpenable(string? url)
    {
        if (string.IsNullOrEmpty(url) || url!.Length > MaxUrlLength + 8) return false;
        foreach (char c in url)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c) || IsBidi(c)) return false;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;
        return uri.Host.Length > 0;
    }

    /// <summary>
    /// The TMP string for <paramref name="text"/>: every character escaped (<see cref="UiRichText.Literal"/>),
    /// with each link wrapped in the kit's own <c>&lt;link="n"&gt;&lt;color=…&gt;&lt;u&gt;</c>. The link
    /// ids are the links' positions (0, 1, …), which is also TMP's link order.
    /// </summary>
    internal static string Markup(string? text, IReadOnlyList<UiTextLink>? links = null)
    {
        if (string.IsNullOrEmpty(text)) return "";
        links ??= Find(text);
        if (links.Count == 0) return UiRichText.Literal(text);
        string color = ColorHex;
        var sb = new StringBuilder(text!.Length + links.Count * 48);
        int at = 0;
        for (int n = 0; n < links.Count; n++)
        {
            var link = links[n];
            if (link.Start < at || link.Start + link.Length > text.Length) continue;
            sb.Append(UiRichText.Literal(text.Substring(at, link.Start - at)));
            sb.Append("<link=\"").Append(n.ToString(CultureInfo.InvariantCulture)).Append("\"><color=").Append(color).Append("><u>");
            sb.Append(UiRichText.Literal(text.Substring(link.Start, link.Length)));
            sb.Append("</u></color></link>");
            at = link.Start + link.Length;
        }

        sb.Append(UiRichText.Literal(text.Substring(at)));
        return sb.ToString();
    }

    /// <summary>The links of a readback: <c>{index, text, url}</c> each.</summary>
    internal static List<object?> Describe(IReadOnlyList<UiTextLink> links, int firstIndex = 0)
    {
        var list = new List<object?>(links.Count);
        foreach (var l in links)
        {
            list.Add(new Dictionary<string, object?>
            {
                ["index"] = firstIndex + l.Index,
                ["text"] = l.Text,
                ["url"] = l.Url,
            });
        }

        return list;
    }

    /// <summary>A bidi override, embedding or isolate, or a left-to-right / right-to-left mark.</summary>
    internal static bool IsBidi(char c) =>
        c == '‎' || c == '‏' || (c >= '‪' && c <= '‮') || (c >= '⁦' && c <= '⁩');

    // ---- detection ------------------------------------------------------------------------------

    /// <summary>A link may start at <paramref name="i"/>: the text's start, or after a character that cannot be part of a URL or word.</summary>
    private static bool StartsHere(string s, int i)
    {
        if (!char.IsLetterOrDigit(s[i])) return false;
        if (i == 0) return true;
        char p = s[i - 1];
        if (char.IsLetterOrDigit(p)) return false;
        return "._-/@%+~#=&?\\".IndexOf(p) < 0;
    }

    /// <summary>The end (exclusive) of the run of characters a link may span: up to whitespace, <c>&lt;</c>, <c>&gt;</c> or <c>"</c>.</summary>
    private static int TokenEnd(string s, int i)
    {
        int j = i;
        while (j < s.Length)
        {
            char c = s[j];
            if (char.IsWhiteSpace(c) || c == '<' || c == '>' || c == '"') break;
            j++;
        }

        return j;
    }

    private static bool TryLink(string s, int start, int end, out int length, out string url)
    {
        length = 0;
        url = "";
        int len = Trim(s, start, end - start);
        if (len <= 0) return false;
        string candidate = s.Substring(start, len);
        if (candidate.Length > MaxUrlLength) return false;
        foreach (char c in candidate)
        {
            if (char.IsControl(c) || IsBidi(c)) return false;
        }

        string normalized;
        if (StartsWithIgnoreCase(candidate, "https://") || StartsWithIgnoreCase(candidate, "http://"))
        {
            int scheme = candidate.IndexOf("://", StringComparison.Ordinal);
            string rest = candidate.Substring(scheme + 3);
            if (rest.Length == 0 || !char.IsLetterOrDigit(rest[0])) return false;
            normalized = candidate.Substring(0, scheme).ToLowerInvariant() + "://" + rest;
        }
        else if (StartsWithIgnoreCase(candidate, "www."))
        {
            int slash = HostEnd(candidate);
            if (!IsHost(candidate.Substring(0, slash))) return false;
            normalized = "https://" + candidate;
        }
        else
        {
            int slash = HostEnd(candidate);
            // A bare host only with a path after it: discord.gg/abc, not file.dll or 1.2.3.
            if (slash >= candidate.Length || candidate[slash] != '/') return false;
            if (!IsHost(candidate.Substring(0, slash))) return false;
            normalized = "https://" + candidate;
        }

        if (!IsOpenable(normalized)) return false;
        length = len;
        url = normalized;
        return true;
    }

    /// <summary>Where the host of a scheme-less candidate ends: its first <c>/</c>, <c>?</c> or <c>#</c>.</summary>
    private static int HostEnd(string candidate)
    {
        int i = 0;
        while (i < candidate.Length && candidate[i] != '/' && candidate[i] != '?' && candidate[i] != '#') i++;
        return i;
    }

    /// <summary>
    /// <c>label.label…tld</c> (optionally <c>:port</c>): ASCII letters, digits and inner hyphens, at least one
    /// dot, the last label 2–24 letters.
    /// </summary>
    private static bool IsHost(string host)
    {
        int colon = host.IndexOf(':');
        if (colon >= 0)
        {
            string port = host.Substring(colon + 1);
            if (port.Length == 0 || port.Length > 5) return false;
            foreach (char c in port)
                if (c < '0' || c > '9') return false;
            host = host.Substring(0, colon);
        }

        string[] labels = host.Split('.');
        if (labels.Length < 2) return false;
        foreach (string label in labels)
        {
            if (label.Length == 0 || label.Length > 63 || label[0] == '-' || label[label.Length - 1] == '-') return false;
            foreach (char c in label)
            {
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-';
                if (!ok) return false;
            }
        }

        string tld = labels[labels.Length - 1];
        if (tld.Length < 2 || tld.Length > 24) return false;
        foreach (char c in tld)
            if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z'))) return false;
        return true;
    }

    /// <summary>The candidate's length without trailing <c>.,;:!?</c> and unbalanced closing <c>)</c> / <c>]</c>.</summary>
    private static int Trim(string s, int start, int length)
    {
        while (length > 0)
        {
            char last = s[start + length - 1];
            if (".,;:!?".IndexOf(last) >= 0)
            {
                length--;
                continue;
            }

            if (last == ')' && Count(s, start, length, ')') > Count(s, start, length, '('))
            {
                length--;
                continue;
            }

            if (last == ']' && Count(s, start, length, ']') > Count(s, start, length, '['))
            {
                length--;
                continue;
            }

            break;
        }

        return length;
    }

    private static int Count(string s, int start, int length, char c)
    {
        int n = 0;
        for (int i = start; i < start + length; i++)
            if (s[i] == c) n++;
        return n;
    }

    private static bool StartsWithIgnoreCase(string s, string prefix) => s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

    private static string Hex(PaletteColor c) =>
        "#" + ((int)Math.Round(c.R * 255f)).ToString("x2", CultureInfo.InvariantCulture)
            + ((int)Math.Round(c.G * 255f)).ToString("x2", CultureInfo.InvariantCulture)
            + ((int)Math.Round(c.B * 255f)).ToString("x2", CultureInfo.InvariantCulture);
}
