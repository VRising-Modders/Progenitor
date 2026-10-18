using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Progenitor.Inventory;
using Progenitor.Sample;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// The container open gate (<c>ctx.Inventory.OnOpenRequest</c>), game-free: how handlers combine (deny wins, then the
/// challenges), session grants and one-shot passes, the prompt sequence of one pending open, the packets (the typed text
/// rides only in an answer and never in a <c>ToString</c>), the client's repeat of a closed interaction, the preview's
/// hold, and the Sample's salted lock.
/// </summary>
public sealed class ContainerOpenGateTests
{
    private sealed record Req(string Name, bool Granted = false);

    private static readonly ContainerId Chest = ContainerId.New();
    private static readonly ContainerId Other = ContainerId.New();
    private const ulong Ada = 76561198000000001UL;
    private const ulong Bo = 76561198000000002UL;

    private static ContainerGateBook<Req> NewBook(List<string>? warnings = null)
    {
        var book = new ContainerGateBook<Req>();
        if (warnings != null) book.Warn = warnings.Add;
        return book;
    }

    private static ContainerGateOutcome<Req> Ask(ContainerGateBook<Req> book, ulong platform = Ada, ContainerId? container = null, double now = 0) =>
        book.Evaluate(new Req("open"), platform, container ?? Chest, now, (r, granted) => r with { Granted = granted });

    private static Func<Req, string, ChallengeSecret, bool, ContainerAnswerVerdict> Password(string right, bool remember = true) =>
        (_, _, secret, _) => secret.Reveal() == right ? ContainerAnswerVerdict.Accept(remember) : ContainerAnswerVerdict.Reject();

    // ---- combining handlers ------------------------------------------------------------------

    [Fact]
    public void With_no_handler_every_open_is_allowed()
    {
        Assert.Equal(ContainerOpenVerdict.Allow, Ask(NewBook()).Verdict);
    }

    [Fact]
    public void A_deny_wins_over_an_allow_and_a_challenge_and_names_the_first_denying_plugin()
    {
        var book = NewBook();
        book.Add(new object(), "a", "Allower", _ => ContainerOpenDecision.Allow, null);
        book.Add(new object(), "c", "Challenger", _ => ContainerOpenDecision.Challenge("t"), Password("x"));
        book.Add(new object(), "d", "Denier", _ => ContainerOpenDecision.Deny("Clan only."), null);
        book.Add(new object(), "e", "Second denier", _ => ContainerOpenDecision.Deny("Never."), null);
        var outcome = Ask(book);
        Assert.Equal(ContainerOpenVerdict.Deny, outcome.Verdict);
        Assert.Equal("Clan only.", outcome.Reason);
        Assert.Equal("Denier", outcome.By);
    }

    [Fact]
    public void Every_challenge_is_collected_in_registration_order()
    {
        var book = NewBook();
        book.Add(new object(), "one", "One", _ => ContainerOpenDecision.Challenge("t1", "First"), Password("1"));
        book.Add(new object(), "allow", "Allow", _ => ContainerOpenDecision.Allow, null);
        book.Add(new object(), "two", "Two", _ => ContainerOpenDecision.Challenge("t2", "Second"), Password("2"));
        var outcome = Ask(book);
        Assert.Equal(ContainerOpenVerdict.Challenge, outcome.Verdict);
        Assert.Equal(new[] { "One", "Two" }, outcome.Steps.Select(s => s.Handler.PluginName));
        Assert.Equal(new[] { "t1", "t2" }, outcome.Steps.Select(s => s.Decision.Token));
    }

    [Fact]
    public void A_handler_that_throws_refuses_the_open_and_is_logged()
    {
        var warnings = new List<string>();
        var book = NewBook(warnings);
        book.Add(new object(), "boom", "Boom", _ => throw new InvalidOperationException("no data"), null);
        var outcome = Ask(book);
        Assert.Equal(ContainerOpenVerdict.Deny, outcome.Verdict);
        Assert.Equal(ContainerGateText.CheckFailed, outcome.Reason);
        Assert.Contains(warnings, w => w.Contains("Boom") && w.Contains(nameof(InvalidOperationException)));
        Assert.DoesNotContain(warnings, w => w.Contains("no data"));                     // the type only, never the message
    }

    [Fact]
    public void A_challenge_without_an_answer_handler_refuses_and_warns_once()
    {
        var warnings = new List<string>();
        var book = NewBook(warnings);
        book.Add(new object(), "half", "Half", _ => ContainerOpenDecision.Challenge("t"), null);
        Assert.Equal(ContainerOpenVerdict.Deny, Ask(book).Verdict);
        Assert.Equal(ContainerOpenVerdict.Deny, Ask(book).Verdict);
        Assert.Single(warnings);
    }

    // ---- grants and passes -------------------------------------------------------------------

    [Fact]
    public void A_session_grant_answers_that_plugins_challenge_but_not_a_deny()
    {
        var book = NewBook();
        bool deny = false;
        book.Add(new object(), "lock", "Lock", r => deny ? ContainerOpenDecision.Deny("Banned.")
            : r.Granted ? ContainerOpenDecision.Challenge("t") : ContainerOpenDecision.Challenge("t"), Password("x"));
        book.Accepted(Ada, Chest, "lock", remember: true, now: 0);
        Assert.Equal(ContainerOpenVerdict.Allow, Ask(book, now: 100).Verdict);   // long after the pass expired: the grant
        Assert.Equal(ContainerOpenVerdict.Challenge, Ask(book, Bo).Verdict);    // another player
        Assert.Equal(ContainerOpenVerdict.Challenge, Ask(book, container: Other).Verdict); // another container
        deny = true;
        Assert.Equal(ContainerOpenVerdict.Deny, Ask(book).Verdict);
    }

    [Fact]
    public void The_request_tells_each_handler_whether_it_granted_this_pair()
    {
        var book = NewBook();
        bool? seenByLock = null, seenByOther = null;
        book.Add(new object(), "lock", "Lock", r => { seenByLock = r.Granted; return ContainerOpenDecision.Allow; }, null);
        book.Add(new object(), "other", "Other", r => { seenByOther = r.Granted; return ContainerOpenDecision.Allow; }, null);
        book.Accepted(Ada, Chest, "lock", remember: true, now: 0);
        Ask(book);
        Assert.True(seenByLock);
        Assert.False(seenByOther);
    }

    [Fact]
    public void A_pass_lets_the_rerun_through_once_and_expires()
    {
        var book = NewBook();
        book.Add(new object(), "lock", "Lock", _ => ContainerOpenDecision.Challenge("t"), Password("x", remember: false));
        book.Accepted(Ada, Chest, "lock", remember: false, now: 10);
        Assert.Equal(1, book.PassCount);
        Assert.Equal(ContainerOpenVerdict.Allow, Ask(book, now: 11).Verdict);
        Assert.Equal(0, book.PassCount);                                           // spent
        Assert.Equal(ContainerOpenVerdict.Challenge, Ask(book, now: 12).Verdict);

        book.Accepted(Ada, Chest, "lock", remember: false, now: 20);
        Assert.Equal(ContainerOpenVerdict.Challenge, Ask(book, now: 20 + ContainerGateBook<Req>.PassSeconds).Verdict);
        Assert.Equal(0, book.PassCount);                                           // expired
    }

    [Fact]
    public void A_pass_is_kept_when_the_open_is_still_refused_or_still_challenged()
    {
        var book = NewBook();
        bool deny = true;
        book.Add(new object(), "lock", "Lock", _ => ContainerOpenDecision.Challenge("t"), Password("x"));
        book.Add(new object(), "gate", "Gate", _ => deny ? ContainerOpenDecision.Deny("Closed.") : ContainerOpenDecision.Challenge("g"), Password("y"));
        book.Accepted(Ada, Chest, "lock", remember: false, now: 0);
        Assert.Equal(ContainerOpenVerdict.Deny, Ask(book).Verdict);
        Assert.Equal(1, book.PassCount);
        deny = false;
        var outcome = Ask(book);
        Assert.Equal(ContainerOpenVerdict.Challenge, outcome.Verdict);
        Assert.Equal("Gate", Assert.Single(outcome.Steps).Handler.PluginName);   // the answered lock is not asked again
        Assert.Equal(1, book.PassCount);
        book.Accepted(Ada, Chest, "gate", remember: false, now: 1);
        Assert.Equal(ContainerOpenVerdict.Allow, Ask(book, now: 2).Verdict);
        Assert.Equal(0, book.PassCount);
    }

    [Fact]
    public void A_disconnect_clears_that_players_grants_and_passes_only()
    {
        var book = NewBook();
        book.Add(new object(), "lock", "Lock", _ => ContainerOpenDecision.Challenge("t"), Password("x"));
        book.Accepted(Ada, Chest, "lock", remember: true, now: 0);
        book.Accepted(Bo, Chest, "lock", remember: true, now: 0);
        Assert.Equal(2, book.ForgetPlayer(Ada));                                    // Ada's grant and pass
        Assert.False(book.HasGrant(Ada, Chest, "lock"));
        Assert.True(book.HasGrant(Bo, Chest, "lock"));
    }

    [Fact]
    public void Revoking_a_container_or_a_player_drops_only_that_plugins_rows()
    {
        var book = NewBook();
        book.Accepted(Ada, Chest, "lock", remember: true, now: 0);
        book.Accepted(Ada, Chest, "other", remember: true, now: 0);
        book.Accepted(Ada, Other, "lock", remember: true, now: 0);
        Assert.Equal(2, book.RevokeContainer("lock", Chest));
        Assert.False(book.HasGrant(Ada, Chest, "lock"));
        Assert.True(book.HasGrant(Ada, Chest, "other"));
        Assert.True(book.HasGrant(Ada, Other, "lock"));
        Assert.Equal(2, book.RevokePlayer("lock", Ada));
        Assert.False(book.HasGrant(Ada, Other, "lock"));
    }

    [Fact]
    public void Removing_a_plugins_last_handler_drops_its_grants()
    {
        var book = NewBook();
        var first = book.Add(new object(), "lock", "Lock", _ => ContainerOpenDecision.Allow, null);
        var second = book.Add(new object(), "lock", "Lock", _ => ContainerOpenDecision.Allow, null);
        book.Accepted(Ada, Chest, "lock", remember: true, now: 0);
        Assert.True(book.Remove(first));
        Assert.True(book.HasGrant(Ada, Chest, "lock"));                             // another handler of the plugin is left
        Assert.True(book.Remove(second));
        Assert.False(book.HasGrant(Ada, Chest, "lock"));
        Assert.True(second.Removed);
        Assert.False(book.Remove(second));
        Assert.Equal(0, book.Count);
    }

    // ---- one pending open --------------------------------------------------------------------

    private static ContainerGatePending<Req> Pending(ContainerGateBook<Req> book, int sequence = 7, double now = 0)
    {
        var outcome = Ask(book, now: now);
        Assert.Equal(ContainerOpenVerdict.Challenge, outcome.Verdict);
        return new ContainerGatePending<Req>(sequence, Ada, Chest, ContainerOpenKind.Interact, new Req("open"), outcome.Steps, now);
    }

    private static ChallengeSecret Typed(string text) => new(text);

    [Fact]
    public void A_right_answer_records_the_pass_and_the_grant_and_ends_the_prompts()
    {
        var book = NewBook();
        book.Add(new object(), "lock", "Lock", _ => ContainerOpenDecision.Challenge("t"), Password("sesame"));
        var p = Pending(book);
        Assert.Equal(ContainerGateAnswerStep.Done, p.Answer(7, Typed("sesame"), book, 1, out string reason));
        Assert.Equal("", reason);
        Assert.True(book.HasGrant(Ada, Chest, "lock"));
        Assert.Equal(1, book.PassCount);
    }

    [Fact]
    public void A_wrong_answer_fails_with_the_handlers_sentence_and_grants_nothing()
    {
        var book = NewBook();
        book.Add(new object(), "lock", "Lock", _ => ContainerOpenDecision.Challenge("t"),
            (_, _, s, _) => s.Reveal() == "sesame" ? ContainerAnswerVerdict.Accept() : ContainerAnswerVerdict.Reject("Nope."));
        var p = Pending(book);
        Assert.Equal(ContainerGateAnswerStep.Failed, p.Answer(7, Typed("open"), book, 1, out string reason));
        Assert.Equal("Nope.", reason);
        Assert.False(book.HasGrant(Ada, Chest, "lock"));
        Assert.Equal(0, book.PassCount);
    }

    [Fact]
    public void Two_locks_are_asked_in_turn_and_each_answer_reaches_its_own_handler_with_its_token()
    {
        var book = NewBook();
        var seen = new List<string>();
        book.Add(new object(), "one", "One", _ => ContainerOpenDecision.Challenge("t1"), (_, token, s, _) => { seen.Add(token); return s.Reveal() == "1" ? ContainerAnswerVerdict.Accept() : ContainerAnswerVerdict.Reject(); });
        book.Add(new object(), "two", "Two", _ => ContainerOpenDecision.Challenge("t2"), (_, token, s, _) => { seen.Add(token); return s.Reveal() == "2" ? ContainerAnswerVerdict.Accept() : ContainerAnswerVerdict.Reject(); });
        var p = Pending(book);
        Assert.Equal("One", p.Current.Handler.PluginName);
        Assert.Equal(ContainerGateAnswerStep.Next, p.Answer(7, Typed("1"), book, 1, out _));
        Assert.Equal("Two", p.Current.Handler.PluginName);
        Assert.Equal(ContainerGateAnswerStep.Done, p.Answer(7, Typed("2"), book, 2, out _));
        Assert.Equal(new[] { "t1", "t2" }, seen);
        Assert.Equal(ContainerOpenVerdict.Allow, Ask(book, now: 3).Verdict);       // both answered: the rerun opens
    }

    [Fact]
    public void An_old_sequence_is_ignored()
    {
        var book = NewBook();
        book.Add(new object(), "lock", "Lock", _ => ContainerOpenDecision.Challenge("t"), Password("x"));
        var p = Pending(book);
        Assert.Equal(ContainerGateAnswerStep.Ignored, p.Answer(6, Typed("x"), book, 1, out _));
        Assert.Equal(0, book.PassCount);
    }

    [Fact]
    public void An_answer_for_a_removed_handler_or_one_that_throws_fails_closed()
    {
        var warnings = new List<string>();
        var book = NewBook(warnings);
        var h = book.Add(new object(), "lock", "Lock", _ => ContainerOpenDecision.Challenge("t"), Password("x"));
        var p = Pending(book);
        Assert.True(p.Owns(h));
        book.Remove(h);
        Assert.Equal(ContainerGateAnswerStep.Failed, p.Answer(7, Typed("x"), book, 1, out string reason));
        Assert.Equal(ContainerGateText.Changed, reason);

        book.Add(new object(), "boom", "Boom", _ => ContainerOpenDecision.Challenge("t"), (_, _, _, _) => throw new InvalidOperationException("store down"));
        var q = Pending(book, sequence: 8);
        Assert.Equal(ContainerGateAnswerStep.Failed, q.Answer(8, Typed("x"), book, 1, out reason));
        Assert.Equal(ContainerGateText.CheckFailed, reason);
        Assert.Contains(warnings, w => w.Contains(nameof(InvalidOperationException)));
        Assert.DoesNotContain(warnings, w => w.Contains("store down"));                  // an answer handler's message may quote the typed text
        Assert.DoesNotContain(warnings, w => w.Contains("\"x\""));
    }

    [Fact]
    public void An_answered_prompt_still_belongs_to_its_handler_so_an_unload_ends_the_open()
    {
        var book = NewBook();
        var one = book.Add(new object(), "one", "One", _ => ContainerOpenDecision.Challenge("t1"), Password("1"));
        var two = book.Add(new object(), "two", "Two", _ => ContainerOpenDecision.Challenge("t2"), Password("2"));
        var p = Pending(book);
        Assert.Equal(ContainerGateAnswerStep.Next, p.Answer(7, Typed("1"), book, 1, out _));
        Assert.True(p.Owns(one));                                                   // answered, still referenced
        Assert.True(p.Owns(two));
        Assert.False(p.Owns(book.Add(new object(), "three", "Three", _ => ContainerOpenDecision.Allow, null)));
    }

    [Fact]
    public void A_refused_station_interactions_extra_buff_is_watched_for_a_second_and_an_allowed_one_ends_the_watch()
    {
        var extras = new ContainerGateExtras();
        extras.Add(character: 11, prefab: 500, instance: 0, now: 0);
        extras.Add(character: 12, prefab: 501, instance: 99, now: 0);
        Assert.Equal(2, extras.Live().Count);
        extras.Expire(ContainerGateExtras.Seconds - 0.1);
        Assert.Equal(2, extras.Count);
        extras.ForgetCharacter(11);
        Assert.Equal(12, Assert.Single(extras.Live()).Character);
        extras.Expire(ContainerGateExtras.Seconds);
        Assert.Equal(0, extras.Count);
    }

    [Fact]
    public void A_prompt_expires_after_its_wait_and_each_answer_restarts_it()
    {
        var book = NewBook();
        book.Add(new object(), "one", "One", _ => ContainerOpenDecision.Challenge("t1"), Password("1"));
        book.Add(new object(), "two", "Two", _ => ContainerOpenDecision.Challenge("t2"), Password("2"));
        var p = Pending(book, now: 0);
        Assert.False(p.Expired(ContainerGatePending<Req>.PromptSeconds - 1));
        Assert.Equal(ContainerGateAnswerStep.Next, p.Answer(7, Typed("1"), book, 100, out _));
        Assert.False(p.Expired(100 + ContainerGatePending<Req>.PromptSeconds - 1));
        Assert.True(p.Expired(100 + ContainerGatePending<Req>.PromptSeconds));
    }

    [Fact]
    public void A_pending_open_needs_a_prompt()
    {
        Assert.Throws<ArgumentException>(() => new ContainerGatePending<Req>(1, Ada, Chest, ContainerOpenKind.Interact, new Req("x"), new List<ContainerGateStep<Req>>(), 0));
    }

    // ---- decisions and secrets ---------------------------------------------------------------

    [Fact]
    public void A_secret_never_prints_its_text()
    {
        var s = Typed("hunter2");
        Assert.Equal("(hidden)", s.ToString());
        Assert.Equal("(hidden)", $"{s}");
        Assert.Equal("hunter2", s.Reveal());
        Assert.Equal(7, s.Length);
        Assert.True(Typed("").IsEmpty);
        Assert.Equal("", default(ChallengeSecret).Reveal());
    }

    [Fact]
    public void Decisions_clip_their_text_and_fall_back_to_defaults()
    {
        Assert.Equal(ContainerOpenVerdict.Allow, default(ContainerOpenDecision).Verdict);
        Assert.Equal(ContainerGateText.Locked, ContainerOpenDecision.Deny("  ").Reason);
        Assert.Equal(ContainerOpenDecision.MaxText, ContainerOpenDecision.Deny(new string('a', 500)).Reason.Length);
        var c = ContainerOpenDecision.Challenge(new string('t', 1000), "", "", maxLength: 0);
        Assert.Equal(ContainerOpenDecision.MaxToken, c.Token.Length);
        Assert.Equal("Password", c.Title);
        Assert.Equal(ContainerOpenDecision.DefaultMaxLength, c.MaxLength);
        Assert.Equal(ContainerOpenDecision.DefaultMaxLength, ContainerOpenDecision.Challenge("t", maxLength: 999).MaxLength);
        Assert.Equal(12, ContainerOpenDecision.Challenge("t", maxLength: 12).MaxLength);
        Assert.Equal(ContainerGateText.Wrong, ContainerAnswerVerdict.Reject("").Reason);
        Assert.False(ContainerAnswerVerdict.Reject().RememberForSession);
        Assert.True(ContainerAnswerVerdict.Accept().RememberForSession);
        Assert.False(ContainerAnswerVerdict.Accept(rememberForSession: false).RememberForSession);
        Assert.DoesNotContain("secret-token", ContainerOpenDecision.Challenge("secret-token", "Lock").ToString());
    }

    // ---- packets -----------------------------------------------------------------------------

    private static ContainerGatePacket? RoundTrip(ContainerGatePacket p, bool fromClient) =>
        ContainerGateWire.Decode(ContainerGateWire.Encode(p), fromClient);

    [Fact]
    public void Packets_round_trip_in_their_own_direction_only()
    {
        var prompt = new ContainerGatePacket { Kind = ContainerGateWire.KindPrompt, Sequence = 3, Container = Chest.ToString(), Title = "Locked", MaxLength = 32, Mod = "Lock" };
        var back = RoundTrip(prompt, fromClient: false);
        Assert.NotNull(back);
        Assert.Equal(("Locked", 32, "Lock"), (back!.Title, back.MaxLength, back.Mod));
        Assert.Null(RoundTrip(prompt, fromClient: true));

        var answer = new ContainerGatePacket { Kind = ContainerGateWire.KindAnswer, Sequence = 3, Text = "sesame" };
        Assert.Equal("sesame", RoundTrip(answer, fromClient: true)!.Text);
        Assert.Null(RoundTrip(answer, fromClient: false));
        Assert.NotNull(RoundTrip(new ContainerGatePacket { Kind = ContainerGateWire.KindCancel, Sequence = 3 }, fromClient: true));
        Assert.NotNull(RoundTrip(new ContainerGatePacket { Kind = ContainerGateWire.KindDenied, Message = "No." }, fromClient: false));
    }

    [Fact]
    public void Malformed_packets_are_dropped()
    {
        Assert.Null(ContainerGateWire.Decode(Encoding.UTF8.GetBytes("{not json"), fromClient: true));
        Assert.Null(ContainerGateWire.Decode(ReadOnlySpan<byte>.Empty, fromClient: true));
        Assert.Null(ContainerGateWire.Decode(new byte[ContainerGateWire.MaxBytes + 1], fromClient: true));
        Assert.Null(RoundTrip(new ContainerGatePacket { Kind = ContainerGateWire.KindAnswer, Sequence = 0, Text = "x" }, true));          // no sequence
        Assert.Null(RoundTrip(new ContainerGatePacket { Kind = ContainerGateWire.KindAnswer, Sequence = 1, Text = new string('x', 129) }, true));
        Assert.Null(RoundTrip(new ContainerGatePacket { Kind = ContainerGateWire.KindCancel, Sequence = 1, Text = "x" }, true));          // text outside an answer
        Assert.Null(RoundTrip(new ContainerGatePacket { Kind = ContainerGateWire.KindPrompt, Sequence = 1, MaxLength = 0 }, false));
        Assert.Null(RoundTrip(new ContainerGatePacket { Kind = ContainerGateWire.KindPrompt, Sequence = 1, MaxLength = 32, Version = 2 }, false));
        Assert.Null(RoundTrip(new ContainerGatePacket { Kind = "open", Sequence = 1 }, true));
    }

    [Fact]
    public void A_packets_text_never_shows_in_its_ToString()
    {
        var answer = new ContainerGatePacket { Kind = ContainerGateWire.KindAnswer, Sequence = 3, Text = "hunter2" };
        Assert.DoesNotContain("hunter2", answer.ToString());
        Assert.Contains("7 characters", answer.ToString());
    }

    // ---- Remember password -------------------------------------------------------------------

    [Fact]
    public void The_remember_box_reaches_the_handler_only_when_the_challenge_offered_it()
    {
        var book = NewBook();
        var seen = new List<bool>();
        bool offer = true;
        book.Add(new object(), "lock", "Lock", _ => ContainerOpenDecision.Challenge("t", offerRemember: offer),
            (_, _, _, remember) => { seen.Add(remember); return ContainerAnswerVerdict.Accept(rememberForSession: false); });
        Assert.True(Ask(book).Steps[0].Decision.OfferRemember);
        Assert.Equal(ContainerGateAnswerStep.Done, Pending(book).Answer(7, Typed("x"), book, 1, out _, remember: true));
        Assert.Equal(ContainerGateAnswerStep.Done, Pending(book, now: 100).Answer(7, Typed("x"), book, 101, out _, remember: false));
        offer = false;
        Assert.Equal(ContainerGateAnswerStep.Done, Pending(book, now: 200).Answer(7, Typed("x"), book, 201, out _, remember: true));
        Assert.Equal(new[] { true, false, false }, seen);
        Assert.Equal(0, book.GrantCount);                                              // the gate stores no grant for it
    }

    [Fact]
    public void The_remember_flag_round_trips_in_a_prompt_and_an_answer()
    {
        var prompt = new ContainerGatePacket { Kind = ContainerGateWire.KindPrompt, Sequence = 3, MaxLength = 32, Remember = true };
        Assert.True(RoundTrip(prompt, fromClient: false)!.Remember);
        var answer = new ContainerGatePacket { Kind = ContainerGateWire.KindAnswer, Sequence = 3, Text = "x", Remember = true };
        Assert.True(RoundTrip(answer, fromClient: true)!.Remember);
        Assert.False(ContainerOpenDecision.Challenge("t").OfferRemember);
    }

    // ---- one decision per interaction ----------------------------------------------------------

    [Fact]
    public void A_recent_decision_is_reused_for_the_same_pair_for_half_a_second()
    {
        var recent = new ContainerGateRecent();
        Assert.False(recent.TryGet(Ada, Chest, 0, out _));
        recent.Record(Ada, Chest, allowed: false, now: 0);
        Assert.True(recent.TryGet(Ada, Chest, 0.4, out bool allowed));
        Assert.False(allowed);
        Assert.False(recent.TryGet(Bo, Chest, 0.4, out _));
        Assert.False(recent.TryGet(Ada, Other, 0.4, out _));
        Assert.False(recent.TryGet(Ada, Chest, ContainerGateRecent.Seconds, out _));      // expired
        recent.Record(Ada, Chest, allowed: true, now: 1);
        recent.Forget(Ada);                                                                // a right answer
        Assert.False(recent.TryGet(Ada, Chest, 1.1, out _));
    }

    // ---- the client's repeat -----------------------------------------------------------------

    [Fact]
    public void The_repeat_hovers_presses_once_and_ends()
    {
        var plan = new ContainerGateRetryPlan(0);
        for (int i = 1; i < ContainerGateRetryPlan.HoverFrames; i++)
            Assert.Equal(ContainerGateRetryStep.Hover, plan.Step(i * 0.016, windowOpen: false));
        Assert.Equal(ContainerGateRetryStep.Press, plan.Step(0.05, false));
        Assert.True(plan.Pressed);
        Assert.Equal(ContainerGateRetryStep.Hover, plan.Step(0.1, false));
        Assert.Equal(ContainerGateRetryStep.End, plan.Step(0.2, windowOpen: true));
    }

    [Fact]
    public void The_repeat_gives_up_after_its_time_and_an_early_window_does_not_end_it_before_the_press()
    {
        var plan = new ContainerGateRetryPlan(0);
        Assert.Equal(ContainerGateRetryStep.Hover, plan.Step(0.01, windowOpen: true));
        Assert.Equal(ContainerGateRetryStep.End, plan.Step(ContainerGateRetryPlan.TotalSeconds, false));
    }

    // ---- the preview's hold ------------------------------------------------------------------

    [Fact]
    public void A_hold_extends_the_wait_for_the_reply_and_a_timed_preview_counts_from_the_reply()
    {
        var m = new ContainerPreviewMachine(new ContainerPreviewOptions { Seconds = 30 }, 0);
        Assert.True(m.Hold(1, 120));
        Assert.True(m.Held);
        Assert.Equal(ContainerPreviewStep.None, m.Step(ContainerPreviewMachine.RequestTimeoutSeconds + 1, default));
        Assert.Equal(ContainerPreviewStep.None, m.Step(120, default));
        Assert.True(m.OnReply(60, ok: true, ""));
        Assert.Equal(90, m.Deadline);
        Assert.False(m.Hold(61, 120));                                              // only while unanswered
    }

    [Fact]
    public void A_hold_is_capped_and_ignores_nonsense()
    {
        var m = new ContainerPreviewMachine(new ContainerPreviewOptions(), 0);
        Assert.False(m.Hold(0, double.NaN));
        Assert.False(m.Hold(0, -5));
        Assert.True(m.Hold(0, 10_000));
        Assert.Equal(ContainerPreviewStep.Closed, m.Step(ContainerPreviewMachine.MaxHoldSeconds, default));
        Assert.Equal(ContainerPreviewCloseReason.NoAnswer, m.CloseReason);
    }

    [Fact]
    public void The_hold_packet_decodes_as_an_answer_kind()
    {
        var bytes = ContainerPreviewWire.Encode(new ContainerPreviewPacket { Kind = ContainerPreviewWire.KindHold, Sequence = 2, Container = Chest.ToString(), Seconds = 128 });
        var p = ContainerPreviewWire.Decode(bytes, expectRequest: false);
        Assert.NotNull(p);
        Assert.Equal(128, p!.Seconds);
        Assert.Null(ContainerPreviewWire.Decode(bytes, expectRequest: true));
    }

    // ---- the Sample's lock -------------------------------------------------------------------

    [Fact]
    public void The_sample_lock_keeps_a_salted_hash_and_checks_it()
    {
        var a = SampleLocks.Make("sesame");
        var b = SampleLocks.Make("sesame");
        Assert.Equal(16, a.Salt.Length);
        Assert.False(a.Hash.SequenceEqual(b.Hash));                                 // a fresh salt each time
        Assert.True(SampleLocks.Matches(a, "sesame"));
        Assert.False(SampleLocks.Matches(a, "Sesame"));
        Assert.False(SampleLocks.Matches(a, ""));
    }

    [Fact]
    public void The_sample_lock_challenges_a_locked_container_and_accepts_its_password()
    {
        var id = ContainerId.New();
        try
        {
            Assert.Equal(ContainerOpenVerdict.Allow, SampleLocks.Decide(Ada, id).Verdict);
            SampleLocks.Lock(id, "sesame");
            var d = SampleLocks.Decide(Ada, id);
            Assert.Equal(ContainerOpenVerdict.Challenge, d.Verdict);
            Assert.Equal(id.ToString(), d.Token);
            Assert.True(d.OfferRemember);
            Assert.True(SampleLocks.Answer(Ada, id, "sesame", remember: false).Accepted);
            Assert.True(SampleLocks.Answer(Ada, id, "sesame", remember: false).RememberForSession);
            Assert.Equal(ContainerOpenVerdict.Challenge, SampleLocks.Decide(Ada, id).Verdict);      // not remembered
            Assert.False(SampleLocks.Answer(Ada, id, "nope", remember: true).Accepted);
            Assert.Equal(ContainerOpenVerdict.Challenge, SampleLocks.Decide(Ada, id).Verdict);      // a wrong answer remembers nothing
            Assert.True(SampleLocks.Answer(Ada, id, "sesame", remember: true).Accepted);
            Assert.Equal(ContainerOpenVerdict.Allow, SampleLocks.Decide(Ada, id).Verdict);          // the plugin's own grant
            Assert.Equal(ContainerOpenVerdict.Challenge, SampleLocks.Decide(Bo, id).Verdict);
            SampleLocks.Lock(id, "new");                                                             // a new password forgets it
            Assert.Equal(ContainerOpenVerdict.Challenge, SampleLocks.Decide(Ada, id).Verdict);
            Assert.True(SampleLocks.Unlock(id));
            Assert.False(SampleLocks.Answer(Ada, id, "new", remember: false).Accepted);
        }
        finally { SampleLocks.Unlock(id); }
    }
}
