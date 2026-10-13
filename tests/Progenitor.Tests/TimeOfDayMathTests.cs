using System;
using Progenitor.Clock;
using Progenitor.Sample;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// <c>ctx.Time</c>'s game-free half: hour ↔ cycle clock, the day test, the write gate, the
/// override stack, and the Sample's <c>.time</c> parser. Test-server settings are a 1080 s day
/// with sunrise 09:00 and sunset 17:00 (internal research notes).
/// </summary>
public class TimeOfDayMathTests
{
    private const double Day = 1080.0;          // DayDurationInSeconds
    private const double Hour = Day / 24.0;     // 45 s per game hour
    private const double Sunrise = 9 * Hour;    // DayTimeStartInSeconds
    private const double DayLen = 8 * Hour;     // DayTimeDurationInSeconds

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(45.0, 1.0)]
    [InlineData(540.0, 12.0)]
    [InlineData(1080.0, 0.0)]                   // midnight of day 1
    [InlineData(3 * 1080.0 + 1057.5, 23.5)]     // 23:30 on day 3
    [InlineData(-45.0, 23.0)]                   // negative wraps forward, like the game's fmod path
    public void HourOf_maps_the_cycle_clock_to_hours(double time, double hour) =>
        Assert.Equal(hour, TimeOfDayMath.HourOf(time, Day), 9);

    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(1079.9, 0)]
    [InlineData(1080.0, 1)]
    [InlineData(5000.0, 4)]
    public void DayOf_is_floor_of_time_over_day_length(double time, long day) =>
        Assert.Equal(day, TimeOfDayMath.DayOf(time, Day));

    [Fact]
    public void CycleTimeFor_keeps_the_day_and_sets_the_hour()
    {
        double now = 3 * Day + 14 * Hour;                   // day 3, 14:00
        Assert.Equal(3 * Day, TimeOfDayMath.CycleTimeFor(now, Day, 0), 9);          // back to midnight, same day
        Assert.Equal(3 * Day + 23.5 * Hour, TimeOfDayMath.CycleTimeFor(now, Day, 23.5), 9);
        double noon = TimeOfDayMath.CycleTimeFor(now, Day, 12);
        Assert.Equal(12.0, TimeOfDayMath.HourOf(noon, Day), 9);
        Assert.Equal(3, TimeOfDayMath.DayOf(noon, Day));
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(24.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Hours_outside_0_to_24_are_refused(double hour) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => TimeOfDayMath.CycleTimeFor(0, Day, hour));

    [Fact]
    public void An_unusable_day_length_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TimeOfDayMath.CycleTimeFor(0, 0, 12));
        Assert.Equal(0.0, TimeOfDayMath.HourOf(100, 0));
        Assert.Equal(0, TimeOfDayMath.DayOf(100, double.NaN));
    }

    [Theory]
    [InlineData(8.99, false)]
    [InlineData(9.0, true)]                     // sunrise inclusive
    [InlineData(12.0, true)]
    [InlineData(16.99, true)]
    [InlineData(17.0, false)]                   // sunset exclusive
    [InlineData(0.0, false)]
    public void IsDay_matches_the_games_get_TimeOfDay(double hour, bool isDay) =>
        Assert.Equal(isDay, TimeOfDayMath.IsDay(5 * Day + hour * Hour, Day, Sunrise, DayLen));

    [Fact]
    public void Round_trip_hour_to_seconds_and_back()
    {
        for (double h = 0; h < 24; h += 0.25)
            Assert.Equal(h, TimeOfDayMath.HourForSeconds(TimeOfDayMath.SecondsForHour(h, Day), Day), 9);
    }

    [Fact]
    public void NeedsWrite_skips_values_already_at_target()
    {
        Assert.False(TimeOfDayMath.NeedsWrite(100.0, 100.0));
        Assert.False(TimeOfDayMath.NeedsWrite(100.0, 100.00005));
        Assert.True(TimeOfDayMath.NeedsWrite(100.0, 100.01));
        Assert.True(TimeOfDayMath.NeedsWrite(double.NaN, 100.0));
    }

    [Theory]
    [InlineData(0.0, "00:00")]
    [InlineData(23.5, "23:30")]
    [InlineData(9.25, "09:15")]
    [InlineData(23.999, "00:00")]
    public void FormatHour_is_hh_mm(double hour, string text) =>
        Assert.Equal(text, TimeOfDayMath.FormatHour(hour));

    [Fact]
    public void Reading_describes_itself()
    {
        var r = new TimeOfDayReading(3, 23.5, false, Day, 9, 17, true, TimeOfDayOverrideKind.Hold);
        Assert.Equal("day 3 23:30 night (held; sunrise 09:00, sunset 17:00, day 1080s)", r.ToString());
    }

    [Fact]
    public void The_newest_override_governs_and_release_hands_back()
    {
        var stack = new TimeOfDayOverrides<object>();
        object a = new(), b = new(), c = new();
        Assert.Null(stack.Current);
        stack.Push(a);
        stack.Push(b);
        stack.Push(b);                          // idempotent
        Assert.Same(b, stack.Current);
        Assert.Equal(2, stack.Count);
        stack.Push(c);
        Assert.True(stack.Remove(b));           // releasing a middle one leaves the newest governing
        Assert.Same(c, stack.Current);
        Assert.True(stack.Remove(c));
        Assert.Same(a, stack.Current);
        Assert.False(stack.Remove(c));
        Assert.True(stack.Any(x => ReferenceEquals(x, a)));
        Assert.Single(stack.RemoveWhere(x => ReferenceEquals(x, a)));
        Assert.Null(stack.Current);
    }

    [Fact]
    public void DropAll_empties_the_stack()
    {
        var stack = new TimeOfDayOverrides<object>();
        stack.Push(new object());
        stack.Push(new object());
        Assert.Equal(2, stack.DropAll().Count);
        Assert.Equal(0, stack.Count);
    }

    [Theory]
    [InlineData("", "", (int)SampleTime.Kind.Status, true)]
    [InlineData("status", "", (int)SampleTime.Kind.Status, true)]
    [InlineData("hold", "0", (int)SampleTime.Kind.Hold, true)]
    [InlineData("set", "23.5", (int)SampleTime.Kind.Set, true)]
    [InlineData("release", "", (int)SampleTime.Kind.Release, true)]
    [InlineData("resume", "", (int)SampleTime.Kind.Resume, true)]
    [InlineData("hold", "", (int)SampleTime.Kind.Hold, false)]
    [InlineData("hold", "24", (int)SampleTime.Kind.Hold, false)]
    [InlineData("set", "-1", (int)SampleTime.Kind.Set, false)]
    [InlineData("noon", "", (int)SampleTime.Kind.Status, false)]
    public void Sample_time_parses_its_arguments(string what, string hour, int kind, bool ok)
    {
        Assert.Equal(ok, SampleTime.TryParse(what, hour, out var parsed, out _));
        if (ok) Assert.Equal(kind, (int)parsed);
    }
}
