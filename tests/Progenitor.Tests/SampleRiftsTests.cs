using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Progenitor.Sample;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Job FRIFT1. Game-free coverage of the rift-schedule example: the wire shape, the snapshot
/// builder (and above all the scheduled-versus-estimated distinction), the clock skew, the
/// countdown formatter, the cadence decision and the handshake-edge bookkeeping. The
/// <c>WarEventSystem</c> reads, the sends, the receive and the row push need a live server and
/// client and are behind <c>!PROGENITOR_TESTS</c>.
/// </summary>
public class SampleRiftsTests : IDisposable
{
    private const long TicksPerSecond = TimeSpan.TicksPerSecond;

    /// <summary>An arbitrary but realistic absolute UTC tick value to anchor every case on.</summary>
    private static readonly long Now = new DateTime(2026, 9, 5, 12, 0, 0, DateTimeKind.Utc).Ticks;

    /// <summary>Every case starts from a clean generation; so does every teardown.</summary>
    public SampleRiftsTests()
    {
        SampleRifts.Reset();
        // Job FEX9: the template resolver is a generation-scoped static (Attach sets it, Detach
        // clears it), so a case that installs one must not leak it into the next.
        SampleRifts.TemplateResolver = null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        SampleRifts.TemplateResolver = null;
        SampleRifts.RemoveSink();
        SampleRifts.Reset();
        GC.SuppressFinalize(this);
    }

    private static long Seconds(double s) => (long)(s * TicksPerSecond);

    // ---- the wire ---------------------------------------------------------------------------

    [Fact]
    // The Sample's ids are ASCII "Sm??" words outside Progenitor's reserved 0x5072 ("Pr**") range,
    // which CustomEvents refuses a plugin registration inside. The cross-file check is by
    // convention rather than by referencing the other Sample files, which the
    // -p:SkipForeignTestFiles build drops.
    public void The_event_id_is_the_ascii_word_SmRf_outside_the_reserved_range()
    {
        Assert.Equal(0x536D5266, SampleRifts.ScheduleEventId);   // "SmRf"
        Assert.Equal(0x536D, SampleRifts.ScheduleEventId >> 16);
        Assert.NotEqual(0x5072, SampleRifts.ScheduleEventId >> 16);
    }

    [Fact]
    // The struct travels as sizeof(T) bytes (CustomEvents.SendToUser<T> / TryRead<T>) and TryRead
    // refuses any other length, so the declared payload length and the real one must not drift.
    // The number is 56, not the 52 the fields add up to: five longs give the struct an 8-byte
    // alignment and the trailing four bytes are padded out. Unsafe.SizeOf<T> is the metric the
    // transport uses (`sizeof` IL); Marshal.SizeOf agrees here, and both are pinned, because "the
    // size I can compute" versus "the size the runtime uses" is exactly the trap the stride rule
    // in CLAUDE.md exists for - the research doc's 56 was right and 5*8+2*4+4 was not.
    public void The_payload_length_matches_the_struct()
    {
        Assert.Equal(56, SampleRifts.PayloadBytes);
        Assert.NotEqual(5 * 8 + 2 * 4 + 4, SampleRifts.PayloadBytes);
        Assert.Equal(SampleRifts.PayloadBytes, Unsafe.SizeOf<SampleRifts.RiftSchedule>());
        Assert.Equal(SampleRifts.PayloadBytes, Marshal.SizeOf<SampleRifts.RiftSchedule>());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(52)]
    [InlineData(55)]
    [InlineData(57)]
    [InlineData(112)]
    public void A_payload_that_is_not_exactly_one_schedule_is_refused(int length)
    {
        Assert.False(SampleRifts.IsPayloadLength(length));
    }

    [Fact]
    public void A_payload_of_exactly_one_schedule_is_admitted()
    {
        Assert.True(SampleRifts.IsPayloadLength(SampleRifts.PayloadBytes));
    }

    [Fact]
    // WarEventType.Major == 0 (server dump 549577), so a zeroed byte on the wire reads as Major.
    // "None" therefore needs an explicit sentinel and it must not be zero.
    public void The_none_sentinel_is_not_zero_because_Major_is()
    {
        Assert.Equal(0, (byte)SampleRifts.RiftTier.Major);
        Assert.Equal(1, (byte)SampleRifts.RiftTier.Minor);
        Assert.NotEqual(0, SampleRifts.NextTypeNone);
        Assert.Equal(0xFF, SampleRifts.NextTypeNone);
    }

    [Fact]
    public void The_four_flag_bits_are_independent()
    {
        byte[] bits =
        {
            SampleRifts.FlagMinorActive, SampleRifts.FlagMajorActive,
            SampleRifts.FlagMinorNextDerived, SampleRifts.FlagMajorNextDerived,
        };
        Assert.Equal(4, bits.Distinct().Count());
        foreach (byte bit in bits)
        {
            Assert.NotEqual(0, bit);
            Assert.Equal(0, bit & (bit - 1));     // exactly one bit set
        }
    }

    [Fact]
    public void An_unknown_wire_version_is_refused()
    {
        Assert.True(SampleRifts.IsKnownVersion(SampleRifts.ProtocolVersion));
        Assert.False(SampleRifts.IsKnownVersion(0));
        Assert.False(SampleRifts.IsKnownVersion(2));
    }

    // ---- the snapshot builder ---------------------------------------------------------------

    private static SampleRifts.RiftSchedule BuildIdleWithSchedulerOn(SampleRifts.RiftTier nextTier, long nextSpawn) =>
        SampleRifts.Build(
            Now,
            SampleRifts.TierReading.LevelOnly(57),
            SampleRifts.TierReading.LevelOnly(80),
            hasScheduling: true,
            nextSpawn,
            nextTier,
            minorDurationSeconds: 1200, majorDurationSeconds: 1800,
            minorGapSeconds: 3000, majorGapSeconds: 3600);

    [Fact]
    public void A_running_tier_carries_its_end_tick_and_its_active_bit()
    {
        var minor = new SampleRifts.TierReading(true, Now - Seconds(300), Now + Seconds(900), 57);
        var s = SampleRifts.Build(
            Now, minor, SampleRifts.TierReading.LevelOnly(80),
            hasScheduling: true, Now + Seconds(4140), SampleRifts.RiftTier.Major,
            1200, 1800, 3000, 3600);

        Assert.Equal(Now + Seconds(900), s.MinorEndTicks);
        Assert.NotEqual(0, s.Flags & SampleRifts.FlagMinorActive);
        // A running tier reports its remaining time, never a prediction of its own next start.
        Assert.Equal(0, s.MinorNextTicks);
        Assert.Equal(0, s.Flags & SampleRifts.FlagMinorNextDerived);
    }

    [Fact]
    public void The_tier_the_scheduler_names_carries_the_scheduler_tick_verbatim_and_is_not_derived()
    {
        long nextSpawn = Now + Seconds(4140);
        var s = BuildIdleWithSchedulerOn(SampleRifts.RiftTier.Minor, nextSpawn);

        Assert.Equal(nextSpawn, s.MinorNextTicks);
        Assert.Equal(0, s.Flags & SampleRifts.FlagMinorNextDerived);
        Assert.Equal((byte)SampleRifts.RiftTier.Minor, s.NextType);
    }

    [Fact]
    // Research §2.6: the other tier is nextSpawn + duration(nextTier) + gap(thisTier), and it is
    // an estimate, so it travels with its bit set.
    public void The_other_tier_is_estimated_from_duration_plus_gap_and_says_so()
    {
        long nextSpawn = Now + Seconds(4140);
        var s = BuildIdleWithSchedulerOn(SampleRifts.RiftTier.Minor, nextSpawn);

        // The scheduler's next is Minor (duration 1200 s); Major's own gap is 3600 s.
        Assert.Equal(nextSpawn + Seconds(1200 + 3600), s.MajorNextTicks);
        Assert.NotEqual(0, s.Flags & SampleRifts.FlagMajorNextDerived);
    }

    [Fact]
    // A duration or gap accessor that gave nothing means no estimate at all. The alternative -
    // a number computed from a zero - is exactly the fabrication this feature exists not to do.
    public void An_unusable_duration_or_gap_produces_no_estimate_rather_than_a_number()
    {
        var s = SampleRifts.Build(
            Now,
            SampleRifts.TierReading.LevelOnly(57),
            SampleRifts.TierReading.LevelOnly(80),
            hasScheduling: true, Now + Seconds(600), SampleRifts.RiftTier.Minor,
            minorDurationSeconds: 1200, majorDurationSeconds: 0,
            minorGapSeconds: 0, majorGapSeconds: 0);

        Assert.Equal(0, s.MajorNextTicks);
        Assert.Equal(0, s.Flags & SampleRifts.FlagMajorNextDerived);
    }

    [Fact]
    public void No_scheduling_data_means_no_next_start_on_either_tier()
    {
        var s = SampleRifts.Build(
            Now,
            SampleRifts.TierReading.None,
            SampleRifts.TierReading.None,
            hasScheduling: false, 0, SampleRifts.RiftTier.Major,
            1200, 1800, 3000, 3600);

        Assert.Equal(0, s.MinorNextTicks);
        Assert.Equal(0, s.MajorNextTicks);
        Assert.Equal(SampleRifts.NextTypeNone, s.NextType);
        Assert.Equal(0, s.Flags);
    }

    [Fact]
    // A zero NextEventSpawnTicks is "no schedule", not "the epoch".
    public void A_zero_next_spawn_tick_is_treated_as_no_schedule()
    {
        var s = BuildIdleWithSchedulerOn(SampleRifts.RiftTier.Minor, 0);
        Assert.Equal(0, s.MinorNextTicks);
        Assert.Equal(0, s.MajorNextTicks);
        Assert.Equal(SampleRifts.NextTypeNone, s.NextType);
    }

    [Fact]
    // An event entity exists but has not started yet: it is not running, so no Active bit and no
    // end tick. StartTimeTicks <= now < EndTimeTicks is the whole test (research §2.6).
    public void An_event_that_has_not_started_is_not_active()
    {
        var minor = new SampleRifts.TierReading(true, Now + Seconds(60), Now + Seconds(1260), 57);
        Assert.False(SampleRifts.IsRunning(minor, Now));

        var s = SampleRifts.Build(
            Now, minor, SampleRifts.TierReading.LevelOnly(80),
            hasScheduling: false, 0, SampleRifts.RiftTier.Major, 0, 0, 0, 0);
        Assert.Equal(0, s.MinorEndTicks);
        Assert.Equal(0, s.Flags & SampleRifts.FlagMinorActive);
    }

    [Fact]
    public void An_event_that_has_already_ended_is_not_active()
    {
        var minor = new SampleRifts.TierReading(true, Now - Seconds(1260), Now - Seconds(60), 57);
        Assert.False(SampleRifts.IsRunning(minor, Now));
    }

    [Fact]
    // Two entities of the same tier around a transition: prefer the running one, else the later
    // end.
    public void PickBetter_prefers_a_running_event_then_the_later_end()
    {
        var running = new SampleRifts.TierReading(true, Now - Seconds(10), Now + Seconds(100), 57);
        var future = new SampleRifts.TierReading(true, Now + Seconds(600), Now + Seconds(1800), 57);
        Assert.Equal(running.EndTicks, SampleRifts.PickBetter(running, future, Now).EndTicks);
        Assert.Equal(running.EndTicks, SampleRifts.PickBetter(future, running, Now).EndTicks);

        var earlier = new SampleRifts.TierReading(true, Now + Seconds(60), Now + Seconds(600), 57);
        Assert.Equal(future.EndTicks, SampleRifts.PickBetter(earlier, future, Now).EndTicks);

        Assert.Equal(future.EndTicks, SampleRifts.PickBetter(SampleRifts.TierReading.None, future, Now).EndTicks);
        Assert.False(SampleRifts.PickBetter(SampleRifts.TierReading.None, SampleRifts.TierReading.None, Now).HasEvent);
    }

    [Fact]
    public void A_negative_level_never_reaches_the_wire()
    {
        var s = SampleRifts.Build(
            Now,
            new SampleRifts.TierReading(false, 0, 0, -3),
            SampleRifts.TierReading.LevelOnly(80),
            hasScheduling: false, 0, SampleRifts.RiftTier.Major, 0, 0, 0, 0);
        Assert.Equal(0, s.MinorLevel);
    }

    // ---- the clock --------------------------------------------------------------------------

    [Fact]
    // The whole point of shipping absolute ticks plus the server's clock sample: a server whose
    // wall clock is 90 s ahead must produce the same countdown on screen as one in sync.
    public void A_server_ninety_seconds_ahead_shows_the_same_countdown_as_one_in_sync()
    {
        long clientNowAtReceive = Now;
        long skewAhead = Seconds(90);

        // In sync: the server stamps our clock and the target is 600 s out on its clock.
        var inSync = new SampleRifts.RiftSchedule { UtcTicks = Now, MinorNextTicks = Now + Seconds(600) };
        long skew0 = SampleRifts.SkewTicks(inSync.UtcTicks, clientNowAtReceive);

        // Ahead: everything on the server's clock is shifted by the same 90 s, target included.
        var ahead = new SampleRifts.RiftSchedule
        {
            UtcTicks = Now + skewAhead,
            MinorNextTicks = Now + skewAhead + Seconds(600),
        };
        long skew90 = SampleRifts.SkewTicks(ahead.UtcTicks, clientNowAtReceive);

        Assert.Equal(0, skew0);
        Assert.Equal(skewAhead, skew90);
        Assert.Equal(
            SampleRifts.SecondsUntil(inSync.MinorNextTicks, clientNowAtReceive, skew0),
            SampleRifts.SecondsUntil(ahead.MinorNextTicks, clientNowAtReceive, skew90),
            3);
    }

    [Fact]
    public void The_countdown_runs_down_locally_between_packets()
    {
        long skew = 0;
        long target = Now + Seconds(600);
        Assert.Equal(600, SampleRifts.SecondsUntil(target, Now, skew), 3);
        Assert.Equal(540, SampleRifts.SecondsUntil(target, Now + Seconds(60), skew), 3);
        Assert.Equal(-30, SampleRifts.SecondsUntil(target, Now + Seconds(630), skew), 3);
    }

    [Fact]
    public void A_packet_older_than_three_heartbeats_is_stale()
    {
        Assert.False(SampleRifts.IsStale(Now, Now + Seconds(SampleRifts.StaleAfterSeconds - 1)));
        Assert.True(SampleRifts.IsStale(Now, Now + Seconds(SampleRifts.StaleAfterSeconds)));
        Assert.True(SampleRifts.IsStale(Now, Now + Seconds(SampleRifts.StaleAfterSeconds + 600)));
    }

    // ---- the formatter ----------------------------------------------------------------------

    [Theory]
    [InlineData(0, "0s")]
    [InlineData(1, "1s")]
    [InlineData(59, "59s")]
    [InlineData(60, "1m 0s")]
    [InlineData(1189, "19m 49s")]
    [InlineData(3599, "59m 59s")]
    [InlineData(3600, "1h 0m")]
    [InlineData(4140, "1h 9m")]
    [InlineData(86400, "24h 0m")]
    public void The_countdown_ladder_has_the_familiar_shape(double seconds, string expected)
    {
        Assert.Equal(expected, SampleRifts.FormatDuration(seconds));
    }

    [Fact]
    public void A_negative_countdown_clamps_to_zero()
    {
        Assert.Equal("0s", SampleRifts.FormatDuration(-1));
        Assert.Equal("0s", SampleRifts.FormatDuration(-100000));
        Assert.Equal("0s", SampleRifts.FormatDuration(double.NaN));
    }

    [Fact]
    public void The_two_row_shapes_from_the_brief_render_exactly()
    {
        Assert.Equal(
            "T1 (57+) next in 1h 9m",
            SampleRifts.Row(SampleRifts.RiftTier.Minor, 57, active: false, 0, hasNext: true, 4140, derived: false));
        Assert.Equal(
            "T2 (80+) remaining 19m 49s",
            SampleRifts.Row(SampleRifts.RiftTier.Major, 80, active: true, 1189, hasNext: false, 0, derived: false));
    }

    [Fact]
    // The rule this feature exists to demonstrate: a derivation never renders as a schedule.
    public void A_derived_next_start_never_renders_like_a_scheduled_one()
    {
        string scheduled = SampleRifts.Row(SampleRifts.RiftTier.Minor, 57, false, 0, true, 4140, derived: false);
        string derived = SampleRifts.Row(SampleRifts.RiftTier.Minor, 57, false, 0, true, 4140, derived: true);
        Assert.NotEqual(scheduled, derived);
        Assert.EndsWith(SampleRifts.DerivedMarker, derived);
        Assert.DoesNotContain(SampleRifts.DerivedMarker, scheduled);
    }

    [Fact]
    public void A_tier_with_nothing_known_reads_not_scheduled_and_carries_no_number()
    {
        string row = SampleRifts.Row(SampleRifts.RiftTier.Minor, 57, false, 0, hasNext: false, 0, false);
        Assert.Equal("T1 (57+) " + SampleRifts.NotScheduled, row);
        Assert.DoesNotContain("next in", row);
        Assert.DoesNotContain("remaining", row);
    }

    [Fact]
    public void An_unknown_level_omits_the_suffix_rather_than_printing_zero()
    {
        Assert.Equal("T1 - not scheduled", SampleRifts.Row(SampleRifts.RiftTier.Minor, 0, false, 0, false, 0, false));
        Assert.DoesNotContain("(0+)", SampleRifts.Row(SampleRifts.RiftTier.Major, 0, true, 60, false, 0, false));
    }

    [Fact]
    public void The_tier_names_are_the_familiar_labels()
    {
        Assert.Equal("T1", SampleRifts.TierName(SampleRifts.RiftTier.Minor));
        Assert.Equal("T2", SampleRifts.TierName(SampleRifts.RiftTier.Major));
        Assert.Equal("Primal", SampleRifts.TierName(SampleRifts.RiftTier.Primal));
    }

    // ---- R60 finding 9: the rows are localized ------------------------------------------------

    [Fact]
    // The rows sit inside a card the game has already localized, so the templates are keys in this
    // plugin's localization/en.json rather than literals. The formatter reads them through
    // whatever resolver the generation installed; on the client that is the game's string table.
    public void The_row_templates_come_from_the_localization_keys()
    {
        var table = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [SampleRifts.NextKey] = "{tier}{level} in {time}",
            [SampleRifts.RemainingKey] = "{tier}{level} noch {time}",
            [SampleRifts.NotScheduledKey] = "{tier}{level} nicht geplant",
            [SampleRifts.WaitingKey] = "{tier} wartet",
        };
        SampleRifts.TemplateResolver = k => table.TryGetValue(k, out var v) ? v : null;

        Assert.Equal(
            "T1 (57+) in 1h 9m",
            SampleRifts.Row(SampleRifts.RiftTier.Minor, 57, false, 0, hasNext: true, 4140, derived: false));
        Assert.Equal(
            "T2 (80+) noch 19m 49s",
            SampleRifts.Row(SampleRifts.RiftTier.Major, 80, active: true, 1189, hasNext: false, 0, false));
        Assert.Equal(
            "T1 nicht geplant",
            SampleRifts.Row(SampleRifts.RiftTier.Minor, 0, false, 0, false, 0, false));
        Assert.Equal("T2 wartet", SampleRifts.WaitingRow(SampleRifts.RiftTier.Major));

        // The estimate marker is this file's, not a translator's: it is appended outside the
        // template, so a translated row still cannot pass an estimate off as a schedule.
        Assert.EndsWith(
            SampleRifts.DerivedMarker,
            SampleRifts.Row(SampleRifts.RiftTier.Minor, 57, false, 0, true, 4140, derived: true));
    }

    [Fact]
    // A key the file does not carry, a resolver that answers empty, a resolver that throws, and
    // the server - where Register records the string but there is no game string table at all -
    // all render the exact English the rest of this suite pins.
    public void A_missing_key_falls_back_to_the_English_row()
    {
        string english = SampleRifts.Row(SampleRifts.RiftTier.Minor, 57, false, 0, true, 4140, false);
        Assert.Equal("T1 (57+) next in 1h 9m", english);

        SampleRifts.TemplateResolver = _ => null;
        Assert.Equal(english, SampleRifts.Row(SampleRifts.RiftTier.Minor, 57, false, 0, true, 4140, false));

        SampleRifts.TemplateResolver = _ => "";
        Assert.Equal(english, SampleRifts.Row(SampleRifts.RiftTier.Minor, 57, false, 0, true, 4140, false));

        SampleRifts.TemplateResolver = _ => throw new InvalidOperationException("broken translation");
        Assert.Equal(english, SampleRifts.Row(SampleRifts.RiftTier.Minor, 57, false, 0, true, 4140, false));

        // One key present, the rest missing: only that row moves.
        SampleRifts.TemplateResolver = k => k == SampleRifts.RemainingKey ? "{tier}{level} noch {time}" : null;
        Assert.Equal(english, SampleRifts.Row(SampleRifts.RiftTier.Minor, 57, false, 0, true, 4140, false));
        Assert.Equal(
            "T2 (80+) noch 19m 49s",
            SampleRifts.Row(SampleRifts.RiftTier.Major, 80, true, 1189, false, 0, false));
        Assert.Equal("T1 " + SampleRifts.NoData, SampleRifts.WaitingRow(SampleRifts.RiftTier.Minor));
    }

    [Fact]
    // The file is the English translation, so it has to say exactly what the fallback says -
    // otherwise a client (which reads the file) and a server (which cannot) print different rows.
    public void The_sample_en_json_carries_the_four_row_keys_with_the_English_text()
    {
        string json = SourcePin.ReadFile("samples", "Progenitor.Sample", "localization", "en.json");
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(SampleRifts.NextEnglish, root.GetProperty(SampleRifts.NextKey).GetString());
        Assert.Equal(SampleRifts.RemainingEnglish, root.GetProperty(SampleRifts.RemainingKey).GetString());
        Assert.Equal(SampleRifts.NotScheduledEnglish, root.GetProperty(SampleRifts.NotScheduledKey).GetString());
        Assert.Equal(SampleRifts.WaitingEnglish, root.GetProperty(SampleRifts.WaitingKey).GetString());
    }

    [Fact]
    public void Rows_renders_T1_then_T2_from_a_packet()
    {
        var s = SampleRifts.Build(
            Now,
            SampleRifts.TierReading.LevelOnly(57),
            new SampleRifts.TierReading(true, Now - Seconds(11), Now + Seconds(1189), 80),
            hasScheduling: true, Now + Seconds(4140), SampleRifts.RiftTier.Minor,
            1200, 1800, 3000, 3600);

        var rows = SampleRifts.Rows(s, Now, 0);
        Assert.Equal(2, rows.Count);
        Assert.Equal("T1 (57+) next in 1h 9m", rows[0]);
        Assert.Equal("T2 (80+) remaining 19m 49s", rows[1]);
    }

    // ---- change detection and cadence -------------------------------------------------------

    [Fact]
    public void An_identical_snapshot_sends_nothing()
    {
        var a = BuildIdleWithSchedulerOn(SampleRifts.RiftTier.Minor, Now + Seconds(4140));
        var b = a;
        Assert.False(SampleRifts.Differs(a, b));
        Assert.Equal(SampleRifts.SendReason.None, SampleRifts.Decide(everSent: true, changed: false, 5));
    }

    [Fact]
    // UtcTicks moves on every evaluation. If it counted as a change the heartbeat would be
    // meaningless and the server would broadcast twice a second.
    public void A_moved_clock_sample_alone_is_not_a_change()
    {
        var a = BuildIdleWithSchedulerOn(SampleRifts.RiftTier.Minor, Now + Seconds(4140));
        var b = a;
        b.UtcTicks = a.UtcTicks + Seconds(30);
        Assert.False(SampleRifts.Differs(a, b));
    }

    [Fact]
    public void A_one_tick_change_is_a_change()
    {
        var a = BuildIdleWithSchedulerOn(SampleRifts.RiftTier.Minor, Now + Seconds(4140));
        var b = a;
        b.MinorNextTicks += 1;
        Assert.True(SampleRifts.Differs(a, b));
        Assert.Equal(SampleRifts.SendReason.Change, SampleRifts.Decide(true, true, SampleRifts.MinSendIntervalSeconds));
    }

    [Theory]
    [InlineData("MinorEndTicks")]
    [InlineData("MajorEndTicks")]
    [InlineData("MajorNextTicks")]
    [InlineData("MinorLevel")]
    [InlineData("MajorLevel")]
    [InlineData("NextType")]
    [InlineData("Flags")]
    public void Every_meaningful_field_counts_as_a_change(string field)
    {
        var a = BuildIdleWithSchedulerOn(SampleRifts.RiftTier.Minor, Now + Seconds(4140));
        var b = a;
        switch (field)
        {
            case "MinorEndTicks": b.MinorEndTicks = Now + 1; break;
            case "MajorEndTicks": b.MajorEndTicks = Now + 1; break;
            case "MajorNextTicks": b.MajorNextTicks += 1; break;
            case "MinorLevel": b.MinorLevel += 1; break;
            case "MajorLevel": b.MajorLevel += 1; break;
            case "NextType": b.NextType = SampleRifts.NextTypeNone; break;
            case "Flags": b.Flags ^= SampleRifts.FlagMinorActive; break;
        }

        Assert.True(SampleRifts.Differs(a, b));
    }

    [Fact]
    public void The_first_evaluation_always_sends()
    {
        Assert.Equal(SampleRifts.SendReason.First, SampleRifts.Decide(everSent: false, changed: false, 0));
    }

    [Fact]
    public void The_heartbeat_fires_with_no_change_at_all()
    {
        Assert.Equal(SampleRifts.SendReason.None,
            SampleRifts.Decide(true, false, SampleRifts.HeartbeatSeconds - 0.1));
        Assert.Equal(SampleRifts.SendReason.Heartbeat,
            SampleRifts.Decide(true, false, SampleRifts.HeartbeatSeconds));
    }

    [Fact]
    // A schedule that flaps must not become a packet per evaluation.
    public void A_change_inside_the_floor_waits()
    {
        Assert.Equal(SampleRifts.SendReason.None,
            SampleRifts.Decide(true, true, SampleRifts.MinSendIntervalSeconds - 0.1));
        Assert.Equal(SampleRifts.SendReason.Change,
            SampleRifts.Decide(true, true, SampleRifts.MinSendIntervalSeconds));
    }

    [Fact]
    public void DecideSend_walks_the_generation_history()
    {
        var first = BuildIdleWithSchedulerOn(SampleRifts.RiftTier.Minor, Now + Seconds(4140));
        Assert.Equal(SampleRifts.SendReason.First, SampleRifts.DecideSend(first, Now));

        SampleRifts.NoteSent(first, Now, 2);
        Assert.True(SampleRifts.EverSent);
        Assert.Equal(2, SampleRifts.LastRecipients);
        Assert.Equal(SampleRifts.SendReason.None, SampleRifts.DecideSend(first, Now + Seconds(5)));
        Assert.Equal(SampleRifts.SendReason.Heartbeat, SampleRifts.DecideSend(first, Now + Seconds(60)));

        var moved = BuildIdleWithSchedulerOn(SampleRifts.RiftTier.Major, Now + Seconds(9000));
        Assert.Equal(SampleRifts.SendReason.Change, SampleRifts.DecideSend(moved, Now + Seconds(5)));
    }

    // ---- the handshake edge ------------------------------------------------------------------

    [Fact]
    // The join send rides the handshake-ok edge, so it fires once per user per transition.
    public void The_handshake_edge_fires_once_per_user()
    {
        Assert.True(SampleRifts.NoteHandshakeOk(3, isConnected: true, answerIsOk: true));
        Assert.False(SampleRifts.NoteHandshakeOk(3, true, true));
        Assert.Equal(1, SampleRifts.TrackedUsers);
    }

    [Fact]
    public void A_user_who_has_not_answered_gets_nothing_and_stays_armed()
    {
        Assert.False(SampleRifts.NoteHandshakeOk(3, isConnected: true, answerIsOk: false));
        Assert.Equal(0, SampleRifts.TrackedUsers);
        Assert.True(SampleRifts.NoteHandshakeOk(3, true, true));
    }

    [Fact]
    // User.Index is a recycled connection slot: a row that outlives its owner would suppress the
    // join send for the next player to land in that slot, silently.
    public void A_departed_users_row_is_dropped_so_the_next_player_in_that_slot_is_served()
    {
        Assert.True(SampleRifts.NoteHandshakeOk(3, true, true));
        Assert.True(SampleRifts.NoteHandshakeOk(4, true, true));
        Assert.Equal(2, SampleRifts.TrackedUsers);

        SampleRifts.RetainUsers(new[] { 4 });
        Assert.Equal(1, SampleRifts.TrackedUsers);
        Assert.True(SampleRifts.NoteHandshakeOk(3, true, true));    // the recycled slot is served again
        Assert.False(SampleRifts.NoteHandshakeOk(4, true, true));   // the one that stayed is not re-sent

        SampleRifts.RetainUsers(Array.Empty<int>());
        Assert.Equal(0, SampleRifts.TrackedUsers);
    }

    [Fact]
    public void A_disconnect_rearms_the_edge()
    {
        Assert.True(SampleRifts.NoteHandshakeOk(3, true, true));
        Assert.False(SampleRifts.NoteHandshakeOk(3, isConnected: false, answerIsOk: true));
        Assert.Equal(0, SampleRifts.TrackedUsers);
        Assert.True(SampleRifts.NoteHandshakeOk(3, true, true));
    }

    // ---- the client holder -------------------------------------------------------------------

    [Fact]
    public void Before_a_packet_arrives_the_rows_say_so_rather_than_showing_a_number()
    {
        var rows = SampleRifts.CurrentRows(Now);
        Assert.Equal(2, rows.Count);
        Assert.Equal("T1 " + SampleRifts.NoData, rows[0]);
        Assert.Equal("T2 " + SampleRifts.NoData, rows[1]);
        Assert.False(SampleRifts.HasSchedule);
    }

    [Fact]
    public void A_held_packet_renders_and_a_stale_one_stops_rendering()
    {
        var s = SampleRifts.Build(
            Now,
            SampleRifts.TierReading.LevelOnly(57),
            new SampleRifts.TierReading(true, Now - Seconds(11), Now + Seconds(1189), 80),
            hasScheduling: true, Now + Seconds(4140), SampleRifts.RiftTier.Minor,
            1200, 1800, 3000, 3600);

        long skew = SampleRifts.HoldSchedule(s, Now);
        Assert.Equal(0, skew);
        Assert.True(SampleRifts.HasSchedule);
        Assert.Equal("T2 (80+) remaining 19m 49s", SampleRifts.CurrentRows(Now)[1]);

        // Three missed heartbeats later, the numbers stop being claimed as current.
        var stale = SampleRifts.CurrentRows(Now + Seconds(SampleRifts.StaleAfterSeconds));
        Assert.Equal("T1 " + SampleRifts.NoData, stale[0]);
        Assert.Equal("T2 " + SampleRifts.NoData, stale[1]);
    }

    [Fact]
    // A reconnect to a different server must not leave the previous one's rifts on screen.
    public void Forgetting_the_schedule_returns_the_waiting_rows()
    {
        SampleRifts.HoldSchedule(BuildIdleWithSchedulerOn(SampleRifts.RiftTier.Minor, Now + Seconds(600)), Now);
        Assert.True(SampleRifts.ForgetSchedule());
        Assert.False(SampleRifts.ForgetSchedule());
        Assert.False(SampleRifts.HasSchedule);
        Assert.Equal("T1 " + SampleRifts.NoData, SampleRifts.CurrentRows(Now)[0]);
    }

    // ---- the row sink -------------------------------------------------------------------------

    private sealed class CapturingSink : SampleRifts.IRiftRowSink
    {
        internal readonly List<IReadOnlyList<string>> Pushes = new();
        internal int Disposals;

        public string Name => "capturing";

        public void SetRows(IReadOnlyList<string> rows) => Pushes.Add(rows);

        public void Dispose() => Disposals++;
    }

    [Fact]
    public void Rows_go_to_the_installed_sink_and_nowhere_when_there_is_none()
    {
        Assert.False(SampleRifts.PushRows(new[] { "a", "b" }));

        var sink = new CapturingSink();
        SampleRifts.InstallSink(sink);
        Assert.True(SampleRifts.PushRows(new[] { "a", "b" }));
        Assert.Single(sink.Pushes);
        Assert.Equal(new[] { "a", "b" }, sink.Pushes[0]);
    }

    [Fact]
    public void Installing_a_sink_disposes_the_previous_one_and_unload_disposes_the_last()
    {
        var first = new CapturingSink();
        var second = new CapturingSink();
        SampleRifts.InstallSink(first);
        SampleRifts.InstallSink(second);
        Assert.Equal(1, first.Disposals);
        Assert.Same(second, SampleRifts.Sink);

        Assert.True(SampleRifts.RemoveSink());
        Assert.Equal(1, second.Disposals);
        Assert.Null(SampleRifts.Sink);
        Assert.False(SampleRifts.RemoveSink());
    }

    [Fact]
    // The fallback exists so the client half is provable from the log while FUI36 is not in Core;
    // it must not write a line per refresh when the string did not move.
    public void The_log_fallback_writes_only_when_the_rows_change()
    {
        var written = new List<string>();
        SampleLog.Sink = written.Add;
        try
        {
            var sink = new SampleRifts.LogRowSink();
            sink.SetRows(new[] { "T1 (57+) next in 1h 9m", "T2 (80+) remaining 19m 49s" });
            sink.SetRows(new[] { "T1 (57+) next in 1h 9m", "T2 (80+) remaining 19m 49s" });
            Assert.Single(written);
            Assert.Contains("Sample rifts: rows ", written[0]);

            sink.SetRows(new[] { "T1 (57+) next in 1h 8m", "T2 (80+) remaining 19m 49s" });
            Assert.Equal(2, written.Count);
        }
        finally
        {
            SampleLog.Clear();
        }
    }

    // ---- the lines ----------------------------------------------------------------------------

    [Fact]
    public void The_send_line_names_the_reason_the_audience_and_the_schedule()
    {
        var s = BuildIdleWithSchedulerOn(SampleRifts.RiftTier.Minor, Now + Seconds(4140));
        string line = SampleRifts.SentLine(SampleRifts.SendReason.Heartbeat, "all", 2, s);
        Assert.Contains("sent SmRf to all", line);
        Assert.Contains("recipients=2", line);
        Assert.Contains("reason=heartbeat", line);
        Assert.Contains("nextType=T1", line);
        Assert.Contains("(scheduled)", line);
        Assert.Contains("(derived)", line);
    }

    [Fact]
    public void The_apply_line_reports_the_skew_with_a_sign()
    {
        var s = BuildIdleWithSchedulerOn(SampleRifts.RiftTier.Minor, Now + Seconds(4140));
        Assert.Contains("skew=+1.5s", SampleRifts.AppliedLine(s, Seconds(1.5)));
        Assert.Contains("skew=-2s", SampleRifts.AppliedLine(s, -Seconds(2)));
    }

    [Fact]
    public void The_next_type_byte_renders_as_a_tier_name_or_none()
    {
        Assert.Equal("none", SampleRifts.NextTypeName(SampleRifts.NextTypeNone));
        Assert.Equal("T2", SampleRifts.NextTypeName((byte)SampleRifts.RiftTier.Major));
        Assert.Equal("T1", SampleRifts.NextTypeName((byte)SampleRifts.RiftTier.Minor));
        Assert.Equal("Primal", SampleRifts.NextTypeName((byte)SampleRifts.RiftTier.Primal));
        Assert.Contains("unknown", SampleRifts.NextTypeName(9));
    }

    // ---- .rifts ---------------------------------------------------------------------------------

    [Theory]
    // `expectSend` rather than the Kind itself: an internal enum cannot be a public test method's
    // parameter type, and the Sample's types are internal.
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("status", false)]
    [InlineData("STATUS", false)]
    [InlineData("send", true)]
    [InlineData(" Send ", true)]
    public void The_command_parses_its_two_forms(string? token, bool expectSend)
    {
        Assert.True(SampleRifts.TryParse(token, out var kind));
        Assert.Equal(expectSend ? SampleRifts.Kind.Send : SampleRifts.Kind.Status, kind);
    }

    [Theory]
    [InlineData("sent")]
    [InlineData("all")]
    [InlineData("on")]
    public void A_typo_replies_usage_rather_than_doing_something(string token)
    {
        Assert.False(SampleRifts.TryParse(token, out _));
        Assert.Contains(".rifts send", SampleRifts.UsageReply);
    }

    [Fact]
    public void With_no_data_the_command_says_so_and_prints_no_rows()
    {
        var lines = SampleRifts.StatusLines(false, default, Now, false, 0, 0);
        Assert.Single(lines);
        Assert.DoesNotContain("T1 (", lines[0]);
    }

    [Fact]
    // The server half must be provable with no client: the reply carries the same two rows the
    // client renders, the raw ticks, and the scheduled-versus-estimated sentence.
    public void The_command_prints_the_same_two_rows_the_client_renders()
    {
        var s = SampleRifts.Build(
            Now,
            SampleRifts.TierReading.LevelOnly(57),
            new SampleRifts.TierReading(true, Now - Seconds(11), Now + Seconds(1189), 80),
            hasScheduling: true, Now + Seconds(4140), SampleRifts.RiftTier.Minor,
            1200, 1800, 3000, 3600);

        var lines = SampleRifts.StatusLines(true, s, Now, everSent: true, 12, 2);
        Assert.Equal("T1 (57+) next in 1h 9m", lines[0]);
        Assert.Equal("T2 (80+) remaining 19m 49s", lines[1]);
        Assert.Contains("raw: T1 end=0 next=" + (Now + Seconds(4140)), lines[2]);
        Assert.Contains("serverUtc=" + Now, lines[2]);
        Assert.Contains("Scheduled: T1", lines[3]);
        Assert.Contains("last broadcast 12s ago to 2 user(s)", lines[4]);
    }

    [Fact]
    public void The_command_never_calls_an_estimate_a_schedule()
    {
        var s = BuildIdleWithSchedulerOn(SampleRifts.RiftTier.Minor, Now + Seconds(4140));
        string provenance = SampleRifts.Provenance(s);
        Assert.Contains("Scheduled: T1.", provenance);
        Assert.Contains("Estimated", provenance);
        Assert.Contains("T2", provenance.Substring(provenance.IndexOf("Estimated", StringComparison.Ordinal)));
        Assert.Contains("not a schedule", provenance);
    }

    [Fact]
    public void A_tier_with_no_data_is_named_as_having_none()
    {
        var s = SampleRifts.Build(
            Now, SampleRifts.TierReading.None, SampleRifts.TierReading.None,
            hasScheduling: false, 0, SampleRifts.RiftTier.Major, 0, 0, 0, 0);
        string provenance = SampleRifts.Provenance(s);
        Assert.Contains("No data: T1, T2.", provenance);
        Assert.DoesNotContain("Scheduled:", provenance);
        Assert.DoesNotContain("Estimated", provenance);
    }

    [Fact]
    public void Before_the_first_broadcast_the_command_says_never_rather_than_zero_seconds_ago()
    {
        var s = BuildIdleWithSchedulerOn(SampleRifts.RiftTier.Minor, Now + Seconds(4140));
        var lines = SampleRifts.StatusLines(true, s, Now, everSent: false, 0, 0);
        Assert.Contains("never broadcast", lines[4]);
    }

    // ---- teardown ---------------------------------------------------------------------------

    [Fact]
    public void Detach_forgets_the_generation_and_the_sink()
    {
        var sink = new CapturingSink();
        SampleRifts.InstallSink(sink);
        SampleRifts.NoteSent(BuildIdleWithSchedulerOn(SampleRifts.RiftTier.Minor, Now + Seconds(600)), Now, 3);
        SampleRifts.HoldSchedule(BuildIdleWithSchedulerOn(SampleRifts.RiftTier.Minor, Now + Seconds(600)), Now);
        Assert.True(SampleRifts.NoteHandshakeOk(1, true, true));

        SampleRifts.Detach();

        Assert.Equal(1, sink.Disposals);
        Assert.Null(SampleRifts.Sink);
        Assert.False(SampleRifts.EverSent);
        Assert.Equal(0, SampleRifts.LastRecipients);
        Assert.False(SampleRifts.HasSchedule);
        Assert.Equal(0, SampleRifts.TrackedUsers);
        Assert.False(SampleRifts.TryLatest(out _));
    }

    [Fact]
    public void NoteLatest_and_TryLatest_carry_the_last_snapshot()
    {
        Assert.False(SampleRifts.TryLatest(out _));
        var s = BuildIdleWithSchedulerOn(SampleRifts.RiftTier.Major, Now + Seconds(900));
        SampleRifts.NoteLatest(s);
        Assert.True(SampleRifts.TryLatest(out var back));
        Assert.Equal(s.MajorNextTicks, back.MajorNextTicks);
        Assert.Equal(s.NextType, back.NextType);
    }

    [Fact]
    public void SecondsSinceLastSend_is_zero_before_anything_went_out()
    {
        Assert.Equal(0, SampleRifts.SecondsSinceLastSend(Now));
        SampleRifts.NoteSent(BuildIdleWithSchedulerOn(SampleRifts.RiftTier.Minor, Now + Seconds(600)), Now, 1);
        Assert.Equal(30, SampleRifts.SecondsSinceLastSend(Now + Seconds(30)), 3);
    }
}
