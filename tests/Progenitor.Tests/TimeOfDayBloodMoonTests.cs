using Progenitor.Clock;
using Progenitor.Sample;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Blood moon rule and hold targets (internal research notes, "Blood moon"). Test-server
/// settings: 1080 s day, sunrise 09:00, sunset 17:00. The live case was day 1133, N = 1132, 01:00.
/// </summary>
public class TimeOfDayBloodMoonTests
{
    private const double D = 1080.0;
    private const double H = D / 24.0;
    private const double Sunrise = 9 * H;
    private const double DayLen = 8 * H;
    private const int N = 1132;

    private static double At(long day, double hour) => day * D + hour * H;
    private static bool Night(double time, int next = N) => TimeOfDayMath.IsBloodMoonNight(time, D, Sunrise, DayLen, next);

    [Fact]
    public void Evening_of_day_N_is_a_blood_moon() => Assert.True(Night(At(N, 21)));

    [Fact]
    public void Pre_dawn_of_day_N_plus_1_is_a_blood_moon() => Assert.True(Night(At(N + 1, 1)));   // the live case

    [Fact]
    public void Sunset_boundary_starts_it()
    {
        Assert.False(Night(At(N, 17) - 0.001));   // last moment of daylight on day N
        Assert.True(Night(At(N, 17)));            // sunset
    }

    [Fact]
    public void Sunrise_boundary_ends_it()
    {
        Assert.True(Night(At(N + 1, 9) - 0.001)); // just before sunrise on N+1
        Assert.False(Night(At(N + 1, 9)));        // t == DayTimeStart is day: never a blood moon
    }

    [Fact]
    public void Other_nights_are_not()
    {
        Assert.False(Night(At(N, 1)));            // pre-dawn of day N belongs to night N-1
        Assert.False(Night(At(N + 1, 21)));       // evening of N+1
        Assert.False(Night(At(N - 1, 21)));
        Assert.False(Night(At(N, 12)));           // daytime on N
    }

    [Fact]
    public void N_zero_follows_the_games_rule() =>
        Assert.True(Night(At(0, 21), next: 0));   // the game's test, unguarded; HoldTarget refuses to force it

    [Fact]
    public void Avoid_steps_a_pre_dawn_hold_back_one_day()
    {
        double? t = TimeOfDayMath.HoldTarget(At(N + 1, 14), D, Sunrise, DayLen, N, 1, false, out string note);
        Assert.Equal(At(N, 1), t!.Value, 6);
        Assert.False(Night(t.Value));
        Assert.True(N + 1 >= TimeOfDayMath.DayOf(t.Value, D));   // no re-roll
        Assert.Contains("blood moon", note);
    }

    [Fact]
    public void Avoid_steps_an_evening_hold_back_one_day()
    {
        double? t = TimeOfDayMath.HoldTarget(At(N, 10), D, Sunrise, DayLen, N, 21, false, out _);
        Assert.Equal(At(N - 1, 21), t!.Value, 6);
        Assert.False(Night(t.Value));
    }

    [Fact]
    public void Avoid_leaves_an_ordinary_night_alone()
    {
        double? t = TimeOfDayMath.HoldTarget(At(N + 5, 14), D, Sunrise, DayLen, N + 10, 1, false, out string note);
        Assert.Equal(At(N + 5, 1), t!.Value, 6);
        Assert.Equal("", note);
    }

    [Fact]
    public void Force_evening_is_day_N_and_pre_dawn_is_N_plus_1()
    {
        double? evening = TimeOfDayMath.HoldTarget(At(N - 7, 12), D, Sunrise, DayLen, N, 21, true, out _);
        double? dawn = TimeOfDayMath.HoldTarget(At(N - 7, 12), D, Sunrise, DayLen, N, 1, true, out _);
        Assert.Equal(At(N, 21), evening!.Value, 6);
        Assert.Equal(At(N + 1, 1), dawn!.Value, 6);
        Assert.True(Night(evening.Value));
        Assert.True(Night(dawn.Value));
    }

    [Fact]
    public void Force_refuses_a_daytime_hour_and_an_uninitialised_schedule()
    {
        Assert.Null(TimeOfDayMath.HoldTarget(At(N, 1), D, Sunrise, DayLen, N, 12, true, out string day));
        Assert.Contains("daytime", day);
        Assert.Null(TimeOfDayMath.HoldTarget(At(3, 1), D, Sunrise, DayLen, 0, 1, true, out string zero));
        Assert.Contains("not initialized", zero);
    }

    [Fact]
    public void Reading_names_a_blood_moon_night()
    {
        var r = new TimeOfDayReading(1133, 1, false, D, 9, 17, true, TimeOfDayOverrideKind.Hold, true, N);
        Assert.Equal("day 1133 01:00 blood moon night (held; sunrise 09:00, sunset 17:00, day 1080s)", r.ToString());
        Assert.Equal(N, r.NextBloodMoonDay);
    }

    [Theory]
    [InlineData("hold", "1", "bloodmoon", true, true)]
    [InlineData("hold", "1", "", true, false)]
    [InlineData("hold", "1", "BloodMoon", true, true)]
    [InlineData("set", "1", "bloodmoon", false, false)]
    [InlineData("hold", "1", "red", false, false)]
    public void Sample_passes_the_bloodmoon_option(string what, string hour, string option, bool ok, bool bloodMoon)
    {
        Assert.Equal(ok, SampleTime.TryParse(what, hour, option, out _, out _, out bool parsed));
        if (ok) Assert.Equal(bloodMoon, parsed);
    }
}
