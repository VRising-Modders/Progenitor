using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Progenitor.Install;

/// <summary>
/// REMOTEINST1: the client's half of the remote install channel, game-free. It holds what the
/// connected server last said (its status, its ledger rows, each operation's latest step), sends
/// requests through an injected sender, and is reset whenever the connection ends. The Install tab
/// reads it; nothing in here guesses — no status from the server means no remote route.
/// </summary>
internal sealed class RemoteInstallClient
{
    /// <summary>
    /// How long after a completed handshake the client waits for the server's status push before
    /// treating the server as one without remote installs (an older Progenitor server sends none).
    /// </summary>
    public const double StatusGraceSeconds = 10;

    public static RemoteInstallClient Shared { get; } = new();

    private readonly object _gate = new();
    private RemoteInstallStatus? _status;
    private double? _handshakeSeenAt;
    private int _nextNonce;
    private readonly Dictionary<int, (RemoteInstallOp Op, string Package)> _ops = new();
    private readonly Dictionary<string, RemoteInstallProgress> _latest = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _lastHeard = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A running step with no word from the server for this long is treated as lost (the server's
    /// Core reloaded or the reply was dropped): the row stops saying "Downloading on server…".
    /// The server's own total timeout is 120 seconds by default.
    /// </summary>
    public const double StaleSeconds = 180;
    private ulong _steamId;

    /// <summary>Sends one encoded request to the connected server. Null on a process with no client route.</summary>
    internal Func<byte[], bool>? Send { get; set; }

    /// <summary>Raised (on whatever thread delivered the packet) when the status, the rows or a step changed.</summary>
    internal Action? Changed { get; set; }

    /// <summary>Where the last SteamID a server reported for this player is kept between sessions.</summary>
    internal string? SteamIdFile { get; set; }

    internal Func<double> Now { get; set; } =
        () => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;

    /// <summary>The server's last status, or null.</summary>
    public RemoteInstallStatus? Status
    {
        get { lock (_gate) return _status; }
    }

    /// <summary>True once the connected server has answered on this channel during this connection.</summary>
    public bool ServerSupportsRemote
    {
        get { lock (_gate) return _status != null; }
    }

    /// <summary>
    /// The route inputs for the Install tab. A handshake without a status push for
    /// <see cref="StatusGraceSeconds"/> reads as "remote installs off" rather than "still checking".
    /// </summary>
    public ServerRouteInputs Inputs(bool localConfigured, bool connected, bool connectedIsLocal, bool handshakeOk)
    {
        RemoteInstallStatus? status;
        lock (_gate)
        {
            status = _status;
            if (status == null && connected && handshakeOk)
            {
                double now = Now();
                _handshakeSeenAt ??= now;
                if (now - _handshakeSeenAt.Value >= StatusGraceSeconds)
                    status = new RemoteInstallStatus(0, false, false, false, RemoteReachability.Unknown, "", false, null);
            }
        }

        return new ServerRouteInputs(localConfigured, connected, connectedIsLocal, handshakeOk, status);
    }

    // ---- inbound ---------------------------------------------------------------------------

    /// <summary>Decode and adopt one server packet. False when it is not a well-formed reply.</summary>
    public bool Accept(ReadOnlySpan<byte> packet)
    {
        switch (RemoteInstallWire.DirectionOf(packet))
        {
            case RemoteInstallWire.DirectionStatus:
                if (!RemoteInstallWire.TryDecodeStatus(packet, out var status)) return false;
                Accept(status);
                return true;
            case RemoteInstallWire.DirectionProgress:
                if (!RemoteInstallWire.TryDecodeProgress(packet, out var progress)) return false;
                Accept(progress);
                return true;
            default:
                return false;
        }
    }

    public void Accept(RemoteInstallStatus status)
    {
        if (status == null) return;
        bool newId;
        lock (_gate)
        {
            _status = status;
            newId = status.SteamId != 0 && status.SteamId != _steamId;
            if (status.SteamId != 0) _steamId = status.SteamId;
        }

        if (newId) PersistSteamId(status.SteamId);
        try { AdminAuthFlow.Shared.Observe(status); }
        catch { /* the button's state is a hint */ }
        Notify();
    }

    public void Accept(RemoteInstallProgress progress)
    {
        if (progress == null) return;
        lock (_gate)
        {
            // Only a reply to a request this client made in this connection is believed.
            if (!_ops.TryGetValue(progress.Nonce, out var op)) return;
            string package = op.Package.Length > 0 ? op.Package : progress.Package;
            if (package.Length == 0) return;
            if (op.Op == RemoteInstallOp.Cancel)
            {
                if (progress.IsTerminal) _ops.Remove(progress.Nonce);
                return;
            }

            _latest[package] = progress;
            _lastHeard[package] = Now();
            if (progress.IsTerminal)
            {
                _ops.Remove(progress.Nonce);
                // REMOTETEST1: the server's answer, once per finished request.
                InstallLog.Info("Install: remote " + op.Op + " " + package + " finished: " + progress.Stage
                    + (progress.Reason != RemoteInstallReason.None ? " (" + progress.Reason + ")" : "")
                    + (string.IsNullOrEmpty(progress.Detail) ? "" : ": " + progress.Detail) + ".");
            }
        }

        Notify();
        // A finished step changed the server's ledger: ask for the rows again.
        if (progress.IsTerminal && progress.Stage != RemoteInstallStage.Refused) RequestStatus();
    }

    private void Notify()
    {
        try { Changed?.Invoke(); }
        catch { /* a UI hint never breaks the channel */ }
    }

    // ---- outbound --------------------------------------------------------------------------

    /// <summary>REMOTEFOLLOW1: how often an open Install tab re-asks while the route waits on authorization.</summary>
    public static readonly TimeSpan BlockedRefresh = TimeSpan.FromSeconds(4);

    /// <summary>REMOTEFOLLOW1: how often an open Install tab re-asks otherwise (rows another admin changed).</summary>
    public static readonly TimeSpan IdleRefresh = TimeSpan.FromSeconds(30);

    private DateTime _lastStatusAsk = DateTime.MinValue;

    /// <summary>
    /// REMOTEFOLLOW1 (live 2026-10-03: after the adminlist was restored the route stayed
    /// <c>not-admin</c> for 15+ s until the tab was reopened). The server only sends a status when
    /// asked, so an open Install tab asks again on a short beat while the route is blocked by
    /// authorization (not admin, not allowed, no answer yet) and on a long one otherwise. The answer
    /// re-decides the route and refreshes the Server cells through <see cref="Changed"/>. Returns
    /// whether a request was sent.
    /// </summary>
    public bool RequestStatusIfDue(DateTime nowUtc, bool authorizationBlocked)
    {
        var interval = authorizationBlocked ? BlockedRefresh : IdleRefresh;
        lock (_gate)
        {
            if (nowUtc - _lastStatusAsk < interval) return false;
            _lastStatusAsk = nowUtc;
        }

        return RequestStatus();
    }

    /// <summary>Ask the server for its status and (for an allowed admin) its ledger rows.</summary>
    public bool RequestStatus()
    {
        var send = Send;
        // Only a server that has spoken on this channel during this connection is asked: an older
        // server has no handler for the id, and an unowned id would reach the game's own dispatcher.
        if (send == null || !ServerSupportsRemote) return false;
        int nonce = NextNonce();
        try { return send(RemoteInstallWire.EncodeRequest(new RemoteInstallRequest(nonce, RemoteInstallOp.List, "", ""))); }
        catch { return false; }
    }

    /// <summary>
    /// Start one operation. Returns the nonce, or 0 with <paramref name="refusal"/> set when nothing
    /// was sent (no route, a malformed id, an operation for this package already in flight).
    /// </summary>
    public int Begin(RemoteInstallOp op, string package, string version, bool force, out string? refusal)
    {
        refusal = null;
        var send = Send;
        if (send == null || !ServerSupportsRemote)
        {
            refusal = ServerRouteCopy.RemoteOff;
            return 0;
        }

        // SRVUNINST1: an uninstall may address the server's copy by plugin GUID (RemotePluginRef).
        bool addressable = RemoteInstallWire.IsValidPackage(package)
            || (op is RemoteInstallOp.Uninstall or RemoteInstallOp.Cancel && RemotePluginRef.TryGuid(package, out _));
        if (!addressable)
        {
            refusal = "Not a Thunderstore package id.";
            return 0;
        }

        if (op is RemoteInstallOp.Install or RemoteInstallOp.Update && !RemoteInstallWire.IsValidVersion(version))
        {
            refusal = "Not an exact version.";
            return 0;
        }

        int nonce;
        lock (_gate)
        {
            if (op != RemoteInstallOp.Cancel && InFlightLocked(package) != null)
            {
                refusal = "A server step for this mod is already running.";
                return 0;
            }

            nonce = NextNonceLocked();
            _ops[nonce] = (op, package);
            _latest[package] = new RemoteInstallProgress(nonce, op, RemoteInstallStage.Accepted, RemoteInstallReason.None, package, version, "");
            _lastHeard[package] = Now();
        }

        bool sent;
        try { sent = send(RemoteInstallWire.EncodeRequest(new RemoteInstallRequest(nonce, op, package, version, force))); }
        catch { sent = false; }
        if (!sent)
        {
            lock (_gate)
            {
                _ops.Remove(nonce);
                _latest[package] = new RemoteInstallProgress(nonce, op, RemoteInstallStage.Failed, RemoteInstallReason.Unavailable, package, version,
                    "the request could not be sent");
            }

            refusal = "The request could not be sent.";
            InstallLog.Warn("Install: remote " + op + " " + package + " " + version + " could not be sent.");
            Notify();
            return 0;
        }

        // REMOTETEST1: every remote step is in the client log, like the local ones.
        InstallLog.Info("Install: remote " + op + " " + package + (version.Length > 0 ? " " + version : "") + (force ? " (force)" : "")
            + " sent to the connected server (request " + nonce + ").");
        Notify();
        return nonce;
    }

    /// <summary>Ask the server to cancel this package's running step.</summary>
    public bool Cancel(string package)
    {
        var send = Send;
        if (send == null) return false;
        int nonce;
        lock (_gate)
        {
            if (InFlightLocked(package) == null) return false;
            nonce = NextNonceLocked();
            _ops[nonce] = (RemoteInstallOp.Cancel, package);
        }

        InstallLog.Info("Install: remote cancel of " + package + "'s running step sent (request " + nonce + ").");
        try { return send(RemoteInstallWire.EncodeRequest(new RemoteInstallRequest(nonce, RemoteInstallOp.Cancel, package, ""))); }
        catch { return false; }
    }

    // ---- reads for the Install tab ----------------------------------------------------------

    /// <summary>The latest step of the operation on this package, finished or not; null when there was none.</summary>
    public RemoteInstallProgress? LatestFor(string? package)
    {
        if (string.IsNullOrEmpty(package)) return null;
        lock (_gate) return _latest.TryGetValue(package!, out var p) ? p : null;
    }

    /// <summary>The running (non-terminal) step for this package, or null.</summary>
    public RemoteInstallProgress? InFlightFor(string? package)
    {
        if (string.IsNullOrEmpty(package)) return null;
        lock (_gate) return InFlightLocked(package!);
    }

    private RemoteInstallProgress? InFlightLocked(string package)
    {
        if (!_latest.TryGetValue(package, out var p) || p.IsTerminal) return null;
        if (_lastHeard.TryGetValue(package, out double heard) && Now() - heard >= StaleSeconds)
        {
            // Lost: say so once, and stop believing replies to that nonce.
            _ops.Remove(p.Nonce);
            _latest[package] = new RemoteInstallProgress(p.Nonce, p.Op, RemoteInstallStage.Failed, RemoteInstallReason.Unavailable,
                p.Package, p.Version, "no answer from the server; reopen the Install tab to see what it has");
            return null;
        }

        return p;
    }

    /// <summary>Forget a finished step's message (the player acknowledged it by acting on the row again).</summary>
    public void ClearFinished(string? package)
    {
        if (string.IsNullOrEmpty(package)) return;
        lock (_gate)
        {
            if (_latest.TryGetValue(package!, out var p) && p.IsTerminal) _latest.Remove(package!);
        }
    }

    /// <summary>
    /// The server's ledger row for one listing, matched with the same spelling rule the local
    /// ledgers use (the Thunderstore key, or a plugin id the identity cache ties to it).
    /// </summary>
    public RemoteServerRow? RowFor(CatalogEntry? entry, LedgerIdentityProbe? identity = null)
    {
        if (entry == null) return null;
        RemoteInstallStatus? status;
        lock (_gate) status = _status;
        if (status == null || !status.RowsIncluded) return null;
        for (int i = 0; i < status.Rows.Count; i++)
        {
            if (LedgerSpelling.Matches(entry, status.Rows[i].Package, identity)) return status.Rows[i];
        }

        return null;
    }

    /// <summary>
    /// SRVUNINST1: the server's row for one plugin GUID (<c>plugin:&lt;guid&gt;</c>), which says what an
    /// uninstall of it would do there. Null when the server sent no rows or did not identify it (an
    /// older server never does).
    /// </summary>
    public RemoteServerRow? PluginRowFor(string? pluginId)
    {
        string package = RemotePluginRef.Of(pluginId);
        if (package.Length == 0) return null;
        RemoteInstallStatus? status;
        lock (_gate) status = _status;
        if (status == null || !status.RowsIncluded) return null;
        for (int i = 0; i < status.Rows.Count; i++)
        {
            if (string.Equals(status.Rows[i].Package, package, StringComparison.OrdinalIgnoreCase)) return status.Rows[i];
        }

        return null;
    }

    /// <summary>
    /// F16: the connected server's own local test index offers <paramref name="key"/> (its status row <c>local:&lt;key&gt;</c>),
    /// with the version that index has; null when it does not (or the server is older).
    /// </summary>
    public RemoteServerRow? LocalOfferFor(string? key)
    {
        string package = RemoteLocalOffer.Of(key);
        if (package.Length == 0) return null;
        RemoteInstallStatus? status;
        lock (_gate) status = _status;
        if (status == null || !status.RowsIncluded) return null;
        for (int i = 0; i < status.Rows.Count; i++)
            if (string.Equals(status.Rows[i].Package, package, StringComparison.OrdinalIgnoreCase)) return status.Rows[i];
        return null;
    }

    // ---- SteamID ---------------------------------------------------------------------------

    /// <summary>
    /// The SteamID a Progenitor server last resolved for this player (this session, else the one
    /// kept from an earlier session). 0 when no server has said.
    /// </summary>
    public ulong LastKnownSteamId
    {
        get
        {
            lock (_gate)
            {
                if (_steamId != 0) return _steamId;
            }

            ulong stored = ReadSteamId();
            if (stored != 0)
            {
                lock (_gate)
                {
                    if (_steamId == 0) _steamId = stored;
                }
            }

            return stored;
        }
    }

    private ulong ReadSteamId()
    {
        string? path = SteamIdFile;
        if (string.IsNullOrEmpty(path)) return 0;
        try
        {
            if (!File.Exists(path)) return 0;
            string text = File.ReadAllText(path!).Trim();
            return RemoteInstallSettings.IsSteamId64(text, out ulong id) ? id : 0;
        }
        catch
        {
            return 0;
        }
    }

    private void PersistSteamId(ulong id)
    {
        string? path = SteamIdFile;
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path!)!);
            File.WriteAllText(path!, id.ToString(CultureInfo.InvariantCulture));
        }
        catch
        {
            // A cache, not a record: the next status packet carries the id again.
        }
    }

    // ---- lifetime --------------------------------------------------------------------------

    /// <summary>
    /// The connection ended: the server's status and every running step are forgotten. A finished
    /// step's result (refused, failed, done) stays until this package's next operation, so a reply
    /// that landed just before a reconnect or a Core-swap refresh is still on the row; a step still
    /// running reads as lost.
    /// </summary>
    public void Reset()
    {
        lock (_gate)
        {
            _status = null;
            _lastStatusAsk = DateTime.MinValue;
            _handshakeSeenAt = null;
            _ops.Clear();
            var keys = new List<string>(_latest.Keys);
            foreach (string key in keys)
            {
                var p = _latest[key];
                // REMOTEFOLLOW1: a refusal the server would repeat hides Install; the next connection
                // (perhaps to a server that can fetch it) starts without it.
                if (p.IsTerminal && InstallStatusLabels.WouldRepeat(p.Reason))
                {
                    _latest.Remove(key);
                    continue;
                }

                if (p.IsTerminal) continue;
                _latest[key] = new RemoteInstallProgress(p.Nonce, p.Op, RemoteInstallStage.Failed, RemoteInstallReason.Unavailable,
                    p.Package, p.Version, "the connection ended before the server answered");
            }

            _lastHeard.Clear();
        }

        Notify();
    }

    /// <summary>Every package's latest step (a copy).</summary>
    public IReadOnlyList<KeyValuePair<string, RemoteInstallProgress>> AllLatest()
    {
        lock (_gate) return new List<KeyValuePair<string, RemoteInstallProgress>>(_latest);
    }

    internal void ResetForTests()
    {
        lock (_gate)
        {
            _status = null;
            _lastStatusAsk = DateTime.MinValue;
            _handshakeSeenAt = null;
            _ops.Clear();
            _latest.Clear();
            _lastHeard.Clear();
            _steamId = 0;
        }

        Send = null;
        Changed = null;
        SteamIdFile = null;
        Now = () => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
    }

    private int NextNonce()
    {
        lock (_gate) return NextNonceLocked();
    }

    private int NextNonceLocked()
    {
        _nextNonce++;
        if (_nextNonce == 0) _nextNonce = 1;
        return _nextNonce;
    }
}
