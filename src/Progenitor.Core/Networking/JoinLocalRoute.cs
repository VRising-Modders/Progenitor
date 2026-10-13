using System;
using System.Collections.Generic;
using System.Globalization;

namespace Progenitor.Networking;

/// <summary>LOCALREJOIN1: what the rejoin knows about the server it is about to reconnect to and this PC's own server.</summary>
internal sealed class JoinLocalFacts
{
    /// <summary>The target's address is this machine (<c>Install.LocalAddress.IsThisMachine</c>: loopback or an interface address).</summary>
    public bool AddressIsThisMachine { get; init; }

    /// <summary>The target's address is already a loopback address.</summary>
    public bool AddressIsLoopback { get; init; }

    /// <summary><c>[Mods] DedicatedServerPath</c> names a validated server folder.</summary>
    public bool FolderValid { get; init; }

    /// <summary>That folder's game port (<c>ServerHostSettings.json</c>), or 0.</summary>
    public int FolderPort { get; init; }

    /// <summary>That folder's server process is running on this PC (<c>ServerProcessControl.Running</c>).</summary>
    public bool FolderServerRunning { get; init; }

    /// <summary>That folder's server name (<c>ServerIdentity.HostName</c>), or empty.</summary>
    public string FolderServerName { get; init; } = "";

    /// <summary>That folder's server operator key fingerprint (its <c>BepInEx/config/progenitor-operator.pub</c>), or empty.</summary>
    public string FolderOperatorFingerprint { get; init; } = "";
}

/// <summary>
/// LOCALREJOIN1 (live 2026-10-07): Join now / the after-restart rejoin reconnected to the address the original join used.
/// When that join came from the game's Continue it is the server list's public address, so reaching a server on this
/// PC depended on the router's NAT loopback, and the reconnect timed out. When the target is this PC's own server, the
/// reconnect goes to <c>127.0.0.1</c> on the same port instead, and falls back to the original address if that fails.
///
/// <para>"This PC's own server" is the server the Install tab's route already treats as local: the configured
/// <c>[Mods] DedicatedServerPath</c> folder's server, on the folder's own port. The route also requires the joined
/// address to be this machine; a public address is not, so for it the rejoin requires instead that the folder's server
/// is running on this PC and carries the target's server name. Anything less keeps the original address.</para>
/// </summary>
internal static class JoinLocalRoute
{
    public const string Loopback = "127.0.0.1";

    /// <summary>Whether <paramref name="target"/> is this PC's own server, with the reason either way (for the log).</summary>
    public static bool IsLocalServer(JoinTarget target, JoinLocalFacts? facts, out string why)
    {
        if (target == null || facts == null) { why = "no facts about this PC's server"; return false; }
        if (facts.AddressIsLoopback) { why = "the target is already a loopback address"; return false; }
        if (!facts.FolderValid) { why = "no validated [Mods] DedicatedServerPath"; return false; }
        if (facts.FolderPort <= 0) { why = "the folder's port is unknown"; return false; }
        if (target.Port != facts.FolderPort)
        {
            why = "port " + target.Port.ToString(CultureInfo.InvariantCulture) + " is not the folder's port "
                + facts.FolderPort.ToString(CultureInfo.InvariantCulture);
            return false;
        }

        // Review: the operator key is the server's identity. A known key that differs is never this PC's server.
        string targetKey = (target.Fingerprint ?? "").Trim();
        string folderKey = (facts.FolderOperatorFingerprint ?? "").Trim();
        if (targetKey.Length > 0 && folderKey.Length > 0 && !string.Equals(targetKey, folderKey, StringComparison.OrdinalIgnoreCase))
        {
            why = "the target's operator key is not the folder server's";
            return false;
        }

        if (facts.AddressIsThisMachine) { why = "the address is this machine and the port is the folder's"; return true; }
        if (!facts.FolderServerRunning) { why = "the folder's server is not running on this PC"; return false; }
        // A public address: a port and a name could be another server's, so the operator key must match.
        if (targetKey.Length == 0) { why = "a public address, and the target's operator key is unknown"; return false; }
        if (folderKey.Length == 0) { why = "a public address, and the folder server's operator key is unknown"; return false; }
        string a = (facts.FolderServerName ?? "").Trim();
        string b = (target.ServerName ?? "").Trim();
        if (a.Length == 0 || b.Length == 0 || !string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
        {
            why = "the folder's running server is not named like the target";
            return false;
        }

        why = "the folder's server runs on this PC on that port, under the same name and operator key";
        return true;
    }

    /// <summary>
    /// The connect to try first, and the one to fall back to: loopback then the original for this PC's own server,
    /// else the original alone (fallback null). <paramref name="log"/> is the decision as one log line.
    /// </summary>
    public static (JoinTarget Primary, JoinTarget? Fallback) Decide(JoinTarget target, JoinLocalFacts? facts, out string log)
    {
        if (target == null) throw new ArgumentNullException(nameof(target));
        if (!IsLocalServer(target, facts, out string why))
        {
            log = "JoinFlow: reconnect target " + target.Endpoint + " (" + why + ")";
            return (target, null);
        }

        var loop = WithAddress(target, Loopback);
        log = "JoinFlow: reconnect target " + loop.Endpoint + " (local server: " + why + "; was " + target.Endpoint
            + ", which stays the fallback)";
        return (loop, target);
    }

    /// <summary>The fingerprint a server folder's <c>BepInEx/config/progenitor-operator.pub</c> names (<c>fp=</c>), or empty.</summary>
    internal static string ReadOperatorFingerprint(string? serverRoot)
    {
        if (string.IsNullOrWhiteSpace(serverRoot)) return "";
        try
        {
            string path = System.IO.Path.Combine(serverRoot!, "BepInEx", "config", ModManifest.PublicKeyFileName);
            if (!System.IO.File.Exists(path) || new System.IO.FileInfo(path).Length > 4096) return "";
            foreach (string line in System.IO.File.ReadAllLines(path))
            {
                int at = line.IndexOf("fp=", StringComparison.Ordinal);
                if (line.StartsWith("#", StringComparison.Ordinal) && at >= 0) return line.Substring(at + 3).Trim();
            }
        }
        catch { /* an unreadable key file is "unknown" */ }

        return "";
    }

    /// <summary>The same target at another address (every other field copied).</summary>
    internal static JoinTarget WithAddress(JoinTarget t, string address) => new()
    {
        Address = address,
        Port = t.Port,
        Protocol = t.Protocol,
        ServerName = t.ServerName,
        Fingerprint = t.Fingerprint,
        NeedsPassword = t.NeedsPassword,
        HostedByThisClient = t.HostedByThisClient,
        PromptForPassword = t.PromptForPassword,
        PromptOnPasswordRefusal = t.PromptOnPasswordRefusal,
    };
}

/// <summary>
/// LOCALREJOIN1 review: a loopback rejoin keeps the server's identity. Every per-server record (the server profile, the
/// trust pin, the Thunderstore acknowledgments, the last-server record) is keyed by the connect host
/// (<c>ModManifest.ReadLiveConnectHost</c>, HOSTKEY1). After a loopback rejoin that would read <c>127.0.0.1:&lt;port&gt;</c>
/// and give the same server a second identity (and a fresh trust pin). The rejoin records an alias, loopback endpoint →
/// the original endpoint, and the host key reads through it, so the records carry over and nothing is pinned again.
/// </summary>
internal static class ConnectHostAlias
{
    private static readonly object Gate = new();
    private static string _from = "";
    private static string _to = "";

    // Review 2026-10-07 (LOW): the alias belongs to one reconnect. Set arms it for that reconnect's join; the join that
    // consumes it keeps it for its connection; any other join, the end of that connection, or a reconnect that failed
    // clears it, so it never outlives its reconnect.
    private static bool _pendingJoin;

    /// <summary>Read <paramref name="from"/> as <paramref name="to"/> for the next join and its connection (a new one replaces it).</summary>
    public static void Set(string from, string to)
    {
        lock (Gate)
        {
            _from = (from ?? "").Trim();
            _to = (to ?? "").Trim();
            _pendingJoin = _from.Length > 0 && _to.Length > 0;
        }
    }

    public static void Clear() => Set("", "");

    /// <summary>The alias in effect (readback and tests): empty when none.</summary>
    public static string Describe()
    {
        lock (Gate) return _from.Length == 0 ? "" : _from + " -> " + _to + (_pendingJoin ? " (pending)" : "");
    }

    /// <summary>
    /// A join's request arrived (<paramref name="newJoin"/>) or the connection ended. The reconnect's own join takes the
    /// armed alias; anything else clears it. The reconnect leaves the old world first, so a connection end while the alias
    /// is still armed keeps it.
    /// </summary>
    public static void OnJoinBoundary(bool newJoin)
    {
        lock (Gate)
        {
            if (_pendingJoin)
            {
                if (newJoin) _pendingJoin = false;
                return;
            }

            _from = _to = "";
        }
    }

    /// <summary>The host key to record: the alias's target when <paramref name="host"/> is its loopback endpoint.</summary>
    public static string Resolve(string? host)
    {
        string h = (host ?? "").Trim();
        lock (Gate)
            return _from.Length > 0 && _to.Length > 0 && string.Equals(h, _from, StringComparison.OrdinalIgnoreCase) ? _to : h;
    }
}

/// <summary>
/// Review 2026-10-07 (LOW, and the rule after the live freezes): what the configured server folder says about its server
/// (its game port, whether its process runs on this PC, its name, its operator key) costs a process scan, a command-line
/// read and file reads, so it is never computed on the main thread. Each folder's facts are computed on the thread pool,
/// kept, and refreshed when older than <see cref="RefreshSeconds"/> or when a join starts (<see cref="Refresh"/>). The route
/// and the loopback rejoin read the cached facts; until the first arrive they read none, and both take the safe default
/// (not this PC's server: the remote route, the original address).
/// </summary>
internal static class LocalFolderFactsCache
{
    internal const double RefreshSeconds = 30;

    internal sealed class FolderFacts
    {
        public int Port { get; init; }
        public bool Running { get; init; }
        public string Name { get; init; } = "";
        public string OperatorKey { get; init; } = "";
    }

    private sealed class Entry
    {
        public FolderFacts? Value;
        public DateTime At;
        public bool Refreshing;
        public bool Dirty;
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<string, Entry> Entries = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Computes one folder's facts (live: <c>JoinFlowLive.ComputeFolderFacts</c>, off the main thread). Null: none.</summary>
    internal static Func<string, FolderFacts?>? Compute { get; set; }

    /// <summary>Tests: run a refresh inline instead of on the thread pool.</summary>
    internal static Action<Action>? Scheduler { get; set; }

    internal static Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    /// <summary>The cached facts for <paramref name="root"/> (null until computed); starts a refresh when stale. Never blocks.</summary>
    internal static FolderFacts? Get(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) return null;
        FolderFacts? value;
        bool start;
        lock (Gate)
        {
            if (!Entries.TryGetValue(root, out var e)) Entries[root] = e = new Entry { Dirty = true };
            value = e.Value;
            bool stale = e.Dirty || e.Value == null || (Clock() - e.At).TotalSeconds >= RefreshSeconds;
            start = stale && !e.Refreshing && Compute != null;
            if (start) e.Refreshing = true;
        }

        if (start) Start(root);
        return value;
    }

    /// <summary>A join started (or the folder changed): the next read refreshes <paramref name="root"/> now, in the background.</summary>
    internal static void Refresh(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) return;
        lock (Gate)
        {
            if (!Entries.TryGetValue(root, out var e)) Entries[root] = e = new Entry();
            e.Dirty = true;
        }

        Get(root);
    }

    private static void Start(string root)
    {
        void Run()
        {
            FolderFacts? facts = null;
            try { facts = Compute?.Invoke(root); }
            catch { facts = null; }
            lock (Gate)
            {
                if (!Entries.TryGetValue(root, out var e)) Entries[root] = e = new Entry();
                e.Value = facts;
                e.At = Clock();
                e.Refreshing = false;
                e.Dirty = false;
            }
        }

        try
        {
            if (Scheduler != null) Scheduler(Run);
            else System.Threading.Tasks.Task.Run(Run);
        }
        catch
        {
            lock (Gate) if (Entries.TryGetValue(root, out var e)) e.Refreshing = false;
        }
    }

    /// <summary>A join's result arrived: every cached folder refreshes at its next read (a server may have started or stopped).</summary>
    internal static void RefreshAll()
    {
        lock (Gate)
            foreach (var e in Entries.Values) e.Dirty = true;
    }

    internal static void Clear()
    {
        lock (Gate) Entries.Clear();
    }
}
