using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Progenitor.Ui;

namespace Progenitor.Dev.Ops;

/// <summary>
/// Settings organization stages 6 and 7: debug-bridge <c>server_rules</c>, main thread, through Core's
/// <c>Progenitor.Networking.ServerRulesTestBridge</c> by reflection. Client: <c>read</c> (the last state), <c>get</c> (ask the
/// server now and wait for its answer), <c>write</c> (<c>key</c>, <c>value</c>, optional <c>confirm</c> for a change that may
/// reset the world: as the page sends it, then wait). Server:
/// <c>read</c> (files, layers, loaded values, overrides and the state; no direct set). Never returns a password.
/// </summary>
internal static class ServerRulesOps
{
    private const string BridgeType = "Progenitor.Networking.ServerRulesTestBridge";
    private const int AnswerWaitMs = 10_000;

    public static object Run(JsonElement args)
    {
        string action = Str(args, "action")?.Trim().ToLowerInvariant() ?? "read";
        if (Progenitor.Plugin.IsServerProcess)
        {
            if (action != "read") throw new ArgumentException("on the server, args.action must be read (writes take the admin path from a client)");
            return Call("Server");
        }

        switch (action)
        {
            case "read": return Call("Read");
            case "get": return Wait(Call("Get"));
            case "write":
                string key = Str(args, "key") ?? throw new ArgumentException("args.key is required");
                string value = Str(args, "value") ?? throw new ArgumentException("args.value is required (a string; \"\" removes the password)");
                // Review fix: a change that may reset the world (scheduled wipes) needs "confirm": true, as the page's dialog sends.
                bool confirm = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("confirm", out var c) && c.ValueKind == JsonValueKind.True;
                return Wait(Call("WriteWith", key, value, confirm));
            default: throw new ArgumentException("on the client, args.action must be read, get or write");
        }
    }

    private static int Answers(Dictionary<string, object?> d) => d.TryGetValue("answers", out var a) && a is int n ? n : 0;

    private static int Pending(Dictionary<string, object?> d) => d.TryGetValue("pending", out var p) && p is int n ? n : 0;

    private static object Wait(Dictionary<string, object?> reply)
    {
        if (Pending(reply) == 0) return reply;
        int before = Answers(reply) - 0;
        var deferred = new DeferredResult(AnswerWaitMs + 2_000);
        var clock = Stopwatch.StartNew();
        void Poll()
        {
            try
            {
                if (deferred.BoundGate != null && deferred.BoundGate.ShouldSkip(deferred.BoundTicket)) return;
                var now = Call("Read");
                if (Pending(now) > 0 && clock.ElapsedMilliseconds < AnswerWaitMs)
                {
                    MainThread.PostDelayed(0, Poll, deferred);
                    return;
                }

                now["result"] = reply.TryGetValue("result", out var r) ? r : null;
                now["answered"] = Answers(now) > before;
                now["waitedMs"] = clock.ElapsedMilliseconds;
                deferred.SetResult(now);
            }
            catch (Exception e)
            {
                if (!deferred.Completion.Task.IsCompleted)
                    deferred.SetResult(new Dictionary<string, object?> { ["ok"] = false, ["error"] = e.Message });
            }
        }

        MainThread.PostDelayed(0, Poll, deferred);
        return deferred;
    }

    private static Dictionary<string, object?> Call(string method, params object?[] args)
    {
        var type = typeof(ModsMenu).Assembly.GetType(BridgeType, throwOnError: false)
            ?? throw new InvalidOperationException("Core has no " + BridgeType + " (older than settings organization stage 6)");
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
}
