using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Progenitor.Ecs;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Game-free half of <see cref="BuffSweep"/>: the element-count cap and the buffer-order
/// destroy list. Live GetBufferRawRO / DestroyUtility stays in Core; these tests cover the
/// parts that do not need the game.
/// </summary>
public class BuffSweepTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(-1, 0)]
    [InlineData(1, 1)]
    [InlineData(256, 256)]
    [InlineData(257, 256)]
    [InlineData(10_000, 256)]
    public void BoundLength_caps_at_MaxElements_and_floors_at_zero(int reported, int expected)
    {
        Assert.Equal(expected, BuffSweep.BoundLength(reported));
        Assert.Equal(256, BuffSweep.MaxElements);
    }

    [Fact]
    public void CopyEntities_returns_rows_in_buffer_order()
    {
        // Two BuffBuffer elements: Entity at offset 0 (Index, Version), PrefabGUID at 8 ignored.
        var bytes = new byte[BuffSweep.ElementStride * 2];
        WriteEntity(bytes, 0, index: 10, version: 1);
        WriteEntity(bytes, 1, index: 20, version: 2);
        WriteInt(bytes, (0 * BuffSweep.ElementStride) + 8, 396339796);
        WriteInt(bytes, (1 * BuffSweep.ElementStride) + 8, 111);

        var dest = new BuffSweep.EntityId[4];
        int n = BuffSweep.CopyEntities(bytes, reportedLength: 2, dest);
        Assert.Equal(2, n);
        Assert.Equal(10, dest[0].Index);
        Assert.Equal(1, dest[0].Version);
        Assert.Equal(20, dest[1].Index);
        Assert.Equal(2, dest[1].Version);
    }

    [Fact]
    public void CopyEntities_caps_the_walk_at_MaxElements_keeping_buffer_order()
    {
        int reported = BuffSweep.MaxElements + 3;
        var bytes = new byte[reported * BuffSweep.ElementStride];
        for (int i = 0; i < reported; i++)
            WriteEntity(bytes, i, index: 1000 + i, version: 1);

        var dest = new BuffSweep.EntityId[BuffSweep.MaxElements + 8];
        int n = BuffSweep.CopyEntities(bytes, reported, dest);
        Assert.Equal(BuffSweep.MaxElements, n);
        Assert.Equal(1000, dest[0].Index);
        Assert.Equal(1000 + BuffSweep.MaxElements - 1, dest[n - 1].Index);
    }

    [Fact]
    public void CopyEntities_stops_when_the_span_is_shorter_than_the_claimed_length()
    {
        var bytes = new byte[BuffSweep.ElementStride];
        WriteEntity(bytes, 0, index: 7, version: 3);
        var dest = new BuffSweep.EntityId[8];
        int n = BuffSweep.CopyEntities(bytes, reportedLength: 4, dest);
        Assert.Equal(1, n);
        Assert.Equal(7, dest[0].Index);
        Assert.Equal(3, dest[0].Version);
    }

    [Fact]
    public void SelectExisting_keeps_buffer_order_and_skips_missing_or_null_rows()
    {
        var copied = new[]
        {
            new BuffSweep.EntityId(1, 1),
            new BuffSweep.EntityId(0, 0),
            new BuffSweep.EntityId(2, 1),
            new BuffSweep.EntityId(3, 1),
        };
        var exists = new[] { true, true, false, true };
        var dest = new BuffSweep.EntityId[4];
        int n = BuffSweep.SelectExisting(copied, exists, dest);
        Assert.Equal(2, n);
        Assert.Equal(1, dest[0].Index);
        Assert.Equal(3, dest[1].Index);
    }

    [Fact]
    public void FormatLog_is_the_DIAG5_line_with_stale()
    {
        Assert.Equal(
            "BuffSweep unit=318945:79 buffs=1 destroyed=1 skipped=0 stale=0",
            BuffSweep.FormatLog(318945, 79, 1, 1, 0));
        Assert.Equal(
            "BuffSweep unit=318945:79 buffs=2 destroyed=1 skipped=1 stale=1",
            BuffSweep.FormatLog(318945, 79, 2, 1, 1, 1));
    }

    [Fact]
    public void Layout_constants_are_the_dump_stride_and_entity_offset()
    {
        Assert.Equal(12, BuffSweep.ElementStride);
        Assert.Equal(0, BuffSweep.EntityOffset);
        Assert.Equal(Layout.BuffBufferStride, BuffSweep.ElementStride);
        Assert.Equal(Layout.BuffBufferEntityOffset, BuffSweep.EntityOffset);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(256, false)]
    [InlineData(257, true)]
    [InlineData(10_000, true)]
    public void LengthAboveCap_is_only_above_MaxElements(int reported, bool above)
    {
        Assert.Equal(above, BuffSweep.LengthAboveCap(reported));
    }

    [Fact]
    public void FromPass_length_above_cap_is_incomplete_reads_nothing_and_blocks_DestroyTag()
    {
        var result = BuffSweep.FromPass(
            reportedLength: 257,
            copied: 12,
            destroyed: 12,
            skippedAbsent: 0,
            skippedError: 0,
            exceptionSwallowed: false,
            readAttempted: true);
        Assert.False(result.Complete);
        Assert.Equal(0, result.Found);
        Assert.Equal(0, result.Destroyed);
        Assert.Equal(0, result.Skipped);
        Assert.Equal("length 257 above cap", result.Reason);
        Assert.Equal(0, result.Stale);
        Assert.Empty(result.DestroyedIds);
        Assert.Empty(result.StaleIds);
        Assert.False(BuffSweep.ShouldAddDestroyTag(result));
    }

    [Fact]
    public void FromPass_absent_rows_are_complete()
    {
        var result = BuffSweep.FromPass(
            reportedLength: 2,
            copied: 2,
            destroyed: 1,
            skippedAbsent: 1,
            skippedError: 0,
            exceptionSwallowed: false,
            readAttempted: true);
        Assert.True(result.Complete);
        Assert.Null(result.Reason);
        Assert.Equal(2, result.Found);
        Assert.Equal(1, result.Destroyed);
        Assert.Equal(1, result.Skipped);
        Assert.True(BuffSweep.ShouldAddDestroyTag(result));
    }

    [Fact]
    public void FromPass_error_skip_is_incomplete_and_blocks_DestroyTag()
    {
        var result = BuffSweep.FromPass(
            reportedLength: 2,
            copied: 2,
            destroyed: 1,
            skippedAbsent: 0,
            skippedError: 1,
            exceptionSwallowed: false,
            readAttempted: true);
        Assert.False(result.Complete);
        Assert.Equal("row skipped", result.Reason);
        Assert.False(BuffSweep.ShouldAddDestroyTag(result));
    }

    [Fact]
    public void FromPass_swallowed_exception_is_incomplete()
    {
        var result = BuffSweep.FromPass(
            reportedLength: 1,
            copied: 0,
            destroyed: 0,
            skippedAbsent: 0,
            skippedError: 0,
            exceptionSwallowed: true,
            readAttempted: false);
        Assert.False(result.Complete);
        Assert.Equal("exception swallowed", result.Reason);
        Assert.False(BuffSweep.ShouldAddDestroyTag(result));
    }

    [Fact]
    public void FromPass_unread_buffer_is_incomplete()
    {
        var result = BuffSweep.FromPass(
            reportedLength: 3,
            copied: 0,
            destroyed: 0,
            skippedAbsent: 0,
            skippedError: 0,
            exceptionSwallowed: false,
            readAttempted: false);
        Assert.False(result.Complete);
        Assert.Equal(0, result.Found);
        Assert.Equal("buffer not read", result.Reason);
        Assert.False(BuffSweep.ShouldAddDestroyTag(result));
    }

    [Fact]
    public void FromPass_short_copy_is_incomplete()
    {
        var result = BuffSweep.FromPass(
            reportedLength: 4,
            copied: 1,
            destroyed: 1,
            skippedAbsent: 0,
            skippedError: 0,
            exceptionSwallowed: false,
            readAttempted: true);
        Assert.False(result.Complete);
        Assert.Equal("not every row was read", result.Reason);
        Assert.False(BuffSweep.ShouldAddDestroyTag(result));
    }

    [Fact]
    public void FromPass_exact_cap_with_every_row_read_is_complete()
    {
        var result = BuffSweep.FromPass(
            reportedLength: BuffSweep.MaxElements,
            copied: BuffSweep.MaxElements,
            destroyed: BuffSweep.MaxElements,
            skippedAbsent: 0,
            skippedError: 0,
            exceptionSwallowed: false,
            readAttempted: true);
        Assert.True(result.Complete);
        Assert.Null(result.Reason);
        Assert.True(BuffSweep.ShouldAddDestroyTag(result));
    }

    [Fact]
    public void FromPass_no_buffer_is_complete()
    {
        var result = BuffSweep.FromPass(
            reportedLength: 0,
            copied: 0,
            destroyed: 0,
            skippedAbsent: 0,
            skippedError: 0,
            exceptionSwallowed: false,
            readAttempted: true);
        Assert.True(result.Complete);
        Assert.True(BuffSweep.ShouldAddDestroyTag(result));
    }

    [Fact]
    public void FromPass_records_stale_without_changing_completeness()
    {
        var result = BuffSweep.FromPass(
            reportedLength: 2,
            copied: 2,
            destroyed: 1,
            skippedAbsent: 1,
            skippedError: 0,
            exceptionSwallowed: false,
            readAttempted: true,
            stale: 1);
        Assert.True(result.Complete);
        Assert.Equal(1, result.Stale);
        Assert.Equal(1, result.Destroyed);
        Assert.True(BuffSweep.ShouldAddDestroyTag(result));
    }

    [Fact]
    public void FromPass_length_above_cap_zeros_stale()
    {
        var result = BuffSweep.FromPass(
            reportedLength: 257,
            copied: 0,
            destroyed: 0,
            skippedAbsent: 0,
            skippedError: 0,
            exceptionSwallowed: false,
            readAttempted: false,
            stale: 5);
        Assert.False(result.Complete);
        Assert.Equal(0, result.Stale);
        Assert.Empty(result.StaleIds);
    }

    [Fact]
    public void FromPass_keeps_destroyed_ids()
    {
        var ids = new[] { new BuffSweep.EntityId(10, 1), new BuffSweep.EntityId(20, 2) };
        var result = BuffSweep.FromPass(
            reportedLength: 2,
            copied: 2,
            destroyed: 2,
            skippedAbsent: 0,
            skippedError: 0,
            exceptionSwallowed: false,
            readAttempted: true,
            stale: 0,
            destroyedIds: ids);
        Assert.True(result.Complete);
        Assert.Equal(2, result.DestroyedIds.Length);
        Assert.Equal(10, result.DestroyedIds[0].Index);
        Assert.Equal(20, result.DestroyedIds[1].Index);
        Assert.Same(result.DestroyedIds, result.BuffIds);
    }

    [Fact]
    public void FromPass_keeps_stale_ids()
    {
        var destroyed = new[] { new BuffSweep.EntityId(10, 1) };
        var stale = new[] { new BuffSweep.EntityId(20, 2) };
        var result = BuffSweep.FromPass(
            reportedLength: 2,
            copied: 2,
            destroyed: 1,
            skippedAbsent: 1,
            skippedError: 0,
            exceptionSwallowed: false,
            readAttempted: true,
            stale: 1,
            destroyedIds: destroyed,
            staleIds: stale);
        Assert.True(result.Complete);
        Assert.Equal(1, result.Destroyed);
        Assert.Equal(1, result.Stale);
        Assert.Equal(10, result.DestroyedIds[0].Index);
        Assert.Equal(20, result.StaleIds[0].Index);
        Assert.True(BuffSweep.ShouldAddDestroyTag(result));
    }

    [Theory]
    // existingLeft, unitExists -> phase, action
    [InlineData(true, true, "waiting-buffs", "none")]
    [InlineData(false, true, "tagged", "destroy-unit")]
    [InlineData(false, false, "tagged", "none")]
    [InlineData(true, false, "tagged", "none")]
    public void DecideFinalizeStep_maps_existing_unit_to_phase_and_action(
        bool existingLeft,
        bool unitExists,
        string phase,
        string action)
    {
        var step = BuffSweep.DecideFinalizeStep(existingLeft, unitExists);
        Assert.Equal(phase, step.Phase);
        Assert.Equal(action, step.Action);
    }

    [Fact]
    public void DecideFinalizeStep_finalize_twice_while_listed_exist_is_idempotent()
    {
        var first = BuffSweep.DecideFinalizeStep(existingLeft: true, unitExists: true);
        Assert.Equal("waiting-buffs", first.Phase);
        Assert.Equal(BuffSweep.ActionNone, first.Action);
        Assert.True(BuffSweep.ShouldDestroyBuff(exists: true, hasDestroyTag: false));

        var second = BuffSweep.DecideFinalizeStep(
            existingLeft: true,
            unitExists: true,
            newLiveLeft: false);
        Assert.Equal("waiting-buffs", second.Phase);
        Assert.Equal(BuffSweep.ActionNone, second.Action);
        Assert.False(BuffSweep.ShouldDestroyBuff(exists: true, hasDestroyTag: true));
        Assert.False(BuffSweep.ShouldDestroyBuff(exists: false, hasDestroyTag: false));
    }

    [Fact]
    public void ClassifyBufferRows_stale_rows_only_are_ignored_for_tagged()
    {
        var listed = new[] { new BuffSweep.EntityId(567940, 65), new BuffSweep.EntityId(567763, 8) };
        var rows = new[] { new BuffSweep.EntityId(567940, 65), new BuffSweep.EntityId(567763, 8) };
        var exists = new[] { false, false };
        var stale = new BuffSweep.EntityId[4];
        var newLive = new BuffSweep.EntityId[4];
        BuffSweep.ClassifyBufferRows(listed, rows, exists, stale, newLive, out int staleCount, out int newLiveCount);
        Assert.Equal(2, staleCount);
        Assert.Equal(0, newLiveCount);
        Assert.Equal(567940, stale[0].Index);
        Assert.Equal(567763, stale[1].Index);

        var step = BuffSweep.DecideFinalizeStep(
            existingLeft: false,
            unitExists: true,
            newLiveLeft: newLiveCount > 0);
        Assert.Equal("tagged", step.Phase);
        Assert.Equal(BuffSweep.ActionDestroyUnit, step.Action);
    }

    [Fact]
    public void ClassifyBufferRows_new_live_buff_is_buffs_cycle_then_tagged()
    {
        var listed = new[] { new BuffSweep.EntityId(10, 1), new BuffSweep.EntityId(20, 2) };
        var rows = new[]
        {
            new BuffSweep.EntityId(10, 1),
            new BuffSweep.EntityId(20, 2),
            new BuffSweep.EntityId(30, 3),
        };
        var exists = new[] { false, false, true };
        var stale = new BuffSweep.EntityId[4];
        var newLive = new BuffSweep.EntityId[4];
        BuffSweep.ClassifyBufferRows(listed, rows, exists, stale, newLive, out int staleCount, out int newLiveCount);
        Assert.Equal(2, staleCount);
        Assert.Equal(1, newLiveCount);
        Assert.Equal(30, newLive[0].Index);
        Assert.Equal(3, newLive[0].Version);

        var first = BuffSweep.DecideFinalizeStep(
            existingLeft: false,
            unitExists: true,
            newLiveLeft: true,
            cycle: 1);
        Assert.Equal("buffs", first.Phase);
        Assert.Equal(BuffSweep.ActionDestroyBuffs, first.Action);

        // Second cycle: the new id is now listed, then gone; only stale rows remain.
        var second = BuffSweep.DecideFinalizeStep(
            existingLeft: false,
            unitExists: true,
            newLiveLeft: false,
            cycle: 2);
        Assert.Equal("tagged", second.Phase);
        Assert.Equal(BuffSweep.ActionDestroyUnit, second.Action);
    }

    [Fact]
    public void DecideFinalizeStep_four_cycles_exceeded_is_stuck_without_tagging()
    {
        Assert.Equal(4, BuffSweep.MaxCycles);
        var stuck = BuffSweep.DecideFinalizeStep(
            existingLeft: false,
            unitExists: true,
            newLiveLeft: true,
            cycle: BuffSweep.MaxCycles);
        Assert.Equal("stuck", stuck.Phase);
        Assert.Equal(BuffSweep.ActionNone, stuck.Action);
        Assert.Equal(BuffSweep.FormatStuckReason(4), stuck.Reason);
        Assert.NotEqual(BuffSweep.ActionDestroyUnit, stuck.Action);
    }

    [Fact]
    public void ClassifyBufferRows_listed_live_row_is_neither_stale_nor_new()
    {
        var listed = new[] { new BuffSweep.EntityId(10, 1) };
        var rows = new[] { new BuffSweep.EntityId(10, 1), new BuffSweep.EntityId(0, 0) };
        var exists = new[] { true, true };
        var stale = new BuffSweep.EntityId[2];
        var newLive = new BuffSweep.EntityId[2];
        BuffSweep.ClassifyBufferRows(listed, rows, exists, stale, newLive, out int staleCount, out int newLiveCount);
        Assert.Equal(0, staleCount);
        Assert.Equal(0, newLiveCount);
    }

    [Fact]
    public void ContainsId_matches_index_and_version_and_ignores_null()
    {
        var ids = new[] { new BuffSweep.EntityId(10, 1), new BuffSweep.EntityId(0, 0) };
        Assert.True(BuffSweep.ContainsId(ids, new BuffSweep.EntityId(10, 1)));
        Assert.False(BuffSweep.ContainsId(ids, new BuffSweep.EntityId(10, 2)));
        Assert.False(BuffSweep.ContainsId(ids, new BuffSweep.EntityId(0, 0)));
        Assert.False(BuffSweep.ContainsId(Array.Empty<BuffSweep.EntityId>(), new BuffSweep.EntityId(10, 1)));
    }

    [Theory]
    [InlineData(new bool[] { }, true)]
    [InlineData(new bool[] { false }, true)]
    [InlineData(new bool[] { false, false }, true)]
    [InlineData(new bool[] { true }, false)]
    [InlineData(new bool[] { false, true }, false)]
    public void AllAbsent_is_true_only_when_no_exists_flags_remain(bool[] exists, bool absent)
    {
        Assert.Equal(absent, BuffSweep.AllAbsent(exists));
    }

    [Fact]
    public void FormatDeferTimeoutWarning_names_the_unit_frames_and_pending()
    {
        Assert.Equal(120, BuffSweep.DestroyTagDeferFrames);
        Assert.Equal(
            "BuffSweep DIAG5: proceeding past buffs on 318945:79 after 120 frames with 2 buff(s) still pending",
            BuffSweep.FormatDeferTimeoutWarning(318945, 79, 120, 2));
        Assert.Equal(
            "BuffSweep DIAG5: proceeding past buffs on 318945:79 after 120 frames with 2 buff(s) still pending",
            BuffSweep.FormatDeferTimeoutWarning(318945, 79, 120, 2, "buffs"));
    }

    [Fact]
    public void FormatStuckReason_names_the_cycle_cap()
    {
        Assert.Equal(
            "cycle cap (4) exceeded after 4 destroy cycle(s)",
            BuffSweep.FormatStuckReason(4));
    }

    [Fact]
    public void FormatIncompleteUnitWarning_names_the_unit_and_reason()
    {
        Assert.Equal(
            "BuffSweep incomplete on 318945:79; DestroyTag not added (length 257 above cap)",
            BuffSweep.FormatIncompleteUnitWarning(318945, 79, "length 257 above cap"));
    }

    [Fact]
    public void FormatPlayerRefuseLog_names_the_unit()
    {
        Assert.Equal(
            "Units: despawn refused 1:1 (PlayerCharacter or User)",
            BuffSweep.FormatPlayerRefuseLog(1, 1));
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    [InlineData(false, false, false)]
    public void RefuseAsPlayerOrUser_matches_the_Dev_op(bool player, bool user, bool refuse)
    {
        Assert.Equal(refuse, BuffSweep.RefuseAsPlayerOrUser(player, user));
    }

    // ---------------------------------------------------------------- dangling rows

    [Fact]
    public void DanglingRowsDescending_removes_every_row_of_a_swept_army_unit()
    {
        // Film save AutoSave_10307 (2026-09-27): each stranded army unit kept five rows
        // (api-spawned, EarthCrawl, hide-hud, Spawn_Unit, Wounded_Tracker), none naming an entity.
        var rows = new List<int> { 1563932837, 1820474368, -1224043092, 396339796, 224060472 };
        var exists = new bool[rows.Count];

        var remove = new int[rows.Count];
        int n = BuffSweep.DanglingRowsDescending(exists, remove);

        Assert.Equal(new[] { 4, 3, 2, 1, 0 }, remove.AsSpan(0, n).ToArray());
        ApplyRemoveAt(rows, remove.AsSpan(0, n));
        Assert.Empty(rows);
    }

    [Fact]
    public void DanglingRowsDescending_keeps_live_rows_in_order()
    {
        var rows = new List<int> { 10, 11, 12, 13, 14, 15 };
        var exists = new[] { true, false, true, false, false, true };

        var remove = new int[rows.Count];
        int n = BuffSweep.DanglingRowsDescending(exists, remove);

        Assert.Equal(new[] { 4, 3, 1 }, remove.AsSpan(0, n).ToArray());
        ApplyRemoveAt(rows, remove.AsSpan(0, n));
        Assert.Equal(new[] { 10, 12, 15 }, rows);
    }

    [Fact]
    public void DanglingRowsDescending_is_empty_for_a_clean_buffer()
    {
        var remove = new int[3];
        Assert.Equal(0, BuffSweep.DanglingRowsDescending(new[] { true, true, true }, remove));
        Assert.Equal(0, BuffSweep.DanglingRowsDescending(ReadOnlySpan<bool>.Empty, remove));
    }

    [Fact]
    public void DanglingRowsDescending_stops_at_dest_capacity_highest_first()
    {
        var rows = new List<int> { 0, 1, 2, 3 };
        var exists = new bool[4];

        var remove = new int[2];
        int n = BuffSweep.DanglingRowsDescending(exists, remove);

        // A partial list is still highest first, so applying it never shifts a later index.
        Assert.Equal(new[] { 3, 2 }, remove);
        ApplyRemoveAt(rows, remove.AsSpan(0, n));
        Assert.Equal(new[] { 0, 1 }, rows);
    }

    [Fact]
    public void FormatPruneLog_names_the_unit_and_counts()
    {
        Assert.Equal(
            "Units: despawn: removed 5 of 5 BuffBuffer row(s) naming no entity on 345844:18",
            BuffSweep.FormatPruneLog("Units: despawn", 345844, 18, 5, 5));
    }

    [Fact]
    public void Sweep_never_tags_a_buff_directly()
    {
        // A buff that already carries DestroyTag is skipped by
        // ProcessDestroyEventSystem.TagNewDestroyedEntity, so the game never removes its
        // BuffBuffer row (rig qualification 4, 2026-09-07). The only DestroyTag add left in
        // BuffSweep is the unit's logged fallback in the destroy watcher.
        string source = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Progenitor.Core", "Ecs", "BuffSweep.cs"));
        var adds = Regex.Matches(source, @"(\w+(?:\.\w+)*)\.Add<DestroyTag>\(");
        var add = Assert.Single(adds);
        Assert.Equal("w.Unit", add.Groups[1].Value);
    }

    private static void ApplyRemoveAt(List<int> rows, ReadOnlySpan<int> descending)
    {
        foreach (int i in descending) rows.RemoveAt(i);
    }

    private static string RepoRoot()
    {
        string? dir = Path.GetFullPath(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir != null; i++)
        {
            if (File.Exists(Path.Combine(dir, "Progenitor.sln"))) return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }
        throw new DirectoryNotFoundException("Progenitor.sln not found from " + AppContext.BaseDirectory);
    }

    private static void WriteEntity(byte[] bytes, int element, int index, int version)
    {
        int at = element * BuffSweep.ElementStride + BuffSweep.EntityOffset;
        WriteInt(bytes, at, index);
        WriteInt(bytes, at + 4, version);
    }

    private static void WriteInt(byte[] bytes, int at, int value)
    {
        bytes[at] = (byte)value;
        bytes[at + 1] = (byte)(value >> 8);
        bytes[at + 2] = (byte)(value >> 16);
        bytes[at + 3] = (byte)(value >> 24);
    }
}
