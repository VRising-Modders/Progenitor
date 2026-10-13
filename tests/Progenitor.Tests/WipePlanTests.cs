using System;
using System.Linq;
using Progenitor.Networking;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// WIPECONFIRM1 / PENDING2 (decisions 2026-10-06): the game's scheduled wipe set by a date, the confirmation's text
/// and its typed-word gating, and the Pending changes list.
/// </summary>
public sealed class WipePlanTests : IDisposable
{
    private static readonly DateTime Start = new(2026, 9, 10);
    private static readonly TimeZoneInfo Eastern = TimeZoneInfo.CreateCustomTimeZone(
        "Test Eastern", TimeSpan.FromHours(-5), "Test Eastern", "Test Eastern Standard", "Test Eastern Daylight",
        new[]
        {
            TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
                new DateTime(2000, 1, 1), DateTime.MaxValue.Date, TimeSpan.FromHours(1),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 2, DayOfWeek.Sunday),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 11, 1, DayOfWeek.Sunday)),
        });

    public void Dispose() => ServerRulesRows.Reset();

    [Fact]
    public void A_picked_date_becomes_whole_days_from_the_worlds_start()
    {
        var today = new DateTime(2026, 10, 7, 9, 0, 0);
        Assert.Equal(34, WipePlan.IntervalFor(Start, new DateTime(2026, 10, 14, 15, 0, 0), today, out _));
        Assert.Equal(27, WipePlan.IntervalFor(Start, today, today, out _)); // today is allowed
        // Across the fall-back daylight-saving change (Nov 1): calendar days, never 24-hour spans.
        Assert.Equal(60, WipePlan.IntervalFor(Start, new DateTime(2026, 11, 9), today, out _));
        Assert.Null(WipePlan.IntervalFor(null, new DateTime(2026, 10, 14), today, out string noStart));
        Assert.Contains("StartDate.json", noStart, StringComparison.Ordinal);
        Assert.Null(WipePlan.IntervalFor(Start, new DateTime(2026, 10, 1), today, out string past));
        Assert.Equal("Pick today or a later date.", past);
        Assert.Null(WipePlan.IntervalFor(new DateTime(2026, 10, 7), new DateTime(2026, 10, 7), today, out string sameDay));
        Assert.Contains("after the world's start", sameDay, StringComparison.Ordinal);
        Assert.Null(WipePlan.IntervalFor(Start, Start.AddDays(ServerRules.ResetDaysMax + 1), today, out _));

        // Wipe on is kept when the date still falls on it, else Any.
        var wed = new DateTime(2026, 10, 14);
        Assert.Equal("Wednesday", WipePlan.DayFor(wed, "Wednesday"));
        Assert.Equal("Any", WipePlan.DayFor(wed, "Friday"));
        Assert.Equal("Any", WipePlan.DayFor(wed, null));
        Assert.Equal(wed, ServerRules.ResetDate(Start, 34, "Wednesday"));
        Assert.Equal(wed, ServerRules.ResetDate(Start, 34, "Any"));

        Assert.Equal("= every 34 days on Any from the world's start (Thu, Sep 10, 2026)", WipePlan.IntervalNote(34, "Any", Start));
        Assert.Equal("No scheduled wipe.", WipePlan.IntervalNote(0, "Any", Start));
    }

    [Fact]
    public void The_confirmation_spells_out_the_result_and_asks_for_a_word_by_risk()
    {
        var now = new DateTime(2026, 10, 7, 8, 0, 0);
        // Later: every 30 → 40 days (Oct 10 → Oct 20): a click.
        var later = WipePlan.For(Start, 30, "Any", 40, "Any", now, Eastern, Eastern, "world1");
        Assert.Equal("", later.Word);
        Assert.False(later.Due);
        // WIPEPOLISH1: label/value lines.
        Assert.Equal(new[] { "Change", "Wipe date", "Time until", "World", "What happens" }, later.Facts.Select(f => f.Label).ToArray());
        Assert.StartsWith("Change: every 30 days on Any \u2192 every 40 days on Any", later.Body, StringComparison.Ordinal);
        Assert.Contains("Wipe date: Tue, Oct 20, 2026, first server start after 12:00 AM (server time)", later.Body, StringComparison.Ordinal);
        Assert.Contains("Time until: in 12 days 16 hours", later.Body, StringComparison.Ordinal);
        Assert.Contains("World: started Thu, Sep 10, 2026 \u00B7 27 days old", later.Body, StringComparison.Ordinal);
        Assert.Contains("archives this world to Saves/v4/world1_<ticks at wipe> and starts a new world", later.Body, StringComparison.Ordinal);

        // Earlier but not due (30 → 28 days: Oct 10 → Oct 8): WIPE.
        var earlier = WipePlan.For(Start, 30, "Any", 28, "Any", now, Eastern, Eastern, "world1");
        Assert.Equal(WipePlan.WordWipe, earlier.Word);
        Assert.False(earlier.Due);

        // Due (30 → 20 days: Sep 30 has passed): WIPE and the red warning.
        var due = WipePlan.For(Start, 30, "Any", 20, "Any", now, Eastern, Eastern, "world1");
        Assert.True(due.Due);
        Assert.Equal(WipePlan.WordWipe, due.Word);
        Assert.Contains("This world is already 27 days old: the NEXT restart wipes it.", due.Body, StringComparison.Ordinal);
        var warning = due.Facts.Last();
        Assert.Equal("Warning", warning.Label);
        Assert.Equal(DialogFactTone.Danger, warning.Tone);
        Assert.Equal(due.Warning, warning.Value);
        Assert.Equal("", later.Warning);
        Assert.Equal("Saves/v4/<SaveName>_<ticks at wipe>", WipePlan.ArchiveName(null));
        Assert.Equal("Type WIPE exactly to enable the button.", WipePlan.MismatchHint("WIPE"));
        Assert.Contains("Type WIPE to confirm.", due.Body, StringComparison.Ordinal);

        // Turning it on is WIPE; keeping the date (Any → its own weekday) is CONFIRM; off is a click.
        Assert.Equal(WipePlan.WordWipe, WipePlan.For(Start, 0, "Any", 60, "Any", now, Eastern, Eastern, null).Word);
        Assert.Equal(WipePlan.WordConfirm, WipePlan.For(Start, 30, "Any", 30, "Saturday", now, Eastern, Eastern, null).Word);
        var off = WipePlan.For(Start, 30, "Any", 0, "Any", now, Eastern, Eastern, null);
        Assert.Equal("", off.Word);
        Assert.Contains("none; the world keeps running", off.Body, StringComparison.Ordinal);

        // The player's own time is added when it differs from the server's.
        var tokyo = TimeZoneInfo.CreateCustomTimeZone("Test +9", TimeSpan.FromHours(9), "Test +9", "Test +9");
        Assert.Contains("your time)", WipePlan.WhenLine(new DateTime(2026, 10, 20), Eastern, tokyo), StringComparison.Ordinal);
        Assert.DoesNotContain("your time", WipePlan.WhenLine(new DateTime(2026, 10, 20), Eastern, Eastern), StringComparison.Ordinal);
        Assert.Equal("in 3 hours", WipePlan.Until(now, now.AddHours(3.5)));
        Assert.Equal("now", WipePlan.Until(now, now.AddHours(-1)));
    }

    [Fact]
    public void A_typed_confirmation_keeps_its_main_button_off_until_the_word_matches()
    {
        var content = new DialogContent { Shape = UiDialogShape.Prompt, ConfirmWord = WipePlan.WordWipe, MainCaption = "Change the wipe" };
        Assert.False(content.Unlocks(""));
        Assert.False(content.Unlocks("wip"));
        Assert.True(content.Unlocks("wipe"));
        Assert.True(content.Unlocks(" WIPE "));
        Assert.Equal(CellTone.Disabled, content.RowButtons().Single(b => b.Role == DialogButtonRole.Main).Tone);
        Assert.True(new DialogContent().Unlocks(""));
    }

    /// <summary>PENDING3 (live: "Pending changes" vanished with nothing pending): a fitted status row keeps an ordinary row's height.</summary>
    [Fact]
    public void A_one_line_status_row_keeps_room_for_its_name()
    {
        var row = ConfigurationPane.WrappingStatusRowSpec("k", ServerRulesRows.PendingLabel, ServerRulesRows.NothingPendingLine, 760f);
        Assert.True(row.FitWrap);
        Assert.Equal(ModSettingsRowGrid.ActionRowHeight, row.FitWrapFloor);
        Assert.Equal(ModSettingsRowGrid.ActionRowHeight, row.FitWrapHeight(20f, 16f));
        Assert.True(row.FitWrapHeight(120f, 16f) > ModSettingsRowGrid.ActionRowHeight);
        Assert.Equal(ServerRulesRows.PendingLabel, row.Find(ConfigurationPane.CellStatusLabel)!.Text);
    }

    /// <summary>
    /// PENDINGFIT1 (live 2026-10-07: Pending changes cut to one line, "4 changes apply at the next restart…"): a status row
    /// drawn while nothing was pending and rewritten in place with the summary and every change grows to all its lines.
    /// </summary>
    [Fact]
    public void A_status_row_drawn_with_one_line_grows_to_every_change_line_when_rewritten()
    {
        var row = ConfigurationPane.WrappingStatusRowSpec("k", ServerRulesRows.PendingLabel, ServerRulesRows.NothingPendingLine, 760f);
        float line = ConfigurationPane.StatusWrapLineHeight;
        // Summary + 8 changes + "+N more" + the warning: 11 measured lines are all kept (the old cap was twice the estimate).
        float eleven = 11 * line;
        Assert.True(row.FitWrapHeight(eleven, 16f) >= eleven, "fitted " + row.FitWrapHeight(eleven, 16f) + " for " + eleven);
        Assert.True(row.FitWrapCeiling >= (1 + ServerRulesRows.PendingListMax + 2) * line);
        // The other wrapping status rows (Placeholders, Players without Progenitor's message) get the same room; a
        // fixed-line preview (Players see) keeps its fixed lines and is not fitted.
        var tips = ConfigurationPane.WrappingStatusRowSpec("tips", "Placeholders", "one", 760f);
        Assert.Equal(row.FitWrapCeiling, tips.FitWrapCeiling);
        var preview = ConfigurationPane.WrappingStatusRowSpec("see", "Players see", "x", 760f, fixedLines: 3);
        Assert.False(preview.FitWrap);
        // A section copy keeps the bounds.
        Assert.Equal(row.FitWrapCeiling, row.InSection(CellSection.Red, CellSectionEdge.Middle).FitWrapCeiling);

        // The runtime measures again when a fitted row's wrapping cell is rewritten in place, and lays out on a change.
        string runtime = System.IO.File.ReadAllText(System.IO.Path.Combine(CastlesTests.RepoRootForDumpSources(),
            "src", "Progenitor.Core", "Ui", "UnityUiRuntime.cs"));
        Assert.Contains("if (_spec.FitWrap && spec.Wrap == CellWrap.Wrap)", runtime);
        Assert.Contains("if (measured && Math.Abs(after - before) > 0.5f) _host.Relayout();", runtime);
    }

    [Fact]
    public void Pending_changes_list_one_line_each_collapse_past_eight_and_warn_about_a_due_wipe()
    {
        var s = new ServerRulesState { StartDate = "2026-09-10" };
        foreach (var spec in ServerRules.Specs.Take(10))
            s.Rows.Add(new ServerRuleRow { Key = spec.Key, Loaded = spec.Choices.Count > 0 ? spec.Choices[0] : "1",
                Next = spec.Choices.Count > 1 ? spec.Choices[1] : "2", Pending = true });
        ServerRulesRows.Today = () => new DateTime(2026, 10, 7, 8, 0, 0);
        string line = ServerRulesRows.PendingLine(s);
        var lines = line.Split('\n');
        Assert.Equal(ServerRulesRows.PendingSummary(ServerRulesRows.PendingChanges(s).Count), lines[0]);
        Assert.Equal(ServerRulesRows.PendingListMax, lines.Skip(1).Count(l => l.Contains(" → ", StringComparison.Ordinal) || l == ServerRulesRows.PasswordChangedText));
        Assert.Contains(lines, l => l.StartsWith("+", StringComparison.Ordinal) && l.EndsWith(" more", StringComparison.Ordinal));

        // Option labels, not raw values.
        var heart = new ServerRulesState();
        heart.Rows.Add(new ServerRuleRow { Key = ServerRules.CastleHeartDamageMode, Loaded = "CanBeDestroyedByPlayers", Next = "CanBeDestroyedOnlyWhenDecaying", Pending = true });
        Assert.Equal("Castle heart damage: Players can destroy it → Only when decaying", ServerRulesRows.PendingLine(heart).Split('\n')[1]);

        // A due wipe adds the warning line.
        var wipe = new ServerRulesState { StartDate = "2026-09-10" };
        wipe.Rows.Add(new ServerRuleRow { Key = ServerRules.ResetDaysInterval, Loaded = "0", Next = "20", Pending = true });
        Assert.EndsWith("Warning: this world is 27 days old; the next restart wipes it.", ServerRulesRows.PendingLine(wipe), StringComparison.Ordinal);
    }

    [Fact]
    public void The_rules_help_says_what_the_game_does()
    {
        string interval = ServerRules.Find(ServerRules.ResetDaysInterval)!.Help;
        Assert.Contains("archives this world", interval, StringComparison.Ordinal);
        Assert.Contains("0 = never", interval, StringComparison.Ordinal);
        Assert.Contains("NEXT restart", interval, StringComparison.Ordinal);
        Assert.Contains("Scheduled Wipes tag", ServerRules.Find(ServerRules.DayOfReset)!.Help, StringComparison.Ordinal);
        Assert.Equal(WipePlan.Heading, ServerRulesRows.NextWipeDateSpec().Confirm == null ? "" : WipePlan.Heading);
        Assert.Equal(AdminSettingKind.Date, ServerRulesRows.NextWipeDateSpec().Kind);
        Assert.Equal("Next reset (you wipe by hand)", JoinServerSettings.NextResetByHandLabel);
    }
}
