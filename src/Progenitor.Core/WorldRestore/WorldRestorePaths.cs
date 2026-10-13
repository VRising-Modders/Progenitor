using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace Progenitor.WorldRestore;

/// <summary>
/// WORLDRESTORE1: the names and folders of the world restore (research restore-world-backup-spec-2026-10-06 §1, §3.2).
/// <list type="bullet">
/// <item>The live save: <c>&lt;persistentDataPath&gt;\Saves\v&lt;N&gt;\&lt;SaveName&gt;</c>.</item>
/// <item>The game's wipe archives: siblings named <c>&lt;SaveName&gt;_&lt;ticks&gt;</c> (local ticks, <c>CheckIfResetServer</c>).</item>
/// <item>Progenitor's backups: <c>&lt;persistentDataPath&gt;\progenitor-backups\v&lt;N&gt;\&lt;SaveName&gt;\&lt;yyyyMMdd-HHmmss&gt;-&lt;kind&gt;</c>,
/// on the save's volume (the swap is two renames) and outside <c>Saves\</c> (a hosted game's list would show each one as a game).</item>
/// <item>The request and its result: <c>BepInEx\progenitor-restore.json</c> and <c>BepInEx\progenitor-restore.done.json</c>.</item>
/// </list>
/// File-only; compiled into Core, Bootstrap and the tests.
/// </summary>
internal static class WorldRestorePaths
{
    internal const string RequestFileName = "progenitor-restore.json";
    internal const string ResultFileName = "progenitor-restore.done.json";
    internal const string BackupsFolderName = "progenitor-backups";
    internal const string SidecarName = "progenitor-backup.json";
    internal const string TempFolderName = ".TEMP";
    internal const string StartDateName = "StartDate.json";
    internal const string StartDateBeforeRestoreName = "StartDate.before-restore.json";
    internal const string HostSettingsName = "ServerHostSettings.json";
    internal const string GameSettingsName = "ServerGameSettings.json";
    internal const string KindBeforeRestore = "before-restore";
    internal const string PartialSuffix = ".partial";

    /// <summary>The live dedicated server's root: never restored, never touched (the agent-safety fence of <c>world_save</c>).</summary>
    internal const string ForbiddenLiveServerRoot = @"C:\VRisingServer";

    /// <summary>The per-save settings files: the operator's, not the world's, so a restore keeps the current ones.</summary>
    internal static readonly IReadOnlyList<string> PerSaveSettings = new[] { HostSettingsName, GameSettingsName };

    private static readonly Regex VersionFolder = new("^v[0-9]+$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    internal static string AppliedName(string requestId) => "progenitor-restore.applied-" + SafeId(requestId) + ".json";

    internal static string RefusedName(string requestId) => "progenitor-restore.refused-" + SafeId(requestId) + ".json";

    internal static string IncomingName(string requestId) => ".incoming-" + SafeId(requestId);

    internal static string OutgoingName(string requestId) => ".outgoing-" + SafeId(requestId);

    /// <summary>
    /// WORLDRESTORE2: the journal of the restore in progress (<c>BepInEx\progenitor-restore.journal.json</c>), written before
    /// every step that changes something and read by the start-time recovery.
    /// </summary>
    internal const string JournalFileName = "progenitor-restore.journal.json";

    /// <summary>Retention renames a backup to this prefix before deleting it (a half-deleted folder is never listed).</summary>
    internal const string DeletingPrefix = ".deleting-";

    internal const string KindReplaced = "replaced";

    /// <summary>BACKUPNOW1: an admin's <b>Back up now</b> (sidecar kind and folder suffix).</summary>
    internal const string KindManual = "manual";

    /// <summary>BACKUPNOW1: a scheduled backup, at a server start or every N hours (sidecar kind and folder suffix).</summary>
    internal const string KindScheduled = "scheduled";

    /// <summary>
    /// AUTOUPDATE3: a backup the server makes before it applies mod updates (sidecar kind and folder suffix). Its own retention
    /// (<see cref="WorldBackupRetention.PreUpdatePruneList"/>, <c>[AutoUpdate] PreUpdateBackupKeep</c>): it never counts against or
    /// deletes a manual or scheduled backup, and the restore chain's keep count never counts it.
    /// </summary>
    internal const string KindPreUpdate = "pre-update";

    /// <summary>
    /// AUTOUPDATE3 (review 2026-10-09): a pre-update backup's sidecar has its own name. An older Bootstrap or Core lists only
    /// folders with <see cref="SidecarName"/>, so it never sees a pre-update backup: it cannot count one into the restore
    /// chain's keep count (where it would prune real before-restore backups) or delete it. Only code that knows the kind lists
    /// it.
    /// </summary>
    internal const string PreUpdateSidecarName = "progenitor-backup.pre-update.json";

    /// <summary>The sidecar file name a backup of <paramref name="kind"/> is written with.</summary>
    internal static string SidecarNameFor(string? kind) =>
        string.Equals(kind, KindPreUpdate, StringComparison.Ordinal) ? PreUpdateSidecarName : SidecarName;

    /// <summary>Is <paramref name="relative"/> a backup's sidecar (either name)?</summary>
    internal static bool IsSidecarName(string? relative) =>
        string.Equals(relative, SidecarName, StringComparison.OrdinalIgnoreCase) || string.Equals(relative, PreUpdateSidecarName, StringComparison.OrdinalIgnoreCase);

    /// <summary>BACKUPNOW1: what made a backup (the sidecar's <c>trigger</c>).</summary>
    internal const string TriggerStart = "start";

    /// <summary>AUTOUPDATE3: the trigger of a pre-update backup.</summary>
    internal const string TriggerUpdate = "update";

    internal const string TriggerInterval = "interval";
    internal const string TriggerManual = "manual";

    /// <summary>BACKUPNOW1: <c>BepInEx\progenitor-backups.status.json</c>, the last result of each backup trigger.</summary>
    internal const string BackupStatusFileName = "progenitor-backups.status.json";

    /// <summary>
    /// BACKUPNOW1: a manual or scheduled backup; AUTOUPDATE3: or a pre-update one. These have their own retention
    /// (<see cref="WorldBackupRetention"/>); every other Progenitor backup (<c>before-restore</c>, a replaced world) belongs to
    /// the restore chain and its keep count.
    /// </summary>
    internal static bool IsMadeBackupKind(string? kind) =>
        string.Equals(kind, KindManual, StringComparison.Ordinal) || string.Equals(kind, KindScheduled, StringComparison.Ordinal)
        || string.Equals(kind, KindPreUpdate, StringComparison.Ordinal);

    /// <summary>A request id as a file-name part: letters, digits and dashes only.</summary>
    internal static string SafeId(string? id)
    {
        var chars = new List<char>();
        foreach (char c in id ?? "")
            if (char.IsLetterOrDigit(c) && c < 128 || c == '-') chars.Add(c);
        string s = new string(chars.ToArray());
        if (s.Length > 40) s = s.Substring(0, 40);
        return s.Length == 0 ? "unnamed" : s;
    }

    /// <summary>The parts of a save folder <c>&lt;persist&gt;\Saves\v&lt;N&gt;\&lt;save&gt;</c>.</summary>
    internal readonly record struct SaveLayout(string SaveFolder, string PersistRoot, string VersionFolder, string SaveName)
    {
        /// <summary><c>&lt;persist&gt;\progenitor-backups\v&lt;N&gt;\&lt;save&gt;</c>.</summary>
        public string BackupsRoot => Path.Combine(PersistRoot, BackupsFolderName, VersionFolder, SaveName);

        /// <summary>The folder that holds the save and its wipe archives (<c>Saves\v&lt;N&gt;</c>).</summary>
        public string VersionRoot => Path.GetDirectoryName(SaveFolder) ?? "";
    }

    /// <summary>
    /// Split <paramref name="saveFolder"/> into its layout, or say why it is not a dedicated save folder
    /// (<c>CloudSaves\&lt;id&gt;\v4\&lt;name&gt;</c> and every other shape is refused in phase 1).
    /// </summary>
    internal static bool TryLayout(string? saveFolder, out SaveLayout layout, out string reason)
    {
        layout = default;
        reason = "";
        string s = (saveFolder ?? "").Trim().TrimEnd('\\', '/');
        if (s.Length == 0)
        {
            reason = "The save folder is not known.";
            return false;
        }

        string full;
        try { full = Path.GetFullPath(s).TrimEnd('\\', '/'); }
        catch (Exception e)
        {
            reason = "The save folder is not a valid path (" + e.Message + ").";
            return false;
        }

        string name = Path.GetFileName(full);
        string? versionDir = Path.GetDirectoryName(full);
        string version = versionDir == null ? "" : Path.GetFileName(versionDir);
        string? savesDir = versionDir == null ? null : Path.GetDirectoryName(versionDir);
        string saves = savesDir == null ? "" : Path.GetFileName(savesDir);
        string? persist = savesDir == null ? null : Path.GetDirectoryName(savesDir);
        if (name.Length == 0 || !VersionFolder.IsMatch(version) || !string.Equals(saves, "Saves", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrEmpty(persist))
        {
            reason = "The save folder is not <persistentDataPath>\\Saves\\v<N>\\<save name> (" + full + "); only that layout can be restored.";
            return false;
        }

        layout = new SaveLayout(full, persist!, version, name);
        return true;
    }

    /// <summary>True when <paramref name="folderName"/> is a wipe archive of <paramref name="saveName"/>: <c>&lt;save&gt;_&lt;digits&gt;</c>.</summary>
    internal static bool IsArchiveName(string saveName, string folderName, out long ticks)
    {
        ticks = 0;
        if (string.IsNullOrEmpty(saveName) || string.IsNullOrEmpty(folderName)) return false;
        string prefix = saveName + "_";
        if (!folderName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || folderName.Length == prefix.Length) return false;
        string digits = folderName.Substring(prefix.Length);
        foreach (char c in digits)
            if (c < '0' || c > '9') return false;
        return long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out ticks);
    }

    /// <summary>When the game archived the world: <c>DateTime.Now.Ticks</c> at the wipe (local), or null when not a date.</summary>
    internal static DateTime? ArchiveTime(long ticks)
    {
        if (ticks <= 0 || ticks > DateTime.MaxValue.Ticks) return null;
        return new DateTime(ticks, DateTimeKind.Local);
    }

    /// <summary>The name of a new backup folder: <c>yyyyMMdd-HHmmss-&lt;kind&gt;</c> in local time.</summary>
    internal static string BackupFolderName(DateTime local, string kind) =>
        local.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + kind;

    /// <summary>
    /// The game's own save-file rule (<c>ServerRuntimeSettings.CountsAsSaveFile</c>): a top-level file whose name does not
    /// start with a dot and ends in <c>.save.gz</c> or <c>.save</c>.
    /// </summary>
    internal static bool CountsAsSaveFile(string relative)
    {
        if (string.IsNullOrEmpty(relative)) return false;
        if (relative.IndexOf('\\') >= 0 || relative.IndexOf('/') >= 0) return false;
        if (relative.StartsWith(".", StringComparison.Ordinal)) return false;
        return relative.EndsWith(".save.gz", StringComparison.OrdinalIgnoreCase) || relative.EndsWith(".save", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A file a copy of a world leaves out: anything under <c>.TEMP\</c> (the game's save staging).</summary>
    internal static bool IsTransient(string relative)
    {
        string r = relative.Replace('/', '\\');
        return r.StartsWith(TempFolderName + "\\", StringComparison.OrdinalIgnoreCase)
            || string.Equals(r, TempFolderName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when <paramref name="relative"/> names a top-level per-save settings file.</summary>
    internal static bool IsPerSaveSettings(string relative)
    {
        foreach (string n in PerSaveSettings)
            if (string.Equals(relative, n, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// The live-server fence: <paramref name="text"/> (a path or a command line) names <see cref="ForbiddenLiveServerRoot"/>
    /// as a whole token (<c>C:\VRisingServer2</c> is another folder), the rule of <c>WorldSaveTrigger.TryAllowWorldSave</c>.
    /// </summary>
    internal static bool MentionsLiveServer(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        string token = LiveRoot;
        int start = 0;
        while (start < text!.Length)
        {
            int i = text.IndexOf(token, start, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return false;
            bool beforeOk = i == 0 || !IsTokenChar(text[i - 1]);
            int after = i + token.Length;
            bool afterOk = after >= text.Length || !IsTokenChar(text[after]);
            if (beforeOk && afterOk) return true;
            start = i + 1;
        }

        return false;
    }

    private static bool IsTokenChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary><c>\\?\C:\x</c> → <c>C:\x</c>, <c>\\?\UNC\h\s</c> → <c>\\h\s</c>, <c>\\.\C:\x</c> → <c>C:\x</c>.</summary>
    internal static string StripDevicePrefix(string path)
    {
        string s = path ?? "";
        if (s.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + s.Substring(8);
        if (s.StartsWith(@"\\?\", StringComparison.Ordinal) || s.StartsWith(@"\\.\", StringComparison.Ordinal)) return s.Substring(4);
        return s;
    }

    /// <summary>
    /// A path in its final form for the live-server fence: quotes and forward slashes, the <c>\\?\</c> and <c>\\.\</c>
    /// prefixes, this machine's administrative shares (<c>\\localhost\C$\x</c> → <c>C:\x</c>), relative parts, and (on
    /// Windows) 8.3 names, junctions and symbolic links of the deepest existing folder (<c>GetFinalPathNameByHandle</c>).
    /// </summary>
    internal static string FinalPath(string? path)
    {
        string full = PlainFullPath(path);
        if (full.Length == 0 || !OperatingSystem.IsWindows()) return full;
        // Resolve the deepest part that exists, then add the rest back.
        string probe = full;
        var rest = new List<string>();
        while (!string.IsNullOrEmpty(probe) && !Directory.Exists(probe) && !File.Exists(probe))
        {
            rest.Insert(0, Path.GetFileName(probe));
            probe = Path.GetDirectoryName(probe) ?? "";
        }

        if (string.IsNullOrEmpty(probe)) return full;
        string resolved = (FinalPathOfForTests != null ? FinalPathOfForTests(probe) : WindowsFiles.FinalPathOf(probe)) ?? probe;
        foreach (string r in rest) resolved = Path.Combine(resolved, r);
        // Final review (2026-10-08): a mapped drive or a link onto this machine's admin share resolves to
        // \\?\UNC\<this machine>\C$\...: the same text rule as the typed path brings it back to C:\....
        return PlainFullPath(resolved);
    }

    /// <summary>Tests: the final-path resolution of an existing path on this thread (null: the real one).</summary>
    internal static Func<string, string?>? FinalPathOfForTests
    {
        get => _finalPathOfForTests;
        set => _finalPathOfForTests = value;
    }

    [ThreadStatic] private static Func<string, string?>? _finalPathOfForTests;

    /// <summary><see cref="FinalPath"/> without touching the disk: the text forms only.</summary>
    internal static string PlainFullPath(string? path)
    {
        string s = (path ?? "").Trim().Trim('"').Replace('/', '\\');
        if (s.Length == 0) return "";
        s = StripDevicePrefix(s);
        if (s.StartsWith(@"\\", StringComparison.Ordinal))
        {
            string[] parts = s.Substring(2).Split('\\');
            if (parts.Length >= 2 && parts[1].Length == 2 && parts[1][1] == '$' && char.IsLetter(parts[1][0]) && IsThisMachine(parts[0]))
                s = parts[1][0] + @":\" + string.Join("\\", parts, 2, parts.Length - 2);
        }

        try { return Path.GetFullPath(s).TrimEnd('\\', '/'); }
        catch { return s; }
    }

    /// <summary>
    /// An admin share's host is this machine: <c>localhost</c>, <c>.</c>, a loopback address, this machine's name (short or
    /// with its domain), or an address one of its interfaces carries (review L1, 2026-10-08: a LAN IP or the host name).
    /// </summary>
    private static bool IsThisMachine(string host)
    {
        string h = (host ?? "").Trim();
        if (h.Length == 0) return false;
        if (string.Equals(h, "localhost", StringComparison.OrdinalIgnoreCase) || h == "127.0.0.1" || h == "::1" || h == "."
            || string.Equals(h, "[::1]", StringComparison.Ordinal)) return true;
        try
        {
            if (string.Equals(h, Environment.MachineName, StringComparison.OrdinalIgnoreCase)) return true;
            string dns = System.Net.Dns.GetHostName();
            if (string.Equals(h, dns, StringComparison.OrdinalIgnoreCase)) return true;
            int dot = h.IndexOf('.');
            if (dot > 0 && !System.Net.IPAddress.TryParse(h, out _)
                && (string.Equals(h.Substring(0, dot), Environment.MachineName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(h.Substring(0, dot), dns, StringComparison.OrdinalIgnoreCase))) return true;
        }
        catch { /* fall through to the addresses */ }

        if (!System.Net.IPAddress.TryParse(h.Trim('[', ']'), out var ip)) return false;
        if (System.Net.IPAddress.IsLoopback(ip)) return true;
        foreach (var own in OwnAddresses())
            if (own.Equals(ip)) return true;
        return false;
    }

    private static readonly object AddressGate = new();
    private static List<System.Net.IPAddress>? _ownAddresses;
    private static long _ownAddressesAt;

    /// <summary>This machine's interface addresses, read at most every 30 s (this file is shared with Bootstrap).</summary>
    private static IReadOnlyList<System.Net.IPAddress> OwnAddresses()
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        lock (AddressGate)
        {
            if (_ownAddresses != null && now - _ownAddressesAt < 30 * System.Diagnostics.Stopwatch.Frequency) return _ownAddresses;
        }

        var list = new List<System.Net.IPAddress>();
        try
        {
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                foreach (var a in nic.GetIPProperties().UnicastAddresses)
                    list.Add(a.Address);
        }
        catch { /* none known */ }

        lock (AddressGate)
        {
            _ownAddresses = list;
            _ownAddressesAt = now;
        }

        return list;
    }

    /// <summary>Tests: the live root the fence guards on this thread (null: <see cref="ForbiddenLiveServerRoot"/>).</summary>
    internal static string? LiveRootForTests
    {
        get => _liveRootForTests;
        set => _liveRootForTests = value;
    }

    [ThreadStatic] private static string? _liveRootForTests;

    /// <summary>The live root the fence guards.</summary>
    internal static string LiveRoot => LiveRootForTests ?? ForbiddenLiveServerRoot;

    private static readonly object RootFormsGate = new();
    private static string _rootFormsKey = "";
    private static IReadOnlyList<string>? _rootForms;
    private static bool _rootFormsFailed;
    private static long _rootFormsAt;

    /// <summary>
    /// Review M3 (2026-10-08): the live root in every form the fence compares against: the literal text, the target when the
    /// root itself is a junction or symbolic link, and its final path (a link in a parent, an 8.3 spelling). Resolving reads
    /// only the link's metadata (no file under the root is opened or read). Null when the root exists but could not be
    /// resolved: the fence then refuses (fails closed). Reused for 30 s.
    /// </summary>
    internal static IReadOnlyList<string>? LiveRootForms()
    {
        string root = LiveRoot;
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        lock (RootFormsGate)
        {
            if (string.Equals(root, _rootFormsKey, StringComparison.OrdinalIgnoreCase)
                && now - _rootFormsAt < 30 * System.Diagnostics.Stopwatch.Frequency)
                return _rootFormsFailed ? null : _rootForms;
        }

        var forms = new List<string> { PlainFullPath(root) };
        bool failed = false;
        try
        {
            FileAttributes attributes;
            bool exists;
            try
            {
                attributes = File.GetAttributes(root);
                exists = true;
            }
            catch (FileNotFoundException) { attributes = 0; exists = false; }
            catch (DirectoryNotFoundException) { attributes = 0; exists = false; }

            if (exists)
            {
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    var target = new DirectoryInfo(root).ResolveLinkTarget(returnFinalTarget: true);
                    if (target == null) failed = true;
                    else forms.Add(PlainFullPath(target.FullName));
                }

                string? final = FinalPathOfForTests != null ? FinalPathOfForTests(root) : WindowsFiles.FinalPathOf(root);
                if (final != null) forms.Add(PlainFullPath(final));
                else if ((attributes & FileAttributes.ReparsePoint) != 0 && forms.Count < 2) failed = true;
            }
        }
        catch { failed = true; }

        lock (RootFormsGate)
        {
            _rootFormsKey = root;
            _rootForms = forms;
            _rootFormsFailed = failed;
            _rootFormsAt = now;
        }

        return failed ? null : forms;
    }

    /// <summary>Tests: forget the resolved root forms.</summary>
    internal static void ForgetLiveRootForms()
    {
        lock (RootFormsGate) _rootFormsAt = 0;
    }

    /// <summary>
    /// The live-server fence for a path: the text names the live root as a whole token, or the path, as written or in its
    /// final form (<see cref="FinalPath"/>: links, 8.3 names, <c>\\?\</c>, admin shares), is the live root or under it in any of
    /// its forms (<see cref="LiveRootForms"/>: also when the root itself is a junction or symbolic link). A root that
    /// cannot be resolved fails closed.
    /// </summary>
    internal static bool IsLiveServerPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (MentionsLiveServer(path)) return true;
        var roots = LiveRootForms();
        if (roots == null) return true;
        // The text forms first, so a path that names the live root never touches the disk.
        string plain = PlainFullPath(path);
        foreach (string r in roots)
            if (IsUnder(plain, r)) return true;
        string final = FinalPath(path);
        foreach (string r in roots)
            if (IsUnder(final, r)) return true;
        return false;
    }

    /// <summary>The arguments whose next value is a path even without a separator.</summary>
    private static readonly HashSet<string> PathFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        "-persistentDataPath", "-logFile", "-saveDirectory", "-serverDataPath",
    };

    /// <summary>
    /// The fence for a command line: any token names the root, or any argument value (also the value after <c>=</c>, quoted
    /// or not, and a relative path resolved against <paramref name="workingDirectory"/>, review L1) is a path under it.
    /// </summary>
    internal static bool IsLiveServerCommandLine(IReadOnlyList<string>? args, string? workingDirectory = null)
    {
        if (args == null) return false;
        for (int i = 0; i < args.Count; i++)
        {
            string a = args[i] ?? "";
            if (a.Length == 0) continue;
            if (MentionsLiveServer(a)) return true;
            bool pathFlagValue = i > 0 && PathFlags.Contains((args[i - 1] ?? "").Trim());
            if (IsLivePathValue(a, workingDirectory, pathFlagValue)) return true;
            int eq = a.IndexOf('=');
            if (eq > 0 && eq < a.Length - 1
                && IsLivePathValue(a.Substring(eq + 1), workingDirectory, PathFlags.Contains(a.Substring(0, eq).Trim()))) return true;
        }

        return false;
    }

    private static bool IsLivePathValue(string value, string? workingDirectory, bool isPath)
    {
        string v = value.Trim().Trim('"', '\'').Trim();
        if (v.Length == 0) return false;
        bool looksLikePath = isPath || v.Contains('\\') || v.Contains('/') || v.StartsWith(".", StringComparison.Ordinal);
        if (!looksLikePath) return false;
        if (IsLiveServerPath(v)) return true;
        if (!string.IsNullOrWhiteSpace(workingDirectory) && !Path.IsPathRooted(v) && !v.StartsWith(@"\\", StringComparison.Ordinal))
        {
            try { return IsLiveServerPath(Path.Combine(workingDirectory!, v)); }
            catch { return true; }
        }

        return false;
    }

    /// <summary>
    /// A hosted private game's server (the client starts it with <c>-userSave</c>): restores wait for the hosted flow
    /// (spec phase 3).
    /// </summary>
    internal static bool IsHostedGame(IReadOnlyList<string>? args, string? saveFolder)
    {
        if (args != null)
            foreach (string a in args)
                if (string.Equals((a ?? "").TrimStart('-'), "userSave", StringComparison.OrdinalIgnoreCase) && (a ?? "").StartsWith("-", StringComparison.Ordinal))
                    return true;
        return (saveFolder ?? "").IndexOf(@"\CloudSaves\", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    internal const string HostedRefusal = "World restores are not available in a hosted private game yet (only dedicated servers).";

    /// <summary>True when <paramref name="path"/> is <paramref name="root"/> or under it (case-insensitive, full paths).</summary>
    internal static bool IsUnder(string? path, string? root)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(root)) return false;
        try
        {
            string p = Path.GetFullPath(path).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            string r = Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            return p.StartsWith(r, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    internal static bool SamePath(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        try
        {
            return string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>
    /// The save folder a start's command line names: <c>-persistentDataPath</c> \ <c>Saves</c> \ the highest <c>v&lt;N&gt;</c> folder
    /// that holds <c>-saveName</c> (else the highest there, else <see cref="DefaultVersion"/>) \ the save name. The folder
    /// need not exist (it is missing exactly when a restore went wrong). Null without both arguments.
    /// </summary>
    internal static string? SaveFolderFromArgs(IWorldFs fs, IReadOnlyList<string>? args)
    {
        string? persist = Arg(args, "persistentDataPath");
        string? save = Arg(args, "saveName") ?? Arg(args, "serverSaveName");
        if (string.IsNullOrWhiteSpace(persist) || string.IsNullOrWhiteSpace(save)) return null;
        string saves = Path.Combine(persist.Trim().Trim('"'), "Saves");
        string name = save.Trim().Trim('"');
        string? best = null, highest = null;
        int bestN = -1, highestN = -1;
        IReadOnlyList<string> dirs;
        try { dirs = fs.ListDirectories(saves); }
        catch { dirs = Array.Empty<string>(); }
        foreach (string dir in dirs)
        {
            string v = Path.GetFileName(dir);
            if (!VersionFolder.IsMatch(v) || !int.TryParse(v.Substring(1), NumberStyles.None, CultureInfo.InvariantCulture, out int n)) continue;
            if (n > highestN)
            {
                highestN = n;
                highest = dir;
            }

            bool has;
            try { has = fs.DirectoryExists(Path.Combine(dir, name)); }
            catch { has = false; }
            if (has && n > bestN)
            {
                bestN = n;
                best = dir;
            }
        }

        return Path.Combine(best ?? highest ?? Path.Combine(saves, DefaultVersion), name);
    }

    internal const string DefaultVersion = "v4";

    /// <summary>The value after <c>-name</c> (or <c>--name</c>) on a command line, or null.</summary>
    internal static string? Arg(IReadOnlyList<string>? args, string name)
    {
        if (args == null) return null;
        for (int i = 0; i + 1 < args.Count; i++)
        {
            string a = (args[i] ?? "").TrimStart('-');
            if (string.Equals(a, name, StringComparison.OrdinalIgnoreCase) && (args[i] ?? "").StartsWith("-", StringComparison.Ordinal))
                return args[i + 1];
        }

        return null;
    }
}
