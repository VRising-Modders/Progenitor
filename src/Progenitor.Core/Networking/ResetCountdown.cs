using System;
using System.Globalization;
using System.Text;

namespace Progenitor.Networking;

/// <summary>
/// JOINFLOW1: the reset notification's arithmetic and the server message's tokens, game-free.
///
/// <list type="bullet">
/// <item><c>{reset}</c>: the live countdown at an automatic precision (RESETWIPE1: <c>3d</c> while a day or more away, then <c>17h</c>, then
/// <c>42m 05s</c> in the last hour); leading zero units are dropped and a remainder smaller than
/// the precision reads <c>&lt;1m</c>. After a one-time reset has passed it reads the passed text.</item>
/// <item><c>{reset:date}</c>: the reset's date and time in the <b>player's</b> time zone.</item>
/// <item><c>{reset:relative}</c>: <c>in 3 days</c>, <c>in 5 hours</c>, or <c>today</c> under an hour.</item>
/// <item>Any other <c>{…}</c>, and every token while no reset is set, renders literally.</item>
/// </list>
///
/// <para><b>Repeats roll in the server's calendar.</b> "Every 1 day at 06:00" stays at 06:00 server time
/// across a daylight-saving change: the next occurrence is found by adding whole days (or weeks) to the
/// local wall-clock time and converting back to UTC, never by adding 24-hour spans. A local time that does
/// not exist on that day (spring forward) moves forward by the gap; an ambiguous one (fall back) takes the
/// first occurrence.</para>
/// </summary>
public static class ResetCountdown
{
    public const string TokenCountdown = "{reset}";
    public const string TokenDate = "{reset:date}";
    public const string TokenRelative = "{reset:relative}";

    /// <summary>TOKENTIPS1: every token <see cref="Render(string?, ResetSchedule?, DateTime, TimeZoneInfo?, TimeZoneInfo?)"/> replaces (the tips list exactly these).</summary>
    public static readonly string[] Tokens = { TokenCountdown, TokenDate, TokenRelative };

    /// <summary>What <c>{reset}</c> reads once a one-time reset has passed, unless the admin set another text.</summary>
    public const string DefaultPassedText = "now";

    private const string IsoFormat = "yyyy-MM-ddTHH:mm:ssZ";

    /// <summary>Parse an ISO 8601 UTC time (<c>2026-10-10T18:00:00Z</c>, or any round-trip form with an offset).</summary>
    public static bool TryParseUtc(string? text, out DateTime utc)
    {
        utc = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text!.Trim();
        if (DateTime.TryParseExact(text, IsoFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out utc))
            return true;
        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto))
        {
            utc = dto.UtcDateTime;
            return true;
        }

        return false;
    }

    /// <summary>Format a UTC time as the stored ISO 8601 string (seconds, <c>Z</c>).</summary>
    public static string FormatUtc(DateTime utc) =>
        DateTime.SpecifyKind(utc.Kind == DateTimeKind.Local ? utc.ToUniversalTime() : utc, DateTimeKind.Utc)
            .ToString(IsoFormat, CultureInfo.InvariantCulture);

    /// <summary>
    /// The reset that matters at <paramref name="nowUtc"/>: the stored time while it is still ahead; for a
    /// repeating reset whose time has passed, the first occurrence after now. <paramref name="passed"/> is
    /// true only for a one-time reset that has passed. False when no reset is set.
    /// </summary>
    public static bool TryNext(ResetSchedule? schedule, DateTime nowUtc, TimeZoneInfo? serverZone, out DateTime nextUtc, out bool passed)
    {
        nextUtc = default;
        passed = false;
        if (schedule == null || !TryParseUtc(schedule.NextUtc, out var first)) return false;
        nextUtc = first;
        if (nowUtc < first) return true;
        if (schedule.Repeat == ResetRepeat.None)
        {
            passed = true;
            return true;
        }

        nextUtc = RollForward(first, schedule.Repeat, schedule.Every, nowUtc, serverZone ?? TimeZoneInfo.Utc);
        return true;
    }

    /// <summary>
    /// The first occurrence strictly after <paramref name="nowUtc"/> of <paramref name="firstUtc"/> repeated
    /// every <paramref name="every"/> days or weeks in <paramref name="zone"/>'s calendar.
    /// </summary>
    public static DateTime RollForward(DateTime firstUtc, ResetRepeat repeat, int every, DateTime nowUtc, TimeZoneInfo zone)
    {
        if (repeat == ResetRepeat.None || nowUtc < firstUtc) return firstUtc;
        int stepDays = Math.Max(1, every) * (repeat == ResetRepeat.Weeks ? 7 : 1);
        zone ??= TimeZoneInfo.Utc;
        var localFirst = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(firstUtc, DateTimeKind.Utc), zone);
        // A first guess from the elapsed days, then step forward: the guess can be one step short
        // around a daylight-saving change, never more.
        long elapsedDays = (long)Math.Floor((nowUtc - firstUtc).TotalDays);
        long steps = Math.Max(0, elapsedDays / stepDays);
        DateTime candidate = Occurrence(localFirst, steps * stepDays, zone);
        int guard = 0;
        while (candidate <= nowUtc && guard++ < 1000)
        {
            steps++;
            candidate = Occurrence(localFirst, steps * stepDays, zone);
        }

        return candidate;
    }

    /// <summary>
    /// The most recent reset at or before <paramref name="nowUtc"/> (the start of the current reset period),
    /// or null when none has passed yet or no reset is set. A one-time reset is its own time once passed;
    /// a repeating one rolls in the server's calendar exactly as <see cref="RollForward"/> does.
    /// </summary>
    public static DateTime? LastPassed(ResetSchedule? schedule, DateTime nowUtc, TimeZoneInfo? serverZone)
    {
        if (schedule == null || !TryParseUtc(schedule.NextUtc, out var first) || nowUtc < first) return null;
        if (schedule.Repeat == ResetRepeat.None) return first;
        var zone = serverZone ?? TimeZoneInfo.Utc;
        int stepDays = Math.Max(1, schedule.Every) * (schedule.Repeat == ResetRepeat.Weeks ? 7 : 1);
        var localFirst = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(first, DateTimeKind.Utc), zone);
        long elapsedDays = (long)Math.Floor((nowUtc - first).TotalDays);
        long steps = Math.Max(0, elapsedDays / stepDays);
        // The guess may be one step past or short around a daylight-saving change: settle on the last
        // occurrence that is not after now.
        int guard = 0;
        while (steps > 0 && Occurrence(localFirst, steps * stepDays, zone) > nowUtc && guard++ < 1000) steps--;
        while (Occurrence(localFirst, (steps + 1) * stepDays, zone) <= nowUtc && guard++ < 2000) steps++;
        return Occurrence(localFirst, steps * stepDays, zone);
    }

    private static DateTime Occurrence(DateTime localFirst, long addDays, TimeZoneInfo zone)
    {
        double days = Math.Min(addDays, 3_650_000);
        var local = DateTime.SpecifyKind(localFirst.AddDays(days), DateTimeKind.Unspecified);
        return LocalToUtc(local, zone, out _);
    }

    /// <summary>
    /// Convert a wall-clock time in <paramref name="zone"/> to UTC. A time inside a spring-forward gap
    /// moves forward by the gap (<paramref name="note"/> says so); an ambiguous fall-back time takes the
    /// first occurrence (the earlier instant).
    /// </summary>
    public static DateTime LocalToUtc(DateTime local, TimeZoneInfo zone, out string note)
    {
        note = "";
        zone ??= TimeZoneInfo.Utc;
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local))
        {
            // Move forward by the length of the gap (the rule's daylight delta: 02:30 in a one-hour gap
            // becomes 03:30), then, should that still be invalid, a minute at a time.
            var moved = local + GapLength(zone, local);
            int guard = 0;
            while (zone.IsInvalidTime(moved) && guard++ < 24 * 60) moved = moved.AddMinutes(1);
            note = local.ToString("HH:mm", CultureInfo.InvariantCulture) + " does not exist on that day (clocks move forward); using "
                + moved.ToString("HH:mm", CultureInfo.InvariantCulture) + ".";
            return DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeToUtc(moved, zone), DateTimeKind.Utc);
        }

        if (zone.IsAmbiguousTime(local))
        {
            var offsets = zone.GetAmbiguousTimeOffsets(local);
            // The first occurrence is the one with the larger offset (still on daylight time).
            var offset = offsets[0] > offsets[offsets.Length - 1] ? offsets[0] : offsets[offsets.Length - 1];
            note = local.ToString("HH:mm", CultureInfo.InvariantCulture) + " happens twice on that day (clocks move back); using the first.";
            return DateTime.SpecifyKind(local - offset, DateTimeKind.Utc);
        }

        return DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeToUtc(local, zone), DateTimeKind.Utc);
    }

    // The daylight delta of the rule in force, so 02:30 in a one-hour gap becomes 03:30 (not 03:00).
    private static TimeSpan GapLength(TimeZoneInfo zone, DateTime local)
    {
        try
        {
            foreach (var rule in zone.GetAdjustmentRules())
            {
                if (local.Date < rule.DateStart || local.Date > rule.DateEnd) continue;
                var delta = rule.DaylightDelta.Duration();
                if (delta > TimeSpan.Zero) return delta;
            }
        }
        catch { /* fall back to a minute walk */ }

        return TimeSpan.Zero;
    }

    /// <summary>
    /// The countdown at <paramref name="precision"/>: <c>3d</c>, <c>3d 4h</c>, <c>3d 4h 17m</c>,
    /// <c>3d 4h 17m 05s</c>. Leading zero units are dropped (<c>4h 17m</c>); a remainder below the
    /// precision reads <c>&lt;1d</c>, <c>&lt;1h</c>, <c>&lt;1m</c> or <c>&lt;1s</c>. Floors, never rounds up.
    /// </summary>
    public static string FormatCountdown(TimeSpan remaining, ResetPrecision precision)
    {
        if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
        long totalSeconds = (long)Math.Floor(remaining.TotalSeconds);
        long days = totalSeconds / 86400;
        long hours = totalSeconds / 3600 % 24;
        long minutes = totalSeconds / 60 % 60;
        long seconds = totalSeconds % 60;

        var units = new (long Value, string Unit)[]
        {
            (days, "d"), (hours, "h"), (minutes, "m"), (seconds, "s"),
        };
        int last = (int)precision;
        var parts = new System.Collections.Generic.List<string>(4);
        for (int u = 0; u <= last; u++)
        {
            var (value, unit) = units[u];
            if (parts.Count == 0 && value == 0) continue;
            bool pad = unit == "s" && parts.Count > 0;
            parts.Add(value.ToString(pad ? "00" : "0", CultureInfo.InvariantCulture) + unit);
        }

        if (parts.Count == 0) return "<1" + UnitOf(precision);
        return string.Join(" ", parts);
    }

    /// <summary>
    /// RESETWIPE1 (decided 2026-10-06): the countdown's precision is automatic: days while a day or more away
    /// (<c>3d</c>), then hours (<c>17h</c>), then minutes and seconds in the last hour (<c>42m 05s</c>). The stored
    /// <c>ResetPrecision</c> is no longer drawn; it still travels in the join section for earlier clients.
    /// </summary>
    public static ResetPrecision AutoPrecision(TimeSpan remaining) =>
        remaining >= TimeSpan.FromDays(1) ? ResetPrecision.Day
        : remaining >= TimeSpan.FromHours(1) ? ResetPrecision.Hour
        : ResetPrecision.Second;

    /// <summary><see cref="FormatCountdown(TimeSpan, ResetPrecision)"/> at <see cref="AutoPrecision"/>.</summary>
    public static string FormatCountdown(TimeSpan remaining) => FormatCountdown(remaining, AutoPrecision(remaining));

    /// <summary>
    /// RESETWIPE1: the precision <c>{reset:date}</c> is drawn at: always the date and the time to the minute (a game wipe's
    /// 00:00 in the server's zone is another hour in a player's).
    /// </summary>
    public const ResetPrecision DatePrecision = ResetPrecision.Minute;

    private static string UnitOf(ResetPrecision precision) => precision switch
    {
        ResetPrecision.Day => "d",
        ResetPrecision.Hour => "h",
        ResetPrecision.Second => "s",
        _ => "m",
    };

    /// <summary><c>in 3 days</c> (a day or more), <c>in 5 hours</c> (an hour or more), else <c>today</c>.</summary>
    public static string FormatRelative(TimeSpan remaining)
    {
        if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
        long days = (long)Math.Floor(remaining.TotalDays);
        if (days >= 1) return "in " + days.ToString(CultureInfo.InvariantCulture) + (days == 1 ? " day" : " days");
        long hours = (long)Math.Floor(remaining.TotalHours);
        if (hours >= 1) return "in " + hours.ToString(CultureInfo.InvariantCulture) + (hours == 1 ? " hour" : " hours");
        return "today";
    }

    /// <summary>
    /// The reset in <paramref name="zone"/> (the player's): <c>Sat, Oct 10, 6:00 PM</c> at minute
    /// precision, <c>Sat, Oct 10</c> at day precision, seconds at second precision, then the offset
    /// (<c>UTC-04:00</c>). US English, invariant digits.
    /// </summary>
    public static string FormatDate(DateTime utc, TimeZoneInfo zone, ResetPrecision precision)
    {
        zone ??= TimeZoneInfo.Utc;
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone);
        var us = CultureInfo.GetCultureInfo("en-US");
        string date = local.ToString("ddd, MMM d", us);
        if (precision == ResetPrecision.Day) return date;
        string time = precision == ResetPrecision.Second
            ? local.ToString("h:mm:ss tt", us)
            : local.ToString("h:mm tt", us);
        // The offset of the instant, not of the wall-clock time: an ambiguous fall-back hour has two.
        return date + ", " + time + " (" + OffsetLabel(zone.GetUtcOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc))) + ")";
    }

    /// <summary>What a reset token reads while no reset is set.</summary>
    public const string NotScheduledText = "not scheduled";

    /// <summary>
    /// <c>UTC-04:00 (Eastern Daylight Time)</c>: the offset and the zone's name in force at <paramref name="utc"/>
    /// (its daylight name during daylight-saving time), as the date picker labels a zone.
    /// </summary>
    public static string ZoneLabel(TimeZoneInfo? zone, DateTime utc)
    {
        zone ??= TimeZoneInfo.Utc;
        var at = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        string name;
        try { name = zone.IsDaylightSavingTime(at) ? zone.DaylightName : zone.StandardName; }
        catch { name = zone.Id; }
        if (string.IsNullOrWhiteSpace(name)) name = zone.Id;
        string offset = OffsetLabel(zone.GetUtcOffset(at));
        return string.IsNullOrWhiteSpace(name) || name == offset ? offset : offset + " (" + name + ")";
    }

    /// <summary><c>UTC</c>, <c>UTC+02:00</c>, <c>UTC-04:00</c>, <c>UTC+05:30</c>.</summary>
    public static string OffsetLabel(TimeSpan offset)
    {
        if (offset == TimeSpan.Zero) return "UTC";
        string sign = offset < TimeSpan.Zero ? "-" : "+";
        var abs = offset.Duration();
        return "UTC" + sign + ((int)abs.TotalHours).ToString("00", CultureInfo.InvariantCulture) + ":" + abs.Minutes.ToString("00", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The server message with its tokens replaced. Plain text in and out: the caller draws it with rich
    /// text off. <paramref name="hasLiveCountdown"/> is true when the result contains a ticking
    /// <c>{reset}</c>, so a view refreshes it (once a second) only while it is visible and only then.
    /// </summary>
    public static string Render(
        string? message,
        ResetSchedule? schedule,
        DateTime nowUtc,
        TimeZoneInfo? serverZone,
        TimeZoneInfo? playerZone,
        out bool hasLiveCountdown) =>
        Render(message, schedule, nowUtc, serverZone, playerZone, out hasLiveCountdown, out _);

    /// <summary>
    /// Same; <paramref name="hasTime"/> is true when a token produced a time: a countdown, a date or a relative time
    /// (not <c>not scheduled</c>, not the passed text). The message page's "Times shown in your local time." line
    /// shows only then (requested 2026-10-05).
    /// </summary>
    public static string Render(
        string? message,
        ResetSchedule? schedule,
        DateTime nowUtc,
        TimeZoneInfo? serverZone,
        TimeZoneInfo? playerZone,
        out bool hasLiveCountdown,
        out bool hasTime)
    {
        hasLiveCountdown = false;
        hasTime = false;
        message ??= "";
        if (message.IndexOf('{') < 0) return message;
        bool set = TryNext(schedule, nowUtc, serverZone, out var nextUtc, out bool passed);
        var sb = new StringBuilder(message.Length + 16);
        int i = 0;
        while (i < message.Length)
        {
            char c = message[i];
            if (c == '{' && !set)
            {
                // Live 2026-10-04: with no reset set the tokens were drawn raw ("{reset:relative}"). A known
                // token reads "not scheduled"; any other {…} stays literal.
                string? token = Matches(message, i, TokenCountdown) ? TokenCountdown
                    : Matches(message, i, TokenDate) ? TokenDate
                    : Matches(message, i, TokenRelative) ? TokenRelative
                    : null;
                if (token != null)
                {
                    sb.Append(NotScheduledText);
                    i += token.Length;
                    continue;
                }
            }

            if (c == '{' && set)
            {
                if (Matches(message, i, TokenCountdown))
                {
                    if (passed) sb.Append(schedule!.PassedText);
                    else
                    {
                        sb.Append(FormatCountdown(nextUtc - nowUtc));
                        hasLiveCountdown = true;
                        hasTime = true;
                    }

                    i += TokenCountdown.Length;
                    continue;
                }

                if (Matches(message, i, TokenDate))
                {
                    sb.Append(FormatDate(nextUtc, playerZone ?? TimeZoneInfo.Local, DatePrecision));
                    hasTime = true;
                    i += TokenDate.Length;
                    continue;
                }

                if (Matches(message, i, TokenRelative))
                {
                    sb.Append(passed ? schedule!.PassedText : FormatRelative(nextUtc - nowUtc));
                    if (!passed) hasTime = true;
                    i += TokenRelative.Length;
                    continue;
                }
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }

    /// <summary>Same, without the live-countdown flag.</summary>
    public static string Render(string? message, ResetSchedule? schedule, DateTime nowUtc, TimeZoneInfo? serverZone, TimeZoneInfo? playerZone) =>
        Render(message, schedule, nowUtc, serverZone, playerZone, out _);

    private static bool Matches(string text, int at, string token) =>
        string.CompareOrdinal(text, at, token, 0, token.Length) == 0;
}
