using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Progenitor.Ui;

namespace Progenitor.Dev.Ops;

/// <summary>
/// JOINADMIN1 (join flow stage 3): debug-bridge <c>join_admin</c>, main thread. Drives and reads the admin
/// "Players joining" panel (client) and reads the server's store and refused-join record (server), through Core's
/// <c>Progenitor.Networking.JoinAdminTestBridge</c> by reflection.
///
/// <para>Client: <c>read</c>, <c>open</c>, <c>close</c>, <c>refresh</c>, <c>escape</c>,
/// <c>page</c> (<c>page</c>: main | pick-recommend), <c>search</c> (<c>text</c>), <c>pick</c> (<c>index</c>),
/// <c>recommend</c> (<c>id</c> and/or <c>package</c>, <c>version</c>, <c>reason</c>), <c>unrecommend</c> (<c>key</c>),
/// <c>reason</c> (<c>key</c>, <c>reason</c>), <c>window</c> (<c>seconds</c>),
/// <c>update</c> (<c>mod</c>), <c>request</c> (<c>op</c> and its fields: a raw request, as a client without the
/// panel would send it).</para>
/// <para>Server: <c>store</c>, <c>refused</c>, <c>inject_refused</c> (<c>mod</c>, <c>required</c>, <c>had</c>,
/// <c>kind</c>, <c>count</c>; TEST ONLY: writes synthetic rows into the real record), <c>clear_refused</c> (TEST ONLY:
/// empties the record), <c>policy</c>, <c>inject_report</c>, <c>recheck</c> (CLIENTPOLICY3), <c>notices</c>.</para>
/// <para>CLIENTPOLICY3 (client): <c>mid_session</c> (<c>value</c> warn | disconnect), <c>alert_approve</c> (<c>mod</c>, or
/// <c>index</c> in the alert).</para>
/// <para>Mod compatibility C3 (client, staged like every edit): <c>version_matching</c> (<c>value</c> range | minor | exact),
/// <c>server_mod_rule</c> (<c>id</c>, <c>rule</c> exact | at-least | minor | any | default; no <c>rule</c>: the row's Rule
/// button, which cycles). The server's <c>store</c> reads back <c>versionMatching</c>.</para>
/// <para>JOINDRAFT1 (client): every edit action (recommend, unrecommend, reason, window, require, unrequire, require_reason,
/// require_rule, policy_mode, unverified, approve, unapprove, approve_everything_confirm, mid_session, alert_approve) now
/// stages a change in the panel's draft and sends nothing; <c>apply</c> sends the draft (waits until the run ends),
/// <c>discard</c> resets it, <c>undo_change</c> (<c>index</c>) drops one change. <c>request</c> still sends at once.</para>
/// </summary>
internal static class JoinAdminOps
{
    private const string BridgeType = "Progenitor.Networking.JoinAdminTestBridge";

    public static object Run(JsonElement args)
    {
        bool server = Progenitor.Plugin.IsServerProcess;
        string action = Str(args, "action")?.Trim().ToLowerInvariant() ?? (server ? "store" : "read");
        if (server)
        {
            return action switch
            {
                "store" or "read" => Call("Store"),
                "refused" => Call("Refused"),
                "inject_refused" => Call("InjectRefused", Required(args, "mod"), Str(args, "required") ?? "", Str(args, "had") ?? "",
                    Str(args, "kind") ?? "", Int(args, "count", 1)),
                "clear_refused" => Call("ClearRefused"),
                // CLIENTPOLICY1.
                "policy" => Call("Policy"),
                "notices" => Call("Notices"),
                "inject_report" => Call("InjectReport", Required(args, "user"), Raw(args, "mods"), Bool(args, "truncated"), Str(args, "mode") ?? "next"),
                // CLIENTPOLICY3: re-judge every player in the world from their last report now.
                "recheck" => Call("Recheck"),
                _ => throw new ArgumentException("on the server, args.action must be store, refused, inject_refused, clear_refused, policy, inject_report, recheck or notices"),
            };
        }

        // Every client action that sends a request waits (bounded) for the server's answer and returns the state
        // after it, so a readback is deterministic; "read", "page", "search", "close" and "escape" answer at once.
        if (action == "read") return Call("Read");
        if (action == "report") return Call("Report");
        // NOTICEOPEN1: press the mid-session notice's Open Install Mods / Open Mods (closes it, opens the workspace there).
        if (action == "notice_open") return Call("NoticeOpen");
        int before = AnswerCount(Call("Read"));
        var reply = ClientAction(action, args);
        // JOINDRAFT1: Apply sends several requests one after another; wait until the run ends.
        if (action == "apply") return WaitForApply(reply);
        return WaitForAnswer(reply, before);
    }

    /// <summary>How long Apply may take (each request times out at 8 s; a stopped run ends at the first silence).</summary>
    private const int ApplyWaitMs = 60_000;

    private static bool IsApplying(Dictionary<string, object?> d) =>
        d.TryGetValue("applying", out var a) && a is bool b && b;

    private static object WaitForApply(Dictionary<string, object?> reply)
    {
        if (!IsApplying(reply) && PendingCount(reply) == 0) return reply;
        var deferred = new DeferredResult(ApplyWaitMs + 2_000);
        var clock = Stopwatch.StartNew();
        void Poll()
        {
            try
            {
                if (deferred.BoundGate != null && deferred.BoundGate.ShouldSkip(deferred.BoundTicket)) return;
                var now = Call("Read");
                bool busy = IsApplying(now) || PendingCount(now) > 0;
                if (busy && clock.ElapsedMilliseconds < ApplyWaitMs)
                {
                    MainThread.PostDelayed(0, Poll, deferred);
                    return;
                }

                now["result"] = reply.TryGetValue("result", out var r) ? r : null;
                now["waitedMs"] = clock.ElapsedMilliseconds;
                if (busy) now["error"] = "Apply did not finish within " + ApplyWaitMs + " ms";
                deferred.SetResult(now);
            }
            catch (Exception e)
            {
                if (!deferred.Completion.Task.IsCompleted)
                    deferred.SetResult(new Dictionary<string, object?> { ["ok"] = false, ["error"] = e.Message, ["result"] = reply.TryGetValue("result", out var r) ? r : null });
            }
        }

        MainThread.PostDelayed(0, Poll, deferred);
        return deferred;
    }

    /// <summary>How long a client action waits for the server's answer (a request itself times out at 8 s).</summary>
    private const int AnswerWaitMs = 10_000;

    private static int AnswerCount(Dictionary<string, object?> d) =>
        d.TryGetValue("answers", out var a) && a is int n ? n : 0;

    private static int PendingCount(Dictionary<string, object?> d) =>
        d.TryGetValue("pending", out var p) && p is int n ? n : 0;

    private static object WaitForAnswer(Dictionary<string, object?> reply, int before)
    {
        if (PendingCount(reply) == 0) return reply;
        var deferred = new DeferredResult(AnswerWaitMs + 2_000);
        var clock = Stopwatch.StartNew();
        void Poll()
        {
            try
            {
                if (deferred.BoundGate != null && deferred.BoundGate.ShouldSkip(deferred.BoundTicket)) return;
                var now = Call("Read");
                bool answered = AnswerCount(now) > before;
                if (!answered && PendingCount(now) > 0 && clock.ElapsedMilliseconds < AnswerWaitMs)
                {
                    MainThread.PostDelayed(0, Poll, deferred);
                    return;
                }

                now["result"] = reply.TryGetValue("result", out var r) ? r : null;
                now["answered"] = answered;
                now["waitedMs"] = clock.ElapsedMilliseconds;
                if (!answered && PendingCount(now) > 0) now["error"] = "no answer within " + AnswerWaitMs + " ms";
                deferred.SetResult(now);
            }
            catch (Exception e)
            {
                if (!deferred.Completion.Task.IsCompleted)
                    deferred.SetResult(new Dictionary<string, object?> { ["ok"] = false, ["error"] = e.Message, ["result"] = reply.TryGetValue("result", out var r) ? r : null });
            }
        }

        MainThread.PostDelayed(0, Poll, deferred);
        return deferred;
    }

    private static Dictionary<string, object?> ClientAction(string action, JsonElement args)
    {
        return action switch
        {
            "read" => Call("Read"),
            "open" => Call("Open"),
            "close" => Call("Close"),
            "refresh" => Call("Refresh"),
            "escape" => Call("Escape"),
            "page" => Call("Page", Str(args, "page") ?? "main"),
            "search" => Call("Search", Str(args, "text") ?? ""),
            "pick" => Call("Pick", Int(args, "index", 0)),
            "recommend" => Call("Recommend", Str(args, "id") ?? "", Str(args, "package") ?? "", Str(args, "version") ?? "", Str(args, "reason") ?? "",
                Str(args, "name") ?? ""),
            "unrecommend" => Call("Unrecommend", Required(args, "key")),
            "reason" => Call("Reason", Required(args, "key"), Str(args, "reason") ?? ""),
            "window" => Call("Window", OptionalInt(args, "seconds") ?? throw new ArgumentException("args.seconds is required (a whole number)")),
            "update" => Call("Update", Required(args, "mod")),
            "request" when HasAny(args, "rule", "value", "items") => Call("PolicyRequest", Required(args, "op"), Str(args, "id") ?? "", Str(args, "package") ?? "",
                Str(args, "version") ?? "", Str(args, "reason") ?? "", Str(args, "rule") ?? "", Str(args, "value") ?? "", Raw(args, "items")),
            "request" => Call("Request", Required(args, "op"), Str(args, "id") ?? "", Str(args, "package") ?? "", Str(args, "version") ?? "",
                Str(args, "reason") ?? "", OptionalInt(args, "seconds")),
            // CLIENTPOLICY1: the client mod policy's panel actions (the panel's own handlers).
            "require" => Call("RequireAdd", Str(args, "id") ?? "", Str(args, "package") ?? "", Str(args, "version") ?? "", Str(args, "rule") ?? "",
                Str(args, "reason") ?? "", Str(args, "name") ?? ""),
            "unrequire" => Call("RequireRemove", Required(args, "key")),
            "require_reason" => Call("RequireReason", Required(args, "key"), Str(args, "reason") ?? ""),
            "require_rule" => Call("RequireRule", Required(args, "key"), Str(args, "rule") ?? "", Str(args, "version") ?? ""),
            "policy_mode" => Call("PolicyMode", Required(args, "value")),
            "unverified" => Call("Unverified", Required(args, "value")),
            "approve" => Call("ApproveAdd", Str(args, "id") ?? "", Str(args, "package") ?? "", Str(args, "version") ?? "", Str(args, "rule") ?? "",
                Str(args, "name") ?? ""),
            "unapprove" => Call("ApproveRemove", Required(args, "key")),
            "approve_everything" => Call("ApproveEverything"),
            "approve_everything_confirm" => Call("ApproveEverythingConfirm"),
            "approved_page" => Call("ApprovedPage", !string.Equals(Str(args, "direction"), "back", StringComparison.OrdinalIgnoreCase)),
            // CLIENTPOLICY3: the mid-session choice, and an alert line's Approve (by mod id, or by its index in the alert).
            "mid_session" => Call("MidSession", Required(args, "value")),
            "alert_approve" => Call("AlertApprove", Str(args, "mod") ?? "", Int(args, "index", -1)),
            // Mod compatibility C3: Version matching, and the rule of one mod the server runs (no rule: the Rule button).
            "version_matching" => Call("VersionMatching", Required(args, "value")),
            "server_mod_rule" => Call("ServerModRule", Required(args, "id"), Str(args, "rule") ?? ""),
            // JOINDRAFT1: the edit actions above stage; these send the draft, reset it, or undo one change.
            "apply" => Call("Apply"),
            "discard" => Call("Discard"),
            "undo_change" => Call("UndoChange", OptionalInt(args, "index") ?? throw new ArgumentException("args.index is required (0 and up)")),
            _ => throw new ArgumentException(
                "on the client, args.action must be read, report, open, close, refresh, escape, page, search, pick, recommend, unrecommend, "
                + "reason, window, update, request, require, unrequire, require_reason, require_rule, policy_mode, unverified, "
                + "approve, unapprove, approve_everything, approve_everything_confirm, approved_page, mid_session, alert_approve, "
                + "version_matching, server_mod_rule, "
                + "apply, discard or undo_change"),
        };
    }

    private static bool HasAny(JsonElement args, params string[] names)
    {
        if (args.ValueKind != JsonValueKind.Object) return false;
        foreach (string n in names)
            if (args.TryGetProperty(n, out _)) return true;
        return false;
    }

    /// <summary>A structured argument as its JSON text (an array the Core bridge parses), or empty.</summary>
    private static string Raw(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v.GetRawText() : "";

    private static bool Bool(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static Dictionary<string, object?> Call(string method, params object?[] args)
    {
        var type = typeof(ModsMenu).Assembly.GetType(BridgeType, throwOnError: false)
            ?? throw new InvalidOperationException("Core has no " + BridgeType + " (older than JOINADMIN1)");
        var mi = type.GetMethod(method, BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException(BridgeType + "." + method + " not found");
        try
        {
            return mi.Invoke(null, args) as Dictionary<string, object?>
                ?? new Dictionary<string, object?> { ["ok"] = false, ["error"] = method + " returned nothing" };
        }
        catch (TargetInvocationException e) { throw new InvalidOperationException(e.InnerException?.Message ?? e.Message); }
    }

    private static string? Str(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Required(JsonElement args, string name)
    {
        string? v = Str(args, name);
        if (string.IsNullOrWhiteSpace(v)) throw new ArgumentException("args." + name + " is required");
        return v!.Trim();
    }

    private static int? OptionalInt(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n : null;

    private static int Int(JsonElement args, string name, int fallback) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n : fallback;
}
