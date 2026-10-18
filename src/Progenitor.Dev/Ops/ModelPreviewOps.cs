using System;
using System.Reflection;
using System.Text.Json;

namespace Progenitor.Dev.Ops;

internal static class ModelPreviewOps
{
    public static object Run(JsonElement args)
    {
        if (Progenitor.Plugin.IsServerProcess) throw new InvalidOperationException("model_preview is client-only");
        string action = args.TryGetProperty("action", out var a) ? a.GetString() ?? "status" : "status";
        return action switch
        {
            "open" => Call("Open", new object?[] {
                args.GetProperty("name").GetString(),
                args.TryGetProperty("section", out var s) ? s.GetString() : null,
                args.TryGetProperty("seconds", out var d) ? d.GetInt32() : 30 }),
            "close" => Call("Close", null),
            "status" => Call("Status", null),
            _ => throw new ArgumentException("action must be open, status or close"),
        };
    }

    public static void Shutdown() => Call("Close", null);

    private static object Call(string method, object?[]? args)
    {
        var type = typeof(Progenitor.Plugin).Assembly.GetType("Progenitor.Models.ModelPreviewBridge", true)!;
        try { return type.GetMethod(method, BindingFlags.Static | BindingFlags.Public)!.Invoke(null, args)!; }
        catch (TargetInvocationException e) { throw new InvalidOperationException(e.InnerException?.Message ?? e.Message); }
    }
}
