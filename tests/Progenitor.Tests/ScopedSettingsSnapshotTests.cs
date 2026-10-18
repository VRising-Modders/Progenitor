using System;
using System.Collections.Generic;
using Progenitor.Networking;
using Xunit;

namespace Progenitor.Tests;

public sealed class ScopedSettingsSnapshotTests
{
    private static readonly Guid A = Guid.Parse("1260d410-9513-4a37-8555-3ed1b8a23ad9");
    private static readonly Guid B = Guid.Parse("2260d410-9513-4a37-8555-3ed1b8a23ad9");
    private static ScopedSettingsPacket Begin(int count, Guid? generation = null) => new()
    { Kind = 1, Generation = generation ?? A, Count = count, CanEdit = true, ServerName = "Test server" };
    private static ScopedSettingsPacket End(int count, Guid? generation = null) => new()
    { Kind = 3, Generation = generation ?? A, Count = count };
    private static ScopedSettingsPacket Row(int index = 0, Guid? generation = null) => new()
    {
        Kind = 2, Generation = generation ?? A,
        Row = new ScopedSettingRecord
        {
            Id = "mod/Section/Key" + index, PluginId = "mod", Section = "Section", Key = "Key" + index,
            ModName = "Test mod", Version = "1.0", Help = "Help", Type = "System.String",
            Default = "default", Value = "current", Personal = true, Editable = true,
            Choices = new[] { "default", "current" }, Min = "a", Max = "z", MaxLength = 99,
        },
    };
    private static void Empty(ScopedSettingsSnapshot s)
    {
        Assert.Empty(s.Rows); Assert.Equal(Guid.Empty, s.Generation); Assert.Equal(0, s.BeginCount);
        Assert.False(s.CanEdit); Assert.Equal("", s.ServerName);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(512)]
    public void Publishes_exactly_once_only_after_all_rows_and_matching_end(int count)
    {
        var s = new ScopedSettingsSnapshot();
        Empty(s);
        Assert.False(s.Accept(Begin(count), 10));
        Assert.Equal(count, s.BeginCount);
        for (int i = 0; i < count; i++)
        {
            Assert.False(s.Accept(Row(i), 11));
            Assert.Empty(s.Rows);
        }
        Assert.True(s.Accept(End(count), 12));
        Assert.Equal(count, s.Rows.Count); Assert.Equal(A, s.Generation);
        Assert.True(s.CanEdit); Assert.Equal("Test server", s.ServerName);
        for (int i = 0; i < count; i++) Assert.Equal("mod/Section/Key" + i, s.Rows[i].Id);
        Assert.False(s.Accept(End(count), 13));
        Assert.False(s.Accept(Row(count), 13));
        Assert.Equal(count, s.Rows.Count);
    }

    [Theory]
    [InlineData(-1)] [InlineData(513)] [InlineData(int.MaxValue)]
    public void Invalid_begin_count_discards_candidate_and_cannot_complete(int count)
    {
        var s = new ScopedSettingsSnapshot();
        s.Accept(Begin(1), 0); s.Accept(Row(), 1);
        Assert.False(s.Accept(Begin(count, B), 2));
        Empty(s);
        Assert.False(s.Accept(End(1), 3));
        Assert.False(s.Accept(End(count, B), 3));
    }

    [Fact]
    public void Missing_begin_or_empty_generation_never_completes()
    {
        var s = new ScopedSettingsSnapshot();
        Assert.False(s.Accept(Row(), 0)); Assert.False(s.Accept(End(0), 0));
        Assert.False(s.Accept(Begin(0, Guid.Empty), 0));
        Assert.False(s.Accept(End(0, Guid.Empty), 0)); Empty(s);
    }

    [Fact]
    public void Duplicate_row_invalidates_whole_snapshot_even_with_different_value()
    {
        var s = new ScopedSettingsSnapshot(); s.Accept(Begin(2), 0); s.Accept(Row(), 1);
        var duplicate = Row(); duplicate.Row!.Value = "different";
        Assert.False(s.Accept(duplicate, 2)); Empty(s);
        Assert.False(s.Accept(Row(1), 3)); Assert.False(s.Accept(End(2), 4));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(512)]
    public void Extra_row_invalidates_candidate(int count)
    {
        var s = new ScopedSettingsSnapshot(); s.Accept(Begin(count), 0);
        for (int i = 0; i < count; i++) s.Accept(Row(i), 1);
        Assert.False(s.Accept(Row(count), 2)); Empty(s);
        Assert.False(s.Accept(End(count), 3));
    }

    [Theory]
    [InlineData(0)] [InlineData(2)] [InlineData(-1)] [InlineData(513)]
    public void Wrong_end_count_invalidates_candidate(int count)
    {
        var s = new ScopedSettingsSnapshot(); s.Accept(Begin(1), 0); s.Accept(Row(), 1);
        Assert.False(s.Accept(End(count), 2)); Empty(s);
        Assert.False(s.Accept(End(1), 3));
    }

    [Fact]
    public void Missing_row_at_end_discards_partial_result()
    {
        var s = new ScopedSettingsSnapshot(); s.Accept(Begin(2), 0); s.Accept(Row(), 1);
        Assert.False(s.Accept(End(2), 2)); Empty(s);
        Assert.False(s.Accept(Row(1), 3)); Assert.False(s.Accept(End(2), 4));
    }

    [Fact]
    public void Stale_rows_and_end_do_not_poison_new_generation()
    {
        var s = new ScopedSettingsSnapshot(); s.Accept(Begin(2), 0); s.Accept(Row(), 1);
        Assert.False(s.Accept(Begin(1, B), 2));
        Assert.False(s.Accept(Row(1), 3)); Assert.False(s.Accept(End(2), 3));
        Assert.False(s.Accept(Row(9, B), 4)); Assert.True(s.Accept(End(1, B), 5));
        Assert.Equal(B, s.Generation); Assert.Equal("mod/Section/Key9", Assert.Single(s.Rows).Id);
    }

    [Theory]
    [InlineData(29.999, true)] [InlineData(30, false)] [InlineData(31, false)]
    public void Timeout_is_absolute_and_inclusive(double elapsed, bool completes)
    {
        var s = new ScopedSettingsSnapshot(); s.Accept(Begin(1), 100);
        s.Accept(Row(), 120);
        Assert.Equal(completes, s.Accept(End(1), 100 + elapsed));
        if (!completes) Empty(s);
    }

    [Fact]
    public void Duplicate_begin_cannot_extend_deadline_or_replay_completion()
    {
        var s = new ScopedSettingsSnapshot(); s.Accept(Begin(1), 0); s.Accept(Row(), 1);
        s.Accept(Begin(1), 29);
        Assert.False(s.Accept(End(1), 30)); Empty(s);
        s.Accept(Begin(0, B), 31); Assert.True(s.Accept(End(0, B), 32));
        Assert.False(s.Accept(Begin(0, B), 40)); Assert.False(s.Accept(End(0, B), 41));
    }

    [Fact]
    public void Fresh_begin_after_timeout_can_complete()
    {
        var s = new ScopedSettingsSnapshot(); s.Accept(Begin(1), 0);
        Assert.False(s.Accept(Begin(1, B), 31));
        s.Accept(Row(3, B), 32); Assert.True(s.Accept(End(1, B), 33));
    }

    [Theory]
    [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(255)]
    public void Unrelated_packets_do_not_count_as_rows_but_check_expiration(int kind)
    {
        var s = new ScopedSettingsSnapshot(); s.Accept(Begin(1), 0);
        var p = new ScopedSettingsPacket { Kind = (byte)kind, Generation = A, Row = Row().Row };
        Assert.False(s.Accept(p, 1)); Assert.Empty(s.Rows);
        Assert.False(s.Accept(p, 30)); Empty(s);
        Assert.False(s.Accept(End(1), 31));
    }

    [Theory]
    [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)] [InlineData(double.NegativeInfinity)] [InlineData(-1)]
    public void Invalid_clock_cannot_complete_candidate(double now)
    {
        var s = new ScopedSettingsSnapshot(); s.Accept(Begin(0), 0);
        Assert.False(s.Accept(End(0), now)); Empty(s);
    }

    [Fact]
    public void Null_or_missing_row_is_refused()
    {
        var s = new ScopedSettingsSnapshot(); Assert.False(s.Accept(null!, 0));
        s.Accept(Begin(1), 0);
        Assert.False(s.Accept(new ScopedSettingsPacket { Kind = 2, Generation = A }, 1)); Empty(s);
        s.Accept(Begin(1), 2); var p = Row(); p.Row!.Id = "";
        Assert.False(s.Accept(p, 3)); Empty(s);
    }

    [Fact]
    public void Reset_clears_pending_and_completed_state_without_mutating_published_collection()
    {
        var s = new ScopedSettingsSnapshot(); s.Accept(Begin(1), 0); s.Accept(Row(), 1);
        s.Reset(); Empty(s); Assert.False(s.Accept(End(1), 2));
        s.Accept(Begin(1, B), 3); s.Accept(Row(7, B), 4); Assert.True(s.Accept(End(1, B), 5));
        var published = s.Rows;
        s.Reset(); Empty(s); Assert.Single(published);
        Assert.Throws<NotSupportedException>(() => ((IList<ScopedSettingRecord>)published).Clear());
    }

    [Fact]
    public void Accepted_rows_and_choices_are_owned_copies_with_all_metadata()
    {
        var s = new ScopedSettingsSnapshot(); s.Accept(Begin(1), 0);
        var p = Row(); var original = p.Row!; s.Accept(p, 1);
        original.Id = "changed"; original.Value = "changed"; original.Choices[0] = "changed";
        Assert.True(s.Accept(End(1), 2)); var row = Assert.Single(s.Rows);
        Assert.Equal("mod/Section/Key0", row.Id); Assert.Equal("current", row.Value);
        Assert.Equal("default", row.Choices[0]); Assert.Equal("mod", row.PluginId);
        Assert.Equal("Test mod", row.ModName); Assert.Equal("1.0", row.Version);
        Assert.Equal("Section", row.Section); Assert.Equal("Key0", row.Key);
        Assert.Equal("Help", row.Help); Assert.Equal("System.String", row.Type);
        Assert.Equal("default", row.Default); Assert.True(row.Personal); Assert.True(row.Editable);
        Assert.Equal("a", row.Min); Assert.Equal("z", row.Max); Assert.Equal(99, row.MaxLength);
    }

    [Fact]
    public void Wire_decoded_packets_drive_real_accumulator()
    {
        var s = new ScopedSettingsSnapshot();
        var packets = new[] { Begin(1), Row(), End(1) };
        for (int i = 0; i < packets.Length; i++)
        {
            Assert.True(ScopedSettingsWire.TryDecode(ScopedSettingsWire.Encode(packets[i]), true, out var decoded));
            Assert.Equal(i == 2, s.Accept(decoded, i));
        }
        Assert.Single(s.Rows);
    }
}
