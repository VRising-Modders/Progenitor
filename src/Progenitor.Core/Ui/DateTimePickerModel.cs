using System;
using System.Collections.Generic;
using System.Globalization;
using Progenitor.Networking;

namespace Progenitor.Ui;

/// <summary>How finely a date-time picker edits the time: hours and minutes, or seconds as well.</summary>
public enum DateTimePickerPrecision
{
    Minute = 0,
    Second = 1,
}

/// <summary>One cell of the picker's month grid.</summary>
public readonly struct DateTimePickerDay
{
    public DateTimePickerDay(DateTime date, bool inMonth, bool selected, bool today)
    {
        Date = date.Date;
        InMonth = inMonth;
        Selected = selected;
        Today = today;
    }

    /// <summary>The calendar date (a wall-clock date in the picker's zone).</summary>
    public DateTime Date { get; }

    /// <summary>False for the leading and trailing days of the neighboring months.</summary>
    public bool InMonth { get; }

    public bool Selected { get; }

    public bool Today { get; }
}

/// <summary>
/// JOINFLOW1: the state of the date-time picker, game-free so the calendar rules have tests. The value is
/// stored as ISO 8601 UTC (<c>2026-10-10T18:00:00Z</c>) and shown and edited as a wall-clock time in
/// <see cref="Zone"/> (the server's zone for a server-wide setting, the player's for a personal one).
///
/// <list type="bullet">
/// <item><b>Month grid:</b> always 6 weeks of 7 days (42 cells), weeks starting on Sunday (US English),
/// with the neighboring months' days marked out of month. Leap years come from the calendar
/// (<c>DateTime.DaysInMonth</c>), so February 2028 has 29 days and 2100 does not.</item>
/// <item><b>Navigation:</b> previous and next month move the view only; a day click picks that date and
/// keeps the time; picking a day of a neighboring month moves the view to it.</item>
/// <item><b>Time:</b> hour 0-23, minute and second 0-59, set directly or stepped (wrapping, without
/// carrying into the date).</item>
/// <item><b>Daylight saving:</b> the conversion to UTC goes through
/// <see cref="ResetCountdown.LocalToUtc"/>: a wall-clock time in a spring-forward gap moves forward by
/// the gap and an ambiguous fall-back time takes the first occurrence; <see cref="Note"/> says which.</item>
/// </list>
/// </summary>
public sealed class DateTimePickerModel
{
    private DateTime _local;

    public DateTimePickerModel(string? isoUtc, TimeZoneInfo? zone, DateTimePickerPrecision precision, DateTime? nowUtc = null)
    {
        Zone = zone ?? TimeZoneInfo.Utc;
        Precision = precision;
        var now = DateTime.SpecifyKind(nowUtc ?? DateTime.UtcNow, DateTimeKind.Utc);
        TodayLocal = TimeZoneInfo.ConvertTimeFromUtc(now, Zone).Date;
        if (ResetCountdown.TryParseUtc(isoUtc, out var utc))
        {
            HadValue = true;
            _local = Unspecified(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone));
        }
        else
        {
            // No value yet: tomorrow at the next whole hour in the zone, a reasonable starting point.
            var local = TimeZoneInfo.ConvertTimeFromUtc(now, Zone);
            _local = Unspecified(new DateTime(local.Year, local.Month, local.Day, local.Hour, 0, 0).AddDays(1));
        }

        if (precision == DateTimePickerPrecision.Minute) _local = _local.AddSeconds(-_local.Second);
        ViewYear = _local.Year;
        ViewMonth = _local.Month;
    }

    public TimeZoneInfo Zone { get; }

    public DateTimePickerPrecision Precision { get; }

    /// <summary>True when the picker opened on a stored value (false: no value yet, a default was proposed).</summary>
    public bool HadValue { get; }

    /// <summary>Today's date in <see cref="Zone"/>, for the grid's today mark.</summary>
    public DateTime TodayLocal { get; }

    /// <summary>The month the grid shows.</summary>
    public int ViewYear { get; private set; }

    public int ViewMonth { get; private set; }

    /// <summary>The selected wall-clock time in <see cref="Zone"/>.</summary>
    public DateTime Local => _local;

    public int Hour => _local.Hour;

    public int Minute => _local.Minute;

    public int Second => _local.Second;

    /// <summary>What the last conversion said about a gap or an ambiguous time; empty otherwise.</summary>
    public string Note
    {
        get
        {
            ResetCountdown.LocalToUtc(_local, Zone, out var note);
            return note;
        }
    }

    /// <summary>The selected time in UTC (daylight-saving safe, see the class remarks).</summary>
    public DateTime Utc => ResetCountdown.LocalToUtc(_local, Zone, out _);

    /// <summary>The stored form: ISO 8601 UTC with seconds and <c>Z</c>.</summary>
    public string IsoUtc => ResetCountdown.FormatUtc(Utc);

    /// <summary>The grid's caption, <c>October 2026</c>.</summary>
    public string MonthCaption =>
        new DateTime(ViewYear, ViewMonth, 1).ToString("MMMM yyyy", CultureInfo.GetCultureInfo("en-US"));

    /// <summary>The weekday headings, Sunday first (US English).</summary>
    public static IReadOnlyList<string> WeekdayCaptions { get; } = new[] { "Su", "Mo", "Tu", "We", "Th", "Fr", "Sa" };

    /// <summary>The 42 cells of the shown month, Sunday-first weeks.</summary>
    public IReadOnlyList<DateTimePickerDay> Grid() => Grid(ViewYear, ViewMonth, _local.Date, TodayLocal);

    /// <summary>The 42 cells of a month, Sunday-first weeks; <paramref name="selected"/> and <paramref name="today"/> are marked.</summary>
    public static IReadOnlyList<DateTimePickerDay> Grid(int year, int month, DateTime? selected = null, DateTime? today = null)
    {
        var first = new DateTime(year, month, 1);
        int lead = (int)first.DayOfWeek; // Sunday = 0
        var start = first.AddDays(-lead);
        var cells = new List<DateTimePickerDay>(42);
        for (int i = 0; i < 42; i++)
        {
            var d = start.AddDays(i);
            cells.Add(new DateTimePickerDay(
                d,
                d.Month == month && d.Year == year,
                selected.HasValue && d == selected.Value.Date,
                today.HasValue && d == today.Value.Date));
        }

        return cells;
    }

    /// <summary>Show the previous month.</summary>
    public void PreviousMonth() => MoveView(-1);

    /// <summary>Show the next month.</summary>
    public void NextMonth() => MoveView(+1);

    /// <summary>Show <paramref name="year"/>-<paramref name="month"/> (clamped to years 1 to 9998).</summary>
    public void ShowMonth(int year, int month)
    {
        if (month < 1 || month > 12) throw new ArgumentOutOfRangeException(nameof(month));
        ViewYear = Math.Max(1, Math.Min(9998, year));
        ViewMonth = month;
    }

    private void MoveView(int months)
    {
        var d = new DateTime(ViewYear, ViewMonth, 1).AddMonths(months);
        if (d.Year < 1 || d.Year > 9998) return;
        ViewYear = d.Year;
        ViewMonth = d.Month;
    }

    /// <summary>Pick a date, keeping the time; a day of a neighboring month moves the view to it.</summary>
    public void PickDay(DateTime date)
    {
        var d = date.Date;
        _local = Unspecified(new DateTime(d.Year, d.Month, d.Day, _local.Hour, _local.Minute, _local.Second));
        ViewYear = d.Year;
        ViewMonth = d.Month;
    }

    /// <summary>Pick day <paramref name="day"/> of the shown month (1 to its last day).</summary>
    public bool PickDayOfMonth(int day)
    {
        int last = DateTime.DaysInMonth(ViewYear, ViewMonth);
        if (day < 1 || day > last) return false;
        PickDay(new DateTime(ViewYear, ViewMonth, day));
        return true;
    }

    public void SetHour(int hour) => SetTime(Clamp(hour, 0, 23), _local.Minute, _local.Second);

    public void SetMinute(int minute) => SetTime(_local.Hour, Clamp(minute, 0, 59), _local.Second);

    public void SetSecond(int second) =>
        SetTime(_local.Hour, _local.Minute, Precision == DateTimePickerPrecision.Second ? Clamp(second, 0, 59) : 0);

    /// <summary>Step the hour by <paramref name="delta"/>, wrapping 23 → 0 without changing the date.</summary>
    public void StepHour(int delta) => SetTime(Wrap(_local.Hour + delta, 24), _local.Minute, _local.Second);

    public void StepMinute(int delta) => SetTime(_local.Hour, Wrap(_local.Minute + delta, 60), _local.Second);

    public void StepSecond(int delta)
    {
        if (Precision != DateTimePickerPrecision.Second) return;
        SetTime(_local.Hour, _local.Minute, Wrap(_local.Second + delta, 60));
    }

    private void SetTime(int hour, int minute, int second) =>
        _local = Unspecified(new DateTime(_local.Year, _local.Month, _local.Day, hour, minute, second));

    /// <summary>The value as the field shows it: <c>Sat, Oct 10, 2026, 6:00 PM</c> (seconds at second precision).</summary>
    public string DisplayText => Display(_local, Precision);

    /// <summary>The zone line under the picker: <c>Server time: UTC-04:00 (Eastern Daylight Time)</c>.</summary>
    public string ZoneLabel(string who = "Server time") => ZoneCaption(Zone, _local, who);

    /// <summary>A wall-clock time as the field shows it.</summary>
    public static string Display(DateTime local, DateTimePickerPrecision precision)
    {
        var us = CultureInfo.GetCultureInfo("en-US");
        return local.ToString("ddd, MMM d, yyyy", us) + ", "
            + local.ToString(precision == DateTimePickerPrecision.Second ? "h:mm:ss tt" : "h:mm tt", us);
    }

    /// <summary>A zone and its offset at <paramref name="local"/>: <c>Server time: UTC+02:00 (W. Europe Daylight Time)</c>.</summary>
    public static string ZoneCaption(TimeZoneInfo zone, DateTime local, string who)
    {
        zone ??= TimeZoneInfo.Utc;
        local = Unspecified(local);
        TimeSpan offset;
        string name;
        try
        {
            offset = zone.IsInvalidTime(local) ? zone.BaseUtcOffset : zone.GetUtcOffset(local);
            name = zone.IsDaylightSavingTime(local) ? zone.DaylightName : zone.StandardName;
            if (zone.IsAmbiguousTime(local))
            {
                // The picker takes an ambiguous time's first occurrence (ResetCountdown.LocalToUtc):
                // still daylight time, the larger offset. The local reading alone answers standard time.
                var offsets = zone.GetAmbiguousTimeOffsets(local);
                for (int i = 0; i < offsets.Length; i++) if (offsets[i] > offset) offset = offsets[i];
                if (offset != zone.BaseUtcOffset) name = zone.DaylightName;
            }
        }
        catch
        {
            offset = zone.BaseUtcOffset;
            name = zone.StandardName;
        }

        string label = ResetCountdown.OffsetLabel(offset);
        return (who ?? "").Length == 0
            ? label + (string.IsNullOrWhiteSpace(name) || name == label ? "" : " (" + name + ")")
            : who + ": " + label + (string.IsNullOrWhiteSpace(name) || name == label ? "" : " (" + name + ")");
    }

    /// <summary>A stored ISO 8601 UTC value as a wall-clock time in <paramref name="zone"/>, or empty when unset.</summary>
    public static string DisplayStored(string? isoUtc, TimeZoneInfo? zone, DateTimePickerPrecision precision)
    {
        if (!ResetCountdown.TryParseUtc(isoUtc, out var utc)) return "";
        zone ??= TimeZoneInfo.Utc;
        var instant = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        var local = TimeZoneInfo.ConvertTimeFromUtc(instant, zone);
        // The offset of the instant, not of the wall-clock time: an ambiguous fall-back time's local
        // reading answers the standard offset even for its first (daylight) occurrence.
        return Display(local, precision) + " (" + ResetCountdown.OffsetLabel(zone.GetUtcOffset(instant)) + ")";
    }

    private static DateTime Unspecified(DateTime d) => DateTime.SpecifyKind(d, DateTimeKind.Unspecified);

    private static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;

    private static int Wrap(int v, int modulus) => ((v % modulus) + modulus) % modulus;
}
