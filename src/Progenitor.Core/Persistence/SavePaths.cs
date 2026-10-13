using System;
using System.IO;

namespace Progenitor.Persistence;

/// <summary>
/// World-save folder layout for mod JSON. The live game writes
/// <c>AutoSave_N.save.gz</c> under <c>&lt;persistentDataPath&gt;\Saves\vN\&lt;saveName&gt;</c>
/// (v4 on this build). Inventing <c>Saves\&lt;saveName&gt;</c> without a version folder puts
/// the files beside the wrong tree, so a copied save does not take its mod data with it.
/// Kept free of BepInEx and game types so the test project can compile it.
/// </summary>
public static class SavePaths
{
    /// <summary>Subfolder next to the game's gzip saves that holds per-mod JSON.</summary>
    public const string FolderName = "progenitor";

    /// <summary>
    /// Version folder this build of V Rising uses. Used only when disk has no <c>v*</c>
    /// directory yet (a brand-new persist root).
    /// </summary>
    public const string DefaultVersionFolder = "v4";

    /// <summary><c>{saveDirectory}/progenitor</c>.</summary>
    public static string StoreFolder(string saveDirectory)
    {
        if (string.IsNullOrEmpty(saveDirectory))
            throw new ArgumentException("saveDirectory is required.", nameof(saveDirectory));
        return Path.Combine(saveDirectory, FolderName);
    }

    /// <summary><c>{saveDirectory}/progenitor/{modGuid}.json</c>.</summary>
    public static string StoreFile(string saveDirectory, string modGuid)
    {
        if (string.IsNullOrEmpty(modGuid))
            throw new ArgumentException("modGuid is required.", nameof(modGuid));
        return Path.Combine(StoreFolder(saveDirectory), modGuid + ".json");
    }

    /// <summary>
    /// The file name stem a store id is written under (<c>ModSave</c> and <c>ServerSave</c>).
    /// Throws when the id is empty, names a path (<c>/</c>, <c>\</c>, <c>..</c>) or is not a usable
    /// file name; other invalid file-name characters become <c>_</c>.
    /// </summary>
    internal static string SanitizeStoreId(string modGuid)
    {
        if (string.IsNullOrWhiteSpace(modGuid))
            throw new ArgumentException("modGuid is required.", nameof(modGuid));
        var trimmed = modGuid.Trim();
        if (trimmed.IndexOfAny(new[] { '/', '\\' }) >= 0 || trimmed.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException("modGuid must not contain a path.", nameof(modGuid));
        var invalid = Path.GetInvalidFileNameChars();
        var chars = trimmed.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (Array.IndexOf(invalid, chars[i]) >= 0) chars[i] = '_';
        }
        var name = new string(chars);
        if (name is "." or ".." || string.IsNullOrEmpty(name))
            throw new ArgumentException("modGuid is not a usable file name.", nameof(modGuid));
        return name;
    }

    /// <summary>
    /// Combine <c>SaveDirectoryPath</c> with <c>SaveToLoad</c>. If <paramref name="directory"/>
    /// already ends with the save name, or already looks like the folder that holds
    /// <c>*.save.gz</c>, it is returned as-is so we do not nest <c>world\world</c>.
    /// </summary>
    public static string? CombineSaveFolder(string? directory, string? saveName)
    {
        directory = directory?.Trim();
        saveName = saveName?.Trim();
        if (string.IsNullOrEmpty(directory)) return null;
        directory = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrEmpty(saveName)) return directory;
        if (string.Equals(Path.GetFileName(directory), saveName, StringComparison.OrdinalIgnoreCase))
            return directory;
        var combined = Path.Combine(directory, saveName);
        if (Directory.Exists(combined)) return combined;
        if (Directory.Exists(directory) && LooksLikeSaveFolder(directory)) return directory;
        return combined;
    }

    /// <summary>
    /// True when <paramref name="path"/> already holds a world save (gzip files or a progenitor
    /// folder). Used to stop combining <c>SaveToLoad</c> onto a path that is already the leaf.
    /// </summary>
    public static bool LooksLikeSaveFolder(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        try
        {
            foreach (var _ in Directory.EnumerateFiles(path, "*.save.gz"))
                return true;
            return Directory.Exists(Path.Combine(path, FolderName));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Locate <c>Saves\v*\&lt;saveName&gt;</c> (highest version first), then the same under
    /// <c>CloudSaves\&lt;user&gt;</c>. Never returns or invents <c>Saves\&lt;saveName&gt;</c>
    /// without a version folder: a missing save is placed under the newest existing <c>v*</c>
    /// or under <see cref="DefaultVersionFolder"/>.
    /// </summary>
    public static string FindSaveFolder(string persistRoot, string saveName)
    {
        if (string.IsNullOrEmpty(persistRoot))
            throw new ArgumentException("persistRoot is required.", nameof(persistRoot));
        if (string.IsNullOrEmpty(saveName))
            throw new ArgumentException("saveName is required.", nameof(saveName));

        var savesRoot = Path.Combine(persistRoot, "Saves");
        var existing = FindVersionedSave(savesRoot, saveName);
        if (!string.IsNullOrEmpty(existing)) return existing;

        var cloud = Path.Combine(persistRoot, "CloudSaves");
        if (Directory.Exists(cloud))
        {
            foreach (var userDir in Directory.GetDirectories(cloud))
            {
                existing = FindVersionedSave(userDir, saveName);
                if (!string.IsNullOrEmpty(existing)) return existing;
            }
        }

        var version = NewestVersionFolderName(savesRoot) ?? DefaultVersionFolder;
        return Path.Combine(savesRoot, version, saveName);
    }

    /// <summary>
    /// Infix inserted before the extension when an old store file is archived because dest
    /// already existed. <c>sample.mod.json</c> becomes <c>sample.mod.merged-…json</c>.
    /// </summary>
    public const string MergedArchiveMarker = ".merged-";

    /// <summary>
    /// Move <paramref name="oldPath"/> to <paramref name="newPath"/> when the old file exists
    /// and the new one does not. Used when the resolved save folder changes after a write so
    /// the JSON follows the world instead of being left behind. Returns true only after a
    /// successful <see cref="File.Move"/>. False means nothing to move, the destination already
    /// has a file (callers merge old ∪ dest ∪ dirty and archive the old file; we do not
    /// overwrite dest), or the move threw — callers must not retarget onto a missing dest in
    /// the last case, or a later load treats it as empty.
    /// </summary>
    public static bool TryMoveStoreFile(string? oldPath, string newPath)
    {
        if (string.IsNullOrEmpty(oldPath) || string.IsNullOrEmpty(newPath)) return false;
        if (string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            if (!File.Exists(oldPath) || File.Exists(newPath)) return false;
            var dir = Path.GetDirectoryName(newPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.Move(oldPath, newPath);
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"ModSave: moving '{oldPath}' to '{newPath}' threw: {e}");
            return false;
        }
    }

    /// <summary>
    /// Rename a store file that was not moved because dest already existed, so the old copy is
    /// not silently orphaned. The sidecar is <c>{stem}.merged-{utc-timestamp}.json</c> beside
    /// the original. Returns the sidecar path after a successful rename, or null when there was
    /// nothing to archive or every attempt threw — the original is left in place in that case.
    /// </summary>
    public static string? TryArchiveMergedStoreFile(string? oldPath)
    {
        if (string.IsNullOrEmpty(oldPath)) return null;
        try
        {
            if (!File.Exists(oldPath)) return null;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"ModSave: checking '{oldPath}' for merge-archive threw: {e}");
            return null;
        }

        for (int attempt = 0; attempt < 8; attempt++)
        {
            var sidecar = MergedArchivePath(oldPath, DateTime.UtcNow, attempt);
            try
            {
                if (File.Exists(sidecar)) continue;
                File.Move(oldPath, sidecar);
                return sidecar;
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogError($"ModSave: renaming '{oldPath}' to '{sidecar}' threw: {e}");
            }
        }

        return null;
    }

    /// <summary>
    /// Sidecar path for an old store file archived after a dest-exists merge.
    /// <paramref name="attempt"/> is appended so a same-millisecond collision can retry.
    /// </summary>
    public static string MergedArchivePath(string storeFile, DateTime utc, int attempt = 0)
    {
        if (string.IsNullOrEmpty(storeFile))
            throw new ArgumentException("storeFile is required.", nameof(storeFile));
        var dir = Path.GetDirectoryName(storeFile) ?? "";
        var stem = Path.GetFileNameWithoutExtension(storeFile);
        var ext = Path.GetExtension(storeFile);
        if (string.IsNullOrEmpty(ext)) ext = ".json";
        var stamp = utc.ToUniversalTime().ToString("yyyyMMddTHHmmssfffZ");
        if (attempt > 0) stamp += "-" + attempt;
        return Path.Combine(dir, stem + MergedArchiveMarker + stamp + ext);
    }

    /// <summary>
    /// True when <paramref name="path"/> is a merge sidecar, not a live store. Load must skip
    /// these or <c>*.json</c> would register <c>sample.mod.merged-…</c> as a second mod guid.
    /// </summary>
    public static bool IsMergedArchive(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        var name = Path.GetFileName(path);
        if (name.Length < 6) return false;
        if (!name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return false;
        var stem = name.Substring(0, name.Length - 5);
        return stem.IndexOf(MergedArchiveMarker, StringComparison.OrdinalIgnoreCase) > 0;
    }

    /// <summary>
    /// True when <paramref name="name"/> is a persistence version folder (<c>v4</c>, <c>v10</c>).
    /// Anything else (including a save name sitting directly under <c>Saves</c>) is rejected.
    /// </summary>
    public static bool TryParseVersionFolder(string? name, out int version)
    {
        version = 0;
        if (string.IsNullOrEmpty(name) || name.Length < 2) return false;
        if (name[0] is not ('v' or 'V')) return false;
        return int.TryParse(name.AsSpan(1), out version);
    }

    private static string? FindVersionedSave(string root, string saveName)
    {
        if (!Directory.Exists(root)) return null;
        string? best = null;
        int bestVer = int.MinValue;
        foreach (var dir in Directory.GetDirectories(root))
        {
            if (!TryParseVersionFolder(Path.GetFileName(dir), out var ver)) continue;
            var candidate = Path.Combine(dir, saveName);
            if (!Directory.Exists(candidate)) continue;
            if (ver < bestVer) continue;
            bestVer = ver;
            best = candidate;
        }
        return best;
    }

    private static string? NewestVersionFolderName(string savesRoot)
    {
        if (!Directory.Exists(savesRoot)) return null;
        string? best = null;
        int bestVer = int.MinValue;
        foreach (var dir in Directory.GetDirectories(savesRoot))
        {
            var name = Path.GetFileName(dir);
            if (!TryParseVersionFolder(name, out var ver)) continue;
            if (ver < bestVer) continue;
            bestVer = ver;
            best = name;
        }
        return best;
    }
}
