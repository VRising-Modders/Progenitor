using System;
using System.IO;

namespace Progenitor.Persistence;

/// <summary>
/// Folder layout for <see cref="ServerSave"/>: per-mod JSON that belongs to the <b>server</b>, not
/// to one world, at <c>&lt;persistentDataPath&gt;\progenitor-server\&lt;modGuid&gt;.json</c>. That
/// folder sits beside <c>Saves\</c>, never inside a world folder, so the game's wipe (which
/// archives the world folder) and a Progenitor world restore (which replaces it) leave it alone.
/// Kept free of BepInEx and game types so the test project can compile it.
/// </summary>
public static class ServerSavePaths
{
    /// <summary>Folder under the persistent data root that holds every server-level store.</summary>
    public const string FolderName = "progenitor-server";

    /// <summary><c>{persistRoot}/progenitor-server</c>.</summary>
    public static string StoreFolder(string persistRoot)
    {
        if (string.IsNullOrEmpty(persistRoot))
            throw new ArgumentException("persistRoot is required.", nameof(persistRoot));
        return Path.Combine(persistRoot, FolderName);
    }

    /// <summary><c>{persistRoot}/progenitor-server/{modGuid}.json</c>.</summary>
    public static string StoreFile(string persistRoot, string modGuid)
    {
        if (string.IsNullOrEmpty(modGuid))
            throw new ArgumentException("modGuid is required.", nameof(modGuid));
        return Path.Combine(StoreFolder(persistRoot), modGuid + ".json");
    }

    /// <summary>
    /// The persistent data root a world save folder lives under, or null when the folder does not
    /// have one of the game's two shapes: <c>&lt;root&gt;\Saves\v&lt;N&gt;\&lt;save&gt;</c> or
    /// <c>&lt;root&gt;\CloudSaves\&lt;user&gt;\v&lt;N&gt;\&lt;save&gt;</c>. Used when neither
    /// <c>-persistentDataPath</c> nor the game's own value can be read. Pure string work; the disk
    /// is not consulted.
    /// </summary>
    public static string? RootFromSaveFolder(string? saveFolder)
    {
        if (string.IsNullOrWhiteSpace(saveFolder)) return null;
        string folder = saveFolder!.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string? version = Path.GetDirectoryName(folder);
        if (string.IsNullOrEmpty(version) || !SavePaths.TryParseVersionFolder(Path.GetFileName(version), out _))
            return null;

        string? parent = Path.GetDirectoryName(version);
        if (string.IsNullOrEmpty(parent)) return null;
        if (string.Equals(Path.GetFileName(parent), "Saves", StringComparison.OrdinalIgnoreCase))
            return NullIfEmpty(Path.GetDirectoryName(parent));

        // CloudSaves\<user>\v<N>\<save>: parent is the user folder.
        string? cloud = Path.GetDirectoryName(parent);
        if (!string.IsNullOrEmpty(cloud)
            && string.Equals(Path.GetFileName(cloud), "CloudSaves", StringComparison.OrdinalIgnoreCase))
            return NullIfEmpty(Path.GetDirectoryName(cloud));

        return null;
    }

    /// <summary>
    /// True when <paramref name="path"/> is <paramref name="folder"/> or inside it, compared as
    /// full paths, case-insensitively. Used by the tests to state the contract that a server store
    /// is never inside a world folder.
    /// </summary>
    public static bool IsInside(string path, string folder)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(folder)) return false;
        string p = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string f = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(p, f, StringComparison.OrdinalIgnoreCase)) return true;
        return p.StartsWith(f + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
