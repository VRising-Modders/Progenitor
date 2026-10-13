using System;
using System.IO;

namespace Progenitor.Install;

/// <summary>
/// The user's decision of 2026-09-29 ("dont keep local likes or a rating"): the Mods page's Like
/// control, its local store and its rating sends are removed. The store was Progenitor's own cache
/// file, <c>BepInEx/progenitor-cache/likes.json</c>; Core deletes that one file once at load if it
/// exists and logs one line. Nothing else under <c>progenitor-cache</c> is touched, and nothing
/// writes the file any more.
/// </summary>
internal static class LegacyLikesCache
{
    internal const string CacheFolder = "progenitor-cache";
    internal const string FileName = "likes.json";

    internal static string PathFor(string bepInExRoot) => Path.Combine(bepInExRoot ?? "", CacheFolder, FileName);

    /// <summary>
    /// Delete exactly <c>&lt;root&gt;/progenitor-cache/likes.json</c> when it is a plain file (never a
    /// folder of that name, never a link). True when a file was deleted. Never throws.
    /// </summary>
    internal static bool DeleteIfPresent(string? bepInExRoot, Action<string>? log)
    {
        if (string.IsNullOrWhiteSpace(bepInExRoot)) return false;
        string path = PathFor(bepInExRoot!);
        try
        {
            if (!File.Exists(path)) return false;
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return false;
            File.Delete(path);
            log?.Invoke("Mods: deleted " + CacheFolder + "/" + FileName
                + " (local likes are no longer kept; likes and ratings were removed).");
            return true;
        }
        catch (Exception e)
        {
            log?.Invoke("Mods: could not delete " + CacheFolder + "/" + FileName + ": " + e.Message);
            return false;
        }
    }
}
