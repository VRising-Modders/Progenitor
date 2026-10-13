using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using Progenitor.Ui;

namespace Progenitor.Dev.Ops;

/// <summary>
/// AUTOUPDATE1: debug-bridge <c>auto_update</c>, client only, main thread, through Core's
/// <c>Progenitor.Ui.AutoUpdateTestBridge</c> by reflection. Actions: <c>status</c> (default: the update list, the badge
/// as drawn, the Install tab list stamp, the pre-join prompt's state and last decision), <c>recheck</c> (a detection
/// pass on the index already read; never a request), <c>pin</c> / <c>unpin</c> (<c>package</c>), <c>update</c>
/// (<c>package</c>) and <c>update_all</c> (each opens the confirmation; answer it with <c>mods_ui dialog_choose</c>),
/// <c>prejoin_preview</c> (<c>host</c>, <c>port</c>: what the prompt would offer, nothing launched) and
/// <c>prejoin_forget</c> (forget this session's Join anyway answers). The prompt itself is shown by <c>connect</c>.
/// AUTOUPDATE2: <c>check</c> (one index request now, as the Install tab's check), <c>screenshot</c> (<c>path</c>: the game's own frame to a .png, no focus), <c>auto_run</c> (an automatic pass now, when the apply window is open), <c>rollback</c> (<c>plugin</c>,
/// optional <c>package</c>: the mod page's Roll back), <c>mode</c> (<c>package</c>, <c>choice</c> Global, Automatic, Notify,
/// Off or Keep), <c>clear_failed</c> (<c>package</c>), <c>forget_attempts</c>, <c>start_check</c> and <c>subtitle_dot</c>
/// (<c>choice</c> on/off). <c>status</c> carries <c>auto</c> (window, due, notice, modes, pins, failed list, start checks).
/// AUTOUPDATE5: <c>self_status</c>, <c>self_check</c>, <c>self_apply</c>, <c>self_select</c>, <c>self_clear_failed</c> and
/// <c>self_forget_attempts</c> (Progenitor's own update; client and server). <c>status</c> also carries <c>self</c>.
/// </summary>
internal static class AutoUpdateOps
{
    private const string BridgeType = "Progenitor.Ui.AutoUpdateTestBridge";

    /// <summary>AUTOUPDATE3: the server's half (Core's <c>Progenitor.Install.ServerAutoUpdateTestBridge</c>).</summary>
    private const string ServerBridgeType = "Progenitor.Install.ServerAutoUpdateTestBridge";

    public static object Run(JsonElement args)
    {
        string action = Str(args, "action")?.Trim().ToLowerInvariant() ?? "status";
        // AUTOUPDATE5: Progenitor's own update, on either side (before the server's mod-update actions).
        if (action.StartsWith("self_", StringComparison.Ordinal)) return RunSelf(action, args);
        if (Progenitor.Plugin.IsServerProcess) return RunServer(action, args);
        return action switch
        {
            "status" => Call("Status"),
            "recheck" => Call("Recheck"),
            "pin" => Call("Pin", Need(args, "package")),
            "unpin" => Call("Unpin", Need(args, "package")),
            "update" => Call("Update", Need(args, "package")),
            "update_all" => Call("UpdateAll"),
            "prejoin_preview" => Call("PrejoinPreview", Need(args, "host"), Int(args, "port", 9876)),
            "prejoin_forget" => Call("PrejoinForget"),
            // AUTOUPDATE2 (phase 2).
            "check" => Call("Check"),
            "screenshot" => Call("Screenshot", Need(args, "path")),
            "auto_run" => Call("AutoRun"),
            "rollback" => Call("Rollback", Need(args, "plugin"), Str(args, "package") ?? ""),
            "mode" => Call("Mode", Need(args, "package"), Need(args, "choice")),
            "clear_failed" => Call("ClearFailed", Need(args, "package")),
            "forget_attempts" => Call("ForgetAttempts"),
            "start_check" => Call("StartCheck"),
            "subtitle_dot" => Call("SubtitleDot", Need(args, "choice")),
            _ => throw new ArgumentException("args.action must be status, recheck, pin, unpin, update, update_all, prejoin_preview, prejoin_forget, "
                + "check, screenshot, auto_run, rollback, mode, clear_failed, forget_attempts, start_check or subtitle_dot"),
        };
    }

    /// <summary>
    /// AUTOUPDATE3, server: <c>status</c> (default), <c>check</c> (one conditional index request now), <c>check_cache</c> (a pass
    /// on the cached index), <c>tick</c>, <c>start_check</c>, <c>world_loaded</c>, <c>history</c>, <c>restart_now</c>,
    /// <c>cancel_restart</c>, <c>rollback</c> (<c>plugin</c>, <c>package</c>), <c>clear_failed</c> (<c>package</c>),
    /// <c>forget_attempts</c>, <c>backup</c> (<c>label</c>), <c>notice</c> and <c>set</c> (<c>key</c>, <c>value</c>: an
    /// <c>[AutoUpdate]</c> entry). Nothing waits: read <c>status</c> for the result.
    /// </summary>
    private static object RunServer(string action, JsonElement args) => action switch
    {
        "status" or "plan" => CallServer("Status"),
        "check" or "apply" => CallServer("Check"),
        "check_cache" => CallServer("CheckCache"),
        "tick" => CallServer("Tick"),
        "start_check" => CallServer("StartCheck"),
        "world_loaded" => CallServer("WorldLoaded"),
        "history" => CallServer("History"),
        "restart_now" => CallServer("RestartNow"),
        "cancel_restart" => CallServer("CancelRestart"),
        "rollback" => CallServer("Rollback", Need(args, "plugin"), Str(args, "package") ?? ""),
        "clear_failed" => CallServer("ClearFailed", Need(args, "package")),
        "forget_attempts" => CallServer("ForgetAttempts"),
        "backup" => CallServer("Backup", Str(args, "label") ?? ""),
        "notice" => CallServer("Notice"),
        "set" => CallServer("Set", Need(args, "key"), Str(args, "value") ?? ""),
        _ => throw new ArgumentException("on a server args.action must be status, check, check_cache, tick, start_check, world_loaded, history, "
            + "restart_now, cancel_restart, rollback, clear_failed, forget_attempts, backup, notice or set"),
    };

    private static Dictionary<string, object?> CallServer(string method, params object?[] args) => CallType(ServerBridgeType, method, args);

    /// <summary>AUTOUPDATE5: <c>self_status</c>, <c>self_check</c> (optional <c>fetch</c>: one conditional index request first), <c>self_apply</c>, <c>self_select</c> (<c>version</c>, optional <c>sha256</c>), <c>self_clear_failed</c>, <c>self_forget_attempts</c>.</summary>
    private static Dictionary<string, object?> RunSelf(string action, JsonElement args) => action switch
    {
        "self_status" => Call("SelfStatus"),
        "self_check" => Call("SelfCheck", Bool(args, "fetch")),
        "self_apply" => Call("SelfApply"),
        "self_select" => Call("SelfSelect", Need(args, "version"), Str(args, "sha256") ?? ""),
        "self_clear_failed" => Call("SelfClearFailed"),
        "self_forget_attempts" => Call("SelfForgetAttempts"),
        _ => throw new ArgumentException("self actions: self_status, self_check, self_apply, self_select, self_clear_failed, self_forget_attempts"),
    };

    private static bool Bool(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static string Need(JsonElement args, string name) => Str(args, name) ?? throw new ArgumentException("args." + name + " is required");

    private static int Int(JsonElement args, string name, int fallback) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n)
            ? n : fallback;

    private static Dictionary<string, object?> Call(string method, params object?[] args) => CallType(BridgeType, method, args);

    private static Dictionary<string, object?> CallType(string typeName, string method, params object?[] args)
    {
        var type = typeof(ModsMenu).Assembly.GetType(typeName, throwOnError: false)
            ?? throw new InvalidOperationException("Core has no " + typeName + " (older than this auto-update phase)");
        var mi = type.GetMethod(method, BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException(typeName + "." + method + " not found");
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
