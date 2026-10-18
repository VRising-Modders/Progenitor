using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using Progenitor.Ui;

namespace Progenitor.Dev.Ops;

/// <summary>
/// Debug-bridge <c>container_preview</c>, main thread, through Core's <c>Progenitor.Inventory.ContainerPreviewTestBridge</c>
/// by reflection. The game's own container window for a chest somewhere else, read-only (the live experiments E1 to E5
/// in <c>docs/modules.md</c>, "Container preview").
///
/// <list type="bullet">
/// <item><c>{}</c> or <c>{"action":"status"}</c> (either side): the client's running preview (state, phase, network id,
/// client target and inventory entities, hooks, hidden UI, dropped item events) and the game's open menu and container
/// window target; on the server, every server-side preview and every shared streaming lease (user, point, holders).</item>
/// <item><c>{"action":"open","id":"c-...","keepOpen":false,"seconds":30,"hideBag":true,"replicationTimeout":5,"dropEvents":true,"hudGate":true,"syncInventory":true}</c>
/// (client): start a preview of that container; read <c>status</c> for the outcome. <c>hudGate: false</c> (E2 only)
/// leaves the game's interact-HUD auto-close alone, to observe it closing the window. <c>syncInventory: false</c> (E1b only) does
/// not put the user in the chest's inventory sync lists, so the inventory stays away as in E1.</item>
/// <item><c>{"action":"close"}</c> (client): what Dispose does.</item>
/// <item><c>{"action":"bag","mode":"inactive"|"alpha"}</c> (client): how the next preview hides the bag panel (E5).</item>
/// <item><c>{"action":"guard","on":false}</c> (server, E1b persistence read only): leave the inventory sync entries in
/// world saves; back on by itself after one world save, after 5 minutes, and at every Core install.</item>
/// </list>
/// </summary>
internal static class ContainerPreviewOps
{
    private const string BridgeType = "Progenitor.Inventory.ContainerPreviewTestBridge";

    public static object Run(JsonElement args)
    {
        string action = Str(args, "action")?.Trim().ToLowerInvariant() ?? "status";
        return action switch
        {
            "status" => Call("Status"),
            "open" => Call("Open",
                Str(args, "id") ?? throw new ArgumentException("args.id (a container id, c- plus 32 hex digits) is required for open"),
                Bool(args, "keepOpen") ?? false,
                Num(args, "seconds") ?? 30,
                Bool(args, "hideBag") ?? true,
                Num(args, "replicationTimeout") ?? 5,
                Bool(args, "dropEvents") ?? true,
                Bool(args, "hudGate") ?? true,
                Bool(args, "syncInventory") ?? true),
            "close" => Call("Close"),
            "guard" => Call("Guard", Bool(args, "on") ?? throw new ArgumentException("args.on (true or false) is required for guard")),
            "bag" => Call("BagMode", Str(args, "mode")?.Trim().ToLowerInvariant() ?? throw new ArgumentException("args.mode (inactive or alpha) is required for bag")),
            _ => throw new ArgumentException("args.action must be status, open, close, bag or guard"),
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

    private static bool? Bool(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new ArgumentException("args." + name + " must be true or false"),
        };
    }

    private static double? Num(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind != JsonValueKind.Number || !v.TryGetDouble(out double n) || !double.IsFinite(n)) throw new ArgumentException("args." + name + " must be a number");
        return n;
    }
}
