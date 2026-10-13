using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using Progenitor.Ui;

namespace Progenitor.Dev.Ops;

/// <summary>
/// JOINFLOW1: debug-bridge <c>join_ui</c>, client, main thread. Reads and drives the join screens (the
/// server message, the mods page and the Thunderstore confirmation) through Core's
/// <c>Progenitor.Networking.JoinFlowTestBridge</c>, reached by reflection; every press is the on-screen
/// button's own handler.
///
/// <list type="bullet">
/// <item><c>{"action":"read"}</c></item>
/// <item><c>{"action":"press","button":"continue|leave|back|close|install"}</c></item>
/// <item><c>{"action":"row","id":"&lt;mod id&gt;"}</c> — a required row's action</item>
/// <item><c>{"action":"recommend","key":"&lt;id or package&gt;"}</c> — toggle a recommended checkbox</item>
/// <item><c>{"action":"dont_show","on":true}</c>, <c>{"action":"escape"}</c></item>
/// <item><c>{"action":"show"}</c>, <c>{"action":"preview"}</c>, <c>{"action":"forget_seen"}</c>, <c>{"action":"forget_trust"[,"fingerprint":"…"]}</c> (the Thunderstore confirmations)</item>
/// <item><c>{"action":"render","message":"…","next":"2026-10-10T18:00:00Z","repeat":"None","every":1,"wipeEvery":30,"wipeDay":"Saturday","startDate":"2026-09-01","now":"…"}</c>
/// (RESETWIPE1: <c>wipeEvery</c> 0 is no game wipes, omitted is the joined server's schedule; precision and the passed text are automatic)</item>
/// <item>JOINFLOW2: <c>install</c>, <c>ts_install</c>, <c>join_now</c>, <c>restart</c> (<c>"dryRun":true</c> writes the record and
/// reports the relaunch command without quitting), <c>later</c>, <c>cancel</c>, <c>rejoin_read</c>, <c>rejoin_clear</c>,
/// <c>rejoin_now</c> (the after-relaunch pickup without a restart)</item>
/// <item>JOINTURNON: <c>mod_switch</c> {id, on} — the Mods list's switch without the UI</item>
/// <item>CLIENTPOLICY2: <c>not_allowed</c> {id} — a "Not allowed on this server" row's action; <c>policy</c> — the policy remembered
/// for the server and what the automatic pause takes. Required client mod rows are pressed with <c>row</c> (their id is the
/// admins' row key: plugin GUID, else package).</item>
/// <item>JOINFLOW4 (the version store): <c>versions</c> (also <c>versions_journal</c>), <c>versions_park</c> {id[, restart]},
/// <c>versions_swap</c> {id, version[, restart][, forServer: "" | "current" | key]}, <c>versions_restore</c> {id[, version]},
/// <c>versions_restore_all</c>, <c>versions_forget</c> {id, version[, force]}, <c>versions_prefer</c> {id, version},
/// <c>versions_reconcile</c>, <c>versions_evict</c>, <c>versions_plan</c></item>
/// <item>JOINLINKS1 (links in the server message): <c>link_click</c> {index}, <c>link_button</c> {button: copy|cancel|open},
/// <c>link_dry_run</c> {on} (Open in browser records the URL instead of launching a browser); <c>read</c> returns
/// <c>drawn.links</c>, <c>drawn.linkDialog</c>, <c>drawn.linkOpener</c> and <c>drawn.sections</c></item>
/// </list>
/// </summary>
internal static class JoinUiOps
{
    private const string BridgeType = "Progenitor.Networking.JoinFlowTestBridge";

    public static object Run(JsonElement args)
    {
        if (Progenitor.Plugin.IsServerProcess)
            throw new InvalidOperationException("join_ui runs on the client side only");
        string action = Str(args, "action")?.Trim().ToLowerInvariant() ?? "read";
        switch (action)
        {
            case "read": return Call("Read");
            case "press": return Call("Press", Required(args, "button"));
            case "row": return Call("Row", Required(args, "id"));
            case "recommend": return Call("Recommend", Required(args, "key"));
            case "dont_show": return Call("DontShow", Bool(args, "on", true));
            case "escape": return Call("Escape");
            case "show": return Call("Show");
            case "show_mods": return Call("ShowMods");
            case "preview": return Call("Preview");
            case "forget_seen": return Call("ForgetSeen");
            case "forget_trust": return Call("ForgetTrust", Str(args, "fingerprint"));
            case "render":
                // RESETWIPE1: precision is automatic and the passed text is "now" (precision / passed are ignored);
                // wipeEvery / wipeDay / startDate stand for the game's scheduled wipes (omit wipeEvery: the joined server's).
                return Call("Render", Str(args, "message") ?? "", Str(args, "next") ?? "", Str(args, "repeat") ?? "None",
                    Int(args, "every", 1), Int(args, "wipeEvery", -1), Str(args, "wipeDay") ?? "Any", Str(args, "startDate") ?? "",
                    Str(args, "now") ?? "");
            // JOINFLOW2
            case "install": return Call("Install");
            case "ts_install": return Call("TsInstall");
            case "join_now": return Call("JoinNow");
            case "restart": return Call("Restart", Bool(args, "dryRun", false));
            case "later": return Call("Later");
            case "cancel": return Call("Cancel");
            case "rejoin_read": return Call("RejoinRead");
            case "rejoin_clear": return Call("RejoinClear");
            case "rejoin_now": return Call("RejoinNow");
            // JOINTURNON: the Mods list's switch without the UI (turn a mod off, or on, for a test).
            case "mod_switch": return Call("ModSwitch", Required(args, "id"), Bool(args, "on", false));
            // CLIENTPOLICY2: a "Not allowed on this server" row's action, and the policy this client holds.
            case "not_allowed": return Call("NotAllowed", Required(args, "id"));
            case "not_allowed_confirm": return Call("NotAllowedConfirm", Bool(args, "yes", true));
            case "policy": return Call("Policy");
            // JOINNOTICE: the server-change notice's buttons.
            case "change_notice_view": return Call("ChangeNoticeView");
            case "change_notice_close": return Call("ChangeNoticeClose");
            // JOINFLOW4: the version store (keep both versions).
            case "versions":
            case "versions_journal": return Call("Versions");
            case "versions_park":
                return Call("VersionsRun", "park", Required(args, "id"), "", Bool(args, "restart", false), false, "");
            case "versions_swap":
                return Call("VersionsRun", "swap", Required(args, "id"), Required(args, "version"), Bool(args, "restart", false), false,
                    Str(args, "forServer") ?? "");
            case "versions_restore":
                return Call("VersionsRun", "restore", Required(args, "id"), Str(args, "version") ?? "", false, false, "");
            case "versions_restore_all": return Call("VersionsRun", "restore_all", "", "", false, false, "");
            case "versions_forget":
                return Call("VersionsRun", "forget", Required(args, "id"), Required(args, "version"), false, Bool(args, "force", false), "");
            case "versions_prefer":
                return Call("VersionsRun", "prefer", Required(args, "id"), Required(args, "version"), false, false, "");
            case "versions_reconcile": return Call("VersionsRun", "reconcile", "", "", false, false, "");
            case "versions_evict": return Call("VersionsRun", "evict", "", "", false, false, "");
            case "versions_plan": return Call("VersionsRun", "plan", "", "", false, false, "");
            // JOINLINKS1: links in the server message.
            case "link_click": return Call("LinkClick", Int(args, "index", 0));
            case "link_button": return Call("LinkButton", Required(args, "button"));
            case "link_dry_run": return Call("LinkDryRun", Bool(args, "on", true));
            default:
                throw new ArgumentException(
                    "args.action must be read, press, row, recommend, dont_show, escape, show, show_mods, preview, forget_seen, forget_trust, render, "
                    + "install, ts_install, join_now, restart, later, cancel, rejoin_read, rejoin_clear, rejoin_now, mod_switch, not_allowed, not_allowed_confirm, policy, "
                    + "change_notice_view, change_notice_close, versions, versions_park, "
                    + "versions_swap, versions_restore, versions_restore_all, versions_forget, versions_prefer, versions_reconcile, versions_evict "
                    + "versions_plan, link_click, link_button or link_dry_run");
        }
    }

    private static Dictionary<string, object?> Call(string method, params object?[] args)
    {
        var type = typeof(ModsMenu).Assembly.GetType(BridgeType, throwOnError: false)
            ?? throw new InvalidOperationException("Core has no " + BridgeType + " (older than JOINFLOW1)");
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

    private static string Required(JsonElement args, string name)
    {
        string? v = Str(args, name);
        if (string.IsNullOrWhiteSpace(v)) throw new ArgumentException("args." + name + " is required");
        return v!.Trim();
    }

    private static bool Bool(JsonElement args, string name, bool fallback) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v)
            ? v.ValueKind == JsonValueKind.True || (v.ValueKind != JsonValueKind.False && fallback)
            : fallback;

    private static int Int(JsonElement args, string name, int fallback) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n : fallback;
}
