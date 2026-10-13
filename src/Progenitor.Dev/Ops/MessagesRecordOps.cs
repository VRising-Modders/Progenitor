using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using Progenitor.Ui;

namespace Progenitor.Dev.Ops;

/// <summary>
/// Mod compatibility C5: debug-bridge <c>messages_record</c>, both sides, main thread. Records a mod's real
/// <c>ctx.Messages</c> traffic (sent and received on this side) into the fixture format Progenitor.Testing replays,
/// under <c>BepInEx/progenitor-dev/messages/&lt;mod&gt;/</c>, bounded by time, fixture count and bytes. The work is
/// Core's <c>Progenitor.Networking.ModMessageRecordBridge</c>, reached by reflection; see docs/bridge-ops-join.md.
/// </summary>
internal static class MessagesRecordOps
{
    private const string BridgeType = "Progenitor.Networking.ModMessageRecordBridge";

    public static object Run(JsonElement args)
    {
        var req = MessagesRecordArgs.Parse(args);
        return req.Action switch
        {
            "on" => Call("Start", req.Mod, req.Seconds, req.MaxMessages, req.MaxBytes),
            "off" => Call("Stop"),
            _ => Call("Status"),
        };
    }

    private static Dictionary<string, object?> Call(string method, params object?[] args)
    {
        var type = typeof(ModsMenu).Assembly.GetType(BridgeType, throwOnError: false)
            ?? throw new InvalidOperationException("Core has no " + BridgeType + " (older than mod compatibility C5)");
        var mi = type.GetMethod(method, BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException(BridgeType + "." + method + " not found");
        try
        {
            return mi.Invoke(null, args) as Dictionary<string, object?>
                   ?? new Dictionary<string, object?> { ["ok"] = false, ["error"] = method + " returned nothing" };
        }
        catch (TargetInvocationException e) { throw new InvalidOperationException(e.InnerException?.Message ?? e.Message); }
    }
}
