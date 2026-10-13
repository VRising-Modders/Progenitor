using System;
using System.Collections.Generic;
using Progenitor.Bootstrap;
using Xunit;

using Verdict = Progenitor.Bootstrap.SaveSafetyPolicy.BuffRowVerdict;

namespace Progenitor.Tests;

/// <summary>
/// Game-free half of the load guard's <c>BuffBuffer</c> repair and retire path (M2.11 connect
/// crash, 2026-09-28; research <c>save-safety.md</c> §9). Dropping a buff with a bare
/// <c>DestroyEntity</c> left the wearer's persisted <c>BuffBuffer</c> row pointing at nothing, and
/// the server aborted in a Burst job when that player connected. The live buffer walk and
/// <c>DestroyUtility</c> need the game; which rows go, in what order, and what the operator reads
/// do not.
/// </summary>
public class SaveSafetyBuffRowTests
{
    /// <summary>The Sample's <c>sample.nodrain</c> clone guid from the fscope1-serverB run-1 log.</summary>
    private const int SampleNoDrainGuid = 650563057;

    [Fact]
    public void Row_layout_mirrors_the_checked_layout_constants()
    {
        // Bootstrap must not compile-reference Core, so it carries its own copy; this pins them.
        Assert.Equal(Layout.BuffBufferStride, SaveSafetyPolicy.BuffRowStride);
        Assert.Equal(Layout.BuffBufferEntityOffset, SaveSafetyPolicy.BuffRowEntityOffset);
        Assert.Equal(Layout.BuffBufferPrefabGuidOffset, SaveSafetyPolicy.BuffRowPrefabGuidOffset);
    }

    [Theory]
    [InlineData(false, true, true, Verdict.Keep)]
    [InlineData(true, true, true, Verdict.DroppedTarget)]   // the dropped buff still exists at the postfix
    [InlineData(true, false, false, Verdict.DroppedTarget)]
    [InlineData(false, false, false, Verdict.Dangling)]     // what the run-2 save carried, if remapped to nothing
    [InlineData(false, true, false, Verdict.Mismatched)]    // a stale id that collides with another entity
    public void A_row_is_removed_only_when_it_names_a_dropped_missing_or_different_entity(
        bool dropped, bool exists, bool guidMatches, Verdict expected)
    {
        Assert.Equal(expected, SaveSafetyPolicy.ClassifyBuffRow(dropped, exists, guidMatches));
    }

    [Fact]
    public void A_clean_buffer_is_never_edited()
    {
        var verdicts = new[] { Verdict.Keep, Verdict.Keep, Verdict.Keep };
        Assert.Empty(SaveSafetyPolicy.RowsToRemove(verdicts));
    }

    [Fact]
    public void Removal_indices_are_descending_so_RemoveAt_keeps_every_live_row()
    {
        var verdicts = new[] { Verdict.Dangling, Verdict.Keep, Verdict.DroppedTarget, Verdict.Keep, Verdict.Mismatched };
        int[] remove = SaveSafetyPolicy.RowsToRemove(verdicts);
        Assert.Equal(new[] { 4, 2, 0 }, remove);

        // Applied one at a time the way DynamicBuffer.RemoveAt applies them, only the live rows
        // survive, in their original order.
        var rows = new List<string> { "dangling", "haste", "nodrain", "garlic", "collided" };
        foreach (int i in remove) rows.RemoveAt(i);
        Assert.Equal(new[] { "haste", "garlic" }, rows);
    }

    [Fact]
    public void Rows_are_read_at_the_dump_offsets()
    {
        // Two rows: {Entity 345500:1, guid nodrain}, {Entity 12:3, guid 111}. Same bytes the raw
        // GetBufferRawRO walk reads.
        var bytes = new byte[SaveSafetyPolicy.BuffRowStride * 2];
        WriteRow(bytes, 0, index: 345500, version: 1, guid: SampleNoDrainGuid);
        WriteRow(bytes, 1, index: 12, version: 3, guid: 111);

        Assert.Equal(345500, ReadInt(bytes, Row(0) + SaveSafetyPolicy.BuffRowEntityOffset));
        Assert.Equal(1, ReadInt(bytes, Row(0) + SaveSafetyPolicy.BuffRowEntityOffset + 4));
        Assert.Equal(SampleNoDrainGuid, ReadInt(bytes, Row(0) + SaveSafetyPolicy.BuffRowPrefabGuidOffset));
        Assert.Equal(12, ReadInt(bytes, Row(1) + SaveSafetyPolicy.BuffRowEntityOffset));
        Assert.Equal(3, ReadInt(bytes, Row(1) + SaveSafetyPolicy.BuffRowEntityOffset + 4));
        Assert.Equal(111, ReadInt(bytes, Row(1) + SaveSafetyPolicy.BuffRowPrefabGuidOffset));
    }

    [Fact]
    public void The_clean_line_says_the_check_ran()
    {
        var tally = new SaveSafetyPolicy.BuffRowTally();
        tally.NoteHolder();
        tally.NoteHolder();
        string line = SaveSafetyPolicy.FormatBuffRows(tally, repaired: true, elapsedMs: 7);
        Assert.Contains("2 deserialized buff holder(s)", line, StringComparison.Ordinal);
        Assert.Contains("every row names a live entity", line, StringComparison.Ordinal);
    }

    [Fact]
    public void The_repair_line_names_the_guid_and_the_kind_of_row()
    {
        var tally = new SaveSafetyPolicy.BuffRowTally();
        tally.NoteHolder();
        tally.NoteHolder();
        tally.NoteEdited();
        tally.Add(Verdict.DroppedTarget, SampleNoDrainGuid);
        tally.Add(Verdict.Dangling, 42);
        tally.Add(Verdict.Mismatched, 43);
        tally.Add(Verdict.Keep, 7);   // ignored

        Assert.Equal(1, tally.DroppedRows);
        Assert.Equal(1, tally.DanglingRows);
        Assert.Equal(1, tally.MismatchedRows);
        string line = SaveSafetyPolicy.FormatBuffRows(tally, repaired: true, elapsedMs: 3);
        Assert.Contains(
            "removed 1 row(s) naming a dropped entity, 1 row(s) naming no entity and 1 row(s) naming an entity of another prefab",
            line,
            StringComparison.Ordinal);
        Assert.Contains("dropped " + SampleNoDrainGuid + "x1", line, StringComparison.Ordinal);
        Assert.Contains("dangling 42x1", line, StringComparison.Ordinal);
        Assert.Contains("mismatched 43x1", line, StringComparison.Ordinal);
        Assert.Contains("on 1 of 2 buff holder(s)", line, StringComparison.Ordinal);
        Assert.DoesNotContain("could not be edited", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Report_only_mode_removes_nothing_and_warns_about_the_connect_crash()
    {
        var tally = new SaveSafetyPolicy.BuffRowTally();
        tally.NoteHolder();
        tally.NoteEdited();
        tally.Add(Verdict.Dangling, SampleNoDrainGuid);
        string line = SaveSafetyPolicy.FormatBuffRows(tally, repaired: false, elapsedMs: 1);
        Assert.Contains("nothing was removed", line, StringComparison.Ordinal);
        Assert.Contains("crash the server on connect", line, StringComparison.Ordinal);
    }

    [Fact]
    public void A_holder_that_could_not_be_edited_is_reported()
    {
        var tally = new SaveSafetyPolicy.BuffRowTally();
        tally.NoteHolder();
        tally.NoteEdited();
        tally.Add(Verdict.Dangling, 1);
        tally.NoteFailed();
        Assert.Contains(
            "1 holder(s) could not be read or edited",
            SaveSafetyPolicy.FormatBuffRows(tally, repaired: true, elapsedMs: 0),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Holders_that_could_not_be_read_never_produce_the_clean_line()
    {
        // Reviewer finding: a holder whose read threw recorded no row, so the load logged "every
        // row names a live entity" while that holder might still carry the crashing row.
        var tally = new SaveSafetyPolicy.BuffRowTally();
        tally.NoteHolder();
        tally.NoteFailed();
        Assert.True(tally.IsEmpty);
        Assert.False(tally.IsClean);
        string line = SaveSafetyPolicy.FormatBuffRows(tally, repaired: true, elapsedMs: 0);
        Assert.Contains("1 holder(s) could not be read and were not checked", line, StringComparison.Ordinal);
    }

    [Fact]
    public void The_verify_line_separates_game_destroys_from_forced_ones()
    {
        string clean = SaveSafetyPolicy.FormatRetireVerified(planned: 3, gone: 3, forced: 0, failed: 0, attempts: 1);
        Assert.Contains("3 of 3 retired entity(ies) were destroyed by the game", clean, StringComparison.Ordinal);
        Assert.DoesNotContain("directly", clean, StringComparison.Ordinal);

        string forced = SaveSafetyPolicy.FormatRetireVerified(planned: 3, gone: 1, forced: 2, failed: 0, attempts: 6);
        Assert.Contains("2 were still present after 6 check(s) 10 s apart and were destroyed directly", forced, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, 1, SaveSafetyPolicy.RetireCheck.Gone)]
    [InlineData(false, 6, SaveSafetyPolicy.RetireCheck.Gone)]
    [InlineData(true, 1, SaveSafetyPolicy.RetireCheck.Wait)]
    [InlineData(true, 5, SaveSafetyPolicy.RetireCheck.Wait)]
    [InlineData(true, 6, SaveSafetyPolicy.RetireCheck.Force)]
    [InlineData(true, 7, SaveSafetyPolicy.RetireCheck.Force)]
    public void A_survivor_is_only_forced_on_the_last_check(bool exists, int attempt, SaveSafetyPolicy.RetireCheck expected)
    {
        // Reviewer finding: the timer is wall-clock. Forcing a DestroyEntity before the game's
        // ModificationCleanupSystem saw the entity would bring back the orphaned modifications.
        Assert.Equal(6, SaveSafetyPolicy.RetireVerifyMaxAttempts);
        Assert.Equal(expected, SaveSafetyPolicy.RetireCheckAction(exists, attempt, SaveSafetyPolicy.RetireVerifyMaxAttempts));
    }

    [Fact]
    public void RowsToRemove_rejects_null()
    {
        Assert.Throws<ArgumentNullException>(() => SaveSafetyPolicy.RowsToRemove(null!));
    }

    private static int Row(int row) => row * SaveSafetyPolicy.BuffRowStride;

    private static void WriteRow(byte[] bytes, int row, int index, int version, int guid)
    {
        WriteInt(bytes, Row(row) + SaveSafetyPolicy.BuffRowEntityOffset, index);
        WriteInt(bytes, Row(row) + SaveSafetyPolicy.BuffRowEntityOffset + 4, version);
        WriteInt(bytes, Row(row) + SaveSafetyPolicy.BuffRowPrefabGuidOffset, guid);
    }

    private static void WriteInt(byte[] bytes, int at, int value) =>
        BitConverter.GetBytes(value).CopyTo(bytes, at);

    private static int ReadInt(byte[] bytes, int at) => BitConverter.ToInt32(bytes, at);
}
