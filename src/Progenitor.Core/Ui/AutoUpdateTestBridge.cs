using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>
/// AUTOUPDATE1: the dev bridge's <c>auto_update</c> op, called by reflection (Core grants Dev no
/// <c>InternalsVisibleTo</c>); not a plugin API and no network path reaches it. Client only; main thread. The update and
/// Update all actions open the same confirmation the buttons do: answer it with <c>mods_ui dialog_choose</c>. The pre-join
/// prompt itself is driven by the bridge's <c>connect</c> (it calls the same <c>GameHelper.LaunchGame</c> a join does).
/// </summary>
internal static class AutoUpdateTestBridge
{
    public static Dictionary<string, object?> Status() => ModsMenu.DescribeUpdates();

    public static Dictionary<string, object?> Recheck() => ModsMenu.UpdatesAction("recheck", null);

    public static Dictionary<string, object?> Pin(string package) => ModsMenu.UpdatesAction("pin", package);

    public static Dictionary<string, object?> Unpin(string package) => ModsMenu.UpdatesAction("unpin", package);

    public static Dictionary<string, object?> Update(string package) => ModsMenu.UpdatesAction("update", package);

    public static Dictionary<string, object?> UpdateAll() => ModsMenu.UpdatesAction("update_all", null);

    public static Dictionary<string, object?> PrejoinPreview(string host, int port) =>
        ModsMenu.UpdatesAction("prejoin_preview", null, host, port);

    public static Dictionary<string, object?> PrejoinForget() => ModsMenu.UpdatesAction("prejoin_forget", null);

    // AUTOUPDATE2 (phase 2).
    public static Dictionary<string, object?> Screenshot(string path) => ModsMenu.UpdatesAction("screenshot", null, choice: path);

    public static Dictionary<string, object?> Check() => ModsMenu.UpdatesAction("check", null);

    public static Dictionary<string, object?> AutoRun() => ModsMenu.UpdatesAction("auto_run", null);

    public static Dictionary<string, object?> Rollback(string plugin, string package) =>
        ModsMenu.UpdatesAction("rollback", package, plugin: plugin);

    public static Dictionary<string, object?> Mode(string package, string choice) =>
        ModsMenu.UpdatesAction("mode", package, choice: choice);

    public static Dictionary<string, object?> ClearFailed(string package) => ModsMenu.UpdatesAction("clear_failed", package);

    public static Dictionary<string, object?> ForgetAttempts() => ModsMenu.UpdatesAction("forget_attempts", null);

    public static Dictionary<string, object?> StartCheck() => ModsMenu.UpdatesAction("start_check", null);

    public static Dictionary<string, object?> SubtitleDot(string choice) => ModsMenu.UpdatesAction("subtitle_dot", null, choice: choice);

    // AUTOUPDATE5 (phase 5): Progenitor's own update. Both sides (a server has no Mods window; these read SelfUpdates only).

    /// <summary><c>self_status</c>: the running copy, the record, the offer, the journal, the failed list and the kept releases.</summary>
    public static Dictionary<string, object?> SelfStatus() => Progenitor.Install.SelfUpdates.Describe();

    /// <summary>
    /// <c>self_check</c>: read the cached index again (no request) and recompute the offer; with <c>fetch</c> true, first one
    /// conditional index request (Check now, owner <c>auto-update</c>).
    /// </summary>
    public static Dictionary<string, object?> SelfCheck(bool fetch)
    {
        if (fetch)
        {
            _ = Progenitor.Install.SelfUpdates.CheckNow();
            return SelfResult(true, "one conditional index request queued; detection follows (read self_status)");
        }

        Progenitor.Install.SelfUpdates.Detect("bridge");
        return SelfResult(true, "detection queued on the cached index (no request)");
    }

    /// <summary><c>self_apply</c>: install the offered release now, as the Install tab's Update (the window still applies).</summary>
    public static Dictionary<string, object?> SelfApply()
    {
        bool ok = Progenitor.Install.SelfUpdates.Begin(Progenitor.Install.SelfUpdates.Offer, "bridge", byClick: true, out string message);
        return SelfResult(ok, message);
    }

    /// <summary><c>self_select</c>: which kept release an Exact server's Core (<c>version</c>, optional <c>sha256</c>) would use.</summary>
    public static Dictionary<string, object?> SelfSelect(string version, string sha256)
    {
        string root = BepInExRoot();
        var kept = root.Length == 0 ? new List<Progenitor.SelfUpdate.SelfKept>() : Progenitor.Install.SelfStore.List(root);
        var pick = Progenitor.Install.SelfStore.Select(kept, version, string.IsNullOrWhiteSpace(sha256) ? null : sha256);
        var r = SelfResult(pick != null, pick == null ? "no kept release matches " + version : "kept: " + pick.Dir);
        r["selected"] = pick?.Describe();
        return r;
    }

    /// <summary><c>self_clear_failed</c>: the failed versions are tried again.</summary>
    public static Dictionary<string, object?> SelfClearFailed() =>
        SelfResult(true, "cleared " + Progenitor.Install.SelfUpdates.ClearFailed() + " failed version(s)");

    /// <summary><c>self_forget_attempts</c>: this session's attempts are forgotten (a pass may retry).</summary>
    public static Dictionary<string, object?> SelfForgetAttempts() =>
        SelfResult(true, "forgot " + Progenitor.Install.SelfUpdates.ForgetAttempts() + " attempt(s)");

    private static string BepInExRoot()
    {
#if PROGENITOR_TESTS
        return "";
#else
        return BepInEx.Paths.BepInExRootPath ?? "";
#endif
    }

    private static Dictionary<string, object?> SelfResult(bool ok, string message)
    {
        var r = Progenitor.Install.SelfUpdates.Describe();
        r["ok"] = ok;
        r["message"] = message;
        return r;
    }
}
