using System;
using System.IO;

namespace Progenitor.Networking;

/// <summary>
/// SERVEREMPTY1 (docs/mods-menu.md, "The Server tab"): the last server this client joined, for the Server tab's
/// <b>Last server</b> row, and its <b>Join</b>, which connects the way Join now does.
/// </summary>
internal static partial class JoinFlowClient
{
    /// <summary>How often the join is looked at for the record (seconds) until it was written for this world.</summary>
    internal const double LastServerCheckSeconds = 1.0;

    /// <summary>Once written, how often a later name (the catalog's) is looked for (seconds).</summary>
    internal const double LastServerRecheckSeconds = 10.0;

    private static double _lastServerCheckedAt = -10;
    private static string _lastServerNoted = "";
    private static bool _joinedWithoutProgenitor;

    /// <summary>
    /// The cheap inputs of the last full look (<see cref="LastServerInputsKey"/>), or empty. While they are unchanged
    /// the full look is skipped: it names the server through the Mods menu's server picker, which scans the Steam
    /// libraries and reads settings files (live 2026-10-06: <c>JoinFlowPanel.OnFrame</c> took 34-211 ms every 10 s,
    /// longest every 30 s when the picker's discovery cache ran out, in a world with no join screen open).
    /// </summary>
    private static string _lastServerInputs = "";

    /// <summary>Tests: how many full looks (the expensive name lookup) <see cref="TickLastServer"/> made.</summary>
    internal static int LastServerFullLooks { get; private set; }

    /// <summary>
    /// The connected server's key as the game itself names it (live: <c>ModManifest.ReadLiveConnectHost</c>). Bootstrap's
    /// dialed address is never cleared, so a join that did not pass through <c>LaunchGame</c> would otherwise be recorded
    /// as the previous server: when this names another <c>address:port</c>, nothing is written.
    /// </summary>
    internal static Func<string>? LiveHostProbe { get; set; }

    /// <summary>Tests only: replaces the file at <see cref="LastServerPath"/>.</summary>
    internal static Func<LastServerRecord?>? LastServerReaderForTests { get; set; }

    /// <summary><c>BepInEx/config/progenitor-last-server.json</c>.</summary>
    internal static string LastServerPath => Path.Combine(ModManifest.ConfigDirectory, LastServerStore.FileName);

    /// <summary>The last server on disk, or null.</summary>
    internal static LastServerRecord? ReadLastServer()
    {
        var probe = LastServerReaderForTests;
        if (probe != null)
        {
            try { return probe(); }
            catch { return null; }
        }

        return LastServerStore.Read(LastServerPath);
    }

    /// <summary>
    /// A join is under way: Join now's reconnect (or the after-restart rejoin) has not ended, or a client world exists
    /// while the tab reads not connected (the game is connecting or loading).
    /// </summary>
    internal static bool JoinInProgress
    {
        get
        {
            JoinReconnect? r;
            lock (Gate) r = _reconnect;
            if (r != null && !r.Finished) return true;
            try { return ConnectHost?.WorldExists == true; }
            catch { return false; }
        }
    }

    /// <summary>The server without Progenitor that this client is in was detected (<c>ServerModPause</c>): a successful join too.</summary>
    internal static void NoteJoinedWithoutProgenitor() => _joinedWithoutProgenitor = true;

    /// <summary>This client is in a world on a server found to have no Progenitor (cleared when the world goes).</summary>
    internal static bool JoinedWithoutProgenitor => _joinedWithoutProgenitor;

    /// <summary>
    /// Once a second (every ten once written) from <see cref="Tick"/>: in a joined world (the handshake said ok, or a server without Progenitor was
    /// detected), write the last-server record when the server, its name or its password use changed. Leaving clears the
    /// note, so the next join is written again.
    /// </summary>
    private static void TickLastServer(double now)
    {
        var host = ConnectHost;
        if (host == null) return;
        // The world is read every frame (one field read), so leaving is never missed between two checks.
        bool world;
        try { world = host.WorldExists; }
        catch { world = false; }
        if (!world)
        {
            _lastServerNoted = "";
            _lastServerInputs = "";
            _joinedWithoutProgenitor = false;
            return;
        }

        double every = _lastServerNoted.Length > 0 ? LastServerRecheckSeconds : LastServerCheckSeconds;
        if (now - _lastServerCheckedAt < every && now >= _lastServerCheckedAt) return;
        _lastServerCheckedAt = now;

        bool joined;
        try { joined = host.JoinedOk || _joinedWithoutProgenitor; }
        catch { joined = false; }
        if (!joined) return;
        // Everything the record and the game check depend on, except the folder-derived server name, is cheap to
        // read; only a change in it repeats the full look. The catalog's name (_serverName) is one of the inputs, so
        // a name that arrives later is still written.
        string inputs;
        lock (Gate) inputs = LastServerInputsLocked();
        if (string.Equals(inputs, _lastServerInputs, StringComparison.Ordinal)) return;
        _lastServerInputs = inputs;
        LastServerFullLooks++;
        JoinTarget target;
        lock (Gate) target = CurrentTargetLocked();
        var record = LastServerRecord.From(target, DateTime.UtcNow);
        if (record == null) return;
        string key = record.Endpoint + "|" + record.Protocol + "|" + record.ServerName + "|" + record.NeedsPassword;
        if (string.Equals(key, _lastServerNoted, StringComparison.Ordinal)) return;
        _lastServerNoted = key;
        if (!LastServerMatchesGame(record, out string gameHost))
        {
            Plugin.Logger?.LogInfo("JoinFlow: last server not written: the game is connected to " + gameHost + ", not the dialed " + record.Endpoint);
            return;
        }

        try
        {
            if (record.SameAs(LastServerStore.Read(LastServerPath))) return;
            LastServerStore.Write(LastServerPath, record);
            Plugin.Logger?.LogInfo("JoinFlow: last server " + record.DisplayName + " (" + record.Endpoint + ", " + record.Protocol
                + (record.NeedsPassword ? ", password" : "") + ")");
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("JoinFlow: writing the last server threw: " + e.Message); }
    }

    // Called with Gate held: the cheap inputs of the last-server record (no name lookup).
    private static string LastServerInputsLocked()
    {
        JoinTarget probe;
        try { probe = TargetProbe?.Invoke() ?? new JoinTarget(); }
        catch { probe = new JoinTarget(); }
        CapturePasswordLocked();
        string gameHost;
        try { gameHost = LiveHostProbe?.Invoke() ?? ""; }
        catch { gameHost = ""; }
        return LastServerInputsKey(probe, _joinDialed, _serverName, _host, _fp, _passwordUsed, gameHost);
    }

    /// <summary>
    /// The inputs <see cref="TickLastServer"/> compares between looks: Bootstrap's connect record, the address this
    /// join dialed, the catalog's server name, host and fingerprint, the password flag, and the game's own connect host.
    /// </summary>
    internal static string LastServerInputsKey(JoinTarget? probe, JoinTarget? dialed, string catalogName, string host, string fingerprint,
        bool password, string gameHost)
    {
        static string Of(JoinTarget? t) => t == null ? "-" : t.Address + ":" + t.Port + "/" + t.Protocol + (t.HostedByThisClient ? "/hosted" : "")
            + "/" + t.ServerName + "/" + t.Fingerprint + (t.NeedsPassword ? "/pw" : "");
        return Of(probe) + "|" + Of(dialed) + "|" + (catalogName ?? "") + "|" + (host ?? "") + "|" + (fingerprint ?? "")
            + "|" + (password ? "pw" : "") + "|" + (gameHost ?? "");
    }

    /// <summary>True unless the game names an <c>address:port</c> for this connection that is not the record's.</summary>
    internal static bool LastServerMatchesGame(LastServerRecord record, out string gameHost)
    {
        gameHost = "";
        try { gameHost = (LiveHostProbe?.Invoke() ?? "").Trim(); }
        catch { gameHost = ""; }
        if (gameHost.IndexOf(':') < 0 || !ConnectHostKey.IsStable(gameHost)) return true;
        return string.Equals(gameHost, ConnectHostKey.Endpoint(record.Address, record.Port), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// JOINPW1: the server refused the Server tab Join's connect for its password: the record's hint becomes true (only for
    /// the record of that same server). A successful join rewrites the hint from the join itself (<see cref="TickLastServer"/>).
    /// </summary>
    internal static void NoteLastServerPasswordRefused(JoinTarget target)
    {
        try
        {
            var record = ReadLastServer();
            if (record == null || !record.Names(target) || record.NeedsPassword) return;
            LastServerStore.Write(LastServerPath, record.WithPassword(true));
            Plugin.Logger?.LogInfo("JoinFlow: last server " + record.Endpoint + " refused the join without a password; noted (the game's prompt asks for it)");
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("JoinFlow: noting the last server's password refusal threw: " + e.Message); }
    }

    /// <summary>
    /// The Server tab's Last server <b>Join</b>: connect to <paramref name="record"/> through Join now's reconnect (the
    /// <b>Joining</b> page; the game's Leave or the title view first, then <c>LaunchGame</c> with the same address, port and
    /// transport), always without a password first. Only when the server refuses with <c>IncorrectPassword</c> does the
    /// reconnect return to the title view and open the game's own password prompt for that address (JOINPW1); a prompt the
    /// player cancels just ends there. Nothing is stored. Returns <c>joining</c>, or why it did not start.
    /// </summary>
    internal static string JoinLastServer(LastServerRecord? record)
    {
        if (record == null) return "no last server";
        var target = record.ToTarget();
        if (!target.CanReconnect) return "no address to reconnect to";
        if (ConnectHost == null) return "reconnecting is not available here";
        bool hosting;
        try { hosting = TargetProbe?.Invoke()?.HostedByThisClient == true; }
        catch { hosting = false; }
        if (hosting) return "a private game this client hosts is running";
        if (JoinInProgress) return "a join is already in progress";
        Plugin.Logger?.LogInfo("JoinFlow: Server tab Join: " + record.DisplayName + " (" + record.Endpoint + ")");
        OpenReconnect(target, afterRestart: false, record: null);
        return "joining";
    }
}
