using System;
using System.Collections.Generic;
using Progenitor.Install;

namespace Progenitor.Networking;

/// <summary>
/// JOINADMIN1 (join flow stage 3), client: the admin channel's client half. It learns from the server's hello
/// that the joined server has the channel (and whether this player is an admin there), sends the panel's
/// requests, and keeps the last state the server answered with. Reset when the connection ends. Main thread.
/// </summary>
internal static class JoinAdminClient
{
    internal const double TimeoutSeconds = 8;
    private const int MaxPending = 8;

    private static bool _installed;
    private static bool _supports;
    private static bool _helloAdmin;
    private static bool _helloRules;
    private static JoinAdminState? _state;
    private static string _lastOp = "";
    private static string _lastError = "";
    private static string _lastNote = "";
    private static bool _lastOk;
    private static int _answers;
    private static int _nextNonce;
    private static readonly Dictionary<int, (string Op, double Deadline, Action<JoinAdminResponse?>? Answer)> Pending = new();

    /// <summary>Raised on the main thread when the state, an answer or the support flag changed.</summary>
    internal static event Action? Changed;

    /// <summary>The joined server sent its hello this connection: it has the channel.</summary>
    internal static bool ServerSupports => _supports;

    /// <summary>
    /// Whether to offer the "Players joining" button: the server has the channel and this player is an admin there —
    /// the remote-install status when the server sent one (it is re-asked while the Install tab is open), else the
    /// hello. Only the button's visibility: the server authorizes every request again.
    /// </summary>
    internal static bool IsAdmin => JoinAdminOffer.Offered(_supports, _helloAdmin, RemoteInstallClient.Shared.Status);

    internal static bool HelloAdmin => _helloAdmin;

    /// <summary>Settings organization stage 6: the joined server's hello said it answers the Server rules channel.</summary>
    internal static bool ServerHasRules => _supports && _helloRules;
    internal static JoinAdminState? State => _state;
    internal static string LastOp => _lastOp;
    internal static bool LastOk => _lastOk;
    internal static string LastError => _lastError;
    internal static string LastNote => _lastNote;
    internal static int Answers => _answers;
    internal static int PendingCount => Pending.Count;

    /// <summary>Send one request. False (with the reason) when nothing was sent.</summary>
    internal static bool Request(JoinAdminRequest request, out string refusal) => Request(request, out refusal, null);

    /// <summary>
    /// Settings organization stage 5: send one request and call <paramref name="answer"/> once with the server's answer,
    /// or with null when none came (the timeout, or the connection ended). False (with the reason) when nothing was sent;
    /// <paramref name="answer"/> is then not called.
    /// </summary>
    internal static bool Request(JoinAdminRequest request, out string refusal, Action<JoinAdminResponse?>? answer)
    {
        refusal = "";
        if (!_supports)
        {
            refusal = "The joined server has no Players joining panel (an older Progenitor, or not joined).";
            return false;
        }

        if (Pending.Count >= MaxPending)
        {
            refusal = "Too many requests are waiting for the server.";
            return false;
        }

        request.Nonce = ++_nextNonce;
        if (_nextNonce == int.MaxValue) _nextNonce = 0;
        byte[] bytes;
        try { bytes = JoinAdminWire.EncodeRequest(request); }
        catch (Exception e)
        {
            refusal = e.Message;
            return false;
        }

        if (bytes.Length > JoinAdminLimits.MaxPacketBytes)
        {
            refusal = "The request is too large.";
            return false;
        }

        bool sent;
        try { sent = Send(bytes); }
        catch { sent = false; }
        if (!sent)
        {
            refusal = "The request could not be sent.";
            return false;
        }

        Pending[request.Nonce] = (request.Op, Now() + TimeoutSeconds, answer);
        Plugin.Logger?.LogInfo("JoinAdmin: " + request.Op + " sent (request " + request.Nonce + ")");
        return true;
    }

    private static void AcceptHello(bool admin, bool rules)
    {
        _supports = true;
        _helloAdmin = admin;
        _helloRules = rules;
        Plugin.Logger?.LogInfo("JoinAdmin: the server has the Players joining panel" + (admin ? " (you are an admin there)" : ""));
        Notify();
    }

    private static void AcceptResponse(JoinAdminResponse response)
    {
        // Only an answer to a request of this connection is believed.
        if (!Pending.TryGetValue(response.Nonce, out var waiting)) return;
        Pending.Remove(response.Nonce);
        _answers++;
        _lastOp = response.Op;
        _lastOk = response.Ok;
        _lastError = response.Error;
        _lastNote = response.Note;
        // JOINNOTICE: this player's own accepted edit; its catalog resend shows them no "mods changed" notice.
        if (response.Ok && !string.Equals(response.Op, JoinAdminOps.Get, StringComparison.OrdinalIgnoreCase))
            JoinFlowClient.NoteLocalAdminEdit();
        if (response.State != null) _state = response.State;
        else if (!response.Ok && response.Error == JoinAdminPolicy.NotAdminError) _state = null;
        Plugin.Logger?.LogInfo("JoinAdmin: " + response.Op + " answered " + (response.Ok ? "ok" : "refused: " + response.Error)
            + (response.Note.Length > 0 ? " (" + response.Note + ")" : ""));
        Answer(waiting.Answer, response);
        Notify();
    }

    internal static void Tick()
    {
        if (Pending.Count == 0) return;
        double now = Now();
        foreach (var pair in new List<KeyValuePair<int, (string Op, double Deadline, Action<JoinAdminResponse?>? Answer)>>(Pending))
        {
            if (now < pair.Value.Deadline) continue;
            Pending.Remove(pair.Key);
            _lastOp = pair.Value.Op;
            _lastOk = false;
            _lastError = "No answer from the server.";
            _lastNote = "";
            Answer(pair.Value.Answer, null);
            Notify();
        }
    }

    /// <summary>The connection ended: forget the server's support, the state and every waiting request.</summary>
    internal static void Reset()
    {
        _supports = false;
        _helloAdmin = false;
        _helloRules = false;
        _state = null;
        _lastOp = _lastError = _lastNote = "";
        _lastOk = false;
        LastUpdateNotice = null;
        DropPending();
        Notify();
    }

    /// <summary>Every waiting request ends without an answer (its callback gets null).</summary>
    private static void DropPending()
    {
        if (Pending.Count == 0) return;
        var waiting = new List<(string Op, double Deadline, Action<JoinAdminResponse?>? Answer)>(Pending.Values);
        Pending.Clear();
        foreach (var w in waiting) Answer(w.Answer, null);
    }

    private static void Answer(Action<JoinAdminResponse?>? answer, JoinAdminResponse? response)
    {
        if (answer == null) return;
        try { answer(response); }
        catch (Exception e) { Plugin.Logger?.LogWarning("JoinAdmin: answer handler threw: " + e.Message); }
    }

    private static void Notify()
    {
        try { Changed?.Invoke(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("JoinAdmin: change handler threw: " + e.Message); }
    }

    private static double Now() => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;

    private static bool Send(byte[] bytes) => CustomEvents.SendToServer(CustomEventPolicy.JoinAdminEventId, bytes);

    internal static unsafe void Install()
    {
        if (_installed || Plugin.IsServerProcess) return;
        _installed = true;
        try { CustomEvents.OnReceive(CustomEventPolicy.JoinAdminEventId, OnPacket); }
        catch (Exception e) { Plugin.Logger?.LogWarning("JoinAdmin: the client channel could not be registered: " + e.Message); }
        Handshake.ServerContextCleared += OnContextCleared;
        try { Plugin.Frame += Tick; }
        catch { /* requests then never time out; the next answer clears them */ }
    }

    internal static void Uninstall()
    {
        if (!_installed) return;
        _installed = false;
        try { JoinAdminPanel.Uninstall(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("JoinAdmin: panel teardown threw: " + e.Message); }
        Handshake.ServerContextCleared -= OnContextCleared;
        try { Plugin.Frame -= Tick; } catch { /* teardown */ }
        try { CustomEvents.RemoveHandler(CustomEventPolicy.JoinAdminEventId); } catch { /* teardown */ }
        _supports = false;
        _helloAdmin = false;
        _state = null;
        LastUpdateNotice = null;
        DropPending();
        Changed = null;
        UpdateNoticeReceived = null;
    }

    private static void OnContextCleared(string reason) => Reset();

    private static unsafe void OnPacket(int _, byte* payload, int length)
    {
        if (payload == null || length <= 0) return;
        var span = new ReadOnlySpan<byte>(payload, length);
        switch (JoinAdminWire.KindOf(span))
        {
            case JoinAdminWire.KindHello:
                if (JoinAdminWire.TryDecodeHello(span, out bool admin, out bool rules)) AcceptHello(admin, rules);
                break;
            case JoinAdminWire.KindResponse:
                if (JoinAdminWire.TryDecodeResponse(span, out var response)) AcceptResponse(response);
                break;
            case JoinAdminWire.KindNotice:
                if (JoinAdminWire.TryDecodeNotice(span, out string kind, out string text, out bool offerRestart)) AcceptNotice(kind, text, offerRestart);
                break;
        }
    }

    // ---- AUTOUPDATE3: the server's update notices -----------------------------------------------------------

    /// <summary>The last update notice from the joined server (kind, text, Restart now offered), or null.</summary>
    internal static (string Kind, string Text, bool OfferRestart)? LastUpdateNotice { get; private set; }

    /// <summary>Raised on the main thread when the joined server sends an update notice (the panel shows it).</summary>
    internal static event Action<string, string, bool>? UpdateNoticeReceived;

    internal static void AcceptNotice(string kind, string text, bool offerRestart)
    {
        if (!_supports) return;
        LastUpdateNotice = (kind, text, offerRestart);
        Plugin.Logger?.LogInfo("JoinAdmin: update notice from the server (" + kind + "): " + text);
        try { UpdateNoticeReceived?.Invoke(kind, text, offerRestart); }
        catch (Exception e) { Plugin.Logger?.LogWarning("JoinAdmin: the update notice handler threw: " + e.Message); }
        Notify();
    }
}
