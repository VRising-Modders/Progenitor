using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Progenitor.Ui;

namespace Progenitor.Dev.Ops;

/// <summary>
/// TEXTLIMIT1: debug-bridge <c>setting</c>, both sides. Reads a synced setting's stored value on this
/// side (<c>get</c>), tails the server's settings audit log (<c>audit</c>), and sends an administrator
/// edit from a client exactly as the page's Apply does (<c>request</c>, optionally without the
/// client's own length check, to show the server enforces its limit). The work is Core's
/// <c>Progenitor.Networking.ServerSettingsTestBridge</c>, reached by reflection.
///
/// <list type="bullet">
/// <item><c>{"action":"get","row":"progenitor.sample/Menu/ServerNotice"}</c></item>
/// <item><c>{"action":"audit","lines":5}</c> (server)</item>
/// <item><c>{"action":"request","row":"…","value":"…","skipClientCheck":false,"expected":null}</c> (client;
/// <c>value</c> may be replaced by <c>repeat</c> + <c>count</c> to build a long value)</item>
/// </list>
/// </summary>
internal static class SettingOps
{
    private const string BridgeType = "Progenitor.Networking.ServerSettingsTestBridge";

    /// <summary>How long <c>request</c> waits for the server's answer (the request itself times out at 8 s).</summary>
    private const int RequestWaitMs = 10_000;

    /// <summary>The longest value <c>request</c> builds from <c>repeat</c> × <c>count</c>.</summary>
    private const int MaxBuiltValue = 20_000;

    public static object Run(JsonElement args)
    {
        string action = Str(args, "action")?.Trim().ToLowerInvariant() ?? "";
        switch (action)
        {
            case "get":
                return Call("Get", Required(args, "row"));
            case "audit":
                return Call("Audit", Int(args, "lines", 5));
            case "request":
                return Request(args);
            default:
                throw new ArgumentException("args.action must be get, audit or request");
        }
    }

    private static object Request(JsonElement args)
    {
        string row = Required(args, "row");
        string? value = Str(args, "value");
        string? repeat = Str(args, "repeat");
        if (repeat != null)
        {
            int count = Int(args, "count", 1);
            if (repeat.Length == 0 || count < 1 || (long)repeat.Length * count > MaxBuiltValue)
                throw new ArgumentException("args.repeat × args.count must build 1 to " + MaxBuiltValue + " characters");
            value = string.Concat(System.Linq.Enumerable.Repeat(repeat, count));
        }
        if (value == null) throw new ArgumentException("args.value (or args.repeat with args.count) is required");
        bool skip = args.TryGetProperty("skipClientCheck", out var s) && s.ValueKind == JsonValueKind.True;
        string? expected = Str(args, "expected");

        var sent = Call("Request", row, value, skip, expected);
        if (!(sent.TryGetValue("ok", out var ok) && ok is true)) return sent;
        int ticket = Convert.ToInt32(sent["ticket"]);

        var deferred = new DeferredResult(RequestWaitMs + 2_000);
        var clock = Stopwatch.StartNew();
        void Poll()
        {
            try
            {
                if (deferred.BoundGate != null && deferred.BoundGate.ShouldSkip(deferred.BoundTicket))
                {
                    Forget(ticket);
                    return;
                }
                var result = Call("Result", ticket);
                bool done = result.TryGetValue("done", out var d) && d is true;
                if (!done && clock.ElapsedMilliseconds < RequestWaitMs)
                {
                    MainThread.PostDelayed(0, Poll, deferred);
                    return;
                }
                if (!done)
                {
                    Forget(ticket);
                    result["error"] = "no answer within " + RequestWaitMs + " ms";
                }
                result["request"] = sent;
                result["ms"] = clock.ElapsedMilliseconds;
                deferred.SetResult(result);
            }
            catch (Exception e)
            {
                Forget(ticket);
                if (!deferred.Completion.Task.IsCompleted)
                    deferred.SetResult(new Dictionary<string, object?> { ["ok"] = false, ["error"] = e.Message, ["request"] = sent });
            }
        }
        MainThread.PostDelayed(0, Poll, deferred);
        return deferred;
    }

    private static void Forget(int ticket)
    {
        try { Invoke("Forget", ticket); } catch { /* best effort */ }
    }

    private static Dictionary<string, object?> Call(string method, params object?[] args) =>
        Invoke(method, args) as Dictionary<string, object?>
        ?? new Dictionary<string, object?> { ["ok"] = false, ["error"] = method + " returned nothing" };

    private static object? Invoke(string method, params object?[] args)
    {
        var type = typeof(ModsMenu).Assembly.GetType(BridgeType, throwOnError: false)
            ?? throw new InvalidOperationException("Core has no " + BridgeType + " (older than TEXTLIMIT1)");
        var mi = type.GetMethod(method, BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException(BridgeType + "." + method + " not found");
        try { return mi.Invoke(null, args); }
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

    private static int Int(JsonElement args, string name, int fallback) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n : fallback;
}
