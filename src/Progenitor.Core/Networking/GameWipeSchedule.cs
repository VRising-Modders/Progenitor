using System;
using System.Globalization;

namespace Progenitor.Networking;

/// <summary>
/// RESETWIPE1 (decided 2026-10-06): the game's own scheduled wipes as the server message's <c>{reset}</c> countdown
/// reads them, game-free. The server rules "Scheduled wipes every (days)" (<c>ResetDaysInterval</c>) and "Wipe on"
/// (<c>DayOfReset</c>) with the world's <c>StartDate.json</c> give the next wipe date exactly as the game's
/// <c>GetResetDateTime</c> does (<see cref="ServerRules.ResetDate"/>, research server-browser-filter-settings.md §5.7):
/// the start date plus the interval, moved forward to the next such weekday. The game wipes at the first server start
/// once the server's local clock has reached that date, so the countdown runs to 00:00 of that date in the server's zone.
/// The values are the next start's (the ones that wipe), as the Server rules rows show them.
/// </summary>
internal sealed class GameWipeSchedule
{
    internal GameWipeSchedule(int intervalDays, string? dayOfReset, DateTime? nextDate)
    {
        IntervalDays = intervalDays < 0 ? 0 : intervalDays;
        DayOfReset = string.IsNullOrWhiteSpace(dayOfReset) ? "Any" : dayOfReset!.Trim();
        NextDate = IntervalDays > 0 && nextDate is { } d ? d.Date : null;
    }

    /// <summary>Scheduled wipes every N days; 0 is off.</summary>
    internal int IntervalDays { get; }

    /// <summary>Wipe on: <c>Any</c> or a weekday.</summary>
    internal string DayOfReset { get; }

    /// <summary>The next wipe's date in the server's calendar; null when wipes are off or the world's start date is unknown.</summary>
    internal DateTime? NextDate { get; }

    /// <summary>The game schedules wipes: the countdown follows it and the Next reset date is not used.</summary>
    internal bool On => IntervalDays > 0;

    /// <summary>From the start date and the two rules (the next start's values).</summary>
    internal static GameWipeSchedule From(DateTime? startDate, int intervalDays, string? dayOfReset) =>
        new(intervalDays, dayOfReset, startDate is { } s ? ServerRules.ResetDate(s, intervalDays, dayOfReset) : null);

    /// <summary>From the Server rules state (the server's own, or the copy an admin's client holds); null without one.</summary>
    internal static GameWipeSchedule? FromRules(ServerRulesState? state)
    {
        if (state == null) return null;
        int interval = int.TryParse(state.Row(ServerRules.ResetDaysInterval)?.Next, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : 0;
        DateTime? next = DateTime.TryParseExact(state.NextReset, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
        return new GameWipeSchedule(interval, state.Row(ServerRules.DayOfReset)?.Next, next);
    }

    /// <summary>The next wipe as the stored ISO 8601 UTC string: 00:00 of <see cref="NextDate"/> in <paramref name="serverZone"/>; empty when unknown.</summary>
    internal string NextUtc(TimeZoneInfo? serverZone)
    {
        if (NextDate is not { } date) return "";
        var utc = ResetCountdown.LocalToUtc(DateTime.SpecifyKind(date.Date, DateTimeKind.Unspecified), serverZone ?? TimeZoneInfo.Utc, out _);
        return ResetCountdown.FormatUtc(utc);
    }

    /// <summary>The muted note on the read-only Next reset row.</summary>
    internal const string FromScheduleNote = "From the server's wipe schedule";

    /// <summary>
    /// The read-only Next reset row's value: <c>Sat, Oct 10, 2026 · From the server's wipe schedule</c>, or, before the
    /// world's start date is known, <c>Every 30 days, start date unknown · From the server's wipe schedule</c>.
    /// </summary>
    internal string RowText() =>
        (NextDate is { } d
            ? ServerRules.FormatDate(d)
            : "Every " + IntervalDays.ToString(CultureInfo.InvariantCulture) + (IntervalDays == 1 ? " day" : " days") + ", start date unknown")
        + " · " + FromScheduleNote;
}
