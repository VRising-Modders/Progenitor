using System;
using System.Globalization;
using System.Text;
using Progenitor.Ui;

namespace Progenitor.Networking;

/// <summary>
/// TEXTLIMIT1: how long a server-wide text value may be, and why. Game-free, so the server's
/// check, the client's pre-check, the admin field's cap and the tests are the same numbers.
///
/// <para><b>Characters.</b> The setting's own limit is the author's <c>MaxLength</c>, else the
/// default for its kind (<see cref="ModSettingsLayout.DefaultTextMaxLength"/> for a field,
/// <see cref="ModSettingsLayout.DefaultTextAreaMaxLength"/> for a text area). A server-wide value is
/// also capped at <see cref="WireTextBytes"/> characters, because the admin edit packet carries at
/// most that many UTF-8 bytes and a character takes at least one.</para>
///
/// <para><b>Bytes.</b> The value's UTF-8 form must fit the admin edit packet's text field
/// (<see cref="WireTextBytes"/>). That also guarantees the ConfigSync field
/// (<see cref="SyncFieldBytes"/>): BepInEx's cfg escape (<see cref="TomlEscape"/>) replaces a
/// one-byte character with a two-byte escape and leaves every other character as it is, so the
/// escaped form is at most twice the UTF-8 bytes. The server still measures the escaped form, so a change to either constant
/// cannot silently desync clients.</para>
///
/// <para>Counts are UTF-16 code units (<see cref="string.Length"/>), what the text field and the
/// local settings model count; a character outside the Basic Multilingual Plane counts two.</para>
/// </summary>
internal static class ServerSettingsLimits
{
    /// <summary>The admin edit packet's text field (<see cref="ServerSettingsWire.MaxTextBytes"/>).</summary>
    internal const int WireTextBytes = ServerSettingsWire.MaxTextBytes;

    /// <summary>The ConfigSync value field, after BepInEx's cfg escape (<see cref="ConfigSyncWire.MaxFieldBytes"/>).</summary>
    internal const int SyncFieldBytes = ConfigSyncWire.MaxFieldBytes;

    private static readonly UTF8Encoding Utf8 = new(false, false);

    /// <summary>The setting's own limit: the author's cap when set (1 or more), else the default for its kind.</summary>
    internal static int Declared(int? maxLength, bool multiline) =>
        maxLength is int max && max > 0
            ? max
            : multiline ? ModSettingsLayout.DefaultTextAreaMaxLength : ModSettingsLayout.DefaultTextMaxLength;

    /// <summary>The most characters a server-wide text value takes: the declared limit, never above <see cref="WireTextBytes"/>.</summary>
    internal static int Effective(int? maxLength, bool multiline) => Math.Min(Declared(maxLength, multiline), WireTextBytes);

    /// <summary>UTF-8 bytes of <paramref name="text"/> (a lone surrogate counts as the 3-byte replacement).</summary>
    internal static int Utf8Bytes(string? text) => string.IsNullOrEmpty(text) ? 0 : Utf8.GetByteCount(text);

    /// <summary>UTF-8 bytes of the value as ConfigSync sends it (BepInEx's cfg escape applied).</summary>
    internal static int EscapedBytes(string? text) => Utf8Bytes(TomlEscape(text));

    /// <summary>
    /// Null when <paramref name="text"/> fits every limit of a server-wide text value declared with
    /// <paramref name="maxLength"/> / <paramref name="multiline"/>; otherwise the reason, worded for the
    /// row's help line. Checked in this order: characters, packet bytes, ConfigSync bytes.
    /// </summary>
    internal static string? Check(string? text, int? maxLength, bool multiline)
    {
        text ??= "";
        int limit = Effective(maxLength, multiline);
        if (text.Length > limit) return TooLong(limit, text.Length);
        int bytes = Utf8Bytes(text);
        if (bytes > WireTextBytes) return TooManyBytes(bytes);
        int escaped = EscapedBytes(text);
        if (escaped > SyncFieldBytes) return TooLargeToShare(escaped);
        if (!SurvivesCfg(text)) return BackslashError;
        return null;
    }

    /// <summary>
    /// True when <paramref name="text"/> comes back unchanged from BepInEx's cfg escape and unescape —
    /// the form ConfigSync sends and the cfg stores. BepInEx writes a backslash as itself but reads
    /// <c>\n</c>, <c>\t</c>, <c>\\</c> and the like as escapes, so <c>C:\new\tools</c> would reach
    /// clients (and the next load) as <c>C:</c>, a line break, <c>ew</c>, a Tab and <c>ools</c>.
    /// </summary>
    internal static bool SurvivesCfg(string? text) =>
        string.IsNullOrEmpty(text) || string.Equals(TomlUnescape(TomlEscape(text)), text, StringComparison.Ordinal);

    /// <summary>The refusal for a value that would not survive the cfg escape (<see cref="SurvivesCfg"/>).</summary>
    internal const string BackslashError =
        "The value has a backslash before a letter the config file reads as an escape (such as \\n or \\t), "
        + "so it would not be saved as typed. Use / instead, or put a space after the backslash.";

    /// <summary>The character refusal: <c>The value is longer than 1,000 characters (1,023); shorten it to 1,000.</c></summary>
    internal static string TooLong(int limit, int length)
    {
        string max = limit.ToString("N0", CultureInfo.InvariantCulture);
        return "The value is longer than " + max + " characters (" + length.ToString("N0", CultureInfo.InvariantCulture)
            + "); shorten it to " + max + ".";
    }

    /// <summary>The packet refusal, for text whose characters take more than one byte each.</summary>
    internal static string TooManyBytes(int bytes) =>
        "The value is too long to send: " + bytes.ToString("N0", CultureInfo.InvariantCulture) + " of "
        + WireTextBytes.ToString("N0", CultureInfo.InvariantCulture)
        + " bytes (an accented or non-Latin character takes 2 to 4).";

    /// <summary>The ConfigSync refusal (unreachable while the packet check holds; kept so it fails closed).</summary>
    internal static string TooLargeToShare(int bytes) =>
        "The value is too long to share with players: " + bytes.ToString("N0", CultureInfo.InvariantCulture) + " of "
        + SyncFieldBytes.ToString("N0", CultureInfo.InvariantCulture) + " bytes once saved.";

    /// <summary>
    /// The string form BepInEx 6 writes for a <see cref="string"/> config value
    /// (<c>TomlTypeConverter</c>'s private <c>Escape</c>, read from this machine's
    /// <c>BepInEx.Core.dll</c>): NUL, BEL, BS, Tab, LF, VT, FF, CR, <c>'</c> and <c>"</c> become a
    /// backslash and a letter; a backslash is written as itself (BepInEx's own quirk: a typed
    /// <c>\n</c> comes back as a line break). Used to measure, never to write.
    /// </summary>
    internal static string TomlEscape(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(text!.Length + 2);
        foreach (char c in text)
        {
            switch (c)
            {
                case '\0': sb.Append("\\0"); break;
                case '\a': sb.Append("\\a"); break;
                case '\b': sb.Append("\\b"); break;
                case '\t': sb.Append("\\t"); break;
                case '\n': sb.Append("\\n"); break;
                case '\v': sb.Append("\\v"); break;
                case '\f': sb.Append("\\f"); break;
                case '\r': sb.Append("\\r"); break;
                case '\'': sb.Append("\\'"); break;
                case '\\': sb.Append('\\'); break;
                case '"': sb.Append("\\\""); break;
                default: sb.Append(c); break;
            }
        }

        return sb.ToString();
    }

    /// <summary>BepInEx 6's matching <c>Unescape</c> (the read side), for the round-trip tests.</summary>
    internal static string TomlUnescape(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        var sb = new StringBuilder(text!.Length);
        int i = 0;
        while (i < text.Length)
        {
            int slash = text.IndexOf('\\', i);
            if (slash < 0 || slash == text.Length - 1) slash = text.Length;
            sb.Append(text, i, slash - i);
            if (slash >= text.Length) break;
            char c = text[slash + 1];
            switch (c)
            {
                case '0': sb.Append('\0'); break;
                case 'a': sb.Append('\a'); break;
                case 'b': sb.Append('\b'); break;
                case 't': sb.Append('\t'); break;
                case 'n': sb.Append('\n'); break;
                case 'v': sb.Append('\v'); break;
                case 'f': sb.Append('\f'); break;
                case 'r': sb.Append('\r'); break;
                case '\'': sb.Append('\''); break;
                case '"': sb.Append('"'); break;
                case '\\': sb.Append('\\'); break;
                default: sb.Append('\\').Append(c); break;
            }

            i = slash + 2;
        }

        return sb.ToString();
    }
}
