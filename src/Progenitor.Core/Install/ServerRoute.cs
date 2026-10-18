using System;

namespace Progenitor.Install;

/// <summary>REMOTEINST1: how the Install tab's one "Server" target reaches a server.</summary>
internal enum ServerRouteKind
{
    /// <summary>No server half: server-naming installs fall back to the client, with the reason shown.</summary>
    None = 0,

    /// <summary>
    /// A dedicated-server folder on this PC, written through the filesystem. No admin needed. The folder is the hosted
    /// private game's, the auto-detected one that owns the joined port (LOCALDETECT1), or the configured one.
    /// </summary>
    Local = 1,

    /// <summary>The connected Progenitor server installs for this admin (the <c>PrRi</c> channel).</summary>
    Remote = 2,
}

/// <summary>Why there is no usable server route (or, on the remote route, why installs are blocked).</summary>
internal enum ServerRouteBlock
{
    None = 0,

    /// <summary>Not connected, and no local server folder is configured.</summary>
    NoServer = 1,

    /// <summary>Connected to a server that did not answer the Progenitor handshake.</summary>
    NotProgenitor = 2,

    /// <summary>A Progenitor server that has remote installs switched off (or whose Core predates them).</summary>
    RemoteOff = 3,

    /// <summary>The player is not an in-game admin there (adminlist.txt plus <c>adminauth</c>).</summary>
    NotAdmin = 4,

    /// <summary>An admin, but not one the host listed in <c>[RemoteInstall] AllowedSteamIds</c>.</summary>
    NotAllowed = 5,

    /// <summary>Remote route only: the server's probe says it cannot reach Thunderstore. Uninstall still works.</summary>
    Unreachable = 6,

    /// <summary>Connected to a Progenitor server whose remote-install status has not arrived yet.</summary>
    Pending = 7,

    /// <summary>Remote management requires a connection; local fallback is disabled.</summary>
    Disconnected = 8,
}

/// <summary>Everything the route decision reads. Plain values, so the matrix is testable.</summary>
internal readonly struct ServerRouteInputs
{
    public ServerRouteInputs(
        bool localConfigured,
        bool connected,
        bool connectedIsLocal,
        bool handshakeOk,
        RemoteInstallStatus? remoteStatus)
    {
        LocalConfigured = localConfigured;
        Connected = connected;
        ConnectedIsLocal = connectedIsLocal;
        HandshakeOk = handshakeOk;
        RemoteStatus = remoteStatus;
    }

    /// <summary><c>[Mods] DedicatedServerPath</c> is validated, or (LOCALDETECT1) a local server folder was auto-detected for the connection.</summary>
    public bool LocalConfigured { get; }

    /// <summary>The client is in a game on some server.</summary>
    public bool Connected { get; }

    /// <summary>
    /// The connected server is the configured folder's server — decided the way Stop server decides
    /// it (the folder's <c>ServerHostSettings.json</c> port equals the joined port, plus a completed
    /// Progenitor handshake, <see cref="ServerProcessControl.GracefulAvailable"/>) and, because the
    /// route decides where files go, the joined address is this machine (<see cref="LocalAddress"/>).
    /// </summary>
    public bool ConnectedIsLocal { get; }

    /// <summary>The connected server answered the Progenitor handshake.</summary>
    public bool HandshakeOk { get; }

    /// <summary>The server's last remote-install status for this player, or null when none arrived.</summary>
    public RemoteInstallStatus? RemoteStatus { get; }
}

/// <summary>The route one Install click takes for its server half, with the words the row shows.</summary>
internal readonly struct ServerRoute
{
    public ServerRoute(ServerRouteKind kind, ServerRouteBlock block, string detail = "", bool forcedRemote = false)
    {
        Kind = kind;
        Block = block;
        Detail = detail ?? "";
        ForcedRemote = forcedRemote;
    }

    /// <summary>
    /// REMOTETEST1: <c>[Install] TreatLocalServerAsRemote</c> turned what would have been the local
    /// route (the connected server is the configured folder's) into the remote one.
    /// </summary>
    public bool ForcedRemote { get; }

    /// <summary>The kind as the readback names it, with the reason when the setting forced it.</summary>
    public string EffectiveName => ForcedRemote ? KindName + " (forced by " + TreatLocalServerAsRemote.Key + ")" : KindName;

    public ServerRouteKind Kind { get; }
    public ServerRouteBlock Block { get; }

    /// <summary>The probe's sub-cause for <see cref="ServerRouteBlock.Unreachable"/>.</summary>
    public string Detail { get; }

    /// <summary>A server half can be installed now.</summary>
    public bool CanInstall => Kind == ServerRouteKind.Local || (Kind == ServerRouteKind.Remote && Block == ServerRouteBlock.None);

    /// <summary>A server half already there can be updated or uninstalled now.</summary>
    public bool CanManage => Kind != ServerRouteKind.None;

    /// <summary>The short reason a server-side row shows when there is no usable route. Empty when there is one.</summary>
    public string Reason => Block switch
    {
        ServerRouteBlock.NotAdmin => ServerRouteCopy.NotAdmin,
        ServerRouteBlock.NotAllowed => ServerRouteCopy.NotAllowed,
        ServerRouteBlock.RemoteOff => ServerRouteCopy.RemoteOff,
        ServerRouteBlock.NotProgenitor => ServerRouteCopy.NotProgenitor,
        ServerRouteBlock.Unreachable => ServerRouteCopy.Unreachable(Detail),
        ServerRouteBlock.Pending => ServerRouteCopy.Pending,
        ServerRouteBlock.Disconnected => "Connect to manage",
        ServerRouteBlock.NoServer => ServerRouteCopy.NoServer,
        _ => "",
    };

    /// <summary>
    /// The server context line's management words (Simple view, Server tab): <c>Local management</c> or
    /// <c>Remote management</c> only on that route kind; otherwise the route's reason, so a blocked route never reads as
    /// managed. Empty with no route and no reason.
    /// </summary>
    public string ManagementLabel => Kind switch
    {
        ServerRouteKind.Local => ServerRouteCopy.LocalManagement,
        ServerRouteKind.Remote => Block == ServerRouteBlock.None ? ServerRouteCopy.RemoteManagement : ServerRouteCopy.RemoteManagement + " · " + Reason,
        _ => Reason,
    };

    /// <summary>The help line under a blocked row's reason. Empty when there is nothing to do about it.</summary>
    public string Help => Block switch
    {
        ServerRouteBlock.NotAdmin => ServerRouteCopy.NotAdminHelp,
        ServerRouteBlock.NotAllowed => ServerRouteCopy.NotAllowedHelp,
        ServerRouteBlock.Unreachable => ServerRouteCopy.UnreachableHelp,
        _ => "",
    };

    public string KindName => Kind switch
    {
        ServerRouteKind.Local => "local",
        ServerRouteKind.Remote => "remote",
        _ => "none",
    };

    public string BlockName => Block switch
    {
        ServerRouteBlock.Disconnected => "disconnected",
        ServerRouteBlock.NoServer => "no-server",
        ServerRouteBlock.NotProgenitor => "not-progenitor",
        ServerRouteBlock.RemoteOff => "remote-off",
        ServerRouteBlock.NotAdmin => "not-admin",
        ServerRouteBlock.NotAllowed => "not-allowed",
        ServerRouteBlock.Unreachable => "unreachable",
        ServerRouteBlock.Pending => "pending",
        _ => "none",
    };
}

/// <summary>
/// REMOTEINST1 (decided 2026-10-01): one "Server" target, and the route picked for it in a
/// fixed order. The local folder wins whenever it is the server in question — not connected at all,
/// or connected to that very server — because it needs no admin and works with the server stopped.
/// Otherwise a connected Progenitor server that advertises remote installs to this admin is the
/// route. Otherwise there is none, and the reason is named.
/// </summary>
internal static class ServerRouteSelector
{
    /// <summary>
    /// The route from raw facts. "Connected to the local folder's server" is the Stop server identity
    /// (the folder's port equals the joined port, the handshake completed) plus the joined address
    /// being this machine. The local route needs no admin and is decided before anything remote.
    /// </summary>
    /// <summary>
    /// F23, review M5 and M-e (2026-10-08): the private game this client hosts is the local route only while it is hosted,
    /// <c>TreatLocalServerAsRemote</c> is off, and the connection (if any) is this machine with a completed handshake.
    /// Every reader and writer of the private game's folder asks this, not the hosting flag alone.
    /// </summary>
    public static bool HostedApplies(ServerRouteFacts? facts) =>
        facts != null && facts.HostedFolder.Length > 0 && !facts.TreatLocalAsRemote
        && (!facts.Connected || (facts.IsLocalMachine && facts.HandshakeOk));

    public static ServerRouteDecision Decide(ServerRouteFacts facts)
    {
        if (facts == null) throw new ArgumentNullException(nameof(facts));
        // Remote testing must not regain filesystem access when the player leaves the server.
        if (facts.TreatLocalAsRemote && !facts.Connected)
            return new ServerRouteDecision(
                new ServerRoute(ServerRouteKind.None, ServerRouteBlock.Disconnected, forcedRemote: true),
                false, "Connect to manage; local folder access disabled by TreatLocalServerAsRemote", facts);

        // F23 (live 2026-10-07): this client hosts a private game: its own server (the hosted child) is the local route,
        // whatever the configured dedicated folder is. The connected server is that child.
        // Review (M5): a stale hosting flag must not route another server's installs into the private game's folder: while
        // connected, the connection must be this machine with a completed handshake; TreatLocalServerAsRemote still holds.
        if (HostedApplies(facts))
            return new ServerRouteDecision(new ServerRoute(ServerRouteKind.Local, ServerRouteBlock.None), facts.Connected,
                "local: " + HostedServerFolder.Name + " (" + facts.HostedFolder + ")", facts,
                LocalFolderSource.Hosted, facts.HostedFolder);

        string notLocal = "";
        bool connectedIsLocal = false;
        if (facts.PathValid && facts.Connected)
        {
            if (!facts.PortRecorded) notLocal = "this Bootstrap records no joined port";
            else if (facts.ConnectedPort <= 0) notLocal = "no joined port recorded";
            else if (facts.FolderPort <= 0) notLocal = "the folder's ServerHostSettings.json port is unknown";
            else if (facts.ConnectedPort != facts.FolderPort)
                notLocal = "joined port " + facts.ConnectedPort + " is not the folder's port " + facts.FolderPort;
            else if (!facts.IsLocalMachine && !facts.VerifiedThisPcServer)
                notLocal = "joined address '" + facts.ConnectedAddress + "' is not this machine (and not verified as the folder's server)";
            else if (!facts.HandshakeOk) notLocal = "no completed Progenitor handshake";
            else connectedIsLocal = true;
        }

        // LOCALDETECT1 (decided 2026-10-08): any local Progenitor server folder is local automatically. The folder
        // whose own server process holds the joined port on this PC is this connection's local folder. It also wins over a
        // configured folder that matches by its settings port alone: the process that owns the port is the stronger proof
        // (two folders can name the same port; only one server can hold it). Without a detection the configured folder
        // keeps its rule above, so it stays the explicit fallback.
        bool detected = DetectedApplies(facts, out string detectNote);
        if (detected && connectedIsLocal && SameFolder(facts.CanonicalPath, facts.DetectedFolder)) detected = false;
        if (detected) connectedIsLocal = true;
        var source = !connectedIsLocal ? LocalFolderSource.None : detected ? LocalFolderSource.Detected : LocalFolderSource.Configured;
        string localFolder = source == LocalFolderSource.Detected ? facts.DetectedFolder
            : source == LocalFolderSource.Configured ? facts.CanonicalPath : "";

        // REMOTETEST1: with the setting on, the connected server is handled through the remote route
        // even when it is a local folder's. Disconnected remote testing is handled above, and
        // connectedIsLocal (what the picker merges on) keeps its physical meaning.
        bool forced = facts.TreatLocalAsRemote && connectedIsLocal;
        var inputs = new ServerRouteInputs(facts.PathValid || detected, facts.Connected, connectedIsLocal && !forced, facts.HandshakeOk, facts.RemoteStatus);
        var route = Select(inputs);
        if (forced) route = new ServerRoute(route.Kind, route.Block, route.Detail, forcedRemote: true);
        string whose = detected ? "the auto-detected local server " + facts.DetectedFolder : "the configured folder's server";
        string why;
        if (forced)
            why = "connected to " + whose + " (port " + facts.ConnectedPort + ", this machine, handshake ok); "
                + route.EffectiveName + " (" + route.BlockName + ")";
        else if (route.Kind == ServerRouteKind.Local)
            why = facts.Connected
                ? "local: connected to " + whose + " (port " + facts.ConnectedPort + ", this machine, handshake ok)"
                : "local: configured folder, not connected";
        else
        {
            string head = !facts.PathValid
                ? (facts.ConfiguredPath.Length == 0 ? "no local folder configured" : "configured folder not valid")
                : !facts.Connected ? "not connected" : "connected server is not the folder's: " + notLocal;
            if (facts.Connected && detectNote.Length > 0) head += "; not auto-detected: " + detectNote;
            why = head + "; " + (route.Kind == ServerRouteKind.Remote ? "remote" : "none") + " (" + route.BlockName + ")";
        }

        if (!facts.Connected && route.Kind == ServerRouteKind.Local)
        {
            source = LocalFolderSource.Configured;
            localFolder = facts.CanonicalPath;
        }

        return new ServerRouteDecision(route, connectedIsLocal, why, facts, source, localFolder);
    }

    /// <summary>
    /// LOCALDETECT1: the auto-detected folder is this connection's local server: a folder was detected for exactly the joined
    /// port, the joined address is this machine (loopback or an interface address), the Progenitor handshake completed, and
    /// the live-server fence passes on the folder (the detector already fenced the folder and its process).
    /// <paramref name="note"/> says why not (the detector's reason when it found none).
    /// </summary>
    public static bool DetectedApplies(ServerRouteFacts facts, out string note)
    {
        note = "";
        if (facts == null || !facts.Connected) return false;
        string folder = (facts.DetectedFolder ?? "").Trim();
        if (folder.Length == 0)
        {
            note = facts.DetectedNote ?? "";
            return false;
        }

        if (!facts.PortRecorded || facts.ConnectedPort <= 0) note = "no joined port recorded";
        else if (facts.DetectedPort != facts.ConnectedPort)
            note = "the detection is for port " + facts.DetectedPort + ", not the joined port " + facts.ConnectedPort;
        else if (!facts.IsLocalMachine) note = "joined address '" + facts.ConnectedAddress + "' is not this machine";
        else if (!facts.HandshakeOk) note = "no completed Progenitor handshake";
        else if (LocalServerFolder.IsLiveServerFolder(folder)) note = "the folder is the live dedicated server";
        else if (IdentityMismatch(facts) is { Length: > 0 } mismatch) note = mismatch;
        else return true;
        return false;
    }

    /// <summary>
    /// LOCALDETECT1 review (as LOCALROUTE1): the connected server's signed catalog names its operator key and server name;
    /// a known key that differs from the detected folder's, or a known name that differs from the detected server's, says
    /// the server answering is not that folder's. Unknown on either side is no mismatch (the port owner already decided).
    /// The catalog's default name (<see cref="ServerPicker.ManifestDefaultName"/>) names no server.
    /// </summary>
    internal static string IdentityMismatch(ServerRouteFacts facts)
    {
        string a = (facts.ConnectedOperatorKey ?? "").Trim();
        string b = (facts.DetectedOperatorKey ?? "").Trim();
        if (a.Length > 0 && b.Length > 0 && !string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
            return "the connected server's operator key is not the detected folder's";
        string n = (facts.ConnectedServerName ?? "").Trim();
        string m = (facts.DetectedName ?? "").Trim();
        if (n.Length > 0 && m.Length > 0 && !string.Equals(n, ServerPicker.ManifestDefaultName, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(n, m, StringComparison.OrdinalIgnoreCase))
            return "the connected server is named '" + n + "', the detected folder's server '" + m + "'";
        return "";
    }

    private static bool SameFolder(string? a, string? b) =>
        string.Equals((a ?? "").Trim().TrimEnd('\\', '/'), (b ?? "").Trim().TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    public static ServerRoute Select(in ServerRouteInputs inputs)
    {
        if (inputs.LocalConfigured && (!inputs.Connected || inputs.ConnectedIsLocal))
            return new ServerRoute(ServerRouteKind.Local, ServerRouteBlock.None);
        if (!inputs.Connected) return new ServerRoute(ServerRouteKind.None, ServerRouteBlock.NoServer);
        if (!inputs.HandshakeOk) return new ServerRoute(ServerRouteKind.None, ServerRouteBlock.NotProgenitor);

        var status = inputs.RemoteStatus;
        if (status == null) return new ServerRoute(ServerRouteKind.None, ServerRouteBlock.Pending);
        if (!status.Enabled) return new ServerRoute(ServerRouteKind.None, ServerRouteBlock.RemoteOff);
        if (!status.Authorized) return new ServerRoute(ServerRouteKind.None, ServerRouteBlock.NotAdmin);
        if (!status.Allowed) return new ServerRoute(ServerRouteKind.None, ServerRouteBlock.NotAllowed);
        if (status.Reachability == RemoteReachability.Unreachable)
            return new ServerRoute(ServerRouteKind.Remote, ServerRouteBlock.Unreachable, status.ReachabilityDetail);
        return new ServerRoute(ServerRouteKind.Remote, ServerRouteBlock.None);
    }
}

/// <summary>
/// REMOTEINST1: the raw facts the route is decided from, as the client read them. Kept whole so the
/// <c>remote_install</c> readback can show exactly what the decision saw.
/// </summary>
internal sealed class ServerRouteFacts
{
    /// <summary>The configured <c>[Mods] DedicatedServerPath</c>, as typed.</summary>
    public string ConfiguredPath { get; init; } = "";

    /// <summary>F23: the hosted private game's server folder while this client hosts one (empty: not hosting).</summary>
    public string HostedFolder { get; init; } = "";

    /// <summary>The configured folder validated as a dedicated-server install.</summary>
    public bool PathValid { get; init; }

    /// <summary>The validated folder's canonical path ("" when not valid).</summary>
    public string CanonicalPath { get; init; } = "";

    /// <summary>The game port in the folder's <c>ServerHostSettings.json</c> (launch-record persistentDataPath first), 0 when unknown.</summary>
    public int FolderPort { get; init; }

    /// <summary>The client is in a game.</summary>
    public bool Connected { get; init; }

    /// <summary>The address Bootstrap recorded for the last join (no protocol, no port).</summary>
    public string ConnectedAddress { get; init; } = "";

    /// <summary>The port Bootstrap recorded for the last join, 0 when none.</summary>
    public int ConnectedPort { get; init; }

    /// <summary>This Bootstrap records the joined port at all.</summary>
    public bool PortRecorded { get; init; } = true;

    /// <summary>The joined address is this machine (<see cref="LocalAddress.IsThisMachine"/>).</summary>
    public bool IsLocalMachine { get; init; }

    /// <summary>
    /// LOCALROUTE1: the joined address is not this machine (a public address, through the router), but the connected server
    /// is verified to be the folder's server by the loopback rejoin's own rule (<c>JoinLocalRoute.IsLocalServer</c>: the
    /// folder's port, its server running on this PC under the connected server's name and operator key).
    /// </summary>
    public bool VerifiedThisPcServer { get; init; }

    /// <summary>The connected server answered the Progenitor handshake.</summary>
    public bool HandshakeOk { get; init; }

    /// <summary>The server's remote-install status (after the client's grace rule), or null.</summary>
    public RemoteInstallStatus? RemoteStatus { get; init; }

    /// <summary>REMOTETEST1: <c>[Install] TreatLocalServerAsRemote</c> is on.</summary>
    public bool TreatLocalAsRemote { get; init; }

    /// <summary>
    /// LOCALDETECT1: the server folder auto-detected for this connection (<see cref="LocalServerDetector"/>: the
    /// <c>VRisingServer.exe</c> that owns the joined port on this PC, fenced, validated, carrying Progenitor), or empty.
    /// </summary>
    public string DetectedFolder { get; init; } = "";

    /// <summary>LOCALDETECT1: the joined port the detection answered for (0: none).</summary>
    public int DetectedPort { get; init; }

    /// <summary>LOCALDETECT1: the detected server's own name, or empty.</summary>
    public string DetectedName { get; init; } = "";

    /// <summary>LOCALDETECT1: why a folder was or was not detected (readback); empty when detection did not run.</summary>
    public string DetectedNote { get; init; } = "";

    /// <summary>LOCALDETECT1 review: the detected folder's operator key fingerprint (<c>progenitor-operator.pub</c>), or empty.</summary>
    public string DetectedOperatorKey { get; init; } = "";

    /// <summary>LOCALDETECT1 review: the name the connected server's signed catalog gives itself, or empty.</summary>
    public string ConnectedServerName { get; init; } = "";

    /// <summary>LOCALDETECT1 review: the connected server's operator key fingerprint (its signed catalog), or empty.</summary>
    public string ConnectedOperatorKey { get; init; } = "";
}

/// <summary>LOCALDETECT1: where the local route's folder came from.</summary>
internal enum LocalFolderSource
{
    /// <summary>No local folder is the route's.</summary>
    None = 0,

    /// <summary>The private game this client hosts (F23).</summary>
    Hosted = 1,

    /// <summary>The configured <c>[Mods] DedicatedServerPath</c>.</summary>
    Configured = 2,

    /// <summary>Auto-detected: the server folder that owns the joined port on this PC.</summary>
    Detected = 3,
}

/// <summary>The route, whether the connected server is the local folder's, and why — in words.</summary>
internal readonly struct ServerRouteDecision
{
    public ServerRouteDecision(ServerRoute route, bool connectedIsLocal, string why, ServerRouteFacts? facts,
        LocalFolderSource source = LocalFolderSource.None, string localFolder = "")
    {
        Route = route;
        ConnectedIsLocal = connectedIsLocal;
        Why = why ?? "";
        Facts = facts;
        Source = source;
        LocalFolder = localFolder ?? "";
    }

    public ServerRoute Route { get; }
    public bool ConnectedIsLocal { get; }
    public string Why { get; }
    public ServerRouteFacts? Facts { get; }

    /// <summary>
    /// LOCALDETECT1: where the folder of a local route (or of the connected local server, when the setting forced the
    /// remote route) came from; <see cref="LocalFolderSource.None"/> otherwise.
    /// </summary>
    public LocalFolderSource Source { get; }

    /// <summary>LOCALDETECT1: that folder (canonical), or empty.</summary>
    public string LocalFolder { get; }

    /// <summary>The source as the readback names it.</summary>
    public string SourceName => Source switch
    {
        LocalFolderSource.Hosted => "hosted",
        LocalFolderSource.Configured => "configured",
        LocalFolderSource.Detected => "detected",
        _ => "none",
    };
}

/// <summary>REMOTEINST1: whether an address the client joined is this machine.</summary>
internal static class LocalAddress
{
    /// <summary>
    /// True for <c>localhost</c>, a loopback address, or an address one of this machine's network
    /// interfaces carries. A port match alone cannot say "the local folder's server": another
    /// machine's server on the default port matches too, and installs would land in the wrong place.
    /// </summary>
    public static bool IsThisMachine(string? address, Func<System.Collections.Generic.IEnumerable<System.Net.IPAddress>>? interfaces = null)
    {
        string a = (address ?? "").Trim();
        // Accept the forms a connect description uses too: "SteamIPv4://127.0.0.1:9976", "[::1]:9976".
        int scheme = a.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) a = a.Substring(scheme + 3);
        if (a.StartsWith("[", StringComparison.Ordinal) && a.IndexOf("]:", StringComparison.Ordinal) > 0)
            a = a.Substring(0, a.IndexOf("]:", StringComparison.Ordinal) + 1);
        else if (a.IndexOf(':') > 0 && a.IndexOf(':') == a.LastIndexOf(':')) a = a.Substring(0, a.IndexOf(':'));
        if (a.Length == 0) return false;
        if (string.Equals(a, "localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (a.StartsWith("[", StringComparison.Ordinal) && a.EndsWith("]", StringComparison.Ordinal)) a = a.Substring(1, a.Length - 2);
        if (!System.Net.IPAddress.TryParse(a, out var ip)) return false;
        if (System.Net.IPAddress.IsLoopback(ip)) return true;
        try
        {
            var mine = interfaces?.Invoke() ?? (ServerProcessControl.OnMainThread() ? CachedInterfaceAddresses() : InterfaceAddresses());
            foreach (var own in mine)
                if (own.Equals(ip)) return true;
        }
        catch
        {
            // An unreadable interface list is "not known to be local", never a match.
        }

        return false;
    }

    // Review 2026-10-07: the interface walk (an iphlpapi call per adapter) never runs on the main thread; the frame reads
    // the last list, refreshed on the thread pool at most once a minute (empty until the first: "not known to be local").
    private static System.Net.IPAddress[] _cachedInterfaces = Array.Empty<System.Net.IPAddress>();
    private static long _cachedInterfacesAt = long.MinValue;
    private static int _interfacesRefreshing;

    private static System.Collections.Generic.IEnumerable<System.Net.IPAddress> CachedInterfaceAddresses()
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if ((_cachedInterfacesAt == long.MinValue || now - System.Threading.Interlocked.Read(ref _cachedInterfacesAt) > 60 * System.Diagnostics.Stopwatch.Frequency)
            && System.Threading.Interlocked.Exchange(ref _interfacesRefreshing, 1) == 0)
        {
            System.Threading.Tasks.Task.Run(() =>
            {
                try { _cachedInterfaces = new System.Collections.Generic.List<System.Net.IPAddress>(InterfaceAddresses()).ToArray(); }
                catch { /* the last list stays */ }
                System.Threading.Interlocked.Exchange(ref _cachedInterfacesAt, System.Diagnostics.Stopwatch.GetTimestamp());
                System.Threading.Interlocked.Exchange(ref _interfacesRefreshing, 0);
            });
        }

        return _cachedInterfaces;
    }

    private static System.Collections.Generic.IEnumerable<System.Net.IPAddress> InterfaceAddresses()
    {
        foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                yield return unicast.Address;
    }
}

/// <summary>
/// The Install tab's Status column words — one table for the local and remote routes. Each label is
/// short enough for the Status cell (<c>InstallColumns.StatusTextWidth</c>) in at most two lines
/// without an ellipsis; the full sentence goes to the row's drawer note. The live complaint this
/// answers: "Installs at the next…" cut off in a narrow column said nothing useful.
/// </summary>
internal static class InstallStatusLabels
{
    public const string PendingServerRestart = "Pending server restart";
    public const string RemovalPendingServerRestart = "Removal pending server restart";
    public const string PendingGameRestart = "Pending game restart";
    public const string RemovalPendingGameRestart = "Removal pending game restart";
    public const string InstalledOnServerRemote = "Installed on server (remote)";
    public const string InstalledOnServerLocal = "Installed on server (local)";
    public const string Sending = "Sending…";
    public const string Downloading = "Downloading…";
    public const string Verifying = "Verifying…";
    public const string Installing = "Installing…";
    public const string Removing = "Removing…";
    public const string RemovedFromServer = "Removed from server";
    public const string Cancelled = "Cancelled";
    public const string CantReachThunderstore = "Can't reach Thunderstore";
    public const string NeedsAdminList = "Needs admin list";
    public const string NotAllowed = "Not allowed";
    public const string RemoteInstallsOff = "Remote installs off";
    public const string NoProgenitor = "No Progenitor on server";
    public const string Checking = "Checking server…";
    public const string DownloadFailed = "Download failed";
    public const string PackageCheckFailed = "Package check failed";
    public const string ServerBusy = "Server busy";
    public const string TooManyInstalls = "Too many installs";
    public const string VersionDiffers = "Version differs";
    public const string NotOnThunderstore = "Not on Thunderstore";
    public const string NeededByOthers = "Needed by others";
    public const string ServerInstallFailed = "Server install failed";

    /// <summary>INSTALLFAIL1: a confirmed local install that wrote nothing; the sentence is in the drawer and the log.</summary>
    public const string InstallFailed = "Install failed";

    /// <summary>Every label, for the width test.</summary>
    public static readonly string[] All =
    {
        PendingServerRestart, RemovalPendingServerRestart, PendingGameRestart, RemovalPendingGameRestart,
        InstalledOnServerRemote, InstalledOnServerLocal, Sending, Downloading, Verifying, Installing, Removing,
        RemovedFromServer, Cancelled, CantReachThunderstore, NeedsAdminList, NotAllowed, RemoteInstallsOff,
        NoProgenitor, Checking, DownloadFailed, PackageCheckFailed, ServerBusy, TooManyInstalls, VersionDiffers,
        NotOnThunderstore, NeededByOthers, ServerInstallFailed, InstallFailed,
        SideStatusRules.MissingOnServer, SideStatusRules.MissingOnClient, SideStatusRules.BothPendingRestart,
        SideStatusRules.ServerUnknown, SideStatusRules.VersionMismatch,
    };

    /// <summary>The label for one remote step, running or finished.</summary>
    public static string Stage(RemoteInstallProgress progress)
    {
        if (progress == null) return "";
        return progress.Stage switch
        {
            RemoteInstallStage.Accepted => Sending,
            RemoteInstallStage.Downloading => Downloading,
            RemoteInstallStage.Verifying => Verifying,
            RemoteInstallStage.Installing => progress.Op == RemoteInstallOp.Uninstall ? Removing : Installing,
            RemoteInstallStage.HotLoaded => InstalledOnServerRemote,
            RemoteInstallStage.PendingRestart => progress.Op == RemoteInstallOp.Uninstall ? RemovalPendingServerRestart : PendingServerRestart,
            RemoteInstallStage.Removed => RemovedFromServer,
            RemoteInstallStage.Cancelled => Cancelled,
            _ => Failure(progress),
        };
    }

    /// <summary>
    /// REMOTEFOLLOW1 (live: "Not on Thunderstore" still offered Install, which the server refused
    /// again): a reason that is a fact about the package or the version, so asking again for the same
    /// version is refused the same way. Transient ones (network, busy, rate limit, a failed write)
    /// keep their retry.
    /// </summary>
    public static bool WouldRepeat(RemoteInstallReason reason) => reason is
        RemoteInstallReason.NotThunderstore
        or RemoteInstallReason.NotFound
        or RemoteInstallReason.VersionNotFound
        or RemoteInstallReason.NotVRising
        or RemoteInstallReason.BadPackage
        or RemoteInstallReason.BadVersion
        or RemoteInstallReason.PlanRefused;

    /// <summary>A refusal or failure as a short label; <see cref="ServerRouteCopy.Failure"/> is the sentence.</summary>
    public static string Failure(RemoteInstallProgress progress) => progress?.Reason switch
    {
        RemoteInstallReason.ThunderstoreUnreachable => CantReachThunderstore,
        RemoteInstallReason.NotAdmin => NeedsAdminList,
        RemoteInstallReason.NotAllowed => NotAllowed,
        RemoteInstallReason.Disabled => RemoteInstallsOff,
        RemoteInstallReason.DownloadFailed => DownloadFailed,
        RemoteInstallReason.VerifyFailed => PackageCheckFailed,
        RemoteInstallReason.Busy => ServerBusy,
        RemoteInstallReason.RateLimited => TooManyInstalls,
        RemoteInstallReason.VersionNotFound => VersionDiffers,
        RemoteInstallReason.NotFound or RemoteInstallReason.NotThunderstore or RemoteInstallReason.NotVRising => NotOnThunderstore,
        RemoteInstallReason.HasDependents => NeededByOthers,
        _ => ServerInstallFailed,
    };

    /// <summary>The label a route block shows on a server-side row.</summary>
    public static string Route(ServerRoute route) => route.Block switch
    {
        ServerRouteBlock.NotAdmin => NeedsAdminList,
        ServerRouteBlock.NotAllowed => NotAllowed,
        ServerRouteBlock.RemoteOff => RemoteInstallsOff,
        ServerRouteBlock.NotProgenitor => NoProgenitor,
        ServerRouteBlock.Unreachable => CantReachThunderstore,
        ServerRouteBlock.Pending => Checking,
        _ => "",
    };
}

/// <summary>REMOTEINST1: the player-facing words for routes and remote progress (US English).</summary>
internal static class ServerRouteCopy
{
    public const string NoServer = "No local server is configured; installing to my client.";
    public const string LocalManagement = "Local management";
    public const string RemoteManagement = "Remote management";
    public const string NotProgenitor = "Server doesn't run Progenitor";
    public const string RemoteOff = "Server has remote installs off";
    public const string NotAdmin = "Server installs need you on this server's admin list";
    public const string NotAdminHelp =
        "Ask the host to add your SteamID to the server's adminlist.txt; remote installs then work as soon as you join. "
        + "(An admin who is not on that file can also run adminauth in the console.)";
    public const string NotAllowed = "Server installs are limited to listed admins";
    public const string NotAllowedHelp = "Ask the host to add your SteamID to [RemoteInstall] AllowedSteamIds.";
    public const string Pending = "Checking what this server allows…";
    public const string UnreachableHelp =
        "Your host may block outbound connections. Uninstall still works; installs need the server to reach thunderstore.io.";

    public const string InstalledLocal = InstallStatusLabels.InstalledOnServerLocal;
    public const string InstalledRemote = InstallStatusLabels.InstalledOnServerRemote;
    public const string PendingLocal = InstallStatusLabels.PendingServerRestart;
    public const string PendingRemote = InstallStatusLabels.PendingServerRestart;
    public const string RemovedAtRestart = InstallStatusLabels.RemovalPendingServerRestart;

    /// <summary>The drawer sentence behind <see cref="PendingLocal"/>.</summary>
    public const string PendingLocalSentence = "Installs on the server at its next restart (written to the local server folder).";

    /// <summary>The drawer sentence behind <see cref="PendingRemote"/>.</summary>
    public const string PendingRemoteSentence = "Installs on the server at its next restart (written by the server itself).";

    /// <summary>The drawer sentence behind <see cref="RemovedAtRestart"/>.</summary>
    public const string RemovedAtRestartSentence = "Removed from the server at its next restart.";

    /// <summary>A server named by its name or its address (not just "the connected server").</summary>
    private static bool Known(ServerIdentity? server) =>
        server != null && (server.HasName || (server.Remote && server.Address.Length > 0) || (!server.Remote && server.Folder.Length > 0));

    /// <summary>
    /// CONFIRMTARGET1: the drawer sentence of a pending server install, naming the server —
    /// <c>Pending restart of Progenitor Forms Test. Installs at its next restart (written by the
    /// server itself).</c> An unknown server keeps <see cref="PendingLocalSentence"/> /
    /// <see cref="PendingRemoteSentence"/>.
    /// </summary>
    public static string PendingSentence(bool writtenByServer, ServerIdentity? server)
    {
        if (!Known(server)) return writtenByServer ? PendingRemoteSentence : PendingLocalSentence;
        return server!.PendingRestartOf + ". Installs at its next restart ("
            + (writtenByServer ? "written by the server itself" : "written to the local server folder") + ").";
    }

    /// <summary>CONFIRMTARGET1: <c>Removed from Progenitor Forms Test at its next restart.</c></summary>
    public static string RemovedAtRestartSentenceFor(ServerIdentity? server) =>
        Known(server) ? RemovedAtRestartOf(server) + "." : RemovedAtRestartSentence;

    private static string RemovedAtRestartOf(ServerIdentity? server) =>
        "Removed from " + server!.ShortName + " at its next restart";
    /// <summary>F20: the drawer's button. It stops the server; it does not start it again, so it does not say "Restart".</summary>
    public const string RestartNowCaption = "Save and stop server";
    public const string RestartNowHelp =
        "Saves the world and stops the server so the change loads at its next start. Your host or panel has to start it again; Progenitor never restarts it for you.";

    public static string Unreachable(string? cause) =>
        "Remote installs unavailable: the server can't reach Thunderstore" + CauseSuffix(cause) + ".";

    public static string CauseWords(string? cause)
    {
        string c = (cause ?? "").Trim();
        if (c.StartsWith("http-", StringComparison.Ordinal)) return "HTTP " + c.Substring(5);
        return c switch
        {
            "dns" => "name lookup failed",
            "timeout" => "timed out",
            "tls" => "secure connection failed",
            "refused" => "connection refused",
            "stalled" => "the transfer stalled",
            "network" => "network error",
            "config" => "bad ThunderstoreBaseUrl",
            _ => c,
        };
    }

    private static string CauseSuffix(string? cause)
    {
        string words = CauseWords(cause);
        return words.Length > 0 ? " (" + words + ")" : "";
    }

    /// <summary>The row's status words for one in-flight or finished remote step.</summary>
    public static string Stage(RemoteInstallProgress progress) => Stage(progress, null);

    /// <summary>
    /// CONFIRMTARGET1: the same words naming the server when it is known —
    /// <c>Downloading on Progenitor Forms Test…</c>, <c>Pending restart of 1.2.3.4:9876</c>.
    /// An unnamed, unaddressed server keeps the plain "server".
    /// </summary>
    public static string Stage(RemoteInstallProgress progress, ServerIdentity? server)
    {
        if (progress == null) return "";
        string on = Known(server) ? server!.ShortName : "server";
        return progress.Stage switch
        {
            RemoteInstallStage.Accepted => "Sent to " + on + "…",
            RemoteInstallStage.Downloading => "Downloading on " + on + "…",
            RemoteInstallStage.Verifying => "Verifying on " + on + "…",
            RemoteInstallStage.Installing => progress.Op == RemoteInstallOp.Uninstall ? "Removing on " + on + "…" : "Installing on " + on + "…",
            RemoteInstallStage.HotLoaded => InstalledRemote,
            RemoteInstallStage.PendingRestart when Known(server) => progress.Op == RemoteInstallOp.Uninstall
                ? RemovedAtRestartOf(server)
                : server!.PendingRestartOf,
            RemoteInstallStage.PendingRestart => progress.Op == RemoteInstallOp.Uninstall ? RemovedAtRestart : PendingRemote,
            RemoteInstallStage.Removed => "Removed from " + on,
            RemoteInstallStage.Cancelled => "Cancelled",
            _ => Failure(progress),
        };
    }

    /// <summary>A refusal or failure in plain words.</summary>
    public static string Failure(RemoteInstallProgress progress)
    {
        if (progress == null) return "";
        string detail = progress.Detail ?? "";
        return progress.Reason switch
        {
            RemoteInstallReason.ThunderstoreUnreachable when detail.StartsWith("config:", StringComparison.Ordinal) =>
                "The server's ThunderstoreBaseUrl setting is not a valid https URL.",
            RemoteInstallReason.ThunderstoreUnreachable =>
                "The server can't reach Thunderstore" + CauseSuffix(detail) + ". Your host may block outbound connections.",
            RemoteInstallReason.DownloadFailed => "The server's download failed" + (detail.Length > 0 ? " (" + detail + ")" : "") + ". Nothing was installed.",
            RemoteInstallReason.VerifyFailed => "The package failed the server's checks" + (detail.Length > 0 ? ": " + detail : "") + ". Nothing was installed.",
            RemoteInstallReason.NotAdmin => NotAdmin + ".",
            RemoteInstallReason.Disabled => RemoteOff + ".",
            RemoteInstallReason.NotAllowed => NotAllowed + ".",
            RemoteInstallReason.RateLimited => "Too many server installs in a minute; try again shortly.",
            RemoteInstallReason.Busy => "The server is busy with another install; try again when it finishes.",
            RemoteInstallReason.VersionNotFound => "The server sees a different version (" + detail + ").",
            RemoteInstallReason.NotFound => "The server can't find this package on Thunderstore's V Rising index.",
            RemoteInstallReason.HasDependents => "Other server mods need it (" + detail + ").",
            RemoteInstallReason.HotLoadUnconfirmed => "Written to the server's reload folder; the server has not reported it loaded yet.",
            RemoteInstallReason.None => detail,
            _ => detail.Length > 0 ? detail : RemoteInstallWire.ReasonName(progress.Reason),
        };
    }
}

/// <summary>
/// F23 (live 2026-10-07): while this client hosts a private game, the hosted child (the game folder's bundled server, staged
/// by the hosted launcher) is this PC's local server: the Install tab's route is local to that folder, ahead of
/// <c>[Mods] DedicatedServerPath</c> (which only matters when connected to that folder's server). Null when not hosting.
/// </summary>
internal static class HostedServerFolder
{
    /// <summary>The name boxes use for it.</summary>
    public const string Name = "your private game";

    /// <summary>Tests: the hosted folder now (null: not hosting). Null probe: the live reading.</summary>
    internal static Func<string?>? Probe { get; set; }

    /// <summary>The hosted launcher's last staged server folder this process (live: set when it stages the child).</summary>
    internal static string? LastStaged { get; set; }

    private static string? _cached;
    private static long _cachedAt;

    /// <summary>
    /// The hosted private game's server folder (holding <c>VRisingServer.exe</c>), or null when not hosting. Review (L6): read
    /// at most every two seconds (a reflection read and a file check), since frame paths ask.
    /// </summary>
    public static string? Current()
    {
        if (Probe == null)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (_cachedAt != 0 && now - _cachedAt < 2 * System.Diagnostics.Stopwatch.Frequency) return _cached;
            _cached = Read();
            _cachedAt = now;
            return _cached;
        }

        return Read();
    }

    private static string? Read()
    {
        if (Probe != null)
        {
            try { return Probe(); }
            catch { return null; }
        }
#if PROGENITOR_TESTS
        return null;
#else
        try
        {
            if (!BootstrapLink.IsHostingPrivateGame) return null;
            string dir = LastStaged ?? System.IO.Path.Combine(BepInEx.Paths.GameRootPath, "VRising_Server");
            return System.IO.File.Exists(System.IO.Path.Combine(dir, "VRisingServer.exe")) ? System.IO.Path.GetFullPath(dir) : null;
        }
        catch { return null; }
#endif
    }

    /// <summary>The hosted folder's BepInEx root, or null.</summary>
    public static string? BepInExRoot() => Current() is { } dir ? System.IO.Path.Combine(dir, "BepInEx") : null;
}
