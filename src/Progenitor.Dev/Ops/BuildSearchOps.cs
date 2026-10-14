using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using Progenitor.Ui;

namespace Progenitor.Dev.Ops;

/// <summary>
/// BUILDSEARCH1: debug-bridge <c>build_search</c>, client, main thread. Reads and drives the search box
/// Core adds to the game's build menu, through Core's <c>Progenitor.Ui.BuildSearchTestBridge</c> by
/// reflection. The build menu must be open for every action but <c>status</c>, <c>open</c> and <c>close</c>.
///
/// <list type="bullet">
/// <item><c>{}</c> or <c>{"action":"status","cells":40}</c>: open/attached/focus state, the query, the
/// visible and total counts, and up to <c>cells</c> cells with their names and whether the search hid them.</item>
/// <item><c>{"action":"open"}</c> / <c>{"action":"close"}</c>: open or close the game's build menu as the B
/// key does, with no input or focus (the open takes effect on the next frame; read <c>status</c>).</item>
/// <item><c>{"action":"hover","index":0}</c>: show row cell 0's tooltip without moving the mouse (-1 clears).</item>
/// <item><c>{"action":"set","text":"wall"}</c>: write the query through the field's own edit path.</item>
/// <item><c>{"action":"clear"}</c>: what the x button does (empty the query, keep focus).</item>
/// <item><c>{"action":"focus"}</c> / <c>{"action":"blur"}</c>: give or take the keyboard, as a click does.</item>
/// <item><c>{"action":"type","text":"wall","keys":["Escape"]}</c>: with focus, queue text and then chords
/// (<c>mods_ui type</c> syntax) for the typed-key pump, one a frame; read back with <c>status</c>.</item>
/// </list>
/// </summary>
internal static class BuildSearchOps
{
    private const string BridgeType = "Progenitor.Ui.BuildSearchTestBridge";

    public static object Run(JsonElement args)
    {
        if (Progenitor.Plugin.IsServerProcess)
            throw new InvalidOperationException("build_search runs on the client side only");
        string action = Str(args, "action")?.Trim().ToLowerInvariant() ?? "status";
        return action switch
        {
            "status" => Call("Status", Int(args, "cells") ?? 40),
            "set" => Call("Set", Str(args, "text") ?? throw new ArgumentException("args.text is required for set")),
            "clear" => Call("Clear"),
            "focus" => Call("Focus", true),
            "blur" => Call("Focus", false),
            "type" => Call("Type", Str(args, "text"), Keys(args)),
            "open" => Call("OpenMenu"),
            "close" => Call("CloseMenu"),
            "hover" => Call("Hover", Int(args, "index") ?? throw new ArgumentException("args.index is required for hover (-1 for none)")),
            "select" => Call("Select", Int(args, "index") ?? throw new ArgumentException("args.index is required for select")),
            _ => throw new ArgumentException("args.action must be status, open, close, hover, select, set, clear, focus, blur or type"),
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

    private static string[]? Keys(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("keys", out var v)) return null;
        if (v.ValueKind != JsonValueKind.Array) throw new ArgumentException("args.keys must be an array of chords");
        var list = new List<string>();
        foreach (var item in v.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) throw new ArgumentException("args.keys entries must be strings");
            list.Add(item.GetString() ?? "");
        }

        return list.ToArray();
    }

    private static int? Int(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out int n)) throw new ArgumentException("args." + name + " must be an integer");
        return Math.Clamp(n, -1, 500);
    }
}
