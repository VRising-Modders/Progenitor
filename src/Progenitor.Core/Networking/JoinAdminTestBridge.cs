using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
#if !PROGENITOR_TESTS
using Progenitor.Ecs;
#endif

namespace Progenitor.Networking;

/// <summary>
/// JOINADMIN1: the Core half of the dev bridge's <c>join_admin</c> op. Dev reaches this type by reflection; no
/// plugin or network path reaches it. Client actions go through the panel's own handlers (the same code the
/// buttons run), so a bridge press is the button's path; every request is still decided by the server. Server
/// actions read the store and the refused-join record, and inject a synthetic refusal (test only).
/// </summary>
internal static class JoinAdminTestBridge
{
    // ---- client -----------------------------------------------------------------------------

    public static Dictionary<string, object?> Read()
    {
        var d = JoinAdminPanel.Describe();
        d["ok"] = true;
        d["side"] = "client";
        return d;
    }

    public static Dictionary<string, object?> Open() => After(JoinAdminPanel.Open());

    public static Dictionary<string, object?> Close() => After(JoinAdminPanel.Close());

    public static Dictionary<string, object?> Refresh() => After(JoinAdminPanel.Refresh());

    public static Dictionary<string, object?> Escape() => After(JoinAdminPanel.Escape());

    public static Dictionary<string, object?> Page(string page) => After(page.Trim().ToLowerInvariant() switch
    {
        "pick-recommend" or "recommend" => JoinAdminPanel.ShowPage(JoinAdminPanel.Page.PickRecommend),
        "pick-require" or "require" => JoinAdminPanel.ShowPage(JoinAdminPanel.Page.PickRequire),
        "pick-approve" or "approve" => JoinAdminPanel.ShowPage(JoinAdminPanel.Page.PickApprove),
        "confirm-approve-all" => JoinAdminPanel.ShowPage(JoinAdminPanel.Page.ConfirmApproveAll),
        _ => JoinAdminPanel.ShowPage(JoinAdminPanel.Page.Main),
    });

    public static Dictionary<string, object?> Search(string text) => After(JoinAdminPanel.SetSearch(text));

    public static Dictionary<string, object?> Pick(int index) => After(JoinAdminPanel.PickAt(index));

    public static Dictionary<string, object?> Recommend(string id, string package, string version, string reason, string name) =>
        After(JoinAdminPanel.AddRecommendation(id, package, version, reason, name));

    public static Dictionary<string, object?> Unrecommend(string key) => After(JoinAdminPanel.RemoveRecommendation(key));

    public static Dictionary<string, object?> Reason(string key, string reason) => After(JoinAdminPanel.SetReason(key, reason));

    public static Dictionary<string, object?> Window(int seconds) => After(JoinAdminPanel.SetWindow(seconds));

    public static Dictionary<string, object?> Update(string mod) => After(JoinAdminPanel.UpdateOnServer(mod));

    // ---- JOINDRAFT1: the edit actions above stage; these send, reset or undo the draft ------------------------------

    /// <summary>The panel's Apply: sends the whole draft in order (the bridge op waits until the run ends).</summary>
    public static Dictionary<string, object?> Apply() => After(JoinAdminPanel.Apply());

    /// <summary>The panel's Discard: the draft returns to the server's state.</summary>
    public static Dictionary<string, object?> Discard() => After(JoinAdminPanel.Discard());

    /// <summary>The Changes list's × on line <paramref name="index"/> (0 and up).</summary>
    public static Dictionary<string, object?> UndoChange(int index) => After(JoinAdminPanel.UndoChange(index));

    /// <summary>
    /// Send one request as a client that skips the panel (a non-admin has no button): the server decides it like
    /// any other, so this is how the non-admin refusal is tested.
    /// </summary>
    public static Dictionary<string, object?> Request(string op, string id, string package, string version, string reason, int? seconds) =>
        After(JoinAdminPanel.Send(new JoinAdminRequest
        {
            Op = op, Id = id, Package = package, Version = version, Reason = reason,
            Seconds = seconds ?? 0, HasSeconds = seconds != null,
        }, honorLock: false));

    // ---- CLIENTPOLICY1: client (the panel's own handlers) --------------------------------------------------------

    public static Dictionary<string, object?> RequireAdd(string id, string package, string version, string rule, string reason, string name) =>
        After(JoinAdminPanel.AddRequired(id, package, version, rule, reason, name));

    public static Dictionary<string, object?> RequireRemove(string key) => After(JoinAdminPanel.RemoveRequired(key));

    public static Dictionary<string, object?> RequireReason(string key, string reason) => After(JoinAdminPanel.SetRequiredReason(key, reason));

    /// <summary>Set a required row's rule (empty: the row's Rule button, which cycles), optionally with a new version.</summary>
    public static Dictionary<string, object?> RequireRule(string key, string rule, string version) => After(JoinAdminPanel.SetRequiredRule(key, rule, version));

    public static Dictionary<string, object?> PolicyMode(string value) => After(JoinAdminPanel.SetPolicyMode(value));

    public static Dictionary<string, object?> Unverified(string value) => After(JoinAdminPanel.SetUnverified(value));

    public static Dictionary<string, object?> ApproveAdd(string id, string package, string version, string rule, string name) =>
        After(JoinAdminPanel.AddApproved(id, package, version, rule, name));

    public static Dictionary<string, object?> ApproveRemove(string key) => After(JoinAdminPanel.RemoveApproved(key));

    /// <summary>"Approve everything I have": opens the confirmation that lists this PC's client mods (nothing is sent yet).</summary>
    public static Dictionary<string, object?> ApproveEverything() => After(JoinAdminPanel.ApproveEverything());

    /// <summary>The confirmation's Approve all.</summary>
    public static Dictionary<string, object?> ApproveEverythingConfirm() => After(JoinAdminPanel.ConfirmApproveEverything());

    public static Dictionary<string, object?> ApprovedPage(bool forward) => After(JoinAdminPanel.PageApproved(forward));

    // ---- CLIENTPOLICY3: client (the panel's own handlers) --------------------------------------------------------

    /// <summary>The "When a mod that isn't allowed loads mid-session" switch: <c>warn</c> or <c>disconnect</c>.</summary>
    public static Dictionary<string, object?> MidSession(string value) => After(JoinAdminPanel.SetMidSession(value));

    // ---- Mod compatibility C3: client (the panel's own handlers; staged, Apply sends) ---------------------------------

    /// <summary>Version matching: <c>range</c>, <c>minor</c> or <c>exact</c>.</summary>
    public static Dictionary<string, object?> VersionMatching(string value) => After(JoinAdminPanel.SetVersionMatching(value));

    /// <summary>
    /// A server mod's rule: <c>exact</c>, <c>at-least</c>, <c>minor</c>, <c>any</c> or <c>default</c> (the server setting); an
    /// empty <paramref name="rule"/> presses the row's Rule button (it cycles).
    /// </summary>
    public static Dictionary<string, object?> ServerModRule(string id, string rule) =>
        After(JoinAdminPanel.SetServerModRule(id, string.IsNullOrWhiteSpace(rule) ? null : rule.Trim()));

    /// <summary>
    /// An alert line's <b>Approve</b> (the button's own handler): by the line's mod id, or by its index in the alert
    /// (<paramref name="index"/> 0 and up) when <paramref name="mod"/> is empty.
    /// </summary>
    public static Dictionary<string, object?> AlertApprove(string mod, int index)
    {
        string key = (mod ?? "").Trim();
        if (key.Length == 0)
        {
            var s = JoinAdminClient.State;
            if (s == null || index < 0 || index >= s.Alert.Count) return After("not sent: no alert line " + index.ToString(CultureInfo.InvariantCulture));
            key = s.Alert[index].Mod;
        }

        return After(JoinAdminPanel.ApproveFromAlert(key));
    }

    /// <summary>A raw approve-bulk (or any policy op) request, as a client without the panel would send it. <paramref name="itemsJson"/>: a JSON array of {id, package, version, name}.</summary>
    public static Dictionary<string, object?> PolicyRequest(string op, string id, string package, string version, string reason, string rule, string value, string itemsJson)
    {
        var request = new JoinAdminRequest { Op = op, Id = id, Package = package, Version = version, Reason = reason, Rule = rule, Value = value };
        if (!string.IsNullOrWhiteSpace(itemsJson))
        {
            using var doc = JsonDocument.Parse(itemsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new ArgumentException("args.items must be an array");
            foreach (var x in doc.RootElement.EnumerateArray())
                request.Items.Add(new JoinAdminBulkItem
                {
                    Id = JoinAdminConfig.Str(x, "id"), Package = JoinAdminConfig.Str(x, "package"),
                    Version = JoinAdminConfig.Str(x, "version"), Name = JoinAdminConfig.Str(x, "name"),
                });
        }

        return After(JoinAdminPanel.Send(request, honorLock: false));
    }

    /// <summary>What this client sends a server that asks for its mods (the report's entries now) and what it last sent.</summary>
    public static Dictionary<string, object?> Report()
    {
        var d = ClientModReportClient.Describe(withEntries: true);
        d["ok"] = true;
        d["side"] = "client";
        return d;
    }

    /// <summary>NOTICEOPEN1: press the mid-session notice's open button (<c>join_admin notice_open</c>), then the report.</summary>
    public static Dictionary<string, object?> NoticeOpen()
    {
        string result = ServerModPauseNotice.PressOpen();
        var d = Report();
        d["result"] = result;
        return d;
    }

    private static Dictionary<string, object?> After(string result)
    {
        var d = Read();
        d["result"] = result;
        return d;
    }

    // ---- server -----------------------------------------------------------------------------

    /// <summary>The stored choices, the file and the window (server).</summary>
    public static Dictionary<string, object?> Store()
    {
        RequireServer();
        var config = JoinAdminServer.Config;
        var rec = new List<object?>();
        foreach (var r in config.Recommended)
            rec.Add(new Dictionary<string, object?> { ["id"] = r.Id, ["package"] = r.Package, ["version"] = r.Version, ["reason"] = r.Reason });

        var signed = new List<object?>();
        var join = ModManifest.LastEnvelope?.Join;
        if (join != null)
            foreach (var r in join.Recommended)
                signed.Add(new Dictionary<string, object?> { ["id"] = r.Id, ["package"] = r.Package, ["version"] = r.Version, ["reason"] = r.Reason });
        return new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["side"] = "server",
            ["path"] = JoinAdminServer.FilePath,
            ["exists"] = File.Exists(JoinAdminServer.FilePath),
            ["loadNote"] = JoinAdminServer.LoadNote,
            ["recommended"] = rec,
            ["windowSeconds"] = JoinAdminServer.WindowSeconds(),
            ["versionMatching"] = MatchingStore(config),
            ["signedRecommended"] = signed,
            ["catalogIssued"] = ModManifest.LastEnvelope?.Body.Issued ?? "",
        };
    }

    /// <summary>C3 (server): the stored mode, the cfg override, what the judge reads now, and the stored per-mod rules.</summary>
    private static Dictionary<string, object?> MatchingStore(JoinAdminConfig config)
    {
        var rules = new List<object?>();
        foreach (var r in config.ServerModRules)
            rules.Add(new Dictionary<string, object?> { ["id"] = r.Id, ["rule"] = ClientModRules.Word(r.Rule), ["name"] = r.Name });
        var forced = VersionMatchCfg.ForcedNow();
        return new Dictionary<string, object?>
        {
            ["stored"] = VersionMatchingText.Word(config.VersionMatching),
            ["cfg"] = VersionMatchCfg.Text(),
            ["forced"] = forced == null ? null : VersionMatchingText.Word(forced.Value),
            ["effective"] = Progenitor.Networking.VersionMatching.Current().Describe(),
            ["sourceSet"] = Progenitor.Networking.VersionMatching.SettingsSource != null,
            ["serverModRules"] = rules,
            ["serverMods"] = JoinAdminServer.ServerModIds(ModManifest.EnumerateLoaded()),
        };
    }

    /// <summary>The refused-join record as stored (no identities) and its aggregate (server).</summary>
    public static Dictionary<string, object?> Refused()
    {
        RequireServer();
        RefusedJoins.Flush();
        var log = RefusedJoins.Log;
        var now = RefusedJoins.Clock();
        var rows = new List<object?>();
        foreach (var r in log.Rows)
            rows.Add(new Dictionary<string, object?>
            {
                ["mod"] = r.Mod, ["required"] = r.Required, ["had"] = r.Had, ["kind"] = r.Kind,
                ["at"] = r.At.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture), ["join"] = r.Join,
            });
        // The same readable names the panel's alert uses.
        Dictionary<string, string> names;
        try { names = ModManifest.RequiredModNames(ModManifest.EnumerateLoaded()); }
        catch { names = new Dictionary<string, string>(); }
        // CLIENTPOLICY2: the panel's own lookup (the server's plugin names, then the admins' client row names); live
        // 2026-10-05 this readback named a required client row by its id.
        var (joins, top) = log.Aggregate(now, JoinAdminLimits.MaxAlertRows, id => JoinAdminServer.AlertName(id, names));
        var alert = new List<object?>();
        var policy = JoinAdminServer.CurrentPolicy;
        foreach (var a in top)
        {
            // CLIENTPOLICY3: the panel's own marking (a not-approved line whose mod is approved now).
            JoinAdminServer.MarkApproved(a, policy);
            alert.Add(new Dictionary<string, object?>
            {
                ["mod"] = a.Mod, ["required"] = a.Required, ["had"] = a.Had, ["kind"] = a.Kind, ["count"] = a.Count, ["text"] = a.Text,
                ["approved"] = a.Approved, ["approveButton"] = a.CanApprove,
            });
        }
        return new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["side"] = "server",
            ["path"] = RefusedJoins.FilePath,
            ["exists"] = File.Exists(RefusedJoins.FilePath),
            ["rowCount"] = log.Rows.Count,
            ["maxRows"] = RefusedJoinLog.MaxRows,
            ["windowDays"] = RefusedJoinLog.WindowDays,
            ["refusedJoins"] = joins,
            ["alert"] = alert,
            ["rows"] = rows,
            ["heldInFlight"] = RefusedJoins.HeldCount,
        };
    }

    /// <summary>TEST ONLY: record <paramref name="count"/> synthetic refused joins over one mod (server).</summary>
    public static Dictionary<string, object?> InjectRefused(string mod, string required, string had, string kind, int count)
    {
        RequireServer();
        if (!ModManifestIds.TryValidateId(mod, out var error)) throw new ArgumentException("args.mod: " + error);
        int n = RefusedJoins.InjectForTest(mod, required ?? "", had ?? "", kind ?? "", count);
        RefusedJoins.Flush();
        var d = Refused();
        d["injected"] = n;
        d["testOnly"] = true;
        return d;
    }

    /// <summary>TEST ONLY: empty the refused-join record (server), to restore the test server after a run.</summary>
    public static Dictionary<string, object?> ClearRefused()
    {
        RequireServer();
        int n = RefusedJoins.ClearForTest();
        var d = Refused();
        d["cleared"] = n;
        d["testOnly"] = true;
        return d;
    }

    // ---- CLIENTPOLICY1: server ----------------------------------------------------------------------------------

    /// <summary>The stored client mod policy, what the catalog signed, and per user the last report and the last judgment (server).</summary>
    public static Dictionary<string, object?> Policy()
    {
        RequireServer();
        var policy = JoinAdminServer.CurrentPolicy;
        static List<object?> Rows(List<ClientModRow> rows, bool reason)
        {
            var list = new List<object?>();
            foreach (var r in rows)
            {
                var row = new Dictionary<string, object?>
                {
                    ["id"] = r.Id, ["package"] = r.Package, ["version"] = r.Version, ["rule"] = ClientModRules.Word(r.Rule), ["name"] = r.Name,
                    ["label"] = ClientModRules.Label(r.Rule, r.Version),
                };
                if (reason) row["reason"] = r.Reason;
                list.Add(row);
            }

            return list;
        }

        var join = ModManifest.LastEnvelope?.Join;
        var users = new List<object?>();
        foreach (int index in ClientModPolicyRecords.Users())
        {
            var u = new Dictionary<string, object?> { ["user"] = index, ["answer"] = Handshake.UserAnswerStatus(index) };
            var rep = ClientModPolicyRecords.ReportOf(index);
            if (rep != null)
            {
                var r = ClientModPolicyRecords.Describe(rep.Report);
                r["at"] = ClientModPolicyRecords.Iso(rep.AtUtc);
                r["how"] = rep.How;
                r["reports"] = rep.Reports;
                u["lastReport"] = r;
            }

            var j = ClientModPolicyRecords.JudgmentOf(index);
            if (j != null)
            {
                var v = ClientModPolicyRecords.Describe(j.Verdict);
                v["name"] = j.Name;
                v["at"] = ClientModPolicyRecords.Iso(j.AtUtc);
                v["trigger"] = j.Trigger;
                u["lastJudgment"] = v;
            }

#if !PROGENITOR_TESTS
            // CLIENTPOLICY3: the mid-session re-check (problems, what was told, an armed disconnect's seconds left).
            u["recheck"] = Handshake.DescribeRecheck(index);
#endif
            users.Add(u);
        }

        var d = new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["side"] = "server",
            ["path"] = JoinAdminServer.FilePath,
            ["active"] = policy.Active,
            ["requiresAnswer"] = policy.RequiresAnswer,
            ["mode"] = ClientModRules.ModeWord(policy.Mode),
            ["unverified"] = ClientModRules.UnverifiedWord(policy.Unverified),
            ["midSession"] = ClientModRules.MidSessionWord(policy.MidSession),
            ["graceSeconds"] = new Dictionary<string, object?>
            {
                ["loadedMidSession"] = ClientPolicyRecheckLimits.DisconnectGraceSeconds,
                ["loadedBeforePolicyChange"] = ClientPolicyRecheckLimits.PolicyChangeGraceSeconds,
            },
            ["required"] = Rows(policy.Required, reason: true),
            ["approved"] = Rows(policy.Approved, reason: false),
            ["signedRequiredClient"] = join == null ? null : Rows(new List<ClientModRow>(join.RequiredClient), reason: true),
            ["signedPolicyMode"] = join == null ? null : ClientModRules.ModeWord(join.PolicyMode),
            ["signedApproved"] = join?.Approved.Count,
            ["catalogIssued"] = ModManifest.LastEnvelope?.Body.Issued ?? "",
            ["users"] = users,
        };
#if !PROGENITOR_TESTS
        d["waiting"] = Handshake.PolicyGate.Count;
        d["uncheckedLetIn"] = Handshake.UncheckedLetIn;
        d["pendingInjections"] = Handshake.PendingInjections();
#endif
        return d;
    }

    /// <summary>
    /// TEST ONLY (server): a fake client mod report. <paramref name="mode"/> <c>next</c> (default) replaces the next real
    /// report of the user <paramref name="who"/> (character name or SteamID; for a join to come); <c>drop-next</c> drops
    /// it (a client whose report never arrives: the join waits out the deadline and is judged as unchecked); <c>now</c>
    /// delivers it to a connected user at once (user index or name): it decides a waiting join; for a player already in the
    /// world it is a mid-session report (CLIENTPOLICY3), re-checked for real (the notice, and under Disconnect the player
    /// the armed disconnect). <paramref name="modsJson"/>: a JSON array of {guid, name, version, source, package}.
    /// </summary>
    public static Dictionary<string, object?> InjectReport(string who, string modsJson, bool truncated, string mode)
    {
        RequireServer();
        string m = string.IsNullOrWhiteSpace(mode) ? "next" : mode.Trim().ToLowerInvariant();
        ClientModReport? report = null;
        if (m != "drop-next")
        {
            report = new ClientModReport { Truncated = truncated };
            if (!string.IsNullOrWhiteSpace(modsJson))
            {
                using var doc = JsonDocument.Parse(modsJson);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new ArgumentException("args.mods must be an array");
                foreach (var x in doc.RootElement.EnumerateArray())
                {
                    if (x.ValueKind != JsonValueKind.Object) continue;
                    var e = new ClientModEntry(JoinAdminConfig.Str(x, "guid"), JoinAdminConfig.Str(x, "name"), JoinAdminConfig.Str(x, "version"),
                        JoinAdminConfig.Str(x, "source"), JoinAdminConfig.Str(x, "package"));
                    if (e.Guid.Length > 0 || e.Package.Length > 0) report.Entries.Add(e);
                }
            }
        }

        string result;
#if PROGENITOR_TESTS
        result = "not available in tests";
#else
        if (m == "now")
        {
            if (!TryConnectedUser(who, out int index, out string name)) throw new ArgumentException("args.user: no connected user " + who);
            result = Handshake.InjectReportNow(index, name, report!);
        }
        else if (m == "next" || m == "drop-next")
        {
            Handshake.InjectNextReport(who, report);
            result = m == "drop-next" ? "the next real report of " + who + " will be dropped" : "the next real report of " + who + " will be replaced";
        }
        else throw new ArgumentException("args.mode must be next, drop-next or now");
#endif
        Plugin.Logger?.LogWarning("ClientPolicy: TEST ONLY: inject_report " + m + " for " + who + " (bridge)");
        var d = Policy();
        d["result"] = result;
        d["testOnly"] = true;
        return d;
    }

    /// <summary>
    /// CLIENTPOLICY3 (server): re-judge every player in the world from their last report now, with the real effects (the
    /// notice, and under Disconnect the player an armed disconnect), as a policy change would.
    /// </summary>
    public static Dictionary<string, object?> Recheck()
    {
        RequireServer();
        string result;
#if PROGENITOR_TESTS
        result = "not available in tests";
#else
        int n = Handshake.RecheckAllNow();
        result = "re-checked " + n.ToString(CultureInfo.InvariantCulture) + " player(s) from their last report";
#endif
        Plugin.Logger?.LogInfo("ClientPolicy: bridge recheck: " + result);
        var d = Policy();
        d["result"] = result;
        return d;
    }

    /// <summary>NOPROGNOTICE (server): per user, the no-answer countdown and the notices sent (short and full lines, the seconds shown, how it ended).</summary>
    public static Dictionary<string, object?> Notices()
    {
        RequireServer();
        var d = NoAnswerNoticeRecords.Describe();
        d["ok"] = true;
        d["side"] = "server";
        d["template"] = JoinServerSettings.NoProgenitorTemplate();
        return d;
    }

#if !PROGENITOR_TESTS
    private static bool TryConnectedUser(string who, out int index, out string name)
    {
        index = -1;
        name = "";
        var world = Worlds.Server;
        if (world == null || !world.IsCreated) return false;
        var em = world.EntityManager;
        var entities = Query.Entities(em, includeDisabled: true, includeSystems: false,
            ComponentIndex.Of<ProjectM.Network.User>(Unity.Entities.ComponentType.AccessMode.ReadOnly));
        bool numeric = int.TryParse((who ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int asIndex);
        for (int i = 0; i < entities.Count; i++)
        {
            var user = em.GetComponentData<ProjectM.Network.User>(entities[i]);
            if (!user.IsConnected) continue;
            string n = user.CharacterName.ToString();
            if ((numeric && user.Index == asIndex) || string.Equals(n, (who ?? "").Trim(), StringComparison.OrdinalIgnoreCase)
                || string.Equals(user.PlatformId.ToString(CultureInfo.InvariantCulture), (who ?? "").Trim(), StringComparison.Ordinal))
            {
                index = user.Index;
                name = n;
                return true;
            }
        }

        return false;
    }
#endif

    private static void RequireServer()
    {
        if (!Plugin.IsServerProcess) throw new InvalidOperationException("this join_admin action runs on the server side");
    }
}
