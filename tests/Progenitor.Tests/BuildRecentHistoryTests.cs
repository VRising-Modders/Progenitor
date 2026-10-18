using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

public sealed class BuildRecentHistoryTests
{
    [Fact]
    public void Repeated_build_moves_to_front_without_duplicates_and_evicts_oldest()
    {
        var history = new BuildRecentHistory();
        history.Record(11, 3); history.Record(22, 3); history.Record(33, 3);
        history.Record(11, 3); history.Record(44, 3);
        Assert.Equal(new[] { 44, 11, 33 }, history.Items);
        Assert.False(history.Record(0, 3));
        Assert.False(history.Record(44, 3));
    }

    [Fact]
    public void Persists_signed_guids_and_ignores_corrupt_duplicate_and_zero_entries()
    {
        var history = new BuildRecentHistory();
        history.Load("-41,0,broken,52,-41,63,74", 3);
        Assert.Equal(new[] { -41, 52, 63 }, history.Items);
        var restored = new BuildRecentHistory();
        restored.Load(history.Save(), 10);
        Assert.Equal(history.Items, restored.Items);
        restored.Trim(1);
        Assert.Equal(new[] { -41 }, restored.Items);
    }

    [Theory]
    [InlineData(1920)]
    [InlineData(2560)]
    [InlineData(3840)]
    public void Search_and_count_clear_centered_selector(float width)
    {
        float selectorLeft = width / 2 - 160;
        var p = BuildSearchPlacement.Place(60, width - 60, 300, 340, 110, true, selectorLeft, 344);
        Assert.True(p.X + 170 + 120 + 16 <= selectorLeft || p.Y >= 358);
        Assert.True(p.X - 170 >= 60);
    }

    [Fact]
    public void Narrow_layout_moves_search_above_selector()
    {
        var p = BuildSearchPlacement.Place(0, 700, 300, 340, 110, true, 190, 344);
        Assert.Equal(358, p.Y);
    }
}
