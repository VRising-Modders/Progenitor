using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Progenitor.WorldRestore;

/// <summary>
/// WORLDRESTORE1: the restored world's wipe clock (spec §3.5, decided 2026-10-06). The game resets a world at a start
/// once the local time has reached <c>GetResetDateTime</c>: the start date in <c>StartDate.json</c> plus
/// <c>ResetDaysInterval</c> days, moved forward to <c>DayOfReset</c> (research server-browser-filter-settings §5.7). A wipe
/// archive brings its own start date back, so restoring it can make the very next start wipe it again; the admin then picks
/// one of three answers (<see cref="WipeModes"/>). File-only: the reset formula mirrors Core's <c>ServerRules.ResetDate</c>
/// (Bootstrap cannot reference Core; the tests check that both agree).
/// </summary>
internal static class WorldRestoreWipe
{
    /// <summary>The game's <c>DayOfReset</c> words; index 0 (Any) does not move the date.</summary>
    internal static readonly IReadOnlyList<string> Days = new[] { "Any", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday" };

    /// <summary>The format the game writes <c>StartDate.json</c> in (invariant <c>MM/dd/yyyy</c>, no quotes).</summary>
    internal const string StartDateFormat = "MM/dd/yyyy";

    /// <summary>The start date in a <c>StartDate.json</c> text, or null (Core's <c>ServerRules.ParseStartDate</c>).</summary>
    internal static DateTime? ParseStartDate(string? text)
    {
        string t = (text ?? "").Trim().Trim('"');
        if (t.Length == 0) return null;
        return DateTime.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var d) ? d.Date : null;
    }

    internal static string FormatStartDate(DateTime day) => day.Date.ToString(StartDateFormat, CultureInfo.InvariantCulture);

    /// <summary>The scheduled reset date, or null when wipes are off (Core's <c>ServerRules.ResetDate</c>).</summary>
    internal static DateTime? ResetDate(DateTime start, int intervalDays, string? dayOfReset)
    {
        if (intervalDays <= 0) return null;
        var date = start.Date.AddDays(intervalDays);
        int index = -1;
        for (int i = 0; i < Days.Count; i++)
            if (string.Equals(Days[i], dayOfReset, StringComparison.OrdinalIgnoreCase)) index = i;
        if (index <= 0) return date;
        var want = (DayOfWeek)(index % 7);
        int add = ((int)want - (int)date.DayOfWeek + 7) % 7;
        return date.AddDays(add);
    }

    /// <summary>
    /// True when the next start would wipe a world that started on <paramref name="start"/>: wipes are on and the reset date
    /// is today or earlier, or wipes are on and the start date is not known (nothing rules a wipe out).
    /// </summary>
    internal static bool IsDue(DateTime? start, int intervalDays, string? dayOfReset, DateTime nowLocal, out DateTime? reset)
    {
        reset = null;
        if (intervalDays <= 0) return false;
        if (start is not { } s) return true;
        reset = ResetDate(s, intervalDays, dayOfReset);
        return reset is { } r && r <= nowLocal;
    }

    /// <summary>
    /// A picked wipe day as settings: the interval in days from <paramref name="start"/> and the <c>DayOfReset</c> to write
    /// (empty: keep it, when the picked day already falls on it; else <c>Any</c>). Null with <paramref name="error"/> when the
    /// day cannot be the next wipe (not after today, not after the start).
    /// </summary>
    internal static (int Interval, string Day)? ForDate(DateTime start, DateTime picked, string? currentDay, DateTime nowLocal, int maxDays,
        out string error)
    {
        error = "";
        var day = picked.Date;
        if (day <= nowLocal.Date)
        {
            error = "Pick a day after today: the server checks the wipe date at every start.";
            return null;
        }

        int interval = (int)(day - start.Date).TotalDays;
        if (interval < 1)
        {
            error = "Pick a day after the world's start date (" + start.ToString("MMM d, yyyy", CultureInfo.GetCultureInfo("en-US")) + ").";
            return null;
        }

        if (interval > maxDays)
        {
            error = "At most " + maxDays + " days after the world's start date.";
            return null;
        }

        string keep = ResetDate(start, interval, currentDay) == day ? "" : Days[0];
        return (interval, keep);
    }

    /// <summary>
    /// The smallest interval whose reset date is after today, for a world that started on <paramref name="start"/> (the
    /// prefilled date). Null when <paramref name="start"/> is in the future of the cap.
    /// </summary>
    internal static DateTime FirstFutureDate(DateTime start, DateTime nowLocal)
    {
        var tomorrow = nowLocal.Date.AddDays(1);
        var min = start.Date.AddDays(1);
        return tomorrow > min ? tomorrow : min;
    }

    /// <summary>
    /// Set top-level <paramref name="key"/> of a JSON object to <paramref name="literal"/> (raw JSON), keeping the rest of the
    /// file byte for byte: every occurrence of the key (case-insensitive) is replaced, or the key is added before the
    /// closing brace. Null when the text is not a JSON object.
    /// </summary>
    internal static byte[]? SetTopLevel(byte[]? json, string key, string literal)
    {
        byte[] lit = Encoding.UTF8.GetBytes(literal);
        if (json == null || json.Length == 0)
            return Encoding.UTF8.GetBytes("{\n  " + Quote(key) + ": " + literal + "\n}\n");
        int offset = json.Length >= 3 && json[0] == 0xEF && json[1] == 0xBB && json[2] == 0xBF ? 3 : 0;
        var spans = new List<(int Start, int End)>();
        int close = -1;
        try
        {
            var reader = new Utf8JsonReader(new ReadOnlySpan<byte>(json, offset, json.Length - offset),
                new JsonReaderOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return null;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == 0)
                {
                    close = offset + (int)reader.TokenStartIndex;
                    break;
                }

                if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1) continue;
                bool match = string.Equals(reader.GetString(), key, StringComparison.OrdinalIgnoreCase);
                if (!reader.Read()) return null;
                int start = offset + (int)reader.TokenStartIndex;
                if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray) reader.Skip();
                int end = offset + (int)reader.BytesConsumed;
                if (match) spans.Add((start, end));
            }
        }
        catch (JsonException) { return null; }

        if (close < 0) return null;
        var output = new List<byte>(json.Length + lit.Length + 32);
        if (spans.Count > 0)
        {
            int at = 0;
            foreach (var (s, e) in spans)
            {
                for (int i = at; i < s; i++) output.Add(json[i]);
                output.AddRange(lit);
                at = e;
            }

            for (int i = at; i < json.Length; i++) output.Add(json[i]);
            return output.ToArray();
        }

        int prev = close - 1;
        while (prev >= offset && json[prev] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') prev--;
        bool needComma = prev >= offset && json[prev] != (byte)'{' && json[prev] != (byte)',';
        for (int i = 0; i <= prev; i++) output.Add(json[i]);
        output.AddRange(Encoding.UTF8.GetBytes((needComma ? "," : "") + "\n  " + Quote(key) + ": " + literal + "\n"));
        for (int i = close; i < json.Length; i++) output.Add(json[i]);
        return output.ToArray();
    }

    private static string Quote(string s) => JsonSerializer.Serialize(s);
}
