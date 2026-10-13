#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;

namespace Progenitor.Networking;

/// <summary>
/// Settings organization stages 6 and 7, client: the Server rules channel's client half. It sends the page's requests to
/// a server whose hello said it has the channel (<see cref="JoinAdminClient.ServerHasRules"/>), keeps the last state the
/// server answered or pushed, and forgets it when the connection ends. Main thread.
/// </summary>
internal static class ServerRulesClient
{
    internal const double TimeoutSeconds = 8;
    private const int MaxPending = 8;

    private static bool _installed;
    private static ServerRulesState? _state;
    private static int _nextNonce;
    private static int _answers;
    private static readonly Dictionary<int, (string Op, double Deadline, Action<ServerRulesResponse?>? Answer)> Pending = new();

    /// <summary>Raised on the main thread when the state changed (an answer, a push, the connection ending).</summary>
    internal static event Action? Changed;

    internal static ServerRulesState? State => _state;

    internal static int Answers => _answers;

    internal static int PendingCount => Pending.Count;

    internal static bool Request(ServerRulesRequest request, out string refusal, Action<ServerRulesResponse?>? answer)
    {
        refusal = "";
        if (!JoinAdminClient.ServerHasRules)
        {
            refusal = "The joined server does not share its rules (an older Progenitor, or not joined).";
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
        try { bytes = ServerRulesWire.EncodeRequest(request); }
        catch (Exception e)
        {
            refusal = e.Message;
            return false;
        }

        if (bytes.Length > ServerRulesWire.MaxPacketBytes)
        {
            refusal = "The request is too large.";
            return false;
        }

        bool sent;
        try { sent = CustomEvents.SendToServer(CustomEventPolicy.ServerRulesEventId, bytes); }
        catch { sent = false; }
        if (!sent)
        {
            refusal = "The request could not be sent.";
            return false;
        }

        Pending[request.Nonce] = (request.Op, Now() + TimeoutSeconds, answer);
        Plugin.Logger?.LogInfo("ServerRules: " + request.Op + (request.Key.Length > 0 ? " " + request.Key : "") + " sent (request " + request.Nonce + ")");
        return true;
    }

    private static void AcceptResponse(ServerRulesResponse response)
    {
        if (!Pending.TryGetValue(response.Nonce, out var waiting)) return;
        Pending.Remove(response.Nonce);
        _answers++;
        if (response.State != null) _state = response.State;
        if (response.Ok && response.Op == ServerRulesRequest.OpWrite) JoinFlowClient.NoteLocalAdminEdit();
        Plugin.Logger?.LogInfo("ServerRules: " + response.Op + " answered " + (response.Ok ? "ok" : "refused: " + response.Error)
            + (response.Note.Length > 0 ? " (" + response.Note + ")" : ""));
        Answer(waiting.Answer, response);
        Notify();
    }

    private static void AcceptChanged(ServerRulesState state)
    {
        _state = state;
        Notify();
    }

    internal static void Tick()
    {
        if (Pending.Count == 0) return;
        double now = Now();
        foreach (var pair in new List<KeyValuePair<int, (string Op, double Deadline, Action<ServerRulesResponse?>? Answer)>>(Pending))
        {
            if (now < pair.Value.Deadline) continue;
            Pending.Remove(pair.Key);
            Answer(pair.Value.Answer, null);
            Notify();
        }
    }

    internal static void Reset()
    {
        _state = null;
        var waiting = new List<(string Op, double Deadline, Action<ServerRulesResponse?>? Answer)>(Pending.Values);
        Pending.Clear();
        foreach (var w in waiting) Answer(w.Answer, null);
        Notify();
    }

    private static void Answer(Action<ServerRulesResponse?>? answer, ServerRulesResponse? response)
    {
        if (answer == null) return;
        try { answer(response); }
        catch (Exception e) { Plugin.Logger?.LogWarning("ServerRules: answer handler threw: " + e.Message); }
    }

    private static void Notify()
    {
        try { Changed?.Invoke(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("ServerRules: change handler threw: " + e.Message); }
    }

    private static double Now() => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;

    internal static unsafe void Install()
    {
        if (_installed || Plugin.IsServerProcess) return;
        _installed = true;
        try { CustomEvents.OnReceive(CustomEventPolicy.ServerRulesEventId, OnPacket); }
        catch (Exception e) { Plugin.Logger?.LogWarning("ServerRules: the client channel could not be registered: " + e.Message); }
        Handshake.ServerContextCleared += OnContextCleared;
        try { Plugin.Frame += Tick; }
        catch { /* requests then never time out; the next answer clears them */ }
    }

    internal static void Uninstall()
    {
        if (!_installed) return;
        _installed = false;
        Handshake.ServerContextCleared -= OnContextCleared;
        try { Plugin.Frame -= Tick; } catch { /* teardown */ }
        try { CustomEvents.RemoveHandler(CustomEventPolicy.ServerRulesEventId); } catch { /* teardown */ }
        _state = null;
        Pending.Clear();
        Changed = null;
    }

    private static void OnContextCleared(string reason) => Reset();

    private static unsafe void OnPacket(int _, byte* payload, int length)
    {
        if (payload == null || length <= 0) return;
        var span = new ReadOnlySpan<byte>(payload, length);
        switch (ServerRulesWire.KindOf(span))
        {
            case ServerRulesWire.KindResponse:
                if (ServerRulesWire.TryDecodeResponse(span, out var response)) AcceptResponse(response);
                break;
            case ServerRulesWire.KindChanged:
                if (ServerRulesWire.TryDecodeChanged(span, out var state) && state != null) AcceptChanged(state);
                break;
            case ServerRulesWire.KindBackupsChanged:
                // BACKUPSDLG1: the pending restore request changed on the server; an open Backups dialog asks again.
                if (ServerRulesWire.TryDecodeBackupsChanged(span, out string pendingId, out bool countdown, out string why))
                {
                    try { ServerBackupsRows.OnServerChanged(pendingId, countdown, why); }
                    catch (Exception e) { Plugin.Logger?.LogWarning("WorldRestore: the pending-change push threw: " + e.Message); }
                }

                break;
        }
    }
}
#endif
