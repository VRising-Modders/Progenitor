using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using Progenitor.SessionLogs;

namespace Progenitor.Support;

/// <summary>What one support bundle is built from. The game side fills <see cref="GameSummary"/> on the main thread; the rest is files.</summary>
internal sealed class SupportBundleRequest
{
    /// <summary><c>client</c> or <c>server</c>; part of the file name.</summary>
    public string Side { get; set; } = "client";

    /// <summary>This process's BepInEx folder.</summary>
    public string BepInExRoot { get; set; } = "";

    /// <summary>Where the bundle is written; default <c>BepInEx/progenitor-support</c>.</summary>
    public string? OutputFolder { get; set; }

    /// <summary>The Unity player log folder (<c>%USERPROFILE%\AppData\LocalLow\Stunlock Studios\VRising</c> on a client); its <c>Player*.log</c> are collected.</summary>
    public string? UnityLogFolder { get; set; }

    /// <summary>More log files by full path (a dedicated server's <c>-logFile</c>), stored under <c>unity/</c>.</summary>
    public List<string> ExtraLogFiles { get; } = new();

    /// <summary>The BepInEx folder of the private game this client hosts (or last hosted), stored under <c>hosted-server/</c>.</summary>
    public string? HostedBepInExRoot { get; set; }

    /// <summary>Lines about the running game and framework (versions, plugins, problems), already formatted.</summary>
    public string GameSummary { get; set; } = "";

    public DateTime NowUtc { get; set; } = DateTime.UtcNow;
    public DateTime NowLocal { get; set; } = DateTime.Now;

    public string? UserProfile { get; set; }
    public string? UserName { get; set; }
    public string? MachineName { get; set; }

    /// <summary>The most of one log kept (its end); halved while the zip comes out larger than <see cref="MaxZipBytes"/>.</summary>
    public long PerFileCap { get; set; } = SupportBundle.DefaultPerFileCap;

    /// <summary>The largest zip written. The V Rising Mod Community Discord accepts attachments up to 50 MB; 45 MB leaves headroom.</summary>
    public long MaxZipBytes { get; set; } = SupportBundle.DefaultMaxZipBytes;

    /// <summary>
    /// How many bundles <c>BepInEx/progenitor-support</c> keeps. Another output folder (the Desktop, a folder the player
    /// chose) is never pruned.
    /// </summary>
    public int Keep { get; set; } = SupportBundle.DefaultKeep;

    /// <summary>How the status line names <see cref="OutputFolder"/> (<c>your Desktop</c>); null: <c>BepInEx/progenitor-support</c>.</summary>
    public string? OutputLabel { get; set; }

    /// <summary>Create <see cref="OutputFolder"/> when it is missing. False for the Desktop: a missing Desktop is a fallback.</summary>
    public bool CreateOutputFolder { get; set; } = true;

    /// <summary>Why the chosen output folder is already known to be unusable (no Desktop folder); the bundle then goes to <c>BepInEx/progenitor-support</c>.</summary>
    public string? OutputProblem { get; set; }

    /// <summary>Local dedicated servers whose logs are collected under <c>local-server-&lt;n&gt;/</c> (at most <see cref="SupportBundle.MaxLocalServers"/>).</summary>
    public List<LocalServerSource> LocalServers { get; } = new();

    /// <summary>
    /// Runs first on the worker thread, before anything is planned: the live half finds the local dedicated servers here,
    /// so process enumeration never runs on the main thread. A throw is recorded as a note, not a failure.
    /// </summary>
    public Action<SupportBundleRequest>? Prepare { get; set; }

    /// <summary>Lines for summary.txt the preparation adds (a server whose folder could not be read).</summary>
    public List<string> Notes { get; } = new();

    /// <summary>The server's own bundle (an admin's Include this server's logs), merged under <c>server/</c>; null: none.</summary>
    public byte[]? ServerZip { get; set; }

    /// <summary>The SHA-256 the server announced for <see cref="ServerZip"/>; checked by <see cref="SupportBundle.Build"/> on the worker thread.</summary>
    public byte[]? ServerZipSha256 { get; set; }

    /// <summary>
    /// Player names the game knows (the client's and other connected players' character names; on a server every user's,
    /// offline ones included), read on the main thread. Each is replaced everywhere as <c>[player-N]</c>, with the names
    /// learned from the logs themselves (<see cref="SupportRedactor.LearnNames"/>).
    /// </summary>
    public List<string> KnownNames { get; } = new();

    /// <summary>The loaded plugins as <see cref="SupportDiagnosis"/> matches them (names, assemblies, namespaces), read on the main thread.</summary>
    public List<DiagnosisPlugin> DiagnosisPlugins { get; } = new();

    /// <summary>Progenitor's current mod problems as (mod GUID, sentence), for the diagnosis.</summary>
    public List<KeyValuePair<string, string>> ModProblems { get; } = new();

    /// <summary>Why the server's logs are missing when they were asked for; summary.txt and the status line say it.</summary>
    public string? ServerPartNote { get; set; }
}

/// <summary>A dedicated server running on this PC, as the bundle collects it. Read only: nothing in its folder is written.</summary>
internal sealed class LocalServerSource
{
    /// <summary>The server's install folder (holding <c>VRisingServer.exe</c> and <c>BepInEx</c>).</summary>
    public string InstallFolder { get; set; } = "";

    /// <summary>Its <c>-logFile</c>, resolved, when its command line could be read.</summary>
    public string? LogFile { get; set; }

    /// <summary>True when this client is in a world on that server, false when not, null when it cannot be told.</summary>
    public bool? Connected { get; set; }

    /// <summary>The folder's name only (never its path), as summary.txt names the server.</summary>
    public string Name
    {
        get
        {
            try { return System.IO.Path.GetFileName(InstallFolder.TrimEnd('\\', '/')); }
            catch { return "?"; }
        }
    }
}

/// <summary>The outcome of <see cref="SupportBundle.Build"/>.</summary>
internal sealed class SupportBundleResult
{
    public bool Ok { get; set; }
    public string Path { get; set; } = "";
    public string FileName { get; set; } = "";
    public long Bytes { get; set; }
    public string Error { get; set; } = "";
    public List<string> Entries { get; } = new();
    public List<string> Skipped { get; } = new();
    public int SteamIds { get; set; }
    public int Addresses { get; set; }

    /// <summary>Distinct player names replaced as <c>[player-N]</c>.</summary>
    public int Players { get; set; }

    /// <summary>The status line's diagnosis phrase (<c>Likely involved: Foo (from the logs; not proof).</c>), or "".</summary>
    public string LikelyInvolved { get; set; } = "";

    /// <summary>The mod the logs point at most, or "".</summary>
    public string TopSuspect { get; set; } = "";
    public long PerFileCap { get; set; }
    public double Seconds { get; set; }

    /// <summary>The folder the zip was written to.</summary>
    public string Folder { get; set; } = "";

    /// <summary>How the status line names <see cref="Folder"/>.</summary>
    public string FolderLabel { get; set; } = "BepInEx/" + SupportBundle.FolderName;

    /// <summary>True when the chosen folder could not be used and the zip went to <c>BepInEx/progenitor-support</c>.</summary>
    public bool FellBack { get; set; }

    /// <summary>The chosen folder's name and why it could not be used (redacted), when <see cref="FellBack"/>.</summary>
    public string FallbackReason { get; set; } = "";

    /// <summary>True when the server's bundle was merged under <c>server/</c>.</summary>
    public bool ServerPartIncluded { get; set; }

    /// <summary>Why the server's logs are missing, when they were asked for and are.</summary>
    public string ServerPartMissing { get; set; } = "";
}

/// <summary>One file planned into the zip.</summary>
internal readonly struct SupportSource
{
    public SupportSource(string entry, string path, bool isLog)
    {
        Entry = entry;
        Path = path;
        IsLog = isLog;
    }

    /// <summary>Its name inside the zip (forward slashes).</summary>
    public string Entry { get; }

    public string Path { get; }

    /// <summary>A log is tail-cut to the per-file cap; a state file is small and kept whole (up to 1 MB).</summary>
    public bool IsLog { get; }
}

/// <summary>
/// SUPPORT1: the "Collect logs" bundle. One zip (on the client's Desktop by default, else in <c>BepInEx/progenitor-support</c>) named
/// <c>vrising-mod-logs-&lt;side&gt;-&lt;yyyyMMdd-HHmmss&gt;.zip</c> holding README.txt, summary.txt, the logs and a few
/// small Progenitor state files, every text passed through <see cref="SupportRedactor"/>. The newest
/// <see cref="DefaultKeep"/> bundles are kept in <c>BepInEx/progenitor-support</c> only. Game-free: it reads files only, and never throws for one unreadable file
/// (summary.txt lists it as skipped).
/// </summary>
internal static class SupportBundle
{
    internal const string FolderName = "progenitor-support";

    /// <summary>How the status line names the Desktop.</summary>
    internal const string DesktopLabel = "your Desktop";
    /// <summary>
    /// The bundle's file name prefix: <c>vrising-mod-logs-&lt;side&gt;-&lt;yyyyMMdd-HHmmss&gt;.zip</c>. The bundle helps the
    /// developer of whichever mod misbehaves, so its name says V Rising mod logs, not Progenitor.
    /// </summary>
    internal const string FilePrefix = "vrising-mod-logs-";

    /// <summary>The name bundles had before (<c>progenitor-logs-*.zip</c>): still found, kept in retention and cleaned up.</summary>
    internal const string LegacyFilePrefix = "progenitor-logs-";

    /// <summary>Both prefixes, newest first.</summary>
    internal static readonly string[] BundlePrefixes = { FilePrefix, LegacyFilePrefix };

    /// <summary>Every bundle in <paramref name="folder"/> (both names), or none when it cannot be read.</summary>
    internal static List<FileInfo> BundleFiles(string folder, string suffix = ".zip")
    {
        var files = new List<FileInfo>();
        try
        {
            // One listing of the folder, filtered by name (both bundle names): the Desktop can hold many files.
            foreach (var file in new DirectoryInfo(folder).GetFiles("*" + suffix))
                if (IsBundleName(file.Name, suffix)) files.Add(file);
        }
        catch { /* unreadable */ }
        return files;
    }

    /// <summary>True for a bundle's file name (or its temp file's, with <paramref name="suffix"/> <c>.zip.tmp</c>), either name.</summary>
    internal static bool IsBundleName(string? name, string suffix = ".zip")
    {
        if (string.IsNullOrEmpty(name) || !name!.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return false;
        foreach (string prefix in BundlePrefixes)
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
    internal const long DefaultPerFileCap = 16L * 1024 * 1024;
    internal const long DefaultMaxZipBytes = 45L * 1024 * 1024;
    internal const long StateFileCap = 1024 * 1024;
    internal const long MinPerFileCap = 128 * 1024;
    internal const int DefaultKeep = 5;
    /// <summary>The most local dedicated servers one bundle collects.</summary>
    internal const int MaxLocalServers = 3;

    /// <summary>A local dedicated server and its <c>local-server-&lt;n&gt;</c> number.</summary>
    internal readonly struct LocalServerSlot
    {
        public LocalServerSlot(int number, LocalServerSource server)
        {
            Number = number;
            Server = server;
        }

        public int Number { get; }
        public LocalServerSource Server { get; }
    }

    /// <summary>
    /// The local dedicated servers collected, numbered from 1: each install folder once, never this process's own
    /// folder or the private game already collected under <c>hosted-server/</c>, only folders with a <c>BepInEx</c>
    /// folder, and at most <see cref="MaxLocalServers"/>.
    /// </summary>
    internal static List<LocalServerSlot> LocalServerSlots(SupportBundleRequest r)
    {
        var slots = new List<LocalServerSlot>();
        var seen = new List<string>();
        foreach (var server in r.LocalServers)
        {
            if (slots.Count >= MaxLocalServers) break;
            if (server == null || string.IsNullOrWhiteSpace(server.InstallFolder)) continue;
            string bep;
            try { bep = System.IO.Path.Combine(server.InstallFolder, "BepInEx"); }
            catch { continue; }
            if (!Directory.Exists(bep)) continue;
            if (!string.IsNullOrWhiteSpace(r.BepInExRoot) && SamePath(bep, r.BepInExRoot)) continue;
            if (!string.IsNullOrWhiteSpace(r.HostedBepInExRoot) && SamePath(bep, r.HostedBepInExRoot!)) continue;
            bool duplicate = false;
            foreach (string s in seen) if (SamePath(s, bep)) duplicate = true;
            if (duplicate) continue;
            seen.Add(bep);
            slots.Add(new LocalServerSlot(slots.Count + 1, server));
        }
        return slots;
    }

    internal const int ListingLimit = 400;

    /// <summary>Older rolled BepInEx logs (<c>LogOutput.1.log</c>, <c>LogOutput.log.prev</c>) count only when written within this window.</summary>
    internal static readonly TimeSpan RecentWindow = TimeSpan.FromDays(1);

    /// <summary>
    /// The V Rising Mod Community Discord's invite: the community's own redirect, the "Modding Discord" link on
    /// Thunderstore's V Rising page. Printed in README.txt and summary.txt; the Logs saved dialog's <b>Join the Discord</b>.
    /// </summary>
    internal const string DiscordInviteUrl = "https://vrisingmods.com/discord";

    /// <summary>
    /// The community's #technical-support forum channel (a public channel link; the two numbers are the server's and the
    /// channel's ids, not a person's). The Logs saved dialog's <b>Open #technical-support</b> shows only while this is a
    /// <c>https://discord.com/channels/</c> link; README.txt names it for players already in the server.
    /// </summary>
    internal const string TechSupportChannelUrl = "https://discord.com/channels/978094827830915092/1112888087836631090";

    /// <summary>Where to post a bundle.</summary>
    internal const string WhereToPost =
        "the #technical-support forum of the V Rising Mod Community Discord, or the mod's Thunderstore page";

    /// <summary>Progenitor's audit logs in the BepInEx folder (each also as its rolled <c>.1</c>).</summary>
    internal static readonly string[] AuditLogs =
    {
        "progenitor-settings.log", "progenitor-remote-install.log", "progenitor-auto-update.log",
    };

    /// <summary>Small state files, looked for in the BepInEx folder and in <c>BepInEx/config</c>.</summary>
    internal static readonly string[] StateFiles =
    {
        "progenitor-install.json", "progenitor-mod-state.json", "progenitor-auto-update.json",
        "progenitor-self-install.json", "progenitor-plugin-actions.json",
    };

    /// <summary>Progenitor's own cfg files and BepInEx's, in <c>BepInEx/config</c>. Other mods' configs are never collected.</summary>
    internal static readonly string[] ConfigFiles =
    {
        "BepInEx.cfg", "progenitor.cfg", "progenitor.ui.cfg", "progenitor.hosted.cfg", "progenitor.dev.cfg",
        "progenitor.dyeall.cfg", "progenitor.servermessage.cfg", "progenitor.serverlist.cfg", "progenitor.serverchat.cfg",
    };

    /// <summary>Folders listed by name, size and date in summary.txt (never their contents).</summary>
    internal static readonly string[] ListedFolders =
    {
        "plugins", "patchers", "progenitor-reload", "progenitor-disabled",
    };

    /// <summary>
    /// Never collected, whatever a plan says: chat history, saved servers and their passwords, the last server joined, the
    /// servers this client joined (<c>progenitor-server-requirements.json</c> names them by host), refused joins, the
    /// server's host/game settings, admin and ban lists, saves, keys and tokens. <paramref name="entry"/> is the file's path
    /// inside the zip, which is its path relative to a known root (<c>bepinex/…</c>, <c>hosted-server/bepinex/…</c>,
    /// <c>unity/…</c>): folder names above those roots (a game installed under <c>D:\Saves\</c>) never count.
    /// </summary>
    internal static bool IsExcluded(string? entry)
    {
        if (string.IsNullOrWhiteSpace(entry)) return true;
        string rel = entry!.Replace('\\', '/').TrimStart('/').ToLowerInvariant();
        if (rel.StartsWith("hosted-server/", StringComparison.Ordinal)) rel = rel.Substring("hosted-server/".Length);
        else if (rel.StartsWith("local-server-", StringComparison.Ordinal) && rel.IndexOf('/') > 0) rel = rel.Substring(rel.IndexOf('/') + 1);
        if (rel.StartsWith("bepinex/", StringComparison.Ordinal)) rel = rel.Substring("bepinex/".Length);
        if (rel.Length == 0) return true;
        if (rel.StartsWith("progenitor-cache/chat/", StringComparison.Ordinal) || rel == "progenitor-cache/chat") return true;
        string[] segments = rel.Split('/');
        for (int i = 0; i < segments.Length - 1; i++)
            if (segments[i] is "saves" or "save-data" or "cloudsaves") return true;
        string name = segments[segments.Length - 1];
        foreach (string bad in ExcludedNames)
            if (string.Equals(name, bad, StringComparison.Ordinal)) return true;
        string ext = System.IO.Path.GetExtension(name);
        return ext is ".token" or ".key" or ".save";
    }

    /// <summary>File names never collected (lowercase).</summary>
    internal static readonly string[] ExcludedNames =
    {
        "progenitor-server-profiles.json", "progenitor-last-server.json", "progenitor-refused-joins.json",
        "progenitor-server-requirements.json", "serverhostsettings.json", "servergamesettings.json", "progenitor-dev.token",
        "progenitor-operator.key", "progenitor-trusted-operators.json", "adminlist.txt", "banlist.txt",
    };

    /// <summary>
    /// cfg keys whose values are names or free text a player or admin typed (muted players, watched words, chat tab
    /// names, test names, the server's messages and listing text). Their values are replaced by <c>[hidden]</c> in every
    /// collected cfg file; an empty value stays empty.
    /// </summary>
    internal static readonly string[] HiddenCfgKeys =
    {
        "MutedPlayers", "MentionWords", "TestNames", "Tabs", "Colors", "Message", "NoProgenitorMessage", "Description",
        "Published",
    };

    /// <summary>A cfg file's text with the values of <see cref="HiddenCfgKeys"/> hidden. Line by line, no regex.</summary>
    internal static string HideCfgValues(string text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        var sb = new StringBuilder(text.Length);
        int at = 0;
        while (at < text.Length)
        {
            int nl = text.IndexOf('\n', at);
            int end = nl < 0 ? text.Length : nl + 1;
            string line = text.Substring(at, end - at);
            sb.Append(HideCfgLine(line));
            at = end;
        }
        return sb.ToString();
    }

    private static string HideCfgLine(string line)
    {
        int eq = line.IndexOf('=');
        if (eq <= 0) return line;
        string trimmedStart = line.TrimStart();
        if (trimmedStart.StartsWith("#", StringComparison.Ordinal) || trimmedStart.StartsWith("[", StringComparison.Ordinal)) return line;
        string key = line.Substring(0, eq).Trim();
        bool hide = false;
        foreach (string k in HiddenCfgKeys)
            if (string.Equals(key, k, StringComparison.OrdinalIgnoreCase)) { hide = true; break; }
        if (!hide) return line;
        string rest = line.Substring(eq + 1);
        string newline = rest.EndsWith("\r\n", StringComparison.Ordinal) ? "\r\n" : rest.EndsWith("\n", StringComparison.Ordinal) ? "\n" : "";
        if (rest.Trim().Length == 0) return line;
        return line.Substring(0, eq + 1) + " " + SupportRedactor.Hidden + newline;
    }

    internal static string OutputFolderOf(SupportBundleRequest r) =>
        string.IsNullOrWhiteSpace(r.OutputFolder) ? System.IO.Path.Combine(r.BepInExRoot, FolderName) : r.OutputFolder!;

    /// <summary><c>vrising-mod-logs-client-20261010-142233.zip</c>.</summary>
    internal static string FileNameFor(string side, DateTime local) =>
        FilePrefix + (string.IsNullOrWhiteSpace(side) ? "client" : side.Trim().ToLowerInvariant()) + "-"
        + local.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".zip";

    /// <summary>The files a bundle would take, in zip order. Missing files are left out; excluded ones never appear.</summary>
    internal static List<SupportSource> Plan(SupportBundleRequest r)
    {
        var list = new List<SupportSource>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string entry, string path, bool isLog)
        {
            if (IsExcluded(entry) || !File.Exists(path)) return;
            if (!seen.Add(entry)) return;
            list.Add(new SupportSource(entry, path, isLog));
        }

        string root = r.BepInExRoot ?? "";
        if (root.Length > 0)
        {
            AddBepInExLogs(root, "bepinex/", r.NowUtc, Add);
            foreach (string audit in AuditLogs)
            {
                Add("bepinex/" + audit, System.IO.Path.Combine(root, audit), true);
                Add("bepinex/" + audit + ".1", System.IO.Path.Combine(root, audit + ".1"), true);
            }
        }

        if (!string.IsNullOrWhiteSpace(r.UnityLogFolder) && Directory.Exists(r.UnityLogFolder))
            foreach (string f in SafeFiles(r.UnityLogFolder!, "Player*.log"))
                Add("unity/" + System.IO.Path.GetFileName(f), f, true);
        foreach (string extra in r.ExtraLogFiles)
            if (!string.IsNullOrWhiteSpace(extra)) Add("unity/" + System.IO.Path.GetFileName(extra), extra, true);

        if (!string.IsNullOrWhiteSpace(r.HostedBepInExRoot) && Directory.Exists(r.HostedBepInExRoot)
            && !SamePath(r.HostedBepInExRoot!, root))
            AddBepInExLogs(r.HostedBepInExRoot!, "hosted-server/bepinex/", r.NowUtc, Add);

        // Dedicated servers running on this PC (read only), each under local-server-<n>/.
        foreach (var slot in LocalServerSlots(r))
        {
            string prefix = "local-server-" + slot.Number.ToString(CultureInfo.InvariantCulture) + "/";
            string bep = System.IO.Path.Combine(slot.Server.InstallFolder, "BepInEx");
            AddBepInExLogs(bep, prefix + "bepinex/", r.NowUtc, Add);
            foreach (string audit in AuditLogs)
            {
                Add(prefix + "bepinex/" + audit, System.IO.Path.Combine(bep, audit), true);
                Add(prefix + "bepinex/" + audit + ".1", System.IO.Path.Combine(bep, audit + ".1"), true);
            }
            if (!string.IsNullOrWhiteSpace(slot.Server.LogFile))
                Add(prefix + "unity/" + System.IO.Path.GetFileName(slot.Server.LogFile), slot.Server.LogFile!, true);
        }

        if (root.Length > 0)
        {
            string config = System.IO.Path.Combine(root, "config");
            foreach (string name in StateFiles)
            {
                Add("bepinex/" + name, System.IO.Path.Combine(root, name), false);
                Add("bepinex/config/" + name, System.IO.Path.Combine(config, name), false);
            }
            Add("bepinex/progenitor-versions/progenitor/self-update.json",
                System.IO.Path.Combine(root, "progenitor-versions", "progenitor", "self-update.json"), false);
            foreach (string name in ConfigFiles)
                Add("bepinex/config/" + name, System.IO.Path.Combine(config, name), false);
        }

        return list;
    }

    private static void AddBepInExLogs(string root, string prefix, DateTime nowUtc, Action<string, string, bool> add)
    {
        add(prefix + "LogOutput.log", System.IO.Path.Combine(root, "LogOutput.log"), true);
        foreach (string f in SafeFiles(root, "LogOutput*.log*"))
        {
            string name = System.IO.Path.GetFileName(f);
            if (name.Equals("LogOutput.log", StringComparison.OrdinalIgnoreCase)) continue;
            if (!IsRecent(f, nowUtc)) continue;
            add(prefix + name, f, true);
        }
        string sessions = SessionLogFiles.Folder(root);
        if (Directory.Exists(sessions))
            foreach (string f in SafeFiles(sessions, "session*.log"))
                if (SessionLogFiles.IsSessionLogName(System.IO.Path.GetFileName(f)))
                    add(prefix + SessionLogFiles.FolderName + "/" + System.IO.Path.GetFileName(f), f, true);
        add(prefix + "ErrorLog.log", System.IO.Path.Combine(root, "ErrorLog.log"), true);
    }

    /// <summary>
    /// The <c>-logFile</c> argument Unity writes its player log to (a dedicated server's start script normally passes
    /// <c>-logFile ".\logs\VRisingServer.log"</c>), resolved against the working folder. Null when absent or <c>-</c>.
    /// </summary>
    internal static string? UnityLogFileArgument(string[]? args, string? workingDirectory)
    {
        if (args == null) return null;
        for (int i = 0; i + 1 < args.Length; i++)
        {
            if (!string.Equals(args[i], "-logFile", StringComparison.OrdinalIgnoreCase)) continue;
            string value = (args[i + 1] ?? "").Trim().Trim('"');
            if (value.Length == 0 || value == "-" || value.StartsWith("-", StringComparison.Ordinal)) return null;
            try
            {
                return Path.IsPathRooted(value) || string.IsNullOrWhiteSpace(workingDirectory)
                    ? Path.GetFullPath(value)
                    : Path.GetFullPath(Path.Combine(workingDirectory!, value));
            }
            catch { return null; }
        }
        return null;
    }

    private static bool IsRecent(string path, DateTime nowUtc)
    {
        try { return nowUtc - File.GetLastWriteTimeUtc(path) <= RecentWindow; }
        catch { return false; }
    }

    private static List<string> SafeFiles(string folder, string pattern)
    {
        var files = new List<string>();
        try { files.AddRange(Directory.GetFiles(folder, pattern)); }
        catch { /* unreadable folder: nothing from it */ }
        files.Sort(StringComparer.OrdinalIgnoreCase);
        return files;
    }

    private static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(System.IO.Path.GetFullPath(a).TrimEnd('\\', '/'), System.IO.Path.GetFullPath(b).TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>
    /// Build the bundle. A zip larger than <see cref="SupportBundleRequest.MaxZipBytes"/> is rebuilt with half the
    /// per-file cap (down to <see cref="MinPerFileCap"/>). Never throws.
    /// </summary>
    internal static SupportBundleResult Build(SupportBundleRequest r)
    {
        var watch = Stopwatch.StartNew();
        var result = new SupportBundleResult();
        var scrub = new SupportRedactor(r.UserProfile, r.UserName, r.MachineName);

        // The server's zip is used only when it matches the SHA-256 the server announced (checked here, off the main thread).
        if (r.ServerZip != null && r.ServerZipSha256 != null)
        {
            var check = new ServerLogsOutcome { Ok = true, Zip = r.ServerZip, ExpectedSha256 = r.ServerZipSha256 };
            if (!check.Verify())
            {
                r.ServerZip = null;
                r.ServerPartNote = check.Reason;
            }
        }

        if (r.Prepare != null)
        {
            try { r.Prepare(r); }
            catch (Exception e) { r.Notes.Add("Looking for local dedicated servers failed: " + scrub.Redact(e.GetType().Name + ": " + e.Message)); }
        }

        string support = SupportFolderOf(r);
        string primary = OutputFolderOf(r);
        string label = string.IsNullOrWhiteSpace(r.OutputLabel) || SamePath(primary, support) ? "BepInEx/" + FolderName : r.OutputLabel!;
        if (!string.IsNullOrWhiteSpace(r.OutputProblem))
        {
            result.FellBack = true;
            result.FallbackReason = scrub.Redact(r.OutputProblem);
            primary = support;
            label = "BepInEx/" + FolderName;
        }
        else if (!SamePath(primary, support))
        {
            // The chosen folder first; any failure there (missing Desktop, no write access, a full disk) falls back.
            if (!r.CreateOutputFolder && !Directory.Exists(primary))
            {
                result.FellBack = true;
                result.FallbackReason = scrub.Redact(label + " does not exist");
            }
            else
            {
                var first = new SupportBundleResult();
                BuildInto(primary, r, first, prune: false);
                if (first.Ok)
                {
                    first.FolderLabel = label;
                    first.Seconds = watch.Elapsed.TotalSeconds;
                    return first;
                }
                result.FellBack = true;
                result.FallbackReason = scrub.Redact(label + " could not be written (" + first.Error + ")");
            }
            primary = support;
            label = "BepInEx/" + FolderName;
        }

        BuildInto(primary, r, result, prune: true);
        result.FolderLabel = label;
        result.Seconds = watch.Elapsed.TotalSeconds;
        return result;
    }


    /// <summary>
    /// Whether this client is in a world on a local dedicated server listening on <paramref name="serverPort"/>: in a
    /// world, its last join's port is that port, and its last join's address is loopback, <c>localhost</c> or one of this
    /// PC's own addresses. Null when the join or the server's port is not known.
    /// </summary>
    internal static bool? IsConnectedTo(bool inWorld, string? joinAddress, int joinPort, int serverPort, IEnumerable<string>? ownAddresses)
    {
        if (!inWorld) return false;
        if (joinPort <= 0 || serverPort <= 0 || string.IsNullOrWhiteSpace(joinAddress)) return null;
        if (joinPort != serverPort) return false;
        string a = joinAddress!.Trim();
        int scheme = a.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) a = a.Substring(scheme + 3);
        a = a.Trim('[', ']').ToLowerInvariant();
        if (a == "localhost" || a == "::1" || a.StartsWith("127.", StringComparison.Ordinal)) return true;
        if (ownAddresses != null)
            foreach (string own in ownAddresses)
                if (string.Equals((own ?? "").Trim('[', ']'), a, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
    /// <summary><c>BepInEx/progenitor-support</c>: the fallback, and the one folder whose bundles are pruned.</summary>
    internal static string SupportFolderOf(SupportBundleRequest r) => System.IO.Path.Combine(r.BepInExRoot ?? "", FolderName);

    /// <summary>
    /// Where the client's bundle goes: <paramref name="configured"/> (<c>[Support] OutputFolder</c>) when set, else the
    /// Desktop, else <c>BepInEx/progenitor-support</c> with <paramref name="problem"/> saying why. A configured folder is
    /// created when missing; the Desktop never is.
    /// </summary>
    internal static string ChooseOutput(string? configured, string? desktop, string supportFolder, out string label, out bool create,
        out string problem)
    {
        problem = "";
        string c = (configured ?? "").Trim().Trim('"');
        if (c.Length > 0)
        {
            create = true;
            label = c;
            try { return System.IO.Path.GetFullPath(Environment.ExpandEnvironmentVariables(c)); }
            catch (Exception e)
            {
                problem = "the folder " + c + " is not a usable path (" + e.Message + ")";
                label = "BepInEx/" + FolderName;
                return supportFolder;
            }
        }

        create = false;
        if (string.IsNullOrWhiteSpace(desktop))
        {
            problem = "your Desktop could not be found";
            label = "BepInEx/" + FolderName;
            return supportFolder;
        }
        label = DesktopLabel;
        return desktop!;
    }

    private static void BuildInto(string folder, SupportBundleRequest r, SupportBundleResult result, bool prune)
    {
        result.Folder = folder;
        string? temp = null;
        try
        {
            Directory.CreateDirectory(folder);
            CleanTemp(folder);

            // A temp name no other build can share (.progenitor logs and an admin's 'PrSL' request can build at once).
            temp = System.IO.Path.Combine(folder, FilePrefix + Guid.NewGuid().ToString("N") + ".zip.tmp");
            var plan = Plan(r);
            var serverPart = ReadServerPart(r);
            var names = CollectNames(r, plan, Math.Max(MinPerFileCap, r.PerFileCap));
            long cap = Math.Max(MinPerFileCap, r.PerFileCap);
            while (true)
            {
                result.Entries.Clear();
                result.Skipped.Clear();
                var redactor = new SupportRedactor(r.UserProfile, r.UserName, r.MachineName);
                redactor.ExcludeNames(PluginWords(r));
                redactor.AddNames(names);
                WriteZip(temp, r, plan, cap, redactor, result, serverPart);
                result.Players = redactor.PlayerCount;
                long size = new FileInfo(temp).Length;
                result.Bytes = size;
                result.PerFileCap = cap;
                result.SteamIds = redactor.SteamIdCount;
                result.Addresses = redactor.AddressCount;
                if (size <= r.MaxZipBytes || cap <= MinPerFileCap) break;
                cap = Math.Max(MinPerFileCap, cap / 2);
            }

            string name = MoveIntoPlace(temp, folder, r.Side, r.NowLocal, out string final);
            temp = null;
            result.Ok = true;
            result.Path = final;
            result.FileName = name;
            if (prune) Prune(folder, r.Keep, final);
        }
        catch (Exception e)
        {
            result.Ok = false;
            // An exception message can carry an absolute path: it is shown to the player and sent to a server admin.
            result.Error = new SupportRedactor(r.UserProfile, r.UserName, r.MachineName).Redact(e.GetType().Name + ": " + e.Message);
            if (temp != null)
            {
                try { if (File.Exists(temp)) File.Delete(temp); }
                catch { /* best effort */ }
            }
        }
    }

    /// <summary>
    /// Move the finished temp file to the first free <c>vrising-mod-logs-&lt;side&gt;-&lt;time&gt;[-n].zip</c>; a name taken
    /// between the check and the move (another build finishing in the same second) is retried with the next number.
    /// </summary>
    internal static string MoveIntoPlace(string temp, string folder, string side, DateTime local, out string final)
    {
        string stem = System.IO.Path.GetFileNameWithoutExtension(FileNameFor(side, local));
        for (int n = 1; n < 1000; n++)
        {
            string name = n == 1 ? stem + ".zip" : stem + "-" + n.ToString(CultureInfo.InvariantCulture) + ".zip";
            final = System.IO.Path.Combine(folder, name);
            if (File.Exists(final)) continue;
            try
            {
                File.Move(temp, final);
                return name;
            }
            catch (IOException) when (File.Exists(final))
            {
                // Taken meanwhile: try the next number.
            }
        }
        throw new IOException("no free bundle name in the folder");
    }

    /// <summary>
    /// The names to replace as <c>[player-N]</c>: <see cref="SupportBundleRequest.KnownNames"/>, then every name the
    /// collected logs write in a known format (a first pass over the same text the bundle will hold, so a name learned in
    /// Player.log is also removed from a file read before it). A file that cannot be read is skipped here; the write
    /// pass records it.
    /// </summary>
    private static List<string> CollectNames(SupportBundleRequest r, List<SupportSource> plan, long cap)
    {
        var names = new HashSet<string>(r.KnownNames, StringComparer.OrdinalIgnoreCase);
        foreach (var source in plan)
        {
            if (!source.IsLog) continue;
            try { SupportRedactor.LearnNames(ReadTail(source.Path, cap, out _, out _), names); }
            catch { /* unreadable: the write pass names it under Skipped */ }
        }
        return new List<string>(names);
    }

    /// <summary>The loaded plugins' names, GUIDs and assembly names: never treated as player names.</summary>
    internal static List<string> PluginWords(SupportBundleRequest r)
    {
        var words = new List<string>();
        foreach (var p in r.DiagnosisPlugins)
        {
            if (p == null) continue;
            words.Add(p.Name);
            words.Add(p.Guid);
            if (!string.IsNullOrWhiteSpace(p.Display)) words.Add(p.Display!);
            words.AddRange(p.Assemblies);
        }
        return words;
    }

    private static void WriteZip(string temp, SupportBundleRequest r, List<SupportSource> plan, long cap, SupportRedactor redactor,
        SupportBundleResult result, ServerPart? serverPart)
    {
        var fileLines = new List<string>();
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false, entryNameEncoding: Encoding.UTF8))
        {
            var diagnosis = new SupportDiagnosis(r.DiagnosisPlugins);
            var planned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in plan) planned.Add(p.Entry);
            foreach (var problem in r.ModProblems) diagnosis.AddProblem(problem.Key, problem.Value);

            foreach (var source in plan)
            {
                string text;
                long size;
                bool cut;
                try
                {
                    text = ReadTail(source.Path, source.IsLog ? cap : StateFileCap, out size, out cut);
                }
                catch (Exception e)
                {
                    result.Skipped.Add(source.Entry + ": " + e.GetType().Name + ": " + redactor.Redact(e.Message));
                    continue;
                }

                if (source.Entry.EndsWith(".cfg", StringComparison.OrdinalIgnoreCase)) text = HideCfgValues(text);
                int withheldBefore = redactor.WithheldCount;
                string redacted = redactor.Redact(text);
                WriteEntry(zip, source.Entry, redacted);
                // The diagnosis reads what the zip holds: the redacted text.
                if (source.IsLog && SupportDiagnosis.ShouldFeed(source.Entry, planned)) diagnosis.Feed(source.Entry, redacted);
                result.Entries.Add(source.Entry);
                fileLines.Add("  " + source.Entry + "  " + FormatBytes(size) + (cut ? "  (last " + FormatBytes(source.IsLog ? cap : StateFileCap) + " kept)" : "")
                    + (redactor.WithheldCount > withheldBefore ? "  (withheld: redaction timed out)" : ""));
            }

            string serverLine = CopyServerZip(zip, r, serverPart, result);
            var report = diagnosis.Finish();
            result.LikelyInvolved = report.StatusPhrase;
            result.TopSuspect = report.TopName;
            string summary = Summary(r, fileLines, result.Skipped, Listings(r.BepInExRoot), serverLine, report.Section, report.TopName);
            WriteEntry(zip, "summary.txt", redactor.Redact(summary));
            WriteEntry(zip, "README.txt", redactor.Redact(Readme(r.Side, report.TopName)));
            result.Entries.Insert(0, "summary.txt");
            result.Entries.Insert(0, "README.txt");
        }
    }

    /// <summary>The folder the server's bundle is merged under.</summary>
    internal const string ServerFolder = "server/";

    /// <summary>The most a server bundle may hold once uncompressed (a 20 MB zip of text logs is far below it).</summary>
    internal const long MaxServerUncompressedBytes = 80L * 1024 * 1024;

    /// <summary>The most entries a server bundle may hold.</summary>
    internal const int MaxServerEntries = 500;

    /// <summary>The server's entries, read once per build (not once per cap-halving pass).</summary>
    private sealed class ServerPart
    {
        public readonly List<KeyValuePair<string, byte[]>> Entries = new();
        public string Missing = "";
        public string Note = "";
    }

    /// <summary>
    /// Read the server's zip once: entry names under <c>server/</c> (no folders, no <c>..</c>), at most
    /// <see cref="MaxServerEntries"/> entries and <see cref="MaxServerUncompressedBytes"/> uncompressed. Past a cap it stops,
    /// keeps what it read and says so; an unreadable zip is left out whole.
    /// </summary>
    private static ServerPart? ReadServerPart(SupportBundleRequest r)
    {
        if (r.ServerZip == null) return null;
        var part = new ServerPart();
        try
        {
            long total = 0;
            using var source = new ZipArchive(new MemoryStream(r.ServerZip, writable: false), ZipArchiveMode.Read);
            foreach (var entry in source.Entries)
            {
                string name = entry.FullName.Replace('\\', '/').TrimStart('/');
                if (name.Length == 0 || name.EndsWith("/", StringComparison.Ordinal) || name.Contains("..")) continue;
                if (part.Entries.Count >= MaxServerEntries)
                {
                    part.Note = "; stopped at " + MaxServerEntries.ToString(CultureInfo.InvariantCulture) + " files";
                    break;
                }
                long room = MaxServerUncompressedBytes - total;
                using var from = entry.Open();
                var bytes = ReadBounded(from, room, out bool over);
                if (over)
                {
                    part.Note = "; stopped at " + FormatBytes(MaxServerUncompressedBytes) + " uncompressed";
                    break;
                }
                total += bytes.Length;
                part.Entries.Add(new KeyValuePair<string, byte[]>(ServerFolder + name, bytes));
            }
        }
        catch (Exception e)
        {
            part.Entries.Clear();
            part.Missing = "the server's zip could not be read (" + e.GetType().Name + ")";
        }
        return part;
    }

    /// <summary>Write the server's entries (read once by <see cref="ReadServerPart"/>). Returns summary.txt's line about them.</summary>
    private static string CopyServerZip(ZipArchive zip, SupportBundleRequest r, ServerPart? part, SupportBundleResult result)
    {
        if (part == null)
        {
            if (string.IsNullOrWhiteSpace(r.ServerPartNote)) return "";
            result.ServerPartMissing = r.ServerPartNote!;
            return "Server logs: not included: " + r.ServerPartNote;
        }
        if (part.Missing.Length > 0)
        {
            result.ServerPartMissing = part.Missing;
            return "Server logs: not included: " + part.Missing;
        }
        foreach (var e in part.Entries)
        {
            var target = zip.CreateEntry(e.Key, CompressionLevel.Optimal);
            using (var to = target.Open()) to.Write(e.Value, 0, e.Value.Length);
            result.Entries.Add(e.Key);
        }
        result.ServerPartIncluded = true;
        return "Server logs: included under server/ (" + part.Entries.Count.ToString(CultureInfo.InvariantCulture) + " files, "
            + FormatBytes(r.ServerZip!.Length) + " as sent; redacted by the server" + part.Note + ").";
    }

    private static byte[] ReadBounded(Stream from, long max, out bool over)
    {
        over = false;
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        long total = 0;
        int n;
        while ((n = from.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += n;
            if (total > max)
            {
                over = true;
                return Array.Empty<byte>();
            }
            ms.Write(buffer, 0, n);
        }
        return ms.ToArray();
    }

    private static void WriteEntry(ZipArchive zip, string name, string text)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        w.Write(text);
    }

    /// <summary>
    /// The last <paramref name="cap"/> bytes of a file another process may still be writing, starting at a line, with a
    /// <c>[truncated: first N bytes omitted]</c> line in front when it was cut.
    /// </summary>
    internal static string ReadTail(string path, long cap, out long size, out bool truncated)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        size = fs.Length;
        truncated = size > cap;
        long start = truncated ? size - cap : 0;
        fs.Seek(start, SeekOrigin.Begin);
        var buffer = new byte[(int)Math.Min(cap, size)];
        int read = 0;
        while (read < buffer.Length)
        {
            int n = fs.Read(buffer, read, buffer.Length - read);
            if (n <= 0) break;
            read += n;
        }

        int offset = 0;
        if (truncated)
        {
            int nl = Array.IndexOf(buffer, (byte)'\n', 0, read);
            if (nl >= 0 && nl + 1 < read) offset = nl + 1;
        }
        else if (read >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF)
        {
            offset = 3;
        }

        string text = new UTF8Encoding(false, false).GetString(buffer, offset, read - offset);
        if (!truncated) return text;
        long omitted = start + offset;
        return "[truncated: first " + omitted.ToString(CultureInfo.InvariantCulture) + " bytes omitted]" + Environment.NewLine + text;
    }

    /// <summary>Keep the newest <paramref name="keep"/> bundles (by write time, then name); never deletes <paramref name="justWritten"/>.</summary>
    internal static void Prune(string folder, int keep, string? justWritten = null)
    {
        try
        {
            var files = BundleFiles(folder);
            files.Sort((a, b) =>
            {
                int c = b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc);
                return c != 0 ? c : string.Compare(b.Name, a.Name, StringComparison.OrdinalIgnoreCase);
            });
            string? mine = justWritten == null ? null : System.IO.Path.GetFullPath(justWritten);
            int kept = mine != null && File.Exists(mine) ? 1 : 0;
            foreach (var f in files)
            {
                if (mine != null && string.Equals(f.FullName, mine, StringComparison.OrdinalIgnoreCase)) continue;
                if (kept < Math.Max(1, keep))
                {
                    kept++;
                    continue;
                }
                try { f.Delete(); }
                catch { /* open in another program; next time */ }
            }
        }
        catch { /* best effort */ }
    }

    private static void CleanTemp(string folder)
    {
        try
        {
            foreach (var f in BundleFiles(folder, ".zip.tmp"))
            {
                // Only an old leftover: a fresh one may belong to a build running right now.
                if (DateTime.UtcNow - f.LastWriteTimeUtc < TimeSpan.FromMinutes(10)) continue;
                try { f.Delete(); }
                catch { /* in use */ }
            }
        }
        catch { /* best effort */ }
    }

    /// <summary>The newest bundle in <paramref name="folder"/>, or null.</summary>
    internal static FileInfo? Newest(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return null;
        try
        {
            if (!Directory.Exists(folder)) return null;
            FileInfo? best = null;
            foreach (var f in BundleFiles(folder))
                if (best == null || f.LastWriteTimeUtc > best.LastWriteTimeUtc) best = f;
            return best;
        }
        catch { return null; }
    }

    internal static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return bytes.ToString(CultureInfo.InvariantCulture) + " B";
        if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0", CultureInfo.InvariantCulture) + " KB";
        return (bytes / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
    }

    /// <summary>The one line README.txt says about who made it.</summary>
    internal const string CollectedBy = "Collected by Progenitor (Get help > Collect logs).";

    /// <summary>README.txt: how to ask for help, what the zip holds, what was removed. <paramref name="suspect"/> fills the suggested title.</summary>
    internal static string Readme(string side, string? suspect = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("V Rising mod support bundle (" + (side ?? "client") + ")");
        sb.AppendLine();
        sb.AppendLine("These are the logs that help the developer of whichever mod is misbehaving: the game's and BepInEx's");
        sb.AppendLine("logs, the mods you have installed and a few settings, with personal details removed.");
        sb.AppendLine(CollectedBy);
        sb.AppendLine();
        sb.AppendLine("HOW TO ASK FOR HELP");
        sb.AppendLine();
        sb.AppendLine("1. Open the #technical-support forum of the V Rising Mod Community Discord and click New Post.");
        if (DiscordInviteUrl.Length > 0) sb.AppendLine("   Join the Discord: " + DiscordInviteUrl);
        if (TechSupportChannelUrl.Length > 0) sb.AppendLine("   Already in the server? Open #technical-support: " + TechSupportChannelUrl);
        sb.AppendLine("2. Title: \"" + SuggestedTitle(suspect) + "\"");
        sb.AppendLine("   for example \"" + Suspect(suspect) + " \u2013 game crashes when I open the map\".");
        if (!string.IsNullOrWhiteSpace(suspect))
            sb.AppendLine("   (" + suspect!.Trim() + " is the mod the logs point at most; see summary.txt. It is a hint, not proof.)");
        sb.AppendLine("3. Write a few lines:");
        sb.AppendLine("   - what you were doing, and what happened (an error, a crash, a freeze, something missing);");
        sb.AppendLine("   - when it started: after installing or updating which mod?");
        sb.AppendLine("   - does it happen every time?");
        sb.AppendLine("   - on your game (client) or on a server; in single player, a private game or a dedicated server.");
        sb.AppendLine("4. Attach this zip: drag it into the post. It is under 50 MB.");
        sb.AppendLine("5. Check the mod's Thunderstore page first (its author may already know), and mention the mod's author in");
        sb.AppendLine("   your post if you can.");
        sb.AppendLine();
        sb.AppendLine("Please look through the files before posting them publicly.");
        sb.AppendLine();
        sb.AppendLine("WHAT IS IN THIS ZIP");
        sb.AppendLine();
        sb.AppendLine("- summary.txt: the mods the logs point at, versions, loaded mods, the files in your plugins folder (names");
        sb.AppendLine("  only) and current mod problems.");
        sb.AppendLine("- bepinex/: BepInEx's log of this session (LogOutput.log, every mod writes to it) and copies of the two");
        sb.AppendLine("  sessions before it (progenitor-logs/session*.log), Steam's ErrorLog.log, and Progenitor's own audit logs,");
        sb.AppendLine("  settings and state files.");
        sb.AppendLine("- unity/: the game's own player logs.");
        sb.AppendLine("- hosted-server/: the logs of the private game this PC hosts, when there is one.");
        sb.AppendLine("- local-server-1/ to local-server-3/: the logs of dedicated servers running on this PC, when there are any.");
        sb.AppendLine("- server/: the bundle of the server you were on, when you are its admin and asked for it (redacted by that server).");
        sb.AppendLine("Long logs keep only their end; a [truncated: ...] line says how much was left out.");
        sb.AppendLine();
        sb.AppendLine("WHAT WAS REMOVED");
        sb.AppendLine();
        sb.AppendLine("- SteamIDs ([steam-1], [steam-2]: the same number for the same player in every file)");
        sb.AppendLine("- player names Progenitor knows: yours and the other players' character names, Steam names in the game's");
        sb.AppendLine("  log, and the names in Progenitor's own logs ([player-1], [player-2])");
        sb.AppendLine("- IP addresses other than 127.0.0.1 ([ip-1], ...) and server host names in connect strings ([host-1], ...)");
        sb.AppendLine("- your Windows user folder, user name and computer name (%USERPROFILE%, [user], [machine])");
        sb.AppendLine("- Discord webhook links, passwords, tokens and API keys ([hidden]), and email addresses ([email])");
        sb.AppendLine("- in Progenitor's settings files: muted players, watched words, chat tab names and the server's messages ([hidden])");
        sb.AppendLine();
        sb.AppendLine("Not removed: names typed in chat or written by other mods' logs, clan and server names. Never collected:");
        sb.AppendLine("chat history, saved servers and the servers you joined, server passwords, saves, keys and tokens. A log");
        sb.AppendLine("whose redaction timed out is left out whole (summary.txt says which).");
        return sb.ToString();
    }

    private static string Suspect(string? suspect) => string.IsNullOrWhiteSpace(suspect) ? "Unknown mod" : suspect!.Trim();

    /// <summary>The title README.txt suggests: <c>&lt;suspect or Unknown mod&gt; \u2013 &lt;what went wrong&gt;</c>.</summary>
    internal static string SuggestedTitle(string? suspect) => Suspect(suspect) + " \u2013 <what went wrong>";

    /// <summary>The first lines of summary.txt: the short version of README.txt's steps.</summary>
    internal static string ShortGuide(string? suspect) =>
        "How to ask for help: in the V Rising Mod Community Discord, #technical-support, click New Post. Title: \""
        + SuggestedTitle(suspect) + "\". Say what you were doing, what happened, when it started (which mod you installed or"
        + " updated), whether it happens every time, and client or server. Attach this zip. Full steps: README.txt."
        + (DiscordInviteUrl.Length > 0 ? " Discord: " + DiscordInviteUrl : "");

    /// <summary>
    /// summary.txt's lines about the dedicated servers running on this PC: each collected one by folder name only, with
    /// whether this client is connected to it, then the notes (a process whose folder could not be read).
    /// </summary>
    internal static string LocalServersSection(SupportBundleRequest r)
    {
        var sb = new StringBuilder();
        var slots = LocalServerSlots(r);
        sb.AppendLine("Dedicated servers running on this PC (" + slots.Count.ToString(CultureInfo.InvariantCulture) + " collected):");
        if (slots.Count == 0) sb.AppendLine("  (none collected)");
        foreach (var slot in slots)
        {
            string connected = slot.Server.Connected switch
            {
                true => "this client is connected to it",
                false => "this client is not connected to it",
                _ => "connection unknown",
            };
            sb.AppendLine("  local-server-" + slot.Number.ToString(CultureInfo.InvariantCulture) + ": folder \"" + slot.Server.Name + "\", "
                + connected + (string.IsNullOrWhiteSpace(slot.Server.LogFile) ? ", no -logFile read" : ", -logFile collected"));
        }
        int skipped = 0;
        foreach (var s in r.LocalServers) if (s != null) skipped++;
        skipped -= slots.Count;
        if (skipped > 0)
            sb.AppendLine("  " + skipped.ToString(CultureInfo.InvariantCulture) + " more left out (already collected, no BepInEx folder, or over "
                + MaxLocalServers.ToString(CultureInfo.InvariantCulture) + ")");
        foreach (string note in r.Notes) sb.AppendLine("  note: " + note);
        return sb.ToString().TrimEnd();
    }

    /// <summary>summary.txt: the time, the side, the game's lines, the runtime, the files in the zip, what was skipped and the folder listings.</summary>
    internal static string Summary(SupportBundleRequest r, IReadOnlyList<string> files, IReadOnlyList<string> skipped, string listings,
        string serverLine = "", string diagnosis = "", string suspect = "")
    {
        var sb = new StringBuilder();
        sb.AppendLine(ShortGuide(suspect));
        sb.AppendLine();
        if (diagnosis.Length > 0)
        {
            sb.AppendLine(diagnosis);
            sb.AppendLine();
        }
        sb.AppendLine("V Rising mod support bundle");
        sb.AppendLine(CollectedBy);
        sb.AppendLine("Created (UTC): " + r.NowUtc.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        sb.AppendLine("Side: " + r.Side);
        sb.AppendLine("OS: " + Safe(() => RuntimeInformation.OSDescription) + " (" + Safe(() => RuntimeInformation.OSArchitecture.ToString()) + ")");
        sb.AppendLine(".NET runtime: " + Safe(() => RuntimeInformation.FrameworkDescription));
        if (!string.IsNullOrWhiteSpace(r.GameSummary))
        {
            sb.AppendLine();
            sb.AppendLine(r.GameSummary.TrimEnd());
        }
        if (r.LocalServers.Count > 0 || r.Notes.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(LocalServersSection(r));
        }
        if (serverLine.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine(serverLine);
        }
        sb.AppendLine();
        sb.AppendLine("Files in this bundle:");
        if (files.Count == 0) sb.AppendLine("  (none)");
        foreach (string f in files) sb.AppendLine(f);
        sb.AppendLine();
        sb.AppendLine("Skipped:");
        if (skipped.Count == 0) sb.AppendLine("  (none)");
        foreach (string s in skipped) sb.AppendLine("  skipped: " + s);
        if (listings.Length > 0)
        {
            sb.AppendLine();
            sb.Append(listings);
        }
        return sb.ToString();
    }

    /// <summary>Each of <see cref="ListedFolders"/>: relative path, size and UTC write time of every file (names only).</summary>
    internal static string Listings(string? bepInExRoot)
    {
        if (string.IsNullOrWhiteSpace(bepInExRoot)) return "";
        var sb = new StringBuilder();
        foreach (string folder in ListedFolders)
        {
            string full = System.IO.Path.Combine(bepInExRoot!, folder);
            if (!Directory.Exists(full)) continue;
            sb.AppendLine("BepInEx/" + folder + ":");
            int n = 0;
            List<FileInfo> files;
            try
            {
                files = new List<FileInfo>(new DirectoryInfo(full).GetFiles("*", SearchOption.AllDirectories));
            }
            catch (Exception e)
            {
                sb.AppendLine("  (unreadable: " + e.GetType().Name + ")");
                continue;
            }
            files.Sort((a, b) => string.Compare(a.FullName, b.FullName, StringComparison.OrdinalIgnoreCase));
            foreach (var f in files)
            {
                if (n++ >= ListingLimit)
                {
                    sb.AppendLine("  ... " + (files.Count - ListingLimit).ToString(CultureInfo.InvariantCulture) + " more");
                    break;
                }
                string rel = f.FullName.Substring(Math.Min(f.FullName.Length, System.IO.Path.GetFullPath(full).TrimEnd('\\', '/').Length + 1)).Replace('\\', '/');
                long len = 0;
                try { len = f.Length; }
                catch { /* gone */ }
                sb.AppendLine("  " + rel + "  " + FormatBytes(len) + "  "
                    + f.LastWriteTimeUtc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
            }
            if (files.Count == 0) sb.AppendLine("  (empty)");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string Safe(Func<string> read)
    {
        try { return read() ?? ""; }
        catch { return "unknown"; }
    }
}
