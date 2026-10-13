using System;
using Progenitor.Sample;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Game-free coverage of the Sample's map-scope keybind example (<c>SampleMap</c>): the hold-to-measure
/// ruler, the coordinate check and the log lines. The <c>RegisterInMap</c> / <c>RegisterHoldInMap</c>
/// calls and the <c>WorldMap</c> pointer reads need a live client and are behind <c>!PROGENITOR_TESTS</c>.
/// </summary>
public class SampleMapTests
{
    [Fact]
    public void Keybind_ids_are_short_and_distinct()
    {
        Assert.Equal("MapPoint", SampleMap.PointKeybindId);
        Assert.Equal("MapMeasure", SampleMap.MeasureKeybindId);
        Assert.NotEqual(SampleMap.PointKeybindId, SampleMap.MeasureKeybindId);
    }

    [Theory]
    [InlineData(0f, 0f, true)]
    [InlineData(-1234.5f, 2100f, true)]
    [InlineData(100000f, -100000f, true)]
    [InlineData(100000.5f, 0f, false)]
    [InlineData(float.NaN, 0f, false)]
    [InlineData(0f, float.PositiveInfinity, false)]
    public void Only_finite_in_range_points_are_usable(float x, float z, bool usable) =>
        Assert.Equal(usable, SampleMap.IsUsable(x, z));

    [Fact]
    public void A_hold_measures_from_the_press_point_to_the_last_tracked_point()
    {
        var ruler = new MapRuler();
        ruler.Begin(10f, 20f);
        Assert.True(ruler.Active);
        ruler.Track(13f, 24f);
        ruler.Track(float.NaN, 0f); // an unusable read keeps the last good point

        var m = ruler.Finish();

        Assert.False(ruler.Active);
        Assert.True(m.HasValue);
        Assert.Equal(10f, m!.Value.FromX);
        Assert.Equal(20f, m.Value.FromZ);
        Assert.Equal(13f, m.Value.ToX);
        Assert.Equal(24f, m.Value.ToZ);
        Assert.Equal(3f, m.Value.Dx);
        Assert.Equal(4f, m.Value.Dz);
        Assert.Equal(5f, m.Value.Distance, 4);
    }

    [Fact]
    public void A_press_and_release_without_movement_measures_zero()
    {
        var ruler = new MapRuler();
        ruler.Begin(-50f, 75f);
        var m = ruler.Finish();
        Assert.Equal(0f, m!.Value.Distance);
    }

    [Fact]
    public void Nothing_is_measured_without_a_usable_press_point()
    {
        var ruler = new MapRuler();
        ruler.Begin(float.NaN, 1f);
        Assert.False(ruler.Active);
        ruler.Track(5f, 5f);
        Assert.Null(ruler.Finish());
    }

    [Fact]
    public void Cancel_ends_the_measure_and_finish_then_returns_nothing()
    {
        var ruler = new MapRuler();
        ruler.Begin(1f, 1f);
        ruler.Cancel();
        Assert.False(ruler.Active);
        Assert.Null(ruler.Finish());
    }

    [Fact]
    public void A_second_finish_returns_nothing()
    {
        var ruler = new MapRuler();
        ruler.Begin(1f, 1f);
        Assert.NotNull(ruler.Finish());
        Assert.Null(ruler.Finish());
    }

    [Fact]
    public void Lines_use_invariant_one_decimal_formatting()
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            // A comma-decimal culture must not leak into the log lines.
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.Equal("point x=-1234.6 z=2100.0", SampleMap.PointLine(-1234.56f, 2100f));
            Assert.Equal(
                "measured 5.0 m from (10.0, 20.0) to (13.0, 24.0); dx=3.0 dz=4.0",
                SampleMap.MeasureLine(new MapMeasurement(10f, 20f, 13f, 24f)));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Refusal_lines_name_the_reason()
    {
        Assert.Equal("no world point under the cursor (pointer off the map)", SampleMap.NoPointLine("pointer off the map"));
        Assert.Equal("no world point under the cursor (unknown)", SampleMap.NoPointLine(""));
        Assert.Equal("measure canceled (map closed)", SampleMap.CanceledLine("map closed"));
        Assert.Equal("Sample map: measure canceled (map closed)", Progenitor.Sample.SampleLog.Format(SampleMap.Area, SampleMap.CanceledLine("map closed")));
    }
}
