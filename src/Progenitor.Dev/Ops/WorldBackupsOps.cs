using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Progenitor.Ui;

namespace Progenitor.Dev.Ops;

/// <summary>
/// WORLDRESTORE1: debug-bridge <c>world_backups</c>, main thread, through Core's
/// <c>Progenitor.Networking.ServerBackupsTestBridge</c> by reflection.
/// <list type="bullet">
/// <item>Server: <c>list</c> (restore points with paths, sizes, the wipe facts), <c>request</c> (<c>id</c>, optional
/// <c>wipe</c> none / clock-today / wipes-off / date, <c>date</c> yyyy-MM-dd, <c>restartNow</c>; <c>dryRun</c> defaults to
/// <b>true</b>: pass <c>"dryRun": false</c> to write the request file), <c>pending</c>, <c>result</c>, <c>cancel</c>; BACKUPNOW1
/// <c>backup_now</c> (dryRun default true), <c>schedule</c>, <c>retention</c> (a preview; nothing is deleted).</item>
/// <item>Client (the admin path the server audits): <c>read</c>, <c>list</c>, <c>request</c> (as on the server; dryRun
/// default true), <c>cancel</c>, <c>open</c> (the Backups list, as the Show button), <c>dialog_date</c> (<c>date</c>: the
/// wipe-date dialog's day); BACKUPNOW1 <c>backup_now</c>, <c>set</c> (<c>key</c>, <c>value</c>), <c>open_backup_now</c>. Drive the dialogs with <c>mods_ui dialog_choose</c> / <c>dialog_type</c>.</item>
/// </list>
/// A restore applies only at the next server start (Bootstrap).
/// </summary>
internal static class WorldBackupsOps
{
    private const string BridgeType = "Progenitor.Networking.ServerBackupsTestBridge";
    private const int AnswerWaitMs = 10_000;

    public static object Run(JsonElement args)
    {
        string action = Str(args, "action")?.Trim().ToLowerInvariant() ?? (Progenitor.Plugin.IsServerProcess ? "list" : "read");
        bool dryRun = !(args.ValueKind == JsonValueKind.Object && args.TryGetProperty("dryRun", out var dr) && dr.ValueKind == JsonValueKind.False);
        bool restartNow = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("restartNow", out var rn) && rn.ValueKind == JsonValueKind.True;
        if (Progenitor.Plugin.IsServerProcess)
        {
            return action switch
            {
                "list" => Call("ServerList"),
                "request" => Call("ServerRequest", Need(args, "id"), Str(args, "wipe") ?? "", Str(args, "date") ?? "", restartNow, dryRun),
                "pending" => Call("ServerPending"),
                "result" => Call("ServerResult"),
                "cancel" => Call("ServerCancel"),
                "backup_now" => Call("ServerBackupNow", dryRun),
                "schedule" => Call("ServerSchedule"),
                "retention" => Call("ServerRetention"),
                _ => throw new ArgumentException("on the server, args.action must be list, request, pending, result, cancel, backup_now, schedule or retention"),
            };
        }

        return action switch
        {
            "read" => Call("Read"),
            "list" => Wait(Call("List")),
            "request" => Wait(Call("Request", Need(args, "id"), Str(args, "wipe") ?? "", Str(args, "date") ?? "", restartNow, dryRun)),
            "cancel" => Wait(Call("Cancel")),
            "open" => Wait(Call("Open")),
            "dialog_date" => Call("DialogDate", Need(args, "date")),
            "backup_now" => Wait(Call("BackupNow", dryRun)),
            "set" => Wait(Call("Set", Need(args, "key"), Need(args, "value"))),
            "open_backup_now" => Call("OpenBackupNow"),
            _ => throw new ArgumentException("on the client, args.action must be read, list, request, cancel, open, dialog_date, backup_now, set or open_backup_now"),
        };
    }

    private static string Need(JsonElement args, string name) => Str(args, name) ?? throw new ArgumentException("args." + name + " is required");

    private static int Pending(Dictionary<string, object?> d) => d.TryGetValue("pending_requests", out var p) && p is int n ? n : 0;

    private static object Wait(Dictionary<string, object?> reply)
    {
        if (Pending(reply) == 0) return reply;
        var deferred = new DeferredResult(AnswerWaitMs + 2_000);
        var clock = Stopwatch.StartNew();
        void Poll()
        {
            try
            {
                if (deferred.BoundGate != null && deferred.BoundGate.ShouldSkip(deferred.BoundTicket)) return;
                var now = Call("Read");
                if (Pending(now) > 0 && clock.ElapsedMilliseconds < AnswerWaitMs)
                {
                    MainThread.PostDelayed(0, Poll, deferred);
                    return;
                }

                now["result"] = reply.TryGetValue("result", out var r) ? r : null;
                now["waitedMs"] = clock.ElapsedMilliseconds;
                deferred.SetResult(now);
            }
            catch (Exception e)
            {
                if (!deferred.Completion.Task.IsCompleted)
                    deferred.SetResult(new Dictionary<string, object?> { ["ok"] = false, ["error"] = e.Message });
            }
        }

        MainThread.PostDelayed(0, Poll, deferred);
        return deferred;
    }

    private static Dictionary<string, object?> Call(string method, params object?[] args)
    {
        var type = typeof(ModsMenu).Assembly.GetType(BridgeType, throwOnError: false)
            ?? throw new InvalidOperationException("Core has no " + BridgeType + " (older than the world restore)");
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
