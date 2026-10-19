using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Progenitor.Networking;
using Progenitor.Plugins;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// JOINFLOW1: the join screens' game-free half — the reset countdown and the message tokens, the page
/// model's rows and actions, the session's navigation and trust prompt, "don't show again", the trust
/// acknowledgement, and the download queue. The catalog-schema half (old and new envelopes, the signed
/// section) is <see cref="JoinSectionSchemaTests"/>.
/// </summary>
public sealed class JoinFlowTests
{
    private static readonly DateTime Now = new(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);

    // A US-style zone with real rules, built here so the tests do not depend on the machine's zone database:
    // UTC-5, daylight UTC-4 from the second Sunday of March 02:00 to the first Sunday of November 02:00.
    private static readonly TimeZoneInfo Eastern = TimeZoneInfo.CreateCustomTimeZone(
        "Test Eastern", TimeSpan.FromHours(-5), "Test Eastern", "Test Eastern Standard", "Test Eastern Daylight",
        new[]
        {
            TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
                new DateTime(2000, 1, 1), DateTime.MaxValue.Date, TimeSpan.FromHours(1),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 2, DayOfWeek.Sunday),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 11, 1, DayOfWeek.Sunday)),
        });

    private static readonly TimeZoneInfo PlusTwo = TimeZoneInfo.CreateCustomTimeZone("Test +2", TimeSpan.FromHours(2), "Test +2", "Test +2");

    private static ResetSchedule Schedule(DateTime nextUtc, ResetPrecision precision, ResetRepeat repeat = ResetRepeat.None, int every = 1, string passed = "") =>
        new(ResetCountdown.FormatUtc(nextUtc), repeat, every, precision, passed);

    // ------------------------------------------------------------ tokens and precision

    /// <summary>
    /// RESETWIPE1: the precision is automatic, whatever the section's stored one: days while a day or more away, then
    /// hours, then minutes and seconds in the last hour.
    /// </summary>
    [Theory]
    [InlineData(3, 4, 17, 5, "3d")]
    [InlineData(1, 0, 0, 0, "1d")]
    [InlineData(0, 23, 59, 59, "23h")]
    [InlineData(0, 1, 0, 0, "1h")]
    [InlineData(0, 0, 59, 59, "59m 59s")]
    [InlineData(0, 0, 17, 5, "17m 05s")]
    [InlineData(0, 0, 0, 42, "42s")]
    public void The_countdown_picks_its_precision_from_the_time_left(int d, int h, int m, int s, string expected)
    {
        var next = Now + new TimeSpan(d, h, m, s);
        foreach (var stored in new[] { ResetPrecision.Day, ResetPrecision.Second })
        {
            string text = ResetCountdown.Render("Reset in {reset}!", Schedule(next, stored), Now, TimeZoneInfo.Utc, TimeZoneInfo.Utc, out bool live);
            Assert.Equal("Reset in " + expected + "!", text);
            Assert.True(live);
        }

        Assert.Equal(expected, ResetCountdown.FormatCountdown(new TimeSpan(d, h, m, s)));
    }

    [Fact]
    public void Auto_precision_switches_at_a_day_and_at_an_hour()
    {
        Assert.Equal(ResetPrecision.Day, ResetCountdown.AutoPrecision(TimeSpan.FromDays(1)));
        Assert.Equal(ResetPrecision.Hour, ResetCountdown.AutoPrecision(TimeSpan.FromDays(1) - TimeSpan.FromSeconds(1)));
        Assert.Equal(ResetPrecision.Hour, ResetCountdown.AutoPrecision(TimeSpan.FromHours(1)));
        Assert.Equal(ResetPrecision.Second, ResetCountdown.AutoPrecision(TimeSpan.FromMinutes(59)));
        Assert.Equal("<1s", ResetCountdown.FormatCountdown(TimeSpan.Zero));
    }

    [Fact]
    public void Leading_zero_units_are_dropped_and_a_remainder_below_the_precision_reads_less_than_one()
    {
        Assert.Equal("4h 0m", ResetCountdown.FormatCountdown(new TimeSpan(4, 0, 30), ResetPrecision.Minute));
        Assert.Equal("17m 05s", ResetCountdown.FormatCountdown(new TimeSpan(0, 17, 5), ResetPrecision.Second));
        Assert.Equal("<1d", ResetCountdown.FormatCountdown(TimeSpan.FromHours(23), ResetPrecision.Day));
        Assert.Equal("<1m", ResetCountdown.FormatCountdown(TimeSpan.FromSeconds(30), ResetPrecision.Minute));
        Assert.Equal("<1s", ResetCountdown.FormatCountdown(TimeSpan.Zero, ResetPrecision.Second));
        // Floors, never rounds up: 59.9 s at minute precision is still under a minute.
        Assert.Equal("<1m", ResetCountdown.FormatCountdown(TimeSpan.FromSeconds(59.9), ResetPrecision.Minute));
    }

    [Fact]
    public void Relative_reads_days_then_hours_then_today()
    {
        Assert.Equal("in 3 days", ResetCountdown.FormatRelative(TimeSpan.FromHours(80)));
        Assert.Equal("in 1 day", ResetCountdown.FormatRelative(TimeSpan.FromHours(30)));
        Assert.Equal("in 5 hours", ResetCountdown.FormatRelative(TimeSpan.FromHours(5.5)));
        Assert.Equal("in 1 hour", ResetCountdown.FormatRelative(TimeSpan.FromMinutes(61)));
        Assert.Equal("today", ResetCountdown.FormatRelative(TimeSpan.FromMinutes(59)));
    }

    [Fact]
    public void The_date_token_is_in_the_players_zone_with_its_offset()
    {
        var next = new DateTime(2026, 10, 10, 18, 0, 0, DateTimeKind.Utc);
        var schedule = Schedule(next, ResetPrecision.Minute);
        Assert.Equal("Sat, Oct 10, 8:00 PM (UTC+02:00)",
            ResetCountdown.Render("{reset:date}", schedule, Now, Eastern, PlusTwo));
        Assert.Equal("Sat, Oct 10, 2:00 PM (UTC-04:00)",
            ResetCountdown.Render("{reset:date}", schedule, Now, Eastern, Eastern));
        // RESETWIPE1: always the date and the time to the minute, whatever the stored precision.
        Assert.Equal("Sat, Oct 10, 8:00 PM (UTC+02:00)", ResetCountdown.Render("{reset:date}", Schedule(next, ResetPrecision.Day), Now, Eastern, PlusTwo));
        Assert.Equal("Sat, Oct 10, 8:00 PM (UTC+02:00)",
            ResetCountdown.Render("{reset:date}", Schedule(next, ResetPrecision.Second), Now, Eastern, PlusTwo));
        // FormatDate itself still honors a precision (the date picker's readback uses it).
        Assert.Equal("Sat, Oct 10", ResetCountdown.FormatDate(next, PlusTwo, ResetPrecision.Day));
    }

    [Fact]
    public void Unknown_tokens_render_literally_and_known_ones_without_a_reset_read_not_scheduled()
    {
        var schedule = Schedule(Now.AddHours(5), ResetPrecision.Hour);
        Assert.Equal("{foo} {reset:bogus} {RESET} 5h",
            ResetCountdown.Render("{foo} {reset:bogus} {RESET} {reset}", schedule, Now, TimeZoneInfo.Utc, TimeZoneInfo.Utc));
        Assert.Equal("Next: not scheduled (not scheduled)",
            ResetCountdown.Render("Next: {reset} ({reset:relative})", null, Now, TimeZoneInfo.Utc, TimeZoneInfo.Utc, out bool live));
        Assert.False(live);
        Assert.Equal("{", ResetCountdown.Render("{", schedule, Now, TimeZoneInfo.Utc, TimeZoneInfo.Utc));
    }

    [Fact]
    public void A_passed_one_time_reset_reads_the_passed_text_and_stops_ticking()
    {
        var passed = Schedule(Now.AddMinutes(-1), ResetPrecision.Second);
        Assert.Equal("Reset now, see you now", ResetCountdown.Render("Reset {reset}, see you {reset:relative}", passed, Now,
            TimeZoneInfo.Utc, TimeZoneInfo.Utc, out bool live));
        Assert.False(live);
        var custom = Schedule(Now.AddDays(-2), ResetPrecision.Second, passed: "Reset time has passed");
        Assert.Equal("Reset time has passed", ResetCountdown.Render("{reset}", custom, Now, TimeZoneInfo.Utc, TimeZoneInfo.Utc));
        // The date still names when it was.
        Assert.StartsWith("Fri, Oct 2", ResetCountdown.Render("{reset:date}", custom, Now, TimeZoneInfo.Utc, TimeZoneInfo.Utc));
    }

    // ------------------------------------------------------------ repeat roll-forward

    [Fact]
    public void A_daily_repeat_rolls_to_the_first_occurrence_after_now()
    {
        var first = new DateTime(2026, 10, 1, 6, 0, 0, DateTimeKind.Utc);
        var schedule = Schedule(first, ResetPrecision.Minute, ResetRepeat.Days, 1);
        Assert.True(ResetCountdown.TryNext(schedule, Now, TimeZoneInfo.Utc, out var next, out bool passed));
        Assert.False(passed);
        Assert.Equal(new DateTime(2026, 10, 5, 6, 0, 0, DateTimeKind.Utc), next);
        Assert.Equal("18h", ResetCountdown.Render("{reset}", schedule, Now, TimeZoneInfo.Utc, TimeZoneInfo.Utc));
    }

    [Fact]
    public void A_weekly_repeat_every_two_weeks_rolls_by_fourteen_days_and_the_exact_moment_counts_as_passed()
    {
        var first = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        var schedule = Schedule(first, ResetPrecision.Day, ResetRepeat.Weeks, 2);
        ResetCountdown.TryNext(schedule, Now, TimeZoneInfo.Utc, out var next, out _);
        Assert.Equal(new DateTime(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc), next);
        // At exactly an occurrence the countdown moves to the next one, never "0".
        ResetCountdown.TryNext(schedule, new DateTime(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc), TimeZoneInfo.Utc, out var atBoundary, out _);
        Assert.Equal(new DateTime(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc), atBoundary);
        // Before the first occurrence nothing rolls.
        ResetCountdown.TryNext(schedule, first.AddHours(-1), TimeZoneInfo.Utc, out var early, out _);
        Assert.Equal(first, early);
    }

    [Fact]
    public void A_repeat_keeps_the_servers_wall_clock_time_across_a_daylight_saving_change()
    {
        // 06:00 Eastern on Saturday 2026-03-07 (standard time, 11:00 UTC); clocks go forward on Sunday 03-08.
        var first = new DateTime(2026, 3, 7, 11, 0, 0, DateTimeKind.Utc);
        var schedule = Schedule(first, ResetPrecision.Minute, ResetRepeat.Days, 1);
        var now = new DateTime(2026, 3, 9, 12, 0, 0, DateTimeKind.Utc);
        ResetCountdown.TryNext(schedule, now, Eastern, out var next, out _);
        // Tuesday 03-10 06:00 daylight time is 10:00 UTC, not 11:00.
        Assert.Equal(new DateTime(2026, 3, 10, 10, 0, 0, DateTimeKind.Utc), next);
        Assert.Equal(6, TimeZoneInfo.ConvertTimeFromUtc(next, Eastern).Hour);
    }

    [Fact]
    public void Local_to_utc_moves_a_gap_time_forward_and_takes_the_first_of_an_ambiguous_time()
    {
        // 2026-03-08 02:30 does not exist in Eastern: it becomes 03:30 daylight (07:30 UTC).
        var gap = ResetCountdown.LocalToUtc(new DateTime(2026, 3, 8, 2, 30, 0), Eastern, out string gapNote);
        Assert.Equal(new DateTime(2026, 3, 8, 7, 30, 0, DateTimeKind.Utc), gap);
        Assert.Contains("does not exist", gapNote, StringComparison.Ordinal);
        // 2026-11-01 01:30 happens twice: the first is daylight time (UTC-4), 05:30 UTC.
        var twice = ResetCountdown.LocalToUtc(new DateTime(2026, 11, 1, 1, 30, 0), Eastern, out string twiceNote);
        Assert.Equal(new DateTime(2026, 11, 1, 5, 30, 0, DateTimeKind.Utc), twice);
        Assert.Contains("happens twice", twiceNote, StringComparison.Ordinal);
        var plain = ResetCountdown.LocalToUtc(new DateTime(2026, 7, 1, 12, 0, 0), Eastern, out string none);
        Assert.Equal(new DateTime(2026, 7, 1, 16, 0, 0, DateTimeKind.Utc), plain);
        Assert.Equal("", none);
    }

    [Fact]
    public void The_stored_form_is_iso_utc_and_parses_offsets()
    {
        Assert.Equal("2026-10-10T18:00:00Z", ResetCountdown.FormatUtc(new DateTime(2026, 10, 10, 18, 0, 0, DateTimeKind.Utc)));
        Assert.True(ResetCountdown.TryParseUtc("2026-10-10T20:00:00+02:00", out var utc));
        Assert.Equal(new DateTime(2026, 10, 10, 18, 0, 0, DateTimeKind.Utc), utc);
        Assert.False(ResetCountdown.TryParseUtc("next tuesday", out _));
        Assert.False(ResetCountdown.TryParseUtc("", out _));
        Assert.Equal("UTC-04:00", ResetCountdown.OffsetLabel(TimeSpan.FromHours(-4)));
        Assert.Equal("UTC+05:30", ResetCountdown.OffsetLabel(new TimeSpan(5, 30, 0)));
        Assert.Equal("UTC", ResetCountdown.OffsetLabel(TimeSpan.Zero));
    }

    [Fact]
    public void The_server_settings_build_a_section_with_fallbacks_and_caps()
    {
        var section = JoinServerSettings.BuildSection(new string('x', 2500), "2026-10-10T18:00:00Z", "weekly?", 0, "bogus", "",
            new[] { new RecommendedMod("a.mod", "Owner-A", "1.0", "Nice") }, PlusTwo, Now);
        Assert.Equal(2000, section.Message.Length);
        Assert.NotNull(section.Reset);
        Assert.Equal(ResetRepeat.None, section.Reset!.Repeat);
        Assert.Equal(ResetPrecision.Minute, section.Reset.Precision);
        Assert.Equal(1, section.Reset.Every);
        Assert.Equal(ResetCountdown.DefaultPassedText, section.Reset.PassedText);
        Assert.Equal(120, section.Zone.OffsetMinutes);
        Assert.Single(section.Recommended);
        Assert.Null(JoinServerSettings.BuildSection("hi", "", "Days", 1, "Hour", "", null, PlusTwo, Now).Reset);
        Assert.True(JoinServerSettings.BuildSection("", "", "", 1, "", "", null, PlusTwo, Now).IsEmpty);
    }

    // ------------------------------------------------------------ RESETWIPE1: the game's wipe schedule

    [Fact]
    public void The_wipe_schedule_is_the_games_reset_date_at_midnight_server_time()
    {
        // Start Sep 1 + 30 days = Oct 1 (a Thursday); Wipe on Saturday moves it to Oct 3. 00:00 at UTC+2 is 22:00 UTC.
        var wipe = GameWipeSchedule.From(new DateTime(2026, 9, 1), 30, "Saturday");
        Assert.True(wipe.On);
        Assert.Equal(new DateTime(2026, 10, 3), wipe.NextDate);
        Assert.Equal("2026-10-02T22:00:00Z", wipe.NextUtc(PlusTwo));
        Assert.Equal("Sat, Oct 3, 2026 · From the server's wipe schedule", wipe.RowText());

        var any = GameWipeSchedule.From(new DateTime(2026, 9, 1), 30, "Any");
        Assert.Equal(new DateTime(2026, 10, 1), any.NextDate);
        // Daylight time: 00:00 Eastern on Oct 1 is 04:00 UTC.
        Assert.Equal("2026-10-01T04:00:00Z", any.NextUtc(Eastern));

        var unknownStart = GameWipeSchedule.From(null, 7, "Any");
        Assert.True(unknownStart.On);
        Assert.Null(unknownStart.NextDate);
        Assert.Equal("", unknownStart.NextUtc(PlusTwo));
        Assert.StartsWith("Every 7 days, start date unknown", unknownStart.RowText());

        Assert.False(GameWipeSchedule.From(new DateTime(2026, 9, 1), 0, "Any").On);
        Assert.Null(GameWipeSchedule.From(new DateTime(2026, 9, 1), 0, "Any").NextDate);
    }

    [Fact]
    public void The_wipe_schedule_is_read_from_the_server_rules_state()
    {
        var state = new ServerRulesState { NextReset = "2026-10-03" };
        state.Rows.Add(new ServerRuleRow { Key = ServerRules.ResetDaysInterval, Next = "30" });
        state.Rows.Add(new ServerRuleRow { Key = ServerRules.DayOfReset, Next = "Saturday" });
        var wipe = GameWipeSchedule.FromRules(state)!;
        Assert.Equal(30, wipe.IntervalDays);
        Assert.Equal("Saturday", wipe.DayOfReset);
        Assert.Equal(new DateTime(2026, 10, 3), wipe.NextDate);
        Assert.Null(GameWipeSchedule.FromRules(null));
        Assert.False(GameWipeSchedule.FromRules(new ServerRulesState())!.On);
    }

    [Fact]
    public void A_game_wipe_schedule_replaces_the_stored_reset_and_its_repeat()
    {
        var wipe = GameWipeSchedule.From(new DateTime(2026, 9, 1), 30, "Saturday");
        var section = JoinServerSettings.BuildSection("{reset}", "2026-10-10T18:00:00Z", "Days", 3, "Second", "Gone!", null, PlusTwo, Now,
            wipe: wipe);
        Assert.Equal("2026-10-02T22:00:00Z", section.Reset!.NextUtc);
        Assert.Equal(ResetRepeat.None, section.Reset.Repeat);
        Assert.Equal(1, section.Reset.Every);
        // The passed text is fixed; the stored precision still travels for earlier clients.
        Assert.Equal(ResetCountdown.DefaultPassedText, section.Reset.PassedText);
        Assert.Equal(ResetPrecision.Second, section.Reset.Precision);

        // After the date, before the next start wipes the world: "now", no more ticking.
        var after = new DateTime(2026, 10, 3, 9, 0, 0, DateTimeKind.Utc);
        Assert.Equal("now", ResetCountdown.Render("{reset}", section.Reset, after, PlusTwo, PlusTwo, out bool live));
        Assert.False(live);

        // Wipes on but the world's start date unknown: no reset (the tokens read "not scheduled").
        var unknown = JoinServerSettings.BuildSection("{reset}", "2026-10-10T18:00:00Z", "None", 1, "", "", null, PlusTwo, Now,
            wipe: GameWipeSchedule.From(null, 7, "Any"));
        Assert.Null(unknown.Reset);
    }

    [Fact]
    public void The_when_players_join_rows_drop_the_countdown_settings_and_follow_the_wipe_schedule()
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "Progenitor.sln"))) dir = dir.Parent;
        string source = System.IO.File.ReadAllText(System.IO.Path.Combine(dir!.FullName, "src", "Progenitor.Core", "Networking", "JoinServerSettings.cs"));
        int start = source.IndexOf("private static void AddWhenPlayersJoinRows", StringComparison.Ordinal);
        int end = source.IndexOf("private static void AddNoProgenitorRow", StringComparison.Ordinal);
        Assert.True(start > 0 && end > start);
        string rows = source.Substring(start, end - start);
        foreach (string gone in new[] { "label: \"Repeat\"", "label: \"Repeat every\"", "label: \"Countdown precision\"", "label: \"After the reset\"",
                     "page.Radio(_repeat", "page.Number(_every", "page.Radio(_precision", "page.Text(_passed" })
            Assert.DoesNotContain(gone, rows);
        // The date picker only without a game schedule; the computed wipe, read-only and muted, with one.
        Assert.Contains("help: NextResetHelp, showIf: () => !(GameWipe()?.On ?? false));", rows);
        Assert.Contains("page.Info(NextResetLabel, () => GameWipe()?.RowText() ?? \"\"", rows);
        Assert.Contains("showIf: () => GameWipe()?.On ?? false)", rows);
        Assert.Contains(".StatusTone(() => CellTone.Muted);", rows);
        // The section is built with the schedule this side knows.
        Assert.Contains("_showMessage?.Value, _showMods?.Value, GameWipe());", source);
    }

    [Fact]
    public void Without_game_wipes_the_stored_date_is_a_one_off_and_a_saved_repeat_still_rolls_it()
    {
        var off = GameWipeSchedule.From(new DateTime(2026, 9, 1), 0, "Any");
        var oneOff = JoinServerSettings.BuildSection("{reset}", "2026-10-01T06:00:00Z", "None", 1, "", "Custom", null, TimeZoneInfo.Utc, Now, wipe: off);
        Assert.Equal("now", ResetCountdown.Render("{reset}", oneOff.Reset, Now, TimeZoneInfo.Utc, TimeZoneInfo.Utc));

        // The cfg-only fallback: a ResetRepeat / ResetEvery saved before the rows left the menus.
        var rolled = JoinServerSettings.BuildSection("{reset}", "2026-10-01T06:00:00Z", "Days", 1, "", "", null, TimeZoneInfo.Utc, Now, wipe: off);
        Assert.Equal(ResetRepeat.Days, rolled.Reset!.Repeat);
        Assert.Equal("18h", ResetCountdown.Render("{reset}", rolled.Reset, Now, TimeZoneInfo.Utc, TimeZoneInfo.Utc));
        // No probe at all (null) behaves the same.
        Assert.Equal(ResetRepeat.Days,
            JoinServerSettings.BuildSection("{reset}", "2026-10-01T06:00:00Z", "Days", 1, "", "", null, TimeZoneInfo.Utc, Now).Reset!.Repeat);
    }

    // ------------------------------------------------------------ when each page shows

    [Fact]
    public void The_message_hash_is_the_text_as_written_so_a_ticking_countdown_is_no_change()
    {
        string a = JoinShowPolicy.MessageHash("Welcome! Reset in {reset}.");
        Assert.Equal(16, a.Length);
        Assert.Equal(a, JoinShowPolicy.MessageHash("Welcome! Reset in {reset}."));
        Assert.Equal(JoinShowPolicy.MessageHash("Line one\nLine two"), JoinShowPolicy.MessageHash("Line one\r\nLine two"));
        Assert.NotEqual(a, JoinShowPolicy.MessageHash("Welcome! Reset in {reset}!"));
        Assert.Equal("a1b2c3d4e5f67890@1.2.3.4:9876", JoinSeenStore.ServerKey("A1B2C3D4E5F67890", "1.2.3.4:9876"));
        Assert.Equal("1.2.3.4:9876", JoinSeenStore.ServerKey("", "1.2.3.4:9876"));
        Assert.Equal("a1b2c3d4e5f67890", JoinSeenStore.ServerKey("a1b2c3d4e5f67890", ""));
    }

    [Fact]
    public void The_mods_hash_is_the_servers_lists_in_any_order_and_ignores_what_this_client_has()
    {
        var rec = new[] { new RecommendedMod("r1", "O-R1", "1.0", "Maps") };
        var one = Model(new[] { Must(A, "1"), Must(B, "2") }, new[] { Must(A, "1") }, Catalog(Row(A, "1", shares: true)),
            section: new JoinSection("", null, rec, null));
        var reordered = Model(new[] { Must(B, "2"), Must(A, "1") }, new[] { Must(B, "2"), Must(A, "0.9") }, Catalog(Row(A, "1", shares: true)),
            section: new JoinSection("", null, rec, null));
        Assert.Equal(JoinShowPolicy.ModsHash(JoinShowPolicy.ModsSnapshot(one)), JoinShowPolicy.ModsHash(JoinShowPolicy.ModsSnapshot(reordered)));

        var snapshot = JoinShowPolicy.ModsSnapshot(one);
        Assert.Equal("1", snapshot["r:team.alpha"]);
        // 2026-10-05: the server's shares are gone, so a row's hash and size are no longer part of the lists.
        Assert.False(snapshot.ContainsKey("s:team.alpha"));
        Assert.StartsWith("1.0|", snapshot["c:r1"], StringComparison.Ordinal);

        var reason = Model(new[] { Must(A, "1"), Must(B, "2") }, new[] { Must(A, "1") }, Catalog(Row(A, "1", shares: true)),
            section: new JoinSection("", null, new[] { new RecommendedMod("r1", "O-R1", "1.0", "Better maps") }, null));
        Assert.NotEqual(JoinShowPolicy.ModsHash(snapshot), JoinShowPolicy.ModsHash(JoinShowPolicy.ModsSnapshot(reason)));
        var unshared = Model(new[] { Must(A, "1"), Must(B, "2") }, new[] { Must(A, "1") }, Catalog(Row(A, "1")),
            section: new JoinSection("", null, rec, null));
        Assert.Equal(JoinShowPolicy.ModsHash(snapshot), JoinShowPolicy.ModsHash(JoinShowPolicy.ModsSnapshot(unshared)));
    }

    [Fact]
    public void The_reset_period_is_the_last_reset_that_passed_in_the_servers_calendar()
    {
        var first = new DateTime(2026, 10, 1, 6, 0, 0, DateTimeKind.Utc);
        Assert.Null(ResetCountdown.LastPassed(Schedule(first, ResetPrecision.Minute), first.AddMinutes(-1), TimeZoneInfo.Utc));
        Assert.Equal(first, ResetCountdown.LastPassed(Schedule(first, ResetPrecision.Minute), Now, TimeZoneInfo.Utc));
        Assert.Equal(new DateTime(2026, 10, 4, 6, 0, 0, DateTimeKind.Utc),
            ResetCountdown.LastPassed(Schedule(first, ResetPrecision.Minute, ResetRepeat.Days, 1), Now, TimeZoneInfo.Utc));
        Assert.Equal(new DateTime(2026, 10, 3, 6, 0, 0, DateTimeKind.Utc),
            ResetCountdown.LastPassed(Schedule(first, ResetPrecision.Minute, ResetRepeat.Days, 2), Now, TimeZoneInfo.Utc));
        // Exactly at an occurrence, that occurrence is the period's start.
        Assert.Equal(new DateTime(2026, 10, 4, 6, 0, 0, DateTimeKind.Utc),
            ResetCountdown.LastPassed(Schedule(first, ResetPrecision.Minute, ResetRepeat.Days, 1), new DateTime(2026, 10, 4, 6, 0, 0, DateTimeKind.Utc), TimeZoneInfo.Utc));
        // Across the spring-forward change the period still starts at 06:00 server time.
        var march = new DateTime(2026, 3, 7, 11, 0, 0, DateTimeKind.Utc);
        var last = ResetCountdown.LastPassed(Schedule(march, ResetPrecision.Minute, ResetRepeat.Days, 1), new DateTime(2026, 3, 9, 12, 0, 0, DateTimeKind.Utc), Eastern);
        Assert.Equal(new DateTime(2026, 3, 9, 10, 0, 0, DateTimeKind.Utc), last);
        Assert.Null(ResetCountdown.LastPassed(null, Now, TimeZoneInfo.Utc));
    }

    private static JoinSection Freq(string message, MessageFrequency show, ResetSchedule? reset = null) =>
        new(message, reset, null, new JoinServerZone("UTC", 0), showMessage: show);

    private static JoinSeenRow Seen(string message, string resetSeen = "", string dismissed = "") => new()
    {
        Key = "fp@host",
        MessageHash = JoinShowPolicy.MessageHash(message),
        ResetSeen = resetSeen,
        Dismissed = dismissed.Length == 0 ? "" : JoinShowPolicy.MessageHash(dismissed),
    };

    [Fact]
    public void Message_always_shows_on_every_join_and_offers_no_dismissal()
    {
        var section = Freq("Hi", MessageFrequency.Always);
        var d = JoinShowPolicy.Message(section, Seen("Hi"), Now);
        Assert.True(d.Show);
        Assert.Equal(JoinShowPolicy.ReasonAlways, d.Reason);
        var s = new JoinFlowSession(JoinMode.InWorld, Model(Array.Empty<Handshake.ModRecord>(), Array.Empty<Handshake.ModRecord>(), section: section), new FakeEffects());
        Assert.False(s.CanDismiss);
        Assert.False(JoinShowPolicy.Message(Freq("", MessageFrequency.Always), null, Now).Show);
    }

    [Fact]
    public void Message_when_changed_shows_on_first_join_on_a_change_and_after_each_reset()
    {
        var reset = Schedule(new DateTime(2026, 10, 1, 6, 0, 0, DateTimeKind.Utc), ResetPrecision.Hour, ResetRepeat.Days, 1);
        var section = Freq("Hi", MessageFrequency.WhenChanged, reset);
        Assert.Equal(JoinShowPolicy.ReasonFirstJoin, JoinShowPolicy.Message(section, null, Now).Reason);
        Assert.Equal(JoinShowPolicy.ReasonMessageChanged, JoinShowPolicy.Message(section, Seen("Hello"), Now).Reason);
        // Shown in today's period (since 06:00): skipped. Shown in yesterday's: a reset passed since.
        Assert.False(JoinShowPolicy.Message(section, Seen("Hi", "2026-10-04T06:00:00Z"), Now).Show);
        var after = JoinShowPolicy.Message(section, Seen("Hi", "2026-10-03T06:00:00Z"), Now);
        Assert.True(after.Show);
        Assert.Equal(JoinShowPolicy.ReasonResetPassed, after.Reason);
        Assert.Equal("2026-10-04T06:00:00Z", JoinShowPolicy.CurrentPeriod(section, Now));
        // A dismissal of this text skips the reset re-shows; a new text shows again.
        Assert.Equal(JoinShowPolicy.ReasonDismissed, JoinShowPolicy.Message(section, Seen("Hi", "2026-10-03T06:00:00Z", dismissed: "Hi"), Now).Reason);
        Assert.True(JoinShowPolicy.Message(Freq("New text", MessageFrequency.WhenChanged, reset), Seen("Hi", dismissed: "Hi"), Now).Show);
        // No reset set: only changes.
        var plain = Freq("Hi", MessageFrequency.WhenChanged);
        Assert.Equal(JoinShowPolicy.ReasonUnchanged, JoinShowPolicy.Message(plain, Seen("Hi"), Now).Reason);
    }

    [Fact]
    public void A_reset_counts_only_when_it_passed_after_the_message_was_shown_so_moving_the_anchor_is_no_reset()
    {
        var day = new DateTime(2026, 10, 4, 0, 0, 0, DateTimeKind.Utc);
        var eight = Freq("Hi", MessageFrequency.WhenChanged, Schedule(day.AddDays(-3).AddHours(8), ResetPrecision.Hour, ResetRepeat.Days, 1));
        var ten = Freq("Hi", MessageFrequency.WhenChanged, Schedule(day.AddDays(-3).AddHours(10), ResetPrecision.Hour, ResetRepeat.Days, 1));
        var shownAtNoon = Seen("Hi", "2026-10-04T12:00:00Z");
        Assert.False(JoinShowPolicy.Message(eight, shownAtNoon, day.AddHours(12.5)).Show);
        // The admin moved the daily reset from 08:00 to 10:00: 10:00 passed before the message was shown at noon.
        Assert.False(JoinShowPolicy.Message(ten, shownAtNoon, day.AddHours(12.5)).Show);
        Assert.Equal(JoinShowPolicy.ReasonResetPassed, JoinShowPolicy.Message(ten, shownAtNoon, day.AddHours(34)).Reason);
        // A row without a show time (shown before a reset was set) falls back to its last write, not "passed".
        var noReset = new JoinSeenRow { Key = "k", MessageHash = JoinShowPolicy.MessageHash("Hi"), Seen = "2026-10-04T12:00:00Z" };
        Assert.False(JoinShowPolicy.Message(ten, noReset, day.AddHours(12.5)).Show);
        Assert.False(JoinShowPolicy.Message(ten, new JoinSeenRow { Key = "k", MessageHash = JoinShowPolicy.MessageHash("Hi") }, day.AddHours(12.5)).Show);
    }

    [Fact]
    public void A_refresh_keeps_the_decisions_made_at_open_and_records_the_page_on_show_again()
    {
        var fx = new FakeEffects();
        var model = Model(new[] { Must(A, "1") }, new[] { Must(A, "1") }, section: Freq("", MessageFrequency.WhenChanged));
        var s = new JoinFlowSession(JoinMode.InWorld, model, fx,
            new JoinShowDecision(false, JoinShowPolicy.ReasonNoMessage), new JoinShowDecision(true, JoinShowPolicy.ReasonModsChanged),
            new Dictionary<string, string> { [A] = "changed" });
        Assert.Empty(fx.Shown);
        s.Opened();
        Assert.Equal(new[] { "mods" }, fx.Shown);
        // A catalog resend re-decides against the state the page just wrote: the open page keeps its own.
        s.Refresh(model, preferMessage: true, new JoinShowDecision(false, JoinShowPolicy.ReasonNoMessage),
            new JoinShowDecision(false, JoinShowPolicy.ReasonUnchanged), new Dictionary<string, string>());
        Assert.Equal(JoinShowPolicy.ReasonModsChanged, s.ModsDecision.Reason);
        Assert.Equal("changed", s.Changes[A]);
        Assert.Equal(new[] { "mods", "mods" }, fx.Shown);
        // A message that arrives later still decides for itself.
        s.Refresh(Model(new[] { Must(A, "1") }, new[] { Must(A, "1") }, section: Freq("Hi", MessageFrequency.WhenChanged)), preferMessage: true,
            new JoinShowDecision(true, JoinShowPolicy.ReasonFirstJoin), null, null);
        Assert.Equal(JoinShowPolicy.ReasonFirstJoin, s.MessageDecision.Reason);
        Assert.Equal(JoinPage.Message, s.Page);
    }

    /// <summary>User, 2026-10-06: no Leave in the world or in a preview; before the world (refused, install window) it stays.</summary>
    [Fact]
    public void Leave_is_offered_only_before_the_player_is_in_the_world()
    {
        var ready = Model(new[] { Must(A, "1") }, new[] { Must(A, "1") }, section: Message());
        var inWorld = new JoinFlowSession(JoinMode.InWorld, ready, new FakeEffects(),
            new JoinShowDecision(true, JoinShowPolicy.ReasonFirstJoin), new JoinShowDecision(false, JoinShowPolicy.ReasonUnchanged), null);
        Assert.False(JoinFlowLayout.OffersLeave(inWorld));
        Assert.DoesNotContain(JoinFlowLayout.MessageButtons(inWorld), b => b.Key == JoinFlowLayout.ButtonLeave);

        var preview = new JoinFlowSession(JoinMode.InWorld, ready, new FakeEffects(),
            new JoinShowDecision(true, JoinShowPolicy.ReasonFirstJoin), new JoinShowDecision(false, JoinShowPolicy.ReasonUnchanged), null) { IsPreview = true };
        Assert.DoesNotContain(JoinFlowLayout.MessageButtons(preview), b => b.Key == JoinFlowLayout.ButtonLeave);

        var refused = new JoinFlowSession(JoinMode.Refused, Model(new[] { Must(A, "2") }, new[] { Must(A, "1") }, section: Message()), new FakeEffects());
        Assert.True(JoinFlowLayout.OffersLeave(refused));
    }

    /// <summary>
    /// Stage 2 live run: everything ready and nothing changed skips the mods page and Continue closes, so the message
    /// page offers a quiet View mods (between Leave and Continue); when Continue leads to the mods page it is not drawn.
    /// </summary>
    [Fact]
    public void View_mods_is_offered_on_the_message_page_only_when_the_mods_page_is_not_due()
    {
        var fx = new FakeEffects();
        var ready = Model(new[] { Must(A, "1") }, new[] { Must(A, "1") }, section: Message());
        var s = new JoinFlowSession(JoinMode.InWorld, ready, fx,
            new JoinShowDecision(true, JoinShowPolicy.ReasonFirstJoin), new JoinShowDecision(false, JoinShowPolicy.ReasonUnchanged), null);
        Assert.Equal(JoinPage.Message, s.Page);
        Assert.True(s.CanViewMods);
        Assert.Equal(new[] { JoinFlowLayout.ButtonViewMods, JoinFlowLayout.ButtonContinue },
            JoinFlowLayout.MessageButtons(s).Select(b => b.Key).ToArray());
        Assert.Equal("mods", s.ViewMods());
        Assert.Equal(JoinPage.Mods, s.Page);
        Assert.False(s.CanViewMods);
        Assert.Equal("message", s.BackToMessage());

        // The mods page is due (lists changed): Continue goes there, so View mods is not drawn.
        var due = new JoinFlowSession(JoinMode.InWorld, ready, new FakeEffects(),
            new JoinShowDecision(true, JoinShowPolicy.ReasonFirstJoin), new JoinShowDecision(true, JoinShowPolicy.ReasonModsChanged), null);
        Assert.False(due.CanViewMods);
        Assert.DoesNotContain(JoinFlowLayout.MessageButtons(due), b => b.Key == JoinFlowLayout.ButtonViewMods);
        // A required mod needs attention: likewise.
        var missing = new JoinFlowSession(JoinMode.InWorld, Model(new[] { Must(A, "2") }, new[] { Must(A, "1") }, section: Message()), new FakeEffects());
        Assert.False(missing.CanViewMods);
        Assert.Equal("mods", s.ViewMods());
        Assert.Equal("not on the message page", s.ViewMods());
    }

    [Fact]
    public void A_page_not_yet_on_show_is_not_recorded()
    {
        var fx = new FakeEffects();
        var s = new JoinFlowSession(JoinMode.InWorld, Model(new[] { Must(A, "1") }, new[] { Must(A, "1") }, section: Message()), fx);
        s.Refresh(Model(new[] { Must(A, "1") }, new[] { Must(A, "1") }, section: Message()), preferMessage: true);
        Assert.Empty(fx.Shown);
    }

    // ------------------------------------------------------------ live fixes (2026-10-04)

    [Fact]
    public void The_server_name_skips_the_catalog_default_and_never_shows_a_relay_id()
    {
        Assert.Equal("Night Garden", JoinText.PickServerName("Night Garden", "Local Name"));
        Assert.Equal("Progenitor Forms Test", JoinText.PickServerName("V Rising", "Progenitor Forms Test"));
        Assert.Equal("", JoinText.PickServerName("V Rising", ""));
        Assert.Equal("this server", JoinText.ServerLabel("", "Aqf27THwTQgIQMCD3pOBAvJNEAA"));
        // 2026-10-19: the join pages never show an address; the Last server row still tells servers apart by it.
        Assert.Equal("this server", JoinText.ServerLabel("", "127.0.0.1:9976"));
        Assert.Equal("Welcome to this server", JoinText.MessageTitle(JoinText.ServerLabel("", "127.0.0.1:9976")));
        Assert.Equal("127.0.0.1:9976", JoinText.NameOrAddress("", "127.0.0.1:9976"));
        Assert.Equal("this server", JoinText.NameOrAddress("", "Aqf27THwTQgIQMCD3pOBAvJNEAA"));
        Assert.Equal("Night Garden", JoinText.NameOrAddress("Night Garden", "127.0.0.1:9976"));
        Assert.Equal("Welcome to this server", JoinText.MessageTitle(JoinText.ServerLabel(JoinText.PickServerName("V Rising", ""), "Aqf27THwTQgIQMCD3pOBAvJNEAA")));
    }

    [Fact]
    public void In_the_world_rows_use_the_current_versions_and_the_servers_rule_so_a_swap_on_both_sides_is_ready()
    {
        // The join-time tables say the server asked 2026.10.50; both sides now run 2026.10.52.
        var asked = new[] { Must(A, "2026.10.50") };
        var provided = new[] { Must(A, "2026.10.50") };
        JoinSection Now52() => new("", null, null, null, versions: new Dictionary<string, string> { [A] = "2026.10.52" });
        var swapped = Model(asked, provided, section: Now52(), loaded: id => id == A ? "2026.10.52" : null);
        Assert.True(swapped.AllRequiredReady);
        Assert.Equal("2026.10.52", swapped.Required.Single().RequiredVersion);

        // An operator overlay pinning the catalog row's version does not change what the server requires.
        var pinned = Model(new[] { Must(A, "2026.10.52") }, new[] { Must(A, "2026.10.52") }, Catalog(Row(A, "0.1.0")), section: Now52(),
            loaded: id => id == A ? "2026.10.52" : null);
        Assert.True(pinned.AllRequiredReady);

        // Only the client moved, under Exact: needs attention (newer); under None: ready, as the handshake judges.
        var exact = Model(asked, provided, loaded: id => id == A ? "2026.10.52" : null);
        Assert.Equal(JoinRowStatus.Newer, exact.Required.Single().Status);
        var none = Model(new[] { Must(A, "2026.10.50", NetworkVersionRule.None) }, provided, loaded: id => id == A ? "2026.10.52" : null);
        Assert.True(none.AllRequiredReady);

        // A refusal keeps the join's own tables (the narrowed descriptor is not the server's full state).
        var refused = JoinPageModel.Build(new JoinFacts
        {
            Asked = asked, Provided = Array.Empty<Handshake.ModRecord>(), Section = Now52(), Refused = true,
        });
        Assert.Equal("2026.10.50", refused.Required.Single().RequiredVersion);
    }

    [Fact]
    public void The_footer_says_what_is_missing_per_case_and_the_install_button_shows_only_with_something_to_install()
    {
        var missing = Model(new[] { Must(A, "1") }, Array.Empty<Handshake.ModRecord>());
        Assert.Equal("Alpha: this server didn't say where to get it. Ask its admins.", JoinText.CannotFixFooter(missing));
        var ahead = Model(new[] { Must(B, "1") }, new[] { Must(B, "2") }, Catalog(Row(B, "1")), loaded: _ => null);
        Assert.Contains("you have a newer version than the server; its admins must update it.", JoinText.CannotFixFooter(ahead), StringComparison.Ordinal);

        var s = new JoinFlowSession(JoinMode.Refused, missing, new FakeEffects());
        Assert.Equal(new[] { JoinText.CloseButton }, JoinFlowLayout.ModsButtons(s).Select(b => b.Caption).ToArray());
        var ready = new JoinFlowSession(JoinMode.InWorld, Model(new[] { Must(A, "1") }, new[] { Must(A, "1") }), new FakeEffects());
        Assert.Equal(new[] { JoinText.ContinueButton }, JoinFlowLayout.ModsButtons(ready).Select(b => b.Caption).ToArray());
        var installable = new JoinFlowSession(JoinMode.Refused,
            Model(new[] { Must(A, "1") }, Array.Empty<Handshake.ModRecord>(), Catalog(Row(A, "1", "Owner-Alpha"))), new FakeEffects());
        Assert.Equal(new[] { JoinText.CloseButton, JoinText.InstallButton(1) }, JoinFlowLayout.ModsButtons(installable).Select(b => b.Caption).ToArray());
    }

    [Fact]
    public void A_row_that_needs_attention_fits_its_status_version_and_source_on_one_line()
    {
        var model = Model(new[] { Must(A, "2026.10.52") }, Array.Empty<Handshake.ModRecord>(),
            section: new JoinSection("", null, null, null, names: new Dictionary<string, string> { [A] = "Progenitor Alpha" }));
        var row = model.Required.Single();
        Assert.Equal("Progenitor Alpha", row.Name);
        var spec = JoinFlowLayout.Required(row, "", 560f);
        var name = spec.Cells.Single(c => c.Key == JoinFlowLayout.CellName);
        Assert.Equal("Progenitor Alpha\nneeds 2026.10.52 · no download source", name.Text);
        Assert.Equal("Missing", name.NoteLead);
        Assert.True(string.IsNullOrEmpty(name.Glyph));

        // Whatever is drawn fits the row: in a narrow row the source is dropped first, never the version.
        string line = "Missing · " + "needs 2026.10.52 · no download source";
        Assert.Equal(line, MenuPanelLayout.ClipLabel(line, 560f - JoinFlowLayout.ActionWidth - 24f, CellLayout.TwoLineNoteFontSize));
        var narrow = JoinFlowLayout.Required(row, "", JoinFlowLayout.ActionWidth + 200f).Cells.Single(c => c.Key == JoinFlowLayout.CellName);
        Assert.Equal("Progenitor Alpha\nneeds 2026.10.52", narrow.Text);

        // A Thunderstore row, hot-loadable in a window: status, version and the short source.
        var ts = Model(new[] { Must(A, "2026.10.53") }, Array.Empty<Handshake.ModRecord>(),
            Catalog(Row(A, "2026.10.53", "Owner-Alpha", hot: true)), window: true).Required.Single();
        JoinFlowLayout.AttentionLine(ts, "", 560f - JoinFlowLayout.ActionWidth - 24f, out var lead, out var note, out _);
        Assert.Equal("Missing", lead);
        Assert.StartsWith("needs 2026.10.53 · ", note, StringComparison.Ordinal);
        Assert.DoesNotContain("server", note, StringComparison.Ordinal);
        Assert.Equal("you have 1.1, needs 1.2", JoinFlowLayout.CompactVersion(
            Model(new[] { Must(A, "1.2") }, new[] { Must(A, "1.1") }).Required.Single()));

        // A ready row keeps its full version line.
        var ready = JoinFlowLayout.Required(Model(new[] { Must(A, "1") }, new[] { Must(A, "1") }).Required.Single(), "", 560f);
        Assert.Equal("Alpha\nNeeds 1 · You have 1", ready.Cells.Single(c => c.Key == JoinFlowLayout.CellName).Text);
    }

    [Fact]
    public void After_an_install_window_the_in_world_mods_page_is_not_a_change_and_installed_rows_say_so()
    {
        var installedA = new[] { A };
        var onlyA = new Dictionary<string, string> { [A] = "changed" };
        var aAndB = new Dictionary<string, string> { [A] = "changed", [B] = "new" };
        var changedPage = new JoinShowDecision(true, JoinShowPolicy.ReasonModsChanged);
        // The only change is the mod the player just took from the server: no page.
        Assert.Equal(JoinShowPolicy.ReasonSkippedInstalled, JoinShowPolicy.AfterInstallWindow(changedPage, onlyA, installedA).Reason);
        // Another change on the server still shows (live review: a real change must not be hidden).
        Assert.Equal(JoinShowPolicy.ReasonModsChanged, JoinShowPolicy.AfterInstallWindow(changedPage, aAndB, installedA).Reason);
        Assert.Equal(JoinShowPolicy.ReasonModsChanged, JoinShowPolicy.AfterInstallWindow(changedPage, onlyA, Array.Empty<string>()).Reason);
        Assert.Equal(JoinShowPolicy.ReasonSkippedInstalled,
            JoinShowPolicy.AfterInstallWindow(new JoinShowDecision(true, JoinShowPolicy.ReasonFirstJoin), null, installedA).Reason);
        Assert.Equal(JoinShowPolicy.ReasonNeedsAttention,
            JoinShowPolicy.AfterInstallWindow(new JoinShowDecision(true, JoinShowPolicy.ReasonNeedsAttention), onlyA, installedA).Reason);
        Assert.Equal(JoinShowPolicy.ReasonAlways,
            JoinShowPolicy.AfterInstallWindow(new JoinShowDecision(true, JoinShowPolicy.ReasonAlways), onlyA, installedA).Reason);

        var changes = JoinShowPolicy.MarkInstalled(new Dictionary<string, string> { [A] = "changed", [B] = "new" }, new[] { A });
        Assert.Equal(JoinShowPolicy.ChangeInstalled, changes[A]);
        Assert.Equal("new", changes[B]);
        Assert.Equal("Installed", JoinShowPolicy.TagWord(JoinShowPolicy.ChangeInstalled));
        Assert.Equal("Installed 1 mod. You're ready to play.", JoinShowPolicy.InstalledLine(1));

        // With the page shown anyway (Every join), the subtitle confirms the install, not a server change.
        var model = Model(new[] { Must(A, "1") }, new[] { Must(A, "1") });
        var s = new JoinFlowSession(JoinMode.InWorld, model, new FakeEffects(),
            new JoinShowDecision(false, JoinShowPolicy.ReasonNoMessage), new JoinShowDecision(true, JoinShowPolicy.ReasonAlways), changes);
        Assert.Equal("Installed 1 mod. You're ready to play.", JoinFlowLayout.ModsChangedText(s));
        var row = JoinFlowLayout.Required(model.Required.Single(), JoinShowPolicy.ChangeInstalled, 560f);
        Assert.Equal("Installed", row.Cells.Single(c => c.Key == JoinFlowLayout.CellName).NoteLead);
    }

    [Fact]
    public void A_window_catalog_after_the_windows_download_is_the_join_completing_not_a_new_window()
    {
        // Live round 3: the catalog to the re-answered (ok) client arrived while the window was still armed.
        Assert.True(JoinInWorldGate.WindowJoinCompleted(windowArmed: true, screensOpen: false, windowScreensShown: true, () => true));
        // The window's first catalog (screens not yet shown), screens still open, or a mod still missing: a window.
        Assert.False(JoinInWorldGate.WindowJoinCompleted(true, false, false, () => true));
        Assert.False(JoinInWorldGate.WindowJoinCompleted(true, true, true, () => true));
        Assert.False(JoinInWorldGate.WindowJoinCompleted(true, false, true, () => false));
        Assert.False(JoinInWorldGate.WindowJoinCompleted(false, false, true, () => true));
    }

    [Fact]
    public void The_server_fact_names_the_address_or_says_steam_never_a_relay_id()
    {
        Assert.Equal("Night Garden (127.0.0.1:9976)", JoinFlowLayout.ServerFact("Night Garden", "127.0.0.1:9976"));
        Assert.Equal("Progenitor Forms Test (via Steam)", JoinFlowLayout.ServerFact("Progenitor Forms Test", "Aqf27THwTQgIQMCD3pOBAvJNEAA"));
        Assert.Equal("Night Garden", JoinFlowLayout.ServerFact("Night Garden", ""));
    }

    [Fact]
    public void Tokens_read_not_scheduled_while_no_reset_is_set_and_unknown_tokens_stay_literal()
    {
        Assert.Equal("Wipe not scheduled: not scheduled (not scheduled). {unknown}",
            ResetCountdown.Render("Wipe {reset:relative}: {reset} ({reset:date}). {unknown}", null, Now, TimeZoneInfo.Utc, TimeZoneInfo.Utc));
    }

    [Fact]
    public void A_zone_label_names_the_zone_in_force()
    {
        Assert.Equal("UTC-04:00 (Test Eastern Daylight)", ResetCountdown.ZoneLabel(Eastern, new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc)));
        Assert.Equal("UTC-05:00 (Test Eastern Standard)", ResetCountdown.ZoneLabel(Eastern, new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void Only_the_first_catalog_of_an_observed_join_decides_the_in_world_screens()
    {
        Assert.True(JoinInWorldGate.MayDecide(joinObserved: true, alreadyDecided: false, openPending: false));
        Assert.False(JoinInWorldGate.MayDecide(joinObserved: true, alreadyDecided: true, openPending: false));
        Assert.False(JoinInWorldGate.MayDecide(joinObserved: true, alreadyDecided: false, openPending: true));
        // A Core loaded mid-session saw no join attempt: a catalog resend never opens the screens.
        Assert.False(JoinInWorldGate.MayDecide(joinObserved: false, alreadyDecided: false, openPending: false));
    }

    [Fact]
    public void A_first_visit_with_everything_ready_skips_the_mods_page_unless_the_server_recommends_mods()
    {
        var plain = Model(new[] { Must(A, "1") }, new[] { Must(A, "1") }, section: Freq("", MessageFrequency.WhenChanged));
        Assert.Equal(JoinShowPolicy.ReasonSkippedReady, JoinShowPolicy.Mods(plain, null).Reason);
        Assert.False(JoinShowPolicy.Mods(plain, null).Show);
        var recommends = Model(new[] { Must(A, "1") }, new[] { Must(A, "1") },
            section: new JoinSection("", null, new[] { new RecommendedMod("r1", "O-R1", "1", "Maps") }, null));
        Assert.Equal(JoinShowPolicy.ReasonFirstJoin, JoinShowPolicy.Mods(recommends, null).Reason);
    }

    [Fact]
    public void Message_first_join_only_shows_once_and_then_only_on_a_change()
    {
        var reset = Schedule(new DateTime(2026, 10, 1, 6, 0, 0, DateTimeKind.Utc), ResetPrecision.Hour, ResetRepeat.Days, 1);
        var section = Freq("Hi", MessageFrequency.FirstJoin, reset);
        Assert.Equal(JoinShowPolicy.ReasonFirstJoin, JoinShowPolicy.Message(section, null, Now).Reason);
        Assert.False(JoinShowPolicy.Message(section, Seen("Hi", "2026-09-01T06:00:00Z"), Now).Show);
        Assert.Equal(JoinShowPolicy.ReasonMessageChanged, JoinShowPolicy.Message(section, Seen("Old"), Now).Reason);
    }

    [Fact]
    public void Mods_when_needed_shows_for_attention_a_first_visit_or_a_change_and_skips_otherwise()
    {
        var ready = Model(new[] { Must(A, "1") }, new[] { Must(A, "1") }, Catalog(Row(A, "1")), section: Freq("", MessageFrequency.WhenChanged));
        Assert.Equal(JoinShowPolicy.ReasonSkippedReady, JoinShowPolicy.Mods(ready, null).Reason);
        var seen = new JoinSeenRow { Mods = JoinShowPolicy.ModsSnapshot(ready) };
        seen.ModsHash = JoinShowPolicy.ModsHash(seen.Mods);
        var skip = JoinShowPolicy.Mods(ready, seen);
        Assert.False(skip.Show);
        Assert.Equal(JoinShowPolicy.ReasonUnchanged, skip.Reason);

        // Decided 2026-10-06: a changed required version that is already ready leaves nothing to do: no page, the lists recorded.
        var bumped = Model(new[] { Must(A, "2") }, new[] { Must(A, "2") }, Catalog(Row(A, "2")), section: Freq("", MessageFrequency.WhenChanged));
        var bumpedDecision = JoinShowPolicy.Mods(bumped, seen);
        Assert.False(bumpedDecision.Show);
        Assert.Equal(JoinShowPolicy.ReasonSkippedReadyChanged, bumpedDecision.Reason);
        Assert.True(JoinShowPolicy.RecordsListsWhenSkipped(bumpedDecision.Reason));

        var attention = Model(new[] { Must(A, "2") }, new[] { Must(A, "1") }, Catalog(Row(A, "2")));
        Assert.Equal(JoinShowPolicy.ReasonNeedsAttention, JoinShowPolicy.Mods(attention, seen).Reason);

        var always = Model(new[] { Must(A, "1") }, new[] { Must(A, "1") }, Catalog(Row(A, "1")),
            section: new JoinSection("", null, null, null, showMods: ModsPageFrequency.Always));
        Assert.Equal(JoinShowPolicy.ReasonAlways, JoinShowPolicy.Mods(always, seen).Reason);
    }

    [Fact]
    public void The_Joining_page_stays_only_while_something_required_needs_attention()
    {
        // Decided 2026-10-06: "Joined X. Required (0 of 0 ready)" with Continue was shown for nothing.
        Assert.False(JoinShowPolicy.JoinedPageNeeded(null));
        Assert.False(JoinShowPolicy.JoinedPageNeeded(Model(Array.Empty<Handshake.ModRecord>(), Array.Empty<Handshake.ModRecord>())));
        Assert.False(JoinShowPolicy.JoinedPageNeeded(Model(new[] { Must(A, "1") }, new[] { Must(A, "1") })));
        Assert.True(JoinShowPolicy.JoinedPageNeeded(Model(new[] { Must(A, "2") }, new[] { Must(A, "1") }, window: true)));
    }

    [Theory]
    [InlineData("", "Night Garden", "127.0.0.1:9976", "Night Garden")] // known now, not when the page was built
    [InlineData("Night Garden", "", "127.0.0.1:9976", "Night Garden")]
    [InlineData("Old Name", "New Name", "127.0.0.1:9976", "New Name")]
    [InlineData("", "", "127.0.0.1:9976", "this server")] // never the address (2026-10-19)
    [InlineData("", "  ", "steamid-relay", "this server")]
    public void Page_titles_name_the_server_when_any_name_is_known(string modelName, string liveName, string host, string expected)
    {
        Assert.Equal(expected, JoinText.PageLabel(modelName, liveName, host));
        Assert.Equal("Mods for " + expected, JoinText.ModsTitle(JoinText.PageLabel(modelName, liveName, host)));
    }

    [Fact]
    public void Mods_when_needed_opens_for_a_recommended_mod_to_consider_only()
    {
        var recommended = new[] { new RecommendedMod("r1", "O-R1", "1", "Maps") };
        Func<string, string?> haveA = id => id == A ? "1" : null;
        Func<string, string?> haveBoth = id => id == A || id == "r1" ? "1" : null;
        JoinPageModel With(RecommendedMod[] recs, Func<string, string?> loaded) =>
            Model(new[] { Must(A, "1") }, new[] { Must(A, "1") }, Catalog(Row(A, "1")),
                section: new JoinSection("", null, recs, null), loaded: loaded);

        // A first visit: a recommended mod this client lacks opens the page; one it already runs does not.
        Assert.Equal(JoinShowPolicy.ReasonFirstJoin, JoinShowPolicy.Mods(With(recommended, haveA), null).Reason);
        Assert.Equal(JoinShowPolicy.ReasonSkippedReady, JoinShowPolicy.Mods(With(recommended, haveBoth), null).Reason);

        // A change: a newly recommended mod not installed opens it; installed, or an unchanged one, does not.
        var before = With(Array.Empty<RecommendedMod>(), haveA);
        var seen = new JoinSeenRow { Mods = JoinShowPolicy.ModsSnapshot(before) };
        seen.ModsHash = JoinShowPolicy.ModsHash(seen.Mods);
        Assert.Equal(JoinShowPolicy.ReasonModsChanged, JoinShowPolicy.Mods(With(recommended, haveA), seen).Reason);
        Assert.Equal(JoinShowPolicy.ReasonSkippedReadyChanged, JoinShowPolicy.Mods(With(recommended, haveBoth), seen).Reason);

        var withR1 = new JoinSeenRow { Mods = JoinShowPolicy.ModsSnapshot(With(recommended, haveA)) };
        withR1.ModsHash = JoinShowPolicy.ModsHash(withR1.Mods);
        var another = new[] { recommended[0], new RecommendedMod("r2", "O-R2", "1", "Sounds") };
        Func<string, string?> haveR2 = id => id == A || id == "r2" ? "1" : null;
        // r1 is still not installed but unchanged; r2 is new but installed: nothing to decide.
        Assert.Equal(JoinShowPolicy.ReasonSkippedReadyChanged, JoinShowPolicy.Mods(With(another, haveR2), withR1).Reason);

        // A stored row with a hash but no lists: any recommended mod not installed counts.
        var hashOnly = new JoinSeenRow { ModsHash = "0000000000000000" };
        Assert.Equal(JoinShowPolicy.ReasonModsChanged, JoinShowPolicy.Mods(With(recommended, haveA), hashOnly).Reason);
    }

    [Fact]
    public void Changed_rows_are_tagged_new_or_changed_and_a_first_visit_tags_nothing()
    {
        var before = Model(new[] { Must(A, "1"), Must(B, "1") }, Array.Empty<Handshake.ModRecord>(), Catalog(Row(A, "1")),
            section: new JoinSection("", null, new[] { new RecommendedMod("r1", "O-R1", "1", "Maps"), new RecommendedMod("r2", "O-R2", "1", "Sounds") }, null));
        var snapshot = JoinShowPolicy.ModsSnapshot(before);
        var after = Model(new[] { Must(A, "2"), Must(B, "2"), Must("team.gamma", "1") }, Array.Empty<Handshake.ModRecord>(),
            Catalog(Row(A, "2"), Row(B, "2")),
            section: new JoinSection("", null, new[]
            {
                new RecommendedMod("r1", "O-R1", "1", "Better maps"),
                new RecommendedMod("r2", "O-R2", "1", "Sounds"),
                new RecommendedMod("r3", "O-R3", "1", ""),
            }, null));
        var changes = JoinShowPolicy.RowChanges(after, snapshot);
        Assert.Equal("changed", changes[A]);
        Assert.Equal("changed", changes[B]);
        Assert.Equal("new", changes["team.gamma"]);
        Assert.Equal("changed", changes["r1"]);
        Assert.False(changes.ContainsKey("r2"));
        Assert.Equal("new", changes["r3"]);
        Assert.Empty(JoinShowPolicy.RowChanges(after, null));
        Assert.Equal("New", JoinShowPolicy.TagWord("new"));
        Assert.Equal("Changed", JoinShowPolicy.TagWord("changed"));
    }

    [Fact]
    public void Continue_opens_the_mods_page_in_the_world_when_its_decision_says_so_and_pages_are_reported_shown()
    {
        var fx = new FakeEffects();
        var model = Model(new[] { Must(A, "1") }, new[] { Must(A, "1") }, section: Message());
        var s = new JoinFlowSession(JoinMode.InWorld, model, fx,
            new JoinShowDecision(true, JoinShowPolicy.ReasonFirstJoin), new JoinShowDecision(true, JoinShowPolicy.ReasonModsChanged),
            new Dictionary<string, string> { [A] = "changed" });
        s.Opened();
        Assert.Equal(new[] { "message" }, fx.Shown);
        Assert.Equal("mods", s.Continue());
        Assert.Equal(new[] { "message", "mods" }, fx.Shown);
        Assert.Equal("changed", s.Changes[A]);
        Assert.Equal("closed", s.Close());

        // The message skipped, the mods page shown: the screens open on mods and can still go back to the message.
        var skipMessage = new JoinFlowSession(JoinMode.InWorld, model, new FakeEffects(),
            new JoinShowDecision(false, JoinShowPolicy.ReasonUnchanged), new JoinShowDecision(true, JoinShowPolicy.ReasonModsChanged), null);
        Assert.Equal(JoinPage.Mods, skipMessage.Page);
        Assert.Equal("message", skipMessage.BackToMessage());
    }

    [Fact]
    public void The_seen_store_keeps_one_row_per_server_bounded_and_survives_an_unreadable_file()
    {
        string dir = Path.Combine(Path.GetTempPath(), "progenitor-joinseen-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(dir, JoinSeenStore.FileName);
        try
        {
            Assert.Null(JoinSeenStore.Get(path, "fp@h"));
            Assert.True(JoinSeenStore.Update(path, "fp@h", r =>
            {
                r.MessageHash = "m1";
                r.Mods = new Dictionary<string, string> { ["r:a"] = "1" };
                r.ModsHash = "x";
            }, Now));
            var row = JoinSeenStore.Get(path, "FP@H")!;
            Assert.Equal("m1", row.MessageHash);
            Assert.Equal("1", row.Mods["r:a"]);
            JoinSeenStore.Update(path, "fp@h", r => r.Dismissed = "m1", Now);
            Assert.Equal("m1", JoinSeenStore.Get(path, "fp@h")!.Dismissed);
            Assert.Equal("x", JoinSeenStore.Get(path, "fp@h")!.ModsHash);
            Assert.True(JoinSeenStore.Forget(path, "fp@h"));
            Assert.False(JoinSeenStore.Forget(path, "fp@h"));
            for (int i = 0; i < JoinSeenStore.MaxServers + 3; i++) JoinSeenStore.Update(path, "s" + i, r => r.MessageHash = "m", Now);
            Assert.Equal(JoinSeenStore.MaxServers, JoinSeenStore.Read(path).Count);
            Assert.Null(JoinSeenStore.Get(path, "s0"));
            Assert.NotNull(JoinSeenStore.Get(path, "s202"));
            // A locked file is not an empty store: the update is skipped and every row survives.
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Assert.False(JoinSeenStore.Update(path, "other", r => r.MessageHash = "x", Now));
            Assert.Equal(JoinSeenStore.MaxServers, JoinSeenStore.Read(path).Count);
            File.WriteAllText(path, "not json");
            Assert.Null(JoinSeenStore.Get(path, "s202"));
            Assert.True(JoinSeenStore.Update(path, "fresh", r => r.MessageHash = "x", Now));
            Assert.Single(JoinSeenStore.Read(path));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* temp */ }
        }
    }

    // ------------------------------------------------------------ the page model

    private const string A = "team.alpha";
    private const string B = "team.beta";

    private static Handshake.ModRecord Must(string id, string version, NetworkVersionRule rule = NetworkVersionRule.Exact) =>
        Handshake.ModRecord.CreateDeclared(id, version, NetworkCompatibilityLevel.ServerMustHave, rule);

    private static Handshake.ModRecord Optional(string id, string version, NetworkVersionRule rule = NetworkVersionRule.Exact) =>
        Handshake.ModRecord.CreateDeclared(id, version, NetworkCompatibilityLevel.NoNeedForSync, rule);

    private static ManifestMod Row(string id, string version, string? package = null, bool shares = false, bool hot = false) =>
        new(id, version, NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact, hot, null, package,
            shares ? new string('c', 64) : null, shares ? 1234 : null);

    private static ManifestBody Catalog(params ManifestMod[] rows) =>
        new(1, "Night Garden", "2026-10-04T12:00:00Z", new ManifestOperator("Ada", "", "a1b2c3d4e5f67890"), null, rows);

    private static JoinPageModel Model(
        Handshake.ModRecord[] asked, Handshake.ModRecord[] provided, ManifestBody? catalog = null, bool window = false,
        string[]? now = null, JoinSection? section = null, Func<string, string?>? loaded = null) =>
        JoinPageModel.Build(new JoinFacts
        {
            ServerName = "Night Garden",
            Asked = asked,
            Provided = provided,
            Catalog = catalog,
            InstallWindow = window,
            Section = section,
            LoadedVersionOf = loaded,
        });

    [Fact]
    public void Every_required_mod_is_listed_and_a_matching_one_is_ready()
    {
        var model = Model(new[] { Must(A, "1.0") }, new[] { Must(A, "1.0") });
        var row = Assert.Single(model.Required);
        Assert.Equal(JoinRowStatus.Ready, row.Status);
        Assert.Equal(JoinRowAction.Ready, row.Action);
        Assert.True(model.AllRequiredReady);
        Assert.Equal("1 of 1 ready", JoinText.ReadyCount(model));
        Assert.Equal("1 required mod. All ready.", JoinText.Summary(model));
    }

    [Fact]
    public void A_missing_mod_with_a_thunderstore_package_installs_and_says_whether_it_applies_now()
    {
        var model = Model(new[] { Must(A, "1.2") }, Array.Empty<Handshake.ModRecord>(), Catalog(Row(A, "1.2", "Owner-AMod")));
        var row = model.Required[0];
        Assert.Equal(JoinRowStatus.Missing, row.Status);
        Assert.Equal(JoinRowAction.Install, row.Action);
        Assert.Equal(JoinRowTags.NeedsRestart, row.Tags);
        Assert.Equal("Thunderstore · Owner", JoinText.SourceLine(row));
        Assert.Equal("Needs 1.2 · You have nothing", JoinText.VersionLine(row));
        Assert.Equal("AMod", row.Name);

        var hot = Model(new[] { Must(A, "1.2") }, Array.Empty<Handshake.ModRecord>(), Catalog(Row(A, "1.2", "Owner-AMod", hot: true)));
        Assert.Equal(JoinRowTags.AppliesNow, hot.Required[0].Tags);
    }

    [Fact]
    public void An_older_copy_updates_and_a_newer_copy_tells_the_admin()
    {
        var older = Model(new[] { Must(A, "1.2") }, new[] { Must(A, "1.1") }, Catalog(Row(A, "1.2", "Owner-A")));
        Assert.Equal(JoinRowStatus.Older, older.Required[0].Status);
        Assert.Equal(JoinRowAction.Update, older.Required[0].Action);

        var newer = Model(new[] { Must(A, "1.2") }, new[] { Must(A, "1.10") }, Catalog(Row(A, "1.2", "Owner-A")));
        var row = newer.Required[0];
        Assert.Equal(JoinRowStatus.Newer, row.Status);
        Assert.Equal(JoinRowAction.TellAdmin, row.Action);
        Assert.True((row.Tags & JoinRowTags.Ahead) != 0);
        Assert.Equal("Needs 1.2 · You have 1.10 (newer)", JoinText.VersionLine(row));
        Assert.Contains(row.Name, JoinText.CannotFixFooter(newer), StringComparison.Ordinal);

        // A row an older server still signs with a hash (its retired share) changes nothing: the client never takes it.
        var shared = Model(new[] { Must(A, "1.2") }, new[] { Must(A, "1.10") }, Catalog(Row(A, "1.2", shares: true)));
        Assert.Equal(JoinRowAction.TellAdmin, shared.Required[0].Action);
    }

    /// <summary>
    /// 2026-10-05: servers no longer send mod files. A catalog row that carries a hash and size (an older server's share)
    /// is read like any other: Thunderstore when it names a package, else Ask the admin; never a server download.
    /// </summary>
    [Fact]
    public void A_hashed_catalog_row_installs_from_Thunderstore_or_asks_the_admin_never_from_the_server()
    {
        var model = Model(new[] { Must(A, "1.2") }, Array.Empty<Handshake.ModRecord>(),
            Catalog(Row(A, "1.2", "Owner-A", shares: true, hot: true)), window: true);
        var row = model.Required[0];
        Assert.Equal(JoinRowAction.Install, row.Action);
        Assert.Equal(JoinRowTags.AppliesNow, row.Tags);
        Assert.Equal("Thunderstore · Owner", JoinText.SourceLine(row));

        var onlyServer = Model(new[] { Must(A, "1.2") }, Array.Empty<Handshake.ModRecord>(), Catalog(Row(A, "1.2", shares: true)));
        Assert.Equal(JoinRowAction.AskAdmin, onlyServer.Required[0].Action);
        Assert.Equal("No download source", JoinText.SourceLine(onlyServer.Required[0]));
    }

    [Fact]
    public void A_mod_with_no_source_asks_the_admin_and_one_only_this_client_requires_is_turned_off()
    {
        var model = Model(new[] { Must(A, "1.0") }, new[] { Must(B, "2.0") });
        var a = model.FindRequired(A)!;
        Assert.Equal(JoinRowAction.AskAdmin, a.Action);
        Assert.Equal("No download source", JoinText.SourceLine(a));
        var b = model.FindRequired(B)!;
        Assert.Equal(JoinRowOrigin.YouRequire, b.Origin);
        Assert.Equal(JoinRowStatus.ServerLacks, b.Status);
        Assert.Equal(JoinRowAction.TurnOff, b.Action);
        Assert.Equal("Alpha: this server didn't say where to get it. Ask its admins.", JoinText.CannotFixFooter(model));
    }

    [Fact]
    public void Optional_rows_are_listed_only_when_the_join_check_rejects_them()
    {
        var absent = Model(new[] { Must(A, "1.0"), Optional(B, "2.0") }, new[] { Must(A, "1.0") });
        Assert.Single(absent.Required);
        var wrong = Model(new[] { Must(A, "1.0"), Optional(B, "2.0") }, new[] { Must(A, "1.0"), Optional(B, "1.0") });
        Assert.Equal(2, wrong.Required.Count);
        Assert.Equal(JoinRowStatus.Older, wrong.FindRequired(B)!.Status);
        // Needs attention comes first.
        Assert.Equal(B, wrong.Required[0].Id);
    }

    [Fact]
    public void The_summary_counts_required_recommended_and_attention()
    {
        var section = new JoinSection("Hi", null, new[]
        {
            new RecommendedMod("r1", "Owner-One", "1.0", "Maps"),
            new RecommendedMod("r2", "Owner-Two", "2.0", ""),
            new RecommendedMod("", "Owner-Three", "", "Sounds"),
        }, null);
        var asked = new[] { Must("m1", "1"), Must("m2", "1"), Must("m3", "1"), Must("m4", "1"), Must("m5", "1"), Must("m6", "1") };
        var provided = new[] { Must("m1", "1"), Must("m2", "1"), Must("m3", "1"), Must("m4", "1") };
        var model = Model(asked, provided, section: section, loaded: id => id == "r1" ? "1.0" : null);
        Assert.Equal("6 required mods, 3 recommended. 2 need attention.", JoinText.Summary(model));
        Assert.True(model.Recommended[0].Installed);
        Assert.False(model.Recommended[1].Installed);
        Assert.Equal("Owner-Three", JoinPageModel.KeyOf(model.Recommended[2]));
        Assert.Equal("Three", model.Recommended[2].Name);
    }

    [Fact]
    public void Install_targets_are_the_installable_required_rows_and_the_checked_recommended_ones_not_installed()
    {
        var section = new JoinSection("", null, new[] { new RecommendedMod("r1", "O-R1", "1", ""), new RecommendedMod("r2", "O-R2", "1", "") }, null);
        var model = Model(new[] { Must(A, "1"), Must(B, "1") }, Array.Empty<Handshake.ModRecord>(),
            Catalog(Row(A, "1", "O-A"), Row(B, "1")), section: section, loaded: id => id == "r2" ? "1" : null);
        var targets = model.InstallTargets(new HashSet<string> { "r1", "r2" });
        Assert.Equal(new[] { A, "r1" }, targets);
        Assert.Equal("Install 2 mods", JoinText.InstallButton(targets.Count));
    }

    [Fact]
    public void Versions_compare_by_number_part_by_part()
    {
        Assert.True(JoinPageModel.CompareVersions("1.10", "1.9") > 0);
        Assert.Equal(0, JoinPageModel.CompareVersions("1.0", "1.0.0"));
        Assert.True(JoinPageModel.CompareVersions("v2.0", "1.99") > 0);
        Assert.True(JoinPageModel.CompareVersions("1.0-beta", "1.0-alpha") > 0);
    }

    // ------------------------------------------------------------ the session

    private sealed class FakeEffects : IJoinFlowEffects
    {
        public readonly List<string> Calls = new();
        public bool Clipboard = true;

        public bool CopyToClipboard(string text)
        {
            Calls.Add("copy:" + text);
            return Clipboard;
        }

        public readonly List<string> Shown = new();

        public void Leave() => Calls.Add("leave");
        public void Closed(bool completed) => Calls.Add("closed:" + completed);
        public void MarkSeen() => Calls.Add("seen");
        public void PageShown(JoinPage page) => Shown.Add(JoinFlowSession.PageName(page));
    }

    private static JoinSection Message(string text = "Welcome") => new(text, null, null, null);

    [Fact]
    public void Continue_opens_the_mods_page_and_back_returns_to_the_message()
    {
        var fx = new FakeEffects();
        var s = new JoinFlowSession(JoinMode.Refused,
            Model(new[] { Must(A, "1") }, Array.Empty<Handshake.ModRecord>(), section: Message()), fx);
        Assert.Equal(JoinPage.Message, s.Page);
        Assert.Equal("mods", s.Continue());
        Assert.Equal(JoinPage.Mods, s.Page);
        Assert.Equal("message", s.BackToMessage());
        Assert.Equal(JoinPage.Message, s.Page);
        // Escape: Mods → Message, Message → Close.
        s.Continue();
        s.Escape();
        Assert.Equal(JoinPage.Message, s.Page);
        s.Escape();
        Assert.True(s.Closed);
        Assert.Contains("closed:False", fx.Calls);
    }

    [Fact]
    public void Continue_completes_the_join_when_everything_is_ready_and_remembers_a_dismissal()
    {
        var fx = new FakeEffects();
        var s = new JoinFlowSession(JoinMode.InWorld, Model(new[] { Must(A, "1") }, new[] { Must(A, "1") }, section: Message()), fx);
        s.DontShowAgain = true;
        Assert.Equal("closed", s.Continue());
        Assert.True(s.Closed);
        Assert.Equal(new[] { "seen", "closed:True" }, fx.Calls);
    }

    [Fact]
    public void Leave_leaves_and_a_preview_only_closes()
    {
        var fx = new FakeEffects();
        var s = new JoinFlowSession(JoinMode.InWorld, Model(Array.Empty<Handshake.ModRecord>(), Array.Empty<Handshake.ModRecord>(), section: Message()), fx);
        Assert.Equal("left", s.Leave());
        Assert.Equal(new[] { "leave", "closed:False" }, fx.Calls);

        var fx2 = new FakeEffects();
        var preview = new JoinFlowSession(JoinMode.InWorld, Model(Array.Empty<Handshake.ModRecord>(), Array.Empty<Handshake.ModRecord>(), section: Message()), fx2)
        {
            IsPreview = true,
            DontShowAgain = true,
        };
        preview.Leave();
        Assert.Equal(new[] { "closed:False" }, fx2.Calls);
    }

    [Fact]
    public void Without_a_message_the_screens_open_on_mods_and_a_late_message_moves_them_only_before_any_press()
    {
        var fx = new FakeEffects();
        var bare = Model(new[] { Must(A, "1") }, Array.Empty<Handshake.ModRecord>());
        var s = new JoinFlowSession(JoinMode.Refused, bare, fx);
        Assert.Equal(JoinPage.Mods, s.Page);
        Assert.False(s.HasMessage);
        s.Refresh(Model(new[] { Must(A, "1") }, Array.Empty<Handshake.ModRecord>(), section: Message()), preferMessage: true);
        Assert.Equal(JoinPage.Message, s.Page);
        var none = new JoinFlowSession(JoinMode.Refused, bare, new FakeEffects());
        Assert.Equal("no message", none.BackToMessage());

        var t = new JoinFlowSession(JoinMode.Refused, bare, new FakeEffects());
        t.RowAction(A);
        t.Refresh(Model(new[] { Must(A, "1") }, Array.Empty<Handshake.ModRecord>(), section: Message()), preferMessage: true);
        Assert.Equal(JoinPage.Mods, t.Page);
    }

    [Fact]
    public void Row_actions_copy_for_the_admins_answer_later_updates_and_explain_turning_off()
    {
        var fx = new FakeEffects();
        var model = Model(new[] { Must(A, "1"), Must("team.gamma", "1") }, new[] { Must(B, "2") }, Catalog(Row("team.gamma", "1", "O-C")));
        var s = new JoinFlowSession(JoinMode.Refused, model, fx);
        Assert.Equal("copied", s.RowAction(A));
        Assert.StartsWith("copy:Hi! Joining Night Garden needs Alpha (team.alpha 1)", fx.Calls[0], StringComparison.Ordinal);
        Assert.Equal(JoinText.CopiedForAdmins, s.Notice);
        // JOINFLOW2: a Thunderstore row asks the "Install from Thunderstore?" confirmation first.
        Assert.Equal("ts-trust", s.RowAction("team.gamma"));
        Assert.Equal(JoinPage.ThunderstoreTrust, s.Page);
        Assert.Equal("declined", s.TsTrustBack());
        Assert.Equal(JoinText.NothingInstalled, s.Notice);
        Assert.Equal("turn-off", s.RowAction(B));
        Assert.Contains("Turn it off", s.Notice, StringComparison.Ordinal);
        fx.Clipboard = false;
        s.RowAction(A);
        Assert.StartsWith(JoinText.ClipboardUnavailable, s.Notice, StringComparison.Ordinal);
    }

    [Fact]
    public void Recommended_checkboxes_toggle_and_count_toward_install()
    {
        var section = new JoinSection("", null, new[] { new RecommendedMod("r1", "O-R1", "1", "") }, null);
        var s = new JoinFlowSession(JoinMode.InWorld, Model(Array.Empty<Handshake.ModRecord>(), Array.Empty<Handshake.ModRecord>(), section: section), new FakeEffects());
        Assert.Empty(s.InstallTargets);
        Assert.True(s.ToggleRecommended("r1"));
        Assert.Equal(new[] { "r1" }, s.InstallTargets);
        // JOINFLOW2: a checked recommended row installs from Thunderstore behind the confirmation.
        Assert.Equal("ts-trust", s.InstallSelected());
        Assert.Equal("O-R1@1", Assert.Single(s.TsTrust!.Requests).PackageVersion);
        s.TsTrustBack();
        Assert.True(s.ToggleRecommended("O-R1"));
        Assert.Empty(s.InstallTargets);
        Assert.False(s.ToggleRecommended("nope"));
    }

    // ------------------------------------------------------------ the operator pin

    private static TrustedOperatorsFile Pinned(string host, string spki, string fp, bool always) =>
        ModManifestTrust.Upsert(new TrustedOperatorsFile(), host, fp, spki, always, Now);

    [Fact]
    public void The_pin_names_its_first_seen_date_and_a_new_key_names_the_old_one()
    {
        const string host = "1.2.3.4:9876";
        var pinned = Pinned(host, "SPKI1", "fp1", always: true);
        Assert.Equal("first seen Oct 4, 2026", JoinTrustPolicy.FirstSeen(pinned, host, "SPKI1", "fp1"));

        // Re-pinning the same key keeps the first-seen date.
        var repinned = ModManifestTrust.Upsert(pinned, host, "fp1", "SPKI1", true, Now.AddDays(3));
        Assert.Equal("first seen Oct 4, 2026", JoinTrustPolicy.FirstSeen(repinned, host, "SPKI1", "fp1"));

        // A new key for the address names the old one.
        Assert.Equal("fp1", JoinTrustPolicy.PreviousKey(pinned, host, "SPKI2"));
        Assert.Equal("first time on this computer", JoinTrustPolicy.FirstSeen(new TrustedOperatorsFile(), host, "SPKI2", "fp2"));
    }

    /// <summary>
    /// The retired consent panel's <c>riskAck</c> stamp still round-trips (2026-10-05: nothing sets it any more; a file
    /// that has one keeps it), and a pin without one writes none.
    /// </summary>
    [Fact]
    public void An_old_risk_acknowledgement_round_trips_and_is_written_only_when_present()
    {
        var pinned = Pinned("h:1", "SPKI1", "fp1", always: true);
        Assert.DoesNotContain("riskAck", ModManifestJson.CanonicalTrust(pinned), StringComparison.Ordinal);

        const string old = "{\"operators\":[{\"host\":\"h:1\",\"fp\":\"fp1\",\"spki\":\"SPKI1\",\"always\":true,\"seen\":\"2026-10-04T12:00:00Z\",\"riskAck\":\"2026-10-04T12:00:00Z\"}]}";
        Assert.True(ModManifestJson.TryParseTrust(old, out var back, out _));
        string json = ModManifestJson.CanonicalTrust(back);
        Assert.Contains("\"riskAck\":\"2026-10-04T12:00:00Z\"", json, StringComparison.Ordinal);
        Assert.True(ModManifestJson.TryParseTrust(json, out var again, out _));
        Assert.Equal(json, ModManifestJson.CanonicalTrust(again));
    }
}

/// <summary>
/// JOINFLOW1: Progenitor's own server-wide rows (the server message) go to every client that answered the
/// handshake, whatever plugins it answered with; a plugin's rows still need that plugin.
/// </summary>
[Collection("HarmonySlotHandoff")]
public sealed class JoinFrameworkSettingsSyncTests : IDisposable
{
    private readonly string _dir;
    private readonly BepInEx.Configuration.ConfigFile _file;

    public JoinFrameworkSettingsSyncTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "progenitor-joinsync-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _file = new BepInEx.Configuration.ConfigFile(Path.Combine(_dir, JoinServerSettings.FileName), saveOnInit: true);
        HarmonySlotHandoff.SetSkipModules(null, refusedLiveWorld: false);
        ConfigSync.ResetForTests();
        Handshake.ResetTables();
    }

    public void Dispose()
    {
        Progenitor.Config.ConfigHelper.UninstallAll();
        ConfigSync.ResetForTests();
        Handshake.ResetTables();
        HarmonySlotHandoff.SetSkipModules(null, refusedLiveWorld: false);
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* temp */ }
    }

    [Fact]
    public void The_server_message_rows_reach_every_answering_client_and_no_one_else()
    {
        ConfigSync.Bind(_file, ConfigSync.FrameworkSettingsId, JoinServerSettings.Section, JoinServerSettings.MessageKey, "Hello", "message");
        ConfigSync.Bind(_file, "example.mod", "General", "Enabled", true, "on", pluginVersion: "1.0.0");

        var answeredNothing = ConfigSync.SnapshotFor(Array.Empty<Handshake.ModRecord>(), Array.Empty<Handshake.ModRecord>());
        var row = Assert.Single(answeredNothing);
        Assert.Equal(ConfigSync.FrameworkSettingsId, row.PluginId);
        Assert.Equal(JoinServerSettings.MessageKey, row.Key);

        var withExample = ConfigSync.SnapshotFor(
            new[] { Handshake.ModRecord.Undeclared("example.mod", "1.0.0") }, new[] { Handshake.ModRecord.Undeclared("example.mod", "1.0.0") });
        Assert.Equal(2, withExample.Count);
        Assert.Empty(ConfigSync.SnapshotFor(null, null));
        Assert.Equal("progenitor/ServerMessage/Message", JoinServerSettings.RowId(JoinServerSettings.MessageKey));
    }
}

/// <summary>
/// JOINFLOW1: the signed join section on the wire — compatible with older clients (they read only the body
/// and its signature, which the section never changes), handled when absent by newer ones, and verified
/// before use. Same collection as the other <c>ModManifest</c> suites: these drive the module.
/// </summary>
[Collection("ModManifest")]
public sealed class JoinSectionSchemaTests : IDisposable
{
    private readonly string _dir;

    public JoinSectionSchemaTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "progenitor-joinsection-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        ModManifest.ResetForTests();
        ModManifest.TestConfigDirectory = _dir;
        ModManifest.TestLoadedMods = Array.Empty<LoadedModInfo>();
        ModManifest.TestIsServer = true;
        ModManifest.TestConnectHost = "127.0.0.1:9976";
        Plugin.Logger = new TestLogSource();
    }

    public void Dispose()
    {
        ModManifest.ResetForTests();
        ModManifest.TestConfigDirectory = null;
        ModManifest.TestLoadedMods = null;
        ModManifest.TestIsServer = false;
        ModManifest.TestConnectHost = null;
        Plugin.Logger = null;
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    private static readonly DateTime Issued = new(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);

    private static LoadedModInfo[] Loaded() => new[]
    {
        new LoadedModInfo("progenitor.sample", "0.1.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact, true),
    };

    private static JoinSection Section(string message = "Welcome!\nReset in {reset}.") =>
        new(message, new ResetSchedule("2026-10-10T18:00:00Z", ResetRepeat.Days, 7, ResetPrecision.Hour, "now"),
            new[] { new RecommendedMod("r.mod", "Owner-R", "1.0", "Better maps") }, new JoinServerZone("UTC", 0));

    [Fact]
    public void An_envelope_without_a_section_is_byte_identical_to_the_old_format_and_parses_with_no_section()
    {
        using var key = ModManifestCrypto.Generate();
        var old = ModManifest.Build(Loaded(), null, key, Issued);
        string json = ModManifestJson.CanonicalEnvelope(old);
        Assert.DoesNotContain("\"join\"", json, StringComparison.Ordinal);
        Assert.StartsWith("{\"body\":", json, StringComparison.Ordinal);
        Assert.True(ModManifestJson.TryParseEnvelope(json, out var parsed, out _));
        Assert.Null(parsed.Join);
        Assert.Null(parsed.JoinError);
        Assert.Null(JoinSectionJson.Verified(parsed, out var error));
        Assert.Null(error);
    }

    [Fact]
    public void A_section_rides_outside_the_body_so_the_body_and_its_signature_are_unchanged()
    {
        using var key = ModManifestCrypto.Generate();
        var plain = ModManifest.Build(Loaded(), null, key, Issued);
        var withJoin = ModManifest.Build(Loaded(), null, key, Issued, Section());
        Assert.Equal(ModManifestJson.CanonicalBody(plain.Body), ModManifestJson.CanonicalBody(withJoin.Body));
        Assert.True(ModManifestCrypto.Verify(withJoin.Body.Operator.Spki,
            ModManifestCrypto.Utf8(ModManifestJson.CanonicalBody(withJoin.Body)), withJoin.Signature));

        string json = ModManifestJson.CanonicalEnvelope(withJoin);
        using var doc = JsonDocument.Parse(json);
        var keys = new List<string>();
        foreach (var p in doc.RootElement.EnumerateObject()) keys.Add(p.Name);
        Assert.Equal(new[] { "body", "join", "joinSignature", "signature" }, keys);
        Assert.False(doc.RootElement.GetProperty("body").TryGetProperty("join", out _));

        // What an older client does: read body and signature only, re-canonicalize, verify. It still passes.
        var body = doc.RootElement.GetProperty("body").GetRawText();
        Assert.True(ModManifestJson.TryParseBody(body, out var oldView, out _));
        Assert.True(ModManifestCrypto.Verify(oldView.Operator.Spki,
            ModManifestCrypto.Utf8(ModManifestJson.CanonicalBody(oldView)), doc.RootElement.GetProperty("signature").GetString()!));
    }

    [Fact]
    public void A_newer_client_verifies_the_section_and_drops_one_that_was_tampered_with_or_moved()
    {
        using var key = ModManifestCrypto.Generate();
        var envelope = ModManifest.Build(Loaded(), null, key, Issued, Section());
        string json = ModManifestJson.CanonicalEnvelope(envelope);
        Assert.True(ModManifestJson.TryParseEnvelope(json, out var parsed, out _));
        var section = JoinSectionJson.Verified(parsed, out var error);
        Assert.Null(error);
        Assert.NotNull(section);
        Assert.Equal("Welcome!\nReset in {reset}.", section!.Message);
        Assert.Equal(ResetRepeat.Days, section.Reset!.Repeat);
        Assert.Equal(7, section.Reset.Every);
        Assert.Equal("Better maps", section.Recommended[0].Reason);
        Assert.Equal(envelope.Body.Issued, section.CatalogIssued);

        // A changed message breaks the section's signature; the catalog itself is unaffected.
        string tampered = json.Replace("Welcome!", "Hacked!!", StringComparison.Ordinal);
        Assert.True(ModManifestJson.TryParseEnvelope(tampered, out var t, out _));
        Assert.Null(JoinSectionJson.Verified(t, out var tamperError));
        Assert.Contains("signature", tamperError!, StringComparison.Ordinal);
        Assert.True(ModManifestCrypto.Verify(t.Body.Operator.Spki, ModManifestCrypto.Utf8(ModManifestJson.CanonicalBody(t.Body)), t.Signature));

        // A section lifted onto another catalog (another issued) is refused.
        var other = ModManifest.Build(Loaded(), null, key, Issued.AddMinutes(1));
        var moved = new ManifestEnvelope(other.Body, other.Signature, envelope.Join, envelope.JoinSignature);
        Assert.Null(JoinSectionJson.Verified(moved, out var movedError));
        Assert.Contains("another catalog", movedError!, StringComparison.Ordinal);

        // Domain separation: the join signature is not a signature over the bare canonical section.
        Assert.False(ModManifestCrypto.Verify(envelope.Body.Operator.Spki,
            ModManifestCrypto.Utf8(JoinSectionJson.Canonical(envelope.Join!)), envelope.JoinSignature));
    }

    [Fact]
    public void A_section_over_its_caps_or_of_an_unknown_schema_is_dropped_and_the_catalog_still_parses()
    {
        using var key = ModManifestCrypto.Generate();
        var envelope = ModManifest.Build(Loaded(), null, key, Issued, Section());
        string json = ModManifestJson.CanonicalEnvelope(envelope);

        string big = json.Replace("Welcome!", new string('w', JoinSectionLimits.MaxMessageChars + 1), StringComparison.Ordinal);
        Assert.True(ModManifestJson.TryParseEnvelope(big, out var b, out _));
        Assert.Null(b.Join);
        Assert.Contains("longer than", b.JoinError!, StringComparison.Ordinal);
        Assert.Single(b.Body.Mods);

        string future = json.Replace("\"schema\":1,\"zone\"", "\"schema\":2,\"zone\"", StringComparison.Ordinal);
        Assert.True(ModManifestJson.TryParseEnvelope(future, out var f, out _));
        Assert.Null(f.Join);
        Assert.Contains("schema 2", f.JoinError!, StringComparison.Ordinal);

        // Unknown keys inside the section are ignored (a later server may add fields).
        string extra = json.Replace("\"message\":", "\"color\":\"red\",\"message\":", StringComparison.Ordinal);
        Assert.True(ModManifestJson.TryParseEnvelope(extra, out var x, out _));
        Assert.NotNull(x.Join);
    }

    [Fact]
    public void The_overlay_reads_recommended_rows_which_are_capped_and_never_become_catalog_rows()
    {
        var sb = new StringBuilder("{\"mods\":[],\"recommended\":[");
        for (int i = 0; i < 18; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("{\"id\":\"r").Append(i).Append("\",\"package\":\"O-R").Append(i).Append("\",\"version\":\"1\",\"reason\":\"")
                .Append(i == 3 ? new string('z', 201) : "ok").Append("\"}");
        }

        sb.Append("]}");
        Assert.True(ModManifestJson.TryParseOverrides(sb.ToString(), out var overrides, out _));
        Assert.Equal(18, overrides.Recommended.Count);
        var skipped = new List<string>();
        var clean = JoinSectionJson.Clean(overrides.Recommended, skipped);
        Assert.Equal(JoinSectionLimits.MaxRecommended, clean.Count);
        Assert.Contains(skipped, s => s.StartsWith("r3:", StringComparison.Ordinal));
        using var key = ModManifestCrypto.Generate();
        var envelope = ModManifest.Build(Loaded(), overrides, key, Issued);
        Assert.Single(envelope.Body.Mods);
    }

    [Fact]
    public void The_deny_narrowing_keeps_the_section_and_a_join_flow_client_gets_it_alone_when_no_row_is_in_the_catalog()
    {
        JoinServerSettings.TestContent = Section();
        ModManifest.TestLoadedMods = Loaded();
        ModManifest.TestIsServer = true;
        _ = ModManifest.Describe();
        var full = ModManifest.LastEnvelope!;
        Assert.NotNull(full.Join);

        ModManifest.OnHandshakeDenied(3, new List<string> { "progenitor.sample" });
        var narrowed = ModManifest.TestDenyEnvelope!;
        Assert.Single(narrowed.Body.Mods);
        Assert.NotNull(JoinSectionJson.Verified(narrowed, out _));

        ModManifest.TestDenyEnvelope = null;
        ModManifest.OnHandshakeDenied(4, new List<string> { "not.in.catalog" });
        Assert.Null(ModManifest.TestDenyEnvelope);

        ModManifest.OnHandshakeDenied(5, new List<string> { "not.in.catalog" }, joinFlowClient: true);
        var joinOnly = ModManifest.TestDenyEnvelope!;
        Assert.Empty(joinOnly.Body.Mods);
        Assert.Equal(full.Body.Issued, joinOnly.Body.Issued);
        Assert.NotNull(JoinSectionJson.Verified(joinOnly, out _));
        Assert.True(ModManifestCrypto.Verify(joinOnly.Body.Operator.Spki,
            ModManifestCrypto.Utf8(ModManifestJson.CanonicalBody(joinOnly.Body)), joinOnly.Signature));
    }

    [Fact]
    public void A_server_without_a_section_sends_no_join_only_descriptor_and_an_empty_section_still_travels()
    {
        JoinServerSettings.TestContent = null;
        ModManifest.TestLoadedMods = Loaded();
        _ = ModManifest.Describe();
        Assert.Null(ModManifest.LastEnvelope!.Join);
        ModManifest.OnHandshakeDenied(5, new List<string> { "not.in.catalog" }, joinFlowClient: true);
        Assert.Null(ModManifest.TestDenyEnvelope);
        Assert.Null(ModManifestDenyPolicy.JoinOnly(ModManifest.LastEnvelope, null));

        // The live server always builds a section (its zone and the page frequencies travel even with no message).
        JoinServerSettings.TestContent = JoinServerSettings.BuildSection("", "", "None", 1, "Minute", "", null, TimeZoneInfo.Utc, DateTime.UtcNow,
            showMessage: "Always", showMods: "Always");
        _ = ModManifest.Describe();
        var join = ModManifest.LastEnvelope!.Join!;
        Assert.True(join.IsEmpty);
        // Non-default choices are signed keys; defaults are left out, so a default section reads the same to a
        // client that predates them.
        Assert.Contains("\"showMessage\":\"Always\"", JoinSectionJson.Canonical(join), StringComparison.Ordinal);
        Assert.DoesNotContain("showMessage", JoinSectionJson.Canonical(new JoinSection("Hi", null, null, null)), StringComparison.Ordinal);
        Assert.DoesNotContain("showMods", JoinSectionJson.Canonical(new JoinSection("Hi", null, null, null)), StringComparison.Ordinal);
        Assert.Equal(MessageFrequency.Always, join.ShowMessage);
        Assert.Equal(ModsPageFrequency.Always, join.ShowMods);
        Assert.Equal(MessageFrequency.Always, JoinSectionJson.Verified(ModManifest.LastEnvelope, out _)!.ShowMessage);
    }

    [Fact]
    public void The_section_carries_the_servers_names_for_required_mods_signed_and_only_when_it_has_some()
    {
        ModManifest.PluginNameProbe = id => id == "progenitor.sample" ? "Progenitor Sample" : null;
        try
        {
            var names = ModManifest.RequiredModNames(new[]
            {
                new LoadedModInfo("progenitor.sample", "1", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact, true),
                new LoadedModInfo("optional.mod", "1", NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.Exact, true),
            });
            Assert.Equal("Progenitor Sample", Assert.Single(names).Value);
            var versions = ModManifest.RequiredModVersions(Loaded());
            Assert.Equal("0.1.0", versions["progenitor.sample"]);

            using var key = ModManifestCrypto.Generate();
            var envelope = ModManifest.Build(Loaded(), null, key, Issued, JoinSectionJson.WithRequired(Section(), names, versions));
            string json = ModManifestJson.CanonicalEnvelope(envelope);
            Assert.Contains("\"names\":{\"progenitor.sample\":\"Progenitor Sample\"}", json, StringComparison.Ordinal);
            Assert.Contains("\"versions\":{\"progenitor.sample\":\"0.1.0\"}", json, StringComparison.Ordinal);
            Assert.True(ModManifestJson.TryParseEnvelope(json, out var parsed, out _));
            var verified = JoinSectionJson.Verified(parsed, out var error);
            Assert.Null(error);
            Assert.Equal("Progenitor Sample", verified!.NameOf("Progenitor.Sample"));
            Assert.Equal("0.1.0", verified.VersionOf("progenitor.sample"));
            Assert.DoesNotContain("\"names\"", JoinSectionJson.Canonical(Section()), StringComparison.Ordinal);
        }
        finally
        {
            ModManifest.PluginNameProbe = null;
        }
    }

    [Fact]
    public void The_server_names_itself_from_its_command_line_else_its_host_settings()
    {
        string dir = Path.Combine(Path.GetTempPath(), "progenitor-ownname-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "data", "Settings"));
            File.WriteAllText(Path.Combine(dir, "data", "Settings", "ServerHostSettings.json"), "{\"Name\":\"Progenitor Forms Test\",\"Port\":9976}");
            Assert.Equal("Cli Name", ModManifest.OwnServerNameFrom(new[] { "x.exe", "-serverName", "Cli Name" }, dir));
            Assert.Equal("Progenitor Forms Test",
                ModManifest.OwnServerNameFrom(new[] { "x.exe", "-persistentDataPath", Path.Combine(dir, "data") }, dir));
            Assert.Equal("", ModManifest.OwnServerNameFrom(new[] { "x.exe" }, dir, defaultDataPath: ""));
            // Without -persistentDataPath the user's LocalLow data folder is read; the stock name is no name.
            Assert.Equal("Progenitor Forms Test", ModManifest.OwnServerNameFrom(new[] { "x.exe" }, dir, defaultDataPath: Path.Combine(dir, "data")));
            File.WriteAllText(Path.Combine(dir, "data", "Settings", "ServerHostSettings.json"), "{\"Name\":\"V Rising Server\"}");
            Assert.Equal("", ModManifest.OwnServerNameFrom(new[] { "x.exe" }, dir, defaultDataPath: Path.Combine(dir, "data")));
            Assert.Equal("Known", JoinText.PickServerName("V Rising Server", "Known"));

            ModManifest.OwnServerNameProbe = () => "Progenitor Forms Test";
            ModManifest.TestLoadedMods = Loaded();
            JoinServerSettings.TestContent = null;
            _ = ModManifest.Describe();
            Assert.Equal("Progenitor Forms Test", ModManifest.LastEnvelope!.Body.ServerName);
        }
        finally
        {
            ModManifest.OwnServerNameProbe = null;
            try { Directory.Delete(dir, true); } catch { /* temp */ }
        }
    }

    [Fact]
    public void A_body_signature_does_not_verify_a_join_section()
    {
        using var key = ModManifestCrypto.Generate();
        var envelope = ModManifest.Build(Loaded(), null, key, Issued, Section());
        // The body's signature, offered as the section's, is refused: the two payloads are domain-separated.
        var swapped = new ManifestEnvelope(envelope.Body, envelope.Signature, envelope.Join, envelope.Signature);
        Assert.Null(JoinSectionJson.Verified(swapped, out var error));
        Assert.Contains("signature", error!, StringComparison.Ordinal);
    }

    [Fact]
    public void An_edited_message_re_signs_the_catalog()
    {
        JoinServerSettings.TestContent = Section("One");
        ModManifest.TestLoadedMods = Loaded();
        _ = ModManifest.Describe();
        string first = ModManifest.TestCatalogStamp;
        JoinServerSettings.TestContent = Section("Two");
        _ = ModManifest.Describe();
        Assert.NotEqual(first, ModManifest.TestCatalogStamp);
        Assert.Equal("Two", ModManifest.LastEnvelope!.Join!.Message);
    }
}
