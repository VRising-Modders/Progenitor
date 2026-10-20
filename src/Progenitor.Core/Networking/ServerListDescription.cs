using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Progenitor.Networking;

/// <summary>
/// SERVERDESC1 (docs/server-list-description.md): the server-list description, game-free. The admin's text,
/// then on its own line the Progenitor signature, within the game's 128 characters; the coarse reset tokens;
/// the Steam <c>desc{i}</c> chunk arithmetic; when to publish again; and the one-key rewrite of
/// <c>ServerHostSettings.json</c>.
/// </summary>
internal static class ServerListDescription
{
    /// <summary>The game's cap (<c>AppConstants.MAX_DESCRIPTION_LENGTH</c>; <c>ServerBootstrapSystem.SetServerDescription</c> cuts there).</summary>
    public const int MaxChars = 128;

    /// <summary>The fixed line after the admin's text. ASCII, no markup.</summary>
    public const string Signature = "Requires Progenitor: github.com/VRising-Modders/Progenitor";

    /// <summary>INDEX1: the line earlier builds wrote (Progenitor is no longer on Thunderstore); a seed drops it too.</summary>
    public const string LegacySignature = "Requires the Progenitor mod (thunderstore.io)";

    /// <summary>What ends an admin text shortened to fit.</summary>
    public const string Ellipsis = "…";

    /// <summary>Steam's chunk length (<c>PlatformSystemBase.SetServerDescription</c> writes <c>desc0</c>, <c>desc1</c>, …).</summary>
    public const int SteamChunkChars = 120;

    /// <summary>How often the rendered tokens are checked again (a change re-publishes; nothing else does).</summary>
    public static readonly TimeSpan RecheckInterval = TimeSpan.FromMinutes(1);

    /// <summary>The characters the signature line takes, its line break included.</summary>
    public static int SignatureCost => Signature.Length + 1;

    /// <summary>The room for the admin's (rendered) text: 128, less the signature line when it is on.</summary>
    public static int Room(bool signature) => signature ? MaxChars - SignatureCost : MaxChars;

    /// <summary>Line endings to <c>\n</c> and the text trimmed (what is stored and composed).</summary>
    public static string Normalize(string? text) =>
        (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Trim();

    /// <summary>
    /// The published text: the admin's <paramref name="rendered"/> text (tokens already filled in) shortened
    /// to its room with <see cref="Ellipsis"/> when it is longer, then the signature on its own line when
    /// <paramref name="signature"/> is on. The signature is never cut.
    /// </summary>
    public static Composition Compose(string? rendered, bool signature)
    {
        string text = Normalize(rendered);
        int room = Room(signature);
        bool cut = false;
        if (text.Length > room)
        {
            int keep = Math.Max(0, room - Ellipsis.Length);
            // Never split a surrogate pair.
            if (keep > 0 && char.IsHighSurrogate(text[keep - 1])) keep--;
            text = text.Substring(0, keep).TrimEnd() + Ellipsis;
            cut = true;
        }

        string composed = !signature ? text : text.Length == 0 ? Signature : text + "\n" + Signature;
        return new Composition(composed, Normalize(rendered).Length, room, cut);
    }

    /// <summary>The page's counter line for an admin's <paramref name="rendered"/> text (tokens filled in).</summary>
    public static string CounterLine(string? rendered, bool signature)
    {
        int length = Normalize(rendered).Length;
        int room = Room(signature);
        string tail = signature ? " (the signature line takes " + SignatureCost.ToString(CultureInfo.InvariantCulture) + ")" : "";
        if (length > room)
            return (length - room).ToString(CultureInfo.InvariantCulture) + " over " + room.ToString(CultureInfo.InvariantCulture)
                + tail + ": saving cuts the end of your text and adds …";
        int left = room - length;
        return left.ToString(CultureInfo.InvariantCulture) + " of " + room.ToString(CultureInfo.InvariantCulture)
            + (left == 1 ? " character left" : " characters left") + tail;
    }

    // ---- tokens ---------------------------------------------------------------------------------

    /// <summary>TOKENTIPS1: every token <see cref="Render"/> replaces (the tip lists exactly these).</summary>
    public static readonly string[] Tokens = { ResetCountdown.TokenCountdown, ResetCountdown.TokenDate, ResetCountdown.TokenRelative };

    /// <summary>
    /// The admin's text with the coarse reset tokens filled in from the server message's reset settings:
    /// <c>{reset:relative}</c> and <c>{reset}</c> read <c>in 3 days</c> / <c>in 5 hours</c> / <c>today</c> (a list
    /// row is drawn once, so a ticking countdown would only be stale); <c>{reset:date}</c> is the reset in the
    /// <b>server's</b> zone with its abbreviation (<c>Sat, Oct 10, 6:00 PM EDT</c>; the date alone at day
    /// precision). No reset set: <c>not scheduled</c>; a one-time reset that passed: its passed text. Any
    /// other <c>{…}</c> is left as typed.
    /// </summary>
    public static string Render(string? text, ResetSchedule? schedule, DateTime nowUtc, TimeZoneInfo? serverZone)
    {
        string message = text ?? "";
        if (message.IndexOf('{') < 0) return message;
        var zone = serverZone ?? TimeZoneInfo.Utc;
        bool set = ResetCountdown.TryNext(schedule, nowUtc, zone, out var nextUtc, out bool passed);
        var sb = new StringBuilder(message.Length + 16);
        int i = 0;
        while (i < message.Length)
        {
            string? token = message[i] != '{' ? null
                : Matches(message, i, ResetCountdown.TokenRelative) ? ResetCountdown.TokenRelative
                : Matches(message, i, ResetCountdown.TokenDate) ? ResetCountdown.TokenDate
                : Matches(message, i, ResetCountdown.TokenCountdown) ? ResetCountdown.TokenCountdown
                : null;
            if (token == null)
            {
                sb.Append(message[i]);
                i++;
                continue;
            }

            if (!set) sb.Append(ResetCountdown.NotScheduledText);
            else if (token == ResetCountdown.TokenDate) sb.Append(FormatServerDate(nextUtc, zone, ResetCountdown.DatePrecision));
            else sb.Append(passed ? schedule!.PassedText : ResetCountdown.FormatRelative(nextUtc - nowUtc));
            i += token.Length;
        }

        return sb.ToString();
    }

    /// <summary>True when <paramref name="text"/> holds a reset token (so the minute check can change it).</summary>
    public static bool HasTokens(string? text) =>
        !string.IsNullOrEmpty(text)
        && (text!.IndexOf(ResetCountdown.TokenCountdown, StringComparison.Ordinal) >= 0
            || text.IndexOf(ResetCountdown.TokenDate, StringComparison.Ordinal) >= 0
            || text.IndexOf(ResetCountdown.TokenRelative, StringComparison.Ordinal) >= 0);

    /// <summary><c>Sat, Oct 10, 6:00 PM EDT</c> in <paramref name="zone"/> (seconds are never shown; day precision: the date).</summary>
    public static string FormatServerDate(DateTime utc, TimeZoneInfo zone, ResetPrecision precision)
    {
        zone ??= TimeZoneInfo.Utc;
        var at = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        var local = TimeZoneInfo.ConvertTimeFromUtc(at, zone);
        var us = CultureInfo.GetCultureInfo("en-US");
        string date = local.ToString("ddd, MMM d", us);
        if (precision != ResetPrecision.Day) date += ", " + local.ToString("h:mm tt", us);
        return date + " " + ZoneAbbreviation(zone, at);
    }

    /// <summary>
    /// The zone's short name at <paramref name="utc"/>: <c>EDT</c>, <c>CET</c>, <c>UTC</c> for the zones in the
    /// table below (Windows and IANA ids), else the offset (<c>UTC+05:30</c>). Windows has no abbreviations,
    /// and initials of its long names mislead (<c>W. Europe Standard Time</c> is not WEST), so unknown zones
    /// get the offset.
    /// </summary>
    public static string ZoneAbbreviation(TimeZoneInfo? zone, DateTime utc)
    {
        zone ??= TimeZoneInfo.Utc;
        var at = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        bool dst;
        try { dst = zone.IsDaylightSavingTime(at); }
        catch { dst = false; }
        if (Abbreviations.TryGetValue(zone.Id ?? "", out var names)) return dst ? names.Daylight : names.Standard;
        TimeSpan offset;
        try { offset = zone.GetUtcOffset(at); }
        catch { offset = zone.BaseUtcOffset; }
        return ResetCountdown.OffsetLabel(offset);
    }

    private static readonly Dictionary<string, (string Standard, string Daylight)> Abbreviations = Table();

    private static Dictionary<string, (string Standard, string Daylight)> Table()
    {
        var t = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        void Add((string, string) names, params string[] ids)
        {
            foreach (string id in ids) t[id] = names;
        }

        Add(("UTC", "UTC"), "UTC", "Etc/UTC", "Etc/GMT", "Coordinated Universal Time", "UTC+00", "Etc/UCT");
        Add(("EST", "EDT"), "Eastern Standard Time", "America/New_York", "America/Toronto", "America/Detroit", "US/Eastern");
        Add(("CST", "CDT"), "Central Standard Time", "America/Chicago", "America/Winnipeg", "US/Central");
        Add(("MST", "MDT"), "Mountain Standard Time", "America/Denver", "America/Edmonton", "US/Mountain");
        Add(("MST", "MST"), "US Mountain Standard Time", "America/Phoenix");
        Add(("PST", "PDT"), "Pacific Standard Time", "America/Los_Angeles", "America/Vancouver", "US/Pacific");
        Add(("AKST", "AKDT"), "Alaskan Standard Time", "America/Anchorage");
        Add(("HST", "HST"), "Hawaiian Standard Time", "Pacific/Honolulu");
        Add(("AST", "ADT"), "Atlantic Standard Time", "America/Halifax");
        Add(("GMT", "BST"), "GMT Standard Time", "Europe/London");
        Add(("GMT", "IST"), "Europe/Dublin");
        Add(("CET", "CEST"), "W. Europe Standard Time", "Romance Standard Time", "Central Europe Standard Time",
            "Central European Standard Time", "Europe/Berlin", "Europe/Paris", "Europe/Madrid", "Europe/Rome",
            "Europe/Amsterdam", "Europe/Brussels", "Europe/Vienna", "Europe/Stockholm", "Europe/Oslo", "Europe/Copenhagen",
            "Europe/Warsaw", "Europe/Prague", "Europe/Budapest", "Europe/Zurich");
        Add(("EET", "EEST"), "GTB Standard Time", "E. Europe Standard Time", "FLE Standard Time", "Europe/Athens",
            "Europe/Bucharest", "Europe/Helsinki", "Europe/Kiev", "Europe/Kyiv", "Europe/Sofia", "Europe/Riga",
            "Europe/Tallinn", "Europe/Vilnius");
        Add(("MSK", "MSK"), "Russian Standard Time", "Europe/Moscow");
        Add(("AEST", "AEDT"), "AUS Eastern Standard Time", "Australia/Sydney", "Australia/Melbourne");
        Add(("AEST", "AEST"), "E. Australia Standard Time", "Australia/Brisbane");
        Add(("JST", "JST"), "Tokyo Standard Time", "Asia/Tokyo");
        Add(("KST", "KST"), "Korea Standard Time", "Asia/Seoul");
        Add(("NZST", "NZDT"), "New Zealand Standard Time", "Pacific/Auckland");
        return t;
    }

    private static bool Matches(string text, int at, string token) =>
        at + token.Length <= text.Length && string.CompareOrdinal(text, at, token, 0, token.Length) == 0;

    // ---- Steam chunks ---------------------------------------------------------------------------

    /// <summary>How many <c>desc{i}</c> keys the game's setter writes for <paramref name="text"/> (0 for empty).</summary>
    public static int SteamChunks(string? text) =>
        string.IsNullOrEmpty(text) ? 0 : (text!.Length + SteamChunkChars - 1) / SteamChunkChars;

    /// <summary>
    /// The <c>desc{i}</c> keys to set to <c>""</c> after publishing <paramref name="newText"/> when
    /// <paramref name="previousChunks"/> keys were written before: every index the new text no longer uses
    /// (the setter never clears old keys). With an empty new text that includes <c>desc0</c>.
    /// </summary>
    public static List<string> StaleSteamKeys(int previousChunks, string? newText)
    {
        var keys = new List<string>();
        for (int i = SteamChunks(newText); i < previousChunks; i++) keys.Add("desc" + i.ToString(CultureInfo.InvariantCulture));
        return keys;
    }

    // ---- the settings file ----------------------------------------------------------------------

    /// <summary>
    /// <paramref name="json"/> with its top-level <c>Description</c> set to <paramref name="description"/>,
    /// every other byte (keys, order, spacing, comments, a byte-order mark) kept; the key is added after the
    /// opening brace when absent. Null when <paramref name="json"/> is not a JSON object (the file is left alone).
    /// </summary>
    public static byte[]? WithDescription(byte[] json, string description)
    {
        if (json == null) return null;
        int start = json.Length >= 3 && json[0] == 0xEF && json[1] == 0xBB && json[2] == 0xBF ? 3 : 0;
        string encoded = JsonString(description ?? "");
        try
        {
            var reader = new Utf8JsonReader(new ReadOnlySpan<byte>(json, start, json.Length - start),
                new JsonReaderOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return null;
            int open = start + (int)reader.TokenStartIndex;
            long valueStart = -1, valueEnd = -1;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == 0) break;
                if (reader.TokenType == JsonTokenType.PropertyName && reader.CurrentDepth == 1)
                {
                    bool match = string.Equals(reader.GetString(), "Description", StringComparison.OrdinalIgnoreCase);
                    if (!reader.Read()) return null;
                    // A duplicated key's last occurrence is the one the game reads: edit that one.
                    if (match)
                    {
                        if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                        {
                            valueStart = reader.TokenStartIndex;
                            reader.Skip();
                            valueEnd = reader.BytesConsumed;
                        }
                        else
                        {
                            valueStart = reader.TokenStartIndex;
                            valueEnd = reader.BytesConsumed;
                        }
                    }
                    else if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                    {
                        reader.Skip();
                    }
                }
            }

            byte[] insert;
            int cutFrom, cutTo;
            if (valueStart >= 0)
            {
                insert = Encoding.UTF8.GetBytes(encoded);
                cutFrom = start + (int)valueStart;
                cutTo = start + (int)valueEnd;
            }
            else
            {
                // Add it first: `{` + newline + two spaces, the game's own layout. An empty object closes cleanly.
                bool empty = IsEmptyObject(json, open + 1);
                insert = Encoding.UTF8.GetBytes("\n  \"Description\": " + encoded + (empty ? "\n" : ","));
                cutFrom = cutTo = open + 1;
                if (empty)
                {
                    // Drop the whitespace between the braces so the result is `{\n  "Description": …\n}`.
                    int close = cutTo;
                    while (close < json.Length && json[close] != (byte)'}') close++;
                    cutTo = close;
                }
            }

            var result = new byte[cutFrom + insert.Length + (json.Length - cutTo)];
            Buffer.BlockCopy(json, 0, result, 0, cutFrom);
            Buffer.BlockCopy(insert, 0, result, cutFrom, insert.Length);
            Buffer.BlockCopy(json, cutTo, result, cutFrom + insert.Length, json.Length - cutTo);
            return result;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsEmptyObject(byte[] json, int from)
    {
        for (int i = from; i < json.Length; i++)
        {
            byte b = json[i];
            if (b == (byte)'}') return true;
            if (b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') continue;
            return false;
        }

        return false;
    }

    /// <summary>A JSON string literal, ASCII only (anything else as <c>\uXXXX</c>), so any reader takes it.</summary>
    public static string JsonString(string value)
    {
        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');
        foreach (char c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20 || c > 0x7E) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }

        sb.Append('"');
        return sb.ToString();
    }

    /// <summary>The <c>Description</c> a settings file holds now (null: none, or the file is not a JSON object).</summary>
    public static string? ReadDescription(byte[]? json)
    {
        if (json == null || json.Length == 0) return null;
        int start = json.Length >= 3 && json[0] == 0xEF && json[1] == 0xBB && json[2] == 0xBF ? 3 : 0;
        try
        {
            using var doc = JsonDocument.Parse(new ReadOnlyMemory<byte>(json, start, json.Length - start),
                new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            string? last = null;
            bool found = false;
            foreach (var p in doc.RootElement.EnumerateObject())
                if (string.Equals(p.Name, "Description", StringComparison.OrdinalIgnoreCase))
                {
                    found = true;
                    last = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : null;
                }
            return found ? last : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// Write <paramref name="description"/> into <paramref name="path"/> when it differs (idempotent). A missing
    /// file is created holding only that key, so every other value still comes from the layers below it. The
    /// write goes to a temporary file that then replaces the original.
    /// </summary>
    public static PersistOutcome Persist(string path, string description)
    {
        if (string.IsNullOrWhiteSpace(path)) return new PersistOutcome(PersistResult.NoTarget, "no settings file path is known");
        try
        {
            byte[]? before = File.Exists(path) ? File.ReadAllBytes(path) : null;
            byte[]? after;
            if (before == null) after = Encoding.UTF8.GetBytes("{\n  \"Description\": " + JsonString(description) + "\n}\n");
            else
            {
                if (ReadDescription(before) is { } current && string.Equals(current, description, StringComparison.Ordinal))
                    return new PersistOutcome(PersistResult.Unchanged, "");
                after = WithDescription(before, description);
                if (after == null) return new PersistOutcome(PersistResult.Failed, "the file is not a JSON object; left as it is");
            }

            string dir = Path.GetDirectoryName(path) ?? "";
            if (dir.Length > 0) Directory.CreateDirectory(dir);
            string temp = path + ".progenitor-tmp";
            File.WriteAllBytes(temp, after);
            File.Move(temp, path, overwrite: true);
            return new PersistOutcome(before == null ? PersistResult.Created : PersistResult.Written, "");
        }
        catch (Exception e)
        {
            return new PersistOutcome(PersistResult.Failed, e.Message);
        }
    }

    /// <summary>
    /// The admin text to start from on a server that has never had the row: the game's current description
    /// without a trailing signature line (one an earlier Progenitor composed), so it is not doubled.
    /// </summary>
    public static string SeedFrom(string? currentDescription)
    {
        string text = Normalize(currentDescription);
        foreach (string sig in new[] { Signature, LegacySignature })
        {
            if (text == sig) return "";
            if (text.EndsWith("\n" + sig, StringComparison.Ordinal))
                return text.Substring(0, text.Length - sig.Length - 1).TrimEnd();
        }

        return text;
    }

    /// <summary>
    /// The row's warning when the start command overrides the file, or empty. The live text still applies
    /// now; at each start the override wins until Progenitor applies this text again.
    /// </summary>
    public static string OverrideWarning(ServerHostSettingsOverride? found)
    {
        if (found is not { } o) return "";
        return (o.FromCommandLine ? "The start command sets " + o.Source : "The environment variable " + o.Source + " is set")
            + ": at each start it replaces the saved description until Progenitor applies this one again. Remove it to keep the file in charge.";
    }
}

/// <summary>One composed description.</summary>
internal readonly struct Composition
{
    internal Composition(string text, int adminLength, int room, bool cut)
    {
        Text = text;
        AdminLength = adminLength;
        Room = room;
        Cut = cut;
    }

    /// <summary>What is published and persisted (at most 128 characters).</summary>
    public string Text { get; }

    /// <summary>The rendered admin text's length before any cut.</summary>
    public int AdminLength { get; }

    /// <summary>The admin text's room.</summary>
    public int Room { get; }

    /// <summary>True when the admin text was shortened with an ellipsis.</summary>
    public bool Cut { get; }
}

internal enum PersistResult
{
    Unchanged,
    Written,
    Created,
    NoTarget,
    Failed,
}

internal readonly struct PersistOutcome
{
    internal PersistOutcome(PersistResult result, string error)
    {
        Result = result;
        Error = error ?? "";
    }

    public PersistResult Result { get; }

    public string Error { get; }

    public override string ToString() => Error.Length == 0 ? Result.ToString().ToLowerInvariant() : Result.ToString().ToLowerInvariant() + ": " + Error;
}

/// <summary>
/// SERVERDESC1: when to publish. Game-free: the server ticks it each frame with the time and a function that
/// composes the text now; it answers whether to publish (the first time, after an edit, or when the minute
/// check renders a different text) and remembers what was published.
/// </summary>
internal sealed class ServerListPublisher
{
    private DateTime _nextCheckUtc = DateTime.MinValue;
    private bool _dirty = true;

    /// <summary>The text last published (null: nothing yet).</summary>
    public string? Published { get; private set; }

    /// <summary>How many Steam <c>desc{i}</c> keys hold text now (what the next publish must clear past).</summary>
    public int SteamChunks { get; set; }

    /// <summary>An edit (or a forced apply): the next tick composes and publishes at once if the text differs.</summary>
    public void MarkDirty() => _dirty = true;

    /// <summary>
    /// True when <paramref name="compose"/> should run now (an edit, the first tick, or the minute check);
    /// the caller then calls <see cref="ShouldPublish"/>.
    /// </summary>
    public bool Due(DateTime nowUtc)
    {
        if (_dirty || nowUtc >= _nextCheckUtc)
        {
            _dirty = false;
            _nextCheckUtc = nowUtc + ServerListDescription.RecheckInterval;
            return true;
        }

        return false;
    }

    /// <summary>
    /// True when <paramref name="composed"/> should be published: nothing was published yet, the text changed,
    /// or the game's live value (<paramref name="live"/>, null when unknown) no longer holds what was published
    /// (a <c>ReloadSettings</c> re-read, another mod).
    /// </summary>
    public bool ShouldPublish(string composed, string? live) =>
        Published == null
        || !string.Equals(Published, composed, StringComparison.Ordinal)
        || (live != null && !string.Equals(live, composed, StringComparison.Ordinal));

    /// <summary>Record what the game now holds.</summary>
    public void Record(string composed) => Published = composed;

    /// <summary>Seed what Steam already holds (the game's own first publish of <paramref name="live"/>).</summary>
    public void SeedSteam(string? live) => SteamChunks = Math.Max(SteamChunks, ServerListDescription.SteamChunks(live));
}
