using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using Progenitor.Ui;

namespace Progenitor.Dev.Ops;

/// <summary>
/// Debug-bridge <c>materials</c>, either side, main thread. Reads the material seams
/// (<c>ctx.MaterialSource</c>) and, on the server, arms a one-shot fault for live checks of the
/// per-operation transaction, through Core's <c>Progenitor.Materials.MaterialsTestBridge</c> by reflection.
///
/// <list type="bullet">
/// <item><c>{}</c> or <c>{"action":"status"}</c>: the status line (provider, hooks, wired operations,
/// payments, faults, refused), Bootstrap's seam line, and the client's held views (operation, sequence,
/// heart and station network ids, totals) or the server's open operations and armed injection.</item>
/// <item><c>{"action":"inject","from":"c-…","to":"c-…","item":123}</c> (server): right after the next
/// governed check, move every unit of the item from one container to the other, so that operation's
/// payment meets a container that changed between its check and its payment.</item>
/// <item><c>{"action":"clear"}</c> (server): drop an injection that has not run.</item>
/// </list>
/// </summary>
internal static class MaterialsOps
{
    private const string BridgeType = "Progenitor.Materials.MaterialsTestBridge";

    public static object Run(JsonElement args)
    {
        string action = Str(args, "action")?.Trim().ToLowerInvariant() ?? "status";
        return action switch
        {
            "status" => Call("Status"),
            "inject" => Call("InjectMove",
                Str(args, "from") ?? throw new ArgumentException("args.from (c-…) is required for inject"),
                Str(args, "to") ?? throw new ArgumentException("args.to (c-…) is required for inject"),
                Int(args, "item") ?? throw new ArgumentException("args.item is required for inject")),
            "clear" => Call("ClearInjection"),
            _ => throw new ArgumentException("args.action must be status, inject or clear"),
        };
    }

    private static Dictionary<string, object?> Call(string method, params object?[] args)
    {
        var type = typeof(ModsMenu).Assembly.GetType(BridgeType, throwOnError: false)
            ?? throw new InvalidOperationException("Core has no " + BridgeType + " (older than this op)");
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

    private static int? Int(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n : null;
}
