#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;

namespace Progenitor.Install;

/// <summary>
/// AUTOUPDATE3: the dev bridge's <c>auto_update</c> on a server (Dev's <c>AutoUpdateOps</c> calls these by reflection, main
/// thread). Nothing here waits: a check, a backup or a restart starts and <c>status</c> reads how it went. Actions:
/// <c>status</c>, <c>check</c> (one conditional request now), <c>check_cache</c> (a pass on the cached index, no request),
/// <c>tick</c>, <c>start_check</c>, <c>world_loaded</c>, <c>history</c>, <c>restart_now</c> (as an admin's Restart now),
/// <c>cancel_restart</c>, <c>rollback</c> (<c>plugin</c>, <c>package</c>), <c>clear_failed</c> (<c>package</c>),
/// <c>forget_attempts</c>, <c>backup</c> (one pre-update backup under the batch rules, <c>label</c>), <c>notice</c> (send the
/// current notice to the admins online again) and <c>set</c> (<c>key</c>, <c>value</c>: one <c>[AutoUpdate]</c> entry of the
/// server's cfg, written as an operator would).
/// </summary>
internal static class ServerAutoUpdateTestBridge
{
    private static string _lastBackup = "";

    public static Dictionary<string, object?> Status()
    {
        var d = ServerAutoUpdate.Describe();
        d["ok"] = true;
        d["bridgeBackup"] = _lastBackup;
        return d;
    }

    public static Dictionary<string, object?> Check() => Result(ServerAutoUpdate.BeginCheck(true, "bridge"));

    public static Dictionary<string, object?> CheckCache() => Result(ServerAutoUpdate.BeginCheck(false, "bridge (cache)"));

    public static Dictionary<string, object?> Tick() => new() { ["ok"] = true, ["tick"] = ServerAutoUpdate.Tick() };

    public static Dictionary<string, object?> StartCheck() => new() { ["ok"] = true, ["lines"] = ServerAutoUpdate.RunStartCheck() };

    public static Dictionary<string, object?> WorldLoaded()
    {
        ServerAutoUpdate.OnWorldLoaded();
        return new Dictionary<string, object?> { ["ok"] = true };
    }

    public static Dictionary<string, object?> History()
    {
        var d = ServerAutoUpdate.Describe();
        return new Dictionary<string, object?> { ["ok"] = true, ["history"] = d["history"], ["outcomes"] = d["outcomes"] };
    }

    public static Dictionary<string, object?> RestartNow() => Refusal(ServerAutoUpdate.RestartNow("dev bridge"));

    public static Dictionary<string, object?> CancelRestart() => Refusal(ServerAutoUpdate.CancelRestart("dev bridge"));

    public static Dictionary<string, object?> Rollback(string plugin, string package) =>
        new() { ["ok"] = true, ["result"] = ServerAutoUpdate.RollBack(plugin ?? "", package ?? "") };

    public static Dictionary<string, object?> ClearFailed(string package) =>
        new() { ["ok"] = true, ["cleared"] = ServerAutoUpdate.ClearFailed(package ?? "") };

    public static Dictionary<string, object?> ForgetAttempts() => new() { ["ok"] = true, ["forgot"] = ServerAutoUpdate.ForgetAttempts() };

    public static Dictionary<string, object?> Backup(string label)
    {
        _lastBackup = "running";
        var task = ServerAutoUpdate.PreUpdateBackupForBridge(string.IsNullOrWhiteSpace(label) ? "dev bridge" : label);
        task.ContinueWith(t => _lastBackup = t.IsFaulted ? "threw: " + t.Exception?.GetBaseException().Message : t.Result ?? "ok (made, skipped or off: see history)");
        return new Dictionary<string, object?> { ["ok"] = true, ["started"] = true };
    }

    public static Dictionary<string, object?> Notice()
    {
        var n = ServerAutoUpdate.CurrentNotice;
        if (n == null) return new Dictionary<string, object?> { ["ok"] = false, ["error"] = "no current notice" };
        int sent = Networking.JoinAdminServer.SendUpdateNotice(n);
        return new Dictionary<string, object?> { ["ok"] = true, ["sent"] = sent, ["notice"] = n.Describe() };
    }

    public static Dictionary<string, object?> Set(string key, string value)
    {
        string? error = ServerAutoUpdateLive.SetForBridge(key ?? "", value ?? "");
        return error == null
            ? new Dictionary<string, object?> { ["ok"] = true, ["settings"] = ServerAutoUpdateLive.ReadSettings().Describe() }
            : new Dictionary<string, object?> { ["ok"] = false, ["error"] = error };
    }

    private static Dictionary<string, object?> Result(string r) => new() { ["ok"] = r == "started", ["result"] = r };

    private static Dictionary<string, object?> Refusal(string? refusal) =>
        refusal == null ? new Dictionary<string, object?> { ["ok"] = true } : new Dictionary<string, object?> { ["ok"] = false, ["error"] = refusal };
}
#endif
