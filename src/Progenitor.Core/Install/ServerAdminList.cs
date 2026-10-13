using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Progenitor.Install;

/// <summary>REMOTEINST1: why a requester may install remotely. One byte on the wire.</summary>
internal enum RemoteAuthorizedBy : byte
{
    None = 0,

    /// <summary>Their SteamID is in the server's own <c>adminlist.txt</c> (no <c>adminauth</c> needed).</summary>
    AdminList = 1,

    /// <summary>They are not on the list file this server reads, but ran <c>adminauth</c> (<c>User.IsAdmin</c>).</summary>
    AdminAuth = 2,

    /// <summary>
    /// The player hosting this private game (the game's own <c>-owner</c> argument on a
    /// <c>-hostServer</c> process; decided 2026-10-06). Progenitor's authorization only: the
    /// game's <c>User.IsAdmin</c> is never set. Sent as <see cref="AdminAuth"/> on the wire so
    /// clients that only know the first two values still accept the status.
    /// </summary>
    Host = 3,
}

/// <summary>The answer one membership read gives.</summary>
internal enum AdminListRead
{
    NotMember = 0,
    Member = 1,

    /// <summary>The file could not be found or read; the caller falls back to <c>User.IsAdmin</c> alone.</summary>
    Unreadable = 2,
}

/// <summary>
/// REMOTEINST1 (decided 2026-10-01): remote installs are authorized by membership in the
/// server's own admin list — the same file the game loads, which only someone with file access to
/// the server can edit — not by having run <c>adminauth</c> this session.
///
/// <para><b>Which file.</b> The game's <c>ProjectM.FileUserList</c> (owned by
/// <c>AdminAuthSystem</c>) carries a default file and a local file; read-only dump research places
/// the default under the install (<c>VRisingServer_Data/StreamingAssets/Settings/adminlist.txt</c>)
/// and the local copy under <c>&lt;persistentDataPath&gt;/Settings/adminlist.txt</c>, which is the one
/// the game saves. So the persistentDataPath file wins when it exists, and the install's default is
/// read only when it does not. No method body was native; a live check should confirm it.</para>
///
/// <para>Plain file IO: one SteamID per line, trimmed; blank lines and anything that is not a ulong
/// are ignored. The file is re-read when its write time or length changes, checked at most once per
/// request, so an edit takes effect without a restart.</para>
/// </summary>
internal sealed class ServerAdminList
{
    public const string FileName = "adminlist.txt";

    private readonly Func<string?> _persistentDataPath;
    private readonly string _installRoot;
    private readonly object _gate = new();
    private string _path = "";
    private long _stampTicks = -1;
    private long _stampLength = -1;
    private HashSet<ulong> _ids = new();
    private bool _loggedUnreadable;

    /// <param name="persistentDataPath">The server's own persistentDataPath (command line first, then the game's value).</param>
    /// <param name="installRoot">The server's install folder (the parent of <c>BepInEx</c>).</param>
    public ServerAdminList(Func<string?> persistentDataPath, string installRoot)
    {
        _persistentDataPath = persistentDataPath ?? (() => null);
        _installRoot = installRoot ?? "";
    }

    /// <summary>Told once when the list cannot be read (the fallback to IsAdmin alone begins).</summary>
    internal Action<string>? LogOnce { get; set; }

    /// <summary>The candidates in the order the game prefers them.</summary>
    public IReadOnlyList<string> Candidates()
    {
        var list = new List<string>(2);
        string? pdp = null;
        try { pdp = _persistentDataPath(); }
        catch { pdp = null; }
        if (!string.IsNullOrWhiteSpace(pdp)) list.Add(Path.Combine(pdp!, "Settings", FileName));
        if (!string.IsNullOrWhiteSpace(_installRoot))
            list.Add(Path.Combine(_installRoot, "VRisingServer_Data", "StreamingAssets", "Settings", FileName));
        return list;
    }

    /// <summary>The file read last ("" when none could be found).</summary>
    public string CurrentPath
    {
        get { lock (_gate) return _path; }
    }

    /// <summary>Is <paramref name="steamId"/> on the list? Re-reads the file when it changed.</summary>
    public AdminListRead Check(ulong steamId)
    {
        if (!Refresh()) return AdminListRead.Unreadable;
        lock (_gate) return _ids.Contains(steamId) ? AdminListRead.Member : AdminListRead.NotMember;
    }

    private bool Refresh()
    {
        string? path = null;
        foreach (string candidate in Candidates())
        {
            try
            {
                if (File.Exists(candidate)) { path = candidate; break; }
            }
            catch { /* the next candidate */ }
        }

        if (path == null) return Unreadable("no adminlist.txt at " + string.Join(" or ", Candidates()));
        try
        {
            var info = new FileInfo(path);
            long ticks = info.LastWriteTimeUtc.Ticks;
            long length = info.Length;
            lock (_gate)
            {
                if (string.Equals(_path, path, StringComparison.OrdinalIgnoreCase) && _stampTicks == ticks && _stampLength == length)
                    return true;
            }

            var ids = Parse(File.ReadAllText(path));
            lock (_gate)
            {
                _path = path;
                _stampTicks = ticks;
                _stampLength = length;
                _ids = ids;
                _loggedUnreadable = false;
            }

            return true;
        }
        catch (Exception e)
        {
            return Unreadable("reading " + path + " failed: " + e.Message);
        }
    }

    private bool Unreadable(string why)
    {
        bool log;
        lock (_gate)
        {
            _path = "";
            _stampTicks = -1;
            _stampLength = -1;
            _ids = new HashSet<ulong>();
            log = !_loggedUnreadable;
            _loggedUnreadable = true;
        }

        if (log)
        {
            try { LogOnce?.Invoke("RemoteInstall: the admin list cannot be read (" + why + "); only players who ran adminauth are authorized."); }
            catch { /* logging never decides */ }
        }

        return false;
    }

    /// <summary>One SteamID per line, trimmed; blank lines and anything that is not a ulong are ignored.</summary>
    public static HashSet<ulong> Parse(string? text)
    {
        var ids = new HashSet<ulong>();
        foreach (string raw in (text ?? "").Split('\n'))
        {
            string line = raw.Trim().TrimStart('﻿');
            if (line.Length == 0) continue;
            if (ulong.TryParse(line, NumberStyles.None, CultureInfo.InvariantCulture, out ulong id) && id != 0) ids.Add(id);
        }

        return ids;
    }

    /// <summary>
    /// The authorization rule: on the list, or <c>User.IsAdmin</c>. An unreadable list leaves
    /// <c>IsAdmin</c> as the only way in.
    /// </summary>
    /// <summary>
    /// The server's one admin-list reader, set by <c>RemoteInstallEvents</c> on a server and cleared
    /// on its uninstall. Every admin-gated server path reads it — remote installs and administrator
    /// edits of server settings (docs/settings-scopes.md, decided 2026-10-03) — so both
    /// authorize the same people. Null on a client, or before the install module ran: then only
    /// <c>User.IsAdmin</c> authorizes.
    /// </summary>
    internal static ServerAdminList? Shared { get; set; }

    /// <summary>
    /// <see cref="Authorize(AdminListRead, bool, ulong)"/> over <see cref="Shared"/>: membership first, then
    /// <paramref name="isAdmin"/>, then the host of this private game (<see cref="IsHost"/>). A missing or unreadable list (or one that throws) falls back to
    /// <paramref name="isAdmin"/> alone. SteamID 0 is never a member.
    /// </summary>
    public static RemoteAuthorizedBy AuthorizeShared(ulong steamId, bool isAdmin)
    {
        var membership = AdminListRead.Unreadable;
        try
        {
            var list = Shared;
            if (list != null) membership = steamId == 0 ? AdminListRead.NotMember : list.Check(steamId);
        }
        catch { membership = AdminListRead.Unreadable; }
        return Authorize(membership, isAdmin, steamId);
    }

    /// <summary>The audit and readback word for an authorization: <c>adminlist</c>, <c>adminauth</c>, <c>host</c> or <c>none</c>.</summary>
    public static string Word(RemoteAuthorizedBy by) => by switch
    {
        RemoteAuthorizedBy.AdminList => "adminlist",
        RemoteAuthorizedBy.AdminAuth => "adminauth",
        RemoteAuthorizedBy.Host => "host",
        _ => "none",
    };

    /// <summary>
    /// The rule without a host: on the list, else <paramref name="isAdmin"/>. Callers that hold a
    /// SteamID use <see cref="Authorize(AdminListRead, bool, ulong)"/> so the private-game host counts.
    /// </summary>
    public static RemoteAuthorizedBy Authorize(AdminListRead membership, bool isAdmin) =>
        membership == AdminListRead.Member ? RemoteAuthorizedBy.AdminList
        : isAdmin ? RemoteAuthorizedBy.AdminAuth
        : RemoteAuthorizedBy.None;

    /// <summary>On the list, else <paramref name="isAdmin"/>, else the host of this private game.</summary>
    public static RemoteAuthorizedBy Authorize(AdminListRead membership, bool isAdmin, ulong steamId)
    {
        var by = Authorize(membership, isAdmin);
        return by == RemoteAuthorizedBy.None && IsHost(steamId) ? RemoteAuthorizedBy.Host : by;
    }

    // ---- the host of a private game (decided 2026-10-06) --------------------------------

    /// <summary>The argument the game passes its hosted server with the host's SteamID.</summary>
    public const string OwnerArg = "-owner";

    /// <summary>The argument the game passes a server it starts for a private game.</summary>
    public const string HostServerArg = "-hostServer";

    /// <summary>Tests replace the process command line; null reads <see cref="Environment.CommandLine"/>.</summary>
    internal static Func<string?>? CommandLineProbe { get; set; }

    private static readonly object HostGate = new();
    private static bool _hostRead;
    private static ulong _host;

    /// <summary>
    /// The SteamID of the player hosting this private game, or 0. When a client hosts a private
    /// game, the game itself starts <c>VRisingServer.exe</c> with <c>-owner &lt;SteamID&gt; -hostServer</c>
    /// (the argument template is in the game's own launcher; a hosted child's command line was
    /// observed live). Both must be present. A dedicated server is not started with
    /// <c>-hostServer</c>, so this is 0 there. Read once per process: the command line cannot change.
    /// </summary>
    public static ulong HostSteamId
    {
        get
        {
            lock (HostGate)
            {
                if (_hostRead) return _host;
                string? line = null;
                try { line = CommandLineProbe != null ? CommandLineProbe() : Environment.CommandLine; }
                catch { line = null; }
                _host = HostFromCommandLine(line);
                _hostRead = true;
                return _host;
            }
        }
    }

    /// <summary>Forget the cached host (tests that change <see cref="CommandLineProbe"/>).</summary>
    internal static void ResetHostForTests()
    {
        lock (HostGate)
        {
            _hostRead = false;
            _host = 0;
        }
    }

    /// <summary>Is <paramref name="steamId"/> the host of this private game? SteamID 0 never is.</summary>
    public static bool IsHost(ulong steamId) => steamId != 0 && steamId == HostSteamId;

    /// <summary>
    /// The gate for Progenitor features that follow the game's admin flag (admin-only commands, photo pause):
    /// <c>User.IsAdmin</c>, or the host of this private game. Stopping the server (the stop channel and <c>.stop-server</c>)
    /// and every <c>.debug</c> form use <see cref="AuthorizeShared"/> instead, the remote-install rule that also accepts the admin list.
    /// </summary>
    public static bool IsProgenitorAdmin(ulong steamId, bool isAdmin) => isAdmin || IsHost(steamId);

    /// <summary>
    /// <c>-owner &lt;ulong&gt;</c> from a command line that also carries <c>-hostServer</c>; else 0.
    /// Argument names match case-insensitively as whole tokens; quotes around the value are allowed.
    /// </summary>
    public static ulong HostFromCommandLine(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return 0;
        var tokens = Tokens(commandLine!);
        bool hostServer = false;
        ulong owner = 0;
        for (int i = 0; i < tokens.Count; i++)
        {
            if (string.Equals(tokens[i], HostServerArg, StringComparison.OrdinalIgnoreCase)) hostServer = true;
            else if (string.Equals(tokens[i], OwnerArg, StringComparison.OrdinalIgnoreCase) && i + 1 < tokens.Count
                && ulong.TryParse(tokens[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out ulong id))
                owner = id;
        }

        return hostServer ? owner : 0;
    }

    /// <summary>Whitespace-separated tokens; a double-quoted run is one token without its quotes.</summary>
    private static List<string> Tokens(string line)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        bool quoted = false, any = false;
        foreach (char c in line)
        {
            if (c == '"') { quoted = !quoted; any = true; continue; }
            if (!quoted && char.IsWhiteSpace(c))
            {
                if (any) tokens.Add(current.ToString());
                current.Clear();
                any = false;
                continue;
            }

            current.Append(c);
            any = true;
        }

        if (any) tokens.Add(current.ToString());
        return tokens;
    }
}
