using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

// The mod-manager half of the Thunderstore package. V Rising's Thunderstore install rules know only
// BepInEx/plugins, patchers, core, monomod and config: r2modman, the Thunderstore App and Gale flatten any
// other folder into BepInEx/plugins/<Team-Name>/, so a package that carried BepInEx/progenitor-core/ would land
// Core (a Progenitor.dll) among the plugins, where Bootstrap never looks and the assembly resolver can pick it up.
// The Thunderstore zip therefore carries those folders as one archive, BepInEx/patchers/progenitor-files.zip,
// which the mod manager places beside this patcher like any other file, and the patcher unpacks it into the real
// BepInEx/progenitor-core (and progenitor-reload) before the chainloader starts. The manual zip carries the real
// folders and has no archive, so this is a no-op there. References nothing but the base class library, so the
// game-free tests run this file as-is.
namespace Progenitor.PluginActions;

/// <summary>What one start did with the package archive.</summary>
internal enum PackagePayloadOutcome
{
    /// <summary>No archive was found: a manual install, or Progenitor was removed.</summary>
    NoPayload = 0,

    /// <summary>The files from this exact archive are already in place.</summary>
    Current = 1,

    /// <summary>The archive was unpacked.</summary>
    Applied = 2,

    /// <summary>Another game process of this install is running; unpacked at a later start.</summary>
    Deferred = 3,

    /// <summary>A Remove Progenitor request is waiting; the files it would delete are not put back.</summary>
    Skipped = 4,

    /// <summary>The archive names a path it may not write; nothing was written.</summary>
    Refused = 5,

    /// <summary>An I/O error part way; the stamp was not written, so the next start retries.</summary>
    Failed = 6,
}

internal static class PackagePayload
{
    /// <summary>The archive's file name, beside the patcher in the Thunderstore package (<c>BepInEx/patchers/</c>).</summary>
    internal const string FileName = "progenitor-files.zip";

    /// <summary>
    /// Written into <c>BepInEx/progenitor-core</c> after a complete unpack: the archive's sha256. A start whose archive
    /// hashes the same, with Core present, writes nothing, so a Core the player updated in game is never put back.
    /// </summary>
    internal const string StampFileName = "progenitor-files.sha256";

    internal const string CoreFolder = "progenitor-core";
    internal const string CoreEntry = CoreFolder + "/Progenitor.dll";
    internal const string PatchersFolder = "patchers";

    /// <summary>Same name as Bootstrap's <c>UninstallAtStart.FileName</c>: a waiting Remove Progenitor request.</summary>
    internal const string UninstallMarker = "progenitor-uninstall.json";

    /// <summary>The only BepInEx folders the archive may write into.</summary>
    internal static readonly IReadOnlyList<string> AllowedFolders = new[] { CoreFolder, "progenitor-reload" };

    internal const long MaxArchiveBytes = 256L * 1024 * 1024;
    internal const long MaxEntryBytes = 64L * 1024 * 1024;
    internal const int MaxEntries = 4096;

    private const string TempSuffix = ".progenitor-tmp";
    private const string Prefix = "Progenitor package files: ";

    /// <summary>
    /// The archive to apply: beside this patcher first, then <c>BepInEx/patchers</c> and its direct subfolders (where a
    /// mod manager puts a package's patchers). Null when none is found, or when two different folders each hold one and
    /// neither is beside this patcher (an ambiguous install is left alone).
    /// </summary>
    internal static string? Find(string bepInExRoot, string? patcherDirectory, Action<string>? warn = null)
    {
        try
        {
            if (!string.IsNullOrEmpty(patcherDirectory))
            {
                string beside = Path.Combine(patcherDirectory!, FileName);
                if (File.Exists(beside)) return beside;
            }

            string patchers = Path.Combine(bepInExRoot, PatchersFolder);
            if (!Directory.Exists(patchers)) return null;
            var found = new List<string>();
            string top = Path.Combine(patchers, FileName);
            if (File.Exists(top)) found.Add(top);
            foreach (string dir in Directory.GetDirectories(patchers))
            {
                string candidate = Path.Combine(dir, FileName);
                if (File.Exists(candidate)) found.Add(candidate);
            }

            if (found.Count == 1) return found[0];
            if (found.Count > 1)
                Say(warn, "found " + found.Count.ToString(CultureInfo.InvariantCulture) + " copies of " + FileName
                          + " under BepInEx/patchers (" + string.Join(", ", found) + "); none was applied. Remove the copies you did not install.");
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Unpack <paramref name="archivePath"/> into <paramref name="bepInExRoot"/> when it differs from the last one
    /// unpacked there (or Core is missing). Every entry is checked before anything is written: it must sit under one of
    /// <see cref="AllowedFolders"/>, with no rooted, drive or <c>..</c> segment, no link on the way, and the archive must
    /// carry <see cref="CoreEntry"/>. Files are written beside their destination and moved over it; files the archive does
    /// not name are left alone (Core's caches and kept copies live there). The stamp is written last. Never throws.
    /// </summary>
    internal static PackagePayloadOutcome Apply(
        string bepInExRoot,
        string? archivePath,
        Action<string>? info,
        Action<string>? warn,
        Func<bool>? otherGameProcessRunning)
    {
        if (string.IsNullOrEmpty(archivePath)) return PackagePayloadOutcome.NoPayload;
        try
        {
            string root = Path.GetFullPath(bepInExRoot);
            if (File.Exists(Path.Combine(root, UninstallMarker)))
            {
                Say(info, "a Remove Progenitor request is waiting; " + FileName + " was not unpacked.");
                return PackagePayloadOutcome.Skipped;
            }

            // A hosted child's patchers folder is a link to the client's: that archive belongs to the client's tree, and
            // the child's progenitor-core is the copy staging made for it (possibly newer, possibly with a .next).
            if (LinkOnPath(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, Path.GetFullPath(archivePath!)))
            {
                Say(info, archivePath + " is reached through a link and belongs to another install; it was not unpacked.");
                return PackagePayloadOutcome.Skipped;
            }

            var length = new FileInfo(archivePath!).Length;
            if (length > MaxArchiveBytes)
            {
                Say(warn, archivePath + " is larger than " + MaxArchiveBytes.ToString(CultureInfo.InvariantCulture) + " bytes; it was not unpacked.");
                return PackagePayloadOutcome.Refused;
            }

            byte[] bytes = File.ReadAllBytes(archivePath!);
            string sha = Sha256Hex(bytes);
            string coreDll = Path.Combine(root, CoreFolder, "Progenitor.dll");
            string stamp = Path.Combine(root, CoreFolder, StampFileName);
            bool corePresent = File.Exists(coreDll);
            if (corePresent && string.Equals(ReadStamp(stamp), sha, StringComparison.OrdinalIgnoreCase))
                return PackagePayloadOutcome.Current;

            if (corePresent && otherGameProcessRunning != null && otherGameProcessRunning())
            {
                Say(info, "another game process of this install is running; the new files are unpacked at a later start.");
                return PackagePayloadOutcome.Deferred;
            }

            using var archive = new ZipArchive(new MemoryStream(bytes, writable: false), ZipArchiveMode.Read);
            if (!TryPlan(root, archive, out var plan, out string why))
            {
                Say(warn, archivePath + " was not unpacked: " + why + ".");
                return PackagePayloadOutcome.Refused;
            }

            foreach (var (entry, dest) in plan)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                string tmp = dest + TempSuffix;
                using (var input = entry.Open())
                using (var output = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                    input.CopyTo(output);
                File.Move(tmp, dest, overwrite: true);
            }

            // A Core staged for the next start by an earlier in-game update would replace the one just unpacked.
            string next = coreDll + ".next";
            bool droppedNext = false;
            if (File.Exists(next))
            {
                File.Delete(next);
                droppedNext = true;
            }

            File.WriteAllText(stamp, sha + "\n", new UTF8Encoding(false));
            Say(info, "unpacked " + plan.Count.ToString(CultureInfo.InvariantCulture) + " files from " + archivePath
                      + " (sha256 " + sha.Substring(0, 12) + ") into BepInEx/" + CoreFolder
                      + (droppedNext ? "; a staged Progenitor.dll.next was removed" : "") + ".");
            return PackagePayloadOutcome.Applied;
        }
        catch (Exception e)
        {
            Say(warn, FileName + " could not be unpacked: " + e.Message + ". The next start tries again.");
            return PackagePayloadOutcome.Failed;
        }
    }

    /// <summary>Every file entry and its destination, or the first reason the archive is refused whole.</summary>
    internal static bool TryPlan(string root, ZipArchive archive, out List<(ZipArchiveEntry Entry, string Dest)> plan, out string why)
    {
        plan = new List<(ZipArchiveEntry, string)>();
        why = "";
        if (archive.Entries.Count > MaxEntries)
        {
            why = "it has more than " + MaxEntries.ToString(CultureInfo.InvariantCulture) + " entries";
            return false;
        }

        string rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        bool hasCore = false;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            string raw = entry.FullName;
            if (raw.EndsWith("/", StringComparison.Ordinal) || raw.EndsWith("\\", StringComparison.Ordinal)) continue;
            if (!IsAllowedEntry(raw, out string rel))
            {
                why = "it names " + raw + ", outside " + string.Join(" and ", AllowedFolders);
                return false;
            }

            if (string.Equals(Path.GetFileName(rel), StampFileName, StringComparison.OrdinalIgnoreCase)
                || rel.EndsWith(TempSuffix, StringComparison.OrdinalIgnoreCase))
            {
                why = "it names " + raw + ", a file the unpacker owns";
                return false;
            }

            if (entry.Length > MaxEntryBytes)
            {
                why = raw + " is larger than " + MaxEntryBytes.ToString(CultureInfo.InvariantCulture) + " bytes";
                return false;
            }

            if (!seen.Add(rel))
            {
                why = "it names " + rel + " twice";
                return false;
            }

            string dest = Path.GetFullPath(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));
            if (!dest.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            {
                why = "it names " + raw + ", outside BepInEx";
                return false;
            }

            if (LinkOnPath(rootFull, dest))
            {
                why = "the path to " + rel + " goes through a link";
                return false;
            }

            if (string.Equals(rel, CoreEntry, StringComparison.OrdinalIgnoreCase)) hasCore = true;
            plan.Add((entry, dest));
        }

        if (!hasCore)
        {
            why = "it has no " + CoreEntry;
            return false;
        }

        return true;
    }

    /// <summary>
    /// A relative entry under one of <see cref="AllowedFolders"/>: no rooted or drive spelling, no empty, <c>.</c> or
    /// <c>..</c> segment, at least one segment below the folder. <paramref name="relative"/> uses <c>/</c> and the folder's
    /// own spelling.
    /// </summary>
    internal static bool IsAllowedEntry(string? name, out string relative)
    {
        relative = "";
        string raw = name ?? "";
        if (raw.Length == 0 || raw.Length > 400) return false;
        if (raw[0] == '/' || raw[0] == '\\') return false;
        if (raw.IndexOf(':') >= 0) return false;
        string[] parts = raw.Replace('\\', '/').Split('/');
        if (parts.Length < 2) return false;
        foreach (string part in parts)
            // Windows drops a trailing dot or space from a name, so "Progenitor.dll." would alias "Progenitor.dll".
            if (part.Length == 0 || part == "." || part == ".." || part.Trim().Length != part.Length || part.EndsWith(".", StringComparison.Ordinal)) return false;
        string? folder = null;
        foreach (string allowed in AllowedFolders)
            if (string.Equals(parts[0], allowed, StringComparison.OrdinalIgnoreCase)) folder = allowed;
        if (folder == null) return false;
        parts[0] = folder;
        relative = string.Join("/", parts);
        return true;
    }

    private static bool LinkOnPath(string rootFull, string dest)
    {
        // The BepInEx root itself may be a link (a mod manager profile); nothing below it may.
        string stop = rootFull.TrimEnd(Path.DirectorySeparatorChar);
        string? current = Path.GetDirectoryName(dest);
        while (current != null && current.Length > stop.Length)
        {
            if (IsLink(current)) return true;
            current = Path.GetDirectoryName(current);
        }

        return IsLink(dest);
    }

    private static bool IsLink(string path)
    {
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path)) return false;
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return true;
        }
    }

    private static string ReadStamp(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path).Trim() : ""; }
        catch { return ""; }
    }

    internal static string Sha256Hex(byte[] bytes)
    {
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(bytes);
        var sb = new StringBuilder(hash.Length * 2);
        foreach (byte b in hash) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    private static void Say(Action<string>? log, string line)
    {
        try { log?.Invoke(Prefix + line); }
        catch { /* a log is not a gate */ }
    }
}
