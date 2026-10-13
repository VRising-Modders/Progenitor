using System.Collections.Generic;
using Progenitor.Bootstrap;
using Progenitor.Units;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Game-free halves of the Units despawn hand-off across a Core swap: Bootstrap's
/// <see cref="DespawnHandoffBook"/> and Core's <see cref="DespawnHandoffPolicy"/>.
/// </summary>
public class DespawnHandoffTests
{
    private const ulong World = 7;

    [Fact]
    public void Stride_is_the_same_on_both_sides()
    {
        Assert.Equal(DespawnHandoffBook.Stride, DespawnHandoffPolicy.Stride);
    }

    [Fact]
    public void Handed_off_rows_come_back_once_in_the_same_world()
    {
        var book = new DespawnHandoffBook();
        int[] rows = DespawnHandoffPolicy.Pack(new List<(int, int, int)> { (582131, 5, 1234), (346783, 41, 1234) });

        Assert.Equal(2, book.HandOff(rows, World));
        int[] taken = book.Take(World, out int stale);

        Assert.Equal(rows, taken);
        Assert.Equal(0, stale);
        Assert.Equal(0, book.Count);
        Assert.Empty(book.Take(World, out _));
    }

    [Fact]
    public void A_save_load_after_the_hand_off_drops_every_row()
    {
        var book = new DespawnHandoffBook();
        book.HandOff(new[] { 10, 1, 99, 11, 1, 99 }, World);

        book.NoteWorldLoad();
        int[] taken = book.Take(World, out int stale);

        Assert.Empty(taken);
        Assert.Equal(2, stale);
        Assert.Equal(0, book.Count);
    }

    [Fact]
    public void Rows_from_another_world_are_dropped_and_counted()
    {
        var book = new DespawnHandoffBook();
        book.HandOff(new[] { 10, 1, 99 }, World);
        book.HandOff(new[] { 20, 1, 99 }, World + 1);

        int[] taken = book.Take(World + 1, out int stale);

        Assert.Equal(new[] { 20, 1, 99 }, taken);
        Assert.Equal(1, stale);
    }

    [Fact]
    public void A_row_handed_off_after_a_load_is_current()
    {
        var book = new DespawnHandoffBook();
        book.NoteWorldLoad();
        book.HandOff(new[] { 10, 1, 99 }, World);

        Assert.Equal(new[] { 10, 1, 99 }, book.Take(World, out int stale));
        Assert.Equal(0, stale);
    }

    [Fact]
    public void Null_ids_duplicates_and_a_partial_row_are_skipped()
    {
        var book = new DespawnHandoffBook();

        Assert.Equal(1, book.HandOff(new[] { 0, 0, 5, 10, 1, 99, 10, 1, 99, 11 }, World));
        Assert.Equal(0, book.HandOff(new[] { 10, 1, 99 }, World));
        Assert.Equal(0, book.HandOff(null, World));
        Assert.Equal(1, book.Count);
    }

    [Fact]
    public void The_book_stops_at_MaxRows()
    {
        var book = new DespawnHandoffBook();
        var rows = new int[(DespawnHandoffBook.MaxRows + 5) * DespawnHandoffBook.Stride];
        for (int i = 0; i < DespawnHandoffBook.MaxRows + 5; i++)
        {
            rows[i * 3] = i + 1;
            rows[i * 3 + 1] = 1;
        }

        Assert.Equal(DespawnHandoffBook.MaxRows, book.HandOff(rows, World));
        Assert.Equal(DespawnHandoffBook.MaxRows, book.Count);
    }

    [Theory]
    [InlineData(false, false, false, (int)DespawnResume.Gone)]
    [InlineData(false, false, true, (int)DespawnResume.Gone)]
    [InlineData(true, true, true, (int)DespawnResume.Gone)]
    [InlineData(true, false, false, (int)DespawnResume.OtherPrefab)]
    [InlineData(true, false, true, (int)DespawnResume.Resume)]
    public void Decide_resumes_only_a_live_unit_of_the_same_prefab(bool exists, bool destroying, bool samePrefab, int expected)
    {
        Assert.Equal((DespawnResume)expected, DespawnHandoffPolicy.Decide(exists, destroying, samePrefab));
    }

    [Fact]
    public void Pack_and_RowCount_agree()
    {
        int[] packed = DespawnHandoffPolicy.Pack(new List<(int, int, int)> { (1, 2, 3), (4, 5, 6) });

        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6 }, packed);
        Assert.Equal(2, DespawnHandoffPolicy.RowCount(packed));
        Assert.Equal(1, DespawnHandoffPolicy.RowCount(new[] { 1, 2, 3, 4 }));
        Assert.Equal(0, DespawnHandoffPolicy.RowCount(null));
    }

    [Fact]
    public void Log_lines_name_the_counts()
    {
        Assert.Equal(
            "Units: handed 4 of 4 despawn(s) still in flight to Bootstrap for the next Core generation",
            DespawnHandoffPolicy.FormatHandOff(4, 4));
        Assert.Equal(
            "Units: 4 despawn(s) still in flight at uninstall; this Bootstrap cannot hold them, so those units stay in the world",
            DespawnHandoffPolicy.FormatHandOff(4, -1));
        Assert.Equal(
            "Units: resumed 3 despawn(s) handed over by the previous Core generation (already gone 1, id now another prefab 0, refused 0, dropped by Bootstrap for a reloaded or replaced world 2)",
            DespawnHandoffPolicy.FormatResume(3, 1, 0, 0, 2));
    }
}
