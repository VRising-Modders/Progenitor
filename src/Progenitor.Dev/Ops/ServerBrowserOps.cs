using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using Progenitor.Ui;

namespace Progenitor.Dev.Ops;

/// <summary>
/// BROWSERFILTER1: debug-bridge <c>server_browser</c> (client). Reads the game's Join Game browser as Progenitor's
/// Progenitor servers filter sees it and drives that filter. The work is Core's
/// <c>Progenitor.Networking.ServerBrowserTestBridge</c>, reached by reflection.
///
/// <list type="bullet">
/// <item><c>{"action":"read","rows":20}</c>: the switch, the checkbox, the last filter pass, the count line and up to
/// <c>rows</c> shown rows (default 20) with their tags.</item>
/// <item><c>{"action":"set","on":true}</c>: write the switch (the Preferences row's path).</item>
/// <item><c>{"action":"click"}</c>: flip the cloned checkbox (the player's path: <c>onValueChanged</c>).</item>
/// <item><c>{"action":"rebuild"}</c>: make the game rebuild the shown list now.</item>
/// </list>
/// </summary>
internal static class ServerBrowserOps
{
    private const string BridgeType = "Progenitor.Networking.ServerBrowserTestBridge";

    public static object Run(JsonElement args)
    {
        string action = Str(args, "action")?.Trim().ToLowerInvariant() ?? "read";
        switch (action)
        {
            case "":
            case "read":
            {
                int rows = 20;
                if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("rows", out var r) && r.ValueKind == JsonValueKind.Number)
                    rows = Math.Clamp(r.GetInt32(), 0, 500);
                return Call("Read", rows);
            }
            case "set":
            {
                if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("on", out var on)
                    || (on.ValueKind != JsonValueKind.True && on.ValueKind != JsonValueKind.False))
                    throw new ArgumentException("args.on must be true or false");
                return Call("Set", on.ValueKind == JsonValueKind.True);
            }
            case "click":
                return Call("Click");
            case "rebuild":
                return Call("Rebuild");
            default:
                throw new ArgumentException("args.action must be read, set, click or rebuild");
        }
    }

    private static Dictionary<string, object?> Call(string method, params object?[] args)
    {
        var type = typeof(ModsMenu).Assembly.GetType(BridgeType, throwOnError: false)
            ?? throw new InvalidOperationException("Core has no " + BridgeType + " (older than BROWSERFILTER1)");
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
