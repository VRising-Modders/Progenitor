using System;
using System.Text.Json;

namespace Progenitor.Dev.Ops;

/// <summary>A parsed <c>messages_record</c> request.</summary>
internal sealed class MessagesRecordRequest
{
    /// <summary><c>on</c>, <c>off</c> or <c>status</c>.</summary>
    public string Action { get; init; } = "status";

    /// <summary>The mod GUID to record, or <c>*</c> for every mod (required for <c>on</c>).</summary>
    public string Mod { get; init; } = "";

    public int Seconds { get; init; }
    public int MaxMessages { get; init; }
    public int MaxBytes { get; init; }
}

/// <summary>
/// Mod compatibility C5: game-free argument parsing for the debug-bridge <c>messages_record</c> op, so the test project
/// compiles it. Shapes: <c>{}</c> or <c>{"action":"status"}</c>; <c>{"action":"on","mod":"example.trade","seconds":120,
/// "maxMessages":200,"maxBytes":1048576}</c> (every bound optional, each capped: 1800 s, 5000 fixtures, 16 MiB);
/// <c>{"action":"off"}</c>. <c>on</c> needs <c>mod</c>: a GUID, or <c>*</c> to record every mod.
/// </summary>
internal static class MessagesRecordArgs
{
    public static MessagesRecordRequest Parse(JsonElement args)
    {
        string action = (Str(args, "action") ?? "status").Trim().ToLowerInvariant();
        switch (action)
        {
            case "on":
            case "start":
                action = "on";
                break;
            case "off":
            case "stop":
                action = "off";
                break;
            case "status":
            case "":
                action = "status";
                break;
            default:
                throw new ArgumentException("args.action must be on, off or status");
        }

        string mod = (Str(args, "mod") ?? "").Trim();
        // Recording every mod is a deliberate choice: it has to be asked for with "*".
        if (action == "on" && mod.Length == 0) throw new ArgumentException("args.mod is required for on: a mod GUID, or \"*\" for every mod");
        if (mod.Length > 128) throw new ArgumentException("args.mod is longer than 128 characters");
        int seconds = Int(args, "seconds"), messages = Int(args, "maxMessages"), bytes = Int(args, "maxBytes");
        if (seconds < 0 || messages < 0 || bytes < 0) throw new ArgumentException("args.seconds, args.maxMessages and args.maxBytes must not be negative");
        return new MessagesRecordRequest { Action = action, Mod = mod, Seconds = seconds, MaxMessages = messages, MaxBytes = bytes };
    }

    private static string? Str(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int Int(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var v)) return 0;
        if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out int n))
            throw new ArgumentException("args." + name + " must be a whole number");
        return n;
    }
}
