using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Progenitor.Install;

/// <summary>What <see cref="AdminListFile.Add"/> did.</summary>
internal enum AdminListEditKind
{
    Added = 0,
    AlreadyPresent = 1,
    Refused = 2,
}

/// <summary>
/// The outcome of an <c>Add me</c>: what happened, why (for a refusal), the file written or
/// inspected, and the backup copy taken before the write (empty when the file was created).
/// </summary>
internal sealed class AdminListEditResult
{
    public AdminListEditResult(AdminListEditKind kind, ulong steamId, string reason, string path, string backupPath)
    {
        Kind = kind;
        SteamId = steamId;
        Reason = reason ?? "";
        Path = path ?? "";
        BackupPath = backupPath ?? "";
    }

    public AdminListEditKind Kind { get; }
    public ulong SteamId { get; }

    /// <summary>Empty unless <see cref="Kind"/> is <see cref="AdminListEditKind.Refused"/>.</summary>
    public string Reason { get; }

    /// <summary>The adminlist.txt written (Added) or found to hold the ID (AlreadyPresent); empty when none was chosen.</summary>
    public string Path { get; }

    /// <summary>The pre-edit copy, or empty when nothing existed before the write.</summary>
    public string BackupPath { get; }

    /// <summary>The line the pane shows under the row for this outcome.</summary>
    public string Line => Kind switch
    {
        AdminListEditKind.Added => AdminListFile.AddedLine(SteamId),
        AdminListEditKind.AlreadyPresent => AdminListFile.AlreadyPresentLine(SteamId),
        _ => AdminListFile.RefusedLine(Reason),
    };
}

/// <summary>
/// Where the validated local server's <c>adminlist.txt</c> is, the order it is looked for in,
/// and which file an <c>Add me</c> writes.
/// </summary>
internal sealed class AdminListLocation
{
    public AdminListLocation(IReadOnlyList<string> candidates, string? persistentCandidate, string? existing)
    {
        Candidates = candidates;
        PersistentCandidate = persistentCandidate;
        Existing = existing;
    }

    /// <summary>Every place looked, in priority order (see <see cref="AdminListFile.Candidates"/>).</summary>
    public IReadOnlyList<string> Candidates { get; }

    /// <summary>The first <c>-persistentDataPath</c> candidate, or null when no persistentDataPath is known.</summary>
    public string? PersistentCandidate { get; }

    /// <summary>The first candidate that exists on disk, or null.</summary>
    public string? Existing { get; }

    /// <summary>
    /// The file an edit writes: the first existing candidate, else the persistentDataPath
    /// candidate (created), else null — no location is ever invented.
    /// </summary>
    public string? Target => Existing ?? PersistentCandidate;
}

/// <summary>
/// The "Add me" helper for the configured local dedicated server's admin list. Game-free: the
/// tests project links this file, so nothing here may reference the game.
///
/// <para><b>Server behavior (dump research, not live-verified).</b> <c>ProjectM.FileUserList</c>
/// holds a default file (install <c>VRisingServer_Data\StreamingAssets\Settings\adminlist.txt</c>)
/// and a local file (<c>&lt;persistentDataPath&gt;\Settings\adminlist.txt</c>); the local copy most
/// likely wins and is what its <c>Save()</c> writes. <c>ProjectM.AdminAuthSystem</c> owns the
/// <c>FileUserList _LocalAdminList</c>; <c>FileUserList</c> has <c>Refresh/RefreshLocal/
/// IsTimeForRefresh</c> and a <c>minRefreshTime = 60</c> constructor default and the server dump
/// has no file watcher, so a running server probably re-reads the list about once a minute.</para>
///
/// <para>LIVE CHECK PENDING: does a running server pick up an edited adminlist.txt, and how soon?
/// The after-add wording below hedges ("up to a minute, or a restart") until that is observed.
/// Being on the list does not make a player admin by itself: the player still runs
/// <c>adminauth</c> in the console each session, after which <c>User.IsAdmin</c> is true.</para>
/// </summary>
internal static class AdminListFile
{
    public const string FileName = "adminlist.txt";
    public const string SettingsFolderName = "Settings";

    /// <summary>The pre-edit copy beside the list, overwritten by each edit.</summary>
    public const string BackupSuffix = ".progenitor-bak";

    /// <summary>A SteamID64 for an individual account is 17 digits starting 7656119.</summary>
    public const string SteamIdPrefix = "7656119";
    public const int SteamIdDigits = 17;

    // ---- user-facing strings: constants so wording can change after the live check ----

    public const string NotOnListNote = "You're not on this server's admin list";
    public const string AddMeButton = "Add me";
    public const string AddMeHelp =
        "Add your SteamID to the configured server's adminlist.txt. Remote installs read that file directly; the game's admin commands still need adminauth in the console.";

    public const string ReasonInvalidSteamId = "Not a SteamID64 (17 digits starting 7656119).";
    public const string ReasonNoFolder = "No validated local server folder.";
    public const string ReasonNoLocation =
        "No adminlist.txt was found, and the server's -persistentDataPath is unknown, so there is no known place to create one. Start the server from its folder once (or add yourself by hand) and try again.";
    public const string ReasonUnsupportedEncoding = "adminlist.txt is not UTF-8 text; it was left unchanged.";
    public const string ReasonWriteFailedPrefix = "Could not write adminlist.txt: ";

    /// <summary>Under the button: which ID will be added, and where.</summary>
    public static string WillAddLine(ulong steamId, string path) =>
        "Adds SteamID " + Format(steamId) + " to " + (path ?? "");

    /// <summary>
    /// After a successful add. Honest about the two unknowns: the list only permits
    /// <c>adminauth</c>, and a running server's pickup delay is unconfirmed (live check pending).
    /// </summary>
    public static string AddedLine(ulong steamId) =>
        "Added " + Format(steamId) + ". Remote installs work right away; admin commands still need adminauth "
        + "(a running server may need up to a minute, or a restart, before adminauth accepts you).";

    public static string AlreadyPresentLine(ulong steamId) =>
        "SteamID " + Format(steamId) + " is already on this server's admin list.";

    public static string RefusedLine(string reason) => "Not added: " + (reason ?? "");

    // ---- this player's SteamID ----

    /// <summary>
    /// This client's own SteamID64, or 0 when unknown. No proven client-side read exists yet (no
    /// native call or new interop instantiation may be added for it), so production leaves this
    /// null and the helper stays hidden. Tests and a later proven source set it.
    /// </summary>
    internal static Func<ulong>? LocalSteamIdProbe { get; set; }

    /// <summary>The probe's answer, or 0 when there is none or it threw.</summary>
    public static ulong LocalSteamId()
    {
        var probe = LocalSteamIdProbe;
        if (probe == null) return 0;
        try { return probe(); }
        catch { return 0; }
    }

    internal static void ResetForTests() => LocalSteamIdProbe = null;

    public static string Format(ulong steamId) => steamId.ToString(CultureInfo.InvariantCulture);

    public static bool IsPlausibleSteamId(ulong steamId)
    {
        string s = Format(steamId);
        return s.Length == SteamIdDigits && s.StartsWith(SteamIdPrefix, StringComparison.Ordinal);
    }

    // ---- location ----

    /// <summary>
    /// The places <c>adminlist.txt</c> is looked for, highest priority first:
    /// <list type="number">
    /// <item><c>&lt;persistentDataPath&gt;\Settings\adminlist.txt</c> from the running server's own
    /// command line (<see cref="ServerProcessControl.Running"/>);</item>
    /// <item>the same from the launch record's arguments (<see cref="ServerLaunchRecord.TryRead"/>);</item>
    /// <item><c>&lt;root&gt;\Settings\adminlist.txt</c>, only when <c>&lt;root&gt;\Settings</c> exists;</item>
    /// <item><c>&lt;root&gt;\VRisingServer_Data\StreamingAssets\Settings\adminlist.txt</c> (the install default).</item>
    /// </list>
    /// A relative <c>-persistentDataPath</c> is taken against that launch's working directory (or
    /// the server root when unknown). The game's own default persistentDataPath for a launch
    /// without the flag (under the user's AppData LocalLow) is not guessed.
    /// </summary>
    public static IReadOnlyList<string> Candidates(string serverRoot, out string? persistentCandidate)
    {
        var list = new List<string>();
        persistentCandidate = null;
        try
        {
            var live = ServerProcessControl.Running(serverRoot);
            if (live != null)
                AddPersistent(list, ServerLaunchRecord.SplitCommandLine(live.CommandLine), 1, live.WorkingDirectory, serverRoot);
        }
        catch { /* the probe is best effort; the record and folder candidates follow */ }

        try
        {
            var record = ServerLaunchRecord.TryRead(serverRoot);
            if (record != null) AddPersistent(list, record.Args, 0, record.WorkingDirectory, serverRoot);
        }
        catch { /* an unreadable record is "no record" */ }

        if (list.Count > 0) persistentCandidate = list[0];

        string rootSettings = Path.Combine(serverRoot, SettingsFolderName);
        if (Directory.Exists(rootSettings)) AddUnique(list, Path.Combine(rootSettings, FileName));
        AddUnique(list, Path.Combine(serverRoot, "VRisingServer_Data", "StreamingAssets", SettingsFolderName, FileName));
        return list;
    }

    public static AdminListLocation Locate(string serverRoot)
    {
        var candidates = Candidates(serverRoot, out string? persistent);
        string? existing = null;
        for (int i = 0; i < candidates.Count; i++)
        {
            if (File.Exists(candidates[i])) { existing = candidates[i]; break; }
        }

        return new AdminListLocation(candidates, persistent, existing);
    }

    private static void AddPersistent(List<string> list, IReadOnlyList<string> args, int start, string? workingDirectory, string serverRoot)
    {
        for (int i = start; i + 1 < args.Count; i++)
        {
            if (!string.Equals(args[i], "-persistentDataPath", StringComparison.OrdinalIgnoreCase)) continue;
            string value = (args[i + 1] ?? "").Trim();
            if (value.Length == 0) continue;
            try
            {
                if (!Path.IsPathRooted(value))
                {
                    string baseDir = string.IsNullOrWhiteSpace(workingDirectory) ? serverRoot : workingDirectory!;
                    value = Path.GetFullPath(Path.Combine(baseDir, value));
                }

                AddUnique(list, Path.Combine(value, SettingsFolderName, FileName));
            }
            catch { /* an unusable path is not a candidate */ }
        }
    }

    private static void AddUnique(List<string> list, string candidate)
    {
        if (!list.Exists(c => string.Equals(c, candidate, StringComparison.OrdinalIgnoreCase))) list.Add(candidate);
    }

    // ---- reading ----

    /// <summary>
    /// True when a line of <paramref name="text"/>, trimmed, is exactly the ID. Blank lines and
    /// lines starting with <c>#</c> or <c>//</c> never match.
    /// </summary>
    public static bool Contains(string? text, ulong steamId)
    {
        if (string.IsNullOrEmpty(text)) return false;
        string id = Format(steamId);
        string body = text![0] == '﻿' ? text.Substring(1) : text;
        foreach (string raw in body.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal) || line.StartsWith("//", StringComparison.Ordinal))
                continue;
            if (string.Equals(line, id, StringComparison.Ordinal)) return true;
        }

        return false;
    }

    /// <summary>
    /// The pane's decision: true when a plausible ID is known, a server folder is set, and the
    /// list the server would read (<see cref="AdminListLocation.Existing"/>) does not hold it.
    /// No file anywhere counts as "not on the list".
    /// </summary>
    public static bool NeedsAdd(string? serverRoot, ulong steamId, out AdminListLocation? location)
    {
        location = null;
        if (string.IsNullOrWhiteSpace(serverRoot) || !IsPlausibleSteamId(steamId)) return false;
        location = Locate(serverRoot!);
        if (location.Existing == null) return true;
        try { return !Contains(Decode(File.ReadAllBytes(location.Existing)), steamId); }
        catch { return true; }
    }

    // ---- editing ----

    /// <summary>
    /// Add <paramref name="steamId"/> to the validated server's list: the first existing candidate
    /// is edited; with none, the file is created at the persistentDataPath candidate; with no
    /// persistentDataPath known, the add is refused.
    /// </summary>
    public static AdminListEditResult Add(string? serverRoot, ulong steamId)
    {
        if (!IsPlausibleSteamId(steamId)) return Refuse(steamId, ReasonInvalidSteamId, "");
        if (string.IsNullOrWhiteSpace(serverRoot)) return Refuse(steamId, ReasonNoFolder, "");
        var location = Locate(serverRoot!);
        string? target = location.Target;
        if (target == null) return Refuse(steamId, ReasonNoLocation, "");
        return AddToFile(target, steamId);
    }

    /// <summary>
    /// Append the ID to <paramref name="path"/>: every existing byte is kept, the file's line-ending
    /// style is kept, a missing final newline is added first, and nothing is written when the ID is
    /// already there. A backup of the pre-edit bytes is written before the replace; the replace is
    /// a temp file in the same folder moved over the original.
    /// </summary>
    public static AdminListEditResult AddToFile(string path, ulong steamId)
    {
        if (!IsPlausibleSteamId(steamId)) return Refuse(steamId, ReasonInvalidSteamId, path);
        string backup = "";
        try
        {
            bool existed = File.Exists(path);
            byte[] before = existed ? File.ReadAllBytes(path) : Array.Empty<byte>();
            if (IsUtf16(before)) return Refuse(steamId, ReasonUnsupportedEncoding, path);
            if (Contains(Decode(before), steamId))
                return new AdminListEditResult(AdminListEditKind.AlreadyPresent, steamId, "", path, "");

            byte[] after = Append(before, steamId);
            if (existed)
            {
                backup = path + BackupSuffix;
                File.Copy(path, backup, overwrite: true);
            }

            WriteAtomic(path, after);
            return new AdminListEditResult(AdminListEditKind.Added, steamId, "", path, backup);
        }
        catch (Exception e)
        {
            return Refuse(steamId, ReasonWriteFailedPrefix + e.Message, path, backup);
        }
    }

    /// <summary>
    /// The bytes after the append. CRLF when the file already uses CRLF (or has no line break yet),
    /// LF when it uses bare LF. A UTF-8 byte-order mark alone counts as empty.
    /// </summary>
    public static byte[] Append(byte[] before, ulong steamId)
    {
        before ??= Array.Empty<byte>();
        string newline = NewlineOf(before);
        int bom = HasUtf8Bom(before) ? 3 : 0;
        var output = new List<byte>(before.Length + SteamIdDigits + 4);
        output.AddRange(before);
        if (before.Length > bom && before[before.Length - 1] != (byte)'\n')
            output.AddRange(Encoding.ASCII.GetBytes(newline));
        output.AddRange(Encoding.ASCII.GetBytes(Format(steamId) + newline));
        return output.ToArray();
    }

    /// <summary>"\r\n" when the first line break is CRLF or there is none; "\n" when it is a bare LF.</summary>
    public static string NewlineOf(byte[] bytes)
    {
        for (int i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] != (byte)'\n') continue;
            return i > 0 && bytes[i - 1] == (byte)'\r' ? "\r\n" : "\n";
        }

        return "\r\n";
    }

    private static void WriteAtomic(string path, byte[] bytes)
    {
        string dir = Path.GetDirectoryName(path) ?? "";
        if (dir.Length > 0) Directory.CreateDirectory(dir);
        string temp = Path.Combine(dir, FileName + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllBytes(temp, bytes);
            if (File.Exists(path))
            {
                try
                {
                    File.Replace(temp, path, null);
                }
                catch (Exception e) when (e is PlatformNotSupportedException || e is IOException)
                {
                    // Some volumes refuse File.Replace; the backup above already holds the old bytes.
                    File.Copy(temp, path, overwrite: true);
                }
            }
            else
            {
                File.Move(temp, path);
            }
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch { /* best effort */ }
        }
    }

    private static string Decode(byte[] bytes)
    {
        int start = HasUtf8Bom(bytes) ? 3 : 0;
        return Encoding.UTF8.GetString(bytes, start, bytes.Length - start);
    }

    private static bool HasUtf8Bom(byte[] b) => b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF;

    private static bool IsUtf16(byte[] b) =>
        b.Length >= 2 && ((b[0] == 0xFF && b[1] == 0xFE) || (b[0] == 0xFE && b[1] == 0xFF));

    private static AdminListEditResult Refuse(ulong steamId, string reason, string path, string backup = "") =>
        new(AdminListEditKind.Refused, steamId, reason, path, backup);
}
