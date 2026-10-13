using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Progenitor.Install;

/// <summary>
/// Validation of a user-chosen same-machine dedicated-server folder. Path identity and
/// containment port the resolution rules from <c>tools/common/Fence.ps1</c> (form, aliases,
/// reparse, 8.3). Review H1 (2026-10-08): the folder must not be the live dedicated server, by the
/// same fence the product's other server paths use (<see cref="WorldRestore.WorldRestorePaths.IsLiveServerPath"/>,
/// <see cref="Hosted.HostedPathRules.IsForbiddenLiveServerPath"/>): world saves, staging, the hosted
/// launcher and world restores already refuse it, and installs, stops and starts now do too.
/// </summary>
/// <remarks>
/// Lives under <c>Ui/</c> because this job owns the Configuration/Install workspace files
/// and <c>Install/</c> is frozen. Namespace <see cref="Progenitor.Install"/> so the install
/// planner and the panes share one type without a UI-widget dependency.
/// </remarks>
internal sealed class LocalServerFolder
{
    public const string ServerExecutableName = "VRisingServer.exe";
    public const string ClientExecutableName = "VRising.exe";
    public const string BepInExFolderName = "BepInEx";
    public const string WriteProbeName = ".progenitor-write-probe";

    public const string RefusalEmpty = "No local dedicated-server folder is configured.";
    public const string RefusalUnrecognizedForm =
        "Unrecognised path form. Use a local drive path (D:\\Servers\\VRising) or a local share; "
        + "device, volume-GUID, GLOBALROOT and other non-drive spellings are refused.";
    public const string RefusalRemote =
        "Remote roots are outside this increment. Choose a folder on this computer.";
    public const string RefusalNotDirectory = "That path is not an existing directory.";
    public const string RefusalMissingExe =
        "The folder does not contain VRisingServer.exe, so it is not a dedicated-server install.";
    public const string RefusalClientRoot =
        "That folder is a V Rising client install (VRising.exe), not a dedicated-server folder.";
    public const string RefusalMissingBepInEx =
        "BepInEx is not present in that folder. Install BepInEx on the dedicated server first.";
    public const string RefusalUnwritable =
        "The folder is not writable. Installation needs permission to write under that path.";
    public const string RefusalEscapingLink =
        "A link in that folder resolves outside the registered install. Writes would escape the root.";
    public const string RefusalAmbiguous =
        "Client and server identity is ambiguous for that folder.";
    public const string RefusalLaterInvalid =
        "The configured local server folder is no longer valid. That target is unavailable; its pending changes are blocked.";
    /// <summary>Review H1: the live dedicated server is never a folder Progenitor installs into, stops or starts.</summary>
    public const string RefusalLiveServer =
        "That folder is the live dedicated server. Progenitor never installs into, stops or starts it; choose a test server folder.";

    public const string RefusalWriteEscapes =
        "The installation write would leave the registered local-server folder through a link or path.";

    internal const string UnrecognisedSentinel = "!fence:unrecognised-path-form!";

    /// <summary>Test seam: letter → QueryDosDevice target. Production uses kernel32.</summary>
    internal static Func<char, string?>? DosDeviceLookup { get; set; }

    /// <summary>Test seam: local share name → directory. Production leaves UNC local shares unresolvable (refused).</summary>
    internal static Func<string, string?>? ShareLookup { get; set; }

    /// <summary>Test seam: path → reparse target. Production reads <see cref="FileSystemInfo.LinkTarget"/>.</summary>
    internal static Func<string, string?>? ReparseLookup { get; set; }

    /// <summary>Test seam: directory write probe. Production creates and deletes a probe file.</summary>
    internal static Func<string, bool>? WriteAccessOverride { get; set; }

    private LocalServerFolderResult _current = LocalServerFolderResult.Empty;
    private bool _hadValid;

    public LocalServerFolderResult Current => _current;

    public bool HasValidatedServer => _current.Ok && _current.Available;

    public bool IsUnavailable => _hadValid && (!_current.Ok || !_current.Available);

    public string? FriendlyName => _current.FriendlyName;

    public string? Identity => _current.Identity;

    public static LocalServerFolderResult Validate(string? path) => ValidateCore(path, requireDirectory: true);

    /// <summary>Review H1: the live-server fence for a folder (the text forms first, so it rarely touches the disk).</summary>
    internal static bool IsLiveServerFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try { return WorldRestore.WorldRestorePaths.IsLiveServerPath(path) || Hosted.HostedPathRules.IsForbiddenLiveServerPath(path); }
        catch { return true; }
    }

    /// <summary>
    /// Same layout rules as <see cref="Validate"/> without the write probe, so discovery
    /// can offer a folder without creating a file in it.
    /// </summary>
    public static LocalServerFolderResult ValidateSuggestion(string? path) =>
        ValidateCore(path, requireDirectory: true, probeWrite: false);

    public LocalServerFolderResult Apply(string? path)
    {
        if (NormalizeTyped(path).Length == 0)
        {
            Clear();
            return _current;
        }

        var result = Validate(path);
        if (!result.Ok)
            return result;

        _current = result;
        _hadValid = true;
        return result;
    }

    public void Clear()
    {
        _current = LocalServerFolderResult.Empty;
        _hadValid = false;
    }

    /// <summary>Re-check the stored folder. A later-invalid folder becomes unavailable and does not fall back.</summary>
    public LocalServerFolderResult Revalidate()
    {
        if (!_hadValid && string.IsNullOrEmpty(_current.CanonicalPath))
            return _current;

        string stored = _current.CanonicalPath ?? _current.TypedPath ?? "";
        var now = Validate(stored);
        if (now.Ok && now.Available)
        {
            _current = now;
            _hadValid = true;
            return now;
        }

        _current = LocalServerFolderResult.Unavailable(
            stored,
            _current.FriendlyName,
            _current.Identity,
            now.Refusal ?? RefusalLaterInvalid);
        return _current;
    }

    /// <summary>Call before any install write into this folder.</summary>
    public bool CanWrite(out string refusal)
    {
        var now = Revalidate();
        if (!now.Ok || !now.Available)
        {
            refusal = now.Refusal ?? RefusalLaterInvalid;
            return false;
        }

        refusal = "";
        return true;
    }

    public bool TryContainWrite(string destination, out string refusal)
    {
        if (!CanWrite(out refusal)) return false;
        string root = _current.CanonicalPath ?? "";
        string destCanon = Canonicalize(destination);
        if (destCanon == UnrecognisedSentinel)
        {
            refusal = RefusalWriteEscapes;
            return false;
        }

        if (!IsUnderOrEqual(destCanon, root))
        {
            refusal = RefusalWriteEscapes;
            return false;
        }

        refusal = "";
        return true;
    }

    internal static LocalServerFolderResult ValidateCore(string? path, bool requireDirectory) =>
        ValidateCore(path, requireDirectory, probeWrite: true);

    /// <summary>
    /// CONF1: the text a field, the config file or the bridge hands over, made into a plain
    /// path. Strips invisible marks (U+200B/U+200C/U+200D/U+2060/U+FEFF — the caret/placeholder
    /// marker an input field may echo back), surrounding whitespace and quotes, expands
    /// <c>%VAR%</c> and a leading <c>~</c>. Never touches the file system; an empty result means
    /// "nothing configured".
    /// </summary>
    public static string NormalizeTyped(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        var sb = new StringBuilder(path.Length);
        for (int i = 0; i < path.Length; i++)
        {
            char c = path[i];
            if (c == '​' || c == '‌' || c == '‍' || c == '⁠' || c == '﻿' || c == '\0')
                continue;
            sb.Append(c);
        }

        string p = sb.ToString().Trim().Trim('"', '\'').Trim();
        if (p.Length == 0) return "";
        try { p = Environment.ExpandEnvironmentVariables(p); }
        catch { /* keep the literal */ }
        if (p == "~" || p.StartsWith("~\\", StringComparison.Ordinal) || p.StartsWith("~/", StringComparison.Ordinal))
        {
            string home = "";
            try { home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); }
            catch { /* no profile */ }
            if (!string.IsNullOrEmpty(home)) p = home + p.Substring(1);
        }

        return p.Trim();
    }

    internal static LocalServerFolderResult ValidateCore(string? path, bool requireDirectory, bool probeWrite)
    {
        string typed = NormalizeTyped(path);
        if (typed.Length == 0)
            return LocalServerFolderResult.Refuse(RefusalEmpty, path);

        string canon = Canonicalize(typed);
        if (canon == UnrecognisedSentinel)
            return LocalServerFolderResult.Refuse(RefusalUnrecognizedForm, typed);

        if (IsRemote(canon))
            return LocalServerFolderResult.Refuse(RefusalRemote, typed);

        if (IsLiveServerFolder(typed) || IsLiveServerFolder(canon))
            return LocalServerFolderResult.Refuse(RefusalLiveServer, typed);

        if (requireDirectory && !Directory.Exists(canon))
            return LocalServerFolderResult.Refuse(RefusalNotDirectory, typed);

        string friendly = Path.GetFileName(canon.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(friendly)) friendly = canon;

        string exe = Path.Combine(canon, ServerExecutableName);
        string clientExe = Path.Combine(canon, ClientExecutableName);
        bool hasServerExe = File.Exists(exe);
        bool hasClientExe = File.Exists(clientExe);

        if (hasClientExe && hasServerExe)
            return LocalServerFolderResult.Refuse(RefusalAmbiguous, typed);
        if (hasClientExe)
            return LocalServerFolderResult.Refuse(RefusalClientRoot, typed);
        if (!hasServerExe)
            return LocalServerFolderResult.Refuse(RefusalMissingExe, typed);

        string exeCanon = Canonicalize(exe);
        if (exeCanon == UnrecognisedSentinel || !IsUnderOrEqual(ParentDirectory(exeCanon), canon))
            return LocalServerFolderResult.Refuse(RefusalEscapingLink, typed);

        string bep = Path.Combine(canon, BepInExFolderName);
        if (!Directory.Exists(bep))
            return LocalServerFolderResult.Refuse(RefusalMissingBepInEx, typed);

        string bepCanon = Canonicalize(bep);
        if (bepCanon == UnrecognisedSentinel || !IsUnderOrEqual(bepCanon, canon))
            return LocalServerFolderResult.Refuse(RefusalEscapingLink, typed);

        if (probeWrite && !HasWriteAccess(canon))
            return LocalServerFolderResult.Refuse(RefusalUnwritable, typed);

        string identity = canon;
        return LocalServerFolderResult.Valid(typed, canon, friendly, identity);
    }

    internal static string Canonicalize(string? path) => Canonicalize(path, 0);

    internal static string Canonicalize(string? path, int depth)
    {
        if (string.IsNullOrWhiteSpace(path)) return UnrecognisedSentinel;
        if (depth >= 16) return UnrecognisedSentinel;
        try
        {
            string full = NormalizeForm(path);
            if (full == UnrecognisedSentinel) return UnrecognisedSentinel;
            full = ResolveDefinitions(full, depth);
            if (full == UnrecognisedSentinel) return UnrecognisedSentinel;
            return FollowExisting(full, depth);
        }
        catch
        {
            return UnrecognisedSentinel;
        }
    }

    internal static string NormalizeForm(string path)
    {
        string p = path.Trim().Trim('"').Replace('/', '\\');
        if (p.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)
            || p.StartsWith(@"\\.\UNC\", StringComparison.OrdinalIgnoreCase))
            p = @"\\" + p.Substring(8);
        else if (p.StartsWith(@"\\?\", StringComparison.Ordinal) || p.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            string after = p.Substring(4);
            if (after.Length < 2 || !char.IsLetter(after[0]) || after[1] != ':')
                return UnrecognisedSentinel;
            p = after;
        }

        var admin = Regex.Match(p, @"^\\\\(?<host>[^\\]+)\\(?<drive>[A-Za-z])\$(?<rest>\\.*)?$");
        if (admin.Success && IsLocalHost(admin.Groups["host"].Value))
            p = admin.Groups["drive"].Value + ":" + admin.Groups["rest"].Value;

        try { p = Path.GetFullPath(p); }
        catch { return UnrecognisedSentinel; }

        if (!Regex.IsMatch(p, @"^[A-Za-z]:\\") && !Regex.IsMatch(p, @"^\\\\[^\\]+\\[^\\]+"))
            return UnrecognisedSentinel;
        return TrimSep(p);
    }

    private static string ResolveDefinitions(string path, int depth)
    {
        if (depth >= 16) return UnrecognisedSentinel;
        var drive = Regex.Match(path, @"^(?<letter>[A-Za-z]):(?<rest>\\.*)?$");
        if (drive.Success)
        {
            char letter = drive.Groups["letter"].Value[0];
            string rest = drive.Groups["rest"].Value;
            string? target = DosDeviceLookup != null ? DosDeviceLookup(letter) : QueryDosDevice(letter);
            if (!string.IsNullOrEmpty(target))
            {
                var unc = Regex.Match(target, @"^\\\?\?\\UNC\\(?<unc>.+)$");
                if (unc.Success)
                    return Canonicalize(@"\\" + unc.Groups["unc"].Value.TrimEnd('\\') + rest, depth + 1);
                var subst = Regex.Match(target, @"^\\\?\?\\(?<sub>[A-Za-z]:.*)$");
                if (subst.Success)
                    return Canonicalize(subst.Groups["sub"].Value.TrimEnd('\\') + rest, depth + 1);
                if (Regex.IsMatch(target, @"^\\Device\\(LanmanRedirector|Mup)(\\|$)", RegexOptions.IgnoreCase))
                    return UnrecognisedSentinel;
                if (Regex.IsMatch(target, @"^\\Device\\(HarddiskVolume|Harddisk|Volume|CdRom|Floppy|RamDisk)", RegexOptions.IgnoreCase))
                    return path;
                return UnrecognisedSentinel;
            }

            try
            {
                var info = new DriveInfo(letter + ":");
                if (info.DriveType == DriveType.Network || info.DriveType == DriveType.NoRootDirectory)
                    return UnrecognisedSentinel;
            }
            catch { /* letter with no volume: leave as-is */ }

            return path;
        }

        var share = Regex.Match(path, @"^\\\\(?<server>[^\\]+)\\(?<share>[^\\]+)(?<rest>\\.*)?$");
        if (share.Success)
        {
            if (!IsLocalHost(share.Groups["server"].Value))
                return path;
            string? mapped = ShareLookup?.Invoke(share.Groups["share"].Value);
            if (string.IsNullOrEmpty(mapped)) return UnrecognisedSentinel;
            return Canonicalize(mapped.TrimEnd('\\') + share.Groups["rest"].Value, depth + 1);
        }

        return path;
    }

    private static string FollowExisting(string full, int depth)
    {
        if (depth >= 16) return full;
        string? root;
        try { root = Path.GetPathRoot(full); }
        catch { return full; }
        if (string.IsNullOrEmpty(root)) return full;

        string rest = full.Length > root.Length ? full.Substring(root.Length) : "";
        string[] segments = rest.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
        string current = TrimSep(root);

        for (int i = 0; i < segments.Length; i++)
        {
            string seg = segments[i];
            string parent = current;
            if (parent.Length == 2 && parent[1] == ':') parent += "\\";
            string candidate = Path.Combine(parent, seg);

            if (seg.IndexOf('~') >= 0)
            {
                try
                {
                    string[] hits = Directory.GetFileSystemEntries(parent, seg);
                    if (hits.Length == 1) candidate = hits[0];
                }
                catch { /* keep candidate */ }
            }

            string? link = ReadReparseTarget(candidate);
            if (!string.IsNullOrEmpty(link))
            {
                if (!Path.IsPathRooted(link))
                    link = Path.Combine(parent, link);
                string followed = Canonicalize(link, depth + 1);
                if (followed == UnrecognisedSentinel) return UnrecognisedSentinel;
                if (i == segments.Length - 1) return followed;
                string remainder = string.Join("\\", segments, i + 1, segments.Length - i - 1);
                return Canonicalize(Path.Combine(followed, remainder), depth + 1);
            }

            current = candidate;
        }

        return TrimSep(current);
    }

    private static string? ReadReparseTarget(string path)
    {
        if (ReparseLookup != null) return ReparseLookup(path);
        try
        {
            FileSystemInfo info = Directory.Exists(path)
                ? new DirectoryInfo(path)
                : File.Exists(path) ? new FileInfo(path) : null!;
            if (info == null) return null;
            if ((info.Attributes & FileAttributes.ReparsePoint) == 0) return null;
            return string.IsNullOrWhiteSpace(info.LinkTarget) ? null : info.LinkTarget;
        }
        catch
        {
            return null;
        }
    }

    private static bool HasWriteAccess(string directory)
    {
        if (WriteAccessOverride != null) return WriteAccessOverride(directory);
        string probe = Path.Combine(directory, WriteProbeName);
        try
        {
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch
        {
            try { if (File.Exists(probe)) File.Delete(probe); }
            catch { /* */ }
            return false;
        }
    }

    private static bool IsRemote(string canon)
    {
        if (canon.StartsWith(@"\\", StringComparison.Ordinal))
        {
            var m = Regex.Match(canon, @"^\\\\(?<server>[^\\]+)\\");
            if (m.Success && !IsLocalHost(m.Groups["server"].Value))
                return true;
        }

        try
        {
            var root = Path.GetPathRoot(canon);
            if (!string.IsNullOrEmpty(root) && root.Length >= 2 && root[1] == ':')
            {
                var info = new DriveInfo(root);
                if (info.DriveType == DriveType.Network) return true;
            }
        }
        catch { /* */ }

        return false;
    }

    private static bool IsLocalHost(string host)
    {
        if (string.IsNullOrEmpty(host)) return false;
        if (host is "." or "localhost" or "127.0.0.1" or "::1") return true;
        return string.Equals(host, Environment.MachineName, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsUnderOrEqual(string path, string root)
    {
        string p = TrimSep(path);
        string r = TrimSep(root);
        if (string.Equals(p, r, StringComparison.OrdinalIgnoreCase)) return true;
        return p.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase);
    }

    private static string TrimSep(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        if (path.Length == 3 && path[1] == ':') return path;
        return path.TrimEnd('\\', '/');
    }

    private static string ParentDirectory(string path)
    {
        string? parent = Path.GetDirectoryName(path);
        return string.IsNullOrEmpty(parent) ? path : parent;
    }

    private static string? QueryDosDevice(char letter)
    {
        try
        {
            var buffer = new StringBuilder(2048);
            uint written = NativeQueryDosDevice(letter + ":", buffer, (uint)buffer.Capacity);
            if (written == 0) return null;
            string target = buffer.ToString();
            return string.IsNullOrWhiteSpace(target) ? null : target;
        }
        catch
        {
            return null;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "QueryDosDeviceW", SetLastError = true)]
    private static extern uint NativeQueryDosDevice(string lpDeviceName, StringBuilder lpTargetPath, uint ucchMax);
}

internal readonly struct LocalServerFolderResult
{
    private LocalServerFolderResult(
        bool ok,
        bool available,
        string? typedPath,
        string? canonicalPath,
        string? friendlyName,
        string? identity,
        string? refusal)
    {
        Ok = ok;
        Available = available;
        TypedPath = typedPath;
        CanonicalPath = canonicalPath;
        FriendlyName = friendlyName;
        Identity = identity;
        Refusal = refusal;
    }

    public bool Ok { get; }
    public bool Available { get; }
    public string? TypedPath { get; }
    public string? CanonicalPath { get; }
    public string? FriendlyName { get; }
    public string? Identity { get; }
    public string? Refusal { get; }

    public static LocalServerFolderResult Empty { get; } =
        new(false, false, null, null, null, null, LocalServerFolder.RefusalEmpty);

    public static LocalServerFolderResult Valid(string typed, string canon, string friendly, string identity) =>
        new(true, true, typed, canon, friendly, identity, null);

    public static LocalServerFolderResult Refuse(string refusal, string? typed) =>
        new(false, false, typed, null, null, null, refusal);

    public static LocalServerFolderResult Unavailable(string path, string? friendly, string? identity, string refusal) =>
        new(false, false, path, path, friendly, identity, refusal);
}
