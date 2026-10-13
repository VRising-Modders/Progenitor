using System;

namespace Progenitor.Install;

/// <summary>What this client holds of a listing.</summary>
internal enum ClientHalfState
{
    Absent = 0,
    Installed = 1,
    PendingRestart = 2,
    RemovalPending = 3,
}

/// <summary>What the server holds of a listing, as far as this client can tell.</summary>
internal enum ServerHalfState
{
    Absent = 0,
    Installed = 1,
    PendingRestart = 2,
    RemovalPending = 3,

    /// <summary>No source could say: not connected and no local server, or a server that lists nothing.</summary>
    Unknown = 4,
}

/// <summary>Where the server half's state came from, in priority order.</summary>
internal enum ServerHalfSource
{
    Unknown = 0,

    /// <summary>The local server folder's ledger and plugins.</summary>
    Local = 1,

    /// <summary>The connected server's remote-install ledger rows (authorized players; includes pending).</summary>
    Remote = 2,

    /// <summary>The connected server's handshake mod list (loaded mods only).</summary>
    Handshake = 3,
}

/// <summary>The action a both-sided row offers.</summary>
internal enum SideAction
{
    None = 0,
    InstallOnServer = 1,
    InstallOnClient = 2,
    UpdateServer = 3,
    UpdateClient = 4,
}

/// <summary>Everything the per-side decision reads.</summary>
internal readonly struct SideInputs
{
    public SideInputs(
        ClientHalfState client,
        string? clientVersion,
        ServerHalfState server,
        string? serverVersion,
        ServerHalfSource source,
        bool needsBoth,
        ServerRoute route,
        bool versionsAccepted = false)
    {
        Client = client;
        ClientVersion = (clientVersion ?? "").Trim();
        Server = server;
        ServerVersion = (serverVersion ?? "").Trim();
        Source = source;
        NeedsBoth = needsBoth;
        Route = route;
        VersionsAccepted = versionsAccepted;
    }

    public ClientHalfState Client { get; }
    public string ClientVersion { get; }
    public ServerHalfState Server { get; }
    public string ServerVersion { get; }
    public ServerHalfSource Source { get; }

    /// <summary>Only a manifest-verified <c>side: both</c> declaration; Thunderstore category tags never set it.</summary>
    public bool NeedsBoth { get; }

    public ServerRoute Route { get; }

    /// <summary>
    /// Mod compatibility C3: the two versions differ and the connected server's own judgment accepts this client's (its
    /// Version matching, a per-mod rule or the newer side's range). Not a version mismatch then.
    /// </summary>
    public bool VersionsAccepted { get; }
}

/// <summary>The decided Status label, its sentence, the action and the tone hints.</summary>
internal readonly struct SideDecision
{
    public SideDecision(string label, string sentence, SideAction action, string note, bool warn, bool muted, bool mismatch)
    {
        _label = label ?? "";
        _sentence = sentence ?? "";
        Action = action;
        _note = note ?? "";
        Warn = warn;
        Muted = muted;
        Mismatch = mismatch;
    }

    private readonly string? _label;
    private readonly string? _sentence;
    private readonly string? _note;

    /// <summary>The short Status label; empty when the per-side rule has nothing to add (also for <c>default</c>).</summary>
    public string Label => _label ?? "";

    /// <summary>The full sentence for the drawer note.</summary>
    public string Sentence => _sentence ?? "";

    public SideAction Action { get; }

    /// <summary>Why there is no button (no server route); empty otherwise.</summary>
    public string Note => _note ?? "";

    public bool Warn { get; }
    public bool Muted { get; }
    public bool Mismatch { get; }

    public bool Decided => Label.Length > 0;
}

/// <summary>
/// REMOTEINST1 per-side status (user-approved): one decision for a listing's two halves, used by the
/// Install tab row and the mod page head block so the two never disagree. Only a declared
/// both-sided mod (manifest-verified <c>side: both</c>) is ever "missing" a half; a mod Thunderstore
/// merely tags Server + Client shows its sides as information, with no warning.
/// </summary>
internal static class SideStatusRules
{
    public const string MissingOnServer = "Missing on server";
    public const string MissingOnClient = "Missing on client";
    public const string BothPendingRestart = "Both pending restart";
    public const string ServerUnknown = "Server: unknown";
    public const string VersionMismatch = "Version mismatch";
    public const string Installed = "Installed";

    public const string InstallOnServerCaption = "Install on server";
    public const string InstallOnClientCaption = "Install on client";
    public const string UpdateServerCaption = "Update server";
    public const string UpdateClientCaption = "Update client";

    public const string AskServerAdmin = "Ask a server admin";
    public const string NoServerToInstallTo = "No server to install to";

    /// <summary>REMOTEUNREACH1: why no server action is offered, in the block's own words (<see cref="SideCells.BlockNote"/>); none: no server.</summary>
    internal static string BlockedNote(ServerRoute route) =>
        route.Block is ServerRouteBlock.None or ServerRouteBlock.NoServer ? NoServerToInstallTo : SideCells.BlockNote(route);

    public static bool ClientPresent(ClientHalfState s) => s is ClientHalfState.Installed or ClientHalfState.PendingRestart;
    public static bool ServerPresent(ServerHalfState s) => s is ServerHalfState.Installed or ServerHalfState.PendingRestart;

    public static SideDecision Decide(in SideInputs input)
    {
        if (!input.NeedsBoth) return default;
        var c = input.Client;
        var s = input.Server;

        if (c == ClientHalfState.RemovalPending)
            return new SideDecision(InstallStatusLabels.RemovalPendingGameRestart, "Removed from this client at the next game restart.", SideAction.None, "", false, false, false);
        if (s == ServerHalfState.RemovalPending)
            return new SideDecision(InstallStatusLabels.RemovalPendingServerRestart, "Removed from the server at its next restart.", SideAction.None, "", false, false, false);

        bool clientHere = ClientPresent(c);
        if (s == ServerHalfState.Unknown)
        {
            if (!clientHere) return default;
            return new SideDecision(ServerUnknown,
                "This mod needs its server part too; there is no server this client can check right now.", SideAction.None, "", false, true, false);
        }

        bool serverHere = ServerPresent(s);
        if (!clientHere && !serverHere) return default;

        if (clientHere && !serverHere)
        {
            if (input.Route.CanInstall)
                return new SideDecision(MissingOnServer, "This mod needs its server part, and the server does not have it.",
                    SideAction.InstallOnServer, "", true, false, false);
            // REMOTEUNREACH1: the block's own words (an unreachable Thunderstore is not an admin matter).
            string note = BlockedNote(input.Route);
            return new SideDecision(MissingOnServer, "This mod needs its server part, and the server does not have it. " + note + ".",
                SideAction.None, note, true, false, false);
        }

        if (serverHere && !clientHere)
            return new SideDecision(MissingOnClient, "The server has this mod; this client does not, so it will not work here until it is installed.",
                SideAction.InstallOnClient, "", true, false, false);

        // Both present.
        bool differ = input.ClientVersion.Length > 0 && input.ServerVersion.Length > 0
            && !string.Equals(input.ClientVersion, input.ServerVersion, StringComparison.Ordinal);
        if (differ && !input.VersionsAccepted)
        {
            bool serverOlder = Compare(input.ServerVersion, input.ClientVersion) < 0;
            var action = serverOlder ? (input.Route.CanInstall ? SideAction.UpdateServer : SideAction.None) : SideAction.UpdateClient;
            return new SideDecision(VersionMismatch,
                "This client has " + input.ClientVersion + " and the server has " + input.ServerVersion + "; both sides need the same version.",
                action, action == SideAction.None ? BlockedNote(input.Route) : "", true, false, true);
        }

        bool cPending = c == ClientHalfState.PendingRestart;
        bool sPending = s == ServerHalfState.PendingRestart;
        if (cPending && sPending)
            return new SideDecision(BothPendingRestart, "Both halves load at their next restart: the game and the server.", SideAction.None, "", false, false, false);
        if (cPending)
            return new SideDecision(InstallStatusLabels.PendingGameRestart, "The server has it; this client loads it at the next game restart.", SideAction.None, "", false, false, false);
        if (sPending)
            return new SideDecision(InstallStatusLabels.PendingServerRestart, "This client has it; the server loads it at its next restart.", SideAction.None, "", false, false, false);
        if (differ)
            return new SideDecision(Installed, "This client has " + input.ClientVersion + " and the server has " + input.ServerVersion
                + "; the server accepts this client's version.", SideAction.None, "", false, false, false);
        return new SideDecision(Installed, "Installed on this client and on the server.", SideAction.None, "", false, false, false);
    }

    /// <summary>Numeric dotted-version comparison; anything unparsable compares as text.</summary>
    public static int Compare(string a, string b)
    {
        var pa = (a ?? "").Split('.');
        var pb = (b ?? "").Split('.');
        for (int i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            string x = i < pa.Length ? pa[i] : "0";
            string y = i < pb.Length ? pb[i] : "0";
            if (long.TryParse(x, out long nx) && long.TryParse(y, out long ny))
            {
                if (nx != ny) return nx.CompareTo(ny);
                continue;
            }

            int t = string.CompareOrdinal(x, y);
            if (t != 0) return t;
        }

        return 0;
    }

    /// <summary>
    /// The server half from the sources in priority order: the local folder (when the local route
    /// applies, or a local server is configured and no remote route is in use), the remote rows
    /// (authorized), the handshake list (loaded only), else unknown.
    /// </summary>
    public static (ServerHalfState State, string Version, ServerHalfSource Source) ServerHalf(
        ServerRoute route,
        bool localConfigured,
        (ServerHalfState State, string Version)? local,
        RemoteServerRow? remoteRow,
        bool remoteRowsKnown,
        string? handshakeVersion,
        bool handshakeKnown)
    {
        bool useLocal = route.Kind == ServerRouteKind.Local || (localConfigured && route.Kind != ServerRouteKind.Remote);
        if (useLocal && local is { } l) return (l.State, l.Version, ServerHalfSource.Local);
        if (remoteRowsKnown)
        {
            if (remoteRow == null) return (ServerHalfState.Absent, "", ServerHalfSource.Remote);
            var state = remoteRow.State switch
            {
                RemoteRowState.Pending => ServerHalfState.PendingRestart,
                RemoteRowState.UninstallPending => ServerHalfState.RemovalPending,
                _ => ServerHalfState.Installed,
            };
            return (state, remoteRow.Version, ServerHalfSource.Remote);
        }

        if (handshakeKnown)
            return handshakeVersion != null
                ? (ServerHalfState.Installed, handshakeVersion, ServerHalfSource.Handshake)
                : (ServerHalfState.Absent, "", ServerHalfSource.Handshake);
        return (ServerHalfState.Unknown, "", ServerHalfSource.Unknown);
    }

    public static string Name(ClientHalfState s) => s switch
    {
        ClientHalfState.Installed => "installed",
        ClientHalfState.PendingRestart => "pending-restart",
        ClientHalfState.RemovalPending => "removal-pending",
        _ => "absent",
    };

    public static string Name(ServerHalfState s) => s switch
    {
        ServerHalfState.Installed => "installed",
        ServerHalfState.PendingRestart => "pending-restart",
        ServerHalfState.RemovalPending => "removal-pending",
        ServerHalfState.Unknown => "unknown",
        _ => "absent",
    };

    public static string Name(ServerHalfSource s) => s switch
    {
        ServerHalfSource.Local => "local",
        ServerHalfSource.Remote => "remote",
        ServerHalfSource.Handshake => "handshake",
        _ => "unknown",
    };

    public static string Caption(SideAction action) => action switch
    {
        SideAction.InstallOnServer => InstallOnServerCaption,
        SideAction.InstallOnClient => InstallOnClientCaption,
        SideAction.UpdateServer => UpdateServerCaption,
        SideAction.UpdateClient => UpdateClientCaption,
        _ => "",
    };
}
