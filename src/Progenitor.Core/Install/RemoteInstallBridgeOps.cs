using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace Progenitor.Install;

/// <summary>
/// REMOTEINST1: the dev bridge's <c>remote_install</c> op, reached by reflection from
/// <c>Progenitor.Dev.Ops.RemoteInstallOps</c> exactly as <c>install_local</c> reaches
/// <see cref="InstallLocalIndexOps"/> (Core grants Dev no <c>InternalsVisibleTo</c>).
///
/// <para><b>Client</b> (main thread): <c>status</c> reads the route the Install tab is using, the
/// server's last status and each package's latest step; <c>request</c> asks the server again;
/// <c>install</c> / <c>update</c> / <c>uninstall</c> / <c>cancel</c> send the same request the row
/// button sends. <b>Server</b>: <c>status</c> reads the settings, the busy flag, the reachability
/// probe, the ledger rows and the audit tail; <c>probe</c> runs the reachability probe now (at most
/// once a minute); read <c>status</c> again for its answer. Nothing here bypasses a server check: the client's sends go
/// through the same channel, and the server re-reads admin on every request.</para>
/// </summary>
internal static class RemoteInstallBridgeOps
{
    public const int AuditTailLines = 20;

    /// <summary>True in a server process. Set by the wiring; null reads as a client.</summary>
    internal static Func<bool>? IsServer { get; set; }

    /// <summary>The server's install core. Set by the wiring on a server; null elsewhere.</summary>
    internal static Func<RemoteInstallService?>? ServerService { get; set; }

    /// <summary>The settings the server bound. Set by the wiring on a server.</summary>
    internal static Func<RemoteInstallSettings>? ServerSettings { get; set; }

    /// <summary>The route the Install tab is using now (client), with the facts it was decided from.</summary>
    internal static Func<ServerRouteDecision>? ClientRoute { get; set; }

    public static Dictionary<string, object?> Run(string action, string package, string version, bool force) =>
        Run(action, package, version, force, "");

    /// <summary>
    /// <paramref name="route"/>: empty (the default) sends install/update/uninstall only when the
    /// Install tab's own route for the Server target is the remote one; <c>"remote"</c> forces the
    /// remote request whatever the tab would pick (a test of the channel itself).
    /// </summary>
    public static Dictionary<string, object?> Run(string action, string package, string version, bool force, string route)
    {
        string a = (action ?? "").Trim().ToLowerInvariant();
        if (a.Length == 0) a = "status";
        bool server = false;
        try { server = IsServer?.Invoke() ?? false; }
        catch { server = false; }
        return server ? Server(a) : Client(a, package ?? "", version ?? "", force, (route ?? "").Trim().ToLowerInvariant());
    }

    private static Dictionary<string, object?> Client(string action, string package, string version, bool force, string routeArg)
    {
        var client = RemoteInstallClient.Shared;
        if (action is "install" or "update" or "uninstall" && routeArg != "remote")
        {
            ServerRoute uiRoute = default;
            try { uiRoute = ClientRoute?.Invoke().Route ?? default; }
            catch { uiRoute = default; }
            if (uiRoute.Kind != ServerRouteKind.Remote)
            {
                var refused = ClientStatus(client, package);
                refused["ok"] = false;
                refused["error"] = "the Install tab's Server route is '" + uiRoute.KindName + "' (" + uiRoute.BlockName
                    + "), not remote; drive a local install through mods_ui, or pass \"route\":\"remote\" to force the remote request";
                return refused;
            }
        }

        switch (action)
        {
            case "status":
                return ClientStatus(client, package);
            case "request":
                return new Dictionary<string, object?> { ["ok"] = true, ["sent"] = client.RequestStatus() };
            case "install":
            case "update":
            case "uninstall":
            {
                var op = action == "install" ? RemoteInstallOp.Install : action == "update" ? RemoteInstallOp.Update : RemoteInstallOp.Uninstall;
                int nonce = client.Begin(op, package, version, force, out string? refusal);
                var result = ClientStatus(client, package);
                result["ok"] = nonce != 0;
                result["nonce"] = nonce;
                if (refusal != null) result["error"] = refusal;
                return result;
            }
            case "cancel":
                return new Dictionary<string, object?> { ["ok"] = client.Cancel(package) };
            case "adminauth":
            {
                // The same path as the "Authenticate as admin" button: the game's own adminauth.
                bool started = AdminAuthFlow.Shared.Begin();
                var result = ClientStatus(client, package);
                result["ok"] = started;
                if (!started) result["error"] = AdminAuthFlow.Shared.Message;
                return result;
            }
            default:
                return new Dictionary<string, object?>
                {
                    ["ok"] = false,
                    ["error"] = "unknown action '" + action + "'; client actions: status, request, install, update, uninstall, cancel, adminauth",
                };
        }
    }

    private static Dictionary<string, object?> ClientStatus(RemoteInstallClient client, string package)
    {
        var status = client.Status;
        ServerRouteDecision decision = default;
        try { decision = ClientRoute?.Invoke() ?? default; }
        catch { decision = default; }
        var route = decision.Route;
        var facts = decision.Facts;
        var result = new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["side"] = "client",
            ["serverSupportsRemote"] = client.ServerSupportsRemote,
            ["route"] = new Dictionary<string, object?>
            {
                ["kind"] = route.KindName,
                ["effective"] = route.EffectiveName,
                ["forcedRemote"] = route.ForcedRemote,
                ["block"] = route.BlockName,
                ["reason"] = route.Reason,
                ["help"] = route.Help,
                ["why"] = decision.Why,
                ["connectedIsLocal"] = decision.ConnectedIsLocal,
                // LOCALDETECT1: which folder a local route writes, and where it came from (hosted/configured/detected).
                ["localSource"] = decision.SourceName,
                ["localFolder"] = decision.LocalFolder,
                ["inputs"] = facts == null ? null : new Dictionary<string, object?>
                {
                    ["configuredPath"] = facts.ConfiguredPath,
                    ["pathValid"] = facts.PathValid,
                    ["canonicalPath"] = facts.CanonicalPath,
                    ["folderPort"] = facts.FolderPort,
                    ["connected"] = facts.Connected,
                    ["connectedAddress"] = facts.ConnectedAddress,
                    ["connectedPort"] = facts.ConnectedPort,
                    ["portRecorded"] = facts.PortRecorded,
                    ["isLocalMachine"] = facts.IsLocalMachine,
                    ["verifiedThisPcServer"] = facts.VerifiedThisPcServer,
                    ["handshakeOk"] = facts.HandshakeOk,
                    ["detectedFolder"] = facts.DetectedFolder,
                    ["detectedPort"] = facts.DetectedPort,
                    ["detectedName"] = facts.DetectedName,
                    ["detectedNote"] = facts.DetectedNote,
                    ["remoteStatus"] = facts.RemoteStatus == null ? null : StatusToWidget(facts.RemoteStatus),
                },
            },
            ["steamId"] = client.LastKnownSteamId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["adminAuth"] = new Dictionary<string, object?>
            {
                ["buttonEnabled"] = AdminAuthFlow.Shared.Enabled,
                ["state"] = AdminAuthFlow.Shared.StateName,
                ["message"] = AdminAuthFlow.Shared.Message,
                ["offer"] = AdminAuthFlow.Offer(status, AdminAuthFlow.Shared.Enabled, featureGated: true).ToString().ToLowerInvariant(),
            },
            ["status"] = status == null ? null : StatusToWidget(status),
        };
        if (package.Length > 0)
        {
            var latest = client.LatestFor(package);
            result["latest"] = latest == null ? null : ProgressToWidget(latest);
        }

        // Every package's latest step, finished or not, so a reply is visible without naming it.
        var all = new Dictionary<string, object?>();
        foreach (var pair in client.AllLatest()) all[pair.Key] = ProgressToWidget(pair.Value);
        result["steps"] = all;
        return result;
    }

    private static Dictionary<string, object?> Server(string action)
    {
        var service = ServerService?.Invoke();
        if (service == null)
            return new Dictionary<string, object?> { ["ok"] = false, ["side"] = "server", ["error"] = "the remote install core is not installed" };
        switch (action)
        {
            case "status":
                return ServerStatus(service);
            case "probe":
            {
                // Started, never awaited here: this op runs on the main thread and the probe may take
                // the connect plus stall timeouts. Read `status` again for the answer.
                var task = service.ProbeAsync(force: true);
                var result = ServerStatus(service);
                result["probeFinished"] = task.IsCompleted;
                return result;
            }
            default:
                return new Dictionary<string, object?>
                {
                    ["ok"] = false,
                    ["side"] = "server",
                    ["error"] = "unknown action '" + action + "'; server actions: status, probe",
                };
        }
    }

    private static Dictionary<string, object?> ServerStatus(RemoteInstallService service)
    {
        RemoteInstallSettings settings;
        try { settings = ServerSettings?.Invoke() ?? RemoteInstallSettings.Default; }
        catch { settings = RemoteInstallSettings.Default; }
        var probe = service.LastProbe;
        var rows = new List<Dictionary<string, object?>>();
        foreach (var row in service.ListRows()) rows.Add(RowToWidget(row));
        return new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["side"] = "server",
            ["enabled"] = settings.Enabled,
            ["allowedSteamIds"] = settings.AllowedSteamIds.Count,
            ["thunderstoreBaseUrl"] = settings.ThunderstoreBaseUrl,
            ["baseUrlValid"] = settings.BaseUrlValid,
            ["timeouts"] = new Dictionary<string, object?>
            {
                ["connect"] = (int)settings.ConnectTimeout.TotalSeconds,
                ["stall"] = (int)settings.StallTimeout.TotalSeconds,
                ["total"] = (int)settings.TotalTimeout.TotalSeconds,
            },
            ["busy"] = service.Busy,
            ["probe"] = new Dictionary<string, object?>
            {
                ["state"] = probe.State.ToString().ToLowerInvariant(),
                ["detail"] = probe.Detail,
                ["checkedUtc"] = probe.CheckedUtc == DateTime.MinValue ? "" : probe.CheckedUtc.ToString("u"),
            },
            ["rows"] = rows,
            ["auditLog"] = RemoteInstallAuditLog.PathFor(service.BepInExRoot),
            ["auditTail"] = AuditTail(service.BepInExRoot),
        };
    }

    private static List<string> AuditTail(string bepInExRoot)
    {
        var tail = new List<string>();
        try
        {
            string path = RemoteInstallAuditLog.PathFor(bepInExRoot);
            if (!File.Exists(path)) return tail;
            var lines = File.ReadAllLines(path);
            for (int i = Math.Max(0, lines.Length - AuditTailLines); i < lines.Length; i++) tail.Add(lines[i]);
        }
        catch { /* a locked file reads as empty */ }
        return tail;
    }

    internal static Dictionary<string, object?> StatusToWidget(RemoteInstallStatus status)
    {
        var rows = new List<Dictionary<string, object?>>();
        foreach (var row in status.Rows) rows.Add(RowToWidget(row));
        return new Dictionary<string, object?>
        {
            ["enabled"] = status.Enabled,
            ["authorized"] = status.Authorized,
            ["onAdminList"] = status.OnAdminList,
            ["adminAuthed"] = status.AdminAuthed,
            ["authorizedBy"] = RemoteInstallService.AuthorizedByName(status.AuthorizedBy),
            ["admin"] = status.Authorized, // alias of "authorized" for one release
            ["allowed"] = status.Allowed,
            ["reachability"] = status.Reachability.ToString().ToLowerInvariant(),
            ["reachabilityDetail"] = status.ReachabilityDetail,
            ["rowsIncluded"] = status.RowsIncluded,
            ["rows"] = rows,
        };
    }

    internal static Dictionary<string, object?> RowToWidget(RemoteServerRow row) => new()
    {
        ["package"] = row.Package,
        ["version"] = row.Version,
        ["state"] = row.State.ToString().ToLowerInvariant(),
        ["route"] = row.Route.Length > 0 ? row.Route : "local",
    };

    internal static Dictionary<string, object?> ProgressToWidget(RemoteInstallProgress progress) => new()
    {
        ["nonce"] = progress.Nonce,
        ["op"] = RemoteInstallWire.OpName(progress.Op),
        ["stage"] = RemoteInstallWire.StageName(progress.Stage),
        ["reason"] = RemoteInstallWire.ReasonName(progress.Reason),
        ["detail"] = progress.Detail,
        ["words"] = ServerRouteCopy.Stage(progress),
    };
}
