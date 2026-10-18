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

    [Fact]
    public void Sibling_order_lists_cells_newest_first_and_skips_cells_not_in_history()
    {
        // Cells in native order: 33, 99 (not recent), 11, 0 (empty), 22.
        var order = BuildRecentHistory.SiblingOrder(new[] { 11, 22, 33, 44 }, new[] { 33, 99, 11, 0, 22 });
        Assert.Equal(new[] { 2, 4, 0 }, order);
        Assert.Empty(BuildRecentHistory.SiblingOrder(new[] { 0 }, new[] { 0, 0 }));
    }

    [Fact]
    public void Recent_revision_changes_only_when_the_order_changes()
    {
        var history = new BuildRecentHistory();
        history.Record(11, 5);
        int revision = history.Revision;
        Assert.False(history.Record(11, 5));
        Assert.Equal(revision, history.Revision);
        history.Record(22, 5);
        Assert.NotEqual(revision, history.Revision);
    }

    [Fact]
    public void Save_debounce_writes_once_per_wait_however_many_changes()
    {
        var save = new BuildRecentSaveDebounce();
        Assert.False(save.Pending);
        Assert.False(save.Due(0));
        save.Mark(1000);
        save.Mark(1500);
        save.Mark(2900);
        Assert.True(save.Pending);
        Assert.False(save.Due(1000 + BuildRecentSaveDebounce.DelayMs - 1));
        Assert.True(save.Due(1000 + BuildRecentSaveDebounce.DelayMs));
        save.Saved();
        Assert.False(save.Pending);
        Assert.False(save.Due(long.MaxValue));
    }
}
