using System;
using System.Collections.Generic;
using System.Linq;
using Progenitor.Networking;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// NOPROGNOTICE: the countdown a player without Progenitor gets (held during character creation, repeated notices,
/// the same kick at the deadline) and the notice's words. Game-free: the live loop calls this same schedule.
/// </summary>
public sealed class NoAnswerNoticeTests
{
    private static readonly NoAnswerNoticeSettings Defaults = new(20, 5, 3, 600);

    /// <summary>Drive the schedule the way the live loop does: look again after each step's delay.</summary>
    private static List<(double At, NoAnswerStep Step, int Left)> Run(NoAnswerNoticeSettings cfg, Func<double, bool> hasCharacter, double askedAt = 0, double until = 2000)
    {
        var s = new NoAnswerNoticeState(1, "TestPlayer", askedAt);
        var events = new List<(double, NoAnswerStep, int)>();
        double now = askedAt;
        for (int i = 0; i < 100000 && now <= until; i++)
        {
            var r = NoAnswerNoticePlan.Step(s, now, hasCharacter(now), cfg);
            if (r.Step != NoAnswerStep.Wait) events.Add((Math.Round(now, 3), r.Step, r.SecondsLeft));
            if (r.Step == NoAnswerStep.Kick) break;
            now += r.Delay;
        }

        return events;
    }

    [Fact]
    public void A_player_with_a_character_gets_the_notice_at_5_s_then_every_3_s_and_the_kick_at_20_s()
    {
        var events = Run(Defaults, _ => true);
        Assert.Equal(new[] { 5.0, 8.0, 11.0, 14.0, 17.0, 20.0 }, events.Select(e => e.At));
        Assert.Equal(new[] { 15, 12, 9, 6, 3 }, events.Where(e => e.Step == NoAnswerStep.Notice).Select(e => e.Left));
        Assert.Equal(NoAnswerStep.Kick, events[^1].Step);
    }

    [Fact]
    public void Character_creation_holds_the_notice_and_the_kick_until_the_character_is_in_the_world()
    {
        var events = Run(Defaults, now => now >= 100);
        Assert.All(events, e => Assert.True(e.At >= 100));
        var notices = events.Where(e => e.Step == NoAnswerStep.Notice).ToList();
        Assert.Equal(105.0, notices[0].At, 3);
        Assert.Equal(15, notices[0].Left);
        Assert.Equal(NoAnswerStep.Kick, events[^1].Step);
        Assert.Equal(120.0, events[^1].At, 3);
    }

    [Fact]
    public void Without_a_character_the_existing_kick_happens_at_the_creation_limit()
    {
        var events = Run(Defaults, _ => false);
        var only = Assert.Single(events);
        Assert.Equal(NoAnswerStep.Kick, only.Step);
        Assert.Equal(600.0, only.At, 3);
    }

    [Fact]
    public void A_zero_notice_sends_nothing_and_still_kicks_at_the_deadline()
    {
        var events = Run(new NoAnswerNoticeSettings(20, 0, 3, 600), _ => true);
        Assert.Equal((20.0, NoAnswerStep.Kick), (Assert.Single(events).At, events[0].Step));
    }

    [Fact]
    public void Settings_are_clamped_notice_before_the_deadline_repeat_at_least_2_and_the_limit_at_least_the_deadline()
    {
        var late = new NoAnswerNoticeSettings(20, 25, 3, 600);
        Assert.Equal(19, late.NoticeSeconds);
        var events = Run(late, _ => true);
        Assert.Equal(new[] { (19.0, NoAnswerStep.Notice, 1), (20.0, NoAnswerStep.Kick, 0) }, events);

        var fast = new NoAnswerNoticeSettings(20, 5, 1, 600);
        Assert.Equal(NoAnswerNoticeSettings.MinRepeatSeconds, fast.RepeatSeconds);
        Assert.Equal(new[] { 5.0, 7.0, 9.0, 11.0, 13.0, 15.0, 17.0, 19.0 }, Run(fast, _ => true).Where(e => e.Step == NoAnswerStep.Notice).Select(e => e.At));

        Assert.Equal(0, new NoAnswerNoticeSettings(1, 5, 3, 600).NoticeSeconds);
        Assert.Equal(20, new NoAnswerNoticeSettings(20, 5, 3, 3).CreationLimitSeconds);
    }

    [Fact]
    public void A_slow_frame_does_not_send_a_notice_twice()
    {
        var s = new NoAnswerNoticeState(1, "A", 0);
        Assert.Equal(NoAnswerStep.Wait, NoAnswerNoticePlan.Step(s, 0, true, Defaults).Step);
        // Nothing ran until 12.5 s: one notice now (7.5 s left, shown as 8), the next one at 14.
        var r = NoAnswerNoticePlan.Step(s, 12.5, true, Defaults);
        Assert.Equal((NoAnswerStep.Notice, 8), (r.Step, r.SecondsLeft));
        Assert.Equal(1.5, r.Delay, 3);
        Assert.Equal(NoAnswerStep.Wait, NoAnswerNoticePlan.Step(s, 13, true, Defaults).Step);
    }

    // ---- the words -------------------------------------------------------------------------------------------

    [Fact]
    public void The_default_full_line_names_only_Progenitor_and_where_to_get_it()
    {
        const string expected15 = "This server uses mods. To join, install the Progenitor mod: get it free with a mod manager, r2modman or the Thunderstore app (thunderstore.io, game: V Rising). Progenitor sets up the rest of this server's mods when you join again. You'll be disconnected in 15 seconds.";
        var mods = new[] { "Progenitor Sample 2026.10.56", "Exercise Timed Buff 0.1.0" };
        Assert.Equal(expected15, NoAnswerNoticeText.Build("", mods, 15));
        Assert.Equal(expected15, NoAnswerNoticeText.Build("", Array.Empty<string>(), 15));
        // An admin's own text can still list the mods; the list keeps its five-name cap.
        var seven = Enumerable.Range(1, 7).Select(i => "Mod" + i + " 1.0").ToArray();
        Assert.Contains("Mod1 1.0, Mod2 1.0, Mod3 1.0, Mod4 1.0, Mod5 1.0, and 2 more.", NoAnswerNoticeText.Build("Needed: {mods}.", seven, 9));
    }

    [Fact]
    public void The_admins_message_fills_its_tokens_and_stays_plain_ascii_inside_the_chat_cap()
    {
        Assert.Equal("Get A 1.0 from discord. Bye in 12 s.", NoAnswerNoticeText.Build("Get {mods} from discord. Bye in {seconds} s.", new[] { "A 1.0" }, 12));
        Assert.Equal("Join our discord first.", NoAnswerNoticeText.Build("Join our discord first.", new[] { "A" }, 5));
        Assert.Equal("Needs Progenitor.", NoAnswerNoticeText.Build("Needs {mods}.", null, 5));
        string marked = NoAnswerNoticeText.Build("<color=red>Héllo</color>\nnext — line", null, 1);
        Assert.Equal("(color=red)H?llo(/color) next - line", marked);
        string longest = NoAnswerNoticeText.Build(new string('x', 400) + "{mods}", Enumerable.Range(0, 50).Select(i => new string('m', 60)).ToArray(), 99);
        Assert.InRange(longest.Length, 1, Progenitor.Chat.ChatLogic.MaxUtf8Bytes);
        Assert.All(longest, c => Assert.InRange(c, ' ', '~'));
    }

    [Fact]
    public void The_short_line_stays_one_line_over_the_head_and_the_full_line_goes_out_every_other_notice()
    {
        Assert.Equal("This server needs the Progenitor mod. Disconnecting in 12 s.", NoAnswerNoticeText.Short(12));
        Assert.InRange(NoAnswerNoticeText.Short(600).Length, 1, NoAnswerNoticeText.MaxShortChars);
        var first = NoProgenitorNotice.For(0, 15, "", new[] { "A 1.0" });
        var second = NoProgenitorNotice.For(1, 12, "", new[] { "A 1.0" });
        var third = NoProgenitorNotice.For(2, 9, "Custom {seconds}", new[] { "A 1.0" });
        Assert.NotNull(first.Full);
        Assert.Null(second.Full);
        Assert.Equal("This server needs the Progenitor mod. Disconnecting in 12 s.", second.Short);
        Assert.Equal("Custom 9", third.Full);
    }
}
