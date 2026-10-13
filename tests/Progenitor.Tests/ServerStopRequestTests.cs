using System;
using System.Collections.Generic;
using Progenitor.Install;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// F20/F21: the Install tab's one stop request. Fakes only: no packet leaves, no process is read.
/// Each test names the revert that breaks it.
/// </summary>
public sealed class ServerStopRequestTests
{
    private DateTime _now = new(2026, 10, 7, 23, 0, 0, DateTimeKind.Utc);
    private readonly List<int> _sent = new();
    private StopServerReply? _reply;
    private bool _gone;

    private ServerStopRequest Flow(bool sendOk = true) => new()
    {
        Send = nonce => { _sent.Add(nonce); return sendOk; },
        Poll = nonce => _reply is { } r && r.Nonce == nonce ? r : null,
        Gone = () => _gone,
        Clock = () => _now,
    };

    [Fact]
    public void Begin_sends_once_and_a_second_begin_while_in_flight_sends_nothing()
    {
        var log = new List<string>();
        var flow = Flow();
        flow.Log = log.Add;
        Assert.True(flow.Begin("Test Server", ""));
        Assert.False(flow.Begin("Test Server", ""));
        Assert.Single(_sent);
        Assert.Equal(StopRequestPhase.Waiting, flow.Phase);
        Assert.Contains(log, l => l.Contains("still in flight"));
        // Revert: drop the InFlight guard and the live double click sends two requests.
    }

    [Fact]
    public void A_refusal_ends_the_request_with_its_reason()
    {
        var flow = Flow();
        flow.Begin("Test Server", "");
        _reply = StopServerReply.For(_sent[0], accepted: false, StopReplyReason.NotAdmin);
        Assert.True(flow.Tick());
        Assert.Equal(StopRequestPhase.Refused, flow.Phase);
        Assert.False(flow.InFlight);
        Assert.Equal("Test Server refused: " + StopServerEvents.RefusalText(StopReplyReason.NotAdmin), flow.Line);
        // A new press is allowed again.
        Assert.True(flow.Begin("Test Server", ""));
    }

    [Fact]
    public void A_reply_for_another_nonce_is_not_this_requests_answer()
    {
        var flow = Flow();
        flow.Begin("Test Server", "");
        _reply = StopServerReply.For(_sent[0] + 100, accepted: false, StopReplyReason.NotAdmin);
        Assert.False(flow.Tick());
        Assert.Equal(StopRequestPhase.Waiting, flow.Phase);
    }

    [Fact]
    public void Accepted_then_gone_is_stopped_and_says_how_it_starts_again()
    {
        var flow = Flow();
        flow.Begin("Test Server", "Start it again yourself.");
        _reply = StopServerReply.For(_sent[0], accepted: true, StopReplyReason.Saving);
        Assert.True(flow.Tick());
        Assert.Equal(StopRequestPhase.Stopping, flow.Phase);
        Assert.True(flow.InFlight);
        _now = _now.AddSeconds(ServerStopRequest.AnswerTimeoutSeconds + 5);
        Assert.False(flow.Tick()); // accepted: the answer timeout no longer applies
        _gone = true;
        Assert.True(flow.Tick());
        Assert.Equal(StopRequestPhase.Stopped, flow.Phase);
        Assert.Equal("Test Server stopped. Start it again yourself.", flow.Line);
        Assert.Equal(new[] { "waiting", "stopping", "stopped" }, flow.States.ToArray());
    }

    [Fact]
    public void A_save_timeout_after_acceptance_is_a_refusal()
    {
        var flow = Flow();
        flow.Begin("Test Server", "");
        _reply = StopServerReply.For(_sent[0], accepted: true, StopReplyReason.Saving);
        flow.Tick();
        _reply = StopServerReply.For(_sent[0], accepted: false, StopReplyReason.SaveTimeout);
        flow.Tick();
        Assert.Equal(StopRequestPhase.Refused, flow.Phase);
        Assert.Contains("no save file appeared", flow.Line);
    }

    [Fact]
    public void Silence_ends_at_no_answer_and_an_accepted_stop_that_never_exits_at_still_running()
    {
        var flow = Flow();
        flow.Begin("Test Server", "");
        _now = _now.AddSeconds(ServerStopRequest.AnswerTimeoutSeconds);
        Assert.True(flow.Tick());
        Assert.Equal(StopRequestPhase.NoAnswer, flow.Phase);
        Assert.StartsWith(ServerStopRequest.NoAnswerLine, flow.Line);

        flow.Begin("Test Server", "");
        _reply = StopServerReply.For(_sent[1], accepted: true, StopReplyReason.Saving);
        flow.Tick();
        _now = _now.AddSeconds(ServerStopRequest.ExitTimeoutSeconds);
        Assert.True(flow.Tick());
        Assert.Equal(StopRequestPhase.StillRunning, flow.Phase);
        // Revert: drop either timeout and the button stays disabled with "Stopping…" for good.
    }

    [Fact]
    public void A_transport_that_refuses_is_not_sent_and_does_not_lock_the_button()
    {
        var flow = Flow(sendOk: false);
        Assert.False(flow.Begin("Test Server", ""));
        Assert.Equal(StopRequestPhase.NotSent, flow.Phase);
        Assert.False(flow.InFlight);
        Assert.Equal("The stop request could not be sent to Test Server.", flow.Line);
    }

    [Fact]
    public void A_probe_that_throws_never_ends_a_request_early()
    {
        var flow = Flow();
        flow.Gone = () => throw new InvalidOperationException("no world");
        flow.Begin("Test Server", "");
        Assert.False(flow.Tick());
        Assert.Equal(StopRequestPhase.Waiting, flow.Phase);
    }
}
