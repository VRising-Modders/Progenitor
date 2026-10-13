using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using Progenitor.Ui;

namespace Progenitor.Dev.Ops;

/// <summary>
/// SERVERDESC1: debug-bridge <c>server_desc</c>. Reads the server-list description on either side and, on the
/// server, sets the rows and applies them through the same step an administrator's edit runs. The work is
/// Core's <c>Progenitor.Networking.ServerListDescriptionTestBridge</c>, reached by reflection.
///
/// <list type="bullet">
/// <item><c>{"action":"read"}</c> (both sides)</item>
/// <item><c>{"action":"set","text":"…","signature":true}</c> (server; either field may be left out)</item>
/// <item><c>{"action":"apply"}</c> (server: run the apply step now)</item>
/// </list>
/// </summary>
internal static class ServerDescOps
{
    private const string BridgeType = "Progenitor.Networking.ServerListDescriptionTestBridge";

    public static object Run(JsonElement args)
    {
        string action = Str(args, "action")?.Trim().ToLowerInvariant() ?? "read";
        switch (action)
        {
            case "":
            case "read":
                return Call("Read");
            case "set":
            {
                string? text = Str(args, "text");
                string? signature = null;
                if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("signature", out var s))
                {
                    signature = s.ValueKind switch
                    {
                        JsonValueKind.True => "true",
                        JsonValueKind.False => "false",
                        JsonValueKind.String => s.GetString(),
                        _ => throw new ArgumentException("args.signature must be true or false"),
                    };
                }

                if (text == null && signature == null) throw new ArgumentException("args.text and/or args.signature is required");
                return Call("Set", text, signature);
            }
            case "apply":
                return Call("Apply");
            default:
                throw new ArgumentException("args.action must be read, set or apply");
        }
    }

    private static Dictionary<string, object?> Call(string method, params object?[] args)
    {
        var type = typeof(ModsMenu).Assembly.GetType(BridgeType, throwOnError: false)
            ?? throw new InvalidOperationException("Core has no " + BridgeType + " (older than SERVERDESC1)");
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
