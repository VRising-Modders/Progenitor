using System;
using System.Collections.Generic;
using System.Globalization;

namespace Progenitor.Clock;

/// <summary>
/// Game-free arithmetic behind <c>ctx.Time</c>: hour of day to and from the game's cycle clock.
/// Kept free of BepInEx and game types so the test project compiles it.
///
/// <para><b>The game's clock</b> (the server's native code): <c>DayNightCycle.Time</c> is
/// seconds since the world's first midnight.
/// <c>get_TimeSinceMidnight</c> is <c>fmod(Time, DayDurationInSeconds)</c>, and the admin
/// <c>SetTimeOfDayEvent</c> handler maps an hour to <c>hour * dayLength / 24</c> past a multiple of
/// the day length, so midnight is <c>Time % dayLength == 0</c> and every game hour is
/// <c>dayLength / 24</c> real seconds. Day <c>n</c> is <c>floor(Time / dayLength)</c>
/// (<c>DayNightCycleExtensions.TotalDays</c>).</para>
/// </summary>
public static class TimeOfDayMath
{
    /// <summary>Hours in one game day.</summary>
    public const double HoursPerDay = 24.0;

    /// <summary>
    /// A write is skipped when the live value is already this close to the target (seconds), so a
    /// held clock does not bump the component's change version every frame.
    /// </summary>
    public const double WriteToleranceSeconds = 1e-4;

    /// <summary>
    /// Throw unless <paramref name="hour"/> is a finite value in [0, 24). Fractions are minutes:
    /// 23.5 is 23:30.
    /// </summary>
    public static void RequireHour(double hour, string paramName = "hour")
    {
        if (double.IsNaN(hour) || double.IsInfinity(hour) || hour < 0 || hour >= HoursPerDay)
            throw new ArgumentOutOfRangeException(paramName, hour,
                "hour must be in [0, 24): 0 is midnight, 12 is noon, 23.5 is 23:30.");
    }

    /// <summary>True when <paramref name="dayLength"/> is a usable cycle length (finite, &gt; 0).</summary>
    public static bool IsUsableDayLength(double dayLength) =>
        !double.IsNaN(dayLength) && !double.IsInfinity(dayLength) && dayLength > 0;

    /// <summary>Seconds past midnight, in [0, dayLength). Negative clock values wrap forward.</summary>
    public static double SecondsSinceMidnight(double time, double dayLength)
    {
        if (!IsUsableDayLength(dayLength)) return 0;
        double r = time % dayLength;
        if (r < 0) r += dayLength;
        return r >= dayLength ? 0 : r;
    }

    /// <summary>Hour of day in [0, 24) for cycle clock <paramref name="time"/>.</summary>
    public static double HourOf(double time, double dayLength) =>
        IsUsableDayLength(dayLength) ? SecondsSinceMidnight(time, dayLength) / dayLength * HoursPerDay : 0;

    /// <summary>Whole days since the world's first midnight (<c>TotalDays</c>).</summary>
    public static long DayOf(double time, double dayLength) =>
        IsUsableDayLength(dayLength) ? (long)Math.Floor(time / dayLength) : 0;

    /// <summary>Seconds past midnight at which <paramref name="hour"/> falls.</summary>
    public static double SecondsForHour(double hour, double dayLength) => hour / HoursPerDay * dayLength;

    /// <summary>Hour of day that <paramref name="secondsSinceMidnight"/> names.</summary>
    public static double HourForSeconds(double secondsSinceMidnight, double dayLength) =>
        IsUsableDayLength(dayLength) ? secondsSinceMidnight / dayLength * HoursPerDay : 0;

    /// <summary>
    /// The cycle clock value that is <paramref name="hour"/> on the same game day as
    /// <paramref name="currentTime"/>. It may be earlier than <paramref name="currentTime"/> (the
    /// clock goes back within the day); the day number never changes, so day-keyed game logic
    /// such as the blood-moon day is not advanced.
    /// </summary>
    public static double CycleTimeFor(double currentTime, double dayLength, double hour)
    {
        RequireHour(hour);
        if (!IsUsableDayLength(dayLength))
            throw new ArgumentOutOfRangeException(nameof(dayLength), dayLength, "the day length must be a positive number of seconds.");
        return DayOf(currentTime, dayLength) * dayLength + SecondsForHour(hour, dayLength);
    }

    /// <summary>
    /// <c>DayNightCycle.get_TimeOfDay</c>: day when the time since midnight is in
    /// [<paramref name="dayStart"/>, <paramref name="dayStart"/> + <paramref name="dayDuration"/>).
    /// </summary>
    public static bool IsDay(double time, double dayLength, double dayStart, double dayDuration)
    {
        double t = SecondsSinceMidnight(time, dayLength);
        return t >= dayStart && t < dayStart + dayDuration;
    }

    /// <summary>
    /// The game's blood-moon night. <c>DayNightCycle.IsBloodMoonDay</c> (server and client native
    /// code) is
    /// <c>(TotalDays == N &amp;&amp; t &gt;= DayTimeStart) || (TotalDays - N == 1 &amp;&amp; t &lt;= DayTimeStart)</c>
    /// with <c>t</c> the seconds since midnight and <c>N</c> = <c>NextBloodMoonDay</c>; every consumer
    /// (<c>BloodMoonSystem</c>, the blood-bonus getters) also requires night. So it is the night from
    /// sunset of day N to sunrise of day N+1. Implemented exactly as the game does, including N == 0.
    /// </summary>
    public static bool IsBloodMoonNight(double time, double dayLength, double dayStart, double dayDuration, int nextBloodMoonDay)
    {
        if (!IsUsableDayLength(dayLength)) return false;
        if (IsDay(time, dayLength, dayStart, dayDuration)) return false;
        long day = DayOf(time, dayLength);
        double t = SecondsSinceMidnight(time, dayLength);
        return (day == nextBloodMoonDay && t >= dayStart)
            || (day - nextBloodMoonDay == 1 && t <= dayStart);
    }

    /// <summary>
    /// The cycle-clock value a hold at <paramref name="hour"/> writes.
    ///
    /// <para><paramref name="bloodMoon"/> false: <see cref="CycleTimeFor"/> on the current day, or
    /// one day earlier when that is a blood-moon night (<paramref name="note"/> says so). One step
    /// back is always enough (pre-dawn N+1 becomes pre-dawn N, evening N becomes evening N-1) and
    /// never moves <c>TotalDays</c> past N+1, so the server does not re-roll the schedule. Never
    /// forward: on day N+2 the server re-rolls <c>NextBloodMoonDay</c>, and that is saved.</para>
    ///
    /// <para><paramref name="bloodMoon"/> true: the blood-moon night at that hour — day N for an
    /// evening hour (at or after sunset, so after DayTimeStart), day N+1 for a pre-dawn hour. Null with
    /// <paramref name="note"/> for a daytime hour (no blood moon shows by day) or N &lt;= 0 (the
    /// schedule is not initialized).</para>
    /// </summary>
    public static double? HoldTarget(
        double currentTime,
        double dayLength,
        double dayStart,
        double dayDuration,
        int nextBloodMoonDay,
        double hour,
        bool bloodMoon,
        out string note)
    {
        double target = CycleTimeFor(currentTime, dayLength, hour);
        if (!bloodMoon)
        {
            if (!IsBloodMoonNight(target, dayLength, dayStart, dayDuration, nextBloodMoonDay))
            {
                note = "";
                return target;
            }
            note = "day " + DayOf(target, dayLength).ToString(CultureInfo.InvariantCulture)
                + " is a blood moon night; held one day earlier";
            return target - dayLength;
        }

        double seconds = SecondsForHour(hour, dayLength);
        if (seconds >= dayStart && seconds < dayStart + dayDuration)
        {
            note = "a blood moon shows only at night; " + FormatHour(hour) + " is daytime";
            return null;
        }
        if (nextBloodMoonDay <= 0)
        {
            note = "the blood moon schedule is not initialized (NextBloodMoonDay is " + nextBloodMoonDay.ToString(CultureInfo.InvariantCulture) + ")";
            return null;
        }
        long day = seconds >= dayStart ? nextBloodMoonDay : (long)nextBloodMoonDay + 1;
        double forced = day * dayLength + seconds;
        if (!IsBloodMoonNight(forced, dayLength, dayStart, dayDuration, nextBloodMoonDay))
        {
            note = FormatHour(hour) + " is not inside the blood moon night of day " + nextBloodMoonDay.ToString(CultureInfo.InvariantCulture);
            return null;
        }
        note = "blood moon night of day " + nextBloodMoonDay.ToString(CultureInfo.InvariantCulture);
        return forced;
    }

    /// <summary>True when the live value differs from the target by more than the tolerance.</summary>
    public static bool NeedsWrite(double current, double target) =>
        double.IsNaN(current) || Math.Abs(current - target) > WriteToleranceSeconds;

    /// <summary><c>HH:MM</c> for an hour value (23.5 → 23:30).</summary>
    public static string FormatHour(double hour)
    {
        if (double.IsNaN(hour) || double.IsInfinity(hour)) return "--:--";
        int minutes = (int)Math.Round(hour * 60.0);
        minutes = ((minutes % 1440) + 1440) % 1440;
        return (minutes / 60).ToString("00", CultureInfo.InvariantCulture) + ":"
            + (minutes % 60).ToString("00", CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// One read of the world clock from <c>ctx.Time.GetTimeOfDay()</c>. Every value comes from the
/// server's <c>DayNightCycle</c> singleton at the moment of the read.
/// </summary>
public readonly struct TimeOfDayReading
{
    /// <summary>A reading with the given values.</summary>
    public TimeOfDayReading(
        long day,
        double hour,
        bool isDay,
        double dayLengthSeconds,
        double sunriseHour,
        double sunsetHour,
        bool worldClockStopped,
        TimeOfDayOverrideKind overrideKind)
        : this(day, hour, isDay, dayLengthSeconds, sunriseHour, sunsetHour, worldClockStopped, overrideKind, false, 0)
    {
    }

    /// <summary>A reading with the given values, including the blood-moon state.</summary>
    public TimeOfDayReading(
        long day,
        double hour,
        bool isDay,
        double dayLengthSeconds,
        double sunriseHour,
        double sunsetHour,
        bool worldClockStopped,
        TimeOfDayOverrideKind overrideKind,
        bool bloodMoon,
        int nextBloodMoonDay)
    {
        BloodMoon = bloodMoon;
        NextBloodMoonDay = nextBloodMoonDay;
        Day = day;
        Hour = hour;
        IsDay = isDay;
        DayLengthSeconds = dayLengthSeconds;
        SunriseHour = sunriseHour;
        SunsetHour = sunsetHour;
        WorldClockStopped = worldClockStopped;
        Override = overrideKind;
    }

    /// <summary>Whole game days since the world's first midnight.</summary>
    public long Day { get; }

    /// <summary>Hour of day in [0, 24); the fraction is minutes (23.5 is 23:30).</summary>
    public double Hour { get; }

    /// <summary>The game's own day test: sunrise inclusive, sunset exclusive.</summary>
    public bool IsDay { get; }

    /// <summary>Real seconds per game day (server setting <c>DayDurationInSeconds</c>).</summary>
    public double DayLengthSeconds { get; }

    /// <summary>Hour the day starts (server setting <c>DayStartHour</c>/<c>DayStartMinute</c>).</summary>
    public double SunriseHour { get; }

    /// <summary>Hour the night starts (server setting <c>DayEndHour</c>/<c>DayEndMinute</c>).</summary>
    public double SunsetHour { get; }

    /// <summary>
    /// The game's <c>SyncedServerDebugSettings.DayNightCycleDisabled</c>: the game is not advancing
    /// the clock itself. True while any <c>ctx.Time</c> override is active, and also when an admin
    /// (or a save written during an override) stopped it.
    /// </summary>
    public bool WorldClockStopped { get; }

    /// <summary>Which <c>ctx.Time</c> override is governing the clock, if any (any plugin's).</summary>
    public TimeOfDayOverrideKind Override { get; }

    /// <summary>It is a blood-moon night now (<see cref="TimeOfDayMath.IsBloodMoonNight"/>).</summary>
    public bool BloodMoon { get; }

    /// <summary>The game's <c>DayNightCycle.NextBloodMoonDay</c>: the day whose night is the next (or current) blood moon.</summary>
    public int NextBloodMoonDay { get; }

    /// <summary><c>day 3 23:30 night (held; sunrise 09:00, sunset 17:00, day 1080s)</c>.</summary>
    public override string ToString() =>
        "day " + Day.ToString(CultureInfo.InvariantCulture) + " " + TimeOfDayMath.FormatHour(Hour)
        + (IsDay ? " day" : BloodMoon ? " blood moon night" : " night")
        + " (" + (Override == TimeOfDayOverrideKind.Hold ? "held"
            : Override == TimeOfDayOverrideKind.Shift ? "shifted"
            : WorldClockStopped ? "stopped" : "running")
        + "; sunrise " + TimeOfDayMath.FormatHour(SunriseHour)
        + ", sunset " + TimeOfDayMath.FormatHour(SunsetHour)
        + ", day " + DayLengthSeconds.ToString("0.#", CultureInfo.InvariantCulture) + "s)";
}

/// <summary>What kind of <c>ctx.Time</c> override is governing the world clock.</summary>
public enum TimeOfDayOverrideKind
{
    /// <summary>No override: the game runs the clock (or an admin stopped it).</summary>
    None = 0,

    /// <summary><c>SetTimeOfDay</c>: the clock was moved and keeps running from there.</summary>
    Shift = 1,

    /// <summary><c>HoldTimeOfDay</c>: the clock is frozen at one hour.</summary>
    Hold = 2,
}

/// <summary>
/// The process-wide override stack behind <c>ctx.Time</c>: the newest live override governs the
/// clock; releasing it hands the clock back to the one before it, and releasing the last one hands
/// it back to the game. Game-free so the rule is unit-tested.
/// </summary>
/// <typeparam name="T">The lease type.</typeparam>
public sealed class TimeOfDayOverrides<T> where T : class
{
    private readonly List<T> _stack = new();

    /// <summary>Live overrides, oldest first.</summary>
    public int Count => _stack.Count;

    /// <summary>The governing override (the newest), or null when none is live.</summary>
    public T? Current => _stack.Count == 0 ? null : _stack[^1];

    /// <summary>Push <paramref name="lease"/>; it governs until released or a newer one is pushed.</summary>
    public void Push(T lease)
    {
        if (lease == null) throw new ArgumentNullException(nameof(lease));
        if (!_stack.Contains(lease)) _stack.Add(lease);
    }

    /// <summary>Remove <paramref name="lease"/> wherever it is. True when it was live.</summary>
    public bool Remove(T lease) => lease != null && _stack.Remove(lease);

    /// <summary>True when any live lease matches <paramref name="match"/>.</summary>
    public bool Any(Func<T, bool> match)
    {
        foreach (var lease in _stack)
            if (match(lease)) return true;
        return false;
    }

    /// <summary>Remove every lease matching <paramref name="match"/>; returns them.</summary>
    public List<T> RemoveWhere(Func<T, bool> match)
    {
        var removed = new List<T>();
        for (int i = _stack.Count - 1; i >= 0; i--)
        {
            if (!match(_stack[i])) continue;
            removed.Add(_stack[i]);
            _stack.RemoveAt(i);
        }
        return removed;
    }

    /// <summary>A copy of the live leases, oldest first (the last one governs).</summary>
    public List<T> Snapshot() => new(_stack);

    /// <summary>Remove and return every lease.</summary>
    public List<T> DropAll()
    {
        var all = new List<T>(_stack);
        _stack.Clear();
        return all;
    }
}
