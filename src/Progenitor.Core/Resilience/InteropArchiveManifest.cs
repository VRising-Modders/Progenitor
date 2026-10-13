using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace Progenitor.Resilience;

/// <summary>One file inside an archived interop set: where it sits, how long it is, what it hashes to.</summary>
public sealed class InteropArchiveFile
{
    public InteropArchiveFile(string path, long length, string sha256)
    {
        Path = path ?? "";
        Length = length;
        Sha256 = sha256 ?? "";
    }

    /// <summary>Path relative to the archive folder, always with <c>/</c> separators.</summary>
    public string Path { get; }

    public long Length { get; }

    /// <summary>Lowercase-hex SHA-256, or "" when the manifest was written without hashes.</summary>
    public string Sha256 { get; }
}

/// <summary>
/// Job FUPD6 (R53 finding 5). <b>The file that makes an archive able to say it is complete.</b>
///
/// <para>FUPD4 published an archive with a single <c>Directory.Move</c> and then trusted the
/// name. That is only safe against a crash. It is not safe against a second process: the
/// documented two-instance workflows (the test server plus a hosted child, or
/// <c>tools/extra-client.ps1</c> beside the Steam client) share one archive root, and FUPD4's
/// sweep deleted any other process's in-flight temp copy out from under it. The loser of that
/// race could still rename a subtree-deleted copy into the final name, and every later run
/// reported <c>AlreadyPresent</c> without ever counting the files — so a truncated set would be
/// handed to <c>tools/driftmap --old</c> as the previous build, months later, with the real
/// previous build long since overwritten in place by BepInEx.</para>
///
/// <para>So an archive now carries a manifest: a header (side, build, when, which process) and
/// one line per file with its length and SHA-256. The manifest is written into the temp folder
/// <b>after</b> the copy is verified against the source and <b>before</b> the rename, so it is
/// published atomically with the set it describes. <see cref="Verify"/> reads it back cheaply
/// (presence and length, not the hashes) on every <c>AlreadyPresent</c> pass; the hashes are for
/// <c>driftmap</c> and for an operator who wants to prove a set byte for byte later. An archive
/// with no valid manifest is <see cref="InteropArchiveStatus.Incomplete"/>: it is reported, and
/// it is never offered as an <c>--old</c> input.</para>
///
/// <para>Game-free, like the rest of <c>Resilience/</c>.</para>
/// </summary>
public sealed class InteropArchiveManifest
{
    /// <summary>The manifest's file name inside the archive folder.</summary>
    public const string FileName = "progenitor-archive-manifest.txt";

    /// <summary>First token of the first line. Bumped only if the format stops being readable.</summary>
    public const string Magic = "progenitor-interop-archive";

    /// <summary>Format version this code writes.</summary>
    public const int CurrentVersion = 1;

    /// <summary>The line that separates the header from the file list.</summary>
    public const string Separator = "--";

    public InteropArchiveManifest(
        int version,
        string side,
        string build,
        string createdUtc,
        int pid,
        IReadOnlyList<InteropArchiveFile> files)
    {
        Version = version;
        Side = side ?? "";
        Build = build ?? "";
        CreatedUtc = createdUtc ?? "";
        Pid = pid;
        Files = files ?? Array.Empty<InteropArchiveFile>();
        long total = 0;
        foreach (var file in Files) total += file.Length;
        Bytes = total;
    }

    public int Version { get; }
    public string Side { get; }
    public string Build { get; }
    public string CreatedUtc { get; }

    /// <summary>The process that wrote it. Only ever evidence in a log; nothing branches on it.</summary>
    public int Pid { get; }

    public IReadOnlyList<InteropArchiveFile> Files { get; }
    public long Bytes { get; }

    /// <summary>Round-trip text. One file per line, sorted by the caller, so a diff of two is readable.</summary>
    public string Format()
    {
        var sb = new StringBuilder();
        sb.Append(Magic).Append(' ').Append(Version.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("side=").Append(Side).Append('\n');
        sb.Append("build=").Append(Build).Append('\n');
        sb.Append("createdUtc=").Append(CreatedUtc).Append('\n');
        sb.Append("pid=").Append(Pid.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("files=").Append(Files.Count.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("bytes=").Append(Bytes.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append(Separator).Append('\n');
        foreach (var file in Files)
        {
            sb.Append(file.Sha256.Length == 0 ? "-" : file.Sha256)
              .Append(' ')
              .Append(file.Length.ToString(CultureInfo.InvariantCulture))
              .Append(' ')
              .Append(file.Path)
              .Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>
    /// Parse manifest text. Never throws. A malformed manifest is a false return with a reason,
    /// which becomes "this archive is incomplete" — the deliberately conservative reading, because
    /// the cost of trusting a bad archive is a silently wrong drift map months later.
    /// </summary>
    public static bool TryParse(string? text, out InteropArchiveManifest? manifest, out string error)
    {
        manifest = null;
        error = "";
        if (string.IsNullOrEmpty(text)) { error = "the manifest is empty"; return false; }

        var lines = text!.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        if (lines.Length == 0) { error = "the manifest is empty"; return false; }

        string first = lines[0].Trim();
        if (!first.StartsWith(Magic, StringComparison.Ordinal))
        {
            error = "the manifest does not start with " + Magic;
            return false;
        }
        int version = 0;
        string versionText = first.Substring(Magic.Length).Trim();
        if (!int.TryParse(versionText, NumberStyles.Integer, CultureInfo.InvariantCulture, out version)
            || version <= 0)
        {
            error = "unreadable manifest version '" + versionText + "'";
            return false;
        }
        if (version > CurrentVersion)
        {
            error = "manifest version " + version.ToString(CultureInfo.InvariantCulture)
                    + " is newer than this Core understands (" + CurrentVersion + ")";
            return false;
        }

        string side = "", build = "", created = "";
        int pid = 0;
        int declaredFiles = -1;
        long declaredBytes = -1;
        int i = 1;
        bool sawSeparator = false;
        for (; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0) continue;
            if (string.Equals(line, Separator, StringComparison.Ordinal)) { sawSeparator = true; i++; break; }
            int eq = line.IndexOf('=');
            if (eq <= 0) { error = "unreadable header line '" + line + "'"; return false; }
            string key = line.Substring(0, eq).Trim();
            string value = line.Substring(eq + 1).Trim();
            switch (key)
            {
                case "side": side = value; break;
                case "build": build = value; break;
                case "createdUtc": created = value; break;
                case "pid":
                    int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out pid);
                    break;
                case "files":
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out declaredFiles))
                    { error = "unreadable files count '" + value + "'"; return false; }
                    break;
                case "bytes":
                    if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out declaredBytes))
                    { error = "unreadable byte count '" + value + "'"; return false; }
                    break;
                default: break; // a later version may add header keys; ignoring one is not a failure
            }
        }

        if (!sawSeparator) { error = "the manifest has no '" + Separator + "' line"; return false; }

        var files = new List<InteropArchiveFile>();
        for (; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.Trim().Length == 0) continue;
            int firstSpace = line.IndexOf(' ');
            if (firstSpace <= 0) { error = "unreadable file line '" + line + "'"; return false; }
            int secondSpace = line.IndexOf(' ', firstSpace + 1);
            if (secondSpace <= firstSpace + 1) { error = "unreadable file line '" + line + "'"; return false; }
            string sha = line.Substring(0, firstSpace);
            string lengthText = line.Substring(firstSpace + 1, secondSpace - firstSpace - 1);
            string path = line.Substring(secondSpace + 1);
            if (!long.TryParse(lengthText, NumberStyles.Integer, CultureInfo.InvariantCulture, out long length)
                || length < 0)
            { error = "unreadable length on '" + line + "'"; return false; }
            if (path.Length == 0) { error = "a manifest line names no file"; return false; }
            files.Add(new InteropArchiveFile(path, length, sha == "-" ? "" : sha));
        }

        if (declaredFiles >= 0 && declaredFiles != files.Count)
        {
            error = "the manifest says " + declaredFiles.ToString(CultureInfo.InvariantCulture)
                    + " files and lists " + files.Count.ToString(CultureInfo.InvariantCulture);
            return false;
        }

        var parsed = new InteropArchiveManifest(version, side, build, created, pid, files);
        if (declaredBytes >= 0 && declaredBytes != parsed.Bytes)
        {
            error = "the manifest says " + declaredBytes.ToString(CultureInfo.InvariantCulture)
                    + " bytes and its lines add up to " + parsed.Bytes.ToString(CultureInfo.InvariantCulture);
            return false;
        }

        manifest = parsed;
        return true;
    }

    /// <summary>Read the manifest out of an archive folder. A missing file is a false, not a throw.</summary>
    public static bool TryLoad(string archivePath, out InteropArchiveManifest? manifest, out string error)
    {
        manifest = null;
        error = "";
        string path;
        try
        {
            if (string.IsNullOrEmpty(archivePath)) { error = "no archive folder"; return false; }
            path = System.IO.Path.Combine(archivePath, FileName);
            if (!File.Exists(path)) { error = "no " + FileName; return false; }
            return TryParse(File.ReadAllText(path), out manifest, out error);
        }
        catch (Exception e)
        {
            error = "could not read " + FileName + ": " + e.Message;
            return false;
        }
    }

    /// <summary>
    /// Is what is on disk the set this manifest describes? Every listed file must exist with the
    /// listed length, and the folder must hold nothing the manifest does not list. Lengths only:
    /// re-hashing ~180 MB on every start would be the cost the fast path exists to avoid, and a
    /// truncated or partially deleted copy — the failure this whole file is about — always shows
    /// up as a missing file or a short one. The hashes are on disk for whoever wants to prove it.
    /// </summary>
    public bool Verify(string archivePath, out string reason)
    {
        reason = "";
        try
        {
            if (string.IsNullOrEmpty(archivePath) || !Directory.Exists(archivePath))
            {
                reason = "the archive folder is gone";
                return false;
            }

            foreach (var file in Files)
            {
                string full = System.IO.Path.Combine(archivePath, file.Path.Replace('/', System.IO.Path.DirectorySeparatorChar));
                var info = new FileInfo(full);
                if (!info.Exists)
                {
                    reason = "missing " + file.Path;
                    return false;
                }
                if (info.Length != file.Length)
                {
                    reason = file.Path + " is " + info.Length.ToString(CultureInfo.InvariantCulture)
                             + " bytes, the manifest says " + file.Length.ToString(CultureInfo.InvariantCulture);
                    return false;
                }
            }

            string manifestPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(archivePath, FileName));
            int onDisk = 0;
            foreach (var found in Directory.GetFiles(archivePath, "*", SearchOption.AllDirectories))
            {
                if (string.Equals(System.IO.Path.GetFullPath(found), manifestPath, StringComparison.OrdinalIgnoreCase))
                    continue;
                onDisk++;
            }
            if (onDisk != Files.Count)
            {
                reason = "the folder holds " + onDisk.ToString(CultureInfo.InvariantCulture)
                         + " files, the manifest lists " + Files.Count.ToString(CultureInfo.InvariantCulture);
                return false;
            }
            return true;
        }
        catch (Exception e)
        {
            reason = "could not verify the archive: " + e.Message;
            return false;
        }
    }

    /// <summary>
    /// Build a manifest by listing <paramref name="root"/>. <paramref name="hash"/> is the
    /// per-file SHA-256 (the test seam and the offline runner both pass one); null uses a
    /// cancellable streaming read. Paths come out sorted and <c>/</c>-separated.
    /// </summary>
    public static InteropArchiveManifest Describe(
        string root,
        string side,
        string build,
        int pid,
        DateTime createdUtc,
        Func<string, string>? hash = null,
        CancellationToken cancellation = default)
    {
        var files = new List<InteropArchiveFile>();
        var paths = new List<string>(Directory.GetFiles(root, "*", SearchOption.AllDirectories));
        paths.Sort(StringComparer.OrdinalIgnoreCase);
        string prefix = System.IO.Path.GetFullPath(root);
        if (!prefix.EndsWith(System.IO.Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
            prefix += System.IO.Path.DirectorySeparatorChar;

        foreach (var path in paths)
        {
            cancellation.ThrowIfCancellationRequested();
            string full = System.IO.Path.GetFullPath(path);
            string relative = full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? full.Substring(prefix.Length)
                : System.IO.Path.GetFileName(path);
            if (string.Equals(relative, FileName, StringComparison.OrdinalIgnoreCase)) continue;
            long length;
            try { length = new FileInfo(path).Length; }
            catch { length = -1; }
            string sha = "";
            try { sha = hash != null ? hash(path) : HashOf(path, cancellation); }
            catch (OperationCanceledException) { throw; }
            catch { sha = ""; }
            files.Add(new InteropArchiveFile(
                relative.Replace(System.IO.Path.DirectorySeparatorChar, '/'), Math.Max(0, length), sha));
        }

        return new InteropArchiveManifest(
            CurrentVersion, side, build,
            createdUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            pid, files);
    }

    private static string HashOf(string path, CancellationToken cancellation)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 128);
        return ResilienceCheck.Sha256Stream(stream, cancellation);
    }
}
