using System;

namespace Progenitor.Install;

/// <summary>Where one Install-tab stop request stands (F20).</summary>
internal enum StopRequestPhase
{
    /// <summary>Nothing asked yet, or the last outcome was cleared.</summary>
    Idle = 0,

    /// <summary>The request left; no answer yet.</summary>
    Waiting = 1,

    /// <summary>The server accepted: it is saving, then it quits.</summary>
    Stopping = 2,

    /// <summary>The server is gone (its process exited, or this client lost the connection to it).</summary>
    Stopped = 3,

    /// <summary>The server answered with a refusal; it is still running.</summary>
    Refused = 4,

    /// <summary>No answer within <see cref="ServerStopRequest.AnswerTimeoutSeconds"/>.</summary>
    NoAnswer = 5,

    /// <summary>The server accepted but was still there after <see cref="ServerStopRequest.ExitTimeoutSeconds"/>.</summary>
    StillRunning = 6,

    /// <summary>The request could not be sent at all.</summary>
    NotSent = 7,
}

/// <summary>
/// F20 (live 2026-10-07): the Install tab's one stop request at a time. The pending bar's button used
/// to fire the request and forget it — no confirmation, no answer read, a second click sent a second
/// request — so a refusal looked exactly like nothing happening. This holds the request in flight,
/// reads the server's <see cref="StopServerReply"/> for its nonce, and ends on the answer, the server
/// going away, or a timeout, with a sentence for each outcome.
///
/// <para>Game-free: the transport, the reply inbox, the "is it gone" probe and the clock are seams.
/// Nothing here starts, stops or signals a process; the stop itself is the server's own save and
/// quit (<see cref="StopServerEvents"/>).</para>
/// </summary>
internal sealed class ServerStopRequest
{
    /// <summary>Seconds without any answer before the footer says the server did not answer.</summary>
    public const double AnswerTimeoutSeconds = 10;

    /// <summary>
    /// Seconds after an accepted answer before "still running" is said: the server's own save
    /// confirmation waits up to 15 s, then the quit, then the process teardown.
    /// </summary>
    public const double ExitTimeoutSeconds = 45;

    /// <summary>The footer line when nothing answered (the wording F21 asked for).</summary>
    public const string NoAnswerLine = "The server did not answer the stop request.";

    /// <summary>Send the request with this nonce; false when it could not leave.</summary>
    public Func<int, bool>? Send { get; set; }

    /// <summary>The reply for this nonce, or null while none has arrived.</summary>
    public Func<int, StopServerReply?>? Poll { get; set; }

    /// <summary>True once the server is gone. Null (or a throw) means "cannot tell", which never ends a request early.</summary>
    public Func<bool>? Gone { get; set; }

    public Func<DateTime>? Clock { get; set; }

    /// <summary>One line per step, for the client log.</summary>
    public Action<string>? Log { get; set; }

    /// <summary>
    /// F22: raised once when the server accepts the stop (live: <c>JoinFlowClient.ArmRejoinWhenBack</c>). F37: the argument is
    /// <see cref="RestartsAgain"/>, so the rejoin names the restart and need not ask.
    /// </summary>
    public static Action<bool>? Accepted { get; set; }

    /// <summary>F22 review (M3): raised when an accepted or sent stop did not happen (live: <c>JoinFlowClient.DisarmRejoinWhenBack</c>).</summary>
    public static Action? Abandoned { get; set; }

    public StopRequestPhase Phase { get; private set; }

    public int Nonce { get; private set; }

    /// <summary>The server's name as the footer says it.</summary>
    public string ServerName { get; private set; } = "";

    /// <summary>The refusal reason, when <see cref="Phase"/> is <see cref="StopRequestPhase.Refused"/>.</summary>
    public StopReplyReason Reason { get; private set; }

    /// <summary>What to say after "stopped": how this server starts again.</summary>
    public string StartAgain { get; private set; } = "";

    /// <summary>F37: this stop is the player's own "Save, stop and start again": Progenitor starts the server again after it.</summary>
    public bool RestartsAgain { get; private set; }

    /// <summary>Every phase the current request passed through, in order (the readback).</summary>
    public System.Collections.Generic.List<string> States { get; } = new();

    private DateTime _sentAt;
    private DateTime _acceptedAt;

    /// <summary>A request is out and has not ended: the button stays disabled.</summary>
    public bool InFlight => Phase is StopRequestPhase.Waiting or StopRequestPhase.Stopping;

    /// <summary>
    /// Send one request. False, and nothing sent, while one is already in flight. Otherwise the
    /// phase is <see cref="StopRequestPhase.Waiting"/>, or <see cref="StopRequestPhase.NotSent"/>
    /// when the transport refused.
    /// </summary>
    public bool Begin(string? serverName, string? startAgain, bool restartsAgain = false)
    {
        if (InFlight)
        {
            Write("StopServer: request " + Nonce + " is still in flight; a second press sent nothing.");
            return false;
        }

        ServerName = string.IsNullOrWhiteSpace(serverName) ? "The server" : serverName!.Trim();
        StartAgain = startAgain ?? "";
        RestartsAgain = restartsAgain;
        Reason = StopReplyReason.None;
        States.Clear();
        Nonce = StopServerEvents.NextNonce();
        bool sent;
        try { sent = Send?.Invoke(Nonce) ?? false; }
        catch { sent = false; }
        _sentAt = Now();
        Enter(sent ? StopRequestPhase.Waiting : StopRequestPhase.NotSent);
        Write("StopServer: request " + Nonce + " to " + ServerName + (sent ? " sent; waiting for its answer." : " could not be sent."));
        return sent;
    }

    /// <summary>Read the answer and the probe once. True when the phase changed.</summary>
    public bool Tick()
    {
        if (!InFlight) return false;
        var before = Phase;
        StopServerReply? reply = null;
        try { reply = Poll?.Invoke(Nonce); }
        catch { reply = null; }
        if (reply is { } r)
        {
            if (!r.IsAccepted)
            {
                Reason = r.Reason;
                Enter(StopRequestPhase.Refused);
                Write("StopServer: request " + Nonce + " refused by " + ServerName + ": " + StopServerEvents.ReasonWord(r.Reason) + ".");
                return true;
            }

            if (Phase == StopRequestPhase.Waiting)
            {
                _acceptedAt = Now();
                Enter(StopRequestPhase.Stopping);
                Write("StopServer: request " + Nonce + " accepted by " + ServerName + "; it is saving, then stopping.");
                // F22: the connection that ends with this stop is offered a rejoin when the server is back.
                try { Accepted?.Invoke(RestartsAgain); }
                catch { /* the offer is a convenience */ }
            }
        }

        bool gone;
        try { gone = Gone?.Invoke() ?? false; }
        catch { gone = false; }
        if (gone)
        {
            Enter(StopRequestPhase.Stopped);
            Write("StopServer: " + ServerName + " is gone after request " + Nonce + ".");
            return true;
        }

        DateTime now = Now();
        if (Phase == StopRequestPhase.Waiting && (now - _sentAt).TotalSeconds >= AnswerTimeoutSeconds)
        {
            Enter(StopRequestPhase.NoAnswer);
            Write("StopServer: no answer to request " + Nonce + " from " + ServerName + " within " + AnswerTimeoutSeconds + " s.");
            return true;
        }

        if (Phase == StopRequestPhase.Stopping && (now - _acceptedAt).TotalSeconds >= ExitTimeoutSeconds)
        {
            Enter(StopRequestPhase.StillRunning);
            Write("StopServer: " + ServerName + " accepted request " + Nonce + " but was still running after " + ExitTimeoutSeconds + " s.");
            return true;
        }

        return Phase != before;
    }

    /// <summary>The footer's sentence for the current phase, or "" when there is nothing to say.</summary>
    public string Line => Phase switch
    {
        StopRequestPhase.Waiting => "Asking " + ServerName + " to save and stop…",
        StopRequestPhase.Stopping => ServerName + " accepted: saving the world, then stopping…",
        StopRequestPhase.Stopped => ServerName + " stopped." + (StartAgain.Length > 0 ? " " + StartAgain : ""),
        StopRequestPhase.Refused => ServerName + " refused: " + StopServerEvents.RefusalText(Reason),
        StopRequestPhase.NoAnswer => NoAnswerLine + " " + ServerName + " was not stopped by Progenitor.",
        StopRequestPhase.StillRunning => ServerName + " accepted the stop but is still running after "
            + ExitTimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture) + " seconds.",
        StopRequestPhase.NotSent => "The stop request could not be sent to " + ServerName + ".",
        _ => "",
    };

    /// <summary>The readback word for <see cref="Phase"/>.</summary>
    public string PhaseWord => Word(Phase);

    internal static string Word(StopRequestPhase phase) => phase switch
    {
        StopRequestPhase.Waiting => "waiting",
        StopRequestPhase.Stopping => "stopping",
        StopRequestPhase.Stopped => "stopped",
        StopRequestPhase.Refused => "refused",
        StopRequestPhase.NoAnswer => "no-answer",
        StopRequestPhase.StillRunning => "still-running",
        StopRequestPhase.NotSent => "not-sent",
        _ => "idle",
    };

    private void Enter(StopRequestPhase phase)
    {
        Phase = phase;
        States.Add(Word(phase));
        // F22 review (M3): a stop that did not happen (refused, no answer, still running) takes the rejoin offer back.
        if (phase is StopRequestPhase.Refused or StopRequestPhase.NoAnswer or StopRequestPhase.StillRunning)
        {
            try { Abandoned?.Invoke(); }
            catch { /* the offer is a convenience */ }
        }
    }

    private void Write(string line)
    {
        try { Log?.Invoke(line); }
        catch { /* a log line never decides a stop */ }
    }

    private DateTime Now()
    {
        try { return Clock?.Invoke() ?? DateTime.UtcNow; }
        catch { return DateTime.UtcNow; }
    }
}
