using System;
using System.Collections.Generic;
using Progenitor.Networking;
using Progenitor.Plugins;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Game-free coverage of the handshake answer deadline. A fake clock drives
/// <see cref="HandshakeAnswerWatch"/> so expire / cancel / re-arm do not wait on
/// <c>MainThread.PostDelayed</c>. Same collection as <see cref="HandshakeEvaluateTests"/>
/// because both share Handshake tables and <c>RearmDeadlinesOnNextInstall</c>.
/// </summary>
[Collection("Handshake")]
public class HandshakeAnswerWatchTests : IDisposable
{
    public HandshakeAnswerWatchTests() => Handshake.ResetTables();

    public void Dispose() => Handshake.ResetTables();

    [Fact]
    public void Default_timeout_is_20_seconds()
    {
        Assert.Equal(20, HandshakeAnswerWatch.DefaultTimeoutSeconds);
        Assert.Equal(20, Handshake.DefaultAnswerTimeoutSeconds);
        Assert.Equal(2.0, HandshakeAnswerWatch.KickFlushSeconds);
        Assert.Equal(0, HandshakeAnswerWatch.AskRetryDelaySeconds);
    }

    [Fact]
    public void Status_starts_at_none()
    {
        double t = 0;
        var watch = new HandshakeAnswerWatch(() => t);
        Assert.Equal(HandshakeAnswerWatch.Status.None, watch.GetStatus(1));
        Assert.Equal("none", watch.Token(1));
        Assert.Equal(0, watch.PendingCount);
    }

    [Fact]
    public void Arm_then_expire_at_timeout_fires_reason_and_becomes_timeout()
    {
        double t = 0;
        var watch = new HandshakeAnswerWatch(() => t);
        var arm = watch.Arm(3, "TestPlayer", 20, skipUser: false, serverRequiresAnswer: true,
            "progenitor.sample 0.1.0 ServerMustHave/Exact");
        Assert.True(arm.Scheduled);
        Assert.Equal("asked", watch.Token(3));
        Assert.Equal(1, watch.PendingCount);

        t = 19.99;
        Assert.False(watch.TryExpire(3, arm.Generation, out _));
        Assert.Equal("asked", watch.Token(3));

        t = 20;
        Assert.True(watch.TryExpire(3, arm.Generation, out var due));
        Assert.Equal(3, due.UserIndex);
        Assert.Equal("TestPlayer", due.Name);
        Assert.Equal(
            "Progenitor: no handshake answer within 20 s (server requires: progenitor.sample 0.1.0 ServerMustHave/Exact)",
            due.Reason);
        Assert.Equal("timeout", watch.Token(3));
        Assert.Equal(0, watch.PendingCount);
        Assert.False(watch.TryExpire(3, arm.Generation, out _));
    }

    [Fact]
    public void Answer_cancels_pending_deadline()
    {
        double t = 0;
        var watch = new HandshakeAnswerWatch(() => t);
        var arm = watch.Arm(1, "TestPlayer", 20, skipUser: false, serverRequiresAnswer: true, "sample 1.0 ServerMustHave/Exact");
        watch.OnAnswer(1);
        Assert.Equal("ok", watch.Token(1));
        t = 20;
        Assert.False(watch.TryExpire(1, arm.Generation, out _));
        Assert.Equal("ok", watch.Token(1));
    }

    [Fact]
    public void Disconnect_cancels_pending_deadline()
    {
        double t = 0;
        var watch = new HandshakeAnswerWatch(() => t);
        var arm = watch.Arm(1, "TestPlayer", 20, skipUser: false, serverRequiresAnswer: true, "sample 1.0 ServerMustHave/Exact");
        watch.OnDisconnect(1);
        Assert.Equal("none", watch.Token(1));
        t = 20;
        Assert.False(watch.TryExpire(1, arm.Generation, out _));
    }

    [Fact]
    public void Re_arm_invalidates_the_previous_generation()
    {
        double t = 0;
        var watch = new HandshakeAnswerWatch(() => t);
        var first = watch.Arm(1, "TestPlayer", 20, skipUser: false, serverRequiresAnswer: true, "sample 1.0 ServerMustHave/Exact");
        t = 5;
        var second = watch.Arm(1, "TestPlayer", 20, skipUser: false, serverRequiresAnswer: true, "sample 1.0 ServerMustHave/Exact");
        Assert.NotEqual(first.Generation, second.Generation);
        Assert.Equal("asked", watch.Token(1));

        t = 20;
        Assert.False(watch.TryExpire(1, first.Generation, out _));
        Assert.Equal("asked", watch.Token(1));

        t = 25;
        Assert.True(watch.TryExpire(1, second.Generation, out var due));
        Assert.Contains("within 20 s", due.Reason);
        Assert.Equal("timeout", watch.Token(1));
    }

    [Fact]
    public void Skip_user_is_never_pending()
    {
        double t = 0;
        var watch = new HandshakeAnswerWatch(() => t);
        var arm = watch.Arm(10001, "Ghost1", 20, skipUser: true, serverRequiresAnswer: true, "sample 1.0 ServerMustHave/Exact");
        Assert.True(arm.Skipped);
        Assert.False(arm.Scheduled);
        Assert.Equal("none", watch.Token(10001));
        t = 20;
        Assert.False(watch.TryExpire(10001, arm.Generation, out _));
    }

    [Fact]
    public void Unverified_when_nothing_requires_an_answer()
    {
        double t = 0;
        var watch = new HandshakeAnswerWatch(() => t);
        var arm = watch.Arm(1, "TestPlayer", 20, skipUser: false, serverRequiresAnswer: false, "none");
        Assert.True(arm.Unverified);
        Assert.False(arm.Scheduled);
        Assert.Equal("unverified", watch.Token(1));
        t = 20;
        Assert.False(watch.TryExpire(1, arm.Generation, out _));
    }

    [Fact]
    public void Timeout_zero_disables_the_deadline()
    {
        double t = 0;
        var watch = new HandshakeAnswerWatch(() => t);
        var arm = watch.Arm(1, "TestPlayer", 0, skipUser: false, serverRequiresAnswer: true, "sample 1.0 ServerMustHave/Exact");
        Assert.True(arm.Disabled);
        Assert.False(arm.Scheduled);
        Assert.Equal("none", watch.Token(1));
        t = 60;
        Assert.False(watch.TryExpire(1, arm.Generation, out _));
    }

    [Fact]
    public void Skip_wins_over_unverified_and_does_not_schedule()
    {
        var watch = new HandshakeAnswerWatch(() => 0);
        var arm = watch.Arm(1, "Ghost1", 20, skipUser: true, serverRequiresAnswer: false, "none");
        Assert.True(arm.Skipped);
        Assert.False(arm.Unverified);
        Assert.False(arm.Scheduled);
    }

    [Fact]
    public void Late_answer_after_timeout_becomes_ok()
    {
        double t = 0;
        var watch = new HandshakeAnswerWatch(() => t);
        var arm = watch.Arm(1, "TestPlayer", 20, skipUser: false, serverRequiresAnswer: true, "sample 1.0 ServerMustHave/Exact");
        t = 20;
        Assert.True(watch.TryExpire(1, arm.Generation, out _));
        Assert.Equal("timeout", watch.Token(1));
        watch.OnAnswer(1);
        Assert.Equal("ok", watch.Token(1));
    }

    [Fact]
    public void Answer_without_arm_is_ok()
    {
        var watch = new HandshakeAnswerWatch(() => 0);
        watch.OnAnswer(7);
        Assert.Equal("ok", watch.Token(7));
    }

    [Fact]
    public void Wrong_generation_does_not_expire()
    {
        double t = 0;
        var watch = new HandshakeAnswerWatch(() => t);
        var arm = watch.Arm(1, "TestPlayer", 20, skipUser: false, serverRequiresAnswer: true, "sample 1.0 ServerMustHave/Exact");
        t = 20;
        Assert.False(watch.TryExpire(1, arm.Generation + 1, out _));
        Assert.Equal("asked", watch.Token(1));
    }

    [Fact]
    public void Two_users_are_independent()
    {
        double t = 0;
        var watch = new HandshakeAnswerWatch(() => t);
        var a = watch.Arm(1, "A", 20, skipUser: false, serverRequiresAnswer: true, "a 1.0 ServerMustHave/Exact");
        var b = watch.Arm(2, "B", 20, skipUser: false, serverRequiresAnswer: true, "b 1.0 ServerMustHave/Exact");
        watch.OnAnswer(1);
        t = 20;
        Assert.False(watch.TryExpire(1, a.Generation, out _));
        Assert.True(watch.TryExpire(2, b.Generation, out var due));
        Assert.Equal("B", due.Name);
        Assert.Equal("ok", watch.Token(1));
        Assert.Equal("timeout", watch.Token(2));
    }

    [Fact]
    public void ServerRequiresAnswer_empty_or_optional_only_is_false()
    {
        Assert.False(Handshake.Compare.ServerRequiresAnswer(null));
        Assert.False(Handshake.Compare.ServerRequiresAnswer(Array.Empty<Handshake.ModRecord>()));
        Assert.False(Handshake.Compare.ServerRequiresAnswer(new[]
        {
            D("opt.mod", "1.0", NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.Exact),
            D("ui.mod", "1.0", NetworkCompatibilityLevel.ClientOnly, NetworkVersionRule.Exact),
        }));
    }

    [Fact]
    public void ServerRequiresAnswer_ServerMustHave_or_undeclared_is_true()
    {
        Assert.True(Handshake.Compare.ServerRequiresAnswer(new[] { U("my.mod", "1.0") }));
        Assert.True(Handshake.Compare.ServerRequiresAnswer(new[]
        {
            D("need.mod", "1.2.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Minor),
            D("opt.mod", "1.0", NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.Exact),
        }));
        Assert.True(Handshake.Compare.LevelRequiresAnswer(NetworkCompatibilityLevel.ServerMustHave));
        Assert.False(Handshake.Compare.LevelRequiresAnswer(NetworkCompatibilityLevel.NoNeedForSync));
        Assert.False(Handshake.Compare.LevelRequiresAnswer(NetworkCompatibilityLevel.ClientOnly));
        Assert.True(Handshake.Compare.LevelRequiresAnswer((NetworkCompatibilityLevel)9));
    }

    [Fact]
    public void FormatRequiredSummary_lists_enforced_rows_as_level_slash_rule()
    {
        Assert.Equal("none", Handshake.Compare.FormatRequiredSummary(null));
        Assert.Equal("none", Handshake.Compare.FormatRequiredSummary(new[]
        {
            D("opt.mod", "1.0", NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.Exact),
        }));
        var summary = Handshake.Compare.FormatRequiredSummary(new[]
        {
            D("ui.mod", "1.0", NetworkCompatibilityLevel.ClientOnly, NetworkVersionRule.Exact),
            D("progenitor.sample", "0.1.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact),
            D("other.mod", "1.2.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Minor),
            U("legacy.mod", "3.0"),
        });
        Assert.Equal(
            "progenitor.sample 0.1.0 ServerMustHave/Exact, other.mod 1.2.0 ServerMustHave/Minor, legacy.mod 3.0 ServerMustHave/Exact",
            summary);
    }

    [Fact]
    public void NoAnswerReason_matches_the_kick_text()
    {
        Assert.Equal(
            "Progenitor: no handshake answer within 20 s (server requires: sample 1.0 ServerMustHave/Exact)",
            Handshake.Compare.NoAnswerReason(20, "sample 1.0 ServerMustHave/Exact"));
        Assert.Equal(
            "Progenitor: no handshake answer within 0 s (server requires: none)",
            Handshake.Compare.NoAnswerReason(0, null));
    }

    [Fact]
    public void UserAnswerStatus_on_Handshake_is_none_until_armed()
    {
        Assert.Equal("none", Handshake.UserAnswerStatus(1));
    }

    [Fact]
    public void Same_ALC_Install_after_Uninstall_rearms_unanswered_and_invalidates_the_old_generation()
    {
        double t = 0;
        var watch = new HandshakeAnswerWatch(() => t);
        var first = watch.Arm(1, "TestPlayer", 20, skipUser: false, serverRequiresAnswer: true,
            "sample 1.0 ServerMustHave/Exact");
        Handshake.NotifyUninstalled();
        Assert.True(Handshake.RearmDeadlinesOnNextInstall);
        Assert.False(Handshake.AnswerTimeoutBound);
        watch.Clear();
        Assert.Equal("none", watch.Token(1));
        Assert.True(Handshake.ConsumeRearmOnInstall());
        Assert.False(Handshake.ConsumeRearmOnInstall());
        Handshake.AnswerTimeoutBound = true;
        var second = watch.Arm(1, "TestPlayer", 20, skipUser: false, serverRequiresAnswer: true,
            "sample 1.0 ServerMustHave/Exact");
        Assert.NotEqual(first.Generation, second.Generation);
        Assert.Equal("asked", watch.Token(1));
        t = 20;
        Assert.False(watch.TryExpire(1, first.Generation, out _));
        Assert.True(watch.TryExpire(1, second.Generation, out var due));
        Assert.Contains("within 20 s", due.Reason);
        Assert.Equal("timeout", watch.Token(1));
    }

    [Fact]
    public void Uninstall_clears_answer_timeout_binding_so_Install_must_rebind()
    {
        Handshake.AnswerTimeoutBound = true;
        Handshake.NotifyUninstalled();
        Assert.False(Handshake.AnswerTimeoutBound);
        Handshake.AnswerTimeoutBound = true;
        Assert.True(Handshake.AnswerTimeoutBound);
        Handshake.NotifyUninstalled();
        Assert.False(Handshake.AnswerTimeoutBound);
    }

    [Fact]
    public void ShouldSkipAnswerDeadline_is_fail_closed()
    {
        Assert.False(Handshake.Compare.ShouldSkipAnswerDeadline(
            lookupThrew: true, approvedTablePresent: true, userRowFound: true, hasNetConnection: false, isBot: false));
        Assert.False(Handshake.Compare.ShouldSkipAnswerDeadline(
            lookupThrew: false, approvedTablePresent: false, userRowFound: false, hasNetConnection: false, isBot: false));
        Assert.False(Handshake.Compare.ShouldSkipAnswerDeadline(
            lookupThrew: false, approvedTablePresent: true, userRowFound: false, hasNetConnection: false, isBot: false));
        Assert.False(Handshake.Compare.ShouldSkipAnswerDeadline(
            lookupThrew: false, approvedTablePresent: true, userRowFound: true, hasNetConnection: true, isBot: false));
        Assert.False(Handshake.Compare.ShouldSkipAnswerDeadline(
            lookupThrew: false, approvedTablePresent: true, userRowFound: true, hasNetConnection: true, isBot: true));
        Assert.True(Handshake.Compare.ShouldSkipAnswerDeadline(
            lookupThrew: false, approvedTablePresent: true, userRowFound: false, hasNetConnection: false, isBot: true));
        Assert.True(Handshake.Compare.ShouldSkipAnswerDeadline(
            lookupThrew: false, approvedTablePresent: false, userRowFound: false, hasNetConnection: false, isBot: true));
        Assert.True(Handshake.Compare.ShouldSkipAnswerDeadline(
            lookupThrew: false, approvedTablePresent: true, userRowFound: true, hasNetConnection: false, isBot: false));
    }

    [Fact]
    public void Failed_send_retries_once_then_kicks_could_not_ask()
    {
        Assert.Equal(Handshake.Compare.AskSendFollowUp.ArmDeadline,
            Handshake.Compare.NextAskSendFollowUp(sent: true, alreadyRetried: false));
        Assert.Equal(Handshake.Compare.AskSendFollowUp.ArmDeadline,
            Handshake.Compare.NextAskSendFollowUp(sent: true, alreadyRetried: true));
        Assert.Equal(Handshake.Compare.AskSendFollowUp.RetryOnce,
            Handshake.Compare.NextAskSendFollowUp(sent: false, alreadyRetried: false));
        Assert.Equal(Handshake.Compare.AskSendFollowUp.KickCouldNotAsk,
            Handshake.Compare.NextAskSendFollowUp(sent: false, alreadyRetried: true));
        Assert.Equal(
            "Progenitor: could not ask for a handshake answer",
            Handshake.Compare.CouldNotAskReason());
    }

    [Fact]
    public void Successful_answer_cancels_ask_retry_and_could_not_ask_kick()
    {
        Assert.True(Handshake.Compare.ShouldSkipAsk(hasAnswered: true));
        Assert.False(Handshake.Compare.ShouldSkipAsk(hasAnswered: false));

        Assert.False(Handshake.Compare.ShouldProceedWithKick(
            HandshakeAnswerWatch.Status.Ok, timeoutKick: false));
        Assert.False(Handshake.Compare.ShouldProceedWithKick(
            HandshakeAnswerWatch.Status.Ok, timeoutKick: true));
        Assert.True(Handshake.Compare.ShouldProceedWithKick(
            HandshakeAnswerWatch.Status.None, timeoutKick: false));
        Assert.True(Handshake.Compare.ShouldProceedWithKick(
            HandshakeAnswerWatch.Status.Mismatch, timeoutKick: false));
        Assert.False(Handshake.Compare.ShouldProceedWithKick(
            HandshakeAnswerWatch.Status.Pending, timeoutKick: true));
        Assert.True(Handshake.Compare.ShouldProceedWithKick(
            HandshakeAnswerWatch.Status.Timeout, timeoutKick: true));
    }

    [Fact]
    public void Could_not_ask_does_not_send_Result_until_the_two_second_backoff()
    {
        Assert.Equal(2.0, HandshakeAnswerWatch.KickFlushSeconds);

        var delay = new FakeDelay();
        var watch = new HandshakeAnswerWatch(() => delay.Now);
        var results = new List<string>();
        var disconnects = new List<string>();
        ScheduleCouldNotAsk(delay, watch, 1, "TestPlayer", results, disconnects);

        delay.AdvanceTo(1.99);
        Assert.Empty(results);
        Assert.Empty(disconnects);
        Assert.Equal("none", watch.Token(1));

        delay.AdvanceTo(HandshakeAnswerWatch.KickFlushSeconds);
        Assert.Equal(new[] { Handshake.Compare.CouldNotAskReason() }, results);
        Assert.Empty(disconnects);
        Assert.Equal("asked", watch.Token(1));
    }

    [Fact]
    public void Could_not_ask_Pending_waits_then_sends_unless_Ok_arrives()
    {
        var delay = new FakeDelay();
        var watch = new HandshakeAnswerWatch(() => delay.Now);
        watch.Arm(1, "TestPlayer", 20, skipUser: false, serverRequiresAnswer: true, "sample 1.0 ServerMustHave/Exact");
        var results = new List<string>();
        var disconnects = new List<string>();
        ScheduleCouldNotAsk(delay, watch, 1, "TestPlayer", results, disconnects);

        delay.AdvanceTo(HandshakeAnswerWatch.KickFlushSeconds);
        Assert.Equal(new[] { Handshake.Compare.CouldNotAskReason() }, results);
        Assert.Empty(disconnects);

        watch.OnAnswer(1);
        delay.AdvanceTo(HandshakeAnswerWatch.KickFlushSeconds * 2);
        Assert.Empty(disconnects);
        Assert.Equal("ok", watch.Token(1));
        Assert.False(Handshake.Compare.ShouldScheduleCouldNotAskKick(watch.GetStatus(1)));
    }

    [Fact]
    public void Could_not_ask_does_not_kick_Mismatch_or_Unverified()
    {
        Assert.False(Handshake.Compare.ShouldScheduleCouldNotAskKick(HandshakeAnswerWatch.Status.Mismatch));
        Assert.False(Handshake.Compare.ShouldSendCouldNotAskResult(
            HandshakeAnswerWatch.Status.Mismatch, delayed: true));
        Assert.False(Handshake.Compare.ShouldScheduleCouldNotAskKick(HandshakeAnswerWatch.Status.Unverified));
        Assert.False(Handshake.Compare.ShouldSendCouldNotAskResult(
            HandshakeAnswerWatch.Status.Unverified, delayed: true));
        Assert.False(Handshake.Compare.ShouldSendCouldNotAskResult(
            HandshakeAnswerWatch.Status.Ok, delayed: false));
        Assert.False(Handshake.Compare.ShouldDisconnectCouldNotAsk(HandshakeAnswerWatch.Status.Mismatch));
        Assert.False(Handshake.Compare.ShouldDisconnectCouldNotAsk(HandshakeAnswerWatch.Status.Unverified));
        Assert.False(Handshake.Compare.ShouldDisconnectCouldNotAsk(HandshakeAnswerWatch.Status.Ok));
    }

    [Fact]
    public void Could_not_ask_timeout_status_does_not_double_send()
    {
        Assert.False(Handshake.Compare.ShouldScheduleCouldNotAskKick(HandshakeAnswerWatch.Status.Timeout));
        Assert.False(Handshake.Compare.ShouldSendCouldNotAskResult(
            HandshakeAnswerWatch.Status.Timeout, delayed: true));
        Assert.False(Handshake.Compare.ShouldDisconnectCouldNotAsk(HandshakeAnswerWatch.Status.Timeout));
    }

    [Fact]
    public void Any_answer_cancels_the_could_not_ask_backoff()
    {
        double t = 0;
        var watch = new HandshakeAnswerWatch(() => t);
        Assert.True(Handshake.Compare.ShouldSendCouldNotAskResult(
            watch.GetStatus(1), delayed: true));

        watch.OnAnswer(1);
        Assert.True(Handshake.Compare.AnswerCancelsCouldNotAsk(watch.GetStatus(1)));
        Assert.False(Handshake.Compare.ShouldSendCouldNotAskResult(
            watch.GetStatus(1), delayed: true));
        Assert.False(Handshake.Compare.ShouldDisconnectCouldNotAsk(watch.GetStatus(1)));

        watch.OnMismatch(2);
        Assert.True(Handshake.Compare.AnswerCancelsCouldNotAsk(watch.GetStatus(2)));
        Assert.False(Handshake.Compare.ShouldSendCouldNotAskResult(
            watch.GetStatus(2), delayed: true));
        Assert.False(Handshake.Compare.ShouldDisconnectCouldNotAsk(watch.GetStatus(2)));
        Assert.True(watch.HasAnswered(1));
        Assert.True(watch.HasAnswered(2));
    }

    [Fact]
    public void Mismatch_after_could_not_ask_backoff_uses_the_mismatch_reason()
    {
        var delay = new FakeDelay();
        var watch = new HandshakeAnswerWatch(() => delay.Now);
        var results = new List<string>();
        var disconnects = new List<string>();
        ScheduleCouldNotAsk(delay, watch, 1, "TestPlayer", results, disconnects);

        delay.AdvanceTo(HandshakeAnswerWatch.KickFlushSeconds);
        Assert.Equal(new[] { Handshake.Compare.CouldNotAskReason() }, results);
        Assert.Empty(disconnects);

        const string mismatch = "Mod mismatch: example.mod missing";
        watch.OnMismatch(1, mismatch);
        Assert.Equal("mismatch", watch.Token(1));
        Assert.Equal(
            mismatch,
            Handshake.Compare.DisconnectReasonAfterCouldNotAskResult(watch.GetStatus(1), mismatch));

        delay.AdvanceTo(HandshakeAnswerWatch.KickFlushSeconds * 2);
        Assert.Equal(new[] { mismatch }, disconnects);
        Assert.NotEqual(Handshake.Compare.CouldNotAskReason(), disconnects[0]);
    }

    [Fact]
    public void Could_not_ask_disconnect_waits_after_the_result()
    {
        var delay = new FakeDelay();
        var watch = new HandshakeAnswerWatch(() => delay.Now);
        var results = new List<string>();
        var disconnects = new List<string>();
        ScheduleCouldNotAsk(delay, watch, 1, "TestPlayer", results, disconnects);

        delay.AdvanceTo(HandshakeAnswerWatch.KickFlushSeconds);
        Assert.Single(results);
        Assert.Empty(disconnects);
        Assert.Equal("asked", watch.Token(1));

        delay.AdvanceTo(HandshakeAnswerWatch.KickFlushSeconds * 2);
        Assert.Empty(disconnects);
        Assert.Equal("asked", watch.Token(1));
        Assert.False(Handshake.Compare.ShouldDisconnectAfterCouldNotAskResult(
            watch.GetStatus(1), deadlineArmed: true));
        Assert.Equal(
            Handshake.Compare.CouldNotAskReason(),
            Handshake.Compare.DisconnectReasonAfterCouldNotAskResult(
                HandshakeAnswerWatch.Status.None, "Mod mismatch: example.mod missing"));
        Assert.Equal(
            "",
            Handshake.Compare.DisconnectReasonAfterCouldNotAskResult(
                HandshakeAnswerWatch.Status.Ok, "Mod mismatch: example.mod missing"));
    }

    [Fact]
    public void Answer_after_could_not_ask_Arm_is_accepted()
    {
        var delay = new FakeDelay();
        var watch = new HandshakeAnswerWatch(() => delay.Now);
        var results = new List<string>();
        var disconnects = new List<string>();
        ScheduleCouldNotAsk(delay, watch, 1, "TestPlayer", results, disconnects);

        delay.AdvanceTo(HandshakeAnswerWatch.KickFlushSeconds);
        Assert.Equal("asked", watch.Token(1));

        delay.AdvanceTo(HandshakeAnswerWatch.KickFlushSeconds * 2);
        Assert.Empty(disconnects);
        Assert.Equal("asked", watch.Token(1));

        delay.AdvanceTo(5);
        watch.OnAnswer(1);
        Assert.Equal("ok", watch.Token(1));
        Assert.False(watch.TryExpire(1, generation: 1, out _));
        Assert.Empty(disconnects);
        Assert.False(Handshake.Compare.ShouldDisconnectAfterCouldNotAskResult(
            watch.GetStatus(1), deadlineArmed: true));
    }

    [Fact]
    public void Could_not_ask_second_hop_does_not_kick_an_armed_pending_row()
    {
        Assert.True(Handshake.Compare.ShouldDisconnectCouldNotAsk(HandshakeAnswerWatch.Status.Pending));
        Assert.True(Handshake.Compare.ShouldDisconnectCouldNotAsk(HandshakeAnswerWatch.Status.None));
        Assert.False(Handshake.Compare.ShouldDisconnectAfterCouldNotAskResult(
            HandshakeAnswerWatch.Status.Pending, deadlineArmed: true));
        Assert.True(Handshake.Compare.ShouldDisconnectAfterCouldNotAskResult(
            HandshakeAnswerWatch.Status.Mismatch, deadlineArmed: true));
        Assert.True(Handshake.Compare.ShouldDisconnectAfterCouldNotAskResult(
            HandshakeAnswerWatch.Status.None, deadlineArmed: false));
        Assert.False(Handshake.Compare.ShouldDisconnectAfterCouldNotAskResult(
            HandshakeAnswerWatch.Status.Ok, deadlineArmed: false));
    }

    [Fact]
    public void Could_not_ask_without_a_deadline_still_disconnects_at_the_second_hop()
    {
        var delay = new FakeDelay();
        var watch = new HandshakeAnswerWatch(() => delay.Now);
        var results = new List<string>();
        var disconnects = new List<string>();
        ScheduleCouldNotAsk(delay, watch, 1, "TestPlayer", results, disconnects, timeoutSeconds: 0);

        delay.AdvanceTo(HandshakeAnswerWatch.KickFlushSeconds);
        Assert.Equal(new[] { Handshake.Compare.CouldNotAskReason() }, results);
        Assert.Equal("none", watch.Token(1));
        Assert.Empty(disconnects);

        delay.AdvanceTo(HandshakeAnswerWatch.KickFlushSeconds * 2);
        Assert.Equal(new[] { Handshake.Compare.CouldNotAskReason() }, disconnects);
    }

    [Fact]
    public void Arm_does_not_overwrite_Ok_or_Mismatch()
    {
        var watch = new HandshakeAnswerWatch(() => 0);
        watch.Arm(1, "TestPlayer", 20, skipUser: false, serverRequiresAnswer: true, "sample 1.0 ServerMustHave/Exact");
        watch.OnAnswer(1);
        var again = watch.Arm(1, "TestPlayer", 20, skipUser: false, serverRequiresAnswer: true, "sample 1.0 ServerMustHave/Exact");
        Assert.False(again.Scheduled);
        Assert.Equal("ok", watch.Token(1));
        Assert.False(Handshake.Compare.CanArmDeadline(watch.GetStatus(1)));

        watch.OnMismatch(2);
        var mismatchArm = watch.Arm(2, "Other", 20, skipUser: false, serverRequiresAnswer: true, "sample 1.0 ServerMustHave/Exact");
        Assert.False(mismatchArm.Scheduled);
        Assert.Equal("mismatch", watch.Token(2));
        Assert.False(Handshake.Compare.CanArmDeadline(watch.GetStatus(2)));
    }

    [Fact]
    public void Users_op_tokens_keep_none_and_asked_not_pending()
    {
        Assert.NotEqual("pending", HandshakeAnswerWatch.Token(HandshakeAnswerWatch.Status.Pending));
        Assert.Equal("asked", HandshakeAnswerWatch.Token(HandshakeAnswerWatch.Status.Pending));
        Assert.Equal("none", HandshakeAnswerWatch.Token(HandshakeAnswerWatch.Status.None));
        Assert.Equal("unverified", HandshakeAnswerWatch.Token(HandshakeAnswerWatch.Status.Unverified));
        Assert.Equal("ok", HandshakeAnswerWatch.Token(HandshakeAnswerWatch.Status.Ok));
        Assert.Equal("mismatch", HandshakeAnswerWatch.Token(HandshakeAnswerWatch.Status.Mismatch));
        Assert.Equal("timeout", HandshakeAnswerWatch.Token(HandshakeAnswerWatch.Status.Timeout));
    }

    [Fact]
    public void Unrequire_refreshes_pending_required_list_and_expire_cancels_when_nothing_remains()
    {
        // Fake clock + local list only. Do not read Handshake.CopyRequired at expire
        // time: that table is shared with HandshakeEvaluateTests even inside this
        // collection if a Require leaked. Handshake.Unrequire → live watch is
        // Unrequire_on_Handshake_AnswerWatch_updates_the_live_required_list.
        double t = 0;
        var watch = new HandshakeAnswerWatch(() => t);
        var arm = watch.Arm(1, "TestPlayer", 20, skipUser: false, serverRequiresAnswer: true,
            "a.mod 1.0 ServerMustHave/Exact, b.mod 1.0 ServerMustHave/Exact");
        Assert.Contains("a.mod", watch.RequiredListOf(1));
        Assert.Contains("b.mod", watch.RequiredListOf(1));

        const string remaining = "b.mod 1.0 ServerMustHave/Exact";
        watch.ReevaluateRequired(serverRequiresAnswer: true, remaining);
        Assert.Equal("asked", watch.Token(1));
        Assert.DoesNotContain("a.mod", watch.RequiredListOf(1));
        Assert.Contains("b.mod", watch.RequiredListOf(1));

        t = 20;
        Assert.True(watch.TryExpire(1, arm.Generation, serverStillRequiresAnswer: true, out var due));
        Assert.Contains("b.mod", due.Reason);
        Assert.DoesNotContain("a.mod", due.Reason);
        Assert.Equal("timeout", watch.Token(1));
    }

    [Fact]
    public void Unrequire_of_the_last_enforced_plugin_cancels_at_expire_as_unverified()
    {
        double t = 0;
        var watch = new HandshakeAnswerWatch(() => t);
        var arm = watch.Arm(1, "TestPlayer", 20, skipUser: false, serverRequiresAnswer: true,
            "sample.mod 1.0 ServerMustHave/Exact");
        watch.ReevaluateRequired(serverRequiresAnswer: false, "none");
        Assert.Equal("asked", watch.Token(1));
        Assert.Equal("none", watch.RequiredListOf(1));

        t = 20;
        Assert.False(watch.TryExpire(1, arm.Generation, serverStillRequiresAnswer: false, out _));
        Assert.Equal("unverified", watch.Token(1));
    }

    [Fact]
    public void Unrequire_on_Handshake_AnswerWatch_updates_the_live_required_list()
    {
        Handshake.Require("a.mod", "1.0");
        Handshake.Require("b.mod", "1.0");
        Handshake.AnswerWatch.Arm(7, "TestPlayer", 20, skipUser: false, serverRequiresAnswer: true, "stale");
        Assert.Equal("stale", Handshake.AnswerWatch.RequiredListOf(7));
        Handshake.Unrequire("a.mod");
        var list = Handshake.AnswerWatch.RequiredListOf(7);
        Assert.Contains("b.mod", list);
        Assert.DoesNotContain("a.mod", list);
        Assert.Equal("asked", Handshake.UserAnswerStatus(7));
    }

    [Fact]
    public void Mismatch_is_a_distinct_token_and_does_not_expire()
    {
        double t = 0;
        var watch = new HandshakeAnswerWatch(() => t);
        var arm = watch.Arm(1, "TestPlayer", 20, skipUser: false, serverRequiresAnswer: true, "sample 1.0 ServerMustHave/Exact");
        watch.OnMismatch(1);
        Assert.Equal("mismatch", watch.Token(1));
        Assert.True(watch.HasAnswered(1));
        t = 20;
        Assert.False(watch.TryExpire(1, arm.Generation, out _));
        Assert.Equal("mismatch", watch.Token(1));
    }

    [Fact]
    public void Token_values_are_none_asked_ok_mismatch_timeout_unverified()
    {
        Assert.Equal("none", HandshakeAnswerWatch.Token(HandshakeAnswerWatch.Status.None));
        Assert.Equal("asked", HandshakeAnswerWatch.Token(HandshakeAnswerWatch.Status.Pending));
        Assert.Equal("ok", HandshakeAnswerWatch.Token(HandshakeAnswerWatch.Status.Ok));
        Assert.Equal("mismatch", HandshakeAnswerWatch.Token(HandshakeAnswerWatch.Status.Mismatch));
        Assert.Equal("timeout", HandshakeAnswerWatch.Token(HandshakeAnswerWatch.Status.Timeout));
        Assert.Equal("unverified", HandshakeAnswerWatch.Token(HandshakeAnswerWatch.Status.Unverified));
    }

    [Fact]
    public void Answered_users_are_ok_or_mismatch()
    {
        var watch = new HandshakeAnswerWatch(() => 0);
        watch.Arm(1, "A", 20, skipUser: false, serverRequiresAnswer: true, "a 1.0 ServerMustHave/Exact");
        Assert.False(watch.HasAnswered(1));
        watch.OnAnswer(1);
        Assert.True(watch.HasAnswered(1));
        watch.OnMismatch(2);
        Assert.True(watch.HasAnswered(2));
        Assert.False(watch.HasAnswered(99));
    }

    [Fact]
    public void Reserved_guid_does_not_enforce()
    {
        Assert.False(Handshake.Compare.ServerRequiresAnswer(new[]
        {
            D(Handshake.MetaRecordGuid, "2", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact),
            D(Handshake.OverflowGuid, "1", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact),
        }));
    }

    private static void ScheduleCouldNotAsk(
        FakeDelay delay,
        HandshakeAnswerWatch watch,
        int userIndex,
        string name,
        List<string> results,
        List<string> disconnects,
        int timeoutSeconds = HandshakeAnswerWatch.DefaultTimeoutSeconds)
    {
        Handshake.Compare.ScheduleCouldNotAskKick(
            delay.Post,
            watch,
            userIndex,
            Handshake.Compare.CouldNotAskReason(),
            armDeadline: () => watch.Arm(
                userIndex,
                name,
                timeoutSeconds,
                skipUser: false,
                serverRequiresAnswer: true,
                "sample 1.0 ServerMustHave/Exact"),
            sendResult: results.Add,
            disconnect: disconnects.Add);
    }

    private static Handshake.ModRecord U(string guid, string version) =>
        Handshake.ModRecord.Undeclared(guid, version);

    private static Handshake.ModRecord D(
        string guid,
        string version,
        NetworkCompatibilityLevel level,
        NetworkVersionRule rule) =>
        Handshake.ModRecord.CreateDeclared(guid, version, level, rule);

    /// <summary>
    /// Drives <see cref="Handshake.Compare.ScheduleCouldNotAskKick"/> the way live
    /// <c>MainThread.PostDelayed</c> does: queue by due time, fire on advance.
    /// </summary>
    private sealed class FakeDelay
    {
        private double _now;
        private readonly List<(double due, Action action)> _queue = new();

        public double Now => _now;

        public void Post(double seconds, Action action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            _queue.Add((_now + seconds, action));
        }

        public void AdvanceTo(double t)
        {
            if (t < _now) throw new InvalidOperationException("clock cannot go backwards");
            _now = t;
            Drain();
        }

        private void Drain()
        {
            while (true)
            {
                int best = -1;
                double bestDue = 0;
                for (int i = 0; i < _queue.Count; i++)
                {
                    if (_queue[i].due > _now) continue;
                    if (best < 0 || _queue[i].due < bestDue)
                    {
                        best = i;
                        bestDue = _queue[i].due;
                    }
                }
                if (best < 0) return;
                var action = _queue[best].action;
                _queue.RemoveAt(best);
                action();
            }
        }
    }
}

/// <summary>
/// Serializes handshake-table tests. <see cref="HandshakeEvaluateTests"/> and
/// <see cref="HandshakeAnswerWatchTests"/> share <c>Handshake.Required</c> and
/// <c>RearmDeadlinesOnNextInstall</c>; xunit runs different classes in parallel.
/// </summary>
[CollectionDefinition("Handshake")]
public sealed class HandshakeCollection
{
}
