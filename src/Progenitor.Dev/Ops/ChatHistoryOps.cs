using System;
using System.Reflection;
using System.Text.Json;
using Progenitor.Ui;

namespace Progenitor.Dev.Ops;

/// <summary>
/// Debug-bridge <c>chat_history</c>, client: the chat log's size, the drawn window and the history file's state, and a
/// way to fill this client's log with numbered test lines that nobody else sees (no chat is sent). The work is Core's
/// <c>Progenitor.Chat.ChatView</c>, reached by reflection.
///
/// <list type="bullet">
/// <item><c>{"action":"read"}</c></item>
/// <item><c>{"action":"fill","count":5000}</c>, optionally <c>"kind":"Team"</c> (all lines on one channel)</item>
/// </list>
/// </summary>
internal static class ChatHistoryOps
{
    private const string CoreType = "Progenitor.Chat.ChatView";

    public static object Run(JsonElement args)
    {
        if (Progenitor.Plugin.IsServerProcess) throw new InvalidOperationException("chat_history runs on the client only");
        string action = Str(args, "action")?.Trim().ToLowerInvariant() ?? "read";
        var type = typeof(ModsMenu).Assembly.GetType(CoreType, throwOnError: false)
            ?? throw new InvalidOperationException("Core has no " + CoreType);
        switch (action)
        {
            case "":
            case "read":
                return Invoke(type, "DevDescribe")!;
            case "fill":
            {
                int count = Int(args, "count") ?? throw new ArgumentException("args.count is required (1 to 5000)");
                return Invoke(type, "DevFill", count, Str(args, "kind"))!;
            }
            default:
                throw new ArgumentException("args.action must be read or fill");
        }
    }

    private static object? Invoke(Type type, string method, params object?[] args)
    {
        var mi = type.GetMethod(method, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException(CoreType + "." + method + " not found (Core older than the 5,000-line history)");
        try { return mi.Invoke(null, args); }
        catch (TargetInvocationException e) { throw new InvalidOperationException(e.InnerException?.Message ?? e.Message); }
    }

    private static string? Str(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n : null;
}
