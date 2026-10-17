using System;
using System.Collections.Generic;
using System.Linq;
using Progenitor.Chat;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// CHAT5: which entries of the game's chat list (capped at 50, oldest dropped first) are new since the last read.
/// The bug this covers: ten lines sent to one player in one handler reached the client in one frame, the full list kept
/// its count of 50, and the chat view took only the newest line.
/// </summary>
public sealed class ChatNewEntriesTests
{
    private const int Cap = 50;

    /// <summary>Entry "pointers" as the game's list would hold them: ids from <paramref name="from"/>, oldest first.</summary>
    private static List<IntPtr> Ids(int from, int count) =>
        Enumerable.Range(from, count).Select(i => new IntPtr(1000 + i)).ToList();

    /// <summary>The game's list after <paramref name="added"/> appends to <paramref name="list"/>, capped.</summary>
    private static List<IntPtr> Append(List<IntPtr> list, IEnumerable<IntPtr> added)
    {
        var next = new List<IntPtr>(list);
        foreach (var a in added)
        {
            next.Add(a);
            if (next.Count > Cap) next.RemoveAt(0);
        }

        return next;
    }

    [Fact]
    public void Ten_lines_in_one_frame_on_a_full_list_are_all_new_in_order()
    {
        var before = Ids(0, Cap);
        var burst = Ids(500, 10);
        var after = Append(before, burst);
        Assert.Equal(Cap, after.Count);

        int first = ChatNewEntries.FirstNew(before, after);

        Assert.Equal(Cap - 10, first);
        Assert.Equal(burst, after.Skip(first).ToList());
    }

    [Fact]
    public void Lines_on_a_list_that_is_still_growing_are_new()
    {
        var before = Ids(0, 7);
        var after = Append(before, Ids(500, 3));
        Assert.Equal(7, ChatNewEntries.FirstNew(before, after));
    }

    [Fact]
    public void A_burst_that_crosses_the_cap_is_counted_whole()
    {
        var before = Ids(0, 45);
        var burst = Ids(500, 10);
        var after = Append(before, burst);
        int first = ChatNewEntries.FirstNew(before, after);
        Assert.Equal(burst, after.Skip(first).ToList());
    }

    [Fact]
    public void One_line_on_a_full_list_is_one_new_entry()
    {
        var before = Ids(0, Cap);
        var after = Append(before, Ids(500, 1));
        Assert.Equal(Cap - 1, ChatNewEntries.FirstNew(before, after));
    }

    [Fact]
    public void An_unchanged_list_has_nothing_new()
    {
        var before = Ids(0, Cap);
        Assert.Equal(Cap, ChatNewEntries.FirstNew(before, new List<IntPtr>(before)));
    }

    [Fact]
    public void More_lines_than_the_list_holds_are_all_new()
    {
        var before = Ids(0, Cap);
        var after = Append(before, Ids(500, Cap + 5));
        Assert.Equal(0, ChatNewEntries.FirstNew(before, after));
    }

    [Fact]
    public void An_empty_previous_read_makes_every_entry_new()
    {
        var after = Ids(0, 3);
        Assert.Equal(0, ChatNewEntries.FirstNew(new List<IntPtr>(), after));
        Assert.Equal(0, ChatNewEntries.FirstNew(null, after));
        Assert.Equal(0, ChatNewEntries.FirstNew(Ids(0, 3), new List<IntPtr>()));
    }

    [Fact]
    public void A_list_changed_in_the_middle_falls_back_to_after_the_previous_newest()
    {
        // An entry removed from the middle: no tail lines up, but the previous newest is still there.
        var before = Ids(0, 10);
        var after = new List<IntPtr>(before);
        after.RemoveAt(4);
        after.AddRange(Ids(500, 2));
        int first = ChatNewEntries.FirstNew(before, after);
        Assert.Equal(Ids(500, 2), after.Skip(first).ToList());
    }
}
