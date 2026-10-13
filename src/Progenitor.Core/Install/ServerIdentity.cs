using System;
using System.IO;
using System.Text.Json;

namespace Progenitor.Install;

/// <summary>
/// CONFIRMTARGET1: which server an install, update or uninstall touches, in the words the
/// confirmation, the progress line and the pending list use. Pure text over plain facts.
///
/// <para>Local route: the folder's <c>ServerHostSettings.json</c> <c>"Name"</c> (found the way the
/// port is, launch record's <c>-persistentDataPath</c> first) plus the folder. Remote route: a name
/// when one is known, else the joined <c>address:port</c>. The remote-install wire carries no
/// server name, so today a remote server is named by its address.</para>
/// </summary>
internal sealed class ServerIdentity
{
    /// <summary>The longest name a confirmation title carries before it is cut in the middle.</summary>
    public const int TitleNameMax = 32;

    /// <summary>The longest folder a body line carries before it is cut in the middle.</summary>
    public const int FolderMax = 60;

    public const string LocalFallback = "the local server";
    public const string RemoteFallback = "the connected server";
    public const string ThisGame = "this game";

    private ServerIdentity(bool remote, string name, string folder, string address)
    {
        Remote = remote;
        Name = (name ?? "").Trim();
        Folder = (folder ?? "").Trim();
        Address = (address ?? "").Trim();
    }

    public static ServerIdentity Local(string? name, string? folder) => new(false, name ?? "", folder ?? "", "");

    public static ServerIdentity Connected(string? name, string? address) => new(true, name ?? "", "", address ?? "");

    /// <summary>The connected server's identity from Bootstrap's join record (<c>address:port</c>).</summary>
    public static ServerIdentity Connected(string? name, string? address, int port)
    {
        string a = (address ?? "").Trim();
        return Connected(name, a.Length == 0 ? "" : port > 0 ? a + ":" + port : a);
    }

    /// <summary>Remote route (true) or local folder (false).</summary>
    public bool Remote { get; }

    /// <summary>The server's own name; empty when unknown.</summary>
    public string Name { get; }

    /// <summary>The local server's folder (empty on the remote route).</summary>
    public string Folder { get; }

    /// <summary>The connected server's <c>address:port</c> (empty on the local route or when not recorded).</summary>
    public string Address { get; }

    public bool HasName => Name.Length > 0;

    /// <summary>
    /// The name a title and a restart sentence use: the server's name, else the address of a
    /// connected server, else <see cref="LocalFallback"/> / <see cref="RemoteFallback"/>.
    /// </summary>
    public string ShortName =>
        HasName ? Name
        : Remote ? (Address.Length > 0 ? Address : RemoteFallback)
        : LocalFallback;

    /// <summary><see cref="ShortName"/> cut in the middle for a title.</summary>
    public string TitleName => MiddleEllipsis(ShortName, TitleNameMax);

    /// <summary>
    /// <c>your local server Progenitor Forms Test (C:\…\server)</c> /
    /// <c>the connected server Name (1.2.3.4:9876)</c>: the object of "on …".
    /// </summary>
    public string Phrase
    {
        get
        {
            if (Remote)
            {
                string where = Address.Length > 0 ? Address : "";
                if (HasName) return RemoteFallback + " " + Name + (where.Length > 0 ? " (" + where + ")" : "");
                return where.Length > 0 ? RemoteFallback + " " + where : RemoteFallback;
            }

            string folder = MiddleEllipsis(Folder, FolderMax);
            string head = "your local server" + (HasName ? " " + Name : "");
            return folder.Length > 0 ? head + " (" + folder + ")" : head;
        }
    }

    /// <summary>
    /// The confirmation body's first line: <c>Server: Progenitor Forms Test — local folder C:\…\server</c>,
    /// <c>Server: Name — connected at 1.2.3.4:9876</c>; an unknown name leaves the location alone.
    /// </summary>
    public string ServerLine
    {
        get
        {
            string where = Remote
                ? (Address.Length > 0 ? "connected at " + Address : RemoteFallback)
                : (Folder.Length > 0 ? "local folder " + MiddleEllipsis(Folder, FolderMax) : LocalFallback);
            return "Server: " + (HasName ? Name + " — " + where : where);
        }
    }

    /// <summary>The first line of a Client-and-server confirmation: <c>On this game and on your local server … .</c></summary>
    public string BothLine => "On " + ThisGame + " and on " + Phrase + ".";

    /// <summary>The pending list's caption for a server-side change: <c>Pending restart of Name</c>.</summary>
    public string PendingRestartOf => "Pending restart of " + ShortName;

    /// <summary>A long name or path for one line: the head and the tail kept, the middle elided.</summary>
    public static string MiddleEllipsis(string? text, int max)
    {
        string p = (text ?? "").Trim();
        if (p.Length <= max || max < 9) return p;
        int head = (max - 1) / 2;
        int tail = max - 1 - head;
        return p.Substring(0, head) + "…" + p.Substring(p.Length - tail);
    }

    /// <summary>
    /// The <c>"Name"</c> in the folder's <c>ServerHostSettings.json</c>, searched like
    /// <see cref="ServerProcessControl.HostPort"/>; empty when no readable file names one.
    /// </summary>
    public static string HostName(string? serverRoot)
    {
        if (string.IsNullOrWhiteSpace(serverRoot)) return "";
        // Live (dda963fb): -serverName on the command line overrides the settings file's Name, and
        // the server advertises the command-line one. The running process's own command line first,
        // then the launch record's.
        string fromCommandLine = CommandLineServerName(serverRoot!);
        if (fromCommandLine.Length > 0) return fromCommandLine;
        foreach (string candidate in ServerProcessControl.HostSettingsCandidates(serverRoot!))
        {
            if (!File.Exists(candidate)) continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllBytes(candidate));
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                if (root.TryGetProperty("Name", out var n) && n.ValueKind == JsonValueKind.String)
                {
                    string name = (n.GetString() ?? "").Trim();
                    if (name.Length > 0) return name;
                }
            }
            catch { /* an unreadable settings file is "no name", never a failure */ }
        }

        return "";
    }

    /// <summary>
    /// The <c>-serverName</c> value the folder's server runs (or last ran) with: the running
    /// process's command line, then the launch record's arguments. Empty when neither names one.
    /// </summary>
    public static string CommandLineServerName(string? serverRoot)
    {
        if (string.IsNullOrWhiteSpace(serverRoot)) return "";
        try
        {
            var live = ServerProcessControl.Running(serverRoot);
            if (live != null && !string.IsNullOrWhiteSpace(live.CommandLine))
            {
                string name = ArgValue(ServerLaunchRecord.SplitCommandLine(live.CommandLine), "-serverName");
                if (name.Length > 0) return name;
            }
        }
        catch { /* the process read is best effort; the launch record follows */ }

        try
        {
            var record = ServerLaunchRecord.TryRead(serverRoot!);
            if (record != null) return ArgValue(record.Args, "-serverName");
        }
        catch { /* an unreadable record is "no name" */ }

        return "";
    }

    /// <summary>The value after <paramref name="flag"/> (case-insensitive), trimmed; empty when absent.</summary>
    internal static string ArgValue(System.Collections.Generic.IReadOnlyList<string>? args, string flag)
    {
        if (args == null) return "";
        for (int i = 0; i + 1 < args.Count; i++)
        {
            if (!string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase)) continue;
            string value = (args[i + 1] ?? "").Trim().Trim('"').Trim();
            if (value.Length > 0 && !value.StartsWith("-", StringComparison.Ordinal)) return value;
        }

        return "";
    }
}
