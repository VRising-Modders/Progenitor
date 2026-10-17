using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using Progenitor.Ui;

namespace Progenitor.Dev.Ops;

/// <summary>
/// CHAT12: debug-bridge <c>chat_tabs</c>, client. Reads the chat tab row as drawn: the art source (the game's menu tab
/// or the kit), and per item its caption, state (Normal, Hover, Active, Dragged), unread mark, font and screen rect, so
/// a <c>mouse</c> move, click or drag can aim at a tab. <c>move</c> moves a tab the way the quick menu's Move left /
/// right does. The work is Core's <c>Progenitor.Chat.ChatTabs</c>, reached by reflection.
///
/// <list type="bullet">
/// <item><c>{"action":"read"}</c></item>
/// <item><c>{"action":"move","from":2,"to":0}</c></item>
/// <item><c>{"action":"edit","tab":1}</c> (FORMDIALOG1: the tab editor; <c>-1</c>: a new custom tab)</item>
/// <item><c>{"action":"select","index":1}</c>: CHAT13, show a tab as a click does.</item>
/// <item><c>{"action":"line","text":"hello"}</c>: CHAT13, put text in the chat input line.</item>
/// </list>
/// </summary>
internal static class ChatTabsOps
{
    private const string CoreType = "Progenitor.Chat.ChatTabs";

    public static object Run(JsonElement args)
    {
        if (Progenitor.Plugin.IsServerProcess) throw new InvalidOperationException("chat_tabs runs on the client only");
        string action = Str(args, "action")?.Trim().ToLowerInvariant() ?? "read";
        var type = typeof(ModsMenu).Assembly.GetType(CoreType, throwOnError: false)
            ?? throw new InvalidOperationException("Core has no " + CoreType);
        switch (action)
        {
            case "":
            case "read":
                return Invoke(type, "Describe");
            case "move":
            {
                int from = Int(args, "from") ?? throw new ArgumentException("args.from is required");
                int to = Int(args, "to") ?? throw new ArgumentException("args.to is required");
                bool moved = Invoke(type, "MoveTab", from, to) is true;
                var reply = Invoke(type, "Describe") as Dictionary<string, object?> ?? new Dictionary<string, object?>();
                reply["moved"] = moved;
                return reply;
            }
            case "edit":
            {
                // FORMDIALOG1: the tab editor, as the quick menu's Edit this tab (tab N) or New tab, Custom (tab -1).
                int tab = Int(args, "tab") ?? throw new ArgumentException("args.tab is required (a tab index, or -1 for a new tab)");
                var reply = Invoke(type, "OpenEditorForBridge", tab) as Dictionary<string, object?> ?? new Dictionary<string, object?>();
                return new Dictionary<string, object?> { ["opened"] = true, ["dialog"] = reply };
            }
            case "select":
            {
                // CHAT13: show a tab as a click does (its draft and send channel come with it).
                int index = Int(args, "index") ?? throw new ArgumentException("args.index is required");
                bool selected = Invoke(type, "SelectTab", index) is true;
                var reply = Invoke(type, "Describe") as Dictionary<string, object?> ?? new Dictionary<string, object?>();
                reply["selected"] = selected;
                return reply;
            }
            case "line":
            {
                // CHAT13: put text in the chat input line, as typing leaves it.
                string text = Str(args, "text") ?? throw new ArgumentException("args.text is required");
                bool set = Invoke(type, "SetInputLine", text) is true;
                var reply = Invoke(type, "Describe") as Dictionary<string, object?> ?? new Dictionary<string, object?>();
                reply["set"] = set;
                return reply;
            }
            default:
                throw new ArgumentException("args.action must be read, move, edit, select or line");
        }
    }

    private static object? Invoke(Type type, string method, params object?[] args)
    {
        var mi = type.GetMethod(method, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException(CoreType + "." + method + " not found (Core older than CHAT12)");
        try { return mi.Invoke(null, args); }
        catch (TargetInvocationException e) { throw new InvalidOperationException(e.InnerException?.Message ?? e.Message); }
    }

    private static string? Str(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n : null;
}
